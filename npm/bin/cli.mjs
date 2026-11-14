#!/usr/bin/env node
import { spawnSync } from "node:child_process";
import { readFileSync, realpathSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { assets, installApp, openApp, uninstallApp } from "../lib/install.mjs";

const metadata = JSON.parse(readFileSync(new URL("../package.json", import.meta.url), "utf8"));
const repository = metadata.repository.url.replace(/^git\+/, "").replace(/\.git$/, "");
const release = `${repository}/releases/latest`;
const info = {
  name: "Codex Account Switcher",
  author: metadata.author.name,
  forkMaintainer: "sirious0911",
  repository,
  website: metadata.homepage,
  release,
  downloads: { macOS: `${release}/download/${assets.macOS}`, windows: `${release}/download/${assets.windows}` },
  requirements: { macOS: "macOS 14+, Apple Silicon", windows: "Windows 10/11, x64" },
  helperVersion: metadata.version,
  installerVersion: metadata.version,
  nativeAppVersion: "GitHub latest",
};

export async function main(args = process.argv.slice(2)) {
const option = args[0] ?? "install";
const usage = "Usage: codex-account-switcher [install | update | open | uninstall] [--dir <directory>]\n       codex-account-switcher [--help | --links | --json | --open | --version]";
if (["install", "update", "open", "uninstall"].includes(option) && (args.length <= 1 || (args.length === 3 && args[1] === "--dir" && args[2] && !args[2].startsWith("--")))) {
  try {
    const operation = option === "open" ? openApp : option === "uninstall" ? uninstallApp : installApp;
    await operation({ repository, directory: args[2] ?? (process.env.CODEX_ACCOUNT_SWITCHER_INSTALL_DIR || undefined) });
  } catch (error) {
    console.error(`${option} failed: ${error.message}`);
    process.exitCode = 1;
  }
} else if (args.length > 1 || !["--help", "--links", "--json", "--open", "--version"].includes(option)) {
  console.error(usage);
  process.exitCode = 1;
} else if (option === "--version") {
  console.log(info.installerVersion);
} else if (option === "--json") {
  console.log(JSON.stringify(info, null, 2));
} else if (option === "--open") {
  const command = process.platform === "darwin" ? ["open", release]
    : process.platform === "win32" ? ["rundll32.exe", "url.dll,FileProtocolHandler", release]
    : ["xdg-open", release];
  const result = spawnSync(command[0], command.slice(1), { stdio: "inherit", windowsHide: true });
  if (result.error || result.status !== 0) {
    console.error(result.error?.message ?? `Browser command failed (status ${result.status}, signal ${result.signal}).`);
    process.exitCode = 1;
  }
} else {
  console.log(`${info.name} — personal fork maintained by ${info.forkMaintainer}; based on ${info.author}
Native desktop app: macOS 14+ Apple Silicon / Windows 10/11 x64.
Source: ${repository}
Website: ${info.website}
Latest release: ${release}
macOS DMG: ${info.downloads.macOS}
Windows EXE: ${info.downloads.windows}

${usage}

The default command installs or updates from GitHub latest, with SHA-256 verification.
macOS: ~/Applications/Codex Account Switcher.app (use --dir /Applications for a system installation).
Windows: %LOCALAPPDATA%/Programs/Codex Account Switcher, with a Start menu shortcut.
Use --dir with install/update/open/uninstall, or set CODEX_ACCOUNT_SWITCHER_INSTALL_DIR.
Quit the app before updating. Account management runs in the native app.
open starts the installed app; uninstall removes the app and launcher, preserving account data.
--open opens the latest release page; --version reports the installer version.
npm install -g runs the installer through postinstall unless lifecycle scripts are disabled.
Local dependency installation only installs this command-line package.
The installer has no dependencies, account access, or telemetry.

Codex 账号切换器个人 Fork：sirious0911 维护；基于刘朝 liuzhao1225 的 MIT 原版。
默认安装或更新 GitHub latest 发布包；已有账号数据保留。
macOS 同时验证应用身份、版本、签名和 Gatekeeper。Windows EXE 当前未签名。`);
}
}

if (process.argv[1] && realpathSync(process.argv[1]) === fileURLToPath(import.meta.url)) await main();
