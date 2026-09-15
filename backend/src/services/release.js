// Release codes, unlock, remote commands (RELEASE / TERMINATE / WARN) and signed authorizations (CONTRACT §10.4, §10.5).
import crypto from "node:crypto";
import { HttpError, nowIso, isoIn, sha256hex, safeEqual, newNonce, str, requireReason, isPast } from "../util.js";
import { transaction } from "../db.js";
import * as events from "./events.js";
import * as incidents from "./incidents.js";

const codeHash = (sessionId, code) => sha256hex(`rc:${sessionId}:${code}`);

/** Builds a signed authorization for RELEASE/TERMINATE bound to session + device (issuedAt + 120 s). */
export function buildAuthorization(ctx, type, session) {
  const issuedAt = nowIso();
  const expiresAt = isoIn(ctx.config.commandAuthSeconds * 1000);
  return ctx.signer.signCommand({ type, sessionId: session.id, deviceId: session.device_id, nonce: newNonce(), issuedAt, expiresAt });
}

/** Admin: issue a 6-digit release code. The digits are returned once and never stored or logged in clear. */
export function issueReleaseCode(ctx, session, { staff, reason, ip }) {
  if (session.status === "released") throw new HttpError(409, "SESSION_NOT_LOCKED", "Session is already released");
  if (session.release_code_locked) throw new HttpError(423, "RELEASE_CODE_LOCKED", "Release-code entry is locked for this session (too many failures). Use remote release.");
  const code = String(crypto.randomInt(0, 1_000_000)).padStart(6, "0");
  const expiresAt = isoIn(ctx.config.releaseCodeSeconds * 1000);
  ctx.db
    .prepare("INSERT INTO release_codes (session_id, device_id, code_hash, created_by, created_at, expires_at) VALUES (?, ?, ?, ?, ?, ?)")
    .run(session.id, session.device_id, codeHash(session.id, code), staff.username, nowIso(), expiresAt);
  events.append(ctx, session, { type: "RELEASE_CODE_ISSUED", severity: "info", source: "admin", metadata: { expiresAt, by: staff.username, reason } });
  ctx.audit.staffAction(ctx, staff, "session.release_code", { targetType: "session", targetId: session.id, reason, ip, detail: { expiresAt } });
  return { releaseCode: code, expiresAt };
}

/** Student-entered code (POST /sessions/:id/unlock). `device` is the authenticated device. */
export function unlock(ctx, session, device, body) {
  const { db, config } = ctx;
  const bodyDevice = str(body.deviceId, 64);
  if (bodyDevice && bodyDevice !== device.id) throw new HttpError(403, "DEVICE_MISMATCH", "deviceId does not match the authenticated device");
  if (session.device_id !== device.id) throw new HttpError(403, "DEVICE_MISMATCH", "This session belongs to a different device");
  const deny = (code, message, { countFailure = true } = {}) => {
    events.append(ctx, session, { type: "UNLOCK_DENIED", severity: "low", source: "server", metadata: { reason: code, deviceId: device.id } });
    if (countFailure) {
      const fresh = db.prepare("UPDATE sessions SET release_code_failures = release_code_failures + 1, updated_at = ? WHERE id = ? RETURNING release_code_failures").get(nowIso(), session.id);
      if (fresh.release_code_failures >= config.releaseCodeMaxFailures) {
        db.prepare("UPDATE sessions SET release_code_locked = 1, updated_at = ? WHERE id = ?").run(nowIso(), session.id);
        events.append(ctx, session, { type: "RELEASE_CODE_LOCKED", severity: "high", source: "server", metadata: { failures: fresh.release_code_failures } });
        incidents.raise(ctx, { sessionId: session.id, studentId: session.student_id, examId: session.exam_id, type: "RELEASE_CODE_LOCKED", severity: "high", detail: `${fresh.release_code_failures} wrong release codes entered from device ${device.id}` });
        throw new HttpError(423, "RELEASE_CODE_LOCKED", "Too many wrong codes. Release-code entry is locked; ask your teacher for a remote release.");
      }
    }
    throw new HttpError(403, code, message);
  };

  if (session.release_code_locked) throw new HttpError(423, "RELEASE_CODE_LOCKED", "Release-code entry is locked for this session");
  const code = str(body.releaseCode, 16);
  if (!/^\d{6}$/.test(code)) deny("INVALID_RELEASE_CODE", "Release code must be 6 digits");

  const hash = codeHash(session.id, code);
  const candidates = db.prepare("SELECT * FROM release_codes WHERE session_id = ? AND device_id = ? ORDER BY id DESC LIMIT 50").all(session.id, device.id);
  let match = null;
  for (const c of candidates) if (safeEqual(c.code_hash, hash)) match = match || c; // constant-time per row; no early exit
  if (!match) deny("INVALID_RELEASE_CODE", "That release code is not valid for this session");
  if (match.used_at) deny("RELEASE_CODE_USED", "That release code has already been used", { countFailure: false });
  if (isPast(match.expires_at)) deny("RELEASE_CODE_EXPIRED", "That release code has expired (codes last 60 seconds)", { countFailure: false });

  const authorization = buildAuthorization(ctx, "RELEASE", session);
  transaction(db, () => {
    const upd = db.prepare("UPDATE release_codes SET used_at = ? WHERE id = ? AND used_at IS NULL").run(nowIso(), match.id);
    if (upd.changes !== 1) deny("RELEASE_CODE_USED", "That release code has already been used", { countFailure: false });
    db.prepare("UPDATE sessions SET status = 'released', released_at = ?, release_queued = 1, updated_at = ? WHERE id = ?").run(nowIso(), nowIso(), session.id);
    events.append(ctx, session, { type: "UNLOCK_COMPLETED", severity: "info", source: "server", metadata: { nonce: authorization.nonce, deviceId: device.id } });
  });
  ctx.audit.record(ctx, { actorType: "device", actorId: device.id, action: "session.unlock", targetType: "session", targetId: session.id, detail: { nonce: authorization.nonce } });
  return { authorized: true, authorizationId: authorization.nonce, expiresAt: authorization.expiresAt, authorization };
}

/** Queues a command for delivery on the next heartbeat. RELEASE is queued at most once per session. */
export function queueCommand(ctx, session, { type, reason = null, message = null, createdBy = "system" }) {
  if (!["RELEASE", "TERMINATE", "WARN"].includes(type)) throw new Error("bad command type");
  return transaction(ctx.db, () => {
    if (type === "RELEASE") {
      const fresh = ctx.db.prepare("SELECT release_queued, status FROM sessions WHERE id = ?").get(session.id);
      if (fresh.release_queued || fresh.status === "released") return false;
      ctx.db.prepare("UPDATE sessions SET release_queued = 1, updated_at = ? WHERE id = ?").run(nowIso(), session.id);
    }
    ctx.db
      .prepare("INSERT INTO pending_commands (session_id, type, reason, message, created_by, created_at) VALUES (?, ?, ?, ?, ?, ?)")
      .run(session.id, type, reason, message, createdBy, nowIso());
    events.append(ctx, session, { type: `${type}_QUEUED`, severity: "info", source: createdBy === "system" ? "server" : "admin", metadata: { reason, message, by: createdBy } });
    return true;
  });
}

/** Pops ONE command FIFO, signing RELEASE/TERMINATE at delivery time so the 120 s window starts now. */
export function popCommand(ctx, session) {
  const row = ctx.db.prepare("SELECT * FROM pending_commands WHERE session_id = ? AND delivered_at IS NULL ORDER BY id LIMIT 1").get(session.id);
  if (!row) return null;
  const command = { type: row.type, reason: row.reason, message: row.message };
  let authorization = null;
  if (row.type === "RELEASE" || row.type === "TERMINATE") {
    authorization = buildAuthorization(ctx, row.type, session);
    command.authorization = authorization;
    // v1 compatibility fields
    command.authorizationId = authorization.nonce;
    command.expiresAt = authorization.expiresAt;
  }
  transaction(ctx.db, () => {
    ctx.db.prepare("UPDATE pending_commands SET delivered_at = ?, authorization_json = ? WHERE id = ?").run(nowIso(), authorization ? JSON.stringify({ ...authorization, signature: "[omitted]" }) : null, row.id);
    if (row.type === "RELEASE") ctx.db.prepare("UPDATE sessions SET status = 'released', released_at = ?, updated_at = ? WHERE id = ?").run(nowIso(), nowIso(), session.id);
    else if (row.type === "TERMINATE") ctx.db.prepare("UPDATE sessions SET status = 'terminated', terminated_at = ?, updated_at = ? WHERE id = ? AND status <> 'released'").run(nowIso(), nowIso(), session.id);
    events.append(ctx, session, { type: `COMMAND_${row.type}_DELIVERED`, severity: "info", source: "server", metadata: { reason: row.reason, message: row.message, nonce: authorization?.nonce ?? null } });
  });
  return command;
}

// --- staff actions ---------------------------------------------------------------------------

export function remoteRelease(ctx, session, { staff, body, ip }) {
  const reason = requireReason(body);
  if (session.status === "released") throw new HttpError(409, "SESSION_NOT_LOCKED", "Session is already released");
  const queued = queueCommand(ctx, session, { type: "RELEASE", reason, createdBy: staff.username });
  ctx.audit.staffAction(ctx, staff, "session.release", { targetType: "session", targetId: session.id, reason, ip, detail: { queued } });
  return { ok: true, queued };
}

export function remoteTerminate(ctx, session, { staff, body, ip }) {
  const reason = requireReason(body);
  if (["released", "terminated"].includes(session.status)) throw new HttpError(409, "SESSION_ENDED", `Session is already ${session.status}`);
  queueCommand(ctx, session, { type: "TERMINATE", reason, createdBy: staff.username });
  incidents.raise(ctx, { sessionId: session.id, studentId: session.student_id, examId: session.exam_id, type: "TERMINATED_BY_STAFF", severity: "high", detail: `${staff.username}: ${reason}` });
  ctx.audit.staffAction(ctx, staff, "session.terminate", { targetType: "session", targetId: session.id, reason, ip });
  return { ok: true };
}

export function remoteWarn(ctx, session, { staff, body, ip }) {
  const message = str(body.message, 500);
  if (!message) throw new HttpError(400, "INVALID_REQUEST", "message is required");
  const reason = str(body.reason, 500) || message;
  queueCommand(ctx, session, { type: "WARN", reason, message, createdBy: staff.username });
  ctx.audit.staffAction(ctx, staff, "session.warn", { targetType: "session", targetId: session.id, reason, ip, detail: { message } });
  return { ok: true };
}
