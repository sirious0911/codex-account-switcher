// Pinned Codex v0.149.0 account/read: ChatGPT exposes type/email/planType,
// NOT provider accountId. Never upgrade email or local auth file to RPC-ID proof.
const safeId = value => typeof value === "string" && value.trim().length > 0 &&
  value.length <= 256 && !/[\x00-\x1f\x7f]/.test(value);
const safeEmail = value => typeof value === "string" &&
  value.length <= 320 && /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(value);
function decodedClaims(credential) {
  const token = credential?.tokens?.id_token;
  if (typeof token !== "string" || token.split(".").length !== 3) return null;
  try {
    const data = token.split(".")[1];
    return JSON.parse(Buffer.from(data, "base64url").toString("utf8"));
  } catch { return null; }
}
export function localCredentialAccountId(credential) {
  const direct = credential?.tokens?.account_id;
  const fromClaims = decodedClaims(credential)?.["https://api.openai.com/auth"]?.chatgpt_account_id;
  if (safeId(direct) && safeId(fromClaims) && direct !== fromClaims) return null;
  return safeId(direct) ? direct : safeId(fromClaims) ? fromClaims : null;
}
export function localCredentialEmail(credential) {
  const email = decodedClaims(credential)?.email;
  return safeEmail(email) ? email.toLowerCase() : null;
}
export function assessV0149AccountRead(localIdentity, response) {
  const account = response?.account;
  if (!safeId(localIdentity?.id)) return { status: "REJECTED", reason: "LOCAL_IDENTITY_UNAVAILABLE", accountIdConfirmed: false };
  if (!account || account.type !== "chatgpt" ||
      typeof account.planType !== "string" ||
      typeof response.requiresOpenaiAuth !== "boolean") {
    return { status: "REJECTED", reason: "ACCOUNT_READ_SCHEMA_MISMATCH", accountIdConfirmed: false };
  }
  if (account.email !== null && account.email !== undefined && !safeEmail(account.email)) {
    return { status: "REJECTED", reason: "ACCOUNT_EMAIL_INVALID", accountIdConfirmed: false };
  }
  const localEmail = safeEmail(localIdentity.email) ? localIdentity.email.toLowerCase() : null;
  const rpcEmail = safeEmail(account.email) ? account.email.toLowerCase() : null;
  if (localEmail && rpcEmail && localEmail !== rpcEmail) {
    return { status: "REJECTED", reason: "ACCOUNT_EMAIL_MISMATCH", accountIdConfirmed: false };
  }
  // This is NOT an exact provider-ID verification: official response has no ID.
  return {
    status: "AUTH_FILE_PINNED",
    reason: "RPC_ACCOUNT_ID_NOT_EXPOSED",
    accountIdConfirmed: false,
    emailConsistencyChecked: !!(localEmail && rpcEmail),
  };
}
function safeWindow(window, duration) {
  if (window == null) return null;
  const { usedPercent, windowDurationMins, resetsAt } = window;
  if (!Number.isFinite(usedPercent) || usedPercent < 0 || usedPercent > 100 ||
      windowDurationMins !== duration || !Number.isFinite(resetsAt) || resetsAt <= 0) return null;
  return { usedPercent, windowDurationMins, resetsAt };
}
export function validatedWireFrame(accountId, result, observedAtMs) {
  if (!safeId(accountId) || !Number.isSafeInteger(observedAtMs) || observedAtMs <= 0)
    return null;
  const bucket = result?.rateLimitsByLimitId?.codex ?? result?.rateLimits;
  if (!bucket || typeof bucket !== "object") return null;
  const plan = typeof bucket.planType === "string" && /^[a-z0-9_-]{1,32}$/i.test(bucket.planType)
    ? bucket.planType : null;
  const raw = {
    accountId,
    ordinaryUsageAllowed: typeof result.ordinaryUsageAllowed === "boolean" ? result.ordinaryUsageAllowed : null,
    rateLimitsByLimitId: { codex: {
      planType: plan, primary: safeWindow(bucket.primary, 300),
      secondary: safeWindow(bucket.secondary, 10080),
    }},
    rateLimitResetCredits: {
      availableCount: Number.isSafeInteger(result.rateLimitResetCredits?.availableCount) &&
        result.rateLimitResetCredits.availableCount >= 0 ? result.rateLimitResetCredits.availableCount : null,
    },
  };
  if (!raw.rateLimitsByLimitId.codex.primary && !raw.rateLimitsByLimitId.codex.secondary) return null;
  const line = JSON.stringify({ kind: "monitorObservation", observedAtMs, raw }) + "\n";
  return Buffer.byteLength(line, "utf8") <= 8193 ? line : null;
}
