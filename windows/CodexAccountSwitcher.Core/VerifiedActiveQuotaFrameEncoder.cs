using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexAccountSwitcher.Core;

/// <summary>
/// Produces a privacy-minimized V1 5H/7D frame from an observed Windows
/// SwiftHost monitor event. No network, credentials, file system or Telegram.
/// Consumers must separately authorize and implement any remote transport.
/// </summary>
public sealed class VerifiedActiveQuotaFrameEncoder
{
    private readonly object sync=new();
    private readonly ActiveMonitorPublicationGate gate=new();
    private readonly string sourceDeviceId;
    private string sessionId=Guid.NewGuid().ToString("N");
    private string? sourceAccountHash;
    private long sequence;
    private long epochFloor;
    private long lastObserved;
    private bool snapshotConfirmed;
    private bool mixedAccountQuarantine;

    public VerifiedActiveQuotaFrameEncoder(string sourceDeviceId)
    {
        if(string.IsNullOrEmpty(sourceDeviceId) || sourceDeviceId.Length>48 ||
           !sourceDeviceId.All(c=>c is >= 'a' and <= 'z'
                or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-'))
            throw new ArgumentException("An enrolled source device ID is required.",
                nameof(sourceDeviceId));
        this.sourceDeviceId=sourceDeviceId;
    }

    public bool IsQuarantined
    {
        get { lock(sync)return mixedAccountQuarantine; }
    }

    public void ObserveSnapshot(AccountSnapshot snapshot,DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock(sync)
        {
            var old=gate.ActiveProfile;
            gate.Observe(snapshot);
            if(old!=gate.ActiveProfile)ResetEpoch(now.ToUnixTimeMilliseconds());
            var active=gate.ActiveProfile;
            snapshotConfirmed=!mixedAccountQuarantine &&
                active is Guid id &&
                snapshot.ActiveAccountID==id &&
                snapshot.ActiveIdentityConfirmed &&
                !snapshot.IsMutating && !snapshot.IsAddingAccount &&
                string.IsNullOrEmpty(snapshot.Error) &&
                snapshot.Accounts.Count(a=>a.Profile.Id==id)==1;
        }
    }

    public void SwitchRequested(DateTimeOffset now)
    {
        lock(sync)
        {
            gate.SwitchRequested();
            ResetEpoch(now.ToUnixTimeMilliseconds());
            snapshotConfirmed=false;
        }
    }

    /// <summary>
    /// A single fresh observation is consumed once. The account ID is hashed
    /// and cannot be reconstructed from the frame; no token, email or credit
    /// identifier is included. Invalid source/account changes fail closed.
    /// </summary>
    public bool TryBuild(CodexMonitorWireSample sample,DateTimeOffset now,
        out string? frame)
    {
        frame=null;
        lock(sync)
        {
            if(!snapshotConfirmed || mixedAccountQuarantine ||
               !gate.Allows(sample) || sequence>=1_000_000_000)
                return false;
            var observed=sample.ObservedAtMilliseconds;
            var clock=now.ToUnixTimeMilliseconds();
            if(observed<=epochFloor || observed<=lastObserved ||
               observed>clock+3000 || clock-observed>=360_000 ||
               observed<1_577_836_800_000 || observed>4_133_980_800_000)
                return false;
            if(!TryReadUsage(sample.RawJson,observed,
                   out var providerId,out var five,out var seven))
                return false;
            var accountHash=Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(providerId!))).ToLowerInvariant();
            if(sourceAccountHash is not null && sourceAccountHash!=accountHash)
            {
                mixedAccountQuarantine=true;
                snapshotConfirmed=false;
                return false;
            }
            var encoded=JsonSerializer.Serialize(new
            {
                schemaVersion=1,
                sourceDeviceId,
                sourceSessionId=sessionId,
                sequence=sequence+1,
                collectedAtUtcMs=observed,
                accountIdSha256=accountHash,
                fiveHour=new {
                    remainingPercent=five!.Value.remaining,
                    resetAtUtcMs=five.Value.reset
                },
                sevenDay=new {
                    remainingPercent=seven!.Value.remaining,
                    resetAtUtcMs=seven.Value.reset
                },
                actualSendAuthorized=false,
                automaticRetryAllowed=false,
                productionReservationAuthorized=false,
                senderTransitionAuthorized=false
            });
            if(Encoding.UTF8.GetByteCount(encoded)>2048)return false;

            // In-process local identity and sequence are committed only after
            // all input/shape checks succeed. Transport remains separate.
            sourceAccountHash=accountHash;
            sequence++;
            lastObserved=observed;
            frame=encoded;
            return true;
        }
    }

    private void ResetEpoch(long now)
    {
        sourceAccountHash=null;
        mixedAccountQuarantine=false;
        sessionId=Guid.NewGuid().ToString("N");
        sequence=0;
        epochFloor=Math.Max(epochFloor,now);
        lastObserved=epochFloor;
    }

    private static bool TryReadUsage(string? raw,long observed,
        out string? account,
        out (int? remaining,long? reset)? five,
        out (int? remaining,long? reset)? seven)
    {
        account=null;five=null;seven=null;
        if(raw is null || Encoding.UTF8.GetByteCount(raw)>8192)
            return false;
        try
        {
            using var document=JsonDocument.Parse(raw,new JsonDocumentOptions {
                MaxDepth=10,AllowTrailingCommas=false,
                CommentHandling=JsonCommentHandling.Disallow
            });
            var root=document.RootElement;
            if(!NoDuplicateKeys(root,0) ||
               root.ValueKind!=JsonValueKind.Object ||
               !root.TryGetProperty("accountId",out var id) ||
               id.ValueKind!=JsonValueKind.String ||
               id.GetString() is not string name ||
               string.IsNullOrWhiteSpace(name) ||
               Encoding.UTF8.GetByteCount(name)>256 ||
               name.Any(char.IsControl) ||
               !root.TryGetProperty("rateLimitsByLimitId",out var limits) ||
               limits.ValueKind!=JsonValueKind.Object ||
               !limits.TryGetProperty("codex",out var codex) ||
               codex.ValueKind!=JsonValueKind.Object ||
               !ReadWindow(codex,"primary",300,observed,out five) ||
               !ReadWindow(codex,"secondary",10080,observed,out seven) ||
               (five!.Value.remaining is null &&
                seven!.Value.remaining is null))
                return false;
            account=name;
            return true;
        }
        catch(Exception e) when(e is JsonException or InvalidOperationException
              or ArgumentException or OverflowException)
        {
            return false;
        }
    }

    private static bool ReadWindow(JsonElement codex,string key,int minutes,
        long observed,out (int? remaining,long? reset)? window)
    {
        window=null;
        if(!codex.TryGetProperty(key,out var raw) ||
           raw.ValueKind==JsonValueKind.Null)
        {
            window=(null,null);
            return true;
        }
        if(raw.ValueKind!=JsonValueKind.Object ||
           !raw.TryGetProperty("windowDurationMins",out var duration) ||
           !duration.TryGetInt32(out var length) || length!=minutes ||
           !raw.TryGetProperty("usedPercent",out var used))
            return false;
        if(used.ValueKind==JsonValueKind.Null)
        {
            window=(null,null);
            return true;
        }
        if(used.ValueKind!=JsonValueKind.Number ||
           !used.TryGetDouble(out var percentage) ||
           !double.IsFinite(percentage) ||
           percentage<0 || percentage>100)
            return false;
        long? reset=null;
        if(raw.TryGetProperty("resetsAt",out var time) &&
           time.ValueKind!=JsonValueKind.Null)
        {
            if(!time.TryGetInt64(out var seconds) ||
               seconds<1_577_836_800 || seconds>4_133_980_800)
                return false;
            reset=checked(seconds*1000);
            var maxAhead=minutes==300?21_600_000L:691_200_000L;
            if(reset<observed-600_000 || reset>observed+maxAhead)
                return false;
        }
        window=((int)Math.Round(100-percentage,0,
            MidpointRounding.AwayFromZero),reset);
        return true;
    }

    private static bool NoDuplicateKeys(JsonElement value,int depth)
    {
        if(depth>10)return false;
        if(value.ValueKind==JsonValueKind.Object)
        {
            var names=new HashSet<string>(StringComparer.Ordinal);
            foreach(var field in value.EnumerateObject())
                if(!names.Add(field.Name) ||
                   !NoDuplicateKeys(field.Value,depth+1))
                    return false;
        }
        else if(value.ValueKind==JsonValueKind.Array)
        {
            foreach(var element in value.EnumerateArray())
                if(!NoDuplicateKeys(element,depth+1))
                    return false;
        }
        return true;
    }
}
