// Heartbeat (CONTRACT §10.4 / §10.6): record client-reported state, expire, queue auto RELEASE, pop ONE command.
import { nowIso, clampInt } from "../util.js";
import { transaction } from "../db.js";
import * as events from "./events.js";
import * as incidents from "./incidents.js";
import * as release from "./release.js";
import { remainingSeconds, policyOf, jitteredInterval, reload } from "./sessions.js";

export const LOCKDOWN_MODES = ["aac", "kiosk-fallback", "assigned-access", "none"];
export const HEARTBEAT_STATUSES = ["locked", "unlocked", "warning"];
const COMPLIANT_MODES = ["aac", "assigned-access"];

export function beat(ctx, session, body) {
  const { db } = ctx;
  const policy = policyOf(session);
  const now = nowIso();
  const clientStatus = HEARTBEAT_STATUSES.includes(body.status) ? body.status : session.client_status;
  const lockdownMode = LOCKDOWN_MODES.includes(body.lockdownMode) ? body.lockdownMode : session.lockdown_mode;
  const displayCount = clampInt(body.displayCount, 0, 64, session.display_count);
  const uptime = clampInt(body.uptimeSeconds, 0, 10_000_000, session.uptime_seconds);

  transaction(db, () => {
    db.prepare(
      `UPDATE sessions SET last_heartbeat = ?, client_status = ?, lockdown_mode = ?, display_count = ?, uptime_seconds = ?, stale = 0, updated_at = ? WHERE id = ?`
    ).run(now, clientStatus, lockdownMode, displayCount, uptime, now, session.id);
    if (session.stale) events.append(ctx, session, { type: "SESSION_RESUMED", severity: "info", source: "server", metadata: { staleSince: session.stale_since } });

    // Client-reported lockdown mode is stored verbatim; a mismatch with a requireAAC policy is flagged, not trusted.
    if (policy.requireAAC && lockdownMode && !COMPLIANT_MODES.includes(lockdownMode) && !session.policy_mismatch_reported) {
      db.prepare("UPDATE sessions SET policy_mismatch_reported = 1 WHERE id = ?").run(session.id);
      events.append(ctx, session, { type: "POLICY_MISMATCH", severity: "high", source: "server", metadata: { field: "lockdownMode", clientReported: lockdownMode, required: COMPLIANT_MODES } });
      incidents.raise(ctx, { sessionId: session.id, studentId: session.student_id, examId: session.exam_id, type: "POLICY_MISMATCH", severity: "high", detail: `Policy requires AAC/kiosk but client reports lockdownMode=${lockdownMode}` });
    }
    if (typeof body.lockdownMode === "string" && LOCKDOWN_MODES.includes(body.lockdownMode) && session.lockdown_mode && body.lockdownMode !== session.lockdown_mode) {
      events.append(ctx, session, { type: "LOCKDOWN_MODE_CHANGED", severity: body.lockdownMode === "none" ? "high" : "medium", source: "server", metadata: { from: session.lockdown_mode, to: body.lockdownMode } });
    }
  });

  let fresh = reload(ctx, session.id);
  const remaining = remainingSeconds(fresh);

  // Time over: mark expired and RELEASE once (signed at delivery).
  if (fresh.status === "active" && remaining === 0) {
    db.prepare("UPDATE sessions SET status = 'expired', updated_at = ? WHERE id = ? AND status = 'active'").run(now, fresh.id);
    fresh = reload(ctx, session.id);
    events.append(ctx, fresh, { type: "SESSION_EXPIRED", severity: "info", source: "server", metadata: {} });
    release.queueCommand(ctx, fresh, { type: "RELEASE", reason: "Time expired" });
  } else if (fresh.status === "expired") {
    release.queueCommand(ctx, fresh, { type: "RELEASE", reason: "Time expired" });
  }
  // Submitted with auto release.
  if (fresh.status === "submitted" && policy.releaseOnSubmit === "auto") {
    release.queueCommand(ctx, fresh, { type: "RELEASE", reason: "Exam submitted" });
  }

  const command = release.popCommand(ctx, fresh);
  return {
    ok: true,
    serverTime: nowIso(),
    remainingSeconds: remaining,
    nextBeatInSeconds: jitteredInterval(policy.heartbeatIntervalSeconds),
    status: reload(ctx, session.id).status,
    command
  };
}
