namespace CodexAccountSwitcher.Core;

/// <summary>
/// Permanently unarmed rehearsal of exactly-once approval consumption.
/// A synthetic one-use approval is consumed ONLY AFTER a protected native
/// CREATE_NEW claim is written and flushed to a designated OS-TEMP fixture.
/// Neither this approval nor local Sender settings can enable real delivery.
/// </summary>
public static class WindowsTelegramOfflineApprovalConsumption
{
    public static WindowsApprovalConsumptionInspection Simulate(
        WindowsTelegramSameHandleReservationTrial? offlineTrial,
        TelegramNotificationPreferences? preferences,
        WindowsSenderReadinessInspection? localAudit,
        TelegramNotificationLane lane,
        string? eventKey,
        string? localDeviceId,
        TelegramProductionClaimForReview? untrustedClaim,
        TelegramSyntheticOneShotApproval? syntheticApproval,
        DateTimeOffset nowUtc)
    {
        if (!OperatingSystem.IsWindows() || offlineTrial is null ||
            nowUtc.Offset!=TimeSpan.Zero)
            return Result(WindowsApprovalConsumptionStatus.PreconditionsUnavailable);
        if (syntheticApproval is null ||
            syntheticApproval.ExpiresAtUtc.Offset!=TimeSpan.Zero ||
            syntheticApproval.ExpiresAtUtc<=nowUtc ||
            syntheticApproval.ExpiresAtUtc-nowUtc>TimeSpan.FromMinutes(2) ||
            syntheticApproval.Lane!=lane ||
            !string.Equals(syntheticApproval.EventKey,eventKey,
                StringComparison.Ordinal) ||
            !string.Equals(syntheticApproval.DeviceId,localDeviceId,
                StringComparison.Ordinal))
            return Result(WindowsApprovalConsumptionStatus.SyntheticApprovalInvalid);
        if (syntheticApproval.IsConsumed)
            return Result(WindowsApprovalConsumptionStatus.SyntheticApprovalAlreadyConsumed);

        // Crucially: never consume in-memory test consent before a
        // persistent disk reservation succeeds. No real send exists here.
        var disk=offlineTrial.ReserveForOfflineTest(
            preferences,localAudit,lane,eventKey,localDeviceId,
            untrustedClaim,nowUtc);
        if (disk.Status!=WindowsSameHandleReservationStatus.FixtureClaimOnly)
            return new(WindowsApprovalConsumptionStatus.NoNewFixtureClaim,
                false,disk.Status);
        if (!syntheticApproval.TryConsume())
            return new(WindowsApprovalConsumptionStatus.ClaimRecordedConsentUncertain,
                false,disk.Status);
        return new(WindowsApprovalConsumptionStatus.MockConsentConsumedAfterClaim,
            true,disk.Status);
    }

    private static WindowsApprovalConsumptionInspection Result(
        WindowsApprovalConsumptionStatus status) => new(status,false);
}

public enum WindowsApprovalConsumptionStatus
{
    PreconditionsUnavailable,
    SyntheticApprovalInvalid,
    SyntheticApprovalAlreadyConsumed,
    NoNewFixtureClaim,
    ClaimRecordedConsentUncertain,
    MockConsentConsumedAfterClaim
}

public sealed record WindowsApprovalConsumptionInspection(
    WindowsApprovalConsumptionStatus Status,
    bool SyntheticConsentConsumed,
    WindowsSameHandleReservationStatus? DiskReservationStatus=null)
{
    public bool RealUserApprovalGranted=>false;
    public bool ProductionReservationAuthorized=>false;
    public bool TelegramSendAuthorized=>false;
}
