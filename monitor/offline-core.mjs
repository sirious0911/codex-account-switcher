import fs from "node:fs";
import path from "node:path";
import crypto from "node:crypto";

// Offline-only reference implementation. No network, OS process, or account operations.
const integer = value => typeof value === "number" && Number.isSafeInteger(value) ? value : null;
const finite = value => typeof value === "number" && Number.isFinite(value) ? value : null;
const str = value => typeof value === "string" && value.trim() ? value.trim() : null;
const unixMs = value => { const n = integer(value); return n !== null && n > 0 ? n * 1000 : null; };
const plain = value => value && typeof value === "object" && !Array.isArray(value) ? value : null;

function parseWindow(raw) {
  const w = plain(raw);
  if (!w) return { usedPercent: null, remainingPercent: null, resetsAtMs: null, durationMinutes: null };
  const p = finite(w.usedPercent), d = integer(w.windowDurationMins);
  const validP = p !== null && p >= 0 && p <= 100 ? p : null;
  return {
    usedPercent: validP,
    remainingPercent: validP === null ? null : 100 - validP,
    resetsAtMs: unixMs(w.resetsAt),
    durationMinutes: d === 300 || d === 10080 ? d : null
  };
}

export function normalizeSynthetic(raw, observedAtMs) {
  const root = plain(raw?.result) ?? plain(raw);
  if (!root) throw new Error("Invalid synthetic response");
  const buckets = plain(root.rateLimitsByLimitId);
  const item = plain(buckets?.codex) ?? plain(root.rateLimits);
  const window5h = parseWindow(item?.primary);
  const window7d = parseWindow(item?.secondary);
  // Wrong durations are not silently relabeled.
  const five = window5h.durationMinutes === 300 ? window5h : parseWindow(null);
  const week = window7d.durationMinutes === 10080 ? window7d : parseWindow(null);
  const summary = plain(root.rateLimitResetCredits);
  const available = integer(summary?.availableCount);
  const credits = available !== null && available >= 0 ? available : null;
  const details = Array.isArray(summary?.credits)
    ? summary.credits.filter(c => c?.status === "available").map(c => ({
        expiresAtMs: unixMs(c.expiresAt)
      }))
    : null;
  const obs = integer(observedAtMs);
  if (obs === null || obs <= 0) throw new Error("Invalid observation timestamp");
  return {
    accountId: str(root.accountId),
    planType: str(item?.planType),
    ordinaryUsageAllowed: typeof root.ordinaryUsageAllowed === "boolean" ? root.ordinaryUsageAllowed : null,
    observedAtMs: obs,
    five, week, credits,
    creditDetails: details
  };
}

function identity(s) { return s?.accountId && s?.planType ? s.accountId + "|" + s.planType : null; }
const hash = raw => crypto.createHash("sha256").update(raw).digest("hex");
const fmt = w => w === "five" ? "5시간" : "주간";

function proposals(previous, current) {
  const proposals = [];
  if (previous.credits !== null && current.credits !== null && current.credits > previous.credits) {
    proposals.push({
      type: "CREDIT_INCREASE", lane: "credits",
      oldValue: previous.credits, newValue: current.credits,
      anchor: String(previous.observedAtMs)
    });
  }
  if (current.ordinaryUsageAllowed === true && previous.ordinaryUsageAllowed === true) {
    for (const lane of ["five", "week"]) {
      const a = previous[lane], b = current[lane];
      if (a.usedPercent === null || b.usedPercent === null || a.resetsAtMs === null || b.resetsAtMs === null) continue;
      if (a.usedPercent < 15 || b.usedPercent > 5 || a.usedPercent - b.usedPercent < 15) continue;
      if (b.resetsAtMs <= a.resetsAtMs + 120000) continue;
      const early = current.observedAtMs < a.resetsAtMs - 120000;
      const creditsSpent = previous.credits !== null && current.credits !== null && current.credits < previous.credits;
      if (b.resetsAtMs <= current.observedAtMs || (early && creditsSpent)) continue;
      proposals.push({
        type: early ? "EARLY_RECOVERY_SUSPECTED" : "SCHEDULED_RESET",
        lane, oldValue: a.usedPercent, newValue: b.usedPercent,
        anchor: String(a.resetsAtMs) + ":" + String(b.resetsAtMs)
      });
    }
  }
  return proposals;
}

function keyFor(s, p) {
  return hash([s.accountId, s.planType, p.type, p.lane, p.anchor, p.oldValue, p.lane === "credits" ? p.newValue : "reset"].join("|"));
}

export function emptyState() {
  return { schemaVersion: 1, trusted: null, pending: [], emittedKeys: [], outbox: [] };
}

function keepKnown(previous, current) {
  return {
    ...current,
    credits: current.credits ?? previous.credits,
    five: current.five.usedPercent === null ? previous.five : current.five,
    week: current.week.usedPercent === null ? previous.week : current.week
  };
}

export function advanceState(state, next) {
  if (state?.schemaVersion !== 1 || !Array.isArray(state.pending) || !Array.isArray(state.emittedKeys)) {
    throw new Error("Invalid durable state schema");
  }
  const s = structuredClone(state);
  if (!identity(next)) {
    s.trusted = null;
    s.pending = [];
    return { state: s, events: [] };
  }
  if (!s.trusted || identity(s.trusted) !== identity(next)) {
    s.trusted = next;
    s.pending = [];
    return { state: s, events: [] };
  }
  if (next.observedAtMs <= s.trusted.observedAtMs) return { state: s, events: [] };
  if (next.observedAtMs - s.trusted.observedAtMs > 24 * 3600000) {
    s.trusted = next; s.pending = [];
    return { state: s, events: [] };
  }
  const candidateList = proposals(s.trusted, next);
  const oldPending = new Map(s.pending.map(p => [p.key, p]));
  const fresh = [];
  const events = [];
  for (const p of candidateList) {
    const key = keyFor(s.trusted, p);
    const previous = oldPending.get(key);
    if (!previous) {
      fresh.push({ ...p, key, observedAtMs: next.observedAtMs, checks: 1 });
    } else if (next.observedAtMs > previous.observedAtMs) {
      if (!s.emittedKeys.includes(key)) {
        const event = { key, type: p.type, lane: p.lane, oldValue: p.oldValue, newValue: p.newValue,
          observedAtMs: next.observedAtMs };
        events.push(event);
        s.emittedKeys.push(key);
        s.outbox.push({ ...event, status: "MOCK_PENDING" });
      }
    } else {
      fresh.push(previous);
    }
  }
  s.pending = fresh;
  if (candidateList.length === 0 || events.length > 0) {
    s.trusted = next;
    // After any confirmed event the baseline advances to prevent repeat emission.
    if (events.length > 0) s.pending = [];
  }
  return { state: s, events };
}

export function renderTelegramMock(event) {
  if (!["CREDIT_INCREASE", "EARLY_RECOVERY_SUSPECTED", "SCHEDULED_RESET"].includes(event.type)) {
    throw new Error("Unsupported event");
  }
  if (event.type === "CREDIT_INCREASE") {
    return `[모의 알림] Codex 리셋권 증가 관측: ${event.oldValue}개 → ${event.newValue}개 (2회 확인)`;
  }
  return `[모의 알림] Codex ${fmt(event.lane)} ${event.type === "SCHEDULED_RESET" ? "정기 재설정" : "비정기 복구 의심"}: 사용률 ${event.oldValue}% → ${event.newValue}% (2회 확인)`;
}

export function takeSingleWriterLock(directory) {
  fs.mkdirSync(directory, { recursive: true });
  const lock = path.join(directory, ".writer.lock");
  // Do not steal a stale lock. Human recovery is required.
  const fd = fs.openSync(lock, "wx", 0o600);
  fs.writeSync(fd, "OFFLINE_EXCLUSIVE_WRITER\n");
  return () => { fs.closeSync(fd); fs.unlinkSync(lock); };
}

export function readDurableState(directory) {
  const file = path.join(directory, "state.json");
  if (!fs.existsSync(file)) return emptyState();
  const state = JSON.parse(fs.readFileSync(file, "utf8"));
  if (state.schemaVersion !== 1 || !Array.isArray(state.outbox)) throw new Error("Unsupported state");
  return state;
}

export function persistDurableState(directory, state) {
  if (state?.schemaVersion !== 1) throw new Error("Unsupported state");
  fs.mkdirSync(directory, { recursive: true });
  const temp = path.join(directory, "state.json.tmp");
  const target = path.join(directory, "state.json");
  const fd = fs.openSync(temp, "wx", 0o600);
  try {
    fs.writeFileSync(fd, JSON.stringify(state, null, 2), "utf8");
    fs.fsyncSync(fd);
  } finally { fs.closeSync(fd); }
  fs.renameSync(temp, target);
  // This is an offline prototype: directory sync/power-loss guarantees are not verified on Windows.
}

export function renderTaskbarMock(snapshot) {
  const pct = w => w.remainingPercent === null ? "UNKNOWN" : `${w.remainingPercent}%`;
  const credits = snapshot.credits === null ? "UNKNOWN" : String(snapshot.credits);
  return `Codex | 5H ${pct(snapshot.five)} | 7D ${pct(snapshot.week)} | 리셋권 ${credits} | MOCK`;
}
