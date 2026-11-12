namespace CodexAccountSwitcher.Core;

/// <summary>
/// Platform-local preference contract shared in meaning with macOS SwiftUI.
/// No preference value can grant network/process access. Two computers can
/// independently request Sender, so an actual single-owner lease is required
/// before enabling real Telegram delivery.
/// </summary>
public enum TelegramNotificationDeviceRole
{
    Viewer = 0,
    Sender = 1
}

public sealed record TelegramNotificationPreferences(
    TelegramNotificationDeviceRole DeviceRole = TelegramNotificationDeviceRole.Viewer,
    bool NotificationsRequested = false,
    bool FiveHourRequested = true,
    bool WeeklyRequested = true)
{
    public static TelegramNotificationPreferences Default => new();

    public TelegramNotificationPreferences Normalize() =>
        Enum.IsDefined(DeviceRole) ? this :
            this with { DeviceRole = TelegramNotificationDeviceRole.Viewer };

    public bool WantsFiveHour =>
        DeviceRole == TelegramNotificationDeviceRole.Sender &&
        NotificationsRequested && FiveHourRequested;

    public bool WantsWeekly =>
        DeviceRole == TelegramNotificationDeviceRole.Sender &&
        NotificationsRequested && WeeklyRequested;

    // A local setting is not proof that all other hosts are stopped.
    // This revision exposes no live sender transport; this property
    // deliberately remains false regardless of selected preferences.
    public bool ActualTelegramDeliveryAuthorized => false;
}
