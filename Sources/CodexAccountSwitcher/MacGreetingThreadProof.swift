import Foundation
import CryptoKit

/// Offline proof checker for a future account-bound existing-thread picker.
/// No live RPC or session construction; injected evidence is NOT send authority.
enum MacGreetingThreadProof {
    static let dedicatedTitle = "Codex 사용량 초기화 전용"

    struct Identity: Equatable {
        let profileID: UUID
        let email: String?
        let confirmed: Bool
        let busy: Bool
    }

    struct Binding: Equatable, Codable {
        let profileID: UUID
        let emailFingerprint: String
        let threadID: String
        // A read-only proof cannot enable a sender.
        var actualSendEnabled: Bool { false }
    }

    private struct AccountResponse: Decodable {
        struct Account: Decodable { let type: String; let email: String? }
        let account: Account?
    }
    private struct Catalog: Decodable {
        struct Item: Decodable { let id: String; let name: String?; let title: String? }
        let data: [Item]
        let nextCursor: String?
    }
    private struct ThreadResponse: Decodable {
        struct Thread: Decodable {
            struct Status: Decodable { let type: String }
            let id: String
            let ephemeral: Bool
            let path: String?
            let name: String?
            let status: Status
        }
        let thread: Thread
    }

    private static func email(_ value: String?) -> String? {
        guard let value else { return nil }
        let normalized = value.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        let parts = normalized.split(separator: "@", omittingEmptySubsequences: false)
        guard normalized.utf8.count <= 254,
              normalized.unicodeScalars.allSatisfy({
                  !CharacterSet.controlCharacters.contains($0) &&
                  !CharacterSet.whitespacesAndNewlines.contains($0)
              }), parts.count == 2, !parts[0].isEmpty,
              parts[1].contains(".") else { return nil }
        return normalized
    }
    private static func fingerprint(_ normalized: String) -> String {
        SHA256.hash(data: Data(("codex-switcher:verified-chatgpt-email:v1\n"+normalized).utf8))
            .map { String(format: "%02x", $0) }.joined()
    }

    /// Only complete, current-account catalog evidence from the same future
    /// read-only session may be supplied. The caller is not implemented here.
    static func verify(before: Identity, after: Identity, selectedID: String,
                       accountBefore: Data, catalog: Data, thread: Data,
                       accountAfter: Data) -> Binding? {
        guard before == after, before.confirmed, !before.busy,
              let normalized = email(before.email),
              let id = UUID(uuidString: selectedID),
              id.uuidString.lowercased() == selectedID.lowercased(),
              [accountBefore, catalog, thread, accountAfter].allSatisfy({ $0.count <= 1_048_576 })
        else { return nil }
        let decoder = JSONDecoder()
        guard let a = try? decoder.decode(AccountResponse.self, from: accountBefore),
              let b = try? decoder.decode(AccountResponse.self, from: accountAfter),
              a.account?.type == "chatgpt", b.account?.type == "chatgpt",
              email(a.account?.email) == normalized, email(b.account?.email) == normalized,
              let list = try? decoder.decode(Catalog.self, from: catalog),
              list.nextCursor == nil, list.data.count <= 100,
              list.data.filter({ $0.id == selectedID }).count == 1,
              list.data.filter({ ($0.name ?? $0.title) == dedicatedTitle }).count == 1,
              list.data.contains(where: { $0.id == selectedID && ($0.name ?? $0.title) == dedicatedTitle }),
              let detail = try? decoder.decode(ThreadResponse.self, from: thread),
              detail.thread.id == selectedID, !detail.thread.ephemeral,
              let path = detail.thread.path, path.hasPrefix("/"), !path.contains("\0"),
              detail.thread.name == nil || detail.thread.name == dedicatedTitle,
              ["idle", "notLoaded"].contains(detail.thread.status.type)
        else { return nil }
        return Binding(profileID: before.profileID,
                       emailFingerprint: fingerprint(normalized), threadID: selectedID)
    }
}
