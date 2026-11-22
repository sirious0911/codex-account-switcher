using System.Text.Json;

namespace DedicatedThreadCycleStarter;

public interface ICodexJsonRpcSession
{
    /// <summary>Caller-provided initialized Codex app-server session. No process,
    /// authentication, login, or account changes are performed in this adapter.</summary>
    Task<JsonElement> CallAsync(string method, object? parameters, CancellationToken cancellationToken);
}

public sealed record ExistingThreadChoice(string Id, string Title);

/// <summary>Allowlisted Codex app-server adapter for selecting/reusing an
/// existing thread. There is deliberately no method for thread/start.</summary>
public sealed class ExistingCodexThreadRpc : IExistingThreadClient
{
    private readonly ICodexJsonRpcSession rpc;
    public ExistingCodexThreadRpc(ICodexJsonRpcSession rpc) => this.rpc = rpc;

    /// <summary>Show existing chats from only the authenticated active account;
    /// never select by a nonunique title alone. The UI must bind the selected ID.</summary>
    public async Task<IReadOnlyList<ExistingThreadChoice>> ListCurrentAccountAsync(
        string? search, CancellationToken cancellationToken = default)
    {
        var result = await rpc.CallAsync("thread/list",
            new { currentAccountOnly = true, searchTerm = search, limit = 50 },
            cancellationToken);
        if (!result.TryGetProperty("data", out var entries) ||
            entries.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Thread list response missing data");
        var choices = new List<ExistingThreadChoice>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in entries.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !TryString(item, "id", out var id) ||
                !Guid.TryParseExact(id, "D", out _) ||
                !TryDisplayName(item, out var title))
                continue;
            if (ids.Add(id))
                choices.Add(new ExistingThreadChoice(id, title));
        }
        return choices;
    }

    public async Task<ExistingThread?> ReadAsync(
        string pinnedThreadId, CancellationToken cancellationToken)
    {
        RequireUuid(pinnedThreadId);
        var result = await rpc.CallAsync("thread/read",
            new { threadId = pinnedThreadId, includeTurns = false },
            cancellationToken);
        if (!result.TryGetProperty("thread", out var thread) ||
            thread.ValueKind != JsonValueKind.Object ||
            !TryString(thread, "id", out var id) || id != pinnedThreadId)
            return null;
        var isEphemeral = thread.TryGetProperty("ephemeral", out var ephemeral) &&
            ephemeral.ValueKind == JsonValueKind.True;
        // 'path' is the durable rollout path for a persisted thread.
        var hasPath = TryString(thread, "path", out var path) &&
            !string.IsNullOrWhiteSpace(path);
        var idle = false;
        var notLoaded = false;
        if (thread.TryGetProperty("status", out var status))
        {
            if (status.ValueKind == JsonValueKind.Object &&
                TryString(status, "type", out var type))
            {
                idle = type == "idle";
                notLoaded = type == "notLoaded";
            }
            else if (status.ValueKind == JsonValueKind.String)
            {
                idle = status.GetString() == "idle";
                notLoaded = status.GetString() == "notLoaded";
            }
        }
        return new ExistingThread(id, hasPath && !isEphemeral, idle, notLoaded);
    }

    public async Task ResumeAsync(string pinnedThreadId, CancellationToken cancellationToken)
    {
        RequireUuid(pinnedThreadId);
        var result = await rpc.CallAsync("thread/resume",
            new { threadId = pinnedThreadId }, cancellationToken);
        if (!result.TryGetProperty("thread", out var thread) ||
            thread.ValueKind != JsonValueKind.Object ||
            !TryString(thread, "id", out var actualId) || actualId != pinnedThreadId)
            throw new InvalidDataException("Resume did not confirm pinned thread ID");
    }

    public async Task<string?> StartTurnAsync(
        string pinnedThreadId, string text, CancellationToken cancellationToken)
    {
        RequireUuid(pinnedThreadId);
        if (text != "안녕") throw new InvalidOperationException("Unapproved content");
        var result = await rpc.CallAsync("turn/start", new
        {
            threadId = pinnedThreadId,
            input = new[] { new { type = "text", text } }
        }, cancellationToken);
        if (!result.TryGetProperty("turn", out var turn) ||
            turn.ValueKind != JsonValueKind.Object ||
            !TryString(turn, "id", out var turnId))
            return null;
        return turnId;
    }

    /// <summary>
    /// Codex Desktop app-server uses 'name' and 'preview' (without 'title').
    /// The older CLI/test schema may still use 'title'. Normalize for WPF.
    /// Only display metadata is processed: thread IDs remain exact.
    /// </summary>
    private static bool TryDisplayName(JsonElement item, out string title)
    {
        foreach (var key in new[] { "name", "title", "preview" })
        {
            if (!TryString(item, key, out var raw) || string.IsNullOrWhiteSpace(raw))
                continue;
            var normalized = raw.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (normalized.Length == 0) continue;
            title = normalized.Length > 140 ? normalized[..140] : normalized;
            return true;
        }
        title = "";
        return false;
    }

    private static void RequireUuid(string id)
    {
        if (!Guid.TryParseExact(id, "D", out _))
            throw new ArgumentException("Must pin an existing thread UUID");
    }

    private static bool TryString(JsonElement root, string key, out string value)
    {
        if (root.TryGetProperty(key, out var e) && e.ValueKind == JsonValueKind.String)
        {
            value = e.GetString() ?? "";
            return value.Length > 0;
        }
        value = "";
        return false;
    }
}
