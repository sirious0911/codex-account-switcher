// OFFLINE ONLY: a simulated Telegram outbox. No HTTP client, bot, credential,
// host process discovery, or actual delivery routine exists in this module.
import { renderTelegramMock } from "./offline-core.mjs";

const allowedOwners = new Set(["MOCK_WINDOWS", "MOCK_MAC"]);
const validStatuses = new Set(["MOCK_PENDING", "MOCK_RESERVED", "MOCK_COMPLETE", "MOCK_UNCERTAIN"]);

function assertState(state) {
  if (state?.schemaVersion !== 1 || !Array.isArray(state.outbox)) {
    throw new Error("Invalid synthetic event state");
  }
  if (state.outbox.some(e => !validStatuses.has(e.status) || typeof e.key !== "string")) {
    throw new Error("Unrecognized synthetic outbox status");
  }
}

function ensureOwner(state, owner) {
  if (!allowedOwners.has(owner)) throw new Error("Only synthetic host names permitted");
  if (state.mockSenderOwner && state.mockSenderOwner !== owner) {
    throw new Error("Single mock delivery owner violation");
  }
  state.mockSenderOwner = owner;
}

// Caller must persist the returned state while holding its exclusive writer
// lock BEFORE printing a simulated delivery. Unknown outcome remains blocked.
export function reserveMockDelivery(state, eventKey, owner) {
  assertState(state);
  const s = structuredClone(state);
  ensureOwner(s, owner);
  const found = s.outbox.find(e => e.key === eventKey);
  if (!found) throw new Error("Unknown synthetic event");
  if (found.status !== "MOCK_PENDING") return { state: s, mockMessage: null, reserved: false };
  found.status = "MOCK_RESERVED";
  found.mockOwner = owner;
  return { state: s, mockMessage: renderTelegramMock(found), reserved: true };
}

export function acknowledgeMockDelivery(state, eventKey, owner) {
  assertState(state);
  const s = structuredClone(state);
  ensureOwner(s, owner);
  const item = s.outbox.find(e => e.key === eventKey);
  if (!item || item.status !== "MOCK_RESERVED" || item.mockOwner !== owner) {
    throw new Error("No owned synthetic reservation to acknowledge");
  }
  item.status = "MOCK_COMPLETE";
  return s;
}

export function flagMockUncertain(state, eventKey, owner) {
  assertState(state);
  const s = structuredClone(state);
  ensureOwner(s, owner);
  const item = s.outbox.find(e => e.key === eventKey);
  if (!item || item.status !== "MOCK_RESERVED" || item.mockOwner !== owner) {
    throw new Error("No owned synthetic reservation to flag");
  }
  item.status = "MOCK_UNCERTAIN";
  return s;
}

export function inspectMockOutbox(state) {
  assertState(state);
  return state.outbox.map(e => ({ key: e.key, status: e.status, mockOwner: e.mockOwner ?? null }));
}
