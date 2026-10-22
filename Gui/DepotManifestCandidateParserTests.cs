using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using DotaArchive;

internal static class DepotManifestCandidateParserTests
{
    private static int checks;
    private static readonly DepotManifestCandidateParser Parser = new DepotManifestCandidateParser();
    private const string Source = "https://steamdb.info/depot/373301/manifests/";
    private const string Header = "<thead><tr><th>Seen Date</th><th>Relative Date</th><th>ManifestID</th><th></th></tr></thead>";
    private static int Main(string[] args)
    {
        string fixture = args.Length == 1 ? args[0] : Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "fixtures", "depot-date-feasibility-20261021", "20261021-122058-442-depot-373301-manifests.html"));
        var actual = new HistoryPageSnapshot { Url = Source, Html = File.ReadAllText(fixture) };
        DepotManifestCandidate real = Parser.Parse(actual, "373301", At("2015-12-21T04:00:00Z"));
        Check(real.CandidateManifestId == "2427714153971932438", "actual 2015 public Seen Date candidate");
        Check(real.SeenUtc == At("2015-12-20T18:43:55Z"), "actual Seen Date is preserved as UTC");
        Check(real.PublicRowsScanned == 3783, "all actual public rows scanned; external branches excluded");
        Check(real.SourceUrl == Source && real.ManifestHistoryUrl == "https://steamdb.info/depot/373301/history/?changeid=M:2427714153971932438", "source and candidate history URL remain bound to the Depot");
        Check(!real.IsActivationVerified && !real.IsHistoricalMembershipVerified && real.Kind == "UnverifiedSeenDateCandidate", "candidate cannot claim activation or historical Depot membership");
        Check(typeof(DepotManifestCandidate).GetProperty("Complete") == null && !typeof(ResolvedHistoryPlan).IsAssignableFrom(typeof(DepotManifestCandidate)), "result is not a complete download plan");
        Check(real.Assurance.Contains("回滚") && real.Assurance.Contains("不是完整下载组合"), "limitations are explicit on every candidate");
        DepotManifestCandidate equalActual = Parser.Parse(actual, "373301", real.SeenUtc);
        Check(equalActual != null && equalActual.CandidateManifestId != real.CandidateManifestId && equalActual.SeenUtc < real.SeenUtc, "actual equality is excluded");
        DepotManifestCandidate rollback = Parser.Parse(actual, "373301", At("2023-06-21T04:00:00Z"));
        Check(rollback.CandidateManifestId == "354335573223778363", "actual rollback counterexample still returns only the nearest seen candidate");
        Check(rollback.CandidateManifestId != "8685282655945631170" && !rollback.IsActivationVerified && rollback.Assurance.Contains("Seen Date 不证明实际启用时间"), "known actual activation differs; parser does not falsely verify it");

        string older = Row("public", "11", "2015-12-20T01:00:00Z");
        string middle = Row("public", "22", "2015-12-20T02:00:00Z");
        string later = Row("public", "33", "2015-12-20T03:00:00Z");
        var target = At("2015-12-20T03:00:00Z");
        Check(Parse(older + later + middle, target).CandidateManifestId == "22", "unsorted full table selects latest strict predecessor");
        Check(Parse(later + middle + older, target).CandidateManifestId == "22", "descending DOM order is not required or trusted");
        Check(Parse(older + middle, At("2015-12-20T10:30:00+08:00")).CandidateManifestId == "22", "target offset converts to exact UTC instant");
        Check(Parse(Row("public", "44", "2015-12-20T10:15:00+08:00") + older, At("2015-12-20T02:30:00Z")).CandidateManifestId == "44", "row timestamp offset is respected");
        Check(Parse(middle, At("2015-12-20T02:00:00Z")) == null, "equal-only table has no candidate");
        Check(Parse(later, target) == null, "no fallback to equal or future Manifest");
        Check(Parse(older + Row("beta", "99", "2015-12-20T02:59:00Z"), target).CandidateManifestId == "11", "private branch cannot outrank public");
        Check(Parse(older + Row("_steamdb_external_", "99", "2015-12-20T02:59:00Z"), target).CandidateManifestId == "11", "external branch excluded");
        Check(Parse(older + Row("unknown", "99", "2015-12-20T02:59:00Z"), target).CandidateManifestId == "11", "unknown branch excluded");
        Check(Parse(older + Row("public", "99", "2015-12-20T02:59:00Z").Replace(" data-branch='public'", ""), target).CandidateManifestId == "11", "absent branch is not inferred as public");
        Check(Parse(older + Row("beta", "99", "2015-12-20T02:59:00Z").Replace("data-time='2015-12-20T02:59:00Z'", "data-time='bad'"), target).CandidateManifestId == "11", "malformed irrelevant private row does not affect public candidate");
        Check(Parse(Row("beta", "99", "2015-12-20T02:59:00Z"), target) == null, "private-only table has no public candidate");
        Reject(() => Parse(middle + later.Replace("data-time='2015-12-20T03:00:00Z'", "data-time='bad'"), target), "malformed public timestamp after a usable row still fails full scan");
        Reject(() => Parse(older.Replace("data-time='2015-12-20T01:00:00Z'", "data-time='2015-12-20T01:00:00'"), target), "timestamp without timezone rejected");
        Reject(() => Parse(older.Replace("data-time='2015-12-20T01:00:00Z'", "data-time='2015-02-30T01:00:00Z'"), target), "invalid calendar timestamp rejected");
        Reject(() => Parse(older.Replace("01:00:00 UTC", "02:00:00 UTC"), target), "visible Seen Date must match machine timestamp");
        foreach (string badId in new[] { "0", "-1", "01", "1e3", "18446744073709551616", "" })
            Reject(() => Parse(Row("public", badId, "2015-12-20T01:00:00Z"), target), "malformed public Manifest ID rejected: " + badId);
        Reject(() => Parse(older.Replace("/depot/373301/history/", "/depot/381451/history/"), target), "row link must belong to requested Depot");
        Reject(() => Parse(older.Replace("changeid=M:11", "changeid=M:12"), target), "row link and numeric Manifest text must agree");
        Reject(() => Parse(older.Replace("/depot/373301/history/?", "https://example.com/depot/373301/history/?"), target), "external Manifest link rejected");
        Reject(() => Parse(older + older, target), "duplicate public Manifest rows rejected");
        Reject(() => Parse(middle + Row("public", "44", "2015-12-20T02:00:00Z"), target), "equal nearest Seen Dates do not invent an order");
        Reject(() => Parse(older.Replace("<td></td>", ""), target), "changed public row column shape rejected");
        foreach (string url in new[] { "https://steamdb.info/depot/381451/manifests/", "https://steamdb.info/depot/373301/files/", "https://steamdb.info/depot/373301/manifests", "http://steamdb.info/depot/373301/manifests/", "https://steamdb.info:444/depot/373301/manifests/", "https://user@steamdb.info/depot/373301/manifests/", "https://steamdb.info/depot/373301/manifests/?branch=beta", "https://steamdb.info.example.com/depot/373301/manifests/" })
            Reject(() => Parser.Parse(new HistoryPageSnapshot { Url = url, Html = Html(older) }, "373301", target), "wrong source binding rejected: " + url);
        foreach (string depot in new[] { "0", "0373301", "-1", "4294967296", "x" })
            Reject(() => Parser.Parse(actual, depot, target), "invalid Depot identifier rejected: " + depot);
        Reject(() => Parser.Parse(new HistoryPageSnapshot { Url = Source, Html = "<p>Loading</p>" }, "373301", target), "loading page is not a table");
        Reject(() => Parser.Parse(new HistoryPageSnapshot { Url = Source, Html = Html(older).Replace("Seen Date", "Unknown Date") }, "373301", target), "unknown column semantics fail");
        Reject(() => Parser.Parse(new HistoryPageSnapshot { Url = Source, Html = Html(older) + Html(middle) }, "373301", target), "ambiguous duplicate Manifest section rejected");
        string unrelated = "<table>" + Header + "<tbody>" + later + "</tbody></table>";
        Check(Parser.Parse(new HistoryPageSnapshot { Url = Source, Html = unrelated + Html(older) }, "373301", target).CandidateManifestId == "11", "other tables outside Manifest section cannot supply candidates");
        Console.WriteLine("PASS " + checks + " offline Manifest candidate assertions; actual 2015=" + real.CandidateManifestId + ", rollback counterexample=" + rollback.CandidateManifestId + "; both unverified candidates.");
        return 0;
    }
    private static DepotManifestCandidate Parse(string rows, DateTimeOffset target) { return Parser.Parse(new HistoryPageSnapshot { Url = Source, Html = Html(rows) }, "373301", target); }
    private static string Html(string rows) { return "<div id='manifests'><table>" + Header + "<tbody>" + rows + "</tbody></table></div>"; }
    private static string Row(string branch, string id, string seen)
    {
        string visible = At(seen).ToUniversalTime().ToString("d MMMM yyyy '–' HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
        return "<tr data-branch='" + branch + "'><td>" + visible + "</td><td data-time='" + seen + "'></td><td><a href='/depot/373301/history/?changeid=M:" + id + "'>" + id + "</a></td><td></td></tr>";
    }
    private static DateTimeOffset At(string text) { return DateTimeOffset.Parse(text, CultureInfo.InvariantCulture); }
    private static void Check(bool condition, string name) { checks++; if (!condition) throw new Exception("FAIL " + name); }
    private static void Reject(Action action, string name) { bool rejected = false; try { action(); } catch (HistoryResolutionException) { rejected = true; } Check(rejected, name); }
}
