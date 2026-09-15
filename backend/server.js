// Avaibe Exam backend entry point. Protocol v2 (docs/CONTRACT.md §10).
import { createApp, ensureAdmin } from "./src/app.js";
import { VERSION } from "./src/config.js";

const app = createApp();
const { ctx } = app;
const log = ctx.log;

// The process never dies on a bad request or an unexpected async error: log and keep serving.
process.on("uncaughtException", (err) => log.error("uncaughtException", { error: String(err?.stack || err) }));
process.on("unhandledRejection", (err) => log.error("unhandledRejection", { error: String(err?.stack || err) }));

const generated = await ensureAdmin(ctx);
if (generated) {
  // Printed exactly once, on first start, when AVAIBE_ADMIN_PASSWORD is not set. Not written to the structured log.
  process.stderr.write(`\n  First start: created staff user "admin" with password: ${generated}\n  Change it after signing in. This is the only time it is shown.\n\n`);
}

const addr = await app.listen();
const scheme = ctx.config.tls ? "https" : "http";
log.info("listening", { version: VERSION, scheme, port: addr.port, db: ctx.config.dbPath, tls: ctx.config.tls, cookieSecure: ctx.config.cookieSecure });
process.stdout.write(`Avaibe backend ${VERSION} listening on ${scheme}://localhost:${addr.port}  (console: ${scheme}://localhost:${addr.port}/admin/)\n`);

let shuttingDown = false;
async function shutdown(signal) {
  if (shuttingDown) return;
  shuttingDown = true;
  log.info("shutdown", { signal });
  const timer = setTimeout(() => process.exit(0), 5000).unref();
  try {
    await app.close();
  } finally {
    clearTimeout(timer);
    process.exit(0);
  }
}
process.on("SIGINT", () => shutdown("SIGINT"));
process.on("SIGTERM", () => shutdown("SIGTERM"));
