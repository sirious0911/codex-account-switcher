using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodexAccountSwitcher.Core;

internal static class WindowsAmanoApprovedDeliveryCoordinatorTest
{
    public static void Run(string output)
    {
        var root = Path.Combine(output, "approved-amano-integration");
        if (Directory.Exists(root))
            throw new InvalidOperationException("Approval fixture must start absent.");
        var data = Path.Combine(root, "user-data");
        var scripts = Path.Combine(root, "bundled-scripts");
        var legacy = Path.Combine(root, "existing-bot-metadata");
        Directory.CreateDirectory(scripts);
        Directory.CreateDirectory(legacy);
        foreach (var name in new[]
        {
            WindowsPackagedTelegramScripts.MonitorScriptName,
            WindowsPackagedTelegramScripts.AlertScriptName
        })
            File.Copy(Path.Combine("windows", "scripts", name), Path.Combine(scripts, name));
        foreach (var name in new[]
        {
            "send-telegram-health.ps1", "send-telegram-alert.ps1",
            "telegram-health-token.dpapi", "telegram-token.dpapi",
            "telegram-chat-id.txt"
        })
            File.WriteAllText(Path.Combine(legacy, name), "test-fixture-not-actual-credentials");

        var host = Path.Combine(scripts, "SwitcherHost.exe");
        var now = DateTimeOffset.UtcNow;
        var key5 = new string('d', 64);
        var key7 = new string('e', 64);
        var preferences = new TelegramNotificationPreferences(
            TelegramNotificationDeviceRole.Sender, true, true, true);
        var outbox = new WindowsAmanoProductionOutbox(data);
        Check(outbox.Stage(TelegramNotificationLane.FiveHourRecovery,
                key5, now, true, preferences) &&
            outbox.Stage(TelegramNotificationLane.WeeklyRecovery,
                key7, now, true, preferences),
            "Both verified lanes must stage production approval records.");

        var route5 = WindowsExistingAmanoBotRoute.Prepare(host, legacy,
            TelegramNotificationLane.FiveHourRecovery, key5, now);
        var route7 = WindowsExistingAmanoBotRoute.Prepare(host, legacy,
            TelegramNotificationLane.WeeklyRecovery, key7, now);
        Check(route5 is not null && route7 is not null,
            "Both routes need source-matched fake bot configurations.");

        var fake = new FakeRunner(outbox);
        var adapter = new WindowsExistingAmanoBotProcessAdapter(fake);
        var coordinator = new WindowsAmanoApprovedDeliveryCoordinator(outbox, adapter);
        using var lease = new WindowsTelegramLocalSenderLease(data);
        Check(lease.Update(preferences) == WindowsLocalSenderLeaseState.LocalClaimHeld,
            "The Windows sender owner must be acquired by the current UI thread.");

        var blocked1 = coordinator.ExecuteApprovedOnceAsync(route5,
            TelegramNotificationLane.FiveHourRecovery, preferences, lease,
            userApprovedExactEvent: false, otherHostsNotSendingConfirmed: true)
            .GetAwaiter().GetResult();
        var blocked2 = coordinator.ExecuteApprovedOnceAsync(route5,
            TelegramNotificationLane.FiveHourRecovery, preferences, lease,
            userApprovedExactEvent: true, otherHostsNotSendingConfirmed: false)
            .GetAwaiter().GetResult();
        var blocked3 = coordinator.ExecuteApprovedOnceAsync(route5,
            TelegramNotificationLane.WeeklyRecovery, preferences, lease,
            userApprovedExactEvent: true, otherHostsNotSendingConfirmed: true)
            .GetAwaiter().GetResult();
        Check(blocked1.Status == WindowsAmanoApprovalStatus.Blocked &&
            blocked2.Status == WindowsAmanoApprovalStatus.Blocked &&
            blocked3.Status == WindowsAmanoApprovalStatus.Blocked &&
            fake.Calls == 0 &&
            outbox.Read().All(item => item.Status ==
                WindowsAmanoOutboxStatus.AwaitingApproval),
            "No approval, cross-host confirmation or correct lane means no process.");

        fake.Next = new WindowsAmanoProcessResult(0,
            "CODEX_FIVEHOUR100_TELEGRAM_ACK_V1|" + key5 + "|541\r\n", false);
        var sent5 = coordinator.ExecuteApprovedOnceAsync(route5,
            TelegramNotificationLane.FiveHourRecovery, preferences, lease,
            userApprovedExactEvent: true, otherHostsNotSendingConfirmed: true)
            .GetAwaiter().GetResult();
        Check(sent5.Status == WindowsAmanoApprovalStatus.Acknowledged &&
            sent5.TelegramMessageId == 541 &&
            fake.Calls == 1 &&
            fake.ReservationPresentAtInvocation,
            "Disk reservation must precede a typed 5H synthetic ACK.");

        var restarted = new WindowsAmanoApprovedDeliveryCoordinator(
            new WindowsAmanoProductionOutbox(data), adapter);
        var duplicate = restarted.ExecuteApprovedOnceAsync(route5,
            TelegramNotificationLane.FiveHourRecovery, preferences, lease,
            userApprovedExactEvent: true, otherHostsNotSendingConfirmed: true)
            .GetAwaiter().GetResult();
        Check(duplicate.Status == WindowsAmanoApprovalStatus.NotPending &&
            fake.Calls == 1 &&
            new WindowsAmanoProductionOutbox(data).Read()
                .Single(item => item.EventKey == key5).TelegramMessageId == 541,
            "Restart with completed 5H ACK must never dispatch twice.");

        fake.Next = new WindowsAmanoProcessResult(20, "", false);
        var uncertain7 = coordinator.ExecuteApprovedOnceAsync(route7,
            TelegramNotificationLane.WeeklyRecovery, preferences, lease,
            userApprovedExactEvent: true, otherHostsNotSendingConfirmed: true)
            .GetAwaiter().GetResult();
        Check(uncertain7.Status == WindowsAmanoApprovalStatus.Uncertain &&
            fake.Calls == 2 &&
            new WindowsAmanoProductionOutbox(data).Read()
                .Single(item => item.EventKey == key7).Status ==
                    WindowsAmanoOutboxStatus.Uncertain,
            "7D failure must become terminal uncertain, not auto-retry.");

        var again7 = restarted.ExecuteApprovedOnceAsync(route7,
            TelegramNotificationLane.WeeklyRecovery, preferences, lease,
            userApprovedExactEvent: true, otherHostsNotSendingConfirmed: true)
            .GetAwaiter().GetResult();
        Check(again7.Status == WindowsAmanoApprovalStatus.NotPending &&
            fake.Calls == 2,
            "Restart must never replay a failed/ambiguous 7D request.");

        // A stale route whose pinned script changed must not consume even
        // the durable reservation: the user may repair the installation.
        var badKey = new string('a', 64);
        Check(outbox.Stage(TelegramNotificationLane.WeeklyRecovery,
            badKey, now, true, preferences), "A distinct pending 7D event should stage.");
        var badRoute = route7! with { EventKey = badKey };
        File.AppendAllText(Path.Combine(scripts,
            WindowsPackagedTelegramScripts.AlertScriptName), "#tamper-fixture");
        var beforeTamper = fake.Calls;
        var badAttempt = restarted.ExecuteApprovedOnceAsync(badRoute,
            TelegramNotificationLane.WeeklyRecovery, preferences, lease,
            userApprovedExactEvent: true, otherHostsNotSendingConfirmed: true)
            .GetAwaiter().GetResult();
        Check(badAttempt.Status == WindowsAmanoApprovalStatus.Blocked &&
            fake.Calls == beforeTamper &&
            outbox.Read().Single(item => item.EventKey == badKey).Status ==
                WindowsAmanoOutboxStatus.AwaitingApproval,
            "Script tampering must block BEFORE consuming the one-shot reservation.");

        // User OFF releases the sender. Any unconsumed event is blocked.
        var pending = new string('f', 64);
        Check(outbox.Stage(TelegramNotificationLane.FiveHourRecovery,
            pending, now, true, preferences), "A future event should stage.");
        var routePending = WindowsExistingAmanoBotRoute.Prepare(host, legacy,
            TelegramNotificationLane.FiveHourRecovery, pending, now);
        lease.Update(TelegramNotificationPreferences.Default);
        var withoutOwner = restarted.ExecuteApprovedOnceAsync(routePending,
            TelegramNotificationLane.FiveHourRecovery, preferences, lease,
            userApprovedExactEvent: true, otherHostsNotSendingConfirmed: true)
            .GetAwaiter().GetResult();
        Check(withoutOwner.Status == WindowsAmanoApprovalStatus.Blocked &&
            fake.Calls == 2 &&
            outbox.Read().Single(item => item.EventKey == pending).Status ==
                WindowsAmanoOutboxStatus.AwaitingApproval,
            "Turning Sender OFF blocks a previously prepared event.");

        Check(!Directory.Exists(Path.Combine(root, "telegram-outgoing")) &&
            !File.Exists(Path.Combine(root, "real-send-receipt.json")),
            "Offline integration must not touch network or create live sender receipts.");
    }

    private static void Check(bool okay, string message)
    {
        if (!okay) throw new InvalidOperationException(message);
    }

    private sealed class FakeRunner : IWindowsAmanoScriptRunner
    {
        private readonly WindowsAmanoProductionOutbox outbox;
        public FakeRunner(WindowsAmanoProductionOutbox outbox) => this.outbox = outbox;
        public int Calls { get; private set; }
        public bool ReservationPresentAtInvocation { get; private set; }
        public WindowsAmanoProcessResult Next { get; set; }

        public Task<WindowsAmanoProcessResult> RunAsync(
            WindowsExistingAmanoBotInvocation route, CancellationToken token)
        {
            Calls++;
            ReservationPresentAtInvocation = outbox.Read().Any(item =>
                item.EventKey == route.EventKey &&
                item.Status == WindowsAmanoOutboxStatus.Reserved &&
                item.AttemptToken is { Length: 32 });
            return Task.FromResult(Next);
        }
    }
}
