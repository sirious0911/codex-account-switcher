using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DedicatedThreadCycleStarter;

/// <summary>
/// Dedicated, constrained native Codex existing-thread send transport.
/// No login, thread/start, fork, account switching or arbitrary RPC.
/// turn/start is exposed ONLY to a separately fenced dispatch coordinator;
/// production authorization is NOT provided by this transport.
/// Not wired into the installed Windows release.
/// </summary>
public sealed class InitializedCodexConstrainedSendSession : ICodexJsonRpcSession, IAsyncDisposable
{
    private static readonly HashSet<string> AllowedMethods =
        new(StringComparer.Ordinal) {
            "account/read", "thread/list", "thread/read",
            "thread/resume", "turn/start" };
    private readonly Process process;
    private readonly SemaphoreSlim writer = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> pending = new();
    private readonly Task stdout;
    private readonly Task stderr;
    private int nextId;
    private bool initialized;

    private InitializedCodexConstrainedSendSession(Process started)
    {
        process = started;
        stdout = PumpAsync();
        stderr = DrainStderrAsync();
    }

    // This entrypoint is used only for the default home diagnostic.
    public static Task<InitializedCodexConstrainedSendSession> StartAsync(
        string codexExePath, CancellationToken cancellationToken = default) =>
        StartInternalAsync(codexExePath, null, cancellationToken);

    // Codex Desktop normally uses the signed-in user's default ~/.codex home.
    // This is a second read-only source, never implicit authorization to send.
    public static Task<InitializedCodexConstrainedSendSession> StartForDesktopDefaultAsync(
        string codexExePath, CancellationToken cancellationToken = default)
    {
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userHome) || !Path.IsPathFullyQualified(userHome))
            throw new InvalidOperationException("Desktop home unknown");
        var codexHome = Path.Combine(userHome, ".codex");
        var folder = new DirectoryInfo(codexHome);
        if (!folder.Exists || folder.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("Desktop Codex home unavailable");
        return StartInternalAsync(codexExePath, codexHome, cancellationToken);
    }

    // The Switcher profile is isolated under the private app's accounts/<UUID>
    // CODEX_HOME; never infer it from a globally logged-in CLI session.
    public static Task<InitializedCodexConstrainedSendSession> StartForSwitcherProfileAsync(
        string codexExePath, Guid profileId, string switcherDataHome,
        CancellationToken cancellationToken = default)
    {
        if (profileId == Guid.Empty || string.IsNullOrWhiteSpace(switcherDataHome) ||
            !Path.IsPathFullyQualified(switcherDataHome))
            throw new InvalidOperationException("Profile root unavailable");
        var root = Path.GetFullPath(switcherDataHome);
        var home = Path.Combine(root, "accounts", profileId.ToString("D"));
        var directory = new DirectoryInfo(home);
        if (!directory.Exists || directory.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            new DirectoryInfo(root).Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("Profile storage not trusted");
        return StartInternalAsync(codexExePath, home, cancellationToken);
    }

    private static async Task<InitializedCodexConstrainedSendSession> StartInternalAsync(
        string codexExePath, string? profileHome,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(codexExePath) ||
            !Path.IsPathFullyQualified(codexExePath))
            throw new InvalidOperationException("Explicit Windows Codex binary required");
        var full = Path.GetFullPath(codexExePath);
        if (!string.Equals(Path.GetFileName(full), "codex.exe",
            StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unexpected Codex executable name");
        var info = new FileInfo(full);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("Codex executable unavailable");
        var start = new ProcessStartInfo(full)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            WorkingDirectory = info.DirectoryName!
        };
        if (profileHome is not null)
            start.Environment["CODEX_HOME"] = profileHome;
        start.ArgumentList.Add("app-server");
        start.ArgumentList.Add("--stdio");
        var started = Process.Start(start) ??
            throw new InvalidOperationException("Codex app-server did not start");
        var session = new InitializedCodexConstrainedSendSession(started);
        try
        {
            await session.CallRawAsync("initialize", new {
                clientInfo = new {
                    name = "codex-switcher-existing-thread-fenced",
                    title = "Codex Switcher Fenced Existing Thread",
                    version = "0.1.0"
                }
            }, cancellationToken);
            await session.NotifyAsync("initialized", cancellationToken);
            session.initialized = true;
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    public Task<JsonElement> CallAsync(string method, object? parameters,
        CancellationToken cancellationToken)
    {
        if (!initialized || !AllowedMethods.Contains(method))
            throw new InvalidOperationException("RPC operation not allowlisted");
        return CallRawAsync(method, parameters, cancellationToken);
    }

    private async Task<JsonElement> CallRawAsync(string method, object? parameters,
        CancellationToken cancellationToken)
    {
        using var timed = CancellationTokenSource.CreateLinkedTokenSource(
            lifetime.Token, cancellationToken);
        timed.CancelAfter(TimeSpan.FromSeconds(10));
        var id = Interlocked.Increment(ref nextId);
        var waiter = new TaskCompletionSource<JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pending.TryAdd(id, waiter))
            throw new InvalidOperationException("Duplicate RPC id");
        try
        {
            await SendLineAsync(JsonSerializer.Serialize(new { id, method, @params = parameters }),
                timed.Token);
            return await waiter.Task.WaitAsync(timed.Token);
        }
        finally { pending.TryRemove(id, out _); }
    }

    private Task NotifyAsync(string method, CancellationToken cancellationToken) =>
        SendLineAsync(JsonSerializer.Serialize(new { method }), cancellationToken);

    private async Task SendLineAsync(string json, CancellationToken cancellationToken)
    {
        await writer.WaitAsync(cancellationToken);
        try
        {
            if (process.HasExited)
                throw new IOException("Codex app-server stopped");
            await process.StandardInput.WriteLineAsync(json.AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
        }
        finally { writer.Release(); }
    }

    private async Task PumpAsync()
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync(lifetime.Token) is { } line)
            {
                if (line.Length > 2_000_000) throw new InvalidDataException("Oversize RPC");
                using var doc = JsonDocument.Parse(line);
                var obj = doc.RootElement;
                if (obj.ValueKind != JsonValueKind.Object ||
                    !obj.TryGetProperty("id", out var idField) ||
                    !idField.TryGetInt32(out var id) ||
                    !pending.TryGetValue(id, out var waiter))
                    continue;
                if (obj.TryGetProperty("error", out _))
                    waiter.TrySetException(new IOException("Codex RPC returned an error"));
                else if (obj.TryGetProperty("result", out var result))
                    waiter.TrySetResult(result.Clone());
                else waiter.TrySetException(new InvalidDataException("RPC result absent"));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or
            JsonException or InvalidDataException) { /* Never print raw protocol data. */ }
        finally
        {
            foreach (var callback in pending.Values)
                callback.TrySetException(new IOException("Codex app-server unavailable"));
        }
    }

    private async Task DrainStderrAsync()
    {
        try
        {
            var buffer = new char[2048];
            while (await process.StandardError.ReadAsync(buffer.AsMemory(), lifetime.Token) > 0)
            { /* Discard; stderr may contain sensitive local information. */ }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        try { process.StandardInput.Close(); } catch (IOException) { }
        if (!process.HasExited)
        {
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (TimeoutException) { if (!process.HasExited) process.Kill(); }
        }
        try { await Task.WhenAll(stdout, stderr); }
        catch (OperationCanceledException) { }
        process.Dispose();
        writer.Dispose();
        lifetime.Dispose();
    }
}
