# Avaibe Exam backend (protocol v2)

The real server behind the macOS and Windows Avaibe Exam clients. Implements `docs/CONTRACT.md` §10 (and the v1
paths it keeps). Node.js 24+ built-ins only: no npm dependencies, SQLite through `node:sqlite`, ECDSA P-256 through
`node:crypto`.

## Run

```sh
cd backend
AVAIBE_ADMIN_PASSWORD='choose-a-long-password' node server.js      # http://localhost:4000
npm run dev                                                          # same, restarts on file changes
```

On the very first start the server creates the staff user `admin`. If `AVAIBE_ADMIN_PASSWORD` is not set, a random
password is printed once to stderr. Sign in at `http://localhost:4000/admin/`.

Data lives in `backend/data/avaibe.db` (SQLite, WAL mode; the folder is gitignored). Set `AVAIBE_DB` to move it.

## Demo data

```sh
npm run seed
```

Creates exams `DEMO` (5 min, 5 questions, auto release), `MATH101` (60 min) and `SCI202` (45 min), students `1025`
(Demo Student) and `2001`–`2005`, and prints one `school` and one `byod` enrollment token (single use, 24 h).
Enter a token on the client's first run; use a student code and exam code to start.

### Allowed links (Resources)

Links added to an exam appear in the app's **Resources** menu. How they behave:

- A link opens inside the exam window. **Back to exam** returns to the exam with saved answers.
- A link to `example.com` also allows `www.example.com`, because most sites redirect between the two.
- Pages under the link's address work, including the page's own images and scripts. For a link to
  `https://www.google.com/`, searching works.
- Any other site, including other subdomains such as `mail.google.com`, is blocked, and the student
  sees a short "not an allowed link" message.
- To allow only one page, add the full page address, for example
  `https://en.wikipedia.org/wiki/Algebra`.

### When a student cannot start an exam

The app shows the server's message. The usual causes:

| Code | Meaning | Fix |
|---|---|---|
| `ACCESS_CODE_REQUIRED` | The exam has an access code and none was typed | Give the student the code, or remove it in the Exams tab (type `-` in the access code box) |
| `INVALID_ACCESS_CODE` | The typed access code is wrong | Check the code |
| `ACCESS_CODE_LOCKED` | 10 wrong codes from this device within 15 minutes | Wait up to 15 minutes; an incident is shown in the Incidents tab |
| `STUDENT_NOT_FOUND` | No student with that code | Add the student in the Students tab |
| `NOT_ASSIGNED` | The exam is not open to all and the student is not assigned | Assign the student, or tick "open to all students" |
| `EXAM_CLOSED` | The exam is closed or outside its open window | Open the exam |
| `SESSION_ALREADY_ACTIVE` | The student already has a running session for this exam | Release that session in the Live tab |

### Forgot the admin password

Create another admin account, sign in with it, and manage the old one from the Staff tab:

```sh
AVAIBE_NEW_PASSWORD='choose-a-long-password' node scripts/create-admin.js admin2 admin
```

More staff users: `node scripts/create-admin.js <username> [admin|teacher|reviewer]` (or the Staff tab).

## Environment

| Variable | Default | Meaning |
|---|---|---|
| `PORT` | `4000` | listen port |
| `AVAIBE_HOST` | `0.0.0.0` | bind address |
| `AVAIBE_DB` | `backend/data/avaibe.db` | SQLite file |
| `AVAIBE_TLS_CERT` / `AVAIBE_TLS_KEY` | unset | PEM pair; when both are set the server speaks HTTPS |
| `AVAIBE_PUBLIC_BASE_URL` | derived from `Host` | used in `examUrl` and to validate same-host `allowedLinks` — set it behind a proxy |
| `AVAIBE_COOKIE_SECURE` | `true` when TLS | adds `Secure` to cookies |
| `AVAIBE_TRUST_PROXY` | `false` | trust `X-Forwarded-For` for client IPs |
| `AVAIBE_LOG_LEVEL` | `info` | `debug` / `info` / `warn` / `error` |
| `AVAIBE_ADMIN_PASSWORD` | unset | password for the bootstrap `admin` user (first start only) |

## TLS

```sh
npm run dev-cert     # self-signed cert for localhost in backend/certs/ (gitignored)
AVAIBE_TLS_CERT=certs/dev-cert.pem AVAIBE_TLS_KEY=certs/dev-key.pem node server.js
```

Clients accept plain `http://` only for `localhost` / `127.0.0.1`; anything else must be HTTPS with a certificate the
device trusts. In production terminate TLS here or on a reverse proxy and set `AVAIBE_PUBLIC_BASE_URL`.

## Endpoints

Device / client (headers `X-Device-Id`, `X-Device-Token`, `X-Client-Version`; session routes add `Authorization: Bearer`):

| Method | Path | Notes |
|---|---|---|
| GET | `/healthz` | liveness |
| GET | `/api/v1/public-key` | server signing key (SPKI DER base64, `keyId` `k1`) |
| POST | `/api/v1/devices/enroll` | redeem an enrollment token → `deviceId`, `deviceToken`, `serverPublicKey` |
| POST | `/api/v1/sessions` | start; 403 `PREFLIGHT_FAILED`, 409 `SESSION_ALREADY_ACTIVE`, 403 `EXAM_CLOSED` / `NOT_ASSIGNED` / `ACCESS_CODE_REQUIRED` |
| POST | `/api/v1/sessions/:id/heartbeat` | returns `nextBeatInSeconds` and at most one command; RELEASE/TERMINATE are signed |
| POST | `/api/v1/sessions/:id/events` | ≤ 20/s, metadata ≤ 4 KiB, ≤ 5,000 stored (count kept) |
| POST | `/api/v1/sessions/:id/submit` | idempotent; `release: "teacher" | "auto"` |
| POST | `/api/v1/sessions/:id/unlock` | release code → signed authorization; 423 after 5 failures |
| PUT | `/api/v1/sessions/:id/answers` | autosave |
| GET | `/api/v1/sessions/:id/questions`, `/status` | scoped to the session's exam, no answer key |

Exam page: `GET /exam/launch?lt=…` (one-time, 5 min) sets the `avaibe_exam` cookie and redirects to `/exam/<sessionId>`;
the page talks to `/exam/api/status|questions|answers|submit` with that cookie only.

Staff (`avaibe_staff` cookie + header `X-Requested-With: AvaibeAdmin`): `POST /api/v1/auth/login|logout`, `GET /api/v1/auth/me`,
and under `/api/v1/admin/`: `sessions` (list, detail, events, `release-code`, `release`, `terminate`, `warn` — all with a
`reason`), `exams` (CRUD, `open`, `close`), `students` (CRUD, `import`), `enrollment-tokens`, `devices` (kiosk verification,
revoke), `incidents` (`resolve`), `audit`, `staff`, `policy` (defaults). Roles: `admin` (all), `teacher` (no staff/tokens/devices
changes), `reviewer` (read-only).

## What the server decides vs. what the client reports

Preflight, `lockdownMode`, `displayCount` and `X-Client-Version` are stored verbatim and shown in the console as
**client-reported**. `requireAAC` is enforced from the device registry (`kiosk verified`, set by an admin after MDM
confirmation), never from the client flag; a mismatch raises `POLICY_MISMATCH`. Time, release, and termination are
server-authoritative and signed.

## Backups

The database is a single file. With the server running, use SQLite's online backup so the WAL is included:

```sh
sqlite3 data/avaibe.db ".backup 'backup-$(date +%F).db'"
```

Or stop the server and copy `avaibe.db` (plus `-wal`/`-shm` if present). The signing key pair lives in the `server_keys`
table: restoring a backup restores the key clients have pinned. Losing it forces every device to re-enroll.

## Tests

```sh
npm test
```

`node:test` suites cover the signature vector, the full enroll → session → heartbeat → release flow, unlock edge cases,
submit modes, concurrency, rate limits, malformed input, admin auth/CSRF and question scoping.
