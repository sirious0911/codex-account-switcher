# Windows Ctrl+Q CI — GitHub 임시 실행기 AppX 탐색 Timeout 분리

작성시간: 2026-11-04 23:15:42 KST

## 배경

GitHub Actions run **41844837195**는 Release Windows 앱 소스 빌드를 통과했으나 `Check normal close and Store package path refresh` 단계에서 실패했다.

- 실패 명령은 `DesktopCloseChecks.VerifyRunningPackageIdentityAsync()` 내부 `DesktopController.DiscoverAsync(null)`의 읽기 전용 PowerShell `Get-AppxPackage OpenAI.Codex`.
- 임시 CI 러너에서 15초 제한을 초과하여 `TaskCanceledException`이 발생했고 테스트가 종료되었다.
- 실제 사용자 Windows PC에는 공식 `OpenAI.Codex` 설치가 있고, 동일 탐색 경로와 UI Automation은 앞선 실기기 READONLY 검사에서 정상 완료했다.
- **실제 Ctrl+Q 실행 실패가 아니다.** 이번 CI는 해당 단축키 테스트에 도달하기 전에 중단되었다.

## 변경

- `windows/CodexAccountSwitcher.Checks/DesktopCloseChecks.cs` EXACT1:
  - GitHub-hosted 테스트 환경에서 `GITHUB_ACTIONS=true`이면 설치되지 않은 실제 Store 패키지의 AppX 탐색 시험만 건너뛰고 `SKIP` 보고.
  - 기존 임시 소유 `ChatGPT.exe` 합성시험(패키지 버전 경로 변화, File → Quit, Ctrl+Q 표준 종료, 포커스 거부 시 기존 창/프로세스 유지)은 그대로 실행.
  - 실제 운영 `DesktopController.DiscoverAsync` 구현·경로·15초 제한·호스트 안전 경계를 **변경하지 않음**.
  - GitHub 아닌 사용자 Windows 실기기에서는 기존 실제 OpenAI.Codex 패키지 identity 검사를 그대로 수행.

## 로컬 재검증

- `GITHUB_ACTIONS=true dotnet run --project windows/CodexAccountSwitcher.Checks -c Release -- desktop-close windows/CodexAccountSwitcher.CloseFixture/bin/Release/net10.0-windows/ChatGPT.exe` → PASS
  - SKIP: 실제 Store identity가 없는 임시 러너
  - SKIP: 물리 포커스 차단으로 자동 Ctrl+Q 테스트 미실행
  - PASS: 소유 fixture의 Store 경로 식별·File Quit·fail-closed
- `dotnet build windows/CodexAccountSwitcher/CodexAccountSwitcher.csproj -c Release --nologo` → warn0/error0.
- `git diff --check` → PASS.
- 기존 사용자 root README 수정 및 미추적 기록 보존.
- 실제 Codex Desktop에 Ctrl+Q 전송·종료 요청·A/B 인증 전환 **0회**.

## 후속 Gate

- 변경 EXACT1 + 보고서 1개만 feature 브랜치 Commit/Push, 정확한 head SHA로 GitHub `windows.yml` 재실행.
- CI PASS 시 새 EXE SHA/자체시험 및 A auth 보존 확인 후 Switcher만 변경 가능.
- **실제 Codex Desktop 자동 종료 SUCCESS는 아직 미검증**, CI의 focus-gated SKIP는 성공으로 표기하지 않음.
