using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace DotaArchive
{
    internal static class EngineDiagnosticsTests
    {
        private const string Account = "synthetic_private_account";
        private const string SteamId = "76561190000000001";
        private const string Secret = "SYNTHETIC_SECRET_TOKEN";
        private const string Url = "https://synthetic-secret.invalid/request?token=" + Secret;
        private const string Manifest = "3115444187078319616";
        private static int checks;
        private static string root;
        private static string shortFixtures;
        private static void Check(bool condition, string label) { if (!condition) throw new Exception("FAILED: " + label); checks++; }
        private static VersionEntry Plan()
        { return new VersionEntry { Depots = new List<DepotEntry> { new DepotEntry { DepotId = "571", ManifestId = "3694916840459487978", Bytes = 100 }, new DepotEntry { DepotId = "575", ManifestId = Manifest, Bytes = 100 } }, Files = new List<FileEntry> { new FileEntry { Path = "game/file.bin", Size = 1 } } }; }
        private sealed class Capture
        {
            internal readonly CliOutputParser Parser;
            internal readonly List<string> Public = new List<string>();
            internal int Abort, Inputs, Images;
            internal Capture()
            {
                Parser = new CliOutputParser(Plan(), root);
                Parser.Log += Public.Add;
                Parser.Progress += p => Public.Add(p.Phase + "|" + p.Message + "|" + p.CurrentFile);
                Parser.AbortRequested += () => Abort++;
                Parser.InputRequired += () => Inputs++;
                Parser.QrReady += qr => { Images++; qr.Dispose(); };
            }
            internal void Feed(string text, int stream = 0, int fragment = 3)
            { for (int i = 0; i < text.Length; i += fragment) Parser.Feed(stream, text.Substring(i, Math.Min(fragment, text.Length - i))); }
            internal void Safe(string label)
            {
                string exposed = String.Join("\n", Public) + "\n" + Parser.LastErrorCode + "\n" + Parser.LastErrorSummary;
                foreach (string forbidden in new[] { Account, SteamId, Secret, "synthetic-secret.invalid", "token=", "-username", "requestcode=", "\u2588", "C:\\private", "bad_result", "EvilException" })
                    Check(!exposed.Contains(forbidden), label + " suppresses private or arbitrary data");
            }
        }
        private static void Classified(string line, string code, bool fatal)
        {
            var c = new Capture(); c.Feed(line + "\r\n", 0, 1);
            Check(c.Parser.LastErrorCode == code && !String.IsNullOrEmpty(c.Parser.LastErrorSummary), "specific diagnostic " + code);
            Check(c.Parser.HasFatalError == fatal, "fatal classification " + code);
            c.Safe(code);
        }
        private static void ClassificationCases()
        {
            Classified("Depot 571, 575 not listed for app 570", "depot_not_listed", true);
            Classified("App 570 (Dota 2) is not available from this account.", "app_access_denied", true);
            Classified("Depot 575 is not available from this account.", "depot_access_denied", true);
            Classified("No valid depot key for 575, unable to download.", "depot_key_unavailable", true);
            Classified("No manifest request code was returned for depot 575 from app 570, manifest " + Manifest, "manifest_request_code_unavailable", true);
            foreach (string status in new[] { "401", "403", "404" })
                Classified("Encountered " + status + " for depot manifest 575 " + Manifest + ". Aborting.", "manifest_http_error", true);
            Classified("Unable to download manifest " + Manifest + " for depot 575", "manifest_unavailable", true);
            Classified("App 570 was not completely downloaded.", "download_incomplete", true);
            Classified("Error: " + Account + " " + SteamId + " " + Url, "engine_error", true);
            Classified("Could not connect to Steam after 5 tries", "steam_connection_failed", true);
            Classified("Unhandled exception. System.IO.IOException: " + Url + " C:\\private\\" + Account, "engine_exception", true);
            Classified("Failed to allocate file C:\\private\\" + Account + ": " + Url, "file_operation_failed", true);
            Classified("Failed to resize file to expected size C:\\private\\" + Account + ": " + Url, "file_operation_failed", true);
            Classified("Error: Unable to create install directories!", "file_operation_failed", true);
        }
        private static void PrivacyAndRetryCases()
        {
            var privateText = new Capture();
            foreach (string line in new[] {
                "SteamID " + SteamId + " token " + Secret,
                "Got manifest request code for depot 575 from app 570, manifest " + Manifest + ", result: 987654321",
                "http trace " + Url,
                "Success! Next time you can login with -username " + Account + " -remember-password instead of -qr.",
                "Got CDN auth token for " + Url + " result: AccessDenied (expires " + Account + ")",
                "Got depot key for 575 result: bad_result_" + Secret,
                "System.EvilException: " + Secret,
                "Depot " + SteamId + " not listed for app 570",
                "Encountered " + SteamId + " for depot manifest 575 " + Manifest + ". Aborting.",
                "Encountered 403 for depot manifest 575 " + SteamId + ". Aborting.",
                "App " + SteamId + " (" + Secret + ") is not available from this account."
            }) privateText.Feed(line + "\n", 1, 7);
            Check(privateText.Parser.AuthenticationSucceeded && privateText.Parser.AuthenticatedAccountName == Account, "account identity remains internal for retry");
            Check(privateText.Parser.SuppressedDiagnosticLineCount > 0, "suppressed diagnostics count available without raw details");
            privateText.Safe("mixed private diagnostics");
            var warning = new Capture();
            warning.Feed("Encountered error downloading depot manifest 575 " + Manifest + ": BadRequest\n");
            warning.Feed("Connection timeout downloading depot manifest 575 " + Manifest + ". Retrying.\n");
            Check(!warning.Parser.HasFatalError && !warning.Parser.AuthenticationFailure && warning.Abort == 0, "retryable manifest/network warnings never kill engine or trigger auth fallback");
            warning.Safe("retry warning");
            var priority = new Capture();
            priority.Feed("Encountered 403 for depot manifest 575 " + Manifest + ". Aborting.\n");
            string specific = priority.Parser.LastErrorSummary;
            priority.Feed("Unable to download manifest " + Manifest + " for depot 575\nApp 570 was not completely downloaded.\nError: " + Url + "\n");
            Check(priority.Parser.LastErrorCode == "manifest_http_error" && priority.Parser.LastErrorSummary == specific, "specific manifest cause survives generic and incomplete followups");
            Check(priority.Parser.Incomplete && priority.Parser.HasFatalError, "incomplete exit-zero signal remains fatal with a specific cause");
            priority.Safe("retained cause");
            var auth = new Capture();
            auth.Feed("Success! Next time you can login with -username " + Account + " -remember-password instead of -qr.\n");
            auth.Feed("Failed to authenticate with Steam: Failed to poll status with result FileNotFound " + Url + "\n", 1);
            Check(auth.Parser.AuthenticationSucceeded && auth.Parser.AuthenticationRace && auth.Parser.AuthenticationFailure && auth.Parser.HasFatalError && auth.Abort == 1, "late authentication race keeps existing bounded cached-retry signals");
            Check(auth.Parser.LastErrorCode == "authentication_poll_failed", "authentication poll failure has safe category");
            auth.Safe("authentication race");
            var failure = new Capture();
            failure.Feed("Access token was rejected " + Secret + "\n");
            Check(failure.Parser.AuthenticationFailure && !failure.Parser.AuthenticationRace && failure.Abort == 1 && failure.Parser.LastErrorCode == "authentication_failed", "rejected token keeps normal authentication fallback signals");
            failure.Safe("authentication failure");
            var prompt = new Capture(); prompt.Feed("Enter account password for \"" + Account + "\": ", 1, 1);
            Check(prompt.Parser.CredentialsRequired && prompt.Inputs == 1, "fragmented no-newline secret prompt still requests one QR fallback");
            prompt.Safe("password prompt");
            var fragment = new Capture();
            fragment.Feed("No valid depot key for 575, unable to download.", 1, 1); fragment.Parser.Complete(1);
            Check(fragment.Parser.LastErrorCode == "depot_key_unavailable", "EOF completes fragmented diagnostic without newline");
            fragment.Safe("EOF diagnostic");
        }
        private static string HistoricalCompatibility(string ids)
        { return "Historical depot compatibility: app 570, removed depot(s) " + ids + "; checking Steam access for the explicitly supplied manifests."; }
        private static string HistoricalIncomplete(string id)
        { return "Error: Historical depot " + id + " could not be prepared; the complete requested depot set was not downloaded."; }
        private static void HistoricalDiagnosticCases()
        {
            foreach (string ids in new[] { "571", "571, 575" })
            {
                var compatible = new Capture(); compatible.Feed(HistoricalCompatibility(ids) + "\n", 1, 1);
                Check(!compatible.Parser.HasFatalError && compatible.Parser.LastErrorCode == null && compatible.Parser.LastErrorSummary == null && compatible.Abort == 0 && compatible.Inputs == 0, "historical compatibility message is informational and does not alter authentication");
                Check(compatible.Public.Count == 1 && compatible.Public[0].Contains(ids), "historical compatibility identifies only requested depots");
                compatible.Safe("historical informational message");
            }
            Classified(HistoricalIncomplete("575"), "historical_depot_incomplete", true);
            const string sourceRefusal = "Error: -history will not infer a historical shared-depot source from another app's current metadata.";
            Classified(sourceRefusal, "historical_source_unverified", true);
            foreach (string prior in new[] { "Got depot key for 575 result: AccessDenied", "Depot 575 is not available from this account." })
            {
                var priority = new Capture(); priority.Feed(prior + "\n"); string code = priority.Parser.LastErrorCode, summary = priority.Parser.LastErrorSummary;
                priority.Feed(HistoricalIncomplete("575") + "\nApp 570 was not completely downloaded.\n");
                Check(priority.Parser.LastErrorCode == code && priority.Parser.LastErrorSummary == summary && priority.Parser.Incomplete, "historical incomplete preserves more specific permission/key cause");
                priority.Safe("historical priority");
            }
            foreach (string invalid in new[] { HistoricalCompatibility("571, 571"), HistoricalCompatibility("571, " + SteamId), HistoricalCompatibility("999"), HistoricalCompatibility("571, 575") + " " + Url })
            {
                var c = new Capture(); c.Feed(invalid + "\n", 1, 2);
                Check(c.Public.Count == 0 && c.Parser.SuppressedDiagnosticLineCount == 1 && !c.Parser.HasFatalError && c.Parser.LastErrorCode == null, "malformed or unrequested historical compatibility is suppressed");
                c.Safe("rejected historical informational message");
            }
            foreach (string invalid in new[] { HistoricalIncomplete(SteamId), HistoricalIncomplete("999"), HistoricalIncomplete("575") + " " + Url, sourceRefusal + " " + Url })
            {
                var c = new Capture(); c.Feed(invalid + "\n", 1, 2);
                Check(c.Parser.HasFatalError && c.Parser.LastErrorCode == "engine_error", "unrecognized historical Error line uses safe generic failure");
                c.Safe("rejected historical error message");
            }
        }
        private static void QrAndProgressCases()
        {
            var c = new Capture();
            var qr = new StringBuilder("Use the Steam Mobile App to sign in with this QR code:\n");
            for (int y = 0; y < 29; y++) { for (int x = 0; x < 29; x++) qr.Append(x >= 4 && y >= 4 && x < 25 && y < 25 && (x + y) % 2 == 0 ? "\u2588\u2588" : "  "); qr.Append('\n'); }
            c.Feed(qr.ToString(), 0, 1); Check(c.Images == 1, "complete QR is rendered once and stays out of text diagnostics");
            c.Feed("Downloading depot 571\n50.00% " + Path.Combine(root, "game", "file.bin") + "\n");
            Check(c.Parser.LastProgress.Percent == 25 && c.Parser.LastProgress.CurrentFile == "game/file.bin", "weighted progress and known relative file survive new classifier");
            c.Feed("100.00% C:\\private\\" + Account + "\\" + Secret + "\n");
            Check(c.Parser.LastProgress.CurrentFile == "", "unrecognized progress path stays hidden");
            c.Feed("Depot 571 - Downloaded 1 bytes (100 bytes uncompressed)\nDepot 575 - Downloaded 1 bytes (100 bytes uncompressed)\nTotal downloaded: 2 bytes (200 bytes uncompressed) from 2 depots\n");
            Check(c.Parser.LastProgress.Percent < 100 && c.Parser.LastProgress.CompletedDepots == 2 && !c.Parser.HasFatalError, "completed transfers still await final integrity check");
            c.Safe("QR and transfer");
            var longLine = new Capture(); longLine.Feed(new String('x', 40000) + Secret + "\nNo valid depot key for 575, unable to download.\n", 0, 4096);
            Check(longLine.Parser.LastErrorCode == "depot_key_unavailable", "oversized unknown line cannot block next safe diagnostic"); longLine.Safe("oversized line");
        }
        private static int FakeEngine(string[] args)
        {
            Console.OutputEncoding = DownloadEngine.GetRedirectedConsoleEncoding();
            string directory = args[Array.IndexOf(args, "-dir") + 1];
            string mode = File.ReadAllText(Path.Combine(directory, "scenario.txt"));
            File.WriteAllText(Path.Combine(directory, "public-arguments.json"), new JavaScriptSerializer().Serialize(new { History = args.Contains("-history"), App = args[Array.IndexOf(args, "-app") + 1], Depots = ListArgument(args, "-depot"), Manifests = ListArgument(args, "-manifest"), ManifestOnly = args.Contains("-manifest-only"), Validate = args.Contains("-validate"), DepotFlags = args.Count(a => a == "-depot"), ManifestFlags = args.Count(a => a == "-manifest"), HistoryFlags = args.Count(a => a == "-history") }));
            Console.WriteLine("Success! Next time you can login with -username " + Account + " -remember-password instead of -qr.");
            for (int i = 0; i < 600; i++) Console.WriteLine("raw " + Account + " " + SteamId + " " + Url);
            if (mode == "specific")
            {
                Console.WriteLine("Got depot key for 575 result: AccessDenied");
                Console.WriteLine("No valid depot key for 575, unable to download.");
                for (int i = 0; i < 600; i++) Console.WriteLine("Error: " + Secret);
                Console.WriteLine("App 570 was not completely downloaded.");
                return 0;
            }
            if (mode == "unclassified") return 23;
            Console.WriteLine("Total downloaded: 0 bytes (0 bytes uncompressed) from 2 depots"); return 0;
        }
        private static string[] ListArgument(string[] args, string flag)
        { return args.Skip(Array.IndexOf(args, flag) + 1).TakeWhile(a => !a.StartsWith("-", StringComparison.Ordinal)).ToArray(); }
        private static void DiagnosticFiles()
        {
            string executable = Assembly.GetExecutingAssembly().Location, hash;
            using (var sha = SHA256.Create()) using (var input = File.OpenRead(executable)) hash = BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
            // Keep the fixture short enough for a Framework console test without
            // the production application's long-path manifest/configuration.
            string parent = Path.Combine(shortFixtures, Guid.NewGuid().ToString("N").Substring(0, 10));
            foreach (string mode in new[] { "specific", "unclassified", "success" })
            {
                string directory = Path.Combine(parent, mode); Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "scenario.txt"), mode);
                using (var engine = new DownloadEngine(executable, hash)) using (var timeout = new CancellationTokenSource(15000))
                {
                    int code = engine.RunAsync(Plan(), directory, Account, false, DownloadMode.ManifestOnly, timeout.Token).GetAwaiter().GetResult();
                    Check(code == (mode == "specific" ? 1 : mode == "unclassified" ? 23 : 0), "fake process exit semantics " + mode);
                    Check(!String.IsNullOrEmpty(engine.LastDiagnosticPath) && File.Exists(engine.LastDiagnosticPath) && Path.GetFullPath(engine.LastDiagnosticPath).StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "diagnostic is stored only under target " + mode);
                    string text = File.ReadAllText(engine.LastDiagnosticPath);
                    foreach (string forbidden in new[] { Account, SteamId, Secret, "synthetic-secret.invalid", "-username", "\u2588" }) Check(!text.Contains(forbidden), "persisted diagnostic contains no private raw data " + mode);
                    var report = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(text);
                    Check(!(bool)report["RawOutputStored"] && Convert.ToInt32(report["SuppressedDiagnosticLineCount"]) >= 600, "redaction and suppressed count persist " + mode);
                    Check(((System.Collections.ICollection)report["Messages"]).Count <= 512, "diagnostic messages are bounded " + mode);
                    if (mode == "specific") Check(engine.LastErrorCode == "depot_key_unavailable" && (string)report["ErrorCode"] == "depot_key_unavailable" && engine.LastErrorSummary.Contains("AccessDenied"), "specific failure persists across zero exit and generic followups");
                    if (mode == "unclassified") Check(engine.LastErrorCode == "unclassified_exit" && engine.LastErrorSummary.Contains("23") && (string)report["ErrorCode"] == "unclassified_exit" && ((string)report["ErrorSummary"]).Contains("23"), "unknown nonzero exit persists safe category and user summary");
                    if (mode == "success") Check(engine.LastErrorCode == null, "successful engine does not invent fatal diagnostic");
                }
            }
        }
        private static void HistoricalModeArguments()
        {
            string executable = Assembly.GetExecutingAssembly().Location, hash;
            using (var sha = SHA256.Create()) using (var input = File.OpenRead(executable)) hash = BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
            string parent = Path.Combine(shortFixtures, Guid.NewGuid().ToString("N").Substring(0, 10));
            foreach (DownloadMode mode in new[] { DownloadMode.ManifestOnly, DownloadMode.ClientFiles })
            foreach (int option in new[] { 0, 1, 2 })
            {
                string directory = Path.Combine(parent, mode.ToString() + option); Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "scenario.txt"), "success");
                using (var engine = option == 0 ? new DownloadEngine(executable, hash) : new DownloadEngine(executable, hash, option == 2))
                using (var timeout = new CancellationTokenSource(15000))
                {
                    Check(engine.RunAsync(Plan(), directory, Account, false, mode, timeout.Token).GetAwaiter().GetResult() == 0, "historical mode fake engine starts " + mode + option);
                    var received = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(directory, "public-arguments.json")));
                    Check((bool)received["History"] == (option == 2) && Convert.ToInt32(received["HistoryFlags"]) == (option == 2 ? 1 : 0), "historical mode is opt-in and appears once " + mode + option);
                    Check((string)received["App"] == "570" && Convert.ToInt32(received["DepotFlags"]) == 1 && Convert.ToInt32(received["ManifestFlags"]) == 1, "historical mode preserves one explicit App and list pair " + mode + option);
                    Check(((System.Collections.ArrayList)received["Depots"]).Cast<string>().SequenceEqual(new[] { "571", "575" }) && ((System.Collections.ArrayList)received["Manifests"]).Cast<string>().SequenceEqual(new[] { "3694916840459487978", Manifest }), "historical mode preserves each exact aligned manifest " + mode + option);
                    Check((bool)received["ManifestOnly"] == (mode == DownloadMode.ManifestOnly) && (bool)received["Validate"] == (mode == DownloadMode.ClientFiles), "historical mode does not change manifest/client stage flags " + mode + option);
                    var diagnostic = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(engine.LastDiagnosticPath));
                    Check((bool)diagnostic["HistoricalDepots"] == (option == 2) && !(bool)diagnostic["ParserFatalError"] && !(bool)diagnostic["CredentialsRequired"], "historical opt-in and success are recorded in safe diagnostic " + mode + option);
                }
            }
            foreach (string manifest in new[] { (string)null, "", "0" })
            {
                string directory = Path.Combine(parent, "invalid-" + Guid.NewGuid().ToString("N").Substring(0, 6)); Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "scenario.txt"), "success");
                var plan = Plan(); plan.Depots[1].ManifestId = manifest;
                using (var engine = new DownloadEngine(executable, hash, true))
                {
                    bool rejected = false;
                    try { engine.RunAsync(plan, directory, Account, false, DownloadMode.ManifestOnly, CancellationToken.None).GetAwaiter().GetResult(); } catch (ArgumentException) { rejected = true; }
                    Check(rejected && !File.Exists(Path.Combine(directory, "public-arguments.json")), "historical opt-in cannot start without every exact nonzero manifest");
                }
            }
        }
        public static int Main(string[] args)
        {
            if (args.Contains("-app")) return FakeEngine(args);
            try
            {
                root = Path.GetFullPath(args[0]); shortFixtures = Path.GetFullPath(args[1]); ClassificationCases(); PrivacyAndRetryCases(); HistoricalDiagnosticCases(); QrAndProgressCases(); DiagnosticFiles(); HistoricalModeArguments();
                string report = new JavaScriptSerializer().Serialize(new { Passed = true, Checks = checks, NetworkUsed = false, RealCredentialsRead = false, DownloadsStarted = false, Scope = "Synthetic safe error classification, redaction, fragmented stdout/stderr, authentication retry signals, QR and progress", Utc = DateTime.UtcNow.ToString("o") });
                File.WriteAllText(Path.Combine(root, "results.json"), report, new UTF8Encoding(false)); Console.WriteLine(report); return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
        }
    }
}
