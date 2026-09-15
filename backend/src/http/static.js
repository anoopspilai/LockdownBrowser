// Path-traversal-safe static file serving for public/ with per-page CSP.
import fs from "node:fs";
import path from "node:path";
import { HttpError } from "../util.js";

const MIME = {
  ".html": "text/html; charset=utf-8",
  ".css": "text/css; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".json": "application/json; charset=utf-8",
  ".svg": "image/svg+xml",
  ".png": "image/png",
  ".ico": "image/x-icon",
  ".txt": "text/plain; charset=utf-8",
  ".pdf": "application/pdf"
};

export const CSP_EXAM = "default-src 'self'; connect-src 'self'; img-src 'self' data:; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
export const CSP_ADMIN = "default-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
export const CSP_RESOURCE = "default-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; base-uri 'none'; frame-ancestors 'none'";

/** Resolves relPath inside publicDir or throws 404. Never follows ".." out of the root. */
export function resolvePublic(publicDir, relPath) {
  const abs = path.normalize(path.join(publicDir, relPath));
  if (!abs.startsWith(publicDir + path.sep)) throw new HttpError(404, "NOT_FOUND", "Not found");
  return abs;
}

export function sendFile(res, publicDir, relPath, { csp, status = 200 } = {}) {
  const abs = resolvePublic(publicDir, relPath);
  let data;
  try {
    const st = fs.statSync(abs);
    if (!st.isFile()) throw new Error("not a file");
    data = fs.readFileSync(abs);
  } catch {
    throw new HttpError(404, "NOT_FOUND", "Not found");
  }
  if (csp) res.setHeader("Content-Security-Policy", csp);
  res.writeHead(status, {
    "Content-Type": MIME[path.extname(abs).toLowerCase()] || "application/octet-stream",
    "Content-Length": data.length
  });
  res.end(data);
  return true;
}

/**
 * Static handler for unmatched GET/HEAD paths. Serves:
 *   /               -> redirect to /admin/
 *   /admin, /admin/ -> public/admin/index.html
 *   /admin/*        -> public/admin/*
 *   /resources/*    -> public/resources/*
 *   /exam.js, /exam.css -> public/*
 */
export function createStaticHandler(publicDir) {
  return function staticHandler({ res, pathname }) {
    if (pathname === "/") {
      res.writeHead(302, { Location: "/admin/" });
      res.end();
      return true;
    }
    if (pathname === "/admin" || pathname === "/admin/") return sendFile(res, publicDir, "admin/index.html", { csp: CSP_ADMIN });
    if (pathname.startsWith("/admin/")) return sendFile(res, publicDir, pathname.slice(1), { csp: CSP_ADMIN });
    if (pathname.startsWith("/resources/")) return sendFile(res, publicDir, pathname.slice(1), { csp: CSP_RESOURCE });
    if (pathname === "/exam.js" || pathname === "/exam.css" || pathname === "/favicon.ico") return sendFile(res, publicDir, pathname.slice(1), { csp: CSP_EXAM });
    return false;
  };
}
