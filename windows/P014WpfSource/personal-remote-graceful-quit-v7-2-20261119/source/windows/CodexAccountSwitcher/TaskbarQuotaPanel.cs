using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CodexAccountSwitcher.Core;
using Deskband11Lib.Core;
using Deskband11Lib.Wpf;
using Microsoft.Win32;

namespace CodexAccountSwitcher;

// The native WPF panel is hosted *inside* Shell_TrayWnd, immediately before
// TrayNotifyWnd (^). The container never opens a standalone floating minibar.
// The existing active-profile gate is the only source of quota observations.
internal sealed class TaskbarQuotaPanel : IDisposable
{
    // Deskband11Lib positions the hosted HWND at the taskbar's TOP edge.
    // Claim the full 48-DIP taskbar row and center the 30-DIP indicator inside
    // it; merely setting VerticalAlignment.Center on a 30-DIP HWND is not enough.
    internal const double PreferredWidth = 166;
    internal const double PreferredHeight = 48;
    internal const double IndicatorHeight = 30;
    internal const TaskbarContentPlacement Placement = TaskbarContentPlacement.BeforeNotificationArea;

    private readonly IntegratedMiniBarState state = new();
    private NonWeeklyUsage? alternateQuota;
    private readonly Action openAccounts;
    private readonly Action quitApp;
    private readonly Func<bool> canQuit;
    private readonly DispatcherTimer refreshTimer;
    private readonly NativeSettings appearance;
    private bool showResetCountdown;
    private int taskbarFontSize;
    // The compact row stays 166 DIP by default; opt-in countdown gets
    // its own width, never clipping 100% + both reset countdowns.
    internal double EffectiveWidth => showResetCountdown
        ? taskbarFontSize switch {
            9 => 246, 10 => 264, 11 => 284, 12 => 310, _ => 340 }
        : taskbarFontSize == 13 ? 184 : PreferredWidth;
    private TaskbarContentHost? host;
    private Window? hostedWindow;
    private TextBlock? fiveHour;
    private TextBlock? weekly;
    private TextBlock? credits;
    private Border? background;
    private Grid? taskbarCanvas;
    private Border? separatorOne;
    private Border? separatorTwo;
    private bool hovered;
    private bool attaching;
    private bool recreating;
    private bool started;
    private bool disposed;

    internal TaskbarQuotaPanel(Action openAccounts, Action quitApp, Func<bool> canQuit,
        NativeSettings? displaySettings = null)
    {
        this.openAccounts = openAccounts;
        this.quitApp = quitApp;
        this.canQuit = canQuit;
        appearance = displaySettings ?? new NativeSettings();
        showResetCountdown = appearance.ShowTaskbarResetCountdown;
        taskbarFontSize = appearance.TaskbarFontSize;
        appearance.Changed += OnAppearanceChanged;
        refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        refreshTimer.Tick += (_, _) =>
        {
            Refresh();
            // Explorer can start later than this login app. Without a retry,
            // an initial 20-second attachment timeout hides the quota forever.
            if (TaskbarAttachRetryPolicy.ShouldRetry(
                started, disposed, attaching, recreating, host is not null))
                _ = AttachAsync();
        };
        refreshTimer.Start();
    }

    private void OnAppearanceChanged()
    {
        if (disposed) return;
        if (!refreshTimer.Dispatcher.CheckAccess())
        {
            refreshTimer.Dispatcher.BeginInvoke(new Action(OnAppearanceChanged));
            return;
        }
        var nextCountdown = appearance.ShowTaskbarResetCountdown;
        var nextFont = appearance.TaskbarFontSize;
        if (nextCountdown == showResetCountdown && nextFont == taskbarFontSize)
            return;
        showResetCountdown = nextCountdown;
        taskbarFontSize = nextFont;
        if (started && host is not null && !attaching && !recreating)
            _ = RecreateForAppearanceAsync();
        else
            Refresh();
    }

    private async Task RecreateForAppearanceAsync()
    {
        if (disposed || attaching || recreating) return;
        recreating = true;
        try
        {
            ReleaseHostedWindow();
            await Task.Delay(200);
            if (!disposed) await AttachAsync();
        }
        catch (Exception error)
        {
            Trace.TraceWarning("Taskbar presentation change unavailable: " +
                error.GetType().Name);
        }
        finally { recreating = false; }
    }

    internal void Start()
    {
        if (started || disposed) return;
        started = true;
        _ = AttachAsync();
    }

    internal void Accept(CodexMonitorWireSample sample)
    {
        if (disposed) return;
        // A rejected or delayed 5H/7D sample must not clear a genuine
        // Free/30D quota already displayed for the selected profile.
        if (state.TryApply(sample, DateTimeOffset.UtcNow))
            alternateQuota = null;
        Refresh();
    }

    internal void AcceptNonWeekly(NonWeeklyUsage actual)
    {
        if (disposed || actual.RemainingPercent is < 0 or > 100 ||
            actual.WindowDurationMins <= 0 || actual.WindowDurationMins > 525_600) return;
        // The active profile has a genuine plan-specific window. Never
        // represent it as a weekly/5H quota or share the old account's sample.
        state.Invalidate(DateTimeOffset.UtcNow);
        alternateQuota = actual;
        Refresh();
    }

    internal void Invalidate()
    {
        if (disposed) return;
        alternateQuota = null;
        state.Invalidate(DateTimeOffset.UtcNow);
        Refresh();
    }

    internal bool IsVisibleOnTaskbar => host?.IsAttached == true && hostedWindow?.IsVisible == true;
    internal MiniBarReadout CurrentReadout => state.Read(DateTimeOffset.UtcNow);
    internal NonWeeklyUsage? CurrentNonWeeklyUsage => alternateQuota;

    internal void Refresh()
    {
        if (disposed || fiveHour is null || weekly is null || credits is null) return;

        var frame = CurrentReadout;
        var readout = TrayQuotaReadout.From(frame);
        var light = IsLightTaskbar();
        var alternate = alternateQuota;
        var age = alternate is null ? TimeSpan.MaxValue :
            DateTimeOffset.UtcNow - alternate.ObservedAt;
        // Pro and other genuine weekly-only accounts have no 300-minute
        // window. Keep 7D/R, not a misleading fixed "5H ?" slot.
        // Determine this from verified window contents, not subscription text.
        var weeklyOnly = ShouldHideFiveHour(frame);
        fiveHour.Visibility = weeklyOnly ? Visibility.Collapsed : Visibility.Visible;
        if (separatorOne is not null)
            separatorOne.Visibility = weeklyOnly ? Visibility.Collapsed : Visibility.Visible;
        if (alternate is not null && age >= TimeSpan.FromSeconds(-30) &&
            age <= TimeSpan.FromMinutes(6))
        {
            SetMetric(fiveHour, "5H", "?", light);
            SetMetric(weekly, alternate.WindowLabel, alternate.RemainingPercent + "%", light,
                showResetCountdown ? FormatResetCountdown(alternate.ResetsAt, DateTimeOffset.UtcNow) : "");
            SetMetric(credits, "R", alternate.ResetCreditsRemaining?.ToString(
                CultureInfo.InvariantCulture) ?? "?", light);
            fiveHour.ToolTip = "5H: not provided by this Codex account";
            weekly.ToolTip = alternate.WindowLabel + ": " + alternate.RemainingPercent +
                "% remaining; observed " + age.TotalSeconds.ToString("F0",
                CultureInfo.InvariantCulture) + "s ago\n다음 초기화: " +
                (alternate.ResetsAt > alternate.ObservedAt ?
                    alternate.ResetsAt.ToOffset(TimeSpan.FromHours(9))
                        .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " KST" :
                    "정보 없음");
            credits.ToolTip = "Reset credits: " +
                (alternate.ResetCreditsRemaining?.ToString(CultureInfo.InvariantCulture) ?? "unknown") +
                "\n개별 만료일: 제공되지 않음";
        }
        else if (alternate is not null)
        {
            // The last confirmed plan was non-weekly (e.g. Free 30D), but
            // its observation expired. Keep the plan label, not the stale
            // number; do not mislabel this account as a 7D subscription.
            SetMetric(fiveHour, "5H", "?", light);
            SetMetric(weekly, alternate.WindowLabel, "?", light);
            SetMetric(credits, "R", "?", light);
            fiveHour.ToolTip = "5H: 제공되지 않음";
            weekly.ToolTip = alternate.WindowLabel +
                ": UNKNOWN\n관측 만료 · 다음 초기화: 정보 없음";
            credits.ToolTip = "리셋권: UNKNOWN\n개별 만료일: 정보 없음";
        }
        else
        {
            SetMetric(fiveHour, "5H", Format(readout.FiveHourIcon, percent: true), light,
                showResetCountdown && frame.FiveHourRemaining is not null
                    ? FormatResetCountdown(frame.FiveHourResetsAt, DateTimeOffset.UtcNow) : "");
            SetMetric(weekly, "7D", Format(readout.WeeklyIcon, percent: true), light,
                showResetCountdown && frame.WeeklyRemaining is not null
                    ? FormatResetCountdown(frame.WeeklyResetsAt, DateTimeOffset.UtcNow) : "");
            SetMetric(credits, "R", readout.CreditsIcon, light);
            fiveHour.ToolTip = CreateReadableTooltip(readout.FiveHourTooltip);
            weekly.ToolTip = CreateReadableTooltip(readout.WeeklyTooltip);
            credits.ToolTip = CreateReadableTooltip(readout.CreditsTooltip);
        }

        var dividerColor = light ? Color.FromArgb(75, 68, 77, 91)
            : Color.FromArgb(90, 224, 231, 242);
        foreach (var divider in new[] { separatorOne, separatorTwo })
            if (divider != null) divider.Background = new SolidColorBrush(dividerColor);
        ApplyHoverBackground(light);
    }

    internal static bool ShouldHideFiveHour(MiniBarReadout frame) =>
        frame.Status is MiniBarDisplayStatus.FreshUnbound or MiniBarDisplayStatus.AgedUnbound &&
        frame.WeeklyRemaining is >= 0 and <= 100 &&
        frame.FiveHourRemaining is null;

    // DateTimeOffset is a verified provider timestamp, not guessed from
    // usage percentage. Negative/past or missing reset times show no suffix.
    // Rounding up avoids falsely advertising that a reset already occurred.
    internal static string FormatResetCountdown(DateTimeOffset? resetAt, DateTimeOffset now)
    {
        if (resetAt is null || resetAt <= now || resetAt > now.AddDays(366))
            return "";
        var minutes = (long)Math.Ceiling((resetAt.Value - now).TotalMinutes);
        if (minutes >= 1440)
        {
            var days = minutes / 1440;
            var hours = (minutes % 1440 + 59) / 60;
            if (hours >= 24) { days++; hours = 0; }
            return " (" + days.ToString(CultureInfo.InvariantCulture) + "d" +
                (hours > 0 ? "\u2009" + hours.ToString(CultureInfo.InvariantCulture) + "h" : "") + ")";
        }
        if (minutes >= 60)
            return " (" + (minutes / 60).ToString(CultureInfo.InvariantCulture) + "h\u2009" +
                (minutes % 60).ToString(CultureInfo.InvariantCulture) + "m)";
        return " (" + Math.Max(1, minutes).ToString(CultureInfo.InvariantCulture) + "m)";
    }

    // Keep reset time visually prominent and prevent Windows from wrapping
    // the timestamp into a third/fourth line on a narrow taskbar slot.
    private static ToolTip CreateReadableTooltip(string content)
    {
        var lines = new StackPanel { Orientation = Orientation.Vertical };
        foreach (var raw in content.Split('\n'))
        {
            var emphasis = raw.StartsWith("다음 초기화:", StringComparison.Ordinal) ||
                raw.Contains("번 만료:", StringComparison.Ordinal);
            lines.Children.Add(new TextBlock
            {
                Text = raw,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = emphasis ? 13 : 12,
                FontWeight = emphasis ? FontWeights.SemiBold : FontWeights.Normal,
                TextWrapping = TextWrapping.NoWrap,
                Margin = new Thickness(0, emphasis ? 3 : 0, 0, 0)
            });
        }
        return new ToolTip
        {
            Content = lines,
            Padding = new Thickness(10, 7, 10, 7)
        };
    }

    private static string Format(string value, bool percent) =>
        value == "?" ? "?" : value + (percent ? "%" : "");

    // Separate label and value typography without adding visible card chrome.
    private static void SetMetric(TextBlock text, string label, string value, bool light,
        string suffix = "")
    {
        text.Inlines.Clear();
        text.Inlines.Add(new Run(label + " ")
        {
            Foreground = new SolidColorBrush(light
                ? Color.FromRgb(85, 93, 108)
                : Color.FromRgb(181, 189, 201)),
            FontWeight = FontWeights.Medium
        });
        text.Inlines.Add(new Run(value)
        {
            Foreground = new SolidColorBrush(light
                ? Color.FromRgb(21, 28, 40)
                : Color.FromRgb(246, 249, 252)),
            FontWeight = FontWeights.SemiBold
        });
        if (suffix.Length > 0)
            text.Inlines.Add(new Run(suffix)
            {
                Foreground = new SolidColorBrush(light
                    ? Color.FromRgb(85, 93, 108)
                    : Color.FromRgb(181, 189, 201)),
                FontWeight = FontWeights.Normal
            });
    }

    private void ApplyHoverBackground(bool light)
    {
        if (background is null) return;
        // Normally transparent; only a faint Windows-style interactive hover.
        background.Background = hovered
            ? new SolidColorBrush(light ? Color.FromArgb(17, 16, 23, 35)
                : Color.FromArgb(28, 255, 255, 255))
            : Brushes.Transparent;
    }

    private static bool IsLightTaskbar()
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
    }

    private async Task AttachAsync()
    {
        if (disposed || attaching) return;
        attaching = true;
        try
        {
            CreateHostedWindow();
            var attached = host!;
            // If Explorer is unavailable, never leave a regular floating window.
            await attached.AttachWhenLayoutReadyAsync().WaitAsync(TimeSpan.FromSeconds(20));
            if (disposed || !ReferenceEquals(host, attached)) return;
            if (!attached.IsAttached)
            {
                // An attachment that completed without a valid taskbar host
                // must be released so the timer can retry after Explorer returns.
                ReleaseHostedWindow();
                return;
            }
            hostedWindow!.Show();
            Refresh();
        }
        catch (Exception error)
        {
            // Presentation failure must never interfere with credential management.
            Trace.TraceWarning("Taskbar quota attach unavailable: " + error.GetType().Name);
            ReleaseHostedWindow();
        }
        finally { attaching = false; }
    }

    private void CreateHostedWindow()
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            // Keep the whole glyph group right-anchored inside the existing
            // 166-DIP taskbar slot. Windows notification area stays untouched.
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };

        fiveHour = Text("5H ?");
        weekly = Text("7D ?");
        credits = Text("R ?");
        separatorOne = Separator();
        separatorTwo = Separator();
        row.Children.Add(fiveHour);
        row.Children.Add(separatorOne);
        row.Children.Add(weekly);
        row.Children.Add(separatorTwo);
        row.Children.Add(credits);

        background = new Border
        {
            CornerRadius = new CornerRadius(4),
            Height = IndicatorHeight,
            VerticalAlignment = VerticalAlignment.Center,
            // R-label trailing inset ~10 DIP including TextBlock margin.
            // At 150% scale this keeps ~15 px between R and the ^ button area.
            Padding = new Thickness(3, 0, 9, 0),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = "Codex 계정전환 · 클릭해서 계정 관리",
            Child = row
        };
        background.MouseEnter += (_, _) => { hovered = true; ApplyHoverBackground(IsLightTaskbar()); };
        background.MouseLeave += (_, _) => { hovered = false; ApplyHoverBackground(IsLightTaskbar()); };
        background.MouseLeftButtonUp += (_, _) => openAccounts();
        var rightClickMenu = new ContextMenu();
        var openItem = new MenuItem { Header = "Codex Account Switcher 열기" };
        openItem.Click += (_, _) => openAccounts();
        var exitItem = new MenuItem { Header = "종료" };
        exitItem.Click += (_, _) => quitApp();
        rightClickMenu.Items.Add(openItem);
        rightClickMenu.Items.Add(new Separator());
        rightClickMenu.Items.Add(exitItem);
        rightClickMenu.Opened += (_, _) => exitItem.IsEnabled = canQuit();
        background.ContextMenu = rightClickMenu;

        // The transparent 48-DIP host fills the real taskbar height; only
        // the interactive 30-DIP strip is vertically centered and hoverable.
        taskbarCanvas = new Grid
        {
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        taskbarCanvas.Children.Add(background);

        hostedWindow = new Window
        {
            Title = "Codex Usage — Taskbar",
            Width = EffectiveWidth,
            Height = PreferredHeight,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            ResizeMode = ResizeMode.NoResize,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            Content = taskbarCanvas
        };

        host = new TaskbarContentHost(hostedWindow, taskbarCanvas,
            new TaskbarContentHostOptions
            {
                Placement = Placement,
                PreferredMonitorIdentity = 0,
                PreferredWidth = EffectiveWidth,
                PreferredHeight = PreferredHeight,
                AllowFixedSlotResize = true,
                TrackTaskbarButtons = true,
                TrackNotificationArea = true,
                AnimateLayoutChanges = false,
                HighRefreshRateMode = false,
                LayoutRefreshInterval = TimeSpan.FromSeconds(1)
            });
        host.TaskbarWindowRecreationRequired += OnWindowRecreationRequired;
        Refresh();
    }

    private TextBlock Text(string value) => new()
    {
        Text = value,
        FontSize = taskbarFontSize,
        FontFamily = new FontFamily("Segoe UI"),
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(1, 0, 1, 0),
        TextAlignment = TextAlignment.Center
    };

    private static Border Separator() => new()
    {
        Width = 1,
        Height = 14,
        Margin = new Thickness(7, 0, 7, 0),
        VerticalAlignment = VerticalAlignment.Center,
        IsHitTestVisible = false
    };

    private async void OnWindowRecreationRequired(object? sender, EventArgs e)
    {
        if (disposed || recreating || !ReferenceEquals(sender, host)) return;
        recreating = true;
        try
        {
            ReleaseHostedWindow();
            await Task.Delay(900);
            if (!disposed) await AttachAsync();
        }
        catch (Exception error)
        {
            Trace.TraceWarning("Taskbar quota recreation unavailable: " + error.GetType().Name);
        }
        finally { recreating = false; }
    }

    private void ReleaseHostedWindow()
    {
        var oldHost = host;
        host = null;
        if (oldHost != null)
        {
            oldHost.TaskbarWindowRecreationRequired -= OnWindowRecreationRequired;
            oldHost.Dispose();
        }

        var oldWindow = hostedWindow;
        hostedWindow = null;
        fiveHour = null;
        weekly = null;
        credits = null;
        background = null;
        taskbarCanvas = null;
        separatorOne = null;
        separatorTwo = null;
        hovered = false;
        oldWindow?.Close();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        refreshTimer.Stop();
        appearance.Changed -= OnAppearanceChanged;
        ReleaseHostedWindow();
    }
}
