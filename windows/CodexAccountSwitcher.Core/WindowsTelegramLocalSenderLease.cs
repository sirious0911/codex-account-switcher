using System.Security.Cryptography;
using System.Text;

namespace CodexAccountSwitcher.Core;

/// <summary>
/// Optional, session-local Windows sender claim, separate from the app's
/// single-instance lock. It cannot prove Mac/other-computer ownership and
/// NEVER authorizes Telegram sendMessage. Only the WPF dispatcher thread
/// should acquire, update and dispose the mutex.
/// </summary>
public sealed class WindowsTelegramLocalSenderLease : IDisposable
{
    private readonly Mutex mutex;
    private bool owns;
    private bool disposed;
    private bool abandoned;
    public WindowsLocalSenderLeaseState State { get; private set; } =
        WindowsLocalSenderLeaseState.ViewerOrDisabled;

    public WindowsTelegramLocalSenderLease(string userDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userDataDirectory);
        var canonical = Path.GetFullPath(userDataDirectory).ToUpperInvariant();
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        mutex = new Mutex(initiallyOwned: false,
            name: @"Local\CodexSwitcherTelegramSender-" + suffix);
    }

    /// <summary>
    /// Reflects user intent in a session-local OS claim. This is neither a
    /// global sender lease nor authority to read bot credentials or transmit.
    /// </summary>
    public WindowsLocalSenderLeaseState Update(TelegramNotificationPreferences? settings)
    {
        if (disposed) return WindowsLocalSenderLeaseState.Disposed;
        var normalized = settings?.Normalize();
        if (normalized is null || !(normalized.WantsFiveHour || normalized.WantsWeekly))
        {
            Release();
            State = WindowsLocalSenderLeaseState.ViewerOrDisabled;
            return State;
        }

        if (abandoned)
            return State = WindowsLocalSenderLeaseState.PreviousOwnerAbandoned;
        if (owns)
            return State = WindowsLocalSenderLeaseState.LocalClaimHeld;

        try
        {
            if (mutex.WaitOne(0))
            {
                owns = true;
                return State = WindowsLocalSenderLeaseState.LocalClaimHeld;
            }

            return State = WindowsLocalSenderLeaseState.LocalClaimHeldElsewhere;
        }
        catch (AbandonedMutexException)
        {
            // After a crash, an earlier Telegram request could be uncertain.
            // Do not automatically adopt that sender. Release the OS mutex.
            abandoned = true;
            try { mutex.ReleaseMutex(); } catch (ApplicationException) { }
            return State = WindowsLocalSenderLeaseState.PreviousOwnerAbandoned;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or
            IOException or ApplicationException)
        {
            return State = WindowsLocalSenderLeaseState.LocalClaimUnavailable;
        }
    }

    private void Release()
    {
        if (!owns) return;
        owns = false;
        try { mutex.ReleaseMutex(); }
        catch (ApplicationException)
        {
            State = WindowsLocalSenderLeaseState.LocalClaimUnavailable;
        }
    }

    public string Display => State switch
    {
        WindowsLocalSenderLeaseState.ViewerOrDisabled =>
            "Windows 로컬 Sender: OFF (Viewer 또는 알림 OFF)",
        WindowsLocalSenderLeaseState.LocalClaimHeld =>
            "Windows 로컬 Sender: 이 세션 점유 · 다른 PC/Mac 미확인 · 실제 발송 차단",
        WindowsLocalSenderLeaseState.LocalClaimHeldElsewhere =>
            "Windows 로컬 Sender: 다른 프로세스가 점유 · 실제 발송 차단",
        WindowsLocalSenderLeaseState.PreviousOwnerAbandoned =>
            "Windows 로컬 Sender: 이전 점유 비정상 종료 · 수동 확인 필요 · 실제 발송 차단",
        WindowsLocalSenderLeaseState.LocalClaimUnavailable =>
            "Windows 로컬 Sender: 점유 불가 · 실제 발송 차단",
        _ => "Windows 로컬 Sender: 종료됨 · 실제 발송 차단"
    };

    public bool ActualTelegramDeliveryAuthorized => false;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Release();
        mutex.Dispose();
        State = WindowsLocalSenderLeaseState.Disposed;
    }
}

public enum WindowsLocalSenderLeaseState
{
    ViewerOrDisabled,
    LocalClaimHeld,
    LocalClaimHeldElsewhere,
    PreviousOwnerAbandoned,
    LocalClaimUnavailable,
    Disposed
}
