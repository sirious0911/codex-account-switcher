import Foundation

/// An explicitly injected, single-Mac, ONE-ATTEMPT delivery kernel.
/// Has NO default HTTP implementation and is never constructed by AppModel.
/// Test closures must be synthetic. A separately approved production adapter
/// would need to prove its POST has NO implicit redirect or automatic retry.
@MainActor
final class MacTelegramBotDeliveryCoordinator {
    enum Result: Equatable {
        case blocked
        case acknowledged(Int64)
        case uncertainNoRetry
    }

    private let outbox: MacTelegramLocalOutbox
    private let credentials: any MacTelegramBotCredentialSource

    init(outbox: MacTelegramLocalOutbox,
         credentials: any MacTelegramBotCredentialSource) {
        self.outbox = outbox
        self.credentials = credentials
    }

    func attemptExactEventOnce(
        eventKey: String,
        lane: MacTelegramQuotaEventIdentity.Lane,
        stillApproved: () -> Bool,
        postOnce: (URLRequest) async throws -> (Data, Int)
    ) async -> Result {
        guard stillApproved(), !Task.isCancelled else { return .blocked }

        // Route binding is checked against immutable local event metadata,
        // never an untrusted caller's asserted lane.
        guard let text = try? outbox.ordinaryPendingText(eventKey, for: lane),
              let credential = credentials.load(for: lane),
              let request = MacTelegramBotApiContract.makeRequest(
                  lane: lane, credentials: credential,
                  text: text)
        else { return .blocked }

        guard stillApproved(), !Task.isCancelled else { return .blocked }
        do { try outbox.reserveBeforeSend(eventKey) }
        catch { return .blocked }

        // Cancellation AFTER durable reserve is terminal: never undo or
        // blindly reissue a potentially delivered event.
        guard stillApproved(), !Task.isCancelled else {
            try? outbox.markUncertainNoRetry(eventKey)
            return .blocked
        }

        do {
            // Exactly one invocation at THIS layer. No internal retry loop.
            let (bytes, status) = try await postOnce(request)
            guard let messageId =
                MacTelegramBotApiContract.acknowledgedMessageId(
                    bytes: bytes, statusCode: status,
                    expectedChatId: credential.chatId)
            else {
                try? outbox.markUncertainNoRetry(eventKey)
                return .uncertainNoRetry
            }
            try outbox.acknowledgeCompletedLocalSend(
                eventKey, messageId: messageId)
            return .acknowledged(messageId)
        } catch {
            try? outbox.markUncertainNoRetry(eventKey)
            return .uncertainNoRetry
        }
    }
}
