import SwitcherCore
import AppKit
import SwiftUI

@main
struct SwitcherApp: App {
    @NSApplicationDelegateAdaptor(SwitcherAppDelegate.self) private var appDelegate

    var body: some Scene {
        MenuBarExtra {
            MenuBarPopover(model: appDelegate.model, updater: appDelegate.updater,
                           tiboForecast: appDelegate.tiboForecast)
        } label: {
            MenuBarStatusLabel(model: appDelegate.model, updater: appDelegate.updater)
        }
        .menuBarExtraStyle(.window)
        .commands {
            QuitApplicationCommands(model: appDelegate.model)
        }
    }
}

private struct MenuBarStatusLabel: View {
    @ObservedObject var model: AppModel
    @ObservedObject var updater: AppUpdater

    var body: some View {
        HStack(spacing: 4) {
            MenuBarLogo()
                .overlay(alignment: .topTrailing) {
                    if updater.availableVersion != nil {
                        Circle().fill(.blue).frame(width: 5, height: 5)
                            .offset(x: 2, y: -1)
                    }
                }
            if model.settings.showsMenuBarPercentage {
                Text(currentQuota.label)
                    .font(.system(size: 11, weight: .medium).monospacedDigit())
                    .help(currentQuota.detail)
            }
        }
            .accessibilityElement(children: .combine)
            .accessibilityLabel(menuBarAccessibilityLabel)
    }

    private var currentQuota: MenuBarQuotaPresentation {
        let state = model.activeAccountID.flatMap { model.usageStates[$0] }
        return MenuBarQuotaPresentation.make(
            state: state,
            identityConfirmed: model.activeAccountID != nil && model.activeIdentityConfirmed,
            preferredWindow: model.settings.statusBarUsageWindow
        )
    }

    private var menuBarAccessibilityLabel: String {
        let updateStatus = updater.availableVersion.map { ", " + model.format("update_available", $0) } ?? ""
        guard model.settings.showsMenuBarPercentage else {
            return "Codex Account Switcher" + updateStatus
        }
        return "Codex Account Switcher, " + currentQuota.label + updateStatus
    }
}

private struct MenuBarLogo: View {
    private static let logicalSize = NSSize(width: 17.5, height: 17.5)

    private static let image: NSImage = {
        let resourceBundleName = "CodexAccountSwitcher_CodexAccountSwitcher.bundle"
        let resourceBundle = [Bundle.main.resourceURL, Bundle.main.bundleURL]
            .compactMap { $0 }
            .map { $0.appending(path: resourceBundleName) }
            .compactMap { Bundle(url: $0) }
            .first
        guard let url = resourceBundle?.url(
            forResource: "account-switcher-logo",
            withExtension: "png"
        ), let image = NSImage(contentsOf: url) else {
            preconditionFailure("Missing account-switcher-logo.png")
        }
        image.size = logicalSize
        image.isTemplate = true
        return image
    }()

    var body: some View {
        Image(nsImage: Self.image)
            .frame(width: 17.5, height: 17.5)
            .accessibilityHidden(true)
    }
}

private struct QuitApplicationCommands: Commands {
    @ObservedObject var model: AppModel

    var body: some Commands {
        CommandGroup(replacing: .appTermination) {
            Button(model.text("quit")) {
                NSApp.terminate(nil)
            }
            .keyboardShortcut("q", modifiers: .command)
        }
    }
}
