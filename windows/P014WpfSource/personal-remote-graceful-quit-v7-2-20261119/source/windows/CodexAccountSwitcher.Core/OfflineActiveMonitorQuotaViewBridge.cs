using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexAccountSwitcher.Core;

/// <summary>
/// OFFLINE test-only bridge from the EXISTING SwiftHost monitor sample to
/// the untrusted V1 viewer cache. Never attached to App.xaml.cs, CoreClient,
/// a transport, account switching, Sender mode, or the real taskbar.
/// The HMAC key MUST be fake synthetic test material, never an operating key.
/// </summary>
public sealed class OfflineActiveMonitorQuotaViewBridge
{
    private readonly object guard=new();
    private readonly string sourceDeviceId;
    private readonly byte[] syntheticIdentityKey;
    private readonly ActiveMonitorPublicationGate gate=new();
    private readonly QuotaViewV1MemoryCache cache;
    private Guid? currentProfile;
    private string? currentSession;
    private string? sourceAccountHash;
    private long sequence;
    private long lastObservedAtMs;
    private long epochFloorMs;
    private bool providerIdentityQuarantined;
    private bool activeNonWeekly;
    private bool snapshotReady;

    private static readonly Regex SourceRegex=new(
        "^[A-Za-z0-9-]{1,48}$",RegexOptions.CultureInvariant);
    private static readonly Regex SessionRegex=new(
        "^[a-f0-9]{32}$",RegexOptions.CultureInvariant);
    private static readonly Regex HexKeyRegex=new(
        "^[a-f0-9]{64}$",RegexOptions.CultureInvariant);

    public OfflineActiveMonitorQuotaViewBridge(
        string sourceDeviceId,string syntheticTestIdentityKeyHex)
    {
        if(sourceDeviceId is null||!SourceRegex.IsMatch(sourceDeviceId)||
           syntheticTestIdentityKeyHex is null||
           !HexKeyRegex.IsMatch(syntheticTestIdentityKeyHex))
            throw new ArgumentException("Only explicit synthetic source/key inputs");
        this.sourceDeviceId=sourceDeviceId;
        syntheticIdentityKey=Convert.FromHexString(syntheticTestIdentityKeyHex);
        cache=new QuotaViewV1MemoryCache([sourceDeviceId]);
    }

    public OfflineMonitorViewResult BeginSwitch(long trustedNowUtcMs)
    {
        lock(guard)
        {
            gate.SwitchRequested();
            ClearEpoch(trustedNowUtcMs);
            return Hold("HOLD_LOCAL_SWITCH_PENDING");
        }
    }

    /// <summary>
    /// Feed a test AccountSnapshot exactly as a real App.OnClientChanged
    /// would call the existing gate. No account or credential is read.
    /// The caller must supply an explicit new synthetic session ID after
    /// a legitimate active-profile transition.
    /// </summary>
    public OfflineMonitorViewResult ObserveSnapshot(
        AccountSnapshot snapshot,long trustedNowUtcMs,string syntheticSessionId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock(guard)
        {
            // A previously valid profile may become temporarily unsafe
            // without changing the gate's active GUID (e.g. IsAddingAccount).
            // Do not keep accepting samples from the old snapshot state.
            snapshotReady=false;
            bool changed=gate.Observe(snapshot);
            if(changed)ClearEpoch(trustedNowUtcMs);

            if(!ValidTime(trustedNowUtcMs)||
               syntheticSessionId is null||
               !SessionRegex.IsMatch(syntheticSessionId))
                return Hold("HOLD_INVALID_SYNTHETIC_SESSION_OR_TIME");

            Guid? active=gate.ActiveProfile;
            if(active is null || !snapshot.ActiveIdentityConfirmed||
               snapshot.IsMutating||snapshot.IsAddingAccount||
               !string.IsNullOrEmpty(snapshot.Error) ||
               snapshot.ActiveAccountID!=active ||
               snapshot.Accounts.Count(a=>a.Profile.Id==active.Value)!=1)
                return Hold("HOLD_ACTIVE_PROFILE_NOT_CONFIRMED");

            if(currentProfile is not null&&currentProfile==active&&
               currentSession!=syntheticSessionId)
                return Hold("HOLD_SESSION_REBIND_WITHOUT_PROFILE_EPOCH");

            if(currentProfile is null)
            {
                currentProfile=active;
                currentSession=syntheticSessionId;
                epochFloorMs=Math.Max(epochFloorMs,trustedNowUtcMs);
                lastObservedAtMs=epochFloorMs;
            }
            activeNonWeekly=snapshot.Accounts.Single(a=>
                a.Profile.Id==active.Value).UsageStatus=="nonWeekly";
            if(providerIdentityQuarantined)
                return Hold("HOLD_PROVIDER_ACCOUNT_MIXED_IN_ACTIVE_EPOCH");
            snapshotReady=true;
            return Hold("OFFLINE_ACTIVE_PROFILE_READY_NO_SEND");
        }
    }

    public OfflineMonitorViewResult TryIngest(
        CodexMonitorWireSample sample,long trustedNowUtcMs)
    {
        ArgumentNullException.ThrowIfNull(sample);
        lock(guard)
        {
            if(!snapshotReady||currentProfile is null||currentSession is null||
               providerIdentityQuarantined||!gate.Allows(sample))
                return Hold("HOLD_PROFILE_SWITCH_OR_IDENTITY");
            if(!ValidTime(trustedNowUtcMs)||
               sample.ObservedAtMilliseconds<=epochFloorMs||
               sample.ObservedAtMilliseconds<=lastObservedAtMs||
               sample.ObservedAtMilliseconds>trustedNowUtcMs+3000 ||
               trustedNowUtcMs-sample.ObservedAtMilliseconds>=360000)
                return Hold("HOLD_OLD_OR_REORDERED_MONITOR_SAMPLE");
            if(sequence>=1000000000)
                return Hold("HOLD_VIEW_SEQUENCE_EXHAUSTED");

            if(activeNonWeekly)
                return Hold("HOLD_NON_WEEKLY_BUCKET_SEPARATE");
            ParsedMonitor? parsed=Parse(sample.RawJson,sample.ObservedAtMilliseconds);
            if(parsed is null)return Hold("HOLD_INVALID_MONITOR_FRAME");
            if(parsed.NonWeekly)
                return Hold("HOLD_NON_WEEKLY_BUCKET_SEPARATE");
            if(parsed.Five.RemainingPercent is null&&
               parsed.Seven.RemainingPercent is null)
                return Hold("HOLD_NO_WEEKLY_OR_FIVE_HOUR_USAGE");

            var accountHash=Convert.ToHexString(HMACSHA256.HashData(
                syntheticIdentityKey,Encoding.UTF8.GetBytes(
                    "SYNTHETIC_VIEW_ACCOUNT_V1\n"+parsed.SourceAccountId
                ))).ToLowerInvariant();
            if(sourceAccountHash is not null &&
               !string.Equals(sourceAccountHash,accountHash,StringComparison.Ordinal))
            {
                // Different provider account for the SAME active profile:
                // quarantine until a normal active-profile epoch transition.
                providerIdentityQuarantined=true;
                return Hold("HOLD_PROVIDER_ACCOUNT_MIXED_IN_ACTIVE_EPOCH");
            }
            var raw=JsonSerializer.Serialize(new
            {
                schemaVersion=1,sourceDeviceId,
                sourceSessionId=currentSession,
                sequence=sequence+1,
                collectedAtUtcMs=sample.ObservedAtMilliseconds,
                accountIdSha256=accountHash,
                fiveHour=new
                {
                    remainingPercent=parsed.Five.RemainingPercent,
                    resetAtUtcMs=parsed.Five.ResetAtUtcMs
                },
                sevenDay=new
                {
                    remainingPercent=parsed.Seven.RemainingPercent,
                    resetAtUtcMs=parsed.Seven.ResetAtUtcMs
                },
                actualSendAuthorized=false,automaticRetryAllowed=false,
                productionReservationAuthorized=false,
                senderTransitionAuthorized=false
            });
            var reviewed=QuotaViewV1ReadOnlyReview.Inspect(raw,trustedNowUtcMs);
            if(reviewed.Status!="OFFLINE_VIEW_CANDIDATE_ONLY")
                return Hold(reviewed.Status);
            var stored=cache.Ingest(raw,trustedNowUtcMs);
            if(stored.Status!="OFFLINE_VIEW_STORED_CANDIDATE")
                return Hold(stored.Status);

            sourceAccountHash=accountHash;
            sequence++;
            lastObservedAtMs=sample.ObservedAtMilliseconds;
            return new("OFFLINE_ACTIVE_MONITOR_VIEW_STORED_CANDIDATE",raw);
        }
    }

    public QuotaViewV1Inspection ReadView(long trustedNowUtcMs)
    {
        lock(guard)
        {
            if(!snapshotReady||providerIdentityQuarantined||
               currentProfile is null||currentSession is null)
                return new("HOLD_PROFILE_SWITCH_OR_IDENTITY",null);
            return cache.Read(sourceDeviceId,trustedNowUtcMs);
        }
    }

    private void ClearEpoch(long trustedNowUtcMs)
    {
        if(sourceAccountHash is not null)
            cache.ForgetSource(sourceDeviceId,sourceAccountHash);
        sourceAccountHash=null;
        currentProfile=null;currentSession=null;
        providerIdentityQuarantined=false;
        activeNonWeekly=false;
        snapshotReady=false;
        sequence=0;
        if(ValidTime(trustedNowUtcMs))
            epochFloorMs=Math.Max(epochFloorMs,trustedNowUtcMs);
        lastObservedAtMs=epochFloorMs;
    }

    private sealed record Quota(int? RemainingPercent,long? ResetAtUtcMs);
    private sealed record ParsedMonitor(
        string SourceAccountId,Quota Five,Quota Seven,bool NonWeekly);

    private static bool ValidTime(long ms)=>
        ms>=1577836800000 && ms<=4133980800000;

    private static ParsedMonitor? Parse(string? raw,long observed)
    {
        if(raw is null||Encoding.UTF8.GetByteCount(raw)>8192)return null;
        try
        {
            using var doc=JsonDocument.Parse(raw,new JsonDocumentOptions
            {
                MaxDepth=10,AllowTrailingCommas=false,
                CommentHandling=JsonCommentHandling.Disallow
            });
            var root=doc.RootElement;
            if(root.ValueKind!=JsonValueKind.Object||
               !UniqueProperties(root,0))
                return null;
            if(!root.TryGetProperty("accountId",out var account)||
               account.ValueKind!=JsonValueKind.String)
                return null;
            var id=account.GetString();
            if(string.IsNullOrWhiteSpace(id)||id.Length>256)return null;
            var limit=root.GetProperty("rateLimitsByLimitId")
                .GetProperty("codex");
            var five=ParseWindow(limit,"primary",300,observed,out var nonWeekly5);
            var seven=ParseWindow(limit,"secondary",10080,observed,
                out var nonWeekly7);
            if(five is null||seven is null)return null;
            // The current UI's existing preview parser intentionally
            // rejects 30D as a 7D window. Keep that as a distinct HOLD,
            // never silently relabel a Free/30D quota.
            if(!nonWeekly5&&!nonWeekly7 &&
               (!MonitorSyntheticPreview.TryRead(raw,out var preview)||
                preview is null))
                return null;
            return new(id!,five,seven,nonWeekly5||nonWeekly7);
        }
        catch(Exception ex) when(ex is JsonException or InvalidOperationException or
            KeyNotFoundException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    private static Quota? ParseWindow(JsonElement parent,string name,
        int expectedMinutes,long observed,out bool nonWeekly)
    {
        nonWeekly=false;
        if(!parent.TryGetProperty(name,out var item)||
           item.ValueKind==JsonValueKind.Null)
            return new(null,null);
        if(item.ValueKind!=JsonValueKind.Object||
           !item.TryGetProperty("windowDurationMins",out var d)||
           !d.TryGetInt32(out var minutes))
            return null;
        if(minutes!=expectedMinutes)
        {
            if(minutes==43200)
            {
                nonWeekly=true;
                return new(null,null);
            }
            return null;
        }
        if(!item.TryGetProperty("usedPercent",out var pct)||
           pct.ValueKind==JsonValueKind.Null)
            return new(null,null);
        if(pct.ValueKind!=JsonValueKind.Number||
           !pct.TryGetDouble(out double used)||!double.IsFinite(used)||
           used<0||used>100)
            return null;
        long? reset=null;
        if(item.TryGetProperty("resetsAt",out var time)&&
           time.ValueKind!=JsonValueKind.Null)
        {
            if(!time.TryGetInt64(out long seconds)||
               seconds<1577836800||seconds>4133980800)
                return null;
            reset=checked(seconds*1000);
            long ahead=expectedMinutes==300?21600000L:691200000L;
            if(reset<observed-600000||reset>observed+ahead)
                return null;
        }
        return new((int)Math.Round(100-used,0,MidpointRounding.AwayFromZero),
            reset);
    }

    private static bool UniqueProperties(JsonElement element,int depth)
    {
        if(depth>10)return false;
        if(element.ValueKind==JsonValueKind.Object)
        {
            var names=new HashSet<string>(StringComparer.Ordinal);
            foreach(var p in element.EnumerateObject())
                if(!names.Add(p.Name)||!UniqueProperties(p.Value,depth+1))
                    return false;
        }
        else if(element.ValueKind==JsonValueKind.Array)
        {
            foreach(var child in element.EnumerateArray())
                if(!UniqueProperties(child,depth+1))return false;
        }
        return true;
    }

    private static OfflineMonitorViewResult Hold(string status)=>new(status,null);
}

public sealed record OfflineMonitorViewResult(string Status,string? ViewJson)
{
    public bool ActualSendAuthorized=>false;
    public bool AutomaticRetryAllowed=>false;
    public bool SenderTransitionAuthorized=>false;
    public bool ProductionReservationAuthorized=>false;
    public bool RealDeviceAuthenticated=>false;
    public bool RealCrossHostSyncAuthorized=>false;
    public bool LiveMiniBarConnected=>false;
}
