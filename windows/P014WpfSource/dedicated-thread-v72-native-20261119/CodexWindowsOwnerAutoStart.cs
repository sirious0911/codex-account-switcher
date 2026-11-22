using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace CodexAccountSwitcher;

/// <summary>
/// Optional user-session Central Owner child process. This helper is never
/// enabled by a UI draft: it requires a separately provisioned CurrentUser
/// DPAPI bootstrap file, reviewed Node executable and reviewed JS entrypoint.
/// No registry edits, Task Scheduler installation, credentials in argv/env,
/// thread creation, or live Codex message from app startup.
/// </summary>
public sealed class CodexWindowsOwnerAutoStart : IDisposable
{
    private sealed record Bootstrap(
        int Version,bool Enabled,string NodeExePath,string NodeExeSha256,
        string OwnerMainPath,string OwnerMainSha256,
        string OwnerJsBundleSha256,string PrivateConfigDpapiBase64);
    private readonly Process? child;
    private readonly WindowsReviewedOwnerJsBundle? pinnedJs;
    public string State {get;}
    public bool IsRunning
    {
        get
        {
            if(child is null)return false;
            try{return !child.HasExited;}
            catch(InvalidOperationException){return false;}
            catch(System.ComponentModel.Win32Exception){return false;}
        }
    }

    public static bool HasEnabledConfiguration(string personalDataHome) =>
        WindowsOwnerReadOnlyAdmission.IsReady(personalDataHome);

    private CodexWindowsOwnerAutoStart(Process? child,string state,
        WindowsReviewedOwnerJsBundle? pinnedJs=null)
    {
        this.child=child;
        this.pinnedJs=pinnedJs;
        State=state;
    }
    private static readonly Regex Hex64=new("^[a-f0-9]{64}$",
        RegexOptions.CultureInvariant);
    public static CodexWindowsOwnerAutoStart TryStart(string personalDataHome,
        Guid? requiredActiveProfileId=null)
    {
        if(!OperatingSystem.IsWindows() ||
           string.IsNullOrWhiteSpace(personalDataHome) ||
           !Path.IsPathFullyQualified(personalDataHome))
            return new(null,"HOLD_OWNER_DATA_HOME");
        if(WindowsOwnerReadOnlyAdmission.HoldReason(personalDataHome) is { } hold)
            return new(null,hold);
        var path=Path.Combine(personalDataHome,
            "codex-owner-startup-v1.json");
        if(!File.Exists(path))
            return new(null,"OWNER_AUTOSTART_OFF");
        Process? process=null;
        WindowsReviewedOwnerJsBundle? pinnedJs=null;
        byte[]? cleartext=null;
        bool readyVerified=false;
        try
        {
            var file=new FileInfo(path);
            if(file.Length is < 2 or > 10_000 ||
               file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return new(null,"HOLD_OWNER_BOOTSTRAP_FILE");
            var row=JsonSerializer.Deserialize<Bootstrap>(
                File.ReadAllText(path,Encoding.UTF8));
            if(row is null || row.Version!=1 || !row.Enabled)
                return new(null,"OWNER_AUTOSTART_OFF");
            if(!GoodBinary(row.NodeExePath,row.NodeExeSha256) ||
               !GoodBinary(row.OwnerMainPath,row.OwnerMainSha256) ||
               !string.Equals(Path.GetFileName(row.OwnerMainPath),
                   "codex-windows-owner-main.mjs",StringComparison.Ordinal))
                return new(null,"HOLD_OWNER_BOOTSTRAP_IDENTITY");
            pinnedJs=WindowsReviewedOwnerJsBundle.Acquire(
                row.OwnerMainPath,row.OwnerJsBundleSha256);
            var encrypted=Convert.FromBase64String(row.PrivateConfigDpapiBase64);
            if(encrypted.Length is < 64 or > 30_000)
                return new(null,"HOLD_OWNER_BOOTSTRAP_DATA");
            cleartext=ProtectedData.Unprotect(encrypted,null,
                DataProtectionScope.CurrentUser);
            if(cleartext.Length is < 50 or > 50_000)
                return new(null,"HOLD_OWNER_BOOTSTRAP_DATA");
            using(var doc=JsonDocument.Parse(cleartext))
            {
                if(doc.RootElement.ValueKind!=JsonValueKind.Object ||
                   !doc.RootElement.TryGetProperty("operatorPrepared",
                       out var enabled) ||
                   enabled.ValueKind!=JsonValueKind.True)
                    return new(null,"HOLD_OWNER_BOOTSTRAP_NOT_PREPARED");
                if(requiredActiveProfileId is { } expected &&
                   (expected==Guid.Empty ||
                    !doc.RootElement.TryGetProperty("owner",out var owner) ||
                    owner.ValueKind!=JsonValueKind.Object ||
                    !owner.TryGetProperty("profileId",out var profile) ||
                    profile.ValueKind!=JsonValueKind.String ||
                    profile.GetString()!=expected.ToString("D")))
                    return new(null,"HOLD_OWNER_ACTIVE_PROFILE_MISMATCH");
            }
            var psi=new ProcessStartInfo(row.NodeExePath){
                UseShellExecute=false,CreateNoWindow=true,
                RedirectStandardInput=true,RedirectStandardOutput=true,
                RedirectStandardError=true,
                StandardInputEncoding=new UTF8Encoding(false),
                StandardOutputEncoding=new UTF8Encoding(false)
            };
            psi.ArgumentList.Add(row.OwnerMainPath);
            psi.ArgumentList.Add("--prepared-stdin");
            process=new Process{StartInfo=psi,EnableRaisingEvents=true};
            if(!process.Start())
                return new(null,"HOLD_OWNER_PROCESS_START");
            // Private payload goes over an anonymous child stdin pipe, not
            // command line arguments, environment variables or logs.
            process.StandardInput.BaseStream.Write(cleartext);
            process.StandardInput.BaseStream.Flush();
            process.StandardInput.Close();
            var ready=process.StandardOutput.ReadLineAsync()
                .WaitAsync(TimeSpan.FromSeconds(7)).GetAwaiter().GetResult();
            if(ready!="OWNER_PREPARED_RUNNING" || process.HasExited)
                return new(null,"HOLD_OWNER_PROCESS_NOT_READY");
            readyVerified=true;
            return new(process,"OWNER_PROCESS_RUNNING",pinnedJs);
        }
        catch(Exception ex) when(ex is IOException or JsonException or
            CryptographicException or UnauthorizedAccessException or
            ArgumentException or InvalidOperationException or
            TimeoutException or System.ComponentModel.Win32Exception)
        {
            return new(null,"HOLD_OWNER_BOOTSTRAP_UNAVAILABLE");
        }
        finally
        {
            // Zero decrypted private configuration on success and failure.
            if(cleartext is not null)
                CryptographicOperations.ZeroMemory(cleartext);
            // Retain only a verified live child. For every other result,
            // kill the exact spawned process tree before returning.
            if(process is not null && !readyVerified)
            {
                try{if(!process.HasExited)process.Kill(entireProcessTree:true);}catch{}
                process.Dispose();
            }
            if(!readyVerified)pinnedJs?.Dispose();
        }
    }
    private static bool GoodBinary(string path,string digest)
    {
        if(string.IsNullOrWhiteSpace(path) ||
           !Path.IsPathFullyQualified(path) ||
           !Hex64.IsMatch(digest??""))return false;
        var info=new FileInfo(path);
        if(!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            return false;
        using var stream=File.OpenRead(path);
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(stream),Convert.FromHexString(digest!));
    }
    public void Dispose()
    {
        if(child is not null)
        {
            try{if(!child.HasExited)child.Kill(entireProcessTree:true);}
            catch(InvalidOperationException){}
            catch(System.ComponentModel.Win32Exception){}
            child.Dispose();
        }
        pinnedJs?.Dispose();
    }
}
