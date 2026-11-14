using System;
using System.IO;
using System.Threading.Tasks;
using CodexAccountSwitcher.Core;

internal static class WindowsLocalSenderLeaseTest
{
    public static void Run(string root)
    {
        var fakeHome = Path.Combine(root,
            "local-sender-lease-fixture-" + Guid.NewGuid().ToString("N"));
        var request = new TelegramNotificationPreferences(
            TelegramNotificationDeviceRole.Sender, true, true, true);

        using var holder = new WindowsTelegramLocalSenderLease(fakeHome);
        Assert(holder.Update(TelegramNotificationPreferences.Default) ==
            WindowsLocalSenderLeaseState.ViewerOrDisabled,
            "Viewer/OFF must never acquire the Windows local lease.");
        Assert(holder.Update(request) == WindowsLocalSenderLeaseState.LocalClaimHeld &&
            holder.Display.Contains("다른 PC/Mac 미확인", StringComparison.Ordinal) &&
            !holder.ActualTelegramDeliveryAuthorized,
            "Sender setting may hold the local mutex but never authorize Telegram.");
        Assert(holder.Update(request) == WindowsLocalSenderLeaseState.LocalClaimHeld,
            "Repeated preference changes must not recursively acquire the mutex.");

        var blocked = Task.Run(() =>
        {
            using var rival = new WindowsTelegramLocalSenderLease(fakeHome);
            return rival.Update(request) ==
                WindowsLocalSenderLeaseState.LocalClaimHeldElsewhere &&
                !rival.ActualTelegramDeliveryAuthorized;
        }).GetAwaiter().GetResult();
        Assert(blocked, "Independent Windows thread must not steal the lease.");

        Assert(holder.Update(request with { NotificationsRequested = false }) ==
            WindowsLocalSenderLeaseState.ViewerOrDisabled,
            "Sender OFF must release the local mutex.");
        var acquired = Task.Run(() =>
        {
            using var replacement = new WindowsTelegramLocalSenderLease(fakeHome);
            return replacement.Update(request) ==
                WindowsLocalSenderLeaseState.LocalClaimHeld &&
                !replacement.ActualTelegramDeliveryAuthorized;
        }).GetAwaiter().GetResult();
        Assert(acquired, "A new owner must acquire after the earlier one releases.");
        Assert(!Directory.Exists(fakeHome),
            "Named mutex must not write account data or sender outbox files.");
    }

    private static void Assert(bool pass, string message)
    {
        if (!pass) throw new InvalidOperationException(message);
    }
}
