import Foundation
import Testing
@testable import CodexAccountSwitcher

@MainActor
struct MacRemoteQuotaV2ViewerTests {
    private let now = Date(timeIntervalSince1970: 1_900_000_000)
    private let source = "windows-b"
    private let credential = String(repeating: "v", count: 48)
    private let startMs: Int64 = 1_899_999_995_000
    private let expiryA: Int64 = 1_900_200_000_000
    private let expiryB: Int64 = 1_900_300_000_000

    private func viewer() throws -> MacRemoteQuotaV2Viewer {
        try MacRemoteQuotaV2Viewer(
            origin: "https://localhost:9443/",
            deviceID: source, credential: credential)
    }

    private func frame(
        sourceID: String = "windows-b",
        session: String = String(repeating: "a", count: 32),
        sequence: Int64 = 1,
        account: String = String(repeating: "b", count: 64),
        observed: Int64 = 1_899_999_995_000,
        count: Int? = 2,
        expiry: [Int64?]? = [1_900_300_000_000, 1_900_200_000_000],
        free: Bool = false,
        invalidPrivilege: Bool = false,
        schema: Int = 2,
        extra: Bool = false,
        status: String = "FRESH_READ_ONLY"
    ) -> Data {
        let exp: Any = expiry.map {
            $0.map { value in value.map { $0 as Any } ?? NSNull() }
        } ?? NSNull()
        var frame: [String: Any] = [
            "sourceDeviceId": sourceID,
            "sourceSessionId": session,
            "sequence": sequence,
            "collectedAtUtcMs": observed,
            "accountIdSha256": account,
            "fiveHour": [
                "remainingPercent": free ? NSNull() : 85,
                "resetAtUtcMs": free ? NSNull() : observed + 3_600_000
            ] as [String: Any],
            "sevenDay": [
                "remainingPercent": free ? NSNull() : 62,
                "resetAtUtcMs": free ? NSNull() : observed + 172_800_000
            ] as [String: Any],
            "resetCredits": [
                "availableCount": count.map { $0 as Any } ?? NSNull(),
                "expiryDetailsUtcMs": exp
            ] as [String: Any]
        ]
        if extra { frame["creditId"] = "private-provider-credit" }
        return try! JSONSerialization.data(withJSONObject: [
            "schemaVersion": schema,
            "status": status,
            "frame": frame,
            "actualSendAuthorized": invalidPrivilege,
            "automaticRetryAllowed": false,
            "productionReservationAuthorized": false,
            "senderTransitionAuthorized": false
        ] as [String: Any], options: [.sortedKeys])
    }

    @Test func readsWindowsR2AndSortsExpiryWithoutAccountActions() throws {
        let client = try viewer()
        let result = client.inspect(frame(), now: now)
        #expect(result.status == "FRESH_READ_ONLY")
        #expect(result.snapshot?.sourceDeviceID == source)
        #expect(result.snapshot?.fiveHourPercent == 85)
        #expect(result.snapshot?.sevenDayPercent == 62)
        #expect(result.snapshot?.resetCreditsRemaining == 2)
        let dates = try #require(result.snapshot?.resetCreditExpiries)
        #expect(dates.count == 2)
        #expect(dates[0]?.timeIntervalSince1970 == Double(expiryA)/1000)
        #expect(dates[1]?.timeIntervalSince1970 == Double(expiryB)/1000)
        #expect(!result.accountVerified && !result.telegramAllowed)
        #expect(!result.senderTransitionAuthorized)
    }

    @Test func countZeroAndExpiryUnknownNeverReuseOldDates() throws {
        let client = try viewer()
        #expect(client.inspect(frame(), now: now).snapshot?.resetCreditsRemaining == 2)
        let zero = client.inspect(frame(
            sequence: 2, observed: startMs + 1000, count: 0, expiry: nil),
            now: now)
        #expect(zero.snapshot?.resetCreditsRemaining == 0)
        #expect(zero.snapshot?.resetCreditExpiries == nil)

        let freeClient = try viewer()
        let rOnly = freeClient.inspect(frame(free: true, expiry: nil), now: now)
        #expect(rOnly.snapshot?.resetCreditsRemaining == 2)
        #expect(rOnly.snapshot?.fiveHourPercent == nil)
        #expect(rOnly.snapshot?.sevenDayPercent == nil)
        #expect(rOnly.snapshot?.resetCreditExpiries == nil)

        let unknown = try viewer()
        #expect(unknown.inspect(frame(count:nil,expiry:nil),now:now)
            .snapshot?.resetCreditsRemaining == nil)
        let empty = try viewer()
        #expect(empty.inspect(frame(count:1,expiry:[]),now:now)
            .snapshot?.resetCreditExpiries?.isEmpty == true)
    }

    @Test func replayClockAgeAndChangedAccountFailClosed() throws {
        let client = try viewer()
        #expect(client.inspect(frame(), now: now).snapshot != nil)
        let aged = client.inspect(frame(status:"AGED_READ_ONLY"),
                                 now:now.addingTimeInterval(150))
        #expect(aged.status == "AGED_READ_ONLY")
        #expect(aged.snapshot != nil)
        let replay = client.inspect(
            frame(sequence:1,observed:startMs+1000),now:now)
        #expect(replay.snapshot == nil)
        let changed = client.inspect(frame(sequence:2,
            account:String(repeating:"c",count:64),
            observed:startMs+2000),now:now)
        #expect(changed.status == "HOLD_ACCOUNT_SWITCH_REQUIRES_LOCAL_RESET")
        #expect(changed.snapshot == nil)
        #expect(client.inspect(frame(),now:now).snapshot == nil)

        let stale = try viewer()
        #expect(stale.inspect(frame(),now:now.addingTimeInterval(360))
            .snapshot == nil)
    }

    @Test func rejectsUnknownProtocolPrivilegeWrongSourceAndInvalidRCounts() throws {
        for data in [
            frame(schema:1),
            frame(invalidPrivilege:true),
            frame(sourceID:"another-device"),
            frame(extra:true),
            frame(count:0,expiry:[expiryA]),
            frame(count:1,expiry:[expiryA,expiryB]),
            frame(count:nil,expiry:[expiryA]),
            frame(free:true,count:nil,expiry:nil),
            frame(expiry:[1_900_200_000])
        ] {
            let client = try viewer()
            #expect(client.inspect(data,now:now).snapshot == nil)
        }
    }

    @Test func viewerRequiresBareHttpsAndReadOnlyCredential() throws {
        #expect(throws: MacRemoteQuotaV2Viewer.ViewerError.self) {
            try MacRemoteQuotaV2Viewer(origin:"http://localhost:9443/",
                deviceID:source,credential:credential)
        }
        #expect(throws: MacRemoteQuotaV2Viewer.ViewerError.self) {
            try MacRemoteQuotaV2Viewer(origin:"https://localhost:9443/api",
                deviceID:source,credential:credential)
        }
        #expect(throws: MacRemoteQuotaV2Viewer.ViewerError.self) {
            try MacRemoteQuotaV2Viewer(origin:"https://localhost:9443/",
                deviceID:source,credential:"short")
        }
    }
}
