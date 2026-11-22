namespace CodexAccountSwitcher.Core;

/// <summary>
/// Non-authorizing crash/restart disposition for a V2 *test fixture* marker.
/// A durable reservation is not evidence that a Telegram request was sent,
/// accepted, or delivered. No classification may trigger automated replay.
/// This module does not access disk, OS processes, accounts or the network.
/// </summary>
public static class WindowsTelegramCrashRecoveryPolicy
{
    public static WindowsCrashRecoveryDisposition Review(
        WindowsV2RecoveryInspection? inspection)
    {
        if (inspection is null)
            return Hold(WindowsCrashRecoveryReason.MissingInspection);

        return inspection.Status switch
        {
            WindowsV2RecoveryStatus.ValidV2FixtureNoSend =>
                Hold(WindowsCrashRecoveryReason.ReservationObservedDeliveryUnknown),
            WindowsV2RecoveryStatus.EmptyOrInterrupted =>
                Hold(WindowsCrashRecoveryReason.InterruptedReservation),
            WindowsV2RecoveryStatus.MalformedV2Marker or
            WindowsV2RecoveryStatus.WrongReservationVersion or
            WindowsV2RecoveryStatus.EventOrLaneMismatch or
            WindowsV2RecoveryStatus.DeviceMismatch =>
                Hold(WindowsCrashRecoveryReason.InvalidOrConflictedReservation),
            WindowsV2RecoveryStatus.NoClaimObserved =>
                Hold(WindowsCrashRecoveryReason.MissingClaimNotProofOfNoSend),
            WindowsV2RecoveryStatus.ParentUntrusted or
            WindowsV2RecoveryStatus.FileAclOrIdUntrusted =>
                Hold(WindowsCrashRecoveryReason.StorageIdentityUntrusted),
            WindowsV2RecoveryStatus.FileOpenUntrusted or
            WindowsV2RecoveryStatus.ReadUncertain =>
                Hold(WindowsCrashRecoveryReason.ReadOrOpenUncertain),
            WindowsV2RecoveryStatus.UnsupportedPlatform or
            WindowsV2RecoveryStatus.InvalidBinding or
            WindowsV2RecoveryStatus.InvalidFixtureScope =>
                Hold(WindowsCrashRecoveryReason.UnsupportedOrInvalidScope),
            _ => Hold(WindowsCrashRecoveryReason.UnknownStatus)
        };
    }

    private static WindowsCrashRecoveryDisposition Hold(
        WindowsCrashRecoveryReason reason) => new(reason);
}

public enum WindowsCrashRecoveryReason
{
    MissingInspection,
    ReservationObservedDeliveryUnknown,
    InterruptedReservation,
    InvalidOrConflictedReservation,
    MissingClaimNotProofOfNoSend,
    StorageIdentityUntrusted,
    ReadOrOpenUncertain,
    UnsupportedOrInvalidScope,
    UnknownStatus
}

/// <summary>
/// These invariant false flags are independent of the observed marker state.
/// Future real delivery would need separate, independently authenticated evidence
/// and an operator-approved production gate rather than a recovered reservation.
/// </summary>
public sealed record WindowsCrashRecoveryDisposition(
    WindowsCrashRecoveryReason Reason)
{
    public bool DeliveryConfirmed=>false;
    public bool AutomaticReplayAllowed=>false;
    public bool OverwriteOrRepairAllowed=>false;
    public bool ProductionReservationAuthorized=>false;
    public bool TelegramSendAuthorized=>false;
    public bool RequiresManualReview=>true;
}
