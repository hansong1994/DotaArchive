using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace DotaArchive
{
    internal static class HistoryArchiveTests
    {
        private const string Url = "https://steamdb.info/app/570/history/?filterkey=2";
        private static int checks;
        private static string root;
        private static string runRoot;
        private static readonly SteamDbHistoryParser Parser = new SteamDbHistoryParser();
        private static readonly DateTimeOffset Lower = Date("2019-01-01T00:00:00Z");
        private static readonly DateTimeOffset Upper = Date("2019-01-04T00:00:00Z");
        private static DateTimeOffset Date(string value) { return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture); }
        private static void Check(bool passed, string name) { if (!passed) throw new Exception("FAILED: " + name); checks++; }
        private static void Reject<T>(Action action, string name) where T : Exception
        {
            try { action(); } catch (T) { checks++; return; }
            throw new Exception("Expected " + typeof(T).Name + ": " + name);
        }
        private static HistoryPageSnapshot Page(string html) { return new HistoryPageSnapshot { Url = Url, Html = "<main id='history'>" + html + "</main>", Title = "Dota 2 history", CapturedAtUtc = DateTimeOffset.UtcNow }; }
        private static string Delta(string label, string oldValue, string newValue)
        { return "<li class='diff-modified'><span><i>" + label + ":</i>" + (oldValue == null ? "" : "<del>" + oldValue + "</del>") + (newValue == null ? "" : "<ins>" + newValue + "</ins>") + "</span></li>"; }
        private static string Change(string id, string build, string oldBuild, string date, string oldDate, string oldManifest, string manifest, string extra = "")
        {
            return "<div class='panel-history' data-changeid='" + id + "'><relative-time datetime='" + date + "'></relative-time><ul><li class='repo'><span>Changed <i>Depots</i></span><ul>" + extra + "<li><span>Changed <span class='branch-name'>public</span> branch</span><ul>" + (build == null ? "" : Delta("buildid", oldBuild, build) + Delta("timeupdated", oldDate, date)) + Delta("Depot 1 gid", oldManifest, manifest) + "</ul></li></ul></li></ul></div>";
        }
        private static string C400 { get { return Change("400", "40", "30", "2019-01-04T00:00:00Z", "2019-01-03T00:00:00Z", "300", "400"); } }
        private static string C300 { get { return Change("300", "30", "20", "2019-01-03T00:00:00Z", "2019-01-02T00:00:00Z", "200", "300"); } }
        private static string C200 { get { return Change("200", "20", "10", "2019-01-02T00:00:00Z", "2019-01-01T00:00:00Z", "100", "200"); } }
        private static string C100 { get { return Change("100", "10", "5", "2019-01-01T00:00:00Z", "2018-12-31T00:00:00Z", "50", "100"); } }
        private const string End = "<p>No more history</p>";
        private const string More = "<button id='js-history-load'>Load more history</button>";
        private static HistoryBaseline Baseline(string build = "40", string date = "2019-01-04T00:00:00Z", string manifest = "400")
        {
            var state = new HistoryDepotState { CompleteBaseline = true };
            state.Values.Add("depots/1/manifests/public/gid", manifest);
            return new HistoryBaseline { BuildId = build, PublishedAtUtc = Date(date), SourceUrl = "https://steamdb.info/app/570/depots/?branch=public", State = state };
        }
        private static string Seed(string name, string changes = null, HistoryBaseline baseline = null)
        {
            string directory = Path.Combine(runRoot, name); Directory.CreateDirectory(directory);
            baseline = baseline ?? Baseline();
            List<HistoryChange> list = Parser.ParseHistory(Page(changes ?? (C400 + C300))).Changes;
            new HistoryCacheStore(directory).Save(baseline, list);
            new HistoryAnchorStore(directory).Save(new[] { new HistoryAnchorStore.Anchor { Identity = HistoryAnchorStore.Identity(baseline), Provenance = "BrowserBaselineAndContinuousHistory", Baseline = baseline, Changes = list } });
            return directory;
        }
        private static OnlineHistoryResolver Resolver(IHistoryBrowser browser, string directory)
        { return new OnlineHistoryResolver(browser, (p, b, t, log) => { throw new Exception("Archive must never resolve a patch or call an official API."); }, directory); }
        private static HistoryArchiveResult Run(IHistoryBrowser browser, string directory, DateTimeOffset? lower = null, DateTimeOffset? upper = null, CancellationToken token = default(CancellationToken))
        { return Resolver(browser, directory).ArchiveRangeAsync(lower ?? Lower, upper ?? Upper, token, null).GetAwaiter().GetResult(); }
        private static int SavedCount(string directory) { return new HistoryCacheStore(directory).Load().Changes.Count; }
        public static int Main(string[] args)
        {
            root = Path.GetFullPath(args[0]); runRoot = Path.Combine(root, "runs", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
            Directory.CreateDirectory(runRoot);
            try
            {
                ResumeAndCoverage(); BoundaryFailures(); StopsAndPersistence(); OldestAndLowerCases();
                string json = new JavaScriptSerializer().Serialize(new { Passed = true, Checks = checks, CompletedAtUtc = DateTimeOffset.UtcNow.ToString("o"), EvidenceDirectory = runRoot, RealNetworkUsed = false, SteamCredentialsUsed = false });
                File.WriteAllText(Path.Combine(root, "results.json"), json, new UTF8Encoding(false));
                Console.WriteLine("PASS " + checks + " archive checks; " + runRoot); return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
        private static void ResumeAndCoverage()
        {
            string dir = Seed("resume"); var fake = new FakeBrowser(Page(C300 + C200 + C100 + End));
            HistoryArchiveResult result = Run(fake, dir);
            Check(result.RangeCovered && result.ReachedSiteEnd && result.TotalRecords == 4 && result.NewRecords == 2, "resume extends existing chain to explicit endpoint");
            Check(result.RangeRecords == 3 && result.BoundaryChangeId == "100" && result.OldestObservedAtUtc == Lower, "range bounds and observed boundary retained");
            Check(fake.OpenCount == 1 && fake.Dates.SequenceEqual(new[] { Date("2019-01-04T00:00:00Z").UtcDateTime.Date }) && fake.NavigateCount == 0 && fake.ClickCount == 0, "direct boundary date without baseline or DateFirst lookup");
            Check(SavedCount(dir) == 4 && new HistoryAnchorStore(dir).Load().Single().Changes.Count == 4, "v1 and v2 persist extended complete prefix");
            var offline = new FakeBrowser(); HistoryArchiveResult again = Run(offline, dir, Date("2019-01-02T00:00:00Z"));
            Check(again.RangeCovered && !again.ReachedSiteEnd && again.NewRecords == 0 && again.RangeRecords == 2, "covered narrower range returns without guessing endpoint");
            Check(offline.OpenCount == 0 && offline.Dates.Count == 0 && offline.NavigateCount == 0, "covered cache makes no browser calls");
            string noAnchor = Path.Combine(runRoot, "no-anchor");
            var unused = new FakeBrowser(); Reject<HistoryResolutionException>(() => Run(unused, noAnchor), "no anchor refuses new baseline");
            Check(unused.OpenCount == 0 && unused.NavigateCount == 0, "no anchor failure stays offline");
            Reject<ArgumentException>(() => Run(new FakeBrowser(), dir, Upper, Lower), "invalid range");
        }
        private static void BoundaryFailures()
        {
            string dir = Seed("missing-overlap"); var fake = new FakeBrowser(Page(C200 + End));
            Reject<HistoryResolutionException>(() => Run(fake, dir), "missing boundary even with end marker");
            Check(SavedCount(dir) == 2 && fake.Dates.Count == 1, "missing overlap neither mutates cache nor retries");
            dir = Seed("changed-overlap");
            string changed = C300.Replace("<ins>300</ins>", "<ins>301</ins>");
            Reject<HistoryResolutionException>(() => Run(new FakeBrowser(Page(changed + C200 + End)), dir), "changed boundary");
            Check(SavedCount(dir) == 2, "changed boundary retains original prefix");
            dir = Seed("changed-earlier-overlap");
            changed = C400.Replace("<del>300</del>", "<del>299</del>");
            Reject<HistoryResolutionException>(() => Run(new FakeBrowser(Page(changed + C300 + C200 + End)), dir), "all repeated records checked");
            Check(SavedCount(dir) == 2, "non-boundary overlap corruption rejected before append");
            dir = Seed("branch-gap");
            Reject<HistoryResolutionException>(() => Run(new FakeBrowser(Page(C300 + C100 + End)), dir), "missing public event is a replay gap");
            Check(SavedCount(dir) == 2, "branch gap never appended");
            dir = Seed("same-page-duplicate");
            Reject<HistoryResolutionException>(() => Run(new FakeBrowser(Page(C300 + C200 + C200 + End)), dir), "duplicate ID within batch");
            Check(SavedCount(dir) == 2, "duplicate batch not appended");
            dir = Seed("no-progress"); fake = new FakeBrowser(Page(C300));
            Reject<HistoryResolutionException>(() => Run(fake, dir), "date page cannot loop with only duplicate boundary");
            Check(SavedCount(dir) == 2 && fake.Dates.Count == 1, "no-progress error is bounded");
        }
        private static void StopsAndPersistence()
        {
            string dir = Seed("cancel"); var cancel = new CancellationTokenSource();
            var fake = new FakeBrowser(Page(C300 + C200)); fake.OnDate = n => { if (n == 2) cancel.Cancel(); };
            Reject<OperationCanceledException>(() => Run(fake, dir, token: cancel.Token), "cancellation at next batch");
            Check(SavedCount(dir) == 3 && new HistoryAnchorStore(dir).Load().Single().Changes.Count == 3, "cancel saves success prefix in both formats");
            dir = Seed("cancel-before-start"); fake = new FakeBrowser();
            Reject<OperationCanceledException>(() => Run(fake, dir, token: cancel.Token), "cancel before archive");
            Check(SavedCount(dir) == 2 && fake.OpenCount == 0, "pre-cancel does not open browser");
            dir = Seed("429-stop"); fake = new FakeBrowser(Page(C300 + C200));
            fake.OnDate = n => { if (n == 2) throw new HistoryResolutionException("HTTP 429; stop"); };
            Reject<HistoryResolutionException>(() => Run(fake, dir), "restriction propagates without retry");
            Check(SavedCount(dir) == 3 && fake.Dates.Count == 2, "restriction saves proved records once");
            dir = Seed("unknown-leaf");
            string unknown = C100.Replace("<i>Depots</i></span><ul>", "<i>Depots</i></span><ul>" + Delta("Depot 1 unknown/source1", "a", "b"));
            string diagnostics = Path.Combine(dir, "evidence");
            var wrapped = new DiagnosticHistoryBrowser(new FakeBrowser(Page(C300 + C200 + unknown + End)), diagnostics);
            Reject<HistoryResolutionException>(() => Run(wrapped, dir), "unknown old field cannot become verified data");
            Check(SavedCount(dir) == 3, "unknown field saves valid records preceding gap");
            Check(Directory.GetFiles(Path.Combine(diagnostics, "batches"), "history-*.json").Length == 1 && File.ReadAllText(Path.Combine(diagnostics, "latest-history.json")).Contains("unknown/source1"), "raw batch is saved before parse/replay failure");
            dir = Seed("disk-failure"); fake = new FakeBrowser(Page(C300 + C200 + C100 + End));
            string blockedFile = Path.Combine(dir, "verified-public-history-v1.json");
            fake.OnDate = n => { if (n == 1) { File.Delete(blockedFile); Directory.CreateDirectory(blockedFile); } };
            Reject<AggregateException>(() => Run(fake, dir), "strict archive persistence failure cannot report completion");
            Check(Directory.Exists(blockedFile), "save failure test exercised actual filesystem failure");
        }
        private static void OldestAndLowerCases()
        {
            string dir = Seed("oldest-anchor");
            List<HistoryAnchorStore.Anchor> anchors = new HistoryAnchorStore(dir).Load();
            HistoryBaseline older = Baseline("30", "2019-01-03T00:00:00Z", "300");
            anchors.Add(new HistoryAnchorStore.Anchor { Identity = HistoryAnchorStore.Identity(older), Provenance = "BrowserBaselineAndContinuousHistory", Baseline = older, Changes = Parser.ParseHistory(Page(C300 + C200)).Changes });
            new HistoryAnchorStore(dir).Save(anchors);
            var fake = new FakeBrowser(Page(C200 + C100 + End));
            var result = Run(fake, dir, upper: Date("2019-01-03T00:00:00Z"));
            Check(result.AnchorIdentity == HistoryAnchorStore.Identity(older) && result.NewRecords == 1 && result.TotalRecords == 3, "oldest compatible anchor selected");
            Check(fake.Dates.Single() == Date("2019-01-03T00:00:00Z").UtcDateTime.Date && new HistoryAnchorStore(dir).Load().Count == 2, "other historical anchor kept while continuing closest boundary");
            dir = Seed("same-second-lower");
            string two = Change("200", "20", "15", "2019-01-02T00:00:00Z", "2019-01-02T00:00:00Z", "150", "200");
            string oneHalf = Change("150", "15", "10", "2019-01-02T00:00:00Z", "2019-01-01T00:00:00Z", "100", "150");
            result = Run(new FakeBrowser(Page(C300 + two + oneHalf + C100)), dir, Date("2019-01-02T00:00:00Z"));
            Check(result.RangeCovered && !result.ReachedSiteEnd && result.RangeRecords == 3 && result.TotalRecords == 5, "cross lower boundary includes both equal-second changelists");
            Check(result.BoundaryChangeId == "100" && result.OldestObservedAtUtc < result.LowerInclusiveUtc, "lower range proven by an older record");
            dir = Seed("earliest-non-public");
            string noBuild = Change("100", null, null, "2019-01-01T00:00:00Z", null, "50", "100");
            result = Run(new FakeBrowser(Page(C300 + C200 + noBuild + End)), dir);
            Check(result.RangeCovered && result.ReachedSiteEnd && result.NewRecords == 2, "tail can be Depot history without public build metadata");
            Check(new HistoryCacheStore(dir).Load().Changes.Last().Publication == null, "does not invent Build for earliest non-public record");
            string sameDayTop = Change("300", "30", "20", "2019-01-03T12:00:00Z", "2019-01-03T06:00:00Z", "200", "300");
            string sameDayNext = Change("200", "20", "10", "2019-01-03T06:00:00Z", "2019-01-01T00:00:00Z", "100", "200");
            dir = Seed("empty-explicit-end", sameDayTop, Baseline("30", "2019-01-03T12:00:00Z", "300"));
            fake = new FakeBrowser(Page(sameDayTop + sameDayNext + More)); fake.ClickPages.Enqueue(Page(End));
            result = Run(fake, dir, upper: Date("2019-01-03T12:00:00Z"));
            Check(result.RangeCovered && result.ReachedSiteEnd && result.NewRecords == 1 && fake.ClickCount == 1, "empty explicit end after continuous normal pagination");
            dir = Seed("empty-without-end", sameDayTop, Baseline("30", "2019-01-03T12:00:00Z", "300"));
            fake = new FakeBrowser(Page(sameDayTop + sameDayNext + More)); fake.ClickPages.Enqueue(Page("<p>History unavailable</p>"));
            Reject<HistoryResolutionException>(() => Run(fake, dir, upper: Date("2019-01-03T12:00:00Z")), "empty without explicit endpoint is not success");
            Check(SavedCount(dir) == 2 && fake.ClickCount == 1, "empty error retains last verified boundary");
        }
        private sealed class FakeBrowser : IHistoryBrowser, IHistoryDateBrowser, IHistoryDateNavigationBrowser
        {
            internal readonly Queue<HistoryPageSnapshot> DatePages;
            internal readonly Queue<HistoryPageSnapshot> ClickPages = new Queue<HistoryPageSnapshot>();
            internal readonly List<DateTime> Dates = new List<DateTime>();
            internal int OpenCount, NavigateCount, ClickCount;
            internal Action<int> OnDate;
            internal FakeBrowser(params HistoryPageSnapshot[] pages) { DatePages = new Queue<HistoryPageSnapshot>(pages); }
            public Task OpenHistoryForDateAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); OpenCount++; return Task.FromResult(true); }
            public Task<HistoryPageSnapshot> LoadHistoryBeforeAsync(DateTime date, CancellationToken token)
            {
                Dates.Add(date); if (OnDate != null) OnDate(Dates.Count); token.ThrowIfCancellationRequested();
                if (DatePages.Count == 0) throw new Exception("Unexpected extra date request.");
                return Task.FromResult(DatePages.Dequeue());
            }
            public Task<HistoryPageSnapshot> NavigateAsync(string url, CancellationToken token) { NavigateCount++; throw new Exception("Archive should not navigate to fresh baseline, candidate or patch pages: " + url); }
            public Task<HistoryPageSnapshot> CaptureAsync(CancellationToken token) { throw new Exception("Unexpected capture."); }
            public Task<HistoryPageSnapshot> ClickAsync(string selector, CancellationToken token)
            {
                token.ThrowIfCancellationRequested(); ClickCount++;
                if (selector != "#js-history-load" || ClickPages.Count == 0) throw new Exception("Unexpected pagination.");
                return Task.FromResult(ClickPages.Dequeue());
            }
        }
    }
}
