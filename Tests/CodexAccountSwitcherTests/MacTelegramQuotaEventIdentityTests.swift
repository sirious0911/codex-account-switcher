import Foundation
import Testing
@testable import CodexAccountSwitcher

struct MacTelegramQuotaEventIdentityTests {
    private let accountHash =
        "9cb32632c9c9e4460e7a6c44d4de4a5c79b97f934af458d40ebbac4634d95f1c"
    private let previous: Int64 = 1_800_000_000_000
    private let current: Int64 = 1_800_018_000_000

    @Test func windowsAndNodeCompatibleCrossPlatformVectors() {
        #expect(MacTelegramQuotaEventIdentity.schemaVersion == 1)
        let week = MacTelegramQuotaEventIdentity.build(
            lane: .weekly, accountIdSHA256: accountHash,
            previousResetAtUnixMs: previous, currentResetAtUnixMs: current)
        let five = MacTelegramQuotaEventIdentity.build(
            lane: .fiveHour, accountIdSHA256: accountHash,
            previousResetAtUnixMs: previous, currentResetAtUnixMs: current)
        #expect(week == "6a1df6a427dbd778f4562630a26bc318e3d642e435ed854db8bd4b180f627d86")
        #expect(five == "aeb206a6744f984d86c5f3567ea51d5e1fb955b2938e36fbe748bbf6e0aa35a0")
        #expect(week != five)
        #expect(MacTelegramQuotaEventIdentity.build(
            lane: .weekly, accountIdSHA256: accountHash.uppercased(),
            previousResetAtUnixMs: previous, currentResetAtUnixMs: current) == week)
    }

    @Test func invalidAccountAndResetWindowsNeverProduceNotificationIdentity() {
        #expect(MacTelegramQuotaEventIdentity.build(
            lane: .weekly, accountIdSHA256: "raw-account-id",
            previousResetAtUnixMs: previous, currentResetAtUnixMs: current) == nil)
        #expect(MacTelegramQuotaEventIdentity.build(
            lane: .fiveHour, accountIdSHA256: String(repeating: "Z", count: 64),
            previousResetAtUnixMs: previous, currentResetAtUnixMs: current) == nil)
        #expect(MacTelegramQuotaEventIdentity.build(
            lane: .weekly, accountIdSHA256: accountHash,
            previousResetAtUnixMs: current, currentResetAtUnixMs: previous) == nil)
        #expect(MacTelegramQuotaEventIdentity.build(
            lane: .weekly, accountIdSHA256: accountHash,
            previousResetAtUnixMs: previous, currentResetAtUnixMs: current + 999_999_999_999_999) == nil)
    }

    @Test func differentRecoveryIntervalsDoNotShareDeduplicationKey() {
        let week1 = MacTelegramQuotaEventIdentity.build(
            lane: .weekly, accountIdSHA256: accountHash,
            previousResetAtUnixMs: previous, currentResetAtUnixMs: current)
        let week2 = MacTelegramQuotaEventIdentity.build(
            lane: .weekly, accountIdSHA256: accountHash,
            previousResetAtUnixMs: previous + 300_000,
            currentResetAtUnixMs: current)
        #expect(week1 != week2)
    }
}
