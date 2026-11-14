import Foundation
import Testing
import SwitcherCore
@testable import CodexAccountSwitcher

/// Read-only synthetic verification. No actual Codex API, network,
/// credentials, Telegram or reset-credit consumption.
@MainActor
struct MacQuotaV2FrameEncoderTests {
    private let now = Date(timeIntervalSince1970: 1_800_000_000)
    private let profileID = UUID(uuidString: "12345678-0000-4000-8000-000000000001")!

    private func profile(_ account: String = "synthetic-mac-provider") -> AccountProfile {
        AccountProfile(id: profileID, displayName: "Synthetic",
                       email: "synthetic@example.invalid", accountID: account,
                       createdAt: now)
    }

    private func weekly(count: Int? = 2, expiry: Bool = true) -> WeeklyUsage {
        let dates: [BankedResetCredit]? = expiry ? [
            .init(id: "private-later", status: "available",
                  resetType: "codexRateLimits",
                  expiresAt: now.addingTimeInterval(300_000)),
            .init(id: "private-used", status: "used",
                  resetType: "codexRateLimits",
                  expiresAt: now.addingTimeInterval(180_000)),
            .init(id: "private-other-type", status: "available",
                  resetType: "anotherType",
                  expiresAt: now.addingTimeInterval(190_000)),
            .init(id: "private-earlier", status: "available",
                  resetType: "codexRateLimits",
                  expiresAt: now.addingTimeInterval(200_000))
        ] : nil
        return WeeklyUsage(remainingPercent: 62,
            resetsAt: now.addingTimeInterval(172_800),
            fiveHourRemainingPercent: 85,
            fiveHourResetsAt: now.addingTimeInterval(3_600),
            resetCreditsRemaining: count,
            resetCredits: dates)
    }

    private func encoder() throws -> MacQuotaV2FrameEncoder {
        try #require(MacQuotaV2FrameEncoder(sourceDeviceID: "macmini-a"))
    }

    private func activated(_ encoder: MacQuotaV2FrameEncoder) {
        encoder.observeActiveProfile(profileID, identityConfirmed: true,
            isMutating: false, switchPending: false,
            now: now.addingTimeInterval(-5))
    }

    private func observation(_ usage: WeeklyUsage, at moment: Date)
        throws -> CodexMonitorObservation {
        try #require(CodexMonitorPayloadEncoder.encode(
            profile: profile(), usage: usage, planType: "plus",
            ordinaryUsageAllowed: true, observedAt: moment))
    }

    private func dict(_ data: Data) throws -> [String: Any] {
        try #require(JSONSerialization.jsonObject(with: data) as? [String: Any])
    }

    @Test func weeklyFrameFiltersCreditIdsAndSortsR1R2() throws {
        let enc = try encoder()
        activated(enc)
        let moment = now.addingTimeInterval(-1)
        let obs = try observation(weekly(), at: moment)
        let data = try #require(enc.makeWeeklyFrame(
            profile: profile(), observation: obs, now: now))
        let root = try dict(data)
        #expect(root["schemaVersion"] as? Int == 2)
        #expect(root["sourceDeviceId"] as? String == "macmini-a")
        #expect(root["sequence"] as? Int == 1)
        let five = try #require(root["fiveHour"] as? [String: Any])
        let seven = try #require(root["sevenDay"] as? [String: Any])
        #expect(five["remainingPercent"] as? Int == 85)
        #expect(seven["remainingPercent"] as? Int == 62)
        let r = try #require(root["resetCredits"] as? [String: Any])
        #expect(r["availableCount"] as? Int == 2)
        let expiries = try #require(r["expiryDetailsUtcMs"] as? [Int64])
        #expect(expiries == [1_800_200_000_000, 1_800_300_000_000])
        let raw = try #require(String(data: data, encoding: .utf8))
        for forbidden in ["synthetic-mac-provider", "synthetic@example.invalid",
                          "private-used", "private-other-type",
                          "private-earlier", "private-later"] {
            #expect(!raw.contains(forbidden))
        }
        for flag in ["actualSendAuthorized", "automaticRetryAllowed",
                     "productionReservationAuthorized", "senderTransitionAuthorized"] {
            #expect(root[flag] as? Bool == false)
        }
    }

    @Test func rCountZeroDoesNotKeepOldExpiries() throws {
        let enc = try encoder()
        activated(enc)
        let old = try observation(weekly(), at: now.addingTimeInterval(-1))
        #expect(enc.makeWeeklyFrame(profile: profile(), observation: old,
                                    now: now) != nil)
        let next = try observation(weekly(count: 0, expiry: false), at: now)
        let data = try #require(enc.makeWeeklyFrame(
            profile: profile(), observation: next,
            now: now.addingTimeInterval(0.5)))
        let r = try #require(try dict(data)["resetCredits"] as? [String: Any])
        #expect(r["availableCount"] as? Int == 0)
        #expect(r["expiryDetailsUtcMs"] is NSNull)
    }

    @Test func free30DHasRCountAndUnknownExpiryNotFakeWeekly() throws {
        let enc = try encoder()
        activated(enc)
        let free = NonWeeklyUsage(
            remainingPercent: 40, windowDurationMins: 43_200,
            resetsAt: now.addingTimeInterval(25 * 86_400),
            resetCreditsRemaining: 2, observedAt: now.addingTimeInterval(-1))
        let data = try #require(enc.makeNonWeeklyFrame(
            profile: profile(), usage: free, now: now))
        let root = try dict(data)
        let five = try #require(root["fiveHour"] as? [String: Any])
        let seven = try #require(root["sevenDay"] as? [String: Any])
        let credits = try #require(root["resetCredits"] as? [String: Any])
        #expect(five["remainingPercent"] is NSNull)
        #expect(seven["remainingPercent"] is NSNull)
        #expect(credits["availableCount"] as? Int == 2)
        #expect(credits["expiryDetailsUtcMs"] is NSNull)
        #expect(root["nonWeeklyWindow"] == nil)
        #expect((try #require(String(data: data, encoding: .utf8)))
            .contains("private") == false)
    }

    @Test func identityMismatchAndPendingSwitchReject() throws {
        let enc = try encoder()
        let obs = try observation(weekly(), at: now.addingTimeInterval(-1))
        #expect(enc.makeWeeklyFrame(profile: profile(), observation: obs, now: now) == nil)
        enc.observeActiveProfile(profileID, identityConfirmed: false,
            isMutating: false, switchPending: false, now: now.addingTimeInterval(-5))
        #expect(enc.makeWeeklyFrame(profile: profile(), observation: obs, now: now) == nil)
        activated(enc)
        let wrong = AccountProfile(
            id: UUID(), displayName: "Wrong", email: nil,
            accountID: "synthetic-mac-provider", createdAt: now)
        #expect(enc.makeWeeklyFrame(profile: wrong, observation: obs, now: now) == nil)
        #expect(enc.makeWeeklyFrame(profile: profile(), observation: obs, now: now) != nil)
        enc.switchRequested(now: now)
        #expect(enc.makeWeeklyFrame(profile: profile(), observation: obs,
                                    now: now.addingTimeInterval(0.5)) == nil)
    }

    @Test func duplicatesProviderConflictAndExpiryBeyondSixMinutesFailClosed() throws {
        let enc = try encoder()
        activated(enc)
        let obs = try observation(weekly(), at: now.addingTimeInterval(-1))
        #expect(enc.makeWeeklyFrame(profile: profile(), observation: obs, now: now) != nil)
        #expect(enc.makeWeeklyFrame(profile: profile(), observation: obs, now: now) == nil)
        let changed = try #require(CodexMonitorPayloadEncoder.encode(
            profile: profile("different-provider"), usage: weekly(),
            planType: nil, ordinaryUsageAllowed: nil,
            observedAt: now.addingTimeInterval(-0.5)))
        #expect(enc.makeWeeklyFrame(profile: profile("different-provider"),
                                    observation: changed, now: now) == nil)
        #expect(enc.makeWeeklyFrame(profile: profile(),
            observation: try observation(weekly(), at: now),
            now: now) == nil)

        let old = try encoder()
        activated(old)
        #expect(old.makeWeeklyFrame(profile: profile(),
            observation: try observation(weekly(),
                at: now.addingTimeInterval(-360)),
            now: now) == nil)
    }

    @Test func unknownCountDoesNotFabricateZeroOrRDates() throws {
        let enc = try encoder()
        activated(enc)
        let free = NonWeeklyUsage(
            remainingPercent: 40, windowDurationMins: 43_200,
            resetsAt: now.addingTimeInterval(25 * 86_400),
            resetCreditsRemaining: nil, observedAt: now.addingTimeInterval(-1))
        #expect(enc.makeNonWeeklyFrame(profile: profile(), usage: free, now: now) == nil)
        let countZero = NonWeeklyUsage(
            remainingPercent: 40, windowDurationMins: 43_200,
            resetsAt: now.addingTimeInterval(25 * 86_400),
            resetCreditsRemaining: 0, observedAt: now.addingTimeInterval(-1))
        let data = try #require(enc.makeNonWeeklyFrame(
            profile: profile(), usage: countZero, now: now))
        let credits = try #require(try dict(data)["resetCredits"] as? [String: Any])
        #expect(credits["availableCount"] as? Int == 0)
        #expect(credits["expiryDetailsUtcMs"] is NSNull)
    }

    @Test func framesStayUnder2048AndUseMonotonicSequence() throws {
        let enc = try encoder()
        activated(enc)
        for index in 1...4 {
            let t = now.addingTimeInterval(Double(index) - 3)
            let input = try observation(weekly(count: 2, expiry: false), at: t)
            let data = try #require(enc.makeWeeklyFrame(
                profile: profile(), observation: input,
                now: now.addingTimeInterval(2)))
            #expect(data.count <= 2_048)
            #expect(try dict(data)["sequence"] as? Int == index)
        }
    }
}
