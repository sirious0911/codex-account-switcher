using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CodexAccountSwitcher.Core;

internal static class WindowsTelegramAutoSendSessionTest
{
    internal static void Run(string output)
    {
        var root = Path.Combine(output, "telegram-auto-session-fake-only");
        if (Directory.Exists(root))
            throw new InvalidOperationException("Auto test fixture must start absent.");
        var prefs = new TelegramNotificationPreferences(
            TelegramNotificationDeviceRole.Sender, true, true, true);
        var disabled = TelegramNotificationPreferences.Default;
        var session = new WindowsTelegramAutoSendSession();
        Assert(!session.Armed &&
            !session.AllowsNewRecovery(TelegramNotificationLane.FiveHourRecovery,
                isTest: false, prefs, null),
            "Automatic Telegram must be OFF on startup with no registered intent.");

        using var lease = new WindowsTelegramLocalSenderLease(root);
        Assert(lease.Update(prefs) == WindowsLocalSenderLeaseState.LocalClaimHeld,
            "Test-only local mutex must be held before session confirmation.");
        Assert(!session.Arm(false, true, prefs, lease, true) &&
            !session.Arm(true, false, prefs, lease, true) &&
            !session.Arm(true, true, prefs, lease, false) &&
            !session.Arm(true, true, disabled, lease, true) &&
            !session.Armed,
            "No stored Sender preference, missing consent, bot or other-host confirmation can arm.");

        Assert(session.Arm(true, true, prefs, lease, true) &&
            session.Armed &&
            session.AllowsNewRecovery(TelegramNotificationLane.FiveHourRecovery,
                isTest: false, prefs, lease) &&
            session.AllowsNewRecovery(TelegramNotificationLane.WeeklyRecovery,
                isTest: false, prefs, lease) &&
            !session.AllowsNewRecovery(TelegramNotificationLane.FiveHourRecovery,
                isTest: true, prefs, lease),
            "Explicitly armed session may allow only newly confirmed real quota recovery.");

        var changed = prefs with { WeeklyRequested = false };
        Assert(!session.AllowsNewRecovery(TelegramNotificationLane.WeeklyRecovery,
                isTest: false, changed, lease) &&
            !session.AllowsNewRecovery(TelegramNotificationLane.FiveHourRecovery,
                isTest: false, changed, lease),
            "Any post-consent preference mutation must revoke dispatch eligibility.");

        session.Disarm();
        Assert(!session.Armed && !session.AllowsNewRecovery(
                TelegramNotificationLane.FiveHourRecovery, false, prefs, lease),
            "Disarm must make every future quota event pending-only.");

        Assert(new WindowsTelegramAutoSendSession().Armed == false,
            "A process restart (new session instance) must never inherit consent.");

        Assert(lease.Update(disabled) ==
                WindowsLocalSenderLeaseState.ViewerOrDisabled &&
            !session.Arm(true, true, prefs, lease, true),
            "Viewer/OFF local owner prevents an automatic sender claim.");

        // Use the reviewed durable queue and a fake process; no bot credentials
        // or Telegram network may be read in this regression.
        var outbox = new WindowsAmanoProductionOutbox(root);
        var key = new string('b', 64);
        var now = new DateTimeOffset(2026, 11, 13, 3, 45, 0, TimeSpan.Zero);
        Assert(outbox.Stage(TelegramNotificationLane.FiveHourRecovery,
                key, now, true, prefs),
            "A confirmed recovery is durably pending even when auto-session is OFF.");
        var restarted = new WindowsTelegramAutoSendSession();
        Assert(!restarted.Armed &&
            outbox.ReadPendingRows().Any(row => row.EventKey == key && !row.IsTest),
            "No replay after restart; an unsent recovery remains manual-reviewable.");
    }

    private static void Assert(bool okay, string message)
    {
        if (!okay) throw new InvalidOperationException(message);
    }
}
