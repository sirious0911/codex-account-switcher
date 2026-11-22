using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexAccountSwitcher;

/// <summary>
/// First-party Windows TLS-1.3 / certificate-pinned / HMAC client for the
/// isolated Codex-chat authority. NO Codex turn sender exists in this class.
/// All mutating authority calls are disabled by default and never confer
/// permission to send a message.
/// </summary>
public sealed record CodexDispatchReadiness(string Code,bool LiveSendAllowed);
public sealed record CodexAuthorityLease(long Epoch,long ExpiresAtMs);
public sealed record CodexOneShotPermit(
    string EventKey,string AccountSha256,string ThreadId,Guid ProfileId,
    string Lane,long ResetAtUnix,string OwnerDevice,long OwnerEpoch,
    long PermitExpiresAtMs);

public sealed class CodexChatSignedAuthorityClient : IDisposable
{
    private static readonly Regex DeviceIdPattern =
        new("^[A-Za-z0-9-]{1,48}$", RegexOptions.CultureInvariant);
    private static readonly Regex Hex64 =
        new("^[a-f0-9]{64}$", RegexOptions.CultureInvariant);
    private readonly HttpClient client;
    private readonly string deviceId;
    private readonly byte[] hmacKey;
    private readonly bool allowIsolatedLeaseReservations;
    private readonly bool allowLiveCodexDispatch;
    private bool disposed;

    public CodexChatSignedAuthorityClient(
        Uri endpoint,
        string enrolledDeviceId,
        string hmacKeyHex,
        byte[] trustedCertificatePem,
        string reviewedServerCertificateSha256,
        bool allowRemoteEndpoint = false,
        bool allowIsolatedLeaseReservations = false,
        bool allowLiveCodexDispatch = false)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != Uri.UriSchemeHttps ||
            endpoint.UserInfo.Length != 0 || !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment) ||
            endpoint.AbsolutePath != "/" ||
            (!IsLoopbackHost(endpoint.Host) && !allowRemoteEndpoint))
            throw new ArgumentException("Explicit HTTPS authority endpoint required");
        if (string.IsNullOrWhiteSpace(enrolledDeviceId) ||
            !DeviceIdPattern.IsMatch(enrolledDeviceId) ||
            !Hex64.IsMatch(hmacKeyHex ?? "") ||
            !Hex64.IsMatch(reviewedServerCertificateSha256 ?? "") ||
            trustedCertificatePem is null || trustedCertificatePem.Length < 100)
            throw new ArgumentException("Explicit identity, pinned TLS and HMAC required");

        deviceId = enrolledDeviceId;
        hmacKey = Convert.FromHexString(hmacKeyHex!);
        var fingerprint = Convert.FromHexString(reviewedServerCertificateSha256!);
        var root = X509Certificate2.CreateFromPem(
            Encoding.UTF8.GetString(trustedCertificatePem));
        var handler = new HttpClientHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            SslProtocols = SslProtocols.Tls13
        };
        handler.ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
        {
            if (certificate is null ||
                errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch) ||
                errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable) ||
                DateTimeOffset.UtcNow < certificate.NotBefore.ToUniversalTime() ||
                DateTimeOffset.UtcNow > certificate.NotAfter.ToUniversalTime())
                return false;
            var digest = SHA256.HashData(certificate.RawData);
            if (!CryptographicOperations.FixedTimeEquals(digest, fingerprint))
                return false;
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(root);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            return chain.Build(certificate);
        };
        client = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = endpoint,
            Timeout = TimeSpan.FromSeconds(12)
        };
        this.allowIsolatedLeaseReservations = allowIsolatedLeaseReservations;
        this.allowLiveCodexDispatch = allowLiveCodexDispatch;
    }

    private static bool IsLoopbackHost(string host) =>
        string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase);

    public Task<string> GetStatusCodeAsync(
        CancellationToken cancellationToken = default) =>
        CallAsync(HttpMethod.Get, "/v1/codex-chat/status", null, cancellationToken);

    public Task<string> GetExistingEventStatusCodeAsync(
        string eventKey, CancellationToken cancellationToken = default)
    {
        if (!Hex64.IsMatch(eventKey ?? ""))
            throw new ArgumentException("Canonical event SHA256 required");
        return CallAsync(HttpMethod.Get,
            "/v1/codex-chat/events/" + eventKey, null, cancellationToken);
    }

    /// <summary>
    /// Read-only, signed and certificate-pinned check of an existing
    /// reservation's owner/epoch. This is NOT a live send permit.
    /// </summary>
    public async Task<CodexDispatchReadiness> GetDispatchReadinessAsync(
        string eventKey, string accountSha256, string threadId,
        string expectedOwnerDevice, long expectedEpoch, long resetAtUnix,
        CancellationToken cancellationToken = default)
    {
        if (!Hex64.IsMatch(eventKey ?? "") ||
            !Hex64.IsMatch(accountSha256 ?? "") ||
            !Guid.TryParseExact(threadId, "D", out _) ||
            string.IsNullOrWhiteSpace(expectedOwnerDevice) ||
            expectedEpoch <= 0 || resetAtUnix <= 0)
            throw new ArgumentException("Exact reservation metadata required");
        var result = await CallJsonAsync(HttpMethod.Get,
            "/v1/codex-chat/dispatch/readiness/" + eventKey,
            null,cancellationToken);
        var code = result.GetProperty("code").GetString() ?? "HOLD_INVALID_CODE";
        if (code is not ("HOLD_OPERATOR_AUTOMATIC_SEND_APPROVAL_REQUIRED" or
            "READY_FOR_SINGLE_USE_CLAIM"))
            return new(code, false);

        static bool TextEquals(JsonElement source, string field, string? value) =>
            source.TryGetProperty(field, out var got) &&
            got.ValueKind == JsonValueKind.String &&
            got.GetString() == value;
        static bool IntEquals(JsonElement source, string field, long value) =>
            source.TryGetProperty(field, out var got) &&
            got.ValueKind == JsonValueKind.Number &&
            got.TryGetInt64(out var actual) && actual == value;
        if (!TextEquals(result,"eventKey",eventKey) ||
            !TextEquals(result,"accountSha256",accountSha256) ||
            !TextEquals(result,"threadId",threadId) ||
            !TextEquals(result,"ownerDevice",expectedOwnerDevice) ||
            !IntEquals(result,"epoch",expectedEpoch) ||
            !IntEquals(result,"resetAtUnix",resetAtUnix) ||
            !result.TryGetProperty("leaseExpiresAtMs",out var expires) ||
            !expires.TryGetInt64(out var expiresAt) ||
            expiresAt <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() ||
            !result.TryGetProperty("permitExpiresAtMs",out var permitExpiry) ||
            permitExpiry.ValueKind != JsonValueKind.Null)
            throw new InvalidDataException("Authority readiness identity or expiry mismatch");
        return new(code,false);
    }

    /// <summary>
    /// Consumes the server's one-shot grant. The response is bound to the
    /// exact active account, profile, thread, lane, event, owner and epoch.
    /// Never retry if the connection drops: the server may have consumed it.
    /// </summary>
    public async Task<CodexOneShotPermit?> ClaimOnceAsync(
        string eventKey,Guid profileId,string accountSha256,
        string threadId,string lane,long resetAtUnix,
        string expectedOwnerDevice,long ownerEpoch,
        CancellationToken cancellationToken = default)
    {
        if (!allowLiveCodexDispatch)
            throw new InvalidOperationException("Live dispatch disabled in this process");
        if (!Hex64.IsMatch(eventKey??"") ||
            !Hex64.IsMatch(accountSha256??"") ||
            !Guid.TryParseExact(threadId,"D",out _) ||
            profileId==Guid.Empty ||
            lane is not ("CODEX_FIVE_HOUR" or "CODEX_SEVEN_DAY") ||
            resetAtUnix<=0 || ownerEpoch<=0 ||
            string.IsNullOrWhiteSpace(expectedOwnerDevice) ||
            expectedOwnerDevice!=deviceId)
            throw new ArgumentException("Exact approved dispatch identity required");

        var result=await CallJsonAsync(HttpMethod.Post,
            "/v1/codex-chat/dispatch/claim",
            new { epoch=ownerEpoch,eventKey,lane,profileId,
                accountSha256,threadId,resetAtUnix },
            cancellationToken,expectClaim:true);
        if (!result.TryGetProperty("code",out var code) ||
            code.GetString()!="SINGLE_USE_CODEX_GREETING_CLAIMED")
            return null;
        static bool Match(JsonElement root,string field,string? expected) =>
            root.TryGetProperty(field,out var value) &&
            value.ValueKind==JsonValueKind.String &&
            value.GetString()==expected;
        static bool Same(JsonElement root,string field,long expected) =>
            root.TryGetProperty(field,out var value) &&
            value.ValueKind==JsonValueKind.Number &&
            value.TryGetInt64(out var received) && received==expected;
        var now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (!Match(result,"eventKey",eventKey) ||
            !Match(result,"profileId",profileId.ToString("D")) ||
            !Match(result,"accountSha256",accountSha256) ||
            !Match(result,"threadId",threadId) ||
            !Match(result,"lane",lane) ||
            !Match(result,"ownerDevice",expectedOwnerDevice) ||
            !Same(result,"epoch",ownerEpoch) ||
            !Same(result,"resetAtUnix",resetAtUnix) ||
            !result.TryGetProperty("permitExpiresAtMs",out var exp) ||
            exp.ValueKind!=JsonValueKind.Number ||
            !exp.TryGetInt64(out var expires) ||
            expires<=now || expires>now+5_000 ||
            !result.TryGetProperty("actualSendAuthorized",out var allowed) ||
            allowed.ValueKind!=JsonValueKind.True)
            throw new InvalidDataException("One-shot authority grant inconsistent or expired");
        return new(eventKey!,accountSha256!,threadId!,profileId,
            lane,resetAtUnix,expectedOwnerDevice,ownerEpoch,expires);
    }

    /// <summary>
    /// Live-orchestrator-only, signed central lease. Does NOT authorize any
    /// turn/start; the server's separately consumed claim is still required.
    /// </summary>
    public async Task<CodexAuthorityLease?> AcquireSenderLeaseAsync(
        CancellationToken cancellationToken=default)
    {
        if(!allowLiveCodexDispatch)
            throw new InvalidOperationException("Live Codex dispatch disabled");
        var result=await CallJsonAsync(HttpMethod.Post,
            "/v1/codex-chat/lease/acquire",
            new {ttlMs=20000},cancellationToken);
        if(!result.TryGetProperty("code",out var code) ||
           code.GetString()!="LEASE_ACQUIRED")return null;
        if(!result.TryGetProperty("epoch",out var epoch) ||
           !epoch.TryGetInt64(out var e)||e<=0 ||
           !result.TryGetProperty("expiresAtMs",out var expires) ||
           !expires.TryGetInt64(out var time)||
           time<=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            throw new InvalidDataException("Authority lease identity invalid");
        return new CodexAuthorityLease(e,time);
    }

    public async Task<string> ReserveResetEventAsync(
        long epoch,string eventKey,string lane,string accountSha256,
        string threadId,long resetAtUnix,
        CancellationToken cancellationToken=default)
    {
        if(!allowLiveCodexDispatch)
            throw new InvalidOperationException("Live Codex dispatch disabled");
        if(epoch<=0 || !Hex64.IsMatch(eventKey??"") ||
           !Hex64.IsMatch(accountSha256??"") ||
           !Guid.TryParseExact(threadId,"D",out _) ||
           lane is not ("CODEX_FIVE_HOUR" or "CODEX_SEVEN_DAY") ||
           resetAtUnix<=0)
            throw new ArgumentException("Invalid exact reset reservation");
        var result=await CallJsonAsync(HttpMethod.Post,
            "/v1/codex-chat/events/reserve",
            new {epoch,eventKey,lane,accountSha256,threadId,resetAtUnix},
            cancellationToken);
        return result.GetProperty("code").GetString()??
            "HOLD_AUTHORITY_INVALID_RESPONSE";
    }

    /// <summary>
    /// OFFLINE TEST ONLY: reservation cannot authorize Codex messages.
    /// The constructor's explicit isolated opt-in is required.
    /// </summary>
    public Task<string> AcquireIsolatedLeaseAsync(
        CancellationToken cancellationToken = default)
    {
        RequireIsolatedOptIn();
        return CallAsync(HttpMethod.Post, "/v1/codex-chat/lease/acquire",
            new { ttlMs = 20000 }, cancellationToken);
    }

    public Task<string> ReserveIsolatedEventAsync(
        long epoch, string eventKey, string lane, string accountSha256,
        string threadId, long resetAtUnix,
        CancellationToken cancellationToken = default)
    {
        RequireIsolatedOptIn();
        if (epoch <= 0 || !Hex64.IsMatch(eventKey ?? "") ||
            !Hex64.IsMatch(accountSha256 ?? "") ||
            !Guid.TryParseExact(threadId, "D", out _) ||
            lane is not ("CODEX_FIVE_HOUR" or "CODEX_SEVEN_DAY") ||
            resetAtUnix <= 0)
            throw new ArgumentException("Canonical known Codex event required");
        return CallAsync(HttpMethod.Post, "/v1/codex-chat/events/reserve",
            new { epoch, eventKey, lane, accountSha256, threadId, resetAtUnix },
            cancellationToken);
    }

    private void RequireIsolatedOptIn()
    {
        if (!allowIsolatedLeaseReservations)
            throw new InvalidOperationException(
                "Codex chat authority writes disabled by default");
    }

    private async Task<string> CallAsync(
        HttpMethod method, string path, object? parameters,
        CancellationToken cancellationToken) =>
        (await CallJsonAsync(method,path,parameters,cancellationToken))
            .GetProperty("code").GetString() ?? "HOLD_INVALID_CODE";

    private async Task<JsonElement> CallJsonAsync(
        HttpMethod method, string path, object? parameters,
        CancellationToken cancellationToken,bool expectClaim=false)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!path.StartsWith("/v1/codex-chat/", StringComparison.Ordinal))
            throw new InvalidOperationException("Codex-only authority route required");
        var body = parameters is null ? "" : JsonSerializer.Serialize(parameters);
        if (Encoding.UTF8.GetByteCount(body) > 1024)
            throw new InvalidOperationException("Authority signed payload too large");
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var digest = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(body)));
        var canonical = string.Join("\n",
            "CODEX_CHAT_SENDER_AUTH_V1", deviceId, method.Method, path,
            timestamp.ToString(CultureInfo.InvariantCulture),
            nonce, digest, "");
        var signature = Convert.ToHexStringLower(
            HMACSHA256.HashData(hmacKey, Encoding.UTF8.GetBytes(canonical)));
        using var req = new HttpRequestMessage(method, path);
        req.Headers.TryAddWithoutValidation("x-device-id", deviceId);
        req.Headers.TryAddWithoutValidation("x-timestamp-ms",
            timestamp.ToString(CultureInfo.InvariantCulture));
        req.Headers.TryAddWithoutValidation("x-nonce", nonce);
        req.Headers.TryAddWithoutValidation("x-signature", signature);
        if (method == HttpMethod.Post)
        {
            req.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
            req.Content.Headers.ContentType =
                new MediaTypeHeaderValue("application/json");
        }
        using var response = await client.SendAsync(req,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new InvalidOperationException(
                "Codex authority refused request: HTTP " + (int)response.StatusCode);
        await using var payload =
            await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[4097];
        int length = 0;
        while (length < buffer.Length)
        {
            var n = await payload.ReadAsync(
                buffer.AsMemory(length), cancellationToken);
            if (n == 0) break;
            length += n;
        }
        if (length == buffer.Length || length == 0)
            throw new InvalidDataException("Untrusted authority response length");
        using var json = JsonDocument.Parse(buffer.AsMemory(0,length));
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("code", out var code) ||
            code.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("actualSendAuthorized", out var allowed) ||
            allowed.ValueKind != (
                expectClaim && code.GetString()=="SINGLE_USE_CODEX_GREETING_CLAIMED"
                  ? JsonValueKind.True : JsonValueKind.False) ||
            !root.TryGetProperty("automaticRetryAllowed", out var retries) ||
            retries.ValueKind != JsonValueKind.False ||
            !root.TryGetProperty("remoteSenderActivated", out var remote) ||
            remote.ValueKind != JsonValueKind.False)
            throw new InvalidDataException("Codex authority safe flags absent");
        return root.Clone();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        client.Dispose();
        CryptographicOperations.ZeroMemory(hmacKey);
    }
}
