import Foundation

/// A display-only projection of the active, locally verified Codex account.
/// Never borrows another account's quota or presents cached/stale readings as live.
public struct MenuBarQuotaPresentation: Equatable, Sendable {
    public let label: String
    public let detail: String

    public static func make(
        state: UsageViewState?,
        identityConfirmed: Bool,
        preferredWindow: StatusBarUsageWindow = .fiveHour
    ) -> Self {
        let unknown = Self(label: "5H ? · 7D ? · R ?",
                           detail: "Codex quota unknown. Verify the active account or refresh usage.")
        guard identityConfirmed, let state else { return unknown }

        switch state {
        case let .loaded(usage):
            func percent(_ value: Int?) -> String {
                guard let value, (0...100).contains(value) else { return "?" }
                return "\(value)%"
            }
            let fiveHour = "5H " + percent(usage.fiveHourRemainingPercent)
            let weekly = "7D " + percent(usage.remainingPercent)
            let reset = "R " + creditCount(usage.resetCreditsRemaining)
            let firstTwo = preferredWindow == .fiveHour
                ? [fiveHour, weekly] : [weekly, fiveHour]
            var detail = "Codex 5H: " + percent(usage.fiveHourRemainingPercent)
                + " · next reset: " + kst(usage.fiveHourResetsAt) + "\n"
                + "Codex 7D: " + percent(usage.remainingPercent)
                + " · next reset: " + kst(usage.resetsAt) + "\n"
                + "Reset credits: " + creditCount(usage.resetCreditsRemaining)

            if let credits = usage.resetCredits {
                let available = credits
                    .filter { $0.status == "available" && $0.resetType == "codexRateLimits" }
                    .sorted { left, right in
                        (left.expiresAt ?? .distantFuture) < (right.expiresAt ?? .distantFuture)
                    }
                for (index, credit) in available.prefix(2).enumerated() {
                    detail += "\nR\(index + 1) expires: " + kst(credit.expiresAt)
                }
            }
            return Self(label: (firstTwo + [reset]).joined(separator: " · "), detail: detail)
        case let .nonWeekly(usage):
            let window = usage.windowLabel
            let percent = (0...100).contains(usage.remainingPercent)
                ? "\(usage.remainingPercent)%" : "?"
            return Self(label: window + " " + percent + " · R " + creditCount(usage.resetCreditsRemaining),
                        detail: "Codex " + window + ": " + percent
                            + " · next reset: " + kst(usage.resetsAt)
                            + "\nReset credits: " + creditCount(usage.resetCreditsRemaining))
        case let .staleNonWeekly(usage, _):
            return Self(label: usage.windowLabel + " ? · R ?",
                        detail: "Codex quota stale. Refresh to verify current usage.")
        case .idle, .stale, .unavailable:
            return unknown
        }
    }

    private static func creditCount(_ value: Int?) -> String {
        guard let value, value >= 0 else { return "?" }
        return String(value)
    }

    private static func kst(_ date: Date?) -> String {
        guard let date, date.timeIntervalSince1970.isFinite else { return "?" }
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.calendar = Calendar(identifier: .gregorian)
        formatter.timeZone = TimeZone(secondsFromGMT: 9 * 3600)
        formatter.dateFormat = "yyyy-MM-dd HH:mm"
        return formatter.string(from: date) + " KST"
    }
}
