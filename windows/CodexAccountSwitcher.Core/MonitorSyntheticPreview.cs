using System.Text;
using System.Text.Json;

namespace CodexAccountSwitcher.Core;

/// <summary>
/// Display-only parser for the existing monitor's synthetic JSON shape.
/// This deliberately has no CLI, network, file, login, switching or send capability.
/// It is NOT an authority to emit an early-recovery or reset-credit alert.
/// </summary>
public static class MonitorSyntheticPreview
{
    public const int MaxJsonBytes = 8192;

    public sealed record Snapshot(
        double? FiveHourRemaining,
        double? WeeklyRemaining,
        int? CreditsRemaining,
        bool? OrdinaryUsageAllowed,
        bool HasVerifiedPlan,
        DateTimeOffset? FiveHourResetsAt = null,
        DateTimeOffset? WeeklyResetsAt = null,
        IReadOnlyList<DateTimeOffset?>? CreditExpiresAt = null)
    {
        public string Display =>
            $"Codex | 5H {Percent(FiveHourRemaining)} | 7D {Percent(WeeklyRemaining)} | Credits {(CreditsRemaining?.ToString() ?? "UNKNOWN")} | MOCK";

        private static string Percent(double? value) => value is null
            ? "UNKNOWN" : $"{value.Value:0.##}%";
    }

    public static bool TryRead(string? json, out Snapshot? snapshot)
    {
        snapshot = null;
        if (json is null || Encoding.UTF8.GetByteCount(json) > MaxJsonBytes) return false;

        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 10 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("accountId", out var account) ||
                account.ValueKind != JsonValueKind.String)
                return false;

            var accountId = account.GetString();
            if (string.IsNullOrWhiteSpace(accountId) || Encoding.UTF8.GetByteCount(accountId) > 256 ||
                accountId.Any(char.IsControl)) return false;

            bool? permission = null;
            if (root.TryGetProperty("ordinaryUsageAllowed", out var allowed))
            {
                if (allowed.ValueKind == JsonValueKind.True) permission = true;
                else if (allowed.ValueKind == JsonValueKind.False) permission = false;
                else if (allowed.ValueKind != JsonValueKind.Null) return false;
            }

            if (!root.TryGetProperty("rateLimitsByLimitId", out var buckets) ||
                buckets.ValueKind != JsonValueKind.Object ||
                !buckets.TryGetProperty("codex", out var codex) ||
                codex.ValueKind != JsonValueKind.Object)
                return false;

            bool hasPlan = false;
            if (codex.TryGetProperty("planType", out var plan))
            {
                if (plan.ValueKind == JsonValueKind.String)
                {
                    var value = plan.GetString();
                    if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(char.IsControl))
                        return false;
                    hasPlan = true;
                }
                else if (plan.ValueKind != JsonValueKind.Null) return false;
            }

            if (!ReadWindow(codex, "primary", 300, out var five, out var fiveReset) ||
                !ReadWindow(codex, "secondary", 10080, out var weekly, out var weeklyReset))
                return false;

            int? credits = null;
            IReadOnlyList<DateTimeOffset?>? expiries = null;
            if (root.TryGetProperty("rateLimitResetCredits", out var summary))
            {
                if (summary.ValueKind != JsonValueKind.Object) return false;
                if (summary.TryGetProperty("availableCount", out var count))
                {
                    if (count.ValueKind == JsonValueKind.Number)
                    {
                        if (!count.TryGetInt32(out var number) || number < 0) return false;
                        credits = number;
                    }
                    else if (count.ValueKind != JsonValueKind.Null) return false;
                }
                if (summary.TryGetProperty("credits", out var details) &&
                    details.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<DateTimeOffset?>();
                    foreach (var item in details.EnumerateArray())
                    {
                        if (list.Count >= 32) break;
                        if (item.ValueKind != JsonValueKind.Object ||
                            !item.TryGetProperty("status", out var status) ||
                            status.ValueKind != JsonValueKind.String ||
                            status.GetString() != "available") continue;
                        list.Add(TryDate(item, "expiresAt"));
                    }
                    expiries = list;
                }
            }

            snapshot = new Snapshot(five, weekly, credits, permission, hasPlan,
                fiveReset, weeklyReset, expiries);
            return true;
        }
        catch (JsonException) { return false; }
        catch (ArgumentException) { return false; }
    }

    private static bool ReadWindow(JsonElement parent, string name, int duration,
        out double? remaining, out DateTimeOffset? resetsAt)
    {
        remaining = null;
        resetsAt = null;
        if (!parent.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
            return true;
        if (element.ValueKind != JsonValueKind.Object) return false;
        if (!element.TryGetProperty("windowDurationMins", out var minutes) ||
            minutes.ValueKind != JsonValueKind.Number || !minutes.TryGetInt32(out var parsed) ||
            parsed != duration)
            return false;
        resetsAt = TryDate(element, "resetsAt");
        if (!element.TryGetProperty("usedPercent", out var used))
            return true; // Missing means UNKNOWN.
        if (used.ValueKind == JsonValueKind.Null) return true;
        if (used.ValueKind != JsonValueKind.Number || !used.TryGetDouble(out var percent) ||
            !double.IsFinite(percent) || percent < 0 || percent > 100)
            return false;
        remaining = 100 - percent;
        return true;
    }

    private static DateTimeOffset? TryDate(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var raw) ||
            raw.ValueKind != JsonValueKind.Number ||
            !raw.TryGetInt64(out var seconds) ||
            seconds <= 0 || seconds > 253402300799L)
            return null;
        return DateTimeOffset.FromUnixTimeSeconds(seconds);
    }
}
