import Combine
import CryptoKit
import Foundation

/// P3 remote telemetry only. The enrollment is opt-in, memory-only,
/// and grants no Codex account or Telegram action.
struct MacRemoteQuotaSnapshot: Equatable, Sendable {
    let sourceDeviceID: String
    let fiveHourPercent: Int?
    let sevenDayPercent: Int?
    let fiveHourReset: Date?
    let sevenDayReset: Date?
    let observedAt: Date
}

struct MacRemoteQuotaResult: Sendable {
    let status: String
    let snapshot: MacRemoteQuotaSnapshot?
}

@MainActor
final class MacRemoteQuotaViewerModel: ObservableObject {
    @Published private(set) var status = "연결 안 됨"
    @Published private(set) var snapshot: MacRemoteQuotaSnapshot?
    @Published private(set) var isFetching = false
    @Published private(set) var sourceDeviceID: String?
    private var client: MacRemoteQuotaViewer?

    var isConnected: Bool { client != nil }

    func connect(origin: String, deviceID: String, credential: String) throws {
        let newClient = try MacRemoteQuotaViewer(
            origin: origin.trimmingCharacters(in: .whitespacesAndNewlines),
            deviceID: deviceID.trimmingCharacters(in: .whitespacesAndNewlines),
            credential: credential
        )
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
        // Never let a slow previous request replace a disconnected/new source.
        guard self.client === client else { return }
        status = result.status
        snapshot = result.snapshot
    }

    var displaySnapshot: MacRemoteQuotaSnapshot? {
        guard let snapshot,
              status == "FRESH_READ_ONLY" || status == "AGED_READ_ONLY",
              Date().timeIntervalSince(snapshot.observedAt) >= -3,
              Date().timeIntervalSince(snapshot.observedAt) < 360 else { return nil }
        return snapshot
    }
}

@MainActor
final class MacRemoteQuotaViewer {
    private struct Window: Decodable {
        let remainingPercent: Int?
        let resetAtUtcMs: Int64?
    }
    private struct Frame: Decodable {
        let sourceDeviceId: String
        let sourceSessionId: String
        let sequence: Int64
        let collectedAtUtcMs: Int64
        let accountIdSha256: String
        let fiveHour: Window
        let sevenDay: Window
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

    let sourceDeviceID: String
    private let endpoint: URL
    private let credential: String
    private let redirectBlocker = MacRemoteQuotaViewerNoRedirect()
    private var lastAccountHash: String?
    private var lastSession: String?
    private var lastSequence: Int64 = 0
    private var lastTimestamp: Int64 = 0
    private var lastEnvelopeFingerprint: String?
    private var lastSnapshot: MacRemoteQuotaSnapshot?
    private var quarantined = false

    init(origin: String, deviceID: String, credential: String) throws {
        guard let components = URLComponents(string: origin),
              components.scheme == "https",
              let host = components.host, !host.isEmpty,
              components.user == nil, components.password == nil,
              components.query == nil, components.fragment == nil,
              components.path.isEmpty || components.path == "/",
              Self.valid(deviceID, maxLength: 48, allowUnderscore: false),
              Self.valid(credential, maxLength: 256, allowUnderscore: true),
              credential.utf8.count >= 32,
              let endpoint = URL(string:
                  origin.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
                  + "/v1/quota/" + deviceID)
        else { throw ViewerError.invalidEnrollment }
        self.endpoint = endpoint
        self.sourceDeviceID = deviceID
        self.credential = credential
    }

    func fetch(now: Date = Date()) async -> MacRemoteQuotaResult {
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
                  response.statusCode == 200,
                  data.count <= 4096
            else { return .init(status: "HOLD_REMOTE_RESPONSE", snapshot: nil) }
            return inspect(data, now: now)
        } catch {
            // No retries, no logs containing the URL, token, or account hash.
            return .init(status: "HOLD_REMOTE_READ_FAILURE", snapshot: nil)
        }
    }

    /// Pure wire parser used by native macOS unit tests and real HTTPS GET.
    func inspect(_ data: Data, now: Date) -> MacRemoteQuotaResult {
        func held(_ reason: String) -> MacRemoteQuotaResult {
            .init(status: reason, snapshot: nil)
        }
        guard data.count <= 4096,
              let root = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any],
              Set(root.keys) == Set([
                "schemaVersion", "status", "frame", "actualSendAuthorized",
                "automaticRetryAllowed", "productionReservationAuthorized",
                "senderTransitionAuthorized"
              ]),
              let frame = root["frame"] as? [String: Any],
              Set(frame.keys) == Set([
                "sourceDeviceId", "sourceSessionId", "sequence",
                "collectedAtUtcMs", "accountIdSha256", "fiveHour", "sevenDay"
              ]),
              let five = frame["fiveHour"] as? [String: Any],
              let seven = frame["sevenDay"] as? [String: Any],
              Set(five.keys) == Set(["remainingPercent", "resetAtUtcMs"]),
              Set(seven.keys) == Set(["remainingPercent", "resetAtUtcMs"]),
              let decoded = try? JSONDecoder().decode(Envelope.self, from: data),
              decoded.schemaVersion == 1,
              ["FRESH_READ_ONLY", "AGED_READ_ONLY"].contains(decoded.status),
              !decoded.actualSendAuthorized, !decoded.automaticRetryAllowed,
              !decoded.productionReservationAuthorized, !decoded.senderTransitionAuthorized,
              decoded.frame.sourceDeviceId == sourceDeviceID,
              Self.isLowerHex(decoded.frame.sourceSessionId, size: 32),
              Self.isLowerHex(decoded.frame.accountIdSha256, size: 64),
              (1...1_000_000_000).contains(decoded.frame.sequence),
              (1_577_836_800_000...4_133_980_800_000).contains(decoded.frame.collectedAtUtcMs)
        else { return held("HOLD_REMOTE_INVALID_FRAME") }

        let received = decoded.frame
        let nowMilliseconds = Int64(now.timeIntervalSince1970 * 1000)
        let age = nowMilliseconds - received.collectedAtUtcMs
        guard age >= -3_000 && age < 360_000 else {
            return held("HOLD_REMOTE_STALE_OR_FUTURE")
        }
        guard let fiveWindow = Self.validate(received.fiveHour,
                                             observed: received.collectedAtUtcMs,
                                             limit: 21_600_000),
              let sevenWindow = Self.validate(received.sevenDay,
                                              observed: received.collectedAtUtcMs,
                                              limit: 691_200_000),
              fiveWindow.percent != nil || sevenWindow.percent != nil
        else { return held("HOLD_REMOTE_INVALID_WINDOW") }
        if let previous = lastAccountHash, previous != received.accountIdSha256 {
            quarantined = true
            lastSnapshot = nil
            return held("HOLD_ACCOUNT_SWITCH_REQUIRES_LOCAL_RESET")
        }
        guard let canonical = try? JSONSerialization.data(
            withJSONObject: frame, options: [.sortedKeys]) else {
            return held("HOLD_REMOTE_INVALID_FRAME")
        }
        // Relay status changes from FRESH to AGED without changing the
        // underlying observation; only the frame participates in replay identity.
        let fingerprint = SHA256.hash(data: canonical)
            .map { String(format: "%02x", $0) }.joined()
        if received.collectedAtUtcMs == lastTimestamp,
           received.sourceSessionId == lastSession,
           received.sequence == lastSequence,
           lastEnvelopeFingerprint == fingerprint,
           let lastSnapshot {
            return .init(status: age < 120_000 ? "FRESH_READ_ONLY" : "AGED_READ_ONLY",
                         snapshot: lastSnapshot)
        }
        guard lastTimestamp == 0 || (
            received.collectedAtUtcMs > lastTimestamp &&
            (received.sourceSessionId == lastSession
               ? received.sequence > lastSequence
               : received.sequence == 1)
        ) else { return held("HOLD_REMOTE_REPLAY_OR_REORDERED") }

        let snapshot = MacRemoteQuotaSnapshot(
            sourceDeviceID: sourceDeviceID,
            fiveHourPercent: fiveWindow.percent,
            sevenDayPercent: sevenWindow.percent,
            fiveHourReset: fiveWindow.reset,
            sevenDayReset: sevenWindow.reset,
            observedAt: Date(timeIntervalSince1970:
                TimeInterval(received.collectedAtUtcMs) / 1000)
        )
        lastTimestamp = received.collectedAtUtcMs
        lastSession = received.sourceSessionId
        lastSequence = received.sequence
        lastAccountHash = received.accountIdSha256
        lastEnvelopeFingerprint = fingerprint
        lastSnapshot = snapshot
        return .init(status: age < 120_000 ? "FRESH_READ_ONLY" : "AGED_READ_ONLY",
                     snapshot: snapshot)
    }

    private static func validate(_ window: Window, observed: Int64,
                                 limit: Int64)
    -> (percent: Int?, reset: Date?)? {
        if let value = window.remainingPercent, !(0...100).contains(value) {
            return nil
        }
        if let ms = window.resetAtUtcMs,
           ms < observed - 600_000 || ms > observed + limit ||
           ms < 1_577_836_800_000 || ms > 4_133_980_800_000 {
            return nil
        }
        return (window.remainingPercent, window.resetAtUtcMs.map {
            Date(timeIntervalSince1970: TimeInterval($0) / 1000)
        })
    }
    private static func isLowerHex(_ text: String, size: Int) -> Bool {
        text.utf8.count == size && text.utf8.allSatisfy {
            (48...57).contains(Int($0)) || (97...102).contains(Int($0))
        }
    }
    private static func valid(_ value: String, maxLength: Int,
                              allowUnderscore: Bool) -> Bool {
        !value.isEmpty && value.utf8.count <= maxLength &&
        value.utf8.allSatisfy {
            (48...57).contains(Int($0)) || (65...90).contains(Int($0)) ||
            (97...122).contains(Int($0)) || $0 == 45 ||
            (allowUnderscore && $0 == 95)
        }
    }
    enum ViewerError: Error { case invalidEnrollment }
}

private final class MacRemoteQuotaViewerNoRedirect: NSObject, URLSessionTaskDelegate,
                                                     @unchecked Sendable {
    func urlSession(_ session: URLSession, task: URLSessionTask,
                    willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest,
                    completionHandler: @escaping (URLRequest?) -> Void) {
        completionHandler(nil)
    }
}
