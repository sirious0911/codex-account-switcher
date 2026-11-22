using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DedicatedThreadCycleStarter;

namespace CodexAccountSwitcher;

/// <summary>
/// Independently reads completed-turn metadata from an already initialized,
/// credential-local Codex app-server session. The caller MUST supply the
/// allowlisted read-only session, not a client-supplied JSON receipt.
/// No thread/resume, turn/start, new thread or account mutation.
/// </summary>
public static class CodexNativeCompletionEvidence
{
    public sealed record Verified(
        string AccountSha256,
        string ThreadId,
        string TurnId,
        string Status,
        string UserInputText,
        long StartedAtMs,
        long CompletedAtMs);

    public static async Task<Verified> ReadAsync(
        ICodexJsonRpcSession readOnly,
        DedicatedChatSettings pinned,
        string expectedTurnId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(readOnly);
        ArgumentNullException.ThrowIfNull(pinned);
        if (pinned.ProfileId == Guid.Empty ||
            !Guid.TryParseExact(pinned.ExistingThreadId, "D", out _) ||
            !Guid.TryParseExact(expectedTurnId, "D", out _) ||
            pinned.ThreadStorage is not ("desktop-default" or "switcher-profile") ||
            pinned.Message != "안녕")
            throw new InvalidOperationException("Exact pinned Codex conversation required");

        var auth = await readOnly.CallAsync(
            "account/read", new { refreshToken = false }, ct);
        if (!auth.TryGetProperty("account", out var account) ||
            account.ValueKind != JsonValueKind.Object ||
            !account.TryGetProperty("type", out var type) ||
            type.ValueKind != JsonValueKind.String ||
            type.GetString() != "chatgpt" ||
            !account.TryGetProperty("email", out var mail) ||
            mail.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(mail.GetString()))
            throw new InvalidDataException("Authenticated ChatGPT account not proven");
        var email = mail.GetString()!.Trim().ToLowerInvariant();
        if (VerifiedExistingThreadPicker.FingerprintEmail(email)
            != pinned.AccountSha256)
            throw new InvalidDataException("Native Codex account differs from pinned account");

        var response = await readOnly.CallAsync(
            "thread/read", new {
                threadId = pinned.ExistingThreadId,
                includeTurns = true
            }, ct);
        if (!response.TryGetProperty("thread", out var thread) ||
            thread.ValueKind != JsonValueKind.Object ||
            !TryText(thread, "id", out var gotId) ||
            gotId != pinned.ExistingThreadId ||
            !thread.TryGetProperty("ephemeral", out var ephemeral) ||
            ephemeral.ValueKind != JsonValueKind.False ||
            !TryText(thread, "path", out var path) ||
            string.IsNullOrWhiteSpace(path) ||
            !thread.TryGetProperty("turns", out var turns) ||
            turns.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Verified persisted thread metadata unavailable");

        var matched = turns.EnumerateArray().Where(t =>
            t.ValueKind == JsonValueKind.Object &&
            TryText(t, "id", out var id) && id == expectedTurnId).ToArray();
        if (matched.Length != 1)
            throw new InvalidDataException("Exact completed turn is not uniquely present");

        var turn = matched[0];
        if (!TryText(turn, "status", out var status) ||
            status != "completed" ||
            !TryUnixTime(turn, "startedAt", out var startedAtMs) ||
            !TryUnixTime(turn, "completedAt", out var completedAtMs) ||
            completedAtMs < startedAtMs ||
            completedAtMs > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 30_000)
            throw new InvalidDataException("Turn completion and duration not proven");

        if (!turn.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("User input unavailable");
        var userMessages = items.EnumerateArray()
            .Where(i => i.ValueKind == JsonValueKind.Object &&
                TryText(i, "type", out var messageType) &&
                messageType == "userMessage").ToArray();
        if (userMessages.Length != 1 ||
            !userMessages[0].TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.Array ||
            content.GetArrayLength() != 1 ||
            !TryText(content[0], "text", out var input) ||
            input != "안녕")
            throw new InvalidDataException("Exactly one approved greeting is not proven");

        // A different ChatGPT identity appearing during thread/read is
        // not permitted to become proof for the earlier profile binding.
        var after = await readOnly.CallAsync(
            "account/read", new { refreshToken = false }, ct);
        if (!after.TryGetProperty("account", out var afterAccount) ||
            afterAccount.ValueKind != JsonValueKind.Object ||
            !TryText(afterAccount, "type", out var afterType) ||
            afterType != "chatgpt" ||
            !TryText(afterAccount, "email", out var afterEmail) ||
            VerifiedExistingThreadPicker.FingerprintEmail(
                afterEmail.Trim().ToLowerInvariant()) != pinned.AccountSha256)
            throw new InvalidDataException("Codex account changed during completion verification");

        // All evidence is derived from native account/read and thread/read.
        // No token, other chat contents or credentials leave this method.
        return new(pinned.AccountSha256, pinned.ExistingThreadId,
            expectedTurnId, "completed", input, startedAtMs, completedAtMs);
    }

    private static bool TryText(JsonElement item, string key, out string value)
    {
        if (item.ValueKind == JsonValueKind.Object &&
            item.TryGetProperty(key, out var field) &&
            field.ValueKind == JsonValueKind.String &&
            !string.IsNullOrEmpty(field.GetString()))
        {
            value = field.GetString()!;
            return true;
        }
        value = "";
        return false;
    }

    private static bool TryUnixTime(
        JsonElement turn, string field, out long milliseconds)
    {
        milliseconds = 0;
        if (!turn.TryGetProperty(field, out var source) ||
            source.ValueKind != JsonValueKind.Number ||
            !source.TryGetInt64(out var unixSeconds) ||
            unixSeconds < 1_577_836_800 || unixSeconds > 4_102_444_800)
            return false;
        try
        {
            milliseconds = checked(unixSeconds * 1000);
            return true;
        }
        catch (OverflowException) { return false; }
    }
}
