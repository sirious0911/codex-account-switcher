import Foundation

// Formatting and routing preview only. No credentials, I/O or send authority.
enum MacResetNotificationText {
    static func normalizedThreshold(_ value: Int?) -> Int {
        guard let value else { return 5 }
        return (0...20).contains(value) ? value : 0
    }

    static func valid(_ remaining: Double?, threshold: Int?) -> Bool {
        (remaining.map { $0.isFinite && (0...100).contains($0) } ?? true) &&
        (threshold.map { (0...20).contains($0) } ?? true)
    }

    static func important(_ lane: MacTelegramQuotaEventIdentity.Lane,
                          remaining: Double?, threshold: Int?) -> Bool {
        guard lane == .fiveHour, let remaining, let threshold,
              valid(remaining, threshold: threshold) else { return false }
        return remaining <= Double(threshold)
    }

    static func format(_ lane: MacTelegramQuotaEventIdentity.Lane,
                       observedAtMs: Int64, remaining: Double?, isTest: Bool = false) -> String {
        let label = lane == .fiveHour ? "5H" : "7D"
        let date = DateFormatter()
        date.locale = Locale(identifier: "en_US_POSIX")
        date.timeZone = TimeZone(secondsFromGMT: 9 * 3600)
        date.dateFormat = "yyyy-MM-dd HH:mm"
        let when = date.string(from: Date(timeIntervalSince1970: Double(observedAtMs) / 1000))
        if isTest { return "Codex "+label+" 알림 테스트"+"\n" + when+" KST" }
        let formatter = NumberFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.numberStyle = .decimal
        formatter.usesGroupingSeparator = false
        formatter.minimumFractionDigits = 0
        formatter.maximumFractionDigits = 2
        let balance = remaining.flatMap { valid($0, threshold: nil) ? formatter.string(from: NSNumber(value: $0)) : nil }
            .map { $0+"%" } ?? "미확인"
        return ["Codex "+label+" 초기화 완료", "직전 잔여량: "+balance, when+" KST"].joined(separator: "\n")
    }
}
