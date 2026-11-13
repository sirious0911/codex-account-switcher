namespace CodexAccountSwitcher.Core;

/// <summary>
/// Explicit, in-memory automatic Telegram authorization for this ONE Windows
/// process. A preference, Registry value, launch argument or restart cannot
/// arm the session. This is not cross-host ownership or proof another PC/Mac
/// is not sending: the user must directly confirm it at the UI.
/// </summary>
internal sealed class WindowsTelegramAutoSendSession
{
    private TelegramNotificationPreferences? approvedPreferences;
    private WindowsTelegramLocalSenderLease? approvedLease;

    internal bool Armed => approvedPreferences is not null &&
        approvedLease is not null;

    internal bool Arm(
        bool explicitSessionConsent,
        bool userConfirmedOtherHostsOff,
        TelegramNotificationPreferences? preferences,
        WindowsTelegramLocalSenderLease? lease,
        bool configuredBotVerified)
    {
        Disarm();
        if (!explicitSessionConsent || !userConfirmedOtherHostsOff ||
            !configuredBotVerified || preferences is null || lease is null)
            return false;
        var snapshot = preferences.Normalize();
        if (!(snapshot.WantsFiveHour || snapshot.WantsWeekly) ||
            lease.State != WindowsLocalSenderLeaseState.LocalClaimHeld)
            return false;
        approvedPreferences = snapshot;
        approvedLease = lease;
        return true;
    }

    internal bool AllowsNewRecovery(
        TelegramNotificationLane lane, bool isTest,
        TelegramNotificationPreferences? currentPreferences,
        WindowsTelegramLocalSenderLease? currentLease)
    {
        if (isTest || !Armed || currentPreferences is null ||
            !ReferenceEquals(approvedLease, currentLease) ||
            currentLease?.State != WindowsLocalSenderLeaseState.LocalClaimHeld ||
            currentPreferences.Normalize() != approvedPreferences)
            return false;
        return lane switch
        {
            TelegramNotificationLane.FiveHourRecovery =>
                currentPreferences.WantsFiveHour,
            TelegramNotificationLane.WeeklyRecovery =>
                currentPreferences.WantsWeekly,
            _ => false
        };
    }

    internal void Disarm()
    {
        approvedPreferences = null;
        approvedLease = null;
    }
}
