import CryptoKit
import Foundation
import SwitcherCore

/// Explicit, source-only Mac staging boundary. This class never sends a
/// Telegram request, reads a credential, or starts a background task.
/// AppModel.live() passes no instance: production default remains OFF.
@MainActor
final class MacTelegramCandidateStageBridge {
    private struct ObservationIdentity: Decodable {
        let accountId: String?
    }

    private struct VerifiedReading {
        let profileID: UUID
        let accountHash: String
        let observedAtMs: Int64
    }

    private let outbox: MacTelegramLocalOutbox
    private var lastVerifiedReading: VerifiedReading?

    init(outbox: MacTelegramLocalOutbox) {
        self.outbox = outbox
    }

    /// Pure preflight used BEFORE the monitor records Telegram eligibility.
    /// An observation bearing a different provider account must never create
    /// a recovery candidate for the active profile.
    static func matchesProviderIdentity(
        observation: CodexMonitorObservation, profile: AccountProfile
    ) -> Bool {
        guard let account = CodexUsageSnapshotAdapter.accountKey(for: profile),
              let payload = try? JSONDecoder().decode(
                  ObservationIdentity.self, from: observation.jsonData)
        else { return false }
        return payload.accountId?.trimmingCharacters(
            in: .whitespacesAndNewlines) == account.accountID
    }

    /// Account switching or losing identity/health immediately invalidates the
    /// preceding active reading. An ON->OFF->ON transition must also call this.
    func invalidate() {
        lastVerifiedReading = nil
    }

    func invalidateIfUnverified(activeProfileID: UUID?, identityConfirmed: Bool,
                                isMutating: Bool, isAddingAccount: Bool,
                                hasVisibleError: Bool) {
        guard identityConfirmed, !isMutating, !isAddingAccount,
              !hasVisibleError, activeProfileID == lastVerifiedReading?.profileID
        else { invalidate(); return }
    }

    /// Only a newly fetched, independently verified active-account observation
    /// may reach this method. It requires two consecutive eligible readings
    /// before it can stage already confirmed candidates.
    ///
    /// Resulting .pending markers are not send permissions. No callback here
    /// can reserve a send, settle an ACK, or transmit Telegram.
    func stageAfterVerifiedObservation(
        monitorLedger: MacMonitorLedger,
        observation: CodexMonitorObservation,
        profile: AccountProfile,
        activeProfileID: UUID?,
        identityConfirmed: Bool,
        isMutating: Bool,
        isAddingAccount: Bool,
        hasVisibleError: Bool,
        nowMilliseconds: Int64
    ) throws -> [String] {
        guard identityConfirmed, !isMutating, !isAddingAccount,
              !hasVisibleError, profile.id == activeProfileID,
              profile.id == observation.profileID,
              let account = CodexUsageSnapshotAdapter.accountKey(for: profile),
              nowMilliseconds >= observation.observedAtMilliseconds,
              observation.observedAtMilliseconds > 0,
              nowMilliseconds - observation.observedAtMilliseconds <= 120_000
        else { invalidate(); return [] }

        guard Self.matchesProviderIdentity(
            observation: observation, profile: profile)
        else { invalidate(); return [] }

        let hash = SHA256.hash(data: Data(account.accountID.utf8))
            .map { String(format: "%02x", $0) }.joined()
        let current = VerifiedReading(profileID: profile.id,
            accountHash: hash, observedAtMs: observation.observedAtMilliseconds)
        defer { lastVerifiedReading = current }
        guard let previous = lastVerifiedReading,
              previous.profileID == current.profileID,
              previous.accountHash == current.accountHash,
              current.observedAtMs > previous.observedAtMs,
              current.observedAtMs - previous.observedAtMs <= 20 * 60_000
        else { return [] }

        let candidates = try monitorLedger.persistedTelegramCandidates(
            accountIdSHA256: hash, nowMilliseconds: nowMilliseconds)
        var staged: [String] = []
        for candidate in candidates {
            // persistedTelegramCandidates already validates the account
            // hash and canonical key against the trusted monitor snapshot.
            staged.append(try outbox.stage(candidate))
        }
        return staged
    }
}
