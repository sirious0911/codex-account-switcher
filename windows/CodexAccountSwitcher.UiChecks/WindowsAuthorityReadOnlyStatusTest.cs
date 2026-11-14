using System;
using CodexAccountSwitcher.Core;

internal static class WindowsAuthorityReadOnlyStatusTest
{
    internal static void Run()
    {
        var timestamp = DateTimeOffset.Parse("2026-11-14T03:00:00Z")
            .ToUnixTimeMilliseconds();
        var status = new WindowsAuthorityResponse("STATUS",
            Lease: new WindowsAuthorityLease("mac-1", 4, timestamp + 20000),
            AuthorityTimeMs: timestamp);
        var good = WindowsAuthorityReadOnlyStatus.Format(status);
        Assert(good.Contains("mac-1") && good.Contains("epoch 4") &&
            good.Contains("만료 UTC") && good.Contains("Telegram 발송 권한 없음"),
            "The signed read-only status can show the current Sender but must not grant Telegram.");

        var empty = WindowsAuthorityReadOnlyStatus.Format(
            new WindowsAuthorityResponse("STATUS",
                Lease: new WindowsAuthorityLease(null, 4, 0),
                AuthorityTimeMs: timestamp));
        Assert(empty.Contains("Sender 없음") &&
               empty.Contains("Telegram 발송 권한 없음"),
            "No current Sender must not imply a new local Sender.");

        var old = WindowsAuthorityReadOnlyStatus.Format(
            new WindowsAuthorityResponse("STATUS",
                Lease: new WindowsAuthorityLease("windows-1", 2, timestamp - 1),
                AuthorityTimeMs: timestamp));
        Assert(old.Contains("임대 만료") && !old.Contains("Sender windows-1"),
            "Expired remote leases must never be displayed as active ownership.");

        var disconnected = WindowsAuthorityReadOnlyStatus.Format(
            WindowsAuthorityResponse.Hold("HOLD_UNAUTHENTICATED"));
        Assert(disconnected.Contains("검증 불가") &&
               !disconnected.Contains("HOLD_UNAUTHENTICATED"),
            "Never render protocol internals or credentials on authentication failure.");

        var broken = WindowsAuthorityReadOnlyStatus.Format(null);
        Assert(broken.Contains("검증 불가"), "Missing authority must be fail-closed.");
        Console.WriteLine("WINDOWS_AUTHORITY_READONLY_STATUS_OWNER_EPOCH_EXPIRY_PASS");
    }

    private static void Assert(bool pass, string explanation)
    {
        if (!pass) throw new InvalidOperationException(explanation);
    }
}
