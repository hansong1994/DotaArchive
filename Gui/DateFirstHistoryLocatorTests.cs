using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DotaArchive
{
    internal static class DateFirstHistoryLocatorTests
    {
        private static int checks;
        private const string End = "<p>No more history</p>";
        private const string More = "<button id='js-history-load'>Load older history</button>";
        private static readonly DateTimeOffset Clock = At("2019-06-12T00:00:00Z");
        private static readonly DateTimeOffset Input = At("2019-06-06T12:00:00+08:00");
        private static string A { get { return Event("100", "2019-06-06T04:00:10Z", "10", "9", "2019-06-06T04:00:00Z", "2019-06-05T00:00:00Z"); } }
        private static string B { get { return Event("200", "2019-06-06T04:05:10Z", "20", "10", "2019-06-06T04:05:00Z", "2019-06-06T04:00:00Z"); } }
        private static string C { get { return Event("300", "2019-06-06T04:10:10Z", "30", "20", "2019-06-06T04:10:00Z", "2019-06-06T04:05:00Z"); } }

        public static int Main()
        {
            var basic = Fake("2019-06-07", C + B + A + End);
            DateFirstLocation result = Locate(basic, Input, false);
            Check(result.Publication.BuildId == "20" && result.CandidateChange.ChangeId == "200", "earliest strictly later public event");
            Check(result.EffectiveTime == At("2019-06-06T04:05:00Z") && result.HasPrecisePublicTime, "uses original branch time");
            Check(result.PreviousPublicChange.ChangeId == "100", "actual predecessor returned");
            Check(result.RequestedDates.Single() == new DateTime(2019, 6, 7) && result.RequestedDates[0].Kind == DateTimeKind.Unspecified, "Beijing calendar plus one without invented UTC boundary");
            Check(basic.Navigations == 1 && basic.Clicks == 0 && basic.Dates.Count == 1, "does not replay current-day initial navigation");
            Check(!result.UsedObservationFallback && result.TimeSource == "Valve public/timeupdated", "precise source is explicit");
            Check(result.ObservedFromUtc == At("2019-06-06T04:00:10Z") && result.ObservedThroughUtc == At("2019-06-06T04:10:10Z"), "actual observed range preserved");
            Check(result.Publication.PublishedAtUtc == result.CandidateChange.Publication.PublishedAtUtc && Object.ReferenceEquals(result.Publication, result.CandidateChange.Publication), "raw publication preserved");
            var directDateBrowser = new DateNavigationBrowser(Fake("2019-06-07", C + B + A + End));
            result = new DateFirstHistoryLocator(directDateBrowser, delegate { return Clock; }, false).LocateAfterAsync(Input, CancellationToken.None, null).GetAwaiter().GetResult();
            Check(result.Publication.BuildId == "20" && result.CandidateChange.ChangeId == "200", "date-navigation interface locates same candidate");
            Check(directDateBrowser.OpenCalls == 1 && directDateBrowser.DateCalls == 1 && directDateBrowser.NormalNavigateCalls == 0, "open visible date control before date request without waiting for latest history");
            Check(basic.Navigations == 1, "older browser without date-navigation interface remains supported");

            var forward = Fake("2019-06-07", A + End).On("2019-06-08", A + End).On("2019-06-09", B + A + End);
            result = Locate(forward, Input, false);
            Check(result.Publication.BuildId == "20" && forward.Dates.Count == 3 && forward.Clicks == 0, "empty update days advance calendar not old pagination");
            Check(forward.Dates.SequenceEqual(new[] { new DateTime(2019,6,7), new DateTime(2019,6,8), new DateTime(2019,6,9) }), "successive date controls");

            string evening = Event("200", "2019-06-07T18:30:10Z", "20", "10", "2019-06-07T18:30:00Z", "2019-06-06T04:00:00Z");
            result = Locate(Fake("2019-06-07", evening + A + End), Input, false);
            Check(result.Publication.BuildId == "20", "selected date may contain that evening no midnight cutoff assumption");
            string delayedObservation = Event("200", "2019-06-06T08:00:00Z", "20", "10", "2019-06-06T04:05:00Z", "2019-06-06T04:00:00Z");
            result = Locate(Fake("2019-06-07", delayedObservation + A + End), Input, false);
            Check(result.EffectiveTime.Hour == 4, "tracker observation does not replace existing Valve time");
            result = Locate(Fake("2019-06-07", C + B + A + End), At("2019-06-06T04:05:00Z"), false);
            Check(result.Publication.BuildId == "30", "equality skipped");

            var paged = Fake("2019-06-07", C + More).ThenClick(C + B + More).ThenClick(C + B + A + End);
            result = Locate(paged, Input, false);
            Check(paged.Clicks == 2 && result.NeighborChanges.Count == 3, "multiple within-day overlapping pages");
            Reject(() => Locate(Fake("2019-06-07", C + More).ThenClick(B + A + End), Input, false), "missing overlap rejected");
            Reject(() => Locate(Fake("2019-06-07", C + More).ThenClick(C.Replace("<del>20</del>", "<del>21</del>") + B + A + End), Input, false), "changed overlap rejected");
            string inserted = Event("400", "2019-06-06T04:11:00Z", "40", "30", "2019-06-06T04:11:00Z", "2019-06-06T04:10:00Z");
            Reject(() => Locate(Fake("2019-06-07", C + More).ThenClick(inserted + C + B + A + End), Input, false), "new record ahead of boundary rejected");
            var repeated = Fake("2019-06-07", C + More).ThenClick(C + More).On("2019-06-07", C + More);
            Reject(() => Locate(repeated, Input, false), "unchanged local pagination cannot loop");
            Check(repeated.Dates.Count == 2 && repeated.Clicks == 1, "only one bounded overlapping date fallback");
            var midnightExclusive = Fake("2019-06-07", C + More).ThenClick(C + More).BeforeExclusive(C + B + A + End);
            result = Locate(midnightExclusive, Input, false);
            Check(result.Publication.BuildId == "20" && midnightExclusive.Dates.Count == 2 && midnightExclusive.Dates.Last() == new DateTime(2019,6,7), "exclusive-midnight date fallback includes boundary day plus older records");
            var midnightWithoutButton = Fake("2019-06-07", C).BeforeExclusive(C + B + A + End);
            Check(Locate(midnightWithoutButton, Input, false).Publication.BuildId == "20" && midnightWithoutButton.Clicks == 0, "no-button continuation also uses following calendar day");
            Reject(() => Locate(Fake("2019-06-07", C + More).ThenClick(C + More).BeforeExclusive(B + A + End), Input, false), "following-day filter still rejects absent exact overlap");

            string oldA = Event("100", "2015-12-23T11:00:00Z", "10", "9", null, null);
            string oldB = Event("200", "2015-12-23T12:00:00Z", "20", "10", null, null);
            DateTimeOffset oldInput = At("2015-12-23T19:00:00+08:00");
            Reject(() => Locate(Fake("2015-12-24", oldB + oldA + End), oldInput, false), "old missing time strict mode rejected");
            var observed = Fake("2015-12-24", oldB + oldA + End);
            result = Locate(observed, oldInput, true);
            Check(result.Publication.BuildId == "20" && result.EffectiveTime == At("2015-12-23T12:00:00Z"), "authorized old observed time nearest successor");
            Check(!result.HasPrecisePublicTime && result.UsedObservationFallback && result.TimeSource.Contains("SteamDB"), "old source disclosed");
            Check(result.Publication.PublishedAtUtc == DateTimeOffset.MinValue && result.Publication.PreviousPublishedAtUtc == null && result.PreviousPublicChange.Publication.PublishedAtUtc == DateTimeOffset.MinValue, "old raw timestamp and previous timestamp never invented");
            Check(result.Assurance.Contains("不是 Valve") && result.Assurance.Contains("原始记录未被改写"), "fallback assurance clear");
            string mixedB = Event("200", "2015-12-23T12:00:10Z", "20", "10", "2015-12-23T12:00:00Z", null);
            result = Locate(Fake("2015-12-24", mixedB + oldA + End), oldInput, true);
            Check(result.HasPrecisePublicTime && result.UsedObservationFallback && result.TimeSource == "Valve public/timeupdated", "precise candidate with observed predecessor disclosed separately");
            Reject(() => Locate(Fake("2015-12-24", oldB.Replace("<del>10</del>", "<del>11</del>") + oldA + End), oldInput, true), "old Build old-new chain gap rejected");
            Reject(() => Locate(Fake("2015-12-24", oldB.Replace("<del>10</del>", "") + oldA + End), oldInput, true), "old Build unknown predecessor rejected");
            string rollback = Event("300", "2015-12-23T13:00:00Z", "10", "20", null, null);
            result = Locate(Fake("2015-12-24", rollback + oldB + oldA + End), At("2015-12-23T12:00:00Z"), true);
            Check(result.Publication.BuildId == "10" && result.CandidateChange.ChangeId == "300", "old rollback is separate activation");

            var exact = Fake("2019-06-07", C + B + A + End);
            result = Locator(exact, false).LocateExactChangeAsync("200", At("2019-06-06T04:05:10Z"), CancellationToken.None, null).GetAwaiter().GetResult();
            Check(result.CandidateChange.ChangeId == "200" && !result.RequestedUtc.HasValue, "patch source exact ChangeId date lookup");
            result = Locator(Fake("2015-12-24", oldB + oldA + End), true).LocateExactChangeAsync("200", At("2015-12-23T12:00:00Z"), CancellationToken.None, null).GetAwaiter().GetResult();
            Check(result.UsedObservationFallback && result.EffectiveTime.Year == 2015 && result.Publication.PublishedAtUtc == DateTimeOffset.MinValue, "exact old source retains original missing time");
            Reject(() => Locator(Fake("2019-06-07", C + A + End), false).LocateExactChangeAsync("200", At("2019-06-06T04:00:00Z"), CancellationToken.None, null).GetAwaiter().GetResult(), "missing exact ChangeId in range rejected");

            var equivalent = Fake("2019-06-07", B + A + End);
            Locate(equivalent, At("2019-06-05T21:00:00-07:00"), false);
            Check(equivalent.Dates.Single() == new DateTime(2019,6,7), "other input timezone converts to Beijing calendar");
            var future = new FakeBrowser();
            Reject(() => Locate(future, Clock, false), "future input rejected");
            Check(future.Navigations == 0, "future rejected before navigation");
            using(var cancel = new CancellationTokenSource())
            {
                cancel.Cancel(); bool canceled = false;
                try { Locator(new FakeBrowser(), false).LocateAsync(Input, cancel.Token, null).GetAwaiter().GetResult(); } catch(OperationCanceledException) { canceled = true; }
                Check(canceled, "cancellation propagated before navigation");
            }
            var cancellation = new CancellationTokenSource();
            var cancelPage = Fake("2019-06-07", C + More); cancelPage.CancelOnClick = cancellation;
            bool pageCanceled = false;
            try { Locator(cancelPage, false).LocateAsync(Input, cancellation.Token, null).GetAwaiter().GetResult(); } catch(OperationCanceledException) { pageCanceled = true; }
            Check(pageCanceled, "cancellation propagated during local pagination"); cancellation.Dispose();

            string privateAfter = Private("300", "2019-06-06T04:10:00Z");
            string privateBefore = Private("100", "2019-06-06T03:00:00Z");
            var metadataOnly = Fake("2019-06-07", privateAfter + privateBefore + More).On("2019-06-08", B + A + End);
            result = Locate(metadataOnly, Input, false);
            Check(metadataOnly.Dates.Count == 2 && metadataOnly.Clicks == 0 && result.Publication.BuildId == "20", "private-only neighborhood crossing input advances date not ancient pagination");
            var privatePaged = Fake("2019-06-07", privateAfter + More).ThenClick(privateAfter + B + A + End);
            result = Locate(privatePaged, Input, false);
            Check(privatePaged.Clicks == 1 && result.Publication.BuildId == "20", "public absent but range not crossing input still local pages");
            Reject(() => Locate(Fake("2019-06-07", privateAfter.Replace("privatebranches", "unknown_public_change") + privateBefore + End), Input, false), "unknown relevant field cannot be skipped as private-only day");
            Reject(() => Locate(Fake("2019-06-07", B.Replace("2019-06-06T04:05:10Z", "2019-06-06T04:04:10Z") + A + End), Input, false), "publication after observation rejected rather than assuming calendar coverage");
            Reject(() => Locate(Fake("2019-06-07", B.Replace("2019-06-06T04:00:00Z", "2019-06-06T03:00:00Z") + A + End), Input, false), "precise previous-public-time chain gap rejected");
            Reject(() => Locate(Fake("2019-06-07", "<p>Loading history</p>"), Input, false), "loading page not empty update day");
            var emptyDay = Fake("2019-06-07", End).On("2019-06-08", B + A + End);
            Check(Locate(emptyDay, Input, false).Publication.BuildId == "20" && emptyDay.Dates.Count == 2, "explicit empty end can advance a day");
            Reject(() => Locator(Fake("2019-06-07", privateAfter + End), false).LocateExactChangeAsync("300", At("2019-06-06T04:10:00Z"), CancellationToken.None, null).GetAwaiter().GetResult(), "exact private-only ChangeId not declared a public release");
            string unknownExact = B.Replace("<i>buildid:</i>", "<i>unknown_public_field:</i>");
            Reject(() => Locator(Fake("2019-06-07", unknownExact + A + End), false).LocateExactChangeAsync("200", At("2019-06-06T04:05:10Z"), CancellationToken.None, null).GetAwaiter().GetResult(), "exact public ChangeId rejects unknown relevant fields");
            string actualPath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "fixtures", "date-2015-12-22", "20261018-140400-121-app-570-history.html"));
            string actualHtml = File.ReadAllText(actualPath);
            var actualBrowser = Fake("2015-12-22", actualHtml);
            result = Locate(actualBrowser, At("2015-12-21T12:00:00+08:00"), true);
            Check(result.CandidateChange.ChangeId == "1627190" && result.Publication.BuildId == "909005", "actual 2015 date-first candidate");
            Check(result.PreviousPublicChange.ChangeId == "1624513" && result.PreviousPublicChange.Publication.BuildId == "907474", "actual prior public chain matches Build");
            Check(result.EffectiveTime == At("2015-12-21T23:33:19Z") && result.TimeSource == "SteamDB public branch change observed time", "actual 2015 observed timestamp explicitly identified");
            Check(result.Publication.PublishedAtUtc == DateTimeOffset.MinValue && result.Publication.PreviousPublishedAtUtc == null, "actual missing Valve fields remain missing");
            Check(actualBrowser.Clicks == 0 && actualBrowser.Dates.Count == 1 && result.NeighborChanges.Count == 100, "actual one date request contains complete local neighborhood");
            Reject(() => Locate(Fake("2015-12-22", actualHtml), At("2015-12-21T12:00:00+08:00"), false), "actual 2015 strict mode does not fabricate Valve time");
            Console.WriteLine("Actual fixture: Build " + result.Publication.BuildId + ", ChangeId " + result.CandidateChange.ChangeId + ", EffectiveTime " + result.EffectiveTime.ToString("o") + ", TimeSource " + result.TimeSource + ".");
            BeforeAssertions(actualHtml);
            Console.WriteLine("PASS " + checks + " date-first locator assertions (synthetic and captured 2015 public history; no network).");
            return 0;
        }

        private static DateFirstHistoryLocator Locator(FakeBrowser browser, bool fallback) { return new DateFirstHistoryLocator(browser, delegate { return Clock; }, fallback); }
        // Keep the former successor assertions as regression coverage for official
        // patch release windows; user timestamps now go through Before below.
        private static DateFirstLocation Locate(FakeBrowser browser, DateTimeOffset input, bool fallback) { return Locator(browser, fallback).LocateAfterAsync(input, CancellationToken.None, null).GetAwaiter().GetResult(); }
        private static DateFirstLocation Before(FakeBrowser browser, DateTimeOffset input, bool fallback) { return Locator(browser, fallback).LocateAsync(input, CancellationToken.None, null).GetAwaiter().GetResult(); }

        private static void BeforeAssertions(string actualHtml)
        {
            var basic = Fake("2019-06-07", C + B + A + End);
            var result = Before(basic, At("2019-06-06T04:07:00Z"), false);
            Check(result.Publication.BuildId == "20" && result.NextPublicChange.ChangeId == "300", "timestamp selects nearest predecessor with actual upper neighbor");
            Check(result.PreviousPublicChange == null && result.RequestedUtc == At("2019-06-06T04:07:00Z"), "upper neighbor is not mislabeled previous event");
            Check(basic.Dates.Count == 1 && basic.Clicks == 0, "before query uses one date neighborhood");
            result = Before(Fake("2019-06-07", C + B + A + End), At("2019-06-06T04:05:00Z"), false);
            Check(result.Publication.BuildId == "10", "exact equality excludes event");
            result = Before(Fake("2019-06-07", C + B + A + End), At("2019-06-06T04:05:00Z").AddTicks(1), false);
            Check(result.Publication.BuildId == "20", "one tick after event selects it");
            result = Before(Fake("2019-06-07", C + B + A + End), At("2019-06-06T04:05:00Z").AddTicks(-1), false);
            Check(result.Publication.BuildId == "10", "one tick before event selects older");
            var emptyUpdateDay = Fake("2019-06-07", A + More);
            result = Before(emptyUpdateDay, At("2019-06-06T04:05:00Z"), false);
            Check(result.Publication.BuildId == "10" && result.NextPublicChange == null && emptyUpdateDay.Dates.Count == 1 && emptyUpdateDay.Clicks == 0, "no later event needed to locate candidate without advancing calendar");
            Check(result.Assurance.Contains("完整历史回放还需确认") && result.Assurance.Contains("相等时间已排除"), "candidate does not claim a fully verified nearest plan");
            var direct = new DateNavigationBrowser(Fake("2019-06-07", C + B + A + End));
            result = new DateFirstHistoryLocator(direct, delegate { return Clock; }, false).LocateAsync(At("2019-06-06T04:07:00Z"), CancellationToken.None, null).GetAwaiter().GetResult();
            Check(direct.NormalNavigateCalls == 0 && direct.OpenCalls == 1 && direct.DateCalls == 1, "before query opens date controls without current-day capture");
            var paged = Fake("2019-06-07", C + B + More).ThenClick(C + B + A + End);
            Check(Before(paged, At("2019-06-06T04:05:00Z"), false).Publication.BuildId == "10" && paged.Clicks == 1, "equal boundary continues older local page");
            Reject(() => Before(Fake("2019-06-07", C + More).ThenClick(B + A + End), At("2019-06-06T04:05:00Z"), false), "before pagination requires exact overlap");
            var privatePage = Fake("2019-06-07", Private("400", "2019-06-06T05:00:00Z") + More).ThenClick(Private("400", "2019-06-06T05:00:00Z") + C + B + A + End);
            Check(Before(privatePage, At("2019-06-06T04:07:00Z"), false).Publication.BuildId == "20" && privatePage.Clicks == 1, "private updates are not releases");
            Reject(() => Before(Fake("2019-06-07", C.Replace("<del>20</del>", "<del>99</del>") + B + A + End), At("2019-06-06T04:07:00Z"), false), "before rejects public chain gap");
            Reject(() => Before(Fake("2019-06-07", C.Replace("2019-06-06T04:05:00Z", "2019-06-06T04:04:00Z") + B + A + End), At("2019-06-06T04:07:00Z"), false), "before rejects time chain gap");
            Reject(() => Before(Fake("2019-06-07", C.Replace("<i>buildid:</i>", "<i>unknown_public_field:</i>") + B + A + End), At("2019-06-06T04:07:00Z"), false), "before rejects unknown field above candidate");
            Reject(() => Before(Fake("2019-06-07", A + End), Input, false), "no strictly older event is explicit failure");
            var noHistory = Fake("2019-06-07", End);
            Reject(() => Before(noHistory, Input, false), "empty history does not seek a future release");
            Check(noHistory.Dates.Count == 1, "empty historical range does not increment future days");
            Reject(() => Before(Fake("2019-06-07", "<p>Loading history</p>"), Input, false), "before rejects loading as empty history");
            var zone = Fake("2019-06-07", C + B + A + End);
            Check(Before(zone, At("2019-06-05T21:07:00-07:00"), false).Publication.BuildId == "20" && zone.Dates.Single() == new DateTime(2019,6,7), "equivalent timezone selects same predecessor");
            var latest = Fake("2019-06-13", C + B + A + End);
            Check(Before(latest, Clock.AddDays(4), false).Publication.BuildId == "30" && latest.Dates.Single() == new DateTime(2019,6,13), "future input searches available history without future-day loop");
            string oldA = Event("100", "2015-12-23T11:00:00Z", "10", "9", null, null);
            string oldB = Event("200", "2015-12-23T12:00:00Z", "20", "10", null, null);
            string rollback = Event("300", "2015-12-23T13:00:00Z", "10", "20", null, null);
            result = Before(Fake("2015-12-24", rollback + oldB + oldA + End), At("2015-12-23T13:00:00Z"), true);
            Check(result.Publication.BuildId == "20" && result.CandidateChange.ChangeId == "200", "rollback equality selects prior activation");
            result = Before(Fake("2015-12-24", rollback + oldB + oldA + End), At("2015-12-23T13:00:01Z"), true);
            Check(result.Publication.BuildId == "10" && result.CandidateChange.ChangeId == "300", "rollback after time selects new activation of old Build");
            Check(result.Publication.PublishedAtUtc == DateTimeOffset.MinValue && result.UsedObservationFallback, "before keeps missing Valve timestamp and labels fallback");
            var actual = Fake("2015-12-22", actualHtml);
            result = Before(actual, At("2015-12-21T12:00:00+08:00"), true);
            Check(result.Publication.BuildId == "907474" && result.CandidateChange.ChangeId == "1624513", "actual old timestamp now selects 907474 instead of successor 909005");
            Check(result.EffectiveTime == At("2015-12-20T18:43:55Z") && result.NextPublicChange.ChangeId == "1627190", "actual predecessor observed time and upper neighbor");
            Check(actual.Dates.Count == 1 && actual.Clicks == 0, "actual before fixture needs one date request no older-page load");
            Reject(() => Before(Fake("2015-12-22", actualHtml), At("2015-12-21T12:00:00+08:00"), false), "before strict mode still rejects missing Valve time");
            Console.WriteLine("Before fixture: Build " + result.Publication.BuildId + ", ChangeId " + result.CandidateChange.ChangeId + ", EffectiveTime " + result.EffectiveTime.ToString("o") + ".");
        }
        private static FakeBrowser Fake(string date, string html) { return new FakeBrowser().On(date, html); }
        private static DateTimeOffset At(string value) { return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture); }
        private static string Delta(string key, string oldValue, string newValue) { return "<li><span><i>" + key + ":</i>" + (oldValue == null ? "" : "<del>" + oldValue + "</del>") + (newValue == null ? "" : "<ins>" + newValue + "</ins>") + "</span></li>"; }
        private static string Panel(string id, string observed, string body) { return "<div class='panel-history' data-changeid='" + id + "'><relative-time datetime='" + observed + "'></relative-time><ul class='app-history'><li class='repo'><span>Changed <i>Depots</i></span><ul>" + body + "</ul></li></ul></div>"; }
        private static string Event(string id, string observed, string build, string previousBuild, string published, string previousPublished) { return Panel(id, observed, "<li><span>Changed <span class='branch-name branch-public'>public</span> branch<ul class='app-history'>" + (build == null ? "" : Delta("buildid", previousBuild, build)) + (published == null ? "" : Delta("timeupdated", previousPublished, published)) + "</ul></span></li>"); }
        private static string Private(string id, string observed) { return Panel(id, observed, Delta("privatebranches", null, "1")); }
        private static void Check(bool value, string name) { checks++; if(!value) throw new Exception("FAIL " + name); }
        private static void Reject(Action action, string name) { bool rejected = false; try { action(); } catch(HistoryResolutionException) { rejected = true; } Check(rejected, name); }
        private sealed class DateNavigationBrowser : IHistoryBrowser, IHistoryDateBrowser, IHistoryDateNavigationBrowser
        {
            private readonly FakeBrowser inner;
            public int OpenCalls, DateCalls, NormalNavigateCalls;
            public DateNavigationBrowser(FakeBrowser inner) { this.inner = inner; }
            public Task OpenHistoryForDateAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); OpenCalls++; return Task.FromResult(0); }
            public Task<HistoryPageSnapshot> LoadHistoryBeforeAsync(DateTime date, CancellationToken token) { if(OpenCalls != 1) throw new Exception("Date control was not opened before date request"); DateCalls++; return inner.LoadHistoryBeforeAsync(date, token); }
            public Task<HistoryPageSnapshot> NavigateAsync(string url, CancellationToken token) { NormalNavigateCalls++; throw new Exception("Initial ordinary navigation would wait for current history; date-navigation interface must be preferred"); }
            public Task<HistoryPageSnapshot> CaptureAsync(CancellationToken token) { throw new Exception("Unexpected current-history capture"); }
            public Task<HistoryPageSnapshot> ClickAsync(string selector, CancellationToken token) { return inner.ClickAsync(selector, token); }
        }
        private sealed class FakeBrowser : IHistoryBrowser, IHistoryDateBrowser
        {
            private readonly Dictionary<string, Queue<string>> pages = new Dictionary<string, Queue<string>>();
            private readonly Queue<string> clickPages = new Queue<string>();
            private string midnightExclusiveHtml;
            public readonly List<DateTime> Dates = new List<DateTime>();
            public int Navigations, Clicks;
            public CancellationTokenSource CancelOnClick;
            public FakeBrowser On(string date, string html) { Queue<string> values; if(!pages.TryGetValue(date, out values)) pages.Add(date, values = new Queue<string>()); values.Enqueue(html); return this; }
            public FakeBrowser ThenClick(string html) { clickPages.Enqueue(html); return this; }
            public FakeBrowser BeforeExclusive(string html) { midnightExclusiveHtml = html; return this; }
            public Task<HistoryPageSnapshot> NavigateAsync(string url, CancellationToken token) { token.ThrowIfCancellationRequested(); Navigations++; if(!url.Contains("/app/570/history/")) throw new Exception("Unexpected navigation"); return Task.FromResult(Page("CURRENT PAGE IS DELIBERATELY NOT PARSEABLE")); }
            public Task<HistoryPageSnapshot> CaptureAsync(CancellationToken token) { throw new Exception("Unexpected capture"); }
            public Task<HistoryPageSnapshot> ClickAsync(string selector, CancellationToken token) { Clicks++; if(CancelOnClick != null) CancelOnClick.Cancel(); token.ThrowIfCancellationRequested(); if(clickPages.Count == 0) throw new Exception("Unexpected pagination"); return Task.FromResult(Page(clickPages.Dequeue())); }
            public Task<HistoryPageSnapshot> LoadHistoryBeforeAsync(DateTime date, CancellationToken token)
            {
                token.ThrowIfCancellationRequested(); Dates.Add(date); Queue<string> values;
                if(pages.TryGetValue(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), out values) && values.Count > 0) return Task.FromResult(Page(values.Dequeue()));
                if(midnightExclusiveHtml != null)
                {
                    var document = new HtmlAgilityPack.HtmlDocument(); document.LoadHtml(midnightExclusiveHtml);
                    DateTimeOffset cutoff = new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Unspecified), TimeSpan.Zero);
                    foreach(var panel in document.DocumentNode.Descendants().Where(n => n.GetAttributeValue("class", "").Contains("panel-history")).ToList())
                    {
                        var time = panel.Descendants("relative-time").Single();
                        if(At(time.GetAttributeValue("datetime", "")) >= cutoff) panel.Remove();
                    }
                    return Task.FromResult(Page(document.DocumentNode.InnerHtml));
                }
                throw new Exception("Unexpected date " + date.ToString("yyyy-MM-dd"));
            }
            private static HistoryPageSnapshot Page(string html) { return new HistoryPageSnapshot { Url = "https://steamdb.info/app/570/history/?filterkey=2", Html = html, CapturedAtUtc = Clock }; }
        }
    }
}
