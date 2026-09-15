// Exams: CRUD, questions (answer key stays server-side), allowedLinks, per-exam policy, window, access code.
import { HttpError, nowIso, isObject, newId, str, sha256hex, safeEqual, CODE_RE, parseVersion, clampInt, normalizeSitePattern } from "../util.js";
import { transaction, getSetting, setSetting } from "../db.js";
import { MIN_CLIENT_VERSION } from "../config.js";

export const MODES = ["school", "byod"];
export const DISPLAY_ACTIONS = ["WARN", "BLOCK_START", "PAUSE", "TERMINATE", "FLAG"];

/** Built-in default policy (CONTRACT §4 + §10.3). Editable via /api/v1/admin/policy; exams override fields. */
export const BUILTIN_DEFAULT_POLICY = Object.freeze({
  mode: "school",
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
  minClientVersion: MIN_CLIENT_VERSION,
  allowStudentReleaseCode: true,
  offlineGraceSeconds: 600
});

const bool = (v) => typeof v === "boolean" || "must be a boolean";
export const POLICY_SCHEMA = {
  mode: (v) => MODES.includes(v) || `must be one of ${MODES.join(", ")}`,
  allowedApps: (v) =>
    (Array.isArray(v) && v.length <= 50 && v.every((a) => isObject(a) && typeof a.bundleId === "string" && a.bundleId && (a.teamId === undefined || typeof a.teamId === "string"))) ||
    "must be an array of { bundleId, teamId? }",
  blockExternalDisplay: bool,
  externalDisplayAction: (v) => DISPLAY_ACTIONS.includes(v) || `must be one of ${DISPLAY_ACTIONS.join(", ")}`,
  requireSIP: bool,
  requireMDM: bool,
  requireStandardAccount: bool,
  requireAAC: bool,
  allowClipboard: bool,
  allowPrinting: bool,
  allowDownloads: bool,
  allowDevTools: bool,
  heartbeatIntervalSeconds: (v) => (Number.isInteger(v) && v >= 2 && v <= 120) || "must be an integer between 2 and 120",
  eventFlushIntervalSeconds: (v) => (Number.isInteger(v) && v >= 1 && v <= 300) || "must be an integer between 1 and 300",
  minClientVersion: (v) => (typeof v === "string" && parseVersion(v) !== null) || "must be a semver string like 0.1.0",
  allowStudentReleaseCode: bool,
  offlineGraceSeconds: (v) => (Number.isInteger(v) && v >= 60 && v <= 3600) || "must be an integer between 60 and 3600"
};

/** Validates a policy patch; unknown fields and derived fields (allowedDomains, allowedLinks, releaseOnSubmit) are rejected. */
export function validatePolicyPatch(patch) {
  if (!isObject(patch)) throw new HttpError(400, "INVALID_POLICY", "policy must be a JSON object");
  const problems = [];
  const clean = {};
  for (const [key, value] of Object.entries(patch)) {
    const check = POLICY_SCHEMA[key];
    if (!check) {
      problems.push(`${key}: unknown or derived policy field`);
      continue;
    }
    const result = check(value);
    if (result !== true) {
      problems.push(`${key}: ${result}`);
      continue;
    }
    clean[key] = key === "allowedApps" ? value.map((a) => ({ bundleId: a.bundleId.slice(0, 200), teamId: (a.teamId ?? "").slice(0, 20) })) : value;
  }
  if (problems.length) throw new HttpError(400, "INVALID_POLICY", problems.join("; "), { problems });
  return clean;
}

export function defaultPolicy(ctx) {
  return { ...BUILTIN_DEFAULT_POLICY, ...getSetting(ctx.db, "defaultPolicy", {}) };
}

export function updateDefaultPolicy(ctx, patch) {
  const clean = validatePolicyPatch(patch);
  const next = { ...getSetting(ctx.db, "defaultPolicy", {}), ...clean };
  setSetting(ctx.db, "defaultPolicy", next);
  return defaultPolicy(ctx);
}

export function resetDefaultPolicy(ctx) {
  setSetting(ctx.db, "defaultPolicy", {});
  return defaultPolicy(ctx);
}

// --- allowedLinks -----------------------------------------------------------------------------

/**
 * Validates a link: label 1..80 chars; url either https://... or a same-host http(s) URL (matching publicHost), or a
 * root-relative path like /resources/x.html (resolved against the public base URL at session start).
 */
export function validateLink(link, publicHost) {
  if (!isObject(link)) throw new HttpError(400, "INVALID_LINK", "allowedLinks entries must be objects { label, url }");
  const label = str(link.label, 80);
  const url = str(link.url, 2000);
  if (!label) throw new HttpError(400, "INVALID_LINK", "link label is required");
  if (!url) throw new HttpError(400, "INVALID_LINK", `link "${label}": url is required`);
  if (url.startsWith("/")) {
    if (url.startsWith("//") || url.includes("..") || /[\s<>"'\\]/.test(url)) throw new HttpError(400, "INVALID_LINK", `link "${label}": invalid relative path`);
    return { label, url };
  }
  let u;
  try {
    u = new URL(url);
  } catch {
    throw new HttpError(400, "INVALID_LINK", `link "${label}": not a valid URL`);
  }
  if (u.username || u.password) throw new HttpError(400, "INVALID_LINK", `link "${label}": userinfo is not allowed`);
  const sameHost = publicHost && u.host.toLowerCase() === publicHost.toLowerCase();
  if (u.protocol !== "https:" && !(u.protocol === "http:" && sameHost)) {
    throw new HttpError(400, "INVALID_LINK", `link "${label}": url must be https:// (or http:// on this server's own host)`);
  }
  return { label, url: u.toString() };
}

// --- external-website exams (CONTRACT §11) --------------------------------------------------

export const EXAM_KINDS = ["questions", "external"];

/** Start address of an external exam: https (http only on this server's own host or localhost), no userinfo. */
export function validateStartUrl(value, publicHost) {
  const url = str(value, 2000);
  if (!url) throw new HttpError(400, "INVALID_START_URL", "An external exam needs a start address (https://...)");
  let u;
  try {
    u = new URL(url);
  } catch {
    throw new HttpError(400, "INVALID_START_URL", "The start address is not a valid URL");
  }
  if (u.username || u.password) throw new HttpError(400, "INVALID_START_URL", "The start address must not contain a user name or password");
  const host = u.hostname.toLowerCase();
  // Same rule as the apps: plain http only for local testing.
  const local = host === "localhost" || host === "127.0.0.1" || host === "[::1]";
  if (u.protocol !== "https:" && !(u.protocol === "http:" && local)) {
    throw new HttpError(400, "INVALID_START_URL", "The start address must start with https://");
  }
  return u.toString();
}

/** Teacher's extra sites: host or *.host, normalised, de-duplicated, at most 48 (the start host and its www twin make 50). */
export function validateAllowedSites(list) {
  if (!Array.isArray(list)) throw new HttpError(400, "INVALID_SITE", "allowedSites must be a list of site addresses");
  const out = [];
  for (const raw of list) {
    if (typeof raw !== "string" || !raw.trim()) continue;
    const p = normalizeSitePattern(raw);
    if (!p) {
      throw new HttpError(400, "INVALID_SITE", `"${String(raw).slice(0, 80)}" is not a valid site. Use a host like cdn.example.com or *.example.com (no https://, no paths; *.com is not allowed).`);
    }
    if (!out.includes(p)) out.push(p);
  }
  if (out.length > 48) throw new HttpError(400, "INVALID_SITE", "At most 48 allowed sites");
  return out;
}

// --- questions -------------------------------------------------------------------------------

export function validateQuestions(list) {
  if (!Array.isArray(list)) throw new HttpError(400, "INVALID_QUESTIONS", "questions must be an array");
  if (list.length > 500) throw new HttpError(400, "INVALID_QUESTIONS", "at most 500 questions");
  const seen = new Set();
  return list.map((q, i) => {
    if (!isObject(q)) throw new HttpError(400, "INVALID_QUESTIONS", `question ${i + 1}: must be an object`);
    const id = str(q.id, 32) || `q${i + 1}`;
    if (!/^[A-Za-z0-9_-]{1,32}$/.test(id)) throw new HttpError(400, "INVALID_QUESTIONS", `question ${i + 1}: invalid id`);
    if (seen.has(id)) throw new HttpError(400, "INVALID_QUESTIONS", `question ${i + 1}: duplicate id ${id}`);
    seen.add(id);
    const text = str(q.text, 2000);
    if (!text) throw new HttpError(400, "INVALID_QUESTIONS", `question ${i + 1}: text is required`);
    if (!Array.isArray(q.options) || q.options.length < 2 || q.options.length > 10 || !q.options.every((o) => typeof o === "string" && o.trim())) {
      throw new HttpError(400, "INVALID_QUESTIONS", `question ${i + 1}: options must be 2-10 non-empty strings`);
    }
    const options = q.options.map((o) => o.trim().slice(0, 500));
    const answer = q.answer === undefined || q.answer === null || q.answer === "" ? null : str(q.answer, 500);
    if (answer !== null && !options.includes(answer)) throw new HttpError(400, "INVALID_QUESTIONS", `question ${i + 1}: answer must be one of the options`);
    return { id, text, options, answer };
  });
}

// --- CRUD -------------------------------------------------------------------------------------

export function getByCode(ctx, code) {
  if (typeof code !== "string") return null;
  return ctx.db.prepare("SELECT * FROM exams WHERE code = ?").get(code.trim().toUpperCase());
}

export function getById(ctx, id) {
  return ctx.db.prepare("SELECT * FROM exams WHERE id = ?").get(id);
}

export function questionsFor(ctx, examId, { withAnswers = false } = {}) {
  return ctx.db
    .prepare("SELECT qid, text, options_json, answer FROM exam_questions WHERE exam_id = ? ORDER BY position")
    .all(examId)
    .map((r) => ({ id: r.qid, text: r.text, options: JSON.parse(r.options_json), ...(withAnswers ? { answer: r.answer } : {}) }));
}

/** Questions without the answer key: what the exam page and clients may see. */
export function publicQuestions(ctx, examId) {
  return questionsFor(ctx, examId, { withAnswers: false });
}

export function linksFor(ctx, examId) {
  return ctx.db.prepare("SELECT label, url FROM exam_links WHERE exam_id = ? ORDER BY position").all(examId).map((r) => ({ label: r.label, url: r.url }));
}

export function assignedStudentCodes(ctx, examId) {
  return ctx.db
    .prepare("SELECT s.code FROM exam_assignments a JOIN students s ON s.id = a.student_id WHERE a.exam_id = ? ORDER BY s.code")
    .all(examId)
    .map((r) => r.code);
}

export function publicExam(ctx, row, { full = false } = {}) {
  const out = {
    code: row.code,
    title: row.title,
    durationMinutes: row.duration_minutes,
    isOpen: !!row.is_open,
    openFrom: row.open_from,
    openUntil: row.open_until,
    openToAll: !!row.open_to_all,
    hasAccessCode: !!row.access_code_hash,
    releaseOnSubmit: row.release_on_submit,
    kind: row.kind || "questions",
    startUrl: row.start_url || null,
    allowedSites: JSON.parse(row.allowed_sites_json || "[]"),
    questionCount: ctx.db.prepare("SELECT COUNT(*) AS n FROM exam_questions WHERE exam_id = ?").get(row.id).n,
    allowedLinks: linksFor(ctx, row.id),
    policy: JSON.parse(row.policy_json),
    createdAt: row.created_at,
    updatedAt: row.updated_at
  };
  if (full) {
    out.questions = questionsFor(ctx, row.id, { withAnswers: true });
    out.assignedStudentCodes = assignedStudentCodes(ctx, row.id);
    out.activeSessions = ctx.db.prepare("SELECT COUNT(*) AS n FROM sessions WHERE exam_id = ? AND status IN ('active','submitted')").get(row.id).n;
  }
  return out;
}

function parseIsoOrNull(v, name) {
  if (v === undefined || v === null || v === "") return null;
  if (typeof v !== "string" || !Number.isFinite(Date.parse(v))) throw new HttpError(400, "INVALID_REQUEST", `${name} must be an ISO-8601 date-time`);
  return new Date(Date.parse(v)).toISOString();
}

/** Creates or updates an exam. `body` fields are optional on update. */
export function upsert(ctx, body, { existing = null, publicHost = null } = {}) {
  if (!isObject(body)) throw new HttpError(400, "INVALID_REQUEST", "Body must be an object");
  const now = nowIso();
  const code = existing ? existing.code : str(body.code, 32).toUpperCase();
  if (!existing) {
    if (!CODE_RE.test(code)) throw new HttpError(400, "INVALID_REQUEST", "code must be 1-64 chars: letters, digits, _ . -");
    if (getByCode(ctx, code)) throw new HttpError(409, "EXAM_EXISTS", `Exam ${code} already exists`);
  }
  const title = body.title !== undefined ? str(body.title, 200) : existing?.title;
  if (!title) throw new HttpError(400, "INVALID_REQUEST", "title is required");
  const duration = body.durationMinutes !== undefined ? clampInt(body.durationMinutes, 1, 1440, null) : existing?.duration_minutes;
  if (!Number.isInteger(duration)) throw new HttpError(400, "INVALID_REQUEST", "durationMinutes must be 1-1440");
  const releaseOnSubmit = body.releaseOnSubmit !== undefined ? body.releaseOnSubmit : existing?.release_on_submit ?? "teacher";
  if (!["teacher", "auto"].includes(releaseOnSubmit)) throw new HttpError(400, "INVALID_REQUEST", "releaseOnSubmit must be teacher or auto");
  const isOpen = body.isOpen !== undefined ? !!body.isOpen : existing ? !!existing.is_open : true;
  const openToAll = body.openToAll !== undefined ? !!body.openToAll : existing ? !!existing.open_to_all : true;
  const openFrom = body.openFrom !== undefined ? parseIsoOrNull(body.openFrom, "openFrom") : existing?.open_from ?? null;
  const openUntil = body.openUntil !== undefined ? parseIsoOrNull(body.openUntil, "openUntil") : existing?.open_until ?? null;
  let accessHash = existing?.access_code_hash ?? null;
  if (body.accessCode !== undefined) {
    const ac = body.accessCode === null ? "" : str(body.accessCode, 64);
    accessHash = ac ? sha256hex(`access:${code}:${ac}`) : null;
  }
  const kind = body.kind !== undefined ? body.kind : existing?.kind ?? "questions";
  if (!EXAM_KINDS.includes(kind)) throw new HttpError(400, "INVALID_REQUEST", "kind must be questions or external");
  let startUrl = existing?.start_url ?? null;
  if (body.startUrl !== undefined) startUrl = body.startUrl === null || body.startUrl === "" ? null : validateStartUrl(body.startUrl, publicHost);
  if (kind === "external" && !startUrl) throw new HttpError(400, "INVALID_START_URL", "An external exam needs a start address (https://...)");
  const allowedSites = body.allowedSites !== undefined ? validateAllowedSites(body.allowedSites) : JSON.parse(existing?.allowed_sites_json || "[]");
  const policy = body.policy !== undefined ? validatePolicyPatch(body.policy) : existing ? JSON.parse(existing.policy_json) : {};
  const questions = body.questions !== undefined ? validateQuestions(body.questions) : null;
  const links = body.allowedLinks !== undefined ? (Array.isArray(body.allowedLinks) ? body.allowedLinks : (() => { throw new HttpError(400, "INVALID_LINK", "allowedLinks must be an array"); })()).slice(0, 50).map((l) => validateLink(l, publicHost)) : null;
  let assigned = null;
  if (body.assignedStudentCodes !== undefined) {
    if (!Array.isArray(body.assignedStudentCodes) || !body.assignedStudentCodes.every((c) => typeof c === "string")) throw new HttpError(400, "INVALID_REQUEST", "assignedStudentCodes must be an array of strings");
    assigned = [...new Set(body.assignedStudentCodes.map((c) => c.trim()).filter(Boolean))];
  }

  return transaction(ctx.db, () => {
    let id = existing?.id;
    if (existing) {
      ctx.db
        .prepare(
          `UPDATE exams SET title = ?, duration_minutes = ?, access_code_hash = ?, is_open = ?, open_from = ?, open_until = ?, open_to_all = ?, release_on_submit = ?, policy_json = ?,
             kind = ?, start_url = ?, allowed_sites_json = ?, updated_at = ? WHERE id = ?`
        )
        .run(title, duration, accessHash, isOpen ? 1 : 0, openFrom, openUntil, openToAll ? 1 : 0, releaseOnSubmit, JSON.stringify(policy), kind, startUrl, JSON.stringify(allowedSites), now, id);
    } else {
      id = newId("exm");
      ctx.db
        .prepare(
          `INSERT INTO exams (id, code, title, duration_minutes, access_code_hash, is_open, open_from, open_until, open_to_all, release_on_submit, policy_json,
             kind, start_url, allowed_sites_json, created_at, updated_at)
           VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`
        )
        .run(id, code, title, duration, accessHash, isOpen ? 1 : 0, openFrom, openUntil, openToAll ? 1 : 0, releaseOnSubmit, JSON.stringify(policy), kind, startUrl, JSON.stringify(allowedSites), now, now);
    }
    if (questions) {
      ctx.db.prepare("DELETE FROM exam_questions WHERE exam_id = ?").run(id);
      const ins = ctx.db.prepare("INSERT INTO exam_questions (exam_id, position, qid, text, options_json, answer, created_at) VALUES (?, ?, ?, ?, ?, ?, ?)");
      questions.forEach((q, i) => ins.run(id, i, q.id, q.text, JSON.stringify(q.options), q.answer, now));
    }
    if (links) {
      ctx.db.prepare("DELETE FROM exam_links WHERE exam_id = ?").run(id);
      const ins = ctx.db.prepare("INSERT INTO exam_links (exam_id, position, label, url, created_at) VALUES (?, ?, ?, ?, ?)");
      links.forEach((l, i) => ins.run(id, i, l.label, l.url, now));
    }
    if (assigned) {
      ctx.db.prepare("DELETE FROM exam_assignments WHERE exam_id = ?").run(id);
      const find = ctx.db.prepare("SELECT id FROM students WHERE code = ?");
      const ins = ctx.db.prepare("INSERT INTO exam_assignments (exam_id, student_id, created_at) VALUES (?, ?, ?)");
      for (const c of assigned) {
        const s = find.get(c);
        if (!s) throw new HttpError(400, "STUDENT_NOT_FOUND", `Unknown student code ${c}`);
        ins.run(id, s.id, now);
      }
    }
    return getById(ctx, id);
  });
}

export function remove(ctx, row) {
  const inUse = ctx.db.prepare("SELECT COUNT(*) AS n FROM sessions WHERE exam_id = ?").get(row.id).n;
  if (inUse) throw new HttpError(409, "EXAM_IN_USE", `Exam has ${inUse} session(s); close it instead of deleting`);
  ctx.db.prepare("DELETE FROM exams WHERE id = ?").run(row.id);
}

export function list(ctx) {
  return ctx.db.prepare("SELECT * FROM exams ORDER BY code").all().map((r) => publicExam(ctx, r));
}

export function checkAccessCode(row, provided) {
  if (!row.access_code_hash) return true;
  const ac = str(provided, 64);
  return !!ac && safeEqual(row.access_code_hash, sha256hex(`access:${row.code}:${ac}`));
}

export function isWindowOpen(row) {
  if (!row.is_open) return false;
  const now = Date.now();
  if (row.open_from && Date.parse(row.open_from) > now) return false;
  if (row.open_until && Date.parse(row.open_until) < now) return false;
  return true;
}

/** Scores a session's answers against the key. Questions without an answer key are skipped. */
export function score(ctx, examId, answers) {
  const qs = questionsFor(ctx, examId, { withAnswers: true });
  let correct = 0;
  let gradable = 0;
  for (const q of qs) {
    if (q.answer === null) continue;
    gradable++;
    if (answers[q.id] === q.answer) correct++;
  }
  return { correct, gradable, total: qs.length, answered: Object.keys(answers).length };
}
