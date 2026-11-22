using System;

namespace CodexAccountSwitcher;

/// <summary>Shared admission point before Owner startup or due IPC.</summary>
internal static class WindowsOwnerReadOnlyAdmission
{
    internal const string PendingTrust = "HOLD_OWNER_ADMISSION_TRUST_UNAVAILABLE";

    internal static string? HoldReason(string dataHome)
    {
        if (!OperatingSystem.IsWindows() ||
            string.IsNullOrWhiteSpace(dataHome) ||
            !System.IO.Path.IsPathFullyQualified(dataHome))
            return "HOLD_OWNER_DATA_HOME";

        // An independently provisioned trust reader and a handle-relative
        // generation reader must be approved before any path returns null.
        return PendingTrust;
    }

    internal static bool IsReady(string dataHome) => HoldReason(dataHome) is null;
}
