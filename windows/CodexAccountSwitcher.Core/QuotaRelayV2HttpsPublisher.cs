using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CodexAccountSwitcher.Core;

/// <summary>
/// Explicit, single-flight P3 V2 telemetry PUT transport. This type does
/// not subscribe to account events, start itself, persist credentials,
/// change accounts, access Telegram, or retry failed deliveries.
/// No operating application constructs it at startup.
/// </summary>
public sealed class QuotaRelayV2HttpsPublisher : IDisposable
{
    private readonly HttpClient http;
    private readonly Uri target;
    private readonly string sourceDeviceId;
    private readonly Func<CancellationToken, ValueTask<string?>> credentialProvider;
    private readonly CancellationTokenSource lifetime = new();
    private int active;
    private int disposed;

    public QuotaRelayV2HttpsPublisher(
        Uri origin,
        string enrolledSourceDeviceId,
        Func<CancellationToken, ValueTask<string?>> credentialProvider,
        HttpMessageHandler? syntheticTransport = null)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(credentialProvider);
        if (!origin.IsAbsoluteUri || origin.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(origin.UserInfo) ||
            !string.IsNullOrEmpty(origin.Query) ||
            !string.IsNullOrEmpty(origin.Fragment) ||
            origin.AbsolutePath != "/" ||
            string.IsNullOrWhiteSpace(origin.DnsSafeHost))
            throw new ArgumentException("Expected a bare HTTPS origin.", nameof(origin));
        if (!ValidDeviceId(enrolledSourceDeviceId))
            throw new ArgumentException("Invalid enrolled source.", nameof(enrolledSourceDeviceId));

        this.sourceDeviceId = enrolledSourceDeviceId;
        this.credentialProvider = credentialProvider;
        target = new Uri(origin, "v2/quota/" + enrolledSourceDeviceId);
        // The default path verifies the OS certificate trust and hostname.
        // Redirects are disabled so a credential is never forwarded.
        http = new HttpClient(syntheticTransport ?? new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false
        }, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
    }

    /// <summary>
    /// Sends one already verified V2 frame, returns true only on HTTP 202.
    /// The caller must fence the account epoch and cancel on switching.
    /// Duplicate notifications and delivery failures are never retried.
    /// This method never grants a response-based sender authorization.
    /// </summary>
    public async Task<bool> PublishOnceAsync(
        string verifiedV2Frame,
        CancellationToken epochCancellation = default)
    {
        if (Volatile.Read(ref disposed) != 0 ||
            Interlocked.CompareExchange(ref active, 1, 0) != 0)
            return false;

        try
        {
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(
                epochCancellation, lifetime.Token);
            if (cancel.IsCancellationRequested ||
                !ValidPayload(verifiedV2Frame, sourceDeviceId, out var payload))
                return false;

            var credential = await credentialProvider(cancel.Token).ConfigureAwait(false);
            if (!ValidCredential(credential) || cancel.IsCancellationRequested)
                return false;

            using var request = new HttpRequestMessage(HttpMethod.Put, target);
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", credential);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = new ByteArrayContent(payload!);
            request.Content.Headers.ContentType =
                new MediaTypeHeaderValue("application/json");

            using var response = await http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead,
                cancel.Token).ConfigureAwait(false);
            return response.StatusCode == HttpStatusCode.Accepted &&
                !cancel.IsCancellationRequested &&
                Volatile.Read(ref disposed) == 0;
        }
        catch (OperationCanceledException) { return false; }
        catch (HttpRequestException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (Exception)
        {
            // No credential, payload, profile, URL or response body in logs.
            return false;
        }
        finally
        {
            Interlocked.Exchange(ref active, 0);
        }
    }

    private static bool ValidPayload(
        string? frame, string expectedSource, out byte[]? payload)
    {
        payload = null;
        if (string.IsNullOrWhiteSpace(frame))
            return false;
        byte[] bytes;
        try { bytes = new UTF8Encoding(false, true).GetBytes(frame); }
        catch (EncoderFallbackException) { return false; }
        if (bytes.Length is < 1 or > 2048)
            return false;
        try
        {
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                MaxDepth = 12, AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
            var root = json.RootElement;
            if (!UniqueKeys(root, 0) ||
                !ExactKeys(root, ["schemaVersion", "sourceDeviceId",
                    "sourceSessionId", "sequence", "collectedAtUtcMs",
                    "accountIdSha256", "fiveHour", "sevenDay",
                    "resetCredits", "actualSendAuthorized",
                    "automaticRetryAllowed", "productionReservationAuthorized",
                    "senderTransitionAuthorized"]) ||
                root.GetProperty("schemaVersion").GetInt32() != 2 ||
                root.GetProperty("sourceDeviceId").GetString() != expectedSource ||
                !ValidHex(root.GetProperty("sourceSessionId").GetString(), 32) ||
                !ValidHex(root.GetProperty("accountIdSha256").GetString(), 64) ||
                !root.GetProperty("sequence").TryGetInt64(out var seq) || seq < 1 ||
                !root.GetProperty("collectedAtUtcMs").TryGetInt64(out var observed) ||
                observed is < 1577836800000 or > 4133980800000 ||
                root.GetProperty("actualSendAuthorized").ValueKind != JsonValueKind.False ||
                root.GetProperty("automaticRetryAllowed").ValueKind != JsonValueKind.False ||
                root.GetProperty("productionReservationAuthorized").ValueKind != JsonValueKind.False ||
                root.GetProperty("senderTransitionAuthorized").ValueKind != JsonValueKind.False ||
                root.GetProperty("fiveHour").ValueKind != JsonValueKind.Object ||
                root.GetProperty("sevenDay").ValueKind != JsonValueKind.Object ||
                root.GetProperty("resetCredits").ValueKind != JsonValueKind.Object)
                return false;
            payload = bytes;
            return true;
        }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (FormatException) { return false; }
    }

    private static bool UniqueKeys(JsonElement element, int depth)
    {
        if (depth > 12) return false;
        if (element.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var prop in element.EnumerateObject())
                if (!keys.Add(prop.Name) || !UniqueKeys(prop.Value, depth + 1))
                    return false;
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                if (!UniqueKeys(item, depth + 1))
                    return false;
        }
        return true;
    }

    private static bool ExactKeys(JsonElement element, string[] required)
    {
        if (element.ValueKind != JsonValueKind.Object) return false;
        var names = element.EnumerateObject().Select(x => x.Name).ToArray();
        return names.Length == required.Length &&
            names.All(x => required.Contains(x, StringComparer.Ordinal));
    }

    private static bool ValidHex(string? text, int size) =>
        text is not null && text.Length == size &&
        text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool ValidDeviceId(string? text) =>
        text is not null && text.Length is >= 1 and <= 48 &&
        text.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z'
            or >= '0' and <= '9' or '-');

    private static bool ValidCredential(string? text) =>
        text is not null && text.Length is >= 32 and <= 256 &&
        text.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z'
            or >= '0' and <= '9' or '-' or '_');

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        http.Dispose();
        lifetime.Dispose();
    }
}
