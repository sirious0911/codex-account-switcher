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

    // Diagnostic-only native protected-source observation. Even if HKLM and
    // the ProgramData HEAD are present and their ACLs pass the OS checks,
    // this DOES NOT validate the dual Ed25519 signatures, P0-12 enrollment,
    // rollback floor acceptance, user DPAPI or the exclusive Sender lease.
    // Never use a positive source-presence observation for admission.
    internal static string ProtectedTrustSourceStatus()
    {
        var observation = WindowsP015AdminTrustSource.Probe();
        return ProtectedSourceHoldStatus(observation.IsAvailable,
            observation.Code);
    }

    // Pure mapping allows an in-memory positive-source fixture to verify
    // that the OS *presence* of public inputs never unlocks the Owner.
    internal static string ProtectedSourceHoldStatus(bool inputsPresent,
        string? sourceCode)
    {
        if (inputsPresent)
            return "HOLD_OWNER_SIGNED_HEAD_AND_ENROLLMENT_NOT_ADMITTED";
        return sourceCode?.StartsWith("HOLD_P015_", StringComparison.Ordinal)
            is true ? sourceCode : PendingTrust;
    }

    internal static bool IsReady(string dataHome) => HoldReason(dataHome) is null;
}
