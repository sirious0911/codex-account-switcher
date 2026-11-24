import Foundation
import Testing
@testable import SwitcherCore

struct AdaptiveUsageCadenceTests {
    @Test func activityAndResetCadences() {
        let t = Date(timeIntervalSince1970: 1_800_000_000)
        let id = UUID()
        var cadence = AdaptiveUsageCadence(now: t)
        cadence.select(id, now: t)
        let due1 = cadence.isDue(now: t.addingTimeInterval(29), activeWork: true)
        #expect(!due1)
        let due2 = cadence.isDue(now: t.addingTimeInterval(30), activeWork: true)
        #expect(due2)
        cadence.didRequest(now: t.addingTimeInterval(30))
        cadence.observed(profile: id, now: t.addingTimeInterval(32),
                         fiveReset: t.addingTimeInterval(180), weeklyReset: nil)
        let due3 = cadence.isDue(now: t.addingTimeInterval(44), activeWork: false)
        #expect(!due3)
        let due4 = cadence.isDue(now: t.addingTimeInterval(45), activeWork: false)
        #expect(due4)
        cadence.select(UUID(), now: t.addingTimeInterval(45))
        let due5 = cadence.isDue(now: t.addingTimeInterval(60), activeWork: false)
        #expect(!due5)
        let due6 = cadence.isDue(now: t.addingTimeInterval(75), activeWork: true)
        #expect(due6)
    }

    @Test func missedSamplesBackOff() {
        let t = Date(timeIntervalSince1970: 1_800_000_000)
        var cadence = AdaptiveUsageCadence(now: t)
        cadence.select(UUID(), now: t)
        cadence.didRequest(now: t.addingTimeInterval(30))
        let due7 = cadence.isDue(now: t.addingTimeInterval(69), activeWork: true)
        #expect(!due7)
        let due8 = cadence.isDue(now: t.addingTimeInterval(89), activeWork: true)
        #expect(!due8)
        let due9 = cadence.isDue(now: t.addingTimeInterval(90), activeWork: true)
        #expect(due9)
        cadence.didRequest(now: t.addingTimeInterval(90))
        let due10 = cadence.isDue(now: t.addingTimeInterval(209), activeWork: true)
        #expect(!due10)
        let due11 = cadence.isDue(now: t.addingTimeInterval(210), activeWork: true)
        #expect(due11)
    }

    @Test func recentSessionMetadataExcludesStaleAndLinks() throws {
        let fm = FileManager.default
        let home = fm.temporaryDirectory.appendingPathComponent("cadence-fixture-\(UUID())")
        defer { try? fm.removeItem(at: home) }
        let now = Date()
        var cal = Calendar(identifier: .gregorian)
        cal.timeZone = TimeZone(secondsFromGMT: 0)!
        let parts = cal.dateComponents([.year,.month,.day], from: now)
        let bucket = home.appendingPathComponent(String(format:"sessions/%04d/%02d/%02d",
            parts.year!,parts.month!,parts.day!))
        try fm.createDirectory(at: bucket, withIntermediateDirectories: true)
        let file = bucket.appendingPathComponent("rollout-fixture.jsonl")
        try Data().write(to: file)
        try fm.setAttributes([.modificationDate:now.addingTimeInterval(-181)], ofItemAtPath:file.path)
        #expect(!RecentCodexSessionMetadata.hasRecentWork(home:home,now:now))
        try fm.setAttributes([.modificationDate:now.addingTimeInterval(-120)], ofItemAtPath:file.path)
        #expect(RecentCodexSessionMetadata.hasRecentWork(home:home,now:now))
        let outside = home.appendingPathComponent("outside.jsonl")
        try fm.moveItem(at:file,to:outside)
        try fm.createSymbolicLink(at:file,withDestinationURL:outside)
        #expect(!RecentCodexSessionMetadata.hasRecentWork(home:home,now:now))
    }
}
