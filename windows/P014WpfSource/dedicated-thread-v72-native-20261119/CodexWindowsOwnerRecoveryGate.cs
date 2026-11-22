using System;
using System.IO;
using System.Linq;
using DedicatedThreadCycleStarter;

namespace CodexAccountSwitcher;

/// <summary>
/// Bounded restart policy for the user's existing WPF login-start process.
/// Never starts a new Windows service, registry autorun, conversation or sender.
/// Config and account proof are independently required by TryStart.
/// Restarting the process cannot retry any previously reserved Codex event.
/// </summary>
public sealed class CodexWindowsOwnerRecoveryGate
{
    private DateTimeOffset? firstAttempt;
    private DateTimeOffset? lastAttempt;
    private int attempts;
    public int Attempts => attempts;

    // A saved user master-OFF must stop the optional Owner completely.
    // Read the public draft + previously selected existing-thread binding;
    // neither file grants permission to send without separate operator setup.
    public static bool HasActiveUserLane(string dataHome,Guid profile)
    {
        if(profile==Guid.Empty || string.IsNullOrWhiteSpace(dataHome) ||
           !Path.IsPathFullyQualified(dataHome))return false;
        try
        {
            var path=Path.Combine(dataHome,
                "dedicated-existing-thread-bindings-v1.json");
            var rows=new DedicatedThreadBindingStore(path)
                .ReadAll().Where(x=>x.ProfileId==profile).ToArray();
            if(rows.Length!=1)return false;
            var binding=rows[0];
            if(binding.ThreadStorage!="desktop-default" ||
               binding.Message!="안녕" || !binding.AutomaticMessagesEnabled)
                return false;
            var draft=new CodexAutoSendDraftStore(dataHome)
                .ReadFor(binding);
            return draft is not null && draft.WantAutomatic &&
                ((draft.WantFiveHour && binding.StartOnFiveHour) ||
                 (draft.WantSevenDay && binding.StartOnSevenDay));
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException
            or InvalidDataException or InvalidOperationException
            or System.Text.Json.JsonException or System.Security.SecurityException)
        {return false;}
    }

    public static bool ShouldDeferDue(bool ownerConfigured,bool ownerRunning)
        => ownerConfigured && !ownerRunning;

    public bool CanAttempt(DateTimeOffset now,bool configured,
        bool ownerRunning,bool exiting,bool accountConfirmed)
    {
        if(!configured || ownerRunning || exiting || !accountConfirmed)
            return false;
        if(firstAttempt is { } start &&
            now - start >= TimeSpan.FromHours(1))
        {
            firstAttempt=null;lastAttempt=null;attempts=0;
        }
        // At most 3 restarts per hour, separated by >=30 seconds.
        if(attempts>=3)return false;
        if(lastAttempt is { } last &&
           now - last < TimeSpan.FromSeconds(30))
            return false;
        return true;
    }

    public void RecordAttempt(DateTimeOffset now)
    {
        if(firstAttempt is null)firstAttempt=now;
        lastAttempt=now;attempts++;
    }

    public void ResetAfterAccountChange()
    {
        // Account changes stop the old owner immediately. A new owner still
        // requires independent configured profile and current account proof.
        firstAttempt=null;lastAttempt=null;attempts=0;
    }
}
