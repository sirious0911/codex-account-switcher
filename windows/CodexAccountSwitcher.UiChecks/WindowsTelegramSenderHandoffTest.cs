using System;
using System.IO;
using CodexAccountSwitcher.Core;

internal static class WindowsTelegramSenderHandoffTest
{
    internal static void Run(string output)
    {
        var root = Path.Combine(output, "telegram-windows-to-mac-handoff-offline");
        if (Directory.Exists(root))
            throw new InvalidOperationException("Handoff fixture must start absent.");
        var preferences = new TelegramNotificationPreferences(
            TelegramNotificationDeviceRole.Sender,
            NotificationsRequested: true,
            FiveHourRequested: true,
            WeeklyRequested: true);
        using var lease = new WindowsTelegramLocalSenderLease(root);
        Assert(lease.Update(preferences) == WindowsLocalSenderLeaseState.LocalClaimHeld,
            "The original Windows Sender needs a local lease.");
        var session = new WindowsTelegramAutoSendSession();
        Assert(session.Arm(true, true, preferences, lease, true),
            "Initial synthetic auto session must be explicitly armed.");
        var writes = 0;
        var inFlight = false;
        var service = new WindowsTelegramSenderHandoff(
            session, lease,
            readPreferences: () => preferences,
            persistPreferences: value => { writes++; preferences = value; },
            anySendInProgress: () => inFlight);
        var noApproval = service.StopWindowsSender(userConfirmed: false);
        Assert(noApproval.Status == WindowsTelegramHandoffStatus.NotApproved &&
            !noApproval.WindowsReleased && !noApproval.RemoteOwnerAuthorized &&
            session.Armed && writes == 0 &&
            lease.State == WindowsLocalSenderLeaseState.LocalClaimHeld,
            "Without the user's final Yes, no Windows settings or lease may change.");

        inFlight = true;
        var hold = service.StopWindowsSender(userConfirmed: true);
        Assert(hold.Status == WindowsTelegramHandoffStatus.InFlightSendMustFinish &&
            !session.Armed && writes == 0 &&
            preferences.DeviceRole == TelegramNotificationDeviceRole.Sender &&
            lease.State == WindowsLocalSenderLeaseState.LocalClaimHeld,
            "An active Telegram subprocess must block completion but disarm auto send.");

        inFlight = false;
        var finished = service.StopWindowsSender(userConfirmed: true);
        Assert(finished.Status == WindowsTelegramHandoffStatus.WindowsViewerOffVerified &&
            finished.WindowsReleased && !finished.RemoteOwnerAuthorized &&
            !session.Armed && writes == 1 &&
            preferences.DeviceRole == TelegramNotificationDeviceRole.Viewer &&
            !preferences.NotificationsRequested &&
            preferences.FiveHourRequested && preferences.WeeklyRequested &&
            lease.State == WindowsLocalSenderLeaseState.ViewerOrDisabled,
            "Windows sender transfer must disable Viewer/OFF and release its mutex.");
        Assert(!session.AllowsNewRecovery(
                TelegramNotificationLane.FiveHourRecovery,
                isTest: false, preferences, lease),
            "After local sender release, no automatic Telegram recovery is authorized.");

        // The OS mutex must be released, not merely labelled "OFF".
        using (var other = new WindowsTelegramLocalSenderLease(root))
        {
            var selected = preferences with
            {
                DeviceRole = TelegramNotificationDeviceRole.Sender,
                NotificationsRequested = true
            };
            Assert(other.Update(selected) ==
                    WindowsLocalSenderLeaseState.LocalClaimHeld,
                "The OS lease must be available after handoff.");
            Assert(other.Update(TelegramNotificationPreferences.Default) ==
                    WindowsLocalSenderLeaseState.ViewerOrDisabled,
                "The synthetic successor releases the OS lease without sending.");
        }
        var again = service.StopWindowsSender(userConfirmed: true);
        Assert(again.WindowsReleased && writes == 1,
            "Retrying a completed stop must be idempotent.");

        var failingPrefs = new TelegramNotificationPreferences(
            TelegramNotificationDeviceRole.Sender, true, true, true);
        Assert(lease.Update(failingPrefs) ==
                WindowsLocalSenderLeaseState.LocalClaimHeld,
            "Create a second synthetic sender state for failure handling.");
        Assert(session.Arm(true, true, failingPrefs, lease, true),
            "Synthetic session must be armed before a failed transfer.");
        var broken = new WindowsTelegramSenderHandoff(
            session, lease,
            readPreferences: () => failingPrefs,
            persistPreferences: _ => throw new IOException("synthetic storage failure"),
            anySendInProgress: () => false);
        var failure = broken.StopWindowsSender(userConfirmed: true);
        Assert(failure.Status == WindowsTelegramHandoffStatus.LocalReleaseNotVerified &&
            !failure.WindowsReleased && !failure.RemoteOwnerAuthorized &&
            !session.Armed,
            "Registry failure must NEVER be reported as a successful remote handoff.");

        // The test does not touch HKCU, Telegram DPAPI tokens, accounts,
        // OS autorun, the existing Mac app, or network.
        lease.Update(TelegramNotificationPreferences.Default);
    }

    private static void Assert(bool pass, string explanation)
    {
        if (!pass) throw new InvalidOperationException(explanation);
    }
}
