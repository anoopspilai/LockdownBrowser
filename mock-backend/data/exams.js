// Exam catalogue for the mock backend. Codes and durations are fixed by docs/CONTRACT.md §3/§8.
// Each exam carries five sample multiple-choice questions used by public/exam.html.

export const EXAMS = {
  MATH101: {
    code: "MATH101",
    title: "Mathematics 101",
    durationMinutes: 60,
    questions: [
      { id: "q1", text: "What is 7 × 8?", options: ["54", "56", "64", "48"], answer: "56" },
      { id: "q2", text: "Solve for x: 2x + 6 = 14", options: ["2", "3", "4", "5"], answer: "4" },
      { id: "q3", text: "What is the square root of 144?", options: ["10", "11", "12", "14"], answer: "12" },
      { id: "q4", text: "Which of these is a prime number?", options: ["21", "27", "29", "33"], answer: "29" },
      { id: "q5", text: "What is 15% of 200?", options: ["20", "25", "30", "35"], answer: "30" }
    ]
  },
  SCI202: {
    code: "SCI202",
    title: "Science 202",
    durationMinutes: 45,
    questions: [
      { id: "q1", text: "What is the chemical symbol for water?", options: ["H2O", "CO2", "NaCl", "O2"], answer: "H2O" },
      { id: "q2", text: "Which planet is closest to the Sun?", options: ["Venus", "Earth", "Mercury", "Mars"], answer: "Mercury" },
      { id: "q3", text: "What force keeps planets in orbit around the Sun?", options: ["Magnetism", "Gravity", "Friction", "Inertia"], answer: "Gravity" },
      { id: "q4", text: "What part of the cell contains genetic material?", options: ["Ribosome", "Nucleus", "Mitochondrion", "Membrane"], answer: "Nucleus" },
      { id: "q5", text: "At sea level, water boils at approximately:", options: ["90 °C", "100 °C", "110 °C", "120 °C"], answer: "100 °C" }
    ]
  },
  DEMO: {
    code: "DEMO",
    title: "Demo Exam",
    durationMinutes: 5,
    questions: [
      { id: "q1", text: "What colour is the sky on a clear day?", options: ["Green", "Blue", "Red", "Yellow"], answer: "Blue" },
      { id: "q2", text: "How many days are in a week?", options: ["5", "6", "7", "8"], answer: "7" },
      { id: "q3", text: "Which animal is known as man's best friend?", options: ["Cat", "Dog", "Horse", "Parrot"], answer: "Dog" },
      { id: "q4", text: "What is 2 + 2?", options: ["3", "4", "5", "22"], answer: "4" },
      { id: "q5", text: "Which of these is a fruit?", options: ["Carrot", "Potato", "Apple", "Onion"], answer: "Apple" }
    ]
  }
};

/** Return questions without the answer key (the page must never see the correct answers). */
export function publicQuestions(code) {
  const exam = EXAMS[code];
  if (!exam) return null;
  return exam.questions.map(({ id, text, options }) => ({ id, text, options }));
}
