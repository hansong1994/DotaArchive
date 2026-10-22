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
    internal static class OptionalDlcHistoryTests
    {
        private const string Prefix = "depots/381450/";
        private static int checks;
        private static void Check(bool value, string name) { if (!value) throw new Exception("FAILED: " + name); checks++; }
        private static HistoryDepotState State()
        {
            var state = new HistoryDepotState { CompleteBaseline = true };
            state.Values.Add("depots/373301/manifests/public/gid", "100");
            state.Values.Add(Prefix + "manifests/public/gid", "200");
            state.Values.Add(Prefix + "dlcappid", "313250");
            return state;
        }
        private static HistoryChange Change(string oldValue = "373300", string newValue = "313250")
        {
            var change = new HistoryChange { ChangeId = "1258534", ParsedAllDepotChanges = true };
            change.Deltas.Add(new HistoryDelta { Path = Prefix + "config/optionaldlc", OldExists = oldValue != null, OldValue = oldValue, NewExists = true, NewValue = newValue });
            change.Deltas.Add(new HistoryDelta { Path = Prefix + "dlcappid", OldExists = oldValue != null, OldValue = oldValue, NewExists = true, NewValue = newValue });
            return change;
        }
        private static void Reject(HistoryDepotState state, HistoryChange change, string name)
        {
            string before = String.Join("|", state.Values.OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value));
            try { state.Reverse(change); } catch (HistoryResolutionException)
            {
                Check(before == String.Join("|", state.Values.OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value)), name + " does not half-apply"); return;
            }
            throw new Exception("Expected rejection: " + name);
        }
        public static int Main(string[] args)
        {
            try
            {
                SyntheticChecks();
                string root = Path.GetFullPath(args[0]), sourceDirectory = Path.GetFullPath(args[1]), sourceSnapshot = Path.GetFullPath(args[2]);
                string replayDirectory = Path.Combine(root, "run-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"), "replayed-cache");
                Directory.CreateDirectory(replayDirectory);
                string[] cacheNames = { "verified-public-history-v1.json", "verified-public-anchors-v2.json" };
                var originalHashes = cacheNames.ToDictionary(n => n, n => Hash(Path.Combine(sourceDirectory, n)));
                string originalSnapshotHash = Hash(sourceSnapshot);
                foreach (string name in cacheNames) File.Copy(Path.Combine(sourceDirectory, name), Path.Combine(replayDirectory, name));
                var serializer = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue, RecursionLimit = 200 };
                var snapshot = serializer.Deserialize<HistoryPageSnapshot>(File.ReadAllText(sourceSnapshot));
                var parsed = new SteamDbHistoryParser().ParseHistory(snapshot);
                // Reconstruct the saved page's overlap prefix in test-owned
                // files; the live source archive may already contain this batch.
                var full = new HistoryCacheStore(replayDirectory).Load();
                var prefix = full.Changes.TakeWhile(c => c.ChangeId != parsed.Changes[0].ChangeId).Concat(full.Changes.Where(c => c.ChangeId == parsed.Changes[0].ChangeId)).ToList();
                Check(prefix.Count > 0 && prefix.Last().ChangeId == parsed.Changes[0].ChangeId, "saved page overlaps the complete source chain");
                new HistoryCacheStore(replayDirectory).Save(full.Baseline, prefix);
                var anchors = new HistoryAnchorStore(replayDirectory).Load();
                foreach (var a in anchors) a.Changes = a.Changes.TakeWhile(c => c.ChangeId != parsed.Changes[0].ChangeId).Concat(a.Changes.Where(c => c.ChangeId == parsed.Changes[0].ChangeId)).ToList();
                new HistoryAnchorStore(replayDirectory).Save(anchors);
                var original = new HistoryCacheStore(replayDirectory).Load();
                var originalIds = new HashSet<string>(original.Changes.Select(c => c.ChangeId));
                var actual = parsed.Changes.Single(c => c.ChangeId == "1258534");
                Check(actual.Deltas.Count == 2 && actual.Deltas.All(d => d.OldValue == "373300" && d.NewValue == "313250" && d.OldExists && d.NewExists), "actual DOM contains identical paired DLC migration");
                foreach (HistoryChange change in original.Changes) original.Baseline.State.Reverse(change);
                Check(original.Baseline.State.Value(Prefix + "dlcappid") == "313250" && original.Baseline.State.Value(Prefix + "config/optionaldlc") == null && !original.Baseline.State.KnownOptionalDlcPaths.Contains(Prefix + "config/optionaldlc"), "actual chain reaches unknown table-projected optional value");
                var browser = new SavedBrowser(snapshot);
                var resolver = new OnlineHistoryResolver(browser, null, replayDirectory);
                bool exhausted = false;
                try { resolver.ArchiveRangeAsync(DateTimeOffset.Parse("2013-02-02T00:00:00Z", CultureInfo.InvariantCulture), DateTimeOffset.Parse("2015-12-22T00:00:00Z", CultureInfo.InvariantCulture), CancellationToken.None, null).GetAwaiter().GetResult(); }
                catch (EndOfSavedEvidenceException) { exhausted = true; }
                Check(exhausted && browser.Reads == 2, "only saved snapshot replayed and next evidence request stopped without network");
                var saved = new HistoryCacheStore(replayDirectory).Load();
                int expectedNew = parsed.Changes.Count(c => !originalIds.Contains(c.ChangeId));
                Check(saved.Changes.Count == original.Changes.Count + expectedNew, "entire captured batch appended through normal ArchiveRange validation");
                Check(saved.Changes.Last().ChangeId == parsed.Changes.Last().ChangeId, "replayed cache ends at raw batch oldest record");
                foreach (var anchor in new HistoryAnchorStore(replayDirectory).Load()) resolver.ValidateAnchorAsync(anchor, CancellationToken.None).GetAwaiter().GetResult();
                Check(true, "updated v2 full replay validates Build and Depot continuity");
                Check(cacheNames.All(n => originalHashes[n] == Hash(Path.Combine(sourceDirectory, n))) && originalSnapshotHash == Hash(sourceSnapshot), "original caches and captured snapshot untouched");
                foreach (HistoryChange change in saved.Changes) saved.Baseline.State.Reverse(change);
                Check(saved.Baseline.State.KnownOptionalDlcPaths.Contains(Prefix + "config/optionaldlc") && saved.Baseline.State.Value(Prefix + "config/optionaldlc") == null, "older creation record strictly removes now-known optional field");
                var result = new { Passed = true, Checks = checks, ReplayedCache = replayDirectory, OriginalRecords = original.Changes.Count, NewRecords = expectedNew, TotalRecords = saved.Changes.Count, OldestChangeId = saved.Changes.Last().ChangeId, OldestObservedAtUtc = saved.Changes.Last().ObservedAtUtc.ToUniversalTime().ToString("o"), StoppedReason = "EndOfSavedEvidence", NextUnknownField = (string)null, FailingChangeFixed = "1258534", RealNetworkUsed = false, SourceHashes = originalHashes, SnapshotSha256 = originalSnapshotHash };
                string json = serializer.Serialize(result);
                File.WriteAllText(Path.Combine(root, "results.json"), json, new UTF8Encoding(false)); Console.WriteLine(json); return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
        private static void SyntheticChecks()
        {
            HistoryDepotState state = State(); Check(state.SelectWindowsClient("schinese").All(d => d.DepotId != "381450"), "proven DLC exclusion before optional migration");
            state.Reverse(Change());
            Check(state.Value(Prefix + "dlcappid") == "373300" && state.Value(Prefix + "config/optionaldlc") == "373300" && state.KnownOptionalDlcPaths.Contains(Prefix + "config/optionaldlc"), "paired migration establishes and restores exact optional value");
            Check(state.SelectWindowsClient("schinese").All(d => d.DepotId != "381450"), "same proven exclusion after migration");
            state.Reverse(Change(null, "373300"));
            Check(state.Value(Prefix + "config/optionaldlc") == null && state.Value(Prefix + "dlcappid") == null && state.KnownOptionalDlcPaths.Contains(Prefix + "config/optionaldlc"), "earlier paired creation remains strictly validated");
            HistoryChange change = Change(); change.Deltas.RemoveAt(1); Reject(State(), change, "unpaired optional migration");
            change = Change(); change.Deltas[1].OldValue = "1"; Reject(State(), change, "different old DLC app");
            change = Change(); change.Deltas[1].NewValue = "1"; Reject(State(), change, "different new DLC app");
            change = Change(); change.Deltas[1].OldExists = false; change.Deltas[1].OldValue = null; Reject(State(), change, "different old presence");
            state = State(); state.Values[Prefix + "dlcappid"] = "1"; Reject(state, Change(), "unproven current DLC app");
            state = State(); state.KnownOptionalDlcPaths.Add(Prefix + "config/optionaldlc"); Reject(state, Change(), "known absent optional field");
            state = State(); state.KnownOptionalDlcPaths.Add(Prefix + "config/optionaldlc"); state.Values[Prefix + "config/optionaldlc"] = "999"; Reject(state, Change(), "known conflicting optional field");
            state = State(); state.Values[Prefix + "config/optionaldlc"] = "999"; Reject(state, Change(), "existing unmarked optional field");
            Reject(State(), Change("0"), "old zero changes scope");
            state = State(); state.Values[Prefix + "dlcappid"] = "0"; Reject(state, Change("373300", "0"), "new zero changes scope");
            Reject(State(), Change("invalid"), "old nonnumeric app");
            state = State(); state.Values[Prefix + "dlcappid"] = "invalid"; Reject(state, Change("373300", "invalid"), "new nonnumeric app");
            state = State(); state.Reverse(Change(null)); Check(state.Value(Prefix + "dlcappid") == null && state.Value(Prefix + "config/optionaldlc") == null, "original paired-addition compatibility preserved");
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
