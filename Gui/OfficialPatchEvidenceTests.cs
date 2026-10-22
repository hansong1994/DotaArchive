using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace DotaArchive
{
    internal static class OfficialPatchEvidenceTests
    {
        private static int checks;
        private static void Check(bool value, string message) { if(!value) throw new Exception("FAILED: " + message); checks++; }
        private static void Reject(Action action, string message) { try { action(); } catch(HistoryResolutionException) { checks++; return; } throw new Exception("Expected rejection: " + message); }
        private static HistoryBuildEvent Build(string id, DateTimeOffset observed, string title) { return new HistoryBuildEvent { BuildId = id, PublishedAtUtc = observed, Title = title, SourceUrl = "https://steamdb.info/patchnotes/" + id + "/" }; }
        private static HistoryPageSnapshot Page(string id, string body) { return new HistoryPageSnapshot { Url = "https://steamdb.info/patchnotes/" + id + "/", Html = "<article>" + body + "</article>" }; }

        public static int Main(string[] args)
        {
            try {
                string patches = File.ReadAllText(args[0]), news = File.ReadAllText(args[1]);
                var older = OfficialPatchResolver.ParsePatchList(patches, "7.00");
                Check(older.Count == 0, "actual official list starts after 7.00; no invented list entry");
                var modern = OfficialPatchResolver.ParsePatchList(patches, "7.23");
                Check(modern.Count == 1 && modern[0].PatchUrls.Contains("dota2.com/outlanders"), "official named update mapped from feed");
                Check(OfficialPatchResolver.ParsePatchList(patches, "7.22").Count == 1, "exact patch major");
                Check(OfficialPatchResolver.ParsePatchList(patches, "7.22b").Count == 1, "exact patch suffix");
                var actualNews = OfficialPatchResolver.ParseNews(news, "7.00", OfficialPatchResolver.NewsUrl);
                Check(actualNews.ItemCount >= 700, "real complete official news fixture");
                Check(actualNews.Matches.Count == 2, "duplicated official releases found while countdown excluded");
                Check(actualNews.Matches.All(a => a.PatchUrls.Contains("dota2.com/700")), "actual announcement resolves official patch URL");
                Check(actualNews.Matches.All(a => !a.Title.Contains("Countdown")), "preannouncement excluded");
                Check(actualNews.Matches.All(a => a.NewsIds.Count == 0 && a.Sources.Any(s => s.Contains("/news/externalpost/steam_community_announcements/"))), "API news gid never invented as a Steam community announcement identifier");
                Check(actualNews.Statements.Count(s => s.State == "NotYetPublic") == 2 && actualNews.Statements.Count(s => s.State == "AvailablePublic") == 2, "real explicit before-after public statements preserved");
                Check(OfficialPatchResolver.CanonicalDotaUrl("http://www.dota2.com/700/?utm_source=steam#changes") == "dota2.com/700", "old http official alias canonicalized");
                Check(OfficialPatchResolver.CanonicalDotaUrl("https://dota2.com.evil.example/700/") == null, "lookalike domain rejected");
                Check(OfficialPatchResolver.CanonicalDotaUrl("https://evil@www.dota2.com/700/") == null, "userinfo link rejected");
                Check(OfficialPatchResolver.CanonicalDotaUrl("https://www.dota2.com/login/") == null, "login link rejected");
                Check(OfficialPatchResolver.OfficialNewsId("https://steamcommunity.com/games/dota2/announcements/detail/91592690506413557") == "91592690506413557", "legacy announcement ID recognized without double precision");
                Check(OfficialPatchResolver.OfficialNewsId("https://store.steampowered.com/news/app/730/view/91592690506413557") == null, "foreign app article rejected");
                Check(OfficialPatchResolver.CanonicalLegacyUpdateUrl("http://store.steampowered.com/news/14675/") == "https://store.steampowered.com/news/14675", "legacy official product update link retained separately from community IDs");
                Check(OfficialPatchResolver.CanonicalLegacyUpdateUrl("https://store.steampowered.com.evil/news/14675/") == null, "legacy product update lookalike host rejected");
                Check(!OfficialPatchResolver.ContainsPatch("7.22b Update", "7.22"), "patch suffix boundary");
                Check(!OfficialPatchResolver.ContainsPatch("17.00 Update", "7.00"), "major number boundary");
                Check(OfficialPatchResolver.ContainsPatch("7.00 Update - The New Journey", "7.00"), "exact version token");
                DateTimeOffset date = actualNews.Matches[0].AnnouncedAtUtc;
                Check(OfficialPatchResolver.ExtractReleaseStatements("7.00", "7.00 Update", "7.00 Update is now available to everyone on the test client.", date, "1", "fixture").Count == 0, "test availability is not public release");
                Check(OfficialPatchResolver.ExtractReleaseStatements("7.00", "7.00 Update", "7.00 Update will be now available to everyone tomorrow.", date, "1", "fixture").Count == 0, "future availability is not released");
                Check(OfficialPatchResolver.ExtractReleaseStatements("7.00", "7.00 Anniversary", "7.00 Update is now available to everyone.", date, "1", "fixture").Count == 0, "retrospective not reused as release");
                Check(OfficialPatchResolver.ExtractReleaseStatements("7.00", "7.00 hotfix", "7.00 Update is now available to everyone.", date, "1", "fixture").Count == 0, "followup cannot establish a version's first availability");
                Check(OfficialPatchResolver.ExtractReleaseStatements("7.00", "7.00 Update", "7.00 Update will go live to the world in the beta realm.", date, "1", "fixture").Count == 0, "beta scope rejected even with world wording");
                Check(OfficialPatchResolver.ExtractReleaseStatements("7.00", "7.01 Update", "7.00 Update will go live to the world.", date, "1", "fixture").Count == 0, "both statements must belong to exact patch title");
                foreach(string misleading in new[] {
                    "The 7.00 update notes are ready. The new terrain is now available to everyone.",
                    "7.00 is not now available to everyone.",
                    "A preview of 7.00 is now available to everyone on Dota 2 Test.",
                    "Last year, we announced that 7.00 will go live to the world.",
                    "If 7.00 is now available to everyone, please update.",
                    "We said that 7.00 will go live to the world.",
                    "7.00 Update will go live to the world only on the beta realm.",
                    "The story said: \"A preview. 7.00 is now available to everyone.\"",
                    "7.00 Update is now available to everyone in early access.",
                    "7.00a Update is now available to everyone.",
                    "We recall when 7.00 was now available to everyone."
                }) Check(OfficialPatchResolver.ExtractReleaseStatements("7.00", "7.00 Update", misleading, date, "1", "fixture").Count == 0, "misleading public release statement rejected: " + misleading);
                Check(OfficialPatchResolver.ExtractReleaseStatements("7.00", "7.00 Update", "The 7.00 Update is now available to everyone.", date, "1", "fixture").Single().State == "AvailablePublic", "direct exact patch affirmative release supported");
                Check(OfficialPatchResolver.ExtractReleaseStatements("7.00", "7.00 Update - The New Journey Begins", "The unrelated Special Terrain Update, now available to everyone.", date, "1", "fixture").Count == 0, "named subject must be derived from same patch title");
                var candidate = Build("123", date.AddMinutes(-20), "The New Journey");
                Check(OfficialPatchResolver.MatchPatchPage(Page("123", "<h1>7.00 Update</h1><a href='https://www.dota2.com/700/'>The New Journey</a>"), candidate, "7.00", actualNews.Matches).Count == 2, "official patch body links associate original duplicate anchors");
                Check(OfficialPatchResolver.MatchPatchPage(Page("123", "<h1>7.22b (ClientVersion 3523)</h1><a href='https://www.dota2.com/patches/7.22b'>Update notes</a>"), Build("123", OfficialPatchResolver.ParsePatchList(patches, "7.22b")[0].AnnouncedAtUtc, "7.22b"), "7.22b", OfficialPatchResolver.ParsePatchList(patches, "7.22b")).Count == 1, "pure numeric ClientVersion heading suffix does not hide precise patch identity");
                Check(OfficialPatchResolver.MatchPatchPage(Page("123", "<h1>7.00 Update</h1>"), candidate, "7.00", actualNews.Matches).Count == 0, "date and number alone do not prove Build");
                Check(OfficialPatchResolver.MatchPatchPage(Page("123", "<a href='https://www.dota2.com/700/'>New Journey</a><p>7.00a hotfix</p>"), candidate, "7.00", actualNews.Matches).Count == 0, "same website on hotfix not conflated");
                Check(OfficialPatchResolver.MatchPatchPage(Page("123", "<aside><h1>7.00 Update</h1><a href='https://www.dota2.com/700/'>Related update</a></aside><p>Actual unrelated changes</p>"), candidate, "7.00", actualNews.Matches).Count == 0, "sidebar suggestions not evidence");
                Check(OfficialPatchResolver.MatchPatchPage(Page("123", "<blockquote><h1>7.00 Update</h1><a href='https://www.dota2.com/700/'>Quoted old release</a></blockquote><p>Unrelated update</p>"), candidate, "7.00", actualNews.Matches).Count == 0, "quoted release heading not this build's release identity");
                Check(OfficialPatchResolver.MatchPatchPage(new HistoryPageSnapshot { Url = candidate.SourceUrl, Html = "<main><h1>7.00 Update</h1><a href='https://www.dota2.com/700/'>Related update</a></main>" }, candidate, "7.00", actualNews.Matches).Count == 0, "unscoped main body not assumed to be an article");
                Check(OfficialPatchResolver.MatchPatchPage(Page("123", "<h1>Client hotfix</h1><p>Fixes a crash in <a href='https://www.dota2.com/700/'>7.00</a> for all players.</p>"), candidate, "7.00", actualNews.Matches).Count == 0, "hotfix citation of release URL not a release association");
                Check(OfficialPatchResolver.MatchPatchPage(Page("123", "<h1>Client hotfix</h1><p>Fixes <a href='https://steamcommunity.com/games/dota2/announcements/detail/91592690506413557'>7.00</a> crashes.</p>"), candidate, "7.00", actualNews.Matches).Count == 0, "hotfix citation of exact release news ID not a release association");
                var smallAnchors = OfficialPatchResolver.ParsePatchList(patches, "7.22b");
                var smallBuild = Build("123", smallAnchors[0].AnnouncedAtUtc, "7.22b (ClientVersion 3523)");
                var sourcePage = SourceBarPage(smallBuild, "7.22b", "789");
                Check(OfficialPatchResolver.MatchOfficialSourceBar(sourcePage, smallBuild, "7.22b", smallAnchors).Count == 1 && smallBuild.ChangeId == "789", "observed sourcebar layout binds official patch and exact history change without an article wrapper");
                Check(OfficialPatchResolver.MatchOfficialSourceBar(new HistoryPageSnapshot { Url = sourcePage.Url, Html = sourcePage.Html.Replace("patchnotes-official-url", "related-update") }, smallBuild, "7.22b", smallAnchors).Count == 0, "ordinary related link cannot replace sourcebar marker");
                Check(OfficialPatchResolver.MatchOfficialSourceBar(new HistoryPageSnapshot { Url = sourcePage.Url, Html = sourcePage.Html.Replace("Build 123", "Build 124") }, smallBuild, "7.22b", smallAnchors).Count == 0, "sourcebar metadata belongs to exact Build");
                Check(OfficialPatchResolver.MatchOfficialSourceBar(new HistoryPageSnapshot { Url = sourcePage.Url, Html = sourcePage.Html.Replace("/app/570/", "/app/730/") }, smallBuild, "7.22b", smallAnchors).Count == 0, "sourcebar foreign history link rejected");
                Check(OfficialPatchResolver.MatchOfficialSourceBar(new HistoryPageSnapshot { Url = sourcePage.Url, Html = "<aside>" + sourcePage.Html + "</aside>" }, smallBuild, "7.22b", smallAnchors).Count == 0, "sourcebar imitation in sidebar rejected");
                Check(OfficialPatchResolver.MatchOfficialSourceBar(new HistoryPageSnapshot { Url = sourcePage.Url, Html = sourcePage.Html.Replace("patches/7.22b", "patches/7.22c") }, smallBuild, "7.22b", smallAnchors).Count == 0, "sourcebar must link same exact official patch");
                Check(OfficialPatchResolver.MatchOfficialSourceBar(new HistoryPageSnapshot { Url = sourcePage.Url, Html = sourcePage.Html.Replace("7.22b (ClientVersion 3523)", "7.22b Beta") }, smallBuild, "7.22b", smallAnchors).Count == 0, "sourcebar beta title not accepted as public patch identity");
                Check(OfficialPatchResolver.MatchOfficialSourceBar(new HistoryPageSnapshot { Url = sourcePage.Url, Html = sourcePage.Html.Replace("7.22b (ClientVersion 3523)", "7.22b Test Build") }, smallBuild, "7.22b", smallAnchors).Count == 0, "sourcebar test title not accepted as public patch identity");
                Reject(delegate { OfficialPatchResolver.MatchOfficialSourceBar(SourceBarPage(smallBuild, "7.22b", "790"), smallBuild, "7.22b", smallAnchors); }, "contradictory source history identity rejected");
                Reject(delegate { OfficialPatchResolver.MatchPatchPage(Page("999", "7.00"), candidate, "7.00", actualNews.Matches); }, "wrong Build page rejected");
                Reject(delegate { OfficialPatchResolver.ParseNews(news.Replace("\"appid\":570", "\"appid\":730"), "7.00", OfficialPatchResolver.NewsUrl); }, "foreign API app rejected");

                string eventHtml = EventHtml(actualNews.Matches[0], 123, "public", 570, 1, 0, null);
                var eventMatches = OfficialPatchResolver.ParseOfficialEventBuilds(eventHtml, "https://steamcommunity.com/games/dota2/announcements/detail/1", "7.00", actualNews.Matches[0], new[] { candidate });
                Check(eventMatches.Count == 1 && Object.ReferenceEquals(eventMatches[0].Build, candidate) && eventMatches[0].Kind == "OfficialEventBuild", "official explicit public Build field cross checked against original list");
                Check(OfficialPatchResolver.ParseOfficialEventBuilds(EventHtml(actualNews.Matches[0], 0, "", 570, 1, 0, null), "fixture", "7.00", actualNews.Matches[0], new[] { candidate }).Count == 0, "zero build metadata not invented");
                var linkedBuild = Build("123", date.AddMinutes(-20), actualNews.Matches[0].Title);
                var sourceLinked = OfficialPatchResolver.ParseOfficialEventBuilds(EventHtml(actualNews.Matches[0], 0, "", 570, 1, 0, null), "fixture", "7.00", actualNews.Matches[0], new[] { linkedBuild }, "987");
                Check(sourceLinked.Count == 1 && sourceLinked[0].Kind == "OfficialPatchSource" && linkedBuild.ChangeId == "987", "verified exact sourcebar may identify official announcement without inventing missing event Build field");
                Check(OfficialPatchResolver.ParseOfficialEventBuilds(EventHtml(actualNews.Matches[0], 0, "beta", 570, 1, 0, null), "fixture", "7.00", actualNews.Matches[0], new[] { linkedBuild }, "987").Count == 0, "source identity does not override explicit beta scope");
                Check(OfficialPatchResolver.ParseOfficialEventBuilds(EventHtml(actualNews.Matches[0], 123, "beta", 570, 1, 0, null), "fixture", "7.00", actualNews.Matches[0], new[] { candidate }).Count == 0, "non public explicit Build rejected");
                Check(OfficialPatchResolver.ParseOfficialEventBuilds(EventHtml(actualNews.Matches[0], 123, "public", 730, 1, 0, null), "fixture", "7.00", actualNews.Matches[0], new[] { candidate }).Count == 0, "foreign app explicit Build rejected");
                Check(OfficialPatchResolver.ParseOfficialEventBuilds(EventHtml(actualNews.Matches[0], 123, "public", 570, 0, 0, null), "fixture", "7.00", actualNews.Matches[0], new[] { candidate }).Count == 0, "unpublished event rejected");
                Check(OfficialPatchResolver.ParseOfficialEventBuilds(EventHtml(actualNews.Matches[0], 123, "public", 570, 1, 1, null), "fixture", "7.00", actualNews.Matches[0], new[] { candidate }).Count == 0, "hidden event rejected");
                Check(OfficialPatchResolver.ParseOfficialEventBuilds(EventHtml(actualNews.Matches[0], 123, "public", 570, 1, 0, "7.00a Update"), "fixture", "7.00", actualNews.Matches[0], new[] { candidate }).Count == 0, "event patch title must match exact official release");
                var followupAnchor = new OfficialPatchResolver.PatchAnchor { Title = "7.00 hotfix", AnnouncedAtUtc = date, PlainText = "Bug fixes" };
                followupAnchor.PatchUrls.Add("dota2.com/700");
                Check(OfficialPatchResolver.ParseOfficialEventBuilds(EventHtml(followupAnchor, 123, "public", 570, 1, 0, null), "fixture", "7.00", followupAnchor, new[] { candidate }).Count == 0, "explicit hotfix Build metadata is not first release evidence");
                Check(OfficialPatchResolver.MatchPatchPage(Page("123", "<h1>7.00 hotfix</h1><a href='https://www.dota2.com/700/'>7.00 Update</a>"), candidate, "7.00", new[] { followupAnchor }).Count == 0, "official hotfix heading remains followup not first release");
                Reject(delegate { OfficialPatchResolver.ParseOfficialEventBuilds(eventHtml, "fixture", "7.00", actualNews.Matches[0], new[] { Build("124", date, "Unknown") }); }, "official Build must exist in supplied SteamDB list");
                if(args.Length > 3) Check(OfficialPatchResolver.ParseOfficialEventBuilds(File.ReadAllText(args[3]), "https://steamcommunity.com/ogg/570/announcements/detail/528441854366209676", "7.00", actualNews.Matches[0], new[] { candidate }).Count == 0, "real migrated 7.00 official event has no explicit Build and remains unknown");
                if(args.Length > 4) {
                    string productUpdates = File.ReadAllText(args[4]);
                    var productPatch = OfficialPatchResolver.ParseNews(productUpdates, "6.82c", OfficialPatchResolver.UpdatesUrl);
                    Check(productPatch.Matches.Count == 1 && productPatch.Matches[0].NewsUrls.Contains("https://store.steampowered.com/news/14675"), "real steam_updates official feed provides old patch release article");
                    Check(productPatch.Matches[0].NewsIds.Count == 0, "legacy product news GID never used as community event ID");
                    foreach(string samplePatch in new[] { "7.22b", "7.22c", "7.29" }) Check(OfficialPatchResolver.ParsePatchList(patches, samplePatch).Count == 1, "official patch list coverage for " + samplePatch);
                }

                var fake = new FakeBrowser(new Dictionary<string, string> {
                    { "123", "<h1>7.00 Update - The New Journey Begins</h1><p>Visit the <a href='http://www.dota2.com/700/'>7.00 Update</a>.</p>" },
                    { "124", "<h1>Small maintenance</h1><p>Unrelated bug fixes</p>" }
                });
                string noPreviewNews = news.Replace("7.00 Update Countdown", "A Different Update Countdown");
                Func<string, CancellationToken, Task<string>> fetch = delegate(string url, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(url.StartsWith(OfficialPatchResolver.PatchListUrl, StringComparison.Ordinal) ? patches : noPreviewNews); };
                var resolver = new OfficialPatchResolver(fake, fetch);
                var match = resolver.ResolveAsync("7.00", new[] { candidate, Build("124", date.AddHours(1), "Update") }, CancellationToken.None, null).GetAwaiter().GetResult();
                Check(Object.ReferenceEquals(match.Build, candidate), "fallback returns original Build object");
                Check(match.Build.PublishedAtUtc == date.AddMinutes(-20), "announcement date never replaces observed Build date");
                Check(match.Sources.Any(s => s.Contains("91592690506413557")) && match.Sources.Contains(OfficialPatchResolver.NewsUrl), "official provenance retained and duplicate IDs merged");
                Check(fake.Visits.Count == 2, "all nearby candidates checked for ambiguity");
                fake.Content["124"] = "<h1>7.00 Update</h1><a href='https://www.dota2.com/700/'>New Journey</a>";
                Reject(delegate { resolver.ResolveAsync("7.00", new[] { candidate, Build("124", date.AddHours(1), "Update") }, CancellationToken.None, null).GetAwaiter().GetResult(); }, "multiple associated builds rejected");
                Reject(delegate { resolver.ResolveAsync("7.00", new[] { Build("123", date.AddDays(20), "New Journey") }, CancellationToken.None, null).GetAwaiter().GetResult(); }, "out of window not guessed");
                using(var cancelled = new CancellationTokenSource()) { cancelled.Cancel(); bool stopped = false; try { resolver.ResolveAsync("7.00", new[] { candidate }, cancelled.Token, null).GetAwaiter().GetResult(); } catch(OperationCanceledException) { stopped = true; } Check(stopped, "cancel propagated"); }
                var window = OfficialPatchResolver.MatchReleaseWindow("7.00", new[] { candidate }, actualNews.Statements);
                Check(window != null && window.Kind == "OfficialReleaseWindow" && window.WindowEvidence.Count == 2, "explicit official window produces constrained candidate only");
                Check(window.NotAvailableAtUtc < candidate.PublishedAtUtc && window.AvailableByUtc == date, "both announcement bounds retained independently of build date");
                Check(OfficialPatchResolver.MatchReleaseWindow("7.00", new[] { candidate, Build("124", date.AddMinutes(-5), "Update") }, actualNews.Statements) == null, "multiple publications inside window cannot select");
                Check(OfficialPatchResolver.MatchReleaseWindow("7.00", new[] { candidate, Build("123", date.AddMinutes(-5), "Rollback") }, actualNews.Statements) == null, "same Build rollback counts as another occurrence");
                Check(OfficialPatchResolver.MatchReleaseWindow("7.01", new[] { candidate }, actualNews.Statements) == null, "different patch window never reused");
                if(args.Length > 2) {
                    var liveList = new SteamDbHistoryParser().ParsePatchListing(new HistoryPageSnapshot { Url = "https://steamdb.info/app/570/patchnotes/", Html = File.ReadAllText(args[2]) });
                    var realWindow = OfficialPatchResolver.MatchReleaseWindow("7.00", liveList.Builds, actualNews.Statements);
                    Check(realWindow != null && realWindow.Build.BuildId == "1514733", "real complete SteamDB list and real official statements associate sole candidate without production hardcoding");
                    Check(realWindow.Build.PublishedAtUtc != realWindow.AvailableByUtc, "real announcement time not conflated with tracker observation");
                    if(args.Length > 5) {
                        var official729 = OfficialPatchResolver.ParseNews(news, "7.29", OfficialPatchResolver.NewsUrl).Matches.Single();
                        var build729 = liveList.Builds.Single(b => OfficialPatchResolver.ContainsPatch(b.Title, "7.29"));
                        Check(build729.Title.Contains(official729.Title), "actual patch list row contains the official release headline");
                        string realEventHtml = "<div data-partnereventstore='" + WebUtility.HtmlEncode(File.ReadAllText(args[5])) + "'></div>";
                        Check(OfficialPatchResolver.ParseOfficialEventBuilds(realEventHtml, "https://steamcommunity.com/ogg/570/announcements/detail/3086629758333021155", "7.29", official729, new[] { build729 }).Count == 0, "real 7.29 event also has no explicit Build field");
                        Check(OfficialPatchResolver.ParseOfficialEventBuilds(realEventHtml, "https://steamcommunity.com/ogg/570/announcements/detail/3086629758333021155", "7.29", official729, new[] { build729 }, "111").Single().Kind == "OfficialPatchSource", "real 7.29 metadata matches API announcement_body.posttime even though event start differs by 55 seconds");
                        string products = File.ReadAllText(args[4]);
                        Func<string, CancellationToken, Task<string>> cachedFetch = delegate(string url, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(url == OfficialPatchResolver.PatchListUrl ? patches : url == OfficialPatchResolver.NewsUrl ? news : url == OfficialPatchResolver.UpdatesUrl ? products : realEventHtml); };
                        var fullWindow = new OfficialPatchResolver(new FakeBrowser(new Dictionary<string, string>()), cachedFetch).ResolveAsync("7.00", liveList.Builds, CancellationToken.None, null).GetAwaiter().GetResult();
                        Check(fullWindow.Kind == "OfficialReleaseWindow" && fullWindow.Build.BuildId == "1514733", "full helper uses live-source official 7.00 window without browsing an invented index");
                        // Header markup reconstructed from the parent's observed live
                        // IAB DOM. It is intentionally not represented as a raw capture.
                        foreach(string[] sample in new[] { new[] { "7.22b", "3863157", "6334868", "7.22b (ClientVersion 3523)" }, new[] { "7.22c", "3903210", "6398630", "7.22c (ClientVersion 3555)" }, new[] { "7.29", "6517216", "111", official729.Title } }) {
                            var actualBuild = liveList.Builds.Single(b => b.BuildId == sample[1]);
                            actualBuild.ChangeId = null;
                            var headerBuild = Build(actualBuild.BuildId, actualBuild.PublishedAtUtc, sample[3]);
                            string header = SourceBarPage(headerBuild, sample[0], sample[2]).Html;
                            if(sample[0] == "7.29") header = header.Replace("http://www.dota2.com/patches/7.29", "https://steamcommunity.com/ogg/570/announcements/detail/3086629758333021155");
                            var pages = liveList.Builds.GroupBy(b => b.BuildId).ToDictionary(g => g.Key, g => "<p>No official patch notes in this unrelated update.</p>");
                            pages[actualBuild.BuildId] = header;
                            var fullMatch = new OfficialPatchResolver(new FakeBrowser(pages), cachedFetch).ResolveAsync(sample[0], liveList.Builds, CancellationToken.None, null).GetAwaiter().GetResult();
                            Check(fullMatch.Kind == "OfficialPatchSource" && fullMatch.Build.BuildId == sample[1] && fullMatch.Build.ChangeId == sample[2], "full sourcebar+official API route selects " + sample[0] + " using original list Build and parsed ChangeId");
                        }
                    }
                }
                Console.WriteLine("PASS " + checks + " official patch evidence assertions (real Valve API and SteamDB list fixtures; synthetic article and adversarial cases).");
                return 0;
            } catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
        private static string EventHtml(OfficialPatchResolver.PatchAnchor anchor, long build, string branch, long app, int published, int hidden, string title)
        {
            string name = title ?? anchor.Title;
            var value = new Dictionary<string, object> { { "event_name", name }, { "appid", app }, { "rtime32_start_time", (long)(anchor.AnnouncedAtUtc - new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero)).TotalSeconds - 55 }, { "build_id", build }, { "build_branch", branch }, { "published", published }, { "hidden", hidden }, { "announcement_body", new Dictionary<string, object> { { "headline", name }, { "gid", "different-migrated-id" }, { "posttime", (long)(anchor.AnnouncedAtUtc - new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero)).TotalSeconds } } } };
            return "<div data-partnereventstore='" + WebUtility.HtmlEncode(new JavaScriptSerializer().Serialize(new[] { value })) + "'></div>";
        }
        private static HistoryPageSnapshot SourceBarPage(HistoryBuildEvent build, string patch, string changeId)
        {
            return new HistoryPageSnapshot { Url = build.SourceUrl, Html = "<div class='patchnotes-header'><div class='container patchnotes-container-logo'><div><h1 class='header-title' itemprop='headline'>" + WebUtility.HtmlEncode(build.Title) + "</h1></div></div><div class='container'><div class='patchnotes-metadata'><a href='/app/570/history/?changeid=" + changeId + "' rel='nofollow'>Build " + build.BuildId + "</a></div><div class='patchnotes-official-url hint-with-octicon'><span>Update notes via <a href='http://www.dota2.com/patches/" + patch + "'>Dota 2 Blog</a></span></div></div></div>" };
        }
        private sealed class FakeBrowser : IHistoryBrowser
        {
            internal Dictionary<string, string> Content;
            internal List<string> Visits = new List<string>();
            internal FakeBrowser(Dictionary<string, string> content) { Content = content; }
            public Task<HistoryPageSnapshot> NavigateAsync(string url, CancellationToken token) { token.ThrowIfCancellationRequested(); string id = new Uri(url).AbsolutePath.Split('/')[2]; Visits.Add(id); return Task.FromResult(Page(id, Content[id])); }
            public Task<HistoryPageSnapshot> CaptureAsync(CancellationToken token) { throw new NotSupportedException(); }
            public Task<HistoryPageSnapshot> ClickAsync(string selector, CancellationToken token) { throw new NotSupportedException(); }
        }
    }
}
