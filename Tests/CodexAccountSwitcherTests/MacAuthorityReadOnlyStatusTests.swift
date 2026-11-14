import Foundation
import Testing
@testable import CodexAccountSwitcher

/// Pure macOS presentation checks. No Authority network, account change,
/// credential storage, Keychain access or Telegram API can occur here.
@MainActor
struct MacAuthorityReadOnlyStatusTests {
    private let now: Int64 = 1_794_660_000_000

    @Test func signedOwnerDisplaysOnlyLeaseObservation() {
        let active = MacSenderAuthorityResult(code: "STATUS",
            lease: .init(ownerDevice: "windows-1", epoch: 7,
                expiresAtMs: now + 20_000),
            authorityTimeMs: now)
        let message = MacAuthorityReadOnlyStatus.describe(active)
        #expect(message.contains("Sender windows-1"))
        #expect(message.contains("epoch 7"))
        #expect(message.contains("만료 UTC"))
        #expect(message.contains("Telegram 발송 권한 없음"))
        #expect(!active.actualSendAuthorized)
        #expect(!active.senderTransitionAuthorized)
    }

    @Test func unownedExpiredAndFailureRemainReadOnlyAndFailClosed() {
        let empty = MacSenderAuthorityResult(code: "STATUS",
            lease: .init(ownerDevice: nil, epoch: 3, expiresAtMs: 0),
            authorityTimeMs: now)
        #expect(MacAuthorityReadOnlyStatus.describe(empty).contains("Sender 없음"))
        let expired = MacSenderAuthorityResult(code: "STATUS",
            lease: .init(ownerDevice: "mac-1", epoch: 2,
                expiresAtMs: now - 1),
            authorityTimeMs: now)
        let blocked = MacAuthorityReadOnlyStatus.describe(expired)
        #expect(blocked.contains("임대 만료"))
        #expect(!blocked.contains("Sender mac-1"))
        let failure = MacAuthorityReadOnlyStatus.describe(
            .hold("HOLD_UNAUTHENTICATED"))
        #expect(failure.contains("검증 불가"))
        #expect(!failure.contains("HOLD_UNAUTHENTICATED"))
        #expect(failure.contains("Telegram 발송 권한 없음"))
    }
}
