using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using DedicatedThreadCycleStarter;

namespace CodexAccountSwitcher;

/// <summary>
/// Persisted UI preference DRAFT only. It is NOT an operational send grant,
/// and never changes the three enabled flags in DedicatedChatSettings.
/// </summary>
public sealed record CodexAutoSendDraft(
    Guid ProfileId,
    string AccountSha256,
    string ExistingThreadId,
    bool WantFiveHour,
    bool WantSevenDay,
    int SchemaVersion = 1,
    bool WantAutomatic = false)
{
    // A preference for a trusted due lane is still NOT a live-send grant.
    public bool WantsLane(LimitLane lane) =>
        WantAutomatic && (lane == LimitLane.FiveHour
            ? WantFiveHour : lane == LimitLane.SevenDay && WantSevenDay);
    public bool ActualSendEnabled => false;
}
public sealed class CodexAutoSendDraftStore
{
    private readonly string path;
    private static readonly Regex Hex64 = new(
        "^[a-f0-9]{64}$", RegexOptions.CultureInvariant);
    public CodexAutoSendDraftStore(string dataHome)
    {
        if (string.IsNullOrWhiteSpace(dataHome) ||
            !Path.IsPathFullyQualified(dataHome))
            throw new ArgumentException("Absolute app data root required");
        var directory = new DirectoryInfo(Path.GetFullPath(dataHome));
        if (directory.Exists &&
            directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("Untrusted app data root");
        path=Path.Combine(directory.FullName,
            "dedicated-autostart-draft-v1.json");
    }
    public CodexAutoSendDraft? ReadFor(DedicatedChatSettings binding)
    {
        CheckBinding(binding);
        var rows = Load();
        var matched=rows.Where(x=>x.ProfileId==binding.ProfileId).ToArray();
        if(matched.Length==0) return null;
        if(matched.Length!=1 || !Valid(matched[0]) ||
           matched[0].AccountSha256!=binding.AccountSha256 ||
           matched[0].ExistingThreadId!=binding.ExistingThreadId)
            throw new InvalidDataException("Draft cannot override or rebind a chat");
        return matched[0];
    }
    public CodexAutoSendDraft SaveDraft(
        DedicatedChatSettings binding, bool wantFiveHour,
        bool wantSevenDay, bool wantAutomatic = false)
    {
        CheckBinding(binding);
        var record=new CodexAutoSendDraft(binding.ProfileId,
            binding.AccountSha256,binding.ExistingThreadId,
            wantFiveHour,wantSevenDay,WantAutomatic:wantAutomatic);
        // Separate file and never write the active binding or a sender key.
        // The draft alone has no authorizing effect and is safe even if ON.
        var directory=Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        using var fileLock=new FileStream(path+".lock",
            FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        var all=Load();
        if(all.Any(x=>!Valid(x)) ||
           all.GroupBy(x=>x.ProfileId).Any(group=>group.Count()!=1))
            throw new InvalidDataException("Previous draft state invalid");
        var existing=all.FindIndex(x=>x.ProfileId==binding.ProfileId);
        if(existing>=0)all[existing]=record;
        else all.Add(record);
        var staged=path+".pending-"+Guid.NewGuid().ToString("N");
        try {
            File.WriteAllText(staged,JsonSerializer.Serialize(all,
                new JsonSerializerOptions{WriteIndented=true}));
            File.Move(staged,path,overwrite:true);
        }finally {
            if(File.Exists(staged))File.Delete(staged);
        }
        return record;
    }
    private List<CodexAutoSendDraft> Load()
    {
        if(!File.Exists(path))return [];
        var loaded=JsonSerializer.Deserialize<List<CodexAutoSendDraft>>(
            File.ReadAllText(path));
        if(loaded is null ||
           loaded.Count>100 ||
           loaded.Any(x=>!Valid(x)))
            throw new InvalidDataException("Invalid draft state");
        return loaded;
    }
    private static bool Valid(CodexAutoSendDraft? row)=>row is not null &&
        row.SchemaVersion==1 && row.ProfileId!=Guid.Empty &&
        Hex64.IsMatch(row.AccountSha256??"") &&
        Guid.TryParseExact(row.ExistingThreadId,"D",out _);
    private static void CheckBinding(DedicatedChatSettings binding)
    {
        if(binding is null || binding.ProfileId==Guid.Empty ||
           !Hex64.IsMatch(binding.AccountSha256??"") ||
           !Guid.TryParseExact(binding.ExistingThreadId,"D",out _) ||
           binding.ThreadStorage!="desktop-default" ||
           binding.Message!="안녕")
            throw new InvalidOperationException("Exact pinned Codex Desktop chat required");
    }
}
