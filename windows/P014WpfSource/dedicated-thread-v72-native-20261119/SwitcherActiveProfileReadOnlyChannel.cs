using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using CodexAccountSwitcher.Core;
using DedicatedThreadCycleStarter;

namespace CodexAccountSwitcher;

/// <summary>
/// P0-18: The running Switcher answers a one-bit current-account/pinned-chat
/// question over a CURRENT-USER, SAME-SESSION local named pipe.
/// It neither returns identifying values nor grants Owner/sender admission.
/// The existing quit pipe is not used and no mutating command is accepted.
/// </summary>
public sealed class SwitcherActiveProfileReadOnlyChannel : IDisposable
{
    public const string RequestPrefix = "CHECK_ACTIVE_BINDING_V1";
    public const string ResponsePrefix = "P018_ACTIVE_BINDING_V1";
    private readonly string pipeName;
    private readonly Dispatcher dispatcher;
    private readonly Func<AccountSnapshot?> stateSource;
    // The existing SwitchRequested monitor gate closes BEFORE the Swift
    // state changes. It prevents an old active snapshot from being returned
    // as valid during the host's asynchronous account-switch interval.
    private readonly Func<Guid?> activeGateSource;
    private readonly Func<IReadOnlyList<DedicatedChatSettings>> bindingsSource;
    private readonly CancellationTokenSource stop = new();
    private Task? listener;
    private bool disposed;

    public SwitcherActiveProfileReadOnlyChannel(string dataHome,
        Dispatcher dispatcher, Func<AccountSnapshot?> currentState,
        Func<Guid?> confirmedMonitorProfile,
        Func<IReadOnlyList<DedicatedChatSettings>> existingBindings)
    {
        pipeName = PipeName(dataHome);
        this.dispatcher = dispatcher ??
            throw new ArgumentNullException(nameof(dispatcher));
        stateSource = currentState ??
            throw new ArgumentNullException(nameof(currentState));
        activeGateSource = confirmedMonitorProfile ??
            throw new ArgumentNullException(nameof(confirmedMonitorProfile));
        bindingsSource = existingBindings ??
            throw new ArgumentNullException(nameof(existingBindings));
    }

    public static string PipeName(string dataHome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataHome);
        if (!Path.IsPathFullyQualified(dataHome))
            throw new ArgumentException("Absolute data home required");
        var normalized = Path.GetFullPath(dataHome).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(normalized)));
        return "CodexSwitcher-Personal-ActiveProof-v1-" + hash[..32] +
            "-S" + System.Diagnostics.Process.GetCurrentProcess().SessionId
                .ToString(CultureInfo.InvariantCulture);
    }

    // This is an *observation*, not an admission to send. All negative
    // conditions return the same non-identifying HOLD code.
    public static string Evaluate(AccountSnapshot? state,
        IReadOnlyList<DedicatedChatSettings>? bindings,
        Guid? monitorConfirmedProfile)
    {
        const string hold = "HOLD_ACTIVE_BINDING_UNCONFIRMED";
        if (state is null || bindings is null || bindings.Count != 1 ||
            state.ActiveAccountID is not Guid id || id == Guid.Empty ||
            monitorConfirmedProfile != id ||
            !state.ActiveIdentityConfirmed || state.IsMutating ||
            state.IsAddingAccount || !string.IsNullOrEmpty(state.Error))
            return hold;
        var matching = state.Accounts.Where(row => row.Profile.Id == id).Take(2)
            .ToArray();
        if (matching.Length != 1) return hold;
        var email = matching[0].Profile.Email?.Trim().ToLowerInvariant();
        if (email is null || email.Length is 0 or > 254 ||
            email.Any(char.IsControl) || email.Any(char.IsWhiteSpace) ||
            email.Count(ch => ch == '@') != 1)
            return hold;
        var parts = email.Split('@');
        if (parts.Length != 2 || parts[0].Length == 0 || !parts[1].Contains('.'))
            return hold;

        var binding = bindings[0];
        if (binding.ProfileId != id ||
            binding.AccountSha256 != VerifiedExistingThreadPicker.FingerprintEmail(email) ||
            !Guid.TryParseExact(binding.ExistingThreadId, "D", out _) ||
            binding.ThreadStorage != "desktop-default" ||
            binding.Message != "안녕")
            return hold;
        // Identity observation and user consent are intentionally separate.
        // Explicit send-ON preferences cannot revoke a valid read-only
        // identity proof, and a PASS here NEVER authorizes a send.
        return "PASS_ACTIVE_BINDING_MATCH_READ_ONLY";
    }

    public void Start()
    {
        if (listener != null || disposed) throw new InvalidOperationException(
            "Active-proof listener already started or disposed");
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows only");
        listener = Task.Run(ListenAsync);
    }

    private async Task ListenAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(stop.Token);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(4));
                    // Binary framed ASCII avoids StreamWriter flush on a
                    // duplex named pipe. The protocol has fixed ASCII tokens.
                    // Byte-at-a-time request limit prevents StreamReader's
                    // unbounded ReadLine allocation on a malicious same-user
                    // local client. Exactly one ASCII line is accepted.
                    var line = await ReadBoundedLineAsync(server, timeout.Token);
                    if (line is null || line.Length != RequestPrefix.Length + 1 + 32 ||
                        !line.StartsWith(RequestPrefix + "|", StringComparison.Ordinal) ||
                        !Regex.IsMatch(line.AsSpan(RequestPrefix.Length + 1).ToString(),
                            "^[a-f0-9]{32}$"))
                    {
                        await WriteAsciiLineAsync(server, "HOLD_PROTOCOL", timeout.Token);
                        continue;
                    }
                    var nonce = line[(RequestPrefix.Length + 1)..];
                    string verdict;
                    try
                    {
                        verdict = await dispatcher.InvokeAsync(() => {
                            if (dispatcher.HasShutdownStarted)
                                return "HOLD_ACTIVE_BINDING_UNCONFIRMED";
                            return Evaluate(stateSource(), bindingsSource(),
                                activeGateSource());
                        }, DispatcherPriority.Normal, timeout.Token).Task;
                    }
                    catch (Exception e) when (e is IOException or
                        UnauthorizedAccessException or InvalidDataException or
                        System.Text.Json.JsonException or InvalidOperationException or
                        System.Security.SecurityException)
                    {
                        verdict = "HOLD_ACTIVE_BINDING_UNCONFIRMED";
                    }
                    await WriteAsciiLineAsync(server,
                        ResponsePrefix + "|" + nonce + "|" + verdict,
                        timeout.Token);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (OperationCanceledException) { /* Timed out: fail closed. */ }
                catch (Exception e) when (e is IOException or
                    UnauthorizedAccessException or ObjectDisposedException or
                    InvalidOperationException or ArgumentException)
                {
                    // A missing ACL, contested pipe name, or closed client
                    // must not spin the background listener at 100% CPU.
                    // Do not log any identifying data.
                    try { await Task.Delay(150, stop.Token); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }
        finally { stop.Dispose(); }
    }

    private static Task WriteAsciiLineAsync(Stream output, string text,
        CancellationToken ct) =>
        output.WriteAsync(Encoding.ASCII.GetBytes(text + "\n").AsMemory(), ct)
            .AsTask();

    private static async Task<string?> ReadBoundedLineAsync(Stream input,
        CancellationToken ct)
    {
        var single = new byte[1];
        var buffer = new byte[128];
        int count = 0;
        while (count < buffer.Length)
        {
            if (await input.ReadAsync(single.AsMemory(), ct) != 1)
                return null;
            if (single[0] == 10)
                return Encoding.ASCII.GetString(buffer, 0, count);
            if (single[0] < 32 || single[0] > 126)
                return null;
            buffer[count++] = single[0];
        }
        // The 129th byte (or lack of newline) cannot be accepted.
        return null;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        stop.Cancel();
        // Do not block the WPF UI thread waiting for a reader.
        // The listener disposes its own CTS after cancellation finishes.
    }
}
