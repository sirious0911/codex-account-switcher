import Foundation

/// A sanitized, synthetic-compatible observation for the separate usage monitor.
/// No process, file, credential, network or Telegram access occurs in this layer.
public struct CodexMonitorObservation: Sendable {
    public let observedAtMilliseconds: Int64
    public let jsonData: Data
}

public enum CodexMonitorPayloadEncoder {
    /// Bridge into the existing monitor's normalizeSynthetic(raw, observedAtMs) input.
    /// planType/ordinaryUsageAllowed must come from a separately verified read-only
    /// provider adapter. Missing permission or plan information remains null: the
    /// monitor will not infer that a capacity recovery event is legitimate.
    public static func encode(
        profile: AccountProfile,
        usage: WeeklyUsage,
        planType: String?,
        ordinaryUsageAllowed: Bool?,
        observedAt: Date
    ) -> CodexMonitorObservation? {
        guard let account = CodexUsageSnapshotAdapter.accountKey(for: profile),
              let weeklyReset = epochSeconds(usage.resetsAt),
              (0...100).contains(usage.remainingPercent),
              let observedMillis = epochMilliseconds(observedAt)
        else { return nil }

        let safePlan: String? = planType.flatMap { raw in
            let value = raw.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !value.isEmpty, value.utf8.count <= 128,
                  !value.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) })
            else { return nil }
            return value
        }

        let weekly: [String: Any] = [
            "usedPercent": 100 - usage.remainingPercent,
            "resetsAt": weeklyReset,
            "windowDurationMins": 10080
        ]

        var fiveHour: Any = NSNull()
        if let remaining = usage.fiveHourRemainingPercent,
           let reset = usage.fiveHourResetsAt {
            guard (0...100).contains(remaining), let time = epochSeconds(reset) else { return nil }
            fiveHour = [
                "usedPercent": 100 - remaining,
                "resetsAt": time,
                "windowDurationMins": 300
            ] as [String: Any]
        } else if let remaining = usage.fiveHourRemainingPercent, !(0...100).contains(remaining) {
            return nil
        }

        let credits: Any
        if let count = usage.resetCreditsRemaining {
            guard count >= 0 else { return nil }
            credits = count
        } else {
            credits = NSNull()
        }

        // These account details are provider IDs, not authentication materials.
        // Never log or transmit this payload without an independent privacy gate.
        let payload: [String: Any] = [
            "accountId": account.accountID,
            "ordinaryUsageAllowed": ordinaryUsageAllowed.map { $0 as Any } ?? NSNull(),
            "rateLimitsByLimitId": [
                "codex": [
                    "planType": safePlan.map { $0 as Any } ?? NSNull(),
                    "primary": fiveHour,
                    "secondary": weekly
                ] as [String: Any]
            ],
            "rateLimitResetCredits": ["availableCount": credits]
        ]
        guard JSONSerialization.isValidJSONObject(payload),
              let json = try? JSONSerialization.data(withJSONObject: payload, options: [.sortedKeys])
        else { return nil }
        return CodexMonitorObservation(observedAtMilliseconds: observedMillis, jsonData: json)
    }

    private static func epochSeconds(_ date: Date) -> Int64? {
        let value = date.timeIntervalSince1970
        guard value.isFinite, value > 0, value < 253_402_300_799 else { return nil }
        return Int64(value)
    }

    private static func epochMilliseconds(_ date: Date) -> Int64? {
        let value = date.timeIntervalSince1970 * 1000
        guard value.isFinite, value > 0, value < 253_402_300_799_000 else { return nil }
        return Int64(value)
    }
}
