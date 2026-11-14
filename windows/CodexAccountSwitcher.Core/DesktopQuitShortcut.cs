using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CodexAccountSwitcher.Core;

/// <summary>
/// Sends the *documented* Windows app Quit shortcut (Ctrl+Q) only to the
/// verified OpenAI.Codex packaged Desktop window, and only as part of an
/// explicit account-switch confirmation. Never sends keys to the current
/// foreground application unless it is the exact intended process.
/// https://learn.chatgpt.com/docs/reference/commands
/// </summary>
internal static class DesktopQuitShortcut
{
    private const ushort VkControl = 0x11;
    private const ushort VkQ = 0x51;
    private const uint InputKeyboard = 1;
    private const uint KeyUp = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyInput
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X, Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KeyInput Key;
        [FieldOffset(0)] public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, [In] Input[] inputs, int size);

    internal static bool TryInvoke(Process verifiedProcess)
    {
        try
        {
            if (verifiedProcess.HasExited || verifiedProcess.MainWindowHandle == IntPtr.Zero ||
                !verifiedProcess.Responding) return false;

            var hwnd = verifiedProcess.MainWindowHandle;
            // Do not post keystrokes to another application. Activation can be
            // denied by Windows foreground-lock policy, in which case quit
            // must fail closed and the user can press Ctrl+Q manually.
            if (!SetForegroundWindow(hwnd)) return false;
            if (GetForegroundWindow() != hwnd) return false;
            GetWindowThreadProcessId(hwnd, out var foregroundPid);
            if (foregroundPid != (uint)verifiedProcess.Id) return false;

            // Ctrl down, Q down, Q up, Ctrl up as one serialized SendInput.
            // SendInput operates on *physical foreground focus*, hence the
            // HWND+PID check immediately before dispatch.
            var keys = new[]
            {
                Keyboard(VkControl, 0),
                Keyboard(VkQ, 0),
                Keyboard(VkQ, KeyUp),
                Keyboard(VkControl, KeyUp)
            };
            var sent = SendInput((uint)keys.Length, keys, Marshal.SizeOf<Input>());
            if (sent == keys.Length) return true;

            // If Windows rejects part of the chord, release modifier keys.
            // No fallback to Kill/WM_CLOSE/other process input.
            SendInput(2,
                [Keyboard(VkQ, KeyUp), Keyboard(VkControl, KeyUp)],
                Marshal.SizeOf<Input>());
        }
        catch (Win32Exception) { }
        catch (InvalidOperationException) { }
        catch (ArgumentException) { }
        return false;
    }

    private static Input Keyboard(ushort vk, uint flags) => new()
    {
        Type = InputKeyboard,
        Data = new InputUnion { Key = new KeyInput { Vk = vk, Flags = flags } }
    };
}
