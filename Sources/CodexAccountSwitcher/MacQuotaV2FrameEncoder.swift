import CryptoKit
import Foundation
import SwitcherCore

/// Offline P3 V2 read-only telemetry producer. No network, file I/O,
/// credential handling, Telegram or account switching. Never activated
/// from the existing app startup path without a separate operating Gate.
@MainActor
final class MacQuotaV2FrameEncoder {
    private static let minimumTime: Int64 = 1_577_836_800_000
    private static let maximumTime: Int64 = 4_133_980_800_000
    private let sourceDeviceID: String
    private var sessionID = UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased()
    private var sequence: Int64 = 0
    private var lastObserved: Int64 = 0
    private var epochFloor: Int64 = 0
    private var activeProfileID: UUID?
    private var accountHash: String?
    private var quarantined = false

    init?(sourceDeviceID: String) {
        guard !sourceDeviceID.isEmpty, sourceDeviceID.utf8.count <= 48,
              sourceDeviceID.utf8.allSatisfy({
                  (48...57).contains(Int($0)) || (65...90).contains(Int($0)) ||
                  (97...122).contains(Int($0)) || $0 == 45
              }) else { return nil }
        self.sourceDeviceID = sourceDeviceID
    }

    /// The caller must use verified current app/host identity, never a
    /// candidate account inferred from quota data. A transition resets epoch.
    func observeActiveProfile(_ profileID: UUID?, identityConfirmed: Bool,
                              isMutating: Bool, switchPending: Bool,
                              now: Date) {
        let next = identityConfirmed && !isMutating && !switchPending ? profileID : nil
        if next != activeProfileID {
            activeProfileID = next
            sessionID = UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased()
            sequence = 0
            accountHash = nil
            quarantined = false
            let floor = Self.milliseconds(now) ?? Self.maximumTime
            epochFloor = max(epochFloor, floor)
            lastObserved = epochFloor
        }
    }

    func switchRequested(now: Date) {
        observeActiveProfile(nil, identityConfirmed: false,
                             isMutating: true, switchPending: true, now: now)
    }

    /// The sanitized observation must come from
    /// CodexMonitorPayloadEncoder.encode; not raw provider JSON.
    func makeWeeklyFrame(profile: AccountProfile,
                         observation: CodexMonitorObservation,
                         now: Date) -> Data? {
        guard observation.profileID == profile.id,
              let payload = try? JSONSerialization.jsonObject(with: observation.jsonData)
                  as? [String: Any],
              let provider = payload["accountId"] as? String,
              provider == profile.accountID,
              let buckets = payload["rateLimitsByLimitId"] as? [String: Any],
              let codex = buckets["codex"] as? [String: Any],
              let five = Self.window(codex["primary"], minutes: 300,
                                     observed: observation.observedAtMilliseconds),
              let seven = Self.window(codex["secondary"], minutes: 10_080,
                                      observed: observation.observedAtMilliseconds),
              let summary = Self.credits(payload["rateLimitResetCredits"])
        else { return nil }
        return build(profile: profile, provider: provider,
                     observed: observation.observedAtMilliseconds,
                     fiveHour: five, sevenDay: seven,
                     creditCount: summary.count, expiry: summary.expiry, now: now)
    }

    /// NonWeeklyUsage currently carries count only. The R1/R2 expiry list
    /// MUST remain null; 30D must never be relabeled as 7D.
    func makeNonWeeklyFrame(profile: AccountProfile,
                            usage: NonWeeklyUsage,
                            now: Date) -> Data? {
        guard let provider = profile.accountID,
              usage.windowDurationMins > 0,
              (0...100).contains(usage.remainingPercent),
              let observed = Self.milliseconds(usage.observedAt),
              usage.resetCreditsRemaining == nil ||
                usage.resetCreditsRemaining! >= 0
        else { return nil }
        let absent: [String: Any] = [
            "remainingPercent": NSNull(), "resetAtUtcMs": NSNull()
        ]
        return build(profile: profile, provider: provider, observed: observed,
                     fiveHour: absent, sevenDay: absent,
                     creditCount: usage.resetCreditsRemaining,
                     expiry: nil, now: now)
    }

    private func build(profile: AccountProfile, provider: String,
                       observed: Int64, fiveHour: [String: Any],
                       sevenDay: [String: Any],
                       creditCount: Int?, expiry: [Any]?, now: Date) -> Data? {
        guard activeProfileID == profile.id, !quarantined,
              let clock = Self.milliseconds(now),
              (Self.minimumTime...Self.maximumTime).contains(observed),
              observed > epochFloor, observed > lastObserved,
              observed <= clock + 3_000, clock - observed < 360_000,
              sequence < 1_000_000_000,
              !provider.isEmpty, provider.utf8.count <= 256,
              !provider.unicodeScalars.contains(where: {
                  CharacterSet.controlCharacters.contains($0)
              }),
              creditCount == nil || creditCount! >= 0,
              (fiveHour["remainingPercent"] as? Int) != nil ||
                  (sevenDay["remainingPercent"] as? Int) != nil ||
                  creditCount != nil
        else { return nil }

        let hash = SHA256.hash(data: Data(provider.utf8))
            .map { String(format: "%02x", $0) }.joined()
        if let previous = accountHash, previous != hash {
            quarantined = true
            return nil
        }

        let frame: [String: Any] = [
            "schemaVersion": 2,
            "sourceDeviceId": sourceDeviceID,
            "sourceSessionId": sessionID,
            "sequence": sequence + 1,
            "collectedAtUtcMs": observed,
            "accountIdSha256": hash,
            "fiveHour": fiveHour, "sevenDay": sevenDay,
            "resetCredits": [
                "availableCount": creditCount.map { $0 as Any } ?? NSNull(),
                "expiryDetailsUtcMs": expiry.map { $0 as Any } ?? NSNull()
            ] as [String: Any],
            "actualSendAuthorized": false,
            "automaticRetryAllowed": false,
            "productionReservationAuthorized": false,
            "senderTransitionAuthorized": false
        ]
        guard let data = try? JSONSerialization.data(
            withJSONObject: frame, options: [.sortedKeys]),
            data.count <= 2_048 else { return nil }
        accountHash = hash
        lastObserved = observed
        sequence += 1
        return data
    }

    private static func window(_ source: Any?, minutes: Int, observed: Int64)
        -> [String: Any]? {
        let absent: [String: Any] = [
            "remainingPercent": NSNull(), "resetAtUtcMs": NSNull()
        ]
        guard let source, !(source is NSNull) else { return absent }
        guard let fields = source as? [String: Any],
              let duration = fields["windowDurationMins"] as? Int,
              duration == minutes else { return nil }
        guard let used = fields["usedPercent"], !(used is NSNull) else {
            return absent
        }
        guard let usedPercent = used as? Int,
              (0...100).contains(usedPercent) else { return nil }
        var reset: Any = NSNull()
        if let seconds = fields["resetsAt"], !(seconds is NSNull) {
            guard let value = seconds as? Int64,
                  let ms = Self.checkedEpochSeconds(value),
                  ms >= observed - 600_000,
                  ms <= observed + (minutes == 300 ? 21_600_000 : 691_200_000)
            else { return nil }
            reset = ms
        }
        return ["remainingPercent": 100 - usedPercent, "resetAtUtcMs": reset]
    }

    private static func credits(_ source: Any?)
        -> (count: Int?, expiry: [Any]?)? {
        guard let source, !(source is NSNull) else {
            return (nil, nil)
        }
        guard let details = source as? [String: Any] else { return nil }
        let count: Int?
        if let value = details["availableCount"], !(value is NSNull) {
            guard let number = value as? Int, number >= 0,
                  number <= Int(Int32.max) else { return nil }
            count = number
        } else {
            count = nil
        }
        guard let items = details["credits"], !(items is NSNull) else {
            return (count, nil)
        }
        guard count != nil,
              let rows = items as? [[String: Any]], rows.count <= 32
        else { return nil }
        var expiries = [Int64?]()
        for row in rows {
            guard row["status"] as? String == "available",
                  let stamp = row["expiresAt"] else { return nil }
            if stamp is NSNull {
                expiries.append(nil)
            } else {
                guard let seconds = stamp as? Int64,
                      let ms = checkedEpochSeconds(seconds)
                else { return nil }
                expiries.append(ms)
            }
        }
        guard expiries.count <= count! else { return nil }
        // Known dates first, nil expiry slots last; never expose provider IDs.
        expiries.sort {
            switch ($0, $1) {
            case let (a?, b?): return a < b
            case (_?, nil): return true
            case (nil, _?): return false
            default: return false
            }
        }
        return (count, Array(expiries.prefix(2)).map { $0.map { $0 as Any } ?? NSNull() })
    }

    private static func checkedEpochSeconds(_ seconds: Int64) -> Int64? {
        guard seconds >= minimumTime / 1_000,
              seconds <= maximumTime / 1_000 else { return nil }
        return seconds * 1_000
    }

    private static func milliseconds(_ date: Date) -> Int64? {
        let number = date.timeIntervalSince1970 * 1_000
        guard number.isFinite, number >= Double(minimumTime),
              number <= Double(maximumTime) else { return nil }
        return Int64(number)
    }
}
