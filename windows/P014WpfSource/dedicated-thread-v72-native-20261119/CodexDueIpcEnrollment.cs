using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DedicatedThreadCycleStarter;

namespace CodexAccountSwitcher;

/// <summary>
/// Read-only, optional local due-pipe enrollment. OFF by default. The HMAC
/// secret must be separately provisioned with Windows CurrentUser DPAPI;
/// neither the user-facing draft setting nor the repository contains the key.
/// No server is started and no Codex operation is performed by this class.
/// </summary>
public static class CodexDueIpcEnrollment
{
    private sealed record Enrollment(
        int Version,bool Enabled,string ProfileId,string AccountSha256,
        string ThreadId,string PipeName,string ProtectedSecretBase64);
    public static bool IsEnrolled(string appDataHome,DedicatedChatSettings bound)
    {
        try {return Read(appDataHome,bound) is not null;}
        catch(Exception ex) when(ex is not OutOfMemoryException
            and not StackOverflowException){return false;}
    }

    public static async Task<string> DeliverAsync(
        string appDataHome,TrustedResetDeadline due,
        DedicatedChatSettings bound,CancellationToken cancellationToken)
    {
        try {
            var pair=Read(appDataHome,bound);
            if(pair is null)return "HOLD_LOCAL_PIPE_ENROLLMENT_OFF";
            return await new CodexDueLocalPipeClient(pair.Value.Pipe,pair.Value.Key)
                .PublishAsync(due,bound,cancellationToken);
        }
        catch(Exception ex) when(ex is not OutOfMemoryException
            and not StackOverflowException)
        {return "HOLD_LOCAL_PIPE_ENROLLMENT_UNAVAILABLE_NO_RETRY";}
    }

    private static (string Pipe,byte[] Key)? Read(string appDataHome,
        DedicatedChatSettings bound)
    {
        if(!WindowsOwnerReadOnlyAdmission.IsReady(appDataHome))return null;
        if(string.IsNullOrWhiteSpace(appDataHome) ||
           !Path.IsPathFullyQualified(appDataHome))
            return null;
        var baseDir=new DirectoryInfo(Path.GetFullPath(appDataHome));
        if(!baseDir.Exists ||
           baseDir.Attributes.HasFlag(FileAttributes.ReparsePoint))
            return null;
        var path=Path.Combine(baseDir.FullName,
            "codex-due-ipc-enrollment-v1.json");
        var file=new FileInfo(path);
        if(!file.Exists || file.Length is <= 0 or > 2048 ||
           file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            return null;
        var row=JsonSerializer.Deserialize<Enrollment>(
            File.ReadAllText(path,Encoding.UTF8));
        if(row is null || row.Version!=1 || !row.Enabled ||
           row.ProfileId!=bound.ProfileId.ToString("D") ||
           row.AccountSha256!=bound.AccountSha256 ||
           row.ThreadId!=bound.ExistingThreadId ||
           !Regex.IsMatch(row.PipeName??"",
               "^[A-Za-z0-9_-]{8,65}$",RegexOptions.CultureInvariant))
            return null;
        var cipher=Convert.FromBase64String(row.ProtectedSecretBase64);
        if(cipher.Length is < 32 or > 512)return null;
        var key=ProtectedData.Unprotect(cipher,null,DataProtectionScope.CurrentUser);
        if(key.Length!=32)return null;
        return (row.PipeName!,key);
    }
}
