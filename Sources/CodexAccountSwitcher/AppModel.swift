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
    // V2 has no enrollment or sender in AppModel.live(); default OFF.
    private let quotaV2Encoder: MacQuotaV2FrameEncoder?
    private let quotaV2Publisher: MacQuotaV2HttpsPublisher?
    private var quotaV2ActiveProfile: UUID?
    private var quotaV2IdentityEpoch: UInt64 = 0
    let remoteQuotaViewer = MacRemoteQuotaViewerModel()
    private let monitorLedger = MacMonitorLedger(directory:
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appending(path: "Codex Account Switcher Personal/usage-monitor"))

    init(store: AccountStore, codex: any AccountClient, switchService: any SwitchServicing,
         v2SourceDeviceID: String? = nil,
         v2Publisher: MacQuotaV2HttpsPublisher? = nil) {
        self.quotaV2Publisher = v2Publisher
        self.quotaV2Encoder = v2Publisher == nil ? nil :
            v2SourceDeviceID.flatMap { MacQuotaV2FrameEncoder(sourceDeviceID: $0) }
        super.init(store: store, codex: codex, switchService: switchService)
        onChange = { [weak self] in
            guard let self else { return }
            self.objectWillChange.send()
            self.refreshV2Identity()
        }
        refreshV2Identity()
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

            // V2 is strictly opt-in by an explicitly injected offline
            // enrollment. AppModel.live() injects nothing and stays OFF.
            if let encoder = quotaV2Encoder,
               let publisher = quotaV2Publisher,
               let profile = accounts.first(where: { $0.id == observation.profileID }),
               let frame = encoder.makeWeeklyFrame(
                   profile: profile, observation: observation, now: Date()) {
                let acceptedEpoch = quotaV2IdentityEpoch
                Task { @MainActor [weak self] in
                    guard let self,
                          self.quotaV2IdentityEpoch == acceptedEpoch,
                          self.activeIdentityConfirmed,
                          !self.isMutating, !self.isAddingAccount,
                          self.activeAccountID == observation.profileID else { return }
                    _ = await publisher.publishOnce(frame)
                }
            }
        }
        refreshLaunchAtLoginStatus()
    }

    /// Rebinds only after an actual verified identity or mutation transition.
    /// A rejected, switched or otherwise unverified epoch cancels one-shot PUT.
    private func refreshV2Identity() {
        guard let encoder = quotaV2Encoder else { return }
        let eligible = activeIdentityConfirmed && !isMutating &&
            !isAddingAccount && visibleError == nil
            ? activeAccountID : nil
        if eligible != quotaV2ActiveProfile {
            quotaV2IdentityEpoch &+= 1
            quotaV2Publisher?.invalidateEpoch()
            quotaV2ActiveProfile = eligible
            encoder.observeActiveProfile(
                eligible, identityConfirmed: eligible != nil,
                isMutating: isMutating, switchPending: isAddingAccount,
                now: Date())
        }
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
