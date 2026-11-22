using System.Text.Json;
using System.IO;
using CodexAccountSwitcher;
using CodexAccountSwitcher.Core;
using DedicatedThreadCycleStarter;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.IO.Pipes;
using System.Text;

// Real native SwiftHost must use isolated fake Codex CLI and temp identities.
// Match the actual Swift JSONRPCSession launch argv ["app-server", "--stdio"].
if (args.Length is 1 or 2 && args[0] == "app-server" &&
    (args.Length == 1 || args[1] == "--stdio"))
    return await NativeSwiftHostRefreshSmoke.FakeCodexRpcAsync();
if (args.Length == 2 && args[0] == "--native-swift-host-refresh-no-send")
    return await NativeSwiftHostRefreshSmoke.RunAsync(args[1]);

int passed = 0, failed = 0;
async Task Test(string title, Func<Task> action)
{
    try { await action(); passed++; Console.WriteLine($"PASS {passed + failed:D2} {title}"); }
    catch (Exception ex) {
        failed++;
        Console.WriteLine($"FAIL {passed + failed:D2} {title}: {ex.GetType().Name}");
    }
}
static void Ensure(bool ok) { if (!ok) throw new InvalidOperationException("Test invariant"); }

var profileId = Guid.Parse("11111111-1111-4111-8111-111111111111");
var otherId = Guid.Parse("22222222-2222-4222-8222-222222222222");
AccountSnapshot Snapshot(Guid? activeId, bool confirmed = true,
    bool mutating = false, bool adding = false, bool duplicated = false,
    string? email = "person@example.test")
{
    var rows = new List<AccountRow> {
        new(new Profile(profileId, "Demo", email), "D", null, null)
    };
    if (duplicated) rows.Add(rows[0]);
    return new AccountSnapshot(rows.ToArray(), activeId, new Preferences(),
        mutating, adding, confirmed, null, new Dictionary<string,string>());
}
await Test("P015 native protected-input status never enables Owner", () => {
    // Production admission stays internal. Inspect it without granting
    // the integrated test assembly any new permission to send or start.
    var type = typeof(App).Assembly.GetType(
        "CodexAccountSwitcher.WindowsOwnerReadOnlyAdmission", throwOnError: true)!;
    var flags = BindingFlags.Static | BindingFlags.NonPublic;
    var status = (string?)type.GetMethod("ProtectedTrustSourceStatus", flags)!
        .Invoke(null, null);
    Ensure(status is not null &&
        status.StartsWith("HOLD_", StringComparison.Ordinal));
    var holdReason = type.GetMethod("HoldReason", flags)!;
    var isReady = type.GetMethod("IsReady", flags)!;
    var home = Path.GetTempPath();
    Ensure((string?)holdReason.Invoke(null, [home]) ==
        "HOLD_OWNER_ADMISSION_TRUST_UNAVAILABLE");
    Ensure((bool?)isReady.Invoke(null, [home]) == false);
    var map = type.GetMethod("ProtectedSourceHoldStatus", flags)!;
    // Even a hypothetical OS-protected source DOES NOT establish
    // independent dual-signature or Owner lease admission.
    Ensure((string?)map.Invoke(null, [
        true, "P015_PROTECTED_INPUTS_PRESENT_SIGNATURE_UNCHECKED_NO_SEND"
    ]) == "HOLD_OWNER_SIGNED_HEAD_AND_ENROLLMENT_NOT_ADMITTED");
    Ensure((string?)map.Invoke(null, [false, "UNRECOGNIZED"]) ==
        "HOLD_OWNER_ADMISSION_TRUST_UNAVAILABLE");
    Ensure((string?)holdReason.Invoke(null, [string.Empty]) ==
        "HOLD_OWNER_DATA_HOME");
    return Task.CompletedTask;
});

var fake = new FakeAccountClient { State = Snapshot(profileId) };
var binaryResolved = 0;
var context = new PersonalExistingThreadContext(fake,
    Path.Combine(Path.GetTempPath(), "synthetic-switcher-personal"),
    () => { binaryResolved++; throw new Exception("CLI startup not authorized"); });
await Test("Verified active profile maps to exact GUID and email", () => {
    var actual = context.CurrentIdentity();
    Ensure(actual.ActiveProfileId == profileId &&
        actual.SelectedActiveAccountId == profileId &&
        actual.ProfileEmail == "person@example.test" &&
        actual.IdentityConfirmed && !actual.IsSwitching);
    return Task.CompletedTask;
});
await Test("No active account is never verified", () => {
    fake.State = Snapshot(null);
    var actual = context.CurrentIdentity();
    Ensure(!actual.IdentityConfirmed && actual.ActiveProfileId == Guid.Empty);
    return Task.CompletedTask;
});
await Test("Active account mismatch has no valid matching row", () => {
    fake.State = Snapshot(otherId);
    Ensure(!context.CurrentIdentity().IdentityConfirmed);
    return Task.CompletedTask;
});
await Test("Duplicate active profile rows rejected", () => {
    fake.State = Snapshot(profileId, duplicated: true);
    Ensure(!context.CurrentIdentity().IdentityConfirmed);
    return Task.CompletedTask;
});
await Test("Unconfirmed active profile remains unverified", () => {
    fake.State = Snapshot(profileId, confirmed: false);
    Ensure(!context.CurrentIdentity().IdentityConfirmed);
    return Task.CompletedTask;
});
await Test("Account switching blocks read-only session creation", () => {
    fake.State = Snapshot(profileId, mutating: true);
    var actual = context.CurrentIdentity();
    Ensure(actual.IsSwitching);
    return Task.CompletedTask;
});
await Test("Profile-add action blocks read-only session creation", () => {
    fake.State = Snapshot(profileId, adding: true);
    Ensure(context.CurrentIdentity().IsSwitching);
    return Task.CompletedTask;
});
await Test("Stale expected identity never resolves or starts codex.exe", async () => {
    fake.State = Snapshot(profileId);
    var previous = context.CurrentIdentity();
    fake.State = Snapshot(profileId, mutating: true);
    try {
        await context.OpenReadOnlyAsync(previous, CancellationToken.None);
        throw new Exception("Unexpected session");
    } catch (InvalidOperationException) { }
    Ensure(binaryResolved == 0);
});
await Test("Missing active email is not fabricated", () => {
    fake.State = Snapshot(profileId, email: null);
    Ensure(context.CurrentIdentity().ProfileEmail is null);
    return Task.CompletedTask;
});
await Test("MainWindow candidate exposes optional existing-thread data-home binding", () => {
    var ctor = typeof(MainWindow).GetConstructors().Single();
    var last = ctor.GetParameters().Last();
    Ensure(last.Name == "dedicatedExistingThreadDataHome" && last.IsOptional);
    return Task.CompletedTask;
});
await Test("Read-only picker keeps three user toggles disabled for unbound account", () => {
    Exception? failure = null;
    var controls = new List<CheckBox>();
    var thread = new Thread(() => {
        try {
            var panel = new DedicatedThreadPickerPanel(context.CurrentIdentity,
                (_, _) => throw new Exception("Unexpected Codex start"),
                Path.Combine(Path.GetTempPath(), "synthetic-switcher-personal"));
            Ensure(panel.Content is StackPanel);
            foreach (var child in ((StackPanel)panel.Content).Children)
                if (child is CheckBox cb) controls.Add(cb);
            Ensure(controls.Count == 3);
            Ensure(controls.All(x =>
                x.IsEnabled == false && x.IsChecked == false));
        } catch (Exception ex) { failure = ex; }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    if (!thread.Join(TimeSpan.FromSeconds(5))) throw new TimeoutException("STA preview");
    if (failure is not null) throw failure;
    return Task.CompletedTask;
});
await Test("Isolated MainWindow Settings renders dedicated-thread picker", () => {
    Exception? failure = null;
    var visible = false;
    var thread = new Thread(() => {
        try {
            fake.State = Snapshot(profileId);
            var app = new App(false);
            app.InitializeComponent();
            var window = new MainWindow(fake,
                dedicatedExistingThreadDataHome: Path.Combine(
                    Path.GetTempPath(), "synthetic-switcher-personal"));
            window.Navigate("settings");
            // STEP 2: physically render the existing settings UI under one
            // synthetic WPF App(false) without CoreClient startup, Telegram
            // session, taskbar registration or real account/profile writes.
            window.ShowInTaskbar = false;
            window.ShowActivated = false;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -9000;
            window.Top = -9000;
            window.Show();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            window.UpdateLayout();
            if (!(window.IsVisible && window.ActualWidth >= 400 &&
                window.ActualHeight >= 250))
                throw new InvalidOperationException("WPF_WINDOW_SIZE_OR_VISIBLE");
            var width = (int)Math.Ceiling(window.ActualWidth);
            var height = (int)Math.Ceiling(window.ActualHeight);
            var rendered = new System.Windows.Media.Imaging.RenderTargetBitmap(
                width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            rendered.Render(window);
            var pixels = new byte[width * height * 4];
            rendered.CopyPixels(pixels, width * 4, 0);
            if (!pixels.Where((value, i) => (i & 3) == 3)
                .Any(value => value != 0))
                throw new InvalidOperationException("WPF_RENDERED_ALPHA_EMPTY");
            static bool FindPanel(DependencyObject element)
            {
                if (element is DedicatedThreadPickerPanel) return true;
                foreach (var child in LogicalTreeHelper.GetChildren(element))
                    if (child is DependencyObject d && FindPanel(d))
                        return true;
                return false;
            }
            visible = window.Content is DependencyObject root && FindPanel(root);
            // Synthetic settings rendering only; never mutate the live HKCU.
            static ComboBox? FindTaskbarFontPicker(DependencyObject element)
            {
                if (element is ComboBox cb &&
                    System.Windows.Automation.AutomationProperties.GetName(cb) ==
                        "작업표시줄 글자 크기")
                    return cb;
                foreach (var child in LogicalTreeHelper.GetChildren(element))
                    if (child is DependencyObject d &&
                        FindTaskbarFontPicker(d) is { } found)
                        return found;
                return null;
            }
            var sizePicker = window.Content is DependencyObject settingsRoot
                ? FindTaskbarFontPicker(settingsRoot) : null;
            if (sizePicker is null)
                throw new InvalidOperationException("TASKBAR_FONT_PICKER_NOT_FOUND");
            if (sizePicker.Items.Count != 5)
                throw new InvalidOperationException("TASKBAR_FONT_PICKER_COUNT_" + sizePicker.Items.Count);
            if (!(sizePicker.Items[0]?.ToString()?.Contains("(9)") == true &&
                  sizePicker.Items[1]?.ToString()?.Contains("(10)") == true &&
                  sizePicker.Items[3]?.ToString()?.Contains("(12)") == true))
                throw new InvalidOperationException("TASKBAR_FONT_PICKER_OPTIONS_MISSING");
            if (sizePicker.SelectedIndex is < 0 or > 4)
                throw new InvalidOperationException("TASKBAR_FONT_PICKER_SELECTED_INVALID");
            // Same synthetic STA, second settings GUI. No HKCU writes.
            var trialWindow = new MainWindow(fake,
                new NativeSettings(blockWrites: true));
            trialWindow.Navigate("settings");
            trialWindow.ShowInTaskbar = false;
            trialWindow.ShowActivated = false;
            trialWindow.WindowStartupLocation = WindowStartupLocation.Manual;
            trialWindow.Left = -9000;
            trialWindow.Top = -9000;
            trialWindow.Show();
            trialWindow.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            trialWindow.UpdateLayout();
            var trialPicker = trialWindow.Content is DependencyObject trialRoot
                ? FindTaskbarFontPicker(trialRoot) : null;
            Ensure(trialPicker is { IsEnabled: false });
            trialWindow.CloseForExit();
            window.CloseForExit();
            app.Shutdown();
        }
        catch (Exception ex) {
            Console.WriteLine("SYNTHETIC_SETTINGS_FAILURE=" + ex.Message);
            failure = ex;
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    if (!thread.Join(TimeSpan.FromSeconds(8)))
        throw new TimeoutException("Isolated settings STA");
    if (failure is not null) throw failure;
    Ensure(visible);
    return Task.CompletedTask;
});
await Test("Windows basic viewer trial rejects all native HKCU setting writes", () => {
    var trial = new NativeSettings(blockWrites: true);
    Ensure(trial.IsReadOnly);
    var attempts = new Action[] {
        () => trial.LaunchAtLogin = false,
        () => trial.AutomaticallyCheckUpdates = false,
        () => trial.ShowTaskbarResetCountdown = false,
        () => trial.TaskbarFontSize = 9,
        () => trial.SetTelegramPreferences(new TelegramNotificationPreferences()),
        () => trial.SetTelegramBotDirectory(null)
    };
    foreach (var change in attempts)
    {
        var blocked = false;
        try { change(); }
        catch (InvalidOperationException) { blocked = true; }
        Ensure(blocked);
    }
    return Task.CompletedTask;
});
await Test("V7.2 RemoteGracefulQuit owner channel remains compiled", () => {
    var types = typeof(App).GetFields(BindingFlags.Instance | BindingFlags.NonPublic);
    Ensure(types.Any(f => f.FieldType.Name == "RemoteGracefulQuitChannel"));
    Ensure(types.Any(f => f.Name == "remoteQuitPending"));
    return Task.CompletedTask;
});
await Test("V7.2 sticky Telegram consent and restart logic remain compiled", () => {
    var fields = typeof(App).GetFields(BindingFlags.Instance | BindingFlags.NonPublic);
    Ensure(fields.Any(f => f.FieldType.Name == "WindowsTelegramStickyAutoConsentStore"));
    Ensure(fields.Any(f => f.FieldType.Name == "WindowsTelegramRestartIntentStore"));
    Ensure(typeof(NativeSettings).GetProperty("TaskbarFontSize") is not null);
    Ensure(typeof(NativeSettings).GetProperty("ShowTaskbarResetCountdown") is not null);
    return Task.CompletedTask;
});
await Test("V7.2 picker offers Desktop and profile homes without sending", () => {
    Exception? fail = null;
    var checkedStorage = false;
    var sta = new Thread(() => {
        try {
            var panel = new DedicatedThreadPickerPanel(context.CurrentIdentity,
                (_, _) => throw new InvalidOperationException("profile transport not called"),
                Path.Combine(Path.GetTempPath(), "synthetic-switcher-personal"),
                (_, _) => throw new InvalidOperationException("Desktop transport not called"));
            var children = ((StackPanel)panel.Content).Children;
            var selector = children.OfType<ComboBox>().First();
            Ensure(selector.Items.Count == 2 && selector.SelectedIndex == 0 &&
                selector.IsEnabled);
            selector.SelectedIndex = 1;
            Ensure(selector.SelectedIndex == 1);
            checkedStorage = true;
        }
        catch (Exception e) { fail = e; }
    });
    sta.SetApartmentState(ApartmentState.STA);
    sta.Start();
    if (!sta.Join(TimeSpan.FromSeconds(6))) throw new TimeoutException("WPF storage selector");
    if (fail is not null) throw fail;
    Ensure(checkedStorage);
    return Task.CompletedTask;
});
await Test("Native Windows Store Codex path is distinct from npm CLI", () => {
    var root=@"C:\Program Files";
    var packaged=Path.Combine(root,"WindowsApps",
        "OpenAI.Codex_26.1111.9664.0_x64__2p2nqsd0c76g0",
        "app","resources","codex.exe");
    Ensure(PersonalExistingThreadContext.IsTrustedDesktopBinaryPath(packaged,root));
    Ensure(!PersonalExistingThreadContext.IsTrustedDesktopBinaryPath(
        Path.Combine(root,"WindowsApps","Unknown.Codex_1.0_x64__2p2nqsd0c76g0",
            "app","resources","codex.exe"),root));
    Ensure(!PersonalExistingThreadContext.IsTrustedDesktopBinaryPath(
        Path.Combine(root,"WindowsApps",
            "OpenAI.Codex_26.1111.9664.0_x64__otherpublisher",
            "app","resources","codex.exe"),root));
    Ensure(!PersonalExistingThreadContext.IsTrustedDesktopBinaryPath(
        @"C:\Users\Example\AppData\Roaming\npm\codex.exe",root));
    return Task.CompletedTask;
});
await Test("Actual native thread/list name and preview fallback work without title", async () => {
    var id1=Guid.Parse("11111111-1111-4111-8111-111111111111").ToString("D");
    var id2=Guid.Parse("22222222-2222-4222-8222-222222222222").ToString("D");
    using var json=JsonDocument.Parse(JsonSerializer.Serialize(new {
        data=new [] {
            new {id=id1,name="Dedicated chat",preview="First prompt"},
            new {id=id2,name="",preview="Preview based title"}
        }
    }));
    var fakeRpc=new LocalJsonRpc((method,_) => {
        Ensure(method=="thread/list");
        return json.RootElement.Clone();
    });
    var result=await new ExistingCodexThreadRpc(fakeRpc).ListCurrentAccountAsync(null);
    Ensure(result.Count==2 && result[0].Title=="Dedicated chat" &&
        result[1].Title=="Preview based title" && result[1].Id==id2);
});
await Test("Persisted notLoaded thread may be pinned, but is NOT idle for sending", async () => {
    var id=Guid.Parse("33333333-3333-4333-8333-333333333333").ToString("D");
    using var json=JsonDocument.Parse(JsonSerializer.Serialize(new {
        thread=new {
            id,ephemeral=false,path=@"C:\synthetic\rollouts\thread.jsonl",
            status=new {type="notLoaded"}
        }
    }));
    var fakeRpc=new LocalJsonRpc((method,_) => {
        Ensure(method=="thread/read");
        return json.RootElement.Clone();
    });
    var parsed=await new ExistingCodexThreadRpc(fakeRpc).ReadAsync(id,default);
    Ensure(parsed is {IsPersisted:true,IsNotLoaded:true,IsIdle:false});
});
// Scheduled events are NO-SEND previews, distinct from rollover Telegram events.
var windowBoundary = new DateTimeOffset(2026, 11, 20, 5, 0, 0, TimeSpan.Zero);
var weekBoundary = windowBoundary.AddDays(3);
CodexMonitorWireSample WindowSample(DateTimeOffset seen, DateTimeOffset fiveAt,
    DateTimeOffset weekAt, double used = 62,
    bool? ordinary = true, Guid? id = null)
{
    var json = JsonSerializer.Serialize(new {
        accountId = "synthetic-provider-account",
        ordinaryUsageAllowed = ordinary,
        rateLimitsByLimitId = new {
            codex = new {
                planType = "plus",
                primary = new {
                    usedPercent = used,
                    resetsAt = fiveAt.ToUnixTimeSeconds(), windowDurationMins = 300
                },
                secondary = new {
                    usedPercent = used,
                    resetsAt = weekAt.ToUnixTimeSeconds(), windowDurationMins = 10080
                }
            }
        }
    });
    return new CodexMonitorWireSample(seen.ToUnixTimeMilliseconds(), json, id ?? profileId);
}
(DedicatedResetDeadlineObserver watch, ActiveMonitorPublicationGate gate,
    AccountSnapshot snapshot) NewWatcher() {
    var snapshot = Snapshot(profileId);
    var gate = new ActiveMonitorPublicationGate();
    gate.Observe(snapshot);
    return (new DedicatedResetDeadlineObserver(),gate,snapshot);
}
await Test("5H pre-reset 0% evidence selects Alert Bot only in dry-run", () => {
    FiveHour100Alert Observe(double usedBefore, double minutesBefore)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "codex-step3a-" +
            Guid.NewGuid().ToString("N"));
        try
        {
            var worker = new FiveHour100RecoveryWorker(tmp);
            var (watch, gate, account) = NewWatcher();
            var before = windowBoundary.AddMinutes(-minutesBefore);
            Ensure(worker.Process(WindowSample(before, windowBoundary, weekBoundary,
                usedBefore), account, gate, before).Count == 0);
            var after = windowBoundary.AddMinutes(1);
            var alerts = worker.Process(WindowSample(after,
                windowBoundary.AddMinutes(300), weekBoundary, used: 2),
                account, gate, after);
            Ensure(alerts.Count == 1);
            return worker.ReadMockOutbox().Single();
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        }
    }
    var zero = Observe(100, 2);
    Ensure(zero.PreResetRemainingPercent == 0 &&
        zero.PreResetObservedAt == windowBoundary.AddMinutes(-2) &&
        zero.PreResetWindowEndsAt == windowBoundary &&
        QuotaTelegramBotRouting.PreviewFiveHourZeroDestination(zero) ==
            QuotaTelegramBot.AlertImportant);
    // The existing production 5H monitor sender is unchanged and not armed.
    Ensure(QuotaTelegramBotRouting.ForFiveHour(zero)?.Destination ==
        QuotaTelegramBot.MonitorMuted);
    var nonZero = Observe(99, 2);
    Ensure(nonZero.PreResetRemainingPercent == 1 &&
        QuotaTelegramBotRouting.PreviewFiveHourZeroDestination(nonZero) ==
            QuotaTelegramBot.MonitorMuted);
    var stale = Observe(100, 30);
    Ensure(stale.PreResetRemainingPercent is null &&
        QuotaTelegramBotRouting.PreviewFiveHourZeroDestination(stale) ==
            QuotaTelegramBot.MonitorMuted);
    // Observation precisely on a reset boundary does not prove the
    // immediately preceding quota window.
    var boundary = zero with { PreResetObservedAt = windowBoundary };
    Ensure(QuotaTelegramBotRouting.PreviewFiveHourZeroDestination(boundary) ==
        QuotaTelegramBot.MonitorMuted);
    var legacy = zero with {
        PreResetRemainingPercent = null,
        PreResetObservedAt = null, PreResetWindowEndsAt = null
    };
    Ensure(QuotaTelegramBotRouting.PreviewFiveHourZeroDestination(legacy) ==
        QuotaTelegramBot.MonitorMuted);
    var invalid = zero with { PreResetObservedAt = windowBoundary.AddMinutes(1) };
    Ensure(QuotaTelegramBotRouting.PreviewFiveHourZeroDestination(invalid) ==
        QuotaTelegramBot.MonitorMuted);
    return Task.CompletedTask;
});
await Test("5H zero Alert Bot has pinned no-send route and one durable event key", () => {
    var synthetic = Path.Combine(Path.GetTempPath(), "codex-step3a-alert-" +
        Guid.NewGuid().ToString("N"));
    var host = Path.Combine(synthetic, "host");
    var bot = Path.Combine(synthetic, "fake-existing-bot");
    Directory.CreateDirectory(host);
    Directory.CreateDirectory(bot);
    try
    {
        var source = Path.GetFullPath(Path.Combine(
            Directory.GetCurrentDirectory(), "..", "..", "..",
            "windows", "scripts",
            WindowsPackagedTelegramScripts.FiveHourZeroAlertScriptName));
        var script = Path.Combine(host,
            WindowsPackagedTelegramScripts.FiveHourZeroAlertScriptName);
        File.Copy(source, script);
        // Never load a real bot token or chat ID. These are dummy file
        // metadata fixtures; no authentication, PowerShell or network.
        foreach (var name in new[] {
            "send-telegram-alert.ps1", "telegram-token.dpapi",
            "telegram-chat-id.txt" })
            File.WriteAllText(Path.Combine(bot, name), "offline-fixture-only");
        var hostPath = Path.Combine(host, "SwitcherHost.exe");
        File.WriteAllText(hostPath, "synthetic-host");
        Ensure(WindowsPackagedTelegramScripts.Inspect(hostPath)
            .FiveHourZeroAlertVerified);
        var worker = new FiveHour100RecoveryWorker(synthetic);
        var (watch, gate, account) = NewWatcher();
        var first = windowBoundary.AddMinutes(-1);
        Ensure(worker.Process(WindowSample(first, windowBoundary, weekBoundary,
            used: 100), account, gate, first).Count == 0);
        var after = windowBoundary.AddMinutes(1);
        var sentEvents = worker.Process(WindowSample(after,
            windowBoundary.AddMinutes(300), weekBoundary,
            used: 2), account, gate, after);
        Ensure(sentEvents.Count == 1);
        var zero = sentEvents.Single();
        var important = QuotaTelegramBotRouting.ForFiveHourZeroEscalation(zero);
        var normal = QuotaTelegramBotRouting.ForFiveHour(zero);
        Ensure(important is { Destination: QuotaTelegramBot.AlertImportant,
            EncryptedTokenFileName: "telegram-token.dpapi",
            ProposedSenderScriptName: "send-fivehour-zero-existing-alert-bot.ps1" });
        Ensure(normal is { Destination: QuotaTelegramBot.MonitorMuted });
        Ensure(important!.Text.Contains("직전 잔여 0%"));
        var prepared = WindowsExistingAmanoBotRoute.PrepareFiveHourZero(
            hostPath, bot, zero);
        Ensure(prepared is { Silent: false, ActualTelegramSendAuthorized: false } &&
            prepared.ReviewedCodexScriptPath == script &&
            prepared.EventKey == zero.Key &&
            prepared.Message == important!.Text);
        // The REAL production outbox is exercised only inside synthetic
        // OS temp, with fake bot metadata and NO delivery coordinator.
        // Important 5H stays permanently unreservable by old send paths.
        var approvedForStaging = new TelegramNotificationPreferences(
            DeviceRole: TelegramNotificationDeviceRole.Sender,
            NotificationsRequested: true,
            FiveHourRequested: true,
            WeeklyRequested: true);
        var off = approvedForStaging with { NotificationsRequested = false };
        var outbox = new WindowsAmanoProductionOutbox(synthetic);
        Ensure(!outbox.StageFiveHourZeroImportant(zero, prepared, off));
        Ensure(outbox.Read().Count == 0);
        // Start with a real-schema legacy v1 ordinary 7D row. The new
        // version reads it, and upgrades atomically only for the 5H urgent.
        var anotherKey = new string('e', 64);
        Ensure(outbox.Stage(TelegramNotificationLane.WeeklyRecovery,
            anotherKey, zero.ObservedAt, true, approvedForStaging));
        var ledgerPath = Path.Combine(synthetic,
            "telegram-production-outbox", "state.json");
        using (var v1 = JsonDocument.Parse(File.ReadAllText(ledgerPath)))
            Ensure(v1.RootElement.GetProperty("SchemaVersion").GetInt32() == 1);
        Ensure(outbox.StageFiveHourZeroImportant(zero, prepared, approvedForStaging));
        using (var v2 = JsonDocument.Parse(File.ReadAllText(ledgerPath)))
            Ensure(v2.RootElement.GetProperty("SchemaVersion").GetInt32() == 2);
        var pending = outbox.Read().Single(x => x.FiveHourZeroImportant);
        Ensure(pending.Lane == TelegramNotificationLane.FiveHourRecovery &&
            pending.EventKey == zero.Key && pending.FiveHourZeroImportant &&
            !pending.IsTest &&
            pending.Status == WindowsAmanoOutboxStatus.AwaitingApproval);
        Ensure(outbox.ReadPendingRows().Single(x => x.FiveHourZeroImportant)
            .LaneLabel.Contains("송신 차단"));
        Ensure(!outbox.StageFiveHourZeroImportant(
            zero, prepared, approvedForStaging));
        // No second outbox item may be staged under the ordinary 5H lane.
        Ensure(!outbox.Stage(TelegramNotificationLane.FiveHourRecovery,
            zero.Key, zero.ObservedAt, true, approvedForStaging));
        Ensure(outbox.ReserveOnce(TelegramNotificationLane.FiveHourRecovery,
            zero.Key, userApprovedThisExactEvent: true,
            verifiedLocalSender: true,
            crossHostSenderHandoffConfirmed: true) is null);
        Ensure(outbox.Read().Single(x => x.FiveHourZeroImportant).Status ==
            WindowsAmanoOutboxStatus.AwaitingApproval);
        // Existing 7D and ordinary 5H entries stay separate and preserve
        // their original (non-important) classification.
        var otherKey = new string('f', 64);
        Ensure(outbox.Stage(TelegramNotificationLane.FiveHourRecovery,
            otherKey, zero.ObservedAt, true, approvedForStaging));
        Ensure(outbox.Read().Count == 3 &&
            outbox.Read().Count(x => x.FiveHourZeroImportant) == 1);
        // One shared 5H event key is claimed once regardless of which bot
        // preview is selected; no second claim after failure/uncertainty.
        var reserved = worker.ReserveMockDelivery(zero.Key, "MOCK_WINDOWS");
        Ensure(reserved is {Status: "MOCK_RESERVED"} &&
            worker.ReserveMockDelivery(zero.Key, "MOCK_WINDOWS") is null);
        Ensure(worker.MarkMockUncertain(zero.Key, "MOCK_WINDOWS"));
        Ensure(worker.ReserveMockDelivery(zero.Key, "MOCK_WINDOWS") is null);
        var good = new WindowsAlertBotDryRunResult(0,
            TelegramFiveHourZeroReceiptContract.FormatForFakeTest(zero.Key, 17) + Environment.NewLine);
        Ensure(TelegramFiveHourZeroReceiptContract.TryParse(good, zero.Key, out var msg) &&
            msg == 17);
        Ensure(!TelegramFiveHourZeroReceiptContract.TryParse(good,
            new string('a',64), out _));
        Ensure(!TelegramFiveHourZeroReceiptContract.TryParse(
            good with { StandardError = "fake-error" }, zero.Key, out _));
        Ensure(!TelegramFiveHourZeroReceiptContract.TryParse(
            new WindowsAlertBotDryRunResult(0,
                TelegramWeekly100ReceiptContract.FormatForFakeTest(zero.Key, 17)),
            zero.Key, out _));
        // A changed local sender script never qualifies for the important route.
        File.AppendAllText(script, "#tampered");
        Ensure(WindowsExistingAmanoBotRoute.PrepareFiveHourZero(
            hostPath, bot, zero) is null);
    }
    finally { if (Directory.Exists(synthetic)) Directory.Delete(synthetic, true); }
    return Task.CompletedTask;
});
await Test("Windows adaptive quota refresh respects activity reset and account gate", () => {
    var start = windowBoundary.AddHours(-2);
    var policy = new AdaptiveUsageRefreshPolicy(start);
    var account = Snapshot(profileId);
    Ensure(!policy.TrySchedule(start, true, account));
    Ensure(!policy.TrySchedule(start.AddSeconds(29), true, account));
    Ensure(policy.TrySchedule(start.AddSeconds(30), true, account));
    Ensure(!policy.TrySchedule(start.AddMinutes(1), true, account));
    Ensure(!policy.TrySchedule(start.AddMinutes(1).AddSeconds(11), true, account));
    Ensure(policy.ConsecutiveMisses == 1);
    // Missing provider observation backs off to 1 minute, not 30 seconds.
    Ensure(policy.TrySchedule(start.AddMinutes(2), true, account));
    policy.Observed(profileId, start.AddMinutes(2).AddSeconds(5),
        fiveHourNextResetAt: start.AddMinutes(5));
    // Near reset, use 15 seconds even without foreground Codex activity.
    Ensure(!policy.TrySchedule(start.AddMinutes(2).AddSeconds(19), false, account));
    Ensure(policy.TrySchedule(start.AddMinutes(2).AddSeconds(21), false, account));
    // Unconfirmed account and account mutation always suppress polling.
    Ensure(!policy.TrySchedule(start.AddMinutes(5), true,
        Snapshot(profileId, mutating: true)));
    Ensure(policy.ActiveProfile is null);
    Ensure(!policy.TrySchedule(start.AddMinutes(6), false, account));
    Ensure(!policy.TrySchedule(start.AddMinutes(7), true,
        Snapshot(profileId, confirmed: false)));
    return Task.CompletedTask;
});
await Test("Windows quota refresh 30s/15s/backoff and idle guard", () => {
    Ensure(AdaptiveUsageRefreshPolicy.ActiveInterval == TimeSpan.FromSeconds(30));
    Ensure(AdaptiveUsageRefreshPolicy.ResetWatchInterval == TimeSpan.FromSeconds(15));
    Ensure(AdaptiveUsageRefreshPolicy.BaselineInterval == TimeSpan.FromMinutes(5));
    Ensure(AdaptiveUsageRefreshPolicy.IntervalForMisses(1) == TimeSpan.FromMinutes(1));
    Ensure(AdaptiveUsageRefreshPolicy.IntervalForMisses(2) == TimeSpan.FromMinutes(2));
    Ensure(AdaptiveUsageRefreshPolicy.IntervalForMisses(3) == TimeSpan.FromMinutes(5));
    var now = windowBoundary.AddHours(-1);
    var p = new AdaptiveUsageRefreshPolicy(now);
    var confirmed = Snapshot(profileId);
    Ensure(!p.TrySchedule(now.AddMinutes(10), false, confirmed));
    // Verified provider reset in one minute arms 15s checks when idle.
    p.Observed(profileId, now.AddMinutes(10),
        fiveHourNextResetAt: now.AddMinutes(11));
    Ensure(!p.TrySchedule(now.AddMinutes(10).AddSeconds(14), false, confirmed));
    Ensure(p.TrySchedule(now.AddMinutes(10).AddSeconds(15), false, confirmed));
    Ensure(!p.TrySchedule(now.AddMinutes(10).AddSeconds(45), false, confirmed));
    Ensure(p.ConsecutiveMisses == 0);
    Ensure(!p.TrySchedule(now.AddMinutes(10).AddSeconds(56), false, confirmed));
    Ensure(p.ConsecutiveMisses == 1);
    Ensure(p.TrySchedule(now.AddMinutes(11).AddSeconds(15), false, confirmed));
    p.RequestTransportFailed(now.AddMinutes(11).AddSeconds(16));
    Ensure(p.ConsecutiveMisses == 2);
    Ensure(!p.TrySchedule(now.AddMinutes(12), false, confirmed));
    Ensure(p.TrySchedule(now.AddMinutes(13).AddSeconds(15), false, confirmed));
    return Task.CompletedTask;
});
await Test("SwiftHost active-only observation matches strict Windows 5H/7D wire contract", () => {
    var now = windowBoundary.AddMinutes(-1);
    var wire = JsonSerializer.Serialize(new {
        kind = "monitorObservation",
        profileID = profileId.ToString("D"),
        observedAtMs = now.ToUnixTimeMilliseconds(),
        raw = new {
            accountId = "synthetic-provider-account",
            ordinaryUsageAllowed = true,
            rateLimitsByLimitId = new {
                codex = new {
                    planType = (string?)null,
                    primary = new {
                        usedPercent = 82, resetsAt = windowBoundary.ToUnixTimeSeconds(),
                        windowDurationMins = 300
                    },
                    secondary = new {
                        usedPercent = 40, resetsAt = weekBoundary.ToUnixTimeSeconds(),
                        windowDurationMins = 10080
                    }
                }
            },
            rateLimitResetCredits = new { availableCount = (int?)null }
        }
    });
    var result = SwiftHostStrictEnvelopeV1Review.Inspect(wire);
    Ensure(result.Status == "OFFLINE_STRICT_WIRE_CANDIDATE_ONLY" &&
        result.Sample is { ProfileID: var source } &&
        source == profileId &&
        result.Sample.ObservedAtMilliseconds == now.ToUnixTimeMilliseconds());
    Ensure(MonitorSyntheticPreview.TryRead(result.Sample!.RawJson, out var parsed) &&
        parsed!.FiveHourRemaining == 18 &&
        parsed.WeeklyRemaining == 60);
    // Wrong active profile does not authorize publishing a correct-looking
    // envelope from another account.
    var gate = new ActiveMonitorPublicationGate();
    gate.Observe(Snapshot(profileId));
    Ensure(gate.Allows(result.Sample!));
    gate.SwitchRequested();
    Ensure(!gate.Allows(result.Sample!));
    return Task.CompletedTask;
});
await Test("Full Swift 6 compiled SwiftHost monitor sample passes strict Windows validator", () => {
    // Actual stdout monitorObservation from full Linux SwiftHost built
    // under Swift 6.2 strict mode, using an isolated fake Codex App Server.
    // No live account, credentials, real Codex API or Sender transport used.
    const string json = """
    {"kind":"monitorObservation","observedAtMs":1795377234788,"profileID":"2b1e8044-6868-41bf-a6b8-4131d776a6f6","raw":{"accountId":"synthetic-test-provider","rateLimitsByLimitId":{"codex":{"primary":{"windowDurationMins":300,"resetsAt":1795380834,"usedPercent":67},"planType":null,"secondary":{"windowDurationMins":10080,"resetsAt":1795463634,"usedPercent":43}}},"ordinaryUsageAllowed":null,"rateLimitResetCredits":{"availableCount":2}}}
    """;
    var inspected = SwiftHostStrictEnvelopeV1Review.Inspect(json);
    Ensure(inspected.Status == "OFFLINE_STRICT_WIRE_CANDIDATE_ONLY" &&
        inspected.Sample?.ProfileID ==
            Guid.Parse("2b1e8044-6868-41bf-a6b8-4131d776a6f6"));
    Ensure(MonitorSyntheticPreview.TryRead(inspected.Sample!.RawJson, out var quotas) &&
        quotas!.FiveHourRemaining == 33 &&
        quotas.WeeklyRemaining == 57 &&
        quotas.CreditsRemaining == 2 &&
        quotas.OrdinaryUsageAllowed is null);
    return Task.CompletedTask;
});
await Test("Scheduled 5H deadline observed but not emitted early", () => {
    var (watch,gate,account) = NewWatcher();
    var seen=windowBoundary.AddMinutes(-2);
    watch.Observe(WindowSample(seen,windowBoundary,weekBoundary),
        account,gate,seen);
    Ensure(watch.ActiveWatchCount==2 &&
        watch.TakeDue(account,gate,windowBoundary.AddSeconds(-1)).Count==0);
    var due=watch.TakeDue(account,gate,windowBoundary.AddSeconds(1));
    Ensure(due.Count==1 && due[0].Lane==LimitLane.FiveHour &&
        due[0].WindowDurationMinutes==300 &&
        due[0].PreviousResetAtUtc==windowBoundary);
    Ensure(due[0].ActiveAccountSha256==
        VerifiedExistingThreadPicker.FingerprintEmail("person@example.test"));
    return Task.CompletedTask;
});
await Test("Scheduled preview duplicated poll emits at most once", () => {
    var (watch,gate,account)=NewWatcher();
    var seen=windowBoundary.AddMinutes(-1);
    watch.Observe(WindowSample(seen,windowBoundary,weekBoundary),account,gate,seen);
    Ensure(watch.TakeDue(account,gate,windowBoundary).Count==1);
    Ensure(watch.TakeDue(account,gate,windowBoundary.AddSeconds(15)).Count==0);
    return Task.CompletedTask;
});
await Test("7D scheduled deadline eligible even without provider timestamp rollover", () => {
    var (watch,gate,account)=NewWatcher();
    var seen=weekBoundary.AddMinutes(-2);
    watch.Observe(WindowSample(seen,seen.AddHours(4),weekBoundary),
        account,gate,seen);
    var due=watch.TakeDue(account,gate,weekBoundary.AddSeconds(4));
    Ensure(due.Count==1 && due[0].Lane==LimitLane.SevenDay &&
        due[0].WindowDurationMinutes==10080);
    return Task.CompletedTask;
});
await Test("Expired observation cannot schedule any reset", () => {
    var (watch,gate,account)=NewWatcher();
    var seen=windowBoundary.AddMinutes(-7);
    watch.Observe(WindowSample(seen,windowBoundary,weekBoundary),
        account,gate,seen);
    Ensure(watch.TakeDue(account,gate,windowBoundary).Count==0);
    return Task.CompletedTask;
});
await Test("Fresh nearby observation permits due schedule", () => {
    var (watch,gate,account)=NewWatcher();
    var early=windowBoundary.AddMinutes(-7);
    var near=windowBoundary.AddMinutes(-1);
    watch.Observe(WindowSample(early,windowBoundary,weekBoundary),
        account,gate,early);
    watch.Observe(WindowSample(near,windowBoundary,weekBoundary),
        account,gate,near);
    Ensure(watch.TakeDue(account,gate,windowBoundary.AddSeconds(2)).Count==1);
    return Task.CompletedTask;
});
await Test("No confirmed active identity blocks due candidate", () => {
    var (watch,gate,account)=NewWatcher();
    var seen=windowBoundary.AddMinutes(-1);
    watch.Observe(WindowSample(seen,windowBoundary,weekBoundary),account,gate,seen);
    var unconfirmed=account with { ActiveIdentityConfirmed=false };
    Ensure(watch.TakeDue(unconfirmed,gate,windowBoundary).Count==0);
    return Task.CompletedTask;
});
await Test("Active account switch invalidates all watched deadlines", () => {
    var (watch,gate,account)=NewWatcher();
    var seen=windowBoundary.AddMinutes(-1);
    watch.Observe(WindowSample(seen,windowBoundary,weekBoundary),account,gate,seen);
    gate.SwitchRequested();
    watch.Invalidate();
    Ensure(watch.ActiveWatchCount==0 &&
        watch.TakeDue(account,gate,windowBoundary).Count==0);
    return Task.CompletedTask;
});
await Test("Untrusted provider identity is not a schedule", () => {
    var (watch,gate,account)=NewWatcher();
    var seen=windowBoundary.AddMinutes(-2);
    var bad=WindowSample(seen,windowBoundary,weekBoundary,id:otherId);
    watch.Observe(bad,account,gate,seen);
    Ensure(watch.TakeDue(account,gate,windowBoundary).Count==0);
    return Task.CompletedTask;
});
await Test("Ordinary usage forbidden invalidates planned reset", () => {
    var (watch,gate,account)=NewWatcher();
    var seen=windowBoundary.AddMinutes(-1);
    watch.Observe(WindowSample(seen,windowBoundary,weekBoundary,ordinary:false),
        account,gate,seen);
    Ensure(watch.ActiveWatchCount==0 &&
        watch.TakeDue(account,gate,windowBoundary).Count==0);
    return Task.CompletedTask;
});
await Test("Pre-boundary schedule correction revokes old reset deadline", () => {
    var (watch,gate,account)=NewWatcher();
    var old=windowBoundary.AddMinutes(-2);
    var corrected=windowBoundary.AddMinutes(-1);
    watch.Observe(WindowSample(old,windowBoundary,weekBoundary),account,gate,old);
    watch.Observe(WindowSample(corrected,windowBoundary.AddMinutes(30),weekBoundary),
        account,gate,corrected);
    Ensure(watch.TakeDue(account,gate,windowBoundary).Count==0);
    return Task.CompletedTask;
});
await Test("Plausible provider rollover retains last trusted deadline", () => {
    var (watch,gate,account)=NewWatcher();
    var seen=windowBoundary.AddMinutes(-1);
    watch.Observe(WindowSample(seen,windowBoundary,weekBoundary),account,gate,seen);
    var later=windowBoundary.AddSeconds(12);
    watch.Observe(WindowSample(later,windowBoundary.AddMinutes(300),weekBoundary),
        account,gate,later);
    var due=watch.TakeDue(account,gate,later);
    Ensure(due.Count==1 && due[0].PreviousResetAtUtc==windowBoundary);
    return Task.CompletedTask;
});
await Test("Concurrent 5H/7D schedule yields two separate lane previews", () => {
    var (watch,gate,account)=NewWatcher();
    var seen=windowBoundary.AddMinutes(-1);
    watch.Observe(WindowSample(seen,windowBoundary,windowBoundary),
        account,gate,seen);
    var due=watch.TakeDue(account,gate,windowBoundary);
    Ensure(due.Count==2 &&
        due.Any(d=>d.Lane==LimitLane.FiveHour) &&
        due.Any(d=>d.Lane==LimitLane.SevenDay));
    return Task.CompletedTask;
});
// Account-bound, never-send dispatch preflight integration checks.
var fixtureDataHome=Path.Combine(Path.GetTempPath(),
    "codex-thread-preflight-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixtureDataHome);
var fixtureStore=new DedicatedThreadBindingStore(
    Path.Combine(fixtureDataHome,"dedicated-existing-thread-bindings-v1.json"));
var fixtureFingerprint=VerifiedExistingThreadPicker.FingerprintEmail("person@example.test");
var fixtureThread="33333333-3333-4333-8333-333333333333";
var fixtureBinding=new DedicatedChatSettings(profileId,fixtureFingerprint,
    fixtureThread,ThreadStorage:"desktop-default");
var fixtureTime=windowBoundary.AddSeconds(6);
var fixtureDue=new TrustedResetDeadline(
    profileId,fixtureFingerprint,LimitLane.FiveHour,windowBoundary,
    windowBoundary.AddMinutes(-1),300,true,true,true);
var (_,fixtureGate,fixtureState)=NewWatcher();
var fixturePreflight=new DedicatedResetBindingPreflight(fixtureDataHome);
await Test("Actual due bridge refuses an unbound or master-OFF preference",async()=>{
    var current=new ActiveSwitcherIdentity(
        profileId,profileId,"person@example.test",true,false);
    var bridge=new CodexDueDispatchBridge(()=>current,
        binding=>new CodexAutoSendDraft(
            binding.ProfileId,binding.AccountSha256,binding.ExistingThreadId,
            WantFiveHour:true,WantSevenDay:false),
        ()=>true);
    Ensure(await bridge.OnTrustedDueAsync(fixtureDue,fixtureBinding,fixtureTime)
        =="HOLD_USER_SWITCH_OFF");
    Ensure(await bridge.OnTrustedDueAsync(fixtureDue,null,fixtureTime)
        =="HOLD_UNTRUSTED_DUE_OR_THREAD");
});
await Test("Master and 5H ON cannot enable a disconnected sender",async()=>{
    var calls=0;
    var current=new ActiveSwitcherIdentity(
        profileId,profileId,"person@example.test",true,false);
    var draft=new CodexAutoSendDraft(profileId,fixtureFingerprint,
        fixtureThread,true,false,WantAutomatic:true);
    var bridge=new CodexDueDispatchBridge(()=>current,_=>draft,
        ()=>false,(_,_,_)=>{
            calls++;
            return Task.FromResult("UNEXPECTED_SEND");
        });
    Ensure(await bridge.OnTrustedDueAsync(fixtureDue,fixtureBinding,fixtureTime)
        =="HOLD_SENDER_NOT_ENROLLED");
    Ensure(calls==0);
    Ensure(await bridge.OnTrustedDueAsync(
        fixtureDue,fixtureBinding with {AutomaticMessagesEnabled=true,
            StartOnFiveHour=true},fixtureTime)
        =="HOLD_SENDER_NOT_ENROLLED");
    Ensure(calls==0);
});
await Test("Due bridge rechecks identity and never retries a consumed event",async()=>{
    var calls=0;
    var current=new ActiveSwitcherIdentity(
        profileId,profileId,"person@example.test",true,false);
    var draft=new CodexAutoSendDraft(profileId,fixtureFingerprint,
        fixtureThread,true,false,WantAutomatic:true);
    var binding=fixtureBinding with {
        AutomaticMessagesEnabled=true,StartOnFiveHour=true
    };
    var bridge=new CodexDueDispatchBridge(()=>current,_=>draft,
        ()=>true,(_,_,_)=>{
            calls++;
            return Task.FromResult("AUTHORITY_RECEIPT_SYNTHETIC_ONLY");
        });
    current=current with {IsSwitching=true};
    Ensure(await bridge.OnTrustedDueAsync(fixtureDue,binding,fixtureTime)
        =="HOLD_ACCOUNT_CHANGED");
    current=current with {IsSwitching=false};
    Ensure(await bridge.OnTrustedDueAsync(fixtureDue,binding,fixtureTime)
        =="AUTHORITY_RECEIPT_SYNTHETIC_ONLY");
    Ensure(await bridge.OnTrustedDueAsync(fixtureDue,binding,fixtureTime)
        =="HOLD_ALREADY_ATTEMPTED");
    Ensure(calls==1);
    bridge.Disable();
    Ensure(await bridge.OnTrustedDueAsync(fixtureDue,binding,fixtureTime)
        =="HOLD_DISPATCH_DISABLED");
});
await Test("Due reset finds exact account-bound dedicated existing thread", () => {
    fixtureStore.Upsert(fixtureBinding);
    var decision=fixturePreflight.Evaluate(fixtureDue,fixtureState,fixtureGate,fixtureTime);
    Ensure(decision.Status=="AUTOMATIC_SENDING_OFF" &&
        decision.VerifiedBinding?.ExistingThreadId==fixtureThread &&
        decision.VerifiedBinding?.ThreadStorage=="desktop-default" &&
        !decision.WillSend);
    return Task.CompletedTask;
});
await Test("No binding on alternate active profile fails closed", () => {
    var alt=fixtureDue with {ActiveProfileId=otherId};
    var decision=fixturePreflight.Evaluate(alt,fixtureState,fixtureGate,fixtureTime);
    Ensure(decision.Status=="ACTIVE_ACCOUNT_NOT_CONFIRMED" && !decision.WillSend);
    return Task.CompletedTask;
});
await Test("Trusted event account hash must match pinned profile", () => {
    var incorrect=fixtureDue with {ActiveAccountSha256=new string('f',64)};
    var decision=fixturePreflight.Evaluate(incorrect,fixtureState,fixtureGate,fixtureTime);
    Ensure(decision.Status=="RESET_OR_ACCOUNT_PROOF_INVALID" && !decision.WillSend);
    return Task.CompletedTask;
});
await Test("Stale scheduled event cannot use a previously pinned chat", () => {
    var stale=fixtureDue with {
        LastTrustedObservationUtc=windowBoundary.AddMinutes(-12)
    };
    var decision=fixturePreflight.Evaluate(stale,fixtureState,fixtureGate,fixtureTime);
    Ensure(decision.Status=="RESET_EVENT_NOT_FRESH_OR_INVALID" &&
        !decision.WillSend);
    return Task.CompletedTask;
});
await Test("Account mutation after quota observation forbids preflight", () => {
    var changing=fixtureState with {IsMutating=true};
    var decision=fixturePreflight.Evaluate(fixtureDue,changing,fixtureGate,fixtureTime);
    Ensure(decision.Status=="ACTIVE_ACCOUNT_NOT_CONFIRMED" &&
        !decision.WillSend);
    return Task.CompletedTask;
});
await Test("Binding with forbidden message is not a valid target", () => {
    fixtureStore.Upsert(fixtureBinding with {Message="not an approved greeting"});
    var decision=fixturePreflight.Evaluate(fixtureDue,fixtureState,fixtureGate,fixtureTime);
    Ensure(decision.Status=="BINDING_ACCOUNT_THREAD_OR_TEXT_INVALID" &&
        !decision.WillSend);
    fixtureStore.Upsert(fixtureBinding);
    return Task.CompletedTask;
});
await Test("Auto flags ON without multi-device owner proof still cannot send", () => {
    var futureOn=fixtureBinding with {
        AutomaticMessagesEnabled=true,StartOnFiveHour=true,
        StartOnSevenDay=true
    };
    fixtureStore.Upsert(futureOn);
    var decision=fixturePreflight.Evaluate(fixtureDue,fixtureState,fixtureGate,fixtureTime);
    Ensure(decision.Status=="EXCLUSIVE_SENDER_AND_DISPATCH_NOT_AUTHORIZED" &&
        decision.VerifiedBinding?.ExistingThreadId==fixtureThread &&
        !decision.WillSend);
    fixtureStore.Upsert(fixtureBinding);
    return Task.CompletedTask;
});
await Test("5H/7D switches remain independently disabled", () => {
    var futureOn=fixtureBinding with {
        AutomaticMessagesEnabled=true,
        StartOnFiveHour=false,StartOnSevenDay=true
    };
    fixtureStore.Upsert(futureOn);
    var first=fixturePreflight.Evaluate(fixtureDue,fixtureState,fixtureGate,fixtureTime);
    Ensure(first.Status=="FIVE_HOUR_SENDING_OFF" && !first.WillSend);
    var second=fixturePreflight.Evaluate(
        fixtureDue with {Lane=LimitLane.SevenDay,
            WindowDurationMinutes=10080},fixtureState,fixtureGate,fixtureTime);
    Ensure(second.Status=="EXCLUSIVE_SENDER_AND_DISPATCH_NOT_AUTHORIZED" &&
        !second.WillSend);
    fixtureStore.Upsert(fixtureBinding);
    return Task.CompletedTask;
});
await Test("Malformed target UUID or storage provenance is blocked", () => {
    fixtureStore.Upsert(fixtureBinding with {ExistingThreadId="not-a-uuid"});
    var first=fixturePreflight.Evaluate(fixtureDue,fixtureState,fixtureGate,fixtureTime);
    Ensure(first.Status=="BINDING_ACCOUNT_THREAD_OR_TEXT_INVALID");
    fixtureStore.Upsert(fixtureBinding with {ThreadStorage="untrusted"});
    var second=fixturePreflight.Evaluate(fixtureDue,fixtureState,fixtureGate,fixtureTime);
    Ensure(second.Status=="BINDING_ACCOUNT_THREAD_OR_TEXT_INVALID");
    fixtureStore.Upsert(fixtureBinding);
    return Task.CompletedTask;
});
await Test("Corrupt binding file fails closed without new chat or message", () => {
    var path=Path.Combine(fixtureDataHome,"dedicated-existing-thread-bindings-v1.json");
    File.WriteAllText(path,"{not-valid-json");
    var decision=fixturePreflight.Evaluate(fixtureDue,fixtureState,fixtureGate,fixtureTime);
    Ensure(decision.Status=="BINDING_UNAVAILABLE_OR_CORRUPT" && !decision.WillSend);
    File.Delete(path); // Isolated synthetic fixture only; no real user setting.
    fixtureStore.Upsert(fixtureBinding);
    return Task.CompletedTask;
});
await Test("Legacy single-object binding reads without granting automatic send", () => {
    // Disposable local fixture has the same top-level shape as a legacy
    // V7.2 pinned thread. Never read or change the user's real binding.
    var legacyPath = Path.Combine(fixtureDataHome,
        "single-object-binding-compat-synthetic.json");
    var legacy = fixtureBinding with {
        AutomaticMessagesEnabled = true,
        StartOnFiveHour = true,
        StartOnSevenDay = true
    };
    try {
        File.WriteAllText(legacyPath, JsonSerializer.Serialize(legacy));
        var store = new DedicatedThreadBindingStore(legacyPath);
        var read = store.ReadAll().Single();
        Ensure(read.ProfileId == legacy.ProfileId &&
            read.ExistingThreadId == legacy.ExistingThreadId &&
            read.AccountSha256 == legacy.AccountSha256 &&
            read.Message == "안녕" &&
            !read.AutomaticMessagesEnabled &&
            !read.StartOnFiveHour && !read.StartOnSevenDay);
        // User-initiated Upsert is the ONLY migration; it preserves OFF.
        store.Upsert(read);
        using (var doc = JsonDocument.Parse(File.ReadAllText(legacyPath)))
            Ensure(doc.RootElement.ValueKind == JsonValueKind.Array);
        Ensure(store.ReadAll().Single() == read);
        File.WriteAllText(legacyPath,
            JsonSerializer.Serialize(new { ProfileId = legacy.ProfileId }));
        try {
            _ = store.ReadAll();
            throw new InvalidOperationException("Invalid legacy schema accepted");
        } catch (InvalidDataException) { }
    }
    finally { if (File.Exists(legacyPath)) File.Delete(legacyPath); }
    return Task.CompletedTask;
});

await Test("New-array bindings reject duplicates and unexpected fields without auto-migration", () => {
    var path = Path.Combine(fixtureDataHome, "array-binding-strict-synthetic.json");
    var store = new DedicatedThreadBindingStore(path);
    void Rejected(string json)
    {
        File.WriteAllText(path, json);
        try {
            _ = store.ReadAll();
            throw new InvalidOperationException("Invalid array accepted");
        }
        catch (InvalidDataException) { }
    }
    try {
        var valid = fixtureBinding with {
            AutomaticMessagesEnabled = true,
            StartOnFiveHour = true,
            StartOnSevenDay = false
        };
        File.WriteAllText(path, JsonSerializer.Serialize(new[] { valid }));
        var read = store.ReadAll().Single();
        Ensure(read == valid); // Current-format user flags must not be reset.
        var basic = JsonSerializer.Serialize(valid);
        Rejected("[" + basic + "," + basic + "]");
        Rejected("[" + basic + "," +
            JsonSerializer.Serialize(valid with { ProfileId = otherId }) + "]");
        Rejected("[{" + basic[1..^1] +
            ",\"ExistingThreadId\":\"00000000-0000-4000-8000-000000000000\"}]");
        Rejected("[{" + basic[1..^1] + ",\"privateKey\":\"test\"}]");
        Rejected("[null]");
        Rejected("[" + JsonSerializer.Serialize(new { valid.ProfileId }) + "]");
        Rejected(new string(' ', 65_537));
    }
    finally { if (File.Exists(path)) File.Delete(path); }
    return Task.CompletedTask;
});

await Test("P0-18 live active proof is a one-bit match and never send admission", () => {
    var binding = fixtureBinding with { ThreadStorage = "desktop-default" };
    var pass = SwitcherActiveProfileReadOnlyChannel.Evaluate(
        Snapshot(profileId), [binding], profileId);
    Ensure(pass == "PASS_ACTIVE_BINDING_MATCH_READ_ONLY");
    // The old snapshot can remain visible after SwitchRequested() has
    // invalidated the monitor gate. A stale active snapshot must HOLD.
    Ensure(SwitcherActiveProfileReadOnlyChannel.Evaluate(
        Snapshot(profileId), [binding], null).StartsWith("HOLD_"));
    Ensure(SwitcherActiveProfileReadOnlyChannel.Evaluate(
        Snapshot(profileId), [binding], otherId).StartsWith("HOLD_"));
    Ensure(SwitcherActiveProfileReadOnlyChannel.Evaluate(
        Snapshot(otherId), [binding], profileId).StartsWith("HOLD_"));
    Ensure(SwitcherActiveProfileReadOnlyChannel.Evaluate(
        Snapshot(profileId, confirmed: false), [binding], profileId).StartsWith("HOLD_"));
    Ensure(SwitcherActiveProfileReadOnlyChannel.Evaluate(
        Snapshot(profileId, mutating: true), [binding], profileId).StartsWith("HOLD_"));
    Ensure(SwitcherActiveProfileReadOnlyChannel.Evaluate(
        Snapshot(profileId, adding: true), [binding], profileId).StartsWith("HOLD_"));
    Ensure(SwitcherActiveProfileReadOnlyChannel.Evaluate(
        Snapshot(profileId), [binding with { AccountSha256 = new string('0', 64) }], profileId)
        .StartsWith("HOLD_"));
    // Identity matches must be observable even after the user enables
    // 5H/7D consent. This read-only result is NOT a sender authorization.
    Ensure(SwitcherActiveProfileReadOnlyChannel.Evaluate(
        Snapshot(profileId), [binding with {
            AutomaticMessagesEnabled = true,
            StartOnFiveHour = true,
            StartOnSevenDay = true
        }], profileId) == "PASS_ACTIVE_BINDING_MATCH_READ_ONLY");
    Ensure(SwitcherActiveProfileReadOnlyChannel.Evaluate(
        Snapshot(profileId), [binding with {
            AutomaticMessagesEnabled = false,
            StartOnFiveHour = false,
            StartOnSevenDay = false
        }], profileId) == "PASS_ACTIVE_BINDING_MATCH_READ_ONLY");
    Ensure(SwitcherActiveProfileReadOnlyChannel.Evaluate(
        Snapshot(profileId, duplicated: true), [binding], profileId).StartsWith("HOLD_"));
    Ensure(SwitcherActiveProfileReadOnlyChannel.Evaluate(
        Snapshot(profileId), [binding, binding], profileId).StartsWith("HOLD_"));
    return Task.CompletedTask;
});
await Test("P0-18 current-user named pipe answers only a bounded one-bit proof", async () => {
    var started = new TaskCompletionSource<Dispatcher>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var dedicated = new Thread(() => {
        var context = Dispatcher.CurrentDispatcher;
        started.SetResult(context);
        Dispatcher.Run();
    });
    dedicated.SetApartmentState(ApartmentState.STA);
    dedicated.Start();
    var dispatcher = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var value = Snapshot(profileId);
    var gate = new ActiveMonitorPublicationGate();
    Ensure(gate.Observe(value) && gate.ActiveProfile == profileId);
    var binding = fixtureBinding with { ThreadStorage = "desktop-default" };
    try
    {
        using var server = new SwitcherActiveProfileReadOnlyChannel(
            fixtureDataHome, dispatcher, () => value, () => gate.ActiveProfile,
            () => [binding]);
        server.Start();
        async Task<string?> Send(string line)
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var pipe = new NamedPipeClientStream(
                ".", SwitcherActiveProfileReadOnlyChannel.PipeName(fixtureDataHome),
                PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(2000, cancel.Token);
            using var reader = new StreamReader(pipe, Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            await pipe.WriteAsync(Encoding.ASCII.GetBytes(line + "\n"),
                cancel.Token);
            var response = await reader.ReadLineAsync(cancel.Token);
            return response;
        }
        var nonce = new string('a', 32);
        var request = SwitcherActiveProfileReadOnlyChannel.RequestPrefix + "|" + nonce;
        Ensure(await Send(request) ==
            SwitcherActiveProfileReadOnlyChannel.ResponsePrefix + "|" +
            nonce + "|PASS_ACTIVE_BINDING_MATCH_READ_ONLY");
        // Simulate real SwitchRequested BEFORE the Swift state snapshot
        // changes, then wait for a mutating -> stable host sequence.
        gate.SwitchRequested();
        Ensure(gate.ActiveProfile is null);
        Ensure(await Send(request) ==
            SwitcherActiveProfileReadOnlyChannel.ResponsePrefix + "|" +
            nonce + "|HOLD_ACTIVE_BINDING_UNCONFIRMED");
        value = Snapshot(profileId, mutating: true);
        gate.Observe(value);
        Ensure(await Send(request) ==
            SwitcherActiveProfileReadOnlyChannel.ResponsePrefix + "|" +
            nonce + "|HOLD_ACTIVE_BINDING_UNCONFIRMED");
        value = Snapshot(profileId);
        gate.Observe(value);
        Ensure(gate.ActiveProfile == profileId);
        // A user preference change does not invalidate the independent
        // read-only identity proof, and this channel never starts a send.
        binding = binding with {
            AutomaticMessagesEnabled = true,
            StartOnFiveHour = true,
            StartOnSevenDay = true
        };
        Ensure(await Send(request) ==
            SwitcherActiveProfileReadOnlyChannel.ResponsePrefix + "|" +
            nonce + "|PASS_ACTIVE_BINDING_MATCH_READ_ONLY");
        value = Snapshot(otherId);
        Ensure(await Send(request) ==
            SwitcherActiveProfileReadOnlyChannel.ResponsePrefix + "|" +
            nonce + "|HOLD_ACTIVE_BINDING_UNCONFIRMED");
        Ensure(await Send("turn/start|" + nonce) == "HOLD_PROTOCOL");
        // An overlong line may be rejected with HOLD_PROTOCOL or by
        // immediate pipe close before the client finishes writing.
        bool overlongRejected = false;
        try { overlongRejected = await Send(new string('x', 140)) == "HOLD_PROTOCOL"; }
        catch (IOException) { overlongRejected = true; }
        Ensure(overlongRejected);
    }
    finally
    {
        dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
        if (!dedicated.Join(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("Synthetic dispatcher did not stop");
    }
});

await Test("WPF Settings displays actual bound Desktop chat while auto sending OFF", () => {
    Exception? failure = null;
    var verified = false;
    var thread = new Thread(() => {
        try {
            fake.State = Snapshot(profileId);
            var panel = new DedicatedThreadPickerPanel(context.CurrentIdentity,
                (_, _) => throw new InvalidOperationException("No Codex RPC on render"),
                fixtureDataHome,
                (_, _) => throw new InvalidOperationException("No Codex RPC on render"));
            panel.RefreshBoundStatus();
            var labels = ((StackPanel)panel.Content).Children
                .OfType<TextBlock>().Select(t => t.Text ?? "").ToArray();
            Ensure(labels.Any(t => t.Contains("기존 채팅방 연결됨") &&
                t.Contains("Codex Desktop") &&
                t.Contains(fixtureThread[^8..]) &&
                t.Contains("자동 전송 마스터 OFF")));
            verified = true;
        }
        catch(Exception ex) { failure=ex; }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    if(!thread.Join(TimeSpan.FromSeconds(7))) throw new TimeoutException("WPF binding status");
    if(failure is not null)throw failure;
    Ensure(verified);
    return Task.CompletedTask;
});
await Test("WPF Settings invalidates old binding status after active account change", () => {
    Exception? failure = null;
    var verified = false;
    var thread = new Thread(() => {
        try {
            fake.State = Snapshot(profileId);
            var panel = new DedicatedThreadPickerPanel(context.CurrentIdentity,
                (_, _) => throw new InvalidOperationException("No Codex RPC on render"),
                fixtureDataHome);
            fake.State = Snapshot(null);
            panel.RefreshBoundStatus();
            var labels = ((StackPanel)panel.Content).Children
                .OfType<TextBlock>().Select(t => t.Text ?? "").ToArray();
            Ensure(labels.Any(t => t.Contains("계정 확인 대기") &&
                t.Contains("자동 전송 OFF")));
            verified = true;
        } catch(Exception ex) { failure=ex; }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    if(!thread.Join(TimeSpan.FromSeconds(7)))throw new TimeoutException("WPF account change status");
    if(failure is not null)throw failure;
    Ensure(verified);
    return Task.CompletedTask;
});
await Test("Draft 5H and 7D options persist independently but cannot enable sending",()=>{
    var draft = new CodexAutoSendDraftStore(fixtureDataHome);
    var selected=draft.SaveDraft(fixtureBinding,true,false);
    var again=draft.ReadFor(fixtureBinding);
    Ensure(selected == again &&
        again.WantFiveHour && !again.WantSevenDay &&
        !again.WantAutomatic && !again.ActualSendEnabled);
    var real=fixtureStore.ReadAll().Single(x=>x.ProfileId==profileId);
    Ensure(!real.AutomaticMessagesEnabled &&
        !real.StartOnFiveHour && !real.StartOnSevenDay);
    return Task.CompletedTask;
});
await Test("Master and 5H/7D preferences are independent; old draft defaults OFF",()=>{
    var store=new CodexAutoSendDraftStore(fixtureDataHome);
    var active=store.SaveDraft(fixtureBinding,true,false,wantAutomatic:true);
    Ensure(active.WantAutomatic && active.WantsLane(LimitLane.FiveHour) &&
        !active.WantsLane(LimitLane.SevenDay) && !active.ActualSendEnabled);
    var disabled=store.SaveDraft(fixtureBinding,true,true,wantAutomatic:false);
    Ensure(!disabled.WantsLane(LimitLane.FiveHour) &&
        !disabled.WantsLane(LimitLane.SevenDay) &&
        !disabled.ActualSendEnabled);
    var oldJson=JsonSerializer.Serialize(new {
        fixtureBinding.ProfileId,fixtureBinding.AccountSha256,
        fixtureBinding.ExistingThreadId,
        WantFiveHour=true,WantSevenDay=true,SchemaVersion=1
    });
    var legacy=JsonSerializer.Deserialize<CodexAutoSendDraft>(oldJson);
    Ensure(legacy is not null && !legacy.WantAutomatic &&
        !legacy.WantsLane(LimitLane.FiveHour));
    store.SaveDraft(fixtureBinding,true,false);
    return Task.CompletedTask;
});
await Test("Draft does not follow a different pinned thread UUID",()=>{
    var draft=new CodexAutoSendDraftStore(fixtureDataHome);
    try{
        draft.ReadFor(fixtureBinding with {
            ExistingThreadId="99999999-9999-4999-8999-999999999999"
        });
        throw new InvalidOperationException("Expected mismatch blocked");
    }catch(InvalidDataException){}
    return Task.CompletedTask;
});
await Test("Draft display can load only for matching active profile",()=>{
    Exception? failure = null;
    var verified=false;
    var thread = new Thread(()=>{
        try {
            fake.State=Snapshot(profileId);
            var panel=new DedicatedThreadPickerPanel(context.CurrentIdentity,
                (_,_)=>throw new InvalidOperationException("No Codex RPC allowed"),
                fixtureDataHome);
            panel.RefreshBoundStatus();
            var checks=((StackPanel)panel.Content).Children.OfType<CheckBox>()
                .ToArray();
            Ensure(checks.Length==3);
            Ensure(checks.All(c=>c.IsEnabled));
            Ensure(checks[0].IsChecked==false &&
                checks[1].IsChecked==true && checks[2].IsChecked==false);
            verified=true;
        }catch(Exception ex){ failure=ex; }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    if(!thread.Join(TimeSpan.FromSeconds(7)))throw new TimeoutException();
    if(failure is not null)throw failure;
    Ensure(verified);
    return Task.CompletedTask;
});
await Test("Draft save never changes actual sent-message authorization",()=>{
    var draft=new CodexAutoSendDraftStore(fixtureDataHome);
    draft.SaveDraft(fixtureBinding,true,true,wantAutomatic:true);
    var live=fixturePreflight.Evaluate(fixtureDue,fixtureState,
        fixtureGate,fixtureTime);
    Ensure(live.Status=="AUTOMATIC_SENDING_OFF" &&
        !live.WillSend);
    var loaded=draft.ReadFor(fixtureBinding);
    Ensure(loaded is not null && loaded.WantAutomatic &&
        loaded.WantFiveHour && loaded.WantSevenDay &&
        !loaded.ActualSendEnabled);
    return Task.CompletedTask;
});
await Test("WPF user ON/OFF persists exact binding flags but independent owner stays OFF",()=>{
    Exception? failure=null;
    var verified=false;
    var thread=new Thread(()=>{
        try{
            fake.State=Snapshot(profileId);
            var panel=new DedicatedThreadPickerPanel(context.CurrentIdentity,
                (_,_)=>throw new InvalidOperationException("No Codex RPC"),
                fixtureDataHome);
            panel.RefreshBoundStatus();
            var body=(StackPanel)panel.Content;
            var checks=body.Children.OfType<CheckBox>().ToArray();
            var button=body.Children.OfType<Button>().Single(b=>
                b.Content?.ToString()=="자동 인사 ON/OFF 저장");
            Ensure(button.IsEnabled);
            checks[0].IsChecked=true;
            checks[1].IsChecked=false;
            checks[2].IsChecked=true;
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent,button));
            var saved=new CodexAutoSendDraftStore(fixtureDataHome)
                .ReadFor(fixtureBinding);
            Ensure(saved is not null && saved.WantAutomatic &&
                !saved.WantFiveHour && saved.WantSevenDay &&
                !saved.ActualSendEnabled);
            var bound=fixtureStore.ReadAll().Single(x=>x.ProfileId==profileId);
            Ensure(bound.AutomaticMessagesEnabled &&
                !bound.StartOnFiveHour && bound.StartOnSevenDay &&
                bound.ExistingThreadId==fixtureBinding.ExistingThreadId);
            Ensure(!File.Exists(Path.Combine(fixtureDataHome,
                "codex-chat-production-v1.json")));
            panel.RefreshBoundStatus();
            Ensure(checks[0].IsChecked==true &&
                checks[1].IsChecked==false && checks[2].IsChecked==true);
            var viewStatus=body.Children.OfType<TextBlock>()
                .Select(x=>x.Text).FirstOrDefault(x=>
                    x?.Contains("Owner 미등록")==true);
            Ensure(viewStatus is not null &&
                viewStatus.Contains("실제 전송 OFF"));
            verified=true;
        }catch(Exception ex){failure=ex;}
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    if(!thread.Join(TimeSpan.FromSeconds(7)))throw new TimeoutException();
    fixtureStore.Upsert(fixtureBinding);
    if(failure is not null)throw failure;
    Ensure(verified);
    return Task.CompletedTask;
});
await Test("WPF draft save rejects account change, preserves prior draft",()=>{
    Exception? failure=null;
    var verified=false;
    var prior=new CodexAutoSendDraftStore(fixtureDataHome)
        .ReadFor(fixtureBinding);
    var thread=new Thread(()=>{
        try{
            fake.State=Snapshot(profileId);
            var panel=new DedicatedThreadPickerPanel(context.CurrentIdentity,
                (_,_)=>throw new InvalidOperationException("No Codex RPC"),
                fixtureDataHome);
            var body=(StackPanel)panel.Content;
            var checks=body.Children.OfType<CheckBox>().ToArray();
            var button=body.Children.OfType<Button>().Single(b=>
                b.Content?.ToString()=="자동 인사 ON/OFF 저장");
            Ensure(button.IsEnabled && checks.Length==3);
            checks[0].IsChecked=false;
            checks[1].IsChecked=true;
            checks[2].IsChecked=false;
            fake.State=Snapshot(null);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent,button));
            Ensure(!button.IsEnabled);
            var after=new CodexAutoSendDraftStore(fixtureDataHome)
                .ReadFor(fixtureBinding);
            Ensure(prior==after);
            verified=true;
        }catch(Exception ex){failure=ex;}
        finally{fake.State=Snapshot(profileId);}
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    if(!thread.Join(TimeSpan.FromSeconds(7)))throw new TimeoutException();
    if(failure is not null)throw failure;
    Ensure(verified);
    return Task.CompletedTask;
});
await Test("Operational ON binding keeps user master/5H/7D controls editable and OFF stops owner",()=>{
    Exception? failure=null;
    var verified=false;
    var operative=fixtureBinding with {
        AutomaticMessagesEnabled=true,StartOnFiveHour=true,
        StartOnSevenDay=true
    };
    fixtureStore.Upsert(operative);
    var thread=new Thread(()=>{
        try {
            fake.State=Snapshot(profileId);
            var panel=new DedicatedThreadPickerPanel(
                context.CurrentIdentity,
                (_,_)=>throw new InvalidOperationException("No Codex RPC"),
                fixtureDataHome);
            panel.RefreshBoundStatus();
            var body=(StackPanel)panel.Content;
            var checks=body.Children.OfType<CheckBox>().ToArray();
            var button=body.Children.OfType<Button>().Single(b=>
                b.Content?.ToString()=="자동 인사 ON/OFF 저장");
            Ensure(checks.Length==3 && checks.All(x=>x.IsEnabled) &&
                button.IsEnabled);
            // Even with an approved operational binding, OFF must be
            // writable without revoking the permanently pinned thread.
            checks[0].IsChecked=false;
            checks[1].IsChecked=false;
            checks[2].IsChecked=true;
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent,button));
            var disabled=new CodexAutoSendDraftStore(fixtureDataHome)
                .ReadFor(operative);
            Ensure(disabled is not null && !disabled.WantAutomatic &&
                !disabled.WantFiveHour && disabled.WantSevenDay);
            var offBinding=fixtureStore.ReadAll().Single(x=>x.ProfileId==profileId);
            Ensure(!offBinding.AutomaticMessagesEnabled &&
                !offBinding.StartOnFiveHour && !offBinding.StartOnSevenDay &&
                offBinding.ExistingThreadId==operative.ExistingThreadId);
            // User re-enables the desired lane, but separate operator
            // provisioning is never created or modified by this button.
            checks[0].IsChecked=true;
            checks[1].IsChecked=true;
            checks[2].IsChecked=false;
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent,button));
            var enabled=new CodexAutoSendDraftStore(fixtureDataHome)
                .ReadFor(operative);
            Ensure(enabled is not null && enabled.WantAutomatic &&
                enabled.WantFiveHour && !enabled.WantSevenDay);
            var finalBinding=fixtureStore.ReadAll().Single(x=>x.ProfileId==profileId);
            Ensure(finalBinding.AutomaticMessagesEnabled &&
                finalBinding.StartOnFiveHour && !finalBinding.StartOnSevenDay &&
                finalBinding.ExistingThreadId==operative.ExistingThreadId);
            Ensure(!File.Exists(Path.Combine(fixtureDataHome,
                "codex-chat-production-v1.json")));
            verified=true;
        }catch(Exception ex){failure=ex;}
    });
    thread.SetApartmentState(ApartmentState.STA);
    try{
        thread.Start();
        if(!thread.Join(TimeSpan.FromSeconds(7)))
            throw new TimeoutException("Operational user OFF test");
        if(failure is not null)throw failure;
        Ensure(verified);
    }finally{fixtureStore.Upsert(fixtureBinding);}
    return Task.CompletedTask;
});
await Test("Local due IPC defaults OFF when no explicit enrollment exists",()=>{
    Ensure(!CodexDueIpcEnrollment.IsEnrolled(
        fixtureDataHome,fixtureBinding));
    return Task.CompletedTask;
});
await Test("Central Owner admission holds before reading provisioning",()=>{
    using var result=CodexWindowsOwnerAutoStart.TryStart(fixtureDataHome);
    Ensure(result.State=="HOLD_OWNER_ADMISSION_TRUST_UNAVAILABLE");
    return Task.CompletedTask;
});
await Test("Owner admission denies legacy disabled and enabled bootstrap files",()=>{
    var path=Path.Combine(fixtureDataHome,"codex-owner-startup-v1.json");
    try {
        File.WriteAllText(path,JsonSerializer.Serialize(new {
            Version=1,Enabled=false
        }));
        using(var disabled=CodexWindowsOwnerAutoStart.TryStart(fixtureDataHome))
            Ensure(disabled.State=="HOLD_OWNER_ADMISSION_TRUST_UNAVAILABLE");
        File.WriteAllText(path,JsonSerializer.Serialize(new {
            Version=1,Enabled=true,NodeExePath="unreviewed",
            NodeExeSha256="not-pinned",OwnerMainPath="not-a-reviewed-entry",
            OwnerMainSha256="not-pinned",PrivateConfigDpapiBase64=""
        }));
        using(var rejected=CodexWindowsOwnerAutoStart.TryStart(fixtureDataHome))
            Ensure(rejected.State=="HOLD_OWNER_ADMISSION_TRUST_UNAVAILABLE");
    } finally {if(File.Exists(path))File.Delete(path);}
    return Task.CompletedTask;
});
await Test("Read-only admission captures same-handle Windows file identity and bytes",()=>{
    var path=Path.Combine(fixtureDataHome,"p013-admission-file-fixture.json");
    try
    {
        File.WriteAllText(path,"{\"synthetic\":true}");
        using var read=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        var adapter=typeof(CodexWindowsOwnerAutoStart).Assembly.GetType(
            "CodexAccountSwitcher.WindowsAdmissionFileEvidence",throwOnError:true)!;
        var capture=adapter.GetMethod("Read",BindingFlags.NonPublic|BindingFlags.Static)!;
        var snapshot=capture.Invoke(null,new object[]{read.SafeFileHandle})!;
        var expected=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("{\"synthetic\":true}")));
        Ensure((string)snapshot.GetType().GetProperty("Sha256")!.GetValue(snapshot)! ==expected);
        var identity=snapshot.GetType().GetProperty("Identity")!.GetValue(snapshot)!;
        Ensure((uint)identity.GetType().GetProperty("LinkCount")!.GetValue(identity)! ==1u);
        Ensure(((string)identity.GetType().GetProperty("OwnerSid")!.GetValue(identity)!).StartsWith("S-1-"));
        Ensure(((string)identity.GetType().GetProperty("DaclSha256")!.GetValue(identity)!).Length==64);
        Ensure(((string)identity.GetType().GetProperty("FileId")!.GetValue(identity)!).Length==32);
        var repeated=capture.Invoke(null,new object[]{read.SafeFileHandle})!;
        Ensure(Equals(identity,repeated.GetType().GetProperty("Identity")!.GetValue(repeated)));
        var denied=false;
        try { using var writer=File.Open(path,FileMode.Open,FileAccess.Write,FileShare.Read); }
        catch(IOException) { denied=true; }
        Ensure(denied);
    }
    finally {File.Delete(path);}
    return Task.CompletedTask;
});
foreach (var scenario in P014AdmissionChecks.Cases)
    await Test("P0-14 " + scenario, () => {
        P014AdmissionChecks.Run(scenario);
        return Task.CompletedTask;
    });
await Test("Owner recovery requires registration/current account and is capped 3 per hour",()=>{
    var gate=new CodexWindowsOwnerRecoveryGate();
    var now=new DateTimeOffset(2026,11,21,0,0,0,TimeSpan.Zero);
    Ensure(!CodexWindowsOwnerAutoStart.HasEnabledConfiguration(fixtureDataHome));
    Ensure(!gate.CanAttempt(now,configured:false,ownerRunning:false,
        exiting:false,accountConfirmed:true));
    Ensure(!gate.CanAttempt(now,configured:true,ownerRunning:true,
        exiting:false,accountConfirmed:true));
    Ensure(!gate.CanAttempt(now,configured:true,ownerRunning:false,
        exiting:true,accountConfirmed:true));
    Ensure(!gate.CanAttempt(now,configured:true,ownerRunning:false,
        exiting:false,accountConfirmed:false));
    Ensure(CodexWindowsOwnerRecoveryGate.ShouldDeferDue(
        ownerConfigured:true,ownerRunning:false));
    Ensure(!CodexWindowsOwnerRecoveryGate.ShouldDeferDue(
        ownerConfigured:true,ownerRunning:true));
    Ensure(!CodexWindowsOwnerRecoveryGate.ShouldDeferDue(
        ownerConfigured:false,ownerRunning:false));
    Ensure(gate.CanAttempt(now,true,false,false,true));
    gate.RecordAttempt(now);
    Ensure(!gate.CanAttempt(now.AddSeconds(29),true,false,false,true));
    Ensure(gate.CanAttempt(now.AddSeconds(30),true,false,false,true));
    gate.RecordAttempt(now.AddSeconds(30));
    Ensure(gate.CanAttempt(now.AddMinutes(1),true,false,false,true));
    gate.RecordAttempt(now.AddMinutes(1));
    Ensure(gate.Attempts==3);
    Ensure(!gate.CanAttempt(now.AddMinutes(5),true,false,false,true));
    Ensure(gate.CanAttempt(now.AddHours(1),true,false,false,true));
    Ensure(gate.Attempts==0);
    return Task.CompletedTask;
});
await Test("Owner startup and due deferral obey persisted master/5H/7D OFF",()=>{
    var store=new CodexAutoSendDraftStore(fixtureDataHome);
    var on=fixtureBinding with {
        AutomaticMessagesEnabled=true,
        StartOnFiveHour=true,StartOnSevenDay=true
    };
    try
    {
        fixtureStore.Upsert(on);
        store.SaveDraft(on,wantFiveHour:true,
            wantSevenDay:true,wantAutomatic:true);
        Ensure(CodexWindowsOwnerRecoveryGate.HasActiveUserLane(
            fixtureDataHome,profileId));
        store.SaveDraft(on,wantFiveHour:true,
            wantSevenDay:true,wantAutomatic:false);
        Ensure(!CodexWindowsOwnerRecoveryGate.HasActiveUserLane(
            fixtureDataHome,profileId));
        store.SaveDraft(on,wantFiveHour:false,
            wantSevenDay:false,wantAutomatic:true);
        Ensure(!CodexWindowsOwnerRecoveryGate.HasActiveUserLane(
            fixtureDataHome,profileId));
        store.SaveDraft(on,wantFiveHour:true,
            wantSevenDay:false,wantAutomatic:true);
        Ensure(CodexWindowsOwnerRecoveryGate.HasActiveUserLane(
            fixtureDataHome,profileId));
        fixtureStore.Upsert(fixtureBinding);
        Ensure(!CodexWindowsOwnerRecoveryGate.HasActiveUserLane(
            fixtureDataHome,profileId));
        Ensure(!CodexWindowsOwnerRecoveryGate.HasActiveUserLane(
            fixtureDataHome,Guid.Empty));
        Ensure(!CodexWindowsOwnerRecoveryGate.HasActiveUserLane(
            fixtureDataHome,Guid.NewGuid()));
    }
    finally
    {
        fixtureStore.Upsert(fixtureBinding);
        store.SaveDraft(fixtureBinding,wantFiveHour:false,
            wantSevenDay:false,wantAutomatic:false);
    }
    return Task.CompletedTask;
});
await Test("Owner JS authority and offline imports are locked and hash-pinned across startup",()=>{
    var stage=Path.GetFullPath(Path.Combine(
        Directory.GetCurrentDirectory(),"..","codex-auto-greeting-stage-20261120"));
    var expected="42f393deedf879e906dbc204b52ac6393c630179435ae62a839cf1a09051b567";
    var root=Path.Combine(Path.GetTempPath(),
        "codex-owner-js-isolated-"+Guid.NewGuid().ToString("N"));
    try{
        foreach(var area in new[]{"authority","offline"}){
            Directory.CreateDirectory(Path.Combine(root,area));
            foreach(var source in Directory.GetFiles(Path.Combine(stage,area),
                "*.mjs")){
                File.Copy(source,Path.Combine(root,area,Path.GetFileName(source)));
            }
        }
        var entry=Path.Combine(root,"authority",
            "codex-windows-owner-main.mjs");
        var dependent=Path.Combine(root,"authority",
            "codex-chat-windows-local-runtime.mjs");
        var denied=false;
        using(var locked=WindowsReviewedOwnerJsBundle.Acquire(entry,expected)){
            try{
                using var illegal=new FileStream(dependent,FileMode.Open,
                    FileAccess.Write,FileShare.Read);
            }catch(IOException){denied=true;}
            catch(UnauthorizedAccessException){denied=true;}
            Ensure(denied);
        }
        // The same unchanged entrypoint MUST NOT approve altered imports.
        File.AppendAllText(dependent,"\n// SYNTHETIC_MUTATION");
        var rejected=false;
        try{using var bad=WindowsReviewedOwnerJsBundle.Acquire(entry,expected);}
        catch(InvalidOperationException){rejected=true;}
        Ensure(rejected);
    }finally{if(Directory.Exists(root))Directory.Delete(root,recursive:true);}
    return Task.CompletedTask;
});
try { Directory.Delete(fixtureDataHome, recursive:true); }
catch(IOException) { /* Isolated fixture only. */ }
// Cross-language request identity only: neither Windows nor Mac sends a message.
var canonicalAccount=new string('a',64);
var canonicalThread="33333333-3333-4333-8333-333333333333";
var canonicalBinding=new DedicatedChatSettings(profileId,canonicalAccount,
    canonicalThread,ThreadStorage:"desktop-default");
var canonicalDue=new TrustedResetDeadline(
    profileId,canonicalAccount,LimitLane.FiveHour,windowBoundary,
    windowBoundary.AddMinutes(-1),300,true,true,true);
var canonicalPreflight=new DedicatedResetPreflightVerdict(
    "AUTOMATIC_SENDING_OFF",canonicalBinding);
await Test("Windows canonical Codex event hash equals Node authority fixture", () => {
    var preview=CodexChatReservationPlanner.TryCreate(
        canonicalDue,canonicalPreflight,windowBoundary.AddSeconds(1));
    Ensure(preview is not null &&
        preview.EventKey=="7cee3620193f86fc205d4ff967f46456f202be6c30d4303246b122b3e1dd7142" &&
        preview.Lane=="CODEX_FIVE_HOUR" &&
        preview.ResetAtUnix==1795150800 &&
        preview.Endpoint=="/v1/codex-chat/events/reserve" &&
        !preview.RealMessageSendAuthorized);
    return Task.CompletedTask;
});
await Test("Windows/Mac identical event inputs generate identical reserved event key", () => {
    var windows=CodexChatReservationPlanner.TryCreate(canonicalDue,canonicalPreflight,
        windowBoundary.AddSeconds(1));
    var mac=CodexChatReservationPlanner.TryCreate(canonicalDue,canonicalPreflight,
        windowBoundary.AddSeconds(5));
    Ensure(windows is not null && mac is not null &&
        windows.EventKey==mac.EventKey);
    return Task.CompletedTask;
});
await Test("5H and 7D same timestamp have distinct hashes for central coalescing", () => {
    var five=CodexChatReservationPlanner.TryCreate(canonicalDue,canonicalPreflight,
        windowBoundary.AddSeconds(5));
    var seven=CodexChatReservationPlanner.TryCreate(
        canonicalDue with {Lane=LimitLane.SevenDay,WindowDurationMinutes=10080},
        canonicalPreflight,windowBoundary.AddSeconds(5));
    Ensure(five is not null && seven is not null &&
        five.EventKey!=seven.EventKey &&
        seven.Lane=="CODEX_SEVEN_DAY" &&
        !seven.RealMessageSendAuthorized);
    return Task.CompletedTask;
});
await Test("Wrong binding owner account cannot compute a reservation request", () => {
    var mismatch=CodexChatReservationPlanner.TryCreate(canonicalDue,
        canonicalPreflight with {
            VerifiedBinding=canonicalBinding with {
                AccountSha256=new string('f',64)
            }
        },windowBoundary.AddSeconds(5));
    Ensure(mismatch is null);
    return Task.CompletedTask;
});
await Test("Stale reset cannot calculate central reservation after boundary", () => {
    var preview=CodexChatReservationPlanner.TryCreate(canonicalDue,canonicalPreflight,
        windowBoundary.AddMinutes(11));
    Ensure(preview is null);
    return Task.CompletedTask;
});
await Test("Bogus preflight or altered message cannot calculate central request", () => {
    var invalid=CodexChatReservationPlanner.TryCreate(canonicalDue,
        new DedicatedResetPreflightVerdict("BINDING_UNAVAILABLE_OR_CORRUPT",
            canonicalBinding),windowBoundary);
    var altered=CodexChatReservationPlanner.TryCreate(canonicalDue,
        canonicalPreflight with {VerifiedBinding=canonicalBinding with {
            Message="Different greeting"
        }},windowBoundary);
    Ensure(invalid is null && altered is null);
    return Task.CompletedTask;
});
await Test("Central reservation is never a turn/start authorization", () => {
    var conditional=new DedicatedResetPreflightVerdict(
        "EXCLUSIVE_SENDER_AND_DISPATCH_NOT_AUTHORIZED",
        canonicalBinding with {
            AutomaticMessagesEnabled=true,StartOnFiveHour=true,
            StartOnSevenDay=true
        });
    var plan=CodexChatReservationPlanner.TryCreate(
        canonicalDue,conditional,windowBoundary.AddSeconds(1));
    Ensure(plan is not null && !plan.RealMessageSendAuthorized);
    return Task.CompletedTask;
});
Console.WriteLine($"RESULT PASS={passed} FAIL={failed}");
Environment.ExitCode = failed == 0 ? 0 : 1;
return Environment.ExitCode;

sealed class FakeAccountClient : IAccountClient
{
    public AccountSnapshot State { get; set; } = AccountSnapshot.Empty;
    public event Action? Changed { add { } remove { } }
    public Task CommandAsync(string command, Guid? accountID = null,
        bool? value = null, string? language = null) => Task.CompletedTask;
}

sealed class LocalJsonRpc(Func<string,object?,JsonElement> call) : ICodexJsonRpcSession
{
    public Task<JsonElement> CallAsync(string method, object? parameters,
        CancellationToken cancellationToken) =>
        Task.FromResult(call(method,parameters));
}
