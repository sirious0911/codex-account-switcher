namespace CodexAccountSwitcher.Core;

/// <summary>
/// Read-only view of a signed Authority status. It is never a distributed
/// send permit, a local sender role, or a Telegram notification control.
/// </summary>
internal static class WindowsAuthorityReadOnlyStatus
{
    internal static string Format(WindowsAuthorityResponse? response)
    {
        const string suffix = " · 조회 전용 · Telegram 발송 권한 없음";
        if (response is null || response.Code != "STATUS" ||
            response.Lease is null || response.AuthorityTimeMs is null)
            return "Authority: 연결·인증·응답 검증 불가" + suffix;

        var lease = response.Lease;
        var now = response.AuthorityTimeMs.Value;
        if (lease.OwnerDevice is null)
        {
            if (lease.ExpiresAtMs != 0)
                return "Authority: 임대 원장 불일치" + suffix;
            return "Authority: 조회 시점 Sender 없음" +
                " · epoch " + lease.Epoch + suffix;
        }

        if (lease.Epoch <= 0 || lease.ExpiresAtMs <= now)
            return "Authority: 임대 만료 또는 상태 불명확" + suffix;

        DateTimeOffset expiry;
        try { expiry = DateTimeOffset.FromUnixTimeMilliseconds(lease.ExpiresAtMs); }
        catch (ArgumentOutOfRangeException)
        {
            return "Authority: 임대 시각 검증 불가" + suffix;
        }
        return "Authority: 조회 시점 Sender " + lease.OwnerDevice +
            " · epoch " + lease.Epoch +
            " · 만료 UTC " + expiry.ToString("yyyy-MM-dd HH:mm:ss",
                System.Globalization.CultureInfo.InvariantCulture) + suffix;
    }
}
