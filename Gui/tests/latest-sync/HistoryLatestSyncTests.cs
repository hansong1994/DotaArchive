using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace DotaArchive
{
    internal static class HistoryLatestSyncTests
    {
        private const string BaseUrl = "https://steamdb.info/app/570/depots/?branch=public", HistoryUrl = "https://steamdb.info/app/570/history/?filterkey=2";
        private static int checks;
        private static string root, bundled;
        private static readonly DateTimeOffset capture = T("2020-01-10T00:00:00Z");
        private static DateTimeOffset T(string value) { return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture); }
        private static string Day(int day) { return new DateTimeOffset(2020, 1, day, 0, 0, 0, TimeSpan.Zero).ToString("o"); }
        private static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
        private static string Dir(string name) { string path = Path.Combine(root, name); Directory.CreateDirectory(path); return path; }
        private static string Hash(string path) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))); }
        private static HistoryPageSnapshot Page(string url, string html, int captureSeconds = 0)
        { return new HistoryPageSnapshot { Url = url, Html = html, Text = "Public synthetic fixture", Title = "Dota 2", CapturedAtUtc = capture.AddSeconds(captureSeconds) }; }
        private static string Baseline(int build, int manifest, int day, string name = "Core")
        {
            long unix = (long)(T(Day(day)) - new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero)).TotalSeconds;
            return "<main><div id='depots'><h2>Selected Branch: <b>public</b></h2><table><tr><td>buildid</td><td>" + build + "</td></tr><tr><td>timeupdated</td><td>" + unix + "</td></tr></table><table><tr class='depot' data-depotid='1'><td>1</td><td class='depot-config'><span class='i muted'>" + name + "</span></td><td><a href='/depot/1/history/?changeid=M:" + manifest + "'>" + manifest + "</a></td></tr></table></div></main>";
        }
        private static string Delta(string label, string oldValue, string newValue)
        { return "<li class='diff-modified'><span><i>" + label + ":</i><del>" + oldValue + "</del><ins>" + newValue + "</ins></span></li>"; }
        private static string Change(int id, int build, int oldBuild, int day, int oldDay, int manifest, int oldManifest)
        { return Panel(id, Day(day), "<li><span>Changed <span class='branch-name branch-public'>public</span> branch<ul class='app-history'>" + Delta("timeupdated", Day(oldDay), Day(day)) + Delta("buildid", oldBuild.ToString(), build.ToString()) + Delta("Depot 1 gid", oldManifest.ToString(), manifest.ToString()) + "</ul></span></li>"); }
        private static string Panel(int id, string date, string changes)
        { return "<div class='panel panel-history' data-changeid='" + id + "'><div class='panel-heading'><relative-time datetime='" + date + "'/></div><ul class='app-history'><li class='repo'><span>Changed <i>Depots</i></span><ul>" + changes + "</ul></li></ul></div>"; }
        private static string C30() { return Change(300, 30, 20, 3, 2, 3000, 2000); }
        private static string C20() { return Change(200, 20, 10, 2, 1, 2000, 1000); }
        private static string C40() { return Change(400, 40, 30, 4, 3, 4000, 3000); }
        private static string C50() { return Change(500, 50, 40, 5, 4, 5000, 4000); }
        private static string C60() { return Change(600, 60, 50, 6, 5, 6000, 5000); }
        private static HistoryPageSnapshot H(string html, int seconds = 1) { return Page(HistoryUrl, "<main><div id='history'>" + html + "<button id='js-history-load'>Load older history</button></div></main>", seconds); }
        private static void MakeBundle()
        {
            bundled = Dir("bundled"); var parser = new SteamDbHistoryParser();
            HistoryBaseline b = parser.ParseBaseline(Page(BaseUrl, Baseline(30, 3000, 3)));
            List<HistoryChange> list = parser.ParseHistory(H(C30() + C20())).Changes;
            new HistoryCacheStore(bundled).Save(b, list);
            new HistoryAnchorStore(bundled).Save(new[] { new HistoryAnchorStore.Anchor { Identity = HistoryAnchorStore.Identity(b), Provenance = "BrowserBaselineAndContinuousHistory", Baseline = b, Changes = list } });
        }
        private static HistorySyncResult Sync(Fake browser, string cache, CancellationToken token = default(CancellationToken))
        { return new HistoryLatestSync(browser, bundled, cache).SyncAsync(token, null).GetAwaiter().GetResult(); }
        private static void Fail(Action action, string label)
        { try { action(); } catch { checks++; return; } throw new Exception("Expected failure: " + label); }
        private static SyncCatalogSnapshot Active(string cache) { return new SyncCatalogStore(Path.Combine(cache, "latest-sync")).LoadActive(); }
        private static string Checkpoint(string cache)
        { return Directory.GetFiles(Path.Combine(cache, "latest-sync", "checkpoints"), "checkpoint.json", SearchOption.AllDirectories).Single(); }
        public static int Main(string[] args)
        {
            try
            {
                root = Path.Combine(Path.GetFullPath(Path.Combine(args[0], "../../../../../../")), "sync-tests", Guid.NewGuid().ToString("N").Substring(0, 8)); Directory.CreateDirectory(root); MakeBundle(); Run(args[1]);
                Console.WriteLine(new JavaScriptSerializer().Serialize(new { Result = "Passed", Assertions = checks, Scope = "Offline fake-browser latest sync with atomic generations, read-only bundled real archive, cancellation/failure checkpoints; no network/UI/download", Utc = DateTime.UtcNow.ToString("o") })); return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
        private static void Run(string realArchive)
        {
            string bundleHash = Hash(Path.Combine(bundled, "verified-public-history-v1.json"));
            string cache = Dir("unchanged"); var same = new Fake(Baseline(30, 3000, 3), H(C30() + C20()));
            HistorySyncResult result = Sync(same, cache);
            Check(result.AddedRecords == 0 && result.TotalRecords == 2 && result.LatestChangeId == "300" && result.LatestBuildId == "30", "No-update sync validates and publishes existing chain");
            Check(same.Baselines == 2 && same.HistoryNavigations == 1 && same.Clicks == 0 && same.Dates == 0, "No-update still verifies fresh header and baseline twice without old-page crawl");
            Check(result.VerifiedThroughUtc == capture && result.VerifiedThroughUtc < capture.AddSeconds(20), "Coverage uses first baseline capture, not completion");
            Check(Active(cache).GenerationDirectory == result.GenerationDirectory, "Atomic active pointer exposes validated generation");
            Check(File.Exists(Path.Combine(result.GenerationDirectory, "baseline-first.json")) && Directory.GetFiles(Path.Combine(result.GenerationDirectory, "history-evidence")).Length >= 2, "Successful generation keeps original sanitized baseline and history evidence");

            cache = Dir("append"); var added = new Fake(Baseline(50, 5000, 5), H(C50()), H(C50() + C40() + C30() + C20(), 2));
            result = Sync(added, cache);
            Check(result.AddedRecords == 2 && result.TotalRecords == 4 && result.LatestBuildId == "50" && result.LatestChangeId == "500", "Two new changes append to old complete tail");
            Check(added.Clicks == 1 && added.HistoryNavigations == 1 && added.Baselines == 2, "Stops fetching at old first-record boundary");
            var chain = new HistoryCacheStore(result.GenerationDirectory).Load();
            Check(String.Join(",", chain.Changes.Select(c => c.ChangeId)) == "500,400,300,200", "Combined chain deduplicates overlap and preserves old records");
            Check(!File.Exists(Path.Combine(cache, "verified-public-history-v1.json")), "Old prototype cache path not overwritten");
            var twice = new Fake(Baseline(50, 5000, 5), H(C50()));
            HistorySyncResult second = Sync(twice, cache);
            Check(second.AddedRecords == 0 && second.TotalRecords == 4 && second.LatestChangeId == "500", "Second sync reuses published current full chain without recrawling older history");
            Check(twice.Calls == 3 && twice.Baselines == 2 && twice.HistoryNavigations == 1, "Second sync needs just latest header and two baseline captures");
            string packagedSync = Path.Combine(cache, "latest-sync");
            var packagedHashes = Directory.GetFiles(packagedSync, "*", SearchOption.AllDirectories).ToDictionary(p => p, p => Hash(p));
            string cleanProfile = Dir("clean-profile-packaged-latest");
            Check(Active(cleanProfile) == null && !File.Exists(Path.Combine(cleanProfile, "verified-public-history-v1.json")), "New user's profile has no local updated generation or legacy history");
            var packagedLatest = new Fake(Baseline(60, 6000, 6), H(C60() + C50()));
            HistorySyncResult fromPackage = new HistoryLatestSync(packagedLatest, bundled, cleanProfile, packagedSync).SyncAsync(CancellationToken.None, null).GetAwaiter().GetResult();
            Check(fromPackage.AddedRecords == 1 && fromPackage.TotalRecords == 5 && fromPackage.LatestBuildId == "60", "New user's sync bridges from packaged latest generation instead of old bundled head");
            Check(packagedLatest.Calls == 3 && packagedLatest.Baselines == 2 && packagedLatest.HistoryNavigations == 1 && packagedLatest.Clicks == 0 && packagedLatest.Dates == 0, "Packaged generation stops at Change 500 without fetching the original Change 300 boundary");
            Check(String.Join(",", new HistoryCacheStore(fromPackage.GenerationDirectory).Load().Changes.Select(c => c.ChangeId)) == "600,500,400,300,200", "Packaged complete historical tail survives the new user's incremental sync");
            Check(Active(cleanProfile).GenerationDirectory == fromPackage.GenerationDirectory && fromPackage.GenerationDirectory.StartsWith(Path.Combine(cleanProfile, "latest-sync") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "User's new generation is published under their own profile");
            Check(packagedHashes.Count == Directory.GetFiles(packagedSync, "*", SearchOption.AllDirectories).Length && packagedHashes.All(pair => Hash(pair.Key) == pair.Value), "Sync never modifies the packaged latest generation, pointer or original evidence");
            string published = Hash(Path.Combine(cache, "latest-sync", "current.json"));
            var laterFailure = new Fake(Baseline(50, 5000, 5), H(C50() + C40() + C30())); laterFailure.FailureAtCall = 3; laterFailure.Failure = new InvalidOperationException("HTTP 403");
            Fail(() => Sync(laterFailure, cache), "No-update recheck failure cannot publish coverage");
            Check(Hash(Path.Combine(cache, "latest-sync", "current.json")) == published, "Existing successful coverage survives a later failed sync unchanged");
            string oldCache = Dir("old-cache-present");
            foreach (string name in new[] { "verified-public-history-v1.json", "verified-public-anchors-v2.json" }) File.Copy(Path.Combine(bundled, name), Path.Combine(oldCache, name));
            string oldV1 = Hash(Path.Combine(oldCache, "verified-public-history-v1.json")), oldV2 = Hash(Path.Combine(oldCache, "verified-public-anchors-v2.json"));
            Sync(new Fake(Baseline(50, 5000, 5), H(C50() + C40() + C30())), oldCache);
            Check(Hash(Path.Combine(oldCache, "verified-public-history-v1.json")) == oldV1 && Hash(Path.Combine(oldCache, "verified-public-anchors-v2.json")) == oldV2, "Preexisting prototype caches remain byte-identical");

            string metadata = Panel(400, Day(4), Delta("Depot 1 name", "Core", "Renamed"));
            cache = Dir("metadata"); result = Sync(new Fake(Baseline(30, 3000, 3, "Renamed"), H(metadata + C30() + C20())), cache);
            Check(result.AddedRecords == 1 && result.TotalRecords == 3 && result.LatestBuildId == "30" && result.LatestChangeId == "400", "Metadata-only latest change is not mistaken for no update");

            cache = Dir("missing-overlap"); var gap = new Fake(Baseline(50, 5000, 5), H(C50()), H(C40() + C30()));
            Fail(() => Sync(gap, cache), "Pagination without required overlap");
            Check(Active(cache) == null && File.Exists(Checkpoint(cache)), "Gap preserves prefix without success marker");
            cache = Dir("conflicting-overlap"); var conflict = new Fake(Baseline(50, 5000, 5), H(C50()), H(C50().Replace("<del>4000</del>", "<del>4999</del>") + C40() + C30()));
            Fail(() => Sync(conflict, cache), "Repeated change mismatch"); Check(Active(cache) == null, "Conflict cannot publish");
            cache = Dir("skipped-old-boundary"); Fail(() => Sync(new Fake(Baseline(50, 5000, 5), H(C50() + C40() + C20())), cache), "Known old boundary missing");
            Check(Active(cache) == null, "Stops before crawling past known boundary");

            cache = Dir("baseline-changed"); var changed = new Fake(Baseline(50, 5000, 5), H(C50() + C40() + C30())); changed.Confirmed = Baseline(60, 6000, 6);
            Fail(() => Sync(changed, cache), "Baseline changed during sync");
            Check(Active(cache) == null && File.Exists(Checkpoint(cache)), "Concurrent update retains checkpoint, not success coverage");

            foreach (string kind in new[] { "cancel", "403", "429" })
            {
                cache = Dir(kind); var stopped = new Fake(Baseline(50, 5000, 5), H(C50()), H(C50() + C40() + C30()));
                stopped.FailureAtCall = 3; stopped.Failure = kind == "cancel" ? (Exception)new OperationCanceledException() : new InvalidOperationException("HTTP " + kind);
                Fail(() => Sync(stopped, cache), "Stop " + kind);
                Check(Active(cache) == null, kind + " does not advance coverage");
                var checkpoint = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Deserialize<OnlineHistoryResolver.SyncCheckpoint>(File.ReadAllText(Checkpoint(cache)));
                Check(HistoryCacheStore.Parse(checkpoint.Payload).Changes.Count == 1, kind + " saves only validated prefix");
                Check(stopped.Calls == 3, kind + " stops without automated retry");
                string preserved = Hash(Checkpoint(cache));
                var blockedBeforeHeader = new Fake(Baseline(50, 5000, 5), H(C50())); blockedBeforeHeader.FailureAtCall = 2; blockedBeforeHeader.Failure = new InvalidOperationException("HTTP 403");
                Fail(() => Sync(blockedBeforeHeader, cache), "Failure before loading prior checkpoint");
                Check(Hash(Checkpoint(cache)) == preserved, "Earlier validated prefix is not overwritten by an empty failed attempt");
                var resumed = new Fake(Baseline(50, 5000, 5), H(C50()), H(C50() + C40() + C30()));
                result = Sync(resumed, cache);
                Check(result.AddedRecords == 2 && resumed.Dates == 1 && resumed.Clicks == 0, kind + " resumes same-baseline prefix via date overlap");
            }

            cache = Dir("lock"); string syncRoot = Path.Combine(cache, "latest-sync"); Directory.CreateDirectory(syncRoot);
            var locked = new Fake(Baseline(30, 3000, 3), H(C30()));
            using (var held = new FileStream(Path.Combine(syncRoot, "sync.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                Fail(() => Sync(locked, cache), "Single writer lock");
            Check(locked.Calls == 0 && Active(cache) == null, "Lock rejects before website access");
            var preCancelled = new Fake(Baseline(30, 3000, 3), H(C30()));
            Fail(() => Sync(preCancelled, Dir("pre-cancel"), new CancellationToken(true)), "Already canceled request");
            Check(preCancelled.Calls == 0, "Canceled request never uses browser");
            cache = Dir("corrupt-checkpoint"); var interrupted = new Fake(Baseline(50, 5000, 5), H(C50()), H(C50() + C40() + C30())); interrupted.FailureAtCall = 3; interrupted.Failure = new InvalidOperationException("HTTP 429");
            Fail(() => Sync(interrupted, cache), "Create checkpoint for corruption test");
            string checkpointPath = Checkpoint(cache); File.WriteAllText(checkpointPath, File.ReadAllText(checkpointPath).Replace("\"PayloadSha256\":\"", "\"PayloadSha256\":\"00"));
            Fail(() => Sync(new Fake(Baseline(50, 5000, 5), H(C50()), H(C50() + C40() + C30())), cache), "Corrupt checkpoint fails closed");
            Check(Active(cache) == null, "Corrupt checkpoint not silently published");
            Check(Hash(Path.Combine(bundled, "verified-public-history-v1.json")) == bundleHash, "Bundled archive never modified");
            string real1 = Path.Combine(realArchive, "verified-public-history-v1.json"), real2 = Path.Combine(realArchive, "verified-public-anchors-v2.json");
            string hash1 = Hash(real1), hash2 = Hash(real2); var realStop = new Fake(Baseline(30, 3000, 3), H(C30())); realStop.FailureAtCall = 1; realStop.Failure = new OperationCanceledException();
            Fail(() => new HistoryLatestSync(realStop, realArchive, Dir("real-readonly")).SyncAsync(CancellationToken.None, null).GetAwaiter().GetResult(), "Real full archive validated before browser failure");
            Check(realStop.Calls == 1 && Hash(real1) == hash1 && Hash(real2) == hash2, "Real 4412-record archive remains byte-identical");
        }

        private sealed class Fake : IHistoryBrowser, IHistoryDateBrowser
        {
            internal string Initial, Confirmed;
            internal int Calls, Baselines, HistoryNavigations, Clicks, Dates, FailureAtCall;
            internal Exception Failure;
            private readonly Queue<HistoryPageSnapshot> pages;
            internal Fake(string initial, params HistoryPageSnapshot[] pages) { Initial = Confirmed = initial; this.pages = new Queue<HistoryPageSnapshot>(pages); }
            private void Call(CancellationToken token) { token.ThrowIfCancellationRequested(); Calls++; if (Calls == FailureAtCall) throw Failure; if (Calls > 12) throw new Exception("Unexpected repeated request"); }
            public Task<HistoryPageSnapshot> NavigateAsync(string url, CancellationToken token)
            {
                Call(token);
                if (url == BaseUrl) { Baselines++; return Task.FromResult(Page(url, Baselines == 1 ? Initial : Confirmed, Baselines == 1 ? 0 : 20)); }
                if (url != HistoryUrl) throw new Exception("Unexpected navigation: " + url);
                HistoryNavigations++; return Task.FromResult(pages.Dequeue());
            }
            public Task<HistoryPageSnapshot> ClickAsync(string selector, CancellationToken token) { Call(token); Clicks++; if (selector != "#js-history-load") throw new Exception("Unexpected click"); return Task.FromResult(pages.Dequeue()); }
            public Task<HistoryPageSnapshot> LoadHistoryBeforeAsync(DateTime date, CancellationToken token) { Call(token); Dates++; return Task.FromResult(pages.Dequeue()); }
            public Task<HistoryPageSnapshot> CaptureAsync(CancellationToken token) { throw new Exception("Unexpected capture"); }
        }
    }
}
