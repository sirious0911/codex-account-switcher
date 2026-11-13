using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodexAccountSwitcher.Core;

internal static class WindowsTelegramAutoRecoveryDispatcherTest
{
    internal static void Run(string output)
    {
        var root = Path.Combine(output, "telegram-auto-dispatcher-synthetic");
        if (Directory.Exists(root))
            throw new InvalidOperationException("Auto dispatcher test must start absent.");
        var scripts = Path.Combine(root, "reviewed-scripts");
        var bots = Path.Combine(root, "existing-bot-fixture");
        var data = Path.Combine(root, "user-state");
        Directory.CreateDirectory(scripts);
        Directory.CreateDirectory(bots);
        foreach (var name in new[]
        {
            WindowsPackagedTelegramScripts.MonitorScriptName,
            WindowsPackagedTelegramScripts.AlertScriptName
        })
            File.Copy(Path.Combine("windows", "scripts", name),
                Path.Combine(scripts, name));
        foreach (var name in new[]
        {
            "send-telegram-health.ps1", "send-telegram-alert.ps1",
            "telegram-health-token.dpapi", "telegram-token.dpapi",
            "telegram-chat-id.txt"
        })
            File.WriteAllText(Path.Combine(bots, name), "synthetic-not-real-credentials");

        var host = Path.Combine(scripts, "SwitcherHost.exe");
        var when = new DateTimeOffset(2026, 11, 13, 11, 0, 0, TimeSpan.Zero);
        var prefs = new TelegramNotificationPreferences(
            TelegramNotificationDeviceRole.Sender, true, true, true);
        var inbox = new WindowsAmanoProductionOutbox(data);
        var key5 = new string('a', 64);
        var key7 = new string('b', 64);
        var keyStopped = new string('c', 64);
        var test = inbox.StageConnectivityTest(TelegramNotificationLane.FiveHourRecovery,
            prefs, when, reviewedBotRouteAvailable: true);
        Assert(test is { IsTest: true }, "Fixture must hold a distinct test event.");
        Assert(inbox.Stage(TelegramNotificationLane.FiveHourRecovery,
                key5, when, true, prefs),
            "New confirmed 5H reset must enter durable production queue.");
        var session = new WindowsTelegramAutoSendSession();
        using var lease = new WindowsTelegramLocalSenderLease(data);
        Assert(lease.Update(prefs) == WindowsLocalSenderLeaseState.LocalClaimHeld,
            "The local Windows Sender test lease must be explicitly held.");

        var fake = new FakeRunner(inbox);
        var dispatcher = new WindowsTelegramAutoRecoveryDispatcher(session,
            inbox, new WindowsExistingAmanoBotProcessAdapter(fake));

        var beforeConsent = Run(dispatcher, TelegramNotificationLane.FiveHourRecovery,
            key5, when, true, prefs, lease, host, bots);
        Assert(beforeConsent.Status == WindowsAmanoApprovalStatus.Blocked &&
            fake.Calls == 0 &&
            inbox.Read().Single(x => x.EventKey == key5).Status ==
                WindowsAmanoOutboxStatus.AwaitingApproval,
            "Unarmed automatic recovery must not launch a sender process.");

        Assert(!session.Arm(false, true, prefs, lease, true) &&
            session.Arm(true, true, prefs, lease, true),
            "Only explicit session approval and other-device confirmation may arm.");
        var oldPending = Run(dispatcher, TelegramNotificationLane.FiveHourRecovery,
            key5, when, false, prefs, lease, host, bots);
        Assert(oldPending.Status == WindowsAmanoApprovalStatus.Blocked &&
            fake.Calls == 0, "Pre-existing queued alerts cannot auto-replay on arming.");

        var testAuto = Run(dispatcher, TelegramNotificationLane.FiveHourRecovery,
            test!.EventKey, when, true, prefs, lease, host, bots);
        Assert(testAuto.Status == WindowsAmanoApprovalStatus.NotPending &&
            fake.Calls == 0 &&
            inbox.Read().Single(x => x.EventKey == test.EventKey).IsTest,
            "Connectivity TEST events can never silently become auto recovery sends.");

        fake.Next = new WindowsAmanoProcessResult(0,
            "CODEX_FIVEHOUR100_TELEGRAM_ACK_V1|" + key5 + "|951\r\n", false);
        var success = Run(dispatcher, TelegramNotificationLane.FiveHourRecovery,
            key5, when, true, prefs, lease, host, bots);
        Assert(success.Status == WindowsAmanoApprovalStatus.Acknowledged &&
            success.TelegramMessageId == 951 &&
            fake.Calls == 1 && fake.Last?.Silent == true &&
            fake.ReservedBeforeProcess,
            "Newly staged 5H must use muted bot, durable first reservation and typed ACK.");

        var replay = Run(dispatcher, TelegramNotificationLane.FiveHourRecovery,
            key5, when, true, prefs, lease, host, bots);
        Assert(replay.Status == WindowsAmanoApprovalStatus.NotPending && fake.Calls == 1,
            "ACKed event cannot automatically send twice.");

        Assert(inbox.Stage(TelegramNotificationLane.WeeklyRecovery,
                key7, when.AddMinutes(1), true, prefs),
            "New 7D weekly recovery must stage.");
        fake.Next = new WindowsAmanoProcessResult(20, "", false);
        var uncertain = Run(dispatcher, TelegramNotificationLane.WeeklyRecovery,
            key7, when.AddMinutes(1), true, prefs, lease, host, bots);
        Assert(uncertain.Status == WindowsAmanoApprovalStatus.Uncertain &&
            fake.Calls == 2 && fake.Last?.Silent == false &&
            inbox.Read().Single(x => x.EventKey == key7).Status ==
                WindowsAmanoOutboxStatus.Uncertain,
            "7D alert failure must become durable Uncertain, not retryable.");
        Assert(Run(dispatcher, TelegramNotificationLane.WeeklyRecovery,
                key7, when.AddMinutes(1), true, prefs, lease, host, bots)
                    .Status == WindowsAmanoApprovalStatus.NotPending &&
            fake.Calls == 2,
            "Failed 7D notification cannot be replayed automatically.");

        Assert(inbox.Stage(TelegramNotificationLane.FiveHourRecovery,
                keyStopped, when.AddMinutes(2), true, prefs),
            "Another real recovery may stage before user disables automatic mode.");
        session.Disarm();
        Assert(Run(dispatcher, TelegramNotificationLane.FiveHourRecovery,
                keyStopped, when.AddMinutes(2), true, prefs, lease, host, bots)
                    .Status == WindowsAmanoApprovalStatus.Blocked &&
            fake.Calls == 2 &&
            inbox.ReadPendingRows().Any(row => row.EventKey == keyStopped),
            "Disarming leaves the pending event for manual review without an auto-send.");

        var afterRestart = new WindowsTelegramAutoRecoveryDispatcher(
            new WindowsTelegramAutoSendSession(), new WindowsAmanoProductionOutbox(data),
            new WindowsExistingAmanoBotProcessAdapter(fake));
        Assert(Run(afterRestart, TelegramNotificationLane.FiveHourRecovery,
                keyStopped, when.AddMinutes(2), true, prefs, lease, host, bots)
                    .Status == WindowsAmanoApprovalStatus.Blocked &&
            fake.Calls == 2,
            "Process restart never inherits the automatic Telegram send privilege.");
    }

    private static WindowsAmanoApprovalResult Run(
        WindowsTelegramAutoRecoveryDispatcher dispatcher,
        TelegramNotificationLane lane, string key, DateTimeOffset when,
        bool newlyStaged,
        TelegramNotificationPreferences prefs,
        WindowsTelegramLocalSenderLease lease,
        string host, string bots) =>
        dispatcher.DeliverNewlyStagedAsync(lane, key, when, newlyStaged,
            prefs, lease, host, bots).GetAwaiter().GetResult();

    private static void Assert(bool okay, string message)
    {
        if (!okay) throw new InvalidOperationException(message);
    }

    private sealed class FakeRunner : IWindowsAmanoScriptRunner
    {
        private readonly WindowsAmanoProductionOutbox outbox;
        public FakeRunner(WindowsAmanoProductionOutbox outbox) => this.outbox = outbox;
        public int Calls { get; private set; }
        public bool ReservedBeforeProcess { get; private set; }
        public WindowsExistingAmanoBotInvocation? Last { get; private set; }
        public WindowsAmanoProcessResult Next { get; set; }

        public Task<WindowsAmanoProcessResult> RunAsync(
            WindowsExistingAmanoBotInvocation route, CancellationToken token)
        {
            Calls++;
            Last = route;
            ReservedBeforeProcess = outbox.Read().Any(entry =>
                entry.EventKey == route.EventKey &&
                entry.Status == WindowsAmanoOutboxStatus.Reserved &&
                entry.AttemptToken is { Length: 32 });
            return Task.FromResult(Next);
        }
    }
}
