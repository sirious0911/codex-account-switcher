using System.Text.Json;
using System.Text.RegularExpressions;

namespace DedicatedThreadCycleStarter;

/// <summary>One-time picker contract: select a UUID from thread/list under the
/// currently verified Codex account. The saved target is never re-created.</summary>
public sealed class DedicatedThreadBinding
{
    private readonly ExistingCodexThreadRpc client;
    private readonly DedicatedThreadBindingStore store;
    public DedicatedThreadBinding(ExistingCodexThreadRpc client, DedicatedThreadBindingStore store)
    {
        this.client = client;
        this.store = store;
    }

    public async Task<DedicatedChatSettings> SelectExistingAsync(
        Guid activeProfile, string activeAccountSha256, bool activeIdentityConfirmed,
        string selectedThreadId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(activeAccountSha256);
        if (!activeIdentityConfirmed || activeProfile == Guid.Empty ||
            !Regex.IsMatch(activeAccountSha256 ?? "", "^[a-f0-9]{64}$") ||
            !Guid.TryParseExact(selectedThreadId, "D", out _))
            throw new InvalidOperationException("Active account or thread not verified");

        var listed = await client.ListCurrentAccountAsync(null, cancellationToken);
        if (listed.Count(c => c.Id == selectedThreadId) != 1)
            throw new InvalidOperationException("Selected thread not in this account's list");
        var existing = await client.ReadAsync(selectedThreadId, cancellationToken);
        if (existing is null || existing.ThreadId != selectedThreadId ||
            !existing.IsPersisted || (!existing.IsIdle && !existing.IsNotLoaded))
            throw new InvalidOperationException("Selected thread is unavailable/unsaved/busy");

        // One-time binding is NOT permission to start sending messages.
        var binding = new DedicatedChatSettings(activeProfile, activeAccountSha256!,
            selectedThreadId, AutomaticMessagesEnabled: false,
            StartOnFiveHour: false, StartOnSevenDay: false);
        store.Upsert(binding);
        return binding;
    }
}

/// <summary>Local per-account mapping. Does not contain raw credentials.
/// Atomic save and fail-closed parsing; no auto-enabling when thread changes.</summary>
public sealed class DedicatedThreadBindingStore
{
    private readonly string path;
    public DedicatedThreadBindingStore(string path) => this.path = Path.GetFullPath(path);

    private const int MaxBindingFileBytes = 65_536;
    private static readonly string[] ExactBindingFields = [
        "ProfileId", "AccountSha256", "ExistingThreadId",
        "AutomaticMessagesEnabled", "StartOnFiveHour", "StartOnSevenDay",
        "Message", "ThreadStorage"
    ];

    private static DedicatedChatSettings ParseBinding(JsonElement element,
        bool requireValidIdentity)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Invalid binding record");
        var expected = new HashSet<string>(ExactBindingFields,
            StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!expected.Remove(property.Name))
                throw new InvalidDataException(
                    "Unexpected or duplicate binding field");
        if (expected.Count != 0)
            throw new InvalidDataException("Incomplete binding record");
        var record = element.Deserialize<DedicatedChatSettings>()
            ?? throw new InvalidDataException("Empty binding record");
        if (requireValidIdentity && (record.ProfileId == Guid.Empty ||
            !Guid.TryParseExact(record.ExistingThreadId, "D", out _) ||
            record.AccountSha256 is null ||
            !Regex.IsMatch(record.AccountSha256, "^[a-f0-9]{64}$") ||
            record.Message != "안녕" ||
            record.ThreadStorage is not ("desktop-default" or "switcher-profile")))
            throw new InvalidDataException("Invalid binding identity");
        return record;
    }

    public IReadOnlyList<DedicatedChatSettings> ReadAll()
    {
        if (!File.Exists(path)) return [];
        var file = new FileInfo(path);
        if (file.Length <= 0 || file.Length > MaxBindingFileBytes)
            throw new InvalidDataException("Invalid binding store size");
        // Existing single-object V7.2 installations remain readable.
        // No read triggers migration or turns on any send preference.
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (document.RootElement.ValueKind == JsonValueKind.Object)
        {
            var legacy = ParseBinding(document.RootElement,
                requireValidIdentity: true);
            return [legacy with {
                AutomaticMessagesEnabled = false,
                StartOnFiveHour = false,
                StartOnSevenDay = false
            }];
        }
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Unsupported binding store");
        var result = new List<DedicatedChatSettings>();
        var profileIds = new HashSet<Guid>();
        var threads = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in document.RootElement.EnumerateArray())
        {
            // Existing callers validate business identity at their own
            // read-only gate, which intentionally reports specific HOLDS.
            // This storage layer enforces only array structure/uniqueness.
            var binding = ParseBinding(item, requireValidIdentity: false);
            if (!profileIds.Add(binding.ProfileId) ||
                !threads.Add(binding.ExistingThreadId))
                throw new InvalidDataException("Duplicate profile or thread");
            result.Add(binding);
        }
        return result;
    }

    public void Upsert(DedicatedChatSettings selected)
    {
        var existing = ReadAll().ToList();
        if (existing.Any(x => x.ProfileId != selected.ProfileId &&
            x.ExistingThreadId == selected.ExistingThreadId))
            throw new InvalidOperationException("Cannot share a pinned thread across accounts");
        var old = existing.FindIndex(x => x.ProfileId == selected.ProfileId);
        if (old < 0) existing.Add(selected);
        else existing[old] = selected;
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temp = path + ".pending-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(existing,
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
