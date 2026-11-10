import Foundation
import Testing
@testable import SwitcherCore

struct MenuBarQuotaPresentationTests {
    private let now = Date(timeIntervalSince1970: 1_900_000_000)

    @Test func weeklyDisplays5H7DAndTwoCreditsWithExactKSTResetTimes() {
        let usage = WeeklyUsage(
            remainingPercent: 62,
            resetsAt: now.addingTimeInterval(604_800),
            fiveHourRemainingPercent: 85,
            fiveHourResetsAt: now.addingTimeInterval(18_000),
            resetCreditsRemaining: 2,
            resetCredits: [
                BankedResetCredit(id: "private-b", status: "available",
                                  resetType: "codexRateLimits",
                                  expiresAt: now.addingTimeInterval(30_000)),
                BankedResetCredit(id: "private-used", status: "used",
                                  resetType: "codexRateLimits",
                                  expiresAt: now.addingTimeInterval(10_000)),
                BankedResetCredit(id: "private-a", status: "available",
                                  resetType: "codexRateLimits",
                                  expiresAt: now.addingTimeInterval(20_000)),
                BankedResetCredit(id: "private-other", status: "available",
                                  resetType: "otherResetType",
                                  expiresAt: now.addingTimeInterval(15_000)),
            ]
        )
        let view = MenuBarQuotaPresentation.make(state: .loaded(usage),
            identityConfirmed: true)
        #expect(view.label == "5H 85% · 7D 62% · R 2")
        #expect(view.detail.contains("Codex 5H: 85% · next reset: "))
        #expect(view.detail.contains("Codex 7D: 62% · next reset: "))
        #expect(view.detail.contains("R1 expires: "))
        #expect(view.detail.contains("R2 expires: "))
        #expect(view.detail.contains(" KST"))
        #expect(!view.detail.contains("private-"))
        let first = view.detail.range(of: "R1 expires: ")?.lowerBound
        let second = view.detail.range(of: "R2 expires: ")?.lowerBound
        #expect(first != nil && second != nil && first! < second!)
    }

    @Test func preferredWeeklyWindowOnlyReordersNotRebrands() {
        let usage = WeeklyUsage(remainingPercent: 70, resetsAt: now,
                                fiveHourRemainingPercent: 20,
                                fiveHourResetsAt: now, resetCreditsRemaining: 1)
        let view = MenuBarQuotaPresentation.make(state: .loaded(usage),
            identityConfirmed: true, preferredWindow: .weekly)
        #expect(view.label == "7D 70% · 5H 20% · R 1")
    }

    @Test func noVerifiedActiveIdentityMustNotExposeAnotherProfilesValues() {
        let usage = WeeklyUsage(remainingPercent: 91, resetsAt: now,
                                fiveHourRemainingPercent: 86, resetCreditsRemaining: 4)
        let view = MenuBarQuotaPresentation.make(state: .loaded(usage),
            identityConfirmed: false)
        #expect(view.label == "5H ? · 7D ? · R ?")
        #expect(!view.detail.contains("91"))
        #expect(!view.detail.contains("86"))
    }

    @Test func cachedOrStaleUsageNeverAppearsFresh() {
        let usage = WeeklyUsage(remainingPercent: 93, resetsAt: now,
                                fiveHourRemainingPercent: 88, resetCreditsRemaining: 2)
        let staleStates: [UsageViewState] = [.idle, .stale(usage, "offline"), .unavailable("failed")]
        for state in staleStates {
            let view = MenuBarQuotaPresentation.make(state: state, identityConfirmed: true)
            #expect(view.label == "5H ? · 7D ? · R ?")
        }
    }

    @Test func free30DaysIsNeverRelabeled7Days() {
        let usage = NonWeeklyUsage(remainingPercent: 60,
                                   windowDurationMins: 43_200,
                                   resetsAt: now, resetCreditsRemaining: nil)
        let fresh = MenuBarQuotaPresentation.make(state: .nonWeekly(usage),
            identityConfirmed: true)
        #expect(fresh.label == "30D 60% · R ?")
        #expect(fresh.detail.contains("Codex 30D: 60%"))
        let stale = MenuBarQuotaPresentation.make(state: .staleNonWeekly(usage, "old"),
            identityConfirmed: true)
        #expect(stale.label == "30D ? · R ?")
    }

    @Test func missingOrInvalidWindowValuesDoNotGetInvented() {
        let usage = WeeklyUsage(remainingPercent: 101, resetsAt: now,
                                fiveHourRemainingPercent: nil, resetCreditsRemaining: -1)
        let view = MenuBarQuotaPresentation.make(state: .loaded(usage),
            identityConfirmed: true)
        #expect(view.label == "5H ? · 7D ? · R ?")
        #expect(!view.detail.contains("R1 expires:"))
    }
}
