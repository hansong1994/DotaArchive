using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
namespace DotaArchive
{
    internal static class RootDepotMetadataTests
    {
        private static int checks;
        private static readonly SteamDbHistoryParser Parser = new SteamDbHistoryParser();
        private static void Check(bool value, string label) { if (!value) throw new Exception("FAILED: " + label); checks++; }
        private static string Leaf(string key, string oldValue, string newValue)
        { return "<li class='diff-modified'><span><i>" + key + ":</i>" + (oldValue == null ? "" : "<del>" + oldValue + "</del>") + (newValue == null ? "" : "<ins>" + newValue + "</ins>") + "</span></li>"; }
        private static HistoryPageSnapshot Page(string tree)
        { return new HistoryPageSnapshot { Url = "https://steamdb.info/app/570/history/?filterkey=2", Html = "<main><div class='panel-history' data-changeid='630168'><relative-time datetime='2014-08-05T23:11:58Z'></relative-time><ul><li class='repo'><span>Changed <i>Depots</i><ul>" + tree + "</ul></span></li></ul></div></main>" }; }
        private static HistoryChange Parse(string tree) { return Parser.ParseHistory(Page(tree)).Changes.Single(); }
        private static void UnitChecks()
        {
            foreach (string key in new[] { "appmanagesdlc", "workshopdepot" })
            {
                string value = key == "appmanagesdlc" ? "1" : "570";
                foreach (var pair in new[] { new[] { (string)null, value }, new[] { value, (string)null }, new[] { value, value } })
                {
                    var change = Parse(Leaf(key, pair[0], pair[1]));
                    Check(change.ParsedAllDepotChanges && change.Deltas.Count == 0 && change.Publication == null, key + " exact root scalar outside fixed projection");
                }
                Check(!Parse(Leaf(key, "999", value)).ParsedAllDepotChanges, key + " invalid old value rejected");
                Check(!Parse(Leaf(key, value, "999")).ParsedAllDepotChanges, key + " invalid new value rejected");
                Check(!Parse(Leaf(key, "", value)).ParsedAllDepotChanges, key + " empty present value rejected");
                Check(!Parse(Leaf("Depot 1 " + key, null, value)).ParsedAllDepotChanges, key + " same named per-depot field rejected");
                Check(!Parse("<li><span>Changed <span class='branch-name'>public</span> branch</span><ul>" + Leaf(key, null, value) + "</ul></li>").ParsedAllDepotChanges, key + " public branch context rejected");
                Check(!Parse("<li><span>Changed <span class='branch-name'>beta</span> branch</span><ul>" + Leaf(key, null, value) + "</ul></li>").ParsedAllDepotChanges, key + " private branch context rejected");
                Check(!Parse("<li><span>Changed <i>unknown</i></span><ul>" + Leaf(key, null, value) + "</ul></li>").ParsedAllDepotChanges, key + " nested scalar rejected");
                Check(!Parse("<li><span>Changed <i>" + key + "</i></span><ul>" + Leaf("unknown", null, value) + "</ul></li>").ParsedAllDepotChanges, key + " nested tree rejected");
            }
            Check(Parse(Leaf("appmanagesdlc", "0", "1")).ParsedAllDepotChanges && Parse(Leaf("appmanagesdlc", "1", "0")).ParsedAllDepotChanges, "both boolean sides accepted only at root");
            Check(!Parse(Leaf("workshopdepot", "0", "570")).ParsedAllDepotChanges, "workshop zero is not inferred as known metadata");
            Check(!Parse(Leaf("unknownroot", null, "1")).ParsedAllDepotChanges, "unknown root still hard gap");
            string manifest = "<li><span>Changed <span class='branch-name'>public</span> branch</span><ul>" + Leaf("Depot 1 gid", "100", "200") + "</ul></li>";
            HistoryChange mixed = Parse(Leaf("appmanagesdlc", null, "1") + Leaf("workshopdepot", null, "570") + manifest);
            Check(mixed.ParsedAllDepotChanges && mixed.Deltas.Count == 1 && mixed.Deltas[0].Path == "depots/1/manifests/public/gid", "same-event numerical Depot delta retained");
            var state = new HistoryDepotState { CompleteBaseline = true }; state.Values.Add("depots/1/manifests/public/gid", "200"); state.Reverse(mixed);
            Check(state.Value("depots/1/manifests/public/gid") == "100", "same-event manifest continuity applied");
            bool rejected = false; try { state.Reverse(mixed); } catch (HistoryResolutionException) { rejected = true; }
            Check(rejected, "same-event manifest contradiction remains rejected");
        }
        public static int Main(string[] args)
        {
            try
            {
                UnitChecks();
                string root = Path.GetFullPath(args[0]), source = Path.GetFullPath(args[1]), capture = Path.GetFullPath(args[2]);
                string target = Path.Combine(root, "run-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"), "replayed-cache"); Directory.CreateDirectory(target);
                string[] names = { "verified-public-history-v1.json", "verified-public-anchors-v2.json" };
                var hashes = names.ToDictionary(n => n, n => Hash(Path.Combine(source, n))); string captureHash = Hash(capture);
                foreach (string name in names) File.Copy(Path.Combine(source, name), Path.Combine(target, name));
                var json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue, RecursionLimit = 200 };
                var snapshot = json.Deserialize<HistoryPageSnapshot>(File.ReadAllText(capture));
                var listing = Parser.ParseHistory(snapshot);
                foreach (string id in new[] { "630168", "630133" })
                {
                    HistoryChange actual = listing.Changes.Single(c => c.ChangeId == id);
                    Check(actual.ParsedAllDepotChanges && actual.Deltas.Count == 0 && actual.Publication == null, "captured root-only event " + id + " safely projects to no client state change");
                }
                var old = new HistoryCacheStore(target).Load(); var known = new HashSet<string>(old.Changes.Select(c => c.ChangeId));
                var browser = new SavedBrowser(snapshot); var resolver = new OnlineHistoryResolver(browser, null, target);
                bool exhausted = false;
                try { resolver.ArchiveRangeAsync(DateTimeOffset.Parse("2013-02-02T00:00:00Z", CultureInfo.InvariantCulture), DateTimeOffset.Parse("2015-12-22T00:00:00Z", CultureInfo.InvariantCulture), CancellationToken.None, null).GetAwaiter().GetResult(); }
                catch (EndOfSavedEvidenceException) { exhausted = true; }
                Check(exhausted && browser.Reads == 2, "saved evidence exhausted without a network request");
                var saved = new HistoryCacheStore(target).Load();
                int count = listing.Changes.Count(c => !known.Contains(c.ChangeId));
                Check(saved.Changes.Count == old.Changes.Count + count && saved.Changes.Last().ChangeId == listing.Changes.Last().ChangeId, "all saved batch records pass ArchiveRange continuity");
                foreach (var anchor in new HistoryAnchorStore(target).Load()) resolver.ValidateAnchorAsync(anchor, CancellationToken.None).GetAwaiter().GetResult();
                Check(true, "all updated anchors replay against full baseline");
                Check(names.All(n => hashes[n] == Hash(Path.Combine(source, n))) && captureHash == Hash(capture), "original inputs unchanged");
                var result = new { Passed = true, Checks = checks, ReplayedCache = target, OriginalRecords = old.Changes.Count, NewRecords = count, TotalRecords = saved.Changes.Count, OldestChangeId = saved.Changes.Last().ChangeId, OldestObservedAtUtc = saved.Changes.Last().ObservedAtUtc.ToUniversalTime().ToString("o"), StoppedReason = "EndOfSavedEvidence", NextUnknownField = (string)null, RealNetworkUsed = false, SourceHashes = hashes, SnapshotSha256 = captureHash, ProjectionScope = "Explicit standard Windows client; all per-Depot DLC/optional-DLC excluded; non-UGC download. Root flag does not add/remove numerical Depot state.", LocalSource = "DepotDownloader/DepotDownloader/ContentDownloader.cs:490-570: workshopdepot is used only in isUgc; normal path enumerates numerical Depot IDs and explicit requested IDs.", EvidenceLimit = "Steamworks optional-DLC documentation explains app-controlled DLC installation, but does not directly define the literal appmanagesdlc key. Root-only exclusion is a claim about this program's fixed projection, not proof of general Steam installer semantics. Original scalar old/new existence and values remain in the sanitized raw batch." };
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
