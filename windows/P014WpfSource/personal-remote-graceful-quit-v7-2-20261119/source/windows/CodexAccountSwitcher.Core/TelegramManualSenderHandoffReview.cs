namespace CodexAccountSwitcher.Core;

/// <summary>
/// READ-ONLY review of an operator-led Windows/Mac sender handoff.
/// Inputs describe observed facts but are NOT independently authenticated.
/// Even complete-looking input is HOLD until shared cross-host authority
/// and actual verifiable outbox handoff exist. No local settings are changed.
/// </summary>
public static class TelegramManualSenderHandoffReview
{
    public static TelegramManualHandoffInspection Inspect(
        TelegramManualHandoffEvidence? evidence,
        DateTimeOffset nowUtc)
    {
        if (nowUtc.Offset!=TimeSpan.Zero)
            return new(TelegramManualHandoffHold.UntrustedClock);
        if (evidence is null)
            return new(TelegramManualHandoffHold.EvidenceMissing);
        if (evidence.Source==evidence.Target ||
            !Enum.IsDefined(evidence.Source) || !Enum.IsDefined(evidence.Target))
            return new(TelegramManualHandoffHold.InvalidDirection);
        if (evidence.SourceObservedAtUtc.Offset!=TimeSpan.Zero ||
            evidence.TargetObservedAtUtc.Offset!=TimeSpan.Zero ||
            evidence.SourceObservedAtUtc>nowUtc ||
            evidence.TargetObservedAtUtc>nowUtc ||
            nowUtc-evidence.SourceObservedAtUtc>TimeSpan.FromMinutes(2) ||
            nowUtc-evidence.TargetObservedAtUtc>TimeSpan.FromMinutes(2))
            return new(TelegramManualHandoffHold.StaleOrUntrustedObservations);
        if (!evidence.SourceSenderOffObserved)
            return new(TelegramManualHandoffHold.OldSenderNotConfirmedOff);
        if (!evidence.SourceOutboxQuiescentObserved)
            return new(TelegramManualHandoffHold.UnsettledSourceEvents);
        if (!evidence.TargetSenderOffObserved)
            return new(TelegramManualHandoffHold.TargetAlreadyEnabled);
        if (evidence.PreviousFencingEpoch<=0 ||
            evidence.ProposedFencingEpoch<=evidence.PreviousFencingEpoch)
            return new(TelegramManualHandoffHold.FencingEpochNotAdvanced);
        if (!evidence.SharedEventDedupeClaimed)
            return new(TelegramManualHandoffHold.SharedDedupUnavailable);
        if (!evidence.OperatorApprovalClaimed)
            return new(TelegramManualHandoffHold.OperatorApprovalNotProven);

        // A remote 'off' flag, a shared-ledger flag, or claimed operator
        // approval supplied by this caller is NOT cryptographically/OS
        // authenticated. Do not enable either sender on this report.
        return new(TelegramManualHandoffHold.ExternalAttestationsUnverified,
            ChecklistLooksComplete:true);
    }
}

public enum TelegramSenderPlatform
{
    Windows=0,
    Mac=1
}

public sealed record TelegramManualHandoffEvidence(
    TelegramSenderPlatform Source,
    TelegramSenderPlatform Target,
    DateTimeOffset SourceObservedAtUtc,
    DateTimeOffset TargetObservedAtUtc,
    bool SourceSenderOffObserved,
    bool SourceOutboxQuiescentObserved,
    bool TargetSenderOffObserved,
    long PreviousFencingEpoch,
    long ProposedFencingEpoch,
    bool SharedEventDedupeClaimed,
    bool OperatorApprovalClaimed);

public enum TelegramManualHandoffHold
{
    UntrustedClock,
    EvidenceMissing,
    InvalidDirection,
    StaleOrUntrustedObservations,
    OldSenderNotConfirmedOff,
    UnsettledSourceEvents,
    TargetAlreadyEnabled,
    FencingEpochNotAdvanced,
    SharedDedupUnavailable,
    OperatorApprovalNotProven,
    ExternalAttestationsUnverified
}

public sealed record TelegramManualHandoffInspection(
    TelegramManualHandoffHold Reason,
    bool ChecklistLooksComplete=false)
{
    public bool SenderTransitionAuthorized=>false;
    public bool TelegramSendAuthorized=>false;
}
