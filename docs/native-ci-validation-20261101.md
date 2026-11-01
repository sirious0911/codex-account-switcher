# Personal fork native CI verification — 2026-11-01 KST

## Scope

User-owned source repository: https://github.com/sirious0911/codex-account-switcher

Verified application source commit:
`f34fda087d013d2ba7cc3bd248bc2f21a1ee50ae`
on `feature/provider-extensibility`.

The *verification workflow itself* is separately committed on the user-owned fork's `main` branch; it checks out the development branch explicitly, and is never a release/publishing workflow.

## Completed CI evidence

### CI run 1 — entire source regression PASS

https://github.com/sirious0911/codex-account-switcher/actions/runs/41276124136

- macOS 26 runner, Swift 6.3.3 `swift test`: XCTest 20 passed, Swift Testing 126 tests passed, failures 0.
- Windows GitHub runner: WPF Release build PASS, UI synthetic checks PASS, monitor checks 27/27 PASS.
- Ubuntu runner: Node 30 PASS, 1 SKIP, 0 FAIL, site URL consistency PASS.
- All three GitHub Actions jobs concluded SUCCESS.

### CI run 2 — app-bundle packaging PASS

https://github.com/sirious0911/codex-account-switcher/actions/runs/41276608742

- Three jobs SUCCESS including macOS full `swift test`.
- `scripts/package-local-app.sh` created `Codex Account Switcher.app` on the ephemeral macOS arm64 runner.
- Ad-hoc signing and `codesign --verify --deep --strict` succeeded.
- Package `SUFeedURL` was read directly from `Info.plist` and matched `https://sirious0911.github.io/codex-account-switcher/updates/macos/appcast.xml`.
- This is **not** an Apple Developer ID signature, notarization, DMG Release, or installed production application.
- Default Windows runner did not have the Swift compiler installed; Windows Swift `SwitcherHost` was not verified in this run.

## Pending separate Windows Swift native gate

An additional ephemeral Windows runner test uses Swift setup on GitHub Actions to attempt `swift build -c release --product SwitcherHost` and full `swift test`.

CI job link: https://github.com/sirious0911/codex-account-switcher/actions/runs/41276764453 (Windows Swift job awaiting toolchain installation at first observation). Its result must be recorded as PASS only after the corresponding job finishes successfully.

The personal Windows computer has **not** received a new Swift, C++ SDK, Visual Studio or other host-wide installation.

## Unchanged operational boundaries

- The user's original GitHub repository remains unmodified.
- Existing upstream reset-credit and update features were restored and not blocked in this fork.
- Real Codex accounts, credentials, account switching, credit use, Telegram and updater network requests were not exercised by these synthetic/CI tests.
- No GitHub Release, npm publish, personal Pages deployment, main merge, user EXE installation or production macOS app installation was performed.
- Personal repo still requires a real signed Windows native Swift runtime before producing a distributable Windows EXE, and a personal Apple signing/notarization setup before public Mac release.
