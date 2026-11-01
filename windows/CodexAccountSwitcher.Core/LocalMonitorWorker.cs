using System.Security.Cryptography;
using System.Text;
using CodexUsageMonitor.Core;

namespace CodexAccountSwitcher.Core;

/// <summary>
/// An in-process Windows monitor worker using the existing cross-platform
/// .NET monitor algorithms. It needs no separate Node installation.
/// Its outbox is strictly a local mock preview, never a Telegram sender.
/// </summary>
public sealed class LocalMonitorWorker
{
    private readonly string directory;
    private readonly object gate = new();

    public LocalMonitorWorker(string userDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userDataDirectory);
        directory = Path.Combine(userDataDirectory, "usage-monitor");
    }

    public LocalMonitorResult Process(CodexMonitorWireSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (!MonitorSyntheticPreview.TryRead(sample.RawJson, out _))
            throw new InvalidDataException("Invalid monitor observation.");

        var observedAt = DateTimeOffset.FromUnixTimeMilliseconds(sample.ObservedAtMilliseconds);
        var next = SyntheticUsageNormalizer.Parse(sample.RawJson, observedAt);
        if (string.IsNullOrWhiteSpace(next.AccountId))
            throw new InvalidDataException("Missing provider account identity.");

        // Provider IDs never become filesystem path segments.
        var accountKey = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(next.AccountId))).ToLowerInvariant();
        var accountDirectory = Path.Combine(directory, accountKey);

        lock (gate)
        {
            Directory.CreateDirectory(accountDirectory);
            // The handle is OS-owned. An interrupted process releases it,
            // so a subsequent launch can recover without deleting a stale lock.
            using var session = new FileStream(
                Path.Combine(accountDirectory, ".processing.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

            var state = MockStateStore.Read(accountDirectory);
            // State writes use an atomic move. If an earlier run stopped before
            // that move, the last committed state remains authoritative.
            var temporary = Path.Combine(accountDirectory, "state.json.tmp");
            if (File.Exists(temporary)) File.Delete(temporary);

            var events = state.Advance(next);
            MockStateStore.Write(accountDirectory, state);

            var mockMessages = new List<string>();
            foreach (var ev in events)
            {
                var message = MockDeliveryController.Reserve(
                    state, ev.Key, MockDeliveryController.WindowsMock);
                if (message is null) continue;

                // Persist mock reservation before acknowledging it. There is
                // deliberately no network or real bot delivery in this worker.
                MockStateStore.Write(accountDirectory, state);
                MockDeliveryController.Acknowledge(
                    state, ev.Key, MockDeliveryController.WindowsMock);
                MockStateStore.Write(accountDirectory, state);
                mockMessages.Add(message);
            }

            return new LocalMonitorResult(
                SyntheticTaskbarText.Format(next),
                events.Select(e => e.Kind).ToArray(),
                mockMessages.ToArray(),
                state.EmittedKeys.Count,
                state.MockDeliveries.Count(entry => entry.Status == "MOCK_COMPLETE"));
        }
    }
}

public sealed record LocalMonitorResult(
    string MonitorPreview,
    IReadOnlyList<string> EventKinds,
    IReadOnlyList<string> MockAlertMessages,
    int UniqueEventCount,
    int CompletedMockDeliveries);
