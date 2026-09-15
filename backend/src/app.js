// Assembles DB, signer, router and HTTP(S) server. Used by server.js and by tests (in-memory DB, port 0).
import http from "node:http";
import https from "node:https";
import fs from "node:fs";
import { loadConfig } from "./config.js";
import { createLogger } from "./log.js";
import { openDb } from "./db.js";
import { loadSigner } from "./crypto/signer.js";
import { Router, createRequestListener } from "./http/router.js";
import { createStaticHandler } from "./http/static.js";
import * as audit from "./services/audit.js";
import * as exams from "./services/exams.js";
import * as sessions from "./services/sessions.js";
import { purgeExpiredStaffSessions, createStaffUser } from "./auth/staff.js";
import { registerPublicRoutes } from "./routes/public.js";
import { registerDeviceRoutes } from "./routes/device.js";
import { registerSessionRoutes } from "./routes/sessions.js";
import { registerExamPageRoutes } from "./routes/exam-page.js";
import { registerAuthRoutes } from "./routes/auth.js";
import { registerAdminRoutes } from "./routes/admin.js";

export function createContext({ config = loadConfig(), log = createLogger(config.logLevel), db = openDb(config.dbPath) } = {}) {
  const signer = loadSigner(db);
  const ctx = { config, log, db, signer, audit };
  ctx.defaultPolicy = () => exams.defaultPolicy(ctx);
  return ctx;
}

export function buildRouter(ctx) {
  const router = new Router({ log: ctx.log, config: ctx.config });
  registerPublicRoutes(router, ctx);
  registerDeviceRoutes(router, ctx);
  registerSessionRoutes(router, ctx);
  registerExamPageRoutes(router, ctx);
  registerAuthRoutes(router, ctx);
  registerAdminRoutes(router, ctx);
  return router;
}

/** Creates the admin user on first start if no staff exist. Returns the generated password when one was generated. */
export async function ensureAdmin(ctx, { password = ctx.config.adminPassword, username = "admin" } = {}) {
  const n = ctx.db.prepare("SELECT COUNT(*) AS n FROM staff_users").get().n;
  if (n > 0) return null;
  let generated = null;
  if (!password) {
    const { randomBytes } = await import("node:crypto");
    generated = randomBytes(12).toString("base64url");
    password = generated;
  }
  await createStaffUser(ctx.db, { username, password, role: "admin" });
  audit.record(ctx, { actorType: "system", action: "staff.bootstrap", targetType: "staff", targetId: username });
  return generated;
}

export function createApp(options = {}) {
  const ctx = createContext(options);
  const { config, log } = ctx;
  const router = buildRouter(ctx);
  const listener = createRequestListener({ router, log, config, onStatic: createStaticHandler(config.publicDir) });
  const server = config.tls
    ? https.createServer({ cert: fs.readFileSync(config.tlsCert), key: fs.readFileSync(config.tlsKey), minVersion: "TLSv1.2" }, listener)
    : http.createServer(listener);
  server.headersTimeout = 15_000;
  server.requestTimeout = 30_000;
  server.keepAliveTimeout = 5_000;
  server.maxHeadersCount = 64;

  const timers = [];
  function startBackgroundJobs() {
    timers.push(
      setInterval(() => {
        try {
          sessions.sweepStale(ctx);
        } catch (e) {
          log.error("stale sweep failed", { error: String(e) });
        }
      }, 10_000).unref(),
      setInterval(() => {
        try {
          purgeExpiredStaffSessions(ctx.db);
          ctx.db.prepare("DELETE FROM rate_limits WHERE window_start < ?").run(Date.now() - 3_600_000);
          ctx.db.prepare("DELETE FROM login_attempts WHERE created_at < ?").run(new Date(Date.now() - 7 * 86_400_000).toISOString());
        } catch (e) {
          log.error("maintenance failed", { error: String(e) });
        }
      }, 300_000).unref()
    );
  }

  return {
    ctx,
    server,
    router,
    listen(port = config.port, host = config.host) {
      return new Promise((resolve, reject) => {
        server.once("error", reject);
        server.listen(port, host, () => {
          startBackgroundJobs();
          resolve(server.address());
        });
      });
    },
    async close() {
      timers.forEach(clearInterval);
      await new Promise((resolve) => server.close(() => resolve()));
      server.closeAllConnections?.();
      ctx.db.close();
    }
  };
}
