#Requires -Version 5.1
<#
  FUTURE, NOT INSTALLED, NOT INVOKED.
  Dedicated 5H receipt script that reuses the existing muted Amano health bot
  CurrentUser DPAPI token and chat ID without copying them.
  Requires separate one-time send approval and verified sole sender ownership.
  Input switches do NOT themselves prove user approval or sole ownership.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$LegacyBotDirectory,
    [Parameter(Mandatory = $true)][string]$EventKey,
    [Parameter(Mandatory = $true)][string]$Message,
    [switch]$ApprovedSingleSend,
    [switch]$WindowsSenderVerified
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# No file reads, decryption, or network without both explicit gates.
if (-not $ApprovedSingleSend -or -not $WindowsSenderVerified) { exit 40 }
if ($EventKey -cnotmatch '\A[a-f0-9]{64}\z') { exit 40 }
if ($Message.Length -gt 250 -or
    $Message -cnotmatch '\ACodex 5시간 한도 복구: 잔여 사용량 100% \(확인 [0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2} KST\)\z') {
    exit 40
}

$tokenFile = Join-Path $LegacyBotDirectory 'telegram-health-token.dpapi'
$chatFile = Join-Path $LegacyBotDirectory 'telegram-chat-id.txt'
if (-not [IO.File]::Exists($tokenFile) -or
    -not [IO.File]::Exists($chatFile)) { exit 10 }

$secure = $null
$token = $null
$chatId = $null
$bstr = [IntPtr]::Zero
try {
    # Follow the existing Health Bot's Windows PowerShell 5.1 module pin.
    # Avoid loading an incompatible PowerShell 7 Security module from PSModulePath.
    Import-Module -Name (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Security\Microsoft.PowerShell.Security.psd1') -ErrorAction Stop
    $secure = (Get-Content -LiteralPath $tokenFile -Raw).Trim() |
        ConvertTo-SecureString
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    $token = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
    $chatId = (Get-Content -LiteralPath $chatFile -Raw).Trim()
    if ($token -cnotmatch '\A[0-9]{5,20}:[A-Za-z0-9_-]{15,120}\z' -or
        $chatId -notmatch '\A-?[0-9]{1,20}\z') {
        exit 20
    }

    $uri = 'https://api.telegram.org/bot' + $token + '/sendMessage'
    # No automatic retries or redirects with a secret-bearing Telegram URI.
    $request = @{
        Uri = $uri
        Method = 'Post'
        MaximumRedirection = 0
        TimeoutSec = 10
        ErrorAction = 'Stop'
        Body = @{ chat_id = $chatId; text = $Message;
                  disable_web_page_preview = 'true';
                  disable_notification = 'true' }
    }
    $response = Invoke-RestMethod @request
    [long]$messageID = 0
    if ($null -eq $response -or $response.ok -ne $true -or
        $null -eq $response.result -or
        -not [long]::TryParse(
            [string]$response.result.message_id, [ref]$messageID) -or
        $messageID -le 0) {
        exit 20
    }

    # The only successful stdout is a strictly typed, bound receipt.
    Write-Output ('CODEX_FIVEHOUR100_TELEGRAM_ACK_V1|' +
        $EventKey + '|' +
        $messageID.ToString([Globalization.CultureInfo]::InvariantCulture))
    exit 0
}
catch {
    # Exception details may expose a bot token inside a Telegram URL.
    exit 20
}
finally {
    if ($bstr -ne [IntPtr]::Zero) {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    }
    $token = $null
    $chatId = $null
    $secure = $null
}
