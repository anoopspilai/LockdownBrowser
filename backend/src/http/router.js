// Method + pattern router with streaming body limits, safe URL decoding, prototype-key rejection,
// consistent error envelopes and security headers on every response. No CORS.
import { HttpError, isObject } from "../util.js";

const FORBIDDEN_KEYS = new Set(["__proto__", "constructor", "prototype"]);

export class Router {
  constructor({ log, config }) {
    this.routes = [];
    this.log = log;
    this.config = config;
  }

  /**
   * @param {string} method
   * @param {string} pattern e.g. "/api/v1/sessions/:id/heartbeat"; "*" at the end matches the rest.
   * @param {(ctx) => any} handler receives { req, res, params, body, query, url, pathname, ip }
   * @param {object} [opts] { maxBody?: number, raw?: boolean (handler writes the response itself) }
   */
  add(method, pattern, handler, opts = {}) {
    const keys = [];
    let src = pattern.replace(/[.*+?^${}()|[\]\\]/g, (c) => (c === "*" ? "*" : "\\" + c));
    src = src.replace(/\/:([a-zA-Z]+)/g, (_, k) => {
      keys.push(k);
      return "/([^/]+)";
    });
    if (src.endsWith("*")) src = src.slice(0, -1) + "(.*)";
    else src += "/?";
    this.routes.push({ method, re: new RegExp("^" + src + "$"), keys, handler, opts });
    return this;
  }

  get(p, h, o) {
    return this.add("GET", p, h, o);
  }
  post(p, h, o) {
    return this.add("POST", p, h, o);
  }
  put(p, h, o) {
    return this.add("PUT", p, h, o);
  }
  patch(p, h, o) {
    return this.add("PATCH", p, h, o);
  }
  delete(p, h, o) {
    return this.add("DELETE", p, h, o);
  }

  match(method, pathname) {
    let pathMatched = false;
    for (const r of this.routes) {
      const m = pathname.match(r.re);
      if (!m) continue;
      pathMatched = true;
      if (r.method !== method) continue;
      const params = Object.fromEntries(r.keys.map((k, i) => [k, m[i + 1]]));
      if (r.re.source.endsWith("(.*)$")) params.rest = m[m.length - 1];
      return { route: r, params };
    }
    return { route: null, pathMatched };
  }
}

export function securityHeaders(res, { tls } = {}) {
  res.setHeader("X-Content-Type-Options", "nosniff");
  res.setHeader("X-Frame-Options", "DENY");
  res.setHeader("Referrer-Policy", "no-referrer");
  res.setHeader("Permissions-Policy", "camera=(), microphone=(), display-capture=(), geolocation=()");
  res.setHeader("Cross-Origin-Resource-Policy", "same-origin");
  res.setHeader("Cache-Control", "no-store");
  if (!res.hasHeader("Content-Security-Policy")) res.setHeader("Content-Security-Policy", "default-src 'self'");
  if (tls) res.setHeader("Strict-Transport-Security", "max-age=31536000; includeSubDomains");
}

export function sendJson(res, status, payload) {
  const data = Buffer.from(JSON.stringify(payload));
  if (!res.headersSent) {
    res.writeHead(status, { "Content-Type": "application/json; charset=utf-8", "Content-Length": data.length });
  }
  res.end(data);
}

export function sendHtml(res, status, html, csp) {
  const data = Buffer.from(html, "utf8");
  if (csp) res.setHeader("Content-Security-Policy", csp);
  res.writeHead(status, { "Content-Type": "text/html; charset=utf-8", "Content-Length": data.length });
  res.end(data);
}

export function errorPayload(err) {
  if (err instanceof HttpError) return { status: err.status, body: { error: { code: err.code, message: err.message }, ...(err.extra || {}) } };
  return { status: 500, body: { error: { code: "INTERNAL_ERROR", message: "Unexpected server error" } } };
}

/** Decodes a request path safely: bad %-sequences, NUL bytes and dot segments become a 400. */
export function safePathname(rawUrl) {
  let url;
  try {
    url = new URL(rawUrl, "http://localhost");
  } catch {
    throw new HttpError(400, "INVALID_URL", "Malformed request URL");
  }
  let pathname;
  try {
    pathname = decodeURIComponent(url.pathname);
  } catch {
    throw new HttpError(400, "INVALID_URL", "Malformed percent-encoding in URL");
  }
  if (pathname.includes("\0") || pathname.split("/").some((seg) => seg === ".." || seg === ".")) {
    throw new HttpError(400, "INVALID_URL", "Invalid path");
  }
  if (pathname.length > 2048) throw new HttpError(414, "URI_TOO_LONG", "Path too long");
  return { pathname, url };
}

/** Throws if any key anywhere in the parsed JSON is a prototype-pollution vector. Used as a JSON.parse reviver. */
function reviver(key, value) {
  if (FORBIDDEN_KEYS.has(key)) throw new HttpError(400, "INVALID_JSON", `Forbidden key "${key}" in request body`);
  return value;
}

/** Reads a JSON body with a streaming size cap. Empty body => {}. Non-object JSON => 400. */
export function readJsonBody(req, maxBytes) {
  return new Promise((resolve, reject) => {
    const declared = Number(req.headers["content-length"]);
    if (Number.isFinite(declared) && declared > maxBytes) {
      reject(new HttpError(413, "PAYLOAD_TOO_LARGE", `Body exceeds ${maxBytes} bytes`));
      req.resume();
      return;
    }
    const chunks = [];
    let size = 0;
    let done = false;
    const finish = (fn) => {
      if (done) return;
      done = true;
      fn();
    };
    req.on("data", (c) => {
      size += c.length;
      if (size > maxBytes) {
        finish(() => reject(new HttpError(413, "PAYLOAD_TOO_LARGE", `Body exceeds ${maxBytes} bytes`)));
        req.destroy();
        return;
      }
      chunks.push(c);
    });
    req.on("end", () => {
      finish(() => {
        const raw = Buffer.concat(chunks).toString("utf8").trim();
        if (!raw) return resolve({});
        let parsed;
        try {
          parsed = JSON.parse(raw, reviver);
        } catch (e) {
          return reject(e instanceof HttpError ? e : new HttpError(400, "INVALID_JSON", "Request body is not valid JSON"));
        }
        if (!isObject(parsed)) return reject(new HttpError(400, "INVALID_REQUEST", "Request body must be a JSON object"));
        resolve(parsed);
      });
    });
    req.on("error", () => finish(() => reject(new HttpError(400, "BAD_REQUEST", "Request stream error"))));
    req.on("aborted", () => finish(() => reject(new HttpError(400, "BAD_REQUEST", "Request aborted"))));
  });
}

export function parseCookies(header) {
  const out = {};
  if (typeof header !== "string") return out;
  for (const part of header.split(";")) {
    const i = part.indexOf("=");
    if (i < 0) continue;
    const k = part.slice(0, i).trim();
    const v = part.slice(i + 1).trim();
    if (k && !(k in out)) out[k] = v;
  }
  return out;
}

export function clientIp(req, trustProxy) {
  if (trustProxy) {
    const xf = req.headers["x-forwarded-for"];
    if (typeof xf === "string" && xf) return xf.split(",")[0].trim().slice(0, 64);
  }
  return (req.socket?.remoteAddress || "unknown").slice(0, 64);
}

/** Creates the request listener. Every handler is guarded; the process never dies on a bad request. */
export function createRequestListener({ router, log, config, onStatic }) {
  return async function listener(req, res) {
    const started = process.hrtime.bigint();
    let pathname = req.url || "/";
    securityHeaders(res, { tls: config.tls });
    res.on("finish", () => {
      const ms = Number(process.hrtime.bigint() - started) / 1e6;
      // Query strings are never logged (the launch token travels in one).
      log.info("request", { method: req.method, path: pathname.slice(0, 200), status: res.statusCode, ms: Math.round(ms * 10) / 10 });
    });
    try {
      const parsed = safePathname(req.url || "/");
      pathname = parsed.pathname;
      const ip = clientIp(req, config.trustProxy);
      const { route, params, pathMatched } = router.match(req.method, pathname);
      if (!route) {
        if ((req.method === "GET" || req.method === "HEAD") && onStatic) {
          const served = await onStatic({ req, res, pathname });
          if (served) return;
        }
        if (pathMatched) throw new HttpError(405, "METHOD_NOT_ALLOWED", `Method ${req.method} not allowed for ${pathname}`);
        throw new HttpError(404, "NOT_FOUND", `No route for ${req.method} ${pathname}`);
      }
      const hasBody = !["GET", "HEAD", "OPTIONS"].includes(req.method);
      const body = hasBody ? await readJsonBody(req, route.opts.maxBody || config.maxBodyBytes) : {};
      const result = await route.handler({ req, res, params, body, query: parsed.url.searchParams, url: parsed.url, pathname, ip });
      if (route.opts.raw) return; // handler wrote the response itself
      if (res.headersSent) return;
      sendJson(res, 200, result ?? {});
    } catch (err) {
      const { status, body } = errorPayload(err);
      if (status >= 500) log.error("handler error", { method: req.method, path: pathname, error: String(err?.stack || err) });
      if (res.headersSent) {
        res.end();
        return;
      }
      try {
        sendJson(res, status, body);
      } catch {
        res.destroy();
      }
    }
  };
}
