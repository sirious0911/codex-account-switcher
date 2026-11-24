import Foundation
import Testing
@testable import CodexAccountSwitcher

/// The URLSession adapter is tested with an injected, local-only reply.
/// No test issues network traffic or loads Keychain credentials.
@MainActor
struct MacTelegramStrictHttpsPosterTests {
    private actor Requests {
        var count = 0
        func record() { count += 1 }
        func total() -> Int { count }
    }

    private func request() throws -> URLRequest {
        let credential = try #require(MacTelegramBotApiContract.Credentials(
            token: "1234567890:abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ123456",
            chatId: "-1001234567890"))
        return try #require(MacTelegramBotApiContract.makeRequest(
            lane: .fiveHour, credentials: credential,
            text: MacTelegramBotApiContract.defaultText(for: .fiveHour)))
    }

    private func reply(url: URL, statusCode: Int) throws -> HTTPURLResponse {
        try #require(HTTPURLResponse(
            url: url, statusCode: statusCode,
            httpVersion: "HTTP/1.1",
            headerFields: ["Content-Type": "application/json"]))
    }

    @Test func exactRequestInvokesSyntheticPOSTOnlyOnce() async throws {
        let calls = Requests()
        let poster = MacTelegramStrictHttpsPoster(
            syntheticTransport: { req in
                await calls.record()
                let data = Data(
                    #"{"ok":true,"result":{"message_id":42,"chat":{"id":-1001234567890}}}"#.utf8)
                let url = try #require(req.url)
                let response = try #require(HTTPURLResponse(
                    url: url, statusCode: 200,
                    httpVersion: "HTTP/1.1", headerFields: nil))
                return (data, response)
            })
        defer { poster.dispose() }
        let (data, status) = try await poster.postOnce(request())
        #expect(status == 200)
        #expect(MacTelegramBotApiContract.acknowledgedMessageId(
            bytes: data, statusCode: status, expectedChatId: -1001234567890) == 42)
        #expect(await calls.total() == 1)
    }

    @Test func rejectsCrossOriginAndGetBeforeTransport() async throws {
        let calls = Requests()
        let poster = MacTelegramStrictHttpsPoster(
            syntheticTransport: { req in
                await calls.record()
                let url = try #require(req.url)
                return (Data(), try #require(HTTPURLResponse(
                    url: url, statusCode: 200, httpVersion: nil, headerFields: nil)))
            })
        defer { poster.dispose() }
        var wrongHost = try request()
        wrongHost.url = URL(string: "https://example.invalid/not-telegram")
        do {
            _ = try await poster.postOnce(wrongHost)
            Issue.record("A different host must never be contacted")
        } catch {
            #expect(error is MacTelegramStrictHttpsPoster.PosterError)
        }
        var method = try request()
        method.httpMethod = "GET"
        do {
            _ = try await poster.postOnce(method)
            Issue.record("A GET must never trigger a bot send")
        } catch {
            #expect(error is MacTelegramStrictHttpsPoster.PosterError)
        }
        #expect(await calls.total() == 0)
    }

    @Test func syntheticRedirectAndChangedResponseURLAreRejected() async throws {
        let initial = try request()
        let originalURL = try #require(initial.url)
        let redirected = URL(string: "https://example.invalid/redirect")!

        for (code, responseURL) in [(302, originalURL), (200, redirected)] {
            let calls = Requests()
            let poster = MacTelegramStrictHttpsPoster(
                syntheticTransport: { _ in
                    await calls.record()
                    let response = try #require(HTTPURLResponse(
                        url: responseURL, statusCode: code,
                        httpVersion: nil,
                        headerFields: ["Location": redirected.absoluteString]))
                    return (Data(#"{"ok":true}"#.utf8), response)
                })
            do {
                _ = try await poster.postOnce(initial)
                Issue.record("Redirect-shaped replies are not Bot API ACKs")
            } catch {
                #expect(error is MacTelegramStrictHttpsPoster.PosterError)
            }
            #expect(await calls.total() == 1)
            poster.dispose()
        }
    }
}
