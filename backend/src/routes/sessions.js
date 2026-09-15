// Device-facing session routes. All except POST /sessions need device + session auth.
import { HttpError } from "../util.js";
import { requireDevice } from "../auth/device.js";
import { requireSession } from "../auth/session.js";
import * as sessions from "../services/sessions.js";
import * as heartbeat from "../services/heartbeat.js";
import * as events from "../services/events.js";
import * as release from "../services/release.js";
import * as submit from "../services/submit.js";
import * as exams from "../services/exams.js";

export function registerSessionRoutes(router, ctx) {
  router.post("/api/v1/sessions", ({ req, body, ip }) => {
    const device = requireDevice(ctx, req);
    return sessions.start(ctx, { body, device, req, ip, clientVersionHeader: req.headers["x-client-version"] });
  });

  router.post("/api/v1/sessions/:id/heartbeat", ({ req, params, body }) => {
    const { session } = requireSession(ctx, req, params.id);
    return heartbeat.beat(ctx, session, body);
  });

  router.post(
    "/api/v1/sessions/:id/events",
    ({ req, params, body }) => {
      const { session } = requireSession(ctx, req, params.id);
      return events.ingest(ctx, session, body);
    },
    { maxBody: ctx.config.maxEventsBodyBytes }
  );

  router.post("/api/v1/sessions/:id/submit", ({ req, params, body }) => {
    const { session } = requireSession(ctx, req, params.id);
    return submit.submit(ctx, session, body, { source: "client" });
  });

  router.post("/api/v1/sessions/:id/unlock", ({ req, params, body }) => {
    const { session, device } = requireSession(ctx, req, params.id);
    return release.unlock(ctx, session, device, body);
  });

  router.put("/api/v1/sessions/:id/answers", ({ req, params, body }) => {
    const { session } = requireSession(ctx, req, params.id);
    return submit.saveAnswers(ctx, session, body.answers, { source: "client" });
  });

  router.get("/api/v1/sessions/:id/questions", ({ req, params }) => {
    const { session } = requireSession(ctx, req, params.id);
    return questionsPayload(ctx, session);
  });

  router.get("/api/v1/sessions/:id/status", ({ req, params }) => {
    const { session } = requireSession(ctx, req, params.id);
    return statusPayload(ctx, session);
  });

  // v1 path kept for compatibility, but scoped: the session may only read its own exam's questions (M1).
  router.get("/api/v1/exams/:code/questions", ({ req, params }) => {
    // Any session id works for auth purposes here because the token identifies the session; we then check the exam.
    const token = req.headers.authorization;
    if (!token) throw new HttpError(401, "INVALID_SESSION_TOKEN", "Missing session token");
    const device = requireDevice(ctx, req);
    const row = ctx.db
      .prepare("SELECT s.* FROM sessions s WHERE s.device_id = ? AND s.status IN ('active','submitted','expired') ORDER BY s.started_at DESC LIMIT 1")
      .get(device.id);
    if (!row) throw new HttpError(401, "INVALID_SESSION_TOKEN", "No session for this device");
    const { session } = requireSession(ctx, req, row.id);
    const exam = exams.getById(ctx, session.exam_id);
    if (!exam || exam.code !== String(params.code).toUpperCase()) throw new HttpError(403, "FORBIDDEN", "Questions are only available for the exam of your own session");
    return questionsPayload(ctx, session);
  });
}

export function questionsPayload(ctx, session) {
  const exam = exams.getById(ctx, session.exam_id);
  return {
    code: exam.code,
    title: exam.title,
    durationMinutes: exam.duration_minutes,
    questions: exams.publicQuestions(ctx, exam.id),
    answers: sessions.answersOf(ctx, session.id),
    serverTime: new Date().toISOString()
  };
}

export function statusPayload(ctx, session) {
  const exam = exams.getById(ctx, session.exam_id);
  const student = ctx.db.prepare("SELECT code, name FROM students WHERE id = ?").get(session.student_id);
  const policy = sessions.policyOf(session);
  return {
    sessionId: session.id,
    status: session.status,
    studentName: student?.name ?? "",
    studentCode: student?.code ?? "",
    examCode: exam?.code ?? "",
    examTitle: exam?.title ?? "",
    durationMinutes: exam?.duration_minutes ?? null,
    remainingSeconds: sessions.remainingSeconds(session),
    serverTime: new Date().toISOString(),
    expiresAt: session.expires_at,
    submittedAt: session.submitted_at,
    releaseOnSubmit: policy.releaseOnSubmit,
    allowedLinks: policy.allowedLinks || [],
    clientReported: { lockdownMode: session.lockdown_mode, displayCount: session.display_count, clientStatus: session.client_status },
    online: true
  };
}
