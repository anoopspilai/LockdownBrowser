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
Check(!web.IsExternalExam && !web.IsPopupLoadedInPlace("http://localhost:4000/exam/x"), "questions exam: popups always blocked, even to the exam origin");

Section("5. External exams");
const string extStart = "https://www.testwise.com/";
const string extSession = "sess_fedcba9876543210fedcba9876543210";
var extPolicy = new Policy
{
    ExamMode = "EXTERNAL",
    AllowedSites = new List<string> { "www.testwise.com", "testwise.com", "*.testwise.com", "accounts.google.com" },
}.Normalized();
Check(extPolicy.IsExternalExam && extPolicy.ExamMode == "external", "examMode external (any case) -> external");
var ext = new ExamWebView();
ext.ConfigureLinks(extPolicy);
ext.SetExamForTest(new Uri(extStart), extSession);
bool ExtNav(string u) => ext.IsNavigationAllowed(u, out _);
Check(ExamWebView.IsExamUrlAcceptable(new Uri(extStart), extPolicy, out var extWhy), "start URL acceptable under the external rule " + extWhy);
Check(ExtNav(extStart), "start page allowed");
Check(ExtNav("https://app.testwise.com/x"), "subdomain via *.testwise.com allowed");
Check(ExtNav("https://accounts.google.com/o/oauth2"), "Google sign-in allowed");
Check(!ExtNav("https://mail.google.com/"), "other Google host blocked");
Check(!ExtNav("https://testwise.com.evil.com/"), "look-alike host blocked");
Check(!ExtNav("http://www.testwise.com/"), "plain http blocked for a public host");
Check(!ExtNav("https://user:pw@www.testwise.com/"), "userinfo blocked");
Check(!ExtNav("javascript:alert(1)") && !ExtNav("data:text/html,x") && !ExtNav("blob:https://www.testwise.com/1") && !ExtNav("file:///C:/x"), "javascript:/data:/blob:/file: blocked");
Check(!ExtNav("https://www.testwise.com/a/../b"), "dot segments blocked");
Check(ext.IsSubresourceAllowed(new Uri("https://cdn.anything.net/x.js")), "subresources not filtered");
Check(ext.BackToExamTarget == extStart, "Back to exam -> start URL: " + ext.BackToExamTarget);
Check(ext.IsPopupLoadedInPlace("https://accounts.google.com/o/oauth2"), "allowed popup loads in the same view");
Check(!ext.IsPopupLoadedInPlace("https://evil.example/") && !ext.IsPopupLoadedInPlace("about:blank"), "other popups blocked");

var wildPolicy = new Policy { ExamMode = "external", AllowedSites = new List<string> { "*.com", "*.co.uk", "*.testwise.com" } };
var wild = new ExamWebView();
wild.ConfigureLinks(wildPolicy);   // not normalised on purpose: the matcher itself must refuse public-suffix wildcards
wild.SetExamForTest(new Uri(extStart), extSession);
Check(!wild.IsNavigationAllowed("https://example.com/", out _) && !wild.IsNavigationAllowed("https://bbc.co.uk/", out _), "*.com / *.co.uk never allow example.com / bbc.co.uk");
Check(!ExamWebView.IsExamUrlAcceptable(new Uri("https://example.com/"), wildPolicy, out _), "*.com never accepts example.com as a start URL");

var linkExtPolicy = new Policy { ExamMode = "external", AllowedSites = new List<string> { "www.testwise.com" },
    AllowedLinks = new List<AllowedLink> { new AllowedLink { Label = "Help", Url = "https://help.vendor.net/guide" } } }.Normalized();
var linkExt = new ExamWebView();
linkExt.ConfigureLinks(linkExtPolicy);
linkExt.SetExamForTest(new Uri(extStart), extSession);
Check(linkExt.IsNavigationAllowed("https://help.vendor.net/guide/page2", out _) && !linkExt.IsNavigationAllowed("https://help.vendor.net/other", out _), "allowed-link prefix rule still applies in external mode");
Check(!ExamWebView.IsExamUrlAcceptable(new Uri("https://evil.example/"), linkExtPolicy, out var badWhy) && badWhy == "host-not-in-allowedSites", "start URL outside allowedSites refused: " + badWhy);
var localExt = new Policy { ExamMode = "external", AllowedSites = new List<string> { "localhost" } }.Normalized();
Check(ExamWebView.IsExamUrlAcceptable(new Uri("http://localhost:4701/vendor"), localExt, out _), "http allowed for localhost start URL");

var junk = new List<string> { "https://x.com", "x.com/path", "x.com:443", "user@x.com", "a b.com", "*.com", "*.co.uk", "*x.com", "x.*.com", "*", "", "   ", "-bad.com", "WWW.Testwise.COM", "www.testwise.com", "*.Vendor.net" };
for (var i = 0; i < 80; i++) junk.Add("site" + i + ".example.org");
var normalized = new Policy { AllowedSites = junk }.Normalized();
Check(normalized.AllowedSites.Count == Policy.MaxAllowedSites, "allowedSites capped at 50: " + normalized.AllowedSites.Count);
Check(normalized.AllowedSites[0] == "www.testwise.com" && normalized.AllowedSites[1] == "*.vendor.net", "junk dropped, lower-cased, de-duplicated: " + string.Join(",", normalized.AllowedSites.Take(3)));
Check(!normalized.AllowedSites.Any(s => s.Contains('/') || s.Contains(':') || s.Contains('@') || s.Contains(' ') || s == "*.com" || s == "*.co.uk"), "no scheme/path/port/userinfo/public-suffix entries survive");
var missingMode = System.Text.Json.JsonSerializer.Deserialize<Policy>("{\"allowedSites\":null}")!.Normalized();
Check(!missingMode.IsExternalExam && missingMode.AllowedSites.Count == 0, "missing examMode -> questions, null allowedSites -> empty");
Check(!new Policy { ExamMode = "quiz" }.Normalized().IsExternalExam, "unknown examMode -> questions");
Check(!Policy.ConservativeDefault.IsExternalExam && Policy.ConservativeDefault.AllowedSites.Count == 0, "conservative default stays questions with no sites");

// Questions mode keeps its behaviour with allowedSites present (they are ignored there).
var qPolicy = new Policy { AllowedDomains = new List<string> { "localhost" }, AllowedSites = new List<string> { "*.testwise.com" } }.Normalized();
var q = new ExamWebView();
q.ConfigureLinks(qPolicy);
q.SetExamForTest(new Uri("http://localhost:4000/exam/launch?lt=abc"), extSession);
Check(!q.IsNavigationAllowed("https://app.testwise.com/", out _) && q.IsNavigationAllowed("http://localhost:4000/exam/" + extSession, out _), "questions mode: allowedSites ignored, exam origin allowed");
Check(q.BackToExamTarget == "http://localhost:4000/exam/" + extSession && !q.IsSubresourceAllowed(new Uri("https://cdn.anything.net/x.js")), "questions mode: Back to exam and subresource filtering unchanged");

// "I have finished" decision logic.
Check(AppState.CanStudentFinish(true, true, false, false, false, false, false, false), "finish enabled on a running external exam");
Check(!AppState.CanStudentFinish(false, true, false, false, false, false, false, false), "finish never for questions exams");
Check(!AppState.CanStudentFinish(true, true, false, false, false, true, false, false) && !AppState.CanStudentFinish(true, true, false, false, true, false, false, false), "finish disabled while submitting and after success");
Check(!AppState.CanStudentFinish(true, true, false, false, false, false, true, false) && !AppState.CanStudentFinish(true, true, false, false, false, false, false, true), "finish disabled while confirming or behind an overlay");
Check(!AppState.CanStudentFinish(true, true, true, false, false, false, false, false) && !AppState.CanStudentFinish(true, true, false, true, false, false, false, false), "finish disabled after release or terminate");
var confirm = AppState.FinishConfirmation();
Check(confirm.HasCancel && confirm.OkText == "Finish" && confirm.CancelText == "Cancel" && confirm.FocusCancel &&
      confirm.Message.StartsWith("Only press this after you have submitted the test on the exam website."), "confirmation text and Finish/Cancel buttons");
Check(AppState.PostSubmitTexts(false, true, "x").Message == "Finished. Waiting for your teacher to release you.", "teacher release wording after finish");
Check(AppState.PostSubmitTexts(false, false, "x").Message == "Submitted. Waiting for your teacher to release you.", "time-up wording unchanged");
Check(EventType.StudentFinished == "STUDENT_FINISHED", "STUDENT_FINISHED event type");

Section("6. Frames and printing (Windows parity)");
Check(ExamWebView.IsInertFrameUrl("about:blank") && ExamWebView.IsInertFrameUrl("ABOUT:SRCDOC"), "empty frames recognised");
Check(!ExamWebView.IsInertFrameUrl("about:config") && !ExamWebView.IsInertFrameUrl("https://evil.example/"), "only empty frames are exempt");
Check(ExamWebView.PrintBlockScript.Contains("window") && ExamWebView.PrintBlockScript.Contains("print"), "print guard script present");

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

// External-website exams (§11): one exam per releaseOnSubmit value.
foreach (var release in new[] { "auto", "teacher" })
{
    var extCode = "LX" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(3));
    var created = await admin.PostAsJsonAsync("/api/v1/admin/exams", new {
        code = extCode, title = "External logic test (" + release + ")", durationMinutes = 10, openToAll = true, accessCode = extCode,
        releaseOnSubmit = release, kind = "external", startUrl = "https://www.testwise.com/", allowedSites = new[] { "*.testwise.com" } });
    Check(created.IsSuccessStatusCode, "external exam created (releaseOnSubmit=" + release + "): " + (int)created.StatusCode + " " + (created.IsSuccessStatusCode ? "" : await created.Content.ReadAsStringAsync()));
    if (!created.IsSuccessStatusCode) continue;

    SessionStartResponse extSess;
    try
    {
        extSess = await api.StartSessionAsync(new SessionStartRequest { StudentCode = studentCode, ExamCode = extCode, DeviceId = enroll.DeviceId, AccessCode = extCode });
    }
    catch (ApiException ex)
    {
        Check(false, "external session started: " + ex.Code + " | " + ex.Message);
        continue;
    }
    Check(extSess.Normalize() == null, "external session response usable");
    var extPol = extSess.PolicyOrDefault;
    Check(extSess.ExamUrl == "https://www.testwise.com/", "examUrl is the start address: " + extSess.ExamUrl);
    Check(extPol.ExamMode == "external" && extPol.IsExternalExam, "policy.examMode external: " + extPol.ExamMode);
    Check(extPol.AllowedSites.Contains("www.testwise.com") && extPol.AllowedSites.Contains("*.testwise.com"), "allowedSites has start host and teacher list: " + string.Join(",", extPol.AllowedSites));
    Check(ExamWebView.IsExamUrlAcceptable(new Uri(extSess.ExamUrl), extPol, out var liveWhy), "client accepts the server's start URL " + liveWhy);
    var liveWeb = new ExamWebView();
    liveWeb.ConfigureLinks(extPol);
    liveWeb.SetExamForTest(new Uri(extSess.ExamUrl), extSess.SessionId);
    Check(liveWeb.IsNavigationAllowed("https://app.testwise.com/test", out _) && !liveWeb.IsNavigationAllowed("https://mail.google.com/", out _) &&
          liveWeb.BackToExamTarget == extSess.ExamUrl, "client rules with the server policy");

    api.SetSessionToken(extSess.SessionToken);
    verifier.UpdateServerTime(extSess.ServerTime);
    var evResp = await api.PostEventsAsync(extSess.SessionId, new List<TelemetryEvent> {
        new TelemetryEvent(EventType.StudentFinished, EventSeverity.Info, new Dictionary<string, object?> { ["remainingSeconds"] = 540 }) });
    Check((evResp.Accepted ?? 0) == 1, "server accepts STUDENT_FINISHED event: accepted=" + evResp.Accepted);
    var submitted = await api.SubmitAsync(extSess.SessionId);
    Check(submitted.Release == release, "submit -> release " + submitted.Release + " (expected " + release + ")");
    var beat = await api.HeartbeatAsync(extSess.SessionId, new HeartbeatRequest { Status = HeartbeatStatus.Locked, DisplayCount = 1, LockdownMode = LockdownMode.KioskFallback, UptimeSeconds = 30 });
    verifier.UpdateServerTime(beat.ServerTime);
    if (release == "auto")
    {
        Check(beat.Command?.Type == HeartbeatCommandType.Release && beat.Command.Authorization != null, "auto: next heartbeat carries a signed RELEASE");
        Check(beat.Command != null && verifier.Verify(beat.Command.Authorization, "RELEASE", extSess.SessionId, enroll.DeviceId, out var whyExt) != null, "auto: verifier accepts that RELEASE");
    }
    else
    {
        Check(beat.Command == null || beat.Command.Type != HeartbeatCommandType.Release, "teacher: no RELEASE until the teacher releases");
    }
}

Console.WriteLine($"\n{pass} passed, {fail} failed");
return fail == 0 ? 0 : 1;
