using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace CodexAccountSwitcher.Core;

/// <summary>
/// READ-ONLY recovery review of ONE previously claimed native test event.
/// The only permitted root is a direct, GUID-like Windows OS-TEMP fixture.
/// Recovery never creates, deletes, fixes, retries, sends, or consumes consent.
/// Even a verified marker is NOT a production outbox or delivery receipt.
/// </summary>
public static class WindowsTelegramReservationRecoveryReview
{
    private const string Prefix="codex-switcher-telegram-handle-test-";
    private const int MaxMarkerBytes=4096;
    private const int ExpectedFieldCount=6;

    public static WindowsReservationRecoveryInspection InspectForOfflineTest(
        string? scratchRoot,TelegramNotificationLane lane,string? eventKey)
    {
        if(!OperatingSystem.IsWindows())
            return Hold(WindowsReservationRecoveryStatus.UnsupportedPlatform);
        if(!Enum.IsDefined(lane) || !ValidHex64(eventKey))
            return Hold(WindowsReservationRecoveryStatus.InvalidEvent);
        if(!TryValidateRoot(scratchRoot,out var root))
            return Hold(WindowsReservationRecoveryStatus.InvalidFixtureScope);

        var sub=lane==TelegramNotificationLane.FiveHourRecovery
            ?"five-hour":"weekly";
        var folder=Path.Combine(root,sub);
        var marker=Path.Combine(folder,eventKey!+".reservation");
        // A missing folder cannot imply "not previously sent" because the
        // ledger may have been lost, substituted, or never provisioned.
        if(WindowsTelegramSameHandleReview.InspectDirectoryReadOnly(root).Status!=
                WindowsSameHandleStatus.HandleAclAndIdObservedOnly ||
           WindowsTelegramSameHandleReview.InspectDirectoryReadOnly(folder).Status!=
                WindowsSameHandleStatus.HandleAclAndIdObservedOnly)
            return Hold(WindowsReservationRecoveryStatus.ParentUntrusted);

        try
        {
            // Explicit READ_CONTROL is required to inspect native DACL on
            // this exact read-only OS handle. Never use CREATE/WRITE flags.
            using var native=CreateFileW(marker,0x80000000u|0x20000u|0x80u,
                1,IntPtr.Zero,3,0x00200000u,IntPtr.Zero);
            if(native.IsInvalid)
            {
                var error=Marshal.GetLastWin32Error();
                return Hold(error==2
                    ?WindowsReservationRecoveryStatus.NoLocalClaimObserved
                    :WindowsReservationRecoveryStatus.BusyOrUntrustedStorage);
            }
            var actual=WindowsTelegramSameHandleReview.InspectOpenedHandle(
                native,false);
            if(actual.Status!=WindowsSameHandleStatus.HandleAclAndIdObservedOnly)
                return Hold(WindowsReservationRecoveryStatus.ClaimFileUntrusted);
            using var file=new FileStream(native,FileAccess.Read,4096,
                isAsync:false);
            if(file.Length==0)
                return Hold(WindowsReservationRecoveryStatus.InterruptedOrEmptyClaim);
            if(file.Length>MaxMarkerBytes)
                return Hold(WindowsReservationRecoveryStatus.OversizedOrCorruptClaim);
            var data=new byte[checked((int)file.Length)];
            file.ReadExactly(data);
            // Re-examine the SAME file handle after content read.
            var after=WindowsTelegramSameHandleReview.InspectOpenedHandle(
                file.SafeFileHandle,false,actual.FileId);
            if(after.Status!=WindowsSameHandleStatus.HandleAclAndIdObservedOnly)
                return Hold(WindowsReservationRecoveryStatus.ClaimFileUntrusted);
            return ParseStrict(data,sub,eventKey!);
        }
        catch(FileNotFoundException)
        {
            // Verified empty lane, but NOT approval to send. A shared owner
            // authority and persistent production ledger still do not exist.
            return Hold(WindowsReservationRecoveryStatus.NoLocalClaimObserved);
        }
        catch(DirectoryNotFoundException)
        {
            return Hold(WindowsReservationRecoveryStatus.ParentUntrusted);
        }
        catch (Exception ex) when(ex is IOException or UnauthorizedAccessException or
            System.Security.SecurityException or ArgumentException or
            NotSupportedException or InvalidOperationException)
        {
            return Hold(WindowsReservationRecoveryStatus.BusyOrUntrustedStorage);
        }
    }

    private static WindowsReservationRecoveryInspection ParseStrict(
        byte[] data,string expectedLane,string expectedEventKey)
    {
        try
        {
            using var doc=JsonDocument.Parse(data,new JsonDocumentOptions {
                AllowTrailingCommas=false,
                CommentHandling=JsonCommentHandling.Disallow,
                MaxDepth=8
            });
            var root=doc.RootElement;
            if(root.ValueKind!=JsonValueKind.Object)
                return Hold(WindowsReservationRecoveryStatus.OversizedOrCorruptClaim);
            var allowed=new HashSet<string>(StringComparer.Ordinal) {
                "version","lane","eventKey","deviceSha256",
                "observedAtUtcMs","status"
            };
            var seen=new HashSet<string>(StringComparer.Ordinal);
            foreach(var prop in root.EnumerateObject())
                if(!allowed.Contains(prop.Name)||!seen.Add(prop.Name))
                    return Hold(WindowsReservationRecoveryStatus.OversizedOrCorruptClaim);
            if(seen.Count!=ExpectedFieldCount ||
                !root.GetProperty("version").TryGetInt32(out var version) ||
                version!=1)
                return Hold(WindowsReservationRecoveryStatus.OversizedOrCorruptClaim);
            if(root.GetProperty("lane").ValueKind!=JsonValueKind.String ||
                root.GetProperty("eventKey").ValueKind!=JsonValueKind.String ||
                root.GetProperty("status").ValueKind!=JsonValueKind.String ||
                root.GetProperty("deviceSha256").ValueKind!=JsonValueKind.String ||
                root.GetProperty("observedAtUtcMs").ValueKind!=JsonValueKind.Number)
                return Hold(WindowsReservationRecoveryStatus.OversizedOrCorruptClaim);

            if(root.GetProperty("lane").GetString()!=expectedLane ||
                root.GetProperty("eventKey").GetString()!=expectedEventKey)
                return Hold(WindowsReservationRecoveryStatus.ClaimBindingMismatch);
            if(root.GetProperty("status").GetString()!=
                "HANDLE_BOUND_FIXTURE_ONLY_NO_SEND" ||
                !ValidHex64(root.GetProperty("deviceSha256").GetString()) ||
                !root.GetProperty("observedAtUtcMs").TryGetInt64(out var ms) ||
                ms<0 || ms>253402300799999)
                return Hold(WindowsReservationRecoveryStatus.OversizedOrCorruptClaim);
            return new(WindowsReservationRecoveryStatus.ValidFixtureClaimNoSend);
        }
        catch (Exception ex) when (ex is JsonException or
            InvalidOperationException or ArgumentException)
        {
            return Hold(WindowsReservationRecoveryStatus.OversizedOrCorruptClaim);
        }
    }

    private static bool ValidHex64(string? input)=>
        input is {Length:64} &&
        input.All(c=>c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool TryValidateRoot(string? raw,out string full)
    {
        full="";
        if(string.IsNullOrWhiteSpace(raw) || raw.IndexOfAny(['\0','*','?'])>=0 ||
            !Path.IsPathFullyQualified(raw))
            return false;
        try
        {
            if(raw.Replace('/','\\').Split('\\').Any(x=>x is ".." or "."))
                return false;
            var actual=Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw));
            var temp=Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(Path.GetTempPath()));
            var leaf=Path.GetFileName(actual);
            if(!string.Equals(Path.GetDirectoryName(actual),temp,
                    StringComparison.OrdinalIgnoreCase) ||
                !leaf.StartsWith(Prefix,StringComparison.Ordinal) ||
                leaf.Length!=Prefix.Length+32 ||
                !leaf[Prefix.Length..].All(c=>c is >= '0' and <= '9'
                    or >= 'a' and <= 'f'))
                return false;
            full=actual;return true;
        }
        catch (Exception ex) when(ex is ArgumentException or IOException or
            NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll",EntryPoint="CreateFileW",CharSet=CharSet.Unicode,
        SetLastError=true,ExactSpelling=true)]
    private static extern SafeFileHandle CreateFileW(
        string path,uint access,uint share,IntPtr attributes,uint mode,
        uint flags,IntPtr template);

    private static WindowsReservationRecoveryInspection Hold(
        WindowsReservationRecoveryStatus status)=>new(status);
}

public enum WindowsReservationRecoveryStatus
{
    UnsupportedPlatform,
    InvalidEvent,
    InvalidFixtureScope,
    ParentUntrusted,
    NoLocalClaimObserved,
    ClaimFileUntrusted,
    InterruptedOrEmptyClaim,
    OversizedOrCorruptClaim,
    ClaimBindingMismatch,
    BusyOrUntrustedStorage,
    ValidFixtureClaimNoSend
}

public sealed record WindowsReservationRecoveryInspection(
    WindowsReservationRecoveryStatus Status)
{
    public bool AutomaticRetryAllowed=>false;
    public bool ProductionReservationAuthorized=>false;
    public bool TelegramSendAuthorized=>false;
}
