using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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
    internal const double PreferredWidth = 230;
    internal const double PreferredHeight = 36;
    internal const TaskbarContentPlacement Placement = TaskbarContentPlacement.BeforeNotificationArea;

    private readonly IntegratedMiniBarState state = new();
    private readonly Action openAccounts;
    private readonly Action quitApp;
    private readonly Func<bool> canQuit;
    private readonly DispatcherTimer refreshTimer;
    private TaskbarContentHost? host;
    private Window? hostedWindow;
    private TextBlock? fiveHour;
    private TextBlock? weekly;
    private TextBlock? credits;
    private Border? background;
    private bool attaching;
    private bool recreating;
    private bool disposed;

    internal TaskbarQuotaPanel(Action openAccounts, Action quitApp, Func<bool> canQuit)
    {
        this.openAccounts = openAccounts;
        this.quitApp = quitApp;
        this.canQuit = canQuit;
        refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        refreshTimer.Tick += (_, _) => Refresh();
        refreshTimer.Start();
    }

    internal void Start() => _ = AttachAsync();

    internal void Accept(CodexMonitorWireSample sample)
    {
        if (disposed) return;
        state.TryApply(sample, DateTimeOffset.UtcNow);
        Refresh();
    }

    internal void Invalidate()
    {
        if (disposed) return;
        state.Invalidate(DateTimeOffset.UtcNow);
        Refresh();
    }

    internal MiniBarReadout CurrentReadout => state.Read(DateTimeOffset.UtcNow);

    internal void Refresh()
    {
        if (disposed || fiveHour is null || weekly is null || credits is null) return;

        var readout = TrayQuotaReadout.From(CurrentReadout);
        fiveHour.Text = "5H " + Format(readout.FiveHourIcon, percent: true);
        weekly.Text = "7D " + Format(readout.WeeklyIcon, percent: true);
        credits.Text = "R " + readout.CreditsIcon;
        fiveHour.ToolTip = readout.FiveHourTooltip;
        weekly.ToolTip = readout.WeeklyTooltip;
        credits.ToolTip = readout.CreditsTooltip;

        var light = IsLightTaskbar();
        var ink = light ? Color.FromRgb(28, 36, 47) : Color.FromRgb(240, 244, 249);
        var fill = light ? Color.FromArgb(232, 235, 242, 252) : Color.FromArgb(210, 38, 42, 50);
        foreach (var text in new[] { fiveHour, weekly, credits })
            text.Foreground = new SolidColorBrush(ink);
        if (background != null) background.Background = new SolidColorBrush(fill);
    }

    private static string Format(string value, bool percent) =>
        value == "?" ? "?" : value + (percent ? "%" : "");

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
            if (disposed || !ReferenceEquals(host, attached) || !attached.IsAttached) return;
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
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        fiveHour = Text("5H ?");
        weekly = Text("7D ?");
        credits = Text("R ?");
        row.Children.Add(fiveHour);
        row.Children.Add(weekly);
        row.Children.Add(credits);

        background = new Border
        {
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(2, 0, 2, 0),
            Child = row
        };
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

        hostedWindow = new Window
        {
            Title = "Codex Usage — Taskbar",
            Width = PreferredWidth,
            Height = PreferredHeight,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            ResizeMode = ResizeMode.NoResize,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            Content = background
        };

        host = new TaskbarContentHost(hostedWindow, background,
            new TaskbarContentHostOptions
            {
                Placement = Placement,
                PreferredMonitorIdentity = 0,
                PreferredWidth = PreferredWidth,
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

    private static TextBlock Text(string value) => new()
    {
        Text = value,
        FontSize = 11,
        FontWeight = FontWeights.SemiBold,
        FontFamily = new FontFamily("Segoe UI"),
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(5, 0, 5, 0)
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
        oldWindow?.Close();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        refreshTimer.Stop();
        ReleaseHostedWindow();
    }
}
