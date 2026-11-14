namespace CodexAccountSwitcher.Core;

/// <summary>
/// Fail-closed P3 submission coordinator for active-profile telemetry.
/// Constructing this object never initiates network work. The caller must
/// explicitly supply an already authorized, read-only destination callback.
/// Credentials, HTTP configuration and operating-app attachment are outside
/// this component, and it never grants Telegram or account-changing rights.
/// </summary>
public sealed class VerifiedQuotaDispatchCoordinator : IDisposable
{
    private readonly object sync = new();
    private readonly VerifiedActiveQuotaFrameEncoder encoder;
    private readonly Func<string, CancellationToken, Task<bool>> submit;
    private CancellationTokenSource epoch = new();
    private Guid? activeProfile;
    private bool identityConfirmed;
    private bool disposed;
    private long generation;
    private int inFlight;

    public VerifiedQuotaDispatchCoordinator(
        string enrolledSourceDevice,
        Func<string, CancellationToken, Task<bool>> submit)
    {
        this.submit = submit ?? throw new ArgumentNullException(nameof(submit));
        encoder = new VerifiedActiveQuotaFrameEncoder(enrolledSourceDevice);
    }

    /// <summary>
    /// Called when the existing local account snapshot changes. Any loss of
    /// the previously verified active identity cancels the in-flight attempt.
    /// Merely observing a fresh quota never changes an account.
    /// </summary>
    public void ObserveSnapshot(AccountSnapshot snapshot, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (sync)
        {
            if (disposed) return;
            var next = snapshot.ActiveAccountID is Guid id &&
                snapshot.ActiveIdentityConfirmed &&
                !snapshot.IsMutating &&
                !snapshot.IsAddingAccount &&
                string.IsNullOrEmpty(snapshot.Error) &&
                snapshot.Accounts.Count(row => row.Profile.Id == id) == 1
                ? id : (Guid?)null;

            if (next != activeProfile || (next is null && identityConfirmed))
                InvalidatePending();
            activeProfile = next;
            identityConfirmed = next.HasValue;
            encoder.ObserveSnapshot(snapshot, now);
        }
    }

    /// <summary>Immediately fence the outgoing channel before host mutation.</summary>
    public void SwitchRequested(DateTimeOffset now)
    {
        lock (sync)
        {
            if (disposed) return;
            InvalidatePending();
            identityConfirmed = false;
            activeProfile = null;
            encoder.SwitchRequested(now);
        }
    }

    /// <summary>
    /// Exactly one dispatch can be pending. A sample is consumed once, even
    /// if delivery fails; this class never retries a quota observation.
    /// </summary>
    public async Task<bool> DispatchAsync(
        CodexMonitorWireSample sample,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (Interlocked.CompareExchange(ref inFlight, 1, 0) != 0)
            return false;

        try
        {
            CancellationToken epochToken;
            long acceptedGeneration;
            string? frame;
            lock (sync)
            {
                if (disposed || !identityConfirmed ||
                    sample.ProfileID != activeProfile ||
                    !encoder.TryBuild(sample, now, out frame) ||
                    frame is null)
                    return false;

                acceptedGeneration = generation;
                epochToken = epoch.Token;
            }

            using var scope = CancellationTokenSource.CreateLinkedTokenSource(
                epochToken, cancellationToken);
            try
            {
                scope.Token.ThrowIfCancellationRequested();
                var completed = await submit(frame, scope.Token).ConfigureAwait(false);
                lock (sync)
                {
                    return completed && !disposed &&
                        acceptedGeneration == generation &&
                        identityConfirmed && !scope.IsCancellationRequested;
                }
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception)
            {
                // No raw frames, paths, account IDs or authorization data in logs.
                return false;
            }
        }
        finally
        {
            Interlocked.Exchange(ref inFlight, 0);
        }
    }

    private void InvalidatePending()
    {
        generation++;
        epoch.Cancel();
        epoch.Dispose();
        epoch = new CancellationTokenSource();
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            identityConfirmed = false;
            activeProfile = null;
            generation++;
            epoch.Cancel();
            epoch.Dispose();
        }
    }
}
