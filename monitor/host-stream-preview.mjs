import { createInterface } from "node:readline";
import { pathToFileURL } from "node:url";
import { normalizeSynthetic } from "./offline-core.mjs";
import { previewSyntheticReadings } from "./synthetic-pipeline.mjs";

/**
 * Interpret the additional usage-only messages from SwitcherHost's existing
 * JSON-lines stdout protocol. This is an offline consumer for fixtures: no
 * processes are started and no message is delivered to Telegram.
 *
 * Each account gets an independent event history, avoiding false baselines
 * when several accounts refresh in interleaved order.
 */
export function previewHostMessages(messages) {
  const byAccount = new Map();
  let monitorPreview = null;
  for (const item of messages) {
    const message = typeof item === "string" ? JSON.parse(item) : item;
    if (message?.kind !== "monitorObservation") continue;
    const sample = normalizeSynthetic(message.raw, message.observedAtMs);
    const key = sample.accountId;
    // UNKNOWN account IDs cannot be used to tie observations to prior readings.
    if (!key) continue;
    const existing = byAccount.get(key) ?? [];
    existing.push({ raw: message.raw, observedAtMs: message.observedAtMs });
    byAccount.set(key, existing);
    // Status-only Monitor Bot preview is available even when a plan is unknown.
    monitorPreview = sample;
  }

  const accounts = [];
  const alerts = [];
  for (const [accountId, readings] of byAccount) {
    const result = previewSyntheticReadings(readings);
    accounts.push({
      accountId,
      eventTypes: result.events.map(event => event.type),
      outboxStatuses: result.outbox.map(record => record.status)
    });
    alerts.push(...result.mockDeliveries);
  }
  return {
    observedAccounts: accounts,
    monitorBot: monitorPreview && byAccount.has(monitorPreview.accountId)
      ? previewSyntheticReadings(byAccount.get(monitorPreview.accountId)).monitorPreview
      : null,
    alertBot: alerts
  };
}

if (process.argv[1] && pathToFileURL(process.argv[1]).href === import.meta.url) {
  const lines = [];
  for await (const line of createInterface({ input: process.stdin, crlfDelay: Infinity })) {
    if (line.trim()) lines.push(line);
  }
  process.stdout.write(JSON.stringify(previewHostMessages(lines), null, 2) + "\n");
}
