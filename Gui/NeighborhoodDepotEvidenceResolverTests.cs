using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace DotaArchive
{
    internal static class NeighborhoodDepotEvidenceResolverTests
    {
        private static int checks;
        private static readonly NeighborhoodDepotEvidenceResolver Resolver = new NeighborhoodDepotEvidenceResolver();
        public static int Main(string[] args)
        {
            if (args.Length != 2) throw new ArgumentException("fixture JSON and evidence output JSON are required");
            var json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
            var page = json.Deserialize<HistoryPageSnapshot>(File.ReadAllText(args[0]));
            var location = new DateFirstHistoryLocator(new FixtureBrowser(page), () => At("2026-10-21T00:00:00Z"), true)
                .LocateAsync(At("2015-12-21T12:00:00+08:00"), CancellationToken.None, null).GetAwaiter().GetResult();
            var actual = Resolver.Resolve(location, true);
            Check(actual.CandidateBuildId == "907474" && actual.CandidateChangeId == "1624513", "real candidate");
            Check(actual.CandidateEffectiveTimeUtc == At("2015-12-20T18:43:55Z") && actual.CandidateTimeSource.StartsWith("SteamDB"), "legacy observed time is explicit");
            Check(location.Publication.PublishedAtUtc == DateTimeOffset.MinValue, "raw missing Valve time stays missing");
            Check(!actual.Complete && !actual.CanDownload && !actual.HistoricalMembershipComplete, "partial cannot become complete or downloadable");
            Check(actual.KnownFields.Count(f => f.Field == "manifests/public/gid" && f.Exists) == 16, "sixteen independently witnessed active manifest fields");
            Check(Value(actual, "373301", "manifests/public/gid") == "2427714153971932438", "real common manifest");
            Check(Value(actual, "373302", "manifests/public/gid") == "4122038266032860717", "real Windows manifest");
            Check(Value(actual, "373309", "manifests/public/gid") == "5238771144942397785", "retained simplified Chinese manifest");
            Check(Value(actual, "381451", "manifests/public/gid") == "8036493341345921773", "retained unmodified target Depot");
            Check(Value(actual, "381453", "manifests/public/gid") == "3171873214760467348", "real second content manifest");
            Check(actual.ObservedDepots.Single(d => d.DepotId == "373305").UnknownFields.Contains("manifests/public/gid"), "missing English manifest remains unknown");
            Check(!actual.KnownFields.Any(f => f.DepotId == "373305" && f.Field == "manifests/public/gid"), "missing record is not an absent field");
            Check(!Field(actual, "571", "manifests/public/gid").Exists, "explicit Source1 removal proves field absence");
            Check(Field(actual, "373301", "manifests/public/gid").Evidence.Count == 2, "nearest before and after corroborate one field");
            Check(Field(actual, "381451", "manifests/public/gid").Evidence.Any(e => e.ChangeId == "1623074"), "unchanged field retains actual source change");
            Reject(() => Resolver.Resolve(location, false), "continuity must be explicitly attested");

            SyntheticAssertions();
            var report = new {
                kind = "partial_neighborhood_field_evidence_not_download_plan", checksPassed = checks,
                fixture = Path.GetFullPath(args[0]), complete = actual.Complete, canDownload = actual.CanDownload,
                historicalMembershipComplete = actual.HistoricalMembershipComplete,
                requestedUtc = actual.RequestedUtc.Value.ToString("o"), candidateBuildId = actual.CandidateBuildId,
                candidateChangeId = actual.CandidateChangeId, candidateEffectiveTimeUtc = actual.CandidateEffectiveTimeUtc.ToString("o"),
                candidateTimeSource = actual.CandidateTimeSource, candidateSourceUrl = actual.CandidateSourceUrl,
                observedFromUtc = actual.ObservedFromUtc.ToString("o"), observedThroughUtc = actual.ObservedThroughUtc.ToString("o"),
                knownFields = actual.KnownFields.Select(f => new {
                    path = f.Path, depotId = f.DepotId, field = f.Field, exists = f.Exists, value = f.Value, method = f.Method,
                    evidence = f.Evidence.Select(e => new { changeId = e.ChangeId, sourceUrl = e.SourceUrl, observedAtUtc = e.ObservedAtUtc.ToString("o"), side = e.Side, exists = e.Exists, value = e.Value }).ToArray()
                }).ToArray(),
                observedDepots = actual.ObservedDepots.Select(d => new { depotId = d.DepotId, unknownFields = d.UnknownFields }).ToArray(),
                limitation = actual.Limitation
            };
            File.WriteAllText(args[1], json.Serialize(report), new System.Text.UTF8Encoding(false));
            Console.WriteLine("PASS " + checks + " checks; Build 907474, 16 present public manifest fields; Complete=false; CanDownload=false.");
            return 0;
        }

        private static void SyntheticAssertions()
        {
            var a = Event("100", "2019-01-01T12:00:00Z", "2019-01-01T11:59:00Z", "10", Delta("1", "manifests/public/gid", "99", "101"));
            var b = Event("200", "2019-01-01T12:00:00Z", "2019-01-01T12:00:00Z", "20", Delta("1", "manifests/public/gid", "101", "102"));
            var c = Event("300", "2019-01-01T12:00:00Z", "2019-01-01T12:00:00Z", "10", Delta("1", "manifests/public/gid", "102", "101"));
            var before = Location(a, At("2019-01-01T12:00:00Z"), c, a, b);
            var result = Resolver.Resolve(before, true);
            Check(Value(result, "1", "manifests/public/gid") == "101" && result.CandidateBuildId == "10", "same-second equality excludes both new activations");
            result = Resolver.Resolve(Location(c, At("2019-01-01T12:00:00Z").AddTicks(1), b, c, a), true);
            Check(Value(result, "1", "manifests/public/gid") == "101" && result.CandidateChangeId == "300", "same second uses ChangeId and handles rollback");
            Reject(() => Resolver.Resolve(Location(b, At("2019-01-01T12:00:00Z").AddTicks(1), a, b, c), true), "stale same-second candidate rejected");
            Reject(() => Resolver.Resolve(Location(b, At("2019-01-01T12:00:00Z"), a, b, c), true), "equal-time candidate rejected");
            Reject(() => Resolver.Resolve(Location(a, At("2019-01-01T11:58:00Z"), a, b, c), true), "future candidate rejected");

            var meta = Change("250", "2019-01-01T12:00:00Z", Delta("2", "config/oslist", "windows", "linux"), Delta("3", "manifests/public/gid", null, "301"));
            result = Resolver.Resolve(Location(b, At("2019-01-01T12:00:00Z").AddTicks(1), a, b, meta), true);
            Check(Value(result, "2", "config/oslist") == "windows" && Field(result, "2", "config/oslist").Method == "NearestOldAfterCandidate", "metadata after candidate uses old value even at same second");
            Check(!Field(result, "3", "manifests/public/gid").Exists, "later addition explicitly establishes earlier absence");
            Check(result.ObservedDepots.Single(d => d.DepotId == "2").UnknownFields.Contains("config/language"), "missing configuration remains unknown");
            Check(!result.ObservedDepots.Any(d => d.DepotId == "999"), "unobserved Depot not fabricated");
            var deletion = Change("150", "2019-01-01T12:00:00Z", Delta("4", "config/language", "english", null));
            result = Resolver.Resolve(Location(b, At("2019-01-01T12:00:01Z"), a, deletion, b), true);
            Check(!Field(result, "4", "config/language").Exists && !result.ObservedDepots.Single(d => d.DepotId == "4").UnknownFields.Contains("config/language"), "explicit absence differs from unknown");

            var bad = Event("200", "2019-01-01T12:00:00Z", "2019-01-01T12:00:00Z", "20", Delta("1", "manifests/public/gid", "999", "102"));
            Reject(() => Resolver.Resolve(Location(a, At("2019-01-01T12:00:00Z"), a, bad), true), "nearest witnesses must agree");
            var distant = Change("50", "2019-01-01T11:58:00Z", Delta("1", "manifests/public/gid", "1", "555"));
            result = Resolver.Resolve(Location(a, At("2019-01-01T12:00:00Z"), distant, a, b), true);
            Check(Value(result, "1", "manifests/public/gid") == "101", "distant unrelated field history is not used instead of nearest cut witnesses");
            Reject(() => Resolver.Resolve(Location(a, At("2019-01-01T12:00:00Z"), a, a, b), true), "duplicate ChangeId rejected");
            var missing = Change("400", "2019-01-01T12:01:00Z"); missing.ParsedAllDepotChanges = false;
            Reject(() => Resolver.Resolve(Location(a, At("2019-01-01T12:00:00Z"), a, missing), true), "unknown parsed region cannot attest fields");
            var different = Event("100", "2019-01-01T12:00:00Z", "2019-01-01T11:59:00Z", "999", Delta("1", "manifests/public/gid", "99", "101"));
            Reject(() => Resolver.Resolve(Location(different, At("2019-01-01T12:00:00Z"), a, b), true), "candidate raw mismatch rejected");
            var unknown = Change("400", "2019-01-01T12:01:00Z", Delta("2", "config/future_scope", null, "1"));
            Reject(() => Resolver.Resolve(Location(a, At("2019-01-01T12:00:00Z"), a, unknown), true), "unsupported scope field rejected");
            var privateManifest = Change("400", "2019-01-01T12:01:00Z", Delta("2", "manifests/beta/gid", "11", "22"));
            Reject(() => Resolver.Resolve(Location(a, At("2019-01-01T12:00:00Z"), a, privateManifest), true), "non-public manifest not projected");
            var duplicatePath = Change("400", "2019-01-01T12:01:00Z", Delta("2", "name", "one", "two"), Delta("2", "name", "two", "three"));
            Reject(() => Resolver.Resolve(Location(a, At("2019-01-01T12:00:00Z"), a, duplicatePath), true), "unreconciled duplicate field rejected");
            Check(typeof(NeighborhoodDepotEvidenceResult).GetProperty("Complete").GetSetMethod(true) == null, "no Complete setter");
            Check(typeof(NeighborhoodDepotEvidenceResult).GetProperty("CanDownload").GetSetMethod(true) == null, "no download setter");
        }

        private static NeighborhoodDepotFieldEvidence Field(NeighborhoodDepotEvidenceResult result, string depot, string field) { return result.KnownFields.Single(f => f.DepotId == depot && f.Field == field); }
        private static string Value(NeighborhoodDepotEvidenceResult result, string depot, string field) { var value = Field(result, depot, field); Check(value.Exists, "expected present " + depot + "/" + field); return value.Value; }
        private static DateTimeOffset At(string text) { return DateTimeOffset.Parse(text, CultureInfo.InvariantCulture); }
        private static HistoryDelta Delta(string depot, string field, string oldValue, string newValue) { return new HistoryDelta { Path = "depots/" + depot + "/" + field, OldExists = oldValue != null, OldValue = oldValue, NewExists = newValue != null, NewValue = newValue }; }
        private static HistoryChange Change(string id, string observed, params HistoryDelta[] deltas) { return new HistoryChange { ChangeId = id, ObservedAtUtc = At(observed), SourceUrl = "https://steamdb.info/app/570/history/?changeid=" + id, ParsedAllDepotChanges = true, Deltas = deltas.ToList() }; }
        private static HistoryChange Event(string id, string observed, string published, string build, params HistoryDelta[] deltas) { var value = Change(id, observed, deltas); value.Publication = new HistoryBuildEvent { ChangeId = id, IsPublic = true, BuildId = build, PublishedAtUtc = At(published) }; return value; }
        private static DateFirstLocation Location(HistoryChange candidate, DateTimeOffset input, params HistoryChange[] changes) { return new DateFirstLocation { CandidateChange = candidate, Publication = candidate.Publication, RequestedUtc = input, EffectiveTime = candidate.Publication.PublishedAtUtc, NeighborChanges = changes.ToList() }; }
        private static void Check(bool value, string name) { checks++; if (!value) throw new Exception("FAIL " + name); }
        private static void Reject(Action action, string name) { bool rejected = false; try { action(); } catch (HistoryResolutionException) { rejected = true; } Check(rejected, name); }
        private sealed class FixtureBrowser : IHistoryBrowser, IHistoryDateBrowser
        {
            private readonly HistoryPageSnapshot page;
            internal FixtureBrowser(HistoryPageSnapshot page) { this.page = page; }
            public Task<HistoryPageSnapshot> NavigateAsync(string url, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(page); }
            public Task<HistoryPageSnapshot> CaptureAsync(CancellationToken token) { throw new Exception("Unexpected capture"); }
            public Task<HistoryPageSnapshot> ClickAsync(string selector, CancellationToken token) { throw new Exception("Unexpected pagination"); }
            public Task<HistoryPageSnapshot> LoadHistoryBeforeAsync(DateTime date, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(page); }
        }
    }
}
