# Windows read-only monitor preview integration gate

Date: 2026-11-01 KST

## Scope

This is **synthetic-only integration** between the Codex account-switcher Windows core and the independent monitor data shape. It does not attach the Windows UI to a real monitor, load auth.json, query app-server, install a background worker, or send notifications.

The independent monitor remains at C:\Codex\projects\codex-usage-monitor-prototype and is unchanged.

New files:
- windows/CodexAccountSwitcher.Core/MonitorSyntheticPreview.cs
- windows/CodexAccountSwitcher.MonitorChecks/CodexAccountSwitcher.MonitorChecks.csproj
- windows/CodexAccountSwitcher.MonitorChecks/Program.cs

The reader handles the Swift CodexMonitorPayloadEncoder schema, retains UNKNOWN values, requires provider account identity, validates 5-hour and weekly duration, prevents negative or nonnumeric credits/percent, caps JSON input to 8192 UTF-8 bytes, and renders MOCK-labelled values without exposing provider ID, email, or name.

## Direct Windows evidence

Command:
C:\Program Files\dotnet\dotnet.exe run --project windows/CodexAccountSwitcher.MonitorChecks/CodexAccountSwitcher.MonitorChecks.csproj -c Release

Result: 27/27 PASS, real Codex reads 0, actual account switches 0, Telegram sends 0.

Upstream Windows application:
C:\Program Files\dotnet\dotnet.exe build windows/CodexAccountSwitcher/CodexAccountSwitcher.csproj -c Release --nologo

Result: success, 0 warnings, 0 errors.

Existing upstream synthetic UI checks:
C:\Program Files\dotnet\dotnet.exe run --project windows/CodexAccountSwitcher.UiChecks/CodexAccountSwitcher.UiChecks.csproj -c Release

Result: PASS.

## Strict limitations and security decisions

- This read-only parser is a data display surface, not a verified authorization or real-time usage source.
- Even a valid snapshot **does not** authorize or prove an early-reset alert. Do not infer permission or plan.
- There is no live IPC between Swift and Windows, no real usage read, and no integration into the running WPF window.
- The original upstream exposes an auto-reset setting that may cause automatic reset-credit consumption. Its safety has not been approved. Do not install or run the upstream/fork in a live profile until that path is explicitly disabled and independently tested.
- Full Windows Swift host build and macOS full suite remain HOLD; Windows PC currently lacks Swift, Visual Studio Build Tools, Windows SDK and WSL Linux. Do not silently install host dependencies.
- Native Swift Linux isolated tests (20/20) are **not** full upstream Swift tests.
- All changes are in the user-owned fork feature branch; main stays intact.

## Future Gate

1. Independently review any account-changing or credit-consuming code path before connecting live profile data.
2. Obtain user approval for any host-wide Swift toolchain or native build-tools installation, or use an isolated CI runner enabled for the fork.
3. Verify real cross-language snapshot from compiled Swift with Windows display-only reader.
4. Connect existing monitor through a bounded, one-way, local authenticated data channel, initially using fake data.
5. Run full Swift regression and WPF regression, review files changed and credential safety.
6. Only then discuss actual account-switching and Telegram separate LIVE gates.

No real credential, automatic switching, reset-credit consumption, Telegram send, or GitHub main merge is approved here.
