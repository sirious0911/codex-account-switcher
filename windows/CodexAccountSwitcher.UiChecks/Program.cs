using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexAccountSwitcher;
using CodexAccountSwitcher.Core;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var output = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine("windows", "artifacts", "ui"));
        try {
            using var releases = System.Text.Json.JsonDocument.Parse("""
                [
                  {"tag_name":"macos-v99.0.0","draft":false,"assets":[{"name":"Codex-Account-Switcher-windows-x64.exe"}]},
                  {"tag_name":"windows-v99.0.0","draft":false,"assets":[{"name":"Codex-Account-Switcher-windows-x64.exe"}]},
                  {"tag_name":"v9.0.0","draft":true,"assets":[{"name":"Codex-Account-Switcher-windows-x64.exe"}]},
                  {"tag_name":"v8.0.0","draft":false,"prerelease":true,"assets":[{"name":"Codex-Account-Switcher-windows-x64.exe"}]},
                  {"tag_name":"v7.0.0","draft":false,"assets":[]},
                  {"tag_name":"v06.0.0","draft":false,"assets":[{"name":"Codex-Account-Switcher-windows-x64.exe"}]},
                  {"tag_name":"v0.1.9","draft":false,"assets":[{"name":"Codex-Account-Switcher-windows-x64.exe"}]},
                  {"tag_name":"v0.1.12","draft":false,"assets":[{"name":"Codex-Account-Switcher-windows-x64.exe"}]}
                ]
                """);
            Assert(NativeSettings.LatestWindowsVersion(releases.RootElement) == new Version(0, 1, 12),
                "Updates must select stable unified releases with a Windows EXE, ordered by version.");
            using var macOnly = System.Text.Json.JsonDocument.Parse("""
                [{"tag_name":"v99.0.0","draft":false,"assets":[{"name":"Codex-Account-Switcher-macos-arm64.dmg"}]}]
                """);
            Assert(NativeSettings.LatestWindowsVersion(macOnly.RootElement) == null, "A release without a Windows EXE must never prompt a Windows update.");
            Directory.CreateDirectory(output);
            TelegramSenderScriptContractTest.Run();
            var app = new App(false); app.InitializeComponent();
            var appFields = typeof(App).GetFields(
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic);
            Assert(appFields.Any(f => f.FieldType == typeof(TaskbarQuotaPanel)) &&
                !appFields.Any(f => f.FieldType == typeof(TrayQuotaIcons)) &&
                !appFields.Any(f => f.FieldType == typeof(IntegratedMiniBarWindow)),
                "Normal app startup must own a taskbar child host, not floating or overflow-tray displays.");
            Assert(TaskbarQuotaPanel.Placement ==
                Deskband11Lib.Core.TaskbarContentPlacement.BeforeNotificationArea &&
                TaskbarQuotaPanel.PreferredWidth == 166 &&
                TaskbarQuotaPanel.PreferredHeight == 48 &&
                TaskbarQuotaPanel.IndicatorHeight == 30,
                "The hosted HWND must span the taskbar row while the smaller quota strip stays vertically centered, immediately LEFT of ^.");
            Assert(appFields.Any(f => f.FieldType == typeof(WindowsTelegramLocalSenderLease)),
                "Windows app must own the session-local Telegram mutex lifecycle.");
            Assert(appFields.Any(f => f.FieldType == typeof(AdaptiveUsageRefreshService)),
                "Adaptive work polling must be owned by the same Windows app, not a second process.");
            // Metadata-only recent-session detection: never inspect login or transcript bytes.
            var syntheticHome = Path.Combine(output, "adaptive-work-fixture");
            var exampleDay = DateTimeOffset.UtcNow;
            var dayFolder = Path.Combine(syntheticHome, "sessions",
                exampleDay.Year.ToString("0000"), exampleDay.Month.ToString("00"),
                exampleDay.Day.ToString("00"));
            Directory.CreateDirectory(dayFolder);
            var rollout = Path.Combine(dayFolder, "rollout-synthetic.jsonl");
            var unrelated = Path.Combine(dayFolder, "unrelated.txt");
            File.WriteAllText(rollout, "content-is-not-used-in-activity-detection");
            File.WriteAllText(unrelated, "test");
            File.SetLastWriteTimeUtc(rollout, exampleDay.UtcDateTime.AddSeconds(-40));
            Assert(CodexWorkActivityDetector.HasRecentSessionFileMetadata(syntheticHome, exampleDay),
                "Recent Codex rollout metadata should count as possible work.");
            File.SetLastWriteTimeUtc(rollout, exampleDay.UtcDateTime.AddMinutes(-4));
            Assert(!CodexWorkActivityDetector.HasRecentSessionFileMetadata(syntheticHome, exampleDay),
                "Old rollout activity should not hold one-minute polling forever.");
            File.SetLastWriteTimeUtc(rollout, exampleDay.UtcDateTime.AddMinutes(2));
            Assert(!CodexWorkActivityDetector.HasRecentSessionFileMetadata(syntheticHome, exampleDay),
                "Future timestamps must not falsely trigger active polling.");
            File.Delete(rollout);
            Assert(!CodexWorkActivityDetector.HasRecentSessionFileMetadata(syntheticHome, exampleDay),
                "Non-rollout file changes are not Codex task evidence.");
            Assert(!CodexWorkActivityDetector.HasRecentSessionFileMetadata(
                Path.Combine(output, "missing-user-codex-home"), exampleDay),
                "Missing session files fail idle without blocking the app.");
            Assert(CodexWorkActivityDetector.IsOfficialCodexDesktopProcess(
                "ChatGPT", @"C:\Program Files\WindowsApps\OpenAI.Codex_0.1_x64__fixture\ChatGPT.exe"),
                "Official Windows Codex Desktop can use ChatGPT.exe as its manifest binary.");
            Assert(CodexWorkActivityDetector.IsOfficialCodexDesktopProcess(
                "Codex", @"C:\Program Files\WindowsApps\OpenAI.Codex_0.1_x64__fixture\Codex.exe"),
                "Official Codex executable is also accepted when package path matches.");
            Assert(!CodexWorkActivityDetector.IsOfficialCodexDesktopProcess(
                "ChatGPT", @"C:\Program Files\ChatGPT\ChatGPT.exe"),
                "Unrelated ChatGPT application must not count as active Codex work.");
            Assert(!CodexWorkActivityDetector.IsOfficialCodexDesktopProcess(
                "Codex", @"C:\Tools\unrelated\Codex.exe"),
                "A process name alone never authorizes higher-frequency API polling.");

            using (var taskbar = new TaskbarQuotaPanel(() => {}, () => {}, () => true))
            {
                Assert(taskbar.CurrentReadout.FiveHourRemaining == null &&
                    taskbar.CurrentReadout.ResetCreditsAvailable == null,
                    "Fresh taskbar host must not invent account quota values.");
                taskbar.Invalidate();
                Assert(taskbar.CurrentReadout.Status == MiniBarDisplayStatus.Invalidated,
                    "Switch or host error must clear the embedded taskbar quota surface.");
                var free = new NonWeeklyUsage(73, 43_200,
                    DateTimeOffset.FromUnixTimeSeconds(1_900_000_000), 0, DateTimeOffset.UtcNow);
                taskbar.AcceptNonWeekly(free);
                Assert(taskbar.CurrentNonWeeklyUsage?.WindowLabel == "30D" &&
                       taskbar.CurrentReadout.Status == MiniBarDisplayStatus.Invalidated,
                    "Real Free 30D must not be published as a weekly quota.");
                taskbar.Invalidate();
                Assert(taskbar.CurrentNonWeeklyUsage is null,
                    "Switch invalidation must immediately clear another account's 30D quota.");
            }
            var iconSize = new System.Drawing.Size(16, 16);
            using (var fiveIcon = TrayUsageIcon.CreateMetric("5H", "85", false,
                iconSize, MiniBarDisplayStatus.FreshUnbound))
            using (var weeklyIcon = TrayUsageIcon.CreateMetric("7D", "62", false,
                iconSize, MiniBarDisplayStatus.FreshUnbound))
            using (var resetIcon = TrayUsageIcon.CreateMetric("R", "?", true,
                iconSize, MiniBarDisplayStatus.Unknown))
            {
                Assert(fiveIcon.Width == 16 && weeklyIcon.Height == 16 &&
                    resetIcon.Width == 16,
                    "Three compact native metric icon drawings must be available.");
            }
            var client = new FixtureClient();

            // The new in-app minibar is shown by the SAME tray owner. No
            // Codex runtime, Named Pipe listener or account login is used.
            var mini = new IntegratedMiniBarWindow(() => {});
            Assert(!mini.ShowInTaskbar && !mini.Topmost &&
                Math.Abs(mini.Width - 360) < 0.1 && Math.Abs(mini.Height - 52) < 0.1,
                "The integrated minibar must be 360x52 DIP, borderless and taskbar-adjacent.");
            Assert(mini.DisplayFrame.FiveHourRemaining == null,
                "No synthetic or cached usage must appear before the first active sample.");
            Render(mini, Path.Combine(output, "integrated-minibar-unknown.png"));
            Assert(mini.LastDock is not null,
                "Integrated WPF must invoke native window placement on initialization.");
            if (mini.LastDock!.Placement.FitsInsideWorkArea)
            {
                Assert(mini.LastDock.Success &&
                    mini.LastDock.ActualRectInsideWorkArea &&
                    mini.LastDock.TargetDpi >= 96,
                    "Native placement must verify actual window rectangle and target monitor DPI.");
                Console.WriteLine("PASS: integrated native dock targetDpi=" +
                    mini.LastDock.TargetDpi + ", physicalRectInside=" +
                    mini.LastDock.ActualRectInsideWorkArea);
            }
            else Console.WriteLine("SKIP: native dock skipped for undersized CI work area.");
            var physicalScreens = System.Windows.Forms.Screen.AllScreens;
            Console.WriteLine("INFO: physical display count=" + physicalScreens.Length);
            Assert(mini.SelectScreen(Array.Empty<System.Windows.Forms.Screen>()) is null,
                "Transient zero-display re-enumeration must return no target, not throw.");
            if (physicalScreens.Length != 0)
                Assert(mini.SelectScreen(physicalScreens) != null,
                    "A stable connected display snapshot must resolve a docking target.");
            var selectorMenu = mini.ContextMenu!.Items.OfType<MenuItem>().First(item =>
                (string?)item.Header == "표시 모니터 선택");
            Assert(selectorMenu.Items.Count == physicalScreens.Length,
                "The integrated minibar monitor menu must enumerate the actual connected screens.");
            foreach (var screen in physicalScreens)
            {
                var selectable = selectorMenu.Items.OfType<MenuItem>().Single(item =>
                    ((string)item.Header).StartsWith(screen.DeviceName,
                        StringComparison.Ordinal));
                selectable.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                var selectedDock = mini.LastDock ??
                    throw new Exception("The monitor menu must re-run native docking on selection.");
                if (selectedDock.Placement.FitsInsideWorkArea)
                {
                    Assert(selectedDock.Success && selectedDock.ActualRectInsideWorkArea,
                        "Each usable monitor must report a verified physical WPF window rectangle.");
                    double windowWidthPx = Math.Round(mini.Width * selectedDock.TargetDpi / 96.0);
                    double windowHeightPx = Math.Round(mini.Height * selectedDock.TargetDpi / 96.0);
                    Assert(selectedDock.PixelLeft >= screen.WorkingArea.Left - 2 &&
                        selectedDock.PixelTop >= screen.WorkingArea.Top - 2 &&
                        selectedDock.PixelLeft + windowWidthPx <= screen.WorkingArea.Right + 2 &&
                        selectedDock.PixelTop + windowHeightPx <= screen.WorkingArea.Bottom + 2,
                        "Window coordinates must stay within the selected physical monitor work area.");
                    Console.WriteLine("PASS: native monitor selection, targetDpi=" +
                        selectedDock.TargetDpi + ", work-area verified");
                }
                else Console.WriteLine("SKIP: selected monitor work area too small for minibar.");
            }
            var uiNow = DateTimeOffset.UtcNow;
            const string miniRaw = """
                {"accountId":"ui-fixture","ordinaryUsageAllowed":null,
                 "rateLimitsByLimitId":{"codex":{"planType":null,
                   "primary":{"usedPercent":15,"windowDurationMins":300,"resetsAt":1900000000},
                   "secondary":{"usedPercent":38,"windowDurationMins":10080,"resetsAt":1900100000}}},
                 "rateLimitResetCredits":{"availableCount":2,"credits":[
                   {"status":"available","expiresAt":1900200000},
                   {"status":"available","expiresAt":1900300000}]}}
                """;
            mini.Accept(new CodexMonitorWireSample(uiNow.ToUnixTimeMilliseconds(), miniRaw));
            var miniFrame = mini.DisplayFrame;
            Assert(miniFrame.FiveHourRemaining == 85 && miniFrame.WeeklyRemaining == 62 &&
                miniFrame.ResetCreditsAvailable == 2 && !miniFrame.AccountVerified &&
                !miniFrame.EventsAllowed && !miniFrame.TelegramAllowed,
                "In-app WPF should render all three fresh quota values without trusting account identity.");
            Assert(mini.ToolTip is string freshTip && freshTip.Contains("계정 미확인") &&
                freshTip.Contains("Codex 5H: 85%") && freshTip.Contains("Codex 7D: 62%") &&
                freshTip.Contains("Codex 리셋권: 2") &&
                freshTip.Contains("1번 만료: ") && freshTip.Contains("2번 만료: ") &&
                freshTip.Contains("다음 초기화: ") && freshTip.Contains(" KST"),
                "Integrated UI must show both synthetic credit expiries and quota resets without real account access.");
            Render(mini, Path.Combine(output, "integrated-minibar-fresh.png"));
            mini.Invalidate();
            Assert(mini.DisplayFrame.FiveHourRemaining == null &&
                mini.DisplayFrame.ResetCreditsAvailable == null &&
                mini.ToolTip is string staleTip && staleTip.Contains("UNKNOWN"),
                "Account switch or host failure must immediately clear the visible quota values.");
            Render(mini, Path.Combine(output, "integrated-minibar-invalidated.png"));
            mini.Hide();
            Assert(!mini.IsVisible, "Integrated minibar must hide without exiting the account application.");
            mini.ShowFromTray();
            Assert(mini.IsVisible, "Existing tray can reopen the same minibar window.");
            mini.CloseForExit();

            var window = new MainWindow(client, telegramLocalSenderStatus: () =>
                "Windows local sender: test-only, actual delivery blocked");
            Assert(window.Icon != null, "The taskbar and title bar must use the packaged application logo.");
            using (var stream = Application.GetResourceStream(App.IconUri).Stream)
            using (var trayIcon = new System.Drawing.Icon(stream, 16, 16))
            using (var pixels = trayIcon.ToBitmap())
                Assert(pixels.Width == 16 && pixels.Height == 16, "The original logo must contain a usable native tray size.");
            foreach (var variant in new[] { "light", "dark" }) {
                foreach (var size in new[] { 16, 20, 24, 32, 40, 48, 64 }) {
                    var uri = new Uri($"pack://application:,,,/Codex-Account-Switcher-windows-x64;component/tray-{variant}.ico");
                    using var stream = Application.GetResourceStream(uri).Stream;
                    using var trayIcon = new System.Drawing.Icon(stream, size, size);
                    using var pixels = trayIcon.ToBitmap();
                    Assert(pixels.Width == size && pixels.Height == size, $"Tray {variant} must include {size}px (loaded {pixels.Width} x {pixels.Height}).");
                    Assert(pixels.GetPixel(0, 0).A == 0 && pixels.GetPixel(size - 1, size - 1).A == 0,
                        "Tray logos must have transparent corners, with no application tile.");
                    var top = size; var bottom = -1;
                    for (var y = 0; y < size; y++) for (var x = 0; x < size; x++) {
                        var pixel = pixels.GetPixel(x, y);
                        if (pixel.A < 128) continue;
                        top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                        Assert(pixel.R == (variant == "light" ? 0 : 255), "Tray foreground must match the taskbar theme.");
                    }
                    Assert(bottom - top + 1 >= size * 0.95, "The visible logo must fill the tray canvas without the old app-icon inset.");
                }
            }
            Render(window, Path.Combine(output, "accounts-zh.png"));
            Assert(Math.Abs(window.ActualWidth - 420) < 2 && window.ActualHeight < 360,
                $"Home must be a compact 420 DIP window (actual {window.ActualWidth} × {window.ActualHeight}).");
            Assert(window.ShowInTaskbar && !window.Topmost && window.WindowStyle == WindowStyle.SingleBorderWindow,
                "Windows must use a normal titled window visible in the taskbar.");
            var other = new Window { Width = 100, Height = 100, ShowInTaskbar = false };
            other.Show(); other.Activate(); other.Close();
            Assert(window.IsVisible, "Losing focus must not dismiss the account window.");
            var active = All<Button>(window).Single(button => System.Windows.Automation.AutomationProperties.GetName(button) == "personal@example.test");
            active.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(window.IsVisible && client.Commands.Count == 0, "Clicking the active account keeps the window open.");
            Assert(All<TextBlock>(window).Count(text => text.Text.Contains("@example.test")) == 2, "Home must use full emails as account titles.");
            Assert(All<TextBlock>(window).Where(text => text.Text.Contains("@example.test")).All(text =>
                text.TextWrapping == TextWrapping.NoWrap && text.TextTrimming == TextTrimming.CharacterEllipsis && Equals(text.ToolTip, text.Text)),
                "Account emails must stay on one line and expose the full address in a tooltip.");
            Assert(!All<TextBlock>(window).Any(text => text.Text == "当前"), "Home uses selection color, not active labels.");
            Assert(All<TextBlock>(window).Any(text => text.Text == "重置次数: 2"), "Home shows the reported reset count beneath usage.");
            Assert(All<TextBlock>(window).Any(text => text.Text == "重置次数: 未知"), "Missing reset count must not be shown as zero.");
            Assert(All<TextBlock>(window).Any(text => text.Text == "有效期：2099-10-17" && text.ToolTip is string),
                "Home shows the subscription date without a parenthetical label and exposes its check time on hover.");
            Assert(All<TextBlock>(window).Any(text => text.Text == "有效期：未知"), "Missing subscription date stays unknown.");
            var target = All<Button>(window).Single(button => button.Content is Grid && System.Windows.Automation.AutomationProperties.GetName(button) == "studio@example.test");
            target.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(window.CurrentPage == "switch", "Selecting a row must open an in-place confirmation.");
            Assert(client.Commands.Count == 0, "Selection must not switch before confirmation.");
            Render(window, Path.Combine(output, "switch-zh.png"));
            window.Navigate("manage"); Render(window, Path.Combine(output, "manage-zh.png"));
            Assert(All<TextBlock>(window).Count(text => text.Text.Contains("@example.test")) == 2, "Manage must show account email addresses.");
            Assert(!All<TextBlock>(window).Any(text => text.Text is "Personal" or "Studio"), "Manage must not duplicate the email with a display-name row.");
            var addHint = All<TextBlock>(window).Single(text => text.Text == client.State.Text("sign_in_hint"));
            var addButton = All<Button>(window).Single(button => System.Windows.Automation.AutomationProperties.GetName(button) == client.State.Text("add_account"));
            var hintPosition = addHint.TranslatePoint(new Point(), window);
            var addPosition = addButton.TranslatePoint(new Point(), window);
            Assert(hintPosition.X >= addPosition.X + addButton.ActualWidth &&
                hintPosition.Y >= addPosition.Y && hintPosition.Y + addHint.ActualHeight <= addPosition.Y + addButton.ActualHeight + 1,
                "The sign-in hint must sit to the right of Add Account within the same row.");
            Assert(All<TextBlock>(window).Count(text => text.Text == "当前") == 1, "Manage identifies the active account.");
            window.Navigate("settings"); Render(window, Path.Combine(output, "settings-zh.png"));
            Assert(All<TextBlock>(window).Any(text =>
                text.Text == "Windows local sender: test-only, actual delivery blocked"),
                "Settings must show local sender status but never grant delivery.");
            var runningSha = new NativeSettings().RunningExecutableSha256;
            Assert(runningSha is { Length: 64 } &&
                runningSha.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'),
                "Settings must distinguish running EXE builds with a SHA256 identifier.");
            using (var input = File.OpenRead(Environment.ProcessPath!)) {
                var expected = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(input)).ToLowerInvariant();
                Assert(runningSha == expected,
                    "Build fingerprint must hash the running executable, not user credentials.");
            }
            Assert(All<TextBlock>(window).Any(text =>
                    text.Text == "실행 빌드: SHA256 " + runningSha![..16] &&
                    text.ToolTip is string tip && tip.Contains(runningSha, StringComparison.Ordinal)),
                "Settings must show the actual running EXE identifier and full hash on hover.");
            Assert(All<Button>(window).Any(button =>
                    System.Windows.Automation.AutomationProperties.GetName(button) == "빌드 SHA256 복사"),
                "Settings must offer copying the full executable SHA256.");
            var existingBot = All<Button>(window).SingleOrDefault(button =>
                System.Windows.Automation.AutomationProperties.GetName(button) ==
                    "기존 Telegram 봇 폴더 선택·저장 (읽기 전용 확인)");
            Assert(existingBot is not null,
                "Settings must let the user persist the bot folder selection.");
            Assert(All<TextBlock>(window).Any(text =>
                text.Text.StartsWith("기존 Telegram 봇 폴더:", StringComparison.Ordinal)),
                "Settings must show stored/unset bot directory status without raw tokens.");
            var botFixture = Path.Combine(output, "telegram-bot-file-metadata-only");
            Directory.CreateDirectory(botFixture);
            Assert(NativeSettings.NormalizeTelegramBotDirectory(botFixture) ==
                    Path.GetFullPath(botFixture).TrimEnd(Path.DirectorySeparatorChar),
                "Existing local fixed-drive bot folder should normalize exactly.");
            Assert(NativeSettings.NormalizeTelegramBotDirectory(
                    Path.Combine(botFixture, "missing")) is null &&
                NativeSettings.NormalizeTelegramBotDirectory(
                    Path.GetPathRoot(botFixture)) is null &&
                NativeSettings.NormalizeTelegramBotDirectory(
                    @"\\remote-host\share\bot") is null,
                "Missing directories, drive roots, and UNC shares must never be saved.");
            var missingBot = TelegramBotFileStatus.Inspect(botFixture);
            Assert(!missingBot.MonitorBotFilesPresent && !missingBot.AlertBotFilesPresent &&
                !missingBot.ChatDestinationPresent,
                "Missing Telegram artifacts must not be shown as connected.");
            foreach (var filename in new[] { "send-telegram-health.ps1",
                     "telegram-health-token.dpapi", "send-telegram-alert.ps1",
                     "telegram-token.dpapi", "telegram-chat-id.txt" })
                File.WriteAllText(Path.Combine(botFixture, filename), "fixture-only");
            var foundBot = TelegramBotFileStatus.Inspect(botFixture);
            Assert(foundBot.MonitorBotFilesPresent && foundBot.AlertBotFilesPresent &&
                foundBot.ChatDestinationPresent && foundBot.Display.Contains("실제 전송: 미연결"),
                "Both bot lanes require metadata presence without authorizing any Telegram send.");
            File.WriteAllText(Path.Combine(botFixture, "telegram-health-token.dpapi"), "");
            Assert(!TelegramBotFileStatus.Inspect(botFixture).MonitorBotFilesPresent &&
                TelegramBotFileStatus.Inspect(botFixture).AlertBotFilesPresent,
                "An empty encrypted bot token must fail closed without inspecting token contents.");
            var telegramPreferences = new NativeSettings().TelegramPreferences;
            var telegramStatus = All<TextBlock>(window).SingleOrDefault(text =>
                text.Text.StartsWith("이 기기: ", StringComparison.Ordinal));
            Assert(telegramStatus is not null &&
                telegramStatus.Text.Contains(Environment.MachineName, StringComparison.Ordinal) &&
                telegramStatus.Text.Contains(telegramPreferences.DeviceRole ==
                    TelegramNotificationDeviceRole.Sender ? "Sender 요청(미승인)" : "Viewer",
                    StringComparison.Ordinal) &&
                telegramStatus.Text.Contains(telegramPreferences.NotificationsRequested ?
                    "ON(요청)" : "OFF", StringComparison.Ordinal) &&
                telegramStatus.Text.Contains("실제 Telegram 발송: 미연결",
                    StringComparison.Ordinal),
                "Isolated WPF settings must display device, role, alert request and disabled delivery.");
            var fiveHour = All<CheckBox>(window).Single(toggle => System.Windows.Automation.AutomationProperties.GetName(toggle) == "显示 5 小时用量");
            fiveHour.IsChecked = true; fiveHour.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            Assert(client.Commands.Single() == "fiveHour:True", "Settings must save immediately.");
            var experiment = All<CheckBox>(window).Single(toggle => System.Windows.Automation.AutomationProperties.GetName(toggle) == client.State.Text("auto_reset"));
            Assert(experiment.IsChecked == false, "Auto-reset must default to off.");
            experiment.IsChecked = true; experiment.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            Assert(client.Commands.SequenceEqual(new[] { "fiveHour:True", "autoReset:True" }),
                "The auto-reset toggle must send the setting to the shared host.");
            Assert(All<TextBlock>(window).Any(text => text.Text == client.State.Text("auto_reset_hint")),
                "Settings must disclose real reset consumption and runtime requirements.");
            Assert(!All<TextBlock>(window).Any(text => text.Text is "保存" or "Save"), "Settings must not have a Save flow.");
            client.State = client.State with { Settings = client.State.Settings with { ShowsFiveHourUsage = true, AutoResetEnabled = true } };
            window.Navigate("settings"); Render(window, Path.Combine(output, "settings-auto-reset-enabled-zh.png"));
            var enabledToggles = All<CheckBox>(window).ToArray();
            if (!enabledToggles.Any(toggle => System.Windows.Automation.AutomationProperties.GetName(toggle) == client.State.Text("auto_reset"))) throw new Exception("Settings toggle absent: page=" + window.CurrentPage + "; total=" + enabledToggles.Length + "; names=" + string.Join(", ", enabledToggles.Select(toggle => System.Windows.Automation.AutomationProperties.GetName(toggle))) + "; expected=" + client.State.Text("auto_reset"));
            Assert(enabledToggles.Single(toggle => System.Windows.Automation.AutomationProperties.GetName(toggle) == client.State.Text("auto_reset")).IsChecked == true,
                "The auto-reset toggle must reflect the persisted setting.");
            window.Navigate("accounts"); Render(window, Path.Combine(output, "accounts-five-hour-zh.png"));
            Assert(All<TextBlock>(window).Count(text => text.Text == "5 小时") == 2, "Five-hour view must be per account.");
            var closed = false; window.Closed += (_, _) => closed = true;
            var beforeClose = client.Commands.Count;
            window.Close();
            Assert(!window.IsVisible && !closed, "Title-bar close must hide the window without disposing the running app.");
            Assert(client.Commands.Count == beforeClose, "Hiding must not send account commands.");
            window.OpenWindow();
            Assert(window.IsVisible && !closed, "The tray must be able to reopen the same window.");
            client.State = client.State with { IsAddingAccount = true };
            window.Navigate("accounts");
            window.Hide();
            window.OpenWindow();
            Assert(window.CurrentPage == "manage", "Reopening a pending login must restore its cancellation page.");
            Render(window, Path.Combine(output, "pending-sign-in-reopened-zh.png"));
            Assert(All<Button>(window).Any(button => System.Windows.Automation.AutomationProperties.GetName(button) == client.State.Text("cancel_add_account") && button.IsEnabled),
                "The pending login must remain cancellable after reopening.");
            client.State = client.State with { IsAddingAccount = false };
            client.State = client.State with { IsMutating = true };
            window.Close();
            Assert(!window.IsVisible && !closed, "An active operation can continue while the window is hidden.");
            client.State = client.State with { IsMutating = false };
            window.CloseForExit();
            Assert(closed, "Explicit application exit must release the window.");
            WindowsLocalSenderLeaseTest.Run(output);
            VerifyMonitorPage(output, VerifyOfflineRecoveryInbox(output));
            Console.WriteLine("PASS: native taskbar window, focus persistence, compact home, full-row confirmation, management, immediate settings and optional five-hour usage.");
            Console.WriteLine(output);
            return 0;
        } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static OfflineTelegramInboxSnapshot VerifyOfflineRecoveryInbox(string output)
    {
        var directory = Path.Combine(output, "telegram-recovery-inbox-fixture");
        Assert(!Directory.Exists(directory), "Inbox fixture must start absent.");
        var missing = OfflineTelegramRecoveryInbox.Read(directory);
        Assert(missing.Rows.Count == 0 && !missing.LedgerReadError &&
            !Directory.Exists(directory),
            "Reading an absent inbox must never create directories or ledger files.");
        var now = DateTimeOffset.UtcNow;
        var firstKey = new string('a', 64);
        var collisionKey = new string('a', 63) + "b"; // same displayed eight chars
        var weeklyKey = new string('b', 64);
        var five = new FiveHour100Alert(firstKey, "FIVE_HOUR_100_RECOVERED",
            "fiveHour", 100, now.AddMinutes(-2), now.AddHours(5), "MOCK_PENDING");
        var collision = five with { Key = collisionKey,
            ObservedAt = now.AddMinutes(-1), Status = "MOCK_UNCERTAIN" };
        var week = new Weekly100Alert(weeklyKey, "WEEKLY_100_RECOVERED",
            "week", 100, now, now.AddDays(7), "MOCK_RESERVED");
        var projection = OfflineTelegramRecoveryInbox.Project(
            [five, collision, five with { ObservedAt = now.AddMinutes(-3) },
                five with { Kind = "WRONG_KIND" }],
            [week, week with { Key = "not-hex" }]);
        Assert(projection.Rows.Count == 3 &&
            projection.Rows[0].Lane == "7D" &&
            projection.Rows[0].Destination == "얼럿 봇" &&
            projection.Rows[1].Lane == "5H" &&
            projection.Rows[1].StatusLabel == "모의 결과 불확실" &&
            projection.Rows[2].StatusLabel == "모의 대기",
            "Offline inbox must route both lanes, dedupe by full key, order and label mock statuses.");
        Assert(projection.Rows[1].EventReference == projection.Rows[2].EventReference,
            "Two events sharing only a short hash prefix must not be dropped.");
        var fiveDir = Path.Combine(directory, "usage-monitor-fivehour100");
        var weekDir = Path.Combine(directory, "usage-monitor-weekly100");
        Directory.CreateDirectory(fiveDir);
        Directory.CreateDirectory(weekDir);
        var fivePath = Path.Combine(fiveDir, "state.json");
        var weekPath = Path.Combine(weekDir, "state.json");
        File.WriteAllText(fivePath,
            System.Text.Json.JsonSerializer.Serialize(
                new FiveHour100DurableState { Outbox = [five, collision] }));
        File.WriteAllText(weekPath,
            System.Text.Json.JsonSerializer.Serialize(
                new Weekly100DurableState { Outbox = [week] }));
        var onDisk = OfflineTelegramRecoveryInbox.Read(directory);
        Assert(onDisk.Rows.Count == 3 && !onDisk.LedgerReadError,
            "Real on-disk detector ledger schemas must be readable without a sender.");
        File.WriteAllText(weekPath, "{invalid-json");
        var corrupt = OfflineTelegramRecoveryInbox.Read(directory);
        Assert(corrupt.LedgerReadError && corrupt.Rows.Count == 2 &&
            corrupt.Rows.All(row => row.Lane == "5H"),
            "A corrupt 7D ledger must report an error while keeping valid 5H rows.");
        File.WriteAllText(weekPath,
            System.Text.Json.JsonSerializer.Serialize(
                new Weekly100DurableState { SchemaVersion = 999, Outbox = [week] }));
        Assert(OfflineTelegramRecoveryInbox.Read(directory).LedgerReadError,
            "Unknown ledger schema must not be reported as a valid event.");
        return projection;
    }

    private static void VerifyMonitorPage(string output, OfflineTelegramInboxSnapshot inbox)
    {
        var client = new FixtureClient();
        var history = new LocalMonitorHistoryRow(
            "a1b2c3d4", "CREDIT_INCREASE", "credits",
            DateTimeOffset.FromUnixTimeSeconds(1_800_000_000),
            1, 2, true);
        var window = new MainWindow(client, monitorHistory: () => new[] { history },
            telegramRecoveryInbox: () => inbox);
        Render(window, Path.Combine(output, "monitor-account-list-zh.png"));
        var commandsBefore = client.Commands.Count;
        var untouched = client.State;
        var monitorButton = All<Button>(window).Single(button =>
            System.Windows.Automation.AutomationProperties.GetName(button) == client.State.Text("monitor_title"));
        monitorButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert(window.CurrentPage == "monitor" && client.Commands.Count == commandsBefore,
            "Opening the monitor must not issue a change or sign-in command.");
        Render(window, Path.Combine(output, "monitor-zh.png"));
        var texts = All<TextBlock>(window).Select(text => text.Text).ToArray();
        Assert(texts.Contains("Telegram 복구 알림 대기함 (오프라인)") &&
            texts.Any(text => text.Contains("실제 발송") &&
                text.Contains("아닙니다")) &&
            texts.Any(text => text.Contains("5H → 모니터 봇(묵음)") &&
                text.Contains("모의 대기")) &&
            texts.Any(text => text.Contains("7D → 얼럿 봇") &&
                text.Contains("모의 예약")),
            "Monitor must connect real recovery ledger projections to a no-send 5H/7D inbox.");
        Assert(All<TextBlock>(window).Any(text => text.ToolTip is string tip &&
                tip.Contains("실제 Telegram 발송/수신 증거 없음")),
            "No offline recovery event may be presented as a Telegram delivery receipt.");
        Assert(texts.Contains("원격 기기 사용량 (P3)") &&
               texts.Any(text => text.Contains("Mac 5H: ?") &&
                                 text.Contains("Mac 7D: ?") &&
                                 text.Contains("Mac R: ?")),
            "Remote quota must be an independent UNKNOWN view until opt-in.");
        Assert(All<PasswordBox>(window).Count(x =>
                   System.Windows.Automation.AutomationProperties.GetName(x) ==
                       "원격 조회 자격정보") == 1,
            "Remote viewer must request an ephemeral password, not persist it.");
        Assert(All<Button>(window).Any(x =>
                   System.Windows.Automation.AutomationProperties.GetName(x) ==
                       "원격 사용량 연결"),
            "Remote viewer must require explicit connection.");
        Assert(texts.Any(text => text.Contains("personal@example.test")) &&
               texts.Any(text => text.Contains("studio@example.test")),
            "Monitor must show both account snapshot rows.");
        Assert(texts.Count(text => text == "5 小时") == 2 &&
               texts.Count(text => text == "7 天") == 2,
            "Monitor must display both quota windows independent of the home toggle.");
        Assert(texts.Contains("重置次数: 2") && texts.Contains("重置次数: 未知"),
            "Reset credits must distinguish reported quantity and UNKNOWN.");
        Assert(texts.Any(text => text.Contains(client.State.Text("monitor_credit_increase"))),
            "The monitor should show persisted mock event history when available.");
        Assert(All<TextBlock>(window).Any(text => text.ToolTip is string tip &&
            tip.Contains("a1b2c3d4") && tip.Contains(client.State.Text("monitor_mock_complete"))),
            "Historical entries must show their mock-only delivery status.");
        var unavailable = untouched.Accounts[1] with {
            Usage = null, UsageStatus = "error", UsageError = "fixture refresh failed"
        };
        client.State = untouched with { Accounts = new[] { untouched.Accounts[0], unavailable } };
        window.Navigate("monitor");
        Render(window, Path.Combine(output, "monitor-unavailable-zh.png"));
        var missing = All<TextBlock>(window).Select(text => text.Text).ToArray();
        Assert(missing.Contains("5 小时: 未知") && missing.Contains("7 天: 未知"),
            "Unreported quota windows must display UNKNOWN, not 0%.");
        var refreshButton = All<Button>(window).Single(button =>
            System.Windows.Automation.AutomationProperties.GetName(button) == client.State.Text("monitor_refresh"));
        refreshButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert(client.Commands.SequenceEqual(new[] { "refresh:" }),
            "Refresh must use the existing usage-refresh command, not an account switch.");
        window.CloseForExit();
    }

    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static IEnumerable<T> All<T>(DependencyObject parent) where T : DependencyObject {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) {
            var child = VisualTreeHelper.GetChild(parent, i); if (child is T match) yield return match;
            foreach (var descendant in All<T>(child)) yield return descendant;
        }
    }
    private static void Render(Window window, string path) {
        window.Show(); window.UpdateLayout();
        var frame = new DispatcherFrame(); window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame);
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * 2), (int)Math.Ceiling(window.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
        var visual = new DrawingVisual(); using (var drawing = visual.RenderOpen()) drawing.DrawRectangle(new VisualBrush(window), null, new Rect(0, 0, window.ActualWidth, window.ActualHeight));
        bitmap.Render(visual); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    private sealed class FixtureClient : IAccountClient {
        public event Action? Changed { add {} remove {} }
        public List<string> Commands { get; } = [];
        public AccountSnapshot State { get; set; }
        public FixtureClient() {
            var id = Guid.Parse("11111111-1111-1111-1111-111111111111");
            var time = new DateTimeOffset(2026, 9, 15, 9, 25, 0, TimeSpan.FromHours(8));
            State = new([
                new(new(id, "Personal", "personal@example.test"), "P", new(83, time, 91, time.AddHours(-2), 2), null,
                    "loaded", new(new DateTimeOffset(2099, 10, 17, 12, 0, 0, TimeSpan.Zero), time)),
                new(new(Guid.Parse("22222222-2222-2222-2222-222222222222"), "Studio", "studio@example.test"), "S", new(56, time.AddHours(1), 68, time.AddHours(-1)), null)
            ], id, new("simplifiedChinese"), false, false, true, null, new() {
                ["monitor_title"] = "用量监控", ["monitor_source_hint"] = "从现有服务读取的用量快照。", ["monitor_refresh"] = "刷新用量",
                ["monitor_last_known"] = "上次记录的数据。", ["usage"] = "用量", ["resets"] = "重置于", ["reset_credits"] = "重置次数", ["unknown"] = "未知", ["left"] = "% 剩余", ["manage"] = "管理账号", ["settings"] = "设置", ["quit"] = "退出应用",
                ["subscription_until"] = "有效期：%@", ["subscription_snapshot"] = "登录快照", ["subscription_old_snapshot"] = "旧登录快照",
                ["subscription_unknown"] = "有效期：未知", ["subscription_detail"] = "最近核验于 %@。",
                ["accounts"] = "账号", ["back"] = "返回", ["active"] = "当前", ["remove"] = "移除", ["add_account"] = "添加账号",
                ["sign_in_hint"] = "将打开浏览器进行 Codex 登录。", ["register_current_account"] = "登记当前登录账号",
                ["settings_general"] = "通用", ["settings_updates"] = "软件更新", ["launch_at_login"] = "登录时自动启动",
                ["settings_automation"] = "自动化", ["auto_reset"] = "到期前自动使用 reset",
                ["auto_reset_hint"] = "到期前 10 分钟自动使用所有已保存账号的 reset；应用须运行且电脑保持唤醒。", ["open_reset_log"] = "打开日志文件夹",
                ["show_menu_bar_percentage"] = "在菜单栏显示百分比", ["show_five_hour_usage"] = "显示 5 小时用量", ["language"] = "语言",
                ["system_default"] = "跟随系统", ["english"] = "English", ["simplified_chinese"] = "简体中文", ["five_hour"] = "5 小时", ["weekly"] = "7 天",
                ["automatically_check_updates"] = "自动检查更新", ["update_check_hint"] = "每小时检查一次，有新版本时显示蓝点。",
                ["current_version"] = "当前版本 %@", ["check_for_updates"] = "检查更新", ["cancel"] = "取消", ["switch"] = "切换账号",
                ["switch_title"] = "切换到 %@？", ["switch_body"] = "Codex Desktop 将关闭并重新打开。请先完成或停止正在运行的 Desktop 任务。如果 Desktop 显示退出提示，请处理该提示；无法正常退出时会停止切换。现有 CLI 会话保持运行，新 CLI 会话将使用所选账号。"
            });
        }
        public Task CommandAsync(string command, Guid? accountID = null, bool? value = null, string? language = null) {
            Commands.Add(command + ":" + value); return Task.CompletedTask;
        }
    }
}
