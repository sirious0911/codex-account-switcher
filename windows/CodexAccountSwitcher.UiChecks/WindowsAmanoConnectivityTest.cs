using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodexAccountSwitcher.Core;

internal static class WindowsAmanoConnectivityTest
{
    internal static void Run(string output)
    {
        var root = Path.Combine(output, "amano-connectivity-test-isolation");
        if (Directory.Exists(root))
            throw new InvalidOperationException("Connectivity fixture must start absent.");
        var scripts = Path.Combine(root, "synthetic-bundled-runtime");
        var legacy = Path.Combine(root, "synthetic-existing-bot");
        var data = Path.Combine(root, "synthetic-approval-outbox");
        Directory.CreateDirectory(scripts);
        Directory.CreateDirectory(legacy);
        foreach (var name in new[] {
            WindowsPackagedTelegramScripts.MonitorScriptName,
            WindowsPackagedTelegramScripts.AlertScriptName
        })
            File.Copy(Path.Combine("windows", "scripts", name), Path.Combine(scripts, name));
        foreach (var name in new[] {
            "send-telegram-health.ps1", "send-telegram-alert.ps1",
            "telegram-health-token.dpapi", "telegram-token.dpapi",
            "telegram-chat-id.txt"
        })
            File.WriteAllText(Path.Combine(legacy, name), "not-a-token-test-fixture");

        var now = new DateTimeOffset(2026, 11, 13, 3, 45, 0, TimeSpan.Zero);
        var prefs = new TelegramNotificationPreferences(
            TelegramNotificationDeviceRole.Sender, true, true, true);
        var outbox = new WindowsAmanoProductionOutbox(data);
        Assert(outbox.StageConnectivityTest(TelegramNotificationLane.FiveHourRecovery,
                   TelegramNotificationPreferences.Default, now, true) is null &&
               outbox.StageConnectivityTest(TelegramNotificationLane.WeeklyRecovery,
                   prefs, now, false) is null &&
               !Directory.Exists(data),
            "OFF device or unverified bot must not create a sendable test event.");

        var five = outbox.StageConnectivityTest(
            TelegramNotificationLane.FiveHourRecovery, prefs, now, true);
        var seven = outbox.StageConnectivityTest(
            TelegramNotificationLane.WeeklyRecovery, prefs, now, true);
        Assert(five is { IsTest: true } && seven is { IsTest: true } &&
            five.EventKey.Length == 64 && seven.EventKey.Length == 64 &&
            five.EventKey != seven.EventKey &&
            five.LaneLabel.Contains("연결 테스트", StringComparison.Ordinal) &&
            seven.LaneLabel.Contains("연결 테스트", StringComparison.Ordinal),
            "Two lanes must stage distinctly labeled random-key tests.");

        var sameFive = outbox.StageConnectivityTest(
            TelegramNotificationLane.FiveHourRecovery, prefs, now.AddSeconds(25), true);
        Assert(sameFive?.EventKey == five!.EventKey && outbox.Read().Count == 2,
            "Repeat test setup must reuse one pending test, not enqueue Telegram spam.");

        // A real recovery must never be reclassified or overwritten by a test.
        var realKey = new string('c', 64);
        Assert(outbox.Stage(TelegramNotificationLane.FiveHourRecovery,
                realKey, now, true, prefs),
            "An unrelated real recovery must still be stageable.");
        var afterRestart = new WindowsAmanoProductionOutbox(data);
        var pending = afterRestart.ReadPendingRows();
        Assert(pending.Count == 3 &&
            pending.Count(x => x.IsTest) == 2 &&
            pending.Single(x => x.EventKey == realKey).IsTest == false,
            "Test/real identity must persist across process restart.");

        var host = Path.Combine(scripts, "SwitcherHost.exe");
        var test5 = WindowsExistingAmanoBotRoute.Prepare(
            host, legacy, five!.Lane, five.EventKey, five.ObservedAt, isTest: true);
        var test7 = WindowsExistingAmanoBotRoute.Prepare(
            host, legacy, seven!.Lane, seven.EventKey, seven.ObservedAt, isTest: true);
        var real = WindowsExistingAmanoBotRoute.Prepare(
            host, legacy, TelegramNotificationLane.FiveHourRecovery, realKey, now);
        Assert(test5 is { IsTest: true, Silent: true } &&
            test7 is { IsTest: true, Silent: false } &&
            real is { IsTest: false } &&
            test5.Message.Contains("연결 테스트", StringComparison.Ordinal) &&
            test7.Message.Contains("연결 테스트", StringComparison.Ordinal) &&
            !real.Message.Contains("테스트", StringComparison.Ordinal) &&
            WindowsExistingAmanoBotProcessAdapter.ValidRoute(test5) &&
            WindowsExistingAmanoBotProcessAdapter.ValidRoute(test7) &&
            WindowsExistingAmanoBotProcessAdapter.ValidRoute(real),
            "Connectivity messages must be independently validated and not claim recovery.");

        var command5 = WindowsAmanoPowerShellRunner.CreateProcessStartInfo(test5!);
        var command7 = WindowsAmanoPowerShellRunner.CreateProcessStartInfo(test7!);
        var commandReal = WindowsAmanoPowerShellRunner.CreateProcessStartInfo(real!);
        Assert(command5.ArgumentList.Contains("-TestSend") &&
            command7.ArgumentList.Contains("-TestSend") &&
            !commandReal.ArgumentList.Contains("-TestSend") &&
            command5.ArgumentList.Contains("-ApprovedSingleSend") &&
            command5.ArgumentList.Contains("-WindowsSenderVerified"),
            "Only test routes must pass the explicit PowerShell -TestSend switch.");

        using var localSender = new WindowsTelegramLocalSenderLease(data);
        Assert(localSender.Update(prefs) == WindowsLocalSenderLeaseState.LocalClaimHeld,
            "Synthetic Windows Sender must hold only its local test mutex.");

        var fake = new FakeRunner();
        var coordinator = new WindowsAmanoApprovedDeliveryCoordinator(
            afterRestart, new WindowsExistingAmanoBotProcessAdapter(fake));
        var wrongKind = coordinator.ExecuteApprovedOnceAsync(
            test5! with { IsTest = false }, five.Lane, prefs, localSender,
            userApprovedExactEvent: true, otherHostsNotSendingConfirmed: true)
            .GetAwaiter().GetResult();
        Assert(wrongKind.Status == WindowsAmanoApprovalStatus.Blocked ||
            wrongKind.Status == WindowsAmanoApprovalStatus.NotPending,
            "Test event cannot be sent as a normal recovery.");
        Assert(afterRestart.Read().Single(x => x.EventKey == five.EventKey)
                .Status == WindowsAmanoOutboxStatus.AwaitingApproval &&
            fake.Calls == 0, "Wrong test/real kind must not consume any send reservation.");

        var unapproved = coordinator.ExecuteApprovedOnceAsync(
            test5, five.Lane, prefs, localSender,
            userApprovedExactEvent: false, otherHostsNotSendingConfirmed: true)
            .GetAwaiter().GetResult();
        Assert(unapproved.Status == WindowsAmanoApprovalStatus.Blocked &&
            fake.Calls == 0, "User approval remains mandatory for connectivity tests.");

        fake.Next = new WindowsAmanoProcessResult(0,
            "CODEX_FIVEHOUR100_TELEGRAM_ACK_V1|" + five.EventKey + "|871\r\n", false);
        var ack = coordinator.ExecuteApprovedOnceAsync(
            test5, five.Lane, prefs, localSender,
            userApprovedExactEvent: true, otherHostsNotSendingConfirmed: true)
            .GetAwaiter().GetResult();
        Assert(ack.Status == WindowsAmanoApprovalStatus.Acknowledged &&
            ack.TelegramMessageId == 871 && fake.Calls == 1 &&
            fake.LastRoute?.IsTest == true &&
            new WindowsAmanoProductionOutbox(data).Read()
                .Single(x => x.EventKey == five.EventKey).IsTest,
            "Offline test receipt must persist without reclassifying test as real.");

        fake.Next = new WindowsAmanoProcessResult(20, "", false);
        var uncertain = coordinator.ExecuteApprovedOnceAsync(
            test7, seven.Lane, prefs, localSender,
            userApprovedExactEvent: true, otherHostsNotSendingConfirmed: true)
            .GetAwaiter().GetResult();
        Assert(uncertain.Status == WindowsAmanoApprovalStatus.Uncertain &&
            new WindowsAmanoProductionOutbox(data).Read()
                .Single(x => x.EventKey == seven.EventKey).Status ==
                WindowsAmanoOutboxStatus.Uncertain &&
            fake.Calls == 2,
            "Ambiguous 7D test result must prevent repeated Telegram calls.");

        var repeated = coordinator.ExecuteApprovedOnceAsync(
            test7, seven.Lane, prefs, localSender,
            userApprovedExactEvent: true, otherHostsNotSendingConfirmed: true)
            .GetAwaiter().GetResult();
        Assert(repeated.Status == WindowsAmanoApprovalStatus.NotPending &&
            fake.Calls == 2 &&
            new WindowsAmanoProductionOutbox(data).Read()
                .Single(x => x.EventKey == realKey).Status ==
                WindowsAmanoOutboxStatus.AwaitingApproval,
            "Duplicate test must not run and real quota events must stay pending.");
    }

    private static void Assert(bool okay, string message)
    {
        if (!okay) throw new InvalidOperationException(message);
    }

    private sealed class FakeRunner : IWindowsAmanoScriptRunner
    {
        public int Calls { get; private set; }
        public WindowsExistingAmanoBotInvocation? LastRoute { get; private set; }
        public WindowsAmanoProcessResult Next { get; set; }
        public Task<WindowsAmanoProcessResult> RunAsync(
            WindowsExistingAmanoBotInvocation route, CancellationToken token)
        {
            Calls++;
            LastRoute = route;
            return Task.FromResult(Next);
        }
    }
}
