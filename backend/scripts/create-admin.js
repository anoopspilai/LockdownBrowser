#!/usr/bin/env node
// Usage: node scripts/create-admin.js <username> [role]   (password from AVAIBE_NEW_PASSWORD or generated and printed once)
import crypto from "node:crypto";
import { createContext } from "../src/app.js";
import { createLogger } from "../src/log.js";
import { createStaffUser } from "../src/auth/staff.js";

const [username, role = "admin"] = process.argv.slice(2);
if (!username) {
  process.stderr.write("Usage: node scripts/create-admin.js <username> [admin|teacher|reviewer]\n  Password: set AVAIBE_NEW_PASSWORD, otherwise one is generated and printed once.\n");
  process.exit(2);
}
const ctx = createContext({ log: createLogger("warn") });
const generated = process.env.AVAIBE_NEW_PASSWORD ? null : crypto.randomBytes(12).toString("base64url");
try {
  const user = await createStaffUser(ctx.db, { username, password: process.env.AVAIBE_NEW_PASSWORD || generated, role });
  ctx.audit.record(ctx, { actorType: "system", action: "staff.create_cli", targetType: "staff", targetId: user.id, detail: { username: user.username, role: user.role } });
  process.stdout.write(`Created ${user.role} "${user.username}"${generated ? ` with password: ${generated}  (shown once)` : ""}\n`);
} catch (e) {
  process.stderr.write(`Error: ${e.message}\n`);
  process.exitCode = 1;
} finally {
  ctx.db.close();
}
