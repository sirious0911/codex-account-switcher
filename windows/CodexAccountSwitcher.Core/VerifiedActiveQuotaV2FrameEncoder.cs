using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexAccountSwitcher.Core;

/// <summary>
/// P3 V2 data-only producer from a verified active Windows SwiftHost
/// observation. No network, credentials, disk persistence or Telegram.
/// This source is intentionally NOT attached to the operating app.
/// </summary>
public sealed class VerifiedActiveQuotaV2FrameEncoder
{
    private const long MinimumTimestamp = 1_577_836_800_000;
    private const long MaximumTimestamp = 4_133_980_800_000;
    private readonly object sync = new();
    private readonly ActiveMonitorPublicationGate gate = new();
    private readonly string sourceDeviceId;
    private string sessionId = Guid.NewGuid().ToString("N");
    private string? accountHash;
    private long sequence;
    private long epochFloor;
    private long lastObserved;
    private bool confirmed;
    private bool quarantined;

    public VerifiedActiveQuotaV2FrameEncoder(string sourceDeviceId)
    {
        if (string.IsNullOrEmpty(sourceDeviceId) || sourceDeviceId.Length > 48 ||
            !sourceDeviceId.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z'
                or >= '0' and <= '9' or '-'))
            throw new ArgumentException("Invalid enrolled source device ID.",
                nameof(sourceDeviceId));
        this.sourceDeviceId = sourceDeviceId;
    }

    public bool IsQuarantined
    {
        get { lock (sync) return quarantined; }
    }

    public void ObserveSnapshot(AccountSnapshot snapshot, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (sync)
        {
            var before = gate.ActiveProfile;
            gate.Observe(snapshot);
            if (before != gate.ActiveProfile)
                ResetEpoch(now.ToUnixTimeMilliseconds());
            confirmed = !quarantined &&
                gate.ActiveProfile is Guid id &&
                snapshot.ActiveAccountID == id &&
                snapshot.ActiveIdentityConfirmed &&
                !snapshot.IsMutating && !snapshot.IsAddingAccount &&
                string.IsNullOrEmpty(snapshot.Error) &&
                snapshot.Accounts.Count(a => a.Profile.Id == id) == 1;
        }
    }

    public void SwitchRequested(DateTimeOffset now)
    {
        lock (sync)
        {
            gate.SwitchRequested();
            ResetEpoch(now.ToUnixTimeMilliseconds());
            confirmed = false;
        }
    }

    /// <summary>
    /// An observation is consumed once only after all V2 validation passes.
    /// A changed provider account ID is quarantined until a verified local
    /// profile epoch transition; never "reset" via a remote frame.
    /// </summary>
    public bool TryBuild(CodexMonitorWireSample sample, DateTimeOffset now,
        out string? frame)
    {
        frame = null;
        ArgumentNullException.ThrowIfNull(sample);
        lock (sync)
        {
            if (!confirmed || quarantined || !gate.Allows(sample) ||
                sequence >= 1_000_000_000)
                return false;
            long observed = sample.ObservedAtMilliseconds;
            long clock = now.ToUnixTimeMilliseconds();
            if (observed <= epochFloor || observed <= lastObserved ||
                observed > clock + 3_000 || clock - observed >= 360_000 ||
                observed < MinimumTimestamp || observed > MaximumTimestamp)
                return false;

            if (!TryRead(sample.RawJson, observed, out var providerId,
                out var five, out var seven, out var resetCount,
                out var expiryDetails))
                return false;
            var hash = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(providerId!))).ToLowerInvariant();
            if (accountHash != null && hash != accountHash)
            {
                quarantined = true;
                confirmed = false;
                return false;
            }

            var value = JsonSerializer.Serialize(new
            {
                schemaVersion = 2,
                sourceDeviceId,
                sourceSessionId = sessionId,
                sequence = sequence + 1,
                collectedAtUtcMs = observed,
                accountIdSha256 = hash,
                fiveHour = new
                {
                    remainingPercent = five!.Value.Percent,
                    resetAtUtcMs = five.Value.ResetAt
                },
                sevenDay = new
                {
                    remainingPercent = seven!.Value.Percent,
                    resetAtUtcMs = seven.Value.ResetAt
                },
                resetCredits = new
                {
                    availableCount = resetCount,
                    expiryDetailsUtcMs = expiryDetails
                },
                actualSendAuthorized = false,
                automaticRetryAllowed = false,
                productionReservationAuthorized = false,
                senderTransitionAuthorized = false
            });
            if (Encoding.UTF8.GetByteCount(value) > 2_048)
                return false;
            accountHash = hash;
            lastObserved = observed;
            sequence++;
            frame = value;
            return true;
        }
    }

    private void ResetEpoch(long now)
    {
        accountHash = null;
        quarantined = false;
        sequence = 0;
        sessionId = Guid.NewGuid().ToString("N");
        epochFloor = Math.Max(epochFloor, now);
        lastObserved = epochFloor;
    }

    private static bool TryRead(string raw, long observed,
        out string? account,
        out (int? Percent, long? ResetAt)? five,
        out (int? Percent, long? ResetAt)? seven,
        out int? availableCount,
        out long?[]? expiries)
    {
        account = null;
        five = null;
        seven = null;
        availableCount = null;
        expiries = null;
        if (raw == null || Encoding.UTF8.GetByteCount(raw) > 8_192)
            return false;
        try
        {
            using var doc = JsonDocument.Parse(raw, new JsonDocumentOptions
            {
                MaxDepth = 10, AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
            var root = doc.RootElement;
            if (!UniqueKeys(root, 0) ||
                !root.TryGetProperty("accountId", out var id) ||
                id.ValueKind != JsonValueKind.String ||
                id.GetString() is not string provider ||
                string.IsNullOrWhiteSpace(provider) ||
                Encoding.UTF8.GetByteCount(provider) > 256 ||
                provider.Any(char.IsControl) ||
                !root.TryGetProperty("rateLimitsByLimitId", out var buckets) ||
                buckets.ValueKind != JsonValueKind.Object ||
                !buckets.TryGetProperty("codex", out var codex) ||
                codex.ValueKind != JsonValueKind.Object ||
                !ReadWindow(codex, "primary", 300, observed, out five) ||
                !ReadWindow(codex, "secondary", 10_080, observed, out seven) ||
                !ReadCredits(root, out availableCount, out expiries))
                return false;

            if (five!.Value.Percent == null &&
                seven!.Value.Percent == null && availableCount == null)
                return false; // no actual 5H / 7D / R observation
            account = provider;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException
            or ArgumentException or OverflowException)
        {
            return false;
        }
    }

    private static bool ReadWindow(JsonElement root, string name,
        int expectedMinutes, long observed,
        out (int? Percent, long? ResetAt)? window)
    {
        window = (null, null);
        if (!root.TryGetProperty(name, out var x) ||
            x.ValueKind == JsonValueKind.Null)
            return true;
        if (x.ValueKind != JsonValueKind.Object ||
            !x.TryGetProperty("windowDurationMins", out var mins) ||
            !mins.TryGetInt32(out var duration) ||
            duration != expectedMinutes ||
            !x.TryGetProperty("usedPercent", out var used))
            return false;
        if (used.ValueKind == JsonValueKind.Null)
            return true;
        if (used.ValueKind != JsonValueKind.Number ||
            !used.TryGetDouble(out var value) ||
            !double.IsFinite(value) || value < 0 || value > 100)
            return false;

        long? reset = null;
        if (x.TryGetProperty("resetsAt", out var timestamp) &&
            timestamp.ValueKind != JsonValueKind.Null)
        {
            if (timestamp.ValueKind != JsonValueKind.Number ||
                !timestamp.TryGetInt64(out long seconds) ||
                seconds < MinimumTimestamp / 1000 ||
                seconds > MaximumTimestamp / 1000)
                return false;
            reset = checked(seconds * 1000);
            long horizon = expectedMinutes == 300 ? 21_600_000 : 691_200_000;
            if (reset < observed - 600_000 || reset > observed + horizon)
                return false;
        }
        window = ((int)Math.Round(100 - value, 0,
            MidpointRounding.AwayFromZero), reset);
        return true;
    }

    private static bool ReadCredits(JsonElement root,
        out int? count, out long?[]? expiryDetails)
    {
        count = null;
        expiryDetails = null;
        if (!root.TryGetProperty("rateLimitResetCredits", out var credits))
            return true;
        if (credits.ValueKind != JsonValueKind.Object)
            return false;
        if (credits.TryGetProperty("availableCount", out var available) &&
            available.ValueKind != JsonValueKind.Null)
        {
            if (available.ValueKind != JsonValueKind.Number ||
                !available.TryGetInt32(out var n) || n < 0)
                return false;
            count = n;
        }
        if (!credits.TryGetProperty("credits", out var details) ||
            details.ValueKind == JsonValueKind.Null ||
            count == null)
        {
            // Unknown totals cannot be paired with known partial dates.
            if (count == null && details.ValueKind == JsonValueKind.Array &&
                details.GetArrayLength() != 0)
                return false;
            return true;
        }
        if (details.ValueKind != JsonValueKind.Array ||
            details.GetArrayLength() > 32)
            return false;
        var values = new List<long?>(details.GetArrayLength());
        foreach (var entry in details.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object ||
                !entry.TryGetProperty("status", out var status) ||
                status.ValueKind != JsonValueKind.String ||
                status.GetString() != "available" ||
                !entry.TryGetProperty("expiresAt", out var expires))
                return false;
            long? when = null;
            if (expires.ValueKind != JsonValueKind.Null)
            {
                if (expires.ValueKind != JsonValueKind.Number ||
                    !expires.TryGetInt64(out long seconds) ||
                    seconds < MinimumTimestamp / 1000 ||
                    seconds > MaximumTimestamp / 1000)
                    return false;
                when = checked(seconds * 1000);
            }
            values.Add(when);
        }
        // Count and available-list are independent provider fields, but
        // source cannot assert more eligible entries than available credits.
        if (values.Count > count.Value)
            return false;
        expiryDetails = values.OrderBy(v => v is null).ThenBy(v => v)
            .Take(2).ToArray();
        return true;
    }

    private static bool UniqueKeys(JsonElement node, int depth)
    {
        if (depth > 10) return false;
        if (node.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in node.EnumerateObject())
                if (!names.Add(p.Name) || !UniqueKeys(p.Value, depth + 1))
                    return false;
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in node.EnumerateArray())
                if (!UniqueKeys(value, depth + 1))
                    return false;
        }
        return true;
    }
}
