using System.Security;
using System.Text.Json;

namespace CodexAccountSwitcher.Core;

/// <summary>
/// Read-only projection of the existing 5H/7D mock recovery outboxes.
/// No network, tokens, account credentials, sender scripts or state writes.
/// Neither a local ledger entry nor a displayed row is a Telegram receipt.
/// </summary>
public static class OfflineTelegramRecoveryInbox
{
    private const long MaxLedgerBytes = 1_048_576;
    private const int MaxVisibleRows = 10;

    public static OfflineTelegramInboxSnapshot Read(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var fivePath = Path.Combine(dataDirectory, "usage-monitor-fivehour100", "state.json");
        var weeklyPath = Path.Combine(dataDirectory, "usage-monitor-weekly100", "state.json");

        var five = ReadState<FiveHour100DurableState>(fivePath, out var fiveError);
        var week = ReadState<Weekly100DurableState>(weeklyPath, out var weeklyError);

        if (five is not null && (five.SchemaVersion != 1 || five.Outbox is null))
        {
            fiveError = true;
            five = null;
        }
        if (week is not null && (week.SchemaVersion != 1 || week.Outbox is null))
        {
            weeklyError = true;
            week = null;
        }
        var projection = Project(five?.Outbox, week?.Outbox);
        return projection with { LedgerReadError = fiveError || weeklyError };
    }

    public static OfflineTelegramInboxSnapshot Project(
        IReadOnlyList<FiveHour100Alert>? fiveHour,
        IReadOnlyList<Weekly100Alert>? weekly)
    {
        var items = new List<OfflineTelegramInboxRow>();
        foreach (var entry in fiveHour ?? [])
        {
            if (entry is not { Kind: "FIVE_HOUR_100_RECOVERED",
                Lane: "fiveHour", RemainingPercent: 100 } ||
                !ValidEvent(entry.Key) || !ValidMockStatus(entry.Status))
                continue;
            items.Add(new OfflineTelegramInboxRow("5H", "모니터 봇(묵음)",
                entry.ObservedAt, entry.Status, entry.Key));
        }
        foreach (var entry in weekly ?? [])
        {
            if (entry is not { Kind: "WEEKLY_100_RECOVERED",
                Lane: "week", RemainingPercent: 100 } ||
                !ValidEvent(entry.Key) || !ValidMockStatus(entry.Status))
                continue;
            items.Add(new OfflineTelegramInboxRow("7D", "얼럿 봇",
                entry.ObservedAt, entry.Status, entry.Key));
        }
        var recent = items
            .GroupBy(row => (row.Lane, row.EventKey))
            .Select(group => group.OrderByDescending(row => row.ObservedAt).First())
            .OrderByDescending(row => row.ObservedAt)
            .ThenBy(row => row.Lane, StringComparer.Ordinal)
            .Take(MaxVisibleRows)
            .ToArray();
        return new OfflineTelegramInboxSnapshot(recent, false);
    }

    private static T? ReadState<T>(string path, out bool error) where T : class
    {
        error = false;
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) return null;
            if (file.Length == 0 || file.Length > MaxLedgerBytes ||
                file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                error = true;
                return null;
            }
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<T>(stream)
                ?? throw new JsonException("Empty recovery ledger");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            JsonException or SecurityException or ArgumentException or NotSupportedException)
        {
            error = true;
            return null;
        }
    }

    private static bool ValidEvent(string? key) =>
        key is { Length: 64 } &&
        key.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool ValidMockStatus(string? status) =>
        status is "MOCK_PENDING" or "MOCK_RESERVED" or
            "MOCK_COMPLETE" or "MOCK_UNCERTAIN";
}

public sealed record OfflineTelegramInboxRow(
    string Lane,
    string Destination,
    DateTimeOffset ObservedAt,
    string MockStatus,
    string EventKey)
{
    public string EventReference => EventKey[..8];
    public string StatusLabel => MockStatus switch
    {
        "MOCK_PENDING" => "모의 대기",
        "MOCK_RESERVED" => "모의 예약",
        "MOCK_COMPLETE" => "모의 완료",
        "MOCK_UNCERTAIN" => "모의 결과 불확실",
        _ => "상태 미확인"
    };
}

public sealed record OfflineTelegramInboxSnapshot(
    IReadOnlyList<OfflineTelegramInboxRow> Rows,
    bool LedgerReadError)
{
    public static OfflineTelegramInboxSnapshot Empty => new([], false);
}
