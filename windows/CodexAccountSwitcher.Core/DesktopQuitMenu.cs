using System.Diagnostics;
using System.Windows.Automation;

namespace CodexAccountSwitcher.Core;

/// <summary>
/// Attempts ONLY the application's own File -> Quit command through accessibility.
/// No window-close, force-termination, keyboard injection or generic process kill.
/// Invoked only for a verified packaged Codex process during a user-requested switch.
/// </summary>
internal static class DesktopQuitMenu
{
    internal static bool TryInvoke(Process verifiedProcess)
    {
        try
        {
            if (verifiedProcess.HasExited || verifiedProcess.MainWindowHandle == IntPtr.Zero ||
                !verifiedProcess.Responding) return false;

            var window = AutomationElement.FromHandle(verifiedProcess.MainWindowHandle);
            if (window is null) return false;
            var menuType = new PropertyCondition(
                AutomationElement.ControlTypeProperty, ControlType.MenuItem);

            // The File item itself must be present in this exact process'
            // window. Do not inspect or interact with any other app/window.
            var menuItems = window.FindAll(TreeScope.Descendants, menuType);
            AutomationElement? file = null;
            foreach (AutomationElement item in menuItems)
            {
                var name = item.Current.Name?.Trim();
                if (name is "File" or "파일" or "&File" or "File (&F)")
                {
                    file = item;
                    break;
                }
            }
            if (file is null) return false;

            // Expand a *menu*, never synthesize Alt+F or arbitrary keystrokes.
            if (file.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expand) &&
                expand is ExpandCollapsePattern expander)
                expander.Expand();
            else if (file.TryGetCurrentPattern(InvokePattern.Pattern, out var invokeFile) &&
                     invokeFile is InvokePattern fileInvoker)
                fileInvoker.Invoke();
            else return false;

            // Only exact names of a Quit action qualify. An Exit on a
            // similarly named unrelated menu item cannot trigger execution.
            var available = window.FindAll(TreeScope.Descendants, menuType);
            foreach (AutomationElement item in available)
            {
                var name = item.Current.Name?.Trim().Replace("&", "");
                if (name is not ("Quit ChatGPT" or "Quit Codex" or "Exit ChatGPT" or
                                "Exit Codex" or "종료" or "ChatGPT 종료" or "Codex 종료"))
                    continue;
                if (!item.TryGetCurrentPattern(InvokePattern.Pattern, out var invokeQuit) ||
                    invokeQuit is not InvokePattern quitter) continue;
                quitter.Invoke();
                return true;
            }
        }
        catch (ElementNotAvailableException) { }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        // Unknown or unavailable app menus fail closed; X is not equivalent to Quit.
        return false;
    }
}
