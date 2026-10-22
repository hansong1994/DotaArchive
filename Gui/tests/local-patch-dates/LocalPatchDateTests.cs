using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace DotaArchive
{
    // Local files only. Synthetic public histories retain unchanged depot state,
    // repeat Build IDs, and prove the boundaries used by the date approximation.
    internal static class LocalPatchDateTests
    {
        private static int checks;
        private static string root;
        private static readonly DateTimeOffset Head = T("2020-01-04T00:00:00Z");
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
        private static readonly List<object> realResults = new List<object>();
        private static DateTimeOffset T(string value) { return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind); }
        private static void Check(bool condition, string label) { if (!condition) throw new Exception(label); checks++; }
        private static void Reject(Action action, string label)
        {
            try { action(); }
            catch (HistoryResolutionException) { checks++; return; }
            throw new Exception("Expected local history rejection: " + label);
        }
        private static string Hash(string path)
        { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))); }
        private static Dictionary<string, string> Fingerprints(string directory)
        { return Directory.GetFiles(directory, "*.json", SearchOption.AllDirectories).ToDictionary(p => p, Hash, StringComparer.OrdinalIgnoreCase); }
        private static void Unchanged(Dictionary<string, string> before, string label)
        { Check(before.All(p => File.Exists(p.Key) && Hash(p.Key) == p.Value), label); }
        private static Dictionary<string, object> Entry(string patch, string precision, string lower, string upper, string announced = null)
        {
            return new Dictionary<string, object> {
                { "Patch", patch }, { "ReleaseDate", T(lower).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
                { "Precision", precision }, { "SourceTimeZone", "UTC" }, { "DateBoundaryUtc", lower },
                { "DateEndUtc", upper }, { "AnnouncedAtUtc", announced },
                { "Sources", new[] { "https://www.dota2.com/patches/" + patch } }
            };
        }
        private static string Catalog(string name, params Dictionary<string, object>[] patches)
        {
            string file = Path.Combine(root, name + ".json");
            File.WriteAllText(file, Json.Serialize(new { SchemaVersion = 1, AppId = "570", Patches = patches }));
            return file;
        }
        private static HistoryAnchorStore.Anchor Chain()
        {
            var state = new HistoryDepotState { CompleteBaseline = true };
            state.Values.Add("depots/1/manifests/public/gid", "4000");
            state.Values.Add("depots/2/manifests/public/gid", "9000");
            var baseline = new HistoryBaseline { BuildId = "40", PublishedAtUtc = Head, SourceUrl = "https://steamdb.info/app/570/depots/?branch=public", State = state };
            string[] ids = { "400", "300", "200", "100", "50" }, builds = { "40", "10", "20", "10", "5", "1" }, gids = { "4000", "3000", "2000", "1000", "500", "100" };
            var changes = new List<HistoryChange>();
            for (int i = 0; i < ids.Length; i++) {
                DateTimeOffset time = Head.AddDays(-i);
                var change = new HistoryChange { ChangeId = ids[i], ObservedAtUtc = time.AddSeconds(5), ParsedAllDepotChanges = true,
                    SourceUrl = "https://steamdb.info/app/570/history/?changeid=" + ids[i],
                    Publication = new HistoryBuildEvent { BuildId = builds[i], PreviousBuildId = builds[i + 1], PublishedAtUtc = time,
                        PreviousPublishedAtUtc = time.AddDays(-1), ChangeId = ids[i], IsPublic = true } };
                change.Deltas.Add(new HistoryDelta { Path = "depots/1/manifests/public/gid", NewExists = true, OldExists = true, NewValue = gids[i], OldValue = gids[i + 1] });
                changes.Add(change);
            }
            return new HistoryAnchorStore.Anchor { Identity = HistoryAnchorStore.Identity(baseline), Provenance = "BrowserBaselineAndContinuousHistory", Baseline = baseline, Changes = changes };
        }
        private static string Save(string name, HistoryAnchorStore.Anchor anchor)
        {
            string directory = Path.Combine(root, name); Directory.CreateDirectory(directory);
            new HistoryAnchorStore(directory).Save(new[] { anchor });
            new HistoryCacheStore(directory).Save(anchor.Baseline, anchor.Changes);
            return directory;
        }
        private static LocalPatchQueryResolver Resolver(string file, string archive, string evidence = null, params string[] otherArchives)
        {
            var archives = new List<LocalArchiveResolver> { new LocalArchiveResolver(archive) };
            archives.AddRange(otherArchives.Select(p => new LocalArchiveResolver(p)));
            return new LocalPatchQueryResolver(new LocalPatchDateCatalog(file), new LocalPatchEvidenceStore(evidence ?? Path.Combine(root, "absent-evidence")), archives.ToArray());
        }
        private static ResolvedHistoryPlan Resolve(LocalPatchQueryResolver resolver, string patch, HistoryTimeDirection direction = HistoryTimeDirection.Before)
        { return resolver.ResolveAsync(new HistoryQuery { Patch = patch, TimeDirection = direction, Language = "schinese" }, CancellationToken.None, null).GetAwaiter().GetResult(); }
        private static void Plan(ResolvedHistoryPlan plan, string build, string change, string gid, string patch, HistoryTimeDirection direction, string label)
        {
            Check(plan != null && plan.Complete && plan.BuildId == build && plan.SourceUrl.EndsWith("=" + change), label + ": exact event");
            Check(plan.Depots.Count == 2 && plan.Depots.Single(d => d.DepotId == "1").ManifestId == gid && plan.Depots.Single(d => d.DepotId == "2").ManifestId == "9000", label + ": complete changed and retained depot set");
            Check(plan.Patch == patch && plan.PatchMatchKind == "LocalReleaseDate" + direction + "Candidate", label + ": direction retained without first-release claim");
            Check(!String.IsNullOrWhiteSpace(plan.PatchDateDescription) && plan.Limitations.Count > 0, label + ": method and limitations retained");
        }
        private static void SyntheticCases()
        {
            string archive = Save("synthetic", Chain()); var original = Fingerprints(archive);
            string catalog = Catalog("synthetic-catalog",
                Entry("7.01", "announcement-time", "2020-01-02T00:00:00Z", "2020-01-04T00:00:00Z", "2020-01-03T00:00:00Z"),
                Entry("7.02", "announcement-time", "2020-01-01T00:00:00Z", "2020-01-03T00:00:00Z", "2020-01-02T23:59:59Z"),
                Entry("7.03", "date-only", "2020-01-02T00:00:00Z", "2020-01-03T00:00:00Z"),
                Entry("7.04a", "date-only", "2020-01-02T00:00:01Z", "2020-01-03T00:00:00Z"),
                Entry("7.05", "announcement-time", "2020-01-01T00:00:00Z", "2020-01-03T00:00:00Z", "2020-01-01T12:00:00Z"),
                Entry("7.06", "date-only", "2020-01-02T08:00:00+08:00", "2020-01-03T08:00:00+08:00"),
                Entry("7.07", "announcement-time", "2020-01-03T00:00:00Z", "2020-01-05T00:00:00Z", "2020-01-04T12:00:00Z"),
                Entry("7.08", "announcement-time", "2020-01-03T00:00:00Z", "2020-01-05T00:00:00Z", "2020-01-04T00:00:00Z"),
                Entry("7.09", "announcement-time", "2019-12-30T00:00:00Z", "2020-01-01T00:00:00Z", "2019-12-31T00:00:00Z"));
            Check(new LocalPatchDateCatalog(catalog).Entries.Count == 9, "catalog entries retained");
            var resolver = Resolver(catalog, archive);
            Plan(Resolve(resolver, "7.01", HistoryTimeDirection.Before), "20", "200", "2000", "7.01", HistoryTimeDirection.Before, "announcement equality excluded before");
            Plan(Resolve(resolver, "7.01", HistoryTimeDirection.After), "40", "400", "4000", "7.01", HistoryTimeDirection.After, "announcement equality excluded after");
            Plan(Resolve(resolver, "7.02", HistoryTimeDirection.Before), "20", "200", "2000", "7.02", HistoryTimeDirection.Before, "closer future event cannot override before");
            Plan(Resolve(resolver, "7.02", HistoryTimeDirection.After), "10", "300", "3000", "7.02", HistoryTimeDirection.After, "after finds one-second-later event with repeated Build ID");
            Plan(Resolve(resolver, "7.03", HistoryTimeDirection.Before), "10", "100", "1000", "7.03", HistoryTimeDirection.Before, "date-only start is strict before anchor");
            Plan(Resolve(resolver, "7.03", HistoryTimeDirection.After), "10", "300", "3000", "7.03", HistoryTimeDirection.After, "date-only after excludes equal start and permits end boundary");
            Plan(Resolve(resolver, "7.04A", HistoryTimeDirection.Before), "20", "200", "2000", "7.04a", HistoryTimeDirection.Before, "letter patch normalized");
            Plan(Resolve(resolver, "7.04a", HistoryTimeDirection.After), "10", "300", "3000", "7.04a", HistoryTimeDirection.After, "next event outside former date window");
            Plan(Resolve(resolver, "7.06", HistoryTimeDirection.Before), "10", "100", "1000", "7.06", HistoryTimeDirection.Before, "date-boundary timezone respected before");
            Plan(Resolve(resolver, "7.06", HistoryTimeDirection.After), "10", "300", "3000", "7.06", HistoryTimeDirection.After, "date-boundary timezone respected after");
            Plan(Resolve(resolver, "7.08", HistoryTimeDirection.Before), "10", "300", "3000", "7.08", HistoryTimeDirection.Before, "upper equality excluded before");
            Reject(() => Resolve(resolver, "7.08", HistoryTimeDirection.After), "no later event cannot return equal or opposite side");
            Reject(() => Resolve(resolver, "7.09", HistoryTimeDirection.Before), "no earlier event cannot return equal or opposite side");
            Plan(Resolve(resolver, "7.09", HistoryTimeDirection.After), "10", "100", "1000", "7.09", HistoryTimeDirection.After, "oldest equal event proves lower coverage for after");
            var gap = Chain();
            for (int i = 2; i < gap.Changes.Count; i++) gap.Changes[i].Publication.PublishedAtUtc = T("2019-12-31T00:00:00Z").AddDays(2 - i);
            for (int i = 0; i < gap.Changes.Count - 1; i++) gap.Changes[i].Publication.PreviousPublishedAtUtc = gap.Changes[i + 1].Publication.PublishedAtUtc;
            gap.Changes.Last().Publication.PreviousPublishedAtUtc = T("2019-12-28T00:00:00Z");
            var gapResolver = Resolver(catalog, Save("gap", gap));
            Plan(Resolve(gapResolver, "7.05", HistoryTimeDirection.Before), "20", "200", "2000", "7.05", HistoryTimeDirection.Before, "preceding event more than24h from announcement permitted");
            Plan(Resolve(gapResolver, "7.05", HistoryTimeDirection.After), "10", "300", "3000", "7.05", HistoryTimeDirection.After, "following event more than24h from announcement permitted");
            foreach (HistoryTimeDirection direction in new[] { HistoryTimeDirection.Before, HistoryTimeDirection.After }) {
                Reject(() => Resolve(resolver, "7.07", direction), "outside verified upper coverage " + direction);
                Reject(() => Resolve(resolver, "7.99", direction), "unknown patch stops locally " + direction);
                Reject(() => Resolve(Resolver(catalog, Path.Combine(root, "absent-archive")), "7.03", direction), "missing archive stops locally " + direction);
                bool cancelled = false;
                try { resolver.ResolveAsync(new HistoryQuery { Patch = "7.03", TimeDirection = direction }, new CancellationToken(true), null).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled, "cancelled query yields no plan " + direction);
            }
            Reject(() => Resolve(resolver, "7.04"), "base patch cannot steal letter-patch date");
            Reject(() => Resolve(resolver, "7.01", (HistoryTimeDirection)42), "invalid direction rejected");
            Reject(() => resolver.ResolveAsync(new HistoryQuery { RequestedUtc = Head }, CancellationToken.None, null).GetAwaiter().GetResult(), "timestamp-only query rejected");
            Reject(() => resolver.ResolveAsync(new HistoryQuery { Patch = "7.01", RequestedUtc = Head }, CancellationToken.None, null).GetAwaiter().GetResult(), "mixed patch and timestamp rejected");
            Plan(Resolve(Resolver(catalog, Path.Combine(root, "absent-archive"), null, archive), "7.03"), "10", "100", "1000", "7.03", HistoryTimeDirection.Before, "next local archive may fill clean miss in same direction");
            var bad = Chain(); bad.Changes.Last().Deltas[0].NewValue = "9999";
            Reject(() => Resolve(Resolver(catalog, Save("damaged", bad), null, archive), "7.03"), "corrupt archive cannot fall through to another answer");
            string damagedEvidence = Path.Combine(root, "damaged-evidence"); Directory.CreateDirectory(damagedEvidence);
            File.WriteAllText(Path.Combine(damagedEvidence, "resource-invalid.json"), "{broken");
            Plan(Resolve(Resolver(catalog, archive, damagedEvidence), "7.03"), "10", "100", "1000", "7.03", HistoryTimeDirection.Before, "unused first-release evidence cannot affect directional lookup");
            var shortened = Chain(); shortened.Changes.RemoveRange(2, 3);
            string shortArchive = Save("no-lower-proof", shortened);
            Reject(() => Resolve(Resolver(catalog, shortArchive), "7.03", HistoryTimeDirection.Before), "partial history no preceding candidate");
            Reject(() => Resolve(Resolver(catalog, shortArchive), "7.03", HistoryTimeDirection.After), "future candidate alone cannot prove first event after uncovered anchor");
            Check(new LocalArchiveResolver(archive).ResolveAsync(new HistoryQuery { Patch = "7.03" }, CancellationToken.None, null).GetAwaiter().GetResult() == null, "raw archive patch API still needs explicit proof");
            Unchanged(original, "synthetic archive remains read-only");
        }
        private static void InvalidCatalogCases()
        {
            Reject(() => new LocalPatchDateCatalog(Path.Combine(root, "missing-catalog.json")).Find("7.01"), "missing date catalog is local error");
            string broken = Path.Combine(root, "broken.json"); File.WriteAllText(broken, "{broken");
            Reject(() => new LocalPatchDateCatalog(broken).Find("7.01"), "invalid JSON is local error");
            string wrongApp = Path.Combine(root, "wrong-app.json"); File.WriteAllText(wrongApp, Json.Serialize(new { SchemaVersion = 1, AppId = "730", Patches = new[] { Entry("7.01", "date-only", "2020-01-02T00:00:00Z", "2020-01-03T00:00:00Z") } }));
            Reject(() => new LocalPatchDateCatalog(wrongApp).Find("7.01"), "foreign game catalog rejected");
            var valid = Entry("7.01", "date-only", "2020-01-02T00:00:00Z", "2020-01-03T00:00:00Z");
            Reject(() => new LocalPatchDateCatalog(Catalog("duplicate", valid, valid)).Find("7.01"), "duplicate patch identities rejected");
            Action<string, Action<Dictionary<string, object>>> invalid = (name, mutate) => {
                var item = Entry("7.01", "date-only", "2020-01-02T00:00:00Z", "2020-01-03T00:00:00Z"); mutate(item);
                Reject(() => new LocalPatchDateCatalog(Catalog(name, item)).Find("7.01"), name);
            };
            invalid("unknown-precision", e => e["Precision"] = "exact-launch");
            invalid("reversed-window", e => e["DateEndUtc"] = "2020-01-01T00:00:00Z");
            invalid("empty-window", e => e["DateEndUtc"] = e["DateBoundaryUtc"]);
            invalid("malformed-date", e => e["ReleaseDate"] = "2020-99-99");
            invalid("unsupported-source", e => e["Sources"] = new[] { "https://example.invalid/patches/7.01" });
            invalid("missing-source", e => e["Sources"] = new string[0]);
            invalid("announcement-missing-time", e => e["Precision"] = "announcement-time");
            invalid("zone-less-time", e => e["DateBoundaryUtc"] = "2020-01-02T00:00:00");
            invalid("window-too-wide", e => e["DateEndUtc"] = "2020-01-05T00:00:00Z");
            invalid("source-credentials", e => e["Sources"] = new[] { "https://user:password@www.dota2.com/patches/7.01" });
            var ambiguous = Entry("7.01", "date-only", "2020-01-02T00:00:00Z", "2020-01-03T00:00:00Z");
            ambiguous["Ambiguities"] = new[] { "Conflicting official source dates" };
            Reject(() => Resolve(Resolver(Catalog("ambiguous", ambiguous), Path.Combine(root, "synthetic")), "7.01"), "documented official date ambiguity is not silently guessed");
            var noEnd = Entry("7.01", "date-only", "2020-01-02T00:00:00Z", null);
            Plan(Resolve(Resolver(Catalog("default-day-end", noEnd), Path.Combine(root, "synthetic")), "7.01"), "10", "100", "1000", "7.01", HistoryTimeDirection.Before, "omitted date end does not alter strict anchor");
        }
        private static void RealCases(string catalogFile, string archive, string evidence, string syncArchive)
        {
            var catalog = new LocalPatchDateCatalog(catalogFile); string catalogHash = Hash(catalogFile);
            foreach (string directory in new[] { archive, syncArchive }) {
                var before = Fingerprints(directory); var resolver = Resolver(catalogFile, directory, evidence);
                foreach (string patch in new[] { "7.00", "7.01", "7.22b" }) {
                    var dates = catalog.Find(patch); Check(dates != null, "real date contains " + patch);
                    DateTimeOffset anchor = T(dates.Precision == "announcement-time" ? dates.AnnouncedAtUtc : dates.DateBoundaryUtc);
                    foreach (HistoryTimeDirection direction in new[] { HistoryTimeDirection.Before, HistoryTimeDirection.After }) {
                        var expected = new LocalArchiveResolver(directory).ResolveAsync(new HistoryQuery { RequestedUtc = anchor, TimeDirection = direction, Language = "schinese" }, CancellationToken.None, null).GetAwaiter().GetResult();
                        var plan = Resolve(resolver, patch, direction);
                        Check(expected != null && plan != null && plan.Complete && plan.Depots.Count > 0 && plan.Depots.All(d => !String.IsNullOrEmpty(d.ManifestId)), "real " + patch + " " + direction + " complete depots");
                        Check(plan.Patch == patch && plan.PatchMatchKind == "LocalReleaseDate" + direction + "Candidate", "real direction retained " + patch + " " + direction);
                        Check(plan.SourceUrl == expected.SourceUrl && plan.BuildId == expected.BuildId && plan.PublishedAtUtc == expected.PublishedAtUtc && Tuples(plan) == Tuples(expected), "real patch equals strict timestamp and complete tuple " + patch + " " + direction);
                        Check(direction == HistoryTimeDirection.Before ? plan.PublishedAtUtc < anchor : plan.PublishedAtUtc > anchor, "real strict side " + patch + " " + direction);
                        Check(!String.IsNullOrWhiteSpace(plan.PatchDateDescription) && plan.Limitations.Count > 0, "real date approximation stated " + patch + " " + direction);
                        realResults.Add(new { Patch = patch, Direction = direction.ToString(), AnchorUtc = anchor.ToString("o"), ArchiveDirectory = directory, BuildId = plan.BuildId, PublishedAtUtc = plan.PublishedAtUtc.ToString("o"), MatchKind = plan.PatchMatchKind, Depots = plan.Depots.Count, SourceUrl = plan.SourceUrl, Description = plan.PatchDateDescription });
                    }
                }
                Unchanged(before, "bundled or synchronized archive read-only");
            }
            Reject(() => Resolve(Resolver(catalogFile, archive, evidence), "9.99"), "unknown real patch stops locally");
            string directional = Catalog("no-proof-shortcircuit", Entry("7.00", "date-only", "2020-01-02T00:00:00Z", "2020-01-03T00:00:00Z"));
            var withProof = Resolver(directional, Path.Combine(root, "synthetic"), evidence, archive);
            Plan(Resolve(withProof, "7.00", HistoryTimeDirection.Before), "10", "100", "1000", "7.00", HistoryTimeDirection.Before, "first-release evidence cannot override before anchor");
            Plan(Resolve(withProof, "7.00", HistoryTimeDirection.After), "10", "300", "3000", "7.00", HistoryTimeDirection.After, "first-release evidence cannot override after anchor");
            Check(Hash(catalogFile) == catalogHash, "date catalog read-only");
        }
        private static string Tuples(ResolvedHistoryPlan plan)
        { return String.Join("|", plan.Depots.OrderBy(d => d.DepotId, StringComparer.Ordinal).Select(d => d.DepotId + ":" + d.ManifestId)); }
        private static void OfflineIntegrationGuards(string testDirectory)
        {
            string prototype = Path.GetFullPath(Path.Combine(testDirectory, "..", ".."));
            string source = File.ReadAllText(Path.Combine(prototype, "PrototypeForm.cs"));
            int begin = source.IndexOf("private async Task Resolve(bool downloadAfter)", StringComparison.Ordinal);
            int finish = source.IndexOf("private void DisplayPlan(", begin, StringComparison.Ordinal);
            Check(begin >= 0 && finish > begin, "GUI resolve integration is inspectable");
            string body = source.Substring(begin, finish - begin);
            int patch = body.IndexOf("if(!String.IsNullOrEmpty(query.Patch))", StringComparison.Ordinal);
            int otherwise = body.IndexOf("} else {", patch, StringComparison.Ordinal);
            Check(patch >= 0 && otherwise > patch, "GUI separates local version path from timestamp route");
            string branch = body.Substring(patch, otherwise - patch);
            Check(branch.Contains("localPatchResolver.ResolveAsync") && !Regex.IsMatch(branch, @"EnsureBrowserReady|resolver\.ResolveAsync|NavigateAsync|FetchOfficial|HttpClient|WebClient"), "GUI patch branch only calls offline patch resolver");
            int fallback = body.IndexOf("if(!localHit)", StringComparison.Ordinal);
            int browser = body.IndexOf("await EnsureBrowserReady()", fallback, StringComparison.Ordinal);
            Check(fallback >= 0 && browser > fallback && Regex.IsMatch(body.Substring(fallback, browser - fallback), @"if\(!String\.IsNullOrEmpty\(query\.Patch\)\)\s*throw\s+new\s+HistoryResolutionException"), "GUI local miss rejects patch before browser initialization");
            Check(source.Contains("plan.PatchDateDescription") && source.Contains("LocalReleaseDate"), "GUI displays date method and candidate distinction");
            string adapter = File.ReadAllText(Path.Combine(prototype, "LocalPatchDates.cs"));
            Check(!Regex.IsMatch(adapter, @"new\s+(?:HttpClient|WebClient|OfficialPatchResolver)|FetchOfficialAsync|EnsureBrowserReady|\.NavigateAsync|\.CaptureAsync|\.ClickAsync"), "date adapter has no network or browser call site");
            Check(typeof(LocalPatchQueryResolver).GetConstructors().All(c => c.GetParameters().All(p => !typeof(IHistoryBrowser).IsAssignableFrom(p.ParameterType))), "date resolver cannot receive a browser dependency");
        }
        public static int Main(string[] args)
        {
            try {
                root = Path.Combine(args[0], "runs", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
                SyntheticCases(); InvalidCatalogCases(); RealCases(args[1], args[2], args[3], args[4]); OfflineIntegrationGuards(args[0]);
                var report = new { Result = "Passed", Assertions = checks, Scope = "Offline directional patch-date lookup; strict boundaries, complete replay, real patch/time equivalence and errors; no browser, Steam request, client download or game launch", RealCases = realResults, Utc = DateTime.UtcNow.ToString("o") };
                string output = Json.Serialize(report); File.WriteAllText(Path.Combine(args[0], "results.json"), output); Console.WriteLine(output); return 0;
            } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
    }
}
