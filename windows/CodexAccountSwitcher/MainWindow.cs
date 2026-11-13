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
        Func<string>? productionTelegramOutboxStatus = null)
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
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(App.IconUri);
        Title = "Codex Account Switcher Personal"; Width = 420; SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.SingleBorderWindow; ResizeMode = ResizeMode.CanMinimize;
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
    public void Navigate(string destination) { page = destination; target = null; localError = null; Render(); }
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
            header.Children.Add(Button("", () => Navigate(page == "remove" ? "manage" : "accounts"), "\uE72B"));
            AutomationProperties.SetName(header.Children[0], T("back"));
            var title = target != null && page is "switch" or "remove"
                ? T(page + "_title").Replace("%@", target.Profile.DisplayName)
                : T(page == "settings" ? "settings" : page == "monitor" ? "monitor_title" : "accounts");
            var heading = Text(title, 20, bold: true); heading.Margin = new Thickness(12, 0, 0, 0); header.Children.Add(heading);
            body.Children.Add(header);
        }
        if (page is "switch" or "remove") Confirmation(body);
        else if (page == "settings") Settings(body);
        else if (page == "monitor") Monitor(body);
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
        // Local display and notification preferences; no live send is wired.
        var telegramHeading = Text("Telegram 알림", 14, bold: true);
        telegramHeading.Margin = new Thickness(0, 20, 0, 0);
        settings.Children.Add(telegramHeading);
        var telegram = native.TelegramPreferences;
        var telegramRoles = new[] { "Viewer — 사용량 표시만", "Sender — 이 PC 발송 담당(예정)" };
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
        var selectedRole = telegram.DeviceRole == TelegramNotificationDeviceRole.Sender
            ? "Sender 요청(미승인)" : "Viewer";
        var requestedAlerts = telegram.NotificationsRequested ? "ON(요청)" : "OFF";
        var telegramState = Text(
            "이 기기: " + Environment.MachineName +
            " | 저장된 역할: " + selectedRole +
            " | 알림 요청: " + requestedAlerts +
            " | 실제 Telegram 발송: 미연결. " +
            "역할과 알림 설정은 이 PC에 저장되며 앱 재시작 후에도 유지됩니다. " +
            "Sender를 선택해도 다른 PC의 Sender가 자동 중지되지 않습니다. " +
            "단일 발송자 확인·전송 검증 전까지 메시지는 발송되지 않습니다.",
            10.5, muted: true);
        telegramState.TextWrapping = TextWrapping.Wrap;
        settings.Children.Add(telegramState);
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
