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
                        weekUsed: Int = 60, weekReset: TimeInterval? = nil,
                        observed: TimeInterval) throws -> CodexMonitorObservation {
        let account = AccountProfile(id: UUID(), displayName: "Mock",
                                     email: "mock@example.test", accountID: accountID,
                                     createdAt: Date(timeIntervalSince1970: t0))
        let usage = WeeklyUsage(
            remainingPercent: 100 - weekUsed,
            resetsAt: Date(timeIntervalSince1970: weekReset ?? t0 + 7 * 86_400),
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

    @Test func onlyTwiceConfirmedScheduledRecoveryYieldsFiveHourCandidate()
    throws {
        let root = FileManager.default.temporaryDirectory
            .appending(path: "mac-telegram-five-source-\(UUID())")
        defer { try? FileManager.default.removeItem(at: root) }
        let monitor = MacMonitorLedger(directory: root.appending(path: "monitor"))
        let outbox = try MacTelegramLocalOutbox(
            directory: root.appending(path: "outbox"))

        // First trusted reading is below full recovery, taken before reset.
        let before = try monitor.recordWithTelegramCandidates(sample(
            fiveUsed: 75, observed: t0 + 17_900))
        #expect(before.eventCount == 0 && before.telegramCandidates.isEmpty)
        let first = try monitor.recordWithTelegramCandidates(sample(
            fiveUsed: 0, fiveReset: t0 + 36_000,
            observed: t0 + 18_060))
        #expect(first.telegramCandidates.isEmpty)
        let second = try monitor.recordWithTelegramCandidates(sample(
            fiveUsed: 0, fiveReset: t0 + 36_000,
            observed: t0 + 18_120))
        #expect(second.eventCount == 1)
        let recovery = try #require(second.telegramCandidates.first)
        #expect(second.telegramCandidates.count == 1)
        #expect(recovery.lane.canonicalKind == "FIVE_HOUR_100")
        #expect(recovery.accountIdSHA256 ==
            "732287f049fae3bb4ca0ad762e16b61b78e1e129c52ab15c0a6ac33860cecca3")
        #expect(recovery.previousResetAtUnixMs == Int64((t0 + 18_000) * 1000))
        #expect(recovery.currentResetAtUnixMs == Int64((t0 + 36_000) * 1000))
        #expect(recovery.confirmedByTwoObservations)

        // Simulate a crash before the M1 local outbox could be staged.
        // Persisted confirmed candidates must be recoverable from the same
        // atomic monitor state as the consumed monitor event identity.
        let resumedMonitor = MacMonitorLedger(
            directory: root.appending(path: "monitor"))
        let rehydrated = try resumedMonitor.persistedTelegramCandidates(
            accountIdSHA256: recovery.accountIdSHA256,
            nowMilliseconds: Int64((t0 + 18_140) * 1000))
        #expect(rehydrated.count == 1)
        #expect(rehydrated[0].lane.canonicalKind == "FIVE_HOUR_100")
        #expect(rehydrated[0].currentResetAtUnixMs == recovery.currentResetAtUnixMs)
        let stale = try resumedMonitor.persistedTelegramCandidates(
            accountIdSHA256: recovery.accountIdSHA256,
            nowMilliseconds: Int64((t0 + 20_000) * 1000))
        #expect(stale.isEmpty)
        #expect(try resumedMonitor.persistedTelegramCandidates(
            accountIdSHA256: String(repeating: "a", count: 64),
            nowMilliseconds: Int64((t0 + 18_140) * 1000)).isEmpty)

        // Candidate-to-outbox is still test-only, NOT wired into AppModel.live().
        let key = try outbox.stage(recovery)
        #expect(try outbox.status(key) == .pending)
        #expect(try outbox.stage(recovery) == key)
        let already = try monitor.recordWithTelegramCandidates(sample(
            fiveUsed: 0, fiveReset: t0 + 36_000,
            observed: t0 + 18_180))
        #expect(already.telegramCandidates.isEmpty)

        // Same provider account after a plan change MUST NOT replay a
        // persisted recovery from the previous plan.
        let changedPlan = try monitor.recordWithTelegramCandidates(sample(
            plan: "team", fiveUsed: 0, fiveReset: t0 + 36_000,
            observed: t0 + 18_240))
        #expect(changedPlan.telegramCandidates.isEmpty)
        let mismatched = try resumedMonitor.persistedTelegramCandidates(
            accountIdSHA256: recovery.accountIdSHA256,
            nowMilliseconds: Int64((t0 + 18_250) * 1000))
        #expect(mismatched.isEmpty)
    }

    @Test func pendingRecoveryFirstObservedWhileTelegramOffNeverReplaysAfterOptIn() throws {
        let root = FileManager.default.temporaryDirectory
            .appending(path: "mac-telegram-off-to-on-\(UUID())")
        defer { try? FileManager.default.removeItem(at: root) }
        let monitor = MacMonitorLedger(directory: root)

        // Preserve the old monitor's read-only behavior while Telegram is OFF.
        #expect(try monitor.record(sample(
            fiveUsed: 75, observed: t0 + 17_900)) == 0)
        #expect(try monitor.record(sample(
            fiveUsed: 0, fiveReset: t0 + 36_000,
            observed: t0 + 18_060)) == 0)

        // Capturing now becomes enabled, but the FIRST recovery observation
        // happened while OFF, so there is no valid Telegram candidate.
        let lateOptIn = try monitor.recordWithTelegramCandidates(sample(
            fiveUsed: 0, fiveReset: t0 + 36_000,
            observed: t0 + 18_120))
        #expect(lateOptIn.eventCount == 1)
        #expect(lateOptIn.telegramCandidates.isEmpty)
        let hash = "732287f049fae3bb4ca0ad762e16b61b78e1e129c52ab15c0a6ac33860cecca3"
        #expect(try MacMonitorLedger(directory: root)
            .persistedTelegramCandidates(
                accountIdSHA256: hash,
                nowMilliseconds: Int64((t0 + 18_130) * 1000)).isEmpty)
    }

    @Test func scheduledWeeklyCandidateAndEarlyRecoveryAreSeparated() throws {
        let root = FileManager.default.temporaryDirectory
            .appending(path: "mac-telegram-week-source-\(UUID())")
        defer { try? FileManager.default.removeItem(at: root) }
        let week = MacMonitorLedger(directory: root.appending(path: "weekly"))
        let oldReset = t0 + 7 * 86_400
        let nextReset = oldReset + 7 * 86_400
        #expect(try week.recordWithTelegramCandidates(sample(
            weekUsed: 60, observed: oldReset - 60)).telegramCandidates.isEmpty)
        #expect(try week.recordWithTelegramCandidates(sample(
            weekUsed: 0, weekReset: nextReset,
            observed: oldReset + 60)).telegramCandidates.isEmpty)
        let result = try week.recordWithTelegramCandidates(sample(
            weekUsed: 0, weekReset: nextReset,
            observed: oldReset + 120))
        #expect(result.telegramCandidates.count == 1)
        #expect(result.telegramCandidates[0].lane.canonicalKind == "WEEKLY_100")

        let early = MacMonitorLedger(directory: root.appending(path: "early"))
        #expect(try early.recordWithTelegramCandidates(sample(
            observed: t0)).telegramCandidates.isEmpty)
        #expect(try early.recordWithTelegramCandidates(sample(
            fiveUsed: 0, fiveReset: t0 + 36_000,
            observed: t0 + 60)).telegramCandidates.isEmpty)
        let suspected = try early.recordWithTelegramCandidates(sample(
            fiveUsed: 0, fiveReset: t0 + 36_000,
            observed: t0 + 120))
        #expect(suspected.eventCount == 1)
        #expect(suspected.telegramCandidates.isEmpty)

        let partial = MacMonitorLedger(directory: root.appending(path: "partial"))
        #expect(try partial.recordWithTelegramCandidates(sample(
            observed: t0 + 17_900)).telegramCandidates.isEmpty)
        #expect(try partial.recordWithTelegramCandidates(sample(
            fiveUsed: 4, fiveReset: t0 + 36_000,
            observed: t0 + 18_060)).telegramCandidates.isEmpty)
        let not100 = try partial.recordWithTelegramCandidates(sample(
            fiveUsed: 4, fiveReset: t0 + 36_000,
            observed: t0 + 18_120))
        #expect(not100.eventCount == 1)
        #expect(not100.telegramCandidates.count == 1)
    }

    @Test func staleSecondReadingNeverCreatesTelegramCandidate() throws {
        let root = FileManager.default.temporaryDirectory
            .appending(path: "mac-telegram-stale-confirmation-\(UUID())")
        defer { try? FileManager.default.removeItem(at: root) }
        let ledger = MacMonitorLedger(directory: root)
        #expect(try ledger.recordWithTelegramCandidates(sample(
            fiveUsed: 75, observed: t0 + 17_900)).telegramCandidates.isEmpty)
        #expect(try ledger.recordWithTelegramCandidates(sample(
            fiveUsed: 0, fiveReset: t0 + 36_000,
            observed: t0 + 18_060)).telegramCandidates.isEmpty)
        let stale = try ledger.recordWithTelegramCandidates(sample(
            fiveUsed: 0, fiveReset: t0 + 36_000,
            observed: t0 + 19_360))
        #expect(stale.eventCount == 1)
        #expect(stale.telegramCandidates.isEmpty)
    }

    @Test func candidateRecoveryPersistsConfirmedRolloverEvenAfterImmediateUsage()
    throws {
        let root = FileManager.default.temporaryDirectory
            .appending(path: "mac-telegram-persisted-filter-\(UUID())")
        defer { try? FileManager.default.removeItem(at: root) }
        let partial = MacMonitorLedger(directory: root)
        #expect(try partial.recordWithTelegramCandidates(sample(
            observed: t0 + 17_900)).telegramCandidates.isEmpty)
        #expect(try partial.recordWithTelegramCandidates(sample(
            fiveUsed: 4, fiveReset: t0 + 36_000,
            observed: t0 + 18_060)).telegramCandidates.isEmpty)
        let candidate = try partial.recordWithTelegramCandidates(sample(
            fiveUsed: 4, fiveReset: t0 + 36_000,
            observed: t0 + 18_120))
        #expect(candidate.eventCount == 1)
        #expect(candidate.telegramCandidates.count == 1)

        let hash = "732287f049fae3bb4ca0ad762e16b61b78e1e129c52ab15c0a6ac33860cecca3"
        let recovered = try MacMonitorLedger(directory: root)
            .persistedTelegramCandidates(
                accountIdSHA256: hash,
                nowMilliseconds: Int64((t0 + 18_130) * 1000))
        #expect(recovered.count == 1)
    }

    @Test func idleAndBusyRolloversDoNotDependOnQuotaDrop() throws {
        let root = FileManager.default.temporaryDirectory
            .appending(path: "mac-telegram-rollover-\(UUID())")
        defer { try? FileManager.default.removeItem(at: root) }
        for used in [0, 4, 25] {
            let ledger = MacMonitorLedger(directory: root.appending(path: "\(used)"))
            #expect(try ledger.recordWithTelegramCandidates(sample(
                fiveUsed: 0, observed: t0 + 17_900)).telegramCandidates.isEmpty)
            // Old expired timestamp must not erase the prior deadline.
            #expect(try ledger.recordWithTelegramCandidates(sample(
                fiveUsed: 0, observed: t0 + 18_015)).telegramCandidates.isEmpty)
            #expect(try ledger.recordWithTelegramCandidates(sample(
                allowed: nil, fiveUsed: used, fiveReset: t0 + 36_000,
                observed: t0 + 18_030)).telegramCandidates.isEmpty)
            let result = try ledger.recordWithTelegramCandidates(sample(
                allowed: nil, fiveUsed: used, fiveReset: t0 + 36_000,
                observed: t0 + 18_045))
            #expect(result.telegramCandidates.count == 1)
            #expect(result.telegramCandidates.first?.lane.canonicalKind == "FIVE_HOUR_100")
            #expect(try ledger.recordWithTelegramCandidates(sample(
                fiveUsed: used, fiveReset: t0 + 36_000,
                observed: t0 + 18_060)).telegramCandidates.isEmpty)
        }
    }

    @Test func deadlineCorrectionsAndDisabledObservationsDoNotCreateCandidates() throws {
        let root = FileManager.default.temporaryDirectory
            .appending(path: "mac-telegram-correction-\(UUID())")
        defer { try? FileManager.default.removeItem(at: root) }
        let ledger = MacMonitorLedger(directory: root)
        #expect(try ledger.recordWithTelegramCandidates(sample(
            observed: t0 + 17_900)).telegramCandidates.isEmpty)
        #expect(try ledger.recordWithTelegramCandidates(sample(
            fiveUsed: 0, fiveReset: t0 + 18_900,
            observed: t0 + 18_015)).telegramCandidates.isEmpty)
        #expect(try ledger.recordWithTelegramCandidates(sample(
            fiveUsed: 0, fiveReset: t0 + 18_900,
            observed: t0 + 18_030)).telegramCandidates.isEmpty)

        let blocked = MacMonitorLedger(directory: root.appending(path: "blocked"))
        #expect(try blocked.recordWithTelegramCandidates(sample(
            observed: t0 + 17_900)).telegramCandidates.isEmpty)
        #expect(try blocked.recordWithTelegramCandidates(sample(
            fiveUsed: 4, fiveReset: t0 + 36_000,
            observed: t0 + 18_030)).telegramCandidates.isEmpty)
        _ = try blocked.recordWithTelegramCandidates(sample(
            allowed: false, fiveUsed: 4, fiveReset: t0 + 36_000,
            observed: t0 + 18_040))
        #expect(try blocked.recordWithTelegramCandidates(sample(
            fiveUsed: 4, fiveReset: t0 + 36_000,
            observed: t0 + 18_060)).telegramCandidates.isEmpty)
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
