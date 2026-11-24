import Foundation

/// A deliberately OFFLINE integration seam. The provided callback is a mock,
/// never a Telegram client. No AppModel.live() or automatic startup wiring.
@MainActor
final class MacTelegramOfflineSendCoordinator {
    enum Outcome: Equatable {
        case blocked
        case acknowledgedByMock(Int64)
        case uncertainNoRetry
    }

    private let outbox: MacTelegramLocalOutbox

    init(outbox: MacTelegramLocalOutbox) {
        self.outbox = outbox
    }

    /// The only callback path is injected by offline tests. A real executor
    /// must separately prove local transport completion and explicit consent.
    func simulateOnce(
        eventKey: String,
        liveConsent: () -> Bool,
        mockTransport: (String) async throws -> Int64?
    ) async -> Outcome {
        guard liveConsent(), !Task.isCancelled else { return .blocked }
        do { try outbox.reserveBeforeSend(eventKey) }
        catch { return .blocked }

        // A revoked approval after the durable reservation prevents even
        // synthetic invocation. The event remains terminally held.
        guard liveConsent(), !Task.isCancelled else {
            try? outbox.markUncertainNoRetry(eventKey)
            return .blocked
        }

        do {
            guard let messageId = try await mockTransport(eventKey),
                  messageId > 0 else {
                try? outbox.markUncertainNoRetry(eventKey)
                return .uncertainNoRetry
            }
            try outbox.acknowledgeCompletedLocalSend(eventKey,
                messageId: messageId)
            return .acknowledgedByMock(messageId)
        } catch {
            // A callback may have been invoked even if it subsequently throws.
            // Never retry the event; preserve its in-flight marker on failure.
            try? outbox.markUncertainNoRetry(eventKey)
            return .uncertainNoRetry
        }
    }
}
