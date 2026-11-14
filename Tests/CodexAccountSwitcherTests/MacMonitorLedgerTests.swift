import Foundation
import SwitcherCore
import Testing
@testable import CodexAccountSwitcher

@MainActor
struct MacMonitorLedgerTests {
    private let t0: TimeInterval = 1_793_500_000

    private func sample(accountID: String = "MOCK_ACCOUNT", plan: String? = "plus",
                        allowed: Bool? = true, credits: Int? = 1,
                        fiveUsed: Int = 75, fiveReset: TimeInterval? = nil,
                        observed: TimeInterval) throws -> CodexMonitorObservation {
        let account = AccountProfile(id: UUID(), displayName: "Mock",
                                     email: "mock@example.test", accountID: accountID,
                                     createdAt: Date(timeIntervalSince1970: t0))
        let usage = WeeklyUsage(
            remainingPercent: 40,
            resetsAt: Date(timeIntervalSince1970: t0 + 7 * 86_400),
            fiveHourRemainingPercent: 100 - fiveUsed,
            fiveHourResetsAt: Date(timeIntervalSince1970: fiveReset ?? t0 + 18_000),
            resetCreditsRemaining: credits
        )
        return try #require(CodexMonitorPayloadEncoder.encode(
            profile: account, usage: usage, planType: plan,
            ordinaryUsageAllowed: allowed,
            observedAt: Date(timeIntervalSince1970: observed)))
    }

    @Test func confirmsCreditIncreaseAndRestoresExactlyOnceAfterRestart() throws {
        let root = FileManager.default.temporaryDirectory
            .appending(path: "mac-monitor-\(UUID())")
        defer { try? FileManager.default.removeItem(at: root) }
        let ledger = MacMonitorLedger(directory: root)
        #expect(try ledger.record(sample(observed: t0)) == 0)
        #expect(try ledger.record(sample(credits: 2, observed: t0 + 60)) == 0)
        #expect(try ledger.record(sample(credits: 2, observed: t0 + 120)) == 1)
        let history = ledger.history()
        #expect(history.count == 1)
        #expect(history[0].kind == "CREDIT_INCREASE")
        #expect(history[0].lane == "credits")
        #expect(history[0].mockCompleted)
        #expect(history[0].accountReference.count == 8)
        // Exact SHA256 key from the existing .NET UsageEventState.Create contract.
        #expect(history[0].key == "afad3a31eae2419a620d0d32ff6ae382d1b516dc1c37396e88582486d22530d6")
        let restarted = MacMonitorLedger(directory: root)
        #expect(restarted.history() == history)
        #expect(try restarted.record(sample(credits: 2, observed: t0 + 180)) == 0)
        #expect(try restarted.record(sample(credits: 1, observed: t0 + 90)) == 0)
        #expect(restarted.history() == history)
    }

    @Test func earlyRecoveryMatchesWindowsEventKeyAndTwoReadingConfirmation() throws {
        let root = FileManager.default.temporaryDirectory
            .appending(path: "mac-monitor-\(UUID())")
        defer { try? FileManager.default.removeItem(at: root) }
        let ledger = MacMonitorLedger(directory: root)
        #expect(try ledger.record(sample(observed: t0)) == 0)
        #expect(try ledger.record(sample(fiveUsed: 0, fiveReset: t0 + 36_000,
                                         observed: t0 + 60)) == 0)
        #expect(try ledger.record(sample(fiveUsed: 0, fiveReset: t0 + 36_000,
                                         observed: t0 + 120)) == 1)
        let event = try #require(ledger.history().first)
        #expect(event.kind == "EARLY_RECOVERY_SUSPECTED")
        #expect(event.lane == "five")
        #expect(event.key == "7b2c6d5ed8982e2537df32cc917e6df187907beed72865be5a2f544f86de4119")
    }

    @Test func unknownPlanSuppressesEventAndKnownPlanStaysAccountSpecific() throws {
        let root = FileManager.default.temporaryDirectory
            .appending(path: "mac-monitor-\(UUID())")
        defer { try? FileManager.default.removeItem(at: root) }
        let ledger = MacMonitorLedger(directory: root)
        #expect(try ledger.record(sample(plan: nil, observed: t0)) == 0)
        #expect(try ledger.record(sample(plan: nil, credits: 2, observed: t0 + 60)) == 0)
        #expect(try ledger.record(sample(plan: nil, credits: 2, observed: t0 + 120)) == 0)
        #expect(ledger.history().isEmpty)

        #expect(try ledger.record(sample(accountID: "MOCK_SECOND", observed: t0)) == 0)
        #expect(try ledger.record(sample(accountID: "MOCK_SECOND", credits: 2, observed: t0 + 60)) == 0)
        #expect(try ledger.record(sample(accountID: "MOCK_SECOND", credits: 2, observed: t0 + 120)) == 1)
        #expect(ledger.history().count == 1)
        #expect(ledger.history(limit: 0).isEmpty)
    }

    @Test func unconfirmedChangeAndStaleSequenceNeverNotify() throws {
        let root = FileManager.default.temporaryDirectory
            .appending(path: "mac-monitor-\(UUID())")
        defer { try? FileManager.default.removeItem(at: root) }
        let ledger = MacMonitorLedger(directory: root)
        #expect(try ledger.record(sample(observed: t0)) == 0)
        #expect(try ledger.record(sample(credits: 2, observed: t0 + 60)) == 0)
        #expect(try ledger.record(sample(credits: 1, observed: t0 + 120)) == 0)
        #expect(ledger.history().isEmpty)
        #expect(try ledger.record(sample(credits: 2, observed: t0 + 25 * 3_600)) == 0)
        #expect(ledger.history().isEmpty)
    }

    @Test func corruptPersistedStateDoesNotSilentlyEraseEventHistory() throws {
        let root = FileManager.default.temporaryDirectory
            .appending(path: "mac-monitor-\(UUID())")
        defer { try? FileManager.default.removeItem(at: root) }
        let ledger = MacMonitorLedger(directory: root)
        #expect(try ledger.record(sample(observed: t0)) == 0)
        let folder = try #require(FileManager.default.contentsOfDirectory(
            at: root, includingPropertiesForKeys: nil).first)
        try Data("not a valid JSON state".utf8).write(to: folder.appending(path: "state.json"))
        #expect(throws: DecodingError.self) {
            try ledger.record(sample(credits: 2, observed: t0 + 60))
        }
        #expect(ledger.history().isEmpty)
    }
}
