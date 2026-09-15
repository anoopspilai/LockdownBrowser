// Session telemetry. Insert-only table, per-session counters, rate limit, metadata and storage caps (CONTRACT §10.7).
import { HttpError, nowIso, isObject } from "../util.js";
import { rateLimit } from "../db.js";
import * as incidents from "./incidents.js";

export const SEVERITIES = ["low", "medium", "high", "info"];

function riskFor(medium, high) {
  if (high > 0 || medium >= 3) return "RED";
  if (medium > 0) return "AMBER";
  return "GREEN";
}

/**
 * Appends one event. Server-generated events bypass the per-session rate limit but respect the storage cap.
 * Returns true if the event was stored, false if it was counted but dropped.
 */
export function append(ctx, session, { type, severity = "info", timestamp = null, metadata = {}, source = "client" }) {
  const { db, config } = ctx;
  const sev = SEVERITIES.includes(severity) ? severity : "info";
  let metaJson = "{}";
  if (isObject(metadata)) {
    metaJson = JSON.stringify(metadata);
    if (Buffer.byteLength(metaJson, "utf8") > config.maxEventMetadataBytes) {
      metaJson = JSON.stringify({ _truncated: true, _bytes: Buffer.byteLength(metaJson, "utf8") });
    }
  }
  const ts = typeof timestamp === "string" && Number.isFinite(Date.parse(timestamp)) ? new Date(Date.parse(timestamp)).toISOString() : nowIso();
  const now = nowIso();
  const current = db.prepare("SELECT stored_event_count, medium_count, high_count FROM sessions WHERE id = ?").get(session.id);
  if (!current) return false;
  const store = current.stored_event_count < config.maxStoredEventsPerSession;
  if (store) {
    db.prepare(
      `INSERT INTO events (session_id, type, severity, timestamp, received_at, source, metadata_json, created_at) VALUES (?, ?, ?, ?, ?, ?, ?, ?)`
    ).run(session.id, String(type).slice(0, 64), sev, ts, now, source, metaJson, now);
  }
  const medium = current.medium_count + (sev === "medium" ? 1 : 0);
  const high = current.high_count + (sev === "high" ? 1 : 0);
  db.prepare(
    `UPDATE sessions SET event_count = event_count + 1, stored_event_count = stored_event_count + ?, medium_count = ?, high_count = ?, updated_at = ? WHERE id = ?`
  ).run(store ? 1 : 0, medium, high, now, session.id);
  if (!store && current.stored_event_count === config.maxStoredEventsPerSession) {
    // Raised once, when the cap is first exceeded.
    const already = db.prepare("SELECT 1 FROM incidents WHERE session_id = ? AND type = 'EVENT_CAP_REACHED'").get(session.id);
    if (!already) incidents.raise(ctx, { sessionId: session.id, studentId: session.student_id, examId: session.exam_id, type: "EVENT_CAP_REACHED", severity: "low", detail: `More than ${config.maxStoredEventsPerSession} events; newer events are counted but not stored.` });
  }
  return store;
}

export function riskLevel(sessionRow) {
  return riskFor(sessionRow.medium_count, sessionRow.high_count);
}

/** Handles POST /sessions/:id/events. */
export function ingest(ctx, session, body) {
  const list = Array.isArray(body.events) ? body.events : null;
  if (!list) throw new HttpError(400, "INVALID_REQUEST", "Body must be { events: [...] }");
  if (list.length > 500) throw new HttpError(400, "INVALID_REQUEST", "At most 500 events per batch");
  let accepted = 0;
  let dropped = 0;
  for (const e of list) {
    if (!isObject(e) || typeof e.type !== "string" || !/^[A-Z][A-Z0-9_]{0,63}$/.test(e.type)) {
      dropped++;
      continue;
    }
    if (!rateLimit(ctx.db, `events:${session.id}`, ctx.config.eventsPerSecondPerSession, 1000)) {
      dropped++;
      continue;
    }
    // Oversized metadata (> 4 KiB) is replaced by a truncation marker inside append().
    append(ctx, session, { type: e.type, severity: e.severity, timestamp: e.timestamp, metadata: isObject(e.metadata) ? e.metadata : {}, source: "client" });
    accepted++;
  }
  return { accepted, dropped };
}

export function list(ctx, sessionId, { limit = 1000 } = {}) {
  const lim = Math.min(Math.max(Number(limit) || 1000, 1), 5000);
  return ctx.db
    .prepare("SELECT * FROM events WHERE session_id = ? ORDER BY id DESC LIMIT ?")
    .all(sessionId, lim)
    .reverse()
    .map((r) => ({
      id: r.id,
      type: r.type,
      severity: r.severity,
      timestamp: r.timestamp,
      receivedAt: r.received_at,
      source: r.source,
      metadata: JSON.parse(r.metadata_json)
    }));
}
