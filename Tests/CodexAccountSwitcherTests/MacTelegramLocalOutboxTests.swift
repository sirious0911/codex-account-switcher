import Foundation
import Testing
@testable import CodexAccountSwitcher

/// Offline-only state tests. No Keychain, bot, URLSession or actual send.
@MainActor
struct MacTelegramLocalOutboxTests {
    private let account =
        "9cb32632c9c9e4460e7a6c44d4de4a5c79b97f934af458d40ebbac4634d95f1c"
    private let previous: Int64 = 1_800_000_000_000
    private let next: Int64 = 1_800_018_000_000

    private func candidate(_ lane: MacTelegramQuotaEventIdentity.Lane = .fiveHour,
                           confirmed: Bool = true,
                           nextReset: Int64 = 1_800_018_000_000)
        -> MacTelegramLocalOutbox.ConfirmedRecovery {
        .init(lane: lane, accountIdSHA256: account,
              previousResetAtUnixMs: previous,
              currentResetAtUnixMs: nextReset,
              observedAtUnixMs: previous + 60_000,
              confirmedByTwoObservations: confirmed)
    }

    private func fixture() -> URL {
        FileManager.default.temporaryDirectory
            .appending(path: "mac-telegram-local-outbox-\(UUID().uuidString)")
    }

    @Test func singleMacExecutorStagedReservationAndCompletedMockReceipt()
    throws {
        let root = fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        do {
            let ledger = try MacTelegramLocalOutbox(directory: root)
            let event = try ledger.stage(candidate())
            #expect(event == MacTelegramQuotaEventIdentity.build(
                lane: .fiveHour, accountIdSHA256: account,
                previousResetAtUnixMs: previous,
                currentResetAtUnixMs: next))
            #expect(try ledger.stage(candidate()) == event)
            #expect(try ledger.status(event) == .pending)
            #expect(try !ledger.hasUnresolvedSend())

            // A synthetic transport is NOT invoked before this marker exists.
            try ledger.reserveBeforeSend(event)
            #expect(try ledger.status(event) == .reservedBeforeSend)
            #expect(try ledger.hasUnresolvedSend())
            #expect(throws: MacTelegramLocalOutbox.OutboxError.notPending) {
                try ledger.reserveBeforeSend(event)
            }
            #expect(throws: MacTelegramLocalOutbox.OutboxError.invalidEvent) {
                try ledger.acknowledgeCompletedLocalSend(event, messageId: 0)
            }
            // Represents a completed, local, fake process with a typed ACK.
            try ledger.acknowledgeCompletedLocalSend(event, messageId: 301)
            #expect(try ledger.status(event) == .acknowledged)
            #expect(try !ledger.hasUnresolvedSend())
            #expect(throws: MacTelegramLocalOutbox.OutboxError.notReserved) {
                try ledger.acknowledgeCompletedLocalSend(event, messageId: 301)
            }
        }
        // The process-owned lock was released, but the irreversible marker
        // persists and can never authorize a repeated external attempt.
        let restarted = try MacTelegramLocalOutbox(directory: root)
        let event = try restarted.stage(candidate())
        #expect(try restarted.status(event) == .acknowledged)
        #expect(throws: MacTelegramLocalOutbox.OutboxError.notPending) {
            try restarted.reserveBeforeSend(event)
        }
        let later = try restarted.stage(candidate(.weekly))
        #expect(later != event)
        try restarted.reserveBeforeSend(later)
        try restarted.markUncertainNoRetry(later)
        #expect(try restarted.status(later) == .uncertainNoRetry)
        #expect(try restarted.hasUnresolvedSend())
    }

    @Test func concurrentMacExecutorAndCrashReservationFailClosed() throws {
        let root = fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        var original: String?
        do {
            let first = try MacTelegramLocalOutbox(directory: root)
            #expect(throws: MacTelegramLocalOutbox.OutboxError.anotherExecutorActive) {
                _ = try MacTelegramLocalOutbox(directory: root)
            }
            let event = try first.stage(candidate())
            original = event
            try first.reserveBeforeSend(event)
        }
        // The prior owner has exited without an ACK. No call to a real Bot
        // API is permitted by this test, and no restart retry is possible.
        let reopened = try MacTelegramLocalOutbox(directory: root)
        let event = try #require(original)
        #expect(try reopened.status(event) == .reservedBeforeSend)
        #expect(try reopened.hasUnresolvedSend())
        #expect(throws: MacTelegramLocalOutbox.OutboxError.notPending) {
            try reopened.reserveBeforeSend(event)
        }
        let nextEvent = try reopened.stage(candidate(.weekly))
        #expect(throws: MacTelegramLocalOutbox.OutboxError.manualReviewRequired) {
            try reopened.reserveBeforeSend(nextEvent)
        }
        #expect(try reopened.status(nextEvent) == .pending)
    }

    @Test func unconfirmedOrMalformedEventsNeverReachDisk() throws {
        let root = fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        let outbox = try MacTelegramLocalOutbox(directory: root)
        #expect(throws: MacTelegramLocalOutbox.OutboxError.invalidEvent) {
            _ = try outbox.stage(candidate(confirmed: false))
        }
        #expect(throws: MacTelegramLocalOutbox.OutboxError.invalidEvent) {
            _ = try outbox.stage(candidate(nextReset: previous + 1))
        }
        #expect(try !outbox.hasUnresolvedSend())
        #expect(throws: MacTelegramLocalOutbox.OutboxError.invalidEvent) {
            _ = try outbox.status("NOT_A_SHA256")
        }
    }


    @Test func syntheticCoordinatorReservesBeforeMockAndNeverRetries() async throws {
        let root = fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        let ledger = try MacTelegramLocalOutbox(directory: root)
        let coordinator = MacTelegramOfflineSendCoordinator(outbox: ledger)
        let key = try ledger.stage(candidate())
        var calls = 0
        let first = await coordinator.simulateOnce(
            eventKey: key, liveConsent: { true },
            mockTransport: { suppliedKey in
                calls += 1
                #expect(suppliedKey == key)
                #expect((try? ledger.status(key)) == .reservedBeforeSend)
                return 712
            })
        #expect(first == .acknowledgedByMock(712))
        #expect(calls == 1)
        #expect(try ledger.status(key) == .acknowledged)

        let replay = await coordinator.simulateOnce(
            eventKey: key, liveConsent: { true },
            mockTransport: { _ in
                calls += 1
                return 713
            })
        #expect(replay == .blocked)
        #expect(calls == 1)
    }

    @Test func fakeTransportTimeoutAndConsentOffStayFailClosed() async throws {
        let root = fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        let ledger = try MacTelegramLocalOutbox(directory: root)
        let coordinator = MacTelegramOfflineSendCoordinator(outbox: ledger)
        let key = try ledger.stage(candidate())
        var attempted = false
        let off = await coordinator.simulateOnce(
            eventKey: key, liveConsent: { false },
            mockTransport: { _ in attempted = true; return 512 })
        #expect(off == .blocked)
        #expect(!attempted)
        #expect(try ledger.status(key) == .pending)

        let lost = await coordinator.simulateOnce(
            eventKey: key, liveConsent: { true },
            mockTransport: { _ in
                #expect((try? ledger.status(key)) == .reservedBeforeSend)
                throw SyntheticTransportFailure.responseLost
            })
        #expect(lost == .uncertainNoRetry)
        #expect(try ledger.status(key) == .uncertainNoRetry)
        #expect(try ledger.hasUnresolvedSend())

        let other = try ledger.stage(candidate(.weekly))
        let blocked = await coordinator.simulateOnce(
            eventKey: other, liveConsent: { true },
            mockTransport: { _ in
                attempted = true
                return 513
            })
        #expect(blocked == .blocked)
        #expect(!attempted)
        #expect(try ledger.status(other) == .pending)
    }

    private enum SyntheticTransportFailure: Error {
        case responseLost
    }

    @Test func corruptPendingMarkerNeverRecreatedOrReplayed() throws {
        let root = fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        let outbox = try MacTelegramLocalOutbox(directory: root)
        let event = try outbox.stage(candidate())
        let marker = root.appending(path: event + ".pending")
        try Data("CORRUPTED-PENDING".utf8).write(to: marker)
        #expect(throws: MacTelegramLocalOutbox.OutboxError.corruptLedger) {
            _ = try outbox.status(event)
        }
        #expect(throws: MacTelegramLocalOutbox.OutboxError.corruptLedger) {
            _ = try outbox.stage(candidate())
        }
        #expect(throws: MacTelegramLocalOutbox.OutboxError.corruptLedger) {
            try outbox.reserveBeforeSend(event)
        }
        let another = try outbox.stage(candidate(.weekly))
        #expect(throws: MacTelegramLocalOutbox.OutboxError.corruptLedger) {
            try outbox.reserveBeforeSend(another)
        }
    }
}
