# Avaibe Exam: mock server

A small Node.js server that stands in for the exam platform during development. It serves the API
the Windows client talks to, the exam page, and a teacher console.

## Run

Requires Node.js 18 or newer. There are no dependencies, so no `npm install`.

```bash
node server.js
```

It listens on port 4000. Use `PORT=5000 node server.js` for a different port.

| Page | Address |
|---|---|
| Teacher console | http://localhost:4000/admin |
| Health check | http://localhost:4000/healthz |

## Demo values

| Field | Value |
|---|---|
| Enrollment token | `SCHOOL-DEMO` (school mode) or `BYOD-DEMO` (personal device) |
| Student code | Any value |
| Exam code | `DEMO` (5 min), `MATH101` (60 min), `SCI202` (45 min) |

## Development only

- All data lives in memory and is lost when the server stops.
- It accepts any student code.
- The teacher console has no login. Do not expose this server to the internet.
