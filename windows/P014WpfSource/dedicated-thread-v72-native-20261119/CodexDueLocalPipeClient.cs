using System;
using System.IO;
using System.IO.Pipes;
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
/// Windows-only local due event publisher. It transmits ONLY a current,
/// account-verified deadline, never credentials or Codex messages. Both the
/// app and separate owner must be explicitly enrolled with a private HMAC key.
/// No configured key => no transport or automatic send.
/// </summary>
public sealed class CodexDueLocalPipeClient
{
    private readonly string pipeName;
    private readonly byte[] key;
    private static readonly Regex Name =
        new("^[A-Za-z0-9_-]{8,65}$",RegexOptions.CultureInvariant);
    public CodexDueLocalPipeClient(string pipeName,byte[] localKey)
    {
        if(!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(pipeName) ||
           !Name.IsMatch(pipeName) || localKey is not { Length:32 })
            throw new ArgumentException("Explicit reviewed local pipe enrollment required");
        this.pipeName=pipeName;
        key=(byte[])localKey.Clone();
    }

    public async Task<string> PublishAsync(TrustedResetDeadline due,
        DedicatedChatSettings bound,CancellationToken cancellationToken=default)
    {
        if(due is null || bound is null ||
           due.ActiveProfileId!=bound.ProfileId ||
           due.ActiveAccountSha256!=bound.AccountSha256 ||
           !due.TrustedSource || !due.ActiveIdentityConfirmed ||
           !due.OrdinaryUsageAllowed || bound.Message!="안녕" ||
           bound.ThreadStorage!="desktop-default" ||
           !Guid.TryParseExact(bound.ExistingThreadId,"D",out _) ||
           due.Lane is not (LimitLane.FiveHour or LimitLane.SevenDay))
            return "HOLD_UNTRUSTED_LOCAL_DUE";
        var lane=due.Lane==LimitLane.FiveHour ?
            "CODEX_FIVE_HOUR":"CODEX_SEVEN_DAY";
        var eventData=new {
            profileId=bound.ProfileId.ToString("D"),
            accountSha256=bound.AccountSha256,
            threadId=bound.ExistingThreadId,
            lane,
            resetAtUnix=due.PreviousResetAtUtc.ToUnixTimeSeconds(),
            observedAtUnix=due.LastTrustedObservationUtc.ToUnixTimeSeconds()
        };
        var sentAtMs=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var nonce=Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var text=string.Join("\n",
            "CODEX_DUE_LOCAL_PIPE_V1",eventData.profileId,
            eventData.accountSha256,eventData.threadId,eventData.lane,
            eventData.resetAtUnix,eventData.observedAtUnix,sentAtMs,nonce);
        var mac=Convert.ToHexStringLower(
            HMACSHA256.HashData(key,Encoding.UTF8.GetBytes(text)));
        var packet=JsonSerializer.Serialize(new {
            version=1,@event=eventData,sentAtMs,nonce,mac
        });
        if(Encoding.UTF8.GetByteCount(packet)>1023)
            return "HOLD_DUE_PIPE_OVERSIZE";
        using var limit=CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        limit.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            await using var pipe=new NamedPipeClientStream(".",pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(2500,limit.Token);
            await using var writer=new StreamWriter(pipe,
                new UTF8Encoding(false),bufferSize:1024,leaveOpen:true);
            using var reader=new StreamReader(pipe,
                new UTF8Encoding(false),detectEncodingFromByteOrderMarks:false,
                bufferSize:1024,leaveOpen:true);
            await writer.WriteLineAsync(packet.AsMemory(),limit.Token);
            await writer.FlushAsync(limit.Token);
            var received=await reader.ReadLineAsync(limit.Token);
            if(received is null || received.Length>180)
                return "HOLD_DUE_PIPE_REPLY_UNCERTAIN";
            using var json=JsonDocument.Parse(received);
            var root=json.RootElement;
            if(root.ValueKind!=JsonValueKind.Object ||
               root.EnumerateObject().Count()!=1 ||
               !root.TryGetProperty("code",out var value) ||
               value.ValueKind!=JsonValueKind.String)
                return "HOLD_DUE_PIPE_BAD_REPLY";
            var code=value.GetString()??"";
            return Regex.IsMatch(code,"^(?:HOLD_[A-Z0-9_]{2,90}|DUE_RECEIVED_OWNER_PENDING)$",
                RegexOptions.CultureInvariant)
                ? code:"HOLD_DUE_PIPE_BAD_REPLY";
        }
        catch(Exception error) when(error is IOException or OperationCanceledException
            or UnauthorizedAccessException or TimeoutException
            or JsonException or InvalidOperationException)
        {
            // Connection failures may occur AFTER a valid event reached owner.
            // Caller MUST NOT retry another native Codex turn on this result.
            return "HOLD_DUE_PIPE_OUTCOME_UNCERTAIN_NO_RETRY";
        }
    }
}
