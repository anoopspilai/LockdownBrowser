// Submit (idempotent) and answer autosave (CONTRACT §10.5). The server clock is authoritative.
import { HttpError, nowIso, isObject } from "../util.js";
import { transaction } from "../db.js";
import * as events from "./events.js";
import * as exams from "./exams.js";
import * as release from "./release.js";
import { policyOf, reload } from "./sessions.js";

const WRITABLE = new Set(["active", "expired"]);

/** Upserts answers for known question ids. Ignores unknown ids. Returns count saved. */
export function saveAnswers(ctx, session, answers, { source = "client" } = {}) {
  if (!isObject(answers)) throw new HttpError(400, "INVALID_REQUEST", "answers must be an object of { questionId: answer }");
  if (session.submitted_at || !WRITABLE.has(session.status)) throw new HttpError(409, "SESSION_NOT_WRITABLE", "Answers can no longer be changed for this session");
  const valid = new Map(exams.publicQuestions(ctx, session.exam_id).map((q) => [q.id, q]));
  const entries = Object.entries(answers).slice(0, 500);
  let saved = 0;
  transaction(ctx.db, () => {
    const up = ctx.db.prepare(
      `INSERT INTO session_answers (session_id, question_id, answer, created_at, updated_at) VALUES (?, ?, ?, ?, ?)
       ON CONFLICT(session_id, question_id) DO UPDATE SET answer = excluded.answer, updated_at = excluded.updated_at`
    );
    const now = nowIso();
    for (const [qid, ans] of entries) {
      const q = valid.get(qid);
      if (!q || typeof ans !== "string" || !q.options.includes(ans)) continue;
      up.run(session.id, qid, ans, now, now);
      saved++;
    }
  });
  if (saved) events.append(ctx, session, { type: "ANSWERS_SAVED", severity: "info", source: source === "page" ? "page" : "server", metadata: { count: saved } });
  return { ok: true, saved, serverTime: nowIso() };
}

/** Idempotent submit. Returns { ok, submittedAt, release }. */
export function submit(ctx, session, body = {}, { source = "client" } = {}) {
  const policy = policyOf(session);
  const mode = policy.releaseOnSubmit === "auto" ? "auto" : "teacher";
  if (session.status === "terminated") throw new HttpError(409, "SESSION_TERMINATED", "This session was terminated");
  if (isObject(body.answers) && !session.submitted_at && WRITABLE.has(session.status)) {
    try {
      saveAnswers(ctx, session, body.answers, { source });
    } catch {
      /* answers are best-effort on submit */
    }
  }
  let fresh = reload(ctx, session.id);
  if (!fresh.submitted_at) {
    const now = nowIso();
    transaction(ctx.db, () => {
      ctx.db
        .prepare("UPDATE sessions SET submitted_at = ?, status = CASE WHEN status IN ('active','expired') THEN 'submitted' ELSE status END, updated_at = ? WHERE id = ? AND submitted_at IS NULL")
        .run(now, now, fresh.id);
      events.append(ctx, fresh, { type: "EXAM_SUBMITTED", severity: "info", source: source === "page" ? "page" : "server", metadata: { release: mode } });
    });
    fresh = reload(ctx, session.id);
    if (mode === "auto" && fresh.status === "submitted") release.queueCommand(ctx, fresh, { type: "RELEASE", reason: "Exam submitted" });
  }
  return { ok: true, submittedAt: fresh.submitted_at, release: mode, status: reload(ctx, session.id).status };
}
