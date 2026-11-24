import Foundation

/// Mac-side, transport-free mirror of the Windows cross-host event review.
/// All inputs are caller-supplied MOCK observations, not authenticated
/// shared-store receipts. This code never enables Telegram sending.
enum MacTelegramSharedEventReview {
    enum EventState: Equatable {
        case reservedBeforeSend
        case sendOutcomeUnknown
        case deliveryExternallyConfirmed
    }

    enum Decision: Equatable {
        case invalidEvent
        case untrustedClock
        case localOrLeaseNotQualified
        case missingOrStaleSharedLedger
        case malformedOrConflictedSharedRecord
        case mockAtomicReserveCandidateOnly
        case existingReservationNeverReissue
        case outcomeUncertainManualReview
        case sharedDeliveredEventAlreadyHandled
    }

    struct LocalPreferences {
        let isSender: Bool
        let notificationsRequested: Bool
        let fiveHourRequested: Bool
        let weeklyRequested: Bool
    }

    struct Lease {
        let ownerDevice: String
        let fencingEpoch: Int64
        let expiresAtUTCUnixMs: Int64
    }

    struct EventRecord {
        let eventKey: String
        let laneKind: String
        let ownerDevice: String
        let fencingEpoch: Int64
        let revision: Int64
        let observedAtUTCUnixMs: Int64
        let state: EventState
    }

    struct SharedSnapshot {
        let authorityRevision: Int64
        let observedAtUTCUnixMs: Int64
        let record: EventRecord?
    }

    struct Inspection {
        let decision: Decision
        // Caller-supplied mock witnesses must NEVER authorize a real send,
        // actual event reservation, automatic retry, or sender transition.
        var actualSendAuthorized: Bool { false }
        var realSharedReservationAuthorized: Bool { false }
        var automaticRetryAllowed: Bool { false }
        var senderTransitionAuthorized: Bool { false }
    }

    static func inspect(
        preferences: LocalPreferences,
        lane: MacTelegramQuotaEventIdentity.Lane,
        localDevice: String,
        lease: Lease?,
        expectedEpoch: Int64,
        eventKey: String,
        shared: SharedSnapshot?,
        authorityNowUTCUnixMs now: Int64
    ) -> Inspection {
        func hold(_ reason: Decision) -> Inspection { Inspection(decision: reason) }

        // Bounded arithmetic prevents overflow and unintended clock behavior.
        let minTimestamp: Int64 = 1_577_836_800_000
        let maxTimestamp: Int64 = 4_133_980_800_000
        guard (minTimestamp...maxTimestamp).contains(now) else {
            return hold(.untrustedClock)
        }
        guard validEventKey(eventKey) else { return hold(.invalidEvent) }
        let requested = switch lane {
        case .fiveHour: preferences.fiveHourRequested
        case .weekly: preferences.weeklyRequested
        }
        guard preferences.isSender,
              preferences.notificationsRequested, requested,
              validDevice(localDevice),
              let lease,
              validDevice(lease.ownerDevice),
              lease.ownerDevice == localDevice,
              expectedEpoch > 0,
              expectedEpoch == lease.fencingEpoch,
              lease.expiresAtUTCUnixMs > now,
              lease.expiresAtUTCUnixMs - now <= 60_000
        else { return hold(.localOrLeaseNotQualified) }

        guard let shared,
              shared.authorityRevision > 0,
              shared.observedAtUTCUnixMs <= now,
              shared.observedAtUTCUnixMs >= now - 30_000
        else { return hold(.missingOrStaleSharedLedger) }

        guard let existing = shared.record else {
            return hold(.mockAtomicReserveCandidateOnly)
        }
        guard existing.eventKey == eventKey,
              existing.laneKind == lane.canonicalKind,
              validDevice(existing.ownerDevice),
              existing.fencingEpoch > 0,
              existing.fencingEpoch <= expectedEpoch,
              existing.revision > 0,
              existing.revision <= shared.authorityRevision,
              existing.observedAtUTCUnixMs > 0,
              existing.observedAtUTCUnixMs <= now
        else { return hold(.malformedOrConflictedSharedRecord) }

        switch existing.state {
        case .reservedBeforeSend: return hold(.existingReservationNeverReissue)
        case .sendOutcomeUnknown: return hold(.outcomeUncertainManualReview)
        case .deliveryExternallyConfirmed:
            return hold(.sharedDeliveredEventAlreadyHandled)
        }
    }

    private static func validEventKey(_ value: String) -> Bool {
        value.utf8.count == 64 && value.utf8.allSatisfy {
            (48...57).contains(Int($0)) || (97...102).contains(Int($0))
        }
    }

    private static func validDevice(_ value: String) -> Bool {
        (1...48).contains(value.utf8.count) && value.utf8.allSatisfy {
            (48...57).contains(Int($0)) ||
            (65...90).contains(Int($0)) ||
            (97...122).contains(Int($0)) || $0 == 45
        }
    }
}
