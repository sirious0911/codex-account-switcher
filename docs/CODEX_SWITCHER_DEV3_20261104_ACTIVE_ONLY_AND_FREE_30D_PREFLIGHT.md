# Codex 계정전환 개발팀 #3 — 활성 계정 전용 조회 및 무료 B 30일 사용량 지원

작성시간: 2026-11-04 10:27:28 KST

## 검증된 사실 및 원인

- 사용자 Windows 개인 Fork는 등록 계정 A/B 2개, 활성 계정 A, 기존 A 인증파일과 개인 저장 A 사본 해시 일치.
- 계정 관리 UI에서 B는 `Usage unavailable`; Tooltip 오류 분류 `No weekly Codex Usage window is available.`를 확인.
- 인증 원문/토큰/이메일/계정 ID를 노출하지 않는 읽기 전용 B 전용 Codex app-server `account/rateLimits/read` 조회를 1회 실행.
- 실제 B 요약 형식: `planType=free`, `rateLimitsByLimitId.codex.primary.windowDurationMins=43200`, `secondary=null`, primary의 usedPercent/resetsAt 존재, resetCreditAvailableCount 존재. **30D 사용량은 제공되고 5H/7D 구간은 없다.**
- 기존 `WeeklyUsageNormalizer.normalize()`는 6~8일 구간이 없으면 `weeklyUsageUnavailable` 오류. 무료 B의 30D 잔여율이 UI에 누락된 직접 원인임.
- 공식 app-server 참고: https://learn.chatgpt.com/docs/app-server
- 기존 Windows 적응형 1분 `refresh` 호출은 Swift `performWeeklyUsageRefresh`가 **등록된 모든 계정**을 조회해서, A 작업 중 B 무료 계정까지 호출하는 불필요한 트래픽이 있었음.

## 변경

- `Sources/SwitcherCore/AccountController.swift`: `refreshWeeklyUsage`(기존 전체 조회)와 `refreshActiveUsage`(신원동기화 후 활성 프로필 하나만 조회)를 분리; 기존 5분 자동조회·계정전환 후 즉시 전체조회 보존. `AccountClient.readUsage()`를 통해 주간/비주간 응답 분리.
- `Sources/SwitcherHost/Host.swift`: `refreshActive` 조회 전용 명령 추가, 승인·계정 변경·토큰 공개 기능 없음.
- `windows/CodexAccountSwitcher/AdaptiveUsageRefreshService.cs`: 적응형 1분 요청을 `refreshActive`로 변경.
- `Sources/SwitcherCore/CodexClient.swift`: 한 번의 공식 RPC 응답에서 7D가 있으면 기존 WeeklyUsage 계산 그대로, 없지만 유효한 duration이 있으면 `NonWeeklyUsage`로 표시. 정확한 windowDurationMins(예: 43200 → 30D)를 보존하며 5H/7D를 생성하지 않음. 모든 기간이 없으면 기존 unavailable 오류.
- `Sources/SwitcherCore/Models.swift`: 비주간 잔여율·기간·실제 reset 시각·creditCount·관측시각을 가진 `NonWeeklyUsage`, `UsageViewState.nonWeekly/staleNonWeekly` 추가. 기존 WeeklyUsage 필드/캐시 형식은 변경하지 않음.
- `Sources/SwitcherCore/AccountSnapshot.swift`, `windows/CodexAccountSwitcher.Core/AccountSnapshot.cs`: Swift→Windows presentation-only optional `nonWeeklyUsage` DTO 확장. credentials/auth 관련 전송 추가 없음.
- `Sources/CodexAccountSwitcher/AccountRow.swift`: macOS Free 30D 등 실제 서버 구간 표시(기존 5H/7D 화면 보존).
- `windows/CodexAccountSwitcher/MainWindow.cs`: Free B 등록 계정에 실제 30D 퍼센트/리셋 시각/credits 표시, 기존 A 화면 보존.
- `windows/CodexAccountSwitcher/TaskbarQuotaPanel.cs`, `App.xaml.cs`: 활성 신원이 확인된 비주간 새 응답만 `5H ? | 30D NN% | R N`로 표시. 이전 계정 샘플과 표시 값 공유하지 않음. observedAt 6분 초과 시 `?`, 5H/7D 별도 표시를 변조하지 않음. 기존 UI 크기·배치·세로 중앙 정렬 유지.
- `Tests/SwitcherCoreTests/CredentialSyncTests.swift`: A/B 모의 인증으로 활성 전용 fast query 1회, 전체조회 2회 및 B 전환 후 active-only 선택 검증.
- `Tests/SwitcherCoreTests/WeeklyUsageNormalizerTests.swift`: Free30D, 기존 5H/7D, 15분 구간, 미제공 구간을 합성 공식 응답으로 검사.
- `windows/CodexAccountSwitcher.MonitorChecks/Program.cs`, `windows/CodexAccountSwitcher.UiChecks/Program.cs`: DTO와 트레이 기간 오표기 금지, 계정 변경 시 비주간 수치 무효화 검사.
- `windows/README.md`: 실사용 반영 예정 사항과 범위 문서화.

## 로컬 검증

- `dotnet run --project windows/CodexAccountSwitcher.MonitorChecks -c Release` → `MONITOR_SYNTHETIC_PREVIEW_PASS checks=196 realCodexReads=0 actualAccountSwitches=0 telegramSends=0`.
- `dotnet run --project windows/CodexAccountSwitcher.UiChecks -c Release -- windows/artifacts/ui-free30d-source-20261104` → PASS.
- `dotnet build windows/CodexAccountSwitcher/CodexAccountSwitcher.csproj -c Release --nologo` → warning0/error0.
- `git diff --check` → PASS.
- Swift 코어 Windows 로컬 Swift 컴파일러는 아직 없어 GitHub Actions `Windows` workflow_dispatch로 독립 빌드 및 `swift test` 진행 예정.
- **현재 운영 앱 PID15552는 구형 Swift 코어 포함: 아직 B 30D 신규 UI/active-only 명령이 적용된 운영본 아님.** GitHub CI 확인 전 운영 교체 금지.
- A/B 계정 원장과 인증정보 원문 변경 없음. B 모의/실제 전환 수행 0.
- 사용자 사전 root README 수정 및 기존 미추적 프로젝트 문서 보존.

## 미검증/제약

- Mac 신규 공용 UI/Swift 합성 테스트는 CI 결과 전 미확정.
- Free B 30D 잔여율은 실제 공식 응답에서 available을 확인했으나 인증보호를 위해 원시 응답·계정 식별정보 미저장.
- 비주간 사용량은 WeeklyUsage 파일 캐시를 오염시키지 않고 메모리에서만 유지. 앱 재시작 후 첫 유효 응답 전까지 UNKNOWN 가능.
- Windows 작업표시줄 창은 비공식 SetParent 호스팅으로 업데이트 호환성 보장 불가.
- Codex Desktop 종료 지연·Windows 재부팅 후 정상 실행·macOS 실제 장비 테스트 별도.
- 본 Gate는 아직 SOURCE_PREFLIGHT, 정식 릴리스 금지.

## 다음 수행 주체

Codex 계정전환 개발팀 #3 → 정확한 변경파일만 feature 브랜치 Commit/Push, GitHub Windows 워크플로를 branch 명시로 수동 시작, 결과 PASS 시 새 Swift runtime 포함 EXE 설치·실기기 B/A UI 검증.
