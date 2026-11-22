using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace CodexAccountSwitcher;

/// <summary>
/// Local, current-user/interactive-session-only, two-phase IPC to request the
/// app's existing orderly WPF QuitAsync. It never kills a process, touches
/// credentials, replays Telegram, or changes persistent Sender consent.
/// </summary>
internal sealed class RemoteGracefulQuitChannel : IDisposable
{
    internal const string Hello = "QUIT_V1";
    internal const string Commit = "COMMIT_V1";
    internal const string Ready = "READY";
    internal const string Accepted = "ACCEPTED";
    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly string name;
    private readonly Dispatcher dispatcher;
    private readonly Func<string> readyCheck;
    private readonly Func<string> reserve;
    private readonly Action cancelReserve;
    private readonly Action quit;
    private readonly CancellationTokenSource stop = new();
    private Task? runner;

    internal RemoteGracefulQuitChannel(string dataHome, Dispatcher dispatcher,
        Func<string> readyCheck, Func<string> reserve,
        Action cancelReserve, Action quit)
    {
        name = PipeName(dataHome);
        this.dispatcher = dispatcher;
        this.readyCheck = readyCheck;
        this.reserve = reserve;
        this.cancelReserve = cancelReserve;
        this.quit = quit;
    }

    internal static string PipeName(string dataHome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataHome);
        var normalized = Path.GetFullPath(dataHome).ToUpperInvariant();
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        // Local Windows named pipes are machine-local. Session ID separates
        // distinct interactive sessions of the same Windows user.
        return "CodexSwitcher-Personal-Quit-v1-" + digest[..32] + "-S" +
            Process.GetCurrentProcess().SessionId.ToString(CultureInfo.InvariantCulture);
    }

    internal void Start()
    {
        if (runner is not null) throw new InvalidOperationException("Already started");
        runner = Task.Run(ListenAsync);
    }

    private async Task ListenAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stop.Token);
                await ServeOneAsync(pipe, stop.Token);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception error) when (error is IOException or TimeoutException or
                UnauthorizedAccessException or InvalidOperationException)
            {
                // Do not print pipe identity, user profile, private path or credentials.
                Trace.TraceWarning("Remote quit IPC unavailable: " + error.GetType().Name);
                try { await Task.Delay(120, stop.Token); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task ServeOneAsync(NamedPipeServerStream pipe, CancellationToken shuttingDown)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(shuttingDown);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        using var reader = new StreamReader(pipe, Utf8, false, 1024, leaveOpen: true);
        using var writer = new StreamWriter(pipe, Utf8, 1024, leaveOpen: true) { AutoFlush = true };
        bool reserved = false;
        bool quitScheduled = false;
        try
        {
            if (await reader.ReadLineAsync(deadline.Token) != Hello)
            {
                await writer.WriteLineAsync("HOLD_PROTOCOL");
                return;
            }
            var preflight = await dispatcher.InvokeAsync(readyCheck,
                DispatcherPriority.Send, deadline.Token).Task;
            if (preflight != Ready)
            {
                await writer.WriteLineAsync("HOLD_" + preflight);
                return;
            }
            await writer.WriteLineAsync(Ready + "|" +
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            if (await reader.ReadLineAsync(deadline.Token) != Commit)
            {
                await writer.WriteLineAsync("HOLD_COMMIT_MISSING");
                return;
            }

            // Second check is serialized on the single WPF UI dispatcher.
            // Reserve blocks new Telegram deliveries until QuitAsync takes over.
            var result = await dispatcher.InvokeAsync(reserve,
                DispatcherPriority.Send, deadline.Token).Task;
            if (result != Ready)
            {
                await writer.WriteLineAsync("HOLD_" + result);
                return;
            }
            reserved = true;
            await writer.WriteLineAsync(Accepted + "|" +
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            await writer.FlushAsync(deadline.Token);

            // Send the acknowledgement before the owner closes this pipe.
            _ = dispatcher.BeginInvoke(quit, DispatcherPriority.Send);
            quitScheduled = true;
        }
        catch (OperationCanceledException) when (!shuttingDown.IsCancellationRequested)
        {
            // A caller that disappears or stalls cannot leave the app reserved.
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            Trace.TraceWarning("Remote quit handshake unavailable: " + ex.GetType().Name);
        }
        finally
        {
            if (reserved && !quitScheduled && !dispatcher.HasShutdownStarted)
            {
                try { await dispatcher.InvokeAsync(cancelReserve,
                    DispatcherPriority.Send, shuttingDown).Task; }
                catch (OperationCanceledException) { }
                catch (InvalidOperationException) { }
            }
        }
    }

    internal static async Task<int> RequestExitAsync(string dataHome)
    {
        // CLI return codes: 0 owner exited, 10 missing/denied, 11 HOLD,
        // 12 protocol/owner mismatch, 13 exited unverified/timeout.
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName(dataHome),
                PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(1100, limit.Token);
            using var reader = new StreamReader(pipe, Utf8, false, 1024, leaveOpen: true);
            using var writer = new StreamWriter(pipe, Utf8, 1024, leaveOpen: true)
            {
                AutoFlush = true
            };
            await writer.WriteLineAsync(Hello);
            var reply = await reader.ReadLineAsync(limit.Token);
            if (reply is null || reply.StartsWith("HOLD_", StringComparison.Ordinal))
                return 11;
            if (!TryPid(reply, Ready, out var pid)) return 12;

            using var owner = Process.GetProcessById(pid);
            var expected = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(expected) ||
                !string.Equals(Path.GetFullPath(owner.MainModule!.FileName),
                    Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase) ||
                owner.Id == Environment.ProcessId || owner.HasExited)
                return 12;

            await writer.WriteLineAsync(Commit);
            var accepted = await reader.ReadLineAsync(limit.Token);
            if (!TryPid(accepted, Accepted, out var committedPid) || committedPid != pid)
                return 11;
            await owner.WaitForExitAsync(limit.Token);
            return owner.HasExited ? 0 : 13;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            TimeoutException or System.ComponentModel.Win32Exception or ArgumentException or InvalidOperationException)
        {
            return 10;
        }
        catch (OperationCanceledException) { return 13; }
    }

    private static bool TryPid(string? line, string heading, out int pid)
    {
        pid = 0;
        if (line is null || !line.StartsWith(heading + "|", StringComparison.Ordinal))
            return false;
        return int.TryParse(line.AsSpan(heading.Length + 1),
            NumberStyles.None, CultureInfo.InvariantCulture, out pid) && pid > 0;
    }

    public void Dispose()
    {
        stop.Cancel();
        // Never block the WPF thread while a waiter finishes cancellation.
        // Dispose CTS only when the listener has actually stopped.
        var run = runner;
        if (run is null) stop.Dispose();
        else _ = run.ContinueWith(_ => stop.Dispose(),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
