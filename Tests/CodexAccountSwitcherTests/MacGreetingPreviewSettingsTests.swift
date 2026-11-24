import Foundation
import Testing
@testable import CodexAccountSwitcher

@MainActor
struct MacGreetingPreviewSettingsTests {
    @Test func persistedIntentNeverEnablesSending() throws {
        let suite = "GreetingPreviewFixture-\(UUID().uuidString)"
        let defaults = try #require(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName:suite) }
        var settings = MacGreetingPreviewSettings.read(from:defaults)
        #expect(settings.enabledLanes.isEmpty)
        #expect(!settings.actualSendEnabled)
        defaults.set(true,forKey:"GreetingPreviewEnabled")
        defaults.set(false,forKey:"GreetingPreviewFiveHour")
        settings = MacGreetingPreviewSettings.read(from:defaults)
        #expect(settings.enabledLanes == [.weekly])
        #expect(!settings.actualSendEnabled)
        defaults.set(false,forKey:"GreetingPreviewWeekly")
        #expect(MacGreetingPreviewSettings.read(from:defaults).enabledLanes.isEmpty)
        defaults.set(false,forKey:"GreetingPreviewEnabled")
        defaults.set(true,forKey:"GreetingPreviewFiveHour")
        #expect(MacGreetingPreviewSettings.read(from:defaults).enabledLanes.isEmpty)
    }
}
