-- Avaibe Exam backend schema v1. Every table has created_at. events and audit_log are append-only (triggers).

CREATE TABLE IF NOT EXISTS server_keys (
  key_id TEXT PRIMARY KEY,
  private_pem TEXT NOT NULL,
  public_spki_b64 TEXT NOT NULL,
  created_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS settings (
  key TEXT PRIMARY KEY,
  value_json TEXT NOT NULL,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS staff_users (
  id TEXT PRIMARY KEY,
  username TEXT NOT NULL UNIQUE COLLATE NOCASE,
  password_hash TEXT NOT NULL,
  role TEXT NOT NULL CHECK (role IN ('admin','teacher','reviewer')),
  disabled INTEGER NOT NULL DEFAULT 0,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS staff_sessions (
  token_hash TEXT PRIMARY KEY,
  user_id TEXT NOT NULL REFERENCES staff_users(id) ON DELETE CASCADE,
  ip TEXT,
  created_at TEXT NOT NULL,
  expires_at TEXT NOT NULL,
  last_seen_at TEXT
);

CREATE TABLE IF NOT EXISTS login_attempts (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  username TEXT NOT NULL COLLATE NOCASE,
  ip TEXT NOT NULL,
  success INTEGER NOT NULL,
  created_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS login_attempts_user_ip ON login_attempts(username, ip, id);

CREATE TABLE IF NOT EXISTS rate_limits (
  key TEXT PRIMARY KEY,
  window_start INTEGER NOT NULL,
  count INTEGER NOT NULL,
  created_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS students (
  id TEXT PRIMARY KEY,
  code TEXT NOT NULL UNIQUE,
  name TEXT NOT NULL,
  email TEXT,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS exams (
  id TEXT PRIMARY KEY,
  code TEXT NOT NULL UNIQUE,
  title TEXT NOT NULL,
  duration_minutes INTEGER NOT NULL,
  access_code_hash TEXT,
  is_open INTEGER NOT NULL DEFAULT 1,
  open_from TEXT,
  open_until TEXT,
  open_to_all INTEGER NOT NULL DEFAULT 1,
  release_on_submit TEXT NOT NULL DEFAULT 'teacher' CHECK (release_on_submit IN ('teacher','auto')),
  policy_json TEXT NOT NULL DEFAULT '{}',
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS exam_questions (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  exam_id TEXT NOT NULL REFERENCES exams(id) ON DELETE CASCADE,
  position INTEGER NOT NULL,
  qid TEXT NOT NULL,
  text TEXT NOT NULL,
  options_json TEXT NOT NULL,
  answer TEXT,
  created_at TEXT NOT NULL,
  UNIQUE (exam_id, qid)
);

CREATE TABLE IF NOT EXISTS exam_links (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  exam_id TEXT NOT NULL REFERENCES exams(id) ON DELETE CASCADE,
  position INTEGER NOT NULL,
  label TEXT NOT NULL,
  url TEXT NOT NULL,
  created_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS exam_assignments (
  exam_id TEXT NOT NULL REFERENCES exams(id) ON DELETE CASCADE,
  student_id TEXT NOT NULL REFERENCES students(id) ON DELETE CASCADE,
  created_at TEXT NOT NULL,
  PRIMARY KEY (exam_id, student_id)
);

CREATE TABLE IF NOT EXISTS enrollment_tokens (
  id TEXT PRIMARY KEY,
  token_hash TEXT NOT NULL UNIQUE,
  mode TEXT NOT NULL CHECK (mode IN ('school','byod')),
  label TEXT,
  created_by TEXT,
  created_at TEXT NOT NULL,
  expires_at TEXT NOT NULL,
  redeemed_at TEXT,
  redeemed_device_id TEXT,
  revoked_at TEXT,
  revoked_by TEXT
);

CREATE TABLE IF NOT EXISTS devices (
  id TEXT PRIMARY KEY,
  token_hash TEXT NOT NULL UNIQUE,
  mode TEXT NOT NULL CHECK (mode IN ('school','byod')),
  platform TEXT,
  os_version TEXT,
  client_version TEXT,
  hardware_id TEXT,
  device_name TEXT,
  enrollment_token_id TEXT,
  kiosk_verified INTEGER NOT NULL DEFAULT 0,
  revoked_at TEXT,
  revoked_by TEXT,
  last_seen_at TEXT,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS sessions (
  id TEXT PRIMARY KEY,
  token_hash TEXT NOT NULL UNIQUE,
  student_id TEXT NOT NULL REFERENCES students(id),
  exam_id TEXT NOT NULL REFERENCES exams(id),
  device_id TEXT NOT NULL REFERENCES devices(id),
  status TEXT NOT NULL CHECK (status IN ('active','submitted','released','terminated','expired')),
  started_at TEXT NOT NULL,
  expires_at TEXT NOT NULL,
  submitted_at TEXT,
  released_at TEXT,
  terminated_at TEXT,
  last_heartbeat TEXT,
  client_status TEXT,
  lockdown_mode TEXT,
  display_count INTEGER,
  uptime_seconds INTEGER,
  client_version TEXT,
  preflight_json TEXT NOT NULL DEFAULT '{}',
  preflight_extras_json TEXT,
  policy_json TEXT NOT NULL,
  event_count INTEGER NOT NULL DEFAULT 0,
  stored_event_count INTEGER NOT NULL DEFAULT 0,
  medium_count INTEGER NOT NULL DEFAULT 0,
  high_count INTEGER NOT NULL DEFAULT 0,
  release_queued INTEGER NOT NULL DEFAULT 0,
  release_code_failures INTEGER NOT NULL DEFAULT 0,
  release_code_locked INTEGER NOT NULL DEFAULT 0,
  policy_mismatch_reported INTEGER NOT NULL DEFAULT 0,
  stale INTEGER NOT NULL DEFAULT 0,
  stale_since TEXT,
  launch_token_hash TEXT,
  launch_token_expires_at TEXT,
  launch_token_used_at TEXT,
  exam_cookie_hash TEXT,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS sessions_student_exam ON sessions(student_id, exam_id, status);
CREATE INDEX IF NOT EXISTS sessions_status ON sessions(status, last_heartbeat);

CREATE TABLE IF NOT EXISTS session_answers (
  session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
  question_id TEXT NOT NULL,
  answer TEXT NOT NULL,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  PRIMARY KEY (session_id, question_id)
);

CREATE TABLE IF NOT EXISTS events (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  session_id TEXT NOT NULL,
  type TEXT NOT NULL,
  severity TEXT NOT NULL,
  timestamp TEXT NOT NULL,
  received_at TEXT NOT NULL,
  source TEXT NOT NULL,
  metadata_json TEXT NOT NULL DEFAULT '{}',
  created_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS events_session ON events(session_id, id);
CREATE TRIGGER IF NOT EXISTS events_no_update BEFORE UPDATE ON events BEGIN SELECT RAISE(ABORT, 'events is append-only'); END;
CREATE TRIGGER IF NOT EXISTS events_no_delete BEFORE DELETE ON events BEGIN SELECT RAISE(ABORT, 'events is append-only'); END;

CREATE TABLE IF NOT EXISTS release_codes (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
  device_id TEXT NOT NULL,
  code_hash TEXT NOT NULL,
  created_by TEXT,
  created_at TEXT NOT NULL,
  expires_at TEXT NOT NULL,
  used_at TEXT
);
CREATE INDEX IF NOT EXISTS release_codes_session ON release_codes(session_id, id);

CREATE TABLE IF NOT EXISTS pending_commands (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
  type TEXT NOT NULL CHECK (type IN ('RELEASE','TERMINATE','WARN')),
  reason TEXT,
  message TEXT,
  created_by TEXT,
  created_at TEXT NOT NULL,
  delivered_at TEXT,
  authorization_json TEXT
);
CREATE INDEX IF NOT EXISTS pending_commands_session ON pending_commands(session_id, delivered_at, id);

CREATE TABLE IF NOT EXISTS incidents (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  session_id TEXT,
  student_id TEXT,
  exam_id TEXT,
  type TEXT NOT NULL,
  severity TEXT NOT NULL,
  detail TEXT,
  resolved_at TEXT,
  resolved_by TEXT,
  resolution_note TEXT,
  created_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS incidents_open ON incidents(resolved_at, id);

CREATE TABLE IF NOT EXISTS audit_log (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  actor_type TEXT NOT NULL,
  actor_id TEXT,
  actor_name TEXT,
  action TEXT NOT NULL,
  target_type TEXT,
  target_id TEXT,
  reason TEXT,
  detail_json TEXT,
  ip TEXT,
  created_at TEXT NOT NULL
);
CREATE TRIGGER IF NOT EXISTS audit_log_no_update BEFORE UPDATE ON audit_log BEGIN SELECT RAISE(ABORT, 'audit_log is append-only'); END;
CREATE TRIGGER IF NOT EXISTS audit_log_no_delete BEFORE DELETE ON audit_log BEGIN SELECT RAISE(ABORT, 'audit_log is append-only'); END;
