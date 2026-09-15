// SQLite persistence via the built-in node:sqlite module. WAL mode, foreign keys on, versioned migrations.
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { DatabaseSync } from "node:sqlite";
import { nowIso } from "./util.js";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const SCHEMA_V1 = fs.readFileSync(path.join(__dirname, "schema.sql"), "utf8");

/** Ordered migrations. Add a new entry to change the schema; never edit an applied one. */
const MIGRATIONS = [
  { version: 1, sql: SCHEMA_V1 },
  // v2 (CONTRACT §11): external-website exams.
  {
    version: 2,
    sql: `ALTER TABLE exams ADD COLUMN kind TEXT NOT NULL DEFAULT 'questions' CHECK (kind IN ('questions','external'));
          ALTER TABLE exams ADD COLUMN start_url TEXT;
          ALTER TABLE exams ADD COLUMN allowed_sites_json TEXT NOT NULL DEFAULT '[]';`
  }
];

export function openDb(dbPath) {
  if (dbPath !== ":memory:") fs.mkdirSync(path.dirname(dbPath), { recursive: true });
  const db = new DatabaseSync(dbPath);
  db.exec("PRAGMA journal_mode = WAL");
  db.exec("PRAGMA foreign_keys = ON");
  db.exec("PRAGMA busy_timeout = 5000");
  db.exec("PRAGMA synchronous = NORMAL");
  db.exec("CREATE TABLE IF NOT EXISTS schema_migrations (version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL)");
  const applied = new Set(db.prepare("SELECT version FROM schema_migrations").all().map((r) => r.version));
  for (const m of MIGRATIONS) {
    if (applied.has(m.version)) continue;
    db.exec("BEGIN");
    try {
      db.exec(m.sql);
      db.prepare("INSERT INTO schema_migrations (version, applied_at) VALUES (?, ?)").run(m.version, nowIso());
      db.exec("COMMIT");
    } catch (e) {
      db.exec("ROLLBACK");
      throw e;
    }
  }
  return db;
}

/** Run fn inside a transaction (nested calls are flattened). */
export function transaction(db, fn) {
  if (db.__inTx) return fn();
  db.__inTx = true;
  db.exec("BEGIN IMMEDIATE");
  try {
    const out = fn();
    db.exec("COMMIT");
    return out;
  } catch (e) {
    try {
      db.exec("ROLLBACK");
    } catch {
      /* ignore */
    }
    throw e;
  } finally {
    db.__inTx = false;
  }
}

export function getSetting(db, key, fallback) {
  const row = db.prepare("SELECT value_json FROM settings WHERE key = ?").get(key);
  if (!row) return fallback;
  try {
    return JSON.parse(row.value_json);
  } catch {
    return fallback;
  }
}

export function setSetting(db, key, value) {
  const now = nowIso();
  db.prepare(
    `INSERT INTO settings (key, value_json, created_at, updated_at) VALUES (?, ?, ?, ?)
     ON CONFLICT(key) DO UPDATE SET value_json = excluded.value_json, updated_at = excluded.updated_at`
  ).run(key, JSON.stringify(value), now, now);
}

/** Fixed-window rate limit backed by the rate_limits table. Returns true when the call is allowed. */
export function rateLimit(db, key, limit, windowMs) {
  const now = Date.now();
  const windowStart = now - (now % windowMs);
  const row = db.prepare("SELECT window_start, count FROM rate_limits WHERE key = ?").get(key);
  if (!row || row.window_start !== windowStart) {
    db.prepare(
      `INSERT INTO rate_limits (key, window_start, count, created_at) VALUES (?, ?, 1, ?)
       ON CONFLICT(key) DO UPDATE SET window_start = excluded.window_start, count = 1`
    ).run(key, windowStart, nowIso());
    return true;
  }
  if (row.count >= limit) return false;
  db.prepare("UPDATE rate_limits SET count = count + 1 WHERE key = ?").run(key);
  return true;
}
