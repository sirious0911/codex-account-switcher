# Codex 계정전환 개발팀 #3 — GitHub Actions HTTP 422 독립 진단 및 재개 조건

작성시간: 2026-11-04 11:07 KST

## 결론

**GitHub 워크플로 소스나 단순 저장소 Actions 설정 오류로는 설명되지 않는 실행 차단.** GitHub API가 workflow_dispatch를 HTTP 422 `Actions has been disabled for this repository.`로 거부한다. GitHub 내부 계정/저장소 제한 또는 GitHub backend 상태 불일치 가능성이 있으나 **원인 미확정**. 해당 접근제어/서비스 차단을 우회하거나 워크플로를 다른 저장소로 복제하여 실행하지 않음.

## 확인된 원문 증거

- Repository: `https://github.com/sirious0911/codex-account-switcher`
- Personal fork: public, fork=true, archived=false, disabled=false, owner repo admin=true.
- Branch: `feature/provider-extensibility`, latest SOURCE code before this document: `70b0867e5833487f53f743061d984ce729d7276c`.
- Default branch: `main`.
- GitHub API `GET /repos/sirious0911/codex-account-switcher/actions/permissions`:
  `{"enabled":true,"allowed_actions":"all"}`
- `GET .../actions/permissions/workflow`: `default_workflow_permissions=read`; read access is sufficient for build jobs and workflow dispatch authorization uses caller workflow scope.
- `GET .../actions/workflows`: 7 workflows registered, each `state=active`, including Windows workflow ID `403302487`, path `.github/workflows/windows.yml`.
- `gh workflow view windows.yml -R sirious0911/codex-account-switcher --yaml`: `workflow_dispatch` exists in the default branch YAML (along with `workflow_call`).
- GitHub authenticated via `gh` as repository owner; token OAuth scopes `repo, workflow` (actual token never displayed or saved).
- Last successful Windows run ID `41544831551`, timestamp `2026-11-02T23:21:43Z`, head SHA `1db9620ce957638fe3251be71680ab4ea8e5beb4`.
- After that, no new GitHub Actions workflow run was created for subsequent feature-branch commits.
- Reproduction: `gh api -i -X POST repos/sirious0911/codex-account-switcher/actions/workflows/403302487/dispatches -f ref=feature/provider-extensibility`
- Reproduced at `2026-11-04T02:02:32Z` / `2026-11-04 11:02:32 KST`.
- Result HTTP `422`: `{"message":"Actions has been disabled for this repository.","documentation_url":"https://docs.github.com/rest/actions/workflows#create-a-workflow-dispatch-event","status":"422"}`.
- GitHub request ID: `DA42:115017:1FBC27:357E7E:6AEA9238`. This is an API support tracing identifier, **not an access credential**.
- Public GitHub status https://www.githubstatus.com/ lists Actions operational when checked; there was a separate Git Operations degradation. This does not rule out account/repo-specific service restrictions.
- GitHub community reports from October-November 2026 document similar enabled=true / workflow active / dispatch 422 mismatch, e.g. https://github.com/orgs/community/discussions/209967 . This is pattern evidence, **not proof of the user's root cause**.

## Local Swift fallback assessment

- User Windows machine: `swift.exe`, `swiftc.exe`, `clang.exe`, `docker.exe`, Visual Studio Build Tools and normal Swift Windows toolchain locations not found. `wsl.exe` exists but `wsl --list --quiet` reports WSL not installed. No OS toolchain installation attempted.
- Separate isolated analysis environment has Swift 6.2.1 on Linux, but has **no network/DNS access to clone the user's GitHub repository**; archive acquisition unsuccessful. Hence no actual Swift parse, compile or unit-test PASS for current code.
- Previous validated Windows executable with older Swift runtime stays installed. New Swift/Free30D implementation is SOURCE-only.
- Windows .NET local unit/regression checks were previously `196/196 PASS`, but **do not substitute Swift CI**.

## Practical remediation and responsible boundary

1. Repository owner can open https://github.com/sirious0911/codex-account-switcher/settings/actions to visually confirm Actions configuration, and https://github.com/sirious0911/codex-account-switcher/actions to inspect any account-specific notification/banner. No evidence the repository setting itself is disabled.
2. GitHub account email verification and billing/Actions spending/usage state require owner-authenticated UI review; OAuth token has no `user` scope, so `GET /user/emails` and `GET /users/sirious0911/settings/billing/usage` both return 404 without providing a verified/usable state. **Do not infer email-unverified or budget exhaustion.**
3. Because the API has a contradictory enabled/disabled state, request GitHub service-side investigation via https://support.github.com/ or https://github.com/orgs/community/discussions/categories/actions . For GitHub Free, support options may be limited; account/security restrictions can have different channels. Never send passwords, GitHub tokens, repository secrets, or personal email to public discussions.
4. Do **not** disable/re-enable settings as a bypass, change account, create another repository to evade service restrictions, or push meaningless commits hoping to generate checks.
5. Re-run the existing Windows workflow only after GitHub access state is legitimately restored. Then require Swift Testing + Windows portable packaging from the **exact new head SHA**, artifact SHA verification, A-credential protection and original taskbar layout regression before LIVE replacement.

## Copy-ready Support issue (English)

Subject: Workflow dispatch HTTP 422 “Actions has been disabled for this repository” while Actions settings and workflow are active

Hello GitHub Support,

I own the public fork https://github.com/sirious0911/codex-account-switcher. I am the repository administrator, and Actions workflows previously ran successfully; the most recent successful Windows workflow was run 41544831551 at 2026-11-02T23:21:43Z.

Now, workflow_dispatch for the active workflow `.github/workflows/windows.yml` (workflow ID 403302487) fails before a run is created:

`gh api -X POST repos/sirious0911/codex-account-switcher/actions/workflows/403302487/dispatches -f ref=feature/provider-extensibility`

Response: HTTP 422, `Actions has been disabled for this repository.`

Reproduced at 2026-11-04T02:02:32Z. Request ID: DA42:115017:1FBC27:357E7E:6AEA9238.

However, GET `/repos/sirious0911/codex-account-switcher/actions/permissions` returns enabled=true and allowed_actions=all, the Windows workflow is state=active, and workflow_dispatch is present on the default branch. My authenticated GitHub CLI has repo/workflow scopes.

Could you investigate whether there is an Actions service-side restriction or repository execution eligibility mismatch and advise the exact permitted steps to restore workflow execution? I do not want to bypass any account/repository security restriction.

Thank you.

## Handoff

SOURCE IMPLEMENTED / DOTNET TESTED / SWIFT CI & EXE DEPLOYMENT HOLD.
Existing Windows A account and 1-minute adaptive binary preserved.
Next actor: GitHub owner/support to clarify/re-enable Actions eligibility, then Codex 계정전환 개발팀 #3 to resume exact-head Swift CI and LIVE integration.
