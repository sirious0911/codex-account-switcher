namespace CodexAccountSwitcher.Core;

/// <summary>
/// Filesystem metadata only. This never opens the bot's encrypted token,
/// chat destination or sender script, and it never authorizes a send.
/// </summary>
public sealed record TelegramBotFileStatus(
    bool MonitorBotFilesPresent,
    bool AlertBotFilesPresent,
    bool ChatDestinationPresent)
{
    public static TelegramBotFileStatus Inspect(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
            return new(false, false, false);

        bool ExistsNonempty(string name)
        {
            try
            {
                var file = new FileInfo(Path.Combine(directory, name));
                return file.Exists && file.Length > 0 &&
                    !file.Attributes.HasFlag(FileAttributes.ReparsePoint);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException)
            {
                return false;
            }
        }

        var destination = ExistsNonempty("telegram-chat-id.txt");
        return new(
            ExistsNonempty("send-telegram-health.ps1") &&
                ExistsNonempty("telegram-health-token.dpapi"),
            ExistsNonempty("send-telegram-alert.ps1") &&
                ExistsNonempty("telegram-token.dpapi"),
            destination);
    }

    public string Display =>
        "5H 모니터 봇 파일: " + (MonitorBotFilesPresent ? "확인" : "미확인") +
        " | 7D 얼럿 봇 파일: " + (AlertBotFilesPresent ? "확인" : "미확인") +
        " | 수신 대상 파일: " + (ChatDestinationPresent ? "확인" : "미확인") +
        " | 메타데이터만 확인 (내용·토큰 미조회, 전송 승인 아님)";
}
