// Exam-session authentication: Bearer token bound to sessionId AND deviceId (CONTRACT §10.1).
import { HttpError, sha256hex, safeEqual, SESSION_ID_RE } from "../util.js";
import { requireDevice } from "./device.js";
import { parseCookies } from "../http/router.js";

export const EXAM_COOKIE = "avaibe_exam";

export function bearerToken(req) {
  const header = req.headers.authorization || "";
  const m = header.match(/^Bearer\s+([0-9a-f]{48})\s*$/i);
  return m ? m[1].toLowerCase() : null;
}

/** Device auth + session token check. Returns { device, session } or throws 401. */
export function requireSession(ctx, req, sessionId) {
  const device = requireDevice(ctx, req);
  const token = bearerToken(req);
  if (!token || typeof sessionId !== "string" || !SESSION_ID_RE.test(sessionId)) {
    throw new HttpError(401, "INVALID_SESSION_TOKEN", "Missing or invalid session token for this session");
  }
  const session = ctx.db.prepare("SELECT * FROM sessions WHERE id = ?").get(sessionId);
  if (!session || !safeEqual(session.token_hash, sha256hex(token)) || session.device_id !== device.id) {
    throw new HttpError(401, "INVALID_SESSION_TOKEN", "Missing or invalid session token for this session");
  }
  return { device, session };
}

/** Cookie auth for the exam web page (/exam/:id and /exam/api/*). Returns the session row or throws 401. */
export function requireExamCookie(ctx, req, expectedSessionId = null) {
  const raw = parseCookies(req.headers.cookie)[EXAM_COOKIE];
  const m = typeof raw === "string" ? raw.match(/^(sess_[0-9a-f]{32})\.([0-9a-f]{48})$/) : null;
  if (!m) throw new HttpError(401, "EXAM_COOKIE_REQUIRED", "Open the exam from the Avaibe Exam app");
  const [, sessionId, token] = m;
  if (expectedSessionId && expectedSessionId !== sessionId) throw new HttpError(403, "SESSION_MISMATCH", "This exam link belongs to a different session");
  const session = ctx.db.prepare("SELECT * FROM sessions WHERE id = ?").get(sessionId);
  if (!session || !session.exam_cookie_hash || !safeEqual(session.exam_cookie_hash, sha256hex(token))) {
    throw new HttpError(401, "EXAM_COOKIE_INVALID", "Exam page session is invalid. Reopen the exam from the app.");
  }
  return session;
}

export function examCookie(config, sessionId, token, { clear = false } = {}) {
  const parts = [`${EXAM_COOKIE}=${clear ? "" : `${sessionId}.${token}`}`, "Path=/exam/", "HttpOnly", "SameSite=Strict"];
  if (config.cookieSecure) parts.push("Secure");
  if (clear) parts.push("Max-Age=0");
  return parts.join("; ");
}
