namespace CodexAccountSwitcher.Core;

/// <summary>
/// Local UI preference is NOT cross-host ownership proof. Keep this boundary
/// fail-closed until an independently verified single-sender lease and actual
/// delivery transport have been reviewed and implemented for BOTH platforms.
/// The decision is a dry-run preview, not an authorization.
/// </summary>
public static class TelegramSenderDispatchPolicy
{
    public static TelegramDispatchInspection Inspect(
        TelegramNotificationPreferences? settings,
        TelegramNotificationLane lane)
    {
        if (settings is null)
            return new(TelegramDispatchHoldReason.SettingsUnavailable, false);
        if (!Enum.IsDefined(lane))
            return new(TelegramDispatchHoldReason.InvalidNotificationLane, false);

        var preferences = settings.Normalize();
        if (preferences.DeviceRole != TelegramNotificationDeviceRole.Sender)
            return new(TelegramDispatchHoldReason.ViewerOnly, false);
        if (!preferences.NotificationsRequested)
            return new(TelegramDispatchHoldReason.NotificationsOff, false);

        bool laneSelected = lane switch {
            TelegramNotificationLane.FiveHourRecovery =>
                preferences.FiveHourRequested,
            TelegramNotificationLane.WeeklyRecovery =>
                preferences.WeeklyRequested,
            _ => false
        };
        if (!laneSelected)
            return new(TelegramDispatchHoldReason.LaneDisabled, false);

        // No shared state / lease authority exists yet. A second running
        // Mac or Windows Switcher cannot be ruled out from local settings.
        return new(TelegramDispatchHoldReason.SingleSenderLeaseUnverified, true);
    }
}

public enum TelegramNotificationLane
{
    FiveHourRecovery = 0,
    WeeklyRecovery = 1
}

public enum TelegramDispatchHoldReason
{
    SettingsUnavailable,
    InvalidNotificationLane,
    ViewerOnly,
    NotificationsOff,
    LaneDisabled,
    SingleSenderLeaseUnverified
}

public sealed record TelegramDispatchInspection(
    TelegramDispatchHoldReason Reason,
    bool LocalSenderPreferenceRequested)
{
    /// <summary>
    /// Must remain false until independently verified ownership and actual
    /// transport are separately connected. This class never sends Telegram.
    /// </summary>
    public bool ActualTelegramDeliveryAuthorized => false;
}
