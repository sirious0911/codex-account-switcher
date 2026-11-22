namespace CodexAccountSwitcher.Core;

/// <summary>
/// In-memory synthetic cross-device 5H/7D viewing candidates ONLY.
/// The explicit device allowlist is NOT registration or proof of identity.
/// There is no network, disk persistence, credentials, Sender permission or
/// Telegram connection. None of this is integrated into the operating UI.
/// </summary>
public sealed class QuotaViewV1MemoryCache
{
    private sealed record Slot(string? Raw,string AccountIdSha256,bool Quarantined);
    private readonly object guard=new();
    private readonly string[] allowed;
    private readonly Dictionary<string,Slot> slots=new(StringComparer.Ordinal);

    public QuotaViewV1MemoryCache(IEnumerable<string> allowedSourceDeviceIds)
    {
        ArgumentNullException.ThrowIfNull(allowedSourceDeviceIds);
        var list=allowedSourceDeviceIds.ToArray();
        if(list.Length is <1 or >4 ||
           list.Any(x=>string.IsNullOrEmpty(x) ||x.Length>48 ||
              x.Any(c=>!(c is >= 'A' and <= 'Z' or
                          >= 'a' and <= 'z' or
                          >= '0' and <= '9' or '-'))) ||
           list.Distinct(StringComparer.Ordinal).Count()!=list.Length)
            throw new ArgumentException("Expected 1..4 explicit test device IDs");
        allowed=list;
    }

    public QuotaViewV1Inspection Ingest(string? raw,long trustedNowUtcMs)
    {
        var initial=QuotaViewV1ReadOnlyReview.Inspect(raw,trustedNowUtcMs);
        if(initial.Status!="OFFLINE_VIEW_CANDIDATE_ONLY"||initial.View is null)
            return Hold(initial.Status);
        var candidate=initial.View;
        if(!Allowed(candidate.SourceDeviceId))
            return Hold("HOLD_UNKNOWN_SOURCE_DEVICE");
        lock(guard)
        {
            slots.TryGetValue(candidate.SourceDeviceId,out var prior);
            if(prior?.Quarantined==true)
                return Hold("HOLD_ACCOUNT_SWITCH_REQUIRES_LOCAL_RESET");
            if(prior is not null)
            {
                var checkedFrame=QuotaViewV1ReadOnlyReview.Inspect(
                    raw,trustedNowUtcMs,prior.Raw);
                if(checkedFrame.Status=="HOLD_ACCOUNT_IDENTITY_CHANGED")
                {
                    // Remove the old account view from UI candidates.
                    slots[candidate.SourceDeviceId]=new(
                        null,prior.AccountIdSha256,true);
                    return Hold("HOLD_ACCOUNT_SWITCH_REQUIRES_LOCAL_RESET");
                }
                if(checkedFrame.Status!="OFFLINE_VIEW_CANDIDATE_ONLY")
                    return Hold(checkedFrame.Status);
            }
            slots[candidate.SourceDeviceId]=new(
                raw!,candidate.AccountIdSha256,false);
            return new("OFFLINE_VIEW_STORED_CANDIDATE",candidate);
        }
    }

    public QuotaViewV1Inspection Read(string sourceDeviceId,long trustedNowUtcMs)
    {
        if(!Allowed(sourceDeviceId))
            return Hold("HOLD_UNKNOWN_SOURCE_DEVICE");
        lock(guard)
        {
            if(!slots.TryGetValue(sourceDeviceId,out var slot))
                return Hold("HOLD_NO_SOURCE_OBSERVATION");
            if(slot.Quarantined)
                return Hold("HOLD_ACCOUNT_SWITCH_REQUIRES_LOCAL_RESET");
            return QuotaViewV1ReadOnlyReview.Inspect(
                slot.Raw,trustedNowUtcMs);
        }
    }

    public QuotaViewV1Inspection ForgetSource(string sourceDeviceId,
        string? expectedCurrentAccountIdSha256)
    {
        if(!Allowed(sourceDeviceId))
            return Hold("HOLD_UNKNOWN_SOURCE_DEVICE");
        lock(guard)
        {
            if(!slots.TryGetValue(sourceDeviceId,out var slot))
                return Hold("HOLD_NO_SOURCE_OBSERVATION");
            if(slot.AccountIdSha256!=expectedCurrentAccountIdSha256)
                return Hold("HOLD_VIEW_RESET_IDENTITY_MISMATCH");
            slots.Remove(sourceDeviceId);
            return Hold("OFFLINE_VIEW_SLOT_CLEARED");
        }
    }

    public IReadOnlyList<QuotaViewV1SourceRow> ReadAll(long trustedNowUtcMs)
    {
        lock(guard)
        {
            return allowed.Select(id=>
                new QuotaViewV1SourceRow(id,Read(id,trustedNowUtcMs)))
                .ToArray();
        }
    }

    private bool Allowed(string? device)=>
        device is not null &&
        allowed.Contains(device,StringComparer.Ordinal);

    private static QuotaViewV1Inspection Hold(string status)=>
        new(status,null);
}

public sealed record QuotaViewV1SourceRow(
    string SourceDeviceId,QuotaViewV1Inspection Inspection);
