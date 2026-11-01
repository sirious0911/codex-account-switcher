import XCTest
import Foundation
@testable import SwitcherCore

final class CodexMonitorPayloadEncoderTests: XCTestCase {
    private func profile(_ accountID: String? = "provider-account-1") -> AccountProfile {
        AccountProfile(id: UUID(), displayName: "Sample", email: "private@example.invalid", accountID: accountID, createdAt: Date())
    }

    private let observed = Date(timeIntervalSince1970: 1_800_000_000)

    private func usage(weekly: Int = 60, fiveHour: Int? = 30, credits: Int? = 2) -> WeeklyUsage {
        WeeklyUsage(remainingPercent: weekly, resetsAt: Date(timeIntervalSince1970: 1_800_600_000), fiveHourRemainingPercent: fiveHour, fiveHourResetsAt: fiveHour == nil ? nil : Date(timeIntervalSince1970: 1_800_030_000), resetCreditsRemaining: credits)
    }

    func testFullObservationHasTheExistingMonitorSchema() throws {
        let result = try XCTUnwrap(CodexMonitorPayloadEncoder.encode(profile: profile(), usage: usage(), planType: "plus", ordinaryUsageAllowed: true, observedAt: observed))
        XCTAssertEqual(result.observedAtMilliseconds, 1_800_000_000_000)
        let object = try XCTUnwrap(JSONSerialization.jsonObject(with: result.jsonData) as? [String: Any])
        XCTAssertEqual(object["accountId"] as? String, "provider-account-1")
        XCTAssertEqual(object["ordinaryUsageAllowed"] as? Bool, true)
        let limits = try XCTUnwrap((object["rateLimitsByLimitId"] as? [String: Any])?["codex"] as? [String: Any])
        XCTAssertEqual(limits["planType"] as? String, "plus")
        let five = try XCTUnwrap(limits["primary"] as? [String: Any])
        XCTAssertEqual(five["usedPercent"] as? Int, 70)
        XCTAssertEqual(five["windowDurationMins"] as? Int, 300)
        let weekly = try XCTUnwrap(limits["secondary"] as? [String: Any])
        XCTAssertEqual(weekly["usedPercent"] as? Int, 40)
        XCTAssertEqual(weekly["windowDurationMins"] as? Int, 10080)
        let credits = try XCTUnwrap(object["rateLimitResetCredits"] as? [String: Any])
        XCTAssertEqual(credits["availableCount"] as? Int, 2)
    }

    func testUnknownProviderPlanAndPermissionStayUnknown() throws {
        let result = try XCTUnwrap(CodexMonitorPayloadEncoder.encode(profile: profile(), usage: usage(), planType: nil, ordinaryUsageAllowed: nil, observedAt: observed))
        let json = try XCTUnwrap(JSONSerialization.jsonObject(with: result.jsonData) as? [String: Any])
        XCTAssertTrue(json["ordinaryUsageAllowed"] is NSNull)
        let limits = try XCTUnwrap((json["rateLimitsByLimitId"] as? [String: Any])?["codex"] as? [String: Any])
        XCTAssertTrue(limits["planType"] is NSNull)
    }

    func testUnknownFiveHourAndCreditsStayUnknown() throws {
        let result = try XCTUnwrap(CodexMonitorPayloadEncoder.encode(profile: profile(), usage: usage(fiveHour: nil, credits: nil), planType: "plus", ordinaryUsageAllowed: false, observedAt: observed))
        let json = try XCTUnwrap(JSONSerialization.jsonObject(with: result.jsonData) as? [String: Any])
        let limits = try XCTUnwrap((json["rateLimitsByLimitId"] as? [String: Any])?["codex"] as? [String: Any])
        XCTAssertTrue(limits["primary"] is NSNull)
        let credits = try XCTUnwrap(json["rateLimitResetCredits"] as? [String: Any])
        XCTAssertTrue(credits["availableCount"] is NSNull)
    }

    func testNoAccountIdIsRejected() {
        XCTAssertNil(CodexMonitorPayloadEncoder.encode(profile: profile(nil), usage: usage(), planType: "plus", ordinaryUsageAllowed: true, observedAt: observed))
    }

    func testInvalidWeeklyAndCreditsAreRejected() {
        XCTAssertNil(CodexMonitorPayloadEncoder.encode(profile: profile(), usage: usage(weekly: -1), planType: "plus", ordinaryUsageAllowed: true, observedAt: observed))
        XCTAssertNil(CodexMonitorPayloadEncoder.encode(profile: profile(), usage: usage(credits: -1), planType: "plus", ordinaryUsageAllowed: true, observedAt: observed))
    }

    func testInvalidPlanCannotBecomeConfirmedIdentity() throws {
        let result = try XCTUnwrap(CodexMonitorPayloadEncoder.encode(profile: profile(), usage: usage(), planType: "bad\nplan", ordinaryUsageAllowed: true, observedAt: observed))
        let json = try XCTUnwrap(JSONSerialization.jsonObject(with: result.jsonData) as? [String: Any])
        let limits = try XCTUnwrap((json["rateLimitsByLimitId"] as? [String: Any])?["codex"] as? [String: Any])
        XCTAssertTrue(limits["planType"] is NSNull)
    }

    func testPrivateDisplayNameEmailAndCredentialsNeverIncluded() throws {
        let result = try XCTUnwrap(CodexMonitorPayloadEncoder.encode(profile: profile(), usage: usage(), planType: "plus", ordinaryUsageAllowed: true, observedAt: observed))
        let text = try XCTUnwrap(String(data: result.jsonData, encoding: .utf8))
        XCTAssertFalse(text.contains("private@example.invalid"))
        XCTAssertFalse(text.contains("Sample"))
        XCTAssertFalse(text.contains("auth.json"))
    }

    func testOutOfRangeTimestampIsRejected() {
        XCTAssertNil(CodexMonitorPayloadEncoder.encode(profile: profile(), usage: usage(), planType: "plus", ordinaryUsageAllowed: true, observedAt: Date(timeIntervalSince1970: 0)))
    }
}
