namespace CodexAccountSwitcher.Core;

/// <summary>
/// Validation-only staging contract. A caller-provided claim, local audit, or
/// Sender toggle cannot establish consent, ownership, or a durable send claim.
/// This code has no filesystem, process, network or credential access.
/// </summary>
public static class WindowsTelegramProductionBoundaryReview
{
    public static TelegramProductionBoundaryInspection Inspect(
        TelegramNotificationPreferences? preferences,
        WindowsSenderReadinessInspection? localAudit,
        TelegramNotificationLane lane,
        string? eventKey,
        string? localDeviceId,
        TelegramProductionClaimForReview? untrustedClaim,
        DateTimeOffset nowUtc)
    {
        if (!OperatingSystem.IsWindows())
            return new(TelegramProductionBoundaryHold.UnsupportedPlatform);
        if (nowUtc.Offset != TimeSpan.Zero)
            return new(TelegramProductionBoundaryHold.UntrustedClock);
        if (!Enum.IsDefined(lane))
            return new(TelegramProductionBoundaryHold.InvalidLane);
        if (eventKey is not { Length: 64 } ||
            !eventKey.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            return new(TelegramProductionBoundaryHold.InvalidEventKey);
        if (localDeviceId is not { Length: > 0 and <= 48 } ||
            !localDeviceId.All(c => c is >= 'a' and <= 'z' or
                >= 'A' and <= 'Z' or >= '0' and <= '9' or '-'))
            return new(TelegramProductionBoundaryHold.InvalidDeviceId);
        if (TelegramSenderDispatchPolicy.Inspect(preferences, lane).Reason !=
            TelegramDispatchHoldReason.SingleSenderLeaseUnverified)
            return new(TelegramProductionBoundaryHold.LocalNotificationDisabled);
        if (localAudit is null)
            return new(TelegramProductionBoundaryHold.LocalAuditMissing);
        if (localAudit.Reason != WindowsSenderReadinessHold.RemoteSenderUnverifiable ||
            !localAudit.LocalChecksLookAcceptable)
            return new(TelegramProductionBoundaryHold.LocalAuditNotReady);
        if (untrustedClaim is null)
            return new(TelegramProductionBoundaryHold.ProductionApprovalMissing);
        if (untrustedClaim.Lane != lane ||
            !string.Equals(untrustedClaim.EventKey, eventKey, StringComparison.Ordinal) ||
            !string.Equals(untrustedClaim.DeviceId, localDeviceId, StringComparison.Ordinal) ||
            untrustedClaim.IssuedAtUtc.Offset != TimeSpan.Zero ||
            untrustedClaim.ExpiresAtUtc.Offset != TimeSpan.Zero ||
            nowUtc - untrustedClaim.IssuedAtUtc < TimeSpan.Zero ||
            nowUtc - untrustedClaim.IssuedAtUtc > TimeSpan.FromMinutes(2) ||
            untrustedClaim.ExpiresAtUtc <= nowUtc ||
            untrustedClaim.ExpiresAtUtc <= untrustedClaim.IssuedAtUtc ||
            untrustedClaim.ExpiresAtUtc - untrustedClaim.IssuedAtUtc > TimeSpan.FromMinutes(2))
            return new(TelegramProductionBoundaryHold.ApprovalBindingOrFreshnessInvalid);
        if (!untrustedClaim.ExplicitOneShotConsentClaimed)
            return new(TelegramProductionBoundaryHold.ExplicitConsentNotProven);
        if (!untrustedClaim.DurableReservationClaimed)
            return new(TelegramProductionBoundaryHold.DurableReservationNotProven);
        if (!untrustedClaim.OtherSendersOffClaimed)
            return new(TelegramProductionBoundaryHold.RemoteOwnershipNotProven);

        // All three booleans are assertions from an untrusted caller.
        // No independent consent issuer, OS-attested durable production outbox,
        // or cross-host owner authority has been implemented. NO success path.
        return new(TelegramProductionBoundaryHold.ClaimsNotIndependentlyAttested,
            LocallyReviewable:true);
    }
}

/// <summary>
/// Untrusted schema only; not an approval credential or live sender capability.
/// No issuer exists and no value of these fields authorizes sending.
/// </summary>
public sealed record TelegramProductionClaimForReview(
    TelegramNotificationLane Lane,
    string EventKey,
    string DeviceId,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    bool ExplicitOneShotConsentClaimed,
    bool DurableReservationClaimed,
    bool OtherSendersOffClaimed);

public enum TelegramProductionBoundaryHold
{
    UnsupportedPlatform,
    UntrustedClock,
    InvalidLane,
    InvalidEventKey,
    InvalidDeviceId,
    LocalNotificationDisabled,
    LocalAuditMissing,
    LocalAuditNotReady,
    ProductionApprovalMissing,
    ApprovalBindingOrFreshnessInvalid,
    ExplicitConsentNotProven,
    DurableReservationNotProven,
    RemoteOwnershipNotProven,
    ClaimsNotIndependentlyAttested
}

public sealed record TelegramProductionBoundaryInspection(
    TelegramProductionBoundaryHold Reason,
    bool LocallyReviewable=false)
{
    public bool ActualTelegramSendAuthorized => false;
}
