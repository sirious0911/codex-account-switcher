namespace CodexAccountSwitcher.Core;

/// <summary>
/// First, fail-closed half of a Windows -> Mac sender handoff:
/// stop this process, persist Viewer/OFF, release the LOCAL Windows sender
/// mutex. It does NOT activate another host, claim distributed ownership,
/// or authorize a Telegram send.
/// </summary>
internal sealed class WindowsTelegramSenderHandoff
{
    private readonly WindowsTelegramAutoSendSession session;
    private readonly WindowsTelegramLocalSenderLease localLease;
    private readonly Func<TelegramNotificationPreferences> readPreferences;
    private readonly Action<TelegramNotificationPreferences> persistPreferences;
    private readonly Func<bool> anySendInProgress;

    internal WindowsTelegramSenderHandoff(
        WindowsTelegramAutoSendSession session,
        WindowsTelegramLocalSenderLease localLease,
        Func<TelegramNotificationPreferences> readPreferences,
        Action<TelegramNotificationPreferences> persistPreferences,
        Func<bool> anySendInProgress)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.localLease = localLease ?? throw new ArgumentNullException(nameof(localLease));
        this.readPreferences = readPreferences ??
            throw new ArgumentNullException(nameof(readPreferences));
        this.persistPreferences = persistPreferences ??
            throw new ArgumentNullException(nameof(persistPreferences));
        this.anySendInProgress = anySendInProgress ??
            throw new ArgumentNullException(nameof(anySendInProgress));
    }

    internal WindowsTelegramHandoffResult StopWindowsSender(bool userConfirmed)
    {
        if (!userConfirmed)
            return new(WindowsTelegramHandoffStatus.NotApproved);
        // Revocation happens BEFORE any subsequent read, disk or Registry
        // operations. Outstanding user sends cannot be recalled, however.
        session.Disarm();
        if (anySendInProgress())
            return new(WindowsTelegramHandoffStatus.InFlightSendMustFinish);
        try
        {
            var current = readPreferences();
            var viewer = current with
            {
                DeviceRole = TelegramNotificationDeviceRole.Viewer,
                NotificationsRequested = false
            };
            if (current != viewer)
                persistPreferences(viewer);

            // Persisted state and the real OS mutex both must indicate OFF.
            var confirmed = readPreferences().Normalize();
            if (confirmed.DeviceRole != TelegramNotificationDeviceRole.Viewer ||
                confirmed.NotificationsRequested ||
                localLease.Update(confirmed) !=
                    WindowsLocalSenderLeaseState.ViewerOrDisabled ||
                session.Armed)
                return new(WindowsTelegramHandoffStatus.LocalReleaseNotVerified);

            return new(WindowsTelegramHandoffStatus.WindowsViewerOffVerified);
        }
        catch (Exception)
        {
            // The UI must never claim successful handoff after partial
            // Registry failure, lease failure or unreadable settings.
            return new(WindowsTelegramHandoffStatus.LocalReleaseNotVerified);
        }
    }
}

public enum WindowsTelegramHandoffStatus
{
    NotApproved,
    InFlightSendMustFinish,
    LocalReleaseNotVerified,
    WindowsViewerOffVerified
}

internal sealed record WindowsTelegramHandoffResult(
    WindowsTelegramHandoffStatus Status)
{
    internal bool WindowsReleased =>
        Status == WindowsTelegramHandoffStatus.WindowsViewerOffVerified;

    // No live Mac device authority exists merely because Windows stopped.
    internal bool RemoteOwnerAuthorized => false;
}
