using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DotaArchive
{
    public sealed partial class OnlineHistoryResolver
    {
        private const string BaselineUrl = "https://steamdb.info/app/570/depots/?branch=public";
        private const string HistoryUrl = "https://steamdb.info/app/570/history/?filterkey=2";
        private readonly IHistoryBrowser browser;
        private readonly SteamDbHistoryParser parser = new SteamDbHistoryParser();
        private readonly Func<string, IList<HistoryBuildEvent>, CancellationToken, Action<string>, Task<OfficialPatchMatch>> patchFallback;
        private readonly HistoryCacheStore cacheStore;
        private readonly HistoryAnchorStore anchorStore;
        private readonly bool useDateFirst;
        private readonly List<HistoryAnchorStore.Anchor> anchors = new List<HistoryAnchorStore.Anchor>();
        private bool anchorsLoaded;
        private bool usingHistoricalAnchor;
        private HistoryBaseline cachedBaseline;
        private readonly List<HistoryChange> cachedChanges = new List<HistoryChange>();
        public event Action<DateFirstLocation> CandidateLocated;

        public OnlineHistoryResolver(IHistoryBrowser browser) : this(browser, null, HistoryCacheStore.DefaultDirectory) { }

        public OnlineHistoryResolver(IHistoryBrowser browser, Func<string, IList<HistoryBuildEvent>, CancellationToken, Action<string>, Task<OfficialPatchMatch>> patchFallback) : this(browser, patchFallback, HistoryCacheStore.DefaultDirectory) { }

        // A null directory disables persistent caching for deterministic offline replay.
        public OnlineHistoryResolver(IHistoryBrowser browser, Func<string, IList<HistoryBuildEvent>, CancellationToken, Action<string>, Task<OfficialPatchMatch>> patchFallback, string cacheDirectory) : this(browser, patchFallback, cacheDirectory, true) { }

        public OnlineHistoryResolver(IHistoryBrowser browser, Func<string, IList<HistoryBuildEvent>, CancellationToken, Action<string>, Task<OfficialPatchMatch>> patchFallback, string cacheDirectory, bool useDateFirst)
        {
            if (browser == null) throw new ArgumentNullException("browser");
            this.browser = browser;
            this.patchFallback = patchFallback ?? new OfficialPatchResolver(browser).ResolveAsync;
            this.cacheStore = String.IsNullOrWhiteSpace(cacheDirectory) ? null : new HistoryCacheStore(cacheDirectory);
            this.anchorStore = useDateFirst && !String.IsNullOrWhiteSpace(cacheDirectory) ? new HistoryAnchorStore(cacheDirectory) : null;
            this.useDateFirst = useDateFirst;
        }

        public async Task<ResolvedHistoryPlan> ResolveAsync(HistoryQuery query, CancellationToken token, Action<string> progress)
        {
            ResolvedHistoryPlan result = null;
            Exception failure = null;
            try { result = await ResolveCoreAsync(query, token, progress); }
            catch (Exception ex) { failure = ex; }
            // Save the proved prefix even after cancellation or a browser/network failure.
            // C# 5 cannot await in finally, so preserve and rethrow after the save completes.
            await PersistCacheAsync(progress);
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
            return result;
        }

        private async Task<ResolvedHistoryPlan> ResolveCoreAsync(HistoryQuery query, CancellationToken token, Action<string> progress)
        {
            if (query == null) throw new ArgumentNullException("query");
            string patch = (query.Patch ?? "").Trim().ToLowerInvariant();
            DateTimeOffset? requested = query.RequestedUtc ?? query.BeijingTime;
            if ((patch.Length == 0) == !requested.HasValue) throw new HistoryResolutionException("请选择一种查询方式：版本号，或北京时间。");
            if (requested.HasValue && query.TimeDirection != HistoryTimeDirection.Before && query.TimeDirection != HistoryTimeDirection.After)
                throw new HistoryResolutionException("时间查询方向无效，请选择此前最近版本或此后最近版本。");
            AfterReplaySelection afterSelection = requested.HasValue && query.TimeDirection == HistoryTimeDirection.After ? new AfterReplaySelection() : null;
            if (patch.Length != 0 && !Regex.IsMatch(patch, @"^\d+\.\d{2}[a-z]?$")) throw new HistoryResolutionException("请输入完整版本号，例如 7.22b；不能用模糊前缀匹配。");
            string targetBuild = null;
            string patchSource = null;
            DateTimeOffset? patchObservedAt = null;
            var officialSources = new List<string>();
            OfficialPatchMatch officialPatchProof = null;
            DateFirstLocation located = null;
            int patchWindowEvents = 0;
            if (patch.Length != 0)
            {
                Report(progress, "正在浏览 SteamDB 公开更新列表，匹配版本标签 " + patch + "……");
                HistoryPageSnapshot patchPage = await browser.NavigateAsync("https://steamdb.info/app/570/patchnotes/", token);
                HistoryListing patches = await Task.Run(() => parser.ParsePatchListing(patchPage), token);
                string pattern = @"(?<![0-9a-z.])" + Regex.Escape(patch) + @"(?![0-9a-z.])";
                List<HistoryBuildEvent> matches = patches.Builds.Where(b => Regex.IsMatch(b.Title ?? "", pattern, RegexOptions.IgnoreCase)).OrderBy(b => b.PublishedAtUtc).ToList();
                // A community-supplied title is a locator, not proof that this Build
                // is the first public release. Every patch query needs official linkage.
                if (matches.Count > 0) Report(progress, "更新标题含有该版本号，正在核对官方发布依据，排除预告、测试版本和后续回滚……");
                {
                    OfficialPatchMatch official = await patchFallback(patch, patches.Builds, token, progress);
                    if (official == null || official.Build == null) throw new HistoryResolutionException("没有取得版本 " + patch + " 与公开构建之间的可核实关联。");
                    HistoryBuildEvent original = patches.Builds.FirstOrDefault(b => b.BuildId == official.Build.BuildId && b.SourceUrl == official.Build.SourceUrl);
                    if (original == null) throw new HistoryResolutionException("官方版本关联返回了公开更新列表中不存在的构建。");
                    if (!String.IsNullOrEmpty(official.Build.ChangeId) && !HistoryDepotState.IsNumeric(official.Build.ChangeId))
                        throw new HistoryResolutionException("候选来源页中的历史变更编号格式无效。");
                    ValidateOfficialWindow(official, patch);
                    officialPatchProof = official;
                    matches.Clear(); matches.Add(original);
                    if (official.Sources != null) officialSources.AddRange(official.Sources);
                    Report(progress, official.Assurance);
                }
                targetBuild = matches[0].BuildId; patchSource = matches[0].SourceUrl; patchObservedAt = matches[0].PublishedAtUtc;
                Report(progress, "官方发布依据关联到 Build " + targetBuild + "，继续核对公开分支的实际发布时间和完整历史清单。");
            }
            token.ThrowIfCancellationRequested();
            if (useDateFirst && browser is IHistoryDateBrowser)
            {
                var locator = new DateFirstHistoryLocator(browser);
                if (requested.HasValue) located = query.TimeDirection == HistoryTimeDirection.After ?
                    await locator.LocateAfterAsync(requested.Value, token, progress) : await locator.LocateAsync(requested.Value, token, progress);
                else if (officialPatchProof != null && !String.IsNullOrEmpty(officialPatchProof.Build.ChangeId))
                    located = await locator.LocateExactChangeAsync(officialPatchProof.Build.ChangeId, patchObservedAt.Value, token, progress);
                else if (officialPatchProof != null && officialPatchProof.Kind == "OfficialReleaseWindow")
                    located = await locator.LocateAfterAsync(officialPatchProof.NotAvailableAtUtc.Value, token, progress);
                if (located != null)
                {
                    if (located.CandidateChange == null || located.Publication == null)
                        throw new HistoryResolutionException("日期定位没有返回公开变更事件，不能继续补齐清单。");
                    if (targetBuild != null && located.Publication.BuildId != null && targetBuild != located.Publication.BuildId)
                        throw new HistoryResolutionException("日期邻域找到的公开 Build 与官方版本来源不一致。");
                    if (requested.HasValue && (query.TimeDirection == HistoryTimeDirection.After ? located.EffectiveTime <= requested.Value : located.EffectiveTime >= requested.Value))
                        throw new HistoryResolutionException(query.TimeDirection == HistoryTimeDirection.After ?
                            "日期定位候选没有严格晚于输入时间；相等时刻的版本不包含在内。" :
                            "日期定位候选没有严格早于输入时间；相等时刻的版本不包含在内。");
                    if (located.Sources != null) officialSources.AddRange(located.Sources);
                    Action<DateFirstLocation> handler = CandidateLocated;
                    if (handler != null) handler(located);
                    Report(progress, "已按日期定位变更 " + located.CandidateChange.ChangeId + "。正在补齐完整 Depot 状态；日期邻近页仅包含差分，首次重建仍可能需要读取较长的连续历史。");
                }
            }
            else if (useDateFirst)
                Report(progress, "当前浏览器不支持日期控件，使用兼容的连续历史回放；不会把局部记录当作完整清单。");
            token.ThrowIfCancellationRequested();
            await LoadAnchorsAsync(token, progress);
            HistoryAnchorStore.Anchor selectedAnchor = SelectAnchor(located, requested);
            usingHistoricalAnchor = selectedAnchor != null;
            HistoryBaseline baseline;
            if (selectedAnchor != null)
            {
                var evidence = selectedAnchor.Changes.ToDictionary(c => c.ChangeId, StringComparer.Ordinal);
                foreach (HistoryChange observed in located.NeighborChanges)
                {
                    HistoryChange known;
                    if (evidence.TryGetValue(observed.ChangeId, out known) && !SameChange(known, observed))
                        throw new HistoryResolutionException("本次日期邻域与历史锚点中相同 ChangeId 的原始记录冲突，不能复用该锚点。");
                }
                baseline = CloneBaseline(selectedAnchor.Baseline);
                cachedBaseline = CloneBaseline(baseline); cachedChanges.Clear(); cachedChanges.AddRange(selectedAnchor.Changes);
                Report(progress, "使用已核实的历史完整基线 " + selectedAnchor.Identity.Substring(0, 12) + "，保留 " + cachedChanges.Count + " 条连续证据；当前版本更新不会使这段历史失效。");
            }
            else
            {
                Report(progress, "没有覆盖目标的已核实历史基线，正在读取当前公开分支的完整 Depot 基线……");
                HistoryPageSnapshot baselinePage = await browser.NavigateAsync(BaselineUrl, token);
                baseline = await Task.Run(() => parser.ParseBaseline(baselinePage), token);
                if (!SameBaseline(cachedBaseline, baseline))
                {
                    cachedChanges.Clear(); cachedBaseline = CloneBaseline(baseline);
                    if (anchorStore == null) await RestoreCacheAsync(baseline, token, progress);
                    else
                    {
                        HistoryAnchorStore.Anchor same = anchors.FirstOrDefault(a => SameBaseline(a.Baseline, baseline));
                        if (same != null) cachedChanges.AddRange(same.Changes);
                    }
                }
            }
            HistoryDepotState state = CloneState(baseline.State);
            string currentBuild = baseline.BuildId;
            DateTimeOffset currentTime = baseline.PublishedAtUtc;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var sources = new List<string> { BaselineUrl };
            sources.AddRange(officialSources);
            int count = 0;
            foreach (HistoryChange cached in cachedChanges)
            {
                token.ThrowIfCancellationRequested(); seen.Add(cached.ChangeId);
                ResolvedHistoryPlan found = ApplyQueryChange(cached, state, requested, targetBuild, patchObservedAt, officialPatchProof, located, patch, query.Language, ref currentBuild, ref currentTime, ref patchWindowEvents, afterSelection);
                count++;
                if (found != null) return await FinishAsync(found, baseline, patchSource, sources, count, token, progress);
                if (count % 100 == 0) await Task.Yield();
            }
            // A date neighborhood is only a set of differences. Reuse it as a
            // continuation only when it contains the identical proved boundary;
            // never treat that neighborhood as a complete historical baseline.
            if (located != null && cachedChanges.Count > 0 && located.NeighborChanges != null &&
                located.NeighborChanges.Any(c => c.ChangeId == cachedChanges.Last().ChangeId))
            {
                var neighborhood = new HistoryListing();
                neighborhood.Changes.AddRange(located.NeighborChanges.OrderByDescending(c => c.ObservedAtUtc).ThenByDescending(c => UInt64.Parse(c.ChangeId, CultureInfo.InvariantCulture)));
                VerifyBatchOverlap(cachedChanges.Last(), neighborhood, seen);
                Report(progress, "日期邻域已接上重新核实的缓存边界，正在沿同一连续链补齐目标 Depot……");
                foreach (HistoryChange change in neighborhood.Changes)
                {
                    if (!seen.Add(change.ChangeId)) continue;
                    token.ThrowIfCancellationRequested();
                    ResolvedHistoryPlan found = ApplyQueryChange(change, state, requested, targetBuild, patchObservedAt, officialPatchProof, located, patch, query.Language, ref currentBuild, ref currentTime, ref patchWindowEvents, afterSelection);
                    cachedChanges.Add(change); count++;
                    if (found != null) return await FinishAsync(found, baseline, patchSource, sources, count, token, progress);
                    if (count % 100 == 0) await Task.Yield();
                }
            }
            Report(progress, "正在按网页顺序读取 Depot 历史；首次查询较早版本需要加载较多记录，可随时取消。");
            IHistoryDateBrowser dateBrowser = browser as IHistoryDateBrowser;
            HistoryPageSnapshot page;
            HistoryChange requiredOverlap = null;
            if (cachedChanges.Count > 0 && dateBrowser != null)
            {
                requiredOverlap = cachedChanges.Last();
                Report(progress, "正在从已核实的历史边界继续，使用网页日期筛选并检查批次重叠……");
                IHistoryDateNavigationBrowser dateNavigation = browser as IHistoryDateNavigationBrowser;
                if (dateNavigation != null) await dateNavigation.OpenHistoryForDateAsync(token);
                else await browser.NavigateAsync(HistoryUrl, token);
                page = await dateBrowser.LoadHistoryBeforeAsync(requiredOverlap.ObservedAtUtc.UtcDateTime.Date.AddDays(1), token);
            }
            else page = await browser.NavigateAsync(HistoryUrl, token);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                HistoryPageSnapshot parsingPage = page;
                HistoryListing listing = await Task.Run(() => parser.ParseHistory(parsingPage), token);
                if (listing.Changes.Count == 0) throw new HistoryResolutionException("SteamDB 历史记录尚未加载，或网页结构已变化；没有得到可用于重建的记录。");
                bool dateFilteredBatch = requiredOverlap != null;
                if (requiredOverlap != null) { VerifyBatchOverlap(requiredOverlap, listing, seen); requiredOverlap = null; }
                if (listing.Changes.All(c => seen.Contains(c.ChangeId)))
                {
                    // SteamDB can finish an ordinary Load older request without adding rows.
                    // Its visible date filter is a separate supported way to continue. Try it
                    // once from the last proved boundary, never skip directly to the target.
                    if (!dateFilteredBatch && dateBrowser != null && cachedChanges.Count > 0 && !listing.EndReached)
                    {
                        requiredOverlap = cachedChanges.Last();
                        Report(progress, "加载按钮没有增加记录，正在使用网站日期筛选接续，并核实前后批次重叠……");
                        page = await dateBrowser.LoadHistoryBeforeAsync(requiredOverlap.ObservedAtUtc.UtcDateTime.Date.AddDays(1), token);
                        continue;
                    }
                    throw new HistoryResolutionException("网页日期筛选没有提供更早的新记录，无法继续证明历史完整性。请查看浏览器中的登录状态和页面提示后重试。");
                }
                bool bridged = false;
                var work = listing.Changes.ToList();
                for (int recordIndex = 0; recordIndex < work.Count; recordIndex++)
                {
                    HistoryChange change = work[recordIndex];
                    if (!seen.Add(change.ChangeId)) continue;
                    token.ThrowIfCancellationRequested();
                    if (!bridged)
                    {
                        List<HistoryChange> tail = TryBridge(change, state, currentBuild, currentTime);
                        if (tail != null)
                        {
                            foreach (HistoryChange incoming in work.Skip(recordIndex))
                            {
                                HistoryChange common = tail.FirstOrDefault(c => c.ChangeId == incoming.ChangeId);
                                if (common != null && !SameChange(common, incoming)) throw new HistoryResolutionException("新旧历史链的重叠记录内容不一致，不能连接锚点。");
                            }
                            // Retain the browser's records after the old chain's end too.
                            var suffix = tail.Concat(work.Skip(recordIndex).Where(c => !tail.Any(t => t.ChangeId == c.ChangeId))).OrderByDescending(c => c.ObservedAtUtc).ThenByDescending(c => UInt64.Parse(c.ChangeId, CultureInfo.InvariantCulture)).ToList();
                            work.RemoveRange(recordIndex, work.Count - recordIndex); work.AddRange(suffix);
                            change = work[recordIndex]; bridged = true;
                            Report(progress, "新基线已通过相同 ChangeId、完整 Depot 状态和 Build 链接上历史锚点，复用其已验证差分。");
                        }
                    }
                    ResolvedHistoryPlan found = ApplyQueryChange(change, state, requested, targetBuild, patchObservedAt, officialPatchProof, located, patch, query.Language, ref currentBuild, ref currentTime, ref patchWindowEvents, afterSelection);
                    cachedChanges.Add(change); count++;
                    if (found != null) return await FinishAsync(found, baseline, patchSource, sources, count, token, progress);
                    if (count % 100 == 0) await Task.Yield();
                }
                await PersistCacheAsync(progress);
                HistoryChange oldest = work.Last();
                Report(progress, "已核对 " + count.ToString(CultureInfo.InvariantCulture) + " 条 Depot 历史，读取到 " + oldest.ObservedAtUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "；正在加载更早记录……");
                if (bridged && dateBrowser != null)
                {
                    requiredOverlap = oldest;
                    page = await dateBrowser.LoadHistoryBeforeAsync(oldest.ObservedAtUtc.UtcDateTime.Date.AddDays(1), token);
                }
                else if (listing.EndReached) throw new HistoryResolutionException("已读取到网站可提供的历史末尾，仍无法证明所请求版本的完整构建组合。");
                else if (dateBrowser != null && listing.Changes.First().ObservedAtUtc.UtcDateTime.Date > oldest.ObservedAtUtc.UtcDateTime.Date)
                {
                    // A batch spanning multiple UTC dates can advance via the site's
                    // date control immediately. Keep the oldest record as mandatory
                    // overlap evidence; do not issue a redundant Load older request.
                    // Same-day batches still need the ordinary pagination control.
                    requiredOverlap = oldest;
                    Report(progress, "正在按已核实边界直接查询更早日期，并检查批次重叠……");
                    page = await dateBrowser.LoadHistoryBeforeAsync(oldest.ObservedAtUtc.UtcDateTime.Date.AddDays(1), token);
                }
                else if (!String.IsNullOrEmpty(listing.LoadMoreSelector)) page = await browser.ClickAsync(listing.LoadMoreSelector, token);
                else if (!String.IsNullOrEmpty(listing.NextPageUrl)) page = await browser.NavigateAsync(listing.NextPageUrl, token);
                else if (dateBrowser != null)
                {
                    requiredOverlap = oldest;
                    Report(progress, "当前历史批次的加载按钮不可用，正在使用网站日期筛选接续，并验证前后批次重叠……");
                    page = await dateBrowser.LoadHistoryBeforeAsync(oldest.ObservedAtUtc.UtcDateTime.Date.AddDays(1), token);
                }
                else throw new HistoryResolutionException("历史页面没有显示可继续加载的按钮，无法证明更早记录完整。请检查 SteamDB 登录状态和页面提示。");
            }
        }

        private async Task LoadAnchorsAsync(CancellationToken token, Action<string> progress)
        {
            if (anchorStore == null) return;
            if (!anchorsLoaded)
            {
                try
                {
                    List<HistoryAnchorStore.Anchor> loaded = await Task.Run(() => anchorStore.Load(), token);
                    // Validate each whole chain before making any of the file eligible.
                    foreach (HistoryAnchorStore.Anchor anchor in loaded) await ValidateAnchorAsync(anchor, token);
                    anchors.Clear(); anchors.AddRange(loaded);
                    if (loaded.Count > 0) Report(progress, "已重新回放核实 " + loaded.Count + " 个历史完整基线及其连续差分证据。");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { anchors.Clear(); Report(progress, "历史锚点不能通过内容或连续性验证，未使用该文件：" + ex.Message); }
                anchorsLoaded = true;
            }
            // The legacy process may still be collecting records. Read v1 again,
            // but accept it only after full replay and identical common prefixes.
            if (cacheStore != null)
            {
                try
                {
                    HistoryCacheStore.CacheData old = await Task.Run(() => cacheStore.Load(), token);
                    if (old != null && old.Changes.Count > 0)
                    {
                        var legacy = new HistoryAnchorStore.Anchor { Identity = HistoryAnchorStore.Identity(old.Baseline), Provenance = "ValidatedLegacyV1Migration", Baseline = old.Baseline, Changes = old.Changes };
                        await ValidateAnchorAsync(legacy, token);
                        bool added = MergeAnchor(legacy);
                        if (added) Report(progress, "已将 v1 中 " + old.Changes.Count + " 条连续历史证据迁入持久锚点；保留原缓存文件。");
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Report(progress, "v1 历史缓存未通过迁移验证，已保留文件但不会使用：" + ex.Message); }
            }
        }

        internal Task ValidateAnchorAsync(HistoryAnchorStore.Anchor anchor, CancellationToken token)
        {
            return Task.Run(delegate {
                if (anchor.Baseline == null || !anchor.Baseline.State.CompleteBaseline || anchor.Changes == null || anchor.Changes.Count == 0)
                    throw new HistoryResolutionException("历史锚点必须具有完整网页基线和连续差分证据。");
                // Also ensure that the baseline represents an actual downloadable
                // client scope, rather than a dictionary containing names alone.
                anchor.Baseline.State.SelectWindowsClient("schinese");
                HistoryDepotState check = CloneState(anchor.Baseline.State);
                string build = anchor.Baseline.BuildId;
                DateTimeOffset time = anchor.Baseline.PublishedAtUtc;
                int events = 0;
                foreach (HistoryChange change in anchor.Changes)
                {
                    token.ThrowIfCancellationRequested();
                    ApplyChange(change, check, null, null, null, null, null, "", null, ref build, ref time, ref events);
                }
            }, token);
        }

        private HistoryAnchorStore.Anchor SelectAnchor(DateFirstLocation located, DateTimeOffset? requested)
        {
            if (located == null || located.CandidateChange == null) return null;
            ulong candidate = UInt64.Parse(located.CandidateChange.ChangeId, CultureInfo.InvariantCulture);
            // A Build may be activated more than once. Scope by changelist identity,
            // never by build number or approximate publication time alone.
            return anchors.Where(a => a.Changes.Count > 0 &&
                (!requested.HasValue || a.Baseline.PublishedAtUtc >= requested.Value) &&
                UInt64.Parse(a.Changes[0].ChangeId, CultureInfo.InvariantCulture) >= candidate &&
                a.Changes[0].ObservedAtUtc >= located.CandidateChange.ObservedAtUtc)
                .OrderBy(a => UInt64.Parse(a.Changes[0].ChangeId, CultureInfo.InvariantCulture)).FirstOrDefault();
        }

        private bool MergeAnchor(HistoryAnchorStore.Anchor incoming)
        {
            HistoryAnchorStore.Anchor existing = anchors.FirstOrDefault(a => a.Identity == incoming.Identity);
            if (existing == null) { anchors.Add(incoming); return true; }
            int common = Math.Min(existing.Changes.Count, incoming.Changes.Count);
            for (int i = 0; i < common; i++)
                if (!SameChange(existing.Changes[i], incoming.Changes[i])) throw new HistoryResolutionException("相同历史基线的差分前缀发生冲突，不能覆盖已有证据。");
            if (incoming.Changes.Count <= existing.Changes.Count) return false;
            existing.Changes = incoming.Changes; return true;
        }

        private List<HistoryChange> TryBridge(HistoryChange boundary, HistoryDepotState state, string build, DateTimeOffset time)
        {
            foreach (HistoryAnchorStore.Anchor anchor in anchors.Where(a => a.Changes.Count > 0 && a.Changes[0].ChangeId == boundary.ChangeId && !SameBaseline(a.Baseline, cachedBaseline)))
            {
                if (!SameChange(anchor.Changes[0], boundary)) throw new HistoryResolutionException("历史锚点与新历史链的相同 ChangeId 内容冲突。");
                if (!SameState(state, anchor.Baseline.State) || build != anchor.Baseline.BuildId ||
                    HasPublicTime(time) && HasPublicTime(anchor.Baseline.PublishedAtUtc) && time != anchor.Baseline.PublishedAtUtc)
                    throw new HistoryResolutionException("历史锚点边界的完整 Depot 状态或公开 Build 不一致，不能只凭相同编号连接。");
                return anchor.Changes.ToList();
            }
            return null;
        }

        private async Task RestoreCacheAsync(HistoryBaseline baseline, CancellationToken token, Action<string> progress)
        {
            if (cacheStore == null) return;
            Exception invalidCache = null;
            try
            {
                HistoryCacheStore.CacheData data = await Task.Run(() => cacheStore.Load(), token);
                if (data == null) return;
                if (!SameBaseline(data.Baseline, baseline))
                {
                    await Task.Run(() => cacheStore.Invalidate(), token);
                    Report(progress, "当前公开分支基线与历史缓存不同，已安全作废旧缓存，重新核实网页历史。");
                    return;
                }
                await Task.Run(delegate {
                    HistoryDepotState check = CloneState(baseline.State);
                    string build = baseline.BuildId;
                    DateTimeOffset time = baseline.PublishedAtUtc;
                    int events = 0;
                    foreach (HistoryChange change in data.Changes)
                    {
                        token.ThrowIfCancellationRequested();
                        ApplyChange(change, check, null, null, null, null, null, "", null, ref build, ref time, ref events);
                    }
                }, token);
                cachedChanges.AddRange(data.Changes);
                Report(progress, "已读取并重新核实 " + data.Changes.Count + " 条本机历史缓存；继续加载时仍检查网页边界重叠。");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { invalidCache = ex; }
            if (invalidCache != null)
            {
                cachedChanges.Clear();
                try { await Task.Run(() => cacheStore.Invalidate()); } catch { }
                Report(progress, "本机历史缓存不能通过验证，已放弃缓存并重新读取网页：" + invalidCache.Message);
            }
        }

        private async Task PersistCacheAsync(Action<string> progress, bool strict = false)
        {
            if (cacheStore == null || cachedBaseline == null || cachedChanges.Count == 0) return;
            try
            {
                if (anchorStore != null)
                {
                    MergeAnchor(new HistoryAnchorStore.Anchor { Identity = HistoryAnchorStore.Identity(cachedBaseline), Provenance = "BrowserBaselineAndContinuousHistory", Baseline = CloneBaseline(cachedBaseline), Changes = cachedChanges.ToList() });
                    await Task.Run(() => anchorStore.Save(anchors));
                }
                // Keep the active-chain v1 file readable by an already running old
                // prototype. Other historical roots remain intact in the v2 store.
                await Task.Run(() => cacheStore.Save(cachedBaseline, cachedChanges));
            }
            catch (Exception ex)
            {
                if (strict) throw;
                Report(progress, "已核实历史暂时无法保存到本机缓存，查询结果不受影响：" + ex.Message);
            }
        }

        private static void VerifyBatchOverlap(HistoryChange boundary, HistoryListing listing, HashSet<string> seen)
        {
            HistoryChange overlap = listing.Changes.FirstOrDefault(c => c.ChangeId == boundary.ChangeId);
            if (overlap == null) throw new HistoryResolutionException("日期筛选后的历史没有包含上一批边界记录 " + boundary.ChangeId + "，无法证明批次之间没有空档，已停止下载。");
            if (!SameChange(boundary, overlap)) throw new HistoryResolutionException("日期筛选批次中的重叠记录与之前读取的内容不同，无法证明历史连续性。");
            ulong number = UInt64.Parse(boundary.ChangeId, CultureInfo.InvariantCulture);
            foreach (HistoryChange change in listing.Changes.Where(c => !seen.Contains(c.ChangeId)))
                if (change.ObservedAtUtc > boundary.ObservedAtUtc || UInt64.Parse(change.ChangeId, CultureInfo.InvariantCulture) >= number)
                    throw new HistoryResolutionException("日期筛选发现上一批边界之前漏读的变更 " + change.ChangeId + "，不能跳过该空档。");
        }

        private static bool SameChange(HistoryChange a, HistoryChange b)
        {
            if (!a.ParsedAllDepotChanges || !b.ParsedAllDepotChanges || a.ChangeId != b.ChangeId || a.ObservedAtUtc != b.ObservedAtUtc || a.Deltas.Count != b.Deltas.Count) return false;
            if ((a.Publication == null) != (b.Publication == null)) return false;
            if (a.Publication != null && (a.Publication.BuildId != b.Publication.BuildId || a.Publication.PublishedAtUtc != b.Publication.PublishedAtUtc || a.Publication.PreviousBuildId != b.Publication.PreviousBuildId || a.Publication.PreviousPublishedAtUtc != b.Publication.PreviousPublishedAtUtc)) return false;
            return a.Deltas.All(x => b.Deltas.Any(y => x.Path == y.Path && x.OldExists == y.OldExists && x.NewExists == y.NewExists && x.OldValue == y.OldValue && x.NewValue == y.NewValue));
        }

        private sealed class AfterReplaySelection
        {
            internal HistoryChange Candidate;
            internal HistoryDepotState State;
            internal string Build;
            internal DateTimeOffset Time;
            internal DateTimeOffset? LastPublicTime;
        }

        // Descending replay cannot return the first event after the request: that
        // would be the newest event, not the nearest one. Retain the candidate's
        // complete state and continue through the real lower public boundary.
        // This is also used when a browser has no date-navigation support.
        private ResolvedHistoryPlan ApplyQueryChange(HistoryChange change, HistoryDepotState state, DateTimeOffset? requested,
            string targetBuild, DateTimeOffset? patchObservedAt, OfficialPatchMatch officialProof, DateFirstLocation located, string patch, string language,
            ref string currentBuild, ref DateTimeOffset currentTime, ref int patchWindowEvents, AfterReplaySelection after)
        {
            if (after == null) return ApplyChange(change, state, requested, targetBuild, patchObservedAt, officialProof, located, patch, language,
                ref currentBuild, ref currentTime, ref patchWindowEvents);
            DateTimeOffset? eventTime = change.Publication == null ? (DateTimeOffset?)null : EffectiveEventTime(change);
            if (eventTime.HasValue)
            {
                if (after.LastPublicTime.HasValue && eventTime.Value > after.LastPublicTime.Value)
                    throw new HistoryResolutionException("完整历史回放中的公开事件时间出现倒退，无法证明严格晚于输入的最近版本；请先更新并校验完整本地资料。");
                after.LastPublicTime = eventTime.Value;
                if (eventTime.Value > requested.Value && (after.Candidate == null || eventTime.Value < after.Time))
                {
                    after.Candidate = change;
                    after.State = CloneState(state);
                    after.Build = currentBuild;
                    after.Time = eventTime.Value;
                }
            }
            // Validate and reverse even the lower boundary before authorizing a
            // result. A candidate alone never proves complete interval coverage.
            ApplyChange(change, state, null, null, null, null, null, "", null,
                ref currentBuild, ref currentTime, ref patchWindowEvents);
            if (!eventTime.HasValue || eventTime.Value > requested.Value) return null;
            if (after.Candidate == null)
                throw new HistoryResolutionException("已核实的公开历史中没有严格晚于输入时间的版本；相等时刻不包含在内。");
            if (located != null && (!SameChange(after.Candidate, located.CandidateChange) || after.Time != located.EffectiveTime))
                throw new HistoryResolutionException("日期定位候选与完整连续回放证明的最近后继版本不一致，不能下载。");
            var exact = located ?? new DateFirstLocation {
                CandidateChange = after.Candidate, Publication = after.Candidate.Publication,
                PreviousPublicChange = change, EffectiveTime = after.Time,
                UsedObservationFallback = !HasPublicTime(after.Candidate.Publication.PublishedAtUtc) || !HasPublicTime(change.Publication.PublishedAtUtc)
            };
            string candidateBuild = after.Build;
            DateTimeOffset candidateTime = HasPublicTime(after.Candidate.Publication.PublishedAtUtc) ? after.Time : DateTimeOffset.MinValue;
            int ignoredWindowEvents = 0;
            ResolvedHistoryPlan result = ApplyChange(after.Candidate, after.State, null, after.Build, null, null, exact, "", language,
                ref candidateBuild, ref candidateTime, ref ignoredWindowEvents);
            if (result == null) throw new HistoryResolutionException("完整回放未能还原最近后继版本的精确状态。");
            result.Assurance += "时间查询已沿完整连续链回放至输入时刻或更早的真实公开事件，按有效时间选择严格晚于输入的最近一次；相等时刻不包含，同一有效时间保留较新的变更记录。";
            if (!result.Sources.Contains(change.SourceUrl)) result.Sources.Add(change.SourceUrl);
            return result;
        }

        private ResolvedHistoryPlan ApplyChange(HistoryChange change, HistoryDepotState state, DateTimeOffset? requested,
            string targetBuild, DateTimeOffset? patchObservedAt, OfficialPatchMatch officialProof, DateFirstLocation located, string patch, string language, ref string currentBuild, ref DateTimeOffset currentTime, ref int patchWindowEvents)
        {
            if (!change.ParsedAllDepotChanges) throw new HistoryResolutionException("无法完整解析历史变更 " + change.ChangeId + " 中的 Depot 字段：" + String.Join(", ", change.UnparsedChanges.Take(6)) + "。已停止下载，避免拼接不兼容的文件。来源：" + change.SourceUrl);
            HistoryBuildEvent publication = change.Publication;
            if (publication != null)
            {
                string build = publication.BuildId ?? currentBuild;
                bool precise = HasPublicTime(publication.PublishedAtUtc);
                DateTimeOffset eventTime = precise ? publication.PublishedAtUtc : change.ObservedAtUtc;
                if (!String.Equals(build, currentBuild, StringComparison.Ordinal) || precise && HasPublicTime(currentTime) && publication.PublishedAtUtc != currentTime)
                    throw new HistoryResolutionException("公开分支历史与当前基线不连续（变更 " + change.ChangeId + "），可能有尚未加载的记录或查询期间发生更新。");
                bool releaseWindow = officialProof != null && officialProof.Kind == "OfficialReleaseWindow";
                string sourceChangeId = located != null ? located.CandidateChange.ChangeId : officialProof == null || officialProof.Build == null ? null : officialProof.Build.ChangeId;
                if (!String.IsNullOrEmpty(sourceChangeId) && sourceChangeId == change.ChangeId && targetBuild != null && build != targetBuild)
                    throw new HistoryResolutionException("来源页指向的历史记录属于另一个公开 Build，版本依据与历史不一致。");
                if (releaseWindow && eventTime > officialProof.NotAvailableAtUtc.Value && eventTime <= officialProof.AvailableByUtc.Value)
                {
                    patchWindowEvents++;
                    if (patchWindowEvents > 1 || build != targetBuild) throw new HistoryResolutionException("官方公告界定的发布窗口内存在其他公开构建或回滚，不能证明版本 " + patch + " 的唯一首发构建。");
                }
                // Timestamp selection is decided by the complete descending replay.
                // A date page may omit a closer event near its cutoff; do not skip
                // that event merely to reach the earlier locator candidate's ID.
                bool selected = requested.HasValue ? eventTime < requested.Value : located != null ? sourceChangeId == change.ChangeId : targetBuild != null && build == targetBuild &&
                    (String.IsNullOrEmpty(sourceChangeId) ? patchObservedAt.HasValue && eventTime <= patchObservedAt.Value : sourceChangeId == change.ChangeId);
                if (selected)
                {
                    state.Validate(change);
                    if (located != null && (!SameChange(change, located.CandidateChange) || eventTime != located.EffectiveTime))
                        throw new HistoryResolutionException("日期定位的候选记录与完整回放中的同一 ChangeId 不一致，不能合并两个不同状态。");
                    DateTimeOffset? previousTime = publication.PreviousPublishedAtUtc;
                    if (!previousTime.HasValue && located != null && located.PreviousPublicChange != null && located.PreviousPublicChange.Publication != null)
                        previousTime = EffectiveEventTime(located.PreviousPublicChange);
                    if (requested.HasValue && eventTime >= requested.Value)
                        throw new HistoryResolutionException("完整回放中的候选时间没有严格早于输入时间；不包含相等时刻。");
                    if (officialProof != null && !previousTime.HasValue && located == null)
                        throw new HistoryResolutionException("候选构建的历史记录缺少前一公开事件的时间，不能完成原始发布记录核对。");
                    if (releaseWindow && (patchWindowEvents != 1 || eventTime <= officialProof.NotAvailableAtUtc.Value || eventTime > officialProof.AvailableByUtc.Value || !previousTime.HasValue || previousTime.Value > officialProof.NotAvailableAtUtc.Value))
                        throw new HistoryResolutionException("候选构建的公开分支历史没有完整界定官方发布窗口，不能只凭公告日期宣布它是首发。");
                    // Reverse has not yet applied this event; this is the exact state immediately after it.
                    return new ResolvedHistoryPlan {
                        BuildId = build, PublishedAtUtc = eventTime, Patch = patch.Length == 0 ? null : patch,
                        SourceUrl = change.SourceUrl, Depots = state.SelectWindowsClient(language ?? "schinese"), Complete = true,
                        Assurance = "从公开分支完整 Depot 基线逐条逆向还原网站提供的连续历史，保留未变更 Depot，处理已解析的字段增删与 Manifest 回滚；始终核对 Build 与 Manifest 连续性，有原始时间字段时同时核对时间。" + (requested.HasValue ? "按时间查询选择完整倒序回放中首个有效时间严格早于输入的公开事件，不包含相等时刻。" : "") + "尚未验证 Steam 实际可下载性和客户端运行。" + (officialProof == null ? "" : officialProof.Assurance) + (precise ? "" : "该旧记录没有 Valve public/timeupdated，本次匹配采用 SteamDB 公开变更观察时间，不能视为 Valve 精确发布时间。") + CompletedLocationAssurance(located, requested.HasValue, precise),
                        TimeSource = precise ? "Valve public/timeupdated" : "SteamDB public branch change observed time",
                        UsedObservationFallback = !precise || located != null && located.UsedObservationFallback,
                        Sources = new List<string> { change.SourceUrl },
                        PatchMatchKind = officialProof == null ? (patch.Length == 0 ? null : "SteamDbPatchTitle") : officialProof.Kind,
                        PatchSourceChangeId = sourceChangeId,
                        PatchSourceObservedAtUtc = patchObservedAt,
                        PatchWindowNotAvailableAtUtc = releaseWindow ? officialProof.NotAvailableAtUtc : null,
                        PatchWindowAvailableByUtc = releaseWindow ? officialProof.AvailableByUtc : null,
                        PatchWindowPublicEvents = releaseWindow ? (int?)patchWindowEvents : null,
                        Limitations = new List<string> { "依赖 SteamDB 所提供的历史记录覆盖范围。", "Windows 标准客户端，包含英语和所选语言，排除其他平台、低暴力变体及可选 DLC；系统运行库另行检查。" }
                    };
                }
                if (publication.PreviousBuildId != null) currentBuild = publication.PreviousBuildId;
                // Missing old time metadata is an unknown timestamp, not year 0001.
                // Build and Manifest continuity remain mandatory at every older event.
                currentTime = publication.PreviousPublishedAtUtc ?? DateTimeOffset.MinValue;
            }
            state.Reverse(change);
            return null;
        }

        private static bool HasPublicTime(DateTimeOffset value) { return value.ToUniversalTime().Year >= 1970; }
        private static string CompletedLocationAssurance(DateFirstLocation located, bool timestampQuery, bool precise)
        {
            if (located == null) return "";
            if (!timestampQuery) return (located.Assurance ?? "").Replace("；尚未还原完整 Depot。", "。");
            return precise && located.UsedObservationFallback ? "相邻旧事件缺少 Valve 原始时间，匹配边界采用 SteamDB 观察时间；候选事件自身仍采用 Valve public/timeupdated。" : "";
        }
        private static DateTimeOffset EffectiveEventTime(HistoryChange change) { return HasPublicTime(change.Publication.PublishedAtUtc) ? change.Publication.PublishedAtUtc : change.ObservedAtUtc; }

        private static void ValidateOfficialWindow(OfficialPatchMatch match, string patch)
        {
            if (match.Kind != "OfficialReleaseWindow") return;
            if (!match.NotAvailableAtUtc.HasValue || !match.AvailableByUtc.HasValue || match.NotAvailableAtUtc.Value >= match.AvailableByUtc.Value || match.WindowEvidence == null)
                throw new HistoryResolutionException("官方发布状态窗口缺少有效上下界。");
            bool before = match.WindowEvidence.Any(e => e.Patch == patch && e.State == "NotYetPublic" && e.AnnouncedAtUtc == match.NotAvailableAtUtc.Value && !String.IsNullOrEmpty(e.SourceUrl));
            bool after = match.WindowEvidence.Any(e => e.Patch == patch && e.State == "AvailablePublic" && e.AnnouncedAtUtc == match.AvailableByUtc.Value && !String.IsNullOrEmpty(e.SourceUrl));
            if (!before || !after) throw new HistoryResolutionException("官方发布窗口必须分别有同一版本尚未公开与已经公开的发布状态证据。");
        }

        private async Task<ResolvedHistoryPlan> FinishAsync(ResolvedHistoryPlan plan, HistoryBaseline baseline, string patchSource,
            List<string> sources, int count, CancellationToken token, Action<string> progress)
        {
            if (usingHistoricalAnchor)
            {
                Report(progress, "已通过历史完整基线与连续差分还原 Build " + plan.BuildId + "；目标 ChangeId 与本次日期查询的原始记录一致。");
                plan.Assurance += "完整状态来自历史锚点 " + HistoryAnchorStore.Identity(baseline) + "；其完整基线与原始差分已重新回放验证，目标 ChangeId 与本次网页定位记录逐字段一致。没有把当前版本变化当作旧历史失效。";
            }
            else
            {
                Report(progress, "已还原 Build " + plan.BuildId + " 的 " + plan.Depots.Count + " 个 Depot；正在确认本次读取的公开分支基线没有变化……");
                HistoryPageSnapshot afterPage = await browser.NavigateAsync(BaselineUrl, token);
                HistoryBaseline after = await Task.Run(() => parser.ParseBaseline(afterPage), token);
                if (!SameBaseline(baseline, after))
                {
                    if (anchorStore == null) { cachedBaseline = null; cachedChanges.Clear(); }
                    throw new HistoryResolutionException("查询期间 Dota 2 公开分支发生更新。已验证的历史证据会保留；请重新定位目标后继续。");
                }
            }
            foreach (string source in sources) if (!plan.Sources.Contains(source)) plan.Sources.Add(source);
            if (patchSource != null) plan.Sources.Add(patchSource);
            plan.Assurance += "已核对 " + count + " 条历史记录。" + (usingHistoricalAnchor ? "" : "并复核本次网页基线未改变。");
            if (plan.PatchMatchKind == "OfficialReleaseWindow") plan.Assurance += "官方尚未公开／已公开公告之间仅有这一次公开分支发布，前次事件位于窗口之外；事件时间来源为 " + plan.TimeSource + "。";
            else if (plan.Patch != null)
                plan.Assurance += String.IsNullOrEmpty(plan.PatchSourceChangeId) ?
                    "版本候选依据官方来源与构建页面原始日期关联，历史链已核实该次公开启用和前一公开事件；首发判断依赖网站原始发布记录的覆盖与准确性。" :
                    "版本候选依据官方来源关联，并精确匹配来源页指向的历史变更 " + plan.PatchSourceChangeId + "；更晚的相同 Build 再次启用不作为本次首发。";
            return plan;
        }

        private static bool SameBaseline(HistoryBaseline left, HistoryBaseline right)
        {
            return left != null && right != null && left.BuildId == right.BuildId && left.PublishedAtUtc == right.PublishedAtUtc &&
                SameState(left.State, right.State);
        }
        private static bool SameState(HistoryDepotState left, HistoryDepotState right)
        {
            var critical = left.Values.Where(p => !HistoryDepotState.IsNamePath(p.Key)).ToList();
            return left.HasKnownLegacyDlcMarking == right.HasKnownLegacyDlcMarking &&
                critical.Count == right.Values.Count(p => !HistoryDepotState.IsNamePath(p.Key)) && critical.All(p => right.Value(p.Key) == p.Value);
        }
        private static HistoryDepotState CloneState(HistoryDepotState source)
        {
            var result = new HistoryDepotState { CompleteBaseline = source.CompleteBaseline, HasSharedPrerequisiteBaseline = source.HasSharedPrerequisiteBaseline, HasKnownLegacyDlcMarking = source.HasKnownLegacyDlcMarking };
            foreach (var pair in source.Values) result.Values.Add(pair.Key, pair.Value);
            foreach (var pair in source.DisplayNames) result.DisplayNames.Add(pair.Key, pair.Value);
            foreach (string path in source.KnownNamePaths) result.KnownNamePaths.Add(path);
            foreach (string path in source.KnownSharedInstallPaths) result.KnownSharedInstallPaths.Add(path);
            foreach (string path in source.KnownOptionalDlcPaths) result.KnownOptionalDlcPaths.Add(path);
            return result;
        }
        private static HistoryBaseline CloneBaseline(HistoryBaseline source) { return new HistoryBaseline { BuildId = source.BuildId, PublishedAtUtc = source.PublishedAtUtc, SourceUrl = source.SourceUrl, State = CloneState(source.State) }; }
        private static void Report(Action<string> progress, string message) { if (progress != null) progress(message); }
    }
}
