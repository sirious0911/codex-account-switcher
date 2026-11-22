using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CodexAccountSwitcher.Core;

namespace CodexAccountSwitcher;

public sealed class MainWindow : Window
{
    private readonly IAccountClient client;
    private readonly NativeSettings native;
    private readonly Func<IReadOnlyList<LocalMonitorHistoryRow>> history;
    private readonly Func<OfflineTelegramInboxSnapshot> recoveryInbox;
    private readonly Func<string> localTelegramSenderStatus;
    private readonly Func<string> bundledTelegramScriptsStatus;
    private readonly Func<string> existingAmanoBotRouteStatus;
    private readonly Func<string> telegramProductionOutboxStatus;
    private readonly Func<IReadOnlyList<WindowsTelegramApprovalRow>> pendingTelegramApprovals;
    private readonly Func<WindowsTelegramApprovalRow, bool, Task<string>>? approvedTelegramSender;
    private readonly Func<TelegramNotificationLane, string>? telegramConnectivityTestStager;
    private readonly Func<bool> telegramAutomaticSessionArmed;
    private readonly Func<bool> stickyWindowsOnlyAutoConsent;
    private readonly Func<WindowsTelegramRestartIntentStatus> restartIntentStatus;
    private readonly Func<bool, bool>? restartIntentChange;
    private readonly Func<string> restartReadinessStatus;
    private readonly Func<bool, bool, bool>? telegramAutomaticSessionAction;
    private readonly Func<WindowsTelegramHandoffStatus>? telegramWindowsHandoffAction;
    private readonly Func<string, string, string, string, Task<string>>?
        authorityStatusReadOnlyProbe;
    private readonly PersonalExistingThreadContext? dedicatedContext;
    private DedicatedThreadPickerPanel? dedicatedPanel;
    private bool authorityReadInProgress;
    private bool authorityDisclosureExpanded;
    private string authorityReadOnlyDisplay =
        "Authority: 미연결 · 수동 조회만 가능 · Telegram 송신 권한 없음";
    private bool telegramAdvancedExpanded;
    private double settingsWindowWidth = 540;
    private double settingsWindowHeight = 700;
    private WindowsTelegramApprovalRow? selectedTelegramApproval;
    private string? telegramApprovalResult;
    private bool telegramApprovalBusy;
    private string page = "accounts";
    private AccountRow? target;
    private string? localError;
    private bool closingForExit;
    // P3 remote viewing is explicit, read-only, memory-only, and separate
    // from the local active-profile taskbar publication gate.
    private QuotaRelayHttpsViewer? remoteViewer;
    private QuotaRelayReadOnlyResult? remoteObservation;
    private QuotaRelayV2HttpsViewer? remoteViewerV2;
    private QuotaRelayV2ReadOnlyResult? remoteObservationV2;
    private bool remoteIsV2;
    private string? remoteSourceDevice;
    private bool remoteReading;
    private readonly DispatcherTimer remotePoll = new() {
        Interval = TimeSpan.FromMinutes(6)
    };
    public bool IsBusy => client.State.IsMutating;
    public string CurrentPage => page;
    private AccountSnapshot State => client.State;
    private string T(string key) => State.Text(key);
    private Brush B(string key) => (Brush)FindResource(key);

    public MainWindow(IAccountClient client, NativeSettings? native = null,
        Func<IReadOnlyList<LocalMonitorHistoryRow>>? monitorHistory = null,
        Func<OfflineTelegramInboxSnapshot>? telegramRecoveryInbox = null,
        Func<string>? telegramLocalSenderStatus = null,
        Func<string>? packagedTelegramScriptsStatus = null,
        Func<string>? existingAmanoBotStatus = null,
        Func<string>? productionTelegramOutboxStatus = null,
        Func<IReadOnlyList<WindowsTelegramApprovalRow>>? telegramPendingApprovals = null,
        Func<WindowsTelegramApprovalRow, bool, Task<string>>? telegramApprovedSend = null,
        Func<TelegramNotificationLane, string>? telegramTestStage = null,
        Func<bool>? telegramAutoArmed = null,
        Func<bool, bool, bool>? telegramAutoAction = null,
        Func<WindowsTelegramHandoffStatus>? telegramHandoff = null,
        Func<string, string, string, string, Task<string>>? authorityReadOnly = null,
        Func<WindowsTelegramRestartIntentStatus>? readRestartIntent = null,
        Func<bool, bool>? setRestartIntent = null,
        Func<string>? readRestartReadiness = null,
        Func<bool>? readStickyWindowsOnlyAutoConsent = null,
        string? dedicatedExistingThreadDataHome = null)
    {
        this.client = client; this.native = native ?? new NativeSettings();
        history = monitorHistory ?? (() => []);
        recoveryInbox = telegramRecoveryInbox ?? (() => OfflineTelegramInboxSnapshot.Empty);
        localTelegramSenderStatus = telegramLocalSenderStatus ??
            (() => "Windows 로컬 Sender: 검증하지 않음 · 실제 발송 차단");
        bundledTelegramScriptsStatus = packagedTelegramScriptsStatus ??
            (() => "내장 Telegram 스크립트: 검증되지 않음 · 실제 발송 차단");
        existingAmanoBotRouteStatus = existingAmanoBotStatus ??
            (() => "기존 주차대행 Telegram 봇: 전송 경로 미확인 · 실제 발송 차단");
        telegramProductionOutboxStatus = productionTelegramOutboxStatus ??
            (() => "운영 알림 대기함: 연결되지 않음 · 실제 발송 차단");
        pendingTelegramApprovals = telegramPendingApprovals ?? (() => []);
        approvedTelegramSender = telegramApprovedSend;
        telegramConnectivityTestStager = telegramTestStage;
        telegramAutomaticSessionArmed = telegramAutoArmed ?? (() => false);
        stickyWindowsOnlyAutoConsent = readStickyWindowsOnlyAutoConsent ?? (() => false);
        telegramAutomaticSessionAction = telegramAutoAction;
        telegramWindowsHandoffAction = telegramHandoff;
        authorityStatusReadOnlyProbe = authorityReadOnly;
        restartIntentStatus = readRestartIntent ??
            (() => WindowsTelegramRestartIntentStatus.Missing);
        restartIntentChange = setRestartIntent;
        restartReadinessStatus = readRestartReadiness ??
            (() => "중앙 Sender 상태 확인 불가 · 실제 Telegram 자동발송 OFF");
        if (!string.IsNullOrWhiteSpace(dedicatedExistingThreadDataHome))
            dedicatedContext = new PersonalExistingThreadContext(client,
                dedicatedExistingThreadDataHome);
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(App.IconUri);
        Title = "Codex Account Switcher Personal"; Width = 420; SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.SingleBorderWindow; ResizeMode = ResizeMode.CanResize;
        MinWidth = 420; MinHeight = 260;
        Background = B("Surface"); ShowInTaskbar = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        MaxHeight = SystemParameters.WorkArea.Height - 24;
        client.Changed += Render; this.native.Changed += Render;
        remotePoll.Tick += (_, _) => _ = RefreshRemoteAsync();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && !IsBusy && page != "accounts") { Navigate("accounts"); e.Handled = true; } };
        Closing += (_, e) => {
            if (closingForExit) return;
            e.Cancel = true;
            Hide();
        };
        Closed += (_, _) => {
            remotePoll.Stop();
            remoteViewer?.Dispose();
            remoteViewerV2?.Dispose();
            remoteViewer = null;
            remoteViewerV2 = null;
            client.Changed -= Render;
            this.native.Changed -= Render;
        };
        Render();
    }

    public void CloseForExit() { closingForExit = true; Close(); }

    public void OpenWindow()
    {
        if (State.IsAddingAccount) Navigate("manage");
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        _ = Run("refresh");
    }
    public void Navigate(string destination)
    {
        // Keep the account switcher compact. Settings uses a user-resizable
        // width/height, remembered only while the current app is open.
        if (page == "settings" && destination != "settings")
        {
            if (WindowState == WindowState.Normal)
            {
                settingsWindowWidth = Math.Max(MinWidth, ActualWidth);
                settingsWindowHeight = Math.Max(MinHeight, ActualHeight);
            }
        }
        if (destination == "settings")
        {
            SizeToContent = SizeToContent.Manual;
            Width = settingsWindowWidth;
            Height = Math.Min(settingsWindowHeight, MaxHeight);
        }
        else
        {
            SizeToContent = SizeToContent.Height;
            Width = 420;
        }
        page = destination; target = null; localError = null; Render();
    }
    private async Task Run(string command, Guid? id = null, bool? value = null, string? language = null)
    {
        try { await client.CommandAsync(command, id, value, language); }
        catch (Exception ex) { localError = ex.Message; Render(); }
    }
    private TextBlock Text(string text, double size = 13, bool muted = false, bool bold = false) => new() {
        Text = text, FontSize = size, Foreground = B(muted ? "Muted" : "Ink"),
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
    };
    private Button Button(string label, Action action, string? glyph = null, bool left = false)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Center };
        if (glyph != null) content.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), Margin = new Thickness(0, 0, label.Length > 0 ? 7 : 0, 0), VerticalAlignment = VerticalAlignment.Center });
        if (label.Length > 0) content.Children.Add(new TextBlock { Text = label, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button { Content = content, IsEnabled = !IsBusy, Padding = new Thickness(12, 6, 12, 6), MinHeight = 32, MinWidth = 32 };
        AutomationProperties.SetName(button, label); button.Click += (_, _) => action(); return button;
    }
    private Border Rule() => new() { Height = 1, Background = B("Line") };
    private Grid Pair(UIElement left, UIElement right)
    {
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(right, 1); grid.Children.Add(left); grid.Children.Add(right); return grid;
    }

    private void Render()
    {
        var body = new StackPanel();
        if (State.Error != null || localError != null) {
            var message = Text(localError ?? State.Error!, 11, muted: true); message.TextWrapping = TextWrapping.Wrap;
            var box = Pair(message, Button("×", () => { localError = null; _ = Run("dismissError"); Render(); }));
            box.Margin = new Thickness(12, 8, 12, 10); body.Children.Add(box); body.Children.Add(Rule());
        }
        if (page == "accounts") {
            var commands = new StackPanel { Orientation = Orientation.Horizontal };
            commands.Children.Add(Button(T("manage"), () => Navigate("manage"), "\uE716"));
            var monitorButton = Button("", () => Navigate("monitor"), "\uE9CE");
            monitorButton.Margin = new Thickness(8, 0, 0, 0);
            monitorButton.ToolTip = T("monitor_title");
            AutomationProperties.SetName(monitorButton, T("monitor_title"));
            commands.Children.Add(monitorButton);
            var settings = Button("", () => Navigate("settings"), "\uE713");
            settings.Margin = new Thickness(8, 0, 0, 0); settings.ToolTip = T("settings");
            AutomationProperties.SetName(settings, T("settings"));
            if (native.UpdatePage != null) {
                var glyph = (UIElement)settings.Content; settings.Content = null;
                var indicator = new Grid(); indicator.Children.Add(glyph);
                indicator.Children.Add(new System.Windows.Shapes.Ellipse { Width = 5, Height = 5, Fill = B("Accent"),
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -3, -4, 0) });
                settings.Content = indicator;
            }
            commands.Children.Add(settings);
            var heading = Pair(Text(T("accounts"), 20, bold: true), commands);
            heading.Margin = new Thickness(20, 16, 20, 16); body.Children.Add(heading);
        } else {
            var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 16, 20, 16) };
            header.Children.Add(Button("", () => Navigate(page == "remove" ? "manage" :
                page == "telegram-approve" ? "monitor" : "accounts"), "\uE72B"));
            AutomationProperties.SetName(header.Children[0], T("back"));
            var title = target != null && page is "switch" or "remove"
                ? T(page + "_title").Replace("%@", target.Profile.DisplayName)
                : page == "telegram-approve" ? "Telegram 알림 1건 발송 검토"
                : T(page == "settings" ? "settings" : page == "monitor" ? "monitor_title" : "accounts");
            var heading = Text(title, 20, bold: true); heading.Margin = new Thickness(12, 0, 0, 0); header.Children.Add(heading);
            body.Children.Add(header);
        }
        if (page is "switch" or "remove") Confirmation(body);
        else if (page == "settings") Settings(body);
        else if (page == "monitor") Monitor(body);
        else if (page == "telegram-approve") TelegramApprovalReview(body);
        else Accounts(body, page == "manage");
        Content = new Border { Background = B("Surface"),
            Child = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } };
    }

    private void Accounts(StackPanel body, bool manage)
    {
        if (!manage && !State.ActiveIdentityConfirmed) {
            var warning = new StackPanel { Margin = new Thickness(12, 8, 12, 4) };
            warning.Children.Add(Text(T("active_unconfirmed"), 11, muted: true));
            warning.Children.Add(Button(T("manage"), () => Navigate("manage"), left: true)); body.Children.Add(warning);
        }
        var list = new StackPanel();
        foreach (var row in State.Accounts) {
            var avatar = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(4), Background = B("Hover"),
                Child = Text(row.Initials, 11, bold: true) };
            ((TextBlock)avatar.Child).HorizontalAlignment = HorizontalAlignment.Center;
            var details = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var active = row.Profile.Id == State.ActiveAccountID;
            var title = string.IsNullOrEmpty(row.Profile.Email) ? row.Profile.DisplayName : row.Profile.Email;
            var name = Text(title, 14, bold: true);
            name.ToolTip = title;
            if (manage) {
                details.Children.Add(name);
            } else {
                var reset = Text(row.Usage != null && !State.Settings.ShowsFiveHourUsage ? Reset(row.Usage.ResetsAt) : "", 10.5, muted: true);
                reset.Margin = new Thickness(7, 0, 0, 0); details.Children.Add(Pair(name, reset));
                if (row.Usage is { } usage) {
                    if (State.Settings.ShowsFiveHourUsage && usage.FiveHourRemainingPercent is { } five && usage.FiveHourResetsAt is { } time)
                        details.Children.Add(UsageLine(T("five_hour"), five, Reset(time, false)));
                    details.Children.Add(UsageLine(T(State.Settings.ShowsFiveHourUsage ? "weekly" : "usage"), usage.RemainingPercent,
                        State.Settings.ShowsFiveHourUsage ? Reset(usage.ResetsAt) : null));
                    details.ToolTip = row.UsageError;
                } else if (row.NonWeeklyUsage is { } other) {
                    // Free may expose 30D but no weekly / five-hour window.
                    // Show the provider's exact duration rather than a fake 7D.
                    details.Children.Add(UsageLine(other.WindowLabel, other.RemainingPercent,
                        Reset(other.ResetsAt)));
                    details.ToolTip = row.UsageError;
                } else {
                    var missing = Text(row.UsageStatus == "idle" ? T("usage") + " —" : T("usage_unavailable"), 10.5, muted: true);
                    missing.ToolTip = row.UsageError; details.Children.Add(missing);
                }
                var accountInfo = new Grid();
                accountInfo.ColumnDefinitions.Add(new ColumnDefinition());
                accountInfo.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                if (row.Usage is not null || row.NonWeeklyUsage is not null) {
                    var remaining = row.Usage?.ResetCreditsRemaining ?? row.NonWeeklyUsage?.ResetCreditsRemaining;
                    var resetCredits = Text(T("reset_credits") + ": " + (remaining?.ToString(CultureInfo.CurrentCulture) ?? T("unknown")), 10.5, muted: true);
                    resetCredits.Margin = new Thickness(8, 0, 0, 0);
                    Grid.SetColumn(resetCredits, 1); accountInfo.Children.Add(resetCredits);
                }
                TextBlock subscriptionLine;
                if (row.Subscription is { } subscription) {
                    var date = subscription.ActiveUntil.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    subscriptionLine = Text(T("subscription_until").Replace("%@", date), 10.5, muted: true);
                    subscriptionLine.ToolTip = subscriptionLine.Text + "\n" + T("subscription_detail").Replace("%@", subscription.LastChecked.ToLocalTime().ToString("yyyy MMM d HH:mm", CultureInfo.CurrentCulture));
                } else subscriptionLine = Text(T("subscription_unknown"), 10.5, muted: true);
                accountInfo.Children.Add(subscriptionLine);
                details.Children.Add(accountInfo);
            }
            var grid = new Grid { MinHeight = manage ? 32 : 50 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.Children.Add(avatar); Grid.SetColumn(details, 1); grid.Children.Add(details);
            if (manage) {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                UIElement action = active ? Text(T("active"), 11, muted: true) : Button("", () => { target = row; page = "remove"; Render(); }, "\uE74D");
                AutomationProperties.SetName(action, active ? T("active") : T("remove")); Grid.SetColumn(action, 2); grid.Children.Add(action);
                list.Children.Add(new Border { Padding = new Thickness(16, 12, 16, 12), Child = grid });
            } else {
                var button = new Button { Content = grid, Style = (Style)FindResource("AccountRow"), Background = active ? B("Selected") : Brushes.Transparent, IsEnabled = !IsBusy };
                AutomationProperties.SetName(button, title);
                button.Click += (_, _) => { if (!active) { target = row; page = "switch"; Render(); } };
                var selection = new Border { Width = 3, Height = 24, Background = active ? B("Accent") : Brushes.Transparent,
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
                var rowContainer = new Grid(); rowContainer.Children.Add(button); rowContainer.Children.Add(selection);
                list.Children.Add(rowContainer);
            }
        }
        if (State.Accounts.Length == 0) { var empty = Text(T("no_accounts"), 12, muted: true); empty.Margin = new Thickness(12, 24, 12, 24); list.Children.Add(empty); }
        body.Children.Add(new Border { Margin = new Thickness(20, 0, 20, 20), BorderBrush = B("Line"), BorderThickness = new Thickness(1),
            Background = B("ListSurface"), CornerRadius = new CornerRadius(4), Child = list });
        if (manage) {
            var actions = new StackPanel { Margin = new Thickness(20, 0, 20, 20) };
            var add = Button(T(State.IsAddingAccount ? "cancel_add_account" : "add_account"), () => _ = Run(State.IsAddingAccount ? "cancelAdd" : "add"), State.IsAddingAccount ? "\uE711" : "\uE710");
            var hint = Text(T(State.IsAddingAccount ? "sign_in_pending_hint" : "sign_in_hint"), 12, muted: true);
            hint.ToolTip = hint.Text;
            hint.Margin = new Thickness(8, 0, 0, 0);
            var addRow = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            addRow.ColumnDefinitions.Add(new ColumnDefinition());
            addRow.Children.Add(add); Grid.SetColumn(hint, 1); addRow.Children.Add(hint);
            actions.Children.Add(addRow);
            var register = Button(T("register_current_account"), () => _ = Run("register"));
            register.IsEnabled &= !State.IsAddingAccount; register.HorizontalAlignment = HorizontalAlignment.Left;
            actions.Children.Add(register); body.Children.Add(actions);
        }
    }

    private string Reset(DateTimeOffset date, bool includeDate = true) => T("resets") + " " + date.ToLocalTime().ToString(includeDate ? "MMM d HH:mm" : "HH:mm", CultureInfo.CurrentCulture);
    private Grid UsageLine(string title, int percent, string? reset)
    {
        var grid = new Grid { Margin = new Thickness(0, 5, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(Text(title, 10.5, muted: true));
        var track = new Grid { Height = 4, Margin = new Thickness(7, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
        track.Children.Add(new Border { Background = B("Line"), CornerRadius = new CornerRadius(1) });
        var fill = new Border { Background = B("Accent"), CornerRadius = new CornerRadius(1), HorizontalAlignment = HorizontalAlignment.Left };
        track.SizeChanged += (_, _) => fill.Width = track.ActualWidth * percent / 100.0; track.Children.Add(fill);
        AutomationProperties.SetName(track, title + " " + percent + "%"); Grid.SetColumn(track, 1); grid.Children.Add(track);
        var remaining = Text(reset == null ? percent + T("left") : percent + "%  " + reset, reset == null ? 10.5 : 9.5, muted: true);
        Grid.SetColumn(remaining, 2); grid.Children.Add(remaining); return grid;
    }
    private void Confirmation(StackPanel body)
    {
        if (target == null) return;
        var content = new StackPanel { Margin = new Thickness(20, 0, 20, 20) };
        var copy = T(page == "switch" ? "switch_body" : "remove_body").Replace("这台 Mac", "这台电脑").Replace("this Mac", "this PC");
        var text = Text(copy, 13, muted: true); text.TextWrapping = TextWrapping.Wrap; text.Margin = new Thickness(0, 0, 0, 14); content.Children.Add(text);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(Button(T("cancel"), () => Navigate(page == "remove" ? "manage" : "accounts")));
        var command = page; var id = target.Profile.Id;
        var confirm = Button(T(command), () => { Navigate(command == "remove" ? "manage" : "accounts"); _ = Run(command, id); });
        confirm.Margin = new Thickness(8, 0, 0, 0); confirm.BorderBrush = B("Accent"); confirm.IsDefault = true; buttons.Children.Add(confirm); content.Children.Add(buttons); body.Children.Add(content);
    }
    private void Monitor(StackPanel body)
    {
        var content = new StackPanel { Margin = new Thickness(20, 0, 20, 20) };
        var note = Text(T("monitor_source_hint"), 11, muted: true);
        note.TextWrapping = TextWrapping.Wrap;
        note.Margin = new Thickness(0, 0, 0, 12);
        content.Children.Add(note);
        RemoteQuotaSection(content);
        var rows = MonitorOverview.From(State);
        if (rows.Count == 0) content.Children.Add(Text(T("no_accounts"), 12, muted: true));
        foreach (var row in rows) {
            var section = new StackPanel { Margin = new Thickness(0, 0, 0, 15) };
            section.Children.Add(Text(row.AccountLabel + (row.IsActive ? " · " + T("active") : ""), 13, bold: true));
            if (row.HasUsageError) {
                var status = Text(T("monitor_last_known"), 10.5, muted: true);
                status.Margin = new Thickness(0, 3, 0, 0);
                section.Children.Add(status);
            }
            if (row.FiveHourRemainingPercent is { } five && row.FiveHourResetsAt is { } fiveReset)
                section.Children.Add(UsageLine(T("five_hour"), five, Reset(fiveReset)));
            else section.Children.Add(Text(T("five_hour") + ": " + T("unknown"), 11, muted: true));
            if (row.WeeklyRemainingPercent is { } week && row.WeeklyResetsAt is { } weekReset)
                section.Children.Add(UsageLine(T("weekly"), week, Reset(weekReset)));
            else section.Children.Add(Text(T("weekly") + ": " + T("unknown"), 11, muted: true));
            var credits = Text(T("reset_credits") + ": " +
                (row.ResetCreditsRemaining?.ToString(CultureInfo.CurrentCulture) ?? T("unknown")), 11, muted: true);
            credits.Margin = new Thickness(0, 4, 0, 0);
            section.Children.Add(credits);
            content.Children.Add(section);
        }
        // Existing 5H/7D detector ledgers now have a user-visible, strictly
        // offline review surface. No reservation or Telegram send occurs.
        var recoveryHeading = Text("Telegram 복구 알림 대기함 (오프라인)", 13, bold: true);
        recoveryHeading.Margin = new Thickness(0, 12, 0, 6);
        content.Children.Add(recoveryHeading);
        var recoveryNote = Text(
            "5H → 모니터 봇(묵음) / 7D → 얼럿 봇. 모든 상태는 모의 기록이며 " +
            "Telegram 실제 발송·수신 확인이 아닙니다.", 10.5, muted: true);
        recoveryNote.TextWrapping = TextWrapping.Wrap;
        content.Children.Add(recoveryNote);
        var inbox = recoveryInbox();
        if (inbox.LedgerReadError)
            content.Children.Add(Text("일부 로컬 복구 원장을 읽을 수 없습니다.", 10.5, muted: true));
        if (inbox.Rows.Count == 0)
            content.Children.Add(Text("기록된 5H·7D 복구 알림 없음", 10.5, muted: true));
        foreach (var entry in inbox.Rows)
        {
            var line = Text(entry.Lane + " → " + entry.Destination + " · " +
                entry.ObservedAt.ToOffset(TimeSpan.FromHours(9))
                    .ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) + " KST · " +
                entry.StatusLabel, 10.5, muted: true);
            line.ToolTip = "로컬 모의 이벤트 " + entry.EventReference +
                " · 실제 Telegram 발송/수신 증거 없음";
            line.Margin = new Thickness(0, 3, 0, 0);
            content.Children.Add(line);
        }
        var productionHeading = Text("Telegram 운영 알림 (승인 전 발송 없음)", 13, bold: true);
        productionHeading.Margin = new Thickness(0, 12, 0, 6);
        content.Children.Add(productionHeading);
        var productionSummary = Text(telegramProductionOutboxStatus(), 10.5, muted: true);
        productionSummary.TextWrapping = TextWrapping.Wrap;
        content.Children.Add(productionSummary);
        if (telegramApprovalResult is not null)
        {
            var recentResult = Text(telegramApprovalResult, 10.5, muted: true);
            recentResult.TextWrapping = TextWrapping.Wrap;
            content.Children.Add(recentResult);
        }
        if (telegramConnectivityTestStager is not null)
        {
            var testHint = Text(
                "실제 100% 복구를 기다리지 않고 기존 봇 연결을 시험할 수 있습니다. " +
                "테스트 항목 생성은 메시지를 발송하지 않습니다.",
                10.5, muted: true);
            testHint.TextWrapping = TextWrapping.Wrap;
            content.Children.Add(testHint);
            foreach (var testLane in new[] {
                TelegramNotificationLane.FiveHourRecovery,
                TelegramNotificationLane.WeeklyRecovery })
            {
                var lane = testLane;
                var label = lane == TelegramNotificationLane.FiveHourRecovery
                    ? "5H 묵음 테스트 항목 생성" : "7D 일반 알림 테스트 항목 생성";
                var createTest = Button(label, () =>
                {
                    telegramApprovalResult = telegramConnectivityTestStager(lane);
                    Navigate("monitor");
                });
                createTest.Margin = new Thickness(0, 5, 0, 0);
                content.Children.Add(createTest);
            }
        }
        var waiting = pendingTelegramApprovals();
        if (waiting.Count > 0)
        {
            var reviewNote = Text(
                "대기 항목은 자동 발송되지 않습니다. 한 건씩 검토하고 직접 승인해야 합니다.",
                10.5, muted: true);
            reviewNote.TextWrapping = TextWrapping.Wrap;
            content.Children.Add(reviewNote);
            foreach (var item in waiting.Take(5))
            {
                var entry = item;
                var description = Text(entry.LaneLabel + " · " +
                    entry.ObservedAt.ToOffset(TimeSpan.FromHours(9))
                        .ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) +
                    " KST", 10.5, muted: true);
                description.ToolTip = "대기 이벤트: " + entry.EventKey;
                var openApproval = Button("1건 검토", () =>
                {
                    selectedTelegramApproval = entry;
                    Navigate("telegram-approve");
                });
                openApproval.IsEnabled &= approvedTelegramSender is not null;
                var row = Pair(description, openApproval);
                row.Margin = new Thickness(0, 5, 0, 0);
                content.Children.Add(row);
            }
        }
        var historyHeading = Text(T("monitor_history"), 13, bold: true);
        historyHeading.Margin = new Thickness(0, 12, 0, 8);
        content.Children.Add(historyHeading);
        var events = history();
        if (events.Count == 0) content.Children.Add(Text(T("monitor_history_empty"), 11, muted: true));
        foreach (var ev in events) {
            var kind = ev.EventKind switch {
                "CREDIT_INCREASE" => T("monitor_credit_increase"),
                "EARLY_RECOVERY_SUSPECTED" => T("monitor_early_recovery"),
                "SCHEDULED_RESET" => T("monitor_scheduled_reset"),
                _ => ev.EventKind
            };
            var label = Text(kind + " · " + ev.ObservedAt.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.CurrentCulture), 11);
            label.ToolTip = ev.AccountReference + " · " + T(ev.MockCompleted ? "monitor_mock_complete" : "monitor_mock_pending");
            label.Margin = new Thickness(0, 4, 0, 0);
            content.Children.Add(label);
        }
        content.Children.Add(Button(T("monitor_refresh"), () => _ = Run("refresh"), "\uE72C", left: true));
        body.Children.Add(content);
    }


    private void TelegramApprovalReview(StackPanel body)
    {
        var panel = new StackPanel { Margin = new Thickness(20, 0, 20, 20) };
        body.Children.Add(panel);
        var selected = selectedTelegramApproval;
        var current = selected is not null && pendingTelegramApprovals().Any(item =>
            item.Lane == selected.Lane && item.EventKey == selected.EventKey &&
            item.ObservedAt == selected.ObservedAt &&
            item.IsTest == selected.IsTest);
        if (!current || selected is null)
        {
            panel.Children.Add(Text("대기 항목이 없거나 이미 처리된 알림입니다.", 11, muted: true));
            return;
        }

        var identity = Text(selected.LaneLabel +
            (selected.IsTest ? " · 테스트 생성 " : " · 복구 관측 ") +
            selected.ObservedAt.ToOffset(TimeSpan.FromHours(9))
                .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " KST",
            12, bold: true);
        identity.TextWrapping = TextWrapping.Wrap;
        panel.Children.Add(identity);
        var reference = Text("이벤트 ID: " + selected.EventKey[..12] + "…", 10.5, muted: true);
        reference.ToolTip = selected.EventKey;
        panel.Children.Add(reference);

        var warning = Text(
            "이 버튼을 승인하면 기존 인천공항 주차대행 Telegram 봇을 통해 " +
            "이 알림을 실제로 1회 전송합니다. Telegram Bot API의 응답은 " +
            "휴대전화 수신 여부를 보증하지 않습니다. " +
            "다른 PC/Mac의 송신 중지는 앱이 원격으로 증명하지 못합니다.",
            10.5, muted: true);
        warning.TextWrapping = TextWrapping.Wrap;
        warning.Margin = new Thickness(0, 12, 0, 12);
        panel.Children.Add(warning);

        var senderIntent = native.TelegramPreferences;
        var permittedBySettings = selected.Lane == TelegramNotificationLane.FiveHourRecovery
            ? senderIntent.WantsFiveHour : senderIntent.WantsWeekly;
        if (!permittedBySettings)
        {
            panel.Children.Add(Text(
                "설정에서 현재 Windows PC를 Sender로 선택하고 해당 알림을 ON으로 해야 합니다.",
                11, muted: true));
        }
        if (approvedTelegramSender is null)
        {
            panel.Children.Add(Text(
                "현재 실행 파일에서는 Telegram 송신 기능을 사용할 수 없습니다.",
                11, muted: true));
        }

        var acknowledge = new CheckBox
        {
            Content = new TextBlock
            {
                Text = "다른 Windows/Mac에서 Telegram 송신이 OFF임을 직접 확인했습니다.",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 310
            },
            IsChecked = false,
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 10)
        };
        AutomationProperties.SetName(acknowledge, "다른 장치 Telegram 송신 OFF 확인");
        panel.Children.Add(acknowledge);
        var send = Button("선택한 알림 1건 실제 Telegram 발송", () => { });
        send.IsEnabled = false;
        acknowledge.Checked += (_, _) =>
            send.IsEnabled = !telegramApprovalBusy && !IsBusy &&
                permittedBySettings && approvedTelegramSender is not null;
        acknowledge.Unchecked += (_, _) => send.IsEnabled = false;
        send.Click += async (_, _) =>
        {
            if (telegramApprovalBusy || acknowledge.IsChecked != true ||
                approvedTelegramSender is null || selectedTelegramApproval != selected ||
                !permittedBySettings || IsBusy)
                return;
            var confirmation = MessageBox.Show(this,
                selected.LaneLabel + Environment.NewLine +
                "다른 장치의 발송이 중지된 것을 직접 확인했으며, " +
                "이 이벤트 1건을 기존 Telegram 봇으로 실제 전송하시겠습니까?" +
                Environment.NewLine + "전송 실패나 응답 손실 시 자동 재발송하지 않습니다.",
                "실제 Telegram 발송 최종 확인",
                MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (confirmation != MessageBoxResult.Yes) return;
            telegramApprovalBusy = true;
            send.IsEnabled = false;
            try
            {
                // Real process invocation is reachable ONLY from this
                // event-specific, double-confirmed WPF click, never from
                // an automatic recovery callback or saved preference.
                telegramApprovalResult = await approvedTelegramSender(selected, true);
            }
            catch (Exception)
            {
                telegramApprovalResult = "발송 상태 확인 불가 · 중복 방지를 위해 자동 재시도하지 않습니다.";
            }
            finally
            {
                telegramApprovalBusy = false;
                selectedTelegramApproval = null;
                Navigate("monitor");
            }
        };
        panel.Children.Add(send);
    }

    private void RemoteQuotaSection(StackPanel content)
    {
        var heading = Text("원격 기기 사용량 (P3)", 13, bold: true);
        heading.Margin = new Thickness(0, 5, 0, 7);
        content.Children.Add(heading);

        var note = Text(
            "Windows 로컬 사용량과 분리된 조회 전용 영역입니다. "
            + "HTTPS 자격정보는 메모리에서만 사용하고 저장하지 않습니다. "
            + "V2 조회는 R 개수·만료일을 표시하며 송신 기능은 별도 미연결입니다.", 10.5, muted: true);
        note.TextWrapping = TextWrapping.Wrap;
        note.Margin = new Thickness(0, 0, 0, 8);
        content.Children.Add(note);

        if (remoteViewer is null && remoteViewerV2 is null)
        {
            content.Children.Add(Text(
                "Mac 5H: ?  |  Mac 7D: ?  |  Mac R: ?", 12, muted: true));
            var address = new TextBox {
                Text = "", MinHeight = 28,
                ToolTip = "HTTPS 서버 주소. 예: https://relay.example.com/"
            };
            AutomationProperties.SetName(address, "원격 HTTPS 주소");
            content.Children.Add(Text("HTTPS 연결 주소", 11));
            content.Children.Add(address);

            var source = new TextBox { Text = "", MinHeight = 28,
                Margin = new Thickness(0, 4, 0, 0) };
            AutomationProperties.SetName(source, "원격 기기 ID");
            content.Children.Add(Text("원격 기기 ID", 11));
            content.Children.Add(source);

            var access = new PasswordBox { MinHeight = 28,
                Margin = new Thickness(0, 4, 0, 0) };
            AutomationProperties.SetName(access, "원격 조회 자격정보");
            content.Children.Add(Text("조회 전용 자격정보 (저장하지 않음)", 11));
            content.Children.Add(access);
            // V1 remains the default; user must explicitly select V2.
            var v2Option = new CheckBox {
                Content = "V2 조회 (R 초기화권·만료일 포함)",
                IsChecked = false, Margin = new Thickness(0, 8, 0, 0)
            };
            AutomationProperties.SetName(v2Option, "원격 V2 초기화권 조회");
            content.Children.Add(v2Option);
            var connect = Button("원격 사용량 연결", () => {
                try {
                    if (!Uri.TryCreate(address.Text.Trim(), UriKind.Absolute, out var url))
                        throw new ArgumentException("유효한 HTTPS 연결 주소가 필요합니다.");
                    var id = source.Text.Trim();
                    var enableV2 = v2Option.IsChecked == true;
                    QuotaRelayHttpsViewer? candidateV1 = enableV2
                        ? null : new QuotaRelayHttpsViewer(url, id, access.Password);
                    QuotaRelayV2HttpsViewer? candidateV2 = enableV2
                        ? new QuotaRelayV2HttpsViewer(url, id, access.Password) : null;
                    access.Clear();
                    remoteViewer?.Dispose();
                    remoteViewerV2?.Dispose();
                    remoteViewer = candidateV1;
                    remoteViewerV2 = candidateV2;
                    remoteIsV2 = enableV2;
                    remoteSourceDevice = id;
                    remoteObservation = null;
                    remoteObservationV2 = null;
                    remotePoll.Start();
                    _ = RefreshRemoteAsync();
                    Render();
                } catch (ArgumentException) {
                    localError = "HTTPS 주소, 원격 기기 ID, 조회 전용 자격정보를 확인하세요.";
                    access.Clear();
                    Render();
                }
            }, left: true);
            connect.Margin = new Thickness(0, 10, 0, 12);
            content.Children.Add(connect);
            content.Children.Add(Rule());
            return;
        }

        content.Children.Add(Text(
            "원격 소스: " + (remoteSourceDevice ?? "알 수 없음")
            + "  |  계정 전환·Telegram 발송 권한 없음", 11, muted: true));

        var v1 = remoteObservation?.Snapshot;
        var v2 = remoteObservationV2?.Snapshot;
        var observedAt = remoteIsV2 ? v2?.ObservedAt : v1?.ObservedAt;
        var reportedStatus = remoteIsV2 ? remoteObservationV2?.Status
            : remoteObservation?.Status;
        var age = observedAt is null ? TimeSpan.MaxValue
            : DateTimeOffset.UtcNow - observedAt.Value;
        var readable = (reportedStatus is "FRESH_READ_ONLY" or "AGED_READ_ONLY")
            && age >= TimeSpan.FromSeconds(-3) && age < TimeSpan.FromMinutes(6)
            && observedAt is not null;
        if (readable)
        {
            var five = remoteIsV2 ? v2?.FiveHourRemainingPercent
                : v1?.FiveHourRemainingPercent;
            var seven = remoteIsV2 ? v2?.SevenDayRemainingPercent
                : v1?.SevenDayRemainingPercent;
            var fiveReset = remoteIsV2 ? v2?.FiveHourResetAt : v1?.FiveHourResetAt;
            var sevenReset = remoteIsV2 ? v2?.SevenDayResetAt : v1?.SevenDayResetAt;
            if (five is int fivePercent)
                content.Children.Add(UsageLine("Mac 5H", fivePercent,
                    fiveReset is DateTimeOffset reset ? Reset(reset) : null));
            else content.Children.Add(Text("Mac 5H: ?", 11, muted: true));
            if (seven is int weeklyPercent)
                content.Children.Add(UsageLine("Mac 7D", weeklyPercent,
                    sevenReset is DateTimeOffset reset ? Reset(reset) : null));
            else content.Children.Add(Text("Mac 7D: ?", 11, muted: true));
            if (remoteIsV2 && v2 is not null)
            {
                var rCount = v2.ResetCreditsRemaining;
                content.Children.Add(Text("Mac R: " +
                    (rCount?.ToString(CultureInfo.CurrentCulture) ?? "?"),
                    11, muted: true));
                if (rCount is int positive && positive > 0)
                {
                    // These are expiry-order slots, not persistent credit IDs.
                    for (var index = 0; index < Math.Min(positive, 2); index++)
                    {
                        var when = v2.ResetCreditExpiries is { } dates &&
                            index < dates.Count ? dates[index] : null;
                        var label = when is DateTimeOffset expiration
                            ? expiration.ToLocalTime().ToString("yyyy-MM-dd HH:mm",
                                CultureInfo.CurrentCulture) +
                                (expiration <= DateTimeOffset.UtcNow
                                    ? " (만료 경과·새로고침 필요)" : "")
                            : "UNKNOWN (만료일 미제공)";
                        content.Children.Add(Text(
                            "Mac R" + (index + 1) + " 만료: " + label,
                            10.5, muted: true));
                    }
                }
            }
            else content.Children.Add(Text("Mac R: ? (V1 미지원)", 11, muted: true));
            content.Children.Add(Text("원격 관측: " +
                observedAt!.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm",
                    CultureInfo.CurrentCulture) + " · " + reportedStatus,
                10.5, muted: true));
        }
        else
        {
            content.Children.Add(Text(
                "Mac 5H: ?  |  Mac 7D: ?  |  Mac R: ?", 12, muted: true));
            var state = remoteReading ? "조회 중"
                : reportedStatus ?? "아직 수신되지 않음";
            content.Children.Add(Text("원격 상태: " + state, 10.5, muted: true));
        }

        var controls = new StackPanel {
            Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 12)
        };
        var refresh = Button(remoteReading ? "조회 중" : "원격 새로고침",
            () => _ = RefreshRemoteAsync(), left: true);
        refresh.IsEnabled = !remoteReading;
        controls.Children.Add(refresh);
        var disconnect = Button("원격 연결 해제", () => {
            remotePoll.Stop();
            remoteViewer?.Dispose();
            remoteViewerV2?.Dispose();
            remoteViewer = null;
            remoteViewerV2 = null;
            remoteObservation = null;
            remoteObservationV2 = null;
            remoteSourceDevice = null;
            remoteIsV2 = false;
            Render();
        }, left: true);
        disconnect.Margin = new Thickness(8, 0, 0, 0);
        controls.Children.Add(disconnect);
        content.Children.Add(controls);
        content.Children.Add(Rule());
    }

    private async Task RefreshRemoteAsync()
    {
        var currentV1 = remoteViewer;
        var currentV2 = remoteViewerV2;
        if ((currentV1 is null && currentV2 is null) || remoteReading) return;
        remoteReading = true;
        try
        {
            if (currentV2 is not null)
            {
                var result = await currentV2.FetchAsync(DateTimeOffset.UtcNow);
                if (ReferenceEquals(currentV2, remoteViewerV2))
                    remoteObservationV2 = result;
            }
            else if (currentV1 is not null)
            {
                var result = await currentV1.FetchAsync(DateTimeOffset.UtcNow);
                if (ReferenceEquals(currentV1, remoteViewer))
                    remoteObservation = result;
            }
        }
        catch (Exception)
        {
            if (currentV2 is not null && ReferenceEquals(currentV2, remoteViewerV2))
                remoteObservationV2 = new QuotaRelayV2ReadOnlyResult(
                    "HOLD_REMOTE_READ_FAILURE", null);
            else if (currentV1 is not null && ReferenceEquals(currentV1, remoteViewer))
                remoteObservation = new QuotaRelayReadOnlyResult(
                    "HOLD_REMOTE_READ_FAILURE", null);
        }
        finally
        {
            remoteReading = false;
            if (page == "monitor" && !closingForExit)
                Render();
        }
    }

    private void Settings(StackPanel body)
    {
        var settings = new StackPanel { Margin = new Thickness(20, 0, 20, 20) };
        settings.Children.Add(Text(T("settings_general"), 14, bold: true));
        void Toggle(string title, bool current, Action<bool> action) {
            var toggle = new CheckBox { IsChecked = current, Content = title, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 9, 0, 9) };
            AutomationProperties.SetName(toggle, title); toggle.Click += (_, _) => { try { action(toggle.IsChecked == true); } catch (Exception ex) { localError = ex.Message; Render(); } };
            settings.Children.Add(toggle);
        }
        Toggle(T("launch_at_login"), native.LaunchAtLogin, value => native.LaunchAtLogin = value);
        Toggle(T("show_menu_bar_percentage").Replace("菜单栏", "托盘").Replace("Menu Bar", "System Tray"), State.Settings.ShowsMenuBarPercentage, value => _ = Run("percentage", value: value));
        var usageWindows = new[] { "fiveHour", "weekly" };
        var usageWindow = new ComboBox { Width = 160, VerticalAlignment = VerticalAlignment.Center,
            ItemsSource = new[] { T("five_hour"), T("weekly") },
            SelectedIndex = State.Settings.StatusBarUsageWindow == "fiveHour" ? 0 : 1 };
        usageWindow.SelectionChanged += (_, _) => { if (usageWindow.SelectedIndex >= 0) _ = Run("statusBarUsageWindow", language: usageWindows[usageWindow.SelectedIndex]); };
        var usageWindowRow = Pair(Text(T("status_bar_usage_window")), usageWindow);
        usageWindowRow.Height = 48; settings.Children.Add(usageWindowRow);
        Toggle(T("show_five_hour_usage"), State.Settings.ShowsFiveHourUsage, value => _ = Run("fiveHour", value: value));
        var taskbarTitle = Text("작업표시줄 사용량 표시", 14, bold: true);
        taskbarTitle.Margin = new Thickness(0, 18, 0, 3);
        settings.Children.Add(taskbarTitle);
        Toggle("초기화까지 남은 시간 괄호 표시 (기본 OFF)",
            native.ShowTaskbarResetCountdown,
            value => native.ShowTaskbarResetCountdown = value);
        var taskbarFontSize = new ComboBox {
            Width = 160,
            VerticalAlignment = VerticalAlignment.Center,
            ItemsSource = new[] { "작게 (11)", "보통 (12)", "크게 (13)" },
            SelectedIndex = native.TaskbarFontSize - 11
        };
        AutomationProperties.SetName(taskbarFontSize, "작업표시줄 글자 크기");
        taskbarFontSize.SelectionChanged += (_, _) => {
            if (taskbarFontSize.SelectedIndex is >= 0 and <= 2)
                native.TaskbarFontSize = 11 + taskbarFontSize.SelectedIndex;
        };
        var taskbarFontRow = Pair(Text("사용량 글자 크기"), taskbarFontSize);
        taskbarFontRow.Height = 48;
        settings.Children.Add(taskbarFontRow);
        var resetDisplayHint = Text(
            "예: 5H 62% (2h15m) | 7D 60% (3d2h) | R 3. " +
            "초기화 날짜·시각은 기존 툴팁에서 확인합니다. " +
            "실제 5H 한도가 없는 계정은 5H를 자동 숨깁니다.",
            10.5, muted: true);
        resetDisplayHint.TextWrapping = TextWrapping.Wrap;
        settings.Children.Add(resetDisplayHint);
        var languages = new[] { "system", "english", "simplifiedChinese" };
        var language = new ComboBox { Width = 160, VerticalAlignment = VerticalAlignment.Center, ItemsSource = new[] { T("system_default"), T("english"), T("simplified_chinese") }, SelectedIndex = Array.IndexOf(languages, State.Settings.Language) };
        language.SelectionChanged += (_, _) => { if (language.SelectedIndex >= 0) _ = Run("language", language: languages[language.SelectedIndex]); };
        var languageRow = Pair(Text(T("language")), language); languageRow.Height = 48; settings.Children.Add(languageRow);
        var experimentHeading = Text(T("settings_automation"), 14, bold: true); experimentHeading.Margin = new Thickness(0, 20, 0, 0); settings.Children.Add(experimentHeading);
        Toggle(T("auto_reset"), State.Settings.AutoResetEnabled, value => _ = Run("autoReset", value: value));
        var experimentHint = Text(T("auto_reset_hint"), 10.5, muted: true); experimentHint.TextWrapping = TextWrapping.Wrap; settings.Children.Add(experimentHint);
        settings.Children.Add(Button(T("open_reset_log"), () => {
            try {
                var directory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codex Account Switcher Personal");
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(directory) { UseShellExecute = true });
            } catch (Exception ex) { localError = ex.Message; Render(); }
        }));
        // Existing-only Codex thread selection. Deliberately separate from
        // Telegram reset notifications and automatic Codex turn dispatch.
        var dedicatedHeading = Text("전용 Codex 채팅방", 14, bold: true);
        dedicatedHeading.Margin = new Thickness(0, 20, 0, 2);
        settings.Children.Add(dedicatedHeading);
        if (dedicatedContext is not null)
        {
            dedicatedPanel ??= new DedicatedThreadPickerPanel(
                dedicatedContext.CurrentIdentity,
                dedicatedContext.OpenReadOnlyAsync,
                dedicatedContext.DataHome,
                dedicatedContext.OpenDesktopReadOnlyAsync);
            dedicatedPanel.RefreshBoundStatus();
            if (dedicatedPanel.Parent is StackPanel oldParent)
                oldParent.Children.Remove(dedicatedPanel);
            settings.Children.Add(dedicatedPanel);
        }
        else
        {
            var disconnected = Text("기존 채팅방 조회 미연결 · 자동 전송 OFF", 11, muted: true);
            disconnected.TextWrapping = TextWrapping.Wrap;
            settings.Children.Add(disconnected);
        }

        // Local display and notification preferences; no live send is wired.
        var telegramHeading = Text("Telegram 알림", 14, bold: true);
        telegramHeading.Margin = new Thickness(0, 20, 0, 0);
        settings.Children.Add(telegramHeading);
        var telegram = native.TelegramPreferences;
        var telegramRoles = new[] { "Viewer · 사용량만", "Sender · 이 PC에서 알림 송신" };
        var telegramRole = new ComboBox {
            Width = 235, VerticalAlignment = VerticalAlignment.Center,
            ItemsSource = telegramRoles,
            SelectedIndex = telegram.DeviceRole == TelegramNotificationDeviceRole.Sender ? 1 : 0
        };
        AutomationProperties.SetName(telegramRole, "Telegram PC 역할");
        telegramRole.SelectionChanged += (_, _) => {
            if (telegramRole.SelectedIndex >= 0)
                native.SetTelegramPreferences(native.TelegramPreferences with {
                    DeviceRole = telegramRole.SelectedIndex == 1
                        ? TelegramNotificationDeviceRole.Sender
                        : TelegramNotificationDeviceRole.Viewer
                });
        };
        var telegramRoleRow = Pair(Text("이 PC의 역할"), telegramRole);
        telegramRoleRow.Height = 48;
        settings.Children.Add(telegramRoleRow);
        Toggle("Telegram 알림 ON/OFF (발송 준비 설정)",
            telegram.NotificationsRequested,
            requested => native.SetTelegramPreferences(native.TelegramPreferences with {
                NotificationsRequested = requested
            }));
        Toggle("5시간 100% 초기화 → 모니터 봇(묵음)",
            telegram.FiveHourRequested,
            requested => native.SetTelegramPreferences(native.TelegramPreferences with {
                FiveHourRequested = requested
            }));
        Toggle("7일 100% 초기화 → 얼럿 봇",
            telegram.WeeklyRequested,
            requested => native.SetTelegramPreferences(native.TelegramPreferences with {
                WeeklyRequested = requested
            }));
        // Windows-only persistent ON is only created by the initial explicit
        // other-hosts-OFF confirmation; never from the obsolete request-only
        // restart intent checkbox. Current-session permission is still visible.
        var stickyDesired = stickyWindowsOnlyAutoConsent();
        var autoSummary = Text(telegramAutomaticSessionArmed()
            ? "자동 알림: ON · 재부팅 후 자동 복구"
            : stickyDesired
                ? "자동 알림: 보류 · 저장된 ON의 실행 조건 확인 필요"
                : "자동 알림: OFF · 고급 설정에서 한 번만 활성화", 10.5, muted: true);
        autoSummary.Margin = new Thickness(0, 8, 0, 4);
        autoSummary.TextWrapping = TextWrapping.Wrap;
        settings.Children.Add(autoSummary);
        var rebootResumeStatus = Text(
            "이 PC 단독 Sender 동의: " + (stickyDesired ? "ON 저장" : "OFF") +
            " · 프로그램 재실행 시 로컬 점유·봇·미정산 원장 재검증",
            10.5, muted: true);
        rebootResumeStatus.Margin = new Thickness(0, 2, 0, 6);
        rebootResumeStatus.TextWrapping = TextWrapping.Wrap;
        settings.Children.Add(rebootResumeStatus);
        var advancedTelegramPanel = new StackPanel();
        var autoLabel = Text("5H·7D 자동 Telegram 알림 (ON/OFF 재부팅 후 유지)",
            12, bold: true);
        autoLabel.Margin = new Thickness(0, 18, 0, 4);
        advancedTelegramPanel.Children.Add(autoLabel);
        var autoArmed = telegramAutomaticSessionArmed();
        // Keep the stop action visible after the armed session changes state.
        if (autoArmed || stickyDesired) telegramAdvancedExpanded = true;
        var autoInfo = Text(autoArmed
            ? "자동 송신 ON · 새 5H/7D 복구 이벤트만 1회 발송 · 재부팅 후 복구"
            : stickyDesired
                ? "자동 송신 보류 · 안전 조건 미충족 · 저장된 ON을 OFF로 취소할 수 있습니다."
                : "자동 송신 OFF · 처음 한 번 ON을 승인하면 재부팅 후에도 유지됩니다.",
            10.5, muted: true);
        autoInfo.TextWrapping = TextWrapping.Wrap;
        advancedTelegramPanel.Children.Add(autoInfo);
        var autoWarning = Text(
            "다른 Windows/Mac의 발송 여부는 자동 확인할 수 없습니다. " +
            "이 PC를 유일한 Sender로 운영하는 동안에만 ON을 유지하세요. " +
            "Mac/다른 PC로 변경하기 전 반드시 이 PC를 OFF로 전환해야 합니다. " +
            "이전 대기 알림과 연결 테스트는 자동 전송되지 않습니다.",
            10.5, muted: true);
        autoWarning.TextWrapping = TextWrapping.Wrap;
        advancedTelegramPanel.Children.Add(autoWarning);
        if (telegramAutomaticSessionAction is not null)
        {
            var confirmedOtherHosts = new CheckBox {
                Content = "다른 PC/Mac Telegram 송신이 OFF임을 직접 확인",
                IsChecked = false,
                FontSize = 11,
                Margin = new Thickness(0, 8, 0, 8)
            };
            AutomationProperties.SetName(confirmedOtherHosts,
                "자동 Telegram 다른 장치 OFF 직접 확인");
            var shouldStop = autoArmed || stickyDesired;
            if (!shouldStop) advancedTelegramPanel.Children.Add(confirmedOtherHosts);
            var toggleAutomatic = Button(shouldStop
                ? "자동 Telegram 알림 OFF (설정 저장)"
                : "자동 Telegram 알림 ON (재부팅 후에도 유지)", () => { });
            toggleAutomatic.IsEnabled = shouldStop;
            if (!shouldStop)
            {
                confirmedOtherHosts.Checked += (_, _) =>
                    toggleAutomatic.IsEnabled = true;
                confirmedOtherHosts.Unchecked += (_, _) =>
                    toggleAutomatic.IsEnabled = false;
            }
            toggleAutomatic.Click += (_, _) => {
                var enabling = !shouldStop;
                if (enabling)
                {
                    if (confirmedOtherHosts.IsChecked != true) return;
                    var consent = MessageBox.Show(this,
                        "이 PC를 지속 Sender로 등록합니다. 지금부터 재부팅 후에도 " +
                        "새로 확인되는 5H·7D 복구 알림이 기존 봇으로 전송됩니다." +
                        Environment.NewLine +
                        "다른 Windows/Mac 송신이 OFF임을 확인했으며, " +
                        "다른 장치로 이관하기 전 이 PC를 OFF로 변경하겠습니까?" +
                        Environment.NewLine +
                        "실제 메시지는 별도 이벤트별 재확인 없이 전송될 수 있습니다.",
                        "Telegram 자동 발송 활성화 최종 확인",
                        MessageBoxButton.YesNo, MessageBoxImage.Warning,
                        MessageBoxResult.No);
                    if (consent != MessageBoxResult.Yes) return;
                }
                var completed = telegramAutomaticSessionAction(
                    enabling, enabling && confirmedOtherHosts.IsChecked == true);
                localError = completed
                    ? enabling
                        ? "자동 Telegram 송신: ON 저장 · 재부팅 후 복구"
                        : "자동 Telegram 송신: OFF 저장 · 재부팅 후에도 OFF"
                    : "자동 송신 조건 미충족 · 기존 봇 연결/Sender/알림 설정 확인 필요";
                Render();
            };
            toggleAutomatic.Margin = new Thickness(0, 4, 0, 6);
            advancedTelegramPanel.Children.Add(toggleAutomatic);
        }

        if (telegramWindowsHandoffAction is not null)
        {
            var handoffHeading = Text("Mac/다른 PC로 송신 담당 전환 준비", 12, bold: true);
            handoffHeading.Margin = new Thickness(0, 16, 0, 4);
            advancedTelegramPanel.Children.Add(handoffHeading);
            var handoffInfo = Text(
                "이 PC의 자동 Telegram 발송을 해제하고, Sender 역할과 알림을 " +
                "Viewer/OFF로 저장한 뒤 Windows 로컬 점유 해제를 확인합니다. " +
                "Mac의 실제 발송 권한은 활성화하지 않습니다. 진행 중이던 " +
                "Telegram 메시지는 중간에 취소할 수 없습니다.",
                10.5, muted: true);
            handoffInfo.TextWrapping = TextWrapping.Wrap;
            advancedTelegramPanel.Children.Add(handoffInfo);
            var stopWindows = Button("Windows Sender 중지 및 Mac 이관 준비", () => {
                var confirmed = MessageBox.Show(this,
                    "이 PC의 자동 Telegram 송신을 중지하고 Sender 역할을 " +
                    "Viewer/OFF로 바꾸시겠습니까?" +
                    Environment.NewLine +
                    "실제 송신 중이면 이관을 완료하지 않고 보류합니다." +
                    Environment.NewLine +
                    "Mac 측 Sender는 별도로 검증·활성화해야 합니다.",
                    "Windows Telegram 송신 중지 확인",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning,
                    MessageBoxResult.No);
                if (confirmed != MessageBoxResult.Yes) return;

                var result = telegramWindowsHandoffAction();
                localError = result switch
                {
                    WindowsTelegramHandoffStatus.WindowsViewerOffVerified =>
                        "Windows Viewer/OFF·로컬 송신 점유 해제 확인. " +
                        "Mac 송신자 활성화는 아직 미완료입니다.",
                    WindowsTelegramHandoffStatus.InFlightSendMustFinish =>
                        "진행 중인 Telegram 발송이 있습니다. 자동 송신은 중지했으며 " +
                        "전송 결과를 확인한 후 이관을 다시 실행하세요.",
                    _ => "Windows 송신자 해제를 검증하지 못했습니다. " +
                        "Mac Sender를 활성화하지 마세요."
                };
                Render();
            });
            stopWindows.Margin = new Thickness(0, 6, 0, 6);
            stopWindows.IsEnabled = telegram.DeviceRole ==
                TelegramNotificationDeviceRole.Sender ||
                telegram.NotificationsRequested || autoArmed;
            advancedTelegramPanel.Children.Add(stopWindows);
        }

        // Keep infrequently used automatic delivery and handoff controls
        // accessible without overwhelming the normal Sender/5H/7D settings.
        var advancedTelegramSettings = new Expander {
            Header = "고급 설정 · 자동 송신 / Mac 이관",
            Content = advancedTelegramPanel,
            IsExpanded = telegramAdvancedExpanded || autoArmed || stickyDesired,
            Margin = new Thickness(0, 12, 0, 8)
        };
        AutomationProperties.SetName(advancedTelegramSettings, "Telegram 고급 설정");
        advancedTelegramSettings.Expanded += (_, _) => telegramAdvancedExpanded = true;
        advancedTelegramSettings.Collapsed += (_, _) => telegramAdvancedExpanded = false;
        settings.Children.Add(advancedTelegramSettings);

        var selectedRole = telegram.DeviceRole == TelegramNotificationDeviceRole.Sender
            ? "Sender 선택(1건 수동 승인 필요)" : "Viewer";
        var requestedAlerts = telegram.NotificationsRequested ? "ON(요청)" : "OFF";
        var telegramState = Text(
            "이 기기: " + Environment.MachineName +
            " | 저장된 역할: " + selectedRole +
            " | 알림 요청: " + requestedAlerts +
            " | 실제 Telegram 발송: 이벤트별 수동 승인 또는 " +
            "이 PC 단독 Sender로 지속 동의한 자동 복구 알림만 가능. " +
            "자동 알림 ON/OFF는 이 PC에 암호화 저장되며 재시작 후 재검증합니다. " +
            "Sender를 선택해도 다른 PC의 Sender가 자동 중지되지 않습니다. " +
            "설정에서 알림 ON만 선택해도 자동 발송되지 않습니다.",
            10.5, muted: true);
        telegramState.TextWrapping = TextWrapping.Wrap;
        settings.Children.Add(telegramState);
        if (authorityStatusReadOnlyProbe is not null)
        {
            // Explicit temporary credentials: not HKCU, files, auto-start,
            // Codex profile settings or Telegram Sender preferences.
            var authorityPanel = new StackPanel {
                Margin = new Thickness(4, 8, 4, 10)
            };
            var authorityEndpoint = new TextBox {
                MaxLength = 256,
                ToolTip = "https:// 인증된 Authority 서버 주소 (루트만)"
            };
            var authorityDevice = new TextBox { MaxLength = 48 };
            var authorityKey = new PasswordBox { MaxLength = 64 };
            var authorityPin = new TextBox { MaxLength = 64 };
            void AddAuthorityField(string title, Control control)
            {
                var label = Text(title, 10.5, muted: true);
                label.Margin = new Thickness(0, 5, 0, 2);
                authorityPanel.Children.Add(label);
                AutomationProperties.SetName(control, title);
                authorityPanel.Children.Add(control);
            }
            AddAuthorityField("HTTPS Authority 서버", authorityEndpoint);
            AddAuthorityField("등록된 장치 ID", authorityDevice);
            AddAuthorityField("장치 HMAC 키 64자리 (표시·저장 안 함)", authorityKey);
            AddAuthorityField("서버 인증서 SHA256 64자리", authorityPin);

            var status = Text(authorityReadOnlyDisplay,
                10.5, muted: true);
            status.Margin = new Thickness(0, 9, 0, 7);
            status.TextWrapping = TextWrapping.Wrap;
            authorityPanel.Children.Add(status);
            Button? fetch = null;
            fetch = Button("Authority 상태 1회 조회 (발송 없음)", () => {
                if (authorityReadInProgress || IsBusy) return;
                var origin = authorityEndpoint.Text.Trim();
                var id = authorityDevice.Text.Trim();
                var enteredKey = authorityKey.Password;
                var pin = authorityPin.Text.Trim();
                authorityKey.Clear(); // never retain credential in the view
                if (origin.Length == 0 || id.Length == 0 ||
                    enteredKey.Length != 64 || pin.Length != 64)
                {
                    authorityReadOnlyDisplay = "Authority: 입력 미완료 · 네트워크 요청 없음";
                    status.Text = authorityReadOnlyDisplay;
                    return;
                }
                authorityReadInProgress = true;
                fetch!.IsEnabled = false;
                authorityReadOnlyDisplay =
                    "Authority: 인증된 상태 조회 중 · Telegram 발송 없음";
                status.Text = authorityReadOnlyDisplay;
                _ = InspectAuthorityOnceAsync(origin, id, enteredKey, pin);
            });
            async Task InspectAuthorityOnceAsync(
                string origin, string id, string key, string pin)
            {
                try
                {
                    var result = await authorityStatusReadOnlyProbe!(
                        origin, id, key, pin);
                    authorityReadOnlyDisplay = result;
                }
                catch
                {
                    authorityReadOnlyDisplay =
                        "Authority: 조회 예외 · Telegram 발송 권한 없음";
                }
                finally
                {
                    authorityReadInProgress = false;
                    // Update the currently displayed status in-place. A full
                    // Render() here destroys the expanded Authority pane and
                    // hides the result just returned by the read-only probe.
                    // If another render already replaced the pane, the
                    // nonsecret authorityReadOnlyDisplay field is used there.
                    if (page == "settings" && !closingForExit)
                    {
                        status.Text = authorityReadOnlyDisplay;
                        fetch!.IsEnabled = !IsBusy;
                    }
                }
            }
            fetch.IsEnabled = !authorityReadInProgress && !IsBusy;
            authorityPanel.Children.Add(fetch);
            var authorityDisclosure = new Expander {
                Header = "공통 Sender Authority · 현재 소유권 조회 (읽기 전용)",
                IsExpanded = authorityDisclosureExpanded,
                Content = authorityPanel,
                Margin = new Thickness(0, 12, 0, 8)
            };
            AutomationProperties.SetName(authorityDisclosure, "공통 Sender Authority 읽기 전용 상태");
            authorityDisclosure.Expanded += (_, _) => authorityDisclosureExpanded = true;
            authorityDisclosure.Collapsed += (_, _) => authorityDisclosureExpanded = false;
            settings.Children.Add(authorityDisclosure);
        }
        var localSenderStatus = Text(localTelegramSenderStatus(), 10.5, muted: true);
        localSenderStatus.TextWrapping = TextWrapping.Wrap;
        settings.Children.Add(localSenderStatus);
        var scriptsStatus = Text(bundledTelegramScriptsStatus(), 10.5, muted: true);
        scriptsStatus.TextWrapping = TextWrapping.Wrap;
        settings.Children.Add(scriptsStatus);
        var reuseStatus = Text(existingAmanoBotRouteStatus(), 10.5, muted: true);
        reuseStatus.TextWrapping = TextWrapping.Wrap;
        settings.Children.Add(reuseStatus);
        var outboxStatus = Text(telegramProductionOutboxStatus(), 10.5, muted: true);
        outboxStatus.TextWrapping = TextWrapping.Wrap;
        settings.Children.Add(outboxStatus);
        var savedBotDirectory = native.TelegramBotDirectory;
        var savedBotStatus = Text(savedBotDirectory is null
            ? "기존 Telegram 봇 폴더: 선택되지 않음 · 실제 전송 차단"
            : "기존 Telegram 봇 폴더: 이 PC에 저장됨 · 실제 전송 차단",
            10.5, muted: true);
        savedBotStatus.TextWrapping = TextWrapping.Wrap;
        settings.Children.Add(savedBotStatus);
        // Detect the existing Amano booking bot on this personal Windows PC.
        // The user must explicitly confirm before persisting the path. All
        // Telegram sender/notification toggles remain unchanged and OFF by default.
        var detectedAmano = NativeSettings.FindExistingAmanoBotDirectory(
            @"[workspace]");
        if (detectedAmano is not null &&
            !string.Equals(savedBotDirectory, detectedAmano,
                StringComparison.OrdinalIgnoreCase))
        {
            var linkExistingBot = Button("기존 인천공항 주차대행 Telegram 봇 연결", () => {
                var currentCandidate = NativeSettings.FindExistingAmanoBotDirectory(
                    @"[workspace]");
                if (currentCandidate is null ||
                    !string.Equals(currentCandidate, detectedAmano,
                        StringComparison.OrdinalIgnoreCase))
                {
                    localError = "기존 주차대행 봇 파일 확인 실패 · 설정 변경 없음";
                    Render();
                    return;
                }
                if (MessageBox.Show(this,
                    "기존 주차대행 Telegram 봇 폴더만 이 앱에 등록합니다." +
                    Environment.NewLine +
                    "기존 주차대행 프로그램·봇 토큰·수신 대상은 변경하지 않습니다." +
                    Environment.NewLine +
                    "Telegram Sender 또는 알림 ON 설정도 변경하지 않습니다." +
                    Environment.NewLine + "이 폴더를 연결하시겠습니까?",
                    "기존 Telegram 봇 연결 확인", MessageBoxButton.YesNo,
                    MessageBoxImage.Question, MessageBoxResult.No) !=
                    MessageBoxResult.Yes)
                    return;
                try { native.SetTelegramBotDirectory(currentCandidate); Render(); }
                catch (Exception ex) {
                    localError = "기존 봇 연결 실패: " + ex.GetType().Name;
                    Render();
                }
            });
            linkExistingBot.Margin = new Thickness(0, 8, 0, 0);
            settings.Children.Add(linkExistingBot);
        }
        var botFileCheck = Button("기존 Telegram 봇 폴더 선택·저장 (읽기 전용 확인)", () => {
            using var picker = new System.Windows.Forms.FolderBrowserDialog {
                Description = "기존 Telegram 봇 설정 파일이 있는 로컬 폴더를 선택하세요.",
                ShowNewFolderButton = false
            };
            if (picker.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            try {
                native.SetTelegramBotDirectory(picker.SelectedPath);
                Render();
            } catch (Exception ex) {
                localError = "봇 폴더를 저장할 수 없습니다: " + ex.GetType().Name;
                Render();
            }
        });
        botFileCheck.Margin = new Thickness(0, 8, 0, 0);
        settings.Children.Add(botFileCheck);
        if (savedBotDirectory is not null) {
            var inspection = TelegramBotFileStatus.Inspect(savedBotDirectory);
            var fileStatusText = Text(inspection.Display, 10.5, muted: true);
            fileStatusText.TextWrapping = TextWrapping.Wrap;
            settings.Children.Add(fileStatusText);
            var clearBotDirectory = Button("저장한 Telegram 봇 폴더 선택 해제", () => {
                try { native.SetTelegramBotDirectory(null); Render(); }
                catch (Exception ex) {
                    localError = "봇 폴더 선택을 해제할 수 없습니다: " +
                        ex.GetType().Name;
                    Render();
                }
            });
            settings.Children.Add(clearBotDirectory);
        }
        var heading = Text(T("settings_updates"), 14, bold: true); heading.Margin = new Thickness(0, 20, 0, 0); settings.Children.Add(heading);
        Toggle(T("automatically_check_updates"), native.AutomaticallyCheckUpdates, value => native.AutomaticallyCheckUpdates = value);
        var hint = Text(T(native.UpdateError ?? "update_check_hint"), 10.5, muted: true); hint.Margin = new Thickness(0, 0, 0, 12); hint.TextWrapping = TextWrapping.Wrap; settings.Children.Add(hint); settings.Children.Add(Rule());
        var check = Button(T(native.UpdatePage == null ? "check_for_updates" : "update_action"), async () => { if (native.UpdatePage != null) native.OpenUpdate(); else await native.CheckUpdatesAsync(); }); check.IsEnabled = !native.IsChecking;
        var version = Pair(Text(T("current_version").Replace("%@", native.Version), 12), check);
        version.Margin = new Thickness(0, 12, 0, 0);
        settings.Children.Add(version);
        // The semantic version is the same on earlier personal RCs. Show the
        // hash of the *running executable* so users can identify the exact build.
        var runningSha = native.RunningExecutableSha256;
        var buildID = Text(runningSha is { Length: 64 }
            ? "실행 빌드: SHA256 " + runningSha[..16]
            : "실행 빌드: 확인 불가", 10.5, muted: true);
        buildID.Margin = new Thickness(0, 8, 0, 0);
        buildID.ToolTip = runningSha is null ? "실행 파일 해시를 읽을 수 없습니다."
            : "현재 실행 EXE SHA256:" + Environment.NewLine + runningSha;
        settings.Children.Add(buildID);
        if (runningSha is not null) {
            var copyBuildID = Button("빌드 SHA256 복사", () => {
                try { Clipboard.SetText(runningSha); }
                catch (Exception ex) { localError = ex.Message; Render(); }
            });
            settings.Children.Add(copyBuildID);
        }
        body.Children.Add(settings);
    }
}

