using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Web.Script.Serialization;

namespace DotaArchive
{
    internal static class LocalArchiveResolverTests
    {
        private static int checks;
        private static string root;
        private static DateTimeOffset T(string text) { return DateTimeOffset.Parse(text, CultureInfo.InvariantCulture); }
        private static void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; }
        private static void Reject(Action action, string label)
        { try { action(); } catch (OperationCanceledException) { throw; } catch { checks++; return; } throw new Exception("Expected rejection: " + label); }
        private static HistoryQuery At(string text) { return new HistoryQuery { RequestedUtc = T(text) }; }
        private static ResolvedHistoryPlan Resolve(string dir, HistoryQuery query, OfficialPatchMatch proof = null)
        { return new LocalArchiveResolver(dir).ResolveAsync(query, proof, CancellationToken.None, null).GetAwaiter().GetResult(); }
        private static string Hash(string path)
        { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))); }
        private static string RunDirectory(string name) { string path = Path.Combine(root, name); Directory.CreateDirectory(path); return path; }
        private static HistoryAnchorStore.Anchor Synthetic()
        {
            var state = new HistoryDepotState { CompleteBaseline = true };
            state.Values.Add("depots/1/manifests/public/gid", "4000");
            state.Values.Add("depots/2/manifests/public/gid", "9000");
            var baseline = new HistoryBaseline { BuildId = "40", PublishedAtUtc = T("2020-01-04T00:00:00Z"), SourceUrl = "https://steamdb.info/app/570/depots/?branch=public", State = state };
            string[] ids = { "400", "300", "200", "100", "50" }, builds = { "40", "10", "20", "10", "5", "1" }, gids = { "4000", "3000", "2000", "1000", "500", "100" };
            var changes = new List<HistoryChange>();
            for (int i = 0; i < ids.Length; i++)
            {
                DateTimeOffset time = baseline.PublishedAtUtc.AddDays(-i);
                var c = new HistoryChange { ChangeId = ids[i], ObservedAtUtc = time.AddSeconds(5), ParsedAllDepotChanges = true, SourceUrl = "https://steamdb.info/app/570/history/?changeid=" + ids[i],
                    Publication = new HistoryBuildEvent { BuildId = builds[i], PreviousBuildId = builds[i + 1], PublishedAtUtc = time, PreviousPublishedAtUtc = time.AddDays(-1), ChangeId = ids[i], IsPublic = true } };
                c.Deltas.Add(new HistoryDelta { Path = "depots/1/manifests/public/gid", NewExists = true, OldExists = true, NewValue = gids[i], OldValue = gids[i + 1] });
                changes.Add(c);
            }
            return new HistoryAnchorStore.Anchor { Identity = HistoryAnchorStore.Identity(baseline), Provenance = "BrowserBaselineAndContinuousHistory", Baseline = baseline, Changes = changes };
        }
        private static string Save(string name, HistoryAnchorStore.Anchor anchor, bool v2 = true, bool v1 = true)
        {
            string dir = RunDirectory(name);
            if (v2) new HistoryAnchorStore(dir).Save(new[] { anchor });
            if (v1) new HistoryCacheStore(dir).Save(anchor.Baseline, anchor.Changes);
            return dir;
        }
        private static OfficialPatchMatch Proof(string build, string change, string kind)
        {
            return new OfficialPatchMatch { Kind = kind, Build = new HistoryBuildEvent { BuildId = build, ChangeId = change, PublishedAtUtc = T("2020-01-02T00:00:00Z"), SourceUrl = "https://steamdb.info/patchnotes/" + build + "/", IsPublic = true },
                Sources = new List<string> { "https://steamdb.info/patchnotes/" + build + "/", "https://www.dota2.com/patches/7.00" }, Assurance = "Synthetic verified official proof" };
        }
        private static HistoryPageSnapshot BaselinePage(HistoryAnchorStore.Anchor anchor, DateTimeOffset captured)
        {
            long seconds = (long)(anchor.Baseline.PublishedAtUtc - T("1970-01-01T00:00:00Z")).TotalSeconds;
            string html = "<div id='depots'>Selected Branch: public<table><tr><td>buildid</td><td>" + anchor.Baseline.BuildId + "</td></tr><tr><td>timeupdated</td><td>" + seconds + "</td></tr>";
            foreach (var pair in anchor.Baseline.State.Values.Where(p => p.Key.EndsWith("/manifests/public/gid"))) {
                string id = pair.Key.Split('/')[1];
                html += "<tr class='depot' data-depotid='" + id + "'><td class='depot-config'></td><td><a href='/depot/" + id + "/history/?changeid=M:" + pair.Value + "'>" + pair.Value + "</a></td></tr>";
            }
            return new HistoryPageSnapshot { Url = anchor.Baseline.SourceUrl, Title = "Synthetic public baseline", Html = html + "</table></div>", CapturedAtUtc = captured };
        }
        private static string Generation(SyncCatalogStore store, HistoryAnchorStore.Anchor anchor)
        {
            string generation = Path.Combine(store.RootDirectory, "generations", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(generation);
            new HistoryAnchorStore(generation).Save(new[] { anchor });
            new HistoryCacheStore(generation).Save(anchor.Baseline, anchor.Changes);
            return generation;
        }
        private static void RewriteManifest(string sync, Action<SyncCatalogStore.Manifest> action)
        {
            var json = new JavaScriptSerializer();
            string pointerPath = Path.Combine(sync, "current.json");
            var pointer = json.Deserialize<SyncCatalogStore.Current>(File.ReadAllText(pointerPath));
            string manifestPath = Path.Combine(sync, "generations", pointer.Generation, "manifest.json");
            var manifest = json.Deserialize<SyncCatalogStore.Manifest>(File.ReadAllText(manifestPath));
            action(manifest); File.WriteAllText(manifestPath, json.Serialize(manifest));
            pointer.ManifestSha256 = Hash(manifestPath).Replace("-", "").ToLowerInvariant();
            File.WriteAllText(pointerPath, json.Serialize(pointer));
        }
        private static void RunSyncCatalogTests()
        {
            // Short ASCII paths also exercise .NET Framework's normal file APIs.
            string sync = Path.Combine(Path.GetFullPath(Path.Combine(root, "../../../../../..")), "work", "sc-" + Guid.NewGuid().ToString("N").Substring(0, 8), "latest-sync");
            var store = new SyncCatalogStore(sync);
            var anchor = Synthetic();
            Check(store.LoadActive() == null, "Unpublished synchronized database is absent");
            string staged = Generation(store, anchor);
            Check(store.LoadActive() == null && Resolve(sync, At("2020-01-04T10:00:00Z")) == null, "Complete checkpoint without current commit cannot extend coverage");
            var first = BaselinePage(anchor, T("2020-01-04T12:00:00Z"));
            var confirmed = BaselinePage(anchor, T("2020-01-04T13:00:00Z"));
            var published = store.Publish(staged, first, confirmed, first.CapturedAtUtc, 2);
            Check(published.TotalRecords == 5 && published.AddedRecords == 2 && published.AnchorIdentity == anchor.Identity, "Atomic publication derives real counts and anchor identity");
            var loaded = store.LoadActive();
            Check(new SyncCatalogStore(sync + Path.DirectorySeparatorChar).LoadActive().AnchorIdentity == loaded.AnchorIdentity, "Trailing directory separator does not change generation path validation");
            Check(loaded.VerifiedThroughUtc == first.CapturedAtUtc && loaded.FirstBaselineCapturedAtUtc == first.CapturedAtUtc, "Coverage bound is tied to original first capture evidence");
            Check(loaded.LatestBuildId == "40" && loaded.LatestBuildPublishedAtUtc == anchor.Baseline.PublishedAtUtc && loaded.LatestChangeId == "400", "Displayed synchronization metadata is verified from the chain");
            Check(Resolve(sync, At("2020-01-04T10:00:00Z")).BuildId == "40", "No-new-build quiet period resolves current build offline");
            Check(Resolve(sync, At("2020-01-04T12:00:00Z")).BuildId == "40", "Query exactly at verified capture bound remains covered");
            Check(Resolve(sync, At("2020-01-04T12:00:00.0000001Z")) == null, "Query later than verified capture bound misses");
            Check(Resolve(sync, At("2020-01-04T00:00:00Z")).BuildId == "10", "Synchronization keeps strictly-before public release equality");
            Check(Resolve(staged, At("2020-01-04T10:00:00Z")) == null, "Generation data alone without validated current pointer has conservative coverage");
            string currentPath = Path.Combine(sync, "current.json"), currentHash = Hash(currentPath);
            Reject(() => store.Publish(Generation(store, anchor), first, confirmed, first.CapturedAtUtc.AddTicks(1), 0), "Coverage cannot exceed first capture even when final confirmation is later");
            Check(Hash(currentPath) == currentHash, "Failed publication preserves previous committed database");
            var mismatch = BaselinePage(anchor, confirmed.CapturedAtUtc); mismatch.Html = mismatch.Html.Replace("<td>40</td>", "<td>41</td>");
            Reject(() => store.Publish(Generation(store, anchor), first, mismatch, first.CapturedAtUtc, 0), "Changed final baseline cannot publish stale quiet-period coverage");
            var broken = Synthetic(); broken.Changes.RemoveAt(2);
            Reject(() => store.Publish(Generation(store, broken), first, confirmed, first.CapturedAtUtc, 0), "Incomplete historical chain cannot publish a completed generation");
            Check(Hash(currentPath) == currentHash, "Invalid raw evidence and failed full replay preserve active pointer");
            bool cancelled = false;
            try { store.LoadActive(new CancellationToken(true)); } catch(OperationCanceledException) { cancelled = true; }
            Check(cancelled, "Cancellation interrupts active synchronized database validation");
            first = BaselinePage(anchor, T("2020-01-05T12:00:00Z")); confirmed = BaselinePage(anchor, T("2020-01-05T13:00:00Z"));
            loaded = store.Publish(Generation(store, anchor), first, confirmed, first.CapturedAtUtc, 0);
            Check(loaded.AddedRecords == 0 && Resolve(sync, At("2020-01-05T10:00:00Z")).BuildId == "40", "Second no-new-build synchronization advances verified quiet period");
            string committedPointer = File.ReadAllText(currentPath), manifestPath = Path.Combine(loaded.GenerationDirectory, "manifest.json"), committedManifest = File.ReadAllText(manifestPath);
            RewriteManifest(sync, m => m.VerifiedThroughUtc = T("2020-01-06T12:00:00Z").ToString("o"));
            Reject(() => store.LoadActive(), "Rehashed manifest still cannot exceed raw first capture timestamp");
            File.WriteAllText(currentPath, committedPointer); File.WriteAllText(manifestPath, committedManifest);
            RewriteManifest(sync, m => m.FullChainVerified = false);
            Reject(() => Resolve(sync, At("2020-01-05T10:00:00Z")), "Partial marker cannot masquerade as a published database");
            File.WriteAllText(currentPath, committedPointer); File.WriteAllText(manifestPath, committedManifest);
            RewriteManifest(sync, m => m.AnchorIdentity = new string('0', 64));
            Reject(() => store.LoadActive(), "Rehashed identity metadata must match actual archive");
            File.WriteAllText(currentPath, committedPointer); File.WriteAllText(manifestPath, committedManifest);
            File.WriteAllText(currentPath, committedPointer.Replace("\"Completed\"", "\"Failed\""));
            Reject(() => store.LoadActive(), "Failed current marker is never trusted");
            File.WriteAllText(currentPath, committedPointer);
            var pointer = new JavaScriptSerializer().Deserialize<SyncCatalogStore.Current>(committedPointer); pointer.Generation = "../checkpoints/fake";
            File.WriteAllText(currentPath, new JavaScriptSerializer().Serialize(pointer));
            Reject(() => store.LoadActive(), "Current pointer cannot escape to checkpoint paths");
            File.WriteAllText(currentPath, committedPointer);
            File.AppendAllText(Path.Combine(loaded.GenerationDirectory, "verified-public-history-v1.json"), " ");
            Reject(() => store.LoadActive(), "Published evidence byte tampering fails its manifest hash");
        }
        public static int Main(string[] args)
        {
            try
            {
                root = Path.Combine(args[0], "runs", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
                Run(args[1]);
                Console.WriteLine(new JavaScriptSerializer().Serialize(new { Result = "Passed", Assertions = checks, Scope = "Offline read-only local replay, real 4412-record archive and synthetic boundary/corruption/rollback/patch cases; no browser, UI or network", Utc = DateTime.UtcNow.ToString("o") }));
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
        private static void Run(string realDirectory)
        {
            string dir = Save("normal", Synthetic());
            string original = Hash(Path.Combine(dir, "verified-public-history-v1.json")), originalV2 = Hash(Path.Combine(dir, "verified-public-anchors-v2.json"));
            var result = Resolve(dir, At("2020-01-03T12:00:00Z"));
            Check(result.BuildId == "10" && result.SourceUrl.EndsWith("=300") && result.Depots.Single(d => d.DepotId == "1").ManifestId == "3000", "Most recent reactivation uses exact ChangeId and manifest");
            result = Resolve(dir, At("2020-01-01T12:00:00Z"));
            Check(result.BuildId == "10" && result.SourceUrl.EndsWith("=100") && result.Depots.Single(d => d.DepotId == "1").ManifestId == "1000", "Same Build earlier activation retains a distinct original state");
            Check(result.Depots.Single(d => d.DepotId == "2").ManifestId == "9000", "Unchanged depot retained");
            Check(result.Complete && result.Assurance.Contains("本次未读取网页"), "Only complete read-only replay returned");
            Check(Resolve(dir, At("2020-01-03T00:00:00Z")).BuildId == "20", "Equal public time excluded");
            Check(Resolve(dir, At("2020-01-04T00:00:00Z")).BuildId == "10", "Upper boundary equality chooses previous event");
            Check(Resolve(dir, At("2020-01-04T00:00:01Z")) == null, "After captured upper bound misses");
            Check(Resolve(dir, At("2019-12-31T00:00:00Z")) == null, "Earliest event equality misses without older event");
            Check(Resolve(dir, At("2019-12-30T12:00:00Z")) == null, "Earlier than archive misses");
            Check(Resolve(RunDirectory("missing"), At("2020-01-02T12:00:00Z")) == null, "Missing archive misses");
            Check(Resolve(dir, new HistoryQuery { Patch = "7.00" }) == null, "Patch without official proof misses");
            Reject(() => Resolve(dir, new HistoryQuery()), "No selection mode");
            Reject(() => Resolve(dir, new HistoryQuery { Patch = "7.00", RequestedUtc = T("2020-01-01T00:00:00Z") }), "Conflicting query modes");
            Reject(() => Resolve(dir, new HistoryQuery { Patch = "7" }), "Exact patch validation");
            bool cancelled = false;
            try { new LocalArchiveResolver(dir).ResolveAsync(At("2020-01-02T12:00:00Z"), new CancellationToken(true), null).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "Cancellation propagated");
            Check(Hash(Path.Combine(dir, "verified-public-history-v1.json")) == original && Hash(Path.Combine(dir, "verified-public-anchors-v2.json")) == originalV2, "Archive unchanged after reads and misses");
            Check(Directory.GetFiles(dir).Length == 2, "No extra metadata or lock written into archive");

            var unusual = Synthetic();
            unusual.Changes[1].Publication.PublishedAtUtc = T("2020-01-01T23:59:56Z");
            unusual.Changes[0].Publication.PreviousPublishedAtUtc = unusual.Changes[1].Publication.PublishedAtUtc;
            string unusualDir = Save("out-of-order-effective-time", unusual);
            Check(Resolve(unusualDir, At("2020-01-02T00:00:01Z")).SourceUrl.EndsWith("=200"), "Nearest effective timestamp scans full chain rather than assuming changelist time order");
            var tied = Synthetic(); tied.Changes[1].Publication.PublishedAtUtc = tied.Changes[2].Publication.PublishedAtUtc;
            tied.Changes[0].Publication.PreviousPublishedAtUtc = tied.Changes[1].Publication.PublishedAtUtc;
            Check(Resolve(Save("equal-times", tied), At("2020-01-02T00:00:01Z")).SourceUrl.EndsWith("=300"), "Equal effective timestamps preserve latest activation by change order");
            Check(Resolve(Save("only-v1", Synthetic(), false, true), At("2020-01-02T12:00:00Z")).BuildId == "20", "Validated v1 migration supported read-only");
            Check(Resolve(Save("only-v2", Synthetic(), true, false), At("2020-01-02T12:00:00Z")).BuildId == "20", "Source-bound hashed v2 supported");
            string broken = Save("corrupt-v2", Synthetic());
            File.WriteAllText(Path.Combine(broken, "verified-public-anchors-v2.json"), "broken");
            Reject(() => Resolve(broken, At("2020-01-02T12:00:00Z")), "Corrupt v2 cannot silently fall back to v1");
            broken = Save("corrupt-v1", Synthetic());
            File.WriteAllText(Path.Combine(broken, "verified-public-history-v1.json"), "broken");
            Reject(() => Resolve(broken, At("2020-01-02T12:00:00Z")), "Corrupt v1 cannot silently fall back to v2");
            broken = Save("foreign-source", Synthetic(), true, false);
            string file = Path.Combine(broken, "verified-public-anchors-v2.json");
            File.WriteAllText(file, File.ReadAllText(file).Replace("https://steamdb.info/app/570/depots/?branch=public", "https://example.com/app/570/depots/?branch=public"));
            Reject(() => Resolve(broken, At("2020-01-02T12:00:00Z")), "Envelope source scope checked");
            broken = Save("bad-provenance", Synthetic(), true, false); file = Path.Combine(broken, "verified-public-anchors-v2.json");
            File.WriteAllText(file, File.ReadAllText(file).Replace("BrowserBaselineAndContinuousHistory", "ManuallyEnteredIndex"));
            Reject(() => Resolve(broken, At("2020-01-02T12:00:00Z")), "Manual provenance rejected");
            var gap = Synthetic(); gap.Changes.RemoveAt(2);
            Reject(() => Resolve(Save("gap", gap), At("2020-01-03T12:00:00Z")), "Even corruption after candidate must fail full-chain validation");
            var wrong = Synthetic(); wrong.Changes.Last().Deltas[0].NewValue = "9999";
            Reject(() => Resolve(Save("late-manifest-corruption", wrong), At("2020-01-03T12:00:00Z")), "Entire chain replayed before an early match is returned");
            var mixed = Synthetic(); new HistoryCacheStore(dir).Save(mixed.Baseline, mixed.Changes.Take(2).ToList());
            Check(Resolve(dir, At("2020-01-01T12:00:00Z")).BuildId == "10", "Identical shorter prefix merges without losing longer verified chain");
            var conflicting = Synthetic(); conflicting.Changes[0].Deltas[0].OldValue = "2999"; conflicting.Changes[1].Deltas[0].NewValue = "2999";
            new HistoryCacheStore(dir).Save(conflicting.Baseline, conflicting.Changes);
            Reject(() => Resolve(dir, At("2020-01-03T12:00:00Z")), "Individually valid but conflicting same-baseline prefixes rejected");
            dir = Save("patch", Synthetic());
            var proof = Proof("10", "100", "OfficialPatchSource");
            result = Resolve(dir, new HistoryQuery { Patch = "7.00" }, proof);
            Check(result.BuildId == "10" && result.PatchSourceChangeId == "100" && result.Depots[0].ManifestId == "1000", "Official exact source event not replaced by later same-Build rollback");
            proof.Build.ChangeId = null;
            Check(Resolve(dir, new HistoryQuery { Patch = "7.00" }, proof) == null, "Official date without exact event cannot guess first release");
            proof = Proof("99", "100", "OfficialEventBuild");
            Reject(() => Resolve(dir, new HistoryQuery { Patch = "7.00" }, proof), "Official Build mismatch rejected");
            proof = Proof("10", "100", "SteamDbPatchTitle");
            Reject(() => Resolve(dir, new HistoryQuery { Patch = "7.00" }, proof), "Community title not official first-release proof");
            proof = Proof("10", "100", "OfficialArticle");
            Check(Resolve(dir, new HistoryQuery { Patch = "7.00" }, proof) == null, "Legitimate article candidate is a local miss until first-release linkage is proved");
            proof = Proof("10", "100", "OfficialPatchSource");
            proof.Sources.Add("https://steamstore-a.akamaihd.net/news/externalpost/steam_community_announcements/12345");
            Check(Resolve(dir, new HistoryQuery { Patch = "7.00" }, proof).BuildId == "10", "Exact official news CDN citation is allowed");
            proof.Sources[2] = "https://steamstore-a.akamaihd.net/arbitrary/12345";
            Reject(() => Resolve(dir, new HistoryQuery { Patch = "7.00" }, proof), "Unrelated CDN path is not official patch evidence");
            proof.Sources[2] = "https://steamstore-a.akamaihd.net/news/externalpost/steam_community_announcements/12345?token=untrusted";
            Reject(() => Resolve(dir, new HistoryQuery { Patch = "7.00" }, proof), "CDN citation with query is rejected");
            proof = Proof("10", "100", "OfficialPatchSource"); proof.Build.SourceUrl = "https://steamdb.info/patchnotes/20/";
            Reject(() => Resolve(dir, new HistoryQuery { Patch = "7.00" }, proof), "Exact Build URL binding retained");
            proof = Proof("20", null, "OfficialReleaseWindow");
            proof.NotAvailableAtUtc = T("2020-01-01T12:00:00Z"); proof.AvailableByUtc = T("2020-01-02T12:00:00Z");
            proof.WindowEvidence = new List<OfficialReleaseStatement> {
                new OfficialReleaseStatement { Patch = "7.00", State = "NotYetPublic", AnnouncedAtUtc = proof.NotAvailableAtUtc.Value, SourceUrl = "https://www.dota2.com/news/a" },
                new OfficialReleaseStatement { Patch = "7.00", State = "AvailablePublic", AnnouncedAtUtc = proof.AvailableByUtc.Value, SourceUrl = "https://www.dota2.com/news/b" } };
            result = Resolve(dir, new HistoryQuery { Patch = "7.00" }, proof);
            Check(result.BuildId == "20" && result.PatchWindowPublicEvents == 1 && result.SourceUrl.EndsWith("=200"), "Official window proves one exact public event with preceding lower boundary");
            proof.AvailableByUtc = T("2020-01-03T12:00:00Z"); proof.WindowEvidence[1].AnnouncedAtUtc = proof.AvailableByUtc.Value;
            Reject(() => Resolve(dir, new HistoryQuery { Patch = "7.00" }, proof), "Multiple events in official window cannot claim first release");

            string realV1 = Path.Combine(realDirectory, "verified-public-history-v1.json"), realV2 = Path.Combine(realDirectory, "verified-public-anchors-v2.json");
            string realHash1 = Hash(realV1), realHash2 = Hash(realV2);
            HistoryCacheStore.CacheData real = new HistoryCacheStore(realDirectory).Load();
            Check(real.Changes.Count == 4412, "Real completed archive contains 4412 continuous records");
            result = Resolve(realDirectory, new HistoryQuery { BeijingTime = T("2015-12-20T12:00:00+08:00") });
            Check(result.BuildId == "906288" && result.Complete && result.Depots.Count > 0, "Real Beijing 2015-12-20 12:00:00 resolves Build906288");
            Check(result.PublishedAtUtc < T("2015-12-20T04:00:00Z"), "Real event strictly precedes requested UTC instant");
            var equality = Resolve(realDirectory, new HistoryQuery { RequestedUtc = result.PublishedAtUtc });
            Check(equality != null && equality.PublishedAtUtc < result.PublishedAtUtc && equality.SourceUrl != result.SourceUrl, "Real equal timestamp excludes the same publication");
            Check(Resolve(realDirectory, new HistoryQuery { RequestedUtc = real.Baseline.PublishedAtUtc.AddTicks(1) }) == null, "Real archive upper coverage enforced");
            Check(Resolve(realDirectory, At("2013-02-02T00:36:52Z")) == null, "Real earliest observed equality fails closed");
            var oldest = Resolve(realDirectory, At("2013-02-03T00:00:00Z"));
            Check(oldest != null && oldest.Complete && oldest.BuildId == "41055" && oldest.Depots.Select(d => d.DepotId).SequenceEqual(new[] { "571", "572", "573", "575" }), "Earliest complete event uses historical Depot scope without global marker veto");
            Check(Hash(realV1) == realHash1 && Hash(realV2) == realHash2, "Real archive bytes unchanged after all tests");
            RunSyncCatalogTests();
        }
    }
}
