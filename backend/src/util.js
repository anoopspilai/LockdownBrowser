import crypto from "node:crypto";

export class HttpError extends Error {
  constructor(status, code, message, extra = {}) {
    super(message);
    this.status = status;
    this.code = code;
    this.extra = extra;
  }
}

export const nowIso = () => new Date().toISOString();
export const isoIn = (ms) => new Date(Date.now() + ms).toISOString();
export const isObject = (v) => v !== null && typeof v === "object" && !Array.isArray(v);
export const hex = (n) => crypto.randomBytes(n).toString("hex");
export const sha256hex = (s) => crypto.createHash("sha256").update(String(s), "utf8").digest("hex");

/** Constant-time comparison of two strings (hex/base64/plain). Length mismatch still takes the same path. */
export function safeEqual(a, b) {
  const ba = Buffer.from(String(a ?? ""), "utf8");
  const bb = Buffer.from(String(b ?? ""), "utf8");
  if (ba.length !== bb.length) {
    // Compare against itself to burn the same time, then fail.
    crypto.timingSafeEqual(ba, ba);
    return false;
  }
  return crypto.timingSafeEqual(ba, bb);
}

export const newSessionId = () => `sess_${hex(16)}`;
export const newDeviceId = () => `dev_${hex(8)}`;
export const newId = (prefix) => `${prefix}_${hex(8)}`;
export const newToken = () => hex(24); // 48 hex
export const newNonce = () => hex(16); // 32 hex

export function parseVersion(v) {
  if (typeof v !== "string") return null;
  const m = v.trim().match(/^(\d{1,6})\.(\d{1,6})\.(\d{1,6})/);
  return m ? [Number(m[1]), Number(m[2]), Number(m[3])] : null;
}

/** <0, 0, >0. Unparseable versions compare as 0.0.0. */
export function compareVersions(a, b) {
  const pa = parseVersion(a) || [0, 0, 0];
  const pb = parseVersion(b) || [0, 0, 0];
  for (let i = 0; i < 3; i++) if (pa[i] !== pb[i]) return pa[i] - pb[i];
  return 0;
}

export function clampInt(v, min, max, dflt) {
  const n = Number(v);
  if (!Number.isFinite(n)) return dflt;
  return Math.min(max, Math.max(min, Math.trunc(n)));
}

export function str(v, max = 200) {
  if (typeof v !== "string") return "";
  return v.trim().slice(0, max);
}

export function requireStr(v, name, max = 200) {
  const s = str(v, max);
  if (!s) throw new HttpError(400, "INVALID_REQUEST", `${name} is required`);
  return s;
}

export function requireReason(body) {
  return requireStr(body?.reason, "reason", 500);
}

export function safeJsonParse(text, fallback) {
  try {
    return text ? JSON.parse(text) : fallback;
  } catch {
    return fallback;
  }
}

export function isPast(iso) {
  const t = Date.parse(iso);
  return !Number.isFinite(t) || t <= Date.now();
}

export function secondsUntil(iso) {
  const t = Date.parse(iso);
  if (!Number.isFinite(t)) return 0;
  return Math.max(0, Math.floor((t - Date.now()) / 1000));
}

/** Simple code-safe identifier check (exam/student codes, usernames). */
export const CODE_RE = /^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$/;
export const SESSION_ID_RE = /^sess_[0-9a-f]{32}$/;
export const DEVICE_ID_RE = /^dev_[0-9a-f]{16}$/;

/** Validates a host header value; returns null if it is not a plausible host[:port]. */
export function cleanHost(h) {
  if (typeof h !== "string") return null;
  const s = h.trim();
  if (!/^(\[[0-9a-fA-F:.]+\]|[A-Za-z0-9.-]{1,253})(:\d{1,5})?$/.test(s)) return null;
  return s;
}
