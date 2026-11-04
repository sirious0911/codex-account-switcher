# Codex Account Switcher — 개인 Fork

Codex Desktop 계정 전환과 사용량 모니터를 함께 확장하기 위한 개인 개발 저장소입니다.

**우리 저장소:** https://github.com/sirious0911/codex-account-switcher

이 저장소는 [Zhao Liu의 오픈소스 원본](https://github.com/liuzhao1225/codex-account-switcher)을 기반으로 합니다. 원저작권 고지와 MIT 라이선스는 [LICENSE](LICENSE)에 그대로 유지합니다. **개발·배포·이슈 관리는 개인 저장소에서 진행하며 원본 GitHub에 변경을 요청하지 않습니다.**

## 기능과 개발 상태

- 원본 Windows WPF / macOS SwiftUI 계정 전환 및 업데이트 기능 유지
- Codex 5시간·주간 사용량과 리셋권 표시
- Codex/Claude 서비스별 계정과 사용량을 구분하는 Swift 공통 계약
- 기존 독립 사용량 모니터와 호환되는 읽기 전용 Swift 데이터 어댑터
- Windows 읽기 전용 모의 모니터 UI 모델 및 .NET 시험
- Claude Code 및 Claude Desktop 실제 연동, Telegram 실전송, 전체 Swift 네이티브 통합은 **추후 개발**

원본 기능을 임의로 삭제·차단하지 않습니다. 계정 전환 기능은 사용자가 선택·확인하는 흐름을 유지합니다.

## 코드·다운로드

- **개인 소스:** https://github.com/sirious0911/codex-account-switcher
- **개인 Releases:** https://github.com/sirious0911/codex-account-switcher/releases
- **개발 중 PR:** https://github.com/sirious0911/codex-account-switcher/pull/1

> 현재 개인 저장소에 배포된 Release가 없습니다. Windows EXE와 macOS DMG를 다운로드할 수 있게 되면 **개인 Releases**에 게시합니다. 원본 저장소의 파일은 우리 수정본이 아닙니다. `@sirious0911/codex-account-switcher` npm 패키지도 아직 공개 배포되었다고 보장할 수 없습니다.

### 로컬 개발

```bash
git clone https://github.com/sirious0911/codex-account-switcher.git
cd codex-account-switcher
git switch feature/provider-extensibility

# 설치된 환경에 맞게 실행
swift test
node --test scripts/tests/*.test.mjs
dotnet build windows/CodexAccountSwitcher/CodexAccountSwitcher.csproj -c Release
dotnet run --project windows/CodexAccountSwitcher.UiChecks -c Release
dotnet run --project windows/CodexAccountSwitcher.MonitorChecks -c Release
```

Windows Swift 호스트는 Swift Windows 빌드 도구가 필요하며, Windows .NET UI 빌드만으로 Swift 전체 통합을 확인했다고 볼 수 없습니다. macOS 빌드·배포에도 별도의 Swift/macOS 및 서명 환경이 필요합니다.

## 개인 저장소 운영

실행 시 저장소·업데이트·npm 다운로드·릴리스 피드가 **sirious0911/codex-account-switcher**를 참조하도록 개발합니다. Git 원격 `origin`도 개인 저장소입니다. 과거 프로젝트 설명이나 원저작자 출처를 기록한 파일의 원본 링크는 출처 표기로 남을 수 있으며, 개인 빌드 파일의 다운로드 주소와 구별합니다.

GitHub Pages 사이트와 서명된 macOS Sparkle 업데이트 피드의 실제 운영은 개인 저장소의 Pages 및 배포 서명 설정 후 활성화할 수 있습니다. 기존 원저작자의 서명 자격증명을 사용하지 않습니다.

## 저작권

원본 프로젝트: [liuzhao1225/codex-account-switcher](https://github.com/liuzhao1225/codex-account-switcher) — Copyright (c) 2026 liuzhao1225, MIT License.

개인 Fork의 추가 기능은 원본 라이선스 조건을 준수하면서 개발합니다.
