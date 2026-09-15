// Avaibe Exam page (protocol v2). Authenticated by the httpOnly avaibe_exam cookie set by /exam/launch.
// Works unchanged inside both native clients (bridge, CONTRACT §5 / §9.4) and in browser test mode.
(function () {
  "use strict";

  const sessionId = (location.pathname.match(/\/exam\/([^/]+)/) || [])[1] || "";
  const api = (p) => `${location.origin}${p}`;
  const $ = (id) => document.getElementById(id);

  const state = {
    sessionId,
    questions: [],
    answers: {},
    pendingSave: {},
    saveTimer: null,
    index: 0,
    remaining: null,
    lastTick: null,
    lockdownMode: (window.LockdownNative && window.LockdownNative.lockdownMode) || null,
    submitted: false,
    releaseMode: "teacher",
    bridge: !!(window.webkit && window.webkit.messageHandlers && window.webkit.messageHandlers.lockdown),
    seq: 0
  };

  async function call(method, path, body) {
    const res = await fetch(api(path), {
      method,
      credentials: "same-origin",
      headers: body ? { "Content-Type": "application/json" } : {},
      body: body ? JSON.stringify(body) : undefined
    });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) throw new Error((data.error && data.error.message) || `HTTP ${res.status}`);
    return data;
  }

  // Native bridge (§5). LockdownBridge must exist before READY.
  window.LockdownBridge = {
    onMessage(msg) {
      try {
        if (!msg || typeof msg !== "object") return;
        const payload = msg.payload || {};
        switch (msg.type) {
          case "SESSION_STATUS": applySessionStatus(payload); break;
          case "WARNING": toast(payload.message || "Security warning", "warning"); break;
          case "EXIT_DENIED": toast("Exit denied: " + (payload.reason || "not authorised"), "error"); break;
          case "LOCKDOWN_STATE":
            state.lockdownMode = payload.engaged === false ? "none" : (payload.lockdownMode || state.lockdownMode);
            renderBadge();
            break;
          default: console.log("[bridge] unhandled message", msg);
        }
      } catch (e) { console.error("[bridge] onMessage error", e); }
    }
  };

  function sendNative(type, payload, id) {
    const handler = window.webkit && window.webkit.messageHandlers && window.webkit.messageHandlers.lockdown;
    const message = { type, id: id || `web_${++state.seq}`, payload: payload || {} };
    if (!handler) { console.log("[bridge] (no native) would send", message); return false; }
    try { handler.postMessage(message); return true; }
    catch (e) { console.error("[bridge] postMessage failed", e); return false; }
  }

  function applySessionStatus(p) {
    if (p.examTitle) $("examTitle").textContent = p.examTitle;
    if (p.studentName) $("studentName").textContent = p.studentName;
    if (typeof p.remainingSeconds === "number") { state.remaining = Math.max(0, Math.min(86400, p.remainingSeconds)); state.lastTick = Date.now(); }
    if (p.lockdownMode) state.lockdownMode = p.lockdownMode;
    if (typeof p.online === "boolean") {
      $("onlineDot").className = "dot " + (p.online ? "online" : "offline");
      $("onlineDot").title = p.online ? "Online" : "Offline";
    }
    renderBadge();
    renderCountdown();
  }

  // Server status (cookie-authenticated). Source of allowedLinks; in browser test mode also the source of time.
  async function fetchStatus({ applyTime }) {
    try {
      const s = await call("GET", "/exam/api/status");
      state.releaseMode = s.releaseOnSubmit || "teacher";
      renderResources(s.allowedLinks || []);
      if (applyTime) {
        applySessionStatus({
          studentName: s.studentName, examTitle: s.examTitle, remainingSeconds: s.remainingSeconds,
          lockdownMode: (s.clientReported && s.clientReported.lockdownMode) || "none", online: true
        });
      } else {
        if (s.examTitle) $("examTitle").textContent = s.examTitle;
        if (s.studentName && !$("studentName").textContent) $("studentName").textContent = s.studentName;
        if (state.remaining === null && typeof s.remainingSeconds === "number") applySessionStatus({ remainingSeconds: s.remainingSeconds });
      }
      if (s.submittedAt && !state.submitted) showDone(false, s.releaseOnSubmit, s.status);
      return s;
    } catch (e) {
      $("onlineDot").className = "dot offline";
      if (applyTime) toast("Could not reach the exam service: " + e.message, "error");
      return null;
    }
  }

  const BADGES = { aac: "Secure · AAC", "assigned-access": "Secure · Kiosk", "kiosk-fallback": "Kiosk fallback", none: "Not locked" };
  function renderBadge() {
    const b = $("lockBadge");
    const mode = state.lockdownMode;
    b.className = "badge " + (BADGES[mode] ? mode : "unknown");
    b.textContent = BADGES[mode] || "Checking";
  }

  function renderResources(links) {
    const box = $("resources");
    const holder = $("resourceLinks");
    holder.textContent = "";
    if (!Array.isArray(links) || !links.length) { box.hidden = true; return; }
    links.forEach((l, i) => {
      if (!l || typeof l.url !== "string" || typeof l.label !== "string") return;
      if (i) holder.appendChild(document.createTextNode(" · "));
      const a = document.createElement("a");
      a.href = l.url;
      a.textContent = l.label;
      a.rel = "noopener";
      holder.appendChild(a);
    });
    box.hidden = !holder.childNodes.length;
  }

  function fmt(sec) {
    sec = Math.max(0, Math.floor(sec));
    const h = Math.floor(sec / 3600), m = Math.floor((sec % 3600) / 60), s = sec % 60;
    const mm = String(m).padStart(2, "0"), ss = String(s).padStart(2, "0");
    return h > 0 ? `${h}:${mm}:${ss}` : `${mm}:${ss}`;
  }

  function currentRemaining() {
    if (state.remaining === null) return null;
    return Math.max(0, state.remaining - (Date.now() - state.lastTick) / 1000);
  }

  function renderCountdown() {
    const el = $("countdown");
    const r = currentRemaining();
    if (r === null) { el.textContent = "--:--"; el.className = "countdown"; return; }
    el.textContent = fmt(r);
    el.className = "countdown" + (r <= 60 ? " critical" : r <= 300 ? " low" : "");
    if (r <= 0 && !state.submitted && state.questions.length) timeUp();
  }

  function renderQuestion() {
    const q = state.questions[state.index];
    if (!q) return;
    $("progressText").textContent = `Question ${state.index + 1} of ${state.questions.length}`;
    $("questionText").textContent = q.text;
    const opts = $("options");
    opts.textContent = "";
    q.options.forEach((opt, i) => {
      const label = document.createElement("label");
      label.className = "option" + (state.answers[q.id] === opt ? " selected" : "");
      const input = document.createElement("input");
      input.type = "radio"; input.name = "answer"; input.value = opt; input.id = `opt_${i}`;
      input.checked = state.answers[q.id] === opt;
      input.addEventListener("change", () => saveAnswer(q, opt));
      const span = document.createElement("span");
      span.textContent = opt;
      label.append(input, span);
      opts.appendChild(label);
    });
    $("prevBtn").disabled = state.index === 0;
    $("nextBtn").disabled = state.index === state.questions.length - 1;
    renderPalette();
    renderSummary();
  }

  function renderPalette() {
    const pal = $("palette");
    pal.textContent = "";
    state.questions.forEach((q, i) => {
      const b = document.createElement("button");
      b.type = "button";
      b.textContent = String(i + 1);
      b.className = (state.answers[q.id] ? "answered" : "") + (i === state.index ? " current" : "");
      b.title = `Go to question ${i + 1}`;
      b.addEventListener("click", () => { state.index = i; renderQuestion(); });
      pal.appendChild(b);
    });
  }

  function renderSummary() {
    const answered = Object.keys(state.answers).length;
    $("summary").textContent = `${answered} of ${state.questions.length} answered`;
  }

  function setSaveState(text, cls) { const el = $("saveState"); el.textContent = text; el.className = "save-state " + (cls || ""); }

  function saveAnswer(q, opt) {
    state.answers[q.id] = opt;
    state.pendingSave[q.id] = opt;
    setSaveState("Saving…", "");
    sendNative("REPORT_EVENT", { type: "ANSWER_SAVED", severity: "info", metadata: { question: q.id } });
    renderQuestion();
    clearTimeout(state.saveTimer);
    state.saveTimer = setTimeout(flushAnswers, 400);
  }

  async function flushAnswers() {
    const batch = state.pendingSave;
    if (!Object.keys(batch).length) return;
    state.pendingSave = {};
    try {
      await call("PUT", "/exam/api/answers", { answers: batch });
      setSaveState("Saved " + new Date().toLocaleTimeString(), "saved");
    } catch (e) {
      state.pendingSave = { ...batch, ...state.pendingSave };
      setSaveState("Not saved yet — retrying (" + e.message + ")", "error");
      clearTimeout(state.saveTimer);
      state.saveTimer = setTimeout(flushAnswers, 3000);
    }
  }

  function showDone(auto, release, status) {
    state.submitted = true;
    $("confirmModal").hidden = true;
    $("loading").hidden = true;
    $("examView").hidden = true;
    $("doneView").hidden = false;
    if (auto) { $("doneTitle").textContent = "Time is up"; $("doneText").textContent = "Your exam was submitted automatically."; }
    if (status === "released") $("doneRelease").textContent = "Lockdown has been released.";
    else if (release === "auto") $("doneRelease").textContent = "Lockdown will be released shortly. Please wait for the exit screen.";
    else $("doneRelease").textContent = "Submitted. Waiting for your teacher to release you.";
  }

  async function submitExam(auto) {
    if (state.submitted) return;
    state.submitted = true;
    $("confirmModal").hidden = true;
    $("submitBtn").disabled = true;
    try {
      clearTimeout(state.saveTimer);
      const answers = { ...state.answers };
      state.pendingSave = {};
      const data = await call("POST", "/exam/api/submit", { answers });
      showDone(auto, data.release, data.status);
      sendNative("SUBMIT_COMPLETE", {});
      if (!state.bridge) toast("Submitted. (Browser test mode: no native exit overlay.)", "info");
    } catch (e) {
      state.submitted = false;
      $("submitBtn").disabled = false;
      toast("Submit failed: " + e.message, "error");
    }
  }

  function timeUp() { submitExam(true); }

  function toast(text, kind, ttl) {
    const el = document.createElement("div");
    el.className = "toast " + (kind || "info");
    el.textContent = text;
    const close = document.createElement("button");
    close.type = "button"; close.setAttribute("aria-label", "Dismiss"); close.textContent = "×";
    close.addEventListener("click", () => el.remove());
    el.appendChild(close);
    $("toasts").appendChild(el);
    setTimeout(() => el.remove(), ttl || (kind === "warning" ? 15000 : 8000));
  }

  // Lockdown hygiene inside the page.
  document.addEventListener("contextmenu", (e) => e.preventDefault());
  ["dragstart", "drop", "dragover"].forEach((t) => document.addEventListener(t, (e) => e.preventDefault()));
  document.addEventListener("keydown", (e) => {
    const mod = e.metaKey || e.ctrlKey;
    if (mod && ["p", "s", "u", "o"].includes(e.key.toLowerCase())) { e.preventDefault(); e.stopPropagation(); }
    if (e.key === "F12") e.preventDefault();
  }, true);
  window.addEventListener("beforeprint", (e) => e.preventDefault());

  $("prevBtn").addEventListener("click", () => { if (state.index > 0) { state.index--; renderQuestion(); } });
  $("nextBtn").addEventListener("click", () => { if (state.index < state.questions.length - 1) { state.index++; renderQuestion(); } });
  $("submitBtn").addEventListener("click", () => {
    const unanswered = state.questions.length - Object.keys(state.answers).length;
    $("confirmText").textContent = unanswered > 0
      ? `You have ${unanswered} unanswered question${unanswered === 1 ? "" : "s"}. You will not be able to change your answers after submitting.`
      : "You will not be able to change your answers after submitting.";
    $("confirmModal").hidden = false;
  });
  $("cancelSubmit").addEventListener("click", () => { $("confirmModal").hidden = true; });
  $("confirmSubmit").addEventListener("click", () => submitExam(false));
  $("requestExit").addEventListener("click", (e) => {
    e.preventDefault();
    if (!sendNative("REQUEST_EXIT", { reason: state.submitted ? "submitted" : "student-request" })) {
      toast("Request exit only works inside Avaibe Exam (native overlay).", "info");
    }
  });

  setInterval(renderCountdown, 1000);
  setInterval(() => { if (state.bridge) sendNative("GET_SESSION_STATUS", {}); fetchStatus({ applyTime: !state.bridge }); }, 30000);

  async function boot() {
    if (!sessionId) { $("loadingText").textContent = "No session id in the URL. Expected /exam/<sessionId>."; return; }
    renderBadge();
    if (!state.bridge) $("browserModeBanner").hidden = false;
    if (state.bridge) sendNative("READY", {});

    try {
      $("loadingText").textContent = "Fetching session…";
      const status = await fetchStatus({ applyTime: !state.bridge });
      if (!status) throw new Error("session status unavailable");
      if (status.sessionId && status.sessionId !== sessionId) throw new Error("this page belongs to a different session");

      $("loadingText").textContent = "Loading questions…";
      const exam = await call("GET", "/exam/api/questions");
      state.questions = exam.questions || [];
      state.answers = exam.answers && typeof exam.answers === "object" ? exam.answers : {};
      if (!$("examTitle").textContent || $("examTitle").textContent === "Loading exam…") $("examTitle").textContent = exam.title;
      if (state.remaining === null) { state.remaining = exam.durationMinutes * 60; state.lastTick = Date.now(); }
      if (state.submitted) return;

      $("loading").hidden = true;
      $("examView").hidden = false;
      renderQuestion();
      renderCountdown();
    } catch (e) {
      $("loadingText").textContent = "Could not load the exam: " + e.message;
      toast("Could not load the exam: " + e.message, "error", 20000);
    }
  }

  boot();
})();
