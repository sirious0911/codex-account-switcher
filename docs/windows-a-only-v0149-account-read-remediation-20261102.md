# Codex Account Switcher — Codex v0.149.0 A-only identity contract remediation

작성시간: 2026-11-02 16:01 KST

STATUS: V0149_ACCOUNT_READ_SCHEMA_REMEDIATED / AUTH_FILE_PINNED_ONLY / OFFLINE_PASS / V1_LIVE_CONSUMED / NEW_LIVE_NOT_APPROVED / PRODUCTION_NO_GO

## Incoming evidence

- CodexPro #14 actual A-only exact1: LIVE_A_IPC_INCOMPLETE; no observed real 5H/7D/credits; old gate consumed, not rerun.
- #14 report: C:\Codex\projects\codex-usage-monitor-prototype\docs\DEV14_20261102_A_ONLY_LIVE_EXACT1_INCOMPLETE_ACCOUNT_READ_SCHEMA_FIX_REQUIRED.md
- #14 SHA256: fd49bab339933ef7ace113f8c5925be95a9f48035b60ebdbf9da97f5f120906f.
- #14 confirmed original auth.json SHA256 unchanged, Fork profile store absent, no normal app launch, no real switch. Exact runtime failure code was not persisted: root cause suspected but NOT conclusively attributed to account/read.

## Official source inspected on pinned tag

- https://github.com/openai/codex/blob/rust-v0.149.0/codex-rs/app-server-protocol/src/protocol/v2/account.rs
- https://github.com/openai/codex/blob/rust-v0.149.0/codex-rs/app-server-protocol/src/protocol/common.rs
- GetAccountResponse has account (optional Account) and requiresOpenaiAuth. Account.Chatgpt exposes type=chatgpt, email (optional) and planType. It DOES NOT expose accountId/chatgptAccountId/id.
- The stable account/read method alone cannot prove equality between RPC provider account ID and the account_id in auth.json. Email equality is not exact provider-ID verification.
- This report does NOT assert a supported, stable, read-only RPC capable of providing the same provider ID in this target version. No experimental account-session mutation or session-switch method was invoked.

## Source changes in personal Fork

- scripts/active-a-no-copy-contract.mjs: replace fabricated account ID RPC matcher with assessV0149AccountRead using official ChatGPT response shape. Require valid local credential account ID; reject unsupported account type, malformed response and explicit email mismatch. Return AUTH_FILE_PINNED with accountIdConfirmed=false even when optional emails agree.
- scripts/active-a-no-copy.mjs: synthetic scenario now has official account/read form. Separate consumed --live-a-once hard block from new, unapproved --live-a-once-v2. Require a new environment/argument gate and a new private diagnostic receipt path before any app-server start.
- script records sanitized stage/reason, rpcCalls, accountReadCalls, rateLimitCalls, pipeWrites and identityScope in an exclusively created receipt file. The receipt uses wx + mode 0600, must be outside .codex and the Fork personal registry, and cannot overwrite an existing file. Note: Windows ACL enforcement is not independently verified by POSIX mode.
- In the hypothetical v2 path, label a successful usage observation LIVE_A_AUTH_FILE_PINNED_OBSERVED, accountIdentityConfirmed=false, accountIdVerifiedByRpc=false, identityScope=AUTH_FILE_PINNED_RPC_ID_UNVERIFIED. This is not a confirmation of the real provider ID.
- The IPC raw.accountId, when emitted, is derived from the pinned local auth file, not the account/read response, and is NOT proof of a verified active account. CodexPro #14 must label this accordingly, including in its WPF.
- scripts/tests/active-a-no-copy.test.mjs: actual v0.149.0 ChatGPT fixture (no account ID), honest non-verification, conflict errors, old gate blocked, new gate disabled, sanitized private QA receipt exact-once and unchanged receiver quota contract.

## Offline verification

- Focused Node tests: 9 PASS, 0 FAIL.
- All Node tests: 60 PASS, 1 SKIP, 0 FAIL.
- Existing Windows MonitorChecks: 85 PASS, real Codex reads 0, actual switches 0, Telegram sends 0.
- node --check and --synthetic: PASS, credential bytes read 0, pipe writes 0, rate-limit calls 0.
- git diff --check: exit 0, existing Windows LF/CRLF normalization warnings only.

## Work and safety scope

- No actual Codex A read, login, logout, refresh or account switch in this remediation.
- Consumed A-only exact1 LIVE gate NOT re-used. The v1 command hard blocks even with its old approval variable.
- A new LIVE gate is NOT approved or executed; the new v2 command is fail-closed by default.
- Do not treat stable email consistency or auth file pinning as exact provider account identity proof.
- No changes to normal Windows WPF account-switcher initialization, multi-account refresh, profile credential import, auth.json or #14 receiver project.
- No main merge, production installation, deployment, release or Work Window. Other preexisting uncommitted files preserved.

## Next actor

CodexPro development team #14: independently review official v0.149.0 contract and the new source; update its WPF status contract to distinguish AUTH_FILE_PINNED_RPC_ID_UNVERIFIED from confirmed account ID, verify sanitized private receipt and new gate conditions, without using the consumed live gate.
Only after a newly authorized one-time live test: corroborate real values and receiver behavior and explicitly report any residual unverified identity, app-server cache or credential side effects.
Codex Account Switcher development #2: SOURCE/test/feature branch CI gate, then STOP.
