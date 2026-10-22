using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace DotaArchive
{
    internal static class EarlyClientScopeTests
    {
        private static int checks;
        private static string run;
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue, RecursionLimit = 200 };
        private static DateTimeOffset T(string value) { return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture); }
        private static void Check(bool value, string name) { if (!value) throw new Exception("FAILED: " + name); checks++; }
        private static void Reject(Action action, string name)
        { try { action(); } catch (HistoryResolutionException) { checks++; return; } throw new Exception("Expected rejection: " + name); }
        private static string Hash(string path)
        { using (var hash = SHA256.Create()) using (var stream = File.OpenRead(path)) return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
        private static HistoryDepotState Clone(HistoryDepotState state)
        { return (HistoryDepotState)typeof(OnlineHistoryResolver).GetMethod("CloneState", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { state }); }
        private static DateTimeOffset Time(HistoryChange change)
        { return change.Publication.PublishedAtUtc.Year > 1970 ? change.Publication.PublishedAtUtc : change.ObservedAtUtc; }
        private static string Tuples(IEnumerable<DepotEntry> depots)
        { return String.Join("|", depots.OrderBy(d => UInt64.Parse(d.DepotId)).Select(d => d.DepotId + ":" + d.ManifestId)); }
        private static ResolvedHistoryPlan Resolve(string directory, DateTimeOffset time, HistoryTimeDirection direction)
        { return new LocalArchiveResolver(directory).ResolveAsync(new HistoryQuery { RequestedUtc = time, TimeDirection = direction }, CancellationToken.None, null).GetAwaiter().GetResult(); }
        private sealed class EventResult
        {
            public string ChangeId, Build, Marker, Scope, Error;
            public string[] Depots;
            public DateTimeOffset Utc;
            public bool MarkerObserved, Complete;
        }
        private sealed class NoNetworkBrowser : IHistoryBrowser
        {
            public Task<HistoryPageSnapshot> NavigateAsync(string url, CancellationToken token) { throw new Exception("Offline test attempted navigation: " + url); }
            public Task<HistoryPageSnapshot> CaptureAsync(CancellationToken token) { throw new Exception("Offline test attempted capture"); }
            public Task<HistoryPageSnapshot> ClickAsync(string selector, CancellationToken token) { throw new Exception("Offline test attempted page click"); }
        }
        private static List<EventResult> Scan(HistoryAnchorStore.Anchor anchor)
        {
            var output = new List<EventResult>();
            var state = Clone(anchor.Baseline.State);
            foreach (var change in anchor.Changes)
            {
                state.Validate(change);
                if (change.Publication != null && change.Publication.IsPublic && Time(change) < T("2013-07-20T00:00:00Z"))
                {
                    var row = new EventResult { ChangeId = change.ChangeId, Build = change.Publication.BuildId, Utc = Time(change), Marker = state.Value(HistoryDepotState.LegacyDlcMarkingPath), MarkerObserved = state.HasKnownLegacyDlcMarking };
                    try { var depots = state.SelectWindowsClient("schinese"); row.Complete = true; row.Scope = Tuples(depots); row.Depots = depots.Select(d => d.DepotId).ToArray(); }
                    catch (HistoryResolutionException error) { row.Error = error.Message; }
                    output.Add(row);
                }
                state.Reverse(change);
            }
            return output;
        }
        private static HistoryDepotState State(string marker)
        {
            var state = new HistoryDepotState { CompleteBaseline = true, HasKnownLegacyDlcMarking = true, HasSharedPrerequisiteBaseline = true };
            if (marker != null) state.Values.Add(HistoryDepotState.LegacyDlcMarkingPath, marker);
            for (int id = 1; id <= 16; id++) state.Values.Add("depots/" + id + "/manifests/public/gid", (1000 + id).ToString(CultureInfo.InvariantCulture));
            state.Values.Add("depots/2/isdlc", "0");
            state.Values.Add("depots/3/isdlc", "1");
            state.Values.Add("depots/4/dlcappid", "0");
            state.Values.Add("depots/5/dlcappid", "123");
            state.Values.Add("depots/6/config/optionaldlc", "123");
            state.Values.Add("depots/7/config/optionaldlc", "0");
            state.Values.Add("depots/8/config/lv", "1");
            state.Values.Add("depots/9/config/lowviolence", "1");
            state.Values.Add("depots/10/config/oslist", "linux, macos");
            state.Values.Add("depots/11/config/oslist", "linux, Windows");
            state.Values.Add("depots/12/config/language", "english");
            state.Values.Add("depots/13/config/language", "schinese");
            state.Values.Add("depots/14/config/language", "koreana");
            state.Values.Add("depots/15/depotfromapp", "228980");
            state.Values.Add("depots/16/depotfromapp", "570");
            return state;
        }
        private static void ScopeCases()
        {
            foreach (string invalid in new[] { "", "2", "false", "true", "unknown" })
                Reject(() => State(invalid).SelectWindowsClient("schinese"), "unrecognized global marker remains fail-closed: " + invalid);
            foreach (string marker in new[] { (string)null, "0", "1" })
            {
                var state = State(marker);
                Check(state.SelectWindowsClient("schinese").Select(d => d.DepotId).SequenceEqual(new[] { "1", "2", "4", "7", "11", "12", "13", "16" }), "exact Windows/Chinese scope marker " + (marker ?? "missing"));
                Check(state.SelectWindowsClient("koreana").Select(d => d.DepotId).SequenceEqual(new[] { "1", "2", "4", "7", "11", "12", "14", "16" }), "language filter marker " + (marker ?? "missing"));
                state.Values.Add("depots/17/manifests/public/gid", "1017"); state.Values.Add("depots/17/depotfromapp", "999");
                Reject(() => state.SelectWindowsClient("schinese"), "unsupported shared source still rejected marker " + marker);
                state.Values.Remove("depots/17/depotfromapp"); state.Values["depots/17/manifests/public/gid"] = "0";
                Reject(() => state.SelectWindowsClient("schinese"), "invalid included manifest rejected marker " + marker);
                state.Values["depots/17/manifests/public/gid"] = "1017"; state.CompleteBaseline = false;
                Reject(() => state.SelectWindowsClient("schinese"), "incomplete baseline rejected marker " + marker);
            }
        }
        private static void RealCases(string directory, HistoryAnchorStore.Anchor anchor, List<EventResult> events)
        {
            var before = Resolve(directory, T("2013-02-02T12:00:00+08:00"), HistoryTimeDirection.Before);
            var after = Resolve(directory, T("2013-02-02T12:00:00+08:00"), HistoryTimeDirection.After);
            Check(before != null && before.Complete && before.BuildId == "41055", "real earliest strict-before Build 41055");
            Check(after != null && after.Complete && after.BuildId == "41416", "real earliest strict-after Build 41416");
            Check(before.Depots.Select(d => d.DepotId).SequenceEqual(new[] { "571", "572", "573", "575" }), "earliest build has exact four historical public Windows depots");
            Check(after.Depots.Select(d => d.DepotId).SequenceEqual(new[] { "571", "572", "573", "575" }), "next build retains full four depots");
            Check(Tuples(before.Depots) == "571:6267818599155059489|572:1293698889243070972|573:8493171769881926953|575:3115444187078319616", "earliest full manifest combination matches archived raw evidence");
            Check(Tuples(after.Depots) == "571:3694916840459487978|572:6223183715551423218|573:8493171769881926953|575:3115444187078319616", "next full manifest combination matches archived raw evidence");
            Check(after.Depots.Single(d => d.DepotId == "573").ManifestId == before.Depots.Single(d => d.DepotId == "573").ManifestId, "unchanged language depot retained in both full plans");
            Check(before.UsedObservationFallback && after.UsedObservationFallback, "old timeline explicitly retains SteamDB observation time source");
            Check(Resolve(directory, before.PublishedAtUtc, HistoryTimeDirection.Before) == null, "oldest exact boundary does not fabricate preceding build");
            Check(Resolve(directory, before.PublishedAtUtc, HistoryTimeDirection.After).BuildId == "41416", "strict-after excludes earliest equal event");
            Check(Resolve(directory, after.PublishedAtUtc, HistoryTimeDirection.Before).BuildId == "41055", "strict-before excludes next equal event");
            Check(Resolve(directory, after.PublishedAtUtc, HistoryTimeDirection.After).BuildId == "42682", "strict-after excludes next equal event");
            foreach (string time in new[] { "2013-05-22T12:00:00Z", "2013-05-23T12:00:00Z", "2013-07-15T12:00:00Z", "2013-07-16T12:00:00Z", "2013-07-18T19:00:00Z", "2013-07-18T21:00:00Z" })
            {
                foreach (HistoryTimeDirection direction in new[] { HistoryTimeDirection.Before, HistoryTimeDirection.After })
                {
                    DateTimeOffset requested = T(time);
                    var expected = direction == HistoryTimeDirection.Before ? events.Where(e => e.Utc < requested).OrderByDescending(e => e.Utc).First() : events.Where(e => e.Utc > requested).OrderBy(e => e.Utc).First();
                    var plan = Resolve(directory, requested, direction);
                    Check(plan != null && plan.Complete && plan.BuildId == expected.Build && plan.SourceUrl.EndsWith("=" + expected.ChangeId), "exact event around legacy metadata boundary " + time + " " + direction);
                    Check(Tuples(plan.Depots) == expected.Scope, "full unchanged and changed manifest set around " + time + " " + direction);
                }
            }
            Check(events.Count > 0 && events.All(e => e.Complete), "every stored early public event produces a Windows scope");
            foreach (var expected in events.GroupBy(e => e.Utc).Select(group => group.First()))
            {
                var plan = Resolve(directory, expected.Utc.AddTicks(1), HistoryTimeDirection.Before);
                Check(plan != null && plan.Complete && plan.BuildId == expected.Build && plan.SourceUrl.EndsWith("=" + expected.ChangeId) && Tuples(plan.Depots) == expected.Scope, "public resolver reconstructs complete early event " + expected.ChangeId);
            }
            var sample = new HashSet<string> { "41055", "41416", "42682", "91534" };
            foreach (var item in events.Where(e => sample.Contains(e.Build))) File.WriteAllText(Path.Combine(run, "build-" + item.Build + ".json"), Json.Serialize(item), new UTF8Encoding(false));
            string corruptDirectory = Path.Combine(run, "broken-continuity");
            var broken = new HistoryAnchorStore.Anchor { Identity = anchor.Identity, Provenance = anchor.Provenance, Baseline = anchor.Baseline, Changes = anchor.Changes.ToList() };
            var originalLast = broken.Changes.Last();
            var brokenLast = new HistoryChange { ChangeId = originalLast.ChangeId, SourceUrl = originalLast.SourceUrl, ObservedAtUtc = originalLast.ObservedAtUtc, ParsedAllDepotChanges = true, Publication = originalLast.Publication, Deltas = originalLast.Deltas.Select(d => new HistoryDelta { Path = d.Path, OldExists = d.OldExists, NewExists = d.NewExists, OldValue = d.OldValue, NewValue = d.NewValue }).ToList() };
            brokenLast.Deltas[0].NewValue = "999"; broken.Changes[broken.Changes.Count - 1] = brokenLast;
            new HistoryAnchorStore(corruptDirectory).Save(new[] { broken });
            Reject(() => Resolve(corruptDirectory, T("2013-02-02T12:00:00+08:00"), HistoryTimeDirection.After), "semantically corrupt rehashed oldest record is still rejected");
            brokenLast.Deltas[0].NewValue = originalLast.Deltas[0].NewValue; brokenLast.ParsedAllDepotChanges = false;
            Reject(() => new OnlineHistoryResolver(new NoNetworkBrowser(), null, null).ValidateAnchorAsync(broken, CancellationToken.None).GetAwaiter().GetResult(), "unknown early field is not ignored to make a plan");
        }
        public static int Main(string[] args)
        {
            try
            {
                string root = Path.GetFullPath(args[0]), directory = Path.GetFullPath(args[1]); bool scanOnly = Boolean.Parse(args[2]);
                run = Path.Combine(root, "runs", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff")); Directory.CreateDirectory(run);
                var hashes = Directory.GetFiles(directory, "*", SearchOption.AllDirectories).ToDictionary(p => p, Hash);
                var synchronized = new SyncCatalogStore(directory).LoadActive(CancellationToken.None);
                Check(synchronized != null && synchronized.TotalRecords >= 4415, "complete published 4415-record database loads and validates");
                var anchor = synchronized.Anchors.OrderByDescending(a => a.Changes.Count).First();
                new OnlineHistoryResolver(new NoNetworkBrowser(), null, null).ValidateAnchorAsync(anchor, CancellationToken.None).GetAwaiter().GetResult();
                Check(true, "full production Build/Depot continuity validator passes");
                var events = Scan(anchor);
                if (!scanOnly) { ScopeCases(); RealCases(directory, anchor, events); }
                Check(hashes.All(pair => Hash(pair.Key) == pair.Value), "published source archive and raw evidence remain byte-identical");
                var report = new { Passed = scanOnly ? (bool?)null : true, ScanOnly = scanOnly, Checks = checks, TotalRecords = synchronized.TotalRecords, EarlyPublicEvents = events.Count, CompleteEarlyEvents = events.Count(e => e.Complete), RejectedEarlyEvents = events.Count(e => !e.Complete), EarliestUtc = events.Min(e => e.Utc), LatestScannedUtc = events.Max(e => e.Utc), Events = events, SourceAnchor = anchor.Identity, SourceDirectory = directory, RunDirectory = run, NetworkUsed = false, DownloadsStarted = false };
                string text = Json.Serialize(report); File.WriteAllText(Path.Combine(run, "results.json"), text, new UTF8Encoding(false)); File.WriteAllText(Path.Combine(root, scanOnly ? "scan-results.json" : "results.json"), text, new UTF8Encoding(false));
                Console.WriteLine(Json.Serialize(new { Passed = report.Passed, Checks = checks, TotalRecords = report.TotalRecords, EarlyPublicEvents = events.Count, CompleteEarlyEvents = report.CompleteEarlyEvents, RejectedEarlyEvents = report.RejectedEarlyEvents, RunDirectory = run })); return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
