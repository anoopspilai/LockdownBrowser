// Configuration from environment variables. Nothing here is secret except the TLS key path.
import path from "node:path";
import { fileURLToPath } from "node:url";

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");

export const VERSION = "0.2.0";
export const MIN_CLIENT_VERSION = "0.1.0";

function bool(v, dflt) {
  if (v === undefined || v === "") return dflt;
  return ["1", "true", "yes", "on"].includes(String(v).toLowerCase());
}

export function loadConfig(env = process.env) {
  const tlsCert = env.AVAIBE_TLS_CERT || null;
  const tlsKey = env.AVAIBE_TLS_KEY || null;
  const tls = !!(tlsCert && tlsKey);
  const port = Number.parseInt(env.PORT, 10) || 4000;
  return Object.freeze({
    rootDir: ROOT,
    publicDir: path.join(ROOT, "public"),
    port,
    host: env.AVAIBE_HOST || "0.0.0.0",
    dbPath: env.AVAIBE_DB || path.join(ROOT, "data", "avaibe.db"),
    tls,
    tlsCert,
    tlsKey,
    // Public base URL used in examUrl and to validate allowedLinks. Derived from the Host header when unset.
    publicBaseUrl: (env.AVAIBE_PUBLIC_BASE_URL || "").replace(/\/+$/, "") || null,
    cookieSecure: bool(env.AVAIBE_COOKIE_SECURE, tls),
    trustProxy: bool(env.AVAIBE_TRUST_PROXY, false),
    logLevel: (env.AVAIBE_LOG_LEVEL || "info").toLowerCase(),
    adminPassword: env.AVAIBE_ADMIN_PASSWORD || null,
    // Limits (bytes / counts). Kept here so tests and docs agree.
    maxBodyBytes: 256 * 1024,
    maxEventsBodyBytes: 1024 * 1024,
    maxEventMetadataBytes: 4096,
    maxStoredEventsPerSession: 5000,
    eventsPerSecondPerSession: 20,
    staffSessionHours: 12,
    enrollmentTokenHours: 24,
    launchTokenSeconds: 300,
    releaseCodeSeconds: 60,
    commandAuthSeconds: 120,
    releaseCodeMaxFailures: 5,
    loginMaxFailures: 5,
    loginLockoutMinutes: 15
  });
}
