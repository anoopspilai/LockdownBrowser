// ECDSA P-256 command signer (CONTRACT §10.4). Key pair persisted in server_keys so restarts keep the same key.
// Signature: SHA-256, IEEE P1363 (r||s, 64 bytes), over
//   "avaibe-cmd-v1\n<type>\n<sessionId>\n<deviceId>\n<nonce>\n<issuedAt>\n<expiresAt>"
import crypto from "node:crypto";
import { nowIso } from "../util.js";

export const KEY_ID = "k1";

export function canonicalCommandString({ type, sessionId, deviceId, nonce, issuedAt, expiresAt }) {
  for (const [k, v] of Object.entries({ type, sessionId, deviceId, nonce, issuedAt, expiresAt })) {
    if (typeof v !== "string" || v.includes("\n")) throw new Error(`canonical field ${k} invalid`);
  }
  return `avaibe-cmd-v1\n${type}\n${sessionId}\n${deviceId}\n${nonce}\n${issuedAt}\n${expiresAt}`;
}

export function signCanonical(privateKey, fields) {
  const data = Buffer.from(canonicalCommandString(fields), "utf8");
  return crypto.sign(null, data, { key: privateKey, dsaEncoding: "ieee-p1363" }).toString("base64");
}

export function verifyCanonical(publicKeyOrSpkiB64, fields, signatureB64) {
  const key =
    typeof publicKeyOrSpkiB64 === "string"
      ? crypto.createPublicKey({ key: Buffer.from(publicKeyOrSpkiB64, "base64"), type: "spki", format: "der" })
      : publicKeyOrSpkiB64;
  const data = Buffer.from(canonicalCommandString(fields), "utf8");
  try {
    return crypto.verify(null, data, { key, dsaEncoding: "ieee-p1363" }, Buffer.from(signatureB64, "base64"));
  } catch {
    return false;
  }
}

export function generateKeyPair() {
  const { privateKey, publicKey } = crypto.generateKeyPairSync("ec", { namedCurve: "prime256v1" });
  return {
    privatePem: privateKey.export({ type: "pkcs8", format: "pem" }),
    publicSpkiB64: publicKey.export({ type: "spki", format: "der" }).toString("base64")
  };
}

/** Loads (or creates on first run) the signing key and returns a signer bound to it. */
export function loadSigner(db, keyId = KEY_ID) {
  let row = db.prepare("SELECT key_id, private_pem, public_spki_b64 FROM server_keys WHERE key_id = ?").get(keyId);
  if (!row) {
    const kp = generateKeyPair();
    db.prepare("INSERT INTO server_keys (key_id, private_pem, public_spki_b64, created_at) VALUES (?, ?, ?, ?)").run(
      keyId,
      kp.privatePem,
      kp.publicSpkiB64,
      nowIso()
    );
    row = { key_id: keyId, private_pem: kp.privatePem, public_spki_b64: kp.publicSpkiB64 };
  }
  const privateKey = crypto.createPrivateKey(row.private_pem);
  const publicKey = crypto.createPublicKey(privateKey);
  return {
    keyId,
    publicSpkiB64: row.public_spki_b64,
    publicKey,
    /** Builds and signs an authorization object for a RELEASE or TERMINATE command. */
    signCommand({ type, sessionId, deviceId, nonce, issuedAt, expiresAt }) {
      const fields = { type, sessionId, deviceId, nonce, issuedAt, expiresAt };
      return { ...fields, keyId, signature: signCanonical(privateKey, fields) };
    },
    verify(fields, signatureB64) {
      return verifyCanonical(publicKey, fields, signatureB64);
    }
  };
}
