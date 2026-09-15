// Append-only audit log. Never pass secrets in detail (the logger and this module do not redact digits).
import { nowIso } from "../util.js";

export function record(ctx, { actorType, actorId = null, actorName = null, action, targetType = null, targetId = null, reason = null, detail = null, ip = null }) {
  ctx.db
    .prepare(
      `INSERT INTO audit_log (actor_type, actor_id, actor_name, action, target_type, target_id, reason, detail_json, ip, created_at)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`
    )
    .run(actorType, actorId, actorName, action, targetType, targetId, reason, detail ? JSON.stringify(detail) : null, ip, nowIso());
  ctx.log.info("audit", { action, actorType, actorId, targetType, targetId, reason });
}

export function list(ctx, { limit = 200, before = null } = {}) {
  const lim = Math.min(Math.max(Number(limit) || 200, 1), 1000);
  const rows = before
    ? ctx.db.prepare("SELECT * FROM audit_log WHERE id < ? ORDER BY id DESC LIMIT ?").all(Number(before), lim)
    : ctx.db.prepare("SELECT * FROM audit_log ORDER BY id DESC LIMIT ?").all(lim);
  return rows.map((r) => ({
    id: r.id,
    actorType: r.actor_type,
    actorId: r.actor_id,
    actorName: r.actor_name,
    action: r.action,
    targetType: r.target_type,
    targetId: r.target_id,
    reason: r.reason,
    detail: r.detail_json ? JSON.parse(r.detail_json) : null,
    ip: r.ip,
    createdAt: r.created_at
  }));
}

/** Convenience for staff-initiated actions. */
export function staffAction(ctx, staff, action, { targetType, targetId, reason, detail, ip }) {
  record(ctx, { actorType: "staff", actorId: staff.id, actorName: staff.username, action, targetType, targetId, reason, detail, ip });
}
