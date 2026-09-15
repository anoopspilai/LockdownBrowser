// Staff console API (CONTRACT §10.8). Cookie + X-Requested-With on every request; RBAC per action; reasons audited.
import { HttpError, str, requireReason, nowIso } from "../util.js";
import { requireStaff, createStaffUser, hashPassword, ROLES } from "../auth/staff.js";
import { issueEnrollmentToken, publicEnrollmentToken, publicDevice } from "../auth/device.js";
import * as sessions from "../services/sessions.js";
import * as events from "../services/events.js";
import * as release from "../services/release.js";
import * as exams from "../services/exams.js";
import * as students from "../services/students.js";
import * as incidents from "../services/incidents.js";
import * as audit from "../services/audit.js";

const ANY = ["admin", "teacher", "reviewer"];
const WRITE = ["admin", "teacher"];
const ADMIN = ["admin"];

export function registerAdminRoutes(router, ctx) {
  const A = "/api/v1/admin";
  const staff = (req, roles) => requireStaff(ctx, req, ...roles);

  // --- live sessions ---------------------------------------------------------------------
  router.get(`${A}/sessions`, ({ req, query }) => {
    staff(req, ANY);
    return sessions.listForAdmin(ctx, { status: query.get("status"), limit: query.get("limit") });
  });
  router.get(`${A}/sessions/:id`, ({ req, params }) => {
    staff(req, ANY);
    return sessions.adminView(ctx, sessions.getOr404(ctx, params.id), { detail: true });
  });
  router.get(`${A}/sessions/:id/events`, ({ req, params, query }) => {
    staff(req, ANY);
    return events.list(ctx, sessions.getOr404(ctx, params.id).id, { limit: query.get("limit") });
  });
  router.post(`${A}/sessions/:id/release-code`, ({ req, params, body, ip }) => {
    const user = staff(req, WRITE);
    return release.issueReleaseCode(ctx, sessions.getOr404(ctx, params.id), { staff: user, reason: requireReason(body), ip });
  });
  router.post(`${A}/sessions/:id/release`, ({ req, params, body, ip }) => {
    const user = staff(req, WRITE);
    return release.remoteRelease(ctx, sessions.getOr404(ctx, params.id), { staff: user, body, ip });
  });
  router.post(`${A}/sessions/:id/terminate`, ({ req, params, body, ip }) => {
    const user = staff(req, WRITE);
    return release.remoteTerminate(ctx, sessions.getOr404(ctx, params.id), { staff: user, body, ip });
  });
  router.post(`${A}/sessions/:id/warn`, ({ req, params, body, ip }) => {
    const user = staff(req, WRITE);
    return release.remoteWarn(ctx, sessions.getOr404(ctx, params.id), { staff: user, body, ip });
  });

  // --- default policy (v1 path kept; exams override per field) ------------------------------
  router.get(`${A}/policy`, ({ req }) => {
    staff(req, ANY);
    return exams.defaultPolicy(ctx);
  });
  const putPolicy = ({ req, body, ip }) => {
    const user = staff(req, ADMIN);
    const out = exams.updateDefaultPolicy(ctx, body);
    audit.staffAction(ctx, user, "policy.update", { targetType: "policy", targetId: "default", ip, detail: body });
    return out;
  };
  router.put(`${A}/policy`, putPolicy);
  router.patch(`${A}/policy`, putPolicy);
  router.delete(`${A}/policy`, ({ req, ip }) => {
    const user = staff(req, ADMIN);
    audit.staffAction(ctx, user, "policy.reset", { targetType: "policy", targetId: "default", ip });
    return exams.resetDefaultPolicy(ctx);
  });

  // --- exams -------------------------------------------------------------------------------
  const examOr404 = (code) => {
    const e = exams.getByCode(ctx, code);
    if (!e) throw new HttpError(404, "EXAM_NOT_FOUND", `Unknown exam ${str(code, 64)}`);
    return e;
  };
  router.get(`${A}/exams`, ({ req }) => {
    staff(req, ANY);
    return exams.list(ctx);
  });
  router.post(`${A}/exams`, ({ req, body, ip }) => {
    const user = staff(req, WRITE);
    const row = exams.upsert(ctx, body, { publicHost: sessions.publicHostFor(ctx, req) });
    audit.staffAction(ctx, user, "exam.create", { targetType: "exam", targetId: row.code, ip, detail: { title: row.title } });
    return exams.publicExam(ctx, row, { full: true });
  });
  router.get(`${A}/exams/:code`, ({ req, params }) => {
    staff(req, ANY);
    return exams.publicExam(ctx, examOr404(params.code), { full: true });
  });
  router.put(`${A}/exams/:code`, ({ req, params, body, ip }) => {
    const user = staff(req, WRITE);
    const row = exams.upsert(ctx, body, { existing: examOr404(params.code), publicHost: sessions.publicHostFor(ctx, req) });
    audit.staffAction(ctx, user, "exam.update", { targetType: "exam", targetId: row.code, ip, detail: { fields: Object.keys(body) } });
    return exams.publicExam(ctx, row, { full: true });
  });
  router.delete(`${A}/exams/:code`, ({ req, params, body, ip }) => {
    const user = staff(req, WRITE);
    const row = examOr404(params.code);
    const reason = requireReason(body);
    exams.remove(ctx, row);
    audit.staffAction(ctx, user, "exam.delete", { targetType: "exam", targetId: row.code, reason, ip });
    return { ok: true };
  });
  for (const action of ["open", "close"]) {
    router.post(`${A}/exams/:code/${action}`, ({ req, params, body, ip }) => {
      const user = staff(req, WRITE);
      const row = exams.upsert(ctx, { isOpen: action === "open" }, { existing: examOr404(params.code) });
      audit.staffAction(ctx, user, `exam.${action}`, { targetType: "exam", targetId: row.code, reason: str(body.reason, 500) || null, ip });
      return exams.publicExam(ctx, row);
    });
  }

  // --- students ----------------------------------------------------------------------------
  const studentOr404 = (code) => {
    const s = students.getByCode(ctx, code);
    if (!s) throw new HttpError(404, "STUDENT_NOT_FOUND", "Unknown student");
    return s;
  };
  router.get(`${A}/students`, ({ req }) => {
    staff(req, ANY);
    return students.list(ctx);
  });
  router.post(`${A}/students`, ({ req, body, ip }) => {
    const user = staff(req, WRITE);
    const s = students.create(ctx, body);
    audit.staffAction(ctx, user, "student.create", { targetType: "student", targetId: s.code, ip });
    return s;
  });
  router.post(`${A}/students/import`, ({ req, body, ip }) => {
    const user = staff(req, WRITE);
    const result = students.importCsv(ctx, body.csv);
    audit.staffAction(ctx, user, "student.import", { targetType: "student", targetId: "*", ip, detail: { created: result.created, updated: result.updated, skipped: result.skipped.length } });
    return result;
  });
  router.put(`${A}/students/:code`, ({ req, params, body, ip }) => {
    const user = staff(req, WRITE);
    const s = students.update(ctx, studentOr404(params.code), body);
    audit.staffAction(ctx, user, "student.update", { targetType: "student", targetId: s.code, ip });
    return s;
  });
  router.delete(`${A}/students/:code`, ({ req, params, body, ip }) => {
    const user = staff(req, WRITE);
    const s = studentOr404(params.code);
    const reason = requireReason(body);
    students.remove(ctx, s);
    audit.staffAction(ctx, user, "student.delete", { targetType: "student", targetId: s.code, reason, ip });
    return { ok: true };
  });

  // --- enrollment tokens -------------------------------------------------------------------
  router.get(`${A}/enrollment-tokens`, ({ req }) => {
    staff(req, ANY);
    return ctx.db.prepare("SELECT * FROM enrollment_tokens ORDER BY created_at DESC LIMIT 500").all().map(publicEnrollmentToken);
  });
  router.post(`${A}/enrollment-tokens`, ({ req, body, ip }) => {
    const user = staff(req, ADMIN);
    const out = issueEnrollmentToken(ctx, { mode: body.mode, label: body.label, createdBy: user.username, hours: body.expiresInHours });
    audit.staffAction(ctx, user, "enrollment_token.create", { targetType: "enrollment_token", targetId: out.id, ip, detail: { mode: out.mode, label: out.label } });
    return out; // token shown once
  });
  router.delete(`${A}/enrollment-tokens/:id`, ({ req, params, body, ip }) => {
    const user = staff(req, ADMIN);
    const reason = requireReason(body);
    const r = ctx.db.prepare("UPDATE enrollment_tokens SET revoked_at = ?, revoked_by = ? WHERE id = ? AND revoked_at IS NULL").run(nowIso(), user.username, str(params.id, 64));
    if (!r.changes) throw new HttpError(404, "NOT_FOUND", "Token not found or already revoked");
    audit.staffAction(ctx, user, "enrollment_token.revoke", { targetType: "enrollment_token", targetId: params.id, reason, ip });
    return { ok: true };
  });

  // --- devices -----------------------------------------------------------------------------
  router.get(`${A}/devices`, ({ req }) => {
    staff(req, ANY);
    return ctx.db.prepare("SELECT * FROM devices ORDER BY created_at DESC LIMIT 1000").all().map(publicDevice);
  });
  router.put(`${A}/devices/:id`, ({ req, params, body, ip }) => {
    const user = staff(req, ADMIN);
    const dev = ctx.db.prepare("SELECT * FROM devices WHERE id = ?").get(str(params.id, 64));
    if (!dev) throw new HttpError(404, "DEVICE_NOT_FOUND", "Unknown device");
    const reason = requireReason(body);
    const kiosk = body.kioskVerified !== undefined ? !!body.kioskVerified : !!dev.kiosk_verified;
    const name = body.deviceName !== undefined ? str(body.deviceName, 120) || null : dev.device_name;
    ctx.db.prepare("UPDATE devices SET kiosk_verified = ?, device_name = ?, updated_at = ? WHERE id = ?").run(kiosk ? 1 : 0, name, nowIso(), dev.id);
    audit.staffAction(ctx, user, "device.update", { targetType: "device", targetId: dev.id, reason, ip, detail: { kioskVerified: kiosk, deviceName: name } });
    return publicDevice(ctx.db.prepare("SELECT * FROM devices WHERE id = ?").get(dev.id));
  });
  router.delete(`${A}/devices/:id`, ({ req, params, body, ip }) => {
    const user = staff(req, ADMIN);
    const reason = requireReason(body);
    const r = ctx.db.prepare("UPDATE devices SET revoked_at = ?, revoked_by = ?, updated_at = ? WHERE id = ? AND revoked_at IS NULL").run(nowIso(), user.username, nowIso(), str(params.id, 64));
    if (!r.changes) throw new HttpError(404, "DEVICE_NOT_FOUND", "Device not found or already revoked");
    audit.staffAction(ctx, user, "device.revoke", { targetType: "device", targetId: params.id, reason, ip });
    return { ok: true };
  });

  // --- incidents / audit -------------------------------------------------------------------
  router.get(`${A}/incidents`, ({ req, query }) => {
    staff(req, ANY);
    const open = query.get("open");
    return incidents.list(ctx, { open: open === "1" ? true : open === "0" ? false : null, limit: query.get("limit") });
  });
  router.post(`${A}/incidents/:id/resolve`, ({ req, params, body, ip }) => {
    const user = staff(req, WRITE);
    return incidents.resolve(ctx, params.id, { staff: user, note: body.note, ip });
  });
  router.get(`${A}/audit`, ({ req, query }) => {
    staff(req, ANY);
    return audit.list(ctx, { limit: query.get("limit"), before: query.get("before") });
  });

  // --- staff users (admin only) ------------------------------------------------------------
  router.get(`${A}/staff`, ({ req }) => {
    staff(req, ADMIN);
    return ctx.db.prepare("SELECT id, username, role, disabled, created_at FROM staff_users ORDER BY username").all().map((u) => ({ id: u.id, username: u.username, role: u.role, disabled: !!u.disabled, createdAt: u.created_at }));
  });
  router.post(`${A}/staff`, async ({ req, body, ip }) => {
    const user = staff(req, ADMIN);
    const created = await createStaffUser(ctx.db, { username: body.username, password: body.password, role: body.role });
    audit.staffAction(ctx, user, "staff.create", { targetType: "staff", targetId: created.id, ip, detail: { username: created.username, role: created.role } });
    return created;
  });
  router.put(`${A}/staff/:id`, async ({ req, params, body, ip }) => {
    const user = staff(req, ADMIN);
    const target = ctx.db.prepare("SELECT * FROM staff_users WHERE id = ?").get(str(params.id, 64));
    if (!target) throw new HttpError(404, "NOT_FOUND", "Unknown staff user");
    const role = body.role !== undefined ? body.role : target.role;
    if (!ROLES.includes(role)) throw new HttpError(400, "INVALID_REQUEST", `role must be one of ${ROLES.join(", ")}`);
    const disabled = body.disabled !== undefined ? !!body.disabled : !!target.disabled;
    if (target.id === user.id && (disabled || role !== "admin")) throw new HttpError(400, "INVALID_REQUEST", "You cannot disable or demote your own account");
    const hash = body.password !== undefined && body.password !== "" ? await hashPassword(body.password) : target.password_hash;
    ctx.db.prepare("UPDATE staff_users SET role = ?, disabled = ?, password_hash = ?, updated_at = ? WHERE id = ?").run(role, disabled ? 1 : 0, hash, nowIso(), target.id);
    if (disabled || hash !== target.password_hash) ctx.db.prepare("DELETE FROM staff_sessions WHERE user_id = ?").run(target.id);
    audit.staffAction(ctx, user, "staff.update", { targetType: "staff", targetId: target.id, ip, detail: { role, disabled, passwordChanged: hash !== target.password_hash } });
    return { id: target.id, username: target.username, role, disabled };
  });
  router.delete(`${A}/staff/:id`, ({ req, params, body, ip }) => {
    const user = staff(req, ADMIN);
    const reason = requireReason(body);
    if (params.id === user.id) throw new HttpError(400, "INVALID_REQUEST", "You cannot delete your own account");
    const r = ctx.db.prepare("DELETE FROM staff_users WHERE id = ?").run(str(params.id, 64));
    if (!r.changes) throw new HttpError(404, "NOT_FOUND", "Unknown staff user");
    audit.staffAction(ctx, user, "staff.delete", { targetType: "staff", targetId: params.id, reason, ip });
    return { ok: true };
  });
}
