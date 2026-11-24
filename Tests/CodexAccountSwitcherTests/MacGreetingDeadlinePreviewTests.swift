import Foundation
import SwitcherCore
import Testing
@testable import CodexAccountSwitcher

@MainActor
struct MacGreetingDeadlinePreviewTests {
    private let boundary: Int64 = 1_800_000_000_000
    private func profile() -> AccountProfile {
        AccountProfile(id: UUID(), displayName: "Synthetic", email: "mock@example.test",
                       accountID: "MOCK_ACCOUNT", createdAt: Date())
    }
    private func sample(_ p: AccountProfile, seen: Int64,
                        five: Int64, week: Int64, allowed: Bool = true) throws -> CodexMonitorObservation {
        let usage = WeeklyUsage(remainingPercent: 20,
            resetsAt: Date(timeIntervalSince1970: Double(week) / 1000),
            fiveHourRemainingPercent: 8,
            fiveHourResetsAt: Date(timeIntervalSince1970: Double(five) / 1000))
        return try #require(CodexMonitorPayloadEncoder.encode(profile: p,
            usage: usage, planType: "plus", ordinaryUsageAllowed: allowed,
            observedAt: Date(timeIntervalSince1970: Double(seen) / 1000)))
    }

    @Test func bothBoundariesOnceWithoutTransport() throws {
        let p = profile(), watch = MacGreetingDeadlinePreview()
        let before = boundary - 30_000
        watch.observe(try sample(p, seen: before, five: boundary, week: boundary),
                      profile: p, activeProfileID: p.id, identityConfirmed: true,
                      busy: false, nowMs: before)
        #expect(watch.takeDue(profile: p, activeProfileID: p.id,
            identityConfirmed: true, busy: false, nowMs: before).isEmpty)
        let due = watch.takeDue(profile: p, activeProfileID: p.id,
            identityConfirmed: true, busy: false, nowMs: boundary)
        #expect(due.count == 2)
        #expect(Set(due.map { $0.eventKey }).count == 2)
        #expect(watch.takeDue(profile: p, activeProfileID: p.id,
            identityConfirmed: true, busy: false, nowMs: boundary + 1000).isEmpty)
    }

    @Test func staleCorrectionAndIdentityLossCannotTrigger() throws {
        let p = profile(), watch = MacGreetingDeadlinePreview()
        let before = boundary - 30_000
        watch.observe(try sample(p, seen: before, five: boundary, week: boundary),
            profile: p, activeProfileID: p.id, identityConfirmed: true, busy: false, nowMs: before)
        #expect(watch.takeDue(profile: p, activeProfileID: p.id,
            identityConfirmed: true, busy: false, nowMs: boundary + 400_000).isEmpty)
        let correction = before + 1000
        watch.observe(try sample(p, seen: correction, five: boundary + 600_000,
                                 week: boundary + 600_000),
            profile: p, activeProfileID: p.id, identityConfirmed: true, busy: false, nowMs: correction)
        #expect(watch.takeDue(profile: p, activeProfileID: p.id,
            identityConfirmed: true, busy: false, nowMs: boundary).isEmpty)
        #expect(watch.takeDue(profile: p, activeProfileID: p.id,
            identityConfirmed: false, busy: false, nowMs: boundary + 600_000).isEmpty)
        #expect(watch.takeDue(profile: p, activeProfileID: p.id,
            identityConfirmed: true, busy: false, nowMs: boundary + 600_000).isEmpty)
    }
}
