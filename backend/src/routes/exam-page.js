// Exam web page: /exam/launch?lt= (one-time launch token -> httpOnly cookie), /exam/:sessionId (page), /exam/api/* (cookie auth).
import { HttpError, SESSION_ID_RE } from "../util.js";
import { sendFile, CSP_EXAM } from "../http/static.js";
import { sendHtml } from "../http/router.js";
import { requireExamCookie, examCookie } from "../auth/session.js";
import * as sessions from "../services/sessions.js";
import * as submit from "../services/submit.js";
import { questionsPayload, statusPayload } from "./sessions.js";

const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);

function errorPage(title, message) {
  return `<!doctype html><html lang="en"><head><meta charset="utf-8"><title>${esc(title)}</title><link rel="stylesheet" href="/exam.css"></head>
<body><main class="state card" style="margin:48px auto;max-width:520px"><h2>${esc(title)}</h2><p>${esc(message)}</p><p>Return to the Avaibe Exam app and start the exam again.</p></main></body></html>`;
}

export function registerExamPageRoutes(router, ctx) {
  router.get(
    "/exam/launch",
    ({ res, query }) => {
      try {
        const { sessionId, cookieToken } = sessions.redeemLaunchToken(ctx, query.get("lt"));
        res.setHeader("Set-Cookie", examCookie(ctx.config, sessionId, cookieToken));
        res.writeHead(302, { Location: `/exam/${sessionId}` });
        res.end();
      } catch (e) {
        const status = e instanceof HttpError ? e.status : 500;
        sendHtml(res, status, errorPage("Exam link not valid", e instanceof HttpError ? e.message : "Unexpected error"), CSP_EXAM);
      }
    },
    { raw: true }
  );

  router.get(
    "/exam/:sessionId",
    ({ res, params }) => {
      if (params.sessionId === "api" || !SESSION_ID_RE.test(params.sessionId)) throw new HttpError(404, "NOT_FOUND", "Not found");
      try {
        requireExamCookie(ctx, { headers: res.req.headers }, params.sessionId);
      } catch (e) {
        sendHtml(res, e.status || 401, errorPage("Exam page locked", e.message), CSP_EXAM);
        return;
      }
      sendFile(res, ctx.config.publicDir, "exam.html", { csp: CSP_EXAM });
    },
    { raw: true }
  );

  const withCookie = (fn) => ({ req, body, query }) => {
    const session = requireExamCookie(ctx, req);
    return fn(session, body, query);
  };

  router.get("/exam/api/status", withCookie((s) => statusPayload(ctx, s)));
  router.get("/exam/api/questions", withCookie((s) => questionsPayload(ctx, s)));
  router.put("/exam/api/answers", withCookie((s, body) => submit.saveAnswers(ctx, s, body.answers, { source: "page" })));
  router.post("/exam/api/submit", withCookie((s, body) => submit.submit(ctx, s, body, { source: "page" })));
}
