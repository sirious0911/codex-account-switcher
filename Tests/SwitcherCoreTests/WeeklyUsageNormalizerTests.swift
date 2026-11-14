@testable import SwitcherCore
import Foundation
import Testing


struct WeeklyUsageNormalizerTests {
    @Test func clampsExtremePercentagesBeforeIntegerConversion() throws {
        for (used, expected) in [(1e100, 0), (-1e100, 100)] {
            let usage = try WeeklyUsageNormalizer.normalize([
                RateLimitWindow(usedPercent: used, windowDurationMins: 10_080, resetsAt: 100),
            ])
            #expect(usage.remainingPercent == expected)
        }
    }

    @Test func selectsLongestWeeklyWindowAndCalculatesRemaining() throws {
        let reset = Date(timeIntervalSince1970: 1_750_000_000)
        let result = try WeeklyUsageNormalizer.normalize([
            RateLimitWindow(usedPercent: 1, windowDurationMins: 300, resetsAt: 10),
            RateLimitWindow(usedPercent: 58, windowDurationMins: 7 * 24 * 60, resetsAt: reset.timeIntervalSince1970),
            RateLimitWindow(usedPercent: 20, windowDurationMins: 6 * 24 * 60, resetsAt: 20),
        ])
        #expect(result.remainingPercent == 42)
        #expect(result.resetsAt == reset)
        #expect(result.fiveHourRemainingPercent == 99)
        #expect(result.fiveHourResetsAt == Date(timeIntervalSince1970: 10))
    }

    @Test func acceptsSixThroughEightDayWindows() throws {
        for days in [6, 7, 8] {
            let result = try WeeklyUsageNormalizer.normalize([
                RateLimitWindow(usedPercent: 25, windowDurationMins: days * 24 * 60, resetsAt: 50),
            ])
            #expect(result.remainingPercent == 75, "days: \(days)")
        }
    }

    @Test func free30DayQuotaRetainsRealDurationWithoutInventingWeekly() throws {
        let payload = """
            {"rateLimitsByLimitId":{"codex":{"planType":"free",
                "primary":{"usedPercent":27,"windowDurationMins":43200,"resetsAt":1900000000},
                "secondary":null}},
             "rateLimitResetCredits":{"availableCount":0,"credits":[]}}
            """
        let parsed = try JSONDecoder().decode(JSONValue.self, from: Data(payload.utf8))
        let fetched = try CodexClient().interpretRateLimits(parsed)
        guard case let .nonWeekly(other) = fetched else {
            Issue.record("Free 30D must not be relabeled as 7D")
            return
        }
        #expect(other.windowLabel == "30D")
        #expect(other.windowDurationMins == 43_200)
        #expect(other.remainingPercent == 73)
        #expect(other.resetsAt == Date(timeIntervalSince1970: 1_900_000_000))
        #expect(other.resetCreditsRemaining == 0)
    }

    @Test func standardWeeklyAndFiveHourStillNormalizeUnchanged() throws {
        let payload = """
            {"rateLimits":{"primary":{"usedPercent":20,"windowDurationMins":300,"resetsAt":1900000000},
                "secondary":{"usedPercent":30,"windowDurationMins":10080,"resetsAt":1900100000}}}
            """
        let parsed = try JSONDecoder().decode(JSONValue.self, from: Data(payload.utf8))
        let fetched = try CodexClient().interpretRateLimits(parsed)
        guard case let .weekly(usage) = fetched else {
            Issue.record("Existing paid 5H/7D calculation changed")
            return
        }
        #expect(usage.fiveHourRemainingPercent == 80)
        #expect(usage.remainingPercent == 70)
    }

    @Test func missingAllQuotaWindowsRemainsUnavailable() throws {
        let parsed = try JSONDecoder().decode(JSONValue.self,
            from: Data(#"{"rateLimits":{"primary":null,"secondary":null}}"#.utf8))
        #expect(throws: CodexClientError.weeklyUsageUnavailable) {
            try CodexClient().interpretRateLimits(parsed)
        }
    }

    @Test func fifteenMinuteWindowUsesActualLabel() throws {
        let parsed = try JSONDecoder().decode(JSONValue.self,
            from: Data(#"{"rateLimits":{"primary":{"usedPercent":50,"windowDurationMins":15,"resetsAt":1900000000}}}"#.utf8))
        let fetched = try CodexClient().interpretRateLimits(parsed)
        guard case let .nonWeekly(usage) = fetched else {
            Issue.record("15-minute quota must retain its actual window")
            return
        }
        #expect(usage.windowLabel == "15m")
        #expect(usage.remainingPercent == 50)
    }

    @Test func rejectsShortWindows() {
        #expect(throws: CodexClientError.weeklyUsageUnavailable) {
            try WeeklyUsageNormalizer.normalize([
                RateLimitWindow(usedPercent: 25, windowDurationMins: 5 * 60, resetsAt: 50),
                RateLimitWindow(usedPercent: 25, windowDurationMins: 24 * 60, resetsAt: 50),
            ])
        }
    }

    @Test func weeklyUsageStillWorksWhenFiveHourWindowIsMissing() throws {
        let result = try WeeklyUsageNormalizer.normalize([
            RateLimitWindow(usedPercent: 25, windowDurationMins: 7 * 24 * 60, resetsAt: 50),
        ])

        #expect(result.remainingPercent == 75)
        #expect(result.fiveHourRemainingPercent == nil)
        #expect(result.fiveHourResetsAt == nil)
    }

    @Test func acceptsOnlyExactFiveHourWindow() throws {
        let result = try WeeklyUsageNormalizer.normalize([
            RateLimitWindow(usedPercent: 10, windowDurationMins: 4 * 60, resetsAt: 30),
            RateLimitWindow(usedPercent: 35, windowDurationMins: 5 * 60, resetsAt: 40),
            RateLimitWindow(usedPercent: 20, windowDurationMins: 6 * 60, resetsAt: 45),
            RateLimitWindow(usedPercent: 25, windowDurationMins: 7 * 24 * 60, resetsAt: 50),
        ])

        #expect(result.fiveHourRemainingPercent == 65)
        #expect(result.fiveHourResetsAt == Date(timeIntervalSince1970: 40))
    }

    @Test func doesNotLabelFourOrSixHourWindowsAsFiveHour() throws {
        let result = try WeeklyUsageNormalizer.normalize([
            RateLimitWindow(usedPercent: 10, windowDurationMins: 4 * 60, resetsAt: 30),
            RateLimitWindow(usedPercent: 20, windowDurationMins: 6 * 60, resetsAt: 45),
            RateLimitWindow(usedPercent: 25, windowDurationMins: 7 * 24 * 60, resetsAt: 50),
        ])

        #expect(result.fiveHourRemainingPercent == nil)
        #expect(result.fiveHourResetsAt == nil)
    }

    @Test func clampsRemainingPercent() throws {
        for (usedPercent, expected) in [(-5.0, 100), (125.0, 0)] {
            let result = try WeeklyUsageNormalizer.normalize([
                RateLimitWindow(usedPercent: usedPercent, windowDurationMins: 7 * 24 * 60, resetsAt: 50),
            ])
            #expect(result.remainingPercent == expected, "used percent: \(usedPercent)")
        }
    }

    @Test func clampsFiveHourRemainingPercent() throws {
        for (usedPercent, expected) in [(-5.0, 100), (125.0, 0)] {
            let result = try WeeklyUsageNormalizer.normalize([
                RateLimitWindow(usedPercent: usedPercent, windowDurationMins: 300, resetsAt: 40),
                RateLimitWindow(usedPercent: 20, windowDurationMins: 7 * 24 * 60, resetsAt: 50),
            ])
            #expect(result.fiveHourRemainingPercent == expected, "used percent: \(usedPercent)")
        }
    }
}
