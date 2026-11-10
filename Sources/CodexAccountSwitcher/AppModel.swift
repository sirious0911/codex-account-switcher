import SwitcherCore
import Combine
import Foundation
import ServiceManagement
import AppKit

enum LaunchAtLoginState: Equatable {
    case disabled
    case enabled
    case requiresApproval
    case unavailable

    init(status: SMAppService.Status) {
        switch status {
        case .notRegistered:
            self = .disabled
        case .enabled:
            self = .enabled
        case .requiresApproval:
            self = .requiresApproval
        case .notFound:
            self = .unavailable
        @unknown default:
            self = .unavailable
        }
    }

    var isOn: Bool {
        self == .enabled || self == .requiresApproval
    }
}

@MainActor
final class AppModel: AccountController, @MainActor ObservableObject {
    let objectWillChange = ObservableObjectPublisher()
    @Published private(set) var launchAtLoginState: LaunchAtLoginState = .disabled
    @Published private(set) var monitorHistory: [MacMonitorLedger.HistoryEntry] = []
    private let quotaRelayPublisher = MacQuotaRelayPublisher()
    private let monitorLedger = MacMonitorLedger(directory:
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appending(path: "Codex Account Switcher Personal/usage-monitor"))

    override init(store: AccountStore, codex: any AccountClient, switchService: any SwitchServicing) {
        super.init(store: store, codex: codex, switchService: switchService)
        onChange = { [weak self] in self?.objectWillChange.send() }
        monitorHistory = monitorLedger.history()
        onMonitorObservation = { [weak self] observation in
            guard let self else { return }
            do {
                try self.monitorLedger.record(observation)
                self.monitorHistory = self.monitorLedger.history()
            } catch {
                // Monitoring is supplementary and must not interrupt account switching.
                self.monitorHistory = self.monitorLedger.history()
            }
            // Only the freshly observed, verified active account may publish
            // its non-secret 5H/7D view. Disabled unless a separate P3 HTTPS
            // telemetry enrollment is explicitly configured in this process.
            guard self.activeIdentityConfirmed,
                  self.activeAccountID == observation.profileID else { return }
            Task { @MainActor [weak self] in
                guard let self, self.activeIdentityConfirmed,
                      self.activeAccountID == observation.profileID else { return }
                await self.quotaRelayPublisher.publish(observation)
            }
        }
        refreshLaunchAtLoginStatus()
    }

    static func live() -> AppModel {
        let store = AccountStore()
        let codex = CodexClient()
        return AppModel(store: store, codex: codex,
                        switchService: SwitchService(desktop: DesktopController(), store: store, codex: CredentialIdentity()))
    }

    var launchesAtLogin: Bool { launchAtLoginState.isOn }
    var launchAtLoginRequiresApproval: Bool { launchAtLoginState == .requiresApproval }
    var launchAtLoginUnavailable: Bool { launchAtLoginState == .unavailable }

    func refreshLaunchAtLoginStatus() {
        launchAtLoginState = LaunchAtLoginState(status: SMAppService.mainApp.status)
    }

    func setLaunchAtLogin(_ enabled: Bool) {
        do {
            if enabled { try SMAppService.mainApp.register() }
            else { try SMAppService.mainApp.unregister() }
        } catch {
            visibleError = OperationError(stage: nil, titleKey: "operation_failed", messageKey: nil,
                                          message: error.localizedDescription, underlyingDescription: nil)
        }
        refreshLaunchAtLoginStatus()
    }

    func openLoginItemsSettings() { SMAppService.openSystemSettingsLoginItems() }

    func revealResetExpiryLog() {
        if FileManager.default.fileExists(atPath: resetExpiryLogURL.path) {
            NSWorkspace.shared.activateFileViewerSelecting([resetExpiryLogURL])
        } else {
            NSWorkspace.shared.activateFileViewerSelecting([resetExpiryLogURL.deletingLastPathComponent()])
        }
    }
}
