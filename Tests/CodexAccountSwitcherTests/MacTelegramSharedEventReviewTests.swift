import Foundation
import Testing
@testable import CodexAccountSwitcher

/// Compile/run on macOS when native Swift toolchain is available.
/// These are non-sending contract tests, not integration with a shared server.
struct MacTelegramSharedEventReviewTests {
    private let now: Int64 = 1_794_090_100_000
    private let key = String(repeating: "a", count: 64)
    private let preferences = MacTelegramSharedEventReview.LocalPreferences(
        isSender: true, notificationsRequested: true,
        fiveHourRequested: true, weeklyRequested: true)

    private func sample(
        owner: String = "MAC-MINI",
        epoch: Int64 = 9
    ) -> MacTelegramSharedEventReview.Lease {
        .init(ownerDevice: owner, fencingEpoch: epoch,
              expiresAtUTCUnixMs: now + 20_000)
    }

    private func ledger(
        state: MacTelegramSharedEventReview.EventState? = nil,
        owner: String = "WINDOWS-ONE",
        epoch: Int64 = 8
    ) -> MacTelegramSharedEventReview.SharedSnapshot {
        .init(authorityRevision: 12, observedAtUTCUnixMs: now,
              record: state.map {
                  .init(eventKey: key, laneKind: "FIVE_HOUR_100",
                        ownerDevice: owner, fencingEpoch: epoch,
                        revision: 11, observedAtUTCUnixMs: now - 1_000,
                        state: $0)
              })
    }

    private func inspect(
        eventKey: String? = nil,
        lease: MacTelegramSharedEventReview.Lease? = nil,
        shared: MacTelegramSharedEventReview.SharedSnapshot? = nil
    ) -> MacTelegramSharedEventReview.Inspection {
        MacTelegramSharedEventReview.inspect(
            preferences: preferences, lane: .fiveHour,
            localDevice: "MAC-MINI", lease: lease ?? sample(),
            expectedEpoch: 9, eventKey: eventKey ?? key,
            shared: shared ?? ledger(), authorityNowUTCUnixMs: now)
    }

    @Test func noRecordOnlyQualifiesForMockReservation() {
        let result = inspect()
        #expect(result.decision == .mockAtomicReserveCandidateOnly)
        #expect(!result.actualSendAuthorized)
        #expect(!result.realSharedReservationAuthorized)
        #expect(!result.automaticRetryAllowed)
        #expect(!result.senderTransitionAuthorized)
    }

    @Test func existingWindowsReservationCannotBeReissuedAfterMacHandoff() {
        #expect(inspect(shared: ledger(state: .reservedBeforeSend)).decision ==
            .existingReservationNeverReissue)
        #expect(inspect(shared: ledger(state: .sendOutcomeUnknown)).decision ==
            .outcomeUncertainManualReview)
        #expect(inspect(shared: ledger(state: .deliveryExternallyConfirmed)).decision ==
            .sharedDeliveredEventAlreadyHandled)
    }

    @Test func staleLeaseAndWrongOwnerAreRefused() {
        #expect(inspect(lease: sample(owner: "WINDOWS-ONE")).decision ==
            .localOrLeaseNotQualified)
        #expect(inspect(lease: sample(epoch: 8)).decision ==
            .localOrLeaseNotQualified)
        #expect(inspect(lease: .init(ownerDevice: "MAC-MINI",
                fencingEpoch: 9, expiresAtUTCUnixMs: now)).decision ==
            .localOrLeaseNotQualified)
    }

    @Test func sharedLedgerSnapshotMustBeFresh() {
        #expect(inspect(shared: .init(authorityRevision: 0,
            observedAtUTCUnixMs: now, record: nil)).decision ==
            .missingOrStaleSharedLedger)
        #expect(inspect(shared: .init(authorityRevision: 3,
            observedAtUTCUnixMs: now - 31_000, record: nil)).decision ==
            .missingOrStaleSharedLedger)
    }

    @Test func conflictingRecordAndEventKeyAreRefused() {
        #expect(inspect(eventKey: "INVALID").decision == .invalidEvent)
        #expect(inspect(shared: .init(authorityRevision: 12,
            observedAtUTCUnixMs: now,
            record: .init(eventKey: String(repeating: "b", count: 64),
                          laneKind: "FIVE_HOUR_100",
                          ownerDevice: "WINDOWS-ONE",
                          fencingEpoch: 8, revision: 11,
                          observedAtUTCUnixMs: now,
                          state: .reservedBeforeSend))).decision ==
            .malformedOrConflictedSharedRecord)
    }

    @Test func evenDeliveryConfirmationNeverGrantsAutomaticTelegramSend() {
        let result = inspect(shared: ledger(state: .deliveryExternallyConfirmed))
        #expect(!result.actualSendAuthorized)
        #expect(!result.automaticRetryAllowed)
    }
}
