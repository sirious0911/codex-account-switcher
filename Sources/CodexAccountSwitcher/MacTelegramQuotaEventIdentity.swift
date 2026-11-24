import CryptoKit
import Foundation

/// Future Mac Telegram sender event identity. Compatible with the Windows/.NET
/// and Node 5H/7D recovery formulas. This is a pure helper only: it does not
/// observe accounts, authorize delivery, read credentials or send messages.
enum MacTelegramQuotaEventIdentity {
    enum Lane {
        case fiveHour
        case weekly

        var canonicalKind: String {
            switch self {
            case .fiveHour: "FIVE_HOUR_100"
            case .weekly: "WEEKLY_100"
            }
        }
    }

    static let schemaVersion = 1

    static func build(
        lane: Lane,
        accountIdSHA256: String,
        previousResetAtUnixMs: Int64,
        currentResetAtUnixMs: Int64
    ) -> String? {
        let asciiHex = Set("0123456789abcdefABCDEF".utf8)
        guard accountIdSHA256.utf8.count == 64,
              accountIdSHA256.utf8.allSatisfy({ asciiHex.contains($0) }),
              previousResetAtUnixMs > 0,
              currentResetAtUnixMs > previousResetAtUnixMs,
              currentResetAtUnixMs <= 253_402_300_799_000
        else { return nil }

        let input = [
            lane.canonicalKind,
            accountIdSHA256.lowercased(),
            String(previousResetAtUnixMs),
            String(currentResetAtUnixMs)
        ].joined(separator: "|")

        return SHA256.hash(data: Data(input.utf8))
            .map { String(format: "%02x", $0) }.joined()
    }
}
