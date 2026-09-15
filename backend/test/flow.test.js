// End-to-end: enroll -> session -> heartbeat -> events -> release code -> unlock -> submit -> concurrency.
import { test, before, after } from "node:test";
import assert from "node:assert/strict";
import crypto from "node:crypto";
import { startTestApp } from "./helpers.js";
import { canonicalCommandString } from "../src/crypto/signer.js";

let t;
before(async () => {
  t = await startTestApp();
  const r = await t.login();
  assert.equal(r.status, 200);
});
after(() => t.close());

function verifyAuth(pubB64, auth) {
  const pub = crypto.createPublicKey({ key: Buffer.from(pubB64, "base64"), type: "spki", format: "der" });
  return crypto.verify("sha256", Buffer.from(canonicalCommandString(auth), "utf8"), { key: pub, dsaEncoding: "ieee-p1363" }, Buffer.from(auth.signature, "base64"));
}

test("enrollment: admin-issued single-use tokens; prefix never grants a mode; response carries the P-256 key", async () => {
  const bad = await t.req("POST", "/api/v1/devices/enroll", { body: { enrollmentToken: "SCHOOL-DEMO" }, cookies: false });
  assert.equal(bad.status, 403);
  assert.equal(bad.json.error.code, "INVALID_ENROLLMENT_TOKEN");
  const tok = t.enrollmentToken("byod");
  const ok = await t.req("POST", "/api/v1/devices/enroll", { body: { enrollmentToken: tok, platform: "windows", clientVersion: "0.1.0" }, cookies: false });
  assert.equal(ok.status, 200);
  assert.match(ok.json.deviceId, /^dev_[0-9a-f]{16}$/);
  assert.match(ok.json.deviceToken, /^[0-9a-f]{48}$/);
  assert.equal(ok.json.mode, "byod");
  assert.equal(ok.json.keyId, "k1");
  assert.equal(ok.json.serverPublicKey, t.ctx.signer.publicSpkiB64);
  const reuse = await t.req("POST", "/api/v1/devices/enroll", { body: { enrollmentToken: tok }, cookies: false });
  assert.equal(reuse.status, 403);
  assert.equal(reuse.json.error.code, "ENROLLMENT_TOKEN_USED");
});

test("full session flow with signed release via unlock and via remote release", async () => {
  const device = await t.enroll("school");

  // Session start requires device headers.
  const noAuth = await t.req("POST", "/api/v1/sessions", { body: { studentCode: "1025", examCode: "DEMO" }, cookies: false });
  assert.equal(noAuth.status, 401);

  const s = await t.startSession(device);
  assert.equal(s.status, 200, s.text);
  const session = s.json;
  assert.match(session.sessionId, /^sess_[0-9a-f]{32}$/);
  assert.match(session.sessionToken, /^[0-9a-f]{48}$/);
  assert.match(session.examUrl, /\/exam\/launch\?lt=[0-9a-f]{48}$/);
  assert.ok(session.nextBeatInSeconds >= 8 && session.nextBeatInSeconds <= 12);
  assert.equal(session.policy.mode, "school");
  assert.equal(session.policy.releaseOnSubmit, "teacher");
  assert.deepEqual(session.policy.allowedDomains, ["127.0.0.1", "resources.example.org"]);
  assert.equal(session.policy.allowedLinks[0].url, `${t.base}/resources/formula-sheet.html`);
  assert.equal(session.policy.allowedLinks[1].url, "https://resources.example.org/x.pdf");

  // Heartbeat with client-reported values; no command yet.
  const hb = await t.dev("POST", `/api/v1/sessions/${session.sessionId}/heartbeat`, device, session, { status: "locked", displayCount: 1, lockdownMode: "kiosk-fallback", uptimeSeconds: 5 });
  assert.equal(hb.status, 200, hb.text);
  assert.equal(hb.json.command, null);
  assert.ok(hb.json.remainingSeconds > 290 && hb.json.remainingSeconds <= 300);
  assert.ok(hb.json.nextBeatInSeconds >= 8 && hb.json.nextBeatInSeconds <= 12);

  // Wrong device token / wrong device for this session => 401.
  const other = await t.enroll("school");
  const wrong = await t.dev("POST", `/api/v1/sessions/${session.sessionId}/heartbeat`, other, session, { status: "locked" });
  assert.equal(wrong.status, 401);

  // Events.
  const ev = await t.dev("POST", `/api/v1/sessions/${session.sessionId}/events`, device, session, {
    events: [
      { type: "LOCKDOWN_ENGAGED", severity: "info", timestamp: new Date().toISOString(), metadata: { mode: "kiosk-fallback" } },
      { type: "APP_DEACTIVATED", severity: "medium" },
      { type: "bad type", severity: "info" },
      null
    ]
  });
  assert.equal(ev.status, 200);
  assert.equal(ev.json.accepted, 2);
  assert.equal(ev.json.dropped, 2);

  // Admin view labels client-reported fields.
  const list = await t.admin("GET", "/api/v1/admin/sessions");
  assert.equal(list.status, 200);
  const row = list.json.find((x) => x.sessionId === session.sessionId);
  assert.equal(row.clientReported.lockdownMode, "kiosk-fallback");
  assert.equal(row.riskLevel, "AMBER");
  assert.equal(row.studentId, "1025");

  // Release code needs a reason.
  const noReason = await t.admin("POST", `/api/v1/admin/sessions/${session.sessionId}/release-code`, {});
  assert.equal(noReason.status, 400);
  const rc = await t.admin("POST", `/api/v1/admin/sessions/${session.sessionId}/release-code`, { reason: "Student finished early" });
  assert.equal(rc.status, 200, rc.text);
  assert.match(rc.json.releaseCode, /^\d{6}$/);
  // The code is never persisted in clear.
  const stored = t.ctx.db.prepare("SELECT code_hash FROM release_codes").all().map((r) => r.code_hash);
  assert.ok(stored.every((h) => !h.includes(rc.json.releaseCode)));

  // Wrong code -> 403 INVALID; correct -> signed authorization; reuse -> 403 USED.
  const wrongCode = String((Number(rc.json.releaseCode) + 1) % 1_000_000).padStart(6, "0");
  const bad = await t.dev("POST", `/api/v1/sessions/${session.sessionId}/unlock`, device, session, { releaseCode: wrongCode, deviceId: device.deviceId });
  assert.equal(bad.status, 403);
  assert.equal(bad.json.error.code, "INVALID_RELEASE_CODE");
  const good = await t.dev("POST", `/api/v1/sessions/${session.sessionId}/unlock`, device, session, { releaseCode: rc.json.releaseCode, deviceId: device.deviceId });
  assert.equal(good.status, 200, good.text);
  assert.equal(good.json.authorized, true);
  const auth = good.json.authorization;
  assert.equal(auth.type, "RELEASE");
  assert.equal(auth.sessionId, session.sessionId);
  assert.equal(auth.deviceId, device.deviceId);
  assert.match(auth.nonce, /^[0-9a-f]{32}$/);
  assert.equal(auth.keyId, "k1");
  assert.equal(Date.parse(auth.expiresAt) - Date.parse(auth.issuedAt), 120_000);
  assert.equal(verifyAuth(device.serverPublicKey, auth), true);
  const reuse = await t.dev("POST", `/api/v1/sessions/${session.sessionId}/unlock`, device, session, { releaseCode: rc.json.releaseCode, deviceId: device.deviceId });
  assert.equal(reuse.status, 403);
  assert.equal(reuse.json.error.code, "RELEASE_CODE_USED");
  assert.equal(t.ctx.db.prepare("SELECT status FROM sessions WHERE id = ?").get(session.sessionId).status, "released");

  // Second session for the same student+exam after release is allowed; remote release path.
  const s2 = await t.startSession(device);
  assert.equal(s2.status, 200, s2.text);
  const sess2 = s2.json;
  const rel = await t.admin("POST", `/api/v1/admin/sessions/${sess2.sessionId}/release`, { reason: "Fire drill" });
  assert.equal(rel.status, 200);
  const hb2 = await t.dev("POST", `/api/v1/sessions/${sess2.sessionId}/heartbeat`, device, sess2, { status: "locked" });
  assert.equal(hb2.json.command.type, "RELEASE");
  assert.equal(hb2.json.command.reason, "Fire drill");
  assert.equal(verifyAuth(device.serverPublicKey, hb2.json.command.authorization), true);
  const hb3 = await t.dev("POST", `/api/v1/sessions/${sess2.sessionId}/heartbeat`, device, sess2, { status: "unlocked" });
  assert.equal(hb3.json.command, null, "RELEASE is delivered exactly once");

  // Audit log has the reasons; never the digits.
  const audit = await t.admin("GET", "/api/v1/admin/audit");
  const entries = audit.json.filter((a) => a.targetId === session.sessionId || a.targetId === sess2.sessionId);
  assert.ok(entries.some((a) => a.action === "session.release_code" && a.reason === "Student finished early"));
  assert.ok(entries.some((a) => a.action === "session.release" && a.reason === "Fire drill"));
  assert.ok(!JSON.stringify(audit.json).includes(rc.json.releaseCode));
});

test("unlock: expired code -> 403 EXPIRED; 5 wrong codes -> 423 LOCKED + high incident", async () => {
  const device = await t.enroll("school");
  const s = (await t.startSession(device, { studentCode: "2001" })).json;
  const rc = (await t.admin("POST", `/api/v1/admin/sessions/${s.sessionId}/release-code`, { reason: "test" })).json;
  t.ctx.db.prepare("UPDATE release_codes SET expires_at = ? WHERE session_id = ?").run(new Date(Date.now() - 1000).toISOString(), s.sessionId);
  const exp = await t.dev("POST", `/api/v1/sessions/${s.sessionId}/unlock`, device, s, { releaseCode: rc.releaseCode, deviceId: device.deviceId });
  assert.equal(exp.status, 403);
  assert.equal(exp.json.error.code, "RELEASE_CODE_EXPIRED");

  let last;
  for (let i = 0; i < 5; i++) {
    last = await t.dev("POST", `/api/v1/sessions/${s.sessionId}/unlock`, device, s, { releaseCode: String(100000 + i), deviceId: device.deviceId });
  }
  assert.equal(last.status, 423);
  assert.equal(last.json.error.code, "RELEASE_CODE_LOCKED");
  const again = await t.dev("POST", `/api/v1/sessions/${s.sessionId}/unlock`, device, s, { releaseCode: "123456", deviceId: device.deviceId });
  assert.equal(again.status, 423);
  const inc = await t.admin("GET", "/api/v1/admin/incidents?open=1");
  assert.ok(inc.json.some((i) => i.type === "RELEASE_CODE_LOCKED" && i.severity === "high" && i.sessionId === s.sessionId));
  const codeAfterLock = await t.admin("POST", `/api/v1/admin/sessions/${s.sessionId}/release-code`, { reason: "x" });
  assert.equal(codeAfterLock.status, 423);
  // Remote release still works and is signed.
  await t.admin("POST", `/api/v1/admin/sessions/${s.sessionId}/release`, { reason: "unlock after lock" });
  const hb = await t.dev("POST", `/api/v1/sessions/${s.sessionId}/heartbeat`, device, s, { status: "locked" });
  assert.equal(hb.json.command.type, "RELEASE");
});

test("submit: teacher mode waits, auto mode queues a signed RELEASE; submit is idempotent", async () => {
  const device = await t.enroll("byod");
  const s = (await t.startSession(device, { examCode: "DEMO", studentCode: "2001" })).json;
  const sub = await t.dev("POST", `/api/v1/sessions/${s.sessionId}/submit`, device, s, { answers: { q1: "4", q2: "nope" } });
  assert.equal(sub.status, 200, sub.text);
  assert.equal(sub.json.release, "teacher");
  const sub2 = await t.dev("POST", `/api/v1/sessions/${s.sessionId}/submit`, device, s, {});
  assert.equal(sub2.json.submittedAt, sub.json.submittedAt);
  const hb = await t.dev("POST", `/api/v1/sessions/${s.sessionId}/heartbeat`, device, s, { status: "locked" });
  assert.equal(hb.json.command, null, "teacher mode: no auto RELEASE");
  assert.equal(hb.json.remainingSeconds, 0);
  const detail = await t.admin("GET", `/api/v1/admin/sessions/${s.sessionId}`);
  assert.equal(detail.json.status, "submitted");
  assert.deepEqual(detail.json.answers, { q1: "4" }, "invalid option is ignored");
  assert.equal(detail.json.score.correct, 1);
  await t.admin("POST", `/api/v1/admin/sessions/${s.sessionId}/release`, { reason: "Reviewed" });
  const hb2 = await t.dev("POST", `/api/v1/sessions/${s.sessionId}/heartbeat`, device, s, { status: "locked" });
  assert.equal(hb2.json.command.type, "RELEASE");
  assert.equal(verifyAuth(device.serverPublicKey, hb2.json.command.authorization), true);

  const a = (await t.startSession(device, { examCode: "AUTO", studentCode: "2001" })).json;
  const subA = await t.dev("POST", `/api/v1/sessions/${a.sessionId}/submit`, device, a, {});
  assert.equal(subA.json.release, "auto");
  const hbA = await t.dev("POST", `/api/v1/sessions/${a.sessionId}/heartbeat`, device, a, { status: "locked" });
  assert.equal(hbA.json.command.type, "RELEASE");
  assert.equal(hbA.json.command.reason, "Exam submitted");
  assert.equal(verifyAuth(device.serverPublicKey, hbA.json.command.authorization), true);
});

test("time-up: expired session gets exactly one signed RELEASE", async () => {
  const device = await t.enroll("school");
  const s = (await t.startSession(device, { studentCode: "2001", examCode: "OTHER" })).json;
  t.ctx.db.prepare("UPDATE sessions SET expires_at = ? WHERE id = ?").run(new Date(Date.now() - 1000).toISOString(), s.sessionId);
  const hb = await t.dev("POST", `/api/v1/sessions/${s.sessionId}/heartbeat`, device, s, { status: "locked" });
  assert.equal(hb.json.remainingSeconds, 0);
  assert.equal(hb.json.command.type, "RELEASE");
  assert.equal(hb.json.command.reason, "Time expired");
  const hb2 = await t.dev("POST", `/api/v1/sessions/${s.sessionId}/heartbeat`, device, s, { status: "locked" });
  assert.equal(hb2.json.command, null);
});

test("concurrency: second active session for the same student+exam -> 409 + incident", async () => {
  const d1 = await t.enroll("school");
  const d2 = await t.enroll("school");
  const first = await t.startSession(d1, { studentCode: "2001", examCode: "OTHER" });
  assert.equal(first.status, 200, first.text);
  const second = await t.startSession(d2, { studentCode: "2001", examCode: "OTHER" });
  assert.equal(second.status, 409);
  assert.equal(second.json.error.code, "SESSION_ALREADY_ACTIVE");
  const inc = await t.admin("GET", "/api/v1/admin/incidents?open=1");
  assert.ok(inc.json.some((i) => i.type === "SESSION_CONFLICT" && i.sessionId === first.json.sessionId));
  // A different exam is fine.
  const other = await t.startSession(d2, { studentCode: "2001", examCode: "DEMO" });
  assert.equal(other.status, 200, other.text);
});

test("terminate: signed TERMINATE, then only a RELEASE ends it; warn is unsigned", async () => {
  const device = await t.enroll("school");
  const s = (await t.startSession(device, { studentCode: t.newStudent(), examCode: "OTHER" })).json;
  await t.admin("POST", `/api/v1/admin/sessions/${s.sessionId}/warn`, { message: "Eyes on your own screen", reason: "Looking around" });
  await t.admin("POST", `/api/v1/admin/sessions/${s.sessionId}/terminate`, { reason: "Phone seen" });
  const hb1 = await t.dev("POST", `/api/v1/sessions/${s.sessionId}/heartbeat`, device, s, { status: "locked" });
  assert.equal(hb1.json.command.type, "WARN");
  assert.equal(hb1.json.command.message, "Eyes on your own screen");
  assert.equal(hb1.json.command.authorization, undefined);
  const hb2 = await t.dev("POST", `/api/v1/sessions/${s.sessionId}/heartbeat`, device, s, { status: "locked" });
  assert.equal(hb2.json.command.type, "TERMINATE");
  assert.equal(verifyAuth(device.serverPublicKey, hb2.json.command.authorization), true);
  assert.equal(hb2.json.command.authorization.type, "TERMINATE");
  assert.equal(hb2.json.status, "terminated");
});

test("preflight: requireAAC is decided from the device registry, not the client flag", async () => {
  const device = await t.enroll("school");
  const student = t.newStudent();
  await t.admin("PUT", "/api/v1/admin/exams/OTHER", { policy: { requireAAC: true } });
  const lying = await t.startSession(device, { studentCode: student, examCode: "OTHER", preflight: { aacEntitlementPresent: true } });
  assert.equal(lying.status, 403);
  assert.deepEqual(lying.json.failures, ["AAC_REQUIRED"]);
  await t.admin("PUT", `/api/v1/admin/devices/${device.deviceId}`, { kioskVerified: true, reason: "MDM confirmed" });
  const ok = await t.startSession(device, { studentCode: student, examCode: "OTHER", preflight: { aacEntitlementPresent: false } });
  assert.equal(ok.status, 200, ok.text);
  // Client reports fallback despite requireAAC -> POLICY_MISMATCH incident.
  await t.dev("POST", `/api/v1/sessions/${ok.json.sessionId}/heartbeat`, device, ok.json, { status: "locked", lockdownMode: "kiosk-fallback" });
  const inc = await t.admin("GET", "/api/v1/admin/incidents?open=1");
  assert.ok(inc.json.some((i) => i.type === "POLICY_MISMATCH" && i.sessionId === ok.json.sessionId));
  await t.admin("PUT", "/api/v1/admin/exams/OTHER", { policy: { requireAAC: false } });
  await t.admin("POST", `/api/v1/admin/sessions/${ok.json.sessionId}/release`, { reason: "cleanup" });
  await t.dev("POST", `/api/v1/sessions/${ok.json.sessionId}/heartbeat`, device, ok.json, { status: "locked" });
});

test("exam window, assignment and access code gates", async () => {
  const device = await t.enroll("school");
  await t.admin("POST", "/api/v1/admin/exams", { code: "GATED", title: "Gated", durationMinutes: 10, openToAll: false, assignedStudentCodes: ["1025"], accessCode: "letmein", questions: [{ text: "?", options: ["a", "b"] }] });
  assert.equal((await t.startSession(device, { studentCode: "2001", examCode: "GATED" })).json.error.code, "NOT_ASSIGNED");
  assert.equal((await t.startSession(device, { studentCode: "1025", examCode: "GATED" })).json.error.code, "ACCESS_CODE_REQUIRED");
  assert.equal((await t.startSession(device, { studentCode: "1025", examCode: "GATED", accessCode: "wrong" })).json.error.code, "INVALID_ACCESS_CODE");
  await t.admin("POST", "/api/v1/admin/exams/GATED/close", { reason: "not yet" });
  assert.equal((await t.startSession(device, { studentCode: "1025", examCode: "GATED", accessCode: "letmein" })).json.error.code, "EXAM_CLOSED");
  await t.admin("POST", "/api/v1/admin/exams/GATED/open", { reason: "go" });
  const ok = await t.startSession(device, { studentCode: "1025", examCode: "GATED", accessCode: "letmein" });
  assert.equal(ok.status, 200, ok.text);
  assert.equal((await t.startSession(device, { studentCode: "9999", examCode: "GATED", accessCode: "letmein" })).json.error.code, "STUDENT_NOT_FOUND");
  assert.equal((await t.startSession(device, { studentCode: "1025", examCode: "NOPE" })).json.error.code, "EXAM_NOT_FOUND");
});
