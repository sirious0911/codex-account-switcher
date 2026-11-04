# Codex 계정전환 개발팀 #3 — Free 30D + Active-only Gate 보류

작성시간: 2026-11-04 10:32:08 KST

## 상태
- `FREE_B_30D_ROOT_CAUSE_CONFIRMED`
- `ACTIVE_ONLY_SWIFT_SOURCE_IMPLEMENTED`
- `FREE_30D_SWIFT_AND_WINDOWS_UI_SOURCE_IMPLEMENTED`
- `WINDOWS_DOTNET_LOCAL_PASS`
- `FREE_B_LIVE_ACCOUNT_READONLY_SHAPE_PASS`
- `GITHUB_WINDOWS_SWIFT_CI_BLOCKED_422`
- `WINDOWS_NEW_PORTABLE_EXECUTABLE_NOT_DEPLOYED`
- `LIVE_A_ACCOUNT_AND_PREVIOUS_ADAPTIVE_APP_PRESERVED`
- `PRODUCTION_RELEASE_NO_GO`

## 정확한 변경
- Branch feature/provider-extensibility, commit `0dc934ad75e4f3924c897eb447cef5f9ab3f2f70`; origin SHA 일치.
- Swift new Host `refreshActive` → 계정 동기화 후 확인된 활성 프로필만 Codex usage RPC 요청. 기존 `refresh`와 5분 자동 폴링은 A/B 전체 조회 유지.
- Free B 공식 `account/rateLimits/read`에서 primary duration 43,200분(30D), no secondary, planType free, 실제 사용량·초기화시각 있음. 기존 WeeklyUsageNormalizer는 6~8일 기간만 채택하여 B unavailable를 잘못 표시했음.
- 새 `NonWeeklyUsage` 별도 모델 및 UI: 실제 30D 구간만 사용. 5H 또는 7D를 합성하지 않음. active B의 작업표시줄은 `5H ? | 30D NN% | R N`. stale는 6분 후 UNKNOWN.
- 기존 ActiveMonitorPublicationGate가 확인한 활성 계정만 외부 패널에 표시하고, 전환 시 기존 값 무효화, A 기존 값 보존.
- Mac 공용 AccountRow.swift에서도 비주간 기간 표기를 구현했으나 macOS 빌드는 미검증.

## 확인된 시험
- Windows .NET Release build 경고 0/오류 0.
- Monitor synthetic 196/196 PASS, realCodexReads 0, switches 0, Telegram 0.
- Windows GUI synthetic PASS.
- Git diff --cached --check PASS.
- B Free RPC는 공식 Codex CLI를 **별도 profile CODEX_HOME** 경로로 읽기 전용 실행하고 응답 메타데이터만 출력. 이메일·계정 ID·토큰·raw 서버 JSON은 저장/출력하지 않음.
- 사용자의 A active auth는 수정하지 않았고 B 계정전환·로그아웃·Codex Desktop 종료 없음.

## 차단
- GitHub CLI `gh workflow run windows.yml -R sirious0911/codex-account-switcher --ref feature/provider-extensibility` 2026-11-04 10:29, 10:30 KST 두 번 모두 HTTP 422: `Actions has been disabled for this repository.`
- GitHub `repos/sirious0911/codex-account-switcher/actions/permissions` 조회 결과는 `enabled=true`, `allowed_actions=all`. 이 불일치 원인은 아직 미확정이며, 서버 설정을 임의로 변경하지 않음.
- Windows 로컬 Swift compiler 없음(Get-Command swift.exe false, 통상 설치 경로 부재). 기존 EXE Swift runtime 캐시만 있으므로 새 Host `refreshActive`/Free30D를 컴파일해 실제 운영에 적용할 수 없음.
- Actions/Swift 빌드 PASS 전에는 구버전 SwiftHost를 담은 신규 EXE로 교체하지 않음(프로토콜 불일치 위험).

## 재개 조건
1. GitHub Actions 워크플로 실행 권한의 실제 422 원인 해소(기본 설정 조회와 dispatch 오류가 다른 상태).
2. branch `0dc934ad...` 또는 후속 최신 SHA를 지정해 Windows workflow_dispatch → Swift tests + Windows packaging PASS. 필요시 macOS preview source compile 별도 검증.
3. Swift runtime 포함 새 포터블 EXE의 isolated --self-test, 원본 A auth 및 A/B registry SHA 보존, 활동 중 1분 요청 A-only, Free B 30D 실화면 검증.
4. 정상 운영 적용·자동시작 경로 변경/기존 EXE 보존은 CI PASS 이후 수행.
5. Codex Desktop 자동 종료 지연, 로그인 후 재시작, Windows 리부팅 UI 지속 검증은 별도 Gate.

다음 수행 주체: 사용자/저장소 관리자 — GitHub Actions 실제 실행 불일치 해소 또는 승인된 Swift 빌드 환경 제공 → Codex 계정전환 개발팀 #3 — CI/EXE 실사용 Gate.
