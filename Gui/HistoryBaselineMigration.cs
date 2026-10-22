using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace DotaArchive
{
    // Offline recovery from an original, normally captured complete webpage.
    // This never accepts a manually entered Depot map and never changes its inputs.
    internal static class HistoryBaselineMigration
    {
        public sealed class Report
        {
            public string CapturePath { get; set; }
            public string CaptureSha256 { get; set; }
            public string HistoryCaptureSha256 { get; set; }
            public string BaselineSourceUrl { get; set; }
            public string BuildId { get; set; }
            public string PublishedAtUtc { get; set; }
            public bool SameBuildAndTime { get; set; }
            public bool OriginalGameFieldsEqual { get; set; }
            public bool FullPrefixReplayPassed { get; set; }
            public List<string> OldIdentities { get; set; }
            public List<string> NewIdentities { get; set; }
            public int OriginalChanges { get; set; }
            public int ValidatedChanges { get; set; }
            public string LastChangeId { get; set; }
            public string OutputDirectory { get; set; }
        }

        internal static Report Prepare(string capturePath, string sourceDirectory, string outputDirectory, string historyCapturePath)
        {
            capturePath = Path.GetFullPath(capturePath);
            sourceDirectory = Path.GetFullPath(sourceDirectory);
            outputDirectory = Path.GetFullPath(outputDirectory);
            if (String.Equals(sourceDirectory.TrimEnd(Path.DirectorySeparatorChar), outputDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
                Directory.Exists(outputDirectory) && Directory.EnumerateFileSystemEntries(outputDirectory).Any())
                throw new HistoryResolutionException("迁移输出必须是新的空目录；原始缓存保持不变。");
            string v1File = Path.Combine(sourceDirectory, "verified-public-history-v1.json");
            string v2File = Path.Combine(sourceDirectory, "verified-public-anchors-v2.json");
            string oldV1Hash = HashFile(v1File), oldV2Hash = HashFile(v2File);
            var parser = new SteamDbHistoryParser();
            string captureHash;
            HistoryBaseline fresh = parser.ParseBaseline(ReadSnapshot(capturePath, out captureHash));
            var oldAnchors = new HistoryAnchorStore(sourceDirectory).Load();
            HistoryCacheStore.CacheData oldV1 = new HistoryCacheStore(sourceDirectory).Load();
            var verifier = new OnlineHistoryResolver(new NoBrowser(), null, null, false);
            var report = new Report { CapturePath = capturePath, CaptureSha256 = captureHash, BaselineSourceUrl = fresh.SourceUrl,
                BuildId = fresh.BuildId, PublishedAtUtc = fresh.PublishedAtUtc.ToUniversalTime().ToString("o"),
                OldIdentities = new List<string>(), NewIdentities = new List<string>(), OutputDirectory = outputDirectory };
            var prepared = new List<HistoryAnchorStore.Anchor>();
            foreach (HistoryAnchorStore.Anchor old in oldAnchors)
            {
                verifier.ValidateAnchorAsync(old, CancellationToken.None).GetAwaiter().GetResult();
                prepared.Add(Upgrade(old, fresh, report));
            }
            HistoryAnchorStore.Anchor active = null;
            if (oldV1 != null)
            {
                var old = new HistoryAnchorStore.Anchor { Baseline = oldV1.Baseline, Changes = oldV1.Changes, Identity = HistoryAnchorStore.Identity(oldV1.Baseline), Provenance = "ValidatedLegacyV1Migration" };
                verifier.ValidateAnchorAsync(old, CancellationToken.None).GetAwaiter().GetResult();
                active = Upgrade(old, fresh, report);
                prepared.Add(active);
            }
            if (report.OldIdentities.Count == 0) throw new HistoryResolutionException("原始页面与任何缓存基线的 Build 和发布时间均不匹配，未迁移。");
            var combined = new List<HistoryAnchorStore.Anchor>();
            foreach (var group in prepared.GroupBy(a => HistoryAnchorStore.Identity(a.Baseline)))
            {
                HistoryAnchorStore.Anchor longest = group.OrderByDescending(a => a.Changes.Count).First();
                foreach (HistoryAnchorStore.Anchor other in group)
                    for (int i = 0; i < other.Changes.Count; i++)
                        if (!SameChange(longest.Changes[i], other.Changes[i])) throw new HistoryResolutionException("同一基线的缓存原始历史前缀不一致，未迁移。");
                combined.Add(longest);
            }
            if (active == null) active = combined.Where(a => a.Baseline.BuildId == fresh.BuildId && a.Baseline.PublishedAtUtc == fresh.PublishedAtUtc).OrderByDescending(a => a.Changes.Count).First();
            else active = combined.Single(a => HistoryAnchorStore.Identity(a.Baseline) == HistoryAnchorStore.Identity(active.Baseline));
            report.OriginalChanges = active.Changes.Count;
            if (!String.IsNullOrWhiteSpace(historyCapturePath))
            {
                string historyHash;
                HistoryListing listing = parser.ParseHistory(ReadSnapshot(Path.GetFullPath(historyCapturePath), out historyHash));
                ExtendWithOverlap(active, listing);
                report.HistoryCaptureSha256 = historyHash;
            }
            // Every chain, including the complete remaining captured batch, passes
            // the production reducer's Build/time/manifest/configuration checks.
            foreach (HistoryAnchorStore.Anchor anchor in combined)
                verifier.ValidateAnchorAsync(anchor, CancellationToken.None).GetAwaiter().GetResult();
            if (oldV1Hash != HashFile(v1File) || oldV2Hash != HashFile(v2File))
                throw new HistoryResolutionException("迁移期间原始缓存发生变化；未写入候选结果，请使用稳定副本重试。");
            report.SameBuildAndTime = true; report.OriginalGameFieldsEqual = true; report.FullPrefixReplayPassed = true;
            report.ValidatedChanges = active.Changes.Count; report.LastChangeId = active.Changes.Last().ChangeId;
            Directory.CreateDirectory(outputDirectory);
            new HistoryAnchorStore(outputDirectory).Save(combined);
            new HistoryCacheStore(outputDirectory).Save(active.Baseline, active.Changes);
            File.WriteAllText(Path.Combine(outputDirectory, "baseline-migration-report.json"), Serializer().Serialize(report), new UTF8Encoding(false));
            return report;
        }

        private static HistoryAnchorStore.Anchor Upgrade(HistoryAnchorStore.Anchor old, HistoryBaseline fresh, Report report)
        {
            if (old.Baseline.BuildId != fresh.BuildId || old.Baseline.PublishedAtUtc != fresh.PublishedAtUtc) return old;
            var runtimePrefixes = new HashSet<string>(fresh.State.Values.Where(p => p.Key.EndsWith("/depotfromapp", StringComparison.Ordinal) && p.Value == "228980").Select(p => p.Key.Substring(0, p.Key.Length - "depotfromapp".Length)), StringComparer.Ordinal);
            Func<string, bool> included = p => old.Baseline.State.HasSharedPrerequisiteBaseline || !runtimePrefixes.Any(prefix => p.StartsWith(prefix, StringComparison.Ordinal));
            if (!EqualMap(old.Baseline.State.Values, fresh.State.Values.Where(p => included(p.Key))) ||
                !EqualMap(old.Baseline.State.DisplayNames, fresh.State.DisplayNames.Where(p => included(p.Key))) ||
                !old.Baseline.State.KnownNamePaths.SetEquals(fresh.State.KnownNamePaths) ||
                !old.Baseline.State.KnownSharedInstallPaths.SetEquals(fresh.State.KnownSharedInstallPaths) ||
                !old.Baseline.State.KnownOptionalDlcPaths.SetEquals(fresh.State.KnownOptionalDlcPaths) ||
                old.Baseline.State.HasKnownLegacyDlcMarking != fresh.State.HasKnownLegacyDlcMarking)
                throw new HistoryResolutionException("原始页面与缓存的游戏字段或名称证据不一致；不能只凭相同 Build 补入共享来源。");
            string oldIdentity = HistoryAnchorStore.Identity(old.Baseline), newIdentity = HistoryAnchorStore.Identity(fresh);
            if (!report.OldIdentities.Contains(oldIdentity)) report.OldIdentities.Add(oldIdentity);
            if (!report.NewIdentities.Contains(newIdentity)) report.NewIdentities.Add(newIdentity);
            return new HistoryAnchorStore.Anchor { Baseline = fresh, Changes = old.Changes.ToList(), Identity = newIdentity, Provenance = old.Provenance };
        }

        private static bool EqualMap(Dictionary<string, string> left, IEnumerable<KeyValuePair<string, string>> rightValues)
        {
            var right = rightValues.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            return left.Count == right.Count && left.All(p => right.ContainsKey(p.Key) && right[p.Key] == p.Value);
        }

        private static void ExtendWithOverlap(HistoryAnchorStore.Anchor anchor, HistoryListing listing)
        {
            if (listing.Changes.Count == 0 || listing.Changes.Any(c => !c.ParsedAllDepotChanges)) throw new HistoryResolutionException("追加页面未完整解析，未迁移。");
            HistoryChange boundary = anchor.Changes.Last();
            int overlap = listing.Changes.FindIndex(c => c.ChangeId == boundary.ChangeId);
            if (overlap < 0 || !SameChange(boundary, listing.Changes[overlap])) throw new HistoryResolutionException("追加页面缺少完全一致的旧边界，未追加。");
            var old = anchor.Changes.ToDictionary(c => c.ChangeId, StringComparer.Ordinal);
            foreach (HistoryChange item in listing.Changes.Take(overlap + 1))
                if (!old.ContainsKey(item.ChangeId) || !SameChange(old[item.ChangeId], item))
                    throw new HistoryResolutionException("追加页面与已验证缓存的重叠记录不一致，未追加。");
            foreach (HistoryChange item in listing.Changes.Skip(overlap + 1))
            {
                if (old.ContainsKey(item.ChangeId)) throw new HistoryResolutionException("追加页面包含重复旧记录，未追加。");
                anchor.Changes.Add(item);
            }
        }

        private static bool SameChange(HistoryChange a, HistoryChange b)
        {
            if (a.ChangeId != b.ChangeId || a.ObservedAtUtc != b.ObservedAtUtc || a.ParsedAllDepotChanges != b.ParsedAllDepotChanges || a.Deltas.Count != b.Deltas.Count) return false;
            if (!a.Deltas.All(x => b.Deltas.Any(y => x.Path == y.Path && x.OldExists == y.OldExists && x.NewExists == y.NewExists && x.OldValue == y.OldValue && x.NewValue == y.NewValue))) return false;
            if (a.Publication == null || b.Publication == null) return a.Publication == b.Publication;
            return a.Publication.BuildId == b.Publication.BuildId && a.Publication.PreviousBuildId == b.Publication.PreviousBuildId && a.Publication.PublishedAtUtc == b.Publication.PublishedAtUtc && a.Publication.PreviousPublishedAtUtc == b.Publication.PreviousPublishedAtUtc && a.Publication.IsPublic == b.Publication.IsPublic;
        }

        private static HistoryPageSnapshot ReadSnapshot(string path, out string hash)
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length > HistoryAnchorStore.MaximumBytes) throw new HistoryResolutionException("原始网页快照超过大小上限。");
            hash = Hash(bytes);
            var data = Serializer().Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(bytes).TrimStart('\ufeff'));
            object url, html, title;
            if (data == null || !data.TryGetValue("Url", out url) || !(url is string) || !data.TryGetValue("Html", out html) || !(html is string)) throw new HistoryResolutionException("需要原始浏览器 JSON 页面快照。");
            data.TryGetValue("Title", out title);
            return new HistoryPageSnapshot { Url = (string)url, Html = (string)html, Title = title as string };
        }
        private static string HashFile(string path) { return File.Exists(path) ? Hash(File.ReadAllBytes(path)) : null; }
        private static string Hash(byte[] bytes) { using (var sha = SHA256.Create()) return String.Concat(sha.ComputeHash(bytes).Select(b => b.ToString("x2"))); }
        private static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength = HistoryAnchorStore.MaximumBytes, RecursionLimit = 48 }; }
        private sealed class NoBrowser : IHistoryBrowser
        {
            public Task<HistoryPageSnapshot> NavigateAsync(string url, CancellationToken token) { throw new InvalidOperationException("Offline migration cannot navigate."); }
            public Task<HistoryPageSnapshot> CaptureAsync(CancellationToken token) { throw new InvalidOperationException("Offline migration cannot capture."); }
            public Task<HistoryPageSnapshot> ClickAsync(string selector, CancellationToken token) { throw new InvalidOperationException("Offline migration cannot click."); }
        }
    }
}
