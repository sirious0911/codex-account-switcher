import Foundation

/// MAC-ONLY, SOURCE-ONLY Telegram Bot API request/response contract.
/// No network calls, credentials, settings, or background work occur here.
/// Telegram documentation: https://core.telegram.org/bots/api#sendmessage
@MainActor
enum MacTelegramBotApiContract {
    struct Credentials: Equatable {
        let token: String
        let chatId: Int64

        init?(token: String, chatId: String) {
            let parts = token.split(separator: ":", omittingEmptySubsequences: false)
            guard parts.count == 2,
                  (4...20).contains(parts[0].utf8.count),
                  (24...128).contains(parts[1].utf8.count),
                  parts[0].utf8.allSatisfy({ (48...57).contains($0) }),
                  parts[1].utf8.allSatisfy({
                      (48...57).contains($0) || (65...90).contains($0) ||
                      (97...122).contains($0) || $0 == 45 || $0 == 95
                  }),
                  let numericChatId = Int64(chatId),
                  numericChatId != 0
            else { return nil }
            self.token = token
            self.chatId = numericChatId
        }
    }

    static func defaultText(for lane: MacTelegramQuotaEventIdentity.Lane) -> String {
        switch lane {
        case .fiveHour:
            "Codex 5시간 사용량이 100% 복구된 것을 확인했습니다."
        case .weekly:
            "Codex 7일 사용량이 100% 복구된 것을 확인했습니다."
        }
    }

    static func makeRequest(
        lane: MacTelegramQuotaEventIdentity.Lane,
        credentials: Credentials,
        text: String
    ) -> URLRequest? {
        guard !text.isEmpty, text.count <= 4096,
              let url = URL(string:
                "https://api.telegram.org/bot\(credentials.token)/sendMessage"),
              url.scheme == "https", url.host == "api.telegram.org",
              url.user == nil, url.password == nil, url.query == nil,
              url.fragment == nil
        else { return nil }

        // Exactly the intended lane: silent 5H Monitor, ordinary 7D Alert.
        // No paid broadcast, parse_mode, raw account IDs, or unrelated bot route.
        let payload: [String: Any] = [
            "chat_id": credentials.chatId,
            "text": text,
            "disable_notification": lane == .fiveHour,
            "link_preview_options": ["is_disabled": true]
        ]
        guard let body = try? JSONSerialization.data(
            withJSONObject: payload, options: [.sortedKeys])
        else { return nil }
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.httpBody = body
        request.timeoutInterval = 12
        request.cachePolicy = .reloadIgnoringLocalCacheData
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue("no-store", forHTTPHeaderField: "Cache-Control")
        return request
    }

    private struct BotResponse: Decodable {
        struct SentMessage: Decodable {
            struct Chat: Decodable { let id: Int64 }
            let message_id: Int64
            let chat: Chat
        }
        let ok: Bool
        let result: SentMessage?
    }

    /// A success-shaped HTTP 200 or a positive ID alone is NOT an ACK.
    /// Require Bot API ok=true, a positive message ID and the exact requested
    /// numeric chat id. Only the local post completion path may call this.
    static func acknowledgedMessageId(
        bytes: Data, statusCode: Int, expectedChatId: Int64
    ) -> Int64? {
        guard statusCode == 200, expectedChatId != 0,
              !bytes.isEmpty, bytes.count <= 32_768,
              let response = try? JSONDecoder().decode(
                  BotResponse.self, from: bytes),
              response.ok == true,
              let message = response.result,
              message.message_id > 0,
              message.chat.id == expectedChatId
        else { return nil }
        return message.message_id
    }
}
