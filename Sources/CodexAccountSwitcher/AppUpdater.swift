import AppKit
import Combine

/// Fork safety boundary: never discover, download, or install upstream releases.
/// A future fork-owned signed update channel requires separate approval.
@MainActor
final class AppUpdater: ObservableObject {
    @Published private(set) var availableVersion: String? = nil
    @Published private(set) var canCheckForUpdates = false
    @Published private(set) var isInstalling = false
    @Published private(set) var lastError: String? = nil

    var automaticallyChecks: Bool { false }
    var accountOperationInProgress = false

    var currentVersion: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "Development"
    }

    func start() {
        // Intentionally inert: do not instantiate or start Sparkle.
    }

    func checkForUpdates() {
        // No updater network request or installation is allowed in this fork.
    }

    func setAutomaticallyChecks(_ enabled: Bool) {
        // Ignore requests, including persisted legacy preferences.
    }
}
