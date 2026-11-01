import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { resolve, dirname } from "node:path";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
const read = path => readFileSync(resolve(root, path), "utf8");
const models = read("Sources/SwitcherCore/Models.swift");
const controller = read("Sources/SwitcherCore/AccountController.swift");
const codex = read("Sources/SwitcherCore/CodexClient.swift");
const host = read("Sources/SwitcherHost/Host.swift");
const windows = read("windows/CodexAccountSwitcher/MainWindow.cs");
const mac = read("Sources/CodexAccountSwitcher/SettingsView.swift");

test("settings constructors and decoding force automatic reset consumption off", () => {
  assert.match(models, /self\.autoResetEnabled = false/);
  assert.match(models, /autoResetEnabled = false/);
  assert.doesNotMatch(models, /decodeIfPresent\(\s*Bool\.self, forKey: \.autoResetEnabled/);
});
test("Swift controller has no redemption RPC or scheduler activation", () => {
  assert.doesNotMatch(controller, /codex\.consumeResetCredit\s*\(/);
  assert.doesNotMatch(controller, /beginAutoReset\s*\(/);
  assert.doesNotMatch(controller, /checkpoint\.decision == "attempt"/);
  assert.match(controller, /if enabled \{ showError\(CodexClientError\.resetConsumptionDisabled\) \}/);
});
test("concrete Codex client rejects before creating app-server session", () => {
  const start = codex.indexOf("public func consumeResetCredit(");
  const end = codex.indexOf("public func login(", start);
  assert.ok(start !== -1 && end > start);
  const method = codex.slice(start, end);
  assert.match(method, /throw CodexClientError\.resetConsumptionDisabled/);
  assert.doesNotMatch(method, /withSession|session\.request/);
  assert.doesNotMatch(codex, /account\/rateLimitResetCredit\/consume/);
});
test("Windows host rejects legacy autoReset protocol command", () => {
  assert.match(host, /case "autoReset":\s*throw HostError\.message\("Automatic reset-credit consumption is disabled\."\)/);
  assert.doesNotMatch(host, /await model\.setAutoResetEnabled\(value\)/);
});
test("Windows and Mac settings have no redemption toggle", () => {
  assert.doesNotMatch(windows, /Toggle\(T\("auto_reset"\)/);
  assert.doesNotMatch(windows, /Run\("autoReset"/);
  assert.doesNotMatch(mac, /settingSwitch\("auto_reset"/);
  assert.doesNotMatch(mac, /setAutoResetEnabled\(/);
});
test("new Swift tests explicitly assert disabled redemption", () => {
  const controllerTests = read("Tests/SwitcherCoreTests/AccountControllerTests.swift");
  const clientTests = read("Tests/CodexAccountSwitcherTests/CodexClientTests.swift");
  assert.match(controllerTests, /consumeCalls == 0/);
  assert.match(controllerTests, /existingLegacyTrueIsIgnoredBySettingsDecoder/);
  assert.match(clientTests, /resetCreditConsumeIsBlockedBeforeProcessLaunch/);
  assert.match(clientTests, /CodexClientError\.resetConsumptionDisabled/);
});

test("Windows updater has no upstream HTTP or release-opening execution path", () => {
  const native = read("windows/CodexAccountSwitcher/NativeSettings.cs");
  const app = read("windows/CodexAccountSwitcher/App.xaml.cs");
  const ui = read("windows/CodexAccountSwitcher/MainWindow.cs");
  assert.match(native, /get => false;/);
  assert.doesNotMatch(native, /GetStringAsync|new HttpClient|Process.Start/);
  assert.doesNotMatch(app, /updates.Tick|updates.Start|native.CheckUpdatesAsync\(/);
  assert.doesNotMatch(ui, /Run\("autoReset"|OpenUpdate\(|CheckUpdatesAsync\(|Toggle\(T\("automatically_check_updates"\)/);
});

test("macOS updater and UI never instantiate Sparkle or offer update installation", () => {
  const updater = read("Sources/CodexAccountSwitcher/AppUpdater.swift");
  const settings = read("Sources/CodexAccountSwitcher/SettingsView.swift");
  const manifest = read("Package.swift");
  assert.match(updater, /var automaticallyChecks: Bool \{ false \}/);
  assert.doesNotMatch(updater, /import Sparkle|SPUStandardUpdaterController|SPUUpdater|checkForUpdates\(nil\)/);
  assert.doesNotMatch(settings, /setAutomaticallyChecks\(|checkForUpdates\(\)|automatically_check_updates/);
  assert.doesNotMatch(manifest, /Sparkle/);
});

test("macOS application packaging excludes Sparkle and upstream feed metadata", () => {
  const packaging = read("scripts/package-local-app.sh");
  assert.doesNotMatch(packaging, /Sparkle\.framework|SUFeedURL|SUPublicEDKey|SUEnableAutomaticChecks|sparkle-public-key\.txt/);
  assert.match(packaging, /codesign --verify --deep --strict/);
});
