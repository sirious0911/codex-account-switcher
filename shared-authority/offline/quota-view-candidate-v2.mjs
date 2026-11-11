// P3 V2 reset-credit telemetry: pure, strict, OFFLINE, read-only.
//
// This module does not open a socket, fetch account data, access credentials,
// read the OS clock, change accounts, grant Telegram rights, or issue credit
// consumption. trustedNowUtcMs is an explicit caller-supplied fixture input.
// V1's parser and /v1/quota routes remain entirely separate and unchanged.
import {parseUniqueJsonFrame} from './strict-json-frame.mjs';

const TOP = new Set([
  'schemaVersion','sourceDeviceId','sourceSessionId','sequence',
  'collectedAtUtcMs','accountIdSha256','fiveHour','sevenDay','resetCredits',
  'actualSendAuthorized','automaticRetryAllowed',
  'productionReservationAuthorized','senderTransitionAuthorized'
]);
const WINDOW = new Set(['remainingPercent','resetAtUtcMs']);
const RESET = new Set(['availableCount','expiryDetailsUtcMs']);
const PRIVILEGES = Object.freeze([
  'actualSendAuthorized','automaticRetryAllowed',
  'productionReservationAuthorized','senderTransitionAuthorized'
]);
const ID = /^[A-Za-z0-9-]{1,48}$/;
const SESSION = /^[a-f0-9]{32}$/;
const HASH = /^[a-f0-9]{64}$/;
const MIN_UTC_MS = 1577836800000; // 2020-01-01 inclusive
const MAX_UTC_MS = 4133980800000; // 2101-01-01 inclusive, as in V1
const DISPLAY_MS = 360000;         // UI fails closed at six minutes
const FRESH_MS = 120000;
const FUTURE_SKEW_MS = 3000;

const SAFE = Object.freeze({
  actualSendAuthorized: false,
  automaticRetryAllowed: false,
  productionReservationAuthorized: false,
  senderTransitionAuthorized: false,
  realDeviceAuthenticated: false,
  realCrossHostSyncAuthorized: false
});

const rejected = status => Object.freeze({
  status, view: null, presentation: null, ...SAFE
});
const exact = (x, fields) => (
  x !== null && typeof x === 'object' && !Array.isArray(x) &&
  Object.keys(x).length === fields.size &&
  Object.keys(x).every(key => fields.has(key))
);
const boundedTime = value => Number.isSafeInteger(value) &&
  value >= MIN_UTC_MS && value <= MAX_UTC_MS;
const boundedQuota = (window, collected, upperDuration) => {
  if (!exact(window, WINDOW)) return false;
  const {remainingPercent, resetAtUtcMs} = window;
  if (remainingPercent !== null && (
    !Number.isSafeInteger(remainingPercent) ||
    remainingPercent < 0 || remainingPercent > 100
  )) return false;
  return resetAtUtcMs === null ||
    (boundedTime(resetAtUtcMs) &&
     resetAtUtcMs >= collected - 600000 &&
     resetAtUtcMs <= collected + upperDuration);
};

function validCredits(credits) {
  if (!exact(credits, RESET)) return false;
  const {availableCount, expiryDetailsUtcMs} = credits;
  if (availableCount !== null && (
    !Number.isSafeInteger(availableCount) ||
    availableCount < 0 || availableCount > 2147483647
  )) return false;
  if (expiryDetailsUtcMs === null) return true;
  if (!Array.isArray(expiryDetailsUtcMs) ||
      expiryDetailsUtcMs.length > 2 ||
      availableCount === null ||
      expiryDetailsUtcMs.length > availableCount) return false;
  return expiryDetailsUtcMs.every(value => (
    value === null || boundedTime(value)
  ));
}

function decode(raw) {
  if (typeof raw !== 'string' || Buffer.byteLength(raw, 'utf8') > 2048)
    return null;
  try {
    // Escaped duplicate JSON property names are rejected by the shared
    // strict decoder before JSON.parse can silently overwrite any field.
    const x = parseUniqueJsonFrame(raw, 2048, 8);
    if (!exact(x, TOP) || x.schemaVersion !== 2 ||
        !ID.test(x.sourceDeviceId ?? '') ||
        !SESSION.test(x.sourceSessionId ?? '') ||
        !HASH.test(x.accountIdSha256 ?? '') ||
        !Number.isSafeInteger(x.sequence) ||
        x.sequence < 1 || x.sequence > 1000000000 ||
        !boundedTime(x.collectedAtUtcMs) ||
        !boundedQuota(x.fiveHour, x.collectedAtUtcMs, 21600000) ||
        !boundedQuota(x.sevenDay, x.collectedAtUtcMs, 691200000) ||
        !validCredits(x.resetCredits) ||
        PRIVILEGES.some(key => x[key] !== false)) return null;
    // R-only observations have real source evidence only when count is
    // explicit, even if zero; null quotas + null count carry no evidence.
    if (x.fiveHour.remainingPercent === null &&
        x.sevenDay.remainingPercent === null &&
        x.resetCredits.availableCount === null) return null;
    return x;
  } catch {
    return null;
  }
}

const expiryStatus = (when, now) =>
  when === null ? 'UNKNOWN' :
  when <= now ? 'EXPIRED_REFRESH_REQUIRED' : 'KNOWN';

// This is a projection, not authorization or a stable R1/R2 identity.
function present(x, now, freshness) {
  const credits = x.resetCredits;
  const ordered = credits.expiryDetailsUtcMs === null ? null :
    [...credits.expiryDetailsUtcMs]
      .sort((a,b) => a === null ? (b === null ? 0 : 1) :
        (b === null ? -1 : a - b));
  const slots = ordered === null ? null :
    Object.freeze(ordered.map((timestamp, index) => Object.freeze({
      position: index + 1,
      expiresAtUtcMs: timestamp,
      expiryStatus: expiryStatus(timestamp, now)
    })));
  return Object.freeze({
    sourceDeviceId: x.sourceDeviceId,
    freshness,
    fiveHourRemainingPercent: x.fiveHour.remainingPercent,
    sevenDayRemainingPercent: x.sevenDay.remainingPercent,
    availableCount: credits.availableCount, // null != 0
    expiryDetailsProvided: ordered !== null,
    // An empty supplied list remains an empty list; never invent dates.
    expirySlots: slots,
    // R-only provides no basis for claiming a Free/30D subscription.
    inferredNonWeeklyWindow: null,
    resetCreditsMayBeConsumed: false,
    telegramAllowed: false
  });
}

/**
 * Pure validator + display projection for schemaVersion 2; expectedSourceDeviceId
 * must be supplied by the local caller, not inferred from the untrusted frame.
 * Null view means UNKNOWN for all 5H/7D/R fields.
 */
export function reviewOfflineQuotaViewCandidateV2(raw, {
  trustedNowUtcMs,
  expectedSourceDeviceId,
  previousRaw = null
} = {}) {
  if (!boundedTime(trustedNowUtcMs))
    return rejected('HOLD_UNTRUSTED_VIEW_CLOCK');
  if (!ID.test(expectedSourceDeviceId ?? ''))
    return rejected('HOLD_UNENROLLED_SOURCE');
  const x = decode(raw);
  if (!x) return rejected('HOLD_INVALID_V2_FRAME');
  if (x.sourceDeviceId !== expectedSourceDeviceId)
    return rejected('HOLD_SOURCE_DEVICE_CONFLICT');
  const age = trustedNowUtcMs - x.collectedAtUtcMs;
  if (age < -FUTURE_SKEW_MS || age >= DISPLAY_MS)
    return rejected('HOLD_STALE_OR_FUTURE_V2_FRAME');
  if (previousRaw !== null) {
    const old = decode(previousRaw);
    if (!old) return rejected('HOLD_INVALID_PREVIOUS_V2_FRAME');
    if (old.sourceDeviceId !== x.sourceDeviceId ||
        old.sourceDeviceId !== expectedSourceDeviceId)
      return rejected('HOLD_SOURCE_DEVICE_CONFLICT');
    if (old.accountIdSha256 !== x.accountIdSha256)
      return rejected('HOLD_ACCOUNT_IDENTITY_CHANGED');
    if (x.collectedAtUtcMs <= old.collectedAtUtcMs ||
        (x.sourceSessionId === old.sourceSessionId ?
          x.sequence <= old.sequence : x.sequence !== 1))
      return rejected('HOLD_REPLAY_OR_REORDERED_VIEW');
  }
  const freshness = age < FRESH_MS ? 'FRESH_READ_ONLY' : 'AGED_READ_ONLY';
  const view = Object.freeze({
    schemaVersion: 2,
    sourceDeviceId: x.sourceDeviceId,
    sourceSessionId: x.sourceSessionId,
    sequence: x.sequence,
    collectedAtUtcMs: x.collectedAtUtcMs,
    accountIdSha256: x.accountIdSha256,
    fiveHour: Object.freeze({...x.fiveHour}),
    sevenDay: Object.freeze({...x.sevenDay}),
    resetCredits: Object.freeze({
      availableCount: x.resetCredits.availableCount,
      expiryDetailsUtcMs: x.resetCredits.expiryDetailsUtcMs === null
        ? null : Object.freeze([...x.resetCredits.expiryDetailsUtcMs])
    })
  });
  return Object.freeze({
    status: 'OFFLINE_V2_CANDIDATE_ONLY',
    view,
    presentation: present(x, trustedNowUtcMs, freshness),
    ...SAFE
  });
}
