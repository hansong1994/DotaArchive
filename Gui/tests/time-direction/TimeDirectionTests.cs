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
    // Offline fixtures exercise public-event identity and full depot reconstruction,
    // not just a comparator. No website or download engine is invoked.
    internal static class TimeDirectionTests
    {
        private static int checks;
        private static string root;
        private static readonly DateTimeOffset Head = T("2020-01-04T00:00:00Z");
        private static DateTimeOffset T(string value) { return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture); }
        private static void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; }
        private static void Reject(Action action, string label)
        { try { action(); } catch (HistoryResolutionException) { checks++; return; } throw new Exception("Expected history rejection: " + label); }
        private static string Hash(string path)
        { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))); }
        private static HistoryQuery At(string time, HistoryTimeDirection direction)
        { return new HistoryQuery { RequestedUtc = T(time), TimeDirection = direction }; }
        private static ResolvedHistoryPlan Resolve(string directory, HistoryQuery query, OfficialPatchMatch proof = null)
        { return new LocalArchiveResolver(directory).ResolveAsync(query, proof, CancellationToken.None, null).GetAwaiter().GetResult(); }
        private static string Save(string name, HistoryAnchorStore.Anchor anchor)
        {
            string path = Path.Combine(root, name); Directory.CreateDirectory(path);
            new HistoryAnchorStore(path).Save(new[] { anchor }); new HistoryCacheStore(path).Save(anchor.Baseline, anchor.Changes);
            return path;
        }
        private static HistoryAnchorStore.Anchor Chain()
        {
            var state = new HistoryDepotState { CompleteBaseline = true };
            state.Values.Add("depots/1/manifests/public/gid", "4000");
            state.Values.Add("depots/2/manifests/public/gid", "9000");
            var baseline = new HistoryBaseline { BuildId = "40", PublishedAtUtc = Head, SourceUrl = "https://steamdb.info/app/570/depots/?branch=public", State = state };
            string[] ids = { "400", "300", "200", "100", "50" }, builds = { "40", "10", "20", "10", "5", "1" }, gids = { "4000", "3000", "2000", "1000", "500", "100" };
            var changes = new List<HistoryChange>();
            for (int i = 0; i < ids.Length; i++)
            {
                DateTimeOffset time = Head.AddDays(-i);
                var c = new HistoryChange { ChangeId = ids[i], ObservedAtUtc = time.AddSeconds(5), ParsedAllDepotChanges = true,
                    SourceUrl = "https://steamdb.info/app/570/history/?changeid=" + ids[i],
                    Publication = new HistoryBuildEvent { BuildId = builds[i], PreviousBuildId = builds[i + 1], PublishedAtUtc = time,
                        PreviousPublishedAtUtc = time.AddDays(-1), ChangeId = ids[i], IsPublic = true } };
                c.Deltas.Add(new HistoryDelta { Path = "depots/1/manifests/public/gid", NewExists = true, OldExists = true, NewValue = gids[i], OldValue = gids[i + 1] });
                changes.Add(c);
            }
            return new HistoryAnchorStore.Anchor { Identity = HistoryAnchorStore.Identity(baseline), Provenance = "BrowserBaselineAndContinuousHistory", Baseline = baseline, Changes = changes };
        }
        private static void Plan(ResolvedHistoryPlan plan, string build, string change, string manifest, string label)
        {
            Check(plan != null && plan.Complete && plan.BuildId == build && plan.SourceUrl.EndsWith("=" + change), label + " exact public event");
            Check(plan.Depots.Count == 2 && plan.Depots.Single(d => d.DepotId == "1").ManifestId == manifest && plan.Depots.Single(d => d.DepotId == "2").ManifestId == "9000", label + " full changed and retained depot state");
        }
        private static OfficialPatchMatch Proof()
        {
            return new OfficialPatchMatch { Kind = "OfficialPatchSource",
                Build = new HistoryBuildEvent { BuildId = "10", ChangeId = "100", PublishedAtUtc = T("2020-01-01T00:00:00Z"), SourceUrl = "https://steamdb.info/patchnotes/10/", IsPublic = true },
                Sources = new List<string> { "https://steamdb.info/patchnotes/10/", "https://www.dota2.com/patches/7.00" }, Assurance = "Synthetic official release proof" };
        }
        private static void LocalCases()
        {
            string dir = Save("local", Chain());
            string v1 = Path.Combine(dir, "verified-public-history-v1.json"), v2 = Path.Combine(dir, "verified-public-anchors-v2.json"), h1 = Hash(v1), h2 = Hash(v2);
            Check(new HistoryQuery().TimeDirection == HistoryTimeDirection.Before, "new query retains strict-before default");
            var oldQuery = new JavaScriptSerializer().Deserialize<HistoryQuery>("{\"Language\":\"schinese\"}");
            Check(oldQuery.TimeDirection == HistoryTimeDirection.Before, "serialized old query without direction remains before");
            Plan(Resolve(dir, At("2020-01-02T12:00:00Z", HistoryTimeDirection.Before)), "20", "200", "2000", "same instant before");
            Plan(Resolve(dir, At("2020-01-02T12:00:00Z", HistoryTimeDirection.After)), "10", "300", "3000", "same instant after rollback");
            Plan(Resolve(dir, new HistoryQuery { RequestedUtc = T("2020-01-02T12:00:00Z") }), "20", "200", "2000", "default before");
            Plan(Resolve(dir, new HistoryQuery { BeijingTime = T("2020-01-02T20:00:00+08:00"), TimeDirection = HistoryTimeDirection.After }), "10", "300", "3000", "equivalent Beijing input");
            Plan(Resolve(dir, At("2020-01-03T00:00:00Z", HistoryTimeDirection.Before)), "20", "200", "2000", "before excludes equal event");
            Plan(Resolve(dir, At("2020-01-03T00:00:00Z", HistoryTimeDirection.After)), "40", "400", "4000", "after excludes equal event");
            Plan(Resolve(dir, At("2020-01-02T23:59:59.9999999Z", HistoryTimeDirection.After)), "10", "300", "3000", "after retains one-tick precision");
            Plan(Resolve(dir, At("2020-01-03T00:00:00.0000001Z", HistoryTimeDirection.Before)), "10", "300", "3000", "before retains one-tick precision");
            Plan(Resolve(dir, At("2019-12-31T12:00:00Z", HistoryTimeDirection.After)), "10", "100", "1000", "original activation of repeated build");
            Check(Resolve(dir, At("2020-01-04T00:00:00Z", HistoryTimeDirection.After)) == null, "newest equality has no after result");
            Check(Resolve(dir, At("2020-01-04T00:00:00.0000001Z", HistoryTimeDirection.After)) == null, "after above covered upper bound misses");
            Check(Resolve(dir, At("2019-12-31T00:00:00Z", HistoryTimeDirection.Before)) == null, "oldest equality has no before result");
            Check(Resolve(dir, At("2019-12-30T23:59:59.9999999Z", HistoryTimeDirection.After)) == null, "candidate alone cannot establish lower coverage");
            Plan(Resolve(dir, At("2019-12-31T00:00:00Z", HistoryTimeDirection.After)), "10", "100", "1000", "equal lower event supplies coverage but is not selected");
            var partial = Chain(); partial.Changes.RemoveRange(2, 3);
            string partialDir = Save("partial-lower-bound", partial);
            Check(Resolve(partialDir, At("2020-01-02T12:00:00Z", HistoryTimeDirection.After)) == null, "prefix with successor but no preceding event cannot claim nearest");
            Plan(Resolve(partialDir, At("2020-01-03T00:00:00Z", HistoryTimeDirection.After)), "40", "400", "4000", "same short prefix works inside proved range");
            var unordered = Chain(); unordered.Changes[1].Publication.PublishedAtUtc = T("2020-01-01T23:59:56Z");
            unordered.Changes[0].Publication.PreviousPublishedAtUtc = unordered.Changes[1].Publication.PublishedAtUtc;
            Plan(Resolve(Save("effective-time-order", unordered), At("2020-01-01T23:59:55Z", HistoryTimeDirection.After)), "10", "300", "3000", "effective-time ordering differs from changelist ordering");
            var tied = Chain(); tied.Changes[1].Publication.PublishedAtUtc = tied.Changes[2].Publication.PublishedAtUtc;
            tied.Changes[0].Publication.PreviousPublishedAtUtc = tied.Changes[1].Publication.PublishedAtUtc;
            string tiedDir = Save("tied-times", tied);
            Plan(Resolve(tiedDir, At("2020-01-01T12:00:00Z", HistoryTimeDirection.After)), "10", "300", "3000", "same-time after uses newest actual activation");
            Plan(Resolve(tiedDir, At("2020-01-02T00:00:00.0000001Z", HistoryTimeDirection.Before)), "10", "300", "3000", "same-time before has same tie policy");
            Plan(Resolve(tiedDir, At("2020-01-02T00:00:00Z", HistoryTimeDirection.After)), "40", "400", "4000", "after equality excludes all tied events");
            foreach (HistoryTimeDirection direction in new[] { HistoryTimeDirection.Before, HistoryTimeDirection.After })
            {
                Plan(Resolve(dir, new HistoryQuery { Patch = "7.00", TimeDirection = direction }, Proof()), "10", "100", "1000", "patch first release ignores " + direction);
                bool cancelled = false;
                try { new LocalArchiveResolver(dir).ResolveAsync(At("2020-01-02T12:00:00Z", direction), new CancellationToken(true), null).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled, "cancelled " + direction + " lookup does not return a plan");
            }
            var corrupt = Chain(); corrupt.Changes.Last().Deltas[0].NewValue = "9999";
            Reject(() => Resolve(Save("corrupt-late-record", corrupt), At("2020-01-02T12:00:00Z", HistoryTimeDirection.After)), "corruption older than selected candidate still invalidates full proof");
            Check(Hash(v1) == h1 && Hash(v2) == h2 && Directory.GetFiles(dir).Length == 2, "both directions keep archive files unchanged");
        }

        private static string Delta(string label, string oldValue, string newValue)
        { return "<li><span><i>" + label + ":</i><del>" + oldValue + "</del><ins>" + newValue + "</ins></span></li>"; }
        private static string Event(HistoryChange change)
        {
            var pub = change.Publication;
            return "<div class='panel-history' data-changeid='" + change.ChangeId + "'><relative-time datetime='" + change.ObservedAtUtc.ToString("o") + "'></relative-time><ul class='app-history'><li class='repo'><span>Changed <i>Depots</i></span><ul><li><span>Changed <span class='branch-name branch-public'>public</span> branch<ul class='app-history'>" +
                Delta("buildid", pub.PreviousBuildId, pub.BuildId) + Delta("timeupdated", pub.PreviousPublishedAtUtc.Value.ToString("o"), pub.PublishedAtUtc.ToString("o")) +
                Delta("Depot 1 gid", change.Deltas[0].OldValue, change.Deltas[0].NewValue) + "</ul></span></li></ul></li></ul></div>";
        }
        private static string BaselineHtml()
        {
            long epoch = (long)(Head - T("1970-01-01T00:00:00Z")).TotalSeconds;
            return "<div id='depots'>Selected Branch: public<table><tr><td>buildid</td><td>40</td></tr><tr><td>timeupdated</td><td>" + epoch + "</td></tr>" +
                "<tr class='depot' data-depotid='1'><td class='depot-config'></td><td><a href='/depot/1/history/?changeid=M:4000'>4000</a></td></tr>" +
                "<tr class='depot' data-depotid='2'><td class='depot-config'></td><td><a href='/depot/2/history/?changeid=M:9000'>9000</a></td></tr></table></div>";
        }
        private sealed class Browser : IHistoryBrowser, IHistoryDateBrowser
        {
            internal string DateHtml;
            internal int DateLoads, Baselines;
            internal string HistoryHtml = String.Join("", Chain().Changes.Select(Event)) + "<p>No more history</p>";
            public Task<HistoryPageSnapshot> NavigateAsync(string url, CancellationToken token)
            { token.ThrowIfCancellationRequested(); if (url.Contains("/depots/")) { Baselines++; return Task.FromResult(Page(url, BaselineHtml())); } return Task.FromResult(Page(url, HistoryHtml)); }
            public Task<HistoryPageSnapshot> LoadHistoryBeforeAsync(DateTime date, CancellationToken token)
            { token.ThrowIfCancellationRequested(); DateLoads++; return Task.FromResult(Page("https://steamdb.info/app/570/history/?filterkey=2", DateHtml ?? HistoryHtml)); }
            public Task<HistoryPageSnapshot> CaptureAsync(CancellationToken token) { throw new Exception("Unexpected capture"); }
            public Task<HistoryPageSnapshot> ClickAsync(string selector, CancellationToken token) { throw new Exception("Unexpected older-page request"); }
            private static HistoryPageSnapshot Page(string url, string html) { return new HistoryPageSnapshot { Url = url, Html = html, CapturedAtUtc = T("2020-01-04T01:00:00Z") }; }
        }
        private sealed class NoDateBrowser : IHistoryBrowser
        {
            internal readonly Browser Inner = new Browser();
            public Task<HistoryPageSnapshot> NavigateAsync(string url, CancellationToken token) { return Inner.NavigateAsync(url, token); }
            public Task<HistoryPageSnapshot> CaptureAsync(CancellationToken token) { return Inner.CaptureAsync(token); }
            public Task<HistoryPageSnapshot> ClickAsync(string selector, CancellationToken token) { return Inner.ClickAsync(selector, token); }
        }
        private static void OnlineCases()
        {
            foreach (bool dateFirst in new[] { false, true })
            {
                var before = new Browser(); var after = new Browser();
                Plan(new OnlineHistoryResolver(before, null, null, dateFirst).ResolveAsync(At("2020-01-02T12:00:00Z", HistoryTimeDirection.Before), CancellationToken.None, null).GetAwaiter().GetResult(), "20", "200", "2000", "online before dateFirst=" + dateFirst);
                Plan(new OnlineHistoryResolver(after, null, null, dateFirst).ResolveAsync(At("2020-01-02T12:00:00Z", HistoryTimeDirection.After), CancellationToken.None, null).GetAwaiter().GetResult(), "10", "300", "3000", "online after dateFirst=" + dateFirst);
                Check(after.Baselines == 2 && after.DateLoads == (dateFirst ? 1 : 0), "online after still obtains and confirms full baseline");
                Plan(new OnlineHistoryResolver(new Browser(), null, null, dateFirst).ResolveAsync(At("2020-01-03T00:00:00Z", HistoryTimeDirection.After), CancellationToken.None, null).GetAwaiter().GetResult(), "40", "400", "4000", "online strict-after equality dateFirst=" + dateFirst);
                Reject(() => new OnlineHistoryResolver(new Browser(), null, null, dateFirst).ResolveAsync(At("2020-01-04T00:00:00Z", HistoryTimeDirection.After), CancellationToken.None, null).GetAwaiter().GetResult(), "online latest equality must not substitute older build");
            }
            var missingBoundary = new Browser { HistoryHtml = Event(Chain().Changes[0]) + Event(Chain().Changes[1]) + "<p>No more history</p>" };
            Reject(() => new OnlineHistoryResolver(missingBoundary, null, null, false).ResolveAsync(At("2020-01-02T12:00:00Z", HistoryTimeDirection.After), CancellationToken.None, null).GetAwaiter().GetResult(), "online after incomplete lower boundary fails closed");
            var corrupt = new Browser(); corrupt.HistoryHtml = corrupt.HistoryHtml.Replace("<ins>2000</ins>", "<ins>2001</ins>");
            Reject(() => new OnlineHistoryResolver(corrupt, null, null, false).ResolveAsync(At("2020-01-02T12:00:00Z", HistoryTimeDirection.After), CancellationToken.None, null).GetAwaiter().GetResult(), "online after validates lower boundary manifest continuity");
            var stale = new Browser(); stale.DateHtml = stale.HistoryHtml.Replace("<del>2000</del>", "<del>2001</del>");
            Reject(() => new OnlineHistoryResolver(stale, null, null, true).ResolveAsync(At("2020-01-02T12:00:00Z", HistoryTimeDirection.After), CancellationToken.None, null).GetAwaiter().GetResult(), "located after candidate fields must equal full replay evidence");
            var noDate = new NoDateBrowser();
            Plan(new OnlineHistoryResolver(noDate, null, null, true).ResolveAsync(At("2020-01-02T12:00:00Z", HistoryTimeDirection.After), CancellationToken.None, null).GetAwaiter().GetResult(), "10", "300", "3000", "browser without date-control interface");
            Check(noDate.Inner.DateLoads == 0, "missing date-control interface never invokes date navigation");
            var corruptBuild = new Browser(); corruptBuild.HistoryHtml = corruptBuild.HistoryHtml.Replace("<ins>20</ins>", "<ins>21</ins>");
            Reject(() => new OnlineHistoryResolver(corruptBuild, null, null, false).ResolveAsync(At("2020-01-02T12:00:00Z", HistoryTimeDirection.After), CancellationToken.None, null).GetAwaiter().GetResult(), "online after validates lower boundary Build continuity");
            var tied = Chain(); tied.Changes[1].Publication.PublishedAtUtc = tied.Changes[2].Publication.PublishedAtUtc;
            tied.Changes[0].Publication.PreviousPublishedAtUtc = tied.Changes[1].Publication.PublishedAtUtc;
            var tiedBrowser = new Browser { HistoryHtml = String.Join("", tied.Changes.Select(Event)) + "<p>No more history</p>" };
            Plan(new OnlineHistoryResolver(tiedBrowser, null, null, true).ResolveAsync(At("2020-01-01T12:00:00Z", HistoryTimeDirection.After), CancellationToken.None, null).GetAwaiter().GetResult(), "10", "300", "3000", "online date and replay agree on tied activation");
        }
        private static void RealCases(string archive)
        {
            string v1 = Path.Combine(archive, "verified-public-history-v1.json"), v2 = Path.Combine(archive, "verified-public-anchors-v2.json"), h1 = Hash(v1), h2 = Hash(v2);
            var before = Resolve(archive, new HistoryQuery { BeijingTime = T("2015-12-21T12:00:00+08:00"), TimeDirection = HistoryTimeDirection.Before });
            var after = Resolve(archive, new HistoryQuery { BeijingTime = T("2015-12-21T12:00:00+08:00"), TimeDirection = HistoryTimeDirection.After });
            Check(before != null && before.Complete && before.BuildId == "907474" && before.Depots.Count > 0, "real archive before is complete Build907474");
            Check(after != null && after.Complete && after.BuildId == "909005" && after.Depots.Count > 0, "real archive after is complete Build909005");
            Check(before.PublishedAtUtc < T("2015-12-21T04:00:00Z") && after.PublishedAtUtc > T("2015-12-21T04:00:00Z"), "real results straddle exact Beijing input");
            Check(before.UsedObservationFallback && after.UsedObservationFallback && before.TimeSource.Contains("SteamDB") && after.TimeSource.Contains("SteamDB"), "old observed time remains explicitly labeled in both directions");
            Check(Resolve(archive, new HistoryQuery { RequestedUtc = after.PublishedAtUtc, TimeDirection = HistoryTimeDirection.Before }).SourceUrl == before.SourceUrl, "real before equality excludes after event");
            Check(Resolve(archive, new HistoryQuery { RequestedUtc = before.PublishedAtUtc, TimeDirection = HistoryTimeDirection.After }).SourceUrl == after.SourceUrl, "real after equality excludes before event");
            Check(Hash(v1) == h1 && Hash(v2) == h2, "actual archive remains read-only");
        }
        private static void EvidenceComparisonCases()
        {
            Check(SyncCatalogStore.HaveIdenticalChanges(Chain().Changes, Chain().Changes), "independently loaded equal evidence compares by complete semantic fields");
            Action<string, Action<List<HistoryChange>>> changed = (name, mutate) => {
                var right = Chain().Changes; mutate(right);
                Check(!SyncCatalogStore.HaveIdenticalChanges(Chain().Changes, right), "evidence comparator rejects " + name);
            };
            changed("changed ChangeId", c => c[0].ChangeId = "401");
            changed("changed observed time", c => c[0].ObservedAtUtc = c[0].ObservedAtUtc.AddTicks(1));
            changed("changed source", c => c[0].SourceUrl += "0");
            changed("partial parsed marker", c => c[0].ParsedAllDepotChanges = false);
            changed("unknown field list", c => c[0].UnparsedChanges.Add("unknown"));
            changed("missing publication", c => c[0].Publication = null);
            changed("changed public Build", c => c[0].Publication.BuildId = "41");
            changed("changed previous Build", c => c[0].Publication.PreviousBuildId = "11");
            changed("changed public timestamp", c => c[0].Publication.PublishedAtUtc = Head.AddTicks(1));
            changed("changed previous timestamp", c => c[0].Publication.PreviousPublishedAtUtc = null);
            changed("changed public flag", c => c[0].Publication.IsPublic = false);
            changed("changed publication source", c => c[0].Publication.SourceUrl = "https://steamdb.info/patchnotes/40/");
            changed("changed publication ChangeId", c => c[0].Publication.ChangeId = "401");
            changed("changed publication title", c => c[0].Publication.Title = "different");
            changed("changed previous link", c => c[0].Publication.PreviousUrl = "https://steamdb.info/patchnotes/10/");
            changed("changed next link", c => c[0].Publication.NextUrl = "https://steamdb.info/patchnotes/50/");
            changed("changed depot path", c => c[0].Deltas[0].Path = "depots/2/manifests/public/gid");
            changed("changed old manifest", c => c[0].Deltas[0].OldValue = "3001");
            changed("changed new manifest", c => c[0].Deltas[0].NewValue = "4001");
            changed("changed old-exists flag", c => c[0].Deltas[0].OldExists = false);
            changed("changed new-exists flag", c => c[0].Deltas[0].NewExists = false);
            changed("removed delta", c => c[0].Deltas.Clear());
            changed("removed record", c => c.RemoveAt(4));
            changed("reordered records", c => c.Reverse());
            var leftOrder = Chain().Changes; var rightOrder = Chain().Changes;
            leftOrder[0].Deltas.Add(new HistoryDelta { Path = "depots/2/manifests/public/gid", OldExists = true, NewExists = true, OldValue = "9000", NewValue = "9000" });
            rightOrder[0].Deltas.Add(new HistoryDelta { Path = "depots/2/manifests/public/gid", OldExists = true, NewExists = true, OldValue = "9000", NewValue = "9000" });
            rightOrder[0].Deltas.Reverse();
            Check(!SyncCatalogStore.HaveIdenticalChanges(leftOrder, rightOrder), "evidence comparator preserves delta sequence");
        }
        public static int Main(string[] args)
        {
            try
            {
                root = Path.Combine(args[0], "runs", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
                LocalCases(); OnlineCases(); RealCases(args[1]); EvidenceComparisonCases();
                string report = new JavaScriptSerializer().Serialize(new { Result = "Passed", Assertions = checks, Scope = "Offline local and online-fake time direction; exact equality, rollback, coverage, corruption, patch and real archive; no network or game downloads", Utc = DateTime.UtcNow.ToString("o") });
                File.WriteAllText(Path.Combine(args[0], "results.json"), report); Console.WriteLine(report); return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
    }
}
