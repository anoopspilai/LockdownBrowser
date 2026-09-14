// Avaibe Exam — mock backend
// Implements docs/CONTRACT.md §3 (REST), §4 (policy) and §8 (demo credentials).
// Zero dependencies: Node built-ins only. All state is in memory and is lost on restart.

import http from "node:http";
import crypto from "node:crypto";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { EXAMS, publicQuestions } from "./data/exams.js";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const PUBLIC_DIR = path.join(__dirname, "public");
const PORT = Number(process.env.PORT) || 4000;
const VERSION = "0.1.0-mock";
const MAX_BODY_BYTES = 1024 * 1024; // 1 MiB
const RELEASE_CODE_TTL_MS = 60_000;
const RELEASE_AUTH_TTL_MS = 60_000;

// ---------------------------------------------------------------------------
// State
// ---------------------------------------------------------------------------

/** Default policy (§4). `mode` is overridden per session by the device's enrollment mode. */
const DEFAULT_POLICY = Object.freeze({
  mode: "school",
  allowedDomains: ["localhost", "127.0.0.1"],
  allowedApps: [],
  blockExternalDisplay: true,
  externalDisplayAction: "WARN",
  requireSIP: false,
  requireMDM: false,
  requireStandardAccount: false,
  requireAAC: false,
  allowClipboard: false,
  allowPrinting: false,
  allowDownloads: false,
  allowDevTools: false,
  heartbeatIntervalSeconds: 10,
  eventFlushIntervalSeconds: 5,
  minClientVersion: "0.1.0",
  allowStudentReleaseCode: true
});

const state = {
  policy: structuredClone(DEFAULT_POLICY),
  devices: new Map(),   // deviceId -> device
  sessions: new Map(),  // sessionId -> session
  tokens: new Map(),    // sessionToken -> sessionId
  eventSeq: 0
};

const MODES = ["school", "byod"];
const DISPLAY_ACTIONS = ["WARN", "BLOCK_START", "PAUSE", "TERMINATE", "FLAG"];
const LOCKDOWN_MODES = ["aac", "kiosk-fallback", "assigned-access", "none"];
const HEARTBEAT_STATUSES = ["locked", "unlocked", "warning"];
const SEVERITIES = ["low", "medium", "high", "info"];

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

class HttpError extends Error {
  constructor(status, code, message, extra = {}) {
    super(message);
    this.status = status;
    this.code = code;
    this.extra = extra;
  }
}

const hex = (n) => crypto.randomBytes(n).toString("hex");
const nowIso = () => new Date().toISOString();
const isoIn = (ms) => new Date(Date.now() + ms).toISOString();
const isObject = (v) => v !== null && typeof v === "object" && !Array.isArray(v);

function parseVersion(v) {
  if (typeof v !== "string") return null;
  const m = v.trim().match(/^(\d+)\.(\d+)\.(\d+)/);
  return m ? [Number(m[1]), Number(m[2]), Number(m[3])] : null;
}

/** Returns <0, 0, >0 like a comparator. Unparseable versions are treated as 0.0.0. */
function compareVersions(a, b) {
  const pa = parseVersion(a) || [0, 0, 0];
  const pb = parseVersion(b) || [0, 0, 0];
  for (let i = 0; i < 3; i++) if (pa[i] !== pb[i]) return pa[i] - pb[i];
  return 0;
}

function remainingSeconds(session) {
  return Math.max(0, Math.floor((Date.parse(session.expiresAt) - Date.now()) / 1000));
}

function computeRisk(session) {
  let medium = 0;
  let high = 0;
  for (const e of session.events) {
    if (e.severity === "high") high++;
    else if (e.severity === "medium") medium++;
  }
  if (high > 0 || medium >= 3) return "RED";
  if (medium > 0) return "AMBER";
  return "GREEN";
}

function appendEvent(session, { type, severity, timestamp, metadata, source }) {
  const event = {
    id: ++state.eventSeq,
    type,
    severity,
    timestamp: timestamp || nowIso(),
    receivedAt: nowIso(),
    source: source || "client",
    metadata: metadata || {}
  };
  session.events.push(event);
  session.riskLevel = computeRisk(session);
  return event;
}

function queueCommand(session, command) {
  if (command.type === "RELEASE") session.releaseQueued = true;
  session.commands.push(command);
  return command;
}

function makeReleaseCommand(reason) {
  return {
    type: "RELEASE",
    authorizationId: `auth_${hex(6)}`,
    expiresAt: isoIn(RELEASE_AUTH_TTL_MS),
    reason
  };
}

function adminSessionView(session) {
  return {
    sessionId: session.sessionId,
    studentId: session.studentId,
    studentName: session.studentName,
    examCode: session.examCode,
    status: session.status,
    lastHeartbeat: session.lastHeartbeat,
    lockdownMode: session.lockdownMode,
    displayCount: session.displayCount,
    eventCount: session.events.length,
    riskLevel: session.riskLevel,
    // Extra (non-contract) fields used by the mock pages; harmless to real clients.
    examTitle: EXAMS[session.examCode]?.title ?? session.examCode,
    clientStatus: session.clientStatus,
    deviceId: session.deviceId,
    platform: state.devices.get(session.deviceId)?.platform ?? "unknown",
    startedAt: session.startedAt,
    expiresAt: session.expiresAt,
    submittedAt: session.submittedAt,
    remainingSeconds: remainingSeconds(session),
    pendingCommands: session.commands.length
  };
}

function getSession(id) {
  const session = state.sessions.get(id);
  if (!session) throw new HttpError(404, "SESSION_NOT_FOUND", `No session with id ${id}`);
  return session;
}

/** Session endpoints require Authorization: Bearer <sessionToken> that matches :id. */
function authSession(req, id) {
  const header = req.headers.authorization || "";
  const m = header.match(/^Bearer\s+(.+)$/i);
  const token = m ? m[1].trim() : null;
  const session = state.sessions.get(id);
  if (!session || !token || session.sessionToken !== token) {
    throw new HttpError(401, "INVALID_SESSION_TOKEN", "Missing or invalid session token for this session");
  }
  return session;
}

function bearerSession(req) {
  const header = req.headers.authorization || "";
  const m = header.match(/^Bearer\s+(.+)$/i);
  const token = m ? m[1].trim() : null;
  const sessionId = token ? state.tokens.get(token) : null;
  const session = sessionId ? state.sessions.get(sessionId) : null;
  if (!session) throw new HttpError(401, "INVALID_SESSION_TOKEN", "Missing or invalid session token");
  return session;
}

// ---------------------------------------------------------------------------
// Policy validation (PUT /api/v1/admin/policy)
// ---------------------------------------------------------------------------

const POLICY_SCHEMA = {
  mode: (v) => MODES.includes(v) || `must be one of ${MODES.join(", ")}`,
  allowedDomains: (v) => (Array.isArray(v) && v.every((d) => typeof d === "string" && d.trim())) || "must be an array of non-empty strings",
  allowedApps: (v) =>
    (Array.isArray(v) && v.every((a) => isObject(a) && typeof a.bundleId === "string" && a.bundleId && (a.teamId === undefined || typeof a.teamId === "string"))) ||
    "must be an array of { bundleId: string, teamId?: string }",
  blockExternalDisplay: (v) => typeof v === "boolean" || "must be a boolean",
  externalDisplayAction: (v) => DISPLAY_ACTIONS.includes(v) || `must be one of ${DISPLAY_ACTIONS.join(", ")}`,
  requireSIP: (v) => typeof v === "boolean" || "must be a boolean",
  requireMDM: (v) => typeof v === "boolean" || "must be a boolean",
  requireStandardAccount: (v) => typeof v === "boolean" || "must be a boolean",
  requireAAC: (v) => typeof v === "boolean" || "must be a boolean",
  allowClipboard: (v) => typeof v === "boolean" || "must be a boolean",
  allowPrinting: (v) => typeof v === "boolean" || "must be a boolean",
  allowDownloads: (v) => typeof v === "boolean" || "must be a boolean",
  allowDevTools: (v) => typeof v === "boolean" || "must be a boolean",
  heartbeatIntervalSeconds: (v) => (Number.isInteger(v) && v >= 1 && v <= 3600) || "must be an integer between 1 and 3600",
  eventFlushIntervalSeconds: (v) => (Number.isInteger(v) && v >= 1 && v <= 3600) || "must be an integer between 1 and 3600",
  minClientVersion: (v) => (typeof v === "string" && parseVersion(v) !== null) || "must be a semver string like 0.1.0",
  allowStudentReleaseCode: (v) => typeof v === "boolean" || "must be a boolean"
};

function mergePolicy(patch) {
  if (!isObject(patch)) throw new HttpError(400, "INVALID_POLICY", "Body must be a JSON object of policy fields");
  const problems = [];
  for (const [key, value] of Object.entries(patch)) {
    const check = POLICY_SCHEMA[key];
    if (!check) {
      problems.push(`${key}: unknown policy field`);
      continue;
    }
    const result = check(value);
    if (result !== true) problems.push(`${key}: ${result}`);
  }
  if (problems.length) throw new HttpError(400, "INVALID_POLICY", problems.join("; "), { problems });
  const next = { ...state.policy };
  for (const [key, value] of Object.entries(patch)) {
    next[key] = key === "allowedDomains" ? value.map((d) => d.trim()) : key === "allowedApps" ? value.map((a) => ({ bundleId: a.bundleId, teamId: a.teamId ?? "" })) : value;
  }
  state.policy = next;
  return state.policy;
}

// ---------------------------------------------------------------------------
// Preflight evaluation (POST /api/v1/sessions)
// ---------------------------------------------------------------------------

function evaluatePreflight(policy, preflight) {
  const p = isObject(preflight) ? preflight : {};
  const failures = [];
  if (policy.requireSIP && p.sipEnabled !== true) failures.push("SIP_DISABLED");
  if (policy.requireMDM && p.mdmEnrolled !== true) failures.push("MDM_NOT_ENROLLED");
  if (policy.requireStandardAccount && p.accountType !== "standard") failures.push("ADMIN_ACCOUNT");
  if (policy.blockExternalDisplay && policy.externalDisplayAction === "BLOCK_START" && Number(p.displayCount) > 1) failures.push("EXTERNAL_DISPLAY");
  if (p.screenSharingActive === true) failures.push("SCREEN_SHARING");
  if (policy.requireAAC && p.aacEntitlementPresent !== true) failures.push("AAC_REQUIRED");
  if (compareVersions(p.clientVersion, policy.minClientVersion) < 0) failures.push("CLIENT_OUTDATED");
  return failures;
}

// ---------------------------------------------------------------------------
// Route handlers
// ---------------------------------------------------------------------------

const routes = [];
function route(method, pattern, handler) {
  const keys = [];
  const re = new RegExp(
    "^" +
      pattern.replace(/\/:([a-zA-Z]+)/g, (_, k) => {
        keys.push(k);
        return "/([^/]+)";
      }) +
      "/?$"
  );
  routes.push({ method, re, keys, handler });
}

// -- health -----------------------------------------------------------------
route("GET", "/healthz", () => ({
  ok: true,
  version: VERSION,
  serverTime: nowIso(),
  sessions: state.sessions.size,
  devices: state.devices.size
}));

// -- devices ----------------------------------------------------------------
route("POST", "/api/v1/devices/enroll", ({ body }) => {
  const token = typeof body.enrollmentToken === "string" ? body.enrollmentToken.trim() : "";
  if (!token) throw new HttpError(400, "INVALID_ENROLLMENT_TOKEN", "enrollmentToken is required");
  const mode = token.toUpperCase().startsWith("SCHOOL-") ? "school" : "byod";
  const device = {
    deviceId: `dev_${hex(3)}`,
    deviceToken: `devtok_${hex(24)}`,
    mode,
    enrollmentToken: token,
    platform: body.platform ?? "macos",
    osVersion: body.osVersion ?? null,
    clientVersion: body.clientVersion ?? null,
    hardwareId: body.hardwareId ?? null,
    deviceName: body.deviceName ?? null,
    enrolledAt: nowIso()
  };
  state.devices.set(device.deviceId, device);
  return { deviceId: device.deviceId, deviceToken: device.deviceToken, mode, minClientVersion: state.policy.minClientVersion };
});

// -- sessions ---------------------------------------------------------------
route("POST", "/api/v1/sessions", ({ req, body }) => {
  const examCode = typeof body.examCode === "string" ? body.examCode.trim().toUpperCase() : "";
  const exam = EXAMS[examCode];
  if (!exam) throw new HttpError(404, "EXAM_NOT_FOUND", `Unknown exam code "${body.examCode ?? ""}". Try MATH101, SCI202 or DEMO.`);

  const studentCode = String(body.studentCode ?? "").trim() || "0000";
  const deviceId = typeof body.deviceId === "string" ? body.deviceId : null;
  // Lenient: a deviceId the mock does not know (e.g. after a restart) falls back to the policy's mode.
  const device = deviceId ? state.devices.get(deviceId) : null;
  const policy = { ...structuredClone(state.policy), mode: device ? device.mode : state.policy.mode };

  const failures = evaluatePreflight(policy, body.preflight);
  if (failures.length) {
    throw new HttpError(403, "PREFLIGHT_FAILED", `Preflight failed: ${failures.join(", ")}`, { failures });
  }

  const sessionId = `sess_${hex(2)}`;
  const sessionToken = `sesstok_${hex(24)}`;
  const startedAt = nowIso();
  const host = req.headers.host || `localhost:${PORT}`;
  const session = {
    sessionId,
    sessionToken,
    studentId: studentCode,
    studentName: `Demo Student ${studentCode}`,
    examCode,
    deviceId,
    policy,
    preflight: isObject(body.preflight) ? body.preflight : {},
    status: "active", // active | submitted | released | terminated | expired
    clientStatus: null, // locked | unlocked | warning (from heartbeat)
    lockdownMode: null, // reported by the client on the first heartbeat
    displayCount: Number(body.preflight?.displayCount) || 1,
    uptimeSeconds: 0,
    startedAt,
    expiresAt: isoIn(exam.durationMinutes * 60_000),
    submittedAt: null,
    lastHeartbeat: null,
    events: [],
    commands: [],
    releaseCodes: new Map(),
    releaseQueued: false,
    releaseDelivered: false,
    expiryReleaseQueued: false,
    riskLevel: "GREEN",
    examUrl: `http://${host}/exam/${sessionId}`
  };
  state.sessions.set(sessionId, session);
  state.tokens.set(sessionToken, sessionId);
  appendEvent(session, { type: "SESSION_CREATED", severity: "info", source: "server", metadata: { examCode, deviceId } });

  return {
    sessionId,
    sessionToken,
    expiresAt: session.expiresAt,
    student: { id: session.studentId, name: session.studentName },
    exam: { code: exam.code, title: exam.title, durationMinutes: exam.durationMinutes },
    examUrl: session.examUrl,
    policy
  };
});

route("POST", "/api/v1/sessions/:id/heartbeat", ({ req, params, body }) => {
  const session = authSession(req, params.id);
  if (HEARTBEAT_STATUSES.includes(body.status)) session.clientStatus = body.status;
  if (Number.isFinite(Number(body.displayCount))) session.displayCount = Number(body.displayCount);
  if (LOCKDOWN_MODES.includes(body.lockdownMode)) session.lockdownMode = body.lockdownMode;
  if (Number.isFinite(Number(body.uptimeSeconds))) session.uptimeSeconds = Number(body.uptimeSeconds);
  session.lastHeartbeat = nowIso();

  const remaining = remainingSeconds(session);

  // Auto-release after submit (contract §3: "After submit the backend auto-issues a RELEASE on the next heartbeat").
  if (session.status === "submitted" && !session.releaseQueued) {
    queueCommand(session, makeReleaseCommand("Exam submitted"));
  }
  // Auto-release once when time runs out.
  if (remaining === 0 && !session.releaseQueued && !session.expiryReleaseQueued && session.status === "active") {
    session.expiryReleaseQueued = true;
    session.status = "expired";
    queueCommand(session, makeReleaseCommand("Time expired"));
  }

  const command = session.commands.shift() ?? null;
  if (command) {
    if (command.type === "RELEASE") {
      session.releaseDelivered = true;
      session.status = "released";
    } else if (command.type === "TERMINATE") {
      session.status = "terminated";
    }
    appendEvent(session, { type: `COMMAND_${command.type}_DELIVERED`, severity: "info", source: "server", metadata: { ...command } });
  }

  return { ok: true, serverTime: nowIso(), remainingSeconds: remaining, command };
});

route("POST", "/api/v1/sessions/:id/events", ({ req, params, body }) => {
  const session = authSession(req, params.id);
  const list = Array.isArray(body.events) ? body.events : null;
  if (!list) throw new HttpError(400, "INVALID_REQUEST", "Body must be { events: [...] }");
  let accepted = 0;
  for (const e of list) {
    if (!isObject(e) || typeof e.type !== "string" || !e.type) continue;
    appendEvent(session, {
      type: e.type,
      severity: SEVERITIES.includes(e.severity) ? e.severity : "info",
      timestamp: typeof e.timestamp === "string" ? e.timestamp : undefined,
      metadata: isObject(e.metadata) ? e.metadata : {},
      source: "client"
    });
    accepted++;
  }
  return { accepted };
});

route("POST", "/api/v1/sessions/:id/submit", ({ req, params }) => {
  const session = authSession(req, params.id);
  if (!session.submittedAt) {
    session.submittedAt = nowIso();
    if (session.status === "active" || session.status === "expired") session.status = "submitted";
    appendEvent(session, { type: "EXAM_SUBMITTED", severity: "info", source: "server", metadata: {} });
  }
  return { ok: true, submittedAt: session.submittedAt };
});

route("POST", "/api/v1/sessions/:id/unlock", ({ req, params, body }) => {
  const session = authSession(req, params.id);
  const code = String(body.releaseCode ?? "").trim();
  const deny = (httpCode, message) => {
    appendEvent(session, { type: "UNLOCK_DENIED", severity: "low", source: "server", metadata: { reason: httpCode, deviceId: body.deviceId ?? null } });
    throw new HttpError(403, httpCode, message);
  };
  const entry = session.releaseCodes.get(code);
  if (!entry) deny("INVALID_RELEASE_CODE", "That release code is not valid for this session");
  if (entry.used) deny("RELEASE_CODE_USED", "That release code has already been used");
  if (Date.now() > Date.parse(entry.expiresAt)) deny("RELEASE_CODE_EXPIRED", "That release code has expired (codes last 60 seconds)");

  entry.used = true;
  entry.usedAt = nowIso();
  const authorizationId = `auth_${hex(6)}`;
  const expiresAt = isoIn(RELEASE_AUTH_TTL_MS);
  session.releaseQueued = true; // prevents a duplicate auto-RELEASE after a later submit
  session.releaseDelivered = true;
  session.status = "released";
  appendEvent(session, { type: "UNLOCK_COMPLETED", severity: "info", source: "server", metadata: { authorizationId, deviceId: body.deviceId ?? null } });
  return { authorized: true, authorizationId, expiresAt };
});

// Mock-only convenience: the exam page needs the session token to call the API from the browser.
// In production the page would be served with an httpOnly cookie instead. No auth on purpose.
route("GET", "/api/v1/sessions/:id/page-token", ({ params }) => {
  const session = getSession(params.id);
  return { sessionToken: session.sessionToken, examCode: session.examCode };
});

// -- exams ------------------------------------------------------------------
route("GET", "/api/v1/exams", () =>
  Object.values(EXAMS).map((e) => ({ code: e.code, title: e.title, durationMinutes: e.durationMinutes, questionCount: e.questions.length }))
);

route("GET", "/api/v1/exams/:code/questions", ({ req, params }) => {
  bearerSession(req); // any valid session token
  const code = params.code.toUpperCase();
  const questions = publicQuestions(code);
  if (!questions) throw new HttpError(404, "EXAM_NOT_FOUND", `Unknown exam code "${params.code}"`);
  const exam = EXAMS[code];
  return { code: exam.code, title: exam.title, durationMinutes: exam.durationMinutes, questions };
});

// -- admin ------------------------------------------------------------------
route("GET", "/api/v1/admin/sessions", () => [...state.sessions.values()].map(adminSessionView));

route("GET", "/api/v1/admin/sessions/:id", ({ params }) => adminSessionView(getSession(params.id)));

route("POST", "/api/v1/admin/sessions/:id/release-code", ({ params }) => {
  const session = getSession(params.id);
  let code;
  do code = String(crypto.randomInt(0, 1_000_000)).padStart(6, "0");
  while (session.releaseCodes.has(code));
  const expiresAt = isoIn(RELEASE_CODE_TTL_MS);
  session.releaseCodes.set(code, { code, createdAt: nowIso(), expiresAt, used: false });
  appendEvent(session, { type: "RELEASE_CODE_ISSUED", severity: "info", source: "admin", metadata: { expiresAt } });
  return { releaseCode: code, expiresAt };
});

route("POST", "/api/v1/admin/sessions/:id/release", ({ params, body }) => {
  const session = getSession(params.id);
  const reason = typeof body.reason === "string" && body.reason.trim() ? body.reason.trim() : "Teacher release";
  queueCommand(session, makeReleaseCommand(reason));
  appendEvent(session, { type: "RELEASE_QUEUED", severity: "info", source: "admin", metadata: { reason } });
  return { ok: true };
});

route("POST", "/api/v1/admin/sessions/:id/terminate", ({ params, body }) => {
  const session = getSession(params.id);
  const reason = typeof body.reason === "string" && body.reason.trim() ? body.reason.trim() : "Terminated by teacher";
  queueCommand(session, { type: "TERMINATE", reason });
  appendEvent(session, { type: "TERMINATE_QUEUED", severity: "info", source: "admin", metadata: { reason } });
  return { ok: true };
});

route("POST", "/api/v1/admin/sessions/:id/warn", ({ params, body }) => {
  const session = getSession(params.id);
  const message = typeof body.message === "string" ? body.message.trim() : "";
  if (!message) throw new HttpError(400, "INVALID_REQUEST", "message is required");
  queueCommand(session, { type: "WARN", message });
  appendEvent(session, { type: "WARN_QUEUED", severity: "info", source: "admin", metadata: { message } });
  return { ok: true };
});

route("GET", "/api/v1/admin/sessions/:id/events", ({ params }) => getSession(params.id).events);

route("GET", "/api/v1/admin/policy", () => state.policy);
route("PUT", "/api/v1/admin/policy", ({ body }) => mergePolicy(body));
route("PATCH", "/api/v1/admin/policy", ({ body }) => mergePolicy(body));
route("DELETE", "/api/v1/admin/policy", () => {
  state.policy = structuredClone(DEFAULT_POLICY);
  return state.policy;
});

// ---------------------------------------------------------------------------
// Static pages
// ---------------------------------------------------------------------------

const MIME = {
  ".html": "text/html; charset=utf-8",
  ".css": "text/css; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".json": "application/json; charset=utf-8",
  ".svg": "image/svg+xml",
  ".png": "image/png",
  ".ico": "image/x-icon",
  ".txt": "text/plain; charset=utf-8"
};

function sendFile(res, relPath) {
  const abs = path.normalize(path.join(PUBLIC_DIR, relPath));
  if (!abs.startsWith(PUBLIC_DIR + path.sep) && abs !== PUBLIC_DIR) throw new HttpError(404, "NOT_FOUND", "Not found");
  let data;
  try {
    data = fs.readFileSync(abs);
  } catch {
    throw new HttpError(404, "NOT_FOUND", `No such file ${relPath}`);
  }
  res.writeHead(200, {
    "Content-Type": MIME[path.extname(abs).toLowerCase()] || "application/octet-stream",
    "Content-Length": data.length,
    "Cache-Control": "no-store"
  });
  res.end(data);
}

// ---------------------------------------------------------------------------
// HTTP plumbing
// ---------------------------------------------------------------------------

function readBody(req) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    let size = 0;
    req.on("data", (c) => {
      size += c.length;
      if (size > MAX_BODY_BYTES) {
        reject(new HttpError(413, "PAYLOAD_TOO_LARGE", `Body exceeds ${MAX_BODY_BYTES} bytes`));
        req.destroy();
        return;
      }
      chunks.push(c);
    });
    req.on("end", () => {
      const raw = Buffer.concat(chunks).toString("utf8").trim();
      if (!raw) return resolve({});
      try {
        const parsed = JSON.parse(raw);
        resolve(isObject(parsed) ? parsed : { value: parsed });
      } catch {
        reject(new HttpError(400, "INVALID_JSON", "Request body is not valid JSON"));
      }
    });
    req.on("error", reject);
  });
}

function sendJson(res, status, payload) {
  const data = Buffer.from(JSON.stringify(payload, null, 2));
  res.writeHead(status, { "Content-Type": "application/json; charset=utf-8", "Content-Length": data.length, "Cache-Control": "no-store" });
  res.end(data);
}

function sendError(res, err) {
  const status = err instanceof HttpError ? err.status : 500;
  const code = err instanceof HttpError ? err.code : "INTERNAL_ERROR";
  const message = err instanceof HttpError ? err.message : "Unexpected server error";
  if (!(err instanceof HttpError)) console.error(err);
  sendJson(res, status, { error: { code, message }, ...(err.extra || {}) });
}

const server = http.createServer(async (req, res) => {
  const started = process.hrtime.bigint();
  const url = new URL(req.url, `http://${req.headers.host || "localhost"}`);
  const pathname = decodeURIComponent(url.pathname);

  res.setHeader("Access-Control-Allow-Origin", "*");
  res.setHeader("Access-Control-Allow-Methods", "GET, POST, PUT, PATCH, DELETE, OPTIONS");
  res.setHeader("Access-Control-Allow-Headers", "Content-Type, Authorization, X-Device-Id, X-Client-Version");
  res.setHeader("Access-Control-Max-Age", "600");

  res.on("finish", () => {
    const ms = Number(process.hrtime.bigint() - started) / 1e6;
    console.log(`${req.method} ${pathname}${url.search} ${res.statusCode} (${ms.toFixed(ms < 10 ? 1 : 0)}ms)`);
  });

  try {
    if (req.method === "OPTIONS") {
      res.writeHead(204);
      res.end();
      return;
    }

    // Pages and static assets.
    if (req.method === "GET" || req.method === "HEAD") {
      if (pathname === "/" || pathname === "/admin" || pathname === "/admin/") return sendFile(res, "admin.html");
      if (/^\/exam\/[^/]+\/?$/.test(pathname)) return sendFile(res, "exam.html");
      if (pathname.startsWith("/public/")) return sendFile(res, pathname.slice("/public/".length));
    }

    // API routes.
    for (const r of routes) {
      if (r.method !== req.method) continue;
      const m = pathname.match(r.re);
      if (!m) continue;
      const params = Object.fromEntries(r.keys.map((k, i) => [k, m[i + 1]]));
      const body = req.method === "GET" || req.method === "HEAD" ? {} : await readBody(req);
      const result = await r.handler({ req, res, params, body, url });
      return sendJson(res, 200, result ?? {});
    }

    throw new HttpError(404, "NOT_FOUND", `No route for ${req.method} ${pathname}`);
  } catch (err) {
    sendError(res, err);
  }
});

server.listen(PORT, () => {
  console.log(`Avaibe mock backend ${VERSION} listening on http://localhost:${PORT}`);
  console.log(`  Teacher console: http://localhost:${PORT}/admin`);
  console.log(`  Health:          http://localhost:${PORT}/healthz`);
  console.log(`  Demo: enroll with SCHOOL-DEMO or BYOD-DEMO; exams MATH101, SCI202, DEMO`);
});
