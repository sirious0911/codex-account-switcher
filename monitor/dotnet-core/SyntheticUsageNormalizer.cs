using System.Text.Json;

namespace CodexUsageMonitor.Core;

// Cross-platform .NET domain; this file does not contact any process, network, or account.
public sealed record UsageWindow(
    double? UsedPercent,
    double? RemainingPercent,
    long? ResetsAtUnix,
    int? DurationMinutes);

public sealed record CreditExpiry(long? ExpiresAtUnix);

public sealed record UsageObservation(
    string? AccountId,
    string? PlanType,
    bool? OrdinaryUsageAllowed,
    DateTimeOffset ObservedAt,
    UsageWindow FiveHour,
    UsageWindow Weekly,
    long? ResetCreditsAvailable,
    IReadOnlyList<CreditExpiry>? CreditDetails);

public static class SyntheticUsageNormalizer
{
    public static UsageObservation Parse(string json, DateTimeOffset observedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using var document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new FormatException("Expected synthetic object.");

        if (Property(root, "result") is { ValueKind: JsonValueKind.Object } result)
            root = result;

        JsonElement? snapshot = null;
        if (Property(root, "rateLimitsByLimitId") is { ValueKind: JsonValueKind.Object } byId)
            snapshot = Property(byId, "codex");
        if (snapshot is not { ValueKind: JsonValueKind.Object })
            snapshot = Property(root, "rateLimits");

        var primary = Window(Property(snapshot, "primary"), 300);
        var secondary = Window(Property(snapshot, "secondary"), 10080);
        var summary = Property(root, "rateLimitResetCredits");
        var count = Integer(Property(summary, "availableCount"));
        if (count < 0) count = null;

        IReadOnlyList<CreditExpiry>? details = null;
        if (Property(summary, "credits") is { ValueKind: JsonValueKind.Array } credits)
        {
            var list = new List<CreditExpiry>();
            foreach (var c in credits.EnumerateArray())
            {
                if (Text(Property(c, "status")) == "available")
                    list.Add(new CreditExpiry(Integer(Property(c, "expiresAt"))));
            }
            details = list;
        }

        var permission = Property(root, "ordinaryUsageAllowed");
        bool? allowed = permission?.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
        return new UsageObservation(
            Text(Property(root, "accountId")),
            Text(Property(snapshot, "planType")),
            allowed,
            observedAt,
            primary,
            secondary,
            count,
            details);
    }

    private static UsageWindow Window(JsonElement? element, int expectedMinutes)
    {
        long? duration = Integer(Property(element, "windowDurationMins"));
        if (duration != expectedMinutes)
            return new UsageWindow(null, null, null, null);

        double? used = Number(Property(element, "usedPercent"));
        if (used is < 0 or > 100 || (used.HasValue && !double.IsFinite(used.Value)))
            used = null;

        return new UsageWindow(
            used,
            used.HasValue ? 100 - used.Value : null,
            Integer(Property(element, "resetsAt")) is > 0 ? Integer(Property(element, "resetsAt")) : null,
            expectedMinutes);
    }

    private static JsonElement? Property(JsonElement? parent, string key)
    {
        if (parent is { ValueKind: JsonValueKind.Object } obj &&
            obj.TryGetProperty(key, out var result))
            return result;
        return null;
    }

    private static string? Text(JsonElement? value)
    {
        if (value is not { ValueKind: JsonValueKind.String }) return null;
        var text = value.Value.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static long? Integer(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.Number } number && number.TryGetInt64(out long n)
            ? n : null;

    private static double? Number(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.Number } number && number.TryGetDouble(out double n)
            ? n : null;
}

public static class SyntheticTaskbarText
{
    public static string Format(UsageObservation snapshot)
    {
        static string Percent(double? p) =>
            p.HasValue ? FormattableString.Invariant($"{p.Value:0.#}%") : "UNKNOWN";

        return $"Codex | 5H {Percent(snapshot.FiveHour.RemainingPercent)}" +
               $" | 7D {Percent(snapshot.Weekly.RemainingPercent)}" +
               $" | 리셋권 {snapshot.ResetCreditsAvailable?.ToString() ?? "UNKNOWN"} | MOCK";
    }
}
