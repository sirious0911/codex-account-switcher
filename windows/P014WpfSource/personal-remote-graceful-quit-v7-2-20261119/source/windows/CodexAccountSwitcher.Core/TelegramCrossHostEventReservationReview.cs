namespace CodexAccountSwitcher.Core;

/// <summary>
/// Transport-free, non-authorizing cross-host event-ownership review.
/// All snapshots are UNTRUSTED CALLER INPUTS. A real implementation needs
/// authenticated, linearizable CAS/lease storage, server clock, and evidence
/// of actual delivery. No API in this class sends messages or reserves events.
/// </summary>
public static class TelegramCrossHostEventReservationReview
{
    private static readonly TimeSpan MaxSnapshotAge=TimeSpan.FromSeconds(30);

    public static TelegramCrossHostEventInspection Inspect(
        TelegramNotificationPreferences? settings,
        TelegramNotificationLane lane,
        string? deviceId,
        TelegramSharedLeaseWitness? lease,
        long proposedFencingEpoch,
        string? eventKey,
        TelegramSharedEventSnapshot? snapshot,
        DateTimeOffset authorityNowUtc)
    {
        if(authorityNowUtc.Offset!=TimeSpan.Zero ||
            authorityNowUtc.Year is <2020 or >2100)
            return Hold(TelegramCrossHostEventHold.UntrustedClock);

        if(lane is not (TelegramNotificationLane.FiveHourRecovery or
            TelegramNotificationLane.WeeklyRecovery) ||
            !ValidKey(eventKey))
            return Hold(TelegramCrossHostEventHold.InvalidEvent);

        // Reuse the existing 5H/7D preferences and mock lease checks.
        var handoff=TelegramCrossHostHandoffReview.Inspect(
            settings,lane,deviceId,lease,proposedFencingEpoch,authorityNowUtc);
        if(!handoff.WouldPassSharedLeaseSimulation ||
            handoff.Reason!=TelegramHandoffHoldReason.MockLeaseWouldQualify)
            return Hold(TelegramCrossHostEventHold.LocalOrLeaseNotQualified);

        if(snapshot is null || snapshot.AuthorityRevision<=0 ||
            snapshot.ObservedAtUtc.Offset!=TimeSpan.Zero ||
            snapshot.ObservedAtUtc>authorityNowUtc ||
            authorityNowUtc-snapshot.ObservedAtUtc>MaxSnapshotAge)
            return Hold(TelegramCrossHostEventHold.MissingOrStaleSharedLedger);

        if(snapshot.Record is not { } existing)
            return new(TelegramCrossHostEventHold.MockAtomicReserveCandidateOnly,
                CouldTryMockAtomicReserve:true);

        // A record in a shared ledger is an immutable event-level claim:
        // an ownership transfer MUST NOT reset the event deduplication history.
        if(existing.EventKey!=eventKey || existing.Lane!=lane ||
            existing.Revision<=0 ||
            existing.Revision>snapshot.AuthorityRevision ||
            !ValidDevice(existing.OwnerDevice) ||
            existing.FencingEpoch<=0 ||
            existing.FencingEpoch>proposedFencingEpoch ||
            existing.ObservedAtUtc.Offset!=TimeSpan.Zero ||
            existing.ObservedAtUtc>authorityNowUtc ||
            !Enum.IsDefined(existing.State) ||
            existing.State==TelegramSharedEventState.NoRecord)
            return Hold(TelegramCrossHostEventHold.MalformedOrConflictedSharedRecord);

        return existing.State switch
        {
            TelegramSharedEventState.ReservedBeforeSend =>
                Hold(TelegramCrossHostEventHold.ExistingReservationNeverReissue),
            TelegramSharedEventState.SendOutcomeUnknown =>
                Hold(TelegramCrossHostEventHold.OutcomeUncertainManualReview),
            TelegramSharedEventState.DeliveryExternallyConfirmed =>
                Hold(TelegramCrossHostEventHold.SharedDeliveredEventAlreadyHandled),
            _ => Hold(TelegramCrossHostEventHold.MalformedOrConflictedSharedRecord)
        };
    }

    private static bool ValidKey(string? value)=>
        value is {Length:64} &&
        value.All(c=>c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool ValidDevice(string? value)=>
        value is {Length:>0 and <=48} &&
        value.All(c=>c is >= 'A' and <= 'Z' or >= 'a' and <= 'z'
            or >= '0' and <= '9' or '-');

    private static TelegramCrossHostEventInspection Hold(
        TelegramCrossHostEventHold reason)=>new(reason);
}

public enum TelegramSharedEventState
{
    NoRecord=0,
    ReservedBeforeSend=1,
    SendOutcomeUnknown=2,
    DeliveryExternallyConfirmed=3
}

public sealed record TelegramSharedEventRecord(
    string EventKey,
    TelegramNotificationLane Lane,
    string OwnerDevice,
    long FencingEpoch,
    TelegramSharedEventState State,
    long Revision,
    DateTimeOffset ObservedAtUtc);

/// <summary>
/// A hypothetical linearizable shared store snapshot, NOT a proof that one exists.
/// The owner/device fields and revisions are unverified caller assertions.
/// </summary>
public sealed record TelegramSharedEventSnapshot(
    long AuthorityRevision,
    DateTimeOffset ObservedAtUtc,
    TelegramSharedEventRecord? Record);

public enum TelegramCrossHostEventHold
{
    UntrustedClock,
    InvalidEvent,
    LocalOrLeaseNotQualified,
    MissingOrStaleSharedLedger,
    MalformedOrConflictedSharedRecord,
    MockAtomicReserveCandidateOnly,
    ExistingReservationNeverReissue,
    OutcomeUncertainManualReview,
    SharedDeliveredEventAlreadyHandled
}

public sealed record TelegramCrossHostEventInspection(
    TelegramCrossHostEventHold Reason,
    bool CouldTryMockAtomicReserve=false)
{
    public bool ActualSendAuthorized=>false;
    public bool RealSharedReservationAuthorized=>false;
    public bool AutomaticRetryAllowed=>false;
    public bool SenderTransitionAuthorized=>false;
}
