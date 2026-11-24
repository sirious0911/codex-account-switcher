import Foundation

/// Pure read scheduling. No provider calls, storage, credentials or sending.
public struct AdaptiveUsageCadence: Sendable {
    private var profile: UUID?
    private var anchor: Date
    private var independentFresh: Date?
    private var requested: Date?
    private var waiting = false
    private var misses = 0
    private var fiveReset: Date?
    private var weeklyReset: Date?

    public init(now: Date) { anchor = now }

    public mutating func select(_ id: UUID?, now: Date) {
        guard id != profile else { return }
        self = Self(now: now)
        profile = id
    }

    public mutating func observed(profile id: UUID, now: Date,
                                  fiveReset five: Date?, weeklyReset week: Date?) {
        guard profile == id else { return }
        let ownResponse = waiting && requested.map {
            now.timeIntervalSince($0) >= 0 && now.timeIntervalSince($0) <= 40
        } == true
        if !ownResponse { independentFresh = now }
        waiting = false
        misses = 0
        if let five, five > now.addingTimeInterval(-600),
           five <= now.addingTimeInterval(21600) { fiveReset = five }
        if let week, week > now.addingTimeInterval(-600),
           week <= now.addingTimeInterval(691200) { weeklyReset = week }
    }

    public mutating func isDue(now: Date, activeWork: Bool) -> Bool {
        guard profile != nil else { return false }
        let watch = [fiveReset, weeklyReset].compactMap { $0 }.contains {
            now >= $0.addingTimeInterval(-180) && now <= $0.addingTimeInterval(600)
        }
        guard activeWork || watch else { return false }
        if waiting {
            guard let requested, now.timeIntervalSince(requested) >= 40 else { return false }
            waiting = false
            misses = min(3, misses + 1)
        }
        let interval: TimeInterval = misses == 0 ? (watch ? 15 : 30) :
            (misses == 1 ? 60 : misses == 2 ? 120 : 300)
        let last = max(anchor, max(independentFresh ?? anchor, requested ?? anchor))
        return now.timeIntervalSince(last) >= interval
    }

    /// Record only an admitted active-only request, not a rejected busy tick.
    public mutating func didRequest(now: Date) {
        guard profile != nil else { return }
        requested = now
        waiting = true
    }
}

/// Heuristic only: recent rollout-file modification times, never file content.
/// Inspect the last three local/UTC date buckets, with a bounded scan.
public enum RecentCodexSessionMetadata {
    public static func hasRecentWork(home: URL, now: Date,
                                     fileManager: FileManager = .default) -> Bool {
        let keys: Set<URLResourceKey> = [.isDirectoryKey, .isSymbolicLinkKey,
                                        .isRegularFileKey, .contentModificationDateKey]
        func directory(_ url: URL) -> Bool {
            guard let v = try? url.resourceValues(forKeys: keys) else { return false }
            return v.isDirectory == true && v.isSymbolicLink == false
        }
        guard home.isFileURL, directory(home) else { return false }
        let sessions = home.appendingPathComponent("sessions", isDirectory: true)
        guard directory(sessions) else { return false }
        var dates = Set<String>()
        for zone in [TimeZone.current, TimeZone(secondsFromGMT: 0)!] {
            var calendar = Calendar(identifier: .gregorian)
            calendar.timeZone = zone
            for offset in 0...2 {
                guard let date = calendar.date(byAdding: .day, value: -offset, to: now) else { continue }
                let part = calendar.dateComponents([.year, .month, .day], from: date)
                guard let year = part.year, let month = part.month, let day = part.day else { continue }
                dates.insert(String(format: "%04d/%02d/%02d", year, month, day))
            }
        }
        var remaining = 1024
        for bucket in dates.sorted(by: >) {
            var path = sessions
            var valid = true
            for component in bucket.split(separator: "/") {
                path = path.appendingPathComponent(String(component), isDirectory: true)
                if !directory(path) { valid = false; break }
            }
            guard valid, let entries = fileManager.enumerator(at: path,
                includingPropertiesForKeys: Array(keys),
                options: [.skipsHiddenFiles, .skipsSubdirectoryDescendants]) else { continue }
            for case let entry as URL in entries {
                guard remaining > 0 else { return false }
                remaining -= 1
                guard entry.lastPathComponent.hasPrefix("rollout-"),
                      entry.pathExtension == "jsonl",
                      let v = try? entry.resourceValues(forKeys: keys),
                      v.isSymbolicLink == false, v.isRegularFile == true,
                      let modified = v.contentModificationDate else { continue }
                let age = now.timeIntervalSince(modified)
                if age >= -30 && age <= 180 { return true }
            }
        }
        return false
    }
}
