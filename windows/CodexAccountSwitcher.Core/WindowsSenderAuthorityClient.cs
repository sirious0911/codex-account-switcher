using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace CodexAccountSwitcher.Core;

/// <summary>
/// Authenticated Windows client for the separately deployed Mac Authority.
/// Calls are explicit and return observations/leases, NEVER Telegram send
/// authorization. No Registry, Codex account, bot credentials or auto-start
/// interaction. Not wired to live Telegram transports.
/// </summary>
internal sealed class WindowsSenderAuthorityClient : IDisposable
{
    private const string Prefix = "CODEX_SHARED_SENDER_AUTH_V1";
    private static readonly string[] AllowedCodes =
    [
        "STATUS", "EVENT_FOUND", "EVENT_ABSENT",
        "LEASE_ACQUIRED", "LEASE_RENEWED", "LEASE_RELEASED",
        "EVENT_RESERVED_NO_SEND", "EVENT_MARKED_UNCERTAIN",
        "HOLD_OWNER_ACTIVE", "HOLD_STALE_OWNER",
        "HOLD_EVENT_ALREADY_RESERVED", "HOLD_EVENT_UNCHANGED",
        "HOLD_INVALID_BODY", "HOLD_EPOCH_EXHAUSTED",
        "HOLD_ROUTE", "HOLD_FORBIDDEN_ROLE", "HOLD_UNAUTHENTICATED",
        "HOLD_REPLAY", "HOLD_CLOCK", "HOLD_STORAGE_ERROR",
        "HOLD_INVALID_REQUEST", "HOLD_CLOSED"
    ];

    private readonly HttpClient http;
    private readonly Uri baseAddress;
    private readonly string deviceId;
    private readonly byte[] key;
    private readonly TimeProvider clock;
    private bool disposed;

    internal WindowsSenderAuthorityClient(
        Uri authorityUri,
        string enrolledDeviceId,
        byte[] enrolledHmacKey,
        HttpClient httpClient,
        TimeProvider? timeProvider = null)
    {
        if (!ValidAuthorityUri(authorityUri))
            throw new ArgumentException("An explicit HTTPS authority endpoint is required.");
        if (!ValidDeviceId(enrolledDeviceId) ||
            enrolledHmacKey is not { Length: 32 })
            throw new ArgumentException("An explicitly enrolled device and 32-byte key are required.");
        http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        baseAddress = authorityUri;
        deviceId = enrolledDeviceId;
        key = (byte[])enrolledHmacKey.Clone();
        clock = timeProvider ?? TimeProvider.System;
    }

    internal static bool ValidAuthorityUri(Uri? uri) =>
        uri is { IsAbsoluteUri: true } &&
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.UserInfo.Length == 0 && uri.Query.Length == 0 &&
        uri.Fragment.Length == 0 && uri.AbsolutePath == "/" &&
        uri.Host.Length is > 0 and <= 253;

    internal static bool ValidDeviceId(string? value) =>
        value is { Length: >= 1 and <= 48 } &&
        value.All(c => c is >= 'A' and <= 'Z' or
            >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    internal static string Canonical(
        string deviceId, string method, string path,
        long timestampMs, string nonce, string bodyUtf8)
    {
        var bodyHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(bodyUtf8)));
        return string.Join("\n",
            Prefix, deviceId, method, path,
            timestampMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
            nonce, bodyHash, "");
    }

    internal static string Sign(
        ReadOnlySpan<byte> keyBytes, string deviceId,
        string method, string path, long timestampMs,
        string nonce, string bodyUtf8)
    {
        var message = Encoding.UTF8.GetBytes(
            Canonical(deviceId, method, path, timestampMs, nonce, bodyUtf8));
        return Convert.ToHexStringLower(HMACSHA256.HashData(keyBytes, message));
    }

    internal Task<WindowsAuthorityResponse> StatusAsync(
        CancellationToken ct = default) =>
        RequestAsync(HttpMethod.Get, "/v1/status", "", ct);

    internal Task<WindowsAuthorityResponse> EventAsync(
        string eventKey, CancellationToken ct = default) =>
        ValidEventKey(eventKey)
            ? RequestAsync(HttpMethod.Get, "/v1/events/" + eventKey, "", ct)
            : Task.FromResult(WindowsAuthorityResponse.Hold("HOLD_CLIENT_INPUT"));

    internal Task<WindowsAuthorityResponse> AcquireAsync(
        int ttlMs, CancellationToken ct = default) =>
        ValidTtl(ttlMs)
            ? PostAsync("/v1/lease/acquire", new { ttlMs }, ct)
            : Task.FromResult(WindowsAuthorityResponse.Hold("HOLD_CLIENT_INPUT"));

    internal async Task<WindowsAuthorityResponse> RenewAsync(
        long epoch, int ttlMs, CancellationToken ct = default)
    {
        if (epoch <= 0 || !ValidTtl(ttlMs))
            return WindowsAuthorityResponse.Hold("HOLD_CLIENT_INPUT");
        var reply = await PostAsync("/v1/lease/renew", new { epoch, ttlMs }, ct);
        return reply.Code == "LEASE_RENEWED" && reply.Epoch != epoch
            ? WindowsAuthorityResponse.Hold("HOLD_RESPONSE_MISMATCH") : reply;
    }

    internal async Task<WindowsAuthorityResponse> ReleaseAsync(
        long epoch, CancellationToken ct = default)
    {
        if (epoch <= 0) return WindowsAuthorityResponse.Hold("HOLD_CLIENT_INPUT");
        var reply = await PostAsync("/v1/lease/release", new { epoch }, ct);
        return reply.Code == "LEASE_RELEASED" && reply.Epoch != epoch
            ? WindowsAuthorityResponse.Hold("HOLD_RESPONSE_MISMATCH") : reply;
    }

    internal async Task<WindowsAuthorityResponse> ReserveEventAsync(
        long epoch, string eventKey, TelegramNotificationLane lane,
        CancellationToken ct = default)
    {
        var wireLane = lane switch
        {
            TelegramNotificationLane.FiveHourRecovery => "FIVE_HOUR_100",
            TelegramNotificationLane.WeeklyRecovery => "WEEKLY_100",
            _ => null
        };
        if (epoch <= 0 || !ValidEventKey(eventKey) || wireLane is null)
            return WindowsAuthorityResponse.Hold("HOLD_CLIENT_INPUT");
        var reply = await PostAsync("/v1/events/reserve",
            new { epoch, eventKey, lane = wireLane }, ct);
        return reply.Code == "EVENT_RESERVED_NO_SEND" && reply.EventKey != eventKey
            ? WindowsAuthorityResponse.Hold("HOLD_RESPONSE_MISMATCH") : reply;
    }

    internal Task<WindowsAuthorityResponse> MarkUncertainAsync(
        long epoch, string eventKey, CancellationToken ct = default) =>
        epoch > 0 && ValidEventKey(eventKey)
            ? PostAsync("/v1/events/uncertain", new { epoch, eventKey }, ct)
            : Task.FromResult(WindowsAuthorityResponse.Hold("HOLD_CLIENT_INPUT"));

    private static bool ValidTtl(int value) => value is >= 1000 and <= 60000;
    private static bool ValidEventKey(string? value) =>
        value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private Task<WindowsAuthorityResponse> PostAsync(
        string path, object body, CancellationToken ct) =>
        RequestAsync(HttpMethod.Post, path, JsonSerializer.Serialize(body), ct);

    private async Task<WindowsAuthorityResponse> RequestAsync(
        HttpMethod method, string path, string bodyUtf8, CancellationToken ct)
    {
        if (disposed) return WindowsAuthorityResponse.Hold("HOLD_CLIENT_CLOSED");
        try
        {
            var timestampMs = clock.GetUtcNow().ToUnixTimeMilliseconds();
            var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            var signature = Sign(key, deviceId, method.Method,
                path, timestampMs, nonce, bodyUtf8);

            using var message = new HttpRequestMessage(method, new Uri(baseAddress, path));
            message.Version = HttpVersion.Version11;
            message.Headers.TryAddWithoutValidation("x-device-id", deviceId);
            message.Headers.TryAddWithoutValidation("x-timestamp-ms",
                timestampMs.ToString(System.Globalization.CultureInfo.InvariantCulture));
            message.Headers.TryAddWithoutValidation("x-nonce", nonce);
            message.Headers.TryAddWithoutValidation("x-signature", signature);
            if (method == HttpMethod.Post)
            {
                message.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(bodyUtf8));
                message.Content.Headers.ContentType =
                    new MediaTypeHeaderValue("application/json");
            }

            // NO retries. A timed-out reservation may already be committed.
            using var response = await http.SendAsync(message,
                HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode != HttpStatusCode.OK)
                return WindowsAuthorityResponse.Hold("HOLD_HTTP");
            var size = response.Content.Headers.ContentLength;
            if (size is > 4096) return WindowsAuthorityResponse.Hold("HOLD_RESPONSE");
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var memory = new MemoryStream();
            var buffer = new byte[1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                if (memory.Length + read > 4096)
                    return WindowsAuthorityResponse.Hold("HOLD_RESPONSE");
                memory.Write(buffer, 0, read);
            }
            return ParseResponse(memory.ToArray(), timestampMs);
        }
        catch (Exception ex) when (
            ex is HttpRequestException or IOException or
            TaskCanceledException or OperationCanceledException or
            JsonException or InvalidOperationException or ObjectDisposedException)
        {
            return WindowsAuthorityResponse.Hold("HOLD_NETWORK_OR_UNCERTAIN");
        }
    }

    internal static WindowsAuthorityResponse ParseResponse(
        byte[] responseBytes, long requestTimeMs)
    {
        try
        {
            if (responseBytes is not { Length: > 0 and <= 4096 })
                return WindowsAuthorityResponse.Hold("HOLD_RESPONSE");
            var utf8 = new UTF8Encoding(false, true);
            var serialized = utf8.GetString(responseBytes);
            using var parsed = JsonDocument.Parse(serialized,
                new JsonDocumentOptions { MaxDepth = 8 });
            var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return WindowsAuthorityResponse.Hold("HOLD_RESPONSE");
            var fields = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in root.EnumerateObject())
                if (!fields.Add(field.Name))
                    return WindowsAuthorityResponse.Hold("HOLD_RESPONSE");
            if (!root.TryGetProperty("schemaVersion", out var schema) ||
                schema.ValueKind != JsonValueKind.Number ||
                !schema.TryGetInt32(out var version) || version != 1 ||
                !root.TryGetProperty("code", out var status) ||
                status.ValueKind != JsonValueKind.String)
                return WindowsAuthorityResponse.Hold("HOLD_RESPONSE");
            var code = status.GetString()!;
            if (!AllowedCodes.Contains(code, StringComparer.Ordinal))
                return WindowsAuthorityResponse.Hold("HOLD_RESPONSE");
            foreach (var marker in new[] {
                "actualSendAuthorized", "automaticRetryAllowed", "remoteSenderActivated"
            })
                if (!root.TryGetProperty(marker, out var flag) ||
                    flag.ValueKind != JsonValueKind.False)
                    return WindowsAuthorityResponse.Hold("HOLD_UNEXPECTED_AUTHORIZATION");

            long? epoch = null;
            long? expiresAtMs = null;
            string? eventKey = null;
            long? authorityTimeMs = null;
            WindowsAuthorityLease? currentLease = null;
            WindowsAuthorityEvent? currentEvent = null;
            if (code is "LEASE_ACQUIRED" or "LEASE_RENEWED")
            {
                if (!root.TryGetProperty("epoch", out var value) ||
                    !value.TryGetInt64(out var parsedEpoch) || parsedEpoch <= 0 ||
                    !root.TryGetProperty("expiresAtMs", out var expiry) ||
                    !expiry.TryGetInt64(out var parsedExpiry) ||
                    parsedExpiry <= requestTimeMs ||
                    parsedExpiry > requestTimeMs + 90000)
                    return WindowsAuthorityResponse.Hold("HOLD_RESPONSE");
                epoch = parsedEpoch;
                expiresAtMs = parsedExpiry;
            }
            if (code == "LEASE_RELEASED")
            {
                if (!root.TryGetProperty("epoch", out var released) ||
                    !released.TryGetInt64(out var releasedEpoch) || releasedEpoch <= 0)
                    return WindowsAuthorityResponse.Hold("HOLD_RESPONSE");
                epoch = releasedEpoch;
            }
            if (code == "EVENT_RESERVED_NO_SEND")
            {
                if (!root.TryGetProperty("eventKey", out var item) ||
                    item.ValueKind != JsonValueKind.String ||
                    !ValidEventKey(item.GetString()))
                    return WindowsAuthorityResponse.Hold("HOLD_RESPONSE");
                eventKey = item.GetString();
            }
            if (code == "STATUS")
            {
                if (!root.TryGetProperty("authorityTimeMs", out var time) ||
                    !time.TryGetInt64(out var remoteTime) ||
                    Math.Abs(remoteTime - requestTimeMs) > 30000 ||
                    !root.TryGetProperty("lease", out var lease) ||
                    lease.ValueKind != JsonValueKind.Object ||
                    !lease.TryGetProperty("owner", out var owner) ||
                    !lease.TryGetProperty("epoch", out var currentEpoch) ||
                    !currentEpoch.TryGetInt64(out var parsedEpoch) || parsedEpoch < 0 ||
                    !lease.TryGetProperty("expiresAtMs", out var expiry) ||
                    !expiry.TryGetInt64(out var parsedExpiry) || parsedExpiry < 0 ||
                    (owner.ValueKind != JsonValueKind.Null &&
                        (owner.ValueKind != JsonValueKind.String ||
                         !ValidDeviceId(owner.GetString()))))
                    return WindowsAuthorityResponse.Hold("HOLD_CLOCK_OR_SCHEMA");
                var ownerDevice = owner.ValueKind == JsonValueKind.Null
                    ? null : owner.GetString();
                if ((ownerDevice is null && parsedExpiry != 0) ||
                    (ownerDevice is not null &&
                        (parsedEpoch == 0 || parsedExpiry <= 0)))
                    return WindowsAuthorityResponse.Hold("HOLD_CLOCK_OR_SCHEMA");
                currentLease = new WindowsAuthorityLease(
                    ownerDevice, parsedEpoch, parsedExpiry);
                authorityTimeMs = remoteTime;
            }
            if (code == "EVENT_FOUND")
            {
                if (!root.TryGetProperty("event", out var entry) ||
                    entry.ValueKind != JsonValueKind.Object ||
                    !entry.TryGetProperty("owner", out var owner) ||
                    owner.ValueKind != JsonValueKind.String ||
                    !ValidDeviceId(owner.GetString()) ||
                    !entry.TryGetProperty("epoch", out var eventEpoch) ||
                    !eventEpoch.TryGetInt64(out var parsedEpoch) || parsedEpoch <= 0 ||
                    !entry.TryGetProperty("lane", out var lane) ||
                    lane.ValueKind != JsonValueKind.String ||
                    lane.GetString() is not ("FIVE_HOUR_100" or "WEEKLY_100") ||
                    !entry.TryGetProperty("state", out var state) ||
                    state.ValueKind != JsonValueKind.String ||
                    state.GetString() is not
                        ("RESERVED_BEFORE_SEND" or "SEND_OUTCOME_UNCERTAIN"))
                    return WindowsAuthorityResponse.Hold("HOLD_RESPONSE");
                currentEvent = new WindowsAuthorityEvent(lane.GetString()!,
                    state.GetString()!, owner.GetString()!, parsedEpoch);
            }
            if (code == "EVENT_ABSENT" &&
                (!root.TryGetProperty("event", out var absent) ||
                 absent.ValueKind != JsonValueKind.Null))
                return WindowsAuthorityResponse.Hold("HOLD_RESPONSE");
            return new WindowsAuthorityResponse(code, epoch, expiresAtMs,
                eventKey, currentLease, currentEvent, authorityTimeMs);
        }
        catch (Exception ex) when (ex is JsonException or
            InvalidOperationException or ArgumentException or DecoderFallbackException)
        {
            return WindowsAuthorityResponse.Hold("HOLD_RESPONSE");
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        CryptographicOperations.ZeroMemory(key);
    }
}

/// <summary>
/// An authenticated remote observation is NOT a Telegram send permission.
/// The product send gate is deliberately NOT connected to this client.
/// </summary>
internal sealed record WindowsAuthorityLease(
    string? OwnerDevice, long Epoch, long ExpiresAtMs);

internal sealed record WindowsAuthorityEvent(
    string Lane, string State, string OwnerDevice, long Epoch);

internal sealed record WindowsAuthorityResponse(
    string Code, long? Epoch = null,
    long? ExpiresAtMs = null, string? EventKey = null,
    WindowsAuthorityLease? Lease = null,
    WindowsAuthorityEvent? Event = null,
    long? AuthorityTimeMs = null)
{
    internal bool ActualTelegramSendAuthorized => false;
    internal bool AllowsAutomaticRetries => false;
    internal static WindowsAuthorityResponse Hold(string reason) => new(reason);
}

/// <summary>
/// Production network construction NEVER disables certificate verification.
/// Requires a valid CA chain plus an operator-provisioned server certificate
/// SHA256 pin; tests use injected fake HttpMessageHandler without networking.
/// </summary>
internal static class WindowsSenderAuthorityHttpsTransport
{
    internal static HttpClient Create(Uri authorityUri, string certSha256)
    {
        if (!WindowsSenderAuthorityClient.ValidAuthorityUri(authorityUri) ||
            certSha256 is not { Length: 64 } ||
            !certSha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new ArgumentException("Explicit HTTPS endpoint and lowercase server pin required.");

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(4),
            PooledConnectionLifetime = TimeSpan.FromSeconds(30)
        };
        handler.SslOptions.EnabledSslProtocols = SslProtocols.Tls13;
        handler.SslOptions.CertificateRevocationCheckMode = X509RevocationMode.Online;
        handler.SslOptions.RemoteCertificateValidationCallback =
            (_, certificate, _, errors) =>
            {
                if (errors != System.Net.Security.SslPolicyErrors.None ||
                    certificate is null)
                    return false;
                using var cert = new X509Certificate2(certificate);
                return CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(cert.RawData),
                    Convert.FromHexString(certSha256));
            };
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
    }
}
