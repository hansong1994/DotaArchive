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
    // Development-only replay of public pages captured by the running browser.
    // Reference plans are compared after resolution and never become source data.
    internal static class BatchReplayVerifier
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue, RecursionLimit = 100 };
        private sealed class RecordedPage
        {
            public string File, Sha256;
            public HistoryPageSnapshot Page;
        }
        private sealed class ReplayExhaustedException : Exception { internal ReplayExhaustedException() : base("The recorded browser batches do not yet cover the requested event.") { } }
        private sealed class RecordedBrowser : IHistoryBrowser, IHistoryDateBrowser
        {
            private readonly RecordedPage baseline;
            private readonly IList<RecordedPage> history;
            private int index;
            internal string LastFile;
            internal int HistoryLoads;
            internal RecordedBrowser(RecordedPage baseline, IList<RecordedPage> history) { this.baseline = baseline; this.history = history; }
            public Task<HistoryPageSnapshot> NavigateAsync(string url, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                if (new Uri(url).AbsolutePath == "/app/570/depots/") { LastFile = baseline.File; return Task.FromResult(baseline.Page); }
                if (new Uri(url).AbsolutePath == "/app/570/history/") return Next(token);
                throw new InvalidOperationException("Replay has no recorded source for " + url);
            }
            public Task<HistoryPageSnapshot> ClickAsync(string selector, CancellationToken token) { return Next(token); }
            public Task<HistoryPageSnapshot> LoadHistoryBeforeAsync(DateTime date, CancellationToken token) { return Next(token); }
            public Task<HistoryPageSnapshot> CaptureAsync(CancellationToken token) { throw new NotSupportedException(); }
            private Task<HistoryPageSnapshot> Next(CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                if (index >= history.Count) throw new ReplayExhaustedException();
                RecordedPage record = history[index++]; LastFile = record.File; HistoryLoads++;
                return Task.FromResult(record.Page);
            }
        }

        public static int Main(string[] args)
        {
            string exportedCacheDirectory = null;
            var positional = new List<string>();
            for (int index = 0; index < args.Length; index++)
            {
                if (args[index] == "--cache-dir")
                {
                    if (++index >= args.Length) { Console.Error.WriteLine("--cache-dir needs a directory."); return 1; }
                    exportedCacheDirectory = Path.GetFullPath(args[index]);
                }
                else positional.Add(args[index]);
            }
            args = positional.ToArray();
            if (args.Length < 2) { Console.Error.WriteLine("BatchReplayVerifier <batch-directory> <report.json> [reference-plan.json] [requested-time-with-offset] [--cache-dir <new-cache-directory>]"); return 1; }
            var report = new Dictionary<string, object> { { "kind", "recorded-public-browser-replay" }, { "generatedAtUtc", DateTimeOffset.UtcNow.ToString("o") }, { "referenceUsedAsResolverInput", false }, { "liveFinalBaselineRereadProvedByReplay", false } };
            RecordedBrowser browser = null;
            int exitCode = 1;
            try
            {
                if (exportedCacheDirectory != null && File.Exists(Path.Combine(exportedCacheDirectory, "verified-public-history-v1.json")))
                    throw new InvalidOperationException("Replay export requires a destination with no existing history cache; existing records cannot become replay input.");
                var parser = new SteamDbHistoryParser();
                List<RecordedPage> files = Directory.GetFiles(Path.GetFullPath(args[0]), "*.json").Select(Read).OrderBy(r => r.Page.CapturedAtUtc).ThenBy(r => r.File, StringComparer.Ordinal).ToList();
                List<RecordedPage> baselines = files.Where(r => new Uri(r.Page.Url).AbsolutePath == "/app/570/depots/").ToList();
                List<RecordedPage> histories = files.Where(r => new Uri(r.Page.Url).AbsolutePath == "/app/570/history/").ToList();
                if (baselines.Count == 0 || histories.Count == 0) throw new ReplayExhaustedException();
                HistoryBaseline baseline = parser.ParseBaseline(baselines[0].Page);
                foreach (RecordedPage candidate in baselines.Skip(1))
                {
                    HistoryBaseline other = parser.ParseBaseline(candidate.Page);
                    if (baseline.BuildId != other.BuildId || baseline.PublishedAtUtc != other.PublishedAtUtc || baseline.State.Values.Count != other.State.Values.Count || baseline.State.Values.Any(p => other.State.Value(p.Key) != p.Value))
                        throw new HistoryResolutionException("Recorded baseline changed between runs. Select batches from a single unchanged baseline before replaying.");
                }
                var batches = new List<object>();
                foreach (RecordedPage record in histories)
                {
                    HistoryListing listing = parser.ParseHistory(record.Page);
                    batches.Add(new { file = record.File, sha256 = record.Sha256, capturedAtUtc = record.Page.CapturedAtUtc.ToString("o"), changes = listing.Changes.Count, firstChangeId = listing.Changes.FirstOrDefault() == null ? null : listing.Changes.First().ChangeId, lastChangeId = listing.Changes.LastOrDefault() == null ? null : listing.Changes.Last().ChangeId, oldestObservedAtUtc = listing.Changes.Count == 0 ? null : listing.Changes.Last().ObservedAtUtc.ToString("o"), unknownFields = listing.Changes.Where(c => !c.ParsedAllDepotChanges).Select(c => new { changeId = c.ChangeId, fields = c.UnparsedChanges }).ToArray() });
                }
                report["baseline"] = new { file = baselines[0].File, sha256 = baselines[0].Sha256, buildId = baseline.BuildId, publishedAtUtc = baseline.PublishedAtUtc.ToString("o"), fields = baseline.State.Values.Count };
                report["batches"] = batches;
                DateTimeOffset requested = args.Length > 3 ? DateTimeOffset.Parse(args[3], CultureInfo.InvariantCulture) : DateTimeOffset.Parse("2019-06-06T12:00:00+08:00", CultureInfo.InvariantCulture);
                report["requestedUtc"] = requested.ToUniversalTime().ToString("o");
                browser = new RecordedBrowser(baselines[0], histories);
                string lastProgress = null;
                ResolvedHistoryPlan plan = new OnlineHistoryResolver(browser, null, exportedCacheDirectory, false).ResolveAsync(new HistoryQuery { RequestedUtc = requested }, CancellationToken.None, message => { lastProgress = message; report["lastProgress"] = message; }).GetAwaiter().GetResult();
                report["resolvedPlan"] = PlanSummary(plan);
                report["historyBatchesConsumed"] = browser.HistoryLoads;
                if (!plan.Complete || plan.PublishedAtUtc >= requested) throw new Exception("Resolved event did not satisfy the strictly-before timestamp condition.");

                // Boundary assertions are independent resolver runs over only the same captured pages.
                var exactBrowser = new RecordedBrowser(baselines[0], histories);
                ResolvedHistoryPlan exact = new OnlineHistoryResolver(exactBrowser, null, null, false).ResolveAsync(new HistoryQuery { RequestedUtc = plan.PublishedAtUtc }, CancellationToken.None, null).GetAwaiter().GetResult();
                if (exact.PublishedAtUtc >= plan.PublishedAtUtc) throw new Exception("Equal publication timestamp did not select a strictly older event.");
                var afterBrowser = new RecordedBrowser(baselines[0], histories);
                ResolvedHistoryPlan after = new OnlineHistoryResolver(afterBrowser, null, null, false).ResolveAsync(new HistoryQuery { RequestedUtc = plan.PublishedAtUtc.AddTicks(1) }, CancellationToken.None, null).GetAwaiter().GetResult();
                if (after.SourceUrl != plan.SourceUrl || after.BuildId != plan.BuildId || after.PublishedAtUtc != plan.PublishedAtUtc) throw new Exception("One tick after publication did not select this event.");
                report["strictBoundaryChecks"] = new { equalitySelectsEarlier = true, earlierBuildId = exact.BuildId, earlierPublishedAtUtc = exact.PublishedAtUtc.ToString("o"), oneTickAfterSelectsSame = true };

                if (args.Length > 2 && !String.IsNullOrWhiteSpace(args[2]))
                {
                    // This read happens only after successful source resolution and boundary checks.
                    Dictionary<string, object> reference = Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.GetFullPath(args[2]), Encoding.UTF8));
                    string expectedBuild = Convert.ToString(reference["buildId"], CultureInfo.InvariantCulture);
                    var expectedDepots = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (object raw in (System.Collections.IEnumerable)reference["depots"])
                    {
                        var entry = (Dictionary<string, object>)raw;
                        expectedDepots.Add(Convert.ToString(entry["depotId"], CultureInfo.InvariantCulture), Convert.ToString(entry["manifestId"], CultureInfo.InvariantCulture));
                    }
                    DateTimeOffset expectedTime = DateTimeOffset.Parse(Convert.ToString(reference["publishedAtUtc"], CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
                    bool same = expectedBuild == plan.BuildId && expectedTime == plan.PublishedAtUtc && expectedDepots.Count == plan.Depots.Count && plan.Depots.All(d => expectedDepots.ContainsKey(d.DepotId) && expectedDepots[d.DepotId] == d.ManifestId);
                    report["referenceComparison"] = new { file = Path.GetFullPath(args[2]), buildId = expectedBuild, identicalBuildTimeAndDepotManifestSet = same };
                    if (!same) throw new Exception("Resolved Build/time/Depot/Manifest set differs from the independently checked reference plan.");
                }
                report["status"] = "passed"; exitCode = 0;
                Console.WriteLine("PASS recorded replay: Build " + plan.BuildId + ", " + plan.Depots.Count + " depots; strict time boundaries verified.");
            }
            catch (ReplayExhaustedException ex) { report["status"] = "incomplete"; report["reason"] = ex.Message; exitCode = 2; Console.WriteLine("INCOMPLETE: waiting for older recorded browser batches."); }
            catch (Exception ex) { report["status"] = "failed"; report["error"] = ex.GetType().FullName + ": " + ex.Message; Console.Error.WriteLine("FAILED: " + ex.Message); }
            if (exportedCacheDirectory != null)
            {
                report["cacheExportDirectory"] = exportedCacheDirectory;
                try
                {
                    HistoryCacheStore.CacheData exported = new HistoryCacheStore(exportedCacheDirectory).Load();
                    if (exported != null) report["cacheExport"] = new { verifiedChanges = exported.Changes.Count, baselineBuildId = exported.Baseline.BuildId, oldestChangeId = exported.Changes.LastOrDefault() == null ? null : exported.Changes.Last().ChangeId, oldestObservedAtUtc = exported.Changes.LastOrDefault() == null ? null : exported.Changes.Last().ObservedAtUtc.ToString("o") };
                }
                catch (Exception ex) { report["cacheExportReadError"] = ex.Message; }
            }
            if (browser != null) { report["lastConsumedSnapshot"] = browser.LastFile; report["historyBatchesConsumed"] = browser.HistoryLoads; }
            string output = Path.GetFullPath(args[1]); Directory.CreateDirectory(Path.GetDirectoryName(output)); File.WriteAllText(output, Json.Serialize(report), new UTF8Encoding(false));
            return exitCode;
        }

        private static RecordedPage Read(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            string digest; using (var sha = SHA256.Create()) digest = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            HistoryPageSnapshot page = Json.Deserialize<HistoryPageSnapshot>(Encoding.UTF8.GetString(bytes));
            if (page == null || String.IsNullOrEmpty(page.Url)) throw new Exception("Invalid captured page: " + path);
            return new RecordedPage { File = Path.GetFullPath(path), Page = page, Sha256 = digest };
        }
        private static object PlanSummary(ResolvedHistoryPlan plan)
        {
            return new { buildId = plan.BuildId, publishedAtUtc = plan.PublishedAtUtc.ToString("o"), sourceUrl = plan.SourceUrl, complete = plan.Complete, depots = plan.Depots.Select(d => new { depotId = d.DepotId, manifestId = d.ManifestId }).ToArray(), assurance = plan.Assurance };
        }
    }
}
