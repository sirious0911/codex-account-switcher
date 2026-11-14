# Codex Account Switcher — Free-plan partial quota display guard

작성시간: 2026-11-03 15:01:32 KST

STATUS: FREE_PLAN_TRAY_PERCENT_GUARD_OFFLINE_PASS / MONITOR_SYNTHETIC_164_OF_164_PASS / PRODUCTION_NO_GO

## Fixed defect
Previously, when the user's tray preference was 5H but the account had no available 5H reading, Windows displayed the *weekly* remainder as though it were 5H. This can be misleading for a Free or otherwise limited account.

Changes:
- Shared read-only TrayUsageProjection.ActivePercent(AccountSnapshot) computes the number strictly for the selected window. Missing 5H is unknown, not a substitute 7D percentage.
- A reported 0% remains a real 0%, not UNKNOWN.
- Mutating, host error, unconfirmed active identity, absent active profile or disabled number mode return null and show the standard tray icon.
- App.xaml.cs now uses this projection; no additional Codex polling or account storage introduced.

## Verification
- `dotnet run --project windows/CodexAccountSwitcher.MonitorChecks -c Release`: 164/164 PASS; realCodexReads=0 actualAccountSwitches=0 telegramSends=0. Includes 8 new targeted tests.
- `dotnet run --project windows/CodexAccountSwitcher.UiChecks -c Release -- windows/artifacts/ui-free-plan-guard`: PASS, native docking DPI144 / real screen 1.
- `dotnet build windows/CodexAccountSwitcher/CodexAccountSwitcher.csproj -c Release --nologo`: PASS, warnings=0 errors=0.
- `git diff --check`: PASS.
- Existing README.md and all unrelated untracked documents/scripts preserved.

## Important separate limitation
This change corrects *tray projection only*. Swift WeeklyUsageNormalizer currently requires a 6–8-day quota window; if Free B returns only 5H and no 7D, the *underlying usage fetch* may report unavailable. Whether Free B actually lacks 7D is not established and cannot be assumed without its official provider response. Never invent weekly percentages or reset credits.

## Live gate
- B account interactive browser login/registration remains pending the user's return.
- Launching normal Fork may import/copy current Codex auth into private Fork store; not done here.
- No Codex desktop interruption, installation, login, account switch, notification, or auth file change.
- Branch: feature/provider-extensibility / personal sirious0911 repository only.
- The old offline close remains valid for its frozen SHA; this is a narrow post-close real display defect fix.

Next: stage only MonitorOverview.cs, Program.cs, App.xaml.cs, and this report; commit and push personal feature branch once; examine CI. Leave B registration until user can interactively sign in.
