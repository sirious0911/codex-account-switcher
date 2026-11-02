// A-only diagnostic contract. No profile registration, credential copy, switching,
// auto-reset, persistence or Telegram. Actual Codex I/O is confined to the
// separately gated CLI; this module is deterministic and offline-testable.
const safeIdentity = value =>
  typeof value === "string" && value.trim().length > 0 &&
  value.length <= 256 && !/[\x00-\x1f\x7f]/.test(value);

export function localCredentialAccountId(credential) {
  const tokens = credential?.tokens;
  if (!tokens || typeof tokens !== "object") return null;
  if (safeIdentity(tokens.account_id)) return tokens.account_id;
  if (typeof tokens.id_token !== "string") return null;
  const parts = tokens.id_token.split(".");
  if (parts.length !== 3) return null;
  try {
    const claims = JSON.parse(Buffer.from(parts[1], "base64url").toString("utf8"));
    const id = claims?.["https://api.openai.com/auth"]?.chatgpt_account_id;
    return safeIdentity(id) ? id : null;
  } catch {
    return null;
  }
}

export function rpcAccountId(result) {
  const a = result?.account;
  const id = a?.accountId ?? a?.accountID ?? a?.chatgptAccountId ?? a?.id;
  return safeIdentity(id) ? id : null;
}

export function activeIdentityMatches(localId, rpcResult) {
  const reported = rpcAccountId(rpcResult);
  return safeIdentity(localId) && !!reported && localId === reported;
}

function safeWindow(window, targetDuration) {
  if (window == null) return null;
  const { usedPercent, windowDurationMins, resetsAt } = window;
  if (!Number.isFinite(usedPercent) || usedPercent < 0 || usedPercent > 100 ||
      windowDurationMins !== targetDuration || !Number.isFinite(resetsAt) ||
      resetsAt <= 0) return null;
  return { usedPercent, windowDurationMins, resetsAt };
}

export function validatedWireFrame(accountId, result, observedAtMs) {
  if (!safeIdentity(accountId) || !Number.isSafeInteger(observedAtMs) || observedAtMs <= 0)
    return null;
  const b = result?.rateLimitsByLimitId?.codex ?? result?.rateLimits;
  if (!b || typeof b !== "object") return null;
  const plan = typeof b.planType === "string" && /^[a-z0-9_-]{1,32}$/i.test(b.planType)
    ? b.planType : null;
  const raw = {
    accountId,
    ordinaryUsageAllowed: typeof result.ordinaryUsageAllowed === "boolean"
      ? result.ordinaryUsageAllowed : null,
    rateLimitsByLimitId: {
      codex: {
        planType: plan,
        primary: safeWindow(b.primary, 300),
        secondary: safeWindow(b.secondary, 10080),
      },
    },
    rateLimitResetCredits: {
      availableCount: Number.isSafeInteger(result.rateLimitResetCredits?.availableCount) &&
        result.rateLimitResetCredits.availableCount >= 0
        ? result.rateLimitResetCredits.availableCount : null,
    },
  };
  if (!raw.rateLimitsByLimitId.codex.primary &&
      !raw.rateLimitsByLimitId.codex.secondary) return null;
  const frame = { kind: "monitorObservation", observedAtMs, raw };
  const line = JSON.stringify(frame) + "\n";
  if (Buffer.byteLength(line, "utf8") - 1 > 8192) return null;
  return line;
}
