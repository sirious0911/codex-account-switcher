namespace CodexAccountSwitcher.Core;

/// <summary>
/// Future shared-lease contract, for independent static/mock verification.
/// No distributed authority, lease storage, clock oracle, or sender is supplied.
/// Do NOT use a caller-provided witness as actual production authorization.
/// </summary>
public sealed record TelegramSharedLeaseWitness(
    string OwnerDevice,
    long FencingEpoch,
    DateTimeOffset ExpiresAtUtc);

public static class TelegramCrossHostHandoffReview
{
    private static readonly TimeSpan MaxReviewHorizon=TimeSpan.FromSeconds(60);

    public static TelegramHandoffInspection Inspect(
        TelegramNotificationPreferences? local,
        TelegramNotificationLane lane,
        string? localDevice,
        TelegramSharedLeaseWitness? mockSharedWitness,
        long expectedFencingEpoch,
        DateTimeOffset authorityNowUtc)
    {
        var dispatch=TelegramSenderDispatchPolicy.Inspect(local,lane);
        if (dispatch.Reason!=TelegramDispatchHoldReason.SingleSenderLeaseUnverified)
            return new(TelegramHandoffHoldReason.LocalNotificationsNotRequested,false);
        if (!ValidDeviceId(localDevice))
            return new(TelegramHandoffHoldReason.InvalidDeviceIdentifier,false);
        if (mockSharedWitness is null)
            return new(TelegramHandoffHoldReason.SharedAuthorityUnavailable,false);
        if (!ValidDeviceId(mockSharedWitness.OwnerDevice) ||
            mockSharedWitness.ExpiresAtUtc.Offset!=TimeSpan.Zero ||
            authorityNowUtc.Offset!=TimeSpan.Zero)
            return new(TelegramHandoffHoldReason.MalformedLeaseWitness,false);
        if (mockSharedWitness.FencingEpoch<=0 || expectedFencingEpoch<=0 ||
            expectedFencingEpoch!=mockSharedWitness.FencingEpoch)
            return new(TelegramHandoffHoldReason.FencingEpochMismatch,false);
        if (!string.Equals(mockSharedWitness.OwnerDevice,localDevice,
                StringComparison.Ordinal))
            return new(TelegramHandoffHoldReason.DifferentOwner,false);
        var remaining=mockSharedWitness.ExpiresAtUtc-authorityNowUtc;
        if (remaining<=TimeSpan.Zero)
            return new(TelegramHandoffHoldReason.ExpiredLease,false);
        if (remaining>MaxReviewHorizon)
            return new(TelegramHandoffHoldReason.UnreasonableLeaseLifetime,false);

        // Only a mock contract check. Real send would additionally require an
        // authenticated, linearizable shared lease authority, a remote fence
        // check immediately before EVERY send, shared event deduplication, and
        // actual user-authorized one-time message consent.
        return new(TelegramHandoffHoldReason.MockLeaseWouldQualify,true);
    }

    private static bool ValidDeviceId(string? id) =>
        id is {Length:>0 and <=48} &&
        id.All(c=>c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or
            >= '0' and <= '9' or '-');
}

public enum TelegramHandoffHoldReason
{
    LocalNotificationsNotRequested,
    InvalidDeviceIdentifier,
    SharedAuthorityUnavailable,
    MalformedLeaseWitness,
    FencingEpochMismatch,
    DifferentOwner,
    ExpiredLease,
    UnreasonableLeaseLifetime,
    MockLeaseWouldQualify
}

public sealed record TelegramHandoffInspection(
    TelegramHandoffHoldReason Reason,
    bool WouldPassSharedLeaseSimulation)
{
    // A mock witness must not grant actual Telegram sending.
    public bool ActualSendAuthorized=>false;
}
