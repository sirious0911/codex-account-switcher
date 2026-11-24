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
    private let monitorLedger: MacMonitorLedger
    // Only an explicitly injected offline/test bridge enables candidate
    // staging. AppModel.live() injects nil and never opens a Telegram outbox.
    private let telegramStageBridge: MacTelegramCandidateStageBridge?

    init(store: AccountStore, codex: any AccountClient, switchService: any SwitchServicing,
         v2SourceDeviceID: String? = nil,
         v2Publisher: MacQuotaV2HttpsPublisher? = nil,
         telegramStageBridge: MacTelegramCandidateStageBridge? = nil,
         injectedMonitorLedger: MacMonitorLedger? = nil) {
        self.telegramStageBridge = telegramStageBridge
        self.monitorLedger = injectedMonitorLedger ?? MacMonitorLedger(directory:
            FileManager.default.urls(
                for: .applicationSupportDirectory, in: .userDomainMask)[0]
                .appending(path: "Codex Account Switcher Personal/usage-monitor"))
        self.quotaV2Publisher = v2Publisher
        self.quotaV2Encoder = v2Publisher == nil ? nil :
            v2SourceDeviceID.flatMap { MacQuotaV2FrameEncoder(sourceDeviceID: $0) }
        super.init(store: store, codex: codex, switchService: switchService)
        onChange = { [weak self] in
            guard let self else { return }
            self.objectWillChange.send()
            self.refreshV2Identity()
            self.telegramStageBridge?.invalidateIfUnverified(
                activeProfileID: self.activeAccountID,
                identityConfirmed: self.activeIdentityConfirmed,
                isMutating: self.isMutating, isAddingAccount: self.isAddingAccount,
                hasVisibleError: self.visibleError != nil)
        }
        refreshV2Identity()
        monitorHistory = monitorLedger.history()
        onMonitorObservation = { [weak self] observation in
            guard let self else { return }
            let eligibleForOfflineStage = self.telegramStageBridge != nil &&
                self.activeIdentityConfirmed && !self.isMutating &&
                !self.isAddingAccount && self.visibleError == nil &&
                self.activeAccountID == observation.profileID
            let activeProfile = eligibleForOfflineStage ?
                self.accounts.first(where: { $0.id == observation.profileID }) : nil
            let verifiedProfile = activeProfile.flatMap { profile in
                MacTelegramCandidateStageBridge.matchesProviderIdentity(
                    observation: observation, profile: profile) ? profile : nil
            }
            if activeProfile != nil && verifiedProfile == nil {
                self.telegramStageBridge?.invalidate()
            }
            var monitorRecorded = false
            do {
                // Preserve ordinary monitor behavior while default-OFF or
                // observing another profile. Only the verified active profile
                // may durably capture Telegram candidates.
                let emittedCount: Int
                if verifiedProfile != nil {
                    emittedCount = try self.monitorLedger.recordWithTelegramCandidates(
                        observation).eventCount
                } else {
                    emittedCount = try self.monitorLedger.record(observation)
                }
                // The global history list changes only when a new mock event
                // is committed. Normal quota polls are far more frequent and
                // must not re-read/decode every per-account state.json.
                if emittedCount > 0 {
                    self.monitorHistory = self.monitorLedger.history()
                }
                monitorRecorded = true
            } catch {
                // Keep the last successfully displayed history on a failed
                // observation; re-reading every folder cannot repair the
                // failed write and must not interrupt normal account polls.
                self.telegramStageBridge?.invalidate()
            }
            if monitorRecorded,
               let bridge = self.telegramStageBridge,
               let profile = verifiedProfile {
                do {
                    _ = try bridge.stageAfterVerifiedObservation(
                        monitorLedger: self.monitorLedger,
                        observation: observation, profile: profile,
                        activeProfileID: self.activeAccountID,
                        identityConfirmed: self.activeIdentityConfirmed,
                        isMutating: self.isMutating,
                        isAddingAccount: self.isAddingAccount,
                        hasVisibleError: self.visibleError != nil,
                        nowMilliseconds: Int64(Date().timeIntervalSince1970 * 1000))
                } catch {
                    // The confirmed candidate remains in the monitor snapshot.
                    // Staging failures never fall back to a direct send or
                    // affect account switching and the usage UI.
                    bridge.invalidate()
                }
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
