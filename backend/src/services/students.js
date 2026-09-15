import { HttpError, nowIso, newId, str, CODE_RE, isObject } from "../util.js";
import { transaction } from "../db.js";

export function publicStudent(r) {
  return { id: r.id, code: r.code, name: r.name, email: r.email, createdAt: r.created_at, updatedAt: r.updated_at };
}

export function getByCode(ctx, code) {
  if (typeof code !== "string") return null;
  return ctx.db.prepare("SELECT * FROM students WHERE code = ?").get(code.trim());
}

export function list(ctx) {
  return ctx.db.prepare("SELECT * FROM students ORDER BY code").all().map(publicStudent);
}

function validate(body, { partial = false } = {}) {
  if (!isObject(body)) throw new HttpError(400, "INVALID_REQUEST", "Body must be an object");
  const out = {};
  if (!partial || body.code !== undefined) {
    out.code = str(body.code, 64);
    if (!CODE_RE.test(out.code)) throw new HttpError(400, "INVALID_REQUEST", "code must be 1-64 chars: letters, digits, _ . -");
  }
  if (!partial || body.name !== undefined) {
    out.name = str(body.name, 200);
    if (!out.name) throw new HttpError(400, "INVALID_REQUEST", "name is required");
  }
  if (body.email !== undefined) {
    const e = str(body.email, 200);
    if (e && !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(e)) throw new HttpError(400, "INVALID_REQUEST", "email is invalid");
    out.email = e || null;
  }
  return out;
}

export function create(ctx, body) {
  const v = validate(body);
  if (getByCode(ctx, v.code)) throw new HttpError(409, "STUDENT_EXISTS", `Student ${v.code} already exists`);
  const now = nowIso();
  const id = newId("stu");
  ctx.db.prepare("INSERT INTO students (id, code, name, email, created_at, updated_at) VALUES (?, ?, ?, ?, ?, ?)").run(id, v.code, v.name, v.email ?? null, now, now);
  return publicStudent(ctx.db.prepare("SELECT * FROM students WHERE id = ?").get(id));
}

export function update(ctx, row, body) {
  const v = validate(body, { partial: true });
  if (v.code && v.code !== row.code && getByCode(ctx, v.code)) throw new HttpError(409, "STUDENT_EXISTS", `Student ${v.code} already exists`);
  ctx.db
    .prepare("UPDATE students SET code = ?, name = ?, email = ?, updated_at = ? WHERE id = ?")
    .run(v.code ?? row.code, v.name ?? row.name, v.email !== undefined ? v.email : row.email, nowIso(), row.id);
  return publicStudent(ctx.db.prepare("SELECT * FROM students WHERE id = ?").get(row.id));
}

export function remove(ctx, row) {
  const n = ctx.db.prepare("SELECT COUNT(*) AS n FROM sessions WHERE student_id = ?").get(row.id).n;
  if (n) throw new HttpError(409, "STUDENT_IN_USE", `Student has ${n} exam session(s) and cannot be deleted`);
  ctx.db.prepare("DELETE FROM students WHERE id = ?").run(row.id);
}

/** CSV import: lines of `code,name[,email]`; a header row is skipped. Upserts by code. */
export function importCsv(ctx, csv) {
  if (typeof csv !== "string" || !csv.trim()) throw new HttpError(400, "INVALID_REQUEST", "csv text is required");
  if (csv.length > 200_000) throw new HttpError(413, "PAYLOAD_TOO_LARGE", "CSV too large (200 KB max)");
  const lines = csv.split(/\r?\n/).map((l) => l.trim()).filter(Boolean);
  const result = { created: 0, updated: 0, skipped: [] };
  transaction(ctx.db, () => {
    lines.forEach((line, i) => {
      const cells = parseCsvLine(line);
      if (i === 0 && /^code$/i.test(cells[0] || "") ) return;
      const body = { code: cells[0], name: cells[1], email: cells[2] };
      try {
        const v = validate(body);
        const existing = getByCode(ctx, v.code);
        if (existing) {
          update(ctx, existing, v);
          result.updated++;
        } else {
          create(ctx, v);
          result.created++;
        }
      } catch (e) {
        result.skipped.push({ line: i + 1, reason: e.message });
      }
    });
  });
  return result;
}

function parseCsvLine(line) {
  const out = [];
  let cur = "";
  let q = false;
  for (let i = 0; i < line.length; i++) {
    const c = line[i];
    if (q) {
      if (c === '"' && line[i + 1] === '"') {
        cur += '"';
        i++;
      } else if (c === '"') q = false;
      else cur += c;
    } else if (c === '"') q = true;
    else if (c === "," || c === ";" || c === "\t") {
      out.push(cur.trim());
      cur = "";
    } else cur += c;
  }
  out.push(cur.trim());
  return out;
}
