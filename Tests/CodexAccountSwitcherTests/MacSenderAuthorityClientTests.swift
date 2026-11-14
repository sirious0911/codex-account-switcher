import CryptoKit
import Foundation
import Testing
@testable import CodexAccountSwitcher

/// macOS native Swift tests: injected synthetic HTTP responses, NO URLSession
/// network, no Keychain, no Telegram, no app startup or actual device authority.
@MainActor
struct MacSenderAuthorityClientTests {
    private let key = String(repeating: "01", count: 32)
    private let pin = String(repeating: "ab", count: 32)
    private let origin = "https://authority.invalid:9443/"
    private let at = Date(timeIntervalSince1970: 1_794_660_000)
    private let keyA = String(repeating: "c", count: 64)

    private func make(_ fake: MacSenderAuthorityFakeTransport)
    -> MacSenderAuthorityClient? {
        MacSenderAuthorityClient(origin: origin, deviceID: "windows-1",
            keyHex: key, certificateSHA256: pin,
            syntheticTransport: { request in try await fake.respond(request) })
    }

    private func setOverride(_ fake: MacSenderAuthorityFakeTransport,
                             _ fields: [String: Any]) async throws {
        let payload = try JSONSerialization.data(withJSONObject: fields)
        await fake.setOverride(String(decoding: payload, as: UTF8.self))
    }

    @Test func exactNodeAndWindowsHMACReference() {
        let raw = "{\"epoch\":7,\"eventKey\":\"" +
            keyA + "\",\"lane\":\"FIVE_HOUR_100\"}"
        let signature = MacSenderAuthorityClient.signature(keyHex: key,
            deviceID: "windows-1", method: "POST",
            path: "/v1/events/reserve", timestampMs: 1_794_660_000_000,
            nonce: String(repeating: "ab", count: 16), bodyUtf8: raw)
        #expect(signature ==
            "9bfcf2c57e0c65e7ea35a005255820e8522fa7038720d55478c3f8c6b531ab79")
        #expect(MacSenderAuthorityClient.signature(keyHex: "broken",
            deviceID: "windows-1", method: "GET",
            path: "/v1/status", timestampMs: 1_794_660_000_000,
            nonce: String(repeating: "ab", count: 16), bodyUtf8: "") == nil)
    }

    @Test func signedLeaseEventLifecycleAndNoSendPrivilege() async throws {
        let fake = MacSenderAuthorityFakeTransport()
        let client = try #require(make(fake))
        let status = await client.status(now: at)
        #expect(status.code == "STATUS")
        #expect(status.lease == .init(ownerDevice: nil, epoch: 0, expiresAtMs: 0))
        #expect(status.authorityTimeMs == 1_794_660_000_000)
        #expect(!status.actualSendAuthorized && !status.automaticRetryAllowed)
        let acquire = await client.acquire(ttlMs: 20_000, now: at)
        #expect(acquire.code == "LEASE_ACQUIRED" && acquire.epoch == 7)
        #expect(!acquire.senderTransitionAuthorized)
        let renew = await client.renew(epoch: 7, ttlMs: 15_000, now: at)
        #expect(renew.code == "LEASE_RENEWED" && renew.epoch == 7)
        let reserved = await client.reserve(epoch: 7, eventKey: keyA,
            lane: "FIVE_HOUR_100", now: at)
        #expect(reserved.code == "EVENT_RESERVED_NO_SEND")
        #expect(reserved.eventKey == keyA && !reserved.actualSendAuthorized)
        let event = await client.event(keyA, now: at)
        #expect(event.code == "EVENT_FOUND")
        #expect(event.event == .init(lane: "FIVE_HOUR_100",
            state: "RESERVED_BEFORE_SEND", ownerDevice: "windows-1", epoch: 7))
        let uncertain = await client.markUncertain(epoch: 7,
            eventKey: keyA, now: at)
        #expect(uncertain.code == "EVENT_MARKED_UNCERTAIN")
        let release = await client.release(epoch: 7, now: at)
        #expect(release.code == "LEASE_RELEASED" && release.epoch == 7)

        let calls = await fake.evidence()
        #expect(calls.count == 7)
        #expect(Set(calls.map(\.nonce)).count == 7)
        let validDevices = calls.allSatisfy { $0.device == "windows-1" }
        let validHosts = calls.allSatisfy { $0.origin == "authority.invalid" }
        let validSignatures = calls.allSatisfy { $0.signature.count == 64 }
        let validTimestamps = calls.allSatisfy {
            $0.timestamp == 1_794_660_000_000
        }
        #expect(validDevices)
        #expect(validHosts)
        #expect(validSignatures)
        #expect(validTimestamps)
        #expect(calls.first?.body == "")
        #expect(calls[3].body ==
            "{\"epoch\":7,\"eventKey\":\"" + keyA +
            "\",\"lane\":\"FIVE_HOUR_100\"}")
        #expect(calls[3].method == "POST" &&
            calls[3].path == "/v1/events/reserve")
        client.dispose()
        #expect((await client.status(now: at)).code == "HOLD_CLIENT_INPUT")
        #expect((await fake.evidence()).count == 7)
    }

    @Test func mismatchedLeaseEpochEventAndUnsafeServerClaimsFailClosed()
    async throws {
        let fake = MacSenderAuthorityFakeTransport()
        let client = try #require(make(fake))
        #expect((await client.acquire(ttlMs: 99, now: at)).code ==
            "HOLD_CLIENT_INPUT")
        #expect((await client.reserve(epoch: 7, eventKey: "bad",
            lane: "WEEKLY_100", now: at)).code == "HOLD_CLIENT_INPUT")
        #expect((await fake.evidence()).isEmpty)

        try await setOverride(fake, [
            "code": "LEASE_RENEWED", "epoch": 8,
            "expiresAtMs": 1_794_660_015_000
        ])
        #expect((await client.renew(epoch: 7, ttlMs: 15_000, now: at)).code ==
            "HOLD_RESPONSE_MISMATCH")
        try await setOverride(fake, ["code": "LEASE_RELEASED", "epoch": 8])
        #expect((await client.release(epoch: 7, now: at)).code ==
            "HOLD_RESPONSE_MISMATCH")
        try await setOverride(fake, ["code": "EVENT_RESERVED_NO_SEND",
            "eventKey": String(repeating: "d", count: 64)])
        #expect((await client.reserve(epoch: 7, eventKey: keyA,
            lane: "FIVE_HOUR_100", now: at)).code == "HOLD_RESPONSE_MISMATCH")

        try await setOverride(fake, [
            "code": "STATUS", "authorityTimeMs": 1_794_660_000_000,
            "lease": ["owner": "not/valid", "epoch": 4,
                "expiresAtMs": 1_794_660_010_000]
        ])
        #expect((await client.status(now: at)).code == "HOLD_CLOCK_OR_SCHEMA")

        try await setOverride(fake, ["code": "LEASE_ACQUIRED", "epoch": 7,
            "expiresAtMs": 1_794_660_015_000,
            "actualSendAuthorized": true])
        #expect((await client.acquire(ttlMs: 20_000, now: at)).code ==
            "HOLD_RESPONSE")
        try await setOverride(fake, [
            "code": "STATUS", "authorityTimeMs": 1_794_660_000_000,
            "lease": ["owner": NSNull(), "epoch": 0, "expiresAtMs": 0]
        ])
        await fake.setHTTPStatus(302)
        #expect((await client.status(now: at)).code == "HOLD_HTTP_OR_CANCELLED")
        await fake.setHTTPStatus(200)
        await fake.failNext()
        #expect((await client.acquire(ttlMs: 20_000, now: at)).code ==
            "HOLD_NETWORK_OR_UNCERTAIN")

        #expect((await fake.evidence()).count == 7)
        client.dispose()
    }

    @Test func duplicateJSONKeysIncludingEscapesAndBadFlagsAreRejected() {
        let timestamp: Int64 = 1_794_660_000_000
        func code(_ body: String) -> String {
            MacSenderAuthorityClient.inspectResponse(Data(body.utf8),
                requestTimestamp: timestamp).code
        }
        let safe = "\"schemaVersion\":1,\"code\":\"LEASE_ACQUIRED\"," +
            "\"actualSendAuthorized\":false,\"automaticRetryAllowed\":false," +
            "\"remoteSenderActivated\":false,\"epoch\":7," +
            "\"expiresAtMs\":1794660010000"
        #expect(code("{" + safe + "}") == "LEASE_ACQUIRED")
        #expect(code("{" + safe + ",\"epoch\":8}") == "HOLD_RESPONSE")
        #expect(code("{" + safe + ",\"epoc\\u0068\":8}") == "HOLD_RESPONSE")
        #expect(code("{" + safe.replacingOccurrences(
            of: "\"actualSendAuthorized\":false",
            with: "\"actualSendAuthorized\":true") + "}") == "HOLD_RESPONSE")
        #expect(code("{" + safe.replacingOccurrences(
            of: "\"automaticRetryAllowed\":false",
            with: "\"automaticRetryAllowed\":true") + "}") == "HOLD_RESPONSE")
        #expect(code("{" + safe.replacingOccurrences(
            of: "\"expiresAtMs\":1794660010000",
            with: "\"expiresAtMs\":1") + "}") == "HOLD_RESPONSE")
        #expect(code("{" + safe + "}\"trailing\"") == "HOLD_RESPONSE")
        #expect(code("{" + safe + ",\"invalid\":[[[[[[[[[[[[[1]]]]]]]]]]]]]}") ==
            "HOLD_RESPONSE")
    }

    @Test func rejectsUnsafeEnrollmentWithoutNetwork() async {
        let fake = MacSenderAuthorityFakeTransport()
        #expect(MacSenderAuthorityClient(origin: "http://authority.invalid",
            deviceID: "windows-1", keyHex: key,
            certificateSHA256: pin,
            syntheticTransport: { req in try await fake.respond(req) }) == nil)
        #expect(MacSenderAuthorityClient(origin: "https://authority.invalid/extra",
            deviceID: "windows-1", keyHex: key,
            certificateSHA256: pin,
            syntheticTransport: { req in try await fake.respond(req) }) == nil)
        #expect(MacSenderAuthorityClient(origin: origin, deviceID: "invalid/id",
            keyHex: key, certificateSHA256: pin,
            syntheticTransport: { req in try await fake.respond(req) }) == nil)
        #expect(MacSenderAuthorityClient(origin: origin, deviceID: "windows-1",
            keyHex: key, certificateSHA256: "short",
            syntheticTransport: { req in try await fake.respond(req) }) == nil)
        #expect((await fake.evidence()).isEmpty)
    }
}

private actor MacSenderAuthorityFakeTransport {
    struct Evidence: Sendable {
        let method: String
        let path: String
        let body: String
        let device: String
        let origin: String
        let nonce: String
        let signature: String
        let timestamp: Int64
    }
    enum SyntheticError: Error { case lostConnection }
    private var recorded: [Evidence] = []
    private var responseOverride: String?
    private var httpStatus = 200
    private var shouldFail = false

    func evidence() -> [Evidence] { recorded }
    func setOverride(_ json: String) { responseOverride = json }
    func setHTTPStatus(_ value: Int) { httpStatus = value }
    func failNext() { shouldFail = true }

    func respond(_ request: URLRequest) async throws -> (Data, URLResponse) {
        let method = request.httpMethod ?? ""
        let path = request.url?.path ?? ""
        let timestamp = Int64(request.value(forHTTPHeaderField: "x-timestamp-ms") ?? "") ?? 0
        let body = String(data: request.httpBody ?? Data(), encoding: .utf8) ?? ""
        let evidence = Evidence(method: method, path: path, body: body,
            device: request.value(forHTTPHeaderField: "x-device-id") ?? "",
            origin: request.url?.host ?? "",
            nonce: request.value(forHTTPHeaderField: "x-nonce") ?? "",
            signature: request.value(forHTTPHeaderField: "x-signature") ?? "",
            timestamp: timestamp)
        recorded.append(evidence)
        if shouldFail {
            shouldFail = false
            throw SyntheticError.lostConnection
        }
        var fields: [String: Any]
        if let overrideJSON = responseOverride {
            guard let decoded = try JSONSerialization.jsonObject(
                with: Data(overrideJSON.utf8)) as? [String: Any]
            else { throw SyntheticError.lostConnection }
            fields = decoded
        } else {
            switch path {
            case "/v1/status":
                fields = ["code": "STATUS", "authorityTimeMs": timestamp,
                    "lease": ["owner": NSNull(), "epoch": 0, "expiresAtMs": 0]]
            case "/v1/lease/acquire":
                fields = ["code": "LEASE_ACQUIRED", "epoch": 7,
                    "expiresAtMs": timestamp + 20_000]
            case "/v1/lease/renew":
                fields = ["code": "LEASE_RENEWED", "epoch": 7,
                    "expiresAtMs": timestamp + 20_000]
            case "/v1/lease/release":
                fields = ["code": "LEASE_RELEASED", "epoch": 7]
            case "/v1/events/reserve":
                let dictionary = try? JSONSerialization.jsonObject(
                    with: Data(body.utf8)) as? [String: Any]
                fields = ["code": "EVENT_RESERVED_NO_SEND",
                    "eventKey": dictionary?["eventKey"] ?? NSNull()]
            case "/v1/events/uncertain":
                fields = ["code": "EVENT_MARKED_UNCERTAIN"]
            default:
                fields = ["code": "EVENT_FOUND",
                    "event": ["lane": "FIVE_HOUR_100",
                        "state": "RESERVED_BEFORE_SEND",
                        "owner": "windows-1", "epoch": 7]]
            }
        }
        if fields["actualSendAuthorized"] == nil {
            fields["actualSendAuthorized"] = false
        }
        if fields["automaticRetryAllowed"] == nil {
            fields["automaticRetryAllowed"] = false
        }
        if fields["remoteSenderActivated"] == nil {
            fields["remoteSenderActivated"] = false
        }
        fields["schemaVersion"] = 1
        let raw = try JSONSerialization.data(withJSONObject: fields,
            options: [.sortedKeys])
        let response = HTTPURLResponse(url: request.url!,
            statusCode: httpStatus, httpVersion: "HTTP/1.1",
            headerFields: nil)!
        return (raw, response)
    }
}
