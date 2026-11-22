using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexAccountSwitcher;

/// <summary>
/// Current-user Node owner script set. A mutable entrypoint .mjs alone is not
/// sufficient: inspect and hold all authority/*.mjs and offline/*.mjs files
/// for the entire child lifetime. Expected digest must come from an
/// independently reviewed provision; never compute and auto-approve.
/// </summary>
public sealed class WindowsReviewedOwnerJsBundle : IDisposable
{
    private sealed record Row(string file,string sha256);
    private readonly List<FileStream> held=new();
    private bool disposed;
    private WindowsReviewedOwnerJsBundle(){ }
    private static readonly Regex Sha=new("^[a-f0-9]{64}$",
        RegexOptions.CultureInvariant);
    private static readonly Regex Name=new(
        "^[A-Za-z0-9][A-Za-z0-9._-]{0,130}\\.mjs$",
        RegexOptions.CultureInvariant);

    public static WindowsReviewedOwnerJsBundle Acquire(
        string ownerMainPath,string expectedSha256)
    {
        if(!OperatingSystem.IsWindows() ||
           string.IsNullOrWhiteSpace(ownerMainPath) ||
           !Path.IsPathFullyQualified(ownerMainPath) ||
           !string.Equals(Path.GetFileName(ownerMainPath),
              "codex-windows-owner-main.mjs",StringComparison.Ordinal) ||
           !Sha.IsMatch(expectedSha256??""))
            throw new InvalidOperationException("Unreviewed Owner script anchor");
        var root=Directory.GetParent(Path.GetDirectoryName(
            Path.GetFullPath(ownerMainPath))!)?.FullName
            ?? throw new InvalidOperationException("Owner root absent");
        var candidate=new WindowsReviewedOwnerJsBundle();
        try
        {
            var all=new List<Row>();
            foreach(var folder in new[]{"authority","offline"})
            {
                var directory=Path.Combine(root,folder);
                var dir=new DirectoryInfo(directory);
                if(!dir.Exists || dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidOperationException("Unreviewed Owner directory");
                var files=Directory.GetFileSystemEntries(directory);
                if(files.Length<1 || files.Length>40)
                    throw new InvalidOperationException("Unreviewed Owner file count");
                foreach(var path in files)
                {
                    var name=Path.GetFileName(path);
                    var info=new FileInfo(path);
                    if(!Name.IsMatch(name) || !info.Exists ||
                       info.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                       info.Length<1 || info.Length>5*1024*1024)
                        throw new InvalidOperationException("Unreviewed Owner source");
                    var file=new FileStream(path,FileMode.Open,
                        FileAccess.Read,FileShare.Read);
                    candidate.held.Add(file);
                    var fileHash=Convert.ToHexStringLower(SHA256.HashData(file));
                    file.Position=0;
                    if(info.Length!=file.Length)
                        throw new InvalidOperationException("Owner file modified");
                    all.Add(new Row(folder+"/"+name,fileHash));
                }
                if(!files.Select(Path.GetFileName).OrderBy(x=>x,StringComparer.Ordinal)
                       .SequenceEqual(Directory.GetFileSystemEntries(directory)
                           .Select(Path.GetFileName)
                           .OrderBy(x=>x,StringComparer.Ordinal),
                           StringComparer.Ordinal))
                    throw new InvalidOperationException("Owner directory modified");
            }
            all.Sort((a,b)=>StringComparer.Ordinal.Compare(a.file,b.file));
            if(!all.Any(x=>x.file=="authority/codex-windows-owner-main.mjs") ||
               !all.Any(x=>x.file=="offline/strict-json-frame.mjs"))
                throw new InvalidOperationException("Required Owner source missing");
            var manifest=JsonSerializer.Serialize(all);
            var digest=SHA256.HashData(Encoding.UTF8.GetBytes(manifest));
            if(!CryptographicOperations.FixedTimeEquals(digest,
                Convert.FromHexString(expectedSha256!)))
                throw new InvalidOperationException("Reviewed Owner JS bundle mismatch");
            return candidate;
        }
        catch {candidate.Dispose();throw;}
    }

    public void Dispose()
    {
        if(disposed)return;
        disposed=true;
        foreach(var stream in held)stream.Dispose();
        held.Clear();
    }
}
