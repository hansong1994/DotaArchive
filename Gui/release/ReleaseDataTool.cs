using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using HtmlAgilityPack;

namespace DotaArchive
{
    // Offline release tooling only. No network implementation or browser profile is copied.
    internal static class ReleaseDataTool
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 128 * 1024 * 1024, RecursionLimit = 128 };
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        private static int Main(string[] args)
        {
            try {
                if (args.Length == 7 && args[0] == "export") Export(args[1], args[2], args[3], args[4], args[5], args[6]);
                else if (args.Length == 2 && args[0] == "verify") Verify(args[1]);
                else throw new ArgumentException("export <bundle-history> <active-sync> <bundle-patch> <user-patch-or-empty> <patch-dates> <output-data> | verify <output-data>");
                return 0;
            } catch (Exception ex) { Console.Error.WriteLine(ex.ToString()); return 1; }
        }

        private static void Export(string bundle, string activeSync, string patchBundle, string patchUser, string patchDates, string output)
        {
            if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new IOException("Release data output must be empty.");
            Directory.CreateDirectory(output);
            string history = Path.Combine(output, "history"); Directory.CreateDirectory(history);
            foreach (string name in new[] { "verified-public-history-v1.json", "verified-public-anchors-v2.json" }) File.Copy(Path.Combine(bundle, name), Path.Combine(history, name));
            var original = ValidateHistory(history);
            // A successful, fully replayed generation is mandatory for this release.
            string sourcePointerHash = FileHash(Path.Combine(activeSync, "current.json"));
            SyncCatalogSnapshot active = new SyncCatalogStore(activeSync).LoadActive();
            if (active == null) throw new IOException("No committed synchronized generation exists.");
            if (sourcePointerHash != FileHash(Path.Combine(activeSync, "current.json"))) throw new IOException("The active catalog changed during release validation; run packaging again with a stable snapshot.");
            RequireOriginalArchive(active, history);
            string syncOutput = Path.Combine(output, "latest-sync");
            string generation = Path.Combine(syncOutput, "generations", new DirectoryInfo(active.GenerationDirectory).Name);
            Directory.CreateDirectory(generation);
            foreach (string name in new[] { "verified-public-history-v1.json", "verified-public-anchors-v2.json" }) File.Copy(Path.Combine(active.GenerationDirectory, name), Path.Combine(generation, name));
            HistoryPageSnapshot first = ProjectSnapshot(ReadSnapshot(Path.Combine(active.GenerationDirectory, "baseline-first.json")));
            HistoryPageSnapshot confirmed = ProjectSnapshot(ReadSnapshot(Path.Combine(active.GenerationDirectory, "baseline-confirmed.json")));
            var published = new SyncCatalogStore(syncOutput).Publish(generation, first, confirmed, active.VerifiedThroughUtc, active.AddedRecords);
            if (published.AnchorIdentity != active.AnchorIdentity || published.TotalRecords != active.TotalRecords || published.VerifiedThroughUtc != active.VerifiedThroughUtc) throw new IOException("Public export changed archive semantics.");
            string evidenceSource = Path.Combine(active.GenerationDirectory, "history-evidence");
            if (Directory.Exists(evidenceSource)) {
                string target = Path.Combine(output, "history-evidence"); Directory.CreateDirectory(target);
                foreach (string path in Directory.GetFiles(evidenceSource, "*.json")) {
                    HistoryPageSnapshot snapshot = ProjectSnapshot(ReadSnapshot(path));
                    Write(Path.Combine(target, "evidence-" + Hash(snapshot.Url + snapshot.CapturedAtUtc.ToString("o")) + ".json"), Snapshot(snapshot));
                }
            }
            string patches = Path.Combine(output, "patch-evidence"); Directory.CreateDirectory(patches);
            var resources = ReadResources(patchBundle);
            if (!String.IsNullOrEmpty(patchUser)) foreach (var item in ReadResources(patchUser)) resources[item.Key] = item.Value;
            var writer = new LocalPatchEvidenceStore(null, patches);
            foreach (var resource in resources.Values) {
                DateTimeOffset captured = DateTimeOffset.Parse(resource.CapturedAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                if (resource.Kind == "OfficialResponse") writer.SaveOfficialResponse(resource.Url, resource.Payload, captured);
                else {
                    var snapshot = Json.Deserialize<LocalPatchEvidenceStore.SnapshotPayload>(resource.Payload);
                    writer.SaveBrowserSnapshot(ProjectSnapshot(new HistoryPageSnapshot { Url = snapshot.Url, Title = snapshot.Title, Html = snapshot.Html, Text = snapshot.Text, CapturedAtUtc = DateTimeOffset.Parse(snapshot.CapturedAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) }));
                }
            }
            var proof = new LocalPatchEvidenceStore(patches).ResolveAsync("7.00", CancellationToken.None, null).GetAwaiter().GetResult();
            if (proof == null || proof.Build.BuildId != "1514733") throw new IOException("Packaged public evidence lost the independently proven 7.00 mapping.");
            CopyPatchDates(patchDates, Path.Combine(output, "patch-dates"));
            Write(Path.Combine(output, "catalog-provenance.json"), new {
                SchemaVersion = 1, AppId = "570", Branch = "public", ExportedAtUtc = DateTimeOffset.UtcNow.ToString("o"),
                OriginalArchiveRecords = original.Changes.Count, TotalContinuousRecords = published.TotalRecords,
                OldestChangeId = original.Changes.Last().ChangeId, OldestObservedAtUtc = original.Changes.Last().ObservedAtUtc.ToUniversalTime().ToString("o"),
                LatestBuildId = published.LatestBuildId, LatestChangeId = published.LatestChangeId,
                LatestBuildPublishedAtUtc = published.LatestBuildPublishedAtUtc.ToUniversalTime().ToString("o"),
                VerifiedThroughUtc = published.VerifiedThroughUtc.ToUniversalTime().ToString("o"), AnchorIdentity = published.AnchorIdentity,
                OriginalCurrentSha256 = sourcePointerHash,
                OriginalBaselineFirstSha256 = FileHash(Path.Combine(active.GenerationDirectory, "baseline-first.json")),
                OriginalBaselineConfirmedSha256 = FileHash(Path.Combine(active.GenerationDirectory, "baseline-confirmed.json")),
                EvidenceProjection = "Public depot/history/patch content only; original DOM account navigation, forms, scripts, cookies, profile data and logs excluded. Production parsers must preserve baseline identity and history deltas.",
                PatchResourceCount = resources.Count, ManualPatchIndex = false, WholeChainReplayVerified = true, UserProfileCopied = false,
                OldestEndpointEvidence = "User confirmed the earliest website record; not an invented automated end marker."
            });
            Verify(output);
        }

        private static HistoryCacheStore.CacheData ValidateHistory(string path)
        {
            HistoryCacheStore.CacheData cache = new HistoryCacheStore(path).Load();
            var anchors = new HistoryAnchorStore(path).Load();
            if (cache == null || anchors.Count != 1 || anchors[0].Identity != HistoryAnchorStore.Identity(cache.Baseline) || Json.Serialize(anchors[0].Changes) != Json.Serialize(cache.Changes)) throw new IOException("Packaged history files disagree.");
            var resolver = new OnlineHistoryResolver(new OfflineBrowser(), null, null, false);
            resolver.ValidateAnchorAsync(anchors[0], CancellationToken.None).GetAwaiter().GetResult();
            return cache;
        }

        private static void Verify(string output)
        {
            var history = ValidateHistory(Path.Combine(output, "history"));
            var active = new SyncCatalogStore(Path.Combine(output, "latest-sync")).LoadActive();
            if (active == null || active.TotalRecords < history.Changes.Count) throw new IOException("Missing complete synchronized release catalog.");
            RequireOriginalArchive(active, Path.Combine(output, "history"));
            var proof = new LocalPatchEvidenceStore(Path.Combine(output, "patch-evidence")).ResolveAsync("7.00", CancellationToken.None, null).GetAwaiter().GetResult();
            var local = new LocalArchiveResolver(Path.Combine(output, "latest-sync"));
            var plan = local.ResolveAsync(new HistoryQuery { BeijingTime = DateTimeOffset.Parse("2015-12-20T12:00:00+08:00", CultureInfo.InvariantCulture), Language = "schinese" }, CancellationToken.None, null).GetAwaiter().GetResult();
            if (plan == null || plan.BuildId != "906288" || plan.Depots.Count == 0) throw new IOException("Offline historical timestamp query failed.");
            // These are independent regression assertions against saved public
            // evidence, not a version index used by the production resolver.
            DateTimeOffset requested = DateTimeOffset.Parse("2015-12-20T12:00:00+08:00", CultureInfo.InvariantCulture);
            var after = local.ResolveAsync(new HistoryQuery { BeijingTime = requested, TimeDirection = HistoryTimeDirection.After, Language = "schinese" }, CancellationToken.None, null).GetAwaiter().GetResult();
            if (after == null || after.BuildId != "907474" || after.PublishedAtUtc <= requested || !after.Complete || after.Depots.Count != 10) throw new IOException("Offline nearest-after timestamp query failed.");
            var equalBefore = local.ResolveAsync(new HistoryQuery { RequestedUtc = after.PublishedAtUtc, Language = "schinese" }, CancellationToken.None, null).GetAwaiter().GetResult();
            var equalAfter = local.ResolveAsync(new HistoryQuery { RequestedUtc = after.PublishedAtUtc, TimeDirection = HistoryTimeDirection.After, Language = "schinese" }, CancellationToken.None, null).GetAwaiter().GetResult();
            if (equalBefore == null || equalBefore.BuildId != "906288" || equalBefore.PublishedAtUtc >= after.PublishedAtUtc || equalAfter == null || equalAfter.BuildId != "909005" || equalAfter.PublishedAtUtc <= after.PublishedAtUtc) throw new IOException("Offline timestamp direction equality boundary failed.");
            var noFuture = local.ResolveAsync(new HistoryQuery { RequestedUtc = active.VerifiedThroughUtc, TimeDirection = HistoryTimeDirection.After, Language = "schinese" }, CancellationToken.None, null).GetAwaiter().GetResult();
            if (noFuture != null) throw new IOException("An after query incorrectly substituted an earlier version beyond the archive's latest event.");
            var quiet = local.ResolveAsync(new HistoryQuery { RequestedUtc = active.VerifiedThroughUtc, Language = "schinese" }, CancellationToken.None, null).GetAwaiter().GetResult();
            if (quiet == null || quiet.BuildId != active.LatestBuildId) throw new IOException("Relocated verified coverage timestamp was lost.");
            if (proof == null || proof.Build.BuildId != "1514733") throw new IOException("Official 7.00 evidence replay failed.");
            var patchPlan = local.ResolveAsync(new HistoryQuery { Patch = "7.00", Language = "schinese" }, proof, CancellationToken.None, null).GetAwaiter().GetResult();
            if (patchPlan == null || patchPlan.BuildId != "1514733" || patchPlan.Depots.Count != 10) throw new IOException("Full offline 7.00 depot plan failed.");
            var early = VerifyEarlyArchive(local);
            var patchDates = VerifyPatchDates(output, local);
            Console.WriteLine(Json.Serialize(new { Passed = true, OfflineOnly = true, TotalRecords = active.TotalRecords, LatestBuildId = active.LatestBuildId, VerifiedThroughUtc = active.VerifiedThroughUtc.ToString("o"), OriginalArchiveContained = true, TimestampQueryBuild = plan.BuildId, TimestampQueryDepots = plan.Depots.Count, AfterTimestampQueryBuild = after.BuildId, AfterTimestampQueryDepots = after.Depots.Count, EqualTimeBeforeBuild = equalBefore.BuildId, EqualTimeAfterBuild = equalAfter.BuildId, NoFutureFallback = noFuture == null, SavedExactPatch700Build = patchPlan.BuildId, SavedExactPatch700Depots = patchPlan.Depots.Count, EarlyArchiveQueries = early, PatchDates = patchDates }));
        }

        private sealed class PatchDateProvenance
        {
            public int SchemaVersion { get; set; }
            public string AppId { get; set; }
            public string CatalogSha256 { get; set; }
            public int PatchCount { get; set; }
            public bool ManualBuildMapping { get; set; }
            public List<PatchDateSource> SourceFiles { get; set; }
        }
        private sealed class PatchDateSource
        {
            public string Path { get; set; }
            public string Sha256 { get; set; }
            public long Bytes { get; set; }
        }
        private static PatchDateProvenance ValidatePatchDateFiles(string path)
        {
            var provenance = Json.Deserialize<PatchDateProvenance>(File.ReadAllText(Path.Combine(path, "provenance.json"), Encoding.UTF8));
            string catalog = Path.Combine(path, "patch-release-catalog.json");
            if (provenance == null || provenance.SchemaVersion != 1 || provenance.AppId != "570" || provenance.ManualBuildMapping ||
                provenance.CatalogSha256 != FileHash(catalog) || provenance.PatchCount != new LocalPatchDateCatalog(catalog).Entries.Count ||
                provenance.SourceFiles == null || provenance.SourceFiles.Count == 0) throw new IOException("Patch date catalog provenance mismatch.");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (PatchDateSource source in provenance.SourceFiles)
            {
                if (source == null || !Regex.IsMatch(source.Path ?? "", @"^sources/[A-Za-z0-9._-]+\.(json|html)$") || !seen.Add(source.Path)) throw new IOException("Unsafe or duplicate patch date source path.");
                string sourcePath = Path.Combine(path, source.Path.Replace('/', Path.DirectorySeparatorChar));
                if (source.Bytes != new FileInfo(sourcePath).Length || source.Sha256 != FileHash(sourcePath)) throw new IOException("Patch date source evidence mismatch.");
            }
            return provenance;
        }
        private static void CopyPatchDates(string source, string destination)
        {
            PatchDateProvenance provenance = ValidatePatchDateFiles(source);
            Directory.CreateDirectory(Path.Combine(destination, "sources"));
            foreach (string name in new[] { "patch-release-catalog.json", "provenance.json" }) File.Copy(Path.Combine(source, name), Path.Combine(destination, name));
            foreach (PatchDateSource file in provenance.SourceFiles) File.Copy(Path.Combine(source, file.Path), Path.Combine(destination, file.Path));
        }
        private static object VerifyPatchDates(string output, LocalArchiveResolver local)
        {
            string path = Path.Combine(output, "patch-dates");
            PatchDateProvenance provenance = ValidatePatchDateFiles(path);
            var catalog = new LocalPatchDateCatalog(Path.Combine(path, "patch-release-catalog.json"));
            var resolver = new LocalPatchQueryResolver(catalog, new LocalPatchEvidenceStore(Path.Combine(output, "patch-evidence")), local);
            var results = new List<object>();
            foreach (string patch in new[] { "7.00", "7.01", "7.22b", "6.79" })
            {
                LocalPatchReleaseDate entry = catalog.Find(patch);
                DateTimeOffset anchor = LocalPatchDateCatalog.ReadTime(entry.Precision == "announcement-time" ? entry.AnnouncedAtUtc : entry.DateBoundaryUtc);
                foreach (HistoryTimeDirection direction in new[] { HistoryTimeDirection.Before, HistoryTimeDirection.After })
                {
                    ResolvedHistoryPlan plan = resolver.ResolveAsync(new HistoryQuery { Patch = patch, Language = "schinese", TimeDirection = direction }, CancellationToken.None, null).GetAwaiter().GetResult();
                    ResolvedHistoryPlan direct = local.ResolveAsync(new HistoryQuery { RequestedUtc = anchor, Language = "schinese", TimeDirection = direction }, CancellationToken.None, null).GetAwaiter().GetResult();
                    if (plan == null || direct == null || !plan.Complete || plan.Depots.Count == 0 || plan.Patch != patch || plan.BuildId != direct.BuildId || plan.PublishedAtUtc != direct.PublishedAtUtc || Json.Serialize(plan.Depots) != Json.Serialize(direct.Depots)) throw new IOException("Packaged offline directional patch query differs from its timestamp query: " + patch + " / " + direction);
                    if ((direction == HistoryTimeDirection.Before && plan.PublishedAtUtc >= anchor) || (direction == HistoryTimeDirection.After && plan.PublishedAtUtc <= anchor)) throw new IOException("Patch direction must strictly exclude its reference instant.");
                    string expectedKind = direction == HistoryTimeDirection.Before ? "LocalReleaseDateBeforeCandidate" : "LocalReleaseDateAfterCandidate";
                    if (plan.PatchMatchKind != expectedKind || plan.Limitations.Count == 0 || String.IsNullOrEmpty(plan.PatchDateDescription)) throw new IOException("Directional patch matching must retain its reference and identity limitations.");
                    results.Add(new { Patch = patch, Direction = direction.ToString(), AnchorUtc = anchor.ToString("o"), plan.BuildId, PublishedAtUtc = plan.PublishedAtUtc.ToString("o"), DepotCount = plan.Depots.Count, plan.Complete, plan.PatchMatchKind, plan.PatchDateDescription });
                }
            }
            bool missingRejected = false;
            try { resolver.ResolveAsync(new HistoryQuery { Patch = "9.99", Language = "schinese" }, CancellationToken.None, null).GetAwaiter().GetResult(); }
            catch (HistoryResolutionException) { missingRejected = true; }
            if (!missingRejected) throw new IOException("A missing offline patch must not silently match a different version.");
            return new { Passed = true, DateEntries = provenance.PatchCount, SourceFiles = provenance.SourceFiles.Count, ManualBuildMapping = false, MissingPatchRejected = true, NetworkUsed = false, StrictBeforeAfter = true, ExactFirstReleaseOverridesDirection = false, DateWindowCutoff = false, Queries = results };
        }

        private static List<object> VerifyEarlyArchive(LocalArchiveResolver local)
        {
            // These expected BuildIds come from the archived public events. They
            // are regression assertions, never production lookup records.
            var cases = new[] {
                new { BeijingTime = "2013-02-02T12:00:00+08:00", Direction = HistoryTimeDirection.After, BuildId = "41416" },
                new { BeijingTime = "2013-07-15T12:00:00+08:00", Direction = HistoryTimeDirection.Before, BuildId = "91042" },
                new { BeijingTime = "2013-07-16T12:00:00+08:00", Direction = HistoryTimeDirection.Before, BuildId = "91534" }
            };
            var results = new List<object>();
            foreach (var item in cases)
            {
                DateTimeOffset requested = DateTimeOffset.Parse(item.BeijingTime, CultureInfo.InvariantCulture);
                var plan = local.ResolveAsync(new HistoryQuery { BeijingTime = requested, TimeDirection = item.Direction, Language = "schinese" }, CancellationToken.None, null).GetAwaiter().GetResult();
                if (plan == null || !plan.Complete || plan.BuildId != item.BuildId || plan.Depots.Count == 0 ||
                    (item.Direction == HistoryTimeDirection.After ? plan.PublishedAtUtc <= requested : plan.PublishedAtUtc >= requested))
                    throw new IOException("Packaged early historical query failed: " + item.BeijingTime + " / " + item.Direction);
                if (item.BuildId == "41416" && plan.Depots.Count != 4)
                    throw new IOException("The first February regression query must preserve its complete four-depot plan.");
                results.Add(new { item.BeijingTime, Direction = item.Direction.ToString(), plan.BuildId, PublishedAtUtc = plan.PublishedAtUtc.ToString("o"), DepotCount = plan.Depots.Count, plan.Complete });
            }
            return results;
        }

        private static void RequireOriginalArchive(SyncCatalogSnapshot active, string history)
        {
            var required = new HistoryAnchorStore(history).Load().Single();
            var method = typeof(OnlineHistoryResolver).GetMethod("SyncContainsArchive", BindingFlags.Instance | BindingFlags.NonPublic);
            var resolver = new OnlineHistoryResolver(new OfflineBrowser(), null, null, false);
            bool contains = (bool)method.Invoke(resolver, new object[] { active.Anchors.Single(), required, CancellationToken.None });
            if (!contains) throw new IOException("Latest generation does not contain the complete original archive with the same depot state at its boundary.");
        }

        private static Dictionary<string, LocalPatchEvidenceStore.Resource> ReadResources(string directory)
        {
            // Use the production whitelist, hash, size, duplicate and payload validators.
            var method = typeof(LocalPatchEvidenceStore).GetMethod("LoadDirectory", BindingFlags.Static | BindingFlags.NonPublic);
            return (Dictionary<string, LocalPatchEvidenceStore.Resource>)method.Invoke(null, new object[] { directory, CancellationToken.None });
        }

        private static HistoryPageSnapshot ReadSnapshot(string path)
        {
            var record = Json.Deserialize<SyncCatalogStore.Snapshot>(File.ReadAllText(path, Encoding.UTF8));
            return new HistoryPageSnapshot { Url = record.Url, Title = record.Title, Html = record.Html, Text = record.Text, CapturedAtUtc = DateTimeOffset.Parse(record.CapturedAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) };
        }

        private static object Snapshot(HistoryPageSnapshot snapshot)
        { return new { snapshot.Url, snapshot.Title, snapshot.Html, snapshot.Text, CapturedAtUtc = snapshot.CapturedAtUtc.ToUniversalTime().ToString("o") }; }

        private static HistoryPageSnapshot ProjectSnapshot(HistoryPageSnapshot source)
        {
            var doc = new HtmlDocument(); doc.LoadHtml(source.Html ?? "");
            var selected = new List<HtmlNode>();
            bool baseline = source.Url == "https://steamdb.info/app/570/depots/?branch=public";
            bool history = Regex.IsMatch(source.Url ?? "", @"^https://steamdb\.info/app/570/history/\?(?:filterkey=2|changeid=[0-9]+)(?:&[A-Za-z0-9_=:-]+)*$") || source.Url == "https://steamdb.info/app/570/history/";
            bool listing = source.Url == "https://steamdb.info/app/570/patchnotes/";
            if (baseline) { var node = doc.GetElementbyId("depots"); if (node != null) selected.Add(node); }
            else if (history) selected.AddRange(doc.DocumentNode.Descendants().Where(n => HasClass(n, "panel-history")));
            else if (listing) { var node = doc.GetElementbyId("js-patchnotes") ?? doc.GetElementbyId("patchnotes"); if (node != null) selected.Add(node); }
            else if (Regex.IsMatch(source.Url ?? "", @"^https://steamdb\.info/patchnotes/[1-9][0-9]*/$")) selected.AddRange(doc.DocumentNode.Descendants().Where(n => n.Name == "article" || HasClass(n, "patchnotes-header")).Where(n => !n.Ancestors().Any(a => a.Name == "article" || HasClass(a, "patchnotes-header"))));
            if (selected.Count == 0) throw new IOException("Public release projection has no known content: " + source.Url);
            string html = "<main>" + String.Join("\n", selected.Select(n => Clean(n).OuterHtml)) + "</main>";
            var publicDoc = new HtmlDocument(); publicDoc.LoadHtml(html);
            var result = new HistoryPageSnapshot { Url = source.Url, Title = "Dota 2 public archive evidence", Html = html, Text = HtmlEntity.DeEntitize(publicDoc.DocumentNode.InnerText), CapturedAtUtc = source.CapturedAtUtc };
            var parser = new SteamDbHistoryParser();
            if (baseline && HistoryAnchorStore.Identity(parser.ParseBaseline(source)) != HistoryAnchorStore.Identity(parser.ParseBaseline(result))) throw new IOException("Baseline projection changed parsed identity.");
            if (history && Json.Serialize(parser.ParseHistory(source).Changes) != Json.Serialize(parser.ParseHistory(result).Changes)) throw new IOException("History projection changed parsed deltas.");
            if (listing) {
                var before = parser.ParsePatchListing(source).Builds; var after = parser.ParsePatchListing(result).Builds;
                if (Json.Serialize(before) != Json.Serialize(after)) throw new IOException("Patch listing projection changed parsed builds.");
            }
            return result;
        }

        private static HtmlNode Clean(HtmlNode original)
        {
            HtmlNode copy = original.CloneNode(true);
            var prohibited = new HashSet<string>(new[] { "script", "style", "nav", "aside", "footer", "form", "input", "button", "iframe", "object", "embed", "img", "svg", "textarea", "select" });
            foreach (HtmlNode child in copy.Descendants().Where(n => n.NodeType == HtmlNodeType.Comment || prohibited.Contains(n.Name)).ToList()) child.Remove();
            var attributes = new HashSet<string>(new[] { "id", "class", "href", "title", "datetime", "data-date", "data-depotid", "data-appid", "data-changeid" });
            foreach (HtmlNode node in new[] { copy }.Concat(copy.Descendants()).Where(n => n.NodeType == HtmlNodeType.Element)) {
                foreach (HtmlAttribute attribute in node.Attributes.ToList()) if (!attributes.Contains(attribute.Name)) node.Attributes.Remove(attribute);
                if (node.Attributes["href"] != null) {
                    string href = HtmlEntity.DeEntitize(node.GetAttributeValue("href", ""));
                    if (!Regex.IsMatch(href, @"^(?:/(?:app|depot|patchnotes)/[0-9]+/|https?://(?:www\.)?(?:dota2\.com|steamdb\.info|steamcommunity\.com|store\.steampowered\.com|steamstore-a\.akamaihd\.net)/)")) node.Attributes.Remove("href");
                }
            }
            return copy;
        }

        private static bool HasClass(HtmlNode node, string value) { return node.GetAttributeValue("class", "").Split(' ').Contains(value); }
        private static string Hash(string value) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Utf8.GetBytes(value))).Replace("-", "").ToLowerInvariant(); }
        private static string FileHash(string path) { using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
        private static void Write(string path, object value) { File.WriteAllText(path, Json.Serialize(value), Utf8); }
        private sealed class OfflineBrowser : IHistoryBrowser
        {
            public Task<HistoryPageSnapshot> NavigateAsync(string url, CancellationToken token) { throw new InvalidOperationException("Offline release verification cannot access a browser."); }
            public Task<HistoryPageSnapshot> CaptureAsync(CancellationToken token) { throw new InvalidOperationException("Offline release verification cannot access a browser."); }
            public Task<HistoryPageSnapshot> ClickAsync(string selector, CancellationToken token) { throw new InvalidOperationException("Offline release verification cannot access a browser."); }
        }
    }
}
