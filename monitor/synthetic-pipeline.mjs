import {
  normalizeSynthetic, emptyState, advanceState
} from "./offline-core.mjs";
import {
  reserveMockDelivery, acknowledgeMockDelivery, inspectMockOutbox
} from "./mock-outbox.mjs";
import { renderAlertBotPreview, renderMonitorBotPreview } from "./mock-telegram-routing.mjs";

/**
 * Execute the existing monitor algorithm entirely in memory against synthetic
 * Swift-encoder-compatible readings. Nothing is polled or transmitted.
 *
 * @param {Array<{raw: object, observedAtMs: number}>} readings
 * @param {"MOCK_WINDOWS"|"MOCK_MAC"} owner
 */
export function previewSyntheticReadings(readings, owner = "MOCK_WINDOWS") {
  if (!Array.isArray(readings)) throw new TypeError("Synthetic readings must be an array");
  let state = emptyState();
  const events = [];
  let latest = null;
  for (const reading of readings) {
    const normalized = normalizeSynthetic(reading.raw, reading.observedAtMs);
    latest = normalized;
    const result = advanceState(state, normalized);
    state = result.state;
    events.push(...result.events);
  }
  const mockMessages = [];
  const mockDeliveries = [];
  for (const event of state.outbox) {
    if (event.status !== "MOCK_PENDING") continue;
    const reservation = reserveMockDelivery(state, event.key, owner);
    state = reservation.state;
    if (reservation.reserved) {
      mockMessages.push(reservation.mockMessage);
      mockDeliveries.push(renderAlertBotPreview(event));
      state = acknowledgeMockDelivery(state, event.key, owner);
    }
  }
  return {
    state, events, mockMessages, mockDeliveries,
    monitorPreview: latest ? renderMonitorBotPreview(latest) : null,
    outbox: inspectMockOutbox(state)
  };
}
