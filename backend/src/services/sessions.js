// Session start (CONTRACT §10.2), policy assembly (§10.3), launch tokens, admin views, stale sweep (§10.6).
import { HttpError, nowIso, isoIn, isObject, newSessionId, newToken, sha256hex, safeEqual, compareVersions, clampInt, str, cleanHost, secondsUntil, isPast, SESSION_ID_RE } from "../util.js";
import { transaction, rateLimit } from "../db.js";
import * as exams from "./exams.js";
import * as students from "./students.js";
import * as events from "./events.js";
import * as incidents from "./incidents.js";

export const STATUSES = ["active", "submitted", "released", "terminated", "expired"];

export function baseUrlFor(ctx, req) {
  if (ctx.config.publicBaseUrl) return ctx.config.publicBaseUrl;
  const host = cleanHost(req?.headers?.host) || `localhost:${ctx.config.port}`;
  const proto = ctx.config.tls || req?.socket?.encrypted ? "https" : "http";
  return `${proto}://${host}`;
}

export function publicHostFor(ctx, req) {
  try {
    return new URL(baseUrlFor(ctx, req)).host;
  } catch {
    return null;
  }
}

/** Builds the effective policy for a session. allowedDomains is DERIVED from examUrl + allowedLinks hosts. */
export function assemblePolicy(ctx, exam, device, baseUrl) {
  const base = { ...exams.defaultPolicy(ctx), ...JSON.parse(exam.policy_json) };
  const origin = new URL(baseUrl);
  const links = exams.linksFor(ctx, exam.id).map((l) => ({ label: l.label, url: l.url.startsWith("/") ? new URL(l.url, baseUrl).toString() : l.url }));
  const domains = new Set([origin.hostname.toLowerCase()]);
  for (const l of links) {
    try {
      domains.add(new URL(l.url).hostname.toLowerCase());
    } catch {
      /* validated on save; ignore */
    }
  }
  return {
    ...base,
    mode: device.mode,
    allowedDomains: [...domains],
    allowedLinks: links,
    releaseOnSubmit: exam.release_on_submit,
    heartbeatIntervalSeconds: clampInt(base.heartbeatIntervalSeconds, 2, 120, 10),
    eventFlushIntervalSeconds: clampInt(base.eventFlushIntervalSeconds, 1, 300, 5),
    offlineGraceSeconds: clampInt(base.offlineGraceSeconds, 60, 3600, 600)
  };
}

/** Client-reported preflight vs. policy. Server-authoritative requireAAC is decided from the device record separately. */
export function evaluatePreflight(policy, preflight, clientVersion) {
  const p = isObject(preflight) ? preflight : {};
  const failures = [];
  if (policy.requireSIP && p.sipEnabled !== true) failures.push("SIP_DISABLED");
  if (policy.requireMDM && p.mdmEnrolled !== true) failures.push("MDM_NOT_ENROLLED");
  if (policy.requireStandardAccount && p.accountType !== "standard") failures.push("ADMIN_ACCOUNT");
  if (policy.blockExternalDisplay && policy.externalDisplayAction === "BLOCK_START" && Number(p.displayCount) > 1) failures.push("EXTERNAL_DISPLAY");
  if (p.screenSharingActive === true) failures.push("SCREEN_SHARING");
  if (compareVersions(clientVersion, policy.minClientVersion) < 0) failures.push("CLIENT_OUTDATED");
  return failures;
}

export function jitteredInterval(seconds) {
  const s = clampInt(seconds, 2, 120, 10);
  const jitter = Math.round(s * 0.2 * (Math.random() * 2 - 1));
  return Math.max(2, s + jitter);
}

export function getOr404(ctx, id) {
  if (typeof id !== "string" || !SESSION_ID_RE.test(id)) throw new HttpError(404, "SESSION_NOT_FOUND", "No such session");
  const row = ctx.db.prepare("SELECT * FROM sessions WHERE id = ?").get(id);
  if (!row) throw new HttpError(404, "SESSION_NOT_FOUND", "No such session");
  return row;
}

export function reload(ctx, id) {
  return ctx.db.prepare("SELECT * FROM sessions WHERE id = ?").get(id);
}

export function remainingSeconds(row) {
  if (row.status === "submitted" || row.status === "released" || row.status === "terminated" || row.status === "expired") return 0;
  return secondsUntil(row.expires_at);
}

export function policyOf(row) {
  return JSON.parse(row.policy_json);
}

export const ACCESS_CODE_MAX_FAILURES = 10;
export const ACCESS_CODE_WINDOW_MS = 15 * 60_000;

/**
 * Exam access code check with a per-device, per-exam failure limit. A missing code and a wrong
 * code get separate, plain messages so the student knows what to do. After
 * ACCESS_CODE_MAX_FAILURES wrong codes in a fixed 15-minute window the device is locked out of
 * that exam until the window ends (even the correct code is refused, so guessing cannot win),
 * and one high-severity incident is raised for the teacher.
 */
function checkAccessCodeWithLimit(ctx, { exam, student, device, provided }) {
  const { db } = ctx;
  const key = `access-code:${device.id}:${exam.id}`;
  const now = Date.now();
  const windowStart = now - (now % ACCESS_CODE_WINDOW_MS);
  const row = db.prepare("SELECT window_start, count FROM rate_limits WHERE key = ?").get(key);
  if (row && row.window_start === windowStart && row.count >= ACCESS_CODE_MAX_FAILURES) {
    throw new HttpError(429, "ACCESS_CODE_LOCKED", "Too many wrong access codes on this device. Wait 15 minutes, or ask your teacher.");
  }
  const code = str(provided, 64);
  if (!code) {
    throw new HttpError(403, "ACCESS_CODE_REQUIRED", "This exam needs an access code. Ask your teacher for it and type it in the Access code box.");
  }
  if (exams.checkAccessCode(exam, code)) return;

  rateLimit(db, key, ACCESS_CODE_MAX_FAILURES, ACCESS_CODE_WINDOW_MS);
  const after = db.prepare("SELECT count FROM rate_limits WHERE key = ?").get(key);
  if (after && after.count === ACCESS_CODE_MAX_FAILURES) {
    incidents.raise(ctx, {
      studentId: student.id,
      examId: exam.id,
      type: "ACCESS_CODE_LOCKED",
      severity: "high",
      detail: `Device ${device.id} entered ${ACCESS_CODE_MAX_FAILURES} wrong access codes; locked out for up to 15 minutes`
    });
  }
  throw new HttpError(403, "INVALID_ACCESS_CODE", "The access code is not correct. Check it with your teacher.");
}

/** Starts a session. `device` is the authenticated device row; body is the client request. */
export function start(ctx, { body, device, req, ip, clientVersionHeader }) {
  const { db, config } = ctx;
  if (!isObject(body)) throw new HttpError(400, "INVALID_REQUEST", "Body must be an object");
  const bodyDevice = str(body.deviceId, 64);
  if (bodyDevice && bodyDevice !== device.id) throw new HttpError(403, "DEVICE_MISMATCH", "deviceId does not match the authenticated device");

  const exam = exams.getByCode(ctx, str(body.examCode, 64));
  if (!exam) throw new HttpError(404, "EXAM_NOT_FOUND", `Unknown exam code "${str(body.examCode, 64)}"`);
  const student = students.getByCode(ctx, str(body.studentCode, 64));
  if (!student) throw new HttpError(404, "STUDENT_NOT_FOUND", "Unknown student code");
  if (!exam.open_to_all && !db.prepare("SELECT 1 FROM exam_assignments WHERE exam_id = ? AND student_id = ?").get(exam.id, student.id)) {
    throw new HttpError(403, "NOT_ASSIGNED", "This student is not assigned to this exam");
  }
  if (!exams.isWindowOpen(exam)) throw new HttpError(403, "EXAM_CLOSED", "This exam is not open right now");
  if (exam.access_code_hash) checkAccessCodeWithLimit(ctx, { exam, student, device, provided: body.accessCode });

  const baseUrl = baseUrlFor(ctx, req);
  const policy = assemblePolicy(ctx, exam, device, baseUrl);
  const preflight = isObject(body.preflight) ? body.preflight : {};
  const preflightExtras = isObject(body.preflightExtras) ? body.preflightExtras : null;
  const clientVersion = str(clientVersionHeader, 32) || str(preflight.clientVersion, 32) || str(device.client_version, 32) || "0.0.0";

  const failures = evaluatePreflight(policy, preflight, clientVersion);
  // Server-side AAC/kiosk decision comes from the device registry, never from the client flag.
  if (policy.requireAAC && !device.kiosk_verified) failures.push("AAC_REQUIRED");
  if (failures.length) throw new HttpError(403, "PREFLIGHT_FAILED", `Preflight failed: ${failures.join(", ")}`, { failures });

  // One active session per student per exam.
  const conflict = db
    .prepare("SELECT id, device_id, started_at FROM sessions WHERE student_id = ? AND exam_id = ? AND status IN ('active','submitted') AND expires_at > ? ORDER BY started_at DESC LIMIT 1")
    .get(student.id, exam.id, nowIso());
  if (conflict) {
    incidents.raise(ctx, {
      sessionId: conflict.id,
      studentId: student.id,
      examId: exam.id,
      type: "SESSION_CONFLICT",
      severity: "medium",
      detail: `Second start attempt from device ${device.id} while session ${conflict.id} (device ${conflict.device_id}) is active`
    });
    events.append(ctx, { id: conflict.id, student_id: student.id, exam_id: exam.id }, { type: "SESSION_CONFLICT", severity: "medium", source: "server", metadata: { otherDeviceId: device.id } });
    throw new HttpError(409, "SESSION_ALREADY_ACTIVE", "This student already has an active session for this exam. Ask your teacher to release it first.");
  }

  const sessionId = newSessionId();
  const sessionToken = newToken();
  const launchToken = newToken();
  const now = nowIso();
  const expiresAt = isoIn(exam.duration_minutes * 60_000);
  const launchExpires = isoIn(config.launchTokenSeconds * 1000);

  transaction(db, () => {
    db.prepare(
      `INSERT INTO sessions (id, token_hash, student_id, exam_id, device_id, status, started_at, expires_at, display_count, client_version,
         preflight_json, preflight_extras_json, policy_json, launch_token_hash, launch_token_expires_at, created_at, updated_at)
       VALUES (?, ?, ?, ?, ?, 'active', ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`
    ).run(
      sessionId,
      sha256hex(sessionToken),
      student.id,
      exam.id,
      device.id,
      now,
      expiresAt,
      clampInt(preflight.displayCount, 0, 64, 1),
      clientVersion,
      JSON.stringify(preflight).slice(0, 16_000),
      preflightExtras ? JSON.stringify(preflightExtras).slice(0, 8_000) : null,
      JSON.stringify(policy),
      sha256hex(launchToken),
      launchExpires,
      now,
      now
    );
    const s = { id: sessionId, student_id: student.id, exam_id: exam.id };
    events.append(ctx, s, { type: "SESSION_CREATED", severity: "info", source: "server", metadata: { examCode: exam.code, deviceId: device.id, clientVersion } });
    // Client flag vs. registry mismatch is recorded, never trusted.
    if (typeof preflight.aacEntitlementPresent === "boolean" && preflight.aacEntitlementPresent !== !!device.kiosk_verified) {
      events.append(ctx, s, { type: "POLICY_MISMATCH", severity: "medium", source: "server", metadata: { field: "aacEntitlementPresent", clientReported: preflight.aacEntitlementPresent, deviceKioskVerified: !!device.kiosk_verified } });
    }
  });
  ctx.audit.record(ctx, { actorType: "device", actorId: device.id, action: "session.start", targetType: "session", targetId: sessionId, ip, detail: { examCode: exam.code, studentCode: student.code } });

  return {
    sessionId,
    sessionToken,
    expiresAt,
    serverTime: nowIso(),
    nextBeatInSeconds: jitteredInterval(policy.heartbeatIntervalSeconds),
    student: { id: student.code, name: student.name },
    exam: { code: exam.code, title: exam.title, durationMinutes: exam.duration_minutes },
    examUrl: `${baseUrl}/exam/launch?lt=${launchToken}`,
    policy
  };
}

/** Redeems a one-time launch token; returns { sessionId, cookieToken } or throws 403. */
export function redeemLaunchToken(ctx, lt) {
  if (typeof lt !== "string" || !/^[0-9a-f]{48}$/.test(lt)) throw new HttpError(403, "LAUNCH_TOKEN_INVALID", "Invalid launch link");
  const hash = sha256hex(lt);
  const row = ctx.db.prepare("SELECT id, launch_token_hash, launch_token_expires_at, launch_token_used_at, status FROM sessions WHERE launch_token_hash = ?").get(hash);
  if (!row || !safeEqual(row.launch_token_hash, hash)) throw new HttpError(403, "LAUNCH_TOKEN_INVALID", "Invalid launch link");
  if (row.launch_token_used_at) throw new HttpError(403, "LAUNCH_TOKEN_USED", "This launch link was already used");
  if (isPast(row.launch_token_expires_at)) throw new HttpError(403, "LAUNCH_TOKEN_EXPIRED", "This launch link has expired");
  const cookieToken = newToken();
  const upd = ctx.db
    .prepare("UPDATE sessions SET launch_token_used_at = ?, exam_cookie_hash = ?, updated_at = ? WHERE id = ? AND launch_token_used_at IS NULL")
    .run(nowIso(), sha256hex(cookieToken), nowIso(), row.id);
  if (upd.changes !== 1) throw new HttpError(403, "LAUNCH_TOKEN_USED", "This launch link was already used");
  return { sessionId: row.id, cookieToken };
}

export function answersOf(ctx, sessionId) {
  const out = {};
  for (const r of ctx.db.prepare("SELECT question_id, answer FROM session_answers WHERE session_id = ?").all(sessionId)) out[r.question_id] = r.answer;
  return out;
}

const HEARTBEAT_STALE_MULTIPLIER = 3;

export function isStale(row) {
  if (row.status !== "active") return false;
  const interval = clampInt(policyOf(row).heartbeatIntervalSeconds, 2, 120, 10);
  const last = Date.parse(row.last_heartbeat || row.started_at);
  return Date.now() - last > HEARTBEAT_STALE_MULTIPLIER * interval * 1000 + 5000;
}

/** Periodic sweep: mark active sessions with > 3 missed beats as stale and raise an incident once. */
export function sweepStale(ctx) {
  const rows = ctx.db.prepare("SELECT * FROM sessions WHERE status = 'active' AND stale = 0").all();
  let marked = 0;
  for (const row of rows) {
    if (!isStale(row)) continue;
    ctx.db.prepare("UPDATE sessions SET stale = 1, stale_since = ?, updated_at = ? WHERE id = ? AND stale = 0").run(nowIso(), nowIso(), row.id);
    events.append(ctx, row, { type: "SESSION_STALE", severity: "medium", source: "server", metadata: { lastHeartbeat: row.last_heartbeat } });
    incidents.raise(ctx, { sessionId: row.id, studentId: row.student_id, examId: row.exam_id, type: "SESSION_STALE", severity: "medium", detail: `No heartbeat since ${row.last_heartbeat || row.started_at}` });
    marked++;
  }
  return marked;
}

/** Admin list/detail view. Client-reported fields are grouped under `clientReported` and mirrored at top level for v1 compatibility. */
export function adminView(ctx, row, { detail = false } = {}) {
  const student = ctx.db.prepare("SELECT code, name FROM students WHERE id = ?").get(row.student_id);
  const exam = ctx.db.prepare("SELECT code, title, release_on_submit FROM exams WHERE id = ?").get(row.exam_id);
  const device = ctx.db.prepare("SELECT platform, device_name, kiosk_verified, mode FROM devices WHERE id = ?").get(row.device_id);
  const pending = ctx.db.prepare("SELECT COUNT(*) AS n FROM pending_commands WHERE session_id = ? AND delivered_at IS NULL").get(row.id).n;
  const policy = policyOf(row);
  const stale = !!row.stale || isStale(row);
  const view = {
    sessionId: row.id,
    studentId: student?.code ?? row.student_id,
    studentName: student?.name ?? "?",
    examCode: exam?.code ?? "?",
    examTitle: exam?.title ?? exam?.code ?? "?",
    status: row.status,
    stale,
    staleSince: row.stale_since,
    lastHeartbeat: row.last_heartbeat,
    // v1-compatible mirrors; these three are CLIENT-REPORTED and must be labelled as such in any UI.
    lockdownMode: row.lockdown_mode,
    displayCount: row.display_count,
    clientStatus: row.client_status,
    clientReported: {
      lockdownMode: row.lockdown_mode,
      displayCount: row.display_count,
      clientStatus: row.client_status,
      uptimeSeconds: row.uptime_seconds,
      clientVersion: row.client_version,
      clientVersionOk: compareVersions(row.client_version, policy.minClientVersion) >= 0
    },
    eventCount: row.event_count,
    storedEventCount: row.stored_event_count,
    riskLevel: events.riskLevel(row),
    deviceId: row.device_id,
    platform: device?.platform ?? "unknown",
    deviceName: device?.device_name ?? null,
    deviceKioskVerified: !!device?.kiosk_verified,
    mode: policy.mode,
    requireAAC: !!policy.requireAAC,
    releaseOnSubmit: exam?.release_on_submit ?? policy.releaseOnSubmit,
    startedAt: row.started_at,
    expiresAt: row.expires_at,
    submittedAt: row.submitted_at,
    releasedAt: row.released_at,
    terminatedAt: row.terminated_at,
    remainingSeconds: remainingSeconds(row),
    pendingCommands: pending,
    releaseCodeLocked: !!row.release_code_locked,
    releaseCodeFailures: row.release_code_failures
  };
  if (detail) {
    const answers = answersOf(ctx, row.id);
    view.preflight = JSON.parse(row.preflight_json);
    view.preflightExtras = row.preflight_extras_json ? JSON.parse(row.preflight_extras_json) : null;
    view.policy = policy;
    view.answers = answers;
    view.score = exams.score(ctx, row.exam_id, answers);
  }
  return view;
}

export function listForAdmin(ctx, { status = null, limit = 500 } = {}) {
  const lim = Math.min(Math.max(Number(limit) || 500, 1), 2000);
  const rows =
    status && STATUSES.includes(status)
      ? ctx.db.prepare("SELECT * FROM sessions WHERE status = ? ORDER BY started_at DESC LIMIT ?").all(status, lim)
      : status === "live"
        ? ctx.db.prepare("SELECT * FROM sessions WHERE status IN ('active','submitted') ORDER BY started_at DESC LIMIT ?").all(lim)
        : ctx.db.prepare("SELECT * FROM sessions ORDER BY started_at DESC LIMIT ?").all(lim);
  return rows.map((r) => adminView(ctx, r));
}
