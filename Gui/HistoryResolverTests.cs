using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DotaArchive
{
    // Synthetic fixtures model DOM shapes observed in the normally loaded SteamDB pages.
    // These verify the algorithm; live page and Steam download checks remain separate.
    internal static class HistoryResolverTests
    {
        private static int checks;
        private static string Baseline()
        {
            return "<main><div id='depots'><h2>Selected Branch: <b>public</b></h2><table><tr><td>buildid</td><td>30</td></tr><tr><td>timeupdated</td><td>1546473600</td></tr></table><table>" +
                Depot("1", "300", "", "Core") + Depot("2", "500", "", "Retained") + Depot("3", "1000", "<span class='depot-language'>English</span>", "Rollback") +
                Depot("4", "700", "", "New Depot") + Depot("6", "600", "<span class='depot-os'>Linux</span>", "Linux Depot") +
                Depot("7", "7000", "<a href='/app/100/' data-appid='100'>DLC 100</a>", "Tool Depot") +
                Depot("8", "8000", "<span class='depot-language'>Japanese</span>", "Japanese Depot") + "</table></div></main>";
        }
        private static string Depot(string id, string manifest, string config, string name)
        { return "<tr class='depot' data-depotid='" + id + "'><td>" + id + "</td><td class='depot-config'>" + config + "<span class='i muted'>" + name + "</span></td><td><a href='/depot/" + id + "/history/?changeid=M:" + manifest + "'>" + manifest + "</a></td></tr>"; }
        private static string Delta(string label, string oldValue, string newValue)
        { return "<li class='diff-modified'><span><i>" + label + ":</i>" + (oldValue == null ? "" : "<del>" + oldValue + "</del>") + (newValue == null ? "" : "<ins>" + newValue + "</ins>") + "</span></li>"; }
        private static string Change(string change, string build, string oldBuild, string date, string oldDate, string manifestDeltas, string metadata)
        { return "<div class='panel panel-history' data-changeid='" + change + "'><div class='panel-heading'><relative-time datetime='" + date + "'/></div><ul class='app-history'><li class='repo'><span>Changed <i>Depots</i></span><ul>" + metadata + "<li><span>Changed <span class='branch-name branch-public'>public</span> branch<ul class='app-history'>" + Delta("timeupdated", oldDate, date) + Delta("buildid", oldBuild, build) + manifestDeltas + "</ul></span></li></ul></li></ul></div>"; }
        private static string Newest()
        { return Change("300", "30", "20", "2019-01-03T00:00:00Z", "2019-01-02T00:00:00Z", Delta("Depot 1 gid", "200", "300") + Delta("Depot 3 gid", "2000", "1000") + Delta("Depot 4 gid", null, "700") + Delta("Depot 5 gid", "800", null), Delta("Depot 4 name", null, "New Depot") + Delta("Depot 5 name", "Removed Depot", null) + Delta("Depot 5 config/oslist", "windows", null)); }
        private static string Previous()
        { return Change("200", "20", "10", "2019-01-02T00:00:00Z", "2019-01-01T00:00:00Z", Delta("Depot 1 gid", "100", "200"), ""); }
        private static HistoryPageSnapshot Page(string url, string html) { return new HistoryPageSnapshot { Url = url, Html = html, Title = "Dota 2" }; }
        private static void Check(bool truth, string name) { if (!truth) throw new Exception("FAILED: " + name); checks++; }
        private static void Reject(Action action, string name) { try { action(); } catch (HistoryResolutionException) { checks++; return; } throw new Exception("Expected failure: " + name); }
        private static OnlineHistoryResolver Resolver(IHistoryBrowser browser, Func<string, IList<HistoryBuildEvent>, CancellationToken, Action<string>, Task<OfficialPatchMatch>> official = null)
        { return new OnlineHistoryResolver(browser, official, null, false); }

        public static int Main(string[] args)
        {
            try { return Run(); }
            catch (Exception ex) { Console.Error.WriteLine(ex.GetType().FullName + ": " + ex.Message); Console.Error.WriteLine(ex.StackTrace); if (ex.InnerException != null) Console.Error.WriteLine(ex.InnerException.GetType().FullName + ": " + ex.InnerException.Message); return 1; }
        }

        private static int Run()
        {
            var parser = new SteamDbHistoryParser();
            HistoryBaseline baseline = parser.ParseBaseline(Page("https://steamdb.info/app/570/depots/?branch=public", Baseline()));
            Check(baseline.BuildId == "30", "baseline build");
            Check(baseline.PublishedAtUtc == DateTimeOffset.Parse("2019-01-03T00:00:00Z"), "epoch exact");
            var changes = parser.ParseHistory(Page("https://steamdb.info/app/570/history/?filterkey=2", Newest() + "<button id='js-history-load'>Load older history</button>"));
            Check(changes.Changes.Count == 1 && changes.Changes[0].ParsedAllDepotChanges, "actual flat DOM shape");
            Check(changes.LoadMoreSelector == "#js-history-load", "observed load button");
            var hiddenNavigation = parser.ParseHistory(Page("https://steamdb.info/app/570/history/", Newest() + "<div hidden><button id='js-history-load'>Load older history</button></div>"));
            Check(hiddenNavigation.LoadMoreSelector == null && !hiddenNavigation.EndReached, "hidden ancestor is not clickable or end proof");
            var tied = parser.ParseHistory(Page("https://steamdb.info/app/570/history/", Newest() + Newest().Replace("data-changeid='300'", "data-changeid='301'")));
            Check(tied.Changes[0].ChangeId == "301", "same-second changelist order");
            Check(changes.Builds[0].PublishedAtUtc == baseline.PublishedAtUtc, "branch time precise");
            baseline.State.Reverse(changes.Changes[0]);
            var depots = baseline.State.SelectWindowsClient("schinese");
            Check(depots.Single(d => d.DepotId == "1").ManifestId == "200", "changed manifest reversed");
            Check(depots.Single(d => d.DepotId == "2").ManifestId == "500", "retained manifest");
            Check(depots.Single(d => d.DepotId == "3").ManifestId == "2000", "rollback activation reversed");
            Check(!depots.Any(d => d.DepotId == "4"), "new depot absent historically");
            Check(depots.Single(d => d.DepotId == "5").ManifestId == "800", "removed depot restored");
            Check(depots.Count == 4, "scope platforms optional DLC language");
            Reject(() => baseline.State.Reverse(changes.Changes[0]), "discontinuity");
            var invalid = parser.ParseHistory(Page("https://steamdb.info/app/570/history/", Newest().Replace("Depot 5 config/oslist", "Depot 5 unknown/config")));
            Check(!invalid.Changes[0].ParsedAllDepotChanges, "unknown relevant field rejected");
            PrivateBranchMetadataChecks(parser);
            SameManifestMigrationChecks(parser);
            NameProjectionChecks(parser);
            PrerequisiteProjectionChecks(parser);
            SharedTransitionChecks(parser);
            OptionalDlcAndInlineDiffChecks(parser);
            Reject(() => new HistoryDepotState().SelectWindowsClient("schinese"), "partial baseline rejected");
            Reject(() => parser.ParseBaseline(Page("https://example.com/", Baseline())), "foreign origin");
            Reject(() => parser.ParseBaseline(Page("https://steamdb.info/app/730/depots/", Baseline())), "wrong app identity");
            Reject(() => parser.ParseBaseline(Page("https://steamdb.info/app/570/depots/", Baseline().Replace("</table></div>", Depot("1", "999", "", "duplicate") + "</table></div>"))), "duplicate baseline depot");
            var emptyPanel = parser.ParseHistory(Page("https://steamdb.info/app/570/history/", "<div class='panel-history' data-changeid='2'><relative-time datetime='2019-01-01T00:00:00Z'></relative-time></div>"));
            Check(!emptyPanel.Changes[0].ParsedAllDepotChanges, "unloaded panel is not empty change");
            var list = parser.ParsePatchListing(Page("https://steamdb.info/app/570/patchnotes/", "<table id='js-patchnotes'><tr data-date='1546387205'><td><a href='/patchnotes/20/' class='b'>7.22b (ClientVersion 3523)</a></td></tr></table>"));
            Check(list.Builds.Single().BuildId == "20", "patch observed data-date");
            Check(list.Builds.Single().PublishedAtUtc.Second == 5, "tracker observed differs publication");
            var fake = new FakeBrowser(Baseline(), Newest(), Previous());
            ResolvedHistoryPlan plan = Resolver(fake).ResolveAsync(new HistoryQuery { BeijingTime = DateTimeOffset.Parse("2019-01-02T20:00:00+08:00") }, CancellationToken.None, null).GetAwaiter().GetResult();
            Check(plan.Complete && plan.BuildId == "20", "async resolver exact previous");
            Check(plan.Depots.Count == 4, "async complete restoration");
            Check(fake.Clicks == 1 && fake.Baselines == 2, "pagination and final stable baseline");
            var dateBrowser = new FakeBrowser(Baseline(), Newest(), Previous()) { UseDateFilter = true };
            var datePlan = Resolver(dateBrowser).ResolveAsync(new HistoryQuery { RequestedUtc = DateTimeOffset.Parse("2019-01-02T12:00:00Z") }, CancellationToken.None, null).GetAwaiter().GetResult();
            Check(datePlan.BuildId == "20" && dateBrowser.DateLoads == 1 && dateBrowser.Clicks == 0, "normal date control with proved overlap");
            var repeatClick = new FakeBrowser(Baseline(), Newest(), Previous()) { ClickReturnsSame = true };
            var repeatPlan = Resolver(repeatClick).ResolveAsync(new HistoryQuery { RequestedUtc = DateTimeOffset.Parse("2019-01-02T12:00:00Z") }, CancellationToken.None, null).GetAwaiter().GetResult();
            Check(repeatPlan.BuildId == "20" && repeatClick.Clicks == 1 && repeatClick.DateLoads == 1, "unchanged normal pagination falls back to overlapping date control");
            var noOlderRecords = new FakeBrowser(Baseline(), Newest(), Previous()) { ClickReturnsSame = true, DateReturnsSame = true };
            Reject(() => Resolver(noOlderRecords).ResolveAsync(new HistoryQuery { RequestedUtc = DateTimeOffset.Parse("2019-01-02T12:00:00Z") }, CancellationToken.None, null).GetAwaiter().GetResult(), "unchanged date filter cannot loop or claim complete");
            var missingOverlap = new FakeBrowser(Baseline(), Newest(), Previous()) { UseDateFilter = true, MissingOverlap = true };
            Reject(() => Resolver(missingOverlap).ResolveAsync(new HistoryQuery { RequestedUtc = DateTimeOffset.Parse("2019-01-02T12:00:00Z") }, CancellationToken.None, null).GetAwaiter().GetResult(), "date jump without overlap refused");
            var alteredOverlap = new FakeBrowser(Baseline(), Newest(), Previous()) { UseDateFilter = true, AlterOverlap = true };
            Reject(() => Resolver(alteredOverlap).ResolveAsync(new HistoryQuery { RequestedUtc = DateTimeOffset.Parse("2019-01-02T12:00:00Z") }, CancellationToken.None, null).GetAwaiter().GetResult(), "date overlap mutation refused");
            var boundary = Resolver(new FakeBrowser(Baseline(), Newest(), Previous())).ResolveAsync(new HistoryQuery { RequestedUtc = DateTimeOffset.Parse("2019-01-03T00:00:00Z") }, CancellationToken.None, null).GetAwaiter().GetResult();
            Check(boundary.BuildId == "20", "strict before equality excludes equal event");
            DateTimeOffset exactTime = DateTimeOffset.Parse("2019-01-03T00:00:00Z");
            var justBefore = Resolver(new FakeBrowser(Baseline(), Newest(), Previous())).ResolveAsync(new HistoryQuery { RequestedUtc = exactTime.AddTicks(-1) }, CancellationToken.None, null).GetAwaiter().GetResult();
            var justAfter = Resolver(new FakeBrowser(Baseline(), Newest(), Previous())).ResolveAsync(new HistoryQuery { RequestedUtc = exactTime.AddTicks(1) }, CancellationToken.None, null).GetAwaiter().GetResult();
            Check(justBefore.BuildId == "20" && justAfter.BuildId == "30", "strict before preserves tick boundary without rounding");
            var afterHead = Resolver(new FakeBrowser(Baseline(), Newest(), Previous())).ResolveAsync(new HistoryQuery { RequestedUtc = DateTimeOffset.Parse("2019-01-04T00:00:00Z") }, CancellationToken.None, null).GetAwaiter().GetResult();
            Check(afterHead.BuildId == "30", "input after current public release selects latest earlier release");
            var patched = Resolver(new FakeBrowser(Baseline(), Newest(), Previous()), (patch, builds, token, progress) => Task.FromResult(new OfficialPatchMatch { Build = builds.Single(), Sources = new List<string> { "https://www.dota2.com/patches/7.22b" } })).ResolveAsync(new HistoryQuery { Patch = "7.22b" }, CancellationToken.None, null).GetAwaiter().GetResult();
            Check(patched.BuildId == "20" && patched.PublishedAtUtc.Second == 0, "patch exact branch date not observed");
            var sourceIdentityBrowser = new FakeBrowser(Baseline().Replace(">30<", ">20<"), Newest().Replace("<ins>30</ins>", "<ins>20</ins>"), Previous()) { PatchObservedEpoch = "1546646400" };
            var sourceIdentityPlan = Resolver(sourceIdentityBrowser, (patch, builds, token, progress) => { builds.Single().ChangeId = "200"; return Task.FromResult(new OfficialPatchMatch { Kind = "OfficialPatchSource", Build = builds.Single() }); }).ResolveAsync(new HistoryQuery { Patch = "7.22b" }, CancellationToken.None, null).GetAwaiter().GetResult();
            Check(sourceIdentityPlan.PatchSourceChangeId == "200" && sourceIdentityPlan.PublishedAtUtc == DateTimeOffset.Parse("2019-01-02T00:00:00Z"), "source changelist identity skips later same build activation");
            Reject(() => Resolver(new FakeBrowser(Baseline(), Newest(), Previous()), (patch, builds, token, progress) => { builds.Single().ChangeId = "300"; return Task.FromResult(new OfficialPatchMatch { Kind = "OfficialPatchSource", Build = builds.Single() }); }).ResolveAsync(new HistoryQuery { Patch = "7.22b" }, CancellationToken.None, null).GetAwaiter().GetResult(), "source changelist with different public build refused");
            Reject(() => Resolver(new FakeBrowser(Baseline(), Newest(), Previous()), (patch, builds, token, progress) => Task.FromResult<OfficialPatchMatch>(null)).ResolveAsync(new HistoryQuery { Patch = "7.22b" }, CancellationToken.None, null).GetAwaiter().GetResult(), "title alone cannot establish patch release");
            Reject(() => Resolver(new FakeBrowser(Baseline(), Newest(), Previous())).ResolveAsync(new HistoryQuery { RequestedUtc = DateTimeOffset.Parse("2019-01-01T00:00:00Z") }, CancellationToken.None, null).GetAwaiter().GetResult(), "no earlier version in available history");
            Reject(() => Resolver(new FakeBrowser(Baseline(), Newest(), Previous()), (patch, builds, token, progress) => Task.FromResult<OfficialPatchMatch>(null)).ResolveAsync(new HistoryQuery { Patch = "7.22" }, CancellationToken.None, null).GetAwaiter().GetResult(), "exact patch suffix boundary");
            var officialPlan = Resolver(new FakeBrowser(Baseline(), Newest(), Previous()), (patch, builds, token, progress) => Task.FromResult(new OfficialPatchMatch { Build = builds.Single(), Sources = new List<string> { "https://www.dota2.com/700/" } })).ResolveAsync(new HistoryQuery { Patch = "7.00" }, CancellationToken.None, null).GetAwaiter().GetResult();
            Check(officialPlan.BuildId == "20" && officialPlan.Sources.Contains("https://www.dota2.com/700/"), "official fallback source integration");
            var windowPlan = Resolver(new FakeBrowser(Baseline(), Newest(), Previous()), (patch, builds, token, progress) => Task.FromResult(Window(builds.Single(), "2019-01-01T12:00:00Z", "2019-01-02T12:00:00Z"))).ResolveAsync(new HistoryQuery { Patch = "7.00" }, CancellationToken.None, null).GetAwaiter().GetResult();
            Check(windowPlan.PatchWindowPublicEvents == 1 && windowPlan.PublishedAtUtc == DateTimeOffset.Parse("2019-01-02T00:00:00Z"), "unique official window checked against branch history");
            Reject(() => Resolver(new FakeBrowser(Baseline(), Newest(), Previous()), (patch, builds, token, progress) => Task.FromResult(Window(builds.Single(), "2018-12-31T00:00:00Z", "2019-01-02T12:00:00Z"))).ResolveAsync(new HistoryQuery { Patch = "7.00" }, CancellationToken.None, null).GetAwaiter().GetResult(), "previous publication inside window refused");
            Reject(() => Resolver(new FakeBrowser(Baseline(), Newest(), Previous()), (patch, builds, token, progress) => Task.FromResult(Window(builds.Single(), "2019-01-01T12:00:00Z", "2019-01-03T12:00:00Z"))).ResolveAsync(new HistoryQuery { Patch = "7.00" }, CancellationToken.None, null).GetAwaiter().GetResult(), "other build in official window refused");
            var rollbackBrowser = new FakeBrowser(Baseline().Replace(">30<", ">20<"), Newest().Replace("<ins>30</ins>", "<ins>20</ins>"), Previous());
            Reject(() => Resolver(rollbackBrowser, (patch, builds, token, progress) => Task.FromResult(Window(builds.Single(), "2019-01-01T12:00:00Z", "2019-01-03T12:00:00Z"))).ResolveAsync(new HistoryQuery { Patch = "7.00" }, CancellationToken.None, null).GetAwaiter().GetResult(), "same build rollback still counts as another event");
            Reject(() => Resolver(new FakeBrowser(Baseline(), Newest(), Previous()), (patch, builds, token, progress) => Task.FromResult(new OfficialPatchMatch { Build = new HistoryBuildEvent { BuildId = "999", SourceUrl = "https://steamdb.info/patchnotes/999/" } })).ResolveAsync(new HistoryQuery { Patch = "7.00" }, CancellationToken.None, null).GetAwaiter().GetResult(), "fallback cannot invent build");
            var drift = new FakeBrowser(Baseline(), Newest(), Previous()) { Drift = true };
            Reject(() => Resolver(drift).ResolveAsync(new HistoryQuery { RequestedUtc = DateTimeOffset.Parse("2019-01-02T12:00:00Z") }, CancellationToken.None, null).GetAwaiter().GetResult(), "baseline race");
            CacheChecks();
            DateFirstIntegrationChecks();
            DateContinuationChecks();
            Console.WriteLine("PASS " + checks + " historical resolver assertions (synthetic fixtures, captured 27103115 DOM, cache lifecycle, date-first integration).");
            return 0;
        }

        private static string DepotOnlyHistory(string body)
        {
            return "<div class='panel panel-history' data-changeid='700'><div class='panel-heading'><relative-time datetime='2019-01-04T00:00:00Z'></relative-time></div><ul class='app-history'><li class='repo'><span>Changed <i>Depots</i><ul class='app-history'>" + body + "</ul></span></li></ul></div>";
        }

        private static string NamedBranch(string name, string body)
        {
            return "<li><span>Removed <span class='branch-name'>" + name + "</span> branch<ul class='app-history'>" + body + "</ul></span></li>";
        }

        private static void PrivateBranchMetadataChecks(SteamDbHistoryParser parser)
        {
            const string historyUrl = "https://steamdb.info/app/570/history/?filterkey=2";
            string encrypted = Delta("Depot 373301 encrypted download", "080049E03756DF0461E86789D04011E0", null) +
                Delta("Depot 373301 encrypted gid", "0E6A31B222999863713BFCE5B4E169F7", null) +
                Delta("Depot 373301 encrypted size", "C8C06D7D1DA6C14E14C7F8F5CD315559", null);
            string privateBranch = NamedBranch("vulkan_debugging", Delta("pwdrequired", "1", null) + encrypted);
            HistoryChange known = parser.ParseHistory(Page(historyUrl, DepotOnlyHistory(Delta("privatebranches", null, "1") + privateBranch))).Changes.Single();
            Check(known.ParsedAllDepotChanges && known.Deltas.Count == 0 && known.Publication == null, "root privatebranches and named private encrypted metadata do not change public state");
            foreach (string field in new[] { "download", "gid", "size" })
            {
                string leaf = Delta("Depot 373301 encrypted " + field, "AABB", "CCDD");
                Check(parser.ParseHistory(Page(historyUrl, DepotOnlyHistory(NamedBranch("private_beta", leaf)))).Changes.Single().ParsedAllDepotChanges, "known private encrypted field: " + field);
                Check(!parser.ParseHistory(Page(historyUrl, DepotOnlyHistory(NamedBranch("public", leaf)))).Changes.Single().ParsedAllDepotChanges, "public encrypted field stays unsupported: " + field);
                Check(!parser.ParseHistory(Page(historyUrl, DepotOnlyHistory(leaf))).Changes.Single().ParsedAllDepotChanges, "unscoped encrypted field stays unsupported: " + field);
            }
            Check(!parser.ParseHistory(Page(historyUrl, DepotOnlyHistory(NamedBranch("private_beta", Delta("Depot 373301 encrypted hash", "AA", "BB"))))).Changes.Single().ParsedAllDepotChanges, "unknown private encryption field not broadly ignored");
            foreach (string legacy in new[] { "encrypted_size", "encrypted_gid" })
            {
                string leaf = Delta(legacy, "AABB", "CCDD");
                Check(parser.ParseHistory(Page(historyUrl, DepotOnlyHistory(NamedBranch("private_beta", leaf)))).Changes.Single().ParsedAllDepotChanges, "known legacy encrypted metadata remains private: " + legacy);
                Check(!parser.ParseHistory(Page(historyUrl, DepotOnlyHistory(NamedBranch("public", leaf)))).Changes.Single().ParsedAllDepotChanges, "legacy public encrypted metadata is not silently ignored: " + legacy);
                Check(!parser.ParseHistory(Page(historyUrl, DepotOnlyHistory(leaf))).Changes.Single().ParsedAllDepotChanges, "legacy unscoped encrypted metadata is not silently ignored: " + legacy);
            }
            Check(!parser.ParseHistory(Page(historyUrl, DepotOnlyHistory(NamedBranch("", encrypted)))).Changes.Single().ParsedAllDepotChanges, "empty branch identity is not private scope proof");
            Check(!parser.ParseHistory(Page(historyUrl, DepotOnlyHistory(NamedBranch("public", Delta("privatebranches", null, "1"))))).Changes.Single().ParsedAllDepotChanges, "branch-local privatebranches is not root flag");
            Check(!parser.ParseHistory(Page(historyUrl, DepotOnlyHistory("<li><span>Changed <i>Depot 1</i><ul>" + Delta("privatebranches", null, "1") + "</ul></span></li>"))).Changes.Single().ParsedAllDepotChanges, "depot-local privatebranches is not root flag");
            Check(!parser.ParseHistory(Page(historyUrl, DepotOnlyHistory("<li><span>Changed <i>privatebranches:</i><ul>" + Delta("unexpected", null, "1") + "</ul></span></li>"))).Changes.Single().ParsedAllDepotChanges, "privatebranches container is not a scalar whitelist");
            Check(!parser.ParseHistory(Page(historyUrl, DepotOnlyHistory(Delta("privatebranches_extra", null, "1")))).Changes.Single().ParsedAllDepotChanges, "root flag whitelist is exact");

            HistoryChange ordinary = parser.ParseHistory(Page(historyUrl, Newest())).Changes.Single();
            string mixedHtml = Newest().Replace("<span>Changed <i>Depots</i></span><ul>", "<span>Changed <i>Depots</i></span><ul>" + Delta("privatebranches", null, "1") + privateBranch);
            HistoryChange mixed = parser.ParseHistory(Page(historyUrl, mixedHtml)).Changes.Single();
            Check(mixed.ParsedAllDepotChanges && mixed.Publication.BuildId == ordinary.Publication.BuildId && mixed.Publication.PublishedAtUtc == ordinary.Publication.PublishedAtUtc, "private metadata does not hide simultaneous public publication");
            Check(mixed.Deltas.Count == ordinary.Deltas.Count && mixed.Deltas.All(d => ordinary.Deltas.Any(o => o.Path == d.Path && o.OldValue == d.OldValue && o.NewValue == d.NewValue && o.OldExists == d.OldExists && o.NewExists == d.NewExists)), "simultaneous public manifests and depot configuration remain identical");
            HistoryDepotState before = parser.ParseBaseline(Page("https://steamdb.info/app/570/depots/?branch=public", Baseline())).State;
            HistoryDepotState after = parser.ParseBaseline(Page("https://steamdb.info/app/570/depots/?branch=public", Baseline())).State;
            before.Reverse(ordinary); after.Reverse(mixed);
            Check(before.Values.Count == after.Values.Count && before.Values.All(p => after.Value(p.Key) == p.Value), "mixed private/public record reverses to same public depot combination");

            string fixture = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "fixtures", "change-27103115", "20261018-135125-183-app-570-history.html"));
            string realHtml = File.ReadAllText(fixture);
            HistoryChange actual = parser.ParseHistory(Page("https://steamdb.info/app/570/history/?changeid=27103115", realHtml)).Changes.Single();
            Check(actual.ChangeId == "27103115" && actual.ParsedAllDepotChanges && actual.UnparsedChanges.Count == 0, "actual 27103115 fixture fully recognized");
            Check(actual.Publication == null && actual.Deltas.Count == 0, "actual five removed private branches do not invent a public change");
            HistoryDepotState projected = parser.ParseBaseline(Page("https://steamdb.info/app/570/depots/?branch=public", Baseline())).State;
            var values = new Dictionary<string, string>(projected.Values);
            projected.Reverse(actual);
            Check(values.Count == projected.Values.Count && values.All(p => projected.Value(p.Key) == p.Value), "actual private migration leaves public manifests and configs unchanged");
            HistoryChange unknownActual = parser.ParseHistory(Page("https://steamdb.info/app/570/history/?changeid=27103115", realHtml.Replace("encrypted download:", "encrypted unexpected:"))).Changes.Single();
            Check(!unknownActual.ParsedAllDepotChanges && unknownActual.UnparsedChanges.Any(f => f.Contains("encrypted unexpected")), "actual fixture with unknown private field is still rejected");
        }

        private static void SameManifestMigrationChecks(SteamDbHistoryParser parser)
        {
            const string url = "https://steamdb.info/app/570/history/?filterkey=2";
            string removed = Delta("Depot 1 gid", "300", null).Replace("diff-modified", "diff-removed");
            string added = Delta("Depot 1 gid", null, "300").Replace("diff-modified", "diff-added");
            Func<string, HistoryChange> parse = body => parser.ParseHistory(Page(url, DepotOnlyHistory(NamedBranch("public", body)))).Changes.Single();
            HistoryChange retained = parse(removed + added);
            Check(retained.ParsedAllDepotChanges && retained.Deltas.Count == 1 && retained.Deltas[0].OldExists && retained.Deltas[0].NewExists && retained.Deltas[0].OldValue == "300" && retained.Deltas[0].NewValue == "300", "ordered equal public manifest remove/add becomes an explicit retained-value delta");
            HistoryDepotState state = parser.ParseBaseline(Page("https://steamdb.info/app/570/depots/?branch=public", Baseline())).State;
            state.Validate(retained); state.Reverse(retained);
            Check(state.Value("depots/1/manifests/public/gid") == "300", "same-manifest migration validates and leaves full state unchanged");
            state.Values["depots/1/manifests/public/gid"] = "301";
            Reject(() => state.Validate(retained), "retained-value delta still rejects a different expected manifest");
            foreach (string invalid in new[] {
                removed + added.Replace("<ins>300</ins>", "<ins>301</ins>"),
                removed + added + added,
                removed + added + removed,
                added + removed,
                removed + removed,
                added + added,
                removed.Replace("diff-removed", "diff-modified") + added,
                removed + added.Replace("diff-added", "diff-modified"),
                (removed + added).Replace("Depot 1 gid", "Depot 1 name"),
                removed + Delta("Depot 1 gid", "300", "300")
            }) Check(!parse(invalid).ParsedAllDepotChanges, "conflicting, reversed, extra, or non-observed duplicate shape is rejected");
            foreach (string field in new[] { "encrypted_gid_2", "encrypted_size_2" })
            {
                string leaf = Delta("Depot 373301 " + field, "AABB", null);
                Check(parser.ParseHistory(Page(url, DepotOnlyHistory(NamedBranch("private_beta", leaf)))).Changes.Single().ParsedAllDepotChanges, "known private schema field is outside public projection: " + field);
                Check(!parser.ParseHistory(Page(url, DepotOnlyHistory(NamedBranch("public", leaf)))).Changes.Single().ParsedAllDepotChanges, "public underscore encryption field is not ignored: " + field);
                Check(!parser.ParseHistory(Page(url, DepotOnlyHistory(leaf))).Changes.Single().ParsedAllDepotChanges, "unscoped underscore encryption field is not ignored: " + field);
            }
            foreach (string unknown in new[] { "encrypted_hash_2", "encrypted_gid_3", "encrypted_size_20" })
                Check(!parser.ParseHistory(Page(url, DepotOnlyHistory(NamedBranch("private_beta", Delta("Depot 373301 " + unknown, "AA", null))))).Changes.Single().ParsedAllDepotChanges, "unknown similar private encryption field remains unsupported: " + unknown);

            string fixture = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "fixtures", "change-18987972", "history.html"));
            string html = File.ReadAllText(fixture);
            HistoryChange actual = parser.ParseHistory(Page(url, html)).Changes.Single();
            Check(actual.ChangeId == "18987972" && actual.ParsedAllDepotChanges && actual.Publication == null && actual.Deltas.Count == 19, "captured migration parses all 19 public manifest pairs and private metadata");
            Check(actual.Deltas.All(d => d.Path.EndsWith("/manifests/public/gid") && d.OldExists && d.NewExists && d.OldValue == d.NewValue), "captured migration preserves explicit old-equals-new public state checks");
            var projected = new HistoryDepotState { CompleteBaseline = true };
            foreach (HistoryDelta delta in actual.Deltas) projected.Values.Add(delta.Path, delta.NewValue);
            projected.Validate(actual); projected.Reverse(actual);
            Check(projected.Values.Count == 19 && actual.Deltas.All(d => projected.Value(d.Path) == d.OldValue), "captured migration reverse leaves every public manifest unchanged");
            var document = SteamDbHistoryParser.Load(Page(url, html));
            var publicBranch = document.DocumentNode.Descendants("span").First(n => SteamDbHistoryParser.HasClass(n, "branch-public"));
            var firstAddition = publicBranch.ParentNode.Descendants("li").First(n => SteamDbHistoryParser.HasClass(n, "diff-added") && n.InnerText.Contains("gid:"));
            var valueNode = firstAddition.Descendants("a").First(n => SteamDbHistoryParser.HasClass(n, "ins"));
            valueNode.InnerHtml = "999";
            Check(!parser.ParseHistory(Page(url, document.DocumentNode.OuterHtml)).Changes.Single().ParsedAllDepotChanges, "captured migration with conflicting added manifest fails closed");
            document = SteamDbHistoryParser.Load(Page(url, html));
            publicBranch = document.DocumentNode.Descendants("span").First(n => SteamDbHistoryParser.HasClass(n, "branch-public"));
            firstAddition = publicBranch.ParentNode.Descendants("li").First(n => SteamDbHistoryParser.HasClass(n, "diff-added") && n.InnerText.Contains("gid:"));
            firstAddition.ParentNode.AppendChild(firstAddition.CloneNode(true));
            Check(!parser.ParseHistory(Page(url, document.DocumentNode.OuterHtml)).Changes.Single().ParsedAllDepotChanges, "captured migration with a third occurrence fails closed");
        }

        private static void NameProjectionChecks(SteamDbHistoryParser parser)
        {
            const string url = "https://steamdb.info/app/570/history/?filterkey=2";
            HistoryDepotState state = parser.ParseBaseline(Page("https://steamdb.info/app/570/depots/?branch=public", Baseline())).State;
            const string namePath = "depots/1/name";
            Check(state.Value(namePath) == null && state.DisplayNames[namePath] == "Core" && !state.KnownNamePaths.Contains(namePath), "baseline display label does not assert canonical AppInfo name presence");
            Check(state.SelectWindowsClient("schinese").Single(d => d.DepotId == "1").Name == "Core", "unproven display label remains usable as presentation text");
            HistoryChange removedName = parser.ParseHistory(Page(url, DepotOnlyHistory(Delta("Depot 1 name", "Canonical historical name", null)))).Changes.Single();
            state.Validate(removedName); state.Reverse(removedName);
            Check(state.KnownNamePaths.Contains(namePath) && state.Value(namePath) == "Canonical historical name", "first name history establishes canonical state without comparing a display fallback");
            var wrongEarlier = parser.ParseHistory(Page(url, DepotOnlyHistory(Delta("Depot 1 name", "Older name", "Wrong later name")))).Changes.Single();
            Reject(() => state.Validate(wrongEarlier), "known canonical name history still checks its next value");
            var correctEarlier = parser.ParseHistory(Page(url, DepotOnlyHistory(Delta("Depot 1 name", "Older name", "Canonical historical name")))).Changes.Single();
            state.Reverse(correctEarlier);
            Check(state.SelectWindowsClient("schinese").Single(d => d.DepotId == "1").Name == "Older name", "proven historical name is preferred over current display label");
            var manifestMismatch = parser.ParseHistory(Page(url, DepotOnlyHistory(NamedBranch("public", Delta("Depot 1 gid", "200", "999")) + Delta("Depot 2 name", "Retained", null)))).Changes.Single();
            Reject(() => state.Validate(manifestMismatch), "unknown cosmetic name does not relax manifest consistency");
            var configMismatch = parser.ParseHistory(Page(url, DepotOnlyHistory(Delta("Depot 1 config/oslist", "linux", "windows") + Delta("Depot 2 name", "Retained", null)))).Changes.Single();
            Reject(() => state.Validate(configMismatch), "unknown cosmetic name does not relax platform consistency");

            string evidence = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "name-projection-evidence"));
            string v2Path = Path.Combine(evidence, "verified-public-anchors-v2.json");
            string originalV2 = File.ReadAllText(v2Path);
            var anchors = new HistoryAnchorStore(evidence).Load();
            var v1 = new HistoryCacheStore(evidence).Load();
            Check(anchors.Count == 1 && anchors[0].Changes.Count == 1078 && v1.Changes.Count == 1078, "actual v1 and v2 preserve every verified record during name projection migration");
            Check(anchors[0].Baseline.State.DisplayNames.Count > 0 && !anchors[0].Baseline.State.Values.Keys.Any(HistoryDepotState.IsNamePath) && anchors[0].Baseline.State.KnownNamePaths.Count == 0, "legacy baseline names migrate to unproven display labels");
            var json = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
            var wrapper = json.Deserialize<HistoryAnchorStore.Envelope>(originalV2);
            Check(wrapper.Anchors[0].Identity == anchors[0].Identity && HistoryAnchorStore.Identity(v1.Baseline) == anchors[0].Identity, "legacy baseline identity and checked payload digest survive projection migration");
            string snapshotPath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "live-before-2015-20261021-resume1", "latest-history.json"));
            var snapshot = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(snapshotPath));
            string html = (string)snapshot["Html"];
            var listing = parser.ParseHistory(Page((string)snapshot["Url"], html));
            HistoryChange failed = listing.Changes.Single(c => c.ChangeId == "17508630");
            Check(failed.Deltas.Any(d => d.Path == "depots/373301/name" && d.OldValue == "Dota 2 Content" && !d.NewExists), "captured 17508630 explicitly removes the name retained as a later display label");
            DateTimeOffset requested = listing.Changes.Where(c => c.Publication != null && c.Publication.PublishedAtUtc.Year >= 1970).Min(c => c.Publication.PublishedAtUtc).AddTicks(1);
            HistoryChange expected = listing.Changes.First(c => c.Publication != null && c.Publication.PublishedAtUtc.Year >= 1970 && c.Publication.PublishedAtUtc < requested);
            string migrated = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "name-replay-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(migrated);
            File.Copy(v2Path, Path.Combine(migrated, "verified-public-anchors-v2.json"));
            File.Copy(Path.Combine(evidence, "verified-public-history-v1.json"), Path.Combine(migrated, "verified-public-history-v1.json"));
            var replayBrowser = new FakeBrowser(Baseline(), html, "") { DateHistoryOverride = html, FailOnClick = true };
            var result = new OnlineHistoryResolver(replayBrowser, null, migrated).ResolveAsync(new HistoryQuery { RequestedUtc = requested }, CancellationToken.None, null).GetAwaiter().GetResult();
            Check(result.Complete && result.PatchSourceChangeId == expected.ChangeId && result.PublishedAtUtc == expected.Publication.PublishedAtUtc && replayBrowser.Baselines == 0, "actual full verified prefix and failing batch replay through name removal without live current baseline");
            var after = new HistoryAnchorStore(migrated).Load().Single();
            Check(after.Identity == anchors[0].Identity && after.Changes.Count > 1078 && after.Changes.Any(c => c.ChangeId == "17508630"), "migrated cache preserves original identity and extends proof beyond prior failure");
            Check(after.Baseline.State.DisplayNames.Count == anchors[0].Baseline.State.DisplayNames.Count && after.Baseline.State.KnownNamePaths.Count == 0, "new serialized baseline preserves separated display-only projection");
            Check(File.ReadAllText(v2Path) == originalV2, "original copied evidence remains untouched until a separately validated save");
        }

        private static void PrerequisiteProjectionChecks(SteamDbHistoryParser parser)
        {
            const string url = "https://steamdb.info/app/570/history/?filterkey=2";
            const string id = "991001";
            string fields = Delta("Depot " + id + " config/oslist", null, "windows") +
                Delta("Depot " + id + " depotfromapp", null, "228980") +
                Delta("Depot " + id + " name", null, "Runtime") +
                Delta("Depot " + id + " sharedinstall", null, "1");
            HistoryChange addition = parser.ParseHistory(Page(url, DepotOnlyHistory(fields))).Changes.Single();
            Check(addition.ParsedAllDepotChanges && addition.Deltas.Count == 4 && addition.Deltas.Any(d => d.Path.EndsWith("/sharedinstall") && d.NewValue == "1"), "whole prerequisite addition keeps original source and install evidence");
            HistoryDepotState state = parser.ParseBaseline(Page("https://steamdb.info/app/570/depots/?branch=public", Baseline())).State;
            string originalGameState = String.Join("|", state.Values.OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value));
            state.HasSharedPrerequisiteBaseline = false; // Exercise the legacy whole-reference projection.
            state.Reverse(addition);
            Check(!state.Values.Keys.Any(p => p.StartsWith("depots/" + id + "/")) && originalGameState == String.Join("|", state.Values.OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value)), "proven prerequisite addition leaves all downloadable game fields unchanged");
            var removal = parser.ParseHistory(Page(url, DepotOnlyHistory(fields.Replace("<ins>", "<del>").Replace("</ins>", "</del>")))).Changes.Single();
            state.Reverse(removal);
            Check(state.Value("depots/" + id + "/name") == "Runtime" && state.Values.Keys.Count(p => p.StartsWith("depots/" + id + "/")) == 1, "whole prerequisite removal restores only canonical presentation metadata");
            state.Reverse(addition);
            Check(state.Value("depots/" + id + "/name") == null, "canonical prerequisite name is reversed normally across its creation");
            state.Values["depots/" + id + "/name"] = "Wrong name";
            Reject(() => state.Validate(addition), "projected prerequisite still validates a previously proven canonical name");
            state.Values.Remove("depots/" + id + "/name");
            state.Values["depots/" + id + "/manifests/public/gid"] = "999";
            Reject(() => state.Validate(addition), "prerequisite group cannot conceal an existing public game manifest");
            state.Values.Remove("depots/" + id + "/manifests/public/gid");
            state.Values["depots/" + id + "/config/language"] = "english";
            Reject(() => state.Validate(addition), "prerequisite group cannot conceal existing game selection configuration");

            Check(!parser.ParseHistory(Page(url, DepotOnlyHistory(fields.Replace("228980", "730")))).Changes.Single().ParsedAllDepotChanges, "other shared source app does not qualify as Steamworks prerequisite");
            Check(!parser.ParseHistory(Page(url, DepotOnlyHistory(fields.Replace("sharedinstall", "sharedinstall_unknown")))).Changes.Single().ParsedAllDepotChanges, "unknown shared installation field remains a hard gap");
            Check(!parser.ParseHistory(Page(url, DepotOnlyHistory(fields.Replace(Delta("Depot " + id + " depotfromapp", null, "228980"), "")))).Changes.Single().ParsedAllDepotChanges, "sharedinstall alone cannot identify a prerequisite");
            Check(!parser.ParseHistory(Page(url, DepotOnlyHistory(fields.Replace(Delta("Depot " + id + " sharedinstall", null, "1"), Delta("Depot " + id + " sharedinstall", null, "2"))))).Changes.Single().ParsedAllDepotChanges, "unobserved sharedinstall value is not assumed equivalent");
            Check(!parser.ParseHistory(Page(url, DepotOnlyHistory(fields.Replace(Delta("Depot " + id + " depotfromapp", null, "228980"), Delta("Depot " + id + " depotfromapp", "570", "228980"))))).Changes.Single().ParsedAllDepotChanges, "game to shared app transition requires full state treatment");
            Check(!parser.ParseHistory(Page(url, DepotOnlyHistory(fields + NamedBranch("public", Delta("Depot " + id + " gid", null, "777"))))).Changes.Single().ParsedAllDepotChanges, "public manifest addition cannot be projected out with prerequisite metadata");
            Check(!parser.ParseHistory(Page(url, DepotOnlyHistory(fields.Replace(Delta("Depot " + id + " config/oslist", null, "windows"), Delta("Depot " + id + " config/oslist", "linux", "windows"))))).Changes.Single().ParsedAllDepotChanges, "mixed modification is not a whole prerequisite reference addition");
            Check(!parser.ParseHistory(Page(url, DepotOnlyHistory(NamedBranch("public", fields)))).Changes.Single().ParsedAllDepotChanges, "sharedinstall in a branch cannot masquerade as top-level shared reference metadata");
            Check(!parser.ParseHistory(Page(url, DepotOnlyHistory(fields + Delta("Depot " + id + " sharedinstall", null, "1")))).Changes.Single().ParsedAllDepotChanges, "duplicate prerequisite metadata remains rejected");

            string sharedRow = "<tr class='depot' data-depotid='" + id + "'><td>" + id + "</td><td class='depot-config'><span class='depot-os'>Windows</span>Depot from <a href='/app/228980/'>Steamworks Common Redistributables</a></td><td></td></tr>";
            string sharedBaseline = Baseline().Replace("</table></div>", sharedRow + "</table></div>");
            var projectedBaseline = parser.ParseBaseline(Page("https://steamdb.info/app/570/depots/?branch=public", sharedBaseline));
            Check(projectedBaseline.State.Value("depots/" + id + "/depotfromapp") == "228980" && projectedBaseline.State.Value("depots/" + id + "/config/oslist") == "windows" && projectedBaseline.State.SelectWindowsClient("schinese").All(d => d.DepotId != id), "explicit shared source and platform remain in baseline while runtime stays outside selected game client");
            Reject(() => parser.ParseBaseline(Page("https://steamdb.info/app/570/depots/?branch=public", sharedBaseline.Replace("<td></td>", "<td><a href='/depot/" + id + "/history/?changeid=M:99'>99</a></td>"))), "shared baseline public manifest requires explicit shared-app treatment");
            Reject(() => parser.ParseBaseline(Page("https://steamdb.info/app/570/depots/?branch=public", sharedBaseline.Replace("228980", "730"))), "non-Steamworks shared baseline is not excluded");
            Reject(() => parser.ParseBaseline(Page("https://steamdb.info/app/570/depots/?branch=public", sharedBaseline.Replace("</table></div>", sharedRow + "</table></div>"))), "duplicate excluded prerequisite row does not bypass baseline duplicate detection");

            string evidence = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "prerequisite-projection-evidence");
            string v2 = Path.Combine(evidence, "verified-public-anchors-v2.json"), v1 = Path.Combine(evidence, "verified-public-history-v1.json");
            string originalV2 = File.ReadAllText(v2), originalV1 = File.ReadAllText(v1);
            var original = new HistoryAnchorStore(evidence).Load().Single();
            Check(original.Changes.Count == 1215 && original.Changes.Last().ChangeId == "15920163", "real saved prefix retains all 1215 verified changes");
            var serializer = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
            var snapshot = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "live-before-2015-20261021-resume2", "latest-history.json")));
            string html = (string)snapshot["Html"];
            HistoryListing listing = parser.ParseHistory(Page((string)snapshot["Url"], html));
            Check(listing.Changes.Count == 100 && listing.Changes.All(c => c.ParsedAllDepotChanges), "every record in actual 100-change failing batch parses completely");
            HistoryChange actual = listing.Changes.Single(c => c.ChangeId == "15919744");
            Check(actual.Publication == null && actual.Deltas.Count == 4 && HistoryDepotState.ProjectedPrerequisitePrefixes(actual).SetEquals(new[] { "depots/228989/" }), "actual 15919744 is a whole Steamworks reference addition without a public manifest or build event");
            Check(original.Changes.Any(c => c.ChangeId == "17508630" && c.Deltas.Any(d => d.Path == "depots/228989/name" && !d.NewExists)), "actual prefix includes later canonical runtime name removal");
            string replay = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "prerequisite-replay-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(replay); File.Copy(v1, Path.Combine(replay, Path.GetFileName(v1))); File.Copy(v2, Path.Combine(replay, Path.GetFileName(v2)));
            DateTimeOffset requested = listing.Changes.Where(c => c.Publication != null && c.Publication.PublishedAtUtc.Year >= 1970).Min(c => c.Publication.PublishedAtUtc).AddTicks(1);
            HistoryChange expected = listing.Changes.First(c => c.Publication != null && c.Publication.PublishedAtUtc.Year >= 1970 && c.Publication.PublishedAtUtc < requested);
            var browser = new FakeBrowser(Baseline(), html, "") { DateHistoryOverride = html, FailOnClick = true };
            var resolver = new OnlineHistoryResolver(browser, null, replay);
            var plan = resolver.ResolveAsync(new HistoryQuery { RequestedUtc = requested }, CancellationToken.None, null).GetAwaiter().GetResult();
            Check(plan.Complete && plan.PatchSourceChangeId == expected.ChangeId && browser.Baselines == 0 && plan.Depots.All(d => d.DepotId != "228989"), "production resolver resumes actual proof prefix and reconstructs target past the shared prerequisite event");
            HistoryAnchorStore.Anchor extended = new HistoryAnchorStore(replay).Load().Single();
            // Verify even any non-public records older than the selected publication,
            // using the same production full-chain validator before saving the test copy.
            var seen = new HashSet<string>(extended.Changes.Select(c => c.ChangeId));
            extended.Changes.AddRange(listing.Changes.Where(c => !seen.Contains(c.ChangeId)));
            var validate = typeof(OnlineHistoryResolver).GetMethod("ValidateAnchorAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            ((Task)validate.Invoke(resolver, new object[] { extended, CancellationToken.None })).GetAwaiter().GetResult();
            Check(extended.Changes.Last().ChangeId == listing.Changes.Last().ChangeId && extended.Changes.Count > 1215, "entire remaining actual batch passes production Build time and field continuity validation");
            new HistoryAnchorStore(replay).Save(new[] { extended });
            var saved = new HistoryAnchorStore(replay).Load().Single();
            Check(saved.Identity == original.Identity && saved.Changes.Single(c => c.ChangeId == "15919744").Deltas.Any(d => d.Path == "depots/228989/sharedinstall" && d.NewValue == "1"), "cache roundtrip preserves original identity and new raw prerequisite evidence");
            ((Task)validate.Invoke(resolver, new object[] { saved, CancellationToken.None })).GetAwaiter().GetResult();
            Check(File.ReadAllText(v1) == originalV1 && File.ReadAllText(v2) == originalV2, "original host evidence copies remain unmodified");
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "prerequisite-projection-regression-results.json"), serializer.Serialize(new { Passed = true, OriginalChanges = original.Changes.Count, ValidatedChanges = saved.Changes.Count, LastChange = saved.Changes.Last().ChangeId, IdentityPreserved = saved.Identity == original.Identity, ClientBuild = plan.BuildId, CacheCopy = replay }));
        }

        private static void SharedTransitionChecks(SteamDbHistoryParser parser)
        {
            const string historyUrl = "https://steamdb.info/app/570/history/?filterkey=2";
            const string baselineUrl = "https://steamdb.info/app/570/depots/?branch=public";
            const string id = "991002";
            const string prefix = "depots/991002/";
            string row = "<tr class='depot' data-depotid='" + id + "'><td>" + id + "</td><td class='depot-config'><span class='depot-os'>Windows</span><span>Shared Install</span><a href='/app/228980/'>Depot from 228980</a><span class='i muted'>Runtime</span></td><td></td></tr>";
            Func<HistoryDepotState> fresh = () => parser.ParseBaseline(Page(baselineUrl, Baseline().Replace("</table></div>", row + "</table></div>"))).State;
            HistoryChange transition = parser.ParseHistory(Page(historyUrl, DepotOnlyHistory(Delta("Depot " + id + " sharedinstall", null, "1") + NamedBranch("public", Delta("Depot " + id + " gid", "500", null))))).Changes.Single();
            Check(transition.ParsedAllDepotChanges && transition.Deltas.Count == 2, "shared-install migration retains both original flag and removed public manifest");
            HistoryDepotState state = fresh();
            Check(state.HasSharedPrerequisiteBaseline && state.Value(prefix + "depotfromapp") == "228980" && state.Value(prefix + "config/oslist") == "windows", "fresh complete baseline captures explicit runtime source and platform");
            Check(state.Value(prefix + "sharedinstall") == null && !state.KnownSharedInstallPaths.Contains(prefix + "sharedinstall"), "Shared Install badge does not invent an exact numeric AppInfo value");
            state.Reverse(transition);
            Check(state.Value(prefix + "manifests/public/gid") == "500" && state.KnownSharedInstallPaths.Contains(prefix + "sharedinstall") && state.Value(prefix + "sharedinstall") == null, "reverse migration restores authentic historical public manifest and establishes flag absence");
            Check(state.SelectWindowsClient("schinese").All(d => d.DepotId != id), "runtime source at selected historical state excludes it from game client scope");
            Reject(() => state.Validate(transition), "a second contradictory migration cannot bypass established shared-install state");
            var wrongGid = parser.ParseHistory(Page(historyUrl, DepotOnlyHistory(NamedBranch("public", Delta("Depot " + id + " gid", "400", "499"))))).Changes.Single();
            Reject(() => state.Validate(wrongGid), "historical runtime manifests are validated instead of ignored");
            var rightGid = parser.ParseHistory(Page(historyUrl, DepotOnlyHistory(NamedBranch("public", Delta("Depot " + id + " gid", "400", "500"))))).Changes.Single();
            state.Reverse(rightGid);
            var sourceAddition = parser.ParseHistory(Page(historyUrl, DepotOnlyHistory(Delta("Depot " + id + " depotfromapp", null, "228980")))).Changes.Single();
            state.Reverse(sourceAddition);
            Check(state.Value(prefix + "depotfromapp") == null && state.SelectWindowsClient("schinese").Single(d => d.DepotId == id).ManifestId == "400", "reverse source transition can return historical content to game scope");
            var unknownSource = parser.ParseBaseline(Page(baselineUrl, Baseline())).State;
            Reject(() => unknownSource.Validate(transition), "migration cannot fabricate source from a familiar Depot ID");
            var wrongSource = fresh(); wrongSource.Values[prefix + "depotfromapp"] = "730";
            Reject(() => wrongSource.Validate(transition), "migration from another shared app is not treated as Steamworks runtime");
            var legacy = fresh(); legacy.HasSharedPrerequisiteBaseline = false;
            Reject(() => legacy.Validate(transition), "old unproved baseline requires original capture migration");
            var knownMismatch = fresh(); knownMismatch.KnownSharedInstallPaths.Add(prefix + "sharedinstall"); knownMismatch.Values[prefix + "sharedinstall"] = "2";
            Reject(() => knownMismatch.Validate(transition), "known shared-install numeric values retain strict continuity checks");

            string root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", ".."));
            string source = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "shared-transition-evidence");
            string capture = Path.Combine(root, "live-build909005", "latest-baseline.json");
            string history = Path.Combine(root, "live-before-2015-20261021-resume3", "latest-history.json");
            string output = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "shared-transition-replay-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string oldV1 = File.ReadAllText(Path.Combine(source, "verified-public-history-v1.json")), oldV2 = File.ReadAllText(Path.Combine(source, "verified-public-anchors-v2.json"));
            HistoryBaselineMigration.Report report = HistoryBaselineMigration.Prepare(capture, source, output, history);
            Check(report.SameBuildAndTime && report.OriginalGameFieldsEqual && report.FullPrefixReplayPassed && report.OriginalChanges == 2584 && report.ValidatedChanges == 2628 && report.LastChangeId == "5477528", "actual original baseline plus full saved prefix and entire next batch replay through the 2019 migration");
            Check(report.CaptureSha256.Length == 64 && report.HistoryCaptureSha256.Length == 64 && report.OldIdentities.Single() != report.NewIdentities.Single(), "migration records original capture digests and refines baseline identity with real source fields");
            var savedV1 = new HistoryCacheStore(output).Load();
            var savedV2 = new HistoryAnchorStore(output).Load().Single();
            Check(savedV1.Changes.Count == 2628 && savedV2.Changes.Count == 2628 && savedV1.Baseline.State.HasSharedPrerequisiteBaseline && savedV2.Baseline.State.HasSharedPrerequisiteBaseline, "both candidate cache formats preserve the full upgraded proof chain");
            var actual = savedV1.Changes.Single(c => c.ChangeId == "5740110");
            Check(actual.Deltas.Count == 4 && actual.Deltas.Count(d => d.Path.EndsWith("/manifests/public/gid") && d.OldExists && !d.NewExists) == 2, "actual old runtime manifests remain in saved raw evidence");
            Check(File.ReadAllText(Path.Combine(source, "verified-public-history-v1.json")) == oldV1 && File.ReadAllText(Path.Combine(source, "verified-public-anchors-v2.json")) == oldV2, "migration never overwrites original v1 or v2 evidence");
            string repeat = output + "-repeat";
            var repeated = HistoryBaselineMigration.Prepare(capture, output, repeat, null);
            Check(repeated.OldIdentities.Single() == repeated.NewIdentities.Single() && repeated.ValidatedChanges == 2628, "rechecking already enriched evidence is idempotent");

            var json = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
            var snapshot = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(capture));
            var document = SteamDbHistoryParser.Load(Page((string)snapshot["Url"], (string)snapshot["Html"]));
            var gameManifest = document.DocumentNode.Descendants("a").First(n => n.GetAttributeValue("href", "").StartsWith("/depot/373301/history/?changeid=M:", StringComparison.Ordinal));
            gameManifest.InnerHtml = "999999";
            snapshot["Html"] = document.DocumentNode.OuterHtml;
            string altered = Path.Combine(output, "altered-capture.json"); File.WriteAllText(altered, json.Serialize(snapshot));
            Reject(() => HistoryBaselineMigration.Prepare(altered, source, output + "-wrong-fields", null), "same Build with changed game manifest cannot supply migration evidence");
            snapshot = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(capture));
            snapshot["Html"] = ((string)snapshot["Html"]).Replace(">25971480<", ">25971481<");
            File.WriteAllText(altered, json.Serialize(snapshot));
            Reject(() => HistoryBaselineMigration.Prepare(altered, source, output + "-wrong-build", null), "different original baseline Build cannot migrate the cache");
            var historySnapshot = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(history));
            document = SteamDbHistoryParser.Load(Page((string)historySnapshot["Url"], (string)historySnapshot["Html"]));
            document.DocumentNode.Descendants().Single(n => n.GetAttributeValue("data-changeid", "") == "5740502").Remove();
            historySnapshot["Html"] = document.DocumentNode.OuterHtml;
            string missing = Path.Combine(output, "missing-overlap.json"); File.WriteAllText(missing, json.Serialize(historySnapshot));
            Reject(() => HistoryBaselineMigration.Prepare(capture, source, output + "-missing-overlap", missing), "offline batch extension still requires exact saved boundary overlap");
        }

        private static void OptionalDlcAndInlineDiffChecks(SteamDbHistoryParser parser)
        {
            const string url = "https://steamdb.info/app/570/history/?filterkey=2";
            Func<HistoryDepotState> fresh = () => parser.ParseBaseline(Page("https://steamdb.info/app/570/depots/?branch=public", Baseline())).State;
            string optional = Delta("Depot 7 config/optionaldlc", null, "100"), dlc = Delta("Depot 7 dlcappid", null, "100");
            HistoryChange pair = parser.ParseHistory(Page(url, DepotOnlyHistory(optional + dlc))).Changes.Single();
            var state = fresh();
            Check(state.Value("depots/7/dlcappid") == "100" && state.Value("depots/7/config/optionaldlc") == null && state.SelectWindowsClient("schinese").All(d => d.DepotId != "7"), "DLC badge proves exclusion without inventing an unrendered optional value");
            state.Reverse(pair);
            Check(state.KnownOptionalDlcPaths.Contains("depots/7/config/optionaldlc") && state.Value("depots/7/config/optionaldlc") == null && state.Value("depots/7/dlcappid") == null, "paired first addition establishes both fields absent before creation");
            Check(state.SelectWindowsClient("schinese").Any(d => d.DepotId == "7"), "actual paired reverse can restore historical standard-client scope");
            Reject(() => fresh().Validate(parser.ParseHistory(Page(url, DepotOnlyHistory(optional))).Changes.Single()), "unpaired optional DLC value cannot be inferred from badge");
            Reject(() => fresh().Validate(parser.ParseHistory(Page(url, DepotOnlyHistory(optional.Replace(">100<", ">200<") + dlc))).Changes.Single()), "different optional and ordinary DLC app IDs remain a hard gap");
            var knownAbsent = fresh(); knownAbsent.KnownOptionalDlcPaths.Add("depots/7/config/optionaldlc");
            Reject(() => knownAbsent.Validate(pair), "a previously proven optional absence cannot be relaxed by a paired addition");
            var conflicting = fresh(); conflicting.Values["depots/7/config/optionaldlc"] = "200";
            Reject(() => conflicting.Validate(pair), "existing contradictory optional value still fails");
            var missingDlc = fresh(); missingDlc.Values.Remove("depots/7/dlcappid");
            Reject(() => missingDlc.Validate(pair), "missing current DLC source prevents establishing hidden optional field");
            var modified = parser.ParseHistory(Page(url, DepotOnlyHistory(Delta("Depot 7 config/optionaldlc", "100", "100") + dlc))).Changes.Single();
            Reject(() => fresh().Validate(modified), "a modification cannot masquerade as the paired creation shape");
            var knownBaseline = new HistoryBaseline { BuildId = "30", PublishedAtUtc = DateTimeOffset.Parse("2019-01-03T00:00:00Z"), State = knownAbsent, SourceUrl = "https://steamdb.info/app/570/depots/?branch=public" };
            var roundtrip = HistoryCacheStore.Parse(HistoryCacheStore.Serialize(knownBaseline, new List<HistoryChange>()));
            Reject(() => roundtrip.Baseline.State.Validate(pair), "cache preserves proven optional absence and does not reopen inference");

            string inline = "<li class='diff-modified'><span><i>Depot 1 name:</i><span class='diff' title='Source 2 Core →&#10;Core'><del>Source 2 </del><span>Core</span></span></span></li>";
            var inlineChange = parser.ParseHistory(Page(url, DepotOnlyHistory(inline))).Changes.Single();
            Check(inlineChange.ParsedAllDepotChanges && inlineChange.Deltas.Single().OldValue == "Source 2 Core" && inlineChange.Deltas.Single().NewValue == "Core", "character diff combines unchanged text with old and new fragments");
            var knownName = fresh(); knownName.Values["depots/1/name"] = "Core"; knownName.KnownNamePaths.Add("depots/1/name"); knownName.Reverse(inlineChange);
            Check(knownName.Value("depots/1/name") == "Source 2 Core", "reconstructed whole name follows normal canonical validation");
            string replacement = "<li class='diff-modified'><span><i>Depot 1 name:</i><span class='diff' title='Old Core → New Core'><del>Old</del><ins>New</ins><span> Core</span></span></span></li>";
            var replaced = parser.ParseHistory(Page(url, DepotOnlyHistory(replacement))).Changes.Single();
            Check(replaced.Deltas.Single().OldValue == "Old Core" && replaced.Deltas.Single().NewValue == "New Core", "character replacement reconstructs both complete values");
            Check(!parser.ParseHistory(Page(url, DepotOnlyHistory(inline.Replace("Source 2 Core →", "Different Core →")))).Changes.Single().ParsedAllDepotChanges, "inline tooltip and actual DOM disagreement is rejected");
            Check(!parser.ParseHistory(Page(url, DepotOnlyHistory(inline.Replace("title='Source 2 Core →&#10;Core'", "")))).Changes.Single().ParsedAllDepotChanges, "unsupported inline diff without complete tooltip fails closed");

            string root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", ".."));
            string source = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "optionaldlc-evidence");
            string output = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "optionaldlc-replay-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var report = HistoryBaselineMigration.Prepare(Path.Combine(root, "live-build909005", "latest-baseline.json"), source, output, Path.Combine(root, "live-before-2015-20261021-resume6", "latest-history.json"));
            Check(report.OriginalChanges == 3578 && report.ValidatedChanges == 3583 && report.LastChangeId == "1963075" && report.FullPrefixReplayPassed && report.OldIdentities.Single() == report.NewIdentities.Single(), "entire actual prefix and remaining captured page replay with unchanged baseline identity");
            var saved = new HistoryCacheStore(output).Load();
            var actual = saved.Changes.Single(c => c.ChangeId == "1968671");
            Check(actual.Deltas.Single(d => d.Path == "depots/373301/name").OldValue == "Source 2 Dota 2 Content" && actual.Deltas.Single(d => d.Path == "depots/373301/name").NewValue == "Dota 2 Content", "actual 1968671 name preserves complete old and new strings");
            Check(actual.Deltas.Where(d => d.Path.EndsWith("/config/optionaldlc")).All(d => d.NewValue == "476580" && !d.OldExists), "actual optional DLC evidence retains original app IDs instead of boolean defaults");
            Check(new HistoryAnchorStore(output).Load().Single().Changes.Count == 3583 && saved.Changes.Count == 3583, "both cache formats contain the full validated continuation");
        }

        private static void DateContinuationChecks()
        {
            string oldest = Change("100", "10", "5", "2019-01-01T00:00:00Z", "2018-12-31T00:00:00Z", Delta("Depot 1 gid", "50", "100"), "");
            var query = new HistoryQuery { RequestedUtc = DateTimeOffset.Parse("2019-01-01T12:00:00Z") };
            var browser = new FakeBrowser(Baseline(), Newest() + Previous(), oldest);
            var result = Resolver(browser).ResolveAsync(query, CancellationToken.None, null).GetAwaiter().GetResult();
            Check(result.BuildId == "10" && browser.DateLoads == 1 && browser.Clicks == 0, "multi-day complete batch continues directly through overlapping date filter");
            var missing = new FakeBrowser(Baseline(), Newest() + Previous(), oldest) { MissingOverlap = true };
            Reject(() => Resolver(missing).ResolveAsync(query, CancellationToken.None, null).GetAwaiter().GetResult(), "direct date continuation still rejects a missing overlap boundary");
        }

        private static void CacheChecks()
        {
            string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "verified-public-history-v1.json");
            var query = new HistoryQuery { RequestedUtc = DateTimeOffset.Parse("2019-01-02T12:00:00Z") };
            var first = new FakeBrowser(Baseline(), Newest(), Previous()) { FailOnClick = true };
            Reject(() => new OnlineHistoryResolver(first, null, directory, false).ResolveAsync(query, CancellationToken.None, null).GetAwaiter().GetResult(), "browser error leaves verified cache prefix");
            HistoryCacheStore.CacheData saved = new HistoryCacheStore(directory).Load();
            Check(saved.Changes.Count == 1 && saved.Changes[0].ChangeId == "300" && saved.Baseline.PublishedAtUtc == DateTimeOffset.Parse("2019-01-03T00:00:00Z"), "cache UTC and explicit dictionary roundtrip");
            string json = File.ReadAllText(path);
            Check(!json.Contains("Html") && !json.Contains("<") && !json.Contains("Account") && json.Contains("2019-01-03T00:00:00.0000000+00:00"), "cache contains structured public fields only");
            var nextProcess = new FakeBrowser(Baseline(), Newest(), Previous());
            ResolvedHistoryPlan resumed = new OnlineHistoryResolver(nextProcess, null, directory, false).ResolveAsync(query, CancellationToken.None, null).GetAwaiter().GetResult();
            Check(resumed.BuildId == "20" && nextProcess.DateLoads == 1 && nextProcess.Clicks == 0, "new resolver resumes cache through overlapping date control");
            Check(new HistoryCacheStore(directory).Load().Changes.Count == 2, "completed event also preserved after validation");

            string corrupt = File.ReadAllText(path).Replace("\"NewValue\":\"300\"", "\"NewValue\":\"301\"");
            File.WriteAllText(path, corrupt);
            var corruptBrowser = new FakeBrowser(Baseline(), Newest(), Previous());
            var messages = new List<string>();
            new OnlineHistoryResolver(corruptBrowser, null, directory, false).ResolveAsync(query, CancellationToken.None, messages.Add).GetAwaiter().GetResult();
            Check(corruptBrowser.Clicks == 1 && corruptBrowser.DateLoads == 0 && messages.Any(m => m.Contains("缓存不能通过验证")), "cached deltas are revalidated before reuse");

            var changedBaseline = new FakeBrowser(Baseline().Replace(">30<", ">31<"), Newest().Replace("<ins>30</ins>", "<ins>31</ins>"), Previous());
            new OnlineHistoryResolver(changedBaseline, null, directory, false).ResolveAsync(query, CancellationToken.None, null).GetAwaiter().GetResult();
            Check(changedBaseline.Clicks == 1 && new HistoryCacheStore(directory).Load().Baseline.BuildId == "31", "changed live baseline invalidates persistent cache");

            File.WriteAllText(path, File.ReadAllText(path).Replace("\"SchemaVersion\":1", "\"SchemaVersion\":2"));
            var schemaBrowser = new FakeBrowser(Baseline(), Newest(), Previous());
            new OnlineHistoryResolver(schemaBrowser, null, directory, false).ResolveAsync(query, CancellationToken.None, null).GetAwaiter().GetResult();
            Check(schemaBrowser.Clicks == 1, "unknown cache schema discarded safely");

            string partial = Path.Combine(directory, "partial");
            var badOlder = new FakeBrowser(Baseline(), Newest(), Previous().Replace("<ins>200</ins>", "<ins>201</ins>"));
            Reject(() => new OnlineHistoryResolver(badOlder, null, partial, false).ResolveAsync(query, CancellationToken.None, null).GetAwaiter().GetResult(), "malformed new record rejected before caching");
            Check(new HistoryCacheStore(partial).Load().Changes.Count == 1, "only successfully validated prefix saved");

            string canceled = Path.Combine(directory, "canceled");
            using (var cancellation = new CancellationTokenSource())
            {
                var cancelBrowser = new FakeBrowser(Baseline(), Newest(), Previous()) { CancelOnClick = cancellation };
                bool wasCanceled = false;
                try { new OnlineHistoryResolver(cancelBrowser, null, canceled, false).ResolveAsync(query, cancellation.Token, null).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { wasCanceled = true; }
                Check(wasCanceled && new HistoryCacheStore(canceled).Load().Changes.Count == 1, "cancellation persists already verified records");
            }
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write)) stream.SetLength((long)HistoryCacheStore.MaximumBytes + 1);
            var oversized = new FakeBrowser(Baseline(), Newest(), Previous());
            new OnlineHistoryResolver(oversized, null, directory, false).ResolveAsync(query, CancellationToken.None, null).GetAwaiter().GetResult();
            Check(oversized.Clicks == 1 && new FileInfo(path).Length < HistoryCacheStore.MaximumBytes, "oversized cache rejected before deserialization");
        }

        private static void DateFirstIntegrationChecks()
        {
            string oldest = Change("100", "10", "5", "2019-01-01T00:00:00Z", "2018-12-31T00:00:00Z", "", "");
            string local = Newest() + Previous() + oldest;
            var query = new HistoryQuery { RequestedUtc = DateTimeOffset.Parse("2019-01-02T12:00:00Z") };
            var browser = new FakeBrowser(Baseline(), Newest(), Previous()) { DateHistoryOverride = local };
            var resolver = new OnlineHistoryResolver(browser, null, null); // Production default.
            DateFirstLocation notification = null;
            resolver.CandidateLocated += value => { Check(browser.Baselines == 0, "candidate notification precedes complete baseline reconstruction"); notification = value; };
            var result = resolver.ResolveAsync(query, CancellationToken.None, null).GetAwaiter().GetResult();
            Check(notification != null && notification.CandidateChange.ChangeId == "200" && browser.RequestedDates[0] == new DateTime(2019, 1, 3), "production default uses input date plus one calendar day");
            Check(result.Complete && result.BuildId == "20" && result.Depots.Single(d => d.DepotId == "2").ManifestId == "500", "date-first selection preserves unchanged depot through full state replay");
            Check(result.PatchSourceChangeId == "200" && !result.UsedObservationFallback && result.TimeSource == "Valve public/timeupdated", "complete plan records exact candidate identity and precise source");
            var boundaryBrowser = new FakeBrowser(Baseline(), Newest(), Previous()) { DateHistoryOverride = local };
            var boundary = new OnlineHistoryResolver(boundaryBrowser, null, null).ResolveAsync(new HistoryQuery { RequestedUtc = DateTimeOffset.Parse("2019-01-03T00:00:00Z") }, CancellationToken.None, null).GetAwaiter().GetResult();
            Check(boundary.BuildId == "20" && boundary.PatchSourceChangeId == "200", "date-first equality selects strictly earlier event");
            var omittedCloser = new FakeBrowser(Baseline(), Newest(), Previous()) { DateHistoryOverride = Previous() + oldest };
            Reject(() => new OnlineHistoryResolver(omittedCloser, null, null).ResolveAsync(new HistoryQuery { RequestedUtc = DateTimeOffset.Parse("2019-01-03T12:00:00Z") }, CancellationToken.None, null).GetAwaiter().GetResult(), "complete replay rejects date-cutoff candidate when a closer earlier event exists");

            var partialBrowser = new FakeBrowser(Baseline(), Newest(), Previous()) { DateHistoryOverride = local, FailOnClick = true };
            bool partialLocated = false;
            var partialResolver = new OnlineHistoryResolver(partialBrowser, null, null);
            partialResolver.CandidateLocated += value => partialLocated = true;
            Reject(() => partialResolver.ResolveAsync(query, CancellationToken.None, null).GetAwaiter().GetResult(), "located neighborhood cannot replace complete baseline and continuous history");
            Check(partialLocated, "partial candidate reported without complete plan on history failure");
            using (var cancellation = new CancellationTokenSource())
            {
                var cancelBrowser = new FakeBrowser(Baseline(), Newest(), Previous()) { DateHistoryOverride = local };
                var cancelResolver = new OnlineHistoryResolver(cancelBrowser, null, null);
                cancelResolver.CandidateLocated += value => cancellation.Cancel();
                bool canceled = false;
                try { cancelResolver.ResolveAsync(query, cancellation.Token, null).GetAwaiter().GetResult(); } catch (OperationCanceledException) { canceled = true; }
                Check(canceled && cancelBrowser.Baselines == 0, "cancel after candidate prevents baseline navigation");
            }
            var changedCandidate = new FakeBrowser(Baseline(), Newest(), Previous()) { DateHistoryOverride = local.Replace("<del>100</del>", "<del>101</del>") };
            Reject(() => new OnlineHistoryResolver(changedCandidate, null, null).ResolveAsync(query, CancellationToken.None, null).GetAwaiter().GetResult(), "same candidate ChangeId must have identical raw fields in location and full replay");

            string legacyNewest = Newest().Replace(Delta("timeupdated", "2019-01-02T00:00:00Z", "2019-01-03T00:00:00Z"), "");
            string legacyPrevious = Previous().Replace(Delta("timeupdated", "2019-01-01T00:00:00Z", "2019-01-02T00:00:00Z"), "");
            string legacyOldest = oldest.Replace(Delta("timeupdated", "2018-12-31T00:00:00Z", "2019-01-01T00:00:00Z"), "");
            string legacyLocal = legacyNewest + legacyPrevious + legacyOldest;
            string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "date-cache-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var legacyBrowser = new FakeBrowser(Baseline(), legacyNewest, legacyPrevious) { DateHistoryOverride = legacyLocal };
            DateFirstLocation legacyLocation = null;
            var legacyResolver = new OnlineHistoryResolver(legacyBrowser, null, directory);
            legacyResolver.CandidateLocated += value => legacyLocation = value;
            var legacyPlan = legacyResolver.ResolveAsync(query, CancellationToken.None, null).GetAwaiter().GetResult();
            Check(legacyPlan.Complete && legacyPlan.BuildId == "20" && legacyPlan.PublishedAtUtc == DateTimeOffset.Parse("2019-01-02T00:00:00Z"), "legacy Build and Manifest chain reconstructs without fabricated raw timestamps");
            Check(legacyPlan.UsedObservationFallback && legacyPlan.TimeSource == "SteamDB public branch change observed time" && legacyPlan.Assurance.Contains("不能视为 Valve 精确发布时间"), "legacy effective timestamp is explicitly labeled in completed plan");
            Check(legacyLocation.Publication.PublishedAtUtc == DateTimeOffset.MinValue && new HistoryCacheStore(directory).Load().Changes.All(c => c.Publication.PublishedAtUtc == DateTimeOffset.MinValue), "location and persisted records preserve raw missing time through ISO roundtrip");
            var cacheBrowser = new FakeBrowser(Baseline(), legacyNewest, legacyPrevious) { DateHistoryOverride = legacyLocal, FailOnClick = true };
            var restored = new OnlineHistoryResolver(cacheBrowser, null, directory).ResolveAsync(query, CancellationToken.None, null).GetAwaiter().GetResult();
            Check(restored.BuildId == "20" && cacheBrowser.Clicks == 0 && cacheBrowser.Baselines == 0, "legacy raw records pass normal cache validation without querying current baseline");
            var badBuild = new FakeBrowser(Baseline(), legacyNewest, legacyPrevious.Replace("<ins>20</ins>", "<ins>21</ins>")) { DateHistoryOverride = legacyLocal };
            Reject(() => new OnlineHistoryResolver(badBuild, null, null).ResolveAsync(query, CancellationToken.None, null).GetAwaiter().GetResult(), "missing time never relaxes Build continuity");
            var badManifest = new FakeBrowser(Baseline(), legacyNewest, legacyPrevious.Replace("<ins>200</ins>", "<ins>201</ins>")) { DateHistoryOverride = legacyLocal };
            Reject(() => new OnlineHistoryResolver(badManifest, null, null).ResolveAsync(query, CancellationToken.None, null).GetAwaiter().GetResult(), "missing time never relaxes Manifest continuity");
            var mixedBrowser = new FakeBrowser(Baseline(), legacyNewest, Previous()) { DateHistoryOverride = legacyNewest + Previous() + oldest };
            var mixedPlan = new OnlineHistoryResolver(mixedBrowser, null, null).ResolveAsync(query, CancellationToken.None, null).GetAwaiter().GetResult();
            Check(mixedPlan.TimeSource == "Valve public/timeupdated" && mixedPlan.UsedObservationFallback && mixedPlan.Assurance.Contains("观察时间"), "precise candidate labels fallback successor boundary");

            string prefixDirectory = Path.Combine(directory, "prefix");
            var prefixBrowser = new FakeBrowser(Baseline(), Newest(), Previous()) { FailOnClick = true };
            Reject(() => new OnlineHistoryResolver(prefixBrowser, null, prefixDirectory, false).ResolveAsync(query, CancellationToken.None, null).GetAwaiter().GetResult(), "prepare a verified prefix for neighborhood reuse");
            var connecting = new FakeBrowser(Baseline(), Newest(), Previous()) { DateHistoryOverride = local, FailOnClick = true };
            var connectedPlan = new OnlineHistoryResolver(connecting, null, prefixDirectory).ResolveAsync(query, CancellationToken.None, null).GetAwaiter().GetResult();
            Check(connectedPlan.BuildId == "20" && connecting.HistoryLoads == 1 && connecting.Clicks == 0 && connecting.DateLoads == 1 && connecting.Baselines == 0, "v1 migration and identical boundary reuse neighborhood without current baseline refetch");
            Check(new HistoryAnchorStore(prefixDirectory).Load().Single().Provenance == "ValidatedLegacyV1Migration", "v1 migration records explicit provenance");
            HistoryCacheStore.CacheData connectedCache = new HistoryCacheStore(prefixDirectory).Load();
            new HistoryCacheStore(prefixDirectory).Save(connectedCache.Baseline, connectedCache.Changes.Take(1).ToList());
            var shortLegacyBrowser = new FakeBrowser(Baseline(), Newest(), Previous()) { DateHistoryOverride = local, FailOnClick = true };
            new OnlineHistoryResolver(shortLegacyBrowser, null, prefixDirectory).ResolveAsync(query, CancellationToken.None, null).GetAwaiter().GetResult();
            Check(new HistoryAnchorStore(prefixDirectory).Load().Single().Changes.Count == 2 && shortLegacyBrowser.Baselines == 0 && shortLegacyBrowser.Clicks == 0, "shorter v1 prefix cannot truncate a longer verified v2 chain");
            var differentBoundary = new FakeBrowser(Baseline(), Newest(), Previous()) { DateHistoryOverride = local.Replace("<del>2000</del>", "<del>2001</del>") };
            Reject(() => new OnlineHistoryResolver(differentBoundary, null, prefixDirectory).ResolveAsync(query, CancellationToken.None, null).GetAwaiter().GetResult(), "mismatching overlapping neighborhood cannot bridge cache");

            var patchBrowser = new FakeBrowser(Baseline().Replace(">30<", ">20<"), Newest().Replace("<ins>30</ins>", "<ins>20</ins>"), Previous()) { DateHistoryOverride = local.Replace("<ins>30</ins>", "<ins>20</ins>"), PatchObservedEpoch = "1546646400" };
            var patchResolver = new OnlineHistoryResolver(patchBrowser, (patch, builds, token, progress) => { builds.Single().ChangeId = "200"; return Task.FromResult(new OfficialPatchMatch { Kind = "OfficialPatchSource", Build = builds.Single() }); }, null);
            var exact = patchResolver.ResolveAsync(new HistoryQuery { Patch = "7.22b" }, CancellationToken.None, null).GetAwaiter().GetResult();
            Check(exact.PatchSourceChangeId == "200" && exact.PublishedAtUtc == DateTimeOffset.Parse("2019-01-02T00:00:00Z") && patchBrowser.DateLoads == 1, "date-first exact official change skips later rollback of same Build");
            var windowBrowser = new FakeBrowser(Baseline(), Newest(), Previous()) { DateHistoryOverride = local };
            var windowResolver = new OnlineHistoryResolver(windowBrowser, (patch, builds, token, progress) => Task.FromResult(Window(builds.Single(), "2019-01-01T12:00:00Z", "2019-01-02T12:00:00Z")), null);
            var windowPlan = windowResolver.ResolveAsync(new HistoryQuery { Patch = "7.00" }, CancellationToken.None, null).GetAwaiter().GetResult();
            Check(windowPlan.BuildId == "20" && windowPlan.PatchWindowPublicEvents == 1, "official patch window retains strict-after locator independent of timestamp strict-before");

            var parser = new SteamDbHistoryParser();
            HistoryBaseline historical = parser.ParseBaseline(Page("https://steamdb.info/app/570/depots/?branch=public", Baseline()));
            historical.State.Reverse(parser.ParseHistory(Page("https://steamdb.info/app/570/history/", Newest())).Changes.Single());
            historical.BuildId = "20"; historical.PublishedAtUtc = DateTimeOffset.Parse("2019-01-02T00:00:00Z");
            string oldDirectory = Path.Combine(directory, "older-anchor");
            new HistoryCacheStore(oldDirectory).Save(historical, parser.ParseHistory(Page("https://steamdb.info/app/570/history/", Previous())).Changes);
            var uncoveredBrowser = new FakeBrowser(Baseline(), Newest(), Previous()) { DateHistoryOverride = Previous() + oldest };
            Reject(() => new OnlineHistoryResolver(uncoveredBrowser, null, oldDirectory).ResolveAsync(new HistoryQuery { RequestedUtc = DateTimeOffset.Parse("2019-01-03T12:00:00Z") }, CancellationToken.None, null).GetAwaiter().GetResult(), "old anchor below requested time cannot conceal closer public event");
            Check(uncoveredBrowser.Baselines == 1, "timestamp query outside anchor coverage reads current complete baseline");
            Check(new HistoryCacheStore(oldDirectory).Load().Baseline.BuildId == "20", "rejecting uncovered old anchor does not overwrite its evidence");
            AnchorRegressionChecks(prefixDirectory, local);
        }

        private static void AnchorRegressionChecks(string goodDirectory, string local)
        {
            string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "anchor-regression-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(directory);
            string valid = File.ReadAllText(Path.Combine(goodDirectory, "verified-public-anchors-v2.json"));
            File.WriteAllText(Path.Combine(directory, "verified-public-anchors-v2.json"), valid.Replace("PayloadSha256\":\"", "PayloadSha256\":\"0"));
            bool corruptRejected = false;
            try { new HistoryAnchorStore(directory).Load(); } catch (InvalidDataException) { corruptRejected = true; }
            Check(corruptRejected, "anchor payload checksum corruption rejected");
            var broken = new HistoryAnchorStore(goodDirectory).Load();
            broken[0].Changes[0].Deltas.Single(d => d.Path == "depots/1/manifests/public/gid").NewValue = "301";
            new HistoryAnchorStore(directory).Save(broken); // Valid checksum, invalid chain.
            var browser = new FakeBrowser(Baseline(), Newest(), Previous()) { DateHistoryOverride = local };
            var messages = new List<string>();
            var result = new OnlineHistoryResolver(browser, null, directory).ResolveAsync(new HistoryQuery { RequestedUtc = DateTimeOffset.Parse("2019-01-02T12:00:00Z") }, CancellationToken.None, messages.Add).GetAwaiter().GetResult();
            Check(result.BuildId == "20" && browser.Baselines == 2 && messages.Any(m => m.Contains("锚点不能通过")), "valid checksum cannot bypass full chain replay validation");

            string newerBaseline = Baseline().Replace(">30<", ">40<").Replace("1546473600", "1546560000").Replace("M:300'", "M:400'").Replace(">300</a>", ">400</a>");
            string newerChange = Change("400", "40", "30", "2019-01-04T00:00:00Z", "2019-01-03T00:00:00Z", Delta("Depot 1 gid", "300", "400"), "");
            string bridgeDirectory = Path.Combine(directory, "bridge");
            new HistoryAnchorStore(bridgeDirectory).Save(new HistoryAnchorStore(goodDirectory).Load());
            var bridgeBrowser = new FakeBrowser(newerBaseline, newerChange, Newest() + Previous()) { DateHistoryOverride = newerChange + local };
            var bridgeMessages = new List<string>();
            var bridgePlan = new OnlineHistoryResolver(bridgeBrowser, null, bridgeDirectory).ResolveAsync(new HistoryQuery { RequestedUtc = DateTimeOffset.Parse("2019-01-03T12:00:00Z") }, CancellationToken.None, bridgeMessages.Add).GetAwaiter().GetResult();
            Check(bridgePlan.BuildId == "30" && bridgePlan.Depots.Single(d => d.DepotId == "1").ManifestId == "300" && bridgeMessages.Any(m => m.Contains("完整 Depot 状态和 Build 链接")), "new complete baseline bridges old evidence only at matching full state and raw change");
            Check(new HistoryAnchorStore(bridgeDirectory).Load().Count == 2, "new baseline retains old verified historical root");

            string mismatchDirectory = Path.Combine(directory, "mismatch");
            var mismatch = new HistoryAnchorStore(goodDirectory).Load();
            mismatch[0].Baseline.State.Values["depots/2/manifests/public/gid"] = "501";
            new HistoryAnchorStore(mismatchDirectory).Save(mismatch);
            var mismatchBrowser = new FakeBrowser(newerBaseline, newerChange, Newest() + Previous()) { DateHistoryOverride = newerChange + local };
            Reject(() => new OnlineHistoryResolver(mismatchBrowser, null, mismatchDirectory).ResolveAsync(new HistoryQuery { RequestedUtc = DateTimeOffset.Parse("2019-01-03T12:00:00Z") }, CancellationToken.None, null).GetAwaiter().GetResult(), "matching changelist identity cannot bridge conflicting retained depot state");
        }

        private static OfficialPatchMatch Window(HistoryBuildEvent build, string before, string after)
        {
            DateTimeOffset lower = DateTimeOffset.Parse(before), upper = DateTimeOffset.Parse(after);
            return new OfficialPatchMatch { Build = build, Kind = "OfficialReleaseWindow", NotAvailableAtUtc = lower, AvailableByUtc = upper,
                WindowEvidence = new List<OfficialReleaseStatement> {
                    new OfficialReleaseStatement { Patch = "7.00", State = "NotYetPublic", AnnouncedAtUtc = lower, SourceUrl = "https://steamcommunity.com/games/dota2/announcements/detail/1" },
                    new OfficialReleaseStatement { Patch = "7.00", State = "AvailablePublic", AnnouncedAtUtc = upper, SourceUrl = "https://steamcommunity.com/games/dota2/announcements/detail/2" } } };
        }

        private sealed class FakeBrowser : IHistoryBrowser, IHistoryDateBrowser
        {
            private string baseline, newest, previous;
            public int Clicks, Baselines, DateLoads, HistoryLoads;
            public bool Drift, UseDateFilter, MissingOverlap, AlterOverlap, ClickReturnsSame, DateReturnsSame;
            public bool FailOnClick;
            public CancellationTokenSource CancelOnClick;
            public string PatchObservedEpoch = "1546387205";
            public string DateHistoryOverride;
            public List<DateTime> RequestedDates = new List<DateTime>();
            public FakeBrowser(string baseline, string newest, string previous) { this.baseline = baseline; this.newest = newest; this.previous = previous; }
            public Task<HistoryPageSnapshot> NavigateAsync(string url, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                if (url.Contains("/depots/")) { Baselines++; return Task.FromResult(Page(url, Drift && Baselines > 1 ? baseline.Replace(">30<", ">31<") : baseline)); }
                if (url.Contains("/patchnotes/")) return Task.FromResult(Page(url, "<table><tr data-date='" + PatchObservedEpoch + "'><td><a href='/patchnotes/20/' class='b'>7.22b (ClientVersion 3523)</a></td></tr></table>"));
                HistoryLoads++;
                return Task.FromResult(Page(url, newest + (UseDateFilter ? "<div hidden>" : "<div>") + "<button id='js-history-load'>Load older history</button></div>"));
            }
            public Task<HistoryPageSnapshot> ClickAsync(string selector, CancellationToken token) { Clicks++; if (CancelOnClick != null) { CancelOnClick.Cancel(); token.ThrowIfCancellationRequested(); } if (FailOnClick) throw new HistoryResolutionException("Simulated browser failure."); return Task.FromResult(Page("https://steamdb.info/app/570/history/?filterkey=2", ClickReturnsSame ? newest + "<button id='js-history-load'>Load older history</button>" : newest + previous + "<p>No more history</p>")); }
            public Task<HistoryPageSnapshot> CaptureAsync(CancellationToken token) { throw new NotSupportedException(); }
            public Task<HistoryPageSnapshot> LoadHistoryBeforeAsync(DateTime date, CancellationToken token)
            {
                DateLoads++;
                RequestedDates.Add(date);
                if (DateHistoryOverride != null) return Task.FromResult(Page("https://steamdb.info/app/570/history/?filterkey=2", DateHistoryOverride + "<p>No more history</p>"));
                string overlap = MissingOverlap ? "" : AlterOverlap ? newest.Replace("<del>200</del>", "<del>201</del>") : newest;
                return Task.FromResult(Page("https://steamdb.info/app/570/history/?filterkey=2", overlap + (DateReturnsSame ? "" : previous) + "<p>No more history</p>"));
            }
        }
    }
}
