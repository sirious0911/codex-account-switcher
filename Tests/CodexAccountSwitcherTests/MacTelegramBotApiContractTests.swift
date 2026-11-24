import Foundation
import Testing
@testable import CodexAccountSwitcher

/// All credentials and HTTP replies below are invented offline fixtures.
/// No Keychain, URLSession or Telegram network request is performed.
@MainActor
struct MacTelegramBotApiContractTests {
    private let syntheticToken =
        "123456789:abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ123456789"

    private func credential() throws -> MacTelegramBotApiContract.Credentials {
        try #require(MacTelegramBotApiContract.Credentials(
            token: syntheticToken, chatId: "-1001234567890"))
    }

    @Test func laneRoutesAndNotificationSoundAreStrict() throws {
        let credential = try credential()
        for lane in [MacTelegramQuotaEventIdentity.Lane.fiveHour, .weekly] {
            let req = try #require(MacTelegramBotApiContract.makeRequest(
                lane: lane, credentials: credential,
                text: MacTelegramBotApiContract.defaultText(for: lane)))
            #expect(req.httpMethod == "POST")
            #expect(req.url?.scheme == "https")
            #expect(req.url?.host == "api.telegram.org")
            #expect(req.url?.path.hasSuffix("/sendMessage") == true)
            #expect(req.value(forHTTPHeaderField: "Content-Type") ==
                "application/json")
            let bytes = try #require(req.httpBody)
            let json = try #require(JSONSerialization.jsonObject(
                with: bytes) as? [String: Any])
            #expect((json["chat_id"] as? Int64) == -1_001_234_567_890)
            #expect((json["disable_notification"] as? Bool) ==
                (lane == .fiveHour))
            #expect(json["text"] as? String ==
                MacTelegramBotApiContract.defaultText(for: lane))
            #expect(json["allow_paid_broadcast"] == nil)
            #expect(json["parse_mode"] == nil)
            #expect(json["bot_token"] == nil)
        }
    }

    @Test func forgedOrIncompleteHTTP200NeverAcknowledges() throws {
        let good = Data(#"{"ok":true,"result":{"message_id":57,"chat":{"id":-1001234567890}}}"#.utf8)
        #expect(MacTelegramBotApiContract.acknowledgedMessageId(
            bytes: good, statusCode: 200, expectedChatId: -1001234567890) == 57)
        #expect(MacTelegramBotApiContract.acknowledgedMessageId(
            bytes: good, statusCode: 429, expectedChatId: -1001234567890) == nil)
        #expect(MacTelegramBotApiContract.acknowledgedMessageId(
            bytes: good, statusCode: 200, expectedChatId: 987) == nil)
        for raw in [
            #"{"ok":true}"#,
            #"{"ok":false,"result":{"message_id":57,"chat":{"id":-1001234567890}}}"#,
            #"{"ok":true,"result":{"message_id":57}}"#,
            #"{"ok":true,"result":{"message_id":0,"chat":{"id":-1001234567890}}}"#,
            #"{"ok":true,"result":{"message_id":-9,"chat":{"id":-1001234567890}}}"#,
            #"{"ok":true,"result":{"message_id":57,"chat":{"id":-1001234567891}}}"#,
            #"{"ok":"true","result":{"message_id":57,"chat":{"id":-1001234567890}}}"#,
            #"not json"#,
        ] {
            #expect(MacTelegramBotApiContract.acknowledgedMessageId(
                bytes: Data(raw.utf8), statusCode: 200,
                expectedChatId: -1001234567890) == nil)
        }
    }

    @Test func invalidCredentialsOrMessagesNeverCreateRequest() throws {
        #expect(MacTelegramBotApiContract.Credentials(
            token: "raw-token-from-elsewhere", chatId: "12") == nil)
        #expect(MacTelegramBotApiContract.Credentials(
            token: syntheticToken, chatId: "0") == nil)
        #expect(MacTelegramBotApiContract.Credentials(
            token: syntheticToken, chatId: "a-user-name") == nil)
        let credential = try credential()
        #expect(MacTelegramBotApiContract.makeRequest(
            lane: .fiveHour, credentials: credential, text: "") == nil)
        #expect(MacTelegramBotApiContract.makeRequest(
            lane: .weekly, credentials: credential,
            text: String(repeating: "x", count: 4097)) == nil)
    }
}
