import AppKit
import Foundation
import SwitcherCore

/// Mac-only supplemental reads; the common five-minute baseline is unchanged.
@MainActor
final class MacAdaptiveUsageRefresh {
    private var cadence = AdaptiveUsageCadence(now: Date())
    private var loop: Task<Void, Never>?

    func start(model: AccountController) {
        guard loop == nil else { return }
        let previous = model.onMonitorObservation
        model.onMonitorObservation = { [weak self, weak model] observation in
            previous?(observation)
            guard let self, let model,
                  let id = self.eligibleProfile(model),
                  id == observation.profileID,
                  case let .loaded(usage)? = model.usageStates[id] else { return }
            let now = Date()
            self.cadence.select(id, now: now)
            self.cadence.observed(profile: id, now: now,
                fiveReset: usage.fiveHourResetsAt, weeklyReset: usage.resetsAt)
        }
        loop = Task { [weak self, weak model] in
            while !Task.isCancelled {
                do { try await Task.sleep(for: .seconds(5)) } catch { return }
                guard let self, let model else { return }
                let now = Date()
                (model as? AppModel)?.evaluateGreetingPreview()
                self.cadence.select(self.eligibleProfile(model), now: now)
                // Foreground plus recent session metadata, as on Windows.
                // Never read rollout contents, or infer that a model is generating.
                let home = ProcessInfo.processInfo.environment["CODEX_HOME"]
                    .map { URL(fileURLWithPath: $0, isDirectory: true) }
                    ?? FileManager.default.homeDirectoryForCurrentUser
                        .appendingPathComponent(".codex", isDirectory: true)
                let foreground = NSWorkspace.shared.frontmostApplication?.bundleIdentifier == "com.openai.codex"
                let active = foreground || RecentCodexSessionMetadata.hasRecentWork(home: home, now: now)
                if self.cadence.isDue(now: now, activeWork: active),
                   model.refreshActiveUsagePreservingBaseline() {
                    self.cadence.didRequest(now: now)
                }
            }
        }
    }

    private func eligibleProfile(_ model: AccountController) -> UUID? {
        guard model.activeIdentityConfirmed, !model.isMutating,
              !model.isAddingAccount, model.visibleError == nil else { return nil }
        return model.activeAccountID
    }
}
