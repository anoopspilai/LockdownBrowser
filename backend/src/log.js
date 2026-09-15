// Structured single-line JSON logs to stdout. Callers must never pass tokens, passwords,
// release codes, signatures or cookies; redact() is a belt-and-braces guard for common key names.
const LEVELS = { debug: 10, info: 20, warn: 30, error: 40 };
const SECRET_KEYS = /token|password|secret|^cookies?$|set-cookie|signature|releasecode|authorization|code_hash|private/i;

export function redact(value, depth = 0) {
  if (depth > 6) return "[depth]";
  if (Array.isArray(value)) return value.map((v) => redact(v, depth + 1));
  if (value && typeof value === "object") {
    const out = {};
    for (const [k, v] of Object.entries(value)) out[k] = SECRET_KEYS.test(k) ? "[redacted]" : redact(v, depth + 1);
    return out;
  }
  return value;
}

export function createLogger(level = "info", stream = process.stdout) {
  const min = LEVELS[level] ?? LEVELS.info;
  function emit(lvl, msg, fields) {
    if (LEVELS[lvl] < min) return;
    const line = { ts: new Date().toISOString(), level: lvl, msg, ...(fields ? redact(fields) : {}) };
    try {
      stream.write(JSON.stringify(line) + "\n");
    } catch {
      /* never throw from the logger */
    }
  }
  return {
    debug: (m, f) => emit("debug", m, f),
    info: (m, f) => emit("info", m, f),
    warn: (m, f) => emit("warn", m, f),
    error: (m, f) => emit("error", m, f)
  };
}
