import test from "node:test";
import assert from "node:assert/strict";
import { mkdtemp, mkdir, writeFile, readFile, rm } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import fs from "node:fs";
import { installTarget, macLegacyAppsToRemove } from "../../npm/lib/install.mjs";

const root = process.cwd();
const read = file => fs.readFileSync(path.join(root, file), "utf8");
const ownID = "com.sirious0911.codex-account-switcher";
const upstreamID = "com.liuzhao.codex-account-switcher";
const ownApp = "Codex Account Switcher Personal.app";
const upstreamApp = "Codex Account Switcher.app";

test("fork macOS package and installer share one distinct bundle identity", () => {
  const pack = read("scripts/package-local-app.sh");
  const installer = read("npm/lib/install.mjs");
  const preview = read(".github/workflows/macos-preview.yml");
  const release = read(".github/workflows/release.yml");
  assert.ok(pack.includes("CFBundleIdentifier -string com.sirious0911.codex-account-switcher"));
  assert.match(installer, /const bundleID = "com\.sirious0911\.codex-account-switcher"/);
  assert.ok(!pack.includes(upstreamID) && !installer.includes(upstreamID));
  for (const entry of [pack, preview, release, installer])
    assert.ok(entry.includes(ownApp), "fork package paths must use own app");
  assert.ok(!preview.includes(upstreamApp));
});

test("fork Windows data, host cache, startup and installer never reuse upstream directories", async () => {
  const win = read("windows/CodexAccountSwitcher/App.xaml.cs");
  const native = read("windows/CodexAccountSwitcher/NativeSettings.cs");
  const host = read("windows/CodexAccountSwitcher.Core/HostRuntime.cs");
  const store = read("Sources/SwitcherCore/AccountStore.swift");
  const model = read("Sources/CodexAccountSwitcher/AppModel.swift");
  assert.match(win, /"Codex Account Switcher Personal"/);
  assert.match(host, /"Codex Account Switcher Personal Runtime"/);
  assert.match(native, /Software\\Sirious0911\\CodexAccountSwitcher/);
  assert.match(native, /CodexAccountSwitcherPersonal/);
  assert.ok(!native.includes('"CodexAccountSwitcher"'));
  assert.match(store, /path: "Codex Account Switcher Personal"/);
  assert.match(store, /self\.legacyBaseURL = nil/);
  assert.match(model, /Codex Account Switcher Personal\/usage-monitor/);
  const localAppData = path.resolve("synthetic-local-appdata");
  const target = await installTarget({platform:"win32",arch:"x64",release:"10.0.0",localAppData});
  assert.equal(target.target, path.join(localAppData,"Programs","Codex Account Switcher Personal","Codex Account Switcher Personal.exe"));
});

test("personal npm mac installer does not select or remove an existing original app", async (t) => {
  const home = await mkdtemp(path.join(os.tmpdir(), "fork-identity-test-"));
  t.after(() => rm(home,{recursive:true,force:true}));
  const original = path.join(home,"Applications",upstreamApp);
  const marker = path.join(original,"preserve-original.txt");
  await mkdir(original,{recursive:true});
  await writeFile(marker,"original-app-preserved");
  const {target,legacyTargets} = await installTarget({platform:"darwin",arch:"arm64",release:"23.0.0",home});
  assert.equal(target,path.join(home,"Applications",ownApp));
  assert.ok(!legacyTargets.some(p => p.endsWith(upstreamApp)));
  const removed = await macLegacyAppsToRemove(
    {targets:legacyTargets, selectedTarget:target, version:"0.2.1"},
    () => {throw new Error("Original app must not be inspected");}
  );
  assert.deepEqual(removed,[]);
  assert.equal(await readFile(marker,"utf8"),"original-app-preserved");
});
