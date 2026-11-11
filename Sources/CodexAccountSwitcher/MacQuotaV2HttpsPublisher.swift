import Foundation

/// Opt-in macOS P3 V2 quota PUT transport. It does not read Codex,
/// register app observers, persist credentials, send Telegram messages,
/// or activate any network operation until publishOnce is called.
/// Existing V1 publisher and the operating app startup are unchanged.
@MainActor
final class MacQuotaV2HttpsPublisher {
    typealias Transport = @Sendable (URLRequest) async throws -> (Data, URLResponse)

    private let endpoint: URL
    private let sourceDeviceID: String
    private let credentialProvider: @MainActor () -> String?
    private let syntheticTransport: Transport?
    private let redirectBlocker = MacQuotaV2NoRedirect()
    private var generation: UInt64 = 0
    private var sending = false
    private var pending: Task<(Data, URLResponse), Error>?
    private var disposed = false

    init?(origin: String, sourceDeviceID: String,
          credentialProvider: @escaping @MainActor () -> String?,
          syntheticTransport: Transport? = nil) {
        guard let parts = URLComponents(string: origin),
              parts.scheme == "https",
              let host = parts.host, !host.isEmpty,
              parts.user == nil, parts.password == nil,
              parts.query == nil, parts.fragment == nil,
              parts.path.isEmpty || parts.path == "/",
              Self.valid(sourceDeviceID, max: 48, underscore: false),
              let target = URL(string: origin.trimmingCharacters(
                  in: CharacterSet(charactersIn: "/"))
                  + "/v2/quota/" + sourceDeviceID)
        else { return nil }
        endpoint = target
        self.sourceDeviceID = sourceDeviceID
        self.credentialProvider = credentialProvider
        self.syntheticTransport = syntheticTransport
    }

    /// Invalidate before account switching, reauthentication or loss of
    /// verified active identity; any queued result is discarded.
    func invalidateEpoch() {
        generation &+= 1
        pending?.cancel()
    }

    func dispose() {
        guard !disposed else { return }
        disposed = true
        invalidateEpoch()
    }

    /// Exactly one already-verified frame and no automatic retry.
    /// A nil/false return cannot authorize account or Telegram actions.
    func publishOnce(_ frame: Data) async -> Bool {
        guard !disposed, !sending,
              frame.count > 0, frame.count <= 2048,
              Self.validFrame(frame, for: sourceDeviceID),
              let token = credentialProvider(),
              Self.valid(token, max: 256, underscore: true),
              token.utf8.count >= 32
        else { return false }

        sending = true
        let acceptedEpoch = generation
        defer {
            sending = false
            pending = nil
        }

        var request = URLRequest(url: endpoint)
        request.httpMethod = "PUT"
        request.httpBody = frame
        request.timeoutInterval = 8
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue("Bearer " + token, forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        let injected = syntheticTransport
        let blocker = redirectBlocker
        let outgoingRequest = request
        let operation = Task<(Data, URLResponse), Error> {
            if let injected { return try await injected(outgoingRequest) }
            // OS certificate trust and hostname validation remain enabled.
            return try await URLSession.shared.data(for: outgoingRequest, delegate: blocker)
        }
        pending = operation
        do {
            let (_, response) = try await operation.value
            return !disposed && acceptedEpoch == generation &&
                !operation.isCancelled &&
                (response as? HTTPURLResponse)?.statusCode == 202
        } catch {
            // Credentials, account hash, URLs, frame contents and network
            // response bodies are never logged.
            return false
        }
    }

    private static func validFrame(_ data: Data, for source: String) -> Bool {
        guard let raw = try? JSONSerialization.jsonObject(with: data),
              let fields = raw as? [String: Any],
              Set(fields.keys) == Set([
                  "schemaVersion", "sourceDeviceId", "sourceSessionId",
                  "sequence", "collectedAtUtcMs", "accountIdSha256",
                  "fiveHour", "sevenDay", "resetCredits",
                  "actualSendAuthorized", "automaticRetryAllowed",
                  "productionReservationAuthorized", "senderTransitionAuthorized"
              ]),
              (fields["schemaVersion"] as? Int) == 2,
              fields["sourceDeviceId"] as? String == source,
              let session = fields["sourceSessionId"] as? String,
              hex(session, count: 32),
              let hash = fields["accountIdSha256"] as? String,
              hex(hash, count: 64),
              let sequence = fields["sequence"] as? Int, sequence > 0,
              let observed = fields["collectedAtUtcMs"] as? Int64,
              (1_577_836_800_000...4_133_980_800_000).contains(observed),
              fields["fiveHour"] is [String: Any],
              fields["sevenDay"] is [String: Any],
              fields["resetCredits"] is [String: Any],
              fields["actualSendAuthorized"] as? Bool == false,
              fields["automaticRetryAllowed"] as? Bool == false,
              fields["productionReservationAuthorized"] as? Bool == false,
              fields["senderTransitionAuthorized"] as? Bool == false
        else { return false }
        return true
    }

    private static func hex(_ value: String, count: Int) -> Bool {
        value.utf8.count == count && value.utf8.allSatisfy {
            (48...57).contains(Int($0)) || (97...102).contains(Int($0))
        }
    }

    private static func valid(_ value: String, max: Int, underscore: Bool) -> Bool {
        !value.isEmpty && value.utf8.count <= max && value.utf8.allSatisfy {
            (48...57).contains(Int($0)) || (65...90).contains(Int($0)) ||
            (97...122).contains(Int($0)) || $0 == 45 ||
            (underscore && $0 == 95)
        }
    }
}

private final class MacQuotaV2NoRedirect: NSObject, URLSessionTaskDelegate,
                                          @unchecked Sendable {
    func urlSession(_ session: URLSession, task: URLSessionTask,
                    willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest,
                    completionHandler: @escaping (URLRequest?) -> Void) {
        completionHandler(nil)
    }
}
