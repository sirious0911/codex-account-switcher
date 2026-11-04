import Foundation

/// Read-only projection from the existing Codex data model into multi-provider UI data.
/// This must never trigger login, account switching, polling or message delivery.
public enum CodexUsageSnapshotAdapter {
    /// An OS-local profile UUID or email is not a cross-machine identity.
    /// If Codex did not return its provider account ID, refuse to construct an alert key.
    public static func accountKey(for profile: AccountProfile) -> AIAccountKey? {
        guard let id = profile.accountID else { return nil }
        return AIAccountKey(service: .codex, accountID: id)
    }

    public static func snapshot(
        for profile: AccountProfile,
        usage: WeeklyUsage,
        observedAt: Date?,
        freshness: AIDataFreshness
    ) -> AIUsageSnapshot? {
        guard let key = accountKey(for: profile),
              let weekly = AIUsageWindow(
                  kind: .weekly,
                  providerWindowID: "codex:weekly",
                  remainingPercent: Double(usage.remainingPercent),
                  resetsAt: usage.resetsAt
              )
        else { return nil }

        var windows: [AIUsageWindow] = [weekly]
        if let remaining = usage.fiveHourRemainingPercent,
           let resetsAt = usage.fiveHourResetsAt {
            guard let fiveHour = AIUsageWindow(
                kind: .fiveHour,
                providerWindowID: "codex:five-hour",
                remainingPercent: Double(remaining),
                resetsAt: resetsAt
            ) else { return nil }
            windows.append(fiveHour)
        }

        return AIUsageSnapshot(
            account: key,
            observedAt: observedAt,
            freshness: freshness,
            windows: windows,
            resetCreditsRemaining: usage.resetCreditsRemaining
        )
    }
}
