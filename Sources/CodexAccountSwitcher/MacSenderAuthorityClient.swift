import CoreFoundation
import CryptoKit
import Foundation
import Security

/// macOS API client for the existing shared Authority TLS/SQLite service.
/// Authenticated lease and event observations are NOT Telegram send permits.
/// Explicitly constructed, not connected to app startup, account switching,
/// any Telegram bot, Keychain, or automatic background operation.
@MainActor
final class MacSenderAuthorityClient {
    typealias Transport = @Sendable (URLRequest) async throws -> (Data, URLResponse)

    private let origin: URL
    private let deviceID: String
    private var secret: Data
    private let injectedTransport: Transport?
    private let session: URLSession
    private var disabled = false
    private var generation: UInt64 = 0

    init?(origin: String, deviceID: String, keyHex: String,
          certificateSHA256: String,
          syntheticTransport: Transport? = nil) {
        guard let urlParts = URLComponents(string: origin),
              urlParts.scheme == "https",
              let host = urlParts.host, !host.isEmpty,
              urlParts.user == nil, urlParts.password == nil,
              urlParts.query == nil, urlParts.fragment == nil,
              urlParts.path.isEmpty || urlParts.path == "/",
              !origin.contains("#"), !origin.contains("?"),
              Self.validDevice(deviceID),
              let key = Self.hexBytes(keyHex), key.count == 32,
              let pin = Self.hexBytes(certificateSHA256), pin.count == 32,
              let base = URL(string: origin),
              base.scheme == "https", base.user == nil, base.password == nil
        else { return nil }

        self.origin = base
        self.deviceID = deviceID
        self.secret = key
        self.injectedTransport = syntheticTransport

        let configuration = URLSessionConfiguration.ephemeral
        configuration.requestCachePolicy = .reloadIgnoringLocalCacheData
        configuration.urlCache = nil
        configuration.httpCookieStorage = nil
        configuration.tlsMinimumSupportedProtocolVersion = .TLSv13
        let gate = MacAuthorityPinnedTLSDelegate(host: host,
            pinnedCertificateHash: pin)
        session = URLSession(configuration: configuration,
            delegate: gate, delegateQueue: nil)
    }

    func dispose() {
        guard !disabled else { return }
        disabled = true
        generation &+= 1
        let secretSize = secret.count
        secret.resetBytes(in: 0..<secretSize)
        session.invalidateAndCancel()
    }

    static func canonical(deviceID: String, method: String, path: String,
                          timestampMs: Int64, nonce: String,
                          bodyUtf8: String) -> String {
        let hash = digestHex(Data(bodyUtf8.utf8))
        return [
            "CODEX_SHARED_SENDER_AUTH_V1", deviceID, method, path,
            String(timestampMs), nonce, hash, ""
        ].joined(separator: "\n")
    }

    static func signature(keyHex: String, deviceID: String, method: String,
                          path: String, timestampMs: Int64,
                          nonce: String, bodyUtf8: String) -> String? {
        guard let key = hexBytes(keyHex), key.count == 32,
              validDevice(deviceID),
              method == "GET" || method == "POST",
              validNonce(nonce)
        else { return nil }
        let frame = canonical(deviceID: deviceID, method: method,
            path: path, timestampMs: timestampMs, nonce: nonce,
            bodyUtf8: bodyUtf8)
        let mac = HMAC<SHA256>.authenticationCode(
            for: Data(frame.utf8), using: SymmetricKey(data: key))
        return hex(Data(mac))
    }

    func status(now: Date = Date()) async -> MacSenderAuthorityResult {
        await call("GET", "/v1/status", "", now: now)
    }

    func event(_ key: String, now: Date = Date()) async
    -> MacSenderAuthorityResult {
        guard Self.validEvent(key) else { return .hold("HOLD_CLIENT_INPUT") }
        return await call("GET", "/v1/events/" + key, "", now: now)
    }

    func acquire(ttlMs: Int, now: Date = Date()) async
    -> MacSenderAuthorityResult {
        guard Self.validTTL(ttlMs) else { return .hold("HOLD_CLIENT_INPUT") }
        return await call("POST", "/v1/lease/acquire",
            "{\"ttlMs\":\(ttlMs)}", now: now)
    }

    func renew(epoch: Int64, ttlMs: Int, now: Date = Date()) async
    -> MacSenderAuthorityResult {
        guard epoch > 0, Self.validTTL(ttlMs) else {
            return .hold("HOLD_CLIENT_INPUT")
        }
        return await call("POST", "/v1/lease/renew",
            "{\"epoch\":\(epoch),\"ttlMs\":\(ttlMs)}",
            now: now, expectedEpoch: epoch)
    }

    func release(epoch: Int64, now: Date = Date()) async
    -> MacSenderAuthorityResult {
        guard epoch > 0 else { return .hold("HOLD_CLIENT_INPUT") }
        return await call("POST", "/v1/lease/release",
            "{\"epoch\":\(epoch)}", now: now, expectedEpoch: epoch)
    }

    func reserve(epoch: Int64, eventKey: String, lane: String,
                 now: Date = Date()) async -> MacSenderAuthorityResult {
        guard epoch > 0, Self.validEvent(eventKey),
              ["FIVE_HOUR_100", "WEEKLY_100"].contains(lane)
        else { return .hold("HOLD_CLIENT_INPUT") }
        return await call("POST", "/v1/events/reserve",
            "{\"epoch\":\(epoch),\"eventKey\":\"\(eventKey)\",\"lane\":\"\(lane)\"}",
            now: now, expectedEvent: eventKey)
    }

    func markUncertain(epoch: Int64, eventKey: String,
                       now: Date = Date()) async -> MacSenderAuthorityResult {
        guard epoch > 0, Self.validEvent(eventKey) else {
            return .hold("HOLD_CLIENT_INPUT")
        }
        return await call("POST", "/v1/events/uncertain",
            "{\"epoch\":\(epoch),\"eventKey\":\"\(eventKey)\"}", now: now)
    }

    private func call(_ method: String, _ path: String, _ body: String,
                      now: Date, expectedEpoch: Int64? = nil,
                      expectedEvent: String? = nil) async
    -> MacSenderAuthorityResult {
        guard !disabled,
              let url = URL(string: path, relativeTo: origin)?.absoluteURL,
              url.scheme == "https", url.host == origin.host,
              body.utf8.count <= 1024, now.timeIntervalSince1970.isFinite,
              now.timeIntervalSince1970 >= 1_577_836_800,
              now.timeIntervalSince1970 <= 4_133_980_800
        else { return .hold("HOLD_CLIENT_INPUT") }

        let timestamp = Int64(now.timeIntervalSince1970 * 1000)
        var nonce = [UInt8](repeating: 0, count: 16)
        let randomness = SecRandomCopyBytes(kSecRandomDefault, nonce.count, &nonce)
        guard randomness == errSecSuccess else {
            return .hold("HOLD_RANDOMNESS")
        }
        let nonceHex = Self.hex(Data(nonce))
        let frame = Self.canonical(deviceID: deviceID, method: method,
            path: path, timestampMs: timestamp, nonce: nonceHex, bodyUtf8: body)
        let signature = Self.hex(Data(HMAC<SHA256>.authenticationCode(
            for: Data(frame.utf8), using: SymmetricKey(data: secret))))

        var request = URLRequest(url: url)
        request.httpMethod = method
        request.timeoutInterval = 8
        request.setValue(deviceID, forHTTPHeaderField: "x-device-id")
        request.setValue(String(timestamp), forHTTPHeaderField: "x-timestamp-ms")
        request.setValue(nonceHex, forHTTPHeaderField: "x-nonce")
        request.setValue(signature, forHTTPHeaderField: "x-signature")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        if method == "POST" {
            request.httpBody = Data(body.utf8)
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        }

        let acceptedGeneration = generation
        do {
            let (responseData, response) = try await {
                if let injectedTransport {
                    return try await injectedTransport(request)
                }
                return try await session.data(for: request)
            }()
            guard !disabled, generation == acceptedGeneration,
                  let http = response as? HTTPURLResponse,
                  http.statusCode == 200, responseData.count <= 4096
            else { return .hold("HOLD_HTTP_OR_CANCELLED") }
            return Self.inspectResponse(responseData, requestTimestamp: timestamp,
                expectedEpoch: expectedEpoch, expectedEvent: expectedEvent)
        } catch {
            // A timed-out reservation may have committed on the server.
            // No automatic retries or Telegram send permission.
            return .hold("HOLD_NETWORK_OR_UNCERTAIN")
        }
    }

    static func inspectResponse(_ data: Data, requestTimestamp: Int64,
                                expectedEpoch: Int64? = nil,
                                expectedEvent: String? = nil)
    -> MacSenderAuthorityResult {
        guard !data.isEmpty, data.count <= 4096,
              StrictAuthorityJSONScanner.accepts(data),
              let root = try? JSONSerialization.jsonObject(with: data)
                as? [String: Any],
              number(root["schemaVersion"]) == 1,
              let code = root["code"] as? String,
              knownCodes.contains(code),
              ["actualSendAuthorized", "automaticRetryAllowed",
               "remoteSenderActivated"].allSatisfy({ isFalse(root[$0]) })
        else { return .hold("HOLD_RESPONSE") }

        var epoch: Int64?
        var expires: Int64?
        var eventKey: String?
        var lease: MacAuthorityLease?
        var event: MacAuthorityEvent?
        var authorityTime: Int64?

        if code == "LEASE_ACQUIRED" || code == "LEASE_RENEWED" {
            guard let value = number(root["epoch"]), value > 0,
                  let expiration = number(root["expiresAtMs"]),
                  expiration > requestTimestamp,
                  expiration - requestTimestamp <= 90_000
            else { return .hold("HOLD_RESPONSE") }
            epoch = value; expires = expiration
            if code == "LEASE_RENEWED", let expectedEpoch,
               expectedEpoch != value {
                return .hold("HOLD_RESPONSE_MISMATCH")
            }
        }
        if code == "LEASE_RELEASED" {
            guard let value = number(root["epoch"]), value > 0
            else { return .hold("HOLD_RESPONSE") }
            epoch = value
            if let expectedEpoch, expectedEpoch != value {
                return .hold("HOLD_RESPONSE_MISMATCH")
            }
        }
        if code == "EVENT_RESERVED_NO_SEND" {
            guard let key = root["eventKey"] as? String, validEvent(key)
            else { return .hold("HOLD_RESPONSE") }
            eventKey = key
            if let expectedEvent, key != expectedEvent {
                return .hold("HOLD_RESPONSE_MISMATCH")
            }
        }
        if code == "STATUS" {
            guard let ms = number(root["authorityTimeMs"]),
                  abs(Double(ms) - Double(requestTimestamp)) <= 30_000,
                  let raw = root["lease"] as? [String: Any],
                  Set(raw.keys) == Set(["owner", "epoch", "expiresAtMs"]),
                  let value = number(raw["epoch"]), value >= 0,
                  let expiration = number(raw["expiresAtMs"]), expiration >= 0,
                  let ownerField = raw["owner"]
            else { return .hold("HOLD_CLOCK_OR_SCHEMA") }
            let owner = ownerField as? String
            guard (ownerField is NSNull || (owner != nil && validDevice(owner!))),
                  (owner == nil ? expiration == 0 : (value > 0 && expiration > 0))
            else { return .hold("HOLD_CLOCK_OR_SCHEMA") }
            lease = .init(ownerDevice: owner, epoch: value, expiresAtMs: expiration)
            authorityTime = ms
        }
        if code == "EVENT_FOUND" {
            guard let item = root["event"] as? [String: Any],
                  Set(item.keys) == Set(["lane", "state", "owner", "epoch"]),
                  let lane = item["lane"] as? String,
                  ["FIVE_HOUR_100", "WEEKLY_100"].contains(lane),
                  let state = item["state"] as? String,
                  ["RESERVED_BEFORE_SEND", "SEND_OUTCOME_UNCERTAIN"].contains(state),
                  let owner = item["owner"] as? String, validDevice(owner),
                  let value = number(item["epoch"]), value > 0
            else { return .hold("HOLD_RESPONSE") }
            event = .init(lane: lane, state: state, ownerDevice: owner, epoch: value)
        }
        if code == "EVENT_ABSENT" && !(root["event"] is NSNull) {
            return .hold("HOLD_RESPONSE")
        }
        return .init(code: code, epoch: epoch, expiresAtMs: expires,
            eventKey: eventKey, lease: lease, event: event,
            authorityTimeMs: authorityTime)
    }

    private static let knownCodes: Set<String> = [
        "STATUS", "EVENT_FOUND", "EVENT_ABSENT",
        "LEASE_ACQUIRED", "LEASE_RENEWED", "LEASE_RELEASED",
        "EVENT_RESERVED_NO_SEND", "EVENT_MARKED_UNCERTAIN",
        "HOLD_OWNER_ACTIVE", "HOLD_STALE_OWNER", "HOLD_EVENT_ALREADY_RESERVED",
        "HOLD_EVENT_UNCHANGED", "HOLD_INVALID_BODY", "HOLD_EPOCH_EXHAUSTED",
        "HOLD_ROUTE", "HOLD_FORBIDDEN_ROLE", "HOLD_UNAUTHENTICATED",
        "HOLD_REPLAY", "HOLD_CLOCK", "HOLD_STORAGE_ERROR",
        "HOLD_INVALID_REQUEST", "HOLD_CLOSED"
    ]

    private static func number(_ value: Any?) -> Int64? {
        guard let n = value as? NSNumber,
              CFGetTypeID(n) != CFBooleanGetTypeID(),
              n.doubleValue.isFinite,
              n.doubleValue == Double(n.int64Value),
              (0...9_007_199_254_740_991).contains(n.int64Value)
        else { return nil }
        return n.int64Value
    }

    private static func isFalse(_ value: Any?) -> Bool {
        guard let n = value as? NSNumber,
              CFGetTypeID(n) == CFBooleanGetTypeID()
        else { return false }
        return !n.boolValue
    }

    private static func validTTL(_ ttl: Int) -> Bool { (1_000...60_000).contains(ttl) }
    private static func validEvent(_ key: String) -> Bool {
        key.utf8.count == 64 && key.utf8.allSatisfy(isLowerHex)
    }
    private static func validDevice(_ device: String?) -> Bool {
        guard let device, (1...48).contains(device.utf8.count) else { return false }
        return device.utf8.allSatisfy {
            (48...57).contains(Int($0)) || (65...90).contains(Int($0)) ||
            (97...122).contains(Int($0)) || $0 == 45
        }
    }
    private static func validNonce(_ nonce: String) -> Bool {
        nonce.utf8.count == 32 && nonce.utf8.allSatisfy(isLowerHex)
    }
    private static func isLowerHex(_ value: UInt8) -> Bool {
        (48...57).contains(Int(value)) || (97...102).contains(Int(value))
    }
    private static func hexBytes(_ string: String) -> Data? {
        let bytes = Array(string.utf8)
        guard bytes.count.isMultiple(of: 2), bytes.allSatisfy(isLowerHex)
        else { return nil }
        let result: [UInt8] = stride(from: 0, to: bytes.count, by: 2).map { i in
            func nibble(_ x: UInt8) -> UInt8 { x < 58 ? x - 48 : x - 87 }
            return (nibble(bytes[i]) << 4) | nibble(bytes[i+1])
        }
        return Data(result)
    }
    private static func digestHex(_ data: Data) -> String {
        hex(Data(SHA256.hash(data: data)))
    }
    private static func hex(_ data: Data) -> String {
        let chars = Array("0123456789abcdef".utf8)
        let result = data.flatMap { [chars[Int($0 >> 4)], chars[Int($0 & 15)]] }
        return String(decoding: result, as: UTF8.self)
    }
}

struct MacAuthorityLease: Equatable, Sendable {
    let ownerDevice: String?
    let epoch: Int64
    let expiresAtMs: Int64
}

struct MacAuthorityEvent: Equatable, Sendable {
    let lane: String
    let state: String
    let ownerDevice: String
    let epoch: Int64
}

/// No Authority response may become a direct Telegram send authorization.
struct MacSenderAuthorityResult: Equatable, Sendable {
    let code: String
    var epoch: Int64? = nil
    var expiresAtMs: Int64? = nil
    var eventKey: String? = nil
    var lease: MacAuthorityLease? = nil
    var event: MacAuthorityEvent? = nil
    var authorityTimeMs: Int64? = nil
    var actualSendAuthorized: Bool { false }
    var automaticRetryAllowed: Bool { false }
    var senderTransitionAuthorized: Bool { false }

    static func hold(_ reason: String) -> Self { .init(code: reason) }
}

private final class MacAuthorityPinnedTLSDelegate: NSObject,
    URLSessionDelegate, URLSessionTaskDelegate, @unchecked Sendable {
    private let host: String
    private let pinnedHash: Data
    init(host: String, pinnedCertificateHash: Data) {
        self.host = host
        pinnedHash = pinnedCertificateHash
    }
    func urlSession(_ session: URLSession, task: URLSessionTask,
                    willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest,
                    completionHandler: @escaping (URLRequest?) -> Void) {
        completionHandler(nil)
    }
    func urlSession(_ session: URLSession, didReceive challenge: URLAuthenticationChallenge,
                    completionHandler: @escaping (URLSession.AuthChallengeDisposition,
                                                  URLCredential?) -> Void) {
        guard challenge.protectionSpace.authenticationMethod ==
                  NSURLAuthenticationMethodServerTrust,
              challenge.protectionSpace.host == host,
              let trust = challenge.protectionSpace.serverTrust
        else {
            completionHandler(.cancelAuthenticationChallenge, nil)
            return
        }
        // Require both macOS CA/hostname validation AND separately enrolled
        // exact leaf DER SHA256. No self-signed trust bypass in production.
        let policy = SecPolicyCreateSSL(true, host as CFString)
        guard SecTrustSetPolicies(trust, policy) == errSecSuccess,
              SecTrustEvaluateWithError(trust, nil),
              let certificates = SecTrustCopyCertificateChain(trust)
                    as? [SecCertificate],
              let certificate = certificates.first
        else {
            completionHandler(.cancelAuthenticationChallenge, nil)
            return
        }
        let actual = Data(SHA256.hash(data: SecCertificateCopyData(certificate) as Data))
        guard actual.count == pinnedHash.count &&
              zip(actual, pinnedHash).reduce(UInt8(0), {
                  $0 | ($1.0 ^ $1.1)
              }) == 0
        else {
            completionHandler(.cancelAuthenticationChallenge, nil)
            return
        }
        completionHandler(.useCredential, URLCredential(trust: trust))
    }
}

/// Reject duplicate object keys, including escaped key aliases such as
/// "epoch" and "epoc\u0068", before Foundation's lossy JSON dictionary decode.
private struct StrictAuthorityJSONScanner {
    private let bytes: [UInt8]
    private var pos = 0
    private init(_ data: Data) { bytes = Array(data) }
    static func accepts(_ data: Data) -> Bool {
        var scanner = Self(data)
        guard scanner.value(depth: 0) else { return false }
        scanner.ws()
        return scanner.pos == scanner.bytes.count
    }
    private mutating func ws() {
        while pos < bytes.count && [9,10,13,32].contains(bytes[pos]) { pos += 1 }
    }
    private mutating func expect(_ value: UInt8) -> Bool {
        ws()
        guard pos < bytes.count, bytes[pos] == value else { return false }
        pos += 1; return true
    }
    private mutating func string() -> String? {
        ws()
        guard pos < bytes.count && bytes[pos] == 34 else { return nil }
        let start = pos
        pos += 1
        while pos < bytes.count {
            if bytes[pos] == 92 {
                pos += 2
                continue
            }
            if bytes[pos] == 34 {
                pos += 1
                return try? JSONDecoder().decode(String.self,
                    from: Data(bytes[start..<pos]))
            }
            if bytes[pos] < 32 { return nil }
            pos += 1
        }
        return nil
    }
    private mutating func value(depth: Int) -> Bool {
        guard depth <= 12 else { return false }
        ws()
        guard pos < bytes.count else { return false }
        if bytes[pos] == 123 {
            pos += 1; ws()
            if expect(125) { return true }
            var names = Set<String>()
            repeat {
                guard let key = string(), names.insert(key).inserted,
                      expect(58), value(depth: depth + 1)
                else { return false }
                if expect(125) { return true }
            } while expect(44)
            return false
        }
        if bytes[pos] == 91 {
            pos += 1; ws()
            if expect(93) { return true }
            repeat {
                guard value(depth: depth + 1) else { return false }
                if expect(93) { return true }
            } while expect(44)
            return false
        }
        if bytes[pos] == 34 { return string() != nil }
        for text in ["true", "false", "null"] {
            let word = Array(text.utf8)
            if bytes[pos...].starts(with: word) {
                pos += word.count
                return true
            }
        }
        let start = pos
        while pos < bytes.count &&
              ([45,43,46,69,101].contains(bytes[pos]) ||
               (48...57).contains(Int(bytes[pos]))) {
            pos += 1
        }
        return pos > start
    }
}
