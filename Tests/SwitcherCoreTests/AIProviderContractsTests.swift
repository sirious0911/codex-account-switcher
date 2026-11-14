import Foundation
import XCTest
@testable import SwitcherCore

final class AIProviderContractsTests: XCTestCase {
    private func account(_ service: AIServiceKind = .codex, _ id: String = "provider-id-1") throws -> AIAccountKey {
        try XCTUnwrap(AIAccountKey(service: service, accountID: id))
    }

    private func profile(_ id: String? = "provider-id-1") -> AccountProfile {
        AccountProfile(id: UUID(), displayName: "Demo", email: nil, accountID: id, createdAt: Date())
    }

    func testServiceScopedIdentity() throws {
        XCTAssertNotEqual(try account(.codex), try account(.claudeCode))
        XCTAssertNotEqual(try account(.claudeCode), try account(.claudeDesktop))
    }

    func testRejectInvalidAccountIDs() {
        XCTAssertNil(AIAccountKey(service: .codex, accountID: "  \n  "))
        XCTAssertNil(AIAccountKey(service: .codex, accountID: String(repeating: "x", count: 257)))
        XCTAssertNil(AIAccountKey(service: .codex, accountID: "invalid\rid"))
        XCTAssertEqual(AIAccountKey(service: .codex, accountID: "  abc  ")?.accountID, "abc")
    }

    func testUnknownUsageIsNotZero() throws {
        let window = try XCTUnwrap(AIUsageWindow(kind: .weekly, remainingPercent: nil, resetsAt: nil))
        XCTAssertNil(window.remainingPercent)
        XCTAssertNil(AIUsageWindow(kind: .weekly, remainingPercent: -1, resetsAt: nil))
        XCTAssertNil(AIUsageWindow(kind: .weekly, remainingPercent: 101, resetsAt: nil))
        XCTAssertNil(AIUsageWindow(kind: .weekly, remainingPercent: .nan, resetsAt: nil))
    }

    func testFreshnessRequiresTimestampAndNonnegativeCredits() throws {
        let key = try account()
        XCTAssertNil(AIUsageSnapshot(account: key, observedAt: nil, freshness: .fresh,
                                     windows: [], resetCreditsRemaining: 0))
        XCTAssertNotNil(AIUsageSnapshot(account: key, observedAt: nil, freshness: .unknown,
                                        windows: [], resetCreditsRemaining: nil))
        XCTAssertNil(AIUsageSnapshot(account: key, observedAt: Date(), freshness: .fresh,
                                     windows: [], resetCreditsRemaining: -1))
    }

    func testAlertIdentityIsPeriodAndProviderScoped() throws {
        let a = try AIAlertEventKey(account: account(), type: .unusualCapacityRecovery,
                                    limitID: "weekly", periodIdentity: "2026-W44")
        let b = try AIAlertEventKey(account: account(.claudeCode), type: .unusualCapacityRecovery,
                                    limitID: "weekly", periodIdentity: "2026-W44")
        let c = try AIAlertEventKey(account: account(), type: .unusualCapacityRecovery,
                                    limitID: "weekly", periodIdentity: "2026-W45")
        XCTAssertNotNil(a)
        XCTAssertNotEqual(a, b)
        XCTAssertNotEqual(a, c)
        XCTAssertEqual(a, a)
    }

    func testBadAlertIDsFailClosed() throws {
        let key = try account()
        XCTAssertNil(AIAlertEventKey(account: key, type: .resetCreditIncrease,
                                     limitID: "", periodIdentity: "period"))
        XCTAssertNil(AIAlertEventKey(account: key, type: .resetCreditIncrease,
                                     limitID: "weekly", periodIdentity: "bad\nperiod"))
        XCTAssertNil(AIAlertEventKey(account: key, type: .resetCreditIncrease,
                                     limitID: "weekly", periodIdentity: "period", creditID: ""))
    }

    func testSerializationPreservesServiceAndUnknown() throws {
        let key = try account(.claudeDesktop)
        let original = try XCTUnwrap(AIUsageSnapshot(account: key, observedAt: nil, freshness: .unknown,
                                                       windows: [], resetCreditsRemaining: nil))
        let roundTrip = try JSONDecoder().decode(AIUsageSnapshot.self, from: JSONEncoder().encode(original))
        XCTAssertEqual(original, roundTrip)
    }

    func testCodexAdapterRejectsNoProviderIdentity() {
        XCTAssertNil(CodexUsageSnapshotAdapter.accountKey(for: profile(nil)))
    }

    func testCodexAdapterPreservesRealProviderIdentity() {
        XCTAssertEqual(CodexUsageSnapshotAdapter.accountKey(for: profile())?.accountID, "provider-id-1")
        XCTAssertEqual(CodexUsageSnapshotAdapter.accountKey(for: profile())?.service, .codex)
    }

    func testCodexAdapterMapsFreshWindowAndCredit() throws {
        let now = Date(timeIntervalSince1970: 1_800_000_000)
        let usage = WeeklyUsage(remainingPercent: 75, resetsAt: now.addingTimeInterval(300),
                                fiveHourRemainingPercent: 40, fiveHourResetsAt: now.addingTimeInterval(100),
                                resetCreditsRemaining: 2)
        let snapshot = try XCTUnwrap(CodexUsageSnapshotAdapter.snapshot(for: profile(), usage: usage,
                                                                         observedAt: now, freshness: .fresh))
        XCTAssertEqual(snapshot.windows.count, 2)
        XCTAssertEqual(snapshot.windows[0].kind, .weekly)
        XCTAssertEqual(snapshot.windows[0].remainingPercent, 75)
        XCTAssertEqual(snapshot.windows[1].kind, .fiveHour)
        XCTAssertEqual(snapshot.windows[1].remainingPercent, 40)
        XCTAssertEqual(snapshot.resetCreditsRemaining, 2)
    }

    func testCodexAdapterOmitsAbsentFiveHourWindow() throws {
        let usage = WeeklyUsage(remainingPercent: 10, resetsAt: Date(),
                                fiveHourRemainingPercent: nil, fiveHourResetsAt: nil)
        let snapshot = try XCTUnwrap(CodexUsageSnapshotAdapter.snapshot(for: profile(), usage: usage,
                                                                         observedAt: Date(), freshness: .fresh))
        XCTAssertEqual(snapshot.windows.count, 1)
        XCTAssertEqual(snapshot.windows[0].kind, .weekly)
    }

    func testCodexAdapterRejectsInvalidWeeklyPercentAndMissingTime() {
        let usage = WeeklyUsage(remainingPercent: 200, resetsAt: Date())
        XCTAssertNil(CodexUsageSnapshotAdapter.snapshot(for: profile(), usage: usage,
                                                         observedAt: Date(), freshness: .fresh))
        let good = WeeklyUsage(remainingPercent: 50, resetsAt: Date())
        XCTAssertNil(CodexUsageSnapshotAdapter.snapshot(for: profile(), usage: good,
                                                         observedAt: nil, freshness: .fresh))
    }
}
