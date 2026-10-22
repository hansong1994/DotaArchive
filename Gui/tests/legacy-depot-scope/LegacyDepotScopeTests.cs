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
    internal static class LegacyDepotScopeTests
    {
        private static int checks;
        private static readonly SteamDbHistoryParser Parser = new SteamDbHistoryParser();
        private static void Check(bool value, string name) { if (!value) throw new Exception("FAILED: " + name); checks++; }
        private static void Reject(Action action, string name) { try { action(); } catch (HistoryResolutionException) { checks++; return; } throw new Exception("Expected rejection: " + name); }
        private static string Leaf(string key, string oldValue, string newValue)
        { return "<li class='diff-modified'><span><i>" + key + ":</i>" + (oldValue == null ? "" : "<del>" + oldValue + "</del>") + (newValue == null ? "" : "<ins>" + newValue + "</ins>") + "</span></li>"; }
        private static HistoryChange Parse(string tree)
        { return Parser.ParseHistory(new HistoryPageSnapshot { Url = "https://steamdb.info/app/570/history/?filterkey=2", Html = "<main><div class='panel-history' data-changeid='1'><relative-time datetime='2013-08-01T00:00:00Z'></relative-time><ul><li class='repo'><span>Changed <i>Depots</i><ul>" + tree + "</ul></span></li></ul></div></main>" }).Changes.Single(); }
        private static HistoryDepotState State()
        {
            var state = new HistoryDepotState { CompleteBaseline = true };
            state.Values.Add("depots/571/manifests/public/gid", "100");
            state.Values.Add("depots/575/manifests/public/gid", "200");
            state.Values.Add("depots/575/dlcappid", "575");
            state.Values.Add("depots/578/manifests/public/gid", "300");
            state.Values.Add("depots/578/config/lowviolence", "1");
            return state;
        }
        private static HistoryBaseline Baseline(HistoryDepotState state) { return new HistoryBaseline { BuildId = "1", PublishedAtUtc = DateTimeOffset.Parse("2013-08-01T00:00:00Z"), SourceUrl = "https://steamdb.info/app/570/depots/?branch=public", State = state }; }
        private static void UnitChecks(string root)
        {
            var state = State(); var modernIdentity = HistoryAnchorStore.Identity(Baseline(state));
            Check(state.SelectWindowsClient("schinese").Select(d => d.DepotId).SequenceEqual(new[] { "571" }), "current DLC and low-violence filtering");
            var migration = Parse(Leaf("Depot 575 dlcappid", null, "575") + Leaf("Depot 575 isdlc", "1", null));
            Check(migration.ParsedAllDepotChanges && migration.Deltas.Any(d => d.Path == "depots/575/isdlc"), "legacy isdlc retained under its real path");
            state.Reverse(migration);
            Check(state.Value("depots/575/dlcappid") == null && state.Value("depots/575/isdlc") == "1" && state.SelectWindowsClient("schinese").All(d => d.DepotId != "575"), "isdlc migration preserves DLC exclusion");
            var lowViolence = Parse(Leaf("Depot 578 config/lowviolence", null, "1") + Leaf("Depot 578 config/lv", "1", null)); state.Reverse(lowViolence);
            Check(state.Value("depots/578/config/lowviolence") == null && state.Value("depots/578/config/lv") == "1" && state.SelectWindowsClient("schinese").All(d => d.DepotId != "578"), "lv migration preserves exclusion and raw key");
            Reject(() => state.Reverse(migration), "legacy migration still detects mismatched later state");
            foreach (string key in new[] { "Depot 575 isdlc", "Depot 578 config/lv", "markdlcdepots" })
            {
                Check(!Parse(Leaf(key, "2", "1")).ParsedAllDepotChanges, key + " old invalid boolean rejected");
                Check(!Parse(Leaf(key, "1", "2")).ParsedAllDepotChanges, key + " new invalid boolean rejected");
            }
            Check(!Parse("<li><span>Changed <span class='branch-name'>public</span> branch</span><ul>" + Leaf("markdlcdepots", "0", "1") + "</ul></li>").ParsedAllDepotChanges, "app marker cannot appear under branch");
            Reject(() => State().Reverse(Parse(Leaf("markdlcdepots", "0", "1"))), "unknown baseline flag requires matching proved isdlc creation");
            var enable = Parse(Leaf("markdlcdepots", "0", "1") + Leaf("Depot 575 isdlc", null, "1"));
            state.Reverse(enable);
            Check(state.HasKnownLegacyDlcMarking && state.Value(HistoryDepotState.LegacyDlcMarkingPath) == "0" && state.Value("depots/575/isdlc") == null, "real event establishes disabled historical classification state");
            Check(state.SelectWindowsClient("schinese").Select(d => d.DepotId).SequenceEqual(new[] { "571", "575" }), "disabled global marker uses exact historical per-Depot DLC and lv fields");
            state.Reverse(Parse(Leaf("markdlcdepots", null, "0") + Leaf("Depot 578 config/lv", null, "1")));
            Check(state.HasKnownLegacyDlcMarking && state.Value(HistoryDepotState.LegacyDlcMarkingPath) == null, "known flag deletion remains distinguishable from never observed");
            Check(state.SelectWindowsClient("schinese").Select(d => d.DepotId).SequenceEqual(new[] { "571", "575", "578" }), "known absent marker retains historical scope without future isdlc or lv backfill");
            var clone = (HistoryDepotState)typeof(OnlineHistoryResolver).GetMethod("CloneState", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { state });
            Check(clone.HasKnownLegacyDlcMarking, "CloneState keeps marker history"); Check(clone.SelectWindowsClient("schinese").Select(d => d.DepotId).SequenceEqual(new[] { "571", "575", "578" }), "cloned old state retains exact historical scope");
            var unknown = State(); unknown.Values.Clear(); foreach (var pair in state.Values) unknown.Values.Add(pair.Key, pair.Value);
            bool same = (bool)typeof(OnlineHistoryResolver).GetMethod("SameState", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { state, unknown });
            Check(!same && HistoryAnchorStore.Identity(Baseline(state)) != HistoryAnchorStore.Identity(Baseline(unknown)), "state equality and anchor identity distinguish known-deleted metadata");
            var baseline = Baseline(state); var emptyChange = Parse(Leaf("appmanagesdlc", null, "1"));
            string directory = Path.Combine(root, "scope-roundtrip-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            new HistoryCacheStore(directory).Save(baseline, new[] { emptyChange });
            new HistoryAnchorStore(directory).Save(new[] { new HistoryAnchorStore.Anchor { Identity = HistoryAnchorStore.Identity(baseline), Provenance = "BrowserBaselineAndContinuousHistory", Baseline = baseline, Changes = new List<HistoryChange> { emptyChange } } });
            var cache = new HistoryCacheStore(directory).Load(); var anchor = new HistoryAnchorStore(directory).Load().Single();
            Check(cache.Baseline.State.HasKnownLegacyDlcMarking && anchor.Baseline.State.HasKnownLegacyDlcMarking, "v1 and v2 preserve known-deleted state after restart");
            Check(cache.Baseline.State.SelectWindowsClient("schinese").Select(d => d.DepotId).SequenceEqual(new[] { "571", "575", "578" }), "cache restart preserves historical Depot scope");
            Check(HistoryAnchorStore.Identity(Baseline(State())) == modernIdentity, "unaffected modern baseline identity unchanged");
        }
        public static int Main(string[] args)
        {
            try
            {
                string root = Path.GetFullPath(args[0]), source = Path.GetFullPath(args[1]), capture = Path.GetFullPath(args[2]); UnitChecks(root);
                string target = Path.Combine(root, "run-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"), "replayed-cache"); Directory.CreateDirectory(target);
                string[] names = { "verified-public-history-v1.json", "verified-public-anchors-v2.json" };
                var hashes = names.ToDictionary(n => n, n => Hash(Path.Combine(source, n))); string captureHash = Hash(capture);
                foreach (string name in names) File.Copy(Path.Combine(source, name), Path.Combine(target, name));
                var json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue, RecursionLimit = 200 };
                var snapshot = json.Deserialize<HistoryPageSnapshot>(File.ReadAllText(capture)); var listing = Parser.ParseHistory(snapshot);
                foreach (string id in new[] { "264945", "252568", "224449", "224052" }) Check(listing.Changes.Single(c => c.ChangeId == id).ParsedAllDepotChanges, "actual old schema parsed " + id);
                // The input archive may have advanced since this snapshot was
                // captured. Build a verified overlap prefix in the test directory
                // so the same saved evidence still exercises a real append.
                var full = new HistoryCacheStore(target).Load();
                var prefix = full.Changes.TakeWhile(c => c.ChangeId != listing.Changes[0].ChangeId).Concat(full.Changes.Where(c => c.ChangeId == listing.Changes[0].ChangeId)).ToList();
                Check(prefix.Count > 0 && prefix.Last().ChangeId == listing.Changes[0].ChangeId, "saved page has an exact overlap in full source chain");
                new HistoryCacheStore(target).Save(full.Baseline, prefix);
                var anchors = new HistoryAnchorStore(target).Load();
                foreach (var a in anchors) a.Changes = a.Changes.TakeWhile(c => c.ChangeId != listing.Changes[0].ChangeId).Concat(a.Changes.Where(c => c.ChangeId == listing.Changes[0].ChangeId)).ToList();
                new HistoryAnchorStore(target).Save(anchors);
                var old = new HistoryCacheStore(target).Load(); var known = new HashSet<string>(old.Changes.Select(c => c.ChangeId));
                var browser = new SavedBrowser(snapshot); var resolver = new OnlineHistoryResolver(browser, null, target); bool exhausted = false;
                try { resolver.ArchiveRangeAsync(DateTimeOffset.Parse("2013-02-02T00:00:00Z"), DateTimeOffset.Parse("2015-12-22T00:00:00Z"), CancellationToken.None, null).GetAwaiter().GetResult(); }
                catch (EndOfSavedEvidenceException) { exhausted = true; }
                Check(exhausted && browser.Reads == 2, "entire old-schema batch replayed until saved evidence ends");
                var saved = new HistoryCacheStore(target).Load(); int count = listing.Changes.Count(c => !known.Contains(c.ChangeId));
                Check(saved.Changes.Count == old.Changes.Count + count && saved.Changes.Last().ChangeId == listing.Changes.Last().ChangeId, "complete captured batch preserved through production archive path");
                foreach (var anchor in new HistoryAnchorStore(target).Load()) resolver.ValidateAnchorAsync(anchor, CancellationToken.None).GetAwaiter().GetResult(); Check(true, "updated full chains validate after serialization");
                foreach (var change in saved.Changes) saved.Baseline.State.Reverse(change);
                Check(saved.Baseline.State.HasKnownLegacyDlcMarking && saved.Baseline.State.Value(HistoryDepotState.LegacyDlcMarkingPath) == null, "actual oldest state retains earlier missing global marker");
                Check(saved.Baseline.State.SelectWindowsClient("schinese").Select(d => d.DepotId).SequenceEqual(new[] { "571", "572", "573", "575" }), "actual old state uses all four historical public depots without future DLC backfill");
                Check(names.All(n => hashes[n] == Hash(Path.Combine(source, n))) && captureHash == Hash(capture), "source inputs not modified");
                var result = new { Passed = true, Checks = checks, ReplayedCache = target, OriginalRecords = old.Changes.Count, NewRecords = count, TotalRecords = saved.Changes.Count, OldestChangeId = saved.Changes.Last().ChangeId, OldestObservedAtUtc = saved.Changes.Last().ObservedAtUtc.ToUniversalTime().ToString("o"), StoppedReason = "EndOfSavedEvidence", NextUnknownField = (string)null, RealNetworkUsed = false, SourceHashes = hashes, SnapshotSha256 = captureHash, LegacyScope = "isdlc and config/lv retain real 0/1 state and exclude value 1. App markdlcdepots raw state and reverse continuity are retained; 0 or known absence no longer veto a complete historical baseline. Selection uses only per-Depot metadata reconstructed for the exact event, without future classification backfill. No dates or Build IDs define scope." };
                string report = json.Serialize(result); File.WriteAllText(Path.Combine(root, "results.json"), report, new UTF8Encoding(false)); Console.WriteLine(report); return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
        private static string Hash(string file) { using (var sha = SHA256.Create()) using (var stream = File.OpenRead(file)) return String.Concat(sha.ComputeHash(stream).Select(b => b.ToString("x2"))); }
        private sealed class EndOfSavedEvidenceException : Exception { internal EndOfSavedEvidenceException() : base("EndOfSavedEvidence") { } }
        private sealed class SavedBrowser : IHistoryBrowser, IHistoryDateBrowser, IHistoryDateNavigationBrowser
        {
            private readonly HistoryPageSnapshot snapshot; internal int Reads;
            internal SavedBrowser(HistoryPageSnapshot value) { snapshot = value; }
            public Task OpenHistoryForDateAsync(CancellationToken token) { return Task.FromResult(true); }
            public Task<HistoryPageSnapshot> LoadHistoryBeforeAsync(DateTime date, CancellationToken token) { if (++Reads == 1) return Task.FromResult(snapshot); throw new EndOfSavedEvidenceException(); }
            public Task<HistoryPageSnapshot> NavigateAsync(string url, CancellationToken token) { throw new Exception("Unexpected navigation: " + url); }
            public Task<HistoryPageSnapshot> CaptureAsync(CancellationToken token) { throw new Exception("Unexpected capture"); }
            public Task<HistoryPageSnapshot> ClickAsync(string selector, CancellationToken token) { throw new EndOfSavedEvidenceException(); }
        }
    }
}
