import Foundation
import Testing
@testable import CodexAccountSwitcher

@MainActor
struct MacRemoteQuotaViewerTests {
    private let now = Date(timeIntervalSince1970: 1_900_000_000)
    private let source = "windows-desktop"
    private let credential = String(repeating: "v", count: 48)

    private func viewer() throws -> MacRemoteQuotaViewer {
        try MacRemoteQuotaViewer(
            origin: "https://localhost:9443/",
            deviceID: source,
            credential: credential
        )
    }

    private func frame(
        session: String = String(repeating: "a", count: 32),
        sequence: Int64 = 1,
        account: String = String(repeating: "b", count: 64),
        observed: Int64 = 1_899_999_995_000,
        five: Int = 85,
        seven: Int = 62,
        senderAuthorized: Bool = false,
        serverStatus: String = "FRESH_READ_ONLY"
    ) -> Data {
        let packet: [String: Any] = [
            "schemaVersion": 1,
            "status": serverStatus,
            "frame": [
                "sourceDeviceId": source,
                "sourceSessionId": session,
                "sequence": sequence,
                "collectedAtUtcMs": observed,
                "accountIdSha256": account,
                "fiveHour": [
                    "remainingPercent": five,
                    "resetAtUtcMs": observed + 3_600_000
                ],
                "sevenDay": [
                    "remainingPercent": seven,
                    "resetAtUtcMs": observed + 172_800_000
                ]
            ] as [String: Any],
            "actualSendAuthorized": senderAuthorized,
            "automaticRetryAllowed": false,
            "productionReservationAuthorized": false,
            "senderTransitionAuthorized": false
        ]
        return try! JSONSerialization.data(withJSONObject: packet, options: [.sortedKeys])
    }

    @Test func parsesReadOnlyWindowsFrameWithoutChangingLocalAccounts() throws {
        let client = try viewer()
        let response = client.inspect(frame(), now: now)
        #expect(response.status == "FRESH_READ_ONLY")
        #expect(response.snapshot?.sourceDeviceID == source)
        #expect(response.snapshot?.fiveHourPercent == 85)
        #expect(response.snapshot?.sevenDayPercent == 62)
        #expect(response.snapshot?.fiveHourReset != nil)
        #expect(response.snapshot?.sevenDayReset != nil)
    }

    @Test func sameObservationCanAgeWithoutBeingMistakenForReplay() throws {
        let client = try viewer()
        #expect(client.inspect(frame(), now: now).status == "FRESH_READ_ONLY")
        let aged = client.inspect(
            frame(serverStatus: "AGED_READ_ONLY"),
            now: now.addingTimeInterval(150)
        )
        #expect(aged.status == "AGED_READ_ONLY")
        #expect(aged.snapshot?.fiveHourPercent == 85)
        let expired = client.inspect(
            frame(serverStatus: "AGED_READ_ONLY"),
            now: now.addingTimeInterval(361)
        )
        #expect(expired.snapshot == nil)
    }

    @Test func identityChangeBlocksAllFurtherObservation() throws {
        let client = try viewer()
        #expect(client.inspect(frame(), now: now).snapshot != nil)
        let second = frame(
            sequence: 2,
            account: String(repeating: "c", count: 64),
            observed: 1_899_999_997_000
        )
        let conflict = client.inspect(second, now: now)
        #expect(conflict.status == "HOLD_ACCOUNT_SWITCH_REQUIRES_LOCAL_RESET")
        #expect(conflict.snapshot == nil)
        #expect(client.inspect(frame(), now: now).snapshot == nil)
    }

    @Test func rejectsReplayAndPrivilegedSenderFlags() throws {
        let client = try viewer()
        #expect(client.inspect(frame(), now: now).snapshot != nil)
        let replay = frame(
            sequence: 1, observed: 1_899_999_996_000)
        #expect(client.inspect(replay, now: now).snapshot == nil)
        let dangerous = frame(
            sequence: 2, observed: 1_899_999_996_000, senderAuthorized: true)
        #expect(client.inspect(dangerous, now: now).snapshot == nil)
    }

    @Test func connectionRequiresHttpsAndSeparateLongViewerCredential() {
        #expect(throws: MacRemoteQuotaViewer.ViewerError.self) {
            try MacRemoteQuotaViewer(
                origin: "http://localhost:9443/",
                deviceID: source, credential: credential
            )
        }
        #expect(throws: MacRemoteQuotaViewer.ViewerError.self) {
            try MacRemoteQuotaViewer(
                origin: "https://localhost:9443/",
                deviceID: source, credential: "short"
            )
        }
        #expect(throws: MacRemoteQuotaViewer.ViewerError.self) {
            try MacRemoteQuotaViewer(
                origin: "https://example.invalid/path",
                deviceID: source, credential: credential
            )
        }
    }
}
