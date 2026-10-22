using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace DotaArchive
{
    // Range coverage concerns SteamDB's observed Depot changes. It is not a
    // promise that every historical client can be downloaded or run.
    public sealed class HistoryArchiveResult
    {
        public bool RangeCovered { get; set; }
        public bool ReachedSiteEnd { get; set; }
        public int TotalRecords { get; set; }
        public int RangeRecords { get; set; }
        public int NewRecords { get; set; }
        public DateTimeOffset? OldestObservedAtUtc { get; set; }
        public string BoundaryChangeId { get; set; }
        public string AnchorIdentity { get; set; }
        public DateTimeOffset LowerInclusiveUtc { get; set; }
        public DateTimeOffset UpperExclusiveUtc { get; set; }
    }

    public sealed partial class OnlineHistoryResolver
    {
        public async Task<HistoryArchiveResult> ArchiveRangeAsync(DateTimeOffset lowerInclusive,
            DateTimeOffset upperExclusive, CancellationToken token, Action<string> progress)
        {
            if (lowerInclusive >= upperExclusive) throw new ArgumentException("历史保存范围的开始时间必须早于结束时间。");
            if (cacheStore == null || anchorStore == null)
                throw new HistoryResolutionException("范围保存需要已启用的持久历史锚点目录。");
            HistoryArchiveResult result = null;
            Exception failure = null;
            try { result = await ArchiveRangeCoreAsync(lowerInclusive.ToUniversalTime(), upperExclusive.ToUniversalTime(), token, progress); }
            catch (Exception ex) { failure = ex; }
            // Cancellation, parser gaps and browser restrictions retain only the
            // successfully replayed prefix. Saving is part of this task's result.
            try { await PersistCacheAsync(progress, true); }
            catch (Exception saving)
            {
                if (failure != null) throw new AggregateException("历史读取已停止，且已验证断点未能保存。", failure, saving);
                throw;
            }
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
            return result;
        }

        private async Task<HistoryArchiveResult> ArchiveRangeCoreAsync(DateTimeOffset lower,
            DateTimeOffset upper, CancellationToken token, Action<string> progress)
        {
            token.ThrowIfCancellationRequested();
            await LoadAnchorsAsync(token, progress);
            // Select the furthest proved continuation that also covers the upper
            // edge. Do not fetch today's baseline or locate a downloadable Build.
            HistoryAnchorStore.Anchor selected = anchors.Where(a => a.Changes.Count > 0 &&
                (a.Baseline.PublishedAtUtc >= upper || a.Changes[0].ObservedAtUtc >= upper))
                .OrderBy(a => a.Changes.Last().ObservedAtUtc)
                .ThenBy(a => UInt64.Parse(a.Changes.Last().ChangeId, CultureInfo.InvariantCulture))
                .FirstOrDefault();
            if (selected == null)
                throw new HistoryResolutionException("没有覆盖范围上界的已核实历史锚点，不能直接把日期页当作完整历史基线。");
            usingHistoricalAnchor = true;
            cachedBaseline = CloneBaseline(selected.Baseline);
            cachedChanges.Clear(); cachedChanges.AddRange(selected.Changes);
            int initialCount = cachedChanges.Count;
            HistoryDepotState state = CloneState(cachedBaseline.State);
            string currentBuild = cachedBaseline.BuildId;
            DateTimeOffset currentTime = cachedBaseline.PublishedAtUtc;
            int events = 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var known = new Dictionary<string, HistoryChange>(StringComparer.Ordinal);
            foreach (HistoryChange change in cachedChanges)
            {
                token.ThrowIfCancellationRequested();
                ApplyChange(change, state, null, null, null, null, null, "", null, ref currentBuild, ref currentTime, ref events);
                seen.Add(change.ChangeId); known.Add(change.ChangeId, change);
                if (seen.Count % 100 == 0) await Task.Yield();
            }
            Report(progress, "已核实本地 " + initialCount.ToString(CultureInfo.InvariantCulture) + " 条历史；从变更 " + cachedChanges.Last().ChangeId + " 继续保存指定范围。");
            // Strictly crossing the lower instant also includes all records whose
            // displayed timestamps equal that instant, regardless of ChangeId.
            if (cachedChanges.Last().ObservedAtUtc < lower) return ArchiveResult(lower, upper, initialCount, false);
            IHistoryDateBrowser dated = browser as IHistoryDateBrowser;
            if (dated == null) throw new HistoryResolutionException("范围保存需要网页日期筛选支持，不能从最新记录重新翻页。");
            IHistoryDateNavigationBrowser navigation = browser as IHistoryDateNavigationBrowser;
            if (navigation != null) await navigation.OpenHistoryForDateAsync(token);
            else await browser.NavigateAsync(HistoryUrl, token);
            HistoryChange requiredOverlap = cachedChanges.Last();
            HistoryPageSnapshot page = await dated.LoadHistoryBeforeAsync(requiredOverlap.ObservedAtUtc.UtcDateTime.Date.AddDays(1), token);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                HistoryPageSnapshot parsingPage = page;
                HistoryListing listing = await Task.Run(() => parser.ParseHistory(parsingPage), token);
                bool dateBatch = requiredOverlap != null;
                if (listing.Changes.GroupBy(c => c.ChangeId, StringComparer.Ordinal).Any(g => g.Count() != 1))
                    throw new HistoryResolutionException("同一历史批次出现重复 ChangeId，未将该批次写入已验证缓存。");
                if (requiredOverlap != null)
                {
                    VerifyBatchOverlap(requiredOverlap, listing, seen);
                    requiredOverlap = null;
                }
                // Validate every repeated record, not merely the oldest overlap.
                foreach (HistoryChange repeated in listing.Changes.Where(c => seen.Contains(c.ChangeId)))
                    if (!SameChange(known[repeated.ChangeId], repeated))
                        throw new HistoryResolutionException("历史批次中已保存的变更 " + repeated.ChangeId + " 内容发生冲突，已停止保存。");
                int before = cachedChanges.Count;
                foreach (HistoryChange change in listing.Changes)
                {
                    if (seen.Contains(change.ChangeId)) continue;
                    token.ThrowIfCancellationRequested();
                    HistoryChange last = cachedChanges.Last();
                    if (change.ObservedAtUtc > last.ObservedAtUtc ||
                        UInt64.Parse(change.ChangeId, CultureInfo.InvariantCulture) >= UInt64.Parse(last.ChangeId, CultureInfo.InvariantCulture))
                        throw new HistoryResolutionException("新批次的变更 " + change.ChangeId + " 没有严格接续已验证边界，不能跳过中间记录。");
                    ApplyChange(change, state, null, null, null, null, null, "", null, ref currentBuild, ref currentTime, ref events);
                    cachedChanges.Add(change); seen.Add(change.ChangeId); known.Add(change.ChangeId, change);
                }
                await PersistCacheAsync(progress, true);
                HistoryChange oldest = cachedChanges.Last();
                Report(progress, "已保存并验证 " + cachedChanges.Count.ToString(CultureInfo.InvariantCulture) + " 条历史（本次新增 " + (cachedChanges.Count - initialCount).ToString(CultureInfo.InvariantCulture) + " 条），边界 " + oldest.ChangeId + " · " + oldest.ObservedAtUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) + "。");
                if (listing.EndReached) return ArchiveResult(lower, upper, initialCount, true);
                if (oldest.ObservedAtUtc < lower) return ArchiveResult(lower, upper, initialCount, false);
                if (listing.Changes.Count == 0)
                    throw new HistoryResolutionException("历史页面没有记录，也没有明确的历史末尾标记；已保留断点。");
                if (cachedChanges.Count == before)
                {
                    if (!dateBatch)
                    {
                        requiredOverlap = oldest;
                        page = await dated.LoadHistoryBeforeAsync(oldest.ObservedAtUtc.UtcDateTime.Date.AddDays(1), token);
                        continue;
                    }
                    throw new HistoryResolutionException("网页日期筛选没有提供更早的新记录；已保存断点，不会重复请求同一页。");
                }
                if (listing.Changes.First().ObservedAtUtc.UtcDateTime.Date > oldest.ObservedAtUtc.UtcDateTime.Date)
                {
                    requiredOverlap = oldest;
                    page = await dated.LoadHistoryBeforeAsync(oldest.ObservedAtUtc.UtcDateTime.Date.AddDays(1), token);
                }
                else if (!String.IsNullOrEmpty(listing.LoadMoreSelector)) page = await browser.ClickAsync(listing.LoadMoreSelector, token);
                else if (!String.IsNullOrEmpty(listing.NextPageUrl)) page = await browser.NavigateAsync(listing.NextPageUrl, token);
                else
                {
                    requiredOverlap = oldest;
                    page = await dated.LoadHistoryBeforeAsync(oldest.ObservedAtUtc.UtcDateTime.Date.AddDays(1), token);
                }
            }
        }

        private HistoryArchiveResult ArchiveResult(DateTimeOffset lower, DateTimeOffset upper, int initialCount, bool siteEnd)
        {
            HistoryChange oldest = cachedChanges.Last();
            return new HistoryArchiveResult {
                RangeCovered = oldest.ObservedAtUtc < lower || siteEnd, ReachedSiteEnd = siteEnd,
                TotalRecords = cachedChanges.Count, NewRecords = cachedChanges.Count - initialCount,
                RangeRecords = cachedChanges.Count(c => c.ObservedAtUtc >= lower && c.ObservedAtUtc < upper),
                OldestObservedAtUtc = oldest.ObservedAtUtc.ToUniversalTime(), BoundaryChangeId = oldest.ChangeId,
                AnchorIdentity = HistoryAnchorStore.Identity(cachedBaseline), LowerInclusiveUtc = lower, UpperExclusiveUtc = upper
            };
        }
    }
}
