using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AvaibeExam.Browser;
using AvaibeExam.Core;
using AvaibeExam.Models;
using AvaibeExam.Networking;
using AvaibeExam.Security;

// UI-free logic tests for the Windows client (see the .csproj for how to run).
// Sections 1-3 need nothing. Section 4 talks to a running server and CREATES test data there
// (a student, an exam and an enrollment token with random codes): point it at a test server.

int pass = 0, fail = 0;
void Check(bool ok, string name) { if (ok) { pass++; Console.WriteLine("  ok   " + name); } else { fail++; Console.WriteLine("  FAIL " + name); } }
void Section(string s) => Console.WriteLine("== " + s);

Section("1. Sign-in field messages");
Check(AppState.ValidateLoginFields("1201", "") == "Exam code is required.", "student given, exam blank -> only exam is named");
Check(AppState.ValidateLoginFields("", "MATHS1") == "Student code is required.", "exam given, student blank -> only student is named");
Check(AppState.ValidateLoginFields("", "") == "Student code and exam code are required.", "both blank -> both named");
Check(AppState.ValidateLoginFields(new string('x', 65), "MATHS1") == "Student code must be 64 characters or fewer.", "student too long");
Check(AppState.ValidateLoginFields("1201", "MATHS1") == null, "valid -> no message");

Section("2. Access code in the start request");
string StartJson(string? code) => Json.Serialize(new SessionStartRequest { StudentCode = "1201", ExamCode = "MATHS1", DeviceId = "dev_x", AccessCode = SessionStartRequest.NormalizeAccessCode(code) });
Check(!StartJson(null).Contains("accessCode"), "no code -> key omitted");
Check(!StartJson("   ").Contains("accessCode"), "blank code -> key omitted (not a wrong guess)");
Check(JsonNode.Parse(StartJson("  MATHS1 \n"))!["accessCode"]!.GetValue<string>() == "MATHS1", "code trimmed");
Check(JsonNode.Parse(StartJson(new string('a', 200)))!["accessCode"]!.GetValue<string>().Length == 64, "code capped at 64");
Check(JsonNode.Parse(StartJson(null))!["studentCode"]!.GetValue<string>() == "1201", "student code sent as studentCode");

Section("3. Resources links, Back to exam, subresources");
Check(ExamWebView.WwwTwin("google.com") == "www.google.com" && ExamWebView.WwwTwin("www.google.com") == "google.com", "www twin both ways");
Check(ExamWebView.WwwTwin("127.0.0.1") == null && ExamWebView.WwwTwin("localhost") == null, "no twin for IP or localhost");
var web = new ExamWebView();
var linkPolicy = new Policy { AllowedDomains = new List<string> { "localhost" }, AllowedLinks = new List<AllowedLink> {
    new AllowedLink { Label = "Google", Url = "https://google.com/" },
    new AllowedLink { Label = "Algebra", Url = "https://en.wikipedia.org/wiki/Algebra" } } };
web.ConfigureLinks(linkPolicy);
web.SetExamForTest(new Uri("http://localhost:4000/exam/launch?lt=abc"), "sess_0123456789abcdef0123456789abcdef");
bool Nav(string u) => web.IsNavigationAllowed(u, out _);
Check(Nav("https://google.com/"), "saved link opens");
Check(Nav("https://www.google.com/"), "google.com -> www.google.com redirect allowed");
Check(Nav("https://www.google.com/search?q=algebra"), "search inside the allowed site");
Check(!Nav("https://mail.google.com/"), "other subdomain blocked");
Check(!Nav("https://www.google.com.evil.com/"), "look-alike host blocked");
Check(!Nav("http://www.google.com/"), "scheme must match");
Check(!Nav("https://en.wikipedia.org/wiki/Cheating"), "path prefix enforced");
Check(!Nav("https://www.wikipedia.org/"), "twin only for the exact host");
Check(Nav("http://localhost:4000/exam/sess_0123456789abcdef0123456789abcdef"), "exam page allowed");
Check(web.BackToExamTarget == "http://localhost:4000/exam/sess_0123456789abcdef0123456789abcdef", "Back to exam -> real exam page, not the one-time link: " + web.BackToExamTarget);
Check(!web.IsSubresourceAllowed(new Uri("https://www.gstatic.com/x.js")), "exam page: other hosts' scripts blocked");
Check(web.IsSubresourceAllowed(new Uri("http://localhost:4000/exam.js")), "exam page: own scripts allowed");
web.NoteTopLevelNavigation(new Uri("https://www.google.com/"));
Check(web.IsSubresourceAllowed(new Uri("https://www.gstatic.com/x.js")), "Google page: its own assets load");
web.NoteTopLevelNavigation(new Uri("http://localhost:4000/exam/sess_0123456789abcdef0123456789abcdef"));
Check(!web.IsSubresourceAllowed(new Uri("https://www.gstatic.com/x.js")), "back on exam page: strict again");

var baseUrl = Environment.GetEnvironmentVariable("AVAIBE_TEST_BASE_URL");
var adminUser = Environment.GetEnvironmentVariable("AVAIBE_TEST_ADMIN");
var adminPass = Environment.GetEnvironmentVariable("AVAIBE_TEST_PASSWORD");
if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(adminUser) || string.IsNullOrEmpty(adminPass))
{
    Console.WriteLine("== 4. Live protocol: skipped (set AVAIBE_TEST_BASE_URL, AVAIBE_TEST_ADMIN, AVAIBE_TEST_PASSWORD)");
    Console.WriteLine($"\n{pass} passed, {fail} failed");
    return fail == 0 ? 0 : 1;
}
Section("4. Live protocol against " + baseUrl + " (creates test data)");

// Teacher console session (cookie + CSRF header), then provision a student, exam and token.
var cookies = new CookieContainer();
using var admin = new HttpClient(new HttpClientHandler { CookieContainer = cookies }) { BaseAddress = new Uri(baseUrl) };
admin.DefaultRequestHeaders.Add("X-Requested-With", "AvaibeAdmin");
var login = await admin.PostAsJsonAsync("/api/v1/auth/login", new { username = adminUser, password = adminPass });
Check(login.IsSuccessStatusCode, "teacher console login");
var suffix = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(3));
var studentCode = "lt" + suffix.ToLowerInvariant();
var examCode = "LT" + suffix;
Check((await admin.PostAsJsonAsync("/api/v1/admin/students", new { code = studentCode, name = "Logic Test" })).IsSuccessStatusCode, "test student created");
Check((await admin.PostAsJsonAsync("/api/v1/admin/exams", new {
    code = examCode, title = "Logic test", durationMinutes = 10, openToAll = true, accessCode = examCode, releaseOnSubmit = "teacher",
    questions = new[] { new { text = "2+2?", options = new[] { "3", "4" } } },
    allowedLinks = new[] { new { label = "Google", url = "https://google.com/" } } })).IsSuccessStatusCode, "test exam created with access code and a Google link");
var tokenResp = await admin.PostAsJsonAsync("/api/v1/admin/enrollment-tokens", new { mode = "school", label = "logic test" });
var enrollmentToken = JsonNode.Parse(await tokenResp.Content.ReadAsStringAsync())!["token"]!.GetValue<string>();

var api = new ApiClient(baseUrl);
var enroll = await api.EnrollAsync(new EnrollRequest { EnrollmentToken = enrollmentToken, OsVersion = "10.0.22631", HardwareId = "HW-LOGIC-TEST", DeviceName = "Logic test" });
Check(enroll.DeviceId.StartsWith("dev_") && !string.IsNullOrEmpty(enroll.ServerPublicKey), "enrolled, server key received");
api.SetDeviceId(enroll.DeviceId); api.SetDeviceToken(enroll.DeviceToken);
var verifier = new CommandVerifier();
verifier.SetPinnedKey(enroll.ServerPublicKey, enroll.KeyId);

SessionStartRequest Start(string? code) => new SessionStartRequest { StudentCode = studentCode, ExamCode = examCode, DeviceId = enroll.DeviceId, AccessCode = SessionStartRequest.NormalizeAccessCode(code) };
async Task<string> StartError(string? code)
{
    try { await api.StartSessionAsync(Start(code)); return "started"; }
    catch (ApiException ex) { return ex.Code + " | " + ex.Message; }
}
var noCode = await StartError(null);
Check(noCode.StartsWith("ACCESS_CODE_REQUIRED") && noCode.Contains("Access code box"), "no code -> clear message: " + noCode);
var wrong = await StartError(examCode.ToLowerInvariant());
Check(wrong.StartsWith("INVALID_ACCESS_CODE"), "wrong (lower-case) code -> INVALID_ACCESS_CODE: " + wrong);
var session = await api.StartSessionAsync(Start(" " + examCode + " "));
Check(session.SessionId.StartsWith("sess_"), "correct code with spaces -> session started");
Check(session.Policy!.AllowedDomains.Contains("google.com") && session.Policy.AllowedDomains.Contains("www.google.com"), "server policy includes www twin: " + string.Join(",", session.Policy.AllowedDomains));
api.SetSessionToken(session.SessionToken);
verifier.UpdateServerTime(session.ServerTime);

var beat1 = await api.HeartbeatAsync(session.SessionId, new HeartbeatRequest { Status = HeartbeatStatus.Locked, DisplayCount = 1, LockdownMode = LockdownMode.KioskFallback, UptimeSeconds = 5 });
Check(beat1.Command == null, "first heartbeat: no command");
Check((await admin.PostAsJsonAsync($"/api/v1/admin/sessions/{session.SessionId}/release", new { reason = "windows logic test" })).IsSuccessStatusCode, "teacher remote release");

var beat2 = await api.HeartbeatAsync(session.SessionId, new HeartbeatRequest { Status = HeartbeatStatus.Locked, DisplayCount = 1, LockdownMode = LockdownMode.KioskFallback, UptimeSeconds = 15 });
verifier.UpdateServerTime(beat2.ServerTime);
Check(beat2.Command?.Type == HeartbeatCommandType.Release && beat2.Command.Authorization != null, "heartbeat delivers signed RELEASE");
Check(verifier.Verify(beat2.Command!.Authorization, "RELEASE", session.SessionId, enroll.DeviceId, out var why1) != null, "Windows verifier accepts the server's signature " + why1);
Check(verifier.Verify(beat2.Command.Authorization, "RELEASE", session.SessionId, enroll.DeviceId, out var why2) == null, "replaying the same release is refused: " + why2);
Check(verifier.Verify(beat2.Command.Authorization, "RELEASE", "sess_ffffffffffffffffffffffffffffffff", enroll.DeviceId, out var why3) == null && why3 == "session-mismatch", "release for another session refused: " + why3);
var tampered = JsonSerializer.Deserialize<CommandAuthorization>(JsonSerializer.Serialize(beat2.Command.Authorization))!;
tampered.Nonce = new string('0', 32);
Check(verifier.Verify(tampered, "RELEASE", session.SessionId, enroll.DeviceId, out var why4) == null, "tampered release refused: " + why4);
using (var otherKey = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256))
{
    var fake = new CommandVerifier();
    fake.SetPinnedKey(Convert.ToBase64String(otherKey.ExportSubjectPublicKeyInfo()), enroll.KeyId);
    Check(fake.Verify(beat2.Command.Authorization, "RELEASE", session.SessionId, enroll.DeviceId, out var why5) == null, "release signed by a different server refused: " + why5);
}

Console.WriteLine($"\n{pass} passed, {fail} failed");
return fail == 0 ? 0 : 1;
