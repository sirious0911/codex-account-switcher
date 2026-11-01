import CryptoKit
import Foundation
import SwitcherCore

/// A macOS-native equivalent of the existing Windows/.NET monitor's offline
/// confirmation and mock-outbox rules. Never launches a process or sends messages.
@MainActor
final class MacMonitorLedger {
    struct HistoryEntry: Identifiable, Equatable {
        let key: String
        let accountReference: String
        let kind: String
        let lane: String
        let observedAtMilliseconds: Int64
        let mockCompleted: Bool
        var id: String { key }
    }

    private struct Window: Codable {
        var used: Double?
        var reset: Int64?
    }

    private struct Reading: Codable {
        var accountID: String
        var plan: String?
        var allowed: Bool?
        var time: Int64
        var five: Window
        var week: Window
        var credits: Int64?
        var identity: String? {
            guard let plan, !plan.isEmpty else { return nil }
            return accountID + "|" + plan
        }
    }

    private struct Event: Codable {
        var key: String
        var kind: String
        var lane: String
        var previous: Double
        var current: Double
        var observedAtMilliseconds: Int64
    }

    private struct Pending: Codable {
        var event: Event
        var firstObservedAtMilliseconds: Int64
    }

    private struct State: Codable {
        var version = 1
        var trusted: Reading?
        var pending: [Pending] = []
        var emittedKeys: Set<String> = []
        var outbox: [Event] = []
        var mockCompleted: Set<String> = []
    }

    private struct Payload: Decodable {
        struct Bucket: Decodable {
            var planType: String?
            var primary: Quota?
            var secondary: Quota?
        }
        struct Quota: Decodable {
            var usedPercent: Double?
            var windowDurationMins: Int?
            var resetsAt: Int64?
        }
        struct Limits: Decodable { var codex: Bucket? }
        struct Credits: Decodable { var availableCount: Int64? }
        var accountId: String?
        var ordinaryUsageAllowed: Bool?
        var rateLimitsByLimitId: Limits?
        var rateLimitResetCredits: Credits?
    }

    private let directory: URL
    init(directory: URL) { self.directory = directory }

    private static func digest(_ text: String) -> String {
        SHA256.hash(data: Data(text.utf8))
            .map { String(format: "%02x", $0) }.joined()
    }

    private static func number(_ value: Double) -> String {
        if value.isFinite, abs(value) < Double(Int64.max), value.rounded() == value {
            return String(Int64(value))
        }
        return String(value)
    }

    private static func window(_ raw: Payload.Quota?, minutes: Int) -> Window {
        guard let raw, raw.windowDurationMins == minutes else {
            return Window(used: nil, reset: nil)
        }
        let used = raw.usedPercent.flatMap { $0.isFinite && (0...100).contains($0) ? $0 : nil }
        return Window(used: used, reset: raw.resetsAt.flatMap { $0 > 0 ? $0 : nil })
    }

    private static func parse(_ observation: CodexMonitorObservation) throws -> Reading {
        let raw = try JSONDecoder().decode(Payload.self, from: observation.jsonData)
        guard let account = raw.accountId?.trimmingCharacters(in: .whitespacesAndNewlines),
              !account.isEmpty, observation.observedAtMilliseconds > 0 else {
            throw MonitorError.invalidObservation
        }
        let bucket = raw.rateLimitsByLimitId?.codex
        let plan = bucket?.planType?.trimmingCharacters(in: .whitespacesAndNewlines)
        let credits = raw.rateLimitResetCredits?.availableCount
        return Reading(
            accountID: account, plan: plan?.isEmpty == false ? plan : nil,
            allowed: raw.ordinaryUsageAllowed, time: observation.observedAtMilliseconds,
            five: window(bucket?.primary, minutes: 300),
            week: window(bucket?.secondary, minutes: 10080),
            credits: credits.flatMap { $0 >= 0 ? $0 : nil }
        )
    }

    enum MonitorError: Error {
        case invalidObservation
        case unsupportedState
    }

    private static func event(_ before: Reading, _ after: Reading, kind: String,
                              lane: String, previous: Double, current: Double,
                              anchor: String) -> Event {
        let raw = [before.accountID, before.plan ?? "", kind, lane, anchor,
                   number(previous), lane == "credits" ? number(current) : "reset"]
            .joined(separator: "|")
        return Event(key: digest(raw), kind: kind, lane: lane, previous: previous,
                     current: current, observedAtMilliseconds: after.time)
    }

    private static func proposals(_ before: Reading, _ after: Reading) -> [Event] {
        var output: [Event] = []
        if let old = before.credits, let new = after.credits, new > old {
            output.append(event(before, after, kind: "CREDIT_INCREASE",
                                lane: "credits", previous: Double(old), current: Double(new),
                                anchor: String(before.time)))
        }
        guard before.allowed == true, after.allowed == true else { return output }
        for (lane, old, new) in [("five", before.five, after.five),
                                 ("week", before.week, after.week)] {
            guard let oldUsed = old.used, let newUsed = new.used,
                  let oldReset = old.reset, let newReset = new.reset else { continue }
            if oldUsed < 15 || newUsed > 5 || oldUsed - newUsed < 15 { continue }
            if newReset <= oldReset + 120 || newReset <= after.time / 1000 { continue }
            let early = after.time < (oldReset - 120) * 1000
            if early, let oldCredits = before.credits, let newCredits = after.credits,
               newCredits < oldCredits { continue }
            output.append(event(before, after,
                                kind: early ? "EARLY_RECOVERY_SUSPECTED" : "SCHEDULED_RESET",
                                lane: lane, previous: oldUsed, current: newUsed,
                                anchor: "\(oldReset * 1000):\(newReset * 1000)"))
        }
        return output
    }

    /// Returns newly confirmed events, not messages sent to a provider.
    @discardableResult
    func record(_ observation: CodexMonitorObservation) throws -> Int {
        let next = try Self.parse(observation)
        let folder = directory.appending(path: Self.digest(next.accountID))
        let file = folder.appending(path: "state.json")
        var state: State
        if FileManager.default.fileExists(atPath: file.path) {
            state = try JSONDecoder().decode(State.self, from: Data(contentsOf: file))
            guard state.version == 1 else { throw MonitorError.unsupportedState }
        } else { state = State() }

        var emitted: [Event] = []
        if next.identity == nil {
            state.trusted = nil
            state.pending = []
        } else if let trusted = state.trusted, trusted.identity == next.identity {
            if next.time > trusted.time {
                if next.time - trusted.time > 86_400_000 {
                    state.trusted = next
                    state.pending = []
                } else {
                    let proposals = Self.proposals(trusted, next)
                    var pending: [Pending] = []
                    for proposal in proposals {
                        if let previous = state.pending.first(where: { $0.event.key == proposal.key }),
                           next.time > previous.firstObservedAtMilliseconds {
                            if state.emittedKeys.insert(proposal.key).inserted {
                                state.outbox.append(proposal)
                                state.mockCompleted.insert(proposal.key)
                                emitted.append(proposal)
                            }
                        } else {
                            pending.append(Pending(event: proposal,
                                                   firstObservedAtMilliseconds: next.time))
                        }
                    }
                    state.pending = pending
                    if proposals.isEmpty || !emitted.isEmpty {
                        state.trusted = next
                        if !emitted.isEmpty { state.pending = [] }
                    }
                }
            }
        } else {
            state.trusted = next
            state.pending = []
        }

        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        // macOS Foundation atomic replace: a crash must not leave a truncated JSON state.
        try JSONEncoder().encode(state).write(to: file, options: .atomic)
        return emitted.count
    }

    func history(limit: Int = 20) -> [HistoryEntry] {
        guard limit > 0,
              let folders = try? FileManager.default.contentsOfDirectory(
                at: directory, includingPropertiesForKeys: [.isDirectoryKey]) else { return [] }
        var rows: [HistoryEntry] = []
        for folder in folders {
            let name = folder.lastPathComponent
            guard name.count == 64, name.allSatisfy({ $0.isHexDigit }),
                  let data = try? Data(contentsOf: folder.appending(path: "state.json")),
                  let state = try? JSONDecoder().decode(State.self, from: data),
                  state.version == 1 else { continue }
            for event in state.outbox {
                rows.append(HistoryEntry(key: event.key, accountReference: String(name.prefix(8)),
                                         kind: event.kind, lane: event.lane,
                                         observedAtMilliseconds: event.observedAtMilliseconds,
                                         mockCompleted: state.mockCompleted.contains(event.key)))
            }
        }
        return Array(rows.sorted { $0.observedAtMilliseconds > $1.observedAtMilliseconds }
                     .prefix(min(limit, 100)))
    }
}
