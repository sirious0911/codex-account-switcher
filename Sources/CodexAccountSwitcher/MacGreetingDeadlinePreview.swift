import Foundation
import CryptoKit
import SwitcherCore

/// Scheduling preview only: no transport, reservation or send permission.
@MainActor
final class MacGreetingDeadlinePreview {
    enum Lane: String, CaseIterable {
        case fiveHour, weekly
        var minutes: Int64 { self == .fiveHour ? 300 : 10080 }
    }
    struct Candidate: Equatable {
        let profileID: UUID
        let lane: Lane
        let resetAtMs: Int64
        let eventKey: String
    }
    private struct Watch {
        let resetAtMs: Int64
        let observedAtMs: Int64
    }
    private struct Payload: Decodable {
        struct Quota: Decodable {
            let usedPercent: Double?
            let resetsAt: Int64?
            let windowDurationMins: Int64?
        }
        struct Bucket: Decodable { let primary: Quota?; let secondary: Quota? }
        struct Limits: Decodable { let codex: Bucket? }
        let accountId: String?
        let ordinaryUsageAllowed: Bool?
        let rateLimitsByLimitId: Limits?
    }
    private var watches: [Lane: Watch] = [:]
    private var seen: Set<String> = []
    private var latest: Int64 = 0
    private var profileID: UUID?
    private var accountHash: String?
    func invalidate() {
        watches.removeAll()
        profileID = nil
        accountHash = nil
        latest = 0
    }
    private static func hash(_ text: String) -> String {
        SHA256.hash(data: Data(text.utf8)).map { String(format: "%02x", $0) }.joined()
    }
    func observe(_ sample: CodexMonitorObservation, profile: AccountProfile,
                 activeProfileID: UUID?, identityConfirmed: Bool, busy: Bool,
                 nowMs: Int64) {
        guard identityConfirmed, !busy, profile.id == activeProfileID,
              profile.id == sample.profileID,
              let account = CodexUsageSnapshotAdapter.accountKey(for: profile),
              let data = try? JSONDecoder().decode(Payload.self, from: sample.jsonData),
              data.accountId == account.accountID, data.ordinaryUsageAllowed != false
        else { invalidate(); return }
        let hash = Self.hash(account.accountID)
        if profileID != profile.id || accountHash != hash {
            invalidate()
            profileID = profile.id
            accountHash = hash
        }
        let observed = sample.observedAtMilliseconds
        guard nowMs > 0, nowMs <= 253_402_300_799_000,
              observed > latest, observed > 0,
              observed <= nowMs, nowMs - observed <= 360_000 else { return }
        latest = observed
        for lane in Lane.allCases {
            let q = lane == .fiveHour ? data.rateLimitsByLimitId?.codex?.primary :
                                        data.rateLimitsByLimitId?.codex?.secondary
            guard let q, q.windowDurationMins == lane.minutes,
                  let used = q.usedPercent, used.isFinite, (0...100).contains(used),
                  let reset = q.resetsAt, (1...253_402_300_799).contains(reset)
            else { watches.removeValue(forKey: lane); continue }
            let at = reset * 1000
            let horizon: Int64 = lane == .fiveHour ? 21_600_000 : 691_200_000
            guard at >= observed - 600_000, at <= observed + horizon
            else { watches.removeValue(forKey: lane); continue }
            if let prior = watches[lane], prior.resetAtMs != at,
               observed >= prior.resetAtMs, observed <= prior.resetAtMs + 600_000,
               abs(at - prior.resetAtMs - lane.minutes * 60_000) <= 600_000 {
                watches[lane] = Watch(resetAtMs: prior.resetAtMs, observedAtMs: observed)
            } else {
                watches[lane] = Watch(resetAtMs: at, observedAtMs: observed)
            }
        }
    }
    func takeDue(profile: AccountProfile, activeProfileID: UUID?,
                 identityConfirmed: Bool, busy: Bool, nowMs: Int64) -> [Candidate] {
        guard identityConfirmed, !busy, profile.id == activeProfileID,
              profile.id == profileID,
              let account = CodexUsageSnapshotAdapter.accountKey(for: profile),
              Self.hash(account.accountID) == accountHash,
              nowMs > 0, nowMs <= 253_402_300_799_000
        else { invalidate(); return [] }
        var result: [Candidate] = []
        for lane in Lane.allCases {
            guard let watch = watches[lane], nowMs >= watch.resetAtMs,
                  nowMs - watch.resetAtMs <= 600_000,
                  nowMs >= watch.observedAtMs,
                  nowMs - watch.observedAtMs <= 360_000 else { continue }
            let key = Self.hash("MAC_GREETING_PREVIEW_V1|\(account.accountID)|\(lane.rawValue)|\(watch.resetAtMs)")
            guard seen.insert(key).inserted else { continue }
            result.append(Candidate(profileID: profile.id, lane: lane,
                                    resetAtMs: watch.resetAtMs, eventKey: key))
        }
        return result
    }
}
