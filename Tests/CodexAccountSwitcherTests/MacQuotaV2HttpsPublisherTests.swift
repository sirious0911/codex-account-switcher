import Foundation
import SwitcherCore
import Testing
@testable import CodexAccountSwitcher

/// Synthetic URLRequest injection. Never uses URLSession/network, real
/// account credentials, Keychain, Codex commands, or Telegram.
@MainActor
struct MacQuotaV2HttpsPublisherTests {
    private let now = Date(timeIntervalSince1970: 1_800_000_000)
    private let profileID = UUID(uuidString: "12345678-0000-4000-8000-000000000001")!
    private var testCredential: String { String(repeating: "p", count: 48) }

    private func verifiedFrame() throws -> Data {
        let profile = AccountProfile(id: profileID, displayName: "Synthetic",
            email: nil, accountID: "synthetic-mac-provider", createdAt: now)
        let usage = WeeklyUsage(
            remainingPercent: 62, resetsAt: now.addingTimeInterval(172_800),
            fiveHourRemainingPercent: 85,
            fiveHourResetsAt: now.addingTimeInterval(3_600),
            resetCreditsRemaining: 2, resetCredits: [
                BankedResetCredit(id: "private-r1", status: "available",
                    resetType: "codexRateLimits",
                    expiresAt: now.addingTimeInterval(300_000)),
                BankedResetCredit(id: "private-r2", status: "available",
                    resetType: "codexRateLimits",
                    expiresAt: now.addingTimeInterval(200_000))
            ])
        let observation = try #require(CodexMonitorPayloadEncoder.encode(
            profile: profile, usage: usage, planType: "plus",
            ordinaryUsageAllowed: true, observedAt: now.addingTimeInterval(-1)))
        let encoder = try #require(MacQuotaV2FrameEncoder(sourceDeviceID: "macmini-a"))
        encoder.observeActiveProfile(profileID, identityConfirmed: true,
            isMutating: false, switchPending: false,
            now: now.addingTimeInterval(-5))
        return try #require(encoder.makeWeeklyFrame(
            profile: profile, observation: observation, now: now))
    }

    private func publisher(_ fixture: MacV2SyntheticTransport)
      -> MacQuotaV2HttpsPublisher? {
        MacQuotaV2HttpsPublisher(
            origin: "https://example.invalid/",
            sourceDeviceID: "macmini-a",
            credentialProvider: { testCredential },
            syntheticTransport: { request in try await fixture.send(request) })
    }

    @Test func sendsActualEncoderOutputAsOneVersionedRPut() async throws {
        let fixture = MacV2SyntheticTransport(status: 202)
        let sender = try #require(publisher(fixture))
        let frame = try verifiedFrame()
        #expect(await sender.publishOnce(frame))
        let evidence = await fixture.evidence()
        #expect(evidence.calls == 1)
        #expect(evidence.path == "/v2/quota/macmini-a")
        #expect(evidence.method == "PUT")
        #expect(evidence.contentType == "application/json")
        #expect(evidence.auth == "Bearer " + testCredential)
        #expect(evidence.body == frame)
        #expect(frame.count <= 2048)
        let string = try #require(String(data: frame, encoding: .utf8))
        #expect(!string.contains("synthetic-mac-provider"))
        #expect(!string.contains("private-r1"))
        #expect(!string.contains("private-r2"))
        let root = try #require(
            JSONSerialization.jsonObject(with: frame) as? [String: Any])
        let credits = try #require(root["resetCredits"] as? [String: Any])
        #expect(credits["availableCount"] as? Int == 2)
        #expect((credits["expiryDetailsUtcMs"] as? [Int64])?.count == 2)
        sender.dispose()
        #expect(!(await sender.publishOnce(frame)))
        #expect((await fixture.evidence()).calls == 1)
    }

    @Test func invalidFramesNeverReachTransport() async throws {
        let fixture = MacV2SyntheticTransport(status: 202)
        let sender = try #require(publisher(fixture))
        let valid = try verifiedFrame()
        var bad = try #require(
            JSONSerialization.jsonObject(with: valid) as? [String: Any])
        bad["sourceDeviceId"] = "windows-b"
        let wrongSource = try JSONSerialization.data(withJSONObject: bad)
        #expect(!(await sender.publishOnce(wrongSource)))
        bad["sourceDeviceId"] = "macmini-a"
        bad["senderTransitionAuthorized"] = true
        let privileged = try JSONSerialization.data(withJSONObject: bad)
        #expect(!(await sender.publishOnce(privileged)))
        bad["senderTransitionAuthorized"] = false
        bad["schemaVersion"] = 1
        let downgrade = try JSONSerialization.data(withJSONObject: bad)
        #expect(!(await sender.publishOnce(downgrade)))
        #expect(!(await sender.publishOnce(Data(repeating: 65, count: 2049))))
        #expect((await fixture.evidence()).calls == 0)
    }

    @Test func rejectsInsecureOriginAndShortCredentials() async throws {
        let fixture = MacV2SyntheticTransport(status: 202)
        #expect(MacQuotaV2HttpsPublisher(
            origin: "http://example.invalid/",sourceDeviceID: "macmini-a",
            credentialProvider: { testCredential },
            syntheticTransport: { req in try await fixture.send(req) }) == nil)
        #expect(MacQuotaV2HttpsPublisher(
            origin: "https://example.invalid/other",sourceDeviceID: "macmini-a",
            credentialProvider: { testCredential },
            syntheticTransport: { req in try await fixture.send(req) }) == nil)
        #expect(MacQuotaV2HttpsPublisher(
            origin: "https://example.invalid/",sourceDeviceID: "bad/id",
            credentialProvider: { testCredential },
            syntheticTransport: { req in try await fixture.send(req) }) == nil)
        let invalid = try #require(MacQuotaV2HttpsPublisher(
            origin: "https://example.invalid/",sourceDeviceID: "macmini-a",
            credentialProvider: { "short" },
            syntheticTransport: { req in try await fixture.send(req) }))
        #expect(!(await invalid.publishOnce(try verifiedFrame())))
        #expect((await fixture.evidence()).calls == 0)
    }

    @Test func rejects401AndRedirectWithoutRetry() async throws {
        let frame = try verifiedFrame()
        for status in [401, 302, 503] {
            let fixture = MacV2SyntheticTransport(status: status)
            let sender = try #require(publisher(fixture))
            #expect(!(await sender.publishOnce(frame)))
            #expect((await fixture.evidence()).calls == 1)
        }
    }

    @Test func accountSwitchCancelsSingleInFlightRequest() async throws {
        let fixture = MacV2SyntheticTransport(status: 202, delay: true)
        let sender = try #require(publisher(fixture))
        let frame = try verifiedFrame()
        let running = Task { await sender.publishOnce(frame) }
        var entered = false
        for _ in 0..<100 {
            if (await fixture.evidence()).calls == 1 {
                entered = true
                break
            }
            try await Task.sleep(for: .milliseconds(10))
        }
        #expect(entered)
        #expect(!(await sender.publishOnce(frame)))
        sender.invalidateEpoch()
        #expect(!(await running.value))
        #expect((await fixture.evidence()).calls == 1)
    }
}

private actor MacV2SyntheticTransport {
    let status: Int
    let delay: Bool
    var calls = 0
    var path: String?
    var method: String?
    var contentType: String?
    var auth: String?
    var body: Data?

    init(status: Int, delay: Bool = false) {
        self.status = status
        self.delay = delay
    }

    func send(_ request: URLRequest) async throws -> (Data, URLResponse) {
        calls += 1
        path = request.url?.path
        method = request.httpMethod
        contentType = request.value(forHTTPHeaderField: "Content-Type")
        auth = request.value(forHTTPHeaderField: "Authorization")
        body = request.httpBody
        if delay { try await Task.sleep(for: .seconds(30)) }
        let response = HTTPURLResponse(
            url: request.url!, statusCode: status,
            httpVersion: "HTTP/1.1", headerFields: [:])!
        return (Data(), response)
    }

    func evidence() -> (calls: Int, path: String?, method: String?,
                        contentType: String?, auth: String?, body: Data?) {
        (calls,path,method,contentType,auth,body)
    }
}
