// Staff authentication: scrypt passwords, server-side cookie sessions, lockout, CSRF header, RBAC.
import crypto from "node:crypto";
import { promisify } from "node:util";
import { HttpError, nowIso, isoIn, sha256hex, safeEqual, newToken, newId, isPast } from "../util.js";
import { parseCookies } from "../http/router.js";

const scrypt = promisify(crypto.scrypt);
const SCRYPT = { N: 16384, r: 8, p: 1, keylen: 64 };
export const STAFF_COOKIE = "avaibe_staff";
export const CSRF_HEADER = "x-requested-with";
export const CSRF_VALUE = "AvaibeAdmin";
export const ROLES = ["admin", "teacher", "reviewer"];

export async function hashPassword(password) {
  if (typeof password !== "string" || password.length < 10 || password.length > 256) {
    throw new HttpError(400, "WEAK_PASSWORD", "Password must be 10-256 characters");
  }
  const salt = crypto.randomBytes(16);
  const key = await scrypt(password, salt, SCRYPT.keylen, { N: SCRYPT.N, r: SCRYPT.r, p: SCRYPT.p });
  return `scrypt$${SCRYPT.N}$${SCRYPT.r}$${SCRYPT.p}$${salt.toString("base64")}$${key.toString("base64")}`;
}

export async function verifyPassword(password, stored) {
  try {
    const [alg, N, r, p, saltB64, hashB64] = String(stored).split("$");
    if (alg !== "scrypt") return false;
    const expected = Buffer.from(hashB64, "base64");
    const key = await scrypt(String(password), Buffer.from(saltB64, "base64"), expected.length, { N: Number(N), r: Number(r), p: Number(p) });
    return key.length === expected.length && crypto.timingSafeEqual(key, expected);
  } catch {
    return false;
  }
}

export function requireCsrf(req) {
  const v = req.headers[CSRF_HEADER];
  if (v !== CSRF_VALUE) throw new HttpError(403, "CSRF_REQUIRED", `Missing ${CSRF_VALUE} request header`);
}

function lockoutActive(db, config, username, ip) {
  const since = isoIn(-config.loginLockoutMinutes * 60_000);
  const row = db
    .prepare(
      `SELECT COUNT(*) AS n FROM login_attempts
       WHERE username = ? AND ip = ? AND success = 0 AND created_at > ?
         AND id > COALESCE((SELECT MAX(id) FROM login_attempts WHERE username = ? AND ip = ? AND success = 1), 0)`
    )
    .get(username, ip, since, username, ip);
  return row.n >= config.loginMaxFailures;
}

export async function login(ctx, { username, password, ip }) {
  const { db, config } = ctx;
  const u = typeof username === "string" ? username.trim().slice(0, 64) : "";
  if (!u || typeof password !== "string" || !password) throw new HttpError(400, "INVALID_REQUEST", "username and password are required");
  if (lockoutActive(db, config, u, ip)) {
    throw new HttpError(429, "LOGIN_LOCKED", `Too many failed attempts. Try again in ${config.loginLockoutMinutes} minutes.`);
  }
  const user = db.prepare("SELECT * FROM staff_users WHERE username = ?").get(u);
  // Always run a scrypt verification so timing does not reveal whether the user exists.
  const ok = await verifyPassword(password, user?.password_hash || DUMMY_HASH);
  const success = !!(user && ok && !user.disabled);
  db.prepare("INSERT INTO login_attempts (username, ip, success, created_at) VALUES (?, ?, ?, ?)").run(u, ip, success ? 1 : 0, nowIso());
  if (!success) {
    ctx.audit?.record(ctx, { actorType: "staff", actorName: u, action: "auth.login_failed", ip });
    throw new HttpError(401, "INVALID_CREDENTIALS", "Invalid username or password");
  }
  const token = newToken();
  db.prepare("INSERT INTO staff_sessions (token_hash, user_id, ip, created_at, expires_at, last_seen_at) VALUES (?, ?, ?, ?, ?, ?)").run(
    sha256hex(token),
    user.id,
    ip,
    nowIso(),
    isoIn(config.staffSessionHours * 3_600_000),
    nowIso()
  );
  ctx.audit?.record(ctx, { actorType: "staff", actorId: user.id, actorName: user.username, action: "auth.login", ip });
  return { token, user: publicUser(user) };
}

const DUMMY_HASH = "scrypt$16384$8$1$AAAAAAAAAAAAAAAAAAAAAA==$" + Buffer.alloc(64).toString("base64");

export function logout(ctx, req) {
  const token = parseCookies(req.headers.cookie)[STAFF_COOKIE];
  if (token) ctx.db.prepare("DELETE FROM staff_sessions WHERE token_hash = ?").run(sha256hex(token));
}

export function staffCookie(config, token, { clear = false } = {}) {
  const parts = [`${STAFF_COOKIE}=${clear ? "" : token}`, "Path=/", "HttpOnly", "SameSite=Strict"];
  if (config.cookieSecure) parts.push("Secure");
  parts.push(clear ? "Max-Age=0" : `Max-Age=${config.staffSessionHours * 3600}`);
  return parts.join("; ");
}

export function publicUser(u) {
  return { id: u.id, username: u.username, role: u.role, disabled: !!u.disabled, createdAt: u.created_at };
}

/** Returns the staff user for the request cookie or throws 401. Also enforces the CSRF header. */
export function requireStaff(ctx, req, ...roles) {
  requireCsrf(req);
  const token = parseCookies(req.headers.cookie)[STAFF_COOKIE];
  if (!token || !/^[0-9a-f]{48}$/.test(token)) throw new HttpError(401, "UNAUTHENTICATED", "Sign in required");
  const hash = sha256hex(token);
  const row = ctx.db
    .prepare(
      `SELECT s.token_hash, s.expires_at, u.* FROM staff_sessions s JOIN staff_users u ON u.id = s.user_id WHERE s.token_hash = ?`
    )
    .get(hash);
  if (!row || !safeEqual(row.token_hash, hash) || isPast(row.expires_at) || row.disabled) {
    throw new HttpError(401, "UNAUTHENTICATED", "Session expired or invalid. Sign in again.");
  }
  ctx.db.prepare("UPDATE staff_sessions SET last_seen_at = ? WHERE token_hash = ?").run(nowIso(), hash);
  if (roles.length && !roles.includes(row.role)) throw new HttpError(403, "FORBIDDEN", `This action requires role: ${roles.join(" or ")}`);
  return publicUser(row);
}

export async function createStaffUser(db, { username, password, role }) {
  const u = typeof username === "string" ? username.trim() : "";
  if (!/^[A-Za-z0-9][A-Za-z0-9_.@-]{1,63}$/.test(u)) throw new HttpError(400, "INVALID_REQUEST", "username must be 2-64 chars (letters, digits, _ . @ -)");
  if (!ROLES.includes(role)) throw new HttpError(400, "INVALID_REQUEST", `role must be one of ${ROLES.join(", ")}`);
  if (db.prepare("SELECT 1 FROM staff_users WHERE username = ?").get(u)) throw new HttpError(409, "USERNAME_TAKEN", "That username already exists");
  const hash = await hashPassword(password);
  const id = newId("usr");
  const now = nowIso();
  db.prepare("INSERT INTO staff_users (id, username, password_hash, role, disabled, created_at, updated_at) VALUES (?, ?, ?, ?, 0, ?, ?)").run(id, u, hash, role, now, now);
  return publicUser(db.prepare("SELECT * FROM staff_users WHERE id = ?").get(id));
}

export function purgeExpiredStaffSessions(db) {
  db.prepare("DELETE FROM staff_sessions WHERE expires_at < ?").run(nowIso());
}
