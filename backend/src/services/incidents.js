import { HttpError, nowIso, str } from "../util.js";

export function raise(ctx, { sessionId = null, studentId = null, examId = null, type, severity = "medium", detail = null }) {
  const r = ctx.db
    .prepare(`INSERT INTO incidents (session_id, student_id, exam_id, type, severity, detail, created_at) VALUES (?, ?, ?, ?, ?, ?, ?)`)
    .run(sessionId, studentId, examId, type, severity, detail, nowIso());
  ctx.log.warn("incident", { type, severity, sessionId, studentId, examId });
  return Number(r.lastInsertRowid);
}

export function list(ctx, { open = null, limit = 200 } = {}) {
  const lim = Math.min(Math.max(Number(limit) || 200, 1), 1000);
  const where = open === true ? "WHERE i.resolved_at IS NULL" : open === false ? "WHERE i.resolved_at IS NOT NULL" : "";
  return ctx.db
    .prepare(
      `SELECT i.*, s.code AS student_code, s.name AS student_name, e.code AS exam_code
       FROM incidents i LEFT JOIN students s ON s.id = i.student_id LEFT JOIN exams e ON e.id = i.exam_id
       ${where} ORDER BY i.id DESC LIMIT ?`
    )
    .all(lim)
    .map(publicIncident);
}

export function resolve(ctx, id, { staff, note, ip }) {
  const n = str(note, 1000);
  if (!n) throw new HttpError(400, "INVALID_REQUEST", "note is required");
  const row = ctx.db.prepare("SELECT * FROM incidents WHERE id = ?").get(Number(id));
  if (!row) throw new HttpError(404, "INCIDENT_NOT_FOUND", "No such incident");
  if (row.resolved_at) throw new HttpError(409, "INCIDENT_RESOLVED", "Incident already resolved");
  ctx.db.prepare("UPDATE incidents SET resolved_at = ?, resolved_by = ?, resolution_note = ? WHERE id = ?").run(nowIso(), staff.username, n, row.id);
  ctx.audit.staffAction(ctx, staff, "incident.resolve", { targetType: "incident", targetId: String(row.id), reason: n, ip });
  return publicIncident(ctx.db.prepare("SELECT * FROM incidents WHERE id = ?").get(row.id));
}

export function publicIncident(r) {
  return {
    id: r.id,
    sessionId: r.session_id,
    studentId: r.student_id,
    studentCode: r.student_code ?? null,
    studentName: r.student_name ?? null,
    examId: r.exam_id,
    examCode: r.exam_code ?? null,
    type: r.type,
    severity: r.severity,
    detail: r.detail,
    resolvedAt: r.resolved_at,
    resolvedBy: r.resolved_by,
    resolutionNote: r.resolution_note,
    createdAt: r.created_at
  };
}
