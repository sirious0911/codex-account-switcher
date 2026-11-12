namespace CodexAccountSwitcher.Core;

/// <summary>
/// Decide whether a previously requested taskbar panel should retry a failed
/// Explorer attachment. A failed host is retried on the existing presentation
/// timer; this never starts a second worker or initiates a Codex request.
/// </summary>
public static class TaskbarAttachRetryPolicy
{
    public static bool ShouldRetry(
        bool started, bool disposed, bool attaching, bool recreating, bool hasHost) =>
        started && !disposed && !attaching && !recreating && !hasHost;
}
