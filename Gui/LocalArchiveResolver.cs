using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DotaArchive
{
    // Read-only proof replay. A miss is null; corrupt or inconsistent evidence is
    // an error, never a reason to silently download using an unverified mapping.
    public sealed class LocalArchiveResolver
    {
        private readonly string directory;
        public LocalArchiveResolver(string archiveCacheDirectory)
        {
            if (String.IsNullOrWhiteSpace(archiveCacheDirectory)) throw new ArgumentException("需要指定本地历史归档目录。");
            directory = Path.GetFullPath(archiveCacheDirectory);
        }

        public Task<ResolvedHistoryPlan> ResolveAsync(HistoryQuery query, CancellationToken token, Action<string> progress)
        { return ResolveAsync(query, null, token, progress); }

        // The caller must obtain proof by replaying raw official evidence. Merely
        // assigning a patch number or announcement date is not a verified proof.
        public Task<ResolvedHistoryPlan> ResolveAsync(HistoryQuery query, OfficialPatchMatch proof, CancellationToken token, Action<string> progress)
        { return ResolveCoreAsync(query, proof, token, progress, false); }

        // Retained for internal evidence analysis. User timestamp and patch-date
        // queries both use the ordinary strict before/after entry point above.
        internal Task<ResolvedHistoryPlan> ResolveInclusiveAsync(HistoryQuery query, CancellationToken token, Action<string> progress)
        { return ResolveCoreAsync(query, null, token, progress, true); }

        internal Task<ResolvedHistoryPlan> ResolveNearestAsync(HistoryQuery query, CancellationToken token, Action<string> progress)
        { return ResolveCoreAsync(query, null, token, progress, true, true); }

        private async Task<ResolvedHistoryPlan> ResolveCoreAsync(HistoryQuery query, OfficialPatchMatch proof, CancellationToken token, Action<string> progress, bool includeRequestedInstant, bool nearest = false)
        {
            if (query == null) throw new ArgumentNullException("query");
            string patch = (query.Patch ?? "").Trim().ToLowerInvariant();
            DateTimeOffset? requested = query.RequestedUtc ?? query.BeijingTime;
            if ((patch.Length == 0) == !requested.HasValue) throw new HistoryResolutionException("请选择一种查询方式：版本号，或北京时间。");
            if (requested.HasValue && query.TimeDirection != HistoryTimeDirection.Before && query.TimeDirection != HistoryTimeDirection.After)
                throw new HistoryResolutionException("时间查询方向无效，请选择此前最近版本或此后最近版本。");
            if (patch.Length != 0 && !Regex.IsMatch(patch, @"^\d+\.\d{2}[a-z]?$")) throw new HistoryResolutionException("请输入完整版本号，例如 7.22b。");
            if (patch.Length == 0 && proof != null) throw new HistoryResolutionException("时间查询不能混入补丁首发依据。");
            token.ThrowIfCancellationRequested();
            if (patch.Length != 0 && proof == null) return null;
            // A legitimate article association is still only a candidate. The
            // online path may acquire stronger first-release linkage later.
            if (proof != null && proof.Kind == "OfficialArticle") return null;
            SyncCatalogSnapshot synchronized = null;
            List<HistoryAnchorStore.Anchor> loaded = await Task.Run(delegate {
                token.ThrowIfCancellationRequested();
                if (File.Exists(Path.Combine(directory, "current.json")) || String.Equals(new DirectoryInfo(directory).Name, "latest-sync", StringComparison.OrdinalIgnoreCase))
                {
                    synchronized = new SyncCatalogStore(directory).LoadActive(token);
                    return synchronized == null ? new List<HistoryAnchorStore.Anchor>() : synchronized.Anchors;
                }
                var result = new HistoryAnchorStore(directory).Load();
                HistoryCacheStore.CacheData legacy = new HistoryCacheStore(directory).Load();
                if (legacy != null && legacy.Changes.Count > 0)
                    result.Add(new HistoryAnchorStore.Anchor { Identity = HistoryAnchorStore.Identity(legacy.Baseline), Provenance = "ValidatedLegacyV1Migration", Baseline = legacy.Baseline, Changes = legacy.Changes });
                return result;
            }, token);
            if (loaded.Count == 0) return null;
            var replay = new OnlineHistoryResolver(new OfflineBrowser(), null, null, false);
            return await replay.ResolveReadOnlyArchiveAsync(loaded, requested, patch, query.Language, proof, token, progress, synchronized, query.TimeDirection, includeRequestedInstant, nearest);
        }

        private sealed class OfflineBrowser : IHistoryBrowser
        {
            public Task<HistoryPageSnapshot> NavigateAsync(string url, CancellationToken token) { throw new InvalidOperationException("本地归档解析禁止访问浏览器。"); }
            public Task<HistoryPageSnapshot> CaptureAsync(CancellationToken token) { throw new InvalidOperationException("本地归档解析禁止访问浏览器。"); }
            public Task<HistoryPageSnapshot> ClickAsync(string selector, CancellationToken token) { throw new InvalidOperationException("本地归档解析禁止访问浏览器。"); }
        }
    }

    public sealed partial class OnlineHistoryResolver
    {
        private sealed class LocalPublicEvent
        {
            internal HistoryChange Change;
            internal string Build;
            internal DateTimeOffset Time;
            internal int Index;
        }

        internal async Task<ResolvedHistoryPlan> ResolveReadOnlyArchiveAsync(List<HistoryAnchorStore.Anchor> loaded,
            DateTimeOffset? requested, string patch, string language, OfficialPatchMatch proof, CancellationToken token, Action<string> progress, SyncCatalogSnapshot synchronized = null, HistoryTimeDirection timeDirection = HistoryTimeDirection.Before, bool includeRequestedInstant = false, bool nearest = false)
        {
            Report(progress, "正在重新校验本地完整基线和连续历史证据……");
            foreach (HistoryAnchorStore.Anchor anchor in loaded)
            {
                token.ThrowIfCancellationRequested();
                await ValidateAnchorAsync(anchor, token);
                MergeAnchor(anchor); // This resolver instance has no persistent store.
            }
            if (proof != null) ValidateLocalPatchProof(proof, patch);
            return await Task.Run(delegate {
                ResolvedHistoryPlan selected = null;
                foreach (HistoryAnchorStore.Anchor anchor in anchors)
                {
                    token.ThrowIfCancellationRequested();
                    DateTimeOffset? verifiedThrough = synchronized != null && synchronized.AnchorIdentity == anchor.Identity ? synchronized.VerifiedThroughUtc : (DateTimeOffset?)null;
                    ResolvedHistoryPlan plan = ResolveLocalAnchor(anchor, requested, patch, language, proof, token, verifiedThrough, timeDirection, includeRequestedInstant, nearest);
                    if (plan == null) continue;
                    if (selected != null && !SameLocalPlan(selected, plan))
                        throw new HistoryResolutionException("多个已校验本地锚点对同一查询返回不同事件或 Depot，不能自动选择。");
                    selected = plan;
                }
                if (selected != null) Report(progress, "已从本地连续历史还原 Build " + selected.BuildId + " 的完整 Depot 组合；未访问 SteamDB。");
                return selected;
            }, token);
        }

        private ResolvedHistoryPlan ResolveLocalAnchor(HistoryAnchorStore.Anchor anchor, DateTimeOffset? requested,
            string patch, string language, OfficialPatchMatch proof, CancellationToken token, DateTimeOffset? verifiedThrough, HistoryTimeDirection timeDirection, bool includeRequestedInstant, bool nearest)
        {
            HistoryBaseline baseline = anchor.Baseline;
            if (!HasPublicTime(baseline.PublishedAtUtc)) return null;
            // Only an atomically published, fully validated synchronized generation
            // can prove the quiet period after the latest release. Bundled and
            // ordinary caches retain their original conservative upper boundary.
            DateTimeOffset upperBoundary = verifiedThrough ?? baseline.PublishedAtUtc;
            if (requested.HasValue && requested.Value > upperBoundary) return null;
            if (proof != null && proof.Kind == "OfficialReleaseWindow" && proof.AvailableByUtc.Value > upperBoundary) return null;
            var events = new List<LocalPublicEvent>();
            string build = baseline.BuildId;
            for (int index = 0; index < anchor.Changes.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                HistoryChange change = anchor.Changes[index];
                if (change.Publication == null) continue;
                DateTimeOffset time = EffectiveEventTime(change);
                if (time > baseline.PublishedAtUtc)
                    throw new HistoryResolutionException("历史事件时间晚于完整基线，无法确定本地覆盖上界。");
                events.Add(new LocalPublicEvent { Change = change, Build = change.Publication.BuildId ?? build, Time = time, Index = index });
                if (change.Publication.PreviousBuildId != null) build = change.Publication.PreviousBuildId;
            }
            LocalPublicEvent candidate;
            if (requested.HasValue)
            {
                if (nearest)
                {
                    if (!events.Any(e => e.Time <= requested.Value)) return null;
                    candidate = events.OrderBy(e => Math.Abs((e.Time - requested.Value).Ticks))
                        .ThenBy(e => e.Time > requested.Value ? 1 : 0).ThenBy(e => e.Index).FirstOrDefault();
                    // A known earlier event alone cannot rule out a nearer future
                    // update beyond the verified coverage boundary.
                    if (candidate != null && requested.Value.AddTicks(Math.Abs((candidate.Time - requested.Value).Ticks)) > upperBoundary) return null;
                }
                else if (timeDirection == HistoryTimeDirection.After)
                {
                    // A truncated archive can contain a later event without proving
                    // it was the first one after the request. Require a real lower
                    // public boundary, never infer coverage from the oldest row.
                    if (!events.Any(e => e.Time <= requested.Value)) return null;
                    candidate = events.Where(e => e.Time > requested.Value || includeRequestedInstant && e.Time == requested.Value).OrderBy(e => e.Time).ThenBy(e => e.Index).FirstOrDefault();
                }
                else candidate = events.Where(e => e.Time < requested.Value || includeRequestedInstant && e.Time == requested.Value).OrderByDescending(e => e.Time).ThenBy(e => e.Index).FirstOrDefault();
                if (candidate == null) return null;
            }
            else if (proof.Kind == "OfficialReleaseWindow")
            {
                // Both sides must be covered by real public events. The official
                // lower/upper announcement window alone cannot fill a cache gap.
                if (!events.Any(e => e.Time <= proof.NotAvailableAtUtc.Value)) return null;
                var within = events.Where(e => e.Time > proof.NotAvailableAtUtc.Value && e.Time <= proof.AvailableByUtc.Value).ToList();
                if (within.Count != 1) throw new HistoryResolutionException("本地连续历史无法证明官方窗口内只有一次公开发布，不能确定补丁首发。");
                candidate = within[0];
            }
            else
            {
                if (String.IsNullOrEmpty(proof.Build.ChangeId)) return null;
                candidate = events.FirstOrDefault(e => e.Change.ChangeId == proof.Build.ChangeId);
                if (candidate == null) return null;
            }
            if (proof != null && candidate.Build != proof.Build.BuildId)
                throw new HistoryResolutionException("官方补丁依据中的 Build 与本地精确历史变更不一致。");
            LocalPublicEvent previous = events.FirstOrDefault(e => e.Index > candidate.Index);
            if (proof != null && previous == null) return null;
            if (proof != null && proof.Kind == "OfficialReleaseWindow" && previous.Time > proof.NotAvailableAtUtc.Value)
                throw new HistoryResolutionException("补丁首发前一公开事件没有位于官方窗口下界之外。");
            var location = new DateFirstLocation {
                CandidateChange = candidate.Change, Publication = candidate.Change.Publication,
                PreviousPublicChange = previous == null ? null : previous.Change, EffectiveTime = candidate.Time,
                UsedObservationFallback = !HasPublicTime(candidate.Change.Publication.PublishedAtUtc),
                Assurance = "按本地原始 ChangeId 精确还原公开事件。"
            };
            var state = CloneState(baseline.State);
            string currentBuild = baseline.BuildId;
            DateTimeOffset currentTime = baseline.PublishedAtUtc;
            int windowEvents = 0;
            for (int index = 0; index <= candidate.Index; index++)
            {
                token.ThrowIfCancellationRequested();
                // Selection has already scanned all verified timestamps. Using the
                // exact ChangeId here also handles time metadata out of changelist
                // order, repeated BuildIds, and manifest rollbacks.
                ResolvedHistoryPlan result = ApplyChange(anchor.Changes[index], state, null, candidate.Build,
                    proof == null ? (DateTimeOffset?)null : proof.Build.PublishedAtUtc, proof, location, patch, language,
                    ref currentBuild, ref currentTime, ref windowEvents);
                if (result == null) continue;
                result.Assurance += "本地归档锚点 " + anchor.Identity + " 的 " + anchor.Changes.Count + " 条连续记录已经完整重新回放校验；本次未读取网页，未修改归档。";
                if (verifiedThrough.HasValue) result.Assurance += "已发布同步数据库通过原始基线和完整历史核实至 UTC " + verifiedThrough.Value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "；覆盖时间不超过首次完整基线采集时间。";
                if (requested.HasValue && nearest) result.Assurance += "按补丁公告时间在已覆盖的前后公开事件中选择时间距离最近一次，相等时刻包含；等距时优先此前事件。";
                if (requested.HasValue && includeRequestedInstant && !nearest) result.Assurance += "按补丁目录的日期边界或公告时间在本地匹配，包含与该查询端点相等的公开事件。";
                if (requested.HasValue && !includeRequestedInstant) result.Assurance += timeDirection == HistoryTimeDirection.After ?
                    "本地连续历史已覆盖输入时刻及此前的公开事件；在全部已覆盖公开事件中按有效时间选择严格晚于输入的最近一次，相等时刻不包含，同一 Build 的再次启用保留为独立事件。" :
                    "在本地已覆盖的公开事件中按有效时间选择严格早于输入的最近一次；相等时刻不包含，同一 Build 的再次启用保留为独立事件。";
                result.Sources.Add(baseline.SourceUrl);
                if (proof != null) foreach (string source in proof.Sources) if (!result.Sources.Contains(source)) result.Sources.Add(source);
                result.Limitations.Add("本地归档内容校验和连续回放不等同于网站数字签名；没有证明历史文件仍可下载或客户端能够运行。");
                return result;
            }
            throw new HistoryResolutionException("本地完整回放没有返回指定公开事件。");
        }

        private static void ValidateLocalPatchProof(OfficialPatchMatch proof, string patch)
        {
            if (proof.Build == null || !HistoryDepotState.IsNumeric(proof.Build.BuildId) || proof.Sources == null || proof.Sources.Count == 0)
                throw new HistoryResolutionException("本地补丁关联缺少已核实的官方构建和来源。");
            Uri buildSource;
            if (!Uri.TryCreate(proof.Build.SourceUrl, UriKind.Absolute, out buildSource) || buildSource.Scheme != "https" || buildSource.Host != "steamdb.info" ||
                !buildSource.IsDefaultPort || buildSource.UserInfo.Length != 0 || buildSource.Query.Length != 0 || buildSource.Fragment.Length != 0 ||
                buildSource.AbsolutePath.TrimEnd('/') != "/patchnotes/" + proof.Build.BuildId)
                throw new HistoryResolutionException("本地补丁依据的构建来源地址不匹配。");
            if (proof.Kind != "OfficialReleaseWindow" && proof.Kind != "OfficialPatchSource" && proof.Kind != "OfficialEventBuild")
                throw new HistoryResolutionException("本地补丁关联尚无可证明首发的官方窗口或精确来源记录。");
            if (!String.IsNullOrEmpty(proof.Build.ChangeId) && !HistoryDepotState.IsNumeric(proof.Build.ChangeId))
                throw new HistoryResolutionException("本地补丁依据的变更编号无效。");
            foreach (string source in proof.Sources)
            {
                Uri uri;
                if (!Uri.TryCreate(source, UriKind.Absolute, out uri) || (uri.Scheme != "http" && uri.Scheme != "https") || uri.UserInfo.Length != 0 || !uri.IsDefaultPort ||
                    !(uri.Host == "steamdb.info" || uri.Host == "dota2.com" || uri.Host == "www.dota2.com" || uri.Host == "api.steampowered.com" || uri.Host == "store.steampowered.com" || uri.Host == "steamcommunity.com" ||
                    uri.Host == "steamstore-a.akamaihd.net" && uri.Query.Length == 0 && uri.Fragment.Length == 0 && Regex.IsMatch(uri.AbsolutePath, @"^/news/externalpost/(steam_community_announcements|steam_updates)/[0-9]+/?$")))
                    throw new HistoryResolutionException("本地补丁依据包含不受支持的来源地址。");
            }
            if (!proof.Sources.Any(s => new Uri(s).Host != "steamdb.info")) throw new HistoryResolutionException("仅有构建标题不能作为官方补丁首发依据。");
            ValidateOfficialWindow(proof, patch);
        }

        private static bool SameLocalPlan(ResolvedHistoryPlan left, ResolvedHistoryPlan right)
        {
            return left.SourceUrl == right.SourceUrl && left.BuildId == right.BuildId && left.PublishedAtUtc == right.PublishedAtUtc &&
                left.Depots.Count == right.Depots.Count && left.Depots.All(d => right.Depots.Any(r => r.DepotId == d.DepotId && r.ManifestId == d.ManifestId));
        }
    }
}
