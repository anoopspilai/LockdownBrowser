// Avaibe Exam staff console. Vanilla JS, CSP-compatible (no inline handlers, no innerHTML with user data).
(function () {
  "use strict";
  const $ = (id) => document.getElementById(id);

  // ---- DOM helpers (everything user-provided goes through textContent) --------------------------
  function h(tag, attrs, ...children) {
    const el = document.createElement(tag);
    if (attrs) for (const [k, v] of Object.entries(attrs)) {
      if (v === undefined || v === null || v === false) continue;
      if (k === "class") el.className = v;
      else if (k === "dataset") Object.assign(el.dataset, v);
      else if (k in el && typeof v !== "string") el[k] = v;
      else el.setAttribute(k, v);
    }
    for (const c of children.flat()) {
      if (c === null || c === undefined || c === false) continue;
      el.appendChild(typeof c === "string" || typeof c === "number" ? document.createTextNode(String(c)) : c);
    }
    return el;
  }
  const badge = (cls, text) => h("span", { class: "badge " + cls }, text);
  const clear = (el) => { while (el.firstChild) el.removeChild(el.firstChild); return el; };
  const fmtTime = (iso) => (iso ? new Date(iso).toLocaleString() : "—");
  const fmtClock = (iso) => (iso ? new Date(iso).toLocaleTimeString() : "—");
  function ago(iso) {
    if (!iso) return "never";
    const s = Math.max(0, Math.round((Date.now() - Date.parse(iso)) / 1000));
    if (s < 60) return `${s}s ago`;
    if (s < 3600) return `${Math.floor(s / 60)}m ${s % 60}s ago`;
    return `${Math.floor(s / 3600)}h ${Math.floor((s % 3600) / 60)}m ago`;
  }
  const fmtRemaining = (sec) => (typeof sec !== "number" ? "" : `${Math.floor(sec / 60)}:${String(sec % 60).padStart(2, "0")} left`);
  let toastTimer;
  function toast(text, err) {
    document.querySelectorAll(".toast").forEach((t) => t.remove());
    const el = h("div", { class: "toast" + (err ? " err" : "") }, text);
    document.body.appendChild(el);
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => el.remove(), err ? 7000 : 3500);
  }
  function setStatus(id, text, cls) { const el = $(id); el.textContent = text; el.className = "status " + (cls || ""); }
  function askReason(what) {
    const r = prompt(`Reason for "${what}" (recorded in the audit log):`, "");
    if (r === null) return null;
    if (!r.trim()) { toast("A reason is required.", true); return null; }
    return r.trim();
  }

  // ---- API ---------------------------------------------------------------------------------------
  async function api(method, path, body) {
    const res = await fetch(path, {
      method,
      credentials: "same-origin",
      headers: { "X-Requested-With": "AvaibeAdmin", ...(body ? { "Content-Type": "application/json" } : {}) },
      body: body ? JSON.stringify(body) : undefined
    });
    const data = await res.json().catch(() => ({}));
    if (res.status === 401 && state.user) { showLogin("Session expired. Sign in again."); }
    if (!res.ok) throw new Error((data.error && (data.error.code + ": " + data.error.message)) || `HTTP ${res.status}`);
    return data;
  }

  const state = { user: null, tab: "live", sessions: [], expanded: new Set(), codes: new Map(), eventsCache: new Map(), timers: [], editingExam: null, students: [] };

  // ---- Auth / shell -------------------------------------------------------------------------------
  function showLogin(msg) {
    state.user = null;
    stopTimers();
    $("appView").hidden = true;
    $("loginView").hidden = false;
    $("tabs").hidden = true;
    $("logoutBtn").hidden = true;
    $("who").textContent = "";
    if (msg) setStatus("loginStatus", msg, "err");
  }
  function showApp(user) {
    state.user = user;
    $("loginView").hidden = true;
    $("appView").hidden = false;
    $("tabs").hidden = false;
    $("logoutBtn").hidden = false;
    $("staffTabBtn").hidden = user.role !== "admin";
    $("who").textContent = `${user.username} · ${user.role}`;
    selectTab(state.tab);
    startTimers();
  }
  $("loginForm").addEventListener("submit", async (e) => {
    e.preventDefault();
    const f = e.target;
    setStatus("loginStatus", "Signing in…");
    try {
      const r = await api("POST", "/api/v1/auth/login", { username: f.username.value.trim(), password: f.password.value });
      f.password.value = "";
      setStatus("loginStatus", "");
      showApp(r.user);
    } catch (err) { setStatus("loginStatus", err.message, "err"); }
  });
  $("logoutBtn").addEventListener("click", async () => { try { await api("POST", "/api/v1/auth/logout"); } catch {} showLogin(); });
  $("tabs").addEventListener("click", (e) => { const b = e.target.closest("button[data-tab]"); if (b) selectTab(b.dataset.tab); });
  function selectTab(tab) {
    state.tab = tab;
    document.querySelectorAll("#tabs button").forEach((b) => b.classList.toggle("active", b.dataset.tab === tab));
    document.querySelectorAll("main#appView .tab").forEach((s) => { s.hidden = s.id !== "tab-" + tab; });
    ({ live: pollSessions, exams: loadExams, students: loadStudents, devices: loadDevices, incidents: loadIncidents, audit: loadAudit, staff: loadStaff })[tab]?.();
  }
  function startTimers() {
    stopTimers();
    state.timers.push(setInterval(() => { if (state.tab === "live") pollSessions(); }, 3000));
    state.timers.push(setInterval(pollHealth, 10000));
    state.timers.push(setInterval(tickCodes, 1000));
    pollHealth();
  }
  function stopTimers() { state.timers.forEach(clearInterval); state.timers = []; }
  async function pollHealth() {
    try { const hh = await api("GET", "/healthz"); $("health").textContent = `backend ${hh.version} · ${hh.sessions} live · ${hh.devices} devices`; }
    catch { $("health").textContent = "backend unreachable"; }
  }
  const canWrite = () => state.user && (state.user.role === "admin" || state.user.role === "teacher");
  const isAdmin = () => state.user && state.user.role === "admin";

  // ---- LIVE ---------------------------------------------------------------------------------------
  const statusCls = (s) => ({ active: "b-blue", submitted: "b-green", released: "b-grey", terminated: "b-red", expired: "b-amber" }[s] || "b-grey");
  const lockInfo = (m) => ({ aac: ["b-green", "AAC"], "assigned-access": ["b-green", "Assigned Access"], "kiosk-fallback": ["b-amber", "Kiosk fallback"], none: ["b-red", "Not locked"] }[m] || ["b-grey", "not reported"]);
  const riskCls = (r) => ({ GREEN: "b-green", AMBER: "b-amber", RED: "b-red" }[r] || "b-grey");
  const hbCls = (s) => (s.stale ? "b-red" : !s.lastHeartbeat ? "b-grey" : (Date.now() - Date.parse(s.lastHeartbeat)) / 1000 < 30 ? "b-green" : "b-amber");

  async function pollSessions() {
    try {
      const all = $("showAll").checked;
      state.sessions = await api("GET", "/api/v1/admin/sessions" + (all ? "" : "?status=live"));
      $("pollStatus").textContent = "updated " + new Date().toLocaleTimeString();
      await Promise.all([...state.expanded].map(async (id) => { try { state.eventsCache.set(id, await api("GET", `/api/v1/admin/sessions/${id}/events`)); } catch {} }));
    } catch (e) { $("pollStatus").textContent = "poll failed: " + e.message; }
    renderRows();
  }
  $("showAll").addEventListener("change", pollSessions);

  function renderRows() {
    const tbody = clear($("rows"));
    $("sessionCount").textContent = state.sessions.length ? `(${state.sessions.length})` : "";
    if (!state.sessions.length) { tbody.appendChild(h("tr", null, h("td", { colSpan: 11, class: "empty" }, "No sessions. Start one from the Avaibe Exam app."))); return; }
    for (const s of state.sessions) {
      const [lockCls, lockText] = lockInfo(s.clientReported.lockdownMode);
      const isOpen = state.expanded.has(s.sessionId);
      const cr = s.clientReported;
      tbody.appendChild(h("tr", { dataset: { id: s.sessionId } },
        h("td", null, h("code", null, s.sessionId.slice(0, 13) + "…"), h("br"), h("small", { class: "muted" }, s.platform, s.deviceKioskVerified ? " · kiosk ✓" : "")),
        h("td", null, s.studentName, h("br"), h("small", { class: "muted" }, "#" + s.studentId)),
        h("td", null, s.examCode, h("br"), h("small", { class: "muted" }, fmtRemaining(s.remainingSeconds))),
        h("td", null, badge(statusCls(s.status), s.status), s.stale ? [" ", badge("b-red", "stale")] : null, cr.clientStatus ? [h("br"), h("small", { class: "muted" }, "client: " + cr.clientStatus)] : null),
        h("td", null, badge(lockCls, lockText), s.requireAAC ? [h("br"), h("small", { class: "muted" }, "policy: requireAAC")] : null),
        h("td", null, cr.displayCount ?? "—"),
        h("td", null, cr.clientVersion || "—", cr.clientVersionOk ? null : [" ", badge("b-amber", "old")]),
        h("td", null, badge(hbCls(s), ago(s.lastHeartbeat))),
        h("td", null, badge(riskCls(s.riskLevel), s.riskLevel)),
        h("td", null, String(s.eventCount), s.pendingCommands ? h("small", { class: "muted" }, ` (+${s.pendingCommands} cmd)`) : null),
        h("td", { class: "actions" },
          h("button", { dataset: { act: "code" }, disabled: !canWrite() || s.status === "released" || s.releaseCodeLocked }, "Release code"),
          h("button", { dataset: { act: "release" }, disabled: !canWrite() || s.status === "released" }, "Remote release"),
          h("button", { dataset: { act: "warn" }, disabled: !canWrite() }, "Warn…"),
          h("button", { dataset: { act: "terminate" }, class: "danger", disabled: !canWrite() || ["released", "terminated"].includes(s.status) }, "Terminate"),
          h("button", { dataset: { act: "events" }, class: isOpen ? "primary" : "" }, "Details"))
      ));
      const code = state.codes.get(s.sessionId);
      if (code || isOpen) tbody.appendChild(h("tr", { class: "details", dataset: { id: s.sessionId } }, h("td", { colSpan: 11 }, code ? renderCode(code) : null, isOpen ? renderDetails(s) : null)));
    }
  }
  function renderCode(c) {
    const left = Math.max(0, Math.round((Date.parse(c.expiresAt) - Date.now()) / 1000));
    return h("div", { class: "code-box" + (left === 0 ? " expired" : "") },
      h("span", { class: "muted" }, "Release code"), h("span", { class: "code" }, c.releaseCode), h("span", { class: "ttl" }, left === 0 ? "expired" : `expires in ${left}s`));
  }
  function tickCodes() {
    for (const [id, c] of state.codes) {
      const cell = document.querySelector(`tr.details[data-id="${CSS.escape(id)}"] .code-box`);
      if (cell) cell.replaceWith(renderCode(c));
      if (Date.parse(c.expiresAt) + 30000 < Date.now()) { state.codes.delete(id); renderRows(); }
    }
  }
  function renderDetails(s) {
    const list = state.eventsCache.get(s.sessionId);
    const detail = state.eventsCache.get(s.sessionId + ":detail");
    const box = h("div");
    if (detail) {
      const dl = h("dl", { class: "kv" });
      const kv = (k, v) => dl.append(h("dt", null, k), h("dd", null, v));
      kv("Session", detail.sessionId); kv("Device", `${detail.deviceId} (${detail.platform}${detail.deviceName ? ", " + detail.deviceName : ""}) kiosk verified: ${detail.deviceKioskVerified ? "yes" : "no"}`);
      kv("Mode / release", `${detail.mode} · releaseOnSubmit=${detail.releaseOnSubmit}`);
      kv("Started / expires", `${fmtTime(detail.startedAt)} → ${fmtTime(detail.expiresAt)}`);
      kv("Submitted / released", `${fmtTime(detail.submittedAt)} / ${fmtTime(detail.releasedAt)}`);
      kv("Score", `${detail.score.correct}/${detail.score.gradable} correct · ${detail.score.answered}/${detail.score.total} answered`);
      kv("Release code", detail.releaseCodeLocked ? `LOCKED after ${detail.releaseCodeFailures} failures` : `${detail.releaseCodeFailures} failed attempts`);
      kv("Preflight (client-reported)", JSON.stringify(detail.preflight));
      if (detail.preflightExtras) kv("Preflight extras (client-reported)", JSON.stringify(detail.preflightExtras));
      kv("Allowed domains", (detail.policy.allowedDomains || []).join(", "));
      box.appendChild(dl);
    }
    if (!list) box.appendChild(h("div", { class: "muted" }, "Loading events…"));
    else if (!list.length) box.appendChild(h("div", { class: "muted" }, "No events yet."));
    else {
      box.appendChild(h("div", { class: "muted stack-sm" }, `Event timeline (${list.length} stored of ${s.eventCount}, newest last)`));
      const ul = h("ul", { class: "events" });
      for (const e of list) {
        const meta = e.metadata && Object.keys(e.metadata).length ? JSON.stringify(e.metadata) : "";
        ul.appendChild(h("li", null, h("span", { class: "t" }, fmtClock(e.timestamp || e.receivedAt)), h("span", { class: "sev-" + e.severity }, e.severity),
          h("span", null, h("span", { class: "type" }, e.type), e.source !== "client" ? h("small", { class: "muted" }, ` (${e.source})`) : null, meta ? h("div", { class: "meta" }, meta) : null)));
      }
      box.appendChild(ul);
    }
    return box;
  }
  $("rows").addEventListener("click", async (e) => {
    const btn = e.target.closest("button[data-act]");
    if (!btn) return;
    const id = btn.closest("tr").dataset.id;
    const act = btn.dataset.act;
    btn.disabled = true;
    try {
      if (act === "code") {
        const reason = askReason("release code"); if (reason === null) return;
        state.codes.set(id, await api("POST", `/api/v1/admin/sessions/${id}/release-code`, { reason }));
      } else if (act === "release") {
        const reason = askReason("remote release"); if (reason === null) return;
        await api("POST", `/api/v1/admin/sessions/${id}/release`, { reason }); toast("RELEASE queued (delivered on next heartbeat)");
      } else if (act === "warn") {
        const message = prompt("Warning message to show the student:", "Please keep your eyes on your own screen."); if (!message || !message.trim()) return;
        const reason = askReason("warn"); if (reason === null) return;
        await api("POST", `/api/v1/admin/sessions/${id}/warn`, { message: message.trim(), reason }); toast("WARN queued");
      } else if (act === "terminate") {
        if (!confirm("Terminate this session? The client submits the exam and holds a locked screen until a RELEASE.")) return;
        const reason = askReason("terminate"); if (reason === null) return;
        await api("POST", `/api/v1/admin/sessions/${id}/terminate`, { reason }); toast("TERMINATE queued");
      } else if (act === "events") {
        if (state.expanded.has(id)) state.expanded.delete(id);
        else {
          state.expanded.add(id);
          const [ev, det] = await Promise.all([api("GET", `/api/v1/admin/sessions/${id}/events`), api("GET", `/api/v1/admin/sessions/${id}`)]);
          state.eventsCache.set(id, ev); state.eventsCache.set(id + ":detail", det);
        }
      }
      await pollSessions();
    } catch (err) { toast(err.message, true); btn.disabled = false; }
  });

  // ---- EXAMS --------------------------------------------------------------------------------------
  async function loadExams() {
    try {
      const exams = await api("GET", "/api/v1/admin/exams");
      const tbody = clear($("examRows"));
      if (!exams.length) tbody.appendChild(h("tr", null, h("td", { colSpan: 7, class: "empty" }, "No exams yet.")));
      for (const x of exams) {
        const win = [x.isOpen ? badge("b-green", "open") : badge("b-red", "closed"), x.openFrom || x.openUntil ? h("small", { class: "muted" }, ` ${x.openFrom ? "from " + fmtTime(x.openFrom) : ""} ${x.openUntil ? "until " + fmtTime(x.openUntil) : ""}`) : null];
        tbody.appendChild(h("tr", { dataset: { code: x.code } },
          h("td", null, h("code", null, x.code), x.hasAccessCode ? h("small", { class: "muted" }, " · access code") : null),
          h("td", { class: "wrap" }, x.title, h("br"), h("small", { class: "muted" }, x.kind === "external" && x.startUrl ? (() => { try { return new URL(x.startUrl).host + " · "; } catch { return ""; } })() : "", x.openToAll ? "open to all students" : "assigned students only", x.allowedLinks.length ? ` · ${x.allowedLinks.length} link(s)` : "")),
          h("td", null, x.durationMinutes + " min"), h("td", null, x.kind === "external" ? badge("b-grey", "external website") : String(x.questionCount)), h("td", null, win), h("td", null, x.releaseOnSubmit),
          h("td", { class: "actions" }, h("button", { dataset: { act: "edit" } }, "Edit"), h("button", { dataset: { act: x.isOpen ? "close" : "open" }, disabled: !canWrite() }, x.isOpen ? "Close" : "Open"))));
      }
    } catch (e) { toast(e.message, true); }
  }
  $("examRows").addEventListener("click", async (e) => {
    const btn = e.target.closest("button[data-act]"); if (!btn) return;
    const code = btn.closest("tr").dataset.code;
    try {
      if (btn.dataset.act === "edit") openExamEditor(await api("GET", `/api/v1/admin/exams/${encodeURIComponent(code)}`));
      else { const reason = askReason(btn.dataset.act + " exam " + code); if (reason === null) return; await api("POST", `/api/v1/admin/exams/${encodeURIComponent(code)}/${btn.dataset.act}`, { reason }); toast(`Exam ${code} ${btn.dataset.act === "open" ? "opened" : "closed"}`); loadExams(); }
    } catch (err) { toast(err.message, true); }
  });
  $("newExamBtn").addEventListener("click", () => openExamEditor(null));
  /** External exams (CONTRACT §11): start address + allowed sites instead of questions. */
  function applyExamKind() {
    const f = $("examForm");
    const external = f.kind.value === "external";
    $("startUrlField").hidden = !external;
    $("allowedSitesField").hidden = !external;
    $("questionsSection").hidden = external;
    f.startUrl.required = external;
  }
  $("examForm").kind.addEventListener("change", applyExamKind);
  $("closeExamEditor").addEventListener("click", () => { $("examEditor").hidden = true; state.editingExam = null; });
  const P_BOOLS = ["requireSIP", "requireMDM", "requireStandardAccount", "requireAAC", "blockExternalDisplay", "allowStudentReleaseCode", "allowClipboard", "allowPrinting"];
  const P_NUMS = ["heartbeatIntervalSeconds", "eventFlushIntervalSeconds", "offlineGraceSeconds"];
  const toLocalInput = (iso) => { if (!iso) return ""; const d = new Date(iso); const p = (n) => String(n).padStart(2, "0"); return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}T${p(d.getHours())}:${p(d.getMinutes())}`; };
  function openExamEditor(x) {
    state.editingExam = x ? x.code : null;
    const f = $("examForm");
    f.reset();
    $("examEditor").hidden = false;
    $("examEditorTitle").textContent = x ? `Edit ${x.code}` : "New exam";
    $("deleteExam").hidden = !x;
    f.code.disabled = !!x;
    f.code.value = x ? x.code : "";
    f.title.value = x ? x.title : "";
    f.durationMinutes.value = x ? x.durationMinutes : 60;
    f.releaseOnSubmit.value = x ? x.releaseOnSubmit : "teacher";
    f.accessCode.value = ""; f.accessCode.placeholder = x && x.hasAccessCode ? "(set — leave blank to keep, type '-' to remove)" : "none";
    f.isOpen.checked = x ? x.isOpen : true;
    f.openToAll.checked = x ? x.openToAll : true;
    f.openFrom.value = toLocalInput(x && x.openFrom); f.openUntil.value = toLocalInput(x && x.openUntil);
    f.assignedStudentCodes.value = x ? x.assignedStudentCodes.join(", ") : "";
    f.kind.value = x && x.kind === "external" ? "external" : "questions";
    f.startUrl.value = x && x.startUrl ? x.startUrl : "";
    f.allowedSites.value = x && x.allowedSites ? x.allowedSites.join("\n") : "";
    applyExamKind();
    const p = (x && x.policy) || {};
    for (const k of P_BOOLS) f["p_" + k].checked = p[k] !== undefined ? !!p[k] : ["blockExternalDisplay", "allowStudentReleaseCode"].includes(k);
    for (const k of P_NUMS) f["p_" + k].value = p[k] !== undefined ? p[k] : { heartbeatIntervalSeconds: 10, eventFlushIntervalSeconds: 5, offlineGraceSeconds: 600 }[k];
    f.p_externalDisplayAction.value = p.externalDisplayAction || "WARN";
    f.p_minClientVersion.value = p.minClientVersion || "0.1.0";
    clear($("questionList")); clear($("linkList"));
    (x ? x.questions : []).forEach(addQuestionBlock);
    (x ? x.allowedLinks : []).forEach(addLinkRow);
    setStatus("examStatus", "");
    $("examEditor").scrollIntoView({ behavior: "smooth", block: "start" });
  }
  function addQuestionBlock(q) {
    const n = $("questionList").children.length + 1;
    const block = h("div", { class: "qblock" },
      h("div", { class: "qhead" }, h("span", null, `Question ${n}`), h("button", { type: "button", dataset: { act: "remove" }, class: "danger" }, "Remove")),
      h("div", { class: "field" }, "Id", h("input", { type: "text", name: "qid", value: (q && q.id) || `q${n}` })),
      h("div", { class: "field" }, "Text", h("input", { type: "text", name: "qtext", value: (q && q.text) || "" })),
      h("div", { class: "field" }, "Options (one per line)", h("textarea", { name: "qoptions" }, (q && q.options || []).join("\n"))),
      h("div", { class: "field" }, "Correct answer (exact option text; blank = ungraded)", h("input", { type: "text", name: "qanswer", value: (q && q.answer) || "" })));
    $("questionList").appendChild(block);
  }
  function addLinkRow(l) {
    $("linkList").appendChild(h("div", { class: "lrow" },
      h("div", { class: "field" }, "Label", h("input", { type: "text", name: "llabel", value: (l && l.label) || "" })),
      h("div", { class: "field" }, "URL", h("input", { type: "text", name: "lurl", value: (l && l.url) || "", placeholder: "https://… or /resources/…" })),
      h("button", { type: "button", dataset: { act: "remove" }, class: "danger" }, "Remove")));
  }
  $("addQuestion").addEventListener("click", () => addQuestionBlock(null));
  $("addLink").addEventListener("click", () => addLinkRow(null));
  $("questionList").addEventListener("click", (e) => { const b = e.target.closest("button[data-act=remove]"); if (b) b.closest(".qblock").remove(); });
  $("linkList").addEventListener("click", (e) => { const b = e.target.closest("button[data-act=remove]"); if (b) b.closest(".lrow").remove(); });
  function validateLinkUrl(url) {
    if (url.startsWith("/")) return !url.startsWith("//") && !url.includes("..");
    try { const u = new URL(url); return !u.username && !u.password && (u.protocol === "https:" || (u.protocol === "http:" && u.host === location.host)); } catch { return false; }
  }
  $("examForm").addEventListener("submit", async (e) => {
    e.preventDefault();
    const f = e.target;
    const body = {
      title: f.title.value.trim(), durationMinutes: Number(f.durationMinutes.value), releaseOnSubmit: f.releaseOnSubmit.value,
      isOpen: f.isOpen.checked, openToAll: f.openToAll.checked,
      openFrom: f.openFrom.value ? new Date(f.openFrom.value).toISOString() : null, openUntil: f.openUntil.value ? new Date(f.openUntil.value).toISOString() : null,
      assignedStudentCodes: f.assignedStudentCodes.value.split(/[\s,;]+/).map((s) => s.trim()).filter(Boolean),
      questions: [...$("questionList").children].map((b) => ({
        id: b.querySelector("[name=qid]").value.trim(), text: b.querySelector("[name=qtext]").value.trim(),
        options: b.querySelector("[name=qoptions]").value.split("\n").map((s) => s.trim()).filter(Boolean), answer: b.querySelector("[name=qanswer]").value.trim() || null
      })),
      allowedLinks: [...$("linkList").children].map((r) => ({ label: r.querySelector("[name=llabel]").value.trim(), url: r.querySelector("[name=lurl]").value.trim() })),
      policy: {}
    };
    if (!state.editingExam) body.code = f.code.value.trim().toUpperCase();
    body.kind = f.kind.value;
    if (body.kind === "external") {
      body.startUrl = f.startUrl.value.trim();
      body.allowedSites = f.allowedSites.value.split(/[\s,;]+/).map((x) => x.trim()).filter(Boolean);
      delete body.questions;
    }
    const ac = f.accessCode.value.trim();
    if (ac === "-") body.accessCode = null; else if (ac) body.accessCode = ac;
    for (const l of body.allowedLinks) if (!l.label || !validateLinkUrl(l.url)) { setStatus("examStatus", `Link "${l.label || "?"}": URL must be https:// or a /path on this server (no userinfo, no ..)`, "err"); return; }
    for (const k of P_BOOLS) body.policy[k] = f["p_" + k].checked;
    for (const k of P_NUMS) body.policy[k] = Number(f["p_" + k].value);
    body.policy.externalDisplayAction = f.p_externalDisplayAction.value;
    body.policy.minClientVersion = f.p_minClientVersion.value.trim();
    try {
      const saved = state.editingExam ? await api("PUT", `/api/v1/admin/exams/${encodeURIComponent(state.editingExam)}`, body) : await api("POST", "/api/v1/admin/exams", body);
      setStatus("examStatus", `Saved ${saved.code}.`, "ok");
      state.editingExam = saved.code; f.code.disabled = true; $("deleteExam").hidden = false; $("examEditorTitle").textContent = `Edit ${saved.code}`;
      loadExams();
    } catch (err) { setStatus("examStatus", err.message, "err"); }
  });
  $("deleteExam").addEventListener("click", async () => {
    if (!state.editingExam || !confirm(`Delete exam ${state.editingExam}? Only possible when it has no sessions.`)) return;
    const reason = askReason("delete exam"); if (reason === null) return;
    try { await api("DELETE", `/api/v1/admin/exams/${encodeURIComponent(state.editingExam)}`, { reason }); toast("Exam deleted"); $("examEditor").hidden = true; state.editingExam = null; loadExams(); }
    catch (err) { setStatus("examStatus", err.message, "err"); }
  });

  // ---- STUDENTS -----------------------------------------------------------------------------------
  async function loadStudents() {
    try {
      state.students = await api("GET", "/api/v1/admin/students");
      const tbody = clear($("studentRows"));
      $("studentCount").textContent = `(${state.students.length})`;
      if (!state.students.length) tbody.appendChild(h("tr", null, h("td", { colSpan: 4, class: "empty" }, "No students yet.")));
      for (const s of state.students) tbody.appendChild(h("tr", { dataset: { code: s.code } }, h("td", null, h("code", null, s.code)), h("td", { class: "wrap" }, s.name), h("td", null, s.email || "—"),
        h("td", { class: "actions" }, h("button", { dataset: { act: "rename" }, disabled: !canWrite() }, "Rename"), h("button", { dataset: { act: "delete" }, class: "danger", disabled: !canWrite() }, "Delete"))));
    } catch (e) { toast(e.message, true); }
  }
  $("studentRows").addEventListener("click", async (e) => {
    const btn = e.target.closest("button[data-act]"); if (!btn) return;
    const code = btn.closest("tr").dataset.code;
    try {
      if (btn.dataset.act === "rename") { const name = prompt("New name:", ""); if (!name || !name.trim()) return; await api("PUT", `/api/v1/admin/students/${encodeURIComponent(code)}`, { name: name.trim() }); }
      else { if (!confirm(`Delete student ${code}?`)) return; const reason = askReason("delete student"); if (reason === null) return; await api("DELETE", `/api/v1/admin/students/${encodeURIComponent(code)}`, { reason }); }
      loadStudents();
    } catch (err) { toast(err.message, true); }
  });
  $("studentForm").addEventListener("submit", async (e) => {
    e.preventDefault(); const f = e.target;
    try { await api("POST", "/api/v1/admin/students", { code: f.code.value.trim(), name: f.name.value.trim(), email: f.email.value.trim() }); f.reset(); setStatus("studentStatus", "Added.", "ok"); loadStudents(); }
    catch (err) { setStatus("studentStatus", err.message, "err"); }
  });
  $("csvForm").addEventListener("submit", async (e) => {
    e.preventDefault(); const f = e.target;
    try { const r = await api("POST", "/api/v1/admin/students/import", { csv: f.csv.value }); setStatus("csvStatus", `Created ${r.created}, updated ${r.updated}, skipped ${r.skipped.length}${r.skipped.length ? ": " + r.skipped.map((s) => `line ${s.line} (${s.reason})`).join("; ") : ""}`, r.skipped.length ? "err" : "ok"); loadStudents(); }
    catch (err) { setStatus("csvStatus", err.message, "err"); }
  });

  // ---- DEVICES ------------------------------------------------------------------------------------
  async function loadDevices() {
    $("tokenBtn").disabled = !isAdmin();
    try {
      const [tokens, devices] = await Promise.all([api("GET", "/api/v1/admin/enrollment-tokens"), api("GET", "/api/v1/admin/devices")]);
      const tb = clear($("tokenRows"));
      if (!tokens.length) tb.appendChild(h("tr", null, h("td", { colSpan: 7, class: "empty" }, "No enrollment tokens yet.")));
      const tcls = { active: "b-green", redeemed: "b-blue", expired: "b-amber", revoked: "b-red" };
      for (const t of tokens) tb.appendChild(h("tr", { dataset: { id: t.id } }, h("td", { class: "wrap" }, t.label || "—", h("br"), h("small", { class: "muted" }, "by " + (t.createdBy || "?"))), h("td", null, t.mode), h("td", null, badge(tcls[t.status], t.status)),
        h("td", null, fmtTime(t.createdAt)), h("td", null, fmtTime(t.expiresAt)), h("td", null, t.redeemedDeviceId ? h("code", null, t.redeemedDeviceId) : "—"),
        h("td", { class: "actions" }, h("button", { dataset: { act: "revoke" }, class: "danger", disabled: !isAdmin() || t.status !== "active" }, "Revoke"))));
      const db = clear($("deviceRows"));
      if (!devices.length) db.appendChild(h("tr", null, h("td", { colSpan: 8, class: "empty" }, "No devices enrolled yet.")));
      for (const d of devices) db.appendChild(h("tr", { dataset: { id: d.deviceId } }, h("td", null, h("code", null, d.deviceId), d.revokedAt ? [" ", badge("b-red", "revoked")] : null), h("td", { class: "wrap" }, d.deviceName || "—"), h("td", null, d.mode), h("td", null, `${d.platform || "?"} ${d.osVersion || ""}`),
        h("td", null, d.clientVersion || "—"), h("td", null, d.kioskVerified ? badge("b-green", "verified") : badge("b-grey", "no")), h("td", null, ago(d.lastSeenAt)),
        h("td", { class: "actions" }, h("button", { dataset: { act: d.kioskVerified ? "unverify" : "verify" }, disabled: !isAdmin() || !!d.revokedAt }, d.kioskVerified ? "Clear kiosk" : "Mark kiosk verified"), h("button", { dataset: { act: "revoke" }, class: "danger", disabled: !isAdmin() || !!d.revokedAt }, "Revoke"))));
    } catch (e) { toast(e.message, true); }
  }
  $("tokenForm").addEventListener("submit", async (e) => {
    e.preventDefault(); const f = e.target;
    try {
      const t = await api("POST", "/api/v1/admin/enrollment-tokens", { mode: f.mode.value, label: f.label.value.trim() });
      const box = clear($("tokenOnce")); box.hidden = false;
      const codeEl = h("code", null, t.token);
      const copy = h("button", { type: "button" }, "Copy");
      copy.addEventListener("click", () => navigator.clipboard.writeText(t.token).then(() => toast("Copied"), () => toast("Copy failed — select the text", true)));
      box.appendChild(h("div", { class: "token-once" }, h("strong", null, `Enrollment token (${t.mode}) — shown once, expires ${fmtTime(t.expiresAt)}: `), codeEl, " ", copy));
      f.label.value = ""; loadDevices();
    } catch (err) { toast(err.message, true); }
  });
  $("tokenRows").addEventListener("click", async (e) => {
    const btn = e.target.closest("button[data-act=revoke]"); if (!btn) return;
    const reason = askReason("revoke enrollment token"); if (reason === null) return;
    try { await api("DELETE", `/api/v1/admin/enrollment-tokens/${encodeURIComponent(btn.closest("tr").dataset.id)}`, { reason }); loadDevices(); } catch (err) { toast(err.message, true); }
  });
  $("deviceRows").addEventListener("click", async (e) => {
    const btn = e.target.closest("button[data-act]"); if (!btn) return;
    const id = btn.closest("tr").dataset.id; const act = btn.dataset.act;
    const reason = askReason(`${act} device ${id}`); if (reason === null) return;
    try {
      if (act === "revoke") await api("DELETE", `/api/v1/admin/devices/${encodeURIComponent(id)}`, { reason });
      else await api("PUT", `/api/v1/admin/devices/${encodeURIComponent(id)}`, { kioskVerified: act === "verify", reason });
      loadDevices();
    } catch (err) { toast(err.message, true); }
  });

  // ---- INCIDENTS ----------------------------------------------------------------------------------
  async function loadIncidents() {
    try {
      const all = $("incidentsAll").checked;
      const list = await api("GET", "/api/v1/admin/incidents" + (all ? "" : "?open=1"));
      const tb = clear($("incidentRows"));
      if (!list.length) tb.appendChild(h("tr", null, h("td", { colSpan: 10, class: "empty" }, all ? "No incidents." : "No open incidents.")));
      const sc = { high: "b-red", medium: "b-amber", low: "b-blue" };
      for (const i of list) tb.appendChild(h("tr", { dataset: { id: String(i.id) } }, h("td", null, String(i.id)), h("td", null, fmtTime(i.createdAt)), h("td", null, badge(sc[i.severity] || "b-grey", i.severity)), h("td", null, i.type),
        h("td", null, i.studentName ? `${i.studentName} (#${i.studentCode})` : "—"), h("td", null, i.examCode || "—"), h("td", null, i.sessionId ? h("code", null, i.sessionId.slice(0, 13) + "…") : "—"),
        h("td", { class: "wrap" }, i.detail || ""), h("td", { class: "wrap" }, i.resolvedAt ? `${i.resolvedBy}: ${i.resolutionNote}` : h("small", { class: "muted" }, "open")),
        h("td", null, i.resolvedAt ? null : h("button", { dataset: { act: "resolve" }, disabled: !canWrite() }, "Resolve…"))));
    } catch (e) { toast(e.message, true); }
  }
  $("incidentsAll").addEventListener("change", loadIncidents);
  $("incidentRows").addEventListener("click", async (e) => {
    const btn = e.target.closest("button[data-act=resolve]"); if (!btn) return;
    const note = prompt("Resolution note:", ""); if (!note || !note.trim()) return;
    try { await api("POST", `/api/v1/admin/incidents/${btn.closest("tr").dataset.id}/resolve`, { note: note.trim() }); loadIncidents(); } catch (err) { toast(err.message, true); }
  });

  // ---- AUDIT --------------------------------------------------------------------------------------
  async function loadAudit() {
    try {
      const list = await api("GET", "/api/v1/admin/audit?limit=300");
      const tb = clear($("auditRows"));
      if (!list.length) tb.appendChild(h("tr", null, h("td", { colSpan: 8, class: "empty" }, "Empty.")));
      for (const a of list) tb.appendChild(h("tr", null, h("td", null, String(a.id)), h("td", null, fmtTime(a.createdAt)), h("td", null, `${a.actorType}: ${a.actorName || a.actorId || "—"}`), h("td", null, a.action),
        h("td", null, a.targetType ? `${a.targetType} ${a.targetId || ""}` : "—"), h("td", { class: "wrap" }, a.reason || ""), h("td", { class: "wrap" }, a.detail ? JSON.stringify(a.detail) : ""), h("td", null, a.ip || "")));
    } catch (e) { toast(e.message, true); }
  }
  $("reloadAudit").addEventListener("click", loadAudit);

  // ---- STAFF --------------------------------------------------------------------------------------
  async function loadStaff() {
    if (!isAdmin()) return;
    try {
      const list = await api("GET", "/api/v1/admin/staff");
      const tb = clear($("staffRows"));
      for (const u of list) tb.appendChild(h("tr", { dataset: { id: u.id } }, h("td", null, u.username), h("td", null, u.role), h("td", null, u.disabled ? badge("b-red", "disabled") : badge("b-green", "active")), h("td", null, fmtTime(u.createdAt)),
        h("td", { class: "actions" }, h("button", { dataset: { act: "role" } }, "Role…"), h("button", { dataset: { act: "password" } }, "Password…"), h("button", { dataset: { act: u.disabled ? "enable" : "disable" } }, u.disabled ? "Enable" : "Disable"), h("button", { dataset: { act: "delete" }, class: "danger" }, "Delete"))));
    } catch (e) { toast(e.message, true); }
  }
  $("staffRows").addEventListener("click", async (e) => {
    const btn = e.target.closest("button[data-act]"); if (!btn) return;
    const id = btn.closest("tr").dataset.id; const act = btn.dataset.act;
    try {
      if (act === "role") { const role = prompt("Role (admin, teacher, reviewer):", "teacher"); if (!role) return; await api("PUT", `/api/v1/admin/staff/${id}`, { role: role.trim() }); }
      else if (act === "password") { const password = prompt("New password (10+ chars):", ""); if (!password) return; await api("PUT", `/api/v1/admin/staff/${id}`, { password }); toast("Password changed; their sessions were signed out."); }
      else if (act === "disable" || act === "enable") await api("PUT", `/api/v1/admin/staff/${id}`, { disabled: act === "disable" });
      else if (act === "delete") { if (!confirm("Delete this staff user?")) return; const reason = askReason("delete staff user"); if (reason === null) return; await api("DELETE", `/api/v1/admin/staff/${id}`, { reason }); }
      loadStaff();
    } catch (err) { toast(err.message, true); }
  });
  $("staffForm").addEventListener("submit", async (e) => {
    e.preventDefault(); const f = e.target;
    try { await api("POST", "/api/v1/admin/staff", { username: f.username.value.trim(), password: f.password.value, role: f.role.value }); f.reset(); setStatus("staffStatus", "Created.", "ok"); loadStaff(); }
    catch (err) { setStatus("staffStatus", err.message, "err"); }
  });

  // ---- boot ---------------------------------------------------------------------------------------
  (async () => {
    try { const me = await api("GET", "/api/v1/auth/me"); showApp(me.user); }
    catch { showLogin(); }
  })();

  // ---- EXAM RULE EXPLANATIONS (information buttons) --------------------------------------------
  // Plain-English help for every exam rule. Rendered with DOM methods (no HTML strings).
  const INFO = {
    examKind: {
      title: "Exam type",
      p: ["Questions in Avaibe Exam: you write the questions here, and students answer them inside the app.",
          "External exam website: the exam runs on another website, for example CAT4 on Testwise, MAP Growth, or a quiz in your school's learning platform. The app locks the computer and opens that website. The student signs in there and takes the test. Nothing about questions or scores is stored here.",
          "On an external exam the student presses \u201cI have finished\u201d when done. With Release on submit set to auto they are released at once; with teacher they wait for you."],
      tip: "Before the real exam, do one test run on a school computer. Pages that are blocked show up in the Events timeline, so you can add them to Allowed sites."
    },
    startUrl: {
      title: "Start address",
      p: ["The web address the locked window opens first, for example https://www.testwise.com/ or the page the exam provider gives you.",
          "It must start with https://. That website, and the same address with or without www., is always allowed."],
      tip: "Copy the address from the exam provider's instructions for students."
    },
    allowedSites: {
      title: "Allowed sites",
      p: ["Other websites the exam needs, one per line. Most exam websites use a separate sign-in page or a second address, and those must be listed or the student sees a blank or blocked page.",
          "Write only the site name, without https:// or a path. Use *. to allow a site and all its sub-addresses: *.testwise.com allows testwise.com, www.testwise.com and app.testwise.com.",
          "Everything not on the list is blocked, including links inside the exam website that lead elsewhere. Pictures and scripts that the exam website loads are always allowed."],
      tip: "If students sign in with Google or Microsoft, add only the sign-in address, such as accounts.google.com or login.microsoftonline.com.",
      warn: "Do not add *.google.com or *.microsoft.com: that would also open Gmail, Drive, Outlook and search. Wide patterns like *.com are refused."
    },
    policy: {
      title: "Exam rules",
      p: ["These rules control how the Avaibe Exam app locks the computer for this exam.",
          "They are saved with the exam. A student who starts the exam gets the rules as they were at that moment, so changes only affect students who start after you save."],
      tip: "For most exams the defaults are fine: block extra screens, no copy and paste, no printing."
    },
    requireSIP: {
      title: "Require SIP / Secure Boot",
      p: ["A built-in protection must be switched on. On a Mac it is System Integrity Protection (SIP). On Windows it is Secure Boot. Both stop the core of the operating system from being changed or replaced.",
          "They are on by default on almost every computer. When ticked, a computer that has it switched off cannot start the exam."],
      tip: "Tick for school computers. Leave off for students' own laptops unless you have checked them.",
      warn: "The app checks this itself, so a student with administrator rights could fake it."
    },
    requireMDM: {
      title: "Require MDM",
      p: ["The computer must be managed by the school's device management system (MDM), such as Microsoft Intune or Jamf.",
          "A managed computer is set up and controlled by school IT, which makes it much harder to tamper with."],
      tip: "Tick only when every exam computer is school-managed.",
      warn: "Students' own laptops are not managed, so they will not be able to start."
    },
    requireStandardAccount: {
      title: "Require standard account",
      p: ["The student must be signed in to the computer with a normal account, not an administrator account.",
          "Administrator accounts can change system settings and force programs to close, which makes cheating easier."],
      tip: "Tick for school computers and high-stakes exams.",
      warn: "Students' own laptops usually use administrator accounts, so they would be blocked."
    },
    requireAAC: {
      title: "Require AAC / kiosk",
      p: ["The exam only starts on computers where the operating system itself enforces the lockdown: Apple's Automatic Assessment Configuration (AAC) on Mac, or Windows Assigned Access (kiosk mode).",
          "This is the strongest protection. The server does not trust the app for this: an administrator must mark each computer as “Kiosk verified” in the Devices tab."],
      tip: "Use for high-stakes exams on school computers that IT has set up in kiosk mode.",
      warn: "The Mac app does not have Apple's approval for AAC yet, so with this ticked, exams will not start on Macs."
    },
    blockExternalDisplay: {
      title: "Block external display",
      p: ["Extra monitors or TVs are not allowed during the exam.",
          "While the exam is locked, every extra screen is covered in black. If a screen is connected during the exam, the app does what you choose in External display action."],
      tip: "Leave ticked. Untick only if students need a second screen, for example for accessibility.",
      warn: "When unticked, extra screens are not covered and no action is taken."
    },
    allowStudentReleaseCode: {
      title: "Allow student release code",
      p: ["Lets you unlock a student with a 6-digit code. In the Live tab, press Release code, then tell the student the code. They type it on their screen.",
          "Each code works once, only for that student's computer, and only for 60 seconds."],
      tip: "Handy when you are standing next to the student.",
      warn: "When off, the student cannot type a code. The only way to unlock them is Remote release in the Live tab."
    },
    allowClipboard: {
      title: "Allow clipboard",
      p: ["Allows copy and paste inside the exam: Ctrl+C and Ctrl+V on Windows, Cmd+C and Cmd+V on Mac."],
      tip: "Leave off, so students cannot paste answers they prepared before the exam."
    },
    allowPrinting: {
      title: "Allow printing",
      p: ["Allows printing the exam page."],
      tip: "Leave off, so the questions cannot be printed and taken away."
    },
    externalDisplayAction: {
      title: "External display action",
      p: ["What happens when an extra screen is connected. Only used when Block external display is ticked."],
      list: ["WARN: the student sees a warning and you see an alert. The exam continues.",
             "BLOCK_START: the exam cannot start while an extra screen is connected. If one is connected later, the same as WARN.",
             "PAUSE: the exam is hidden until the screen is unplugged. The timer keeps running.",
             "TERMINATE: the exam is submitted at once and the screen stays locked until you release the student.",
             "FLAG: nothing happens on the student's screen. It is only recorded for you to review."],
      tip: "BLOCK_START is a good default: it stops the problem before the exam begins."
    },
    heartbeatIntervalSeconds: {
      title: "Heartbeat interval",
      p: ["How often the app tells the server “I am still here and still locked”. Between 2 and 120 seconds.",
          "It also sets how quickly your actions reach the student: Remote release, Warn and Terminate arrive at the next heartbeat.",
          "If the app misses 3 heartbeats in a row during the exam, the Live tab marks the student as stale. A student still on the readiness screen, before pressing Start Exam, gets 5 minutes."],
      tip: "10 seconds suits most exams.",
      warn: "Lower is faster but puts more load on the server: 1,000 students every 10 seconds is about 100 requests per second."
    },
    eventFlushIntervalSeconds: {
      title: "Event flush interval",
      p: ["The app keeps a log of security events, such as a blocked shortcut, a switch to another app, or a screen being connected. This is how often it sends that log to the server. Between 1 and 300 seconds.",
          "Lower means the events show up in the Events timeline sooner. Some important events, such as an extra screen, are sent immediately anyway."],
      tip: "5 seconds suits most exams."
    },
    offlineGraceSeconds: {
      title: "Offline grace",
      p: ["What happens when a computer loses its connection to the server.",
          "If the app cannot reach the server for this long, it unlocks the student by itself, so nobody is trapped during a network outage. It sends the student's work when the connection comes back. Between 60 and 3600 seconds (1 hour)."],
      tip: "600 seconds (10 minutes) is a good balance.",
      warn: "Too short makes it easy to escape: a student could unplug the network and wait. Too long keeps students locked if the network really fails."
    },
    minClientVersion: {
      title: "Min client version",
      p: ["The oldest version of the Avaibe Exam app allowed to take this exam, for example 0.1.0.",
          "After you install a new version of the app on the computers, raise this so anyone still on an old version must update before starting."],
      warn: "The app reports its own version, so treat this as a guide rather than a security control."
    }
  };

  const infoPop = $("infoPop");
  let infoOpenFor = null;
  function closeInfo(returnFocus) {
    if (!infoOpenFor) return;
    const btn = infoOpenFor;
    infoOpenFor = null;
    infoPop.hidden = true;
    btn.setAttribute("aria-expanded", "false");
    if (returnFocus) btn.focus();
  }
  function openInfo(btn) {
    const data = INFO[btn.dataset.info];
    if (!data) return;
    if (infoOpenFor) closeInfo(false);
    $("infoPopTitle").textContent = data.title;
    const body = clear($("infoPopBody"));
    for (const para of data.p || []) body.appendChild(h("p", null, para));
    if (data.list) body.appendChild(h("ul", null, ...data.list.map((t) => h("li", null, t))));
    if (data.tip) body.appendChild(h("p", { class: "tip" }, data.tip));
    if (data.warn) body.appendChild(h("p", { class: "warn" }, data.warn));
    infoPop.hidden = false;
    const r = btn.getBoundingClientRect();
    const width = infoPop.offsetWidth;
    const left = Math.max(12, Math.min(window.scrollX + r.left - 12, window.scrollX + document.documentElement.clientWidth - width - 12));
    infoPop.style.left = left + "px";
    const height = infoPop.offsetHeight;
    const roomBelow = document.documentElement.clientHeight - r.bottom;
    const above = roomBelow < height + 16 && r.top > height + 16;
    infoPop.style.top = (window.scrollY + (above ? r.top - height - 8 : r.bottom + 8)) + "px";
    btn.setAttribute("aria-expanded", "true");
    infoOpenFor = btn;
    infoPop.querySelector(".info-close").focus();
  }
  document.addEventListener("click", (e) => {
    const btn = e.target.closest("button.info");
    if (btn) {
      e.preventDefault();   // never toggle the checkbox in the same label
      e.stopPropagation();
      if (infoOpenFor === btn) closeInfo(true); else openInfo(btn);
      return;
    }
    if (e.target.closest(".info-close")) { closeInfo(true); return; }
    if (infoOpenFor && !infoPop.contains(e.target)) closeInfo(false);
  });
  document.addEventListener("keydown", (e) => { if (e.key === "Escape") closeInfo(true); });
  window.addEventListener("resize", () => closeInfo(false));
})();
