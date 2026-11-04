# Codex Account Switcher for Windows

Windows is the native WPF window application in this repository, released alongside macOS under one version. Both macOS and Windows use the **same Swift `SwitcherCore`** for accounts, login, switching, settings, usage, cache and recovery. The C# project contains presentation DTOs, a private stdio transport and Windows system adapters; it does not implement account policy. See [shared-core design](../docs/shared-core.md).

The Windows application keeps the macOS account flow and uses Microsoft WPF Fluent controls with a Windows command bar, selected-list indicator, native checkboxes and standard dialog buttons.

## Interface

The compact window follows the existing macOS account flow:

- Account rows show email, reset time, remaining usage, available rate-limit resets, and a saved login snapshot of the subscription active-until date. Missing dates appear as Unknown; hovering shows when the login snapshot was last checked. The date is not a live billing or renewal query. Email titles stay on one line with ellipsis and reveal the full address on hover; accounts without email use their saved name. Select a row, then confirm to switch.
- **Windows 11 taskbar integration:** one compact horizontal, **flat/transparent** panel displays **5H**, **7D** and **R** (remaining reset credits) directly **to the left of the notification area's `^` caret**. The default surface has no colored card or large rounded rectangle; two hairline separators and a subtle hover highlight distinguish the three values. This is a WPF child window hosted *within* the existing taskbar, not a floating minibar or a set of overflow tray icons. Missing values display `?`, never an inferred zero or a percent borrowed from another window. Hover each value to see freshness and the source verification status. Left-click opens the account manager, right-click opens an Open/Quit menu.
- Taskbar hosting uses the pinned MIT-licensed `Deskband11Lib.Wpf` 1.4.2 dependency with `Placement=BeforeNotificationArea`. It relies on a non-Microsoft-supported taskbar child-window technique (`SetParent`). A Windows update can break placement; failure should leave the user with the main account app rather than draw a free-floating quota bar. No Explorer patching or injected DLL is installed. A separate synthetic-host proof was checked on Windows 11 build 26300.9711 at 150% scaling; the integrated app and other devices need live verification.
- Manage Accounts uses the same email titles and contains browser sign-in, register current login and removal of inactive accounts. The browser sign-in hint sits to the right of Add Account on one line.
- Settings take effect immediately: launch at login, tray tooltip percentage, optional 5-hour usage and language.
- Usage refreshes at startup, when opening the window, after account changes, and by default every five minutes. On Windows, an additional adaptive scheduler can request a refresh at **no more than once per minute** while recent Codex session-file modification times or the foreground Codex Desktop window indicate possible activity. No session contents, chat messages or credentials are read for detection. Without this signal, only the original five-minute Swift timer runs. Missing fresh results slow extra requests to **2 / 4 / 5 minutes**, never below the one-minute minimum. The new Swift `refreshActive` host command revalidates the current account and reads **only that active account** during fast polling; normal five-minute reads still include every registered profile. API-side usage accounting can be delayed. Failed refreshes retain the last good value.
- **Free/nonstandard quota windows:** the provider can return a 30-day quota without any 5-hour or 7-day window. The app shows the genuine duration as **30D** (including reset time and available reset-credit count) and does not relabel it as 7D or infer missing five-hour usage. In the taskbar, this replaces the second duration indicator for that active account: `5H ? | 30D NN% | R N`. Existing paid 5H/7D displays are unchanged.
- Updates are checked hourly when enabled. A new Windows version opens its release download page; the running EXE is not replaced automatically.

The account switcher is **one Windows EXE**; it runs in the background and hosts both the taskbar-embedded panel and the account-management window. The user does not launch a separate usage-bar application. The Close button hides the account-management window while the taskbar panel keeps running. Left-click the panel to reopen account management; right-click it and choose Quit to exit. Running the EXE a second time also activates the existing management window. To show the panel again after reboot, either launch the EXE once or enable **Launch at Login** in Settings (HKCU Run); this is optional and disabled until explicitly selected. Active account operations must finish before quitting. The panel uses the existing confirmed-active-account observation gate; optional active-work polling never switches or selects accounts.

## Shared logic and native boundaries

`Sources/SwitcherCore` owns the ordered handoff: normal Desktop close, verify/save current login, activate target, verify target, commit selection and reopen Desktop. A verification/commit failure performs the same bounded recovery on both platforms.

macOS calls the module directly through its SwiftUI model. Windows starts a bundled `SwitcherHost.exe` child and receives presentation snapshots. Credentials never cross that UI protocol. Windows adapters implement file ACLs/atomic replacement, Desktop discovery and normal close/open, browser opening, tray and startup integration.

Desktop is located from the `OpenAI.Codex` Store manifest (its executable can be `ChatGPT.exe`) and common per-user installation paths. Matching uses its signed package identity when available, otherwise the full executable path. Existing CLI sessions are not closed. **Windows Store Codex can treat its window X/WM_CLOSE as hide-to-background, not a full quit.** At an account-switch request, the Windows adapter first looks for the application's exact **File → Quit ChatGPT** menu through UI Automation and invokes it only if the verified app exposes it. If that menu is not exposed (as observed on the user's current Store build), the adapter may send the official **Ctrl+Q Quit** shortcut, but only during an explicitly confirmed switch, after activating and verifying the exact target window and PID. When Windows denies foreground focus or the actual app fails to exit, it **does not send X or force-terminate the process**: switching stops before touching credentials and instructs the user to press Ctrl+Q manually, then retry. Windows runners can refuse synthetic foreground activation; a skipped keyboard integration test does not establish a live automatic-Quit PASS. Non-Store desktop executables continue to use the verified normal window-close method. Neither path force-kills the Codex Desktop process. The 30-second wait applies only after an actual graceful quit request.

## Storage

| Purpose | Location |
| --- | --- |
| Accounts, settings and usage cache | `%LOCALAPPDATA%\Codex Account Switcher Personal\` |
| Saved credentials | `accounts\<uuid>\auth.json` below that directory |
| Active Codex home | `CODEX_HOME`, otherwise `%USERPROFILE%\.codex` |
| Bundled Swift runtime cache | `%LOCALAPPDATA%\Codex Account Switcher Personal Runtime\<payload-hash>\` |

Both Desktop and Switcher must use the same active Codex home. Saved file credentials are protected by a current-user ACL. Windows credential writes use a private temporary file, flush and same-directory replacement. Symbolic links and junctions in credential paths are rejected. Settings and startup integration remain local.

## Develop

Install [Swift for Windows](https://www.swift.org/install/windows/) and its C++/Windows SDK dependencies, plus the [.NET 10 SDK](https://learn.microsoft.com/en-us/dotnet/core/install/windows).

```powershell
swift test
swift build -c release --product SwitcherHost
$bin = swift build -c release --show-bin-path
dotnet run --project windows/CodexAccountSwitcher.Checks -c Release -- "$bin/SwitcherHost.exe"
dotnet run --project windows/CodexAccountSwitcher.UiChecks -c Release
$env:CODEX_SWITCHER_HOST_PATH = "$bin/SwitcherHost.exe"
dotnet run --project windows/CodexAccountSwitcher -c Release
```

The native integration checks use a fresh temporary active home and storage directory. UI checks use fictional presentation data. Neither test operates the user's Desktop or actual credentials.

## Package and release

Build with `./scripts/package-windows.ps1`. The downloadable artifact is a portable, self-contained `Codex-Account-Switcher-windows-x64.exe`, including .NET and the Swift host/runtime. End users do not need either SDK. A private toolchain's runtime directory can be supplied with `-SwiftRuntimeDirectory`; a private .NET SDK can be supplied with `-Dotnet`.

The EXE's `--self-test` mode validates its bundled host against an isolated temporary Codex home. CI runs it with SDK/runtime paths removed. The EXE is unsigned and is not an installer.

Windows versions come from `windows/Directory.Build.props` and must match the macOS version sources. A single `v<major>.<minor>.<patch>` tag tests and builds both platforms, then publishes their packages together. See [release management](../docs/platform-releases.md). Windows 0.1.11 preview requires one manual upgrade to enter the unified update channel.

Each release rebuilds and tests both platforms from the same tag, then publishes one Latest release containing the DMG, EXE, and checksums. The MVP does not reuse previous packages or build caches across runs. Local packaging alone does not publish a release.

Icon resources can be regenerated from the existing artwork with `python scripts/generate-windows-icons.py` (Pillow required). Application artwork and transparent tray artwork are packaged separately.
