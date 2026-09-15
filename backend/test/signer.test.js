import { test } from "node:test";
import assert from "node:assert/strict";
import crypto from "node:crypto";
import { DatabaseSync } from "node:sqlite";
import { openDb } from "../src/db.js";
import { canonicalCommandString, loadSigner, verifyCanonical } from "../src/crypto/signer.js";

const FIELDS = {
  type: "RELEASE",
  sessionId: "sess_0123456789abcdef0123456789abcdef",
  deviceId: "dev_0123456789abcdef",
  nonce: "00112233445566778899aabbccddeeff",
  issuedAt: "2026-09-14T10:00:00.000Z",
  expiresAt: "2026-09-14T10:02:00.000Z"
};

test("canonical string matches CONTRACT §10.4 exactly", () => {
  assert.equal(
    canonicalCommandString(FIELDS),
    "avaibe-cmd-v1\nRELEASE\nsess_0123456789abcdef0123456789abcdef\ndev_0123456789abcdef\n00112233445566778899aabbccddeeff\n2026-09-14T10:00:00.000Z\n2026-09-14T10:02:00.000Z"
  );
  assert.throws(() => canonicalCommandString({ ...FIELDS, nonce: "a\nb" }));
});

test("signature is P-256 / SHA-256 / IEEE P1363 (64 bytes) and verifies with node's verify() from SPKI DER", () => {
  const db = openDb(":memory:");
  const signer = loadSigner(db);
  assert.equal(signer.keyId, "k1");
  const auth = signer.signCommand(FIELDS);
  assert.equal(auth.keyId, "k1");
  const sig = Buffer.from(auth.signature, "base64");
  assert.equal(sig.length, 64);
  const pub = crypto.createPublicKey({ key: Buffer.from(signer.publicSpkiB64, "base64"), type: "spki", format: "der" });
  assert.equal(pub.asymmetricKeyDetails.namedCurve, "prime256v1");
  const data = Buffer.from(canonicalCommandString(FIELDS), "utf8");
  assert.equal(crypto.verify("sha256", data, { key: pub, dsaEncoding: "ieee-p1363" }, sig), true);
  assert.equal(verifyCanonical(signer.publicSpkiB64, FIELDS, auth.signature), true);
  // Tampering with any field breaks verification.
  assert.equal(verifyCanonical(signer.publicSpkiB64, { ...FIELDS, deviceId: "dev_ffffffffffffffff" }, auth.signature), false);
  assert.equal(verifyCanonical(signer.publicSpkiB64, { ...FIELDS, type: "TERMINATE" }, auth.signature), false);
  db.close();
});

test("key pair persists in server_keys across reloads", () => {
  const db = openDb(":memory:");
  const a = loadSigner(db);
  const b = loadSigner(db);
  assert.equal(a.publicSpkiB64, b.publicSpkiB64);
  assert.equal(db.prepare("SELECT COUNT(*) AS n FROM server_keys").get().n, 1);
  assert.ok(db instanceof DatabaseSync);
  db.close();
});
