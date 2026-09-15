// Device enrollment and authentication (CONTRACT §10.1).
import { HttpError, nowIso, isoIn, sha256hex, safeEqual, newToken, newDeviceId, newId, isPast, str, DEVICE_ID_RE } from "../util.js";
import { rateLimit } from "../db.js";

const PLATFORMS = ["macos", "windows", "ios", "ipados", "chromeos", "other"];

export function issueEnrollmentToken(ctx, { mode, label, createdBy, hours }) {
  if (!["school", "byod"].includes(mode)) throw new HttpError(400, "INVALID_REQUEST", "mode must be school or byod");
  const token = `${mode === "school" ? "ENR-S" : "ENR-B"}-${newToken()}`;
  const id = newId("etk");
  const ttl = Math.min(Math.max(Number(hours) || ctx.config.enrollmentTokenHours, 1), 24 * 30);
  ctx.db
    .prepare(
      `INSERT INTO enrollment_tokens (id, token_hash, mode, label, created_by, created_at, expires_at) VALUES (?, ?, ?, ?, ?, ?, ?)`
    )
    .run(id, sha256hex(token), mode, str(label, 120) || null, createdBy || null, nowIso(), isoIn(ttl * 3_600_000));
  return { id, token, mode, label: str(label, 120) || null, expiresAt: isoIn(ttl * 3_600_000) };
}

export function publicEnrollmentToken(row) {
  return {
    id: row.id,
    mode: row.mode,
    label: row.label,
    createdBy: row.created_by,
    createdAt: row.created_at,
    expiresAt: row.expires_at,
    redeemedAt: row.redeemed_at,
    redeemedDeviceId: row.redeemed_device_id,
    revokedAt: row.revoked_at,
    status: row.revoked_at ? "revoked" : row.redeemed_at ? "redeemed" : isPast(row.expires_at) ? "expired" : "active"
  };
}

/** Redeems a single-use enrollment token and registers the device. Returns the plaintext device token once. */
export function enroll(ctx, body, { ip }) {
  const { db, config } = ctx;
  if (!rateLimit(db, `enroll:${ip}`, 30, 60_000)) throw new HttpError(429, "RATE_LIMITED", "Too many enrollment attempts");
  const token = str(body.enrollmentToken, 200);
  if (!token) throw new HttpError(400, "INVALID_ENROLLMENT_TOKEN", "enrollmentToken is required");
  const hash = sha256hex(token);
  const row = db.prepare("SELECT * FROM enrollment_tokens WHERE token_hash = ?").get(hash);
  // A prefix never grants a mode: the mode comes from the admin-issued token record only.
  if (!row || !safeEqual(row.token_hash, hash)) throw new HttpError(403, "INVALID_ENROLLMENT_TOKEN", "Enrollment token not recognised");
  if (row.revoked_at) throw new HttpError(403, "ENROLLMENT_TOKEN_REVOKED", "Enrollment token was revoked");
  if (row.redeemed_at) throw new HttpError(403, "ENROLLMENT_TOKEN_USED", "Enrollment token was already used");
  if (isPast(row.expires_at)) throw new HttpError(403, "ENROLLMENT_TOKEN_EXPIRED", "Enrollment token has expired");

  const platform = PLATFORMS.includes(body.platform) ? body.platform : "other";
  const deviceId = newDeviceId();
  const deviceToken = newToken();
  const now = nowIso();
  db.exec("BEGIN IMMEDIATE");
  try {
    const upd = db.prepare("UPDATE enrollment_tokens SET redeemed_at = ?, redeemed_device_id = ? WHERE id = ? AND redeemed_at IS NULL").run(now, deviceId, row.id);
    if (upd.changes !== 1) throw new HttpError(403, "ENROLLMENT_TOKEN_USED", "Enrollment token was already used");
    db.prepare(
      `INSERT INTO devices (id, token_hash, mode, platform, os_version, client_version, hardware_id, device_name, enrollment_token_id, kiosk_verified, last_seen_at, created_at, updated_at)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, 0, ?, ?, ?)`
    ).run(
      deviceId,
      sha256hex(deviceToken),
      row.mode,
      platform,
      str(body.osVersion, 64) || null,
      str(body.clientVersion, 32) || null,
      str(body.hardwareId, 128) || null,
      str(body.deviceName, 120) || null,
      row.id,
      now,
      now,
      now
    );
    db.exec("COMMIT");
  } catch (e) {
    db.exec("ROLLBACK");
    throw e;
  }
  ctx.audit.record(ctx, { actorType: "device", actorId: deviceId, action: "device.enrolled", targetType: "device", targetId: deviceId, ip, detail: { mode: row.mode, platform, enrollmentTokenId: row.id } });
  return {
    deviceId,
    deviceToken,
    mode: row.mode,
    minClientVersion: ctx.defaultPolicy().minClientVersion,
    serverPublicKey: ctx.signer.publicSpkiB64,
    keyId: ctx.signer.keyId,
    serverTime: nowIso()
  };
}

/** Authenticates X-Device-Id + X-Device-Token. Returns the device row or throws 401. */
export function requireDevice(ctx, req) {
  const id = req.headers["x-device-id"];
  const token = req.headers["x-device-token"];
  if (typeof id !== "string" || !DEVICE_ID_RE.test(id) || typeof token !== "string" || !/^[0-9a-f]{48}$/.test(token)) {
    throw new HttpError(401, "DEVICE_AUTH_REQUIRED", "X-Device-Id and X-Device-Token headers are required");
  }
  const dev = ctx.db.prepare("SELECT * FROM devices WHERE id = ?").get(id);
  if (!dev || !safeEqual(dev.token_hash, sha256hex(token))) throw new HttpError(401, "INVALID_DEVICE_TOKEN", "Device credentials rejected");
  if (dev.revoked_at) throw new HttpError(403, "DEVICE_REVOKED", "This device has been revoked. Re-enroll it.");
  const cv = req.headers["x-client-version"];
  ctx.db.prepare("UPDATE devices SET last_seen_at = ?, client_version = COALESCE(?, client_version) WHERE id = ?").run(nowIso(), typeof cv === "string" ? cv.slice(0, 32) : null, id);
  return dev;
}

export function publicDevice(d) {
  return {
    deviceId: d.id,
    mode: d.mode,
    platform: d.platform,
    osVersion: d.os_version,
    clientVersion: d.client_version,
    hardwareId: d.hardware_id,
    deviceName: d.device_name,
    kioskVerified: !!d.kiosk_verified,
    revokedAt: d.revoked_at,
    lastSeenAt: d.last_seen_at,
    enrolledAt: d.created_at
  };
}
