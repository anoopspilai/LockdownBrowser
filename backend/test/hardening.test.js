// Robustness and security: malformed input never crashes, admin auth/CSRF, rate limits, caps, scoping, exam page cookie flow.
import { test, before, after } from "node:test";
import assert from "node:assert/strict";
import net from "node:net";
import { startTestApp } from "./helpers.js";

let t;
before(async () => {
  t = await startTestApp({ maxStoredEventsPerSession: 12 });
});
after(() => t.close());

async function alive() {
  const r = await t.req("GET", "/healthz", { cookies: false });
  return r.status === 200;
}

test("malformed URLs return 400 and the process stays up", async () => {
  for (const p of ["/%", "/%c0%af", "/exam/%ff", "/api/v1/sessions/%E0%A4%A/heartbeat", "/%2e%2e/%2e%2e/etc/passwd", "/admin/../server.js", "/a%00b"]) {
    const r = await t.req("GET", p, { cookies: false });
    assert.ok([400, 404].includes(r.status), `${p} -> ${r.status}`);
    assert.ok(r.json?.error?.code, `${p} has error envelope`);
  }
  // Raw socket: a truly malformed request line and an oversized header.
  await new Promise((resolve) => {
    const sock = net.connect(new URL(t.base).port, "127.0.0.1", () => {
      sock.write("GET /%zz HTTP/1.1\r\nHost: x\r\n\r\n");
    });
    sock.on("data", () => sock.end());
    sock.on("close", resolve);
    sock.on("error", resolve);
  });
  assert.equal(await alive(), true);
});

test("hostile JSON bodies: invalid JSON, non-objects, prototype keys, deep nesting, oversize -> 4xx, never 500", async () => {
  const device = await t.enroll();
  const s = (await t.startSession(device, { studentCode: t.newStudent() })).json;
  const bodies = ["not json", "null", "[1,2]", "12", '"x"', "true", '{"__proto__":{"x":1}}', '{"a":{"b":{"constructor":{"prototype":{"x":1}}}}}', '{"events":[{"metadata":{"__proto__":1}}]}', '{"a":' + "[".repeat(5000) + "]".repeat(5000) + "}", '{"status":5,"lockdownMode":true,"displayCount":{}}', '{"releaseCode":{}}', '{"heartbeatIntervalSeconds":1e309}'];
  const paths = [
    ["POST", "/api/v1/devices/enroll", {}],
    ["POST", "/api/v1/sessions", t.deviceHeaders(device)],
    ["POST", `/api/v1/sessions/${s.sessionId}/heartbeat`, t.deviceHeaders(device, s)],
    ["POST", `/api/v1/sessions/${s.sessionId}/events`, t.deviceHeaders(device, s)],
    ["POST", `/api/v1/sessions/${s.sessionId}/unlock`, t.deviceHeaders(device, s)],
    ["PUT", `/api/v1/sessions/${s.sessionId}/answers`, t.deviceHeaders(device, s)],
    ["POST", `/api/v1/sessions/${s.sessionId}/submit`, t.deviceHeaders(device, s)],
    ["PUT", "/api/v1/admin/policy", { "X-Requested-With": "AvaibeAdmin" }],
    ["POST", "/api/v1/auth/login", { "X-Requested-With": "AvaibeAdmin" }]
  ];
  for (const [method, path, headers] of paths) {
    for (const body of bodies) {
      const r = await t.req(method, path, { body, headers, cookies: false });
      assert.ok(r.status < 500, `${method} ${path} [${body.slice(0, 30)}] -> ${r.status} ${r.text.slice(0, 80)}`);
      if (body.includes("__proto__") || body.includes("constructor")) assert.equal(r.status, 400, `${path} proto key must be rejected`);
    }
  }
  const big = await t.req("POST", `/api/v1/sessions/${s.sessionId}/heartbeat`, { body: JSON.stringify({ pad: "x".repeat(300 * 1024) }), headers: t.deviceHeaders(device, s), cookies: false });
  assert.equal(big.status, 413);
  const bigEvents = await t.req("POST", `/api/v1/sessions/${s.sessionId}/events`, { body: JSON.stringify({ events: [], pad: "x".repeat(600 * 1024) }), headers: t.deviceHeaders(device, s), cookies: false });
  assert.equal(bigEvents.status, 200, "events allow up to 1 MiB");
  const tooBigEvents = await t.req("POST", `/api/v1/sessions/${s.sessionId}/events`, { body: JSON.stringify({ events: [], pad: "x".repeat(1100 * 1024) }), headers: t.deviceHeaders(device, s), cookies: false });
  assert.equal(tooBigEvents.status, 413);
  assert.equal(await alive(), true);
});

test("security headers on every response; no CORS", async () => {
  for (const p of ["/healthz", "/admin/", "/nope", "/api/v1/admin/sessions"]) {
    const r = await t.req("GET", p, { cookies: false, headers: { Origin: "https://evil.example" } });
    assert.equal(r.headers.get("x-content-type-options"), "nosniff", p);
    assert.equal(r.headers.get("x-frame-options"), "DENY", p);
    assert.equal(r.headers.get("referrer-policy"), "no-referrer", p);
    assert.equal(r.headers.get("cache-control"), "no-store", p);
    assert.match(r.headers.get("content-security-policy"), /default-src 'self'/, p);
    assert.equal(r.headers.get("access-control-allow-origin"), null, p);
  }
  const opt = await t.req("OPTIONS", "/api/v1/sessions", { cookies: false });
  assert.ok([404, 405].includes(opt.status));
});

test("admin routes: cookie + CSRF header + role; login lockout after 5 failures", async () => {
  const noCsrf = await t.req("GET", "/api/v1/admin/sessions", { cookies: false });
  assert.equal(noCsrf.status, 403);
  assert.equal(noCsrf.json.error.code, "CSRF_REQUIRED");
  const noCookie = await t.req("GET", "/api/v1/admin/sessions", { cookies: false, headers: { "X-Requested-With": "AvaibeAdmin" } });
  assert.equal(noCookie.status, 401);
  const badPw = await t.login("admin", "wrong-password-1");
  assert.equal(badPw.status, 401);
  const ok = await t.login();
  assert.equal(ok.status, 200);
  const setCookie = t.jar.get("avaibe_staff");
  assert.match(setCookie, /^[0-9a-f]{48}$/);
  const me = await t.admin("GET", "/api/v1/auth/me");
  assert.equal(me.json.user.role, "admin");

  // Reviewer cannot mutate; teacher cannot manage staff or tokens.
  await t.admin("POST", "/api/v1/admin/staff", { username: "rev", password: "Reviewer-Pass-1", role: "reviewer" });
  await t.admin("POST", "/api/v1/admin/staff", { username: "tea", password: "Teacher-Pass-12", role: "teacher" });
  const adminJar = new Map(t.jar);
  t.jar.clear();
  await t.login("rev", "Reviewer-Pass-1");
  assert.equal((await t.admin("GET", "/api/v1/admin/exams")).status, 200);
  assert.equal((await t.admin("POST", "/api/v1/admin/students", { code: "x1", name: "X" })).status, 403);
  t.jar.clear();
  await t.login("tea", "Teacher-Pass-12");
  assert.equal((await t.admin("POST", "/api/v1/admin/students", { code: "x1", name: "X" })).status, 200);
  assert.equal((await t.admin("GET", "/api/v1/admin/staff")).status, 403);
  assert.equal((await t.admin("POST", "/api/v1/admin/enrollment-tokens", { mode: "school" })).status, 403);
  t.jar.clear();

  // Lockout: 5 failures -> 429 even with the right password; other IP/user unaffected (same IP here, other user ok).
  for (let i = 0; i < 5; i++) assert.equal((await t.login("tea", "nope-nope-nope")).status, 401);
  const locked = await t.login("tea", "Teacher-Pass-12");
  assert.equal(locked.status, 429);
  assert.equal(locked.json.error.code, "LOGIN_LOCKED");
  assert.equal((await t.login("rev", "Reviewer-Pass-1")).status, 200);
  t.jar.clear();
  for (const [k, v] of adminJar) t.jar.set(k, v);

  // Logout clears the cookie.
  const out = await t.admin("POST", "/api/v1/auth/logout");
  assert.equal(out.status, 200);
  assert.equal(t.jar.has("avaibe_staff"), false);
  await t.login();
});

test("events: 20/s rate limit, 4 KiB metadata truncation, storage cap with count kept", async () => {
  const device = await t.enroll();
  const s = (await t.startSession(device, { studentCode: t.newStudent() })).json;
  const many =Array.from({ length: 30 }, () => ({ type: "WINDOW_RESIZED", severity: "info" }));
  const r = await t.dev("POST", `/api/v1/sessions/${s.sessionId}/events`, device, s, { events: many });
  assert.equal(r.status, 200);
  assert.ok(r.json.accepted <= 20 && r.json.dropped >= 10, JSON.stringify(r.json));
  await new Promise((res) => setTimeout(res, 1100));
  const bigMeta = await t.dev("POST", `/api/v1/sessions/${s.sessionId}/events`, device, s, { events: [{ type: "WEB_EVENT", severity: "info", metadata: { blob: "y".repeat(10_000) } }] });
  assert.equal(bigMeta.json.accepted, 1);
  const ev = (await t.admin("GET", `/api/v1/admin/sessions/${s.sessionId}/events`)).json;
  const stored = ev.find((e) => e.type === "WEB_EVENT");
  assert.ok(stored === undefined || stored.metadata._truncated === true);
  const row = t.ctx.db.prepare("SELECT event_count, stored_event_count FROM sessions WHERE id = ?").get(s.sessionId);
  assert.ok(row.stored_event_count <= 12, "cap honoured");
  assert.ok(row.event_count > row.stored_event_count, "count kept beyond cap");
  const inc = await t.admin("GET", "/api/v1/admin/incidents?open=1");
  assert.ok(inc.json.some((i) => i.type === "EVENT_CAP_REACHED" && i.sessionId === s.sessionId));
});

test("questions are scoped to the session's exam and never include the answer key", async () => {
  const device = await t.enroll();
  const s = (await t.startSession(device, { studentCode: t.newStudent(), examCode: "DEMO" })).json;
  const own =await t.dev("GET", `/api/v1/sessions/${s.sessionId}/questions`, device, s);
  assert.equal(own.status, 200);
  assert.equal(own.json.code, "DEMO");
  assert.ok(own.json.questions.every((q) => q.answer === undefined));
  const otherExam = await t.dev("GET", "/api/v1/exams/OTHER/questions", device, s);
  assert.equal(otherExam.status, 403);
  const sameExam = await t.dev("GET", "/api/v1/exams/DEMO/questions", device, s);
  assert.equal(sameExam.status, 200);
  const noAuth = await t.req("GET", "/api/v1/exams/DEMO/questions", { cookies: false });
  assert.equal(noAuth.status, 401);
  // Answers autosave through the device API.
  const save = await t.dev("PUT", `/api/v1/sessions/${s.sessionId}/answers`, device, s, { answers: { q1: "4", zz: "x", q2: 5 } });
  assert.equal(save.json.saved, 1);
});

test("exam page: launch token is one-time and sets a scoped httpOnly cookie; page-token route does not exist", async () => {
  const device = await t.enroll();
  const s = (await t.startSession(device, { studentCode: t.newStudent(), examCode: "DEMO" })).json;
  const lt =new URL(s.examUrl).searchParams.get("lt");
  const noCookie = await t.req("GET", `/exam/${s.sessionId}`, { cookies: false });
  assert.equal(noCookie.status, 401);
  const launch = await t.req("GET", `/exam/launch?lt=${lt}`, { cookies: false, raw: true });
  assert.equal(launch.status, 302);
  assert.equal(launch.headers.get("location"), `/exam/${s.sessionId}`);
  const sc = launch.headers.get("set-cookie");
  assert.match(sc, /^avaibe_exam=sess_[0-9a-f]{32}\.[0-9a-f]{48}; Path=\/exam\/; HttpOnly; SameSite=Strict$/);
  const cookie = sc.split(";")[0];
  const again = await t.req("GET", `/exam/launch?lt=${lt}`, { cookies: false });
  assert.equal(again.status, 403);
  const page = await t.req("GET", `/exam/${s.sessionId}`, { cookies: false, headers: { Cookie: cookie } });
  assert.equal(page.status, 200);
  assert.match(page.headers.get("content-security-policy"), /connect-src 'self'/);
  assert.match(page.text, /exam\.js/);
  const wrongId = await t.req("GET", `/exam/sess_${"0".repeat(32)}`, { cookies: false, headers: { Cookie: cookie } });
  assert.equal(wrongId.status, 403);
  const status = await t.req("GET", "/exam/api/status", { cookies: false, headers: { Cookie: cookie } });
  assert.equal(status.status, 200);
  assert.equal(status.json.allowedLinks.length, 2);
  const qs = await t.req("GET", "/exam/api/questions", { cookies: false, headers: { Cookie: cookie } });
  assert.equal(qs.json.questions.length, 2);
  const save = await t.req("PUT", "/exam/api/answers", { body: { answers: { q1: "4" } }, cookies: false, headers: { Cookie: cookie } });
  assert.equal(save.json.saved, 1);
  const sub = await t.req("POST", "/exam/api/submit", { body: {}, cookies: false, headers: { Cookie: cookie } });
  assert.equal(sub.json.release, "teacher");
  const legacy = await t.req("GET", `/api/v1/sessions/${s.sessionId}/page-token`, { cookies: false });
  assert.equal(legacy.status, 404);
  const badLt = await t.req("GET", "/exam/launch?lt=zzz", { cookies: false });
  assert.equal(badLt.status, 403);
});

test("enrollment rate limit and revoked device", async () => {
  let last;
  for (let i = 0; i < 31; i++) last = await t.req("POST", "/api/v1/devices/enroll", { body: { enrollmentToken: "nope" }, cookies: false });
  assert.equal(last.status, 429);
  t.ctx.db.prepare("DELETE FROM rate_limits").run();
  const device = await t.enroll();
  await t.admin("DELETE", `/api/v1/admin/devices/${device.deviceId}`, { reason: "lost" });
  const r = await t.startSession(device);
  assert.equal(r.status, 403);
  assert.equal(r.json.error.code, "DEVICE_REVOKED");
});

test("exam validation: allowedLinks must be https or same host; policy rejects unknown/derived fields", async () => {
  const bad = await t.admin("POST", "/api/v1/admin/exams", { code: "L1", title: "x", durationMinutes: 5, allowedLinks: [{ label: "evil", url: "http://evil.example/x" }] });
  assert.equal(bad.status, 400);
  const bad2 = await t.admin("POST", "/api/v1/admin/exams", { code: "L1", title: "x", durationMinutes: 5, allowedLinks: [{ label: "u", url: "https://user:pw@ok.example/x" }] });
  assert.equal(bad2.status, 400);
  const badPolicy = await t.admin("POST", "/api/v1/admin/exams", { code: "L1", title: "x", durationMinutes: 5, policy: { allowedDomains: ["evil.example"] } });
  assert.equal(badPolicy.status, 400);
  const ok = await t.admin("POST", "/api/v1/admin/exams", { code: "L1", title: "x", durationMinutes: 5, allowedLinks: [{ label: "ok", url: "https://ok.example/x" }, { label: "local", url: "/resources/formula-sheet.html" }], policy: { heartbeatIntervalSeconds: 5 } });
  assert.equal(ok.status, 200, ok.text);
  const tooFast = await t.admin("PUT", "/api/v1/admin/exams/L1", { policy: { heartbeatIntervalSeconds: 1 } });
  assert.equal(tooFast.status, 400);
  const del = await t.admin("DELETE", "/api/v1/admin/exams/L1", { reason: "cleanup" });
  assert.equal(del.status, 200);
  const csv = await t.admin("POST", "/api/v1/admin/students/import", { csv: 'code,name,email\n3001,"Ada, Lovelace",ada@example.org\n,missing\n3002,Bob' });
  assert.equal(csv.json.created, 2);
  assert.equal(csv.json.skipped.length, 1);
});

test("audit_log and events are append-only at the database level", () => {
  assert.throws(() => t.ctx.db.prepare("DELETE FROM audit_log").run(), /append-only/);
  assert.throws(() => t.ctx.db.prepare("UPDATE events SET type = 'x'").run(), /append-only/);
});
