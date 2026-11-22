using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace CodexAccountSwitcher.Core;

/// <summary>
/// Purely synthetic cross-platform HMAC-SHA256 request-frame inspector.
/// The passed-in key/time are UNTRUSTED fixture inputs. Even a MATCH cannot
/// authorize a real device, API invocation, Telegram message or retry.
/// No local credential store, network, filesystem, process or clock reads.
/// </summary>
public static partial class AuthoritySignedFrameV1Review
{
    [GeneratedRegex("^[A-Za-z0-9-]{1,48}$", RegexOptions.CultureInvariant)]
    private static partial Regex DevicePattern();
    [GeneratedRegex("^[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex NoncePattern();
    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Hex64Pattern();
    [GeneratedRegex("^/v1/[A-Za-z0-9/_-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex PathPattern();

    public static AuthoritySignedFrameInspection InspectFixture(
        string? credentialId, string? method, string? path,
        string? bodyUtf8, long issuedAtUtcMs, string? nonce,
        string? providedSignature, string? syntheticKeyHex,
        long authorityNowUtcMs)
    {
        var invalid = new AuthoritySignedFrameInspection(
            AuthoritySignedFrameVerdict.InvalidFrame);
        if(credentialId is null || !DevicePattern().IsMatch(credentialId) ||
           method is not ("GET" or "POST") ||
           path is null || path.Length>160 || !PathPattern().IsMatch(path) ||
           nonce is null || !NoncePattern().IsMatch(nonce) ||
           providedSignature is null || !Hex64Pattern().IsMatch(providedSignature) ||
           syntheticKeyHex is null || !Hex64Pattern().IsMatch(syntheticKeyHex) ||
           issuedAtUtcMs<1577836800000 ||
           authorityNowUtcMs<1577836800000)
            return invalid;

        string bodyHash;
        if(method=="GET")
        {
            if(bodyUtf8 is not null)return invalid;
            bodyHash=Convert.ToHexString(
                SHA256.HashData(Array.Empty<byte>())).ToLowerInvariant();
        }
        else
        {
            if(string.IsNullOrEmpty(bodyUtf8))return invalid;
            var bytes=Encoding.UTF8.GetBytes(bodyUtf8);
            if(bytes.Length>1024)return invalid;
            bodyHash=Convert.ToHexString(
                SHA256.HashData(bytes)).ToLowerInvariant();
        }

        if(Math.Abs((decimal)authorityNowUtcMs-issuedAtUtcMs)>30000)
            return new(AuthoritySignedFrameVerdict.UntrustedClockWindow);

        var canonical=string.Join('\n',
        [
            "AUTHORITY_OFFLINE_SIGNED_V1",
            credentialId,method,path,
            issuedAtUtcMs.ToString(CultureInfo.InvariantCulture),
            nonce,bodyHash,""
        ]);
        byte[] expected;
        try
        {
            expected=HMACSHA256.HashData(
                Convert.FromHexString(syntheticKeyHex),
                Encoding.UTF8.GetBytes(canonical));
        }
        catch(FormatException)
        {
            return invalid;
        }
        var received=Convert.FromHexString(providedSignature);
        return CryptographicOperations.FixedTimeEquals(expected,received)
            ? new(AuthoritySignedFrameVerdict.MatchedSyntheticFixtureOnly)
            : new(AuthoritySignedFrameVerdict.SignatureMismatch);
    }
}

public enum AuthoritySignedFrameVerdict
{
    InvalidFrame,
    UntrustedClockWindow,
    SignatureMismatch,
    MatchedSyntheticFixtureOnly
}

public sealed record AuthoritySignedFrameInspection(
    AuthoritySignedFrameVerdict Verdict)
{
    public bool RealDeviceAuthenticated=>false;
    public bool ActualSendAuthorized=>false;
    public bool ProductionReservationAuthorized=>false;
    public bool AutomaticRetryAllowed=>false;
    public bool SenderTransitionAuthorized=>false;
}
