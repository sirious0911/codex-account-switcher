# Codex Account Switcher — A-only no-copy diagnostic handoff

작성시간: 2026-11-02 14:51 KST

STATUS: A_ONLY_NO_COPY_DIAGNOSTIC_SOURCE_COMPLETE / OFFLINE_NODE_PASS / LIVE_A_NOT_RUN / PRODUCTION_NO_GO

## Incoming report

- CodexPro development #14 report docs/DEV14_20261102_A_ONLY_LIVE_PREFLIGHT_AUTH_COPY_HOLD.md, SHA256 7735a7cac0091790047804908fab9570409e9af78333bd99768c8a5bdae4c82e.
- Existing Fork normal startup calls AccountController.start() -> AccountStore.syncActiveCredential() -> registerActiveIdentity() and can copy active auth.json into the personal profile store. Starting the normal EXE is not a read-only preflight.
- User's original auth.json existed; Fork personal account registry did not exist at development #14 preflight. No new account login or actual switch has been approved within this work.

## Implemented alternative (diagnostic only, not installed app)

- New scripts/active-a-no-copy-contract.mjs — strict identity matching and receiver-wire schema normalizer.
- New scripts/active-a-no-copy.mjs — independent Node A-only one-shot diagnostic (separate from SwiftHost/AccountController and normal WPF app initialization).
- New scripts/tests/active-a-no-copy.test.mjs — offline identity, data-shape, mutation-method exclusion and execution-gate fixtures.
- Default invocation does not touch live Codex. Only --synthetic performs an offline fixture; --live-a-once requires explicit environment gate and explicit acknowledgement that the Codex app-server process itself might have side effects.
- In the optional live mode only: read local original auth.json into memory, extract local account ID, verify account/read with refreshToken=false returns the exact same provider account ID, perform account/rateLimits/read once, recheck SHA256 of original auth file, then send a single frame to codex-usage-monitor-switcher-v1 and temporarily hold the pipe open. Fail closed on mismatch, unknown identity, missing/invalid rate limits, or changed auth file.
- The source itself does NOT create or copy auth credentials, profile directories, accounts.json, settings, usage caches, registry entries, login/browser sessions, switches, reset-credit consumption or Telegram messages.
- Live output suppresses raw account identifiers, email, credential fields, and full App Server payload. The external IPC retains kind, observedAtMs, raw; raw contains a provider account ID and quota counters for the trusted local receiver.
- This path intentionally does not change the normal Codex account-switcher architecture. Multi-account refresh and active-profile UUID IPC gate remain unchanged.
- A non-default CODEX_HOME is rejected for this A-only diagnostic pending separately verified mapping.

## Latest Windows EXE provenance verified (READONLY, never executed)

- GitHub repo: sirious0911/codex-account-switcher; commit dd343646a8aa2e0d89b5e0aafc796fa4f762ca77.
- GitHub Actions run: https://github.com/sirious0911/codex-account-switcher/actions/runs/41368819821; artifact ID 13108746711.
- GitHub artifact ZIP SHA256 reported by API: 440c9ada5757d08d05f06529d84f10043a1775b3609fdac2c89aef51cf9a5b19.
- Actual artifact downloaded to IGNORED windows/artifacts/ci-dd34364/ and self-contained EXE SHA256 verified against GitHub-provided .exe.sha256 using sha256sum -c: PASS.
- Latest CI EXE SHA256: 91ee82a3147ff46fee6fc427ba54841f5d666c90ab6b8775ad3084fdfc8477ab, size 102599362 bytes.
- Existing older CI EXE from 715a3db: 7db88290f1c2fb767d2cc9970760f78faf4e06f814015f433ec883615ace4c72, size 102593456 bytes; not identical to latest.
- Local .NET Release build EXE is 239104 bytes (not self-contained artifact); it is not the latest CI EXE.
- Latest downloaded EXE Authenticode status: NotSigned. Hash-consistency with personal GitHub CI is established, software publisher signature is NOT established.

## Offline verification

- node scripts/active-a-no-copy.mjs --synthetic: SYNTHETIC_PASS, credential bytes read=0, quota calls=0, pipe writes=0.
- node --test scripts/tests/active-a-no-copy.test.mjs: 7 PASS.
- node --test scripts/tests/*.test.mjs: 58 PASS, 1 SKIP, 0 FAIL.
- git diff --check: PASS (existing README line ending conversion warning only).
- No live auth read, no app-server launch, no external named pipe usage, no real account usage API call, no EXE normal startup, no account state change, no operational deployment.

## Explicit limitations and remaining gates

1. This is a separate Node diagnostic, not a normal EXE mode. It does not demonstrate the full production SwiftHost -> CoreClient -> active-profile gate path with live account A. That integration remains unverified.
2. A real Codex app-server process can write logging, caches or credentials even when the caller performs only read RPC methods. Post-execution auth hash check can DETECT a changed auth.json, not prevent or roll back side effects. The mode must not be advertised as guaranteed filesystem-read-only.
3. The one-shot pipe stays up briefly and then disconnects, causing #14 minibar UNKNOWN. No automatic polling/refresh was implemented. This is for a supervised A-only functional proof, not daily use.
4. Latest CI self-contained EXE can run full app, but full startup still imports auth. Never launch that normal EXE to claim the no-copy gate.
5. Separate scoped LIVE approval and post-test receipt are required before --live-a-once is invoked. Verify Windows A account identity, Codex Desktop state and test receiver first; measure pre/post auth hash and Fork store absence.
6. Development #14 remains owner for WPF real A usage observation and 2m/6m display validation. The user-approved feature branch and CI may be used for source-only checks; no main merge or Release.

Next owner: Codex Account Switcher development #2 for source/CI; CodexPro development #14 for supervised WPF integration after LIVE gate.
