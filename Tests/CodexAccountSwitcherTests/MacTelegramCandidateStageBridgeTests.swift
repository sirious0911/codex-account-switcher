import Foundation
import SwitcherCore
import Testing
@testable import CodexAccountSwitcher

/// The entire staging path remains offline; never starts a bot or URLSession.
@MainActor
struct MacTelegramCandidateStageBridgeTests {
    private let t0: TimeInterval = 1_793_500_000

    private func sample(profile: AccountProfile, used: Int,
                        resetAt: TimeInterval, observedAt: TimeInterval)
        throws -> CodexMonitorObservation {
        let usage = WeeklyUsage(
            remainingPercent: 40,
            resetsAt: Date(timeIntervalSince1970: t0 + 7 * 86_400),
            fiveHourRemainingPercent: 100 - used,
            fiveHourResetsAt: Date(timeIntervalSince1970: resetAt),
            resetCreditsRemaining: 1)
        return try #require(CodexMonitorPayloadEncoder.encode(
            profile: profile, usage: usage, planType: "plus",
            ordinaryUsageAllowed: true,
            observedAt: Date(timeIntervalSince1970: observedAt)))
    }

    private func makeProfile(_ id: UUID = UUID(), account: String = "MOCK_ACCOUNT")
        -> AccountProfile {
        AccountProfile(id: id, displayName: "Mock",
                       email: nil, accountID: account,
                       createdAt: Date(timeIntervalSince1970: t0))
    }

    private func eligibleStage(_ bridge: MacTelegramCandidateStageBridge,
                               _ monitor: MacMonitorLedger,
                               observation: CodexMonitorObservation,
                               profile: AccountProfile,
                               nowOffsetMs: Int64 = 0) throws -> [String] {
        try bridge.stageAfterVerifiedObservation(
            monitorLedger: monitor, observation: observation, profile: profile,
            activeProfileID: profile.id, identityConfirmed: true,
            isMutating: false, isAddingAccount: false, hasVisibleError: false,
            nowMilliseconds: observation.observedAtMilliseconds + nowOffsetMs)
    }

    @Test func defaultMonitorOffNeverCapturesTelegramBacklog() throws {
        let root = FileManager.default.temporaryDirectory
            .appending(path: "m1-stage-off-\(UUID())")
        defer { try? FileManager.default.removeItem(at: root) }
        let profile = makeProfile()
        let monitor = MacMonitorLedger(directory: root)
        #expect(try monitor.record(sample(profile: profile, used: 75,
            resetAt: t0 + 18_000, observedAt: t0 + 17_900)) == 0)
        #expect(try monitor.record(sample(profile: profile, used: 0,
            resetAt: t0 + 36_000, observedAt: t0 + 18_060)) == 0)
        #expect(try monitor.record(sample(profile: profile, used: 0,
            resetAt: t0 + 36_000, observedAt: t0 + 18_120)) == 1)
        let hash = "732287f049fae3bb4ca0ad762e16b61b78e1e129c52ab15c0a6ac33860cecca3"
        #expect(try monitor.persistedTelegramCandidates(
            accountIdSHA256: hash, nowMilliseconds:
            Int64((t0 + 18_130) * 1000)).isEmpty)
    }

    @Test func verifiedActiveTwoReadsStageOnlyPendingOnce() throws {
        let root = FileManager.default.temporaryDirectory
            .appending(path: "m1-stage-on-\(UUID())")
        defer { try? FileManager.default.removeItem(at: root) }
        let profile = makeProfile()
        let monitor = MacMonitorLedger(directory: root.appending(path: "monitor"))
        let outbox = try MacTelegramLocalOutbox(
            directory: root.appending(path: "outbox"))
        let bridge = MacTelegramCandidateStageBridge(outbox: outbox)
        let a = try sample(profile: profile, used: 75, resetAt: t0 + 18_000,
                           observedAt: t0 + 17_900)
        let b = try sample(profile: profile, used: 0, resetAt: t0 + 36_000,
                           observedAt: t0 + 18_060)
        let c = try sample(profile: profile, used: 0, resetAt: t0 + 36_000,
                           observedAt: t0 + 18_120)
        let d = try sample(profile: profile, used: 0, resetAt: t0 + 36_000,
                           observedAt: t0 + 18_180)

        _ = try monitor.recordWithTelegramCandidates(a)
        #expect(try eligibleStage(bridge, monitor, observation: a, profile: profile).isEmpty)
        _ = try monitor.recordWithTelegramCandidates(b)
        #expect(try eligibleStage(bridge, monitor, observation: b, profile: profile).isEmpty)
        let report = try monitor.recordWithTelegramCandidates(c)
        #expect(report.telegramCandidates.count == 1)
        let staged = try eligibleStage(bridge, monitor, observation: c, profile: profile)
        let key = try #require(staged.first)
        #expect(staged.count == 1)
        #expect(try outbox.status(key) == .pending)

        // Re-reading the same verified event must never open a transport.
        _ = try monitor.recordWithTelegramCandidates(d)
        #expect(try eligibleStage(bridge, monitor, observation: d,
                                  profile: profile) == [key])
        #expect(try outbox.status(key) == .pending)
    }

    @Test func unverifiedWrongProfileStaleOrSwitchGatesNeverStage() throws {
        let root = FileManager.default.temporaryDirectory
            .appending(path: "m1-stage-gates-\(UUID())")
        defer { try? FileManager.default.removeItem(at: root) }
        let profile = makeProfile()
        let monitor = MacMonitorLedger(directory: root.appending(path: "monitor"))
        let outbox = try MacTelegramLocalOutbox(
            directory: root.appending(path: "outbox"))
        let bridge = MacTelegramCandidateStageBridge(outbox: outbox)
        let first = try sample(profile: profile, used: 75,
            resetAt: t0 + 18_000, observedAt: t0 + 17_900)
        let next = try sample(profile: profile, used: 0,
            resetAt: t0 + 36_000, observedAt: t0 + 18_060)
        let confirmed = try sample(profile: profile, used: 0,
            resetAt: t0 + 36_000, observedAt: t0 + 18_120)
        _ = try monitor.recordWithTelegramCandidates(first)
        _ = try eligibleStage(bridge, monitor, observation: first, profile: profile)
        _ = try monitor.recordWithTelegramCandidates(next)
        _ = try eligibleStage(bridge, monitor, observation: next, profile: profile)
        _ = try monitor.recordWithTelegramCandidates(confirmed)

        #expect(try bridge.stageAfterVerifiedObservation(
            monitorLedger: monitor, observation: confirmed,
            profile: profile, activeProfileID: profile.id,
            identityConfirmed: false, isMutating: false, isAddingAccount: false,
            hasVisibleError: false,
            nowMilliseconds: confirmed.observedAtMilliseconds).isEmpty)
        #expect(try eligibleStage(bridge, monitor, observation: confirmed,
                                  profile: profile).isEmpty)
        let wrong = makeProfile(profile.id, account: "NOT_THE_OBSERVED_ACCOUNT")
        #expect(MacTelegramCandidateStageBridge.matchesProviderIdentity(
            observation: confirmed, profile: profile))
        #expect(!MacTelegramCandidateStageBridge.matchesProviderIdentity(
            observation: confirmed, profile: wrong))
        #expect(try eligibleStage(bridge, monitor, observation: confirmed,
                                  profile: wrong).isEmpty)
        #expect(try eligibleStage(bridge, monitor, observation: confirmed,
                                  profile: profile, nowOffsetMs: 120_001).isEmpty)
        #expect(try bridge.stageAfterVerifiedObservation(
            monitorLedger: monitor, observation: confirmed,
            profile: profile, activeProfileID: UUID(),
            identityConfirmed: true, isMutating: false, isAddingAccount: false,
            hasVisibleError: false,
            nowMilliseconds: confirmed.observedAtMilliseconds).isEmpty)
        bridge.invalidate()
        #expect(try outbox.hasUnresolvedSend() == false)
    }
}
