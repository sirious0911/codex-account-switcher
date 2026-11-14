using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexAccountSwitcher.Core;

/// <summary>
/// Opt-in P3 V2 GET-only viewer. It never publishes frames, mutates a
/// Codex account, persists enrollment, or grants Telegram permissions.
/// All replay/account/session state exists only for this viewer instance.
/// </summary>
public sealed class QuotaRelayV2HttpsViewer : IDisposable
{
    private const int MaxBytes = 4096;
    private const long MinTime = 1_577_836_800_000;
    private const long MaxTime = 4_133_980_800_000;
    private readonly HttpClient http;
    private readonly bool ownsHttp;
    private readonly Uri endpoint;
    private readonly string sourceId;
    private readonly string viewerToken;
    private string? lastAccountHash;
    private string? lastSession;
    private string? lastFingerprint;
    private long lastTimestamp;
    private long lastSequence;
    private bool quarantined;
    private QuotaRelayV2ReadOnlySnapshot? lastSnapshot;

    public QuotaRelayV2HttpsViewer(Uri origin, string sourceDeviceId,
        string readOnlyCredential, HttpClient? httpClient = null)
    {
        if (origin is null || !origin.IsAbsoluteUri ||
            origin.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(origin.UserInfo) ||
            !string.IsNullOrEmpty(origin.Query) ||
            !string.IsNullOrEmpty(origin.Fragment) ||
            origin.AbsolutePath != "/" ||
            string.IsNullOrWhiteSpace(origin.DnsSafeHost))
            throw new ArgumentException("Only a bare HTTPS origin is supported.", nameof(origin));
        if (!ValidId(sourceDeviceId))
            throw new ArgumentException("Invalid source device.", nameof(sourceDeviceId));
        if (readOnlyCredential is null ||
            readOnlyCredential.Length is < 32 or > 256 ||
            !readOnlyCredential.All(c => c is >= 'a' and <= 'z' or
                >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_'))
            throw new ArgumentException("Invalid V2 viewer enrollment.", nameof(readOnlyCredential));
        endpoint = new Uri(origin, "v2/quota/" + sourceDeviceId);
        sourceId = sourceDeviceId;
        viewerToken = readOnlyCredential;
        ownsHttp = httpClient is null;
        http = httpClient ?? new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false, UseCookies = false
        }) { Timeout = TimeSpan.FromSeconds(8) };
    }

    public async Task<QuotaRelayV2ReadOnlyResult> FetchAsync(
        DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        static QuotaRelayV2ReadOnlyResult Held(string reason) => new(reason, null);
        if (quarantined) return Held("HOLD_ACCOUNT_SWITCH_REQUIRES_LOCAL_RESET");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", viewerToken);
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await http.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK)
                return Held("HOLD_REMOTE_HTTP_STATUS");
            if (response.Content.Headers.ContentLength is long size && size > MaxBytes)
                return Held("HOLD_REMOTE_OVERSIZED");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var memory = new MemoryStream();
            var block = new byte[1024];
            while (true)
            {
                var n = await stream.ReadAsync(block, cancellationToken);
                if (n == 0) break;
                if (memory.Length + n > MaxBytes) return Held("HOLD_REMOTE_OVERSIZED");
                memory.Write(block, 0, n);
            }
            // The UTF8 decoder and JsonDocument must not tolerate duplicate
            // property keys, even inside unrelated nested objects.
            var raw = new UTF8Encoding(false, true).GetString(memory.ToArray());
            using var document = JsonDocument.Parse(raw,
                new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (!UniqueProperties(root, 0) ||
                !Exact(root, ["schemaVersion", "status", "frame",
                    "actualSendAuthorized", "automaticRetryAllowed",
                    "productionReservationAuthorized", "senderTransitionAuthorized"]) ||
                root.GetProperty("schemaVersion").GetInt32() != 2 ||
                root.GetProperty("status").GetString()
                    is not ("FRESH_READ_ONLY" or "AGED_READ_ONLY") ||
                new[] { "actualSendAuthorized", "automaticRetryAllowed",
                    "productionReservationAuthorized", "senderTransitionAuthorized" }
                    .Any(key => root.GetProperty(key).ValueKind != JsonValueKind.False))
                return Held("HOLD_INVALID_V2_RESPONSE");

            var frame = root.GetProperty("frame");
            if (!Exact(frame, ["sourceDeviceId", "sourceSessionId", "sequence",
                    "collectedAtUtcMs", "accountIdSha256", "fiveHour",
                    "sevenDay", "resetCredits"]) ||
                frame.GetProperty("sourceDeviceId").GetString() != sourceId)
                return Held("HOLD_INVALID_V2_FRAME");
            var account = frame.GetProperty("accountIdSha256").GetString();
            var session = frame.GetProperty("sourceSessionId").GetString();
            if (!ValidLowerHex(account, 64) || !ValidLowerHex(session, 32) ||
                !frame.GetProperty("sequence").TryGetInt64(out var sequence) ||
                sequence is < 1 or > 1_000_000_000 ||
                !frame.GetProperty("collectedAtUtcMs").TryGetInt64(out var timestamp) ||
                !InBounds(timestamp))
                return Held("HOLD_INVALID_V2_FRAME");
            var age = now.ToUnixTimeMilliseconds() - timestamp;
            if (age < -3_000 || age >= 360_000)
                return Held("HOLD_STALE_OR_FUTURE_V2_FRAME");

            if (lastAccountHash is not null && lastAccountHash != account)
            {
                quarantined = true;
                lastSnapshot = null;
                return Held("HOLD_ACCOUNT_SWITCH_REQUIRES_LOCAL_RESET");
            }

            var five = ReadWindow(frame.GetProperty("fiveHour"), timestamp, 21_600_000);
            var seven = ReadWindow(frame.GetProperty("sevenDay"), timestamp, 691_200_000);
            var credits = ReadCredits(frame.GetProperty("resetCredits"));
            if (five is null || seven is null || credits is null ||
                (five.Value.Percent is null && seven.Value.Percent is null &&
                 credits.Value.Count is null))
                return Held("HOLD_INVALID_V2_OBSERVATION");

            var fingerprint = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(frame.GetRawText())));
            if (lastTimestamp != 0 && lastTimestamp == timestamp &&
                lastSequence == sequence && lastSession == session &&
                lastFingerprint == fingerprint && lastSnapshot is not null)
                return new(age < 120_000 ? "FRESH_READ_ONLY" : "AGED_READ_ONLY",
                    lastSnapshot);
            if (lastTimestamp != 0 && (timestamp <= lastTimestamp ||
                (session == lastSession ? sequence <= lastSequence : sequence != 1)))
                return Held("HOLD_REPLAY_OR_REORDERED_VIEW");

            var snapshot = new QuotaRelayV2ReadOnlySnapshot(sourceId,
                five.Value.Percent, seven.Value.Percent,
                five.Value.ResetAt, seven.Value.ResetAt,
                credits.Value.Count, credits.Value.ExpiryDates,
                DateTimeOffset.FromUnixTimeMilliseconds(timestamp));
            lastTimestamp = timestamp;
            lastSequence = sequence;
            lastSession = session;
            lastAccountHash = account;
            lastFingerprint = fingerprint;
            lastSnapshot = snapshot;
            return new(age < 120_000 ? "FRESH_READ_ONLY" : "AGED_READ_ONLY", snapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
            or IOException or JsonException or InvalidOperationException
            or ArgumentException or FormatException or OverflowException
            or DecoderFallbackException)
        {
            return Held("HOLD_REMOTE_READ_FAILURE");
        }
    }

    private static (int? Percent, DateTimeOffset? ResetAt)? ReadWindow(
        JsonElement element, long observed, long ahead)
    {
        if (!Exact(element, ["remainingPercent", "resetAtUtcMs"])) return null;
        var pct = element.GetProperty("remainingPercent");
        int? number = null;
        if (pct.ValueKind != JsonValueKind.Null)
        {
            if (!pct.TryGetInt32(out var value) || value is < 0 or > 100)
                return null;
            number = value;
        }
        var resetValue = element.GetProperty("resetAtUtcMs");
        DateTimeOffset? reset = null;
        if (resetValue.ValueKind != JsonValueKind.Null)
        {
            if (!resetValue.TryGetInt64(out var ms) || !InBounds(ms) ||
                ms < observed - 600_000 || ms > observed + ahead)
                return null;
            reset = DateTimeOffset.FromUnixTimeMilliseconds(ms);
        }
        return (number, reset);
    }

    private static (int? Count, IReadOnlyList<DateTimeOffset?>? ExpiryDates)?
        ReadCredits(JsonElement element)
    {
        if (!Exact(element, ["availableCount", "expiryDetailsUtcMs"]))
            return null;
        var countElement = element.GetProperty("availableCount");
        int? count = null;
        if (countElement.ValueKind != JsonValueKind.Null)
        {
            if (!countElement.TryGetInt32(out var n) || n < 0) return null;
            count = n;
        }
        var details = element.GetProperty("expiryDetailsUtcMs");
        if (details.ValueKind == JsonValueKind.Null) return (count, null);
        if (details.ValueKind != JsonValueKind.Array || count is null ||
            details.GetArrayLength() > 2 ||
            details.GetArrayLength() > count.Value)
            return null;
        var expiries = new List<DateTimeOffset?>();
        foreach (var value in details.EnumerateArray())
        {
            if (value.ValueKind == JsonValueKind.Null)
            {
                expiries.Add(null);
                continue;
            }
            if (!value.TryGetInt64(out var stamp) || !InBounds(stamp))
                return null;
            expiries.Add(DateTimeOffset.FromUnixTimeMilliseconds(stamp));
        }
        // Index is a display slot, not a persistent provider credit ID.
        expiries.Sort((a, b) =>
            a is null ? (b is null ? 0 : 1) :
            b is null ? -1 : a.Value.CompareTo(b.Value));
        return (count, expiries.AsReadOnly());
    }

    private static bool UniqueProperties(JsonElement element, int depth)
    {
        if (depth > 8) return false;
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
                if (!names.Add(property.Name) ||
                    !UniqueProperties(property.Value, depth + 1)) return false;
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in element.EnumerateArray())
                if (!UniqueProperties(value, depth + 1)) return false;
        }
        return true;
    }

    private static bool Exact(JsonElement element, string[] keys)
    {
        if (element.ValueKind != JsonValueKind.Object) return false;
        var names = element.EnumerateObject().Select(x => x.Name).ToArray();
        return names.Length == keys.Length &&
            names.Distinct(StringComparer.Ordinal).Count() == names.Length &&
            names.All(x => keys.Contains(x, StringComparer.Ordinal));
    }
    private static bool InBounds(long value) => value is >= MinTime and <= MaxTime;
    private static bool ValidId(string value) =>
        value.Length is >= 1 and <= 48 &&
        value.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z'
            or >= '0' and <= '9' or '-');
    private static bool ValidLowerHex(string? value, int size) =>
        value is not null && value.Length == size &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public void Dispose()
    {
        if (ownsHttp) http.Dispose();
    }
}

public sealed record QuotaRelayV2ReadOnlySnapshot(
    string SourceDeviceId, int? FiveHourRemainingPercent,
    int? SevenDayRemainingPercent, DateTimeOffset? FiveHourResetAt,
    DateTimeOffset? SevenDayResetAt, int? ResetCreditsRemaining,
    IReadOnlyList<DateTimeOffset?>? ResetCreditExpiries,
    DateTimeOffset ObservedAt);

public sealed record QuotaRelayV2ReadOnlyResult(
    string Status, QuotaRelayV2ReadOnlySnapshot? Snapshot)
{
    public bool ActualSendAuthorized => false;
    public bool SenderTransitionAuthorized => false;
    public bool TelegramAllowed => false;
    public bool AccountVerified => false;
}
