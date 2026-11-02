namespace CodexAccountSwitcher.Core;

/// <summary>
/// Selection boundary for the external minibar. The regular multi-profile
/// usage refresh and local monitor keep their existing independent behavior.
/// </summary>
public sealed class ActiveMonitorPublicationGate
{
    private Guid? activeProfile;
    private bool switchPending;
    private bool sawSwitchMutation;

    public Guid? ActiveProfile => activeProfile;

    /// <summary>Fail closed immediately on the user's switch request, before host roundtrip.</summary>
    public void SwitchRequested()
    {
        switchPending = true;
        sawSwitchMutation = false;
        activeProfile = null;
    }

    /// <summary>
    /// True means a pipe epoch reset/disconnect is required. Once switching
    /// begins, the gate remains closed until the host reports completion.
    /// </summary>
    public bool Observe(AccountSnapshot snapshot)
    {
        if (switchPending)
        {
            if (snapshot.IsMutating) sawSwitchMutation = true;
            if (sawSwitchMutation && !snapshot.IsMutating) switchPending = false;
        }

        // Host transport errors leave the last snapshot's active identity intact.
        // Fail closed so a terminated SwiftHost cannot leave stale quota visible.
        Guid? next = !switchPending && !snapshot.IsMutating &&
            snapshot.ActiveIdentityConfirmed && string.IsNullOrEmpty(snapshot.Error)
            ? snapshot.ActiveAccountID : null;
        var changed = next != activeProfile;
        activeProfile = next;
        return changed;
    }

    public bool Allows(CodexMonitorWireSample sample) =>
        activeProfile.HasValue && sample.ProfileID.HasValue &&
        sample.ProfileID.Value == activeProfile.Value && !switchPending;
}
