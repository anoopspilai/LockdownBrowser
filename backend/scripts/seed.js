#!/usr/bin/env node
// Seeds demo data: admin user, exams DEMO/MATH101/SCI202, students, and one school + one byod enrollment token.
// Idempotent for users/exams/students; enrollment tokens are created fresh on every run and printed once.
import { createContext, ensureAdmin } from "../src/app.js";
import { createLogger } from "../src/log.js";
import * as exams from "../src/services/exams.js";
import * as students from "../src/services/students.js";
import { issueEnrollmentToken } from "../src/auth/device.js";

const ctx = createContext({ log: createLogger("warn") });
const out = (s) => process.stdout.write(s + "\n");

const generated = await ensureAdmin(ctx);
if (generated) out(`Created staff user "admin" with password: ${generated}   (shown once — change it after signing in)`);
else out(`Staff user(s) already exist (set AVAIBE_ADMIN_PASSWORD before the first run to choose the admin password).`);

const EXAMS = [
  {
    code: "DEMO", title: "Demo Exam", durationMinutes: 5, releaseOnSubmit: "auto",
    questions: [
      { id: "q1", text: "What colour is the sky on a clear day?", options: ["Green", "Blue", "Red", "Yellow"], answer: "Blue" },
      { id: "q2", text: "How many days are in a week?", options: ["5", "6", "7", "8"], answer: "7" },
      { id: "q3", text: "Which animal is known as man's best friend?", options: ["Cat", "Dog", "Horse", "Parrot"], answer: "Dog" },
      { id: "q4", text: "What is 2 + 2?", options: ["3", "4", "5", "22"], answer: "4" },
      { id: "q5", text: "Which of these is a fruit?", options: ["Carrot", "Potato", "Apple", "Onion"], answer: "Apple" }
    ],
    allowedLinks: [{ label: "Formula sheet", url: "/resources/formula-sheet.html" }]
  },
  {
    code: "MATH101", title: "Mathematics 101", durationMinutes: 60, releaseOnSubmit: "teacher",
    questions: [
      { id: "q1", text: "What is 7 × 8?", options: ["54", "56", "64", "48"], answer: "56" },
      { id: "q2", text: "Solve for x: 2x + 6 = 14", options: ["2", "3", "4", "5"], answer: "4" },
      { id: "q3", text: "What is the square root of 144?", options: ["10", "11", "12", "14"], answer: "12" },
      { id: "q4", text: "Which of these is a prime number?", options: ["21", "27", "29", "33"], answer: "29" },
      { id: "q5", text: "What is 15% of 200?", options: ["20", "25", "30", "35"], answer: "30" }
    ],
    allowedLinks: [{ label: "Formula sheet", url: "/resources/formula-sheet.html" }]
  },
  {
    code: "SCI202", title: "Science 202", durationMinutes: 45, releaseOnSubmit: "teacher",
    questions: [
      { id: "q1", text: "What is the chemical symbol for water?", options: ["H2O", "CO2", "NaCl", "O2"], answer: "H2O" },
      { id: "q2", text: "Which planet is closest to the Sun?", options: ["Venus", "Earth", "Mercury", "Mars"], answer: "Mercury" },
      { id: "q3", text: "What force keeps planets in orbit around the Sun?", options: ["Magnetism", "Gravity", "Friction", "Inertia"], answer: "Gravity" },
      { id: "q4", text: "What part of the cell contains genetic material?", options: ["Ribosome", "Nucleus", "Mitochondrion", "Membrane"], answer: "Nucleus" },
      { id: "q5", text: "At sea level, water boils at approximately:", options: ["90 °C", "100 °C", "110 °C", "120 °C"], answer: "100 °C" }
    ],
    allowedLinks: []
  }
];

for (const e of EXAMS) {
  if (exams.getByCode(ctx, e.code)) {
    out(`Exam ${e.code} exists — skipped`);
    continue;
  }
  exams.upsert(ctx, e);
  out(`Created exam ${e.code} (${e.durationMinutes} min, ${e.questions.length} questions, release=${e.releaseOnSubmit})`);
}

const STUDENTS = [["1025", "Demo Student"], ["2001", "Amira Haddad"], ["2002", "Ben Okafor"], ["2003", "Chloé Martin"], ["2004", "Daniel Reyes"], ["2005", "Eun-ji Park"]];
let created = 0;
for (const [code, name] of STUDENTS) {
  if (students.getByCode(ctx, code)) continue;
  students.create(ctx, { code, name });
  created++;
}
out(`Students: ${created} created, ${STUDENTS.length - created} already existed`);

const school = issueEnrollmentToken(ctx, { mode: "school", label: "seed (school)", createdBy: "seed" });
const byod = issueEnrollmentToken(ctx, { mode: "byod", label: "seed (byod)", createdBy: "seed" });
out("");
out("Enrollment tokens (single use, 24 h, shown once):");
out(`  school: ${school.token}`);
out(`  byod:   ${byod.token}`);
out("");
out("Demo student codes: 1025, 2001-2005.  Exams: DEMO (5 min, auto release), MATH101 (60), SCI202 (45).");
ctx.db.close();
