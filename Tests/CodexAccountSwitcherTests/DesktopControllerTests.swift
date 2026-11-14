@testable import SwitcherCore
import Foundation
import Testing
@testable import CodexAccountSwitcher

struct DesktopControllerTests {
    @Test func allowsHistoryFlushToTakeLongerThanTwoSeconds() async throws {
        let clock = ContinuousClock()
        let exitsAt = clock.now.advanced(by: .milliseconds(2_100))
        try await DesktopQuitWaiter().wait { clock.now < exitsAt }
        #expect(clock.now >= exitsAt)
    }

    @Test func reportsTimeoutWhileDesktopStillRuns() async {
        do {
            try await DesktopQuitWaiter(timeout: .milliseconds(10)).wait { true }
            Issue.record("Expected quit timeout")
        } catch DesktopControllerError.didNotExit {
            // No force-quit or credential mutation is available to the waiter.
        } catch {
            Issue.record("Unexpected error: \(error)")
        }
    }

    @Test func returnsImmediatelyWhenDesktopHasExited() async throws {
        try await DesktopQuitWaiter(timeout: .zero).wait { false }
    }

    @Test func cancellationStopsWaiting() async {
        let task = Task { try await DesktopQuitWaiter().wait { true } }
        task.cancel()
        do {
            try await task.value
            Issue.record("Expected cancellation")
        } catch is CancellationError {
        } catch {
            Issue.record("Unexpected error: \(error)")
        }
    }

    @Test func fallbackAcceptsOnlyActualCodexBundleIdentifiers() throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("codex-desktop-identity-\(UUID())")
        defer { try? FileManager.default.removeItem(at: root) }

        func mockApp(_ name: String, identifier: String) throws -> URL {
            let app = root.appendingPathComponent(name)
            let macOS = app.appendingPathComponent("Contents/MacOS")
            try FileManager.default.createDirectory(at: macOS, withIntermediateDirectories: true)
            let info: [String: String] = [
                "CFBundleIdentifier": identifier,
                "CFBundleName": name,
                "CFBundleExecutable": "MockApp",
                "CFBundlePackageType": "APPL",
            ]
            let plist = try PropertyListSerialization.data(
                fromPropertyList: info, format: .xml, options: 0)
            try plist.write(to: app.appendingPathComponent("Contents/Info.plist"))
            let executable = macOS.appendingPathComponent("MockApp")
            try Data("#!/bin/sh\nexit 0\n".utf8).write(to: executable)
            try FileManager.default.setAttributes(
                [.posixPermissions: 0o700], ofItemAtPath: executable.path)
            return app
        }

        let unrelated = try mockApp("ChatGPT.app", identifier: "com.openai.chatgpt")
        let correct = try mockApp("Codex.app", identifier: "com.openai.codex")
        let misleading = try mockApp("WrongCodex.app", identifier: "com.example.codex")
        let desktop = DesktopController()
        #expect(!desktop.isCodexApplication(unrelated))
        #expect(desktop.isCodexApplication(correct))
        #expect(!desktop.isCodexApplication(misleading))
        #expect(!desktop.isCodexApplication(root.appendingPathComponent("Missing.app")))

        // A stale Launch Services registration must not select a foreign app.
        // The fallback must skip ChatGPT and select only the Codex bundle.
        #expect(desktop.selectApplication(
            registeredURLs: [unrelated],
            fallbackURLs: [unrelated, correct]) == correct)
        #expect(desktop.selectApplication(
            registeredURLs: [correct],
            fallbackURLs: [unrelated]) == correct)
        #expect(desktop.selectApplication(
            registeredURLs: [misleading],
            fallbackURLs: [unrelated]) == nil)
        #expect(desktop.selectApplication(
            registeredURLs: [],
            fallbackURLs: [unrelated, misleading, correct]) == correct)

        // A user-local Codex.app must remain discoverable even when Launch
        // Services has not yet registered it. An unrelated local ChatGPT.app
        // must not be accepted in its place.
        let personalCodex = try mockApp("Applications/Codex.app", identifier: "com.openai.codex")
        let personalChatGPT = try mockApp("Applications/ChatGPT.app", identifier: "com.openai.chatgpt")
        let fallbackURLs = desktop.fallbackApplicationURLs(homeDirectory: root)
        // Foundation can preserve an .app bundle as a directory URL with a
        // trailing slash; path identity must not depend on that URL hint.
        func sameAppPath(_ lhs: URL?, _ rhs: URL) -> Bool {
            guard let lhs else { return false }
            let slashes = CharacterSet(charactersIn: "/")
            return lhs.standardizedFileURL.path.trimmingCharacters(in: slashes)
                == rhs.standardizedFileURL.path.trimmingCharacters(in: slashes)
        }
        #expect(fallbackURLs.contains { sameAppPath($0, personalCodex) })
        #expect(fallbackURLs.contains { sameAppPath($0, personalChatGPT) })
        let personalCandidates = fallbackURLs.filter { $0.path.hasPrefix(root.path + "/") }
        #expect(sameAppPath(
            desktop.selectApplication(registeredURLs: [], fallbackURLs: personalCandidates),
            personalCodex
        ))
    }
}
