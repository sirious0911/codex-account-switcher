import Combine
import CryptoKit
import Foundation

/// P3 V2 Windows-source read-only projection. No Codex, Telegram or credit
/// mutation operations. The credential and replay state live only in memory.
struct MacRemoteQuotaV2Snapshot: Equatable, Sendable {
    let sourceDeviceID: String
    let fiveHourPercent: Int?
    let sevenDayPercent: Int?
    let fiveHourReset: Date?
    let sevenDayReset: Date?
    let resetCreditsRemaining: Int?
    /// nil = details unavailable; [] = provided but zero matched entries.
    let resetCreditExpiries: [Date?]?
    let observedAt: Date
}

struct MacRemoteQuotaV2Result: Sendable {
    let status: String
    let snapshot: MacRemoteQuotaV2Snapshot?
    var accountVerified: Bool { false }
    var telegramAllowed: Bool { false }
    var senderTransitionAuthorized: Bool { false }
}

@MainActor
final class MacRemoteQuotaV2ViewerModel: ObservableObject {
    @Published private(set) var status = "연결 안 됨"
    @Published private(set) var snapshot: MacRemoteQuotaV2Snapshot?
    @Published private(set) var sourceDeviceID: String?
    @Published private(set) var isFetching = false
    private var client: MacRemoteQuotaV2Viewer?

    var isConnected: Bool { client != nil }

    func connect(origin: String, deviceID: String, credential: String) throws {
        let newClient = try MacRemoteQuotaV2Viewer(
            origin: origin.trimmingCharacters(in: .whitespacesAndNewlines),
            deviceID: deviceID.trimmingCharacters(in: .whitespacesAndNewlines),
            credential: credential)
        client = newClient
        sourceDeviceID = newClient.sourceDeviceID
        snapshot = nil
        status = "조회 대기"
    }

    func disconnect() {
        client = nil
        snapshot = nil
        sourceDeviceID = nil
        status = "연결 안 됨"
    }

    func refresh() async {
        guard let client, !isFetching else { return }
        isFetching = true
        defer { isFetching = false }
        let result = await client.fetch(now: Date())
        // A disconnected or replaced enrollment cannot update the UI.
        guard self.client === client else { return }
        status = result.status
        snapshot = result.snapshot
    }

    var displaySnapshot: MacRemoteQuotaV2Snapshot? {
        guard let snapshot,
              status == "FRESH_READ_ONLY" || status == "AGED_READ_ONLY",
              Date().timeIntervalSince(snapshot.observedAt) >= -3,
              Date().timeIntervalSince(snapshot.observedAt) < 360
        else { return nil }
        return snapshot
    }
}

@MainActor
final class MacRemoteQuotaV2Viewer {
    private struct Window: Decodable {
        let remainingPercent: Int?
        let resetAtUtcMs: Int64?
    }
    private struct Credits: Decodable {
        let availableCount: Int?
        let expiryDetailsUtcMs: [Int64?]?
    }
    private struct Frame: Decodable {
        let sourceDeviceId: String
        let sourceSessionId: String
        let sequence: Int64
        let collectedAtUtcMs: Int64
        let accountIdSha256: String
        let fiveHour: Window
        let sevenDay: Window
        let resetCredits: Credits
    }
    private struct Envelope: Decodable {
        let schemaVersion: Int
        let status: String
        let frame: Frame
        let actualSendAuthorized: Bool
        let automaticRetryAllowed: Bool
        let productionReservationAuthorized: Bool
        let senderTransitionAuthorized: Bool
    }

    enum ViewerError: Error { case invalidEnrollment }

    let sourceDeviceID: String
    private let endpoint: URL
    private let credential: String
    private let redirectBlocker = MacRemoteQuotaV2NoRedirect()
    private var accountHash: String?
    private var lastSession: String?
    private var lastSequence: Int64 = 0
    private var lastTimestamp: Int64 = 0
    private var lastFingerprint: String?
    private var lastSnapshot: MacRemoteQuotaV2Snapshot?
    private var quarantined = false

    init(origin: String, deviceID: String, credential: String) throws {
        guard let parts = URLComponents(string: origin),
              parts.scheme == "https",
              let host = parts.host, !host.isEmpty,
              parts.user == nil, parts.password == nil,
              parts.query == nil, parts.fragment == nil,
              parts.path.isEmpty || parts.path == "/",
              Self.valid(deviceID, max: 48, underscore: false),
              Self.valid(credential, max: 256, underscore: true),
              credential.utf8.count >= 32,
              let url = URL(string:
                  origin.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
                  + "/v2/quota/" + deviceID)
        else { throw ViewerError.invalidEnrollment }
        endpoint = url
        sourceDeviceID = deviceID
        self.credential = credential
    }

    func fetch(now: Date = Date()) async -> MacRemoteQuotaV2Result {
        if quarantined {
            return .init(status: "HOLD_ACCOUNT_SWITCH_REQUIRES_LOCAL_RESET", snapshot: nil)
        }
        var request = URLRequest(url: endpoint)
        request.httpMethod = "GET"
        request.timeoutInterval = 8
        request.setValue("Bearer " + credential, forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        do {
            let (data, response) = try await URLSession.shared.data(
                for: request, delegate: redirectBlocker)
            guard let response = response as? HTTPURLResponse,
                  response.statusCode == 200, data.count <= 4096
            else { return .init(status: "HOLD_REMOTE_RESPONSE", snapshot: nil) }
            return inspect(data, now: now)
        } catch {
            // Never report credential, URL or account hash.
            return .init(status: "HOLD_REMOTE_READ_FAILURE", snapshot: nil)
        }
    }

    /// Pure response inspection for deterministic synthetic fixtures.
    func inspect(_ data: Data, now: Date) -> MacRemoteQuotaV2Result {
        func held(_ status: String) -> MacRemoteQuotaV2Result {
            .init(status: status, snapshot: nil)
        }
        guard !quarantined else { return held("HOLD_ACCOUNT_SWITCH_REQUIRES_LOCAL_RESET") }
        guard data.count <= 4096,
              let dictionary = (try? JSONSerialization.jsonObject(with: data))
                  as? [String: Any],
              Self.exact(dictionary, [
                  "schemaVersion", "status", "frame",
                  "actualSendAuthorized", "automaticRetryAllowed",
                  "productionReservationAuthorized", "senderTransitionAuthorized"
              ]),
              let frame = dictionary["frame"] as? [String: Any],
              Self.exact(frame, [
                  "sourceDeviceId", "sourceSessionId", "sequence",
                  "collectedAtUtcMs", "accountIdSha256",
                  "fiveHour", "sevenDay", "resetCredits"
              ]),
              let fiveRaw = frame["fiveHour"] as? [String: Any],
              let sevenRaw = frame["sevenDay"] as? [String: Any],
              let creditRaw = frame["resetCredits"] as? [String: Any],
              Self.exact(fiveRaw, ["remainingPercent", "resetAtUtcMs"]),
              Self.exact(sevenRaw, ["remainingPercent", "resetAtUtcMs"]),
              Self.exact(creditRaw, ["availableCount", "expiryDetailsUtcMs"]),
              let decoded = try? JSONDecoder().decode(Envelope.self, from: data),
              decoded.schemaVersion == 2,
              ["FRESH_READ_ONLY", "AGED_READ_ONLY"].contains(decoded.status),
              !decoded.actualSendAuthorized, !decoded.automaticRetryAllowed,
              !decoded.productionReservationAuthorized,
              !decoded.senderTransitionAuthorized,
              decoded.frame.sourceDeviceId == sourceDeviceID,
              Self.hex(decoded.frame.sourceSessionId, count: 32),
              Self.hex(decoded.frame.accountIdSha256, count: 64),
              (1...1_000_000_000).contains(decoded.frame.sequence),
              (1_577_836_800_000...4_133_980_800_000)
                  .contains(decoded.frame.collectedAtUtcMs)
        else { return held("HOLD_INVALID_V2_RESPONSE") }

        let observation = decoded.frame
        let nowMs = now.timeIntervalSince1970 * 1000
        guard nowMs.isFinite,
              nowMs >= Double(observation.collectedAtUtcMs) - 3_000,
              nowMs - Double(observation.collectedAtUtcMs) < 360_000
        else { return held("HOLD_STALE_OR_FUTURE_V2_FRAME") }

        guard let five = Self.window(observation.fiveHour,
                                      at: observation.collectedAtUtcMs, ahead: 21_600_000),
              let seven = Self.window(observation.sevenDay,
                                       at: observation.collectedAtUtcMs, ahead: 691_200_000),
              let count = Self.credits(observation.resetCredits),
              five.percent != nil || seven.percent != nil || count.remaining != nil
        else { return held("HOLD_INVALID_V2_OBSERVATION") }

        if let prior = accountHash, prior != observation.accountIdSha256 {
            quarantined = true
            lastSnapshot = nil
            return held("HOLD_ACCOUNT_SWITCH_REQUIRES_LOCAL_RESET")
        }
        guard let canonical = try? JSONSerialization.data(
            withJSONObject: frame, options: [.sortedKeys])
        else { return held("HOLD_INVALID_V2_RESPONSE") }
        let fingerprint = SHA256.hash(data: canonical)
            .map { String(format: "%02x", $0) }.joined()
        let age = nowMs - Double(observation.collectedAtUtcMs)
        if observation.collectedAtUtcMs == lastTimestamp,
           observation.sourceSessionId == lastSession,
           observation.sequence == lastSequence,
           lastFingerprint == fingerprint,
           let lastSnapshot {
            return .init(status: age < 120_000 ? "FRESH_READ_ONLY" : "AGED_READ_ONLY",
                         snapshot: lastSnapshot)
        }
        guard lastTimestamp == 0 || (
            observation.collectedAtUtcMs > lastTimestamp &&
            (observation.sourceSessionId == lastSession
                ? observation.sequence > lastSequence
                : observation.sequence == 1)
        ) else { return held("HOLD_REPLAY_OR_REORDERED_VIEW") }

        let snapshot = MacRemoteQuotaV2Snapshot(
            sourceDeviceID: sourceDeviceID,
            fiveHourPercent: five.percent, sevenDayPercent: seven.percent,
            fiveHourReset: five.reset, sevenDayReset: seven.reset,
            resetCreditsRemaining: count.remaining,
            resetCreditExpiries: count.expiries,
            observedAt: Date(timeIntervalSince1970:
                TimeInterval(observation.collectedAtUtcMs) / 1000)
        )
        lastTimestamp = observation.collectedAtUtcMs
        lastSession = observation.sourceSessionId
        lastSequence = observation.sequence
        lastFingerprint = fingerprint
        accountHash = observation.accountIdSha256
        lastSnapshot = snapshot
        return .init(status: age < 120_000 ? "FRESH_READ_ONLY" : "AGED_READ_ONLY",
                     snapshot: snapshot)
    }

    private static func window(_ w: Window, at observed: Int64, ahead: Int64)
        -> (percent: Int?, reset: Date?)? {
        if let percent = w.remainingPercent, !(0...100).contains(percent) { return nil }
        if let ms = w.resetAtUtcMs,
           ms < 1_577_836_800_000 || ms > 4_133_980_800_000 ||
           ms < observed - 600_000 || ms > observed + ahead {
            return nil
        }
        return (w.remainingPercent, w.resetAtUtcMs.map {
            Date(timeIntervalSince1970: TimeInterval($0) / 1000)
        })
    }

    private static func credits(_ source: Credits)
        -> (remaining: Int?, expiries: [Date?]?)? {
        if let remaining = source.availableCount,
           !(0...Int(Int32.max)).contains(remaining) { return nil }
        guard let slots = source.expiryDetailsUtcMs else {
            return (source.availableCount, nil)
        }
        guard let remaining = source.availableCount,
              slots.count <= 2, slots.count <= remaining
        else { return nil }
        let ordered = slots.sorted {
            switch ($0, $1) {
            case let (a?, b?): return a < b
            case (_?, nil): return true
            case (nil, _?): return false
            default: return false
            }
        }
        guard ordered.allSatisfy({
            $0 == nil || (1_577_836_800_000...4_133_980_800_000).contains($0!)
        }) else { return nil }
        return (remaining, ordered.map {
            $0.map { Date(timeIntervalSince1970: TimeInterval($0) / 1000) }
        })
    }

    private static func exact(_ value: [String: Any], _ keys: Set<String>) -> Bool {
        Set(value.keys) == keys && value.count == keys.count
    }
    private static func hex(_ value: String, count: Int) -> Bool {
        value.utf8.count == count && value.utf8.allSatisfy {
            (48...57).contains(Int($0)) || (97...102).contains(Int($0))
        }
    }
    private static func valid(_ value: String, max: Int, underscore: Bool) -> Bool {
        !value.isEmpty && value.utf8.count <= max &&
        value.utf8.allSatisfy {
            (48...57).contains(Int($0)) || (65...90).contains(Int($0)) ||
            (97...122).contains(Int($0)) || $0 == 45 ||
            (underscore && $0 == 95)
        }
    }
}

private final class MacRemoteQuotaV2NoRedirect: NSObject, URLSessionTaskDelegate,
                                                 @unchecked Sendable {
    func urlSession(_ session: URLSession, task: URLSessionTask,
                    willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest,
                    completionHandler: @escaping (URLRequest?) -> Void) {
        completionHandler(nil)
    }
}
