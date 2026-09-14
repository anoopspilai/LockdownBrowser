# Avaibe Exam — mock backend

A dependency-free Node.js mock of the exam platform that the macOS lockdown client
(`macos/`) talks to. It implements every endpoint, the policy object, and the demo
credentials defined in [`docs/CONTRACT.md`](../docs/CONTRACT.md), and serves two pages:

| Page | URL | Purpose |
|---|---|---|
| Exam | `http://localhost:4000/exam/<sessionId>` | Loaded inside the client's `WKWebView`. Also works in a normal browser ("browser test mode"). |
| Teacher console | `http://localhost:4000/admin` | Live sessions, release codes, remote release / warn / terminate, event timelines, policy editor. |

All state (devices, sessions, events, release codes, pending commands, policy) lives in
memory and is lost on restart. Nothing is persisted; there is no auth on admin routes.

## Run

```sh
cd mock-backend
node server.js            # or: npm start
node --watch server.js    # or: npm run dev  (auto-restart on edit; state is wiped)
PORT=5000 node server.js  # different port
```

Requires Node 18+ (tested on Node 26). No `npm install` needed — there are no dependencies.

Every request is logged to stdout as `POST /api/v1/sessions/sess_9f3a/heartbeat 200 (0.3ms)`.

## Demo credentials (contract §8)

| Item | Value |
|---|---|
| Enrollment token | `SCHOOL-DEMO` → school mode. Anything else (e.g. `BYOD-DEMO`) → byod mode. Empty → `400 INVALID_ENROLLMENT_TOKEN`. |
| Student code | any string (e.g. `1025`); the mock names the student "Demo Student 1025" |
| Exam codes | `MATH101` Mathematics 101 (60 min) · `SCI202` Science 202 (45 min) · `DEMO` Demo Exam (5 min) |
| Admin console | http://localhost:4000/admin |

Each exam has five sample multiple-choice questions in [`data/exams.js`](data/exams.js).

## Files

```
mock-backend/
  package.json        name avaibe-mock-backend, "type": "module", start/dev scripts
  server.js           HTTP server, router, all endpoints, in-memory state
  data/exams.js       exam catalogue + questions (answer keys are never sent to the page)
  public/exam.html    student exam page (bridge client, contract §5)
  public/admin.html   teacher console (polls the admin API every 3 s)
  README.md           this file
```

## Endpoints

Contract endpoints (§3). Session endpoints need `Authorization: Bearer <sessionToken>`
matching `:id`, else `401 INVALID_SESSION_TOKEN`.

| Method | Path | Notes |
|---|---|---|
| `GET` | `/healthz` | `{ ok, version: "0.1.0-mock", serverTime, sessions, devices }` |
| `POST` | `/api/v1/devices/enroll` | `{ deviceId, deviceToken, mode, minClientVersion }` |
| `POST` | `/api/v1/sessions` | Evaluates preflight against the current policy. `404 EXAM_NOT_FOUND`, `403 PREFLIGHT_FAILED` + `failures[]`. |
| `POST` | `/api/v1/sessions/:id/heartbeat` | Records status; returns `remainingSeconds` and pops **one** pending command (FIFO). |
| `POST` | `/api/v1/sessions/:id/events` | Appends events; recomputes `riskLevel`. Returns `{ accepted }`. |
| `POST` | `/api/v1/sessions/:id/submit` | Marks submitted; next heartbeat auto-returns `RELEASE` ("Exam submitted"). |
| `POST` | `/api/v1/sessions/:id/unlock` | Validates a release code. `403 INVALID_RELEASE_CODE / RELEASE_CODE_EXPIRED / RELEASE_CODE_USED`. |
| `GET` | `/api/v1/admin/sessions` | Session list (contract fields + extras: `examTitle`, `remainingSeconds`, `clientStatus`, `pendingCommands`, …). |
| `POST` | `/api/v1/admin/sessions/:id/release-code` | `{ releaseCode: "482913", expiresAt }` — 6 digits, single use, 60 s. |
| `POST` | `/api/v1/admin/sessions/:id/release` | Queues `RELEASE` (optional body `{ reason }`). |
| `POST` | `/api/v1/admin/sessions/:id/terminate` | Queues `TERMINATE` (optional body `{ reason }`). |
| `POST` | `/api/v1/admin/sessions/:id/warn` | Body `{ message }` → queues `WARN`. |
| `GET` | `/api/v1/admin/sessions/:id/events` | Event list, newest last. Each has `id, type, severity, timestamp, receivedAt, source, metadata`. |
| `GET` / `PUT` | `/api/v1/admin/policy` | Get / merge-update the default policy. PUT validates types and rejects unknown fields (`400 INVALID_POLICY`). Applies to sessions started afterwards. |
| `GET` | `/admin`, `/exam/:sessionId`, `/public/*` | Static pages. |

Mock-only additions (not in the contract, safe for the client to ignore):

| Method | Path | Notes |
|---|---|---|
| `GET` | `/api/v1/sessions/:id/page-token` | **Mock shortcut, no auth.** Returns `{ sessionToken, examCode }` so `exam.html` can call the API from inside the web view. In production the exam page would be served with an httpOnly session cookie instead; never ship this endpoint. |
| `GET` | `/api/v1/exams` | Exam catalogue (no questions). |
| `GET` | `/api/v1/exams/:code/questions` | Questions without answer keys. Requires any valid session Bearer token. |
| `GET` | `/api/v1/admin/sessions/:id` | Single session view. |
| `PATCH` | `/api/v1/admin/policy` | Same as PUT. |
| `DELETE` | `/api/v1/admin/policy` | Reset policy to defaults. |

### Behaviour notes

* **Preflight rules** (`POST /api/v1/sessions`): `requireSIP && !sipEnabled → SIP_DISABLED`,
  `requireMDM && !mdmEnrolled → MDM_NOT_ENROLLED`, `requireStandardAccount && accountType != "standard" → ADMIN_ACCOUNT`,
  `blockExternalDisplay && externalDisplayAction == "BLOCK_START" && displayCount > 1 → EXTERNAL_DISPLAY`,
  `screenSharingActive → SCREEN_SHARING`, `requireAAC && !aacEntitlementPresent → AAC_REQUIRED`,
  `clientVersion < minClientVersion → CLIENT_OUTDATED`.
* **Default policy** is deliberately dev-friendly: `requireSIP/MDM/StandardAccount/AAC` all `false`,
  `blockExternalDisplay: true` with `externalDisplayAction: "WARN"` (so a second display warns rather than blocks).
  Edit it in the console's Policy panel or with `PUT /api/v1/admin/policy`.
* **`policy.mode`** in a session response comes from the device's enrollment token. An unknown `deviceId`
  (e.g. the client kept one across a mock restart) is accepted and falls back to the policy's `mode`.
* **Commands** are a per-session FIFO; each heartbeat delivers at most one. `RELEASE` carries a fresh
  `authorizationId` and a 60 s `expiresAt`. A `RELEASE` is auto-queued once after submit and once when time runs out.
* **Risk level**: `GREEN`; `AMBER` if any `medium` event; `RED` if any `high` event or 3+ `medium`.
* **Server-side events** (`source: "server"` / `"admin"`) such as `RELEASE_CODE_ISSUED`, `UNLOCK_COMPLETED`,
  `UNLOCK_DENIED`, `RELEASE_QUEUED`, `COMMAND_RELEASE_DELIVERED` are added to the timeline so the console
  shows the whole story; client events keep `source: "client"`.
* **Session `status`** values: `active`, `submitted`, `released`, `terminated`, `expired`. The heartbeat
  `status` (`locked|unlocked|warning`) is stored separately as `clientStatus`.
* CORS is wide open (`*`) with OPTIONS preflight handling. JSON bodies are capped at 1 MiB.

## Exam page (`public/exam.html`)

* Defines `window.LockdownBridge.onMessage` **before** sending `READY`, then handles `SESSION_STATUS`,
  `WARNING` (toast), `EXIT_DENIED` (toast), `LOCKDOWN_STATE` (badge).
* Sends `READY`, `REPORT_EVENT` (`ANSWER_SAVED` per answer), `GET_SESSION_STATUS` (every 30 s), `REQUEST_EXIT`
  ("Request exit" link), `SUBMIT_COMPLETE` (after a successful `POST /submit`).
* Reads the session id from the URL, fetches the token via the mock `page-token` endpoint, loads questions.
* Submit uses an in-page modal (WKWebView ignores `window.confirm` unless native implements `WKUIDelegate`).
  When the countdown reaches zero the page auto-submits.
* Blocks the context menu, text selection outside inputs, drag/drop, and Cmd/Ctrl+P/S/U/O.
* **Browser test mode**: if `window.webkit.messageHandlers.lockdown` is absent, a yellow banner appears and
  `SESSION_STATUS` is simulated from `GET /api/v1/admin/sessions`.

## Teacher console (`public/admin.html`)

Polls `/api/v1/admin/sessions` every 3 s. Per session: status, lockdown mode badge, displays, heartbeat age,
risk, event count, and buttons **Release code** (shows the 6-digit code with a live 60 s countdown),
**Remote release**, **Warn…**, **Terminate**, **Events** (expandable timeline, refreshed while open).
The Policy panel edits every §4 field; `allowedDomains` is comma-separated and `allowedApps` is one
`bundleId[:teamId]` per line. Header shows `/healthz` status.

## curl cheat-sheet

```sh
B=http://localhost:4000; J='Content-Type: application/json'

# 1. Enroll (school mode)
curl -s -X POST $B/api/v1/devices/enroll -H "$J" -d '{"enrollmentToken":"SCHOOL-DEMO","platform":"macos","osVersion":"26.6.2","clientVersion":"0.1.0","hardwareId":"HW-1","deviceName":"Test Mac"}'
# -> {"deviceId":"dev_783d30","deviceToken":"devtok_…","mode":"school","minClientVersion":"0.1.0"}

# 2. Start a session (passing preflight)
curl -s -X POST $B/api/v1/sessions -H "$J" -d '{"studentCode":"1025","examCode":"MATH101","deviceId":"dev_783d30","preflight":{"osVersion":"26.6.2","sipEnabled":true,"mdmEnrolled":false,"accountType":"admin","displayCount":1,"screenSharingActive":false,"aacEntitlementPresent":false,"cameraAuthorized":"authorized","microphoneAuthorized":"denied","internetReachable":true,"clientVersion":"0.1.0"}}'
# -> sessionId, sessionToken, expiresAt, student, exam, examUrl, policy
SID=sess_af8a; TOK=sesstok_…; A="Authorization: Bearer $TOK"

# 3. Heartbeat (pops one queued command, if any)
curl -s -X POST $B/api/v1/sessions/$SID/heartbeat -H "$J" -H "$A" -d '{"status":"locked","displayCount":1,"lockdownMode":"kiosk-fallback","uptimeSeconds":12}'

# 4. Events
curl -s -X POST $B/api/v1/sessions/$SID/events -H "$J" -H "$A" -d '{"events":[{"type":"LOCKDOWN_FALLBACK","severity":"medium","timestamp":"2026-09-12T10:00:01Z","metadata":{}}]}'

# 5. Teacher issues a release code; student enters it
curl -s -X POST $B/api/v1/admin/sessions/$SID/release-code          # -> {"releaseCode":"234366","expiresAt":"…"}
curl -s -X POST $B/api/v1/sessions/$SID/unlock -H "$J" -H "$A" -d '{"releaseCode":"234366","deviceId":"dev_783d30"}'
# -> {"authorized":true,"authorizationId":"auth_…","expiresAt":"…"}; second use -> 403 RELEASE_CODE_USED

# 6. Remote commands (delivered on the next heartbeat, FIFO)
curl -s -X POST $B/api/v1/admin/sessions/$SID/release
curl -s -X POST $B/api/v1/admin/sessions/$SID/warn -H "$J" -d '{"message":"Eyes on your own screen"}'
curl -s -X POST $B/api/v1/admin/sessions/$SID/terminate

# 7. Submit -> next heartbeat returns RELEASE (reason "Exam submitted")
curl -s -X POST $B/api/v1/sessions/$SID/submit -H "$J" -H "$A" -d '{}'

# 8. Timeline and policy
curl -s $B/api/v1/admin/sessions
curl -s $B/api/v1/admin/sessions/$SID/events
curl -s $B/api/v1/admin/policy
curl -s -X PUT $B/api/v1/admin/policy -H "$J" -d '{"requireSIP":true,"externalDisplayAction":"BLOCK_START"}'
curl -s -X DELETE $B/api/v1/admin/policy      # reset to defaults

# 9. Force a PREFLIGHT_FAILED
curl -s -X PUT $B/api/v1/admin/policy -H "$J" -d '{"requireSIP":true}'
curl -s -X POST $B/api/v1/sessions -H "$J" -d '{"studentCode":"3","examCode":"SCI202","deviceId":"dev_783d30","preflight":{"sipEnabled":false,"displayCount":1,"clientVersion":"0.1.0"}}'
# -> 403 {"error":{"code":"PREFLIGHT_FAILED",…},"failures":["SIP_DISABLED"]}

# Mock-only: token + questions for the exam page
curl -s $B/api/v1/sessions/$SID/page-token
curl -s $B/api/v1/exams/MATH101/questions -H "$A"
```
