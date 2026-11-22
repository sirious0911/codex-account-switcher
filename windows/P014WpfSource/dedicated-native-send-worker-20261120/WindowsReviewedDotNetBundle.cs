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
/// Hash-pins the ENTIRE reviewed, flat .NET worker directory, not merely the
/// native apphost .exe. Holds all reviewed file handles without FILE_SHARE_WRITE
/// or FILE_SHARE_DELETE throughout the supervised Windows Job lifetime.
/// This prevents normal Windows replacement of the DLL while the worker runs.
/// The expected SHA256 must be supplied by the trusted authority, independently
/// of the writable directory. No runtime "trust current files" fallback.
/// </summary>
public sealed class WindowsReviewedDotNetBundle : IDisposable
{
    private const int MaximumFiles=80;
    private const long MaximumFileSize=120L*1024*1024;
    private const long MaximumTotalBytes=300L*1024*1024;
    private static readonly Regex SafeFileName=new(
        "^[A-Za-z0-9][A-Za-z0-9._-]{0,150}$",
        RegexOptions.CultureInvariant|RegexOptions.Compiled);
    private static readonly Regex Sha256Hex=new(
        "^[a-f0-9]{64}$",RegexOptions.CultureInvariant|RegexOptions.Compiled);
    private sealed record Entry(string file,string sha256);

    private readonly List<FileStream> held=new();
    private bool disposed;
    private WindowsReviewedDotNetBundle(){}

    public static WindowsReviewedDotNetBundle Acquire(
        string reviewedExecutable,string expectedBundleSha256)
    {
        if(!OperatingSystem.IsWindows() ||
           string.IsNullOrWhiteSpace(reviewedExecutable) ||
           !Path.IsPathFullyQualified(reviewedExecutable) ||
           !Sha256Hex.IsMatch(expectedBundleSha256??""))
            throw new InvalidOperationException("Reviewed bundle anchor absent");
        var exe=Path.GetFullPath(reviewedExecutable);
        var root=Path.GetDirectoryName(exe)!;
        var dir=new DirectoryInfo(root);
        if(!dir.Exists || dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("Untrusted bundle directory");

        var opened=new WindowsReviewedDotNetBundle();
        try
        {
            var entries=Directory.GetFileSystemEntries(root).Select(
                Path.GetFileName).ToArray();
            Array.Sort(entries,StringComparer.Ordinal);
            if(entries.Length<3 || entries.Length>MaximumFiles ||
               !entries.Contains(Path.GetFileName(exe),StringComparer.Ordinal))
                throw new InvalidOperationException("Unexpected bundle content");

            var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total=0;
            var manifest=new List<Entry>();
            foreach(var name in entries)
            {
                if(string.IsNullOrWhiteSpace(name) ||
                   !SafeFileName.IsMatch(name) || !seen.Add(name))
                    throw new InvalidOperationException("Untrusted bundle filename");
                var file=new FileInfo(Path.Combine(root,name));
                if(!file.Exists || file.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                   file.Length<0 || file.Length>MaximumFileSize)
                    throw new InvalidOperationException("Untrusted bundle file");
                total+=file.Length;
                if(total>MaximumTotalBytes)
                    throw new InvalidOperationException("Oversize bundle");
                // Hold each file with FileShare.Read only: no replacement,
                // deletion, or writes can succeed while the Job is active.
                var handle=new FileStream(file.FullName,FileMode.Open,
                    FileAccess.Read,FileShare.Read);
                opened.held.Add(handle);
                var sha=Convert.ToHexStringLower(SHA256.HashData(handle));
                handle.Position=0;
                if(file.Length!=handle.Length)
                    throw new InvalidOperationException("Changed bundle file");
                manifest.Add(new Entry(name,sha));
            }
            // Catch additions/deletions between initial enumeration and all
            // holds. Deployment must additionally prevent directory renaming.
            var after=Directory.GetFileSystemEntries(root).Select(
                Path.GetFileName).OrderBy(x=>x,StringComparer.Ordinal).ToArray();
            if(!entries.SequenceEqual(after,StringComparer.Ordinal))
                throw new InvalidOperationException("Bundle directory changed");
            var canonical=JsonSerializer.Serialize(manifest);
            var digest=SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
            if(!CryptographicOperations.FixedTimeEquals(digest,
                Convert.FromHexString(expectedBundleSha256!)))
                throw new InvalidOperationException("Reviewed bundle SHA256 mismatch");
            return opened;
        }
        catch { opened.Dispose(); throw; }
    }

    public void Dispose()
    {
        if(disposed)return;
        disposed=true;
        foreach(var file in held)file.Dispose();
        held.Clear();
    }
}
