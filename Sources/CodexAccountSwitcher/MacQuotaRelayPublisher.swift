import CryptoKit
import Foundation
import SwitcherCore

/// P3 quota-only publisher. Disabled unless explicitly configured via a
/// process-local HTTPS origin and distinct telemetry credential.
/// Sender/Viewer Telegram preferences do NOT authorize this transport.
@MainActor
final class MacQuotaRelayPublisher {
    private let url: URL?
    private let credential: String?
    private let sourceDeviceId: String?
    private let sessionID = UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased()
    private var sequence: Int64 = 0
    private let redirectBlocker = MacQuotaRelayNoRedirect()

    init(environment: [String: String] = ProcessInfo.processInfo.environment) {
        let device = environment["CODEX_SWITCHER_QUOTA_RELAY_SOURCE_DEVICE_ID"]
        let token = environment["CODEX_SWITCHER_QUOTA_RELAY_PUBLISH_TOKEN"]
        let origin = environment["CODEX_SWITCHER_QUOTA_RELAY_HTTPS_ORIGIN"]
        guard environment["CODEX_SWITCHER_QUOTA_RELAY_ENABLE"] == "PUBLISH_VIEW_ONLY",
              let device, !device.isEmpty, device.utf8.count <= 48,
              device.utf8.allSatisfy({
                  (48...57).contains(Int($0)) || (65...90).contains(Int($0)) ||
                  (97...122).contains(Int($0)) || $0 == 45
              }),
              let token, (32...256).contains(token.utf8.count),
              token.utf8.allSatisfy({
                  (48...57).contains(Int($0)) || (65...90).contains(Int($0)) ||
                  (97...122).contains(Int($0)) || $0 == 45 || $0 == 95
              }),
              let origin, let components = URLComponents(string: origin),
              components.scheme == "https", components.host != nil,
              components.user == nil, components.password == nil,
              components.query == nil, components.fragment == nil,
              components.path.isEmpty || components.path == "/",
              let endpoint = URL(string:
                  origin.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
                  + "/v1/quota/" + device)
        else {
            url = nil
            credential = nil
            sourceDeviceId = nil
            return
        }
        url = endpoint
        credential = token
        sourceDeviceId = device
    }

    var isEnabled: Bool { url != nil && credential != nil }

    func publish(_ observation: CodexMonitorObservation) async {
        guard let url, let credential, let device = sourceDeviceId,
              let frame = makeFrame(observation, device: device)
        else { return }
        sequence += 1
        var wire = frame
        wire["sequence"] = sequence
        guard JSONSerialization.isValidJSONObject(wire),
              let data = try? JSONSerialization.data(withJSONObject: wire, options: [.sortedKeys]),
              data.count <= 2048
        else { return }

        var request = URLRequest(url: url)
        request.httpMethod = "PUT"
        request.httpBody = data
        request.timeoutInterval = 8
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue("Bearer " + credential, forHTTPHeaderField: "Authorization")
        // No telemetry URL, token, account ID or provider credit ID in logs.
        do {
            let (_, response) = try await URLSession.shared.data(
                for: request, delegate: redirectBlocker)
            guard let http = response as? HTTPURLResponse,
                  http.statusCode == 202 else { return }
        } catch {
            // Best effort, no retry or Sender/Telegram action on failure.
        }
    }

    private func makeFrame(_ observation: CodexMonitorObservation,
                           device: String) -> [String: Any]? {
        guard let payload = try? JSONSerialization.jsonObject(with: observation.jsonData)
                    as? [String: Any],
              let accountID = payload["accountId"] as? String,
              !accountID.isEmpty, accountID.utf8.count <= 256,
              let allBuckets = payload["rateLimitsByLimitId"] as? [String: Any],
              let codex = allBuckets["codex"] as? [String: Any],
              let weekly = quota(codex["secondary"], expectedWindowMinutes: 10_080),
              let fiveHour = quota(codex["primary"], expectedWindowMinutes: 300),
              weekly["remainingPercent"] is Int,
              observation.observedAtMilliseconds >= 1_577_836_800_000,
              observation.observedAtMilliseconds <= 4_133_980_800_000
        else { return nil }

        let digest = SHA256.hash(data: Data(accountID.utf8))
        let hash = digest.map { String(format: "%02x", $0) }.joined()
        return [
            "schemaVersion": 1,
            "sourceDeviceId": device,
            "sourceSessionId": sessionID,
            "sequence": 1,
            "collectedAtUtcMs": observation.observedAtMilliseconds,
            "accountIdSha256": hash,
            "fiveHour": fiveHour,
            "sevenDay": weekly,
            "actualSendAuthorized": false,
            "automaticRetryAllowed": false,
            "productionReservationAuthorized": false,
            "senderTransitionAuthorized": false
        ]
    }

    private func quota(_ source: Any?,expectedWindowMinutes: Int)
    -> [String: Any]? {
        if source == nil || source is NSNull {
            return ["remainingPercent": NSNull(), "resetAtUtcMs": NSNull()]
        }
        guard let fields = source as? [String: Any],
              let window = fields["windowDurationMins"] as? Int,
              window == expectedWindowMinutes,
              let used = fields["usedPercent"] as? Int,
              (0...100).contains(used),
              let resetAtSeconds = fields["resetsAt"] as? Int64,
              resetAtSeconds >= 1_577_836_800,
              resetAtSeconds <= 4_133_980_800
        else { return nil }
        return ["remainingPercent": 100 - used,
                "resetAtUtcMs": resetAtSeconds * 1000]
    }
}

private final class MacQuotaRelayNoRedirect: NSObject, URLSessionTaskDelegate, @unchecked Sendable {
    func urlSession(_ session: URLSession, task: URLSessionTask,
                    willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest,
                    completionHandler: @escaping (URLRequest?) -> Void) {
        completionHandler(nil)
    }
}
