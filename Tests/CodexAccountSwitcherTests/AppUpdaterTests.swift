import Testing
@testable import CodexAccountSwitcher

@MainActor
struct AppUpdaterTests {
    @Test func upstreamUpdaterCannotStartOrBeReEnabled() {
        let adapter = AppUpdater()
        adapter.start()
        adapter.setAutomaticallyChecks(true)
        adapter.checkForUpdates()
        #expect(!adapter.automaticallyChecks)
        #expect(!adapter.canCheckForUpdates)
        #expect(adapter.availableVersion == nil)
        #expect(!adapter.isInstalling)
        #expect(adapter.lastError == nil)
    }
}
