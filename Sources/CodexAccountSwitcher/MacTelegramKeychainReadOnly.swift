import Foundation
import Security

/// Credential access is explicitly local to the future Mac mini executor.
/// Reading this source file or constructing the reader accesses no Keychain
/// item. AppModel.live() never creates or invokes this reader.
@MainActor
protocol MacTelegramBotCredentialSource {
    func load(for lane: MacTelegramQuotaEventIdentity.Lane)
        -> MacTelegramBotApiContract.Credentials?
}

/// READ ONLY. Existing Windows DPAPI bot tokens are never copied here.
/// The user must separately provision Mac Keychain items after approval.
@MainActor
final class MacTelegramKeychainReadOnly: MacTelegramBotCredentialSource {
    static let service = "CodexAccountSwitcher.Personal.Telegram.v1"

    func load(for lane: MacTelegramQuotaEventIdentity.Lane)
        -> MacTelegramBotApiContract.Credentials? {
        let account: String
        switch lane {
        case .fiveHour: account = "token.monitor"
        case .weekly: account = "token.alert"
        }
        guard let token = get(account: account),
              let chatId = get(account: "chat.shared")
        else { return nil }
        return MacTelegramBotApiContract.Credentials(
            token: token, chatId: chatId)
    }

    private func get(account: String) -> String? {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: Self.service,
            kSecAttrAccount as String: account,
            kSecMatchLimit as String: kSecMatchLimitOne,
            kSecReturnData as String: true
        ]
        var raw: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &raw) == errSecSuccess,
              let bytes = raw as? Data,
              bytes.count <= 512,
              let value = String(data: bytes, encoding: .utf8)
        else { return nil }
        return value
    }
}
