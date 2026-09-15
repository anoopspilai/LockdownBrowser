import { VERSION } from "../config.js";
import { nowIso } from "../util.js";

export function registerPublicRoutes(router, ctx) {
  router.get("/healthz", () => ({
    ok: true,
    version: VERSION,
    serverTime: nowIso(),
    sessions: ctx.db.prepare("SELECT COUNT(*) AS n FROM sessions WHERE status IN ('active','submitted')").get().n,
    devices: ctx.db.prepare("SELECT COUNT(*) AS n FROM devices WHERE revoked_at IS NULL").get().n
  }));

  router.get("/api/v1/public-key", () => ({
    keyId: ctx.signer.keyId,
    algorithm: "ECDSA-P256-SHA256",
    signatureFormat: "ieee-p1363",
    publicKey: ctx.signer.publicSpkiB64,
    serverTime: nowIso()
  }));
}
