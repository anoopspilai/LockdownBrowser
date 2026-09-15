// Test harness: in-memory DB, random port, seeded admin/exam/student, tiny HTTP client with cookie jar.
import { loadConfig } from "../src/config.js";
import { createLogger } from "../src/log.js";
import { openDb } from "../src/db.js";
import { createApp, ensureAdmin } from "../src/app.js";
import * as exams from "../src/services/exams.js";
import * as students from "../src/services/students.js";
import { issueEnrollmentToken } from "../src/auth/device.js";

export const ADMIN_PASSWORD = "Test-Admin-Pass-1";

export async function startTestApp(overrides = {}) {
  const config = Object.freeze({ ...loadConfig({ PORT: "0", AVAIBE_DB: ":memory:", AVAIBE_LOG_LEVEL: "error" }), ...overrides });
  const app = createApp({ config, log: createLogger("error"), db: openDb(":memory:") });
  await ensureAdmin(app.ctx, { password: ADMIN_PASSWORD });
  const addr = await app.listen(0, "127.0.0.1");
  const base = `http://127.0.0.1:${addr.port}`;
  const ctx = app.ctx;

  exams.upsert(ctx, {
    code: "DEMO", title: "Demo Exam", durationMinutes: 5, releaseOnSubmit: "teacher",
    questions: [
      { id: "q1", text: "2+2?", options: ["3", "4"], answer: "4" },
      { id: "q2", text: "Sky colour?", options: ["Blue", "Green"], answer: "Blue" }
    ],
    allowedLinks: [{ label: "Formula sheet", url: "/resources/formula-sheet.html" }, { label: "Ext", url: "https://resources.example.org/x.pdf" }]
  });
  exams.upsert(ctx, { code: "AUTO", title: "Auto release", durationMinutes: 5, releaseOnSubmit: "auto", questions: [{ id: "q1", text: "1+1?", options: ["2", "3"], answer: "2" }] });
  exams.upsert(ctx, { code: "OTHER", title: "Other exam", durationMinutes: 5, questions: [{ id: "q1", text: "secret?", options: ["a", "b"], answer: "a" }] });
  students.create(ctx, { code: "1025", name: "Demo Student" });
  students.create(ctx, { code: "2001", name: "Second Student" });

  const jar = new Map();
  async function req(method, path, { body, headers = {}, cookies = true, raw = false } = {}) {
    const h = { ...headers };
    if (body !== undefined) h["Content-Type"] = "application/json";
    if (cookies && jar.size) h.Cookie = [...jar].map(([k, v]) => `${k}=${v}`).join("; ");
    const res = await fetch(base + path, { method, headers: h, body: body === undefined ? undefined : typeof body === "string" ? body : JSON.stringify(body), redirect: "manual" });
    for (const sc of res.headers.getSetCookie?.() || []) {
      const [pair] = sc.split(";");
      const i = pair.indexOf("=");
      const k = pair.slice(0, i).trim();
      const v = pair.slice(i + 1).trim();
      if (sc.includes("Max-Age=0") || !v) jar.delete(k);
      else jar.set(k, v);
    }
    if (raw) return res;
    const text = await res.text();
    let json = null;
    try {
      json = JSON.parse(text);
    } catch {
      /* not JSON */
    }
    return { status: res.status, json, text, headers: res.headers };
  }

  const admin = (method, path, body) => req(method, path, { body, headers: { "X-Requested-With": "AvaibeAdmin" } });
  async function login(username = "admin", password = ADMIN_PASSWORD) {
    return admin("POST", "/api/v1/auth/login", { username, password });
  }

  function enrollmentToken(mode = "school") {
    return issueEnrollmentToken(ctx, { mode, label: "test", createdBy: "test" }).token;
  }

  async function enroll(mode = "school", extra = {}) {
    const r = await req("POST", "/api/v1/devices/enroll", { body: { enrollmentToken: enrollmentToken(mode), platform: "macos", osVersion: "26.0", clientVersion: "0.1.0", hardwareId: "hw-1", deviceName: "Test Mac", ...extra }, cookies: false });
    if (r.status !== 200) throw new Error("enroll failed: " + r.text);
    return r.json;
  }

  function deviceHeaders(dev, session) {
    const h = { "X-Device-Id": dev.deviceId, "X-Device-Token": dev.deviceToken, "X-Client-Version": "0.1.0" };
    if (session) h.Authorization = `Bearer ${session.sessionToken}`;
    return h;
  }

  async function startSession(dev, { studentCode = "1025", examCode = "DEMO", preflight = {}, ...rest } = {}) {
    return req("POST", "/api/v1/sessions", {
      body: { studentCode, examCode, deviceId: dev.deviceId, preflight: { osVersion: "26.0", sipEnabled: true, mdmEnrolled: false, accountType: "standard", displayCount: 1, screenSharingActive: false, aacEntitlementPresent: false, clientVersion: "0.1.0", ...preflight }, ...rest },
      headers: deviceHeaders(dev),
      cookies: false
    });
  }

  const dev = (method, path, device, session, body) => req(method, path, { body, headers: deviceHeaders(device, session), cookies: false });

  let studentSeq = 0;
  /** Creates a fresh student so tests never collide on the one-active-session-per-student-per-exam rule. */
  function newStudent() {
    const code = `t${++studentSeq}`;
    students.create(ctx, { code, name: `Test Student ${studentSeq}` });
    return code;
  }

  return {
    app, ctx, base, req, admin, login, jar, enroll, enrollmentToken, deviceHeaders, startSession, dev, newStudent,
    close: () => app.close()
  };
}
