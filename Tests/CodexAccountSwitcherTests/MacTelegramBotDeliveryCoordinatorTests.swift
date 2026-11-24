import Foundation
import Testing
@testable import CodexAccountSwitcher

/// No network client is constructed. Every postOnce closure is a local fake.
@MainActor
struct MacTelegramBotDeliveryCoordinatorTests {
    private let account =
        "9cb32632c9c9e4460e7a6c44d4de4a5c79b97f934af458d40ebbac4634d95f1c"

    @MainActor
    private struct SyntheticCredentials: MacTelegramBotCredentialSource {
        var enabled = true

        func load(for lane: MacTelegramQuotaEventIdentity.Lane)
            -> MacTelegramBotApiContract.Credentials? {
            guard enabled else { return nil }
            let token = lane == .fiveHour
                ? "1234567890:abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ123456"
                : "1234567891:abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ123456"
            return MacTelegramBotApiContract.Credentials(
                token: token, chatId: "-1001234567890")
        }
    }

    private enum MockFailure: Error { case responseLost }

    private func fixture() -> URL {
        FileManager.default.temporaryDirectory.appending(
            path: "mac-telegram-bot-attempt-\(UUID().uuidString)")
    }

    private func stage(_ outbox: MacTelegramLocalOutbox,
                       lane: MacTelegramQuotaEventIdentity.Lane = .fiveHour)
        throws -> String {
        try outbox.stage(.init(
            lane: lane, accountIdSHA256: account,
            previousResetAtUnixMs: 1_800_000_000_000,
            currentResetAtUnixMs: 1_800_018_000_000,
            observedAtUnixMs: 1_800_000_060_000,
            confirmedByTwoObservations: true))
    }

    @Test func matchingBotFakeACKAndReplayDenial() async throws {
        let root = fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        let outbox = try MacTelegramLocalOutbox(directory: root)
        let event = try stage(outbox)
        let runner = MacTelegramBotDeliveryCoordinator(
            outbox: outbox, credentials: SyntheticCredentials())
        var attempts = 0
        let result = await runner.attemptExactEventOnce(
            eventKey: event, lane: .fiveHour,
            stillApproved: { true },
            postOnce: { req in
                attempts += 1
                #expect((try? outbox.status(event)) == .reservedBeforeSend)
                let body = try #require(req.httpBody)
                let json = try #require(JSONSerialization.jsonObject(
                    with: body) as? [String: Any])
                #expect(json["disable_notification"] as? Bool == true)
                return (Data(#"{"ok":true,"result":{"message_id":74,"chat":{"id":-1001234567890}}}"#.utf8), 200)
            })
        #expect(result == .acknowledged(74))
        #expect(attempts == 1)
        #expect(try outbox.status(event) == .acknowledged)
        let replay = await runner.attemptExactEventOnce(
            eventKey: event, lane: .fiveHour,
            stillApproved: { true },
            postOnce: { _ in
                attempts += 1
                return (Data(), 200)
            })
        #expect(replay == .blocked)
        #expect(attempts == 1)
    }

    @Test func wrongBotNoConsentOrMissingKeyNeverReserves() async throws {
        let root = fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        let outbox = try MacTelegramLocalOutbox(directory: root)
        let event = try stage(outbox, lane: .weekly)
        let runner = MacTelegramBotDeliveryCoordinator(
            outbox: outbox, credentials: SyntheticCredentials())
        var invokes = 0
        let wrongRoute = await runner.attemptExactEventOnce(
            eventKey: event, lane: .fiveHour,
            stillApproved: { true },
            postOnce: { _ in invokes += 1; return (Data(), 200) })
        #expect(wrongRoute == .blocked)
        let off = await runner.attemptExactEventOnce(
            eventKey: event, lane: .weekly,
            stillApproved: { false },
            postOnce: { _ in invokes += 1; return (Data(), 200) })
        #expect(off == .blocked)
        let noCredential = MacTelegramBotDeliveryCoordinator(
            outbox: outbox, credentials: SyntheticCredentials(enabled: false))
        let missing = await noCredential.attemptExactEventOnce(
            eventKey: event, lane: .weekly,
            stillApproved: { true },
            postOnce: { _ in invokes += 1; return (Data(), 200) })
        #expect(missing == .blocked)
        #expect(invokes == 0)
        #expect(try outbox.status(event) == .pending)
    }

    @Test func fakeHTTP200WrongChatCannotAcknowledge() async throws {
        let root = fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        let outbox = try MacTelegramLocalOutbox(directory: root)
        let event = try stage(outbox, lane: .weekly)
        let runner = MacTelegramBotDeliveryCoordinator(
            outbox: outbox, credentials: SyntheticCredentials())
        var count = 0
        let forged = await runner.attemptExactEventOnce(
            eventKey: event, lane: .weekly,
            stillApproved: { true },
            postOnce: { req in
                count += 1
                let bytes = try #require(req.httpBody)
                let values = try #require(JSONSerialization.jsonObject(
                    with: bytes) as? [String: Any])
                #expect(values["disable_notification"] as? Bool == false)
                return (Data(#"{"ok":true,"result":{"message_id":19,"chat":{"id":123456}}}"#.utf8), 200)
            })
        #expect(forged == .uncertainNoRetry)
        #expect(count == 1)
        #expect(try outbox.status(event) == .uncertainNoRetry)
    }

    @Test func consentRevokedAfterReserveDoesNotPost() async throws {
        let root = fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        let outbox = try MacTelegramLocalOutbox(directory: root)
        let event = try stage(outbox)
        let runner = MacTelegramBotDeliveryCoordinator(
            outbox: outbox, credentials: SyntheticCredentials())
        var approvals = 0
        var calls = 0
        let result = await runner.attemptExactEventOnce(
            eventKey: event, lane: .fiveHour,
            stillApproved: {
                approvals += 1
                // The third approval happens AFTER durable reservation.
                return approvals < 3
            },
            postOnce: { _ in
                calls += 1
                return (Data(), 200)
            })
        #expect(result == .blocked)
        #expect(calls == 0)
        #expect(try outbox.status(event) == .uncertainNoRetry)
    }

    @Test func responseLostIsUncertainAndBlocksLaterSend() async throws {
        let root = fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        let outbox = try MacTelegramLocalOutbox(directory: root)
        let event = try stage(outbox)
        let runner = MacTelegramBotDeliveryCoordinator(
            outbox: outbox, credentials: SyntheticCredentials())
        var count = 0
        let lost = await runner.attemptExactEventOnce(
            eventKey: event, lane: .fiveHour,
            stillApproved: { true },
            postOnce: { _ in
                count += 1
                throw MockFailure.responseLost
            })
        #expect(lost == .uncertainNoRetry)
        #expect(count == 1)
        #expect(try outbox.status(event) == .uncertainNoRetry)

        let next = try stage(outbox, lane: .weekly)
        let blocked = await runner.attemptExactEventOnce(
            eventKey: next, lane: .weekly,
            stillApproved: { true },
            postOnce: { _ in
                count += 1
                return (Data(), 200)
            })
        #expect(blocked == .blocked)
        #expect(count == 1)
        #expect(try outbox.status(next) == .pending)
    }
}
