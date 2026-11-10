import SwitcherCore
import AppKit
import Foundation

enum DesktopControllerError: LocalizedError, Sendable {
    case applicationNotFound
    case quitRequestFailed
    case didNotExit
    case reopenFailed

    var errorDescription: String? {
        switch self {
        case .applicationNotFound:
            "The Codex Desktop application could not be found."
        case .quitRequestFailed:
            "Codex Desktop rejected the quit request."
        case .didNotExit:
            "Codex Desktop did not finish quitting within 30 seconds. Finish or stop active tasks and close Desktop, then switch again. The account has not changed."
        case .reopenFailed:
            "Codex Desktop could not be reopened. Open it manually to continue."
        }
    }
}

// A quit request may be waiting for Desktop's active-task dialog or durable history writes.
// Expiry must stop the switch before credentials change; it must never escalate to SIGKILL.
struct DesktopQuitWaiter: Sendable {
    var timeout: Duration = .seconds(30)
    var pollInterval: Duration = .milliseconds(100)

    func wait(isRunning: @Sendable () async -> Bool) async throws {
        let clock = ContinuousClock()
        let deadline = clock.now.advanced(by: timeout)
        while await isRunning() {
            try Task.checkCancellation()
            guard clock.now < deadline else { throw DesktopControllerError.didNotExit }
            try await Task.sleep(for: min(pollInterval, deadline - clock.now))
        }
        try Task.checkCancellation()
    }
}

struct DesktopController: DesktopControlling {
    private let bundleIdentifiers = ["com.openai.codex"]
    private let applicationPaths = ["/Applications/ChatGPT.app", "/Applications/Codex.app"]

    func closeDesktop() async throws {
        let running = runningDesktopApplications
        guard !running.isEmpty else { return }
        for application in running where !application.isTerminated {
            let processIdentifier = application.processIdentifier
            guard application.terminate() || !isDesktopRunning(processIdentifier: processIdentifier) else {
                throw DesktopControllerError.quitRequestFailed
            }
        }

        try await DesktopQuitWaiter().wait {
            isDesktopRunning
        }
    }

    func reopenDesktop() async throws {
        guard let url = applicationURL() else {
            throw DesktopControllerError.applicationNotFound
        }
        let configuration = NSWorkspace.OpenConfiguration()
        configuration.activates = true
        do {
            _ = try await NSWorkspace.shared.openApplication(at: url, configuration: configuration)
        } catch {
            throw DesktopControllerError.reopenFailed
        }
    }

    private func applicationURL() -> URL? {
        let registeredURLs = bundleIdentifiers.compactMap { identifier in
            NSWorkspace.shared.urlForApplication(withBundleIdentifier: identifier)
        }
        let fallbackURLs = fallbackApplicationURLs(homeDirectory: FileManager.default.homeDirectoryForCurrentUser)
        return selectApplication(registeredURLs: registeredURLs, fallbackURLs: fallbackURLs)
    }

    // Launch Services may not know an app installed only for this user.
    // Keep the existing system-wide fallback order and also search ~/Applications.
    func fallbackApplicationURLs(homeDirectory: URL) -> [URL] {
        applicationPaths.map(URL.init(fileURLWithPath:)) + [
            homeDirectory.appendingPathComponent("Applications/Codex.app"),
            homeDirectory.appendingPathComponent("Applications/ChatGPT.app"),
        ]
    }

    // A filename or a stale Launch Services registration is not proof of
    // identity. This separate selection path lets regression tests cover
    // registry/fallback priority without launching any Desktop application.
    func selectApplication(registeredURLs: [URL], fallbackURLs: [URL]) -> URL? {
        (registeredURLs + fallbackURLs).first(where: isCodexApplication)
    }

    // Shared by workspace lookup, fallback selection, and isolated regression
    // tests. Never reopen an unrelated app as a "successful" Codex restart.
    func isCodexApplication(_ url: URL) -> Bool {
        guard let actualIdentifier = Bundle(url: url)?.bundleIdentifier else {
            return false
        }
        return bundleIdentifiers.contains(actualIdentifier)
    }

    private var isDesktopRunning: Bool {
        !runningDesktopApplications.isEmpty
    }

    private var runningDesktopApplications: [NSRunningApplication] {
        NSWorkspace.shared.runningApplications.filter { application in
            guard let bundleIdentifier = application.bundleIdentifier else { return false }
            return bundleIdentifiers.contains(bundleIdentifier)
        }
    }

    private func isDesktopRunning(processIdentifier: pid_t) -> Bool {
        runningDesktopApplications.contains { $0.processIdentifier == processIdentifier }
    }
}
