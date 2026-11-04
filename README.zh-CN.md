# Codex Account Switcher — 个人 Fork

个人开发仓库：[sirious0911/codex-account-switcher](https://github.com/sirious0911/codex-account-switcher)

本项目基于 [Zhao Liu（liuzhao1225）的原始开源项目](https://github.com/liuzhao1225/codex-account-switcher)，保留原始 MIT 许可证和版权声明。**本 Fork 的代码修改、发布和问题跟踪都在个人 GitHub 仓库中进行。**

## 开发功能

- 保留原版 Windows WPF、macOS SwiftUI 账户切换和更新功能
- Codex 5 小时、每周用量与重置次数
- Codex / Claude Code / Claude Desktop 的通用数据模型
- 适配现有独立用量监控工具的只读 Swift 数据桥接
- Windows 合成数据用量显示解析和 .NET 测试
- Claude 实际登录/切换与 Telegram 真实通知：仍未完成

## 下载及代码

- [个人仓库源码](https://github.com/sirious0911/codex-account-switcher)
- [个人仓库 Releases](https://github.com/sirious0911/codex-account-switcher/releases)
- [当前 Draft PR](https://github.com/sirious0911/codex-account-switcher/pull/1)

**注意：目前个人仓库尚未发布 EXE/DMG 安装包，也未开通 GitHub Pages。** 原作者仓库的安装包不包含本 Fork 的新增功能。安装器的计划包名是 `@sirious0911/codex-account-switcher`，并不表示该包已经发布至 npm。

## 开发测试

```bash
git clone https://github.com/sirious0911/codex-account-switcher.git
cd codex-account-switcher
git switch feature/provider-extensibility
swift test
node --test scripts/tests/*.test.mjs
dotnet build windows/CodexAccountSwitcher/CodexAccountSwitcher.csproj -c Release
```

完整 Windows Swift Host 与 macOS 应用构建仍需对应原生工具链。当前 Fork 不会自动同步原版 GitHub 修改；原版仍仅作为版权和来源参考。

## 开源许可

原项目作者：Zhao Liu（liuzhao1225）— Copyright (c) 2026 liuzhao1225。适用 [MIT License](LICENSE)。
