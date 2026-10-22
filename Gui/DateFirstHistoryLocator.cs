using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DotaArchive
{
    public sealed class DateFirstLocation
    {
        public DateTimeOffset? RequestedUtc { get; set; }
        public HistoryChange CandidateChange { get; set; }
        public HistoryBuildEvent Publication { get; set; }
        public HistoryChange PreviousPublicChange { get; set; }
        public HistoryChange NextPublicChange { get; set; }
        public List<HistoryChange> NeighborChanges { get; set; }
        public List<DateTime> RequestedDates { get; set; }
        public DateTimeOffset ObservedFromUtc { get; set; }
        public DateTimeOffset ObservedThroughUtc { get; set; }
        public bool HasPrecisePublicTime { get; set; }
        public DateTimeOffset EffectiveTime { get; set; }
        public bool UsedObservationFallback { get; set; }
        public string TimeSource { get; set; }
        public string Assurance { get; set; }
        public List<string> Sources { get; set; }
        public DateFirstLocation() { NeighborChanges = new List<HistoryChange>(); RequestedDates = new List<DateTime>(); Sources = new List<string>(); }
    }

    // Date controls locate a neighborhood. Their inclusive/exclusive timezone
    // semantics are deliberately not used as evidence of a publication timestamp.
    public sealed class DateFirstHistoryLocator
    {
        private const string HistoryUrl = "https://steamdb.info/app/570/history/?filterkey=2";
        private static readonly TimeSpan BeijingOffset = TimeSpan.FromHours(8);
        private readonly IHistoryBrowser browser;
        private readonly IHistoryDateBrowser dated;
        private readonly SteamDbHistoryParser parser = new SteamDbHistoryParser();
        private readonly Func<DateTimeOffset> now;
        private readonly bool allowObservedFallback;

        public DateFirstHistoryLocator(IHistoryBrowser browser) : this(browser, true) { }
        public DateFirstHistoryLocator(IHistoryBrowser browser, bool allowObservedFallback) : this(browser, delegate { return DateTimeOffset.UtcNow; }, allowObservedFallback) { }
        internal DateFirstHistoryLocator(IHistoryBrowser browser, Func<DateTimeOffset> now, bool allowObservedFallback)
        {
            if(browser == null) throw new ArgumentNullException("browser");
            if(now == null) throw new ArgumentNullException("now");
            this.browser = browser; this.dated = browser as IHistoryDateBrowser; this.now = now; this.allowObservedFallback = allowObservedFallback;
        }

        public Task<DateFirstLocation> LocateAsync(DateTimeOffset requested, CancellationToken token, Action<string> progress)
        {
            return LocateCoreAsync(requested.ToUniversalTime(), null, requested, true, token, progress);
        }

        // After-time queries and official patch release windows both locate the
        // first public event strictly after their lower bound.
        public Task<DateFirstLocation> LocateAfterAsync(DateTimeOffset requested, CancellationToken token, Action<string> progress)
        {
            return LocateCoreAsync(requested.ToUniversalTime(), null, requested, false, token, progress);
        }

        public Task<DateFirstLocation> LocateExactChangeAsync(string changeId, DateTimeOffset observedAt, CancellationToken token, Action<string> progress)
        {
            if(!HistoryDepotState.IsNumeric(changeId)) throw new HistoryResolutionException("待定位的历史变更编号无效。");
            return LocateCoreAsync(null, changeId, observedAt, false, token, progress);
        }

        private async Task<DateFirstLocation> LocateCoreAsync(DateTimeOffset? requested, string exactChangeId, DateTimeOffset approximate, bool before,
            CancellationToken token, Action<string> progress)
        {
            if(dated == null) throw new HistoryResolutionException("浏览器不支持日期筛选，不能执行日期优先历史定位。");
            token.ThrowIfCancellationRequested();
            if(!before && requested.HasValue && requested.Value >= now().ToUniversalTime()) throw new HistoryResolutionException("所选时间尚未过去，不能证明已有严格晚于该时间的公开版本。");
            if(before && approximate > now()) approximate = now();
            DateTime date = DateTime.SpecifyKind(approximate.ToOffset(BeijingOffset).Date.AddDays(1), DateTimeKind.Unspecified);
            DateTime lastDate = now().ToOffset(BeijingOffset).Date.AddDays(1);
            var requestedDates = new List<DateTime>();
            // The history page must exist to operate its visible date control.
            // Do not parse or replay the current-day records from this navigation.
            IHistoryDateNavigationBrowser dateNavigation = browser as IHistoryDateNavigationBrowser;
            if(dateNavigation != null) await dateNavigation.OpenHistoryForDateAsync(token);
            else await browser.NavigateAsync(HistoryUrl, token);
            while(date <= lastDate)
            {
                token.ThrowIfCancellationRequested();
                requestedDates.Add(date);
                Report(progress, "日期优先定位：读取网站日历 " + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " 附近记录" + (allowObservedFallback ? "；旧版缺少 Valve 原始时间时将明确标注 SteamDB 观察时间……" : "；严格核对 Valve 公开分支原始时间……"));
                HistoryPageSnapshot page = await dated.LoadHistoryBeforeAsync(date, token);
                var records = new List<HistoryChange>();
                HistoryChange requiredOverlap = null;
                bool triedDateContinuation = false;
                while(true)
                {
                    token.ThrowIfCancellationRequested();
                    HistoryPageSnapshot currentPage = page;
                    HistoryListing listing = await Task.Run(delegate { return parser.ParseHistory(currentPage); }, token);
                    if(listing.Changes.Count == 0)
                    {
                        if(records.Count > 0 || !listing.EndReached) throw new HistoryResolutionException("日期筛选没有返回可核实的历史记录，不能把加载中页面当作空更新日。");
                        if(before) throw new HistoryResolutionException("网站在目标日期之前没有返回公开历史记录，无法确定严格早于输入时间的版本。");
                        break;
                    }
                    int added = MergeOlder(records, listing.Changes, requiredOverlap);
                    if(added == 0 && requiredOverlap != null)
                    {
                        if(triedDateContinuation) throw new HistoryResolutionException("局部日期分页没有增加更早记录，无法证明日内历史完整性。");
                        triedDateContinuation = true;
                        DateTime continuation = DateTime.SpecifyKind(requiredOverlap.ObservedAtUtc.UtcDateTime.Date.AddDays(1), DateTimeKind.Unspecified);
                        requestedDates.Add(continuation);
                        page = await dated.LoadHistoryBeforeAsync(continuation, token);
                        continue;
                    }
                    if(exactChangeId != null)
                    {
                        HistoryChange exact = records.FirstOrDefault(c => c.ChangeId == exactChangeId);
                        if(exact != null) return MakeResult(exact, null, records, requestedDates, null, true);
                        ulong wanted = UInt64.Parse(exactChangeId, CultureInfo.InvariantCulture);
                        ulong newest = UInt64.Parse(records[0].ChangeId, CultureInfo.InvariantCulture);
                        ulong oldest = UInt64.Parse(records[records.Count - 1].ChangeId, CultureInfo.InvariantCulture);
                        if(newest < wanted) break; // The date window is still too early.
                        if(oldest <= wanted) throw new HistoryResolutionException("日期邻域已跨过指定 ChangeId，但没有该条记录，不能跳过历史缺口。");
                    }
                    else
                    {
                        bool moveForward = false;
                        DateFirstLocation found = before ? TryFindPrevious(records, requested.Value, requestedDates) : TryFindNext(records, requested.Value, requestedDates, out moveForward);
                        if(found != null) return found;
                        if(!before && moveForward) break;
                    }
                    if(listing.EndReached) throw new HistoryResolutionException(before ? "已读到网站历史末尾，没有找到严格早于输入时间的公开版本；相等时间不计入。" : "已读到网站历史末尾，但缺少围住所选时间的前后公开事件，无法证明最近版本。");
                    requiredOverlap = records[records.Count - 1];
                    if(!String.IsNullOrEmpty(listing.LoadMoreSelector)) page = await browser.ClickAsync(listing.LoadMoreSelector, token);
                    else if(!String.IsNullOrEmpty(listing.NextPageUrl)) page = await browser.NavigateAsync(listing.NextPageUrl, token);
                    else
                    {
                        if(triedDateContinuation) throw new HistoryResolutionException("局部历史没有可继续的分页方式，不能把一批记录当作完整日期范围。");
                        triedDateContinuation = true;
                        DateTime continuation = DateTime.SpecifyKind(requiredOverlap.ObservedAtUtc.UtcDateTime.Date.AddDays(1), DateTimeKind.Unspecified);
                        requestedDates.Add(continuation);
                        page = await dated.LoadHistoryBeforeAsync(continuation, token);
                    }
                }
                Report(progress, "这一日期邻域尚未出现严格晚于输入的可核实公开事件，向后推进一天……");
                date = date.AddDays(1);
            }
            throw new HistoryResolutionException(exactChangeId == null ? "已推进到当前日期，仍没有找到能严格包围输入时间的公开版本；没有用默认零值或日期筛选边界代替事件时间。" : "已推进到当前日期，仍未找到来源指定的历史变更。");
        }

        private static int MergeOlder(List<HistoryChange> records, IList<HistoryChange> incoming, HistoryChange overlap)
        {
            var byId = records.ToDictionary(c => c.ChangeId, StringComparer.Ordinal);
            if(incoming.Select(c => c.ChangeId).Distinct(StringComparer.Ordinal).Count() != incoming.Count) throw new HistoryResolutionException("邻近历史批次包含重复 ChangeId。");
            if(overlap != null)
            {
                HistoryChange repeated = incoming.FirstOrDefault(c => c.ChangeId == overlap.ChangeId);
                if(repeated == null || !SameChange(overlap, repeated)) throw new HistoryResolutionException("局部分页没有包含相同的边界记录，无法证明日期邻域连续。");
            }
            int added = 0;
            foreach(HistoryChange change in incoming)
            {
                HistoryChange existing;
                if(byId.TryGetValue(change.ChangeId, out existing))
                {
                    if(!SameChange(existing, change)) throw new HistoryResolutionException("同一历史记录在分页期间发生变化，不能继续选择版本。");
                    continue;
                }
                if(overlap != null && CompareNewestFirst(change, overlap) <= 0) throw new HistoryResolutionException("局部分页在边界之前插入了未读取的记录，历史范围存在缺口。");
                records.Add(change); byId.Add(change.ChangeId, change); added++;
            }
            records.Sort(CompareNewestFirst);
            return added;
        }

        private DateFirstLocation TryFindNext(List<HistoryChange> records, DateTimeOffset requested, List<DateTime> dates, out bool moveForward)
        {
            moveForward = false;
            var publics = new List<HistoryChange>();
            foreach(HistoryChange change in records)
            {
                if(!change.ParsedAllDepotChanges) throw new HistoryResolutionException("历史变更 " + change.ChangeId + " 含有尚不能解析的 Depot 字段，无法确认此处是否缺少公开事件：" + String.Join("; ", change.UnparsedChanges.Take(6)));
                if(change.Publication == null) continue;
                DateTimeOffset effective = EffectiveTimeOf(change);
                if(effective > change.ObservedAtUtc) throw new HistoryResolutionException("公开事件时间晚于该记录的 SteamDB 观察时间，日期邻域不足以可靠定位该事件。");
                HistoryBuildEvent current = change.Publication;
                if(publics.Count == 0 && effective <= requested) { moveForward = true; return null; }
                if(publics.Count > 0)
                {
                    HistoryBuildEvent later = publics[publics.Count - 1].Publication;
                    HistoryChange laterChange = publics[publics.Count - 1];
                    if(HasPreciseTime(laterChange) && HasPreciseTime(change) && (!later.PreviousPublishedAtUtc.HasValue || later.PreviousPublishedAtUtc.Value.Year < 1970 || later.PreviousPublishedAtUtc.Value != current.PublishedAtUtc))
                        throw new HistoryResolutionException("相邻公开事件的 timeupdated 前后值不连续，不能证明严格晚于输入的最近版本。");
                    if(EffectiveTimeOf(laterChange) < effective) throw new HistoryResolutionException("相邻公开事件的有效时间出现倒退；混合时间来源不足以自动选择最近版本。");
                    if((!HasPreciseTime(laterChange) || !HasPreciseTime(change)) && (later.PreviousBuildId == null || current.BuildId == null))
                        throw new HistoryResolutionException("旧记录缺少相邻公开 Build 的旧新值，无法通过 SteamDB 观察时间证明最近后继。");
                    if(later.PreviousBuildId != null && current.BuildId != null && later.PreviousBuildId != current.BuildId)
                        throw new HistoryResolutionException("相邻公开事件的 Build 前后值不连续。");
                }
                publics.Add(change);
                if(effective <= requested)
                {
                    // Equal timestamps represent separate activations. Pick the
                    // newer changelist deterministically, as the local resolver does.
                    HistoryChange selected = publics.Where(c => EffectiveTimeOf(c) > requested).OrderBy(EffectiveTimeOf).First();
                    return MakeResult(selected, change, records, dates, requested, false);
                }
            }
            // A fully parsed neighborhood with no public event cannot contain a
            // future publication once its observation range has crossed the input.
            // Public timestamps must not postdate their own tracker observation.
            if(publics.Count == 0 && records.Count > 0 && records[records.Count - 1].ObservedAtUtc <= requested) moveForward = true;
            return null;
        }

        private DateFirstLocation TryFindPrevious(List<HistoryChange> records, DateTimeOffset requested, List<DateTime> dates)
        {
            HistoryChange later = null;
            foreach(HistoryChange change in records)
            {
                if(!change.ParsedAllDepotChanges) throw new HistoryResolutionException("历史变更 " + change.ChangeId + " 含有尚不能解析的 Depot 字段，无法确认此处是否缺少公开事件：" + String.Join("; ", change.UnparsedChanges.Take(6)));
                if(change.Publication == null) continue;
                DateTimeOffset effective = EffectiveTimeOf(change);
                if(effective > change.ObservedAtUtc) throw new HistoryResolutionException("公开事件时间晚于该记录的 SteamDB 观察时间，日期邻域不足以可靠定位该事件。");
                if(later != null)
                {
                    if(HasPreciseTime(later) && HasPreciseTime(change) && (!later.Publication.PreviousPublishedAtUtc.HasValue || later.Publication.PreviousPublishedAtUtc.Value.Year < 1970 || later.Publication.PreviousPublishedAtUtc.Value != change.Publication.PublishedAtUtc))
                        throw new HistoryResolutionException("相邻公开事件的 timeupdated 前后值不连续，不能确认严格早于输入的最近版本。");
                    if(EffectiveTimeOf(later) < effective) throw new HistoryResolutionException("相邻公开事件的有效时间出现倒退；混合时间来源不足以自动选择最近版本。");
                    if((!HasPreciseTime(later) || !HasPreciseTime(change)) && (later.Publication.PreviousBuildId == null || change.Publication.BuildId == null))
                        throw new HistoryResolutionException("旧记录缺少相邻公开 Build 的旧新值，无法通过 SteamDB 观察时间确认最近前驱。");
                    if(later.Publication.PreviousBuildId != null && change.Publication.BuildId != null && later.Publication.PreviousBuildId != change.Publication.BuildId)
                        throw new HistoryResolutionException("相邻公开事件的 Build 前后值不连续。");
                }
                if(effective < requested)
                {
                    var result = MakeResult(change, null, records, dates, requested, false);
                    result.NextPublicChange = later;
                    result.UsedObservationFallback = !HasPreciseTime(change) || (later != null && !HasPreciseTime(later));
                    result.Assurance = "通过日期控件定位到所读邻域中有效时间严格早于输入的最近公开事件，相等时间已排除。完整历史回放还需确认没有更近的公开事件，并补齐全部 Depot；日期页不能单独作为完整下载清单。";
                    if(result.UsedObservationFallback) result.Assurance += "旧记录缺少 Valve 原始 timeupdated，按用户允许使用 SteamDB 公开分支变更的观察时间；此时间不是 Valve 原始发布时间，原始记录未被改写。";
                    if(change.Publication.BuildId == null) result.Assurance += "该条仅改变公开时间，BuildId 仍需由完整历史状态确定。";
                    return result;
                }
                later = change;
            }
            return null;
        }

        private DateFirstLocation MakeResult(HistoryChange candidate, HistoryChange previous, List<HistoryChange> records, List<DateTime> dates, DateTimeOffset? requested, bool exact)
        {
            if(!candidate.ParsedAllDepotChanges) throw new HistoryResolutionException("指定历史变更含有尚不能解析的 Depot 字段，不能确认该公开事件的完整性：" + String.Join("; ", candidate.UnparsedChanges.Take(6)));
            bool precise = HasPreciseTime(candidate);
            if(candidate.Publication == null || !candidate.Publication.IsPublic) throw new HistoryResolutionException("指定历史变更没有可核实的公开分支发布事件。");
            DateTimeOffset effective = EffectiveTimeOf(candidate);
            if(effective > candidate.ObservedAtUtc) throw new HistoryResolutionException("公开事件时间晚于该记录的 SteamDB 观察时间。");
            bool usedObserved = !precise || (previous != null && !HasPreciseTime(previous));
            var result = new DateFirstLocation { RequestedUtc = requested, CandidateChange = candidate, Publication = candidate.Publication, PreviousPublicChange = previous,
                NeighborChanges = records.ToList(), RequestedDates = dates.ToList(), ObservedThroughUtc = records[0].ObservedAtUtc, ObservedFromUtc = records[records.Count - 1].ObservedAtUtc,
                HasPrecisePublicTime = precise, EffectiveTime = effective, UsedObservationFallback = usedObserved,
                TimeSource = precise ? "Valve public/timeupdated" : allowObservedFallback ? "SteamDB public branch change observed time" : "Missing precise public/timeupdated",
                Assurance = exact ? "根据来源 ChangeId 在日期邻域精确定位到原始变更；尚未还原完整 Depot。" : "日期控件仅用于定位；相邻真实公开事件及其可核实的时间/Build 前后值连续包围输入，已选择有效时间严格晚于输入的最早事件；尚未还原完整 Depot。",
                Sources = records.Select(c => c.SourceUrl).Where(s => !String.IsNullOrEmpty(s)).Distinct().ToList() };
            if(usedObserved) result.Assurance += allowObservedFallback ? "旧记录缺少 Valve 原始 timeupdated，按用户允许使用 SteamDB 公开分支变更的观察时间；此时间不是 Valve 原始发布时间，原始记录未被改写。" : "该旧记录缺少 Valve 精确公开时间，不能用默认零值或 SteamDB 观察时间伪装发布时间。";
            if(candidate.Publication != null && candidate.Publication.BuildId == null) result.Assurance += "该条仅改变公开时间，BuildId 仍需由完整历史状态确定。";
            return result;
        }
        private static bool HasPreciseTime(HistoryChange change) { return change.Publication != null && change.Publication.IsPublic && change.Publication.PublishedAtUtc.ToUniversalTime().Year >= 1970; }
        private DateTimeOffset EffectiveTimeOf(HistoryChange change)
        {
            if(HasPreciseTime(change)) return change.Publication.PublishedAtUtc.ToUniversalTime();
            if(allowObservedFallback && change.Publication != null && change.Publication.IsPublic && change.ObservedAtUtc.ToUniversalTime().Year >= 1970) return change.ObservedAtUtc.ToUniversalTime();
            RequirePreciseTime(change); return default(DateTimeOffset);
        }
        private static void RequirePreciseTime(HistoryChange change)
        {
            if(!HasPreciseTime(change)) throw new HistoryResolutionException("历史变更 " + change.ChangeId + " 缺少 Valve public/timeupdated 精确时间；当前严格时间模式不支持以 SteamDB 观察时间替代。");
        }
        private static int CompareNewestFirst(HistoryChange a, HistoryChange b) { int time = b.ObservedAtUtc.CompareTo(a.ObservedAtUtc); return time != 0 ? time : UInt64.Parse(b.ChangeId, CultureInfo.InvariantCulture).CompareTo(UInt64.Parse(a.ChangeId, CultureInfo.InvariantCulture)); }
        private static bool SameChange(HistoryChange a, HistoryChange b)
        {
            if(a.ChangeId != b.ChangeId || a.ObservedAtUtc != b.ObservedAtUtc || a.ParsedAllDepotChanges != b.ParsedAllDepotChanges || a.Deltas.Count != b.Deltas.Count || !a.UnparsedChanges.SequenceEqual(b.UnparsedChanges)) return false;
            if((a.Publication == null) != (b.Publication == null)) return false;
            if(a.Publication != null && (a.Publication.BuildId != b.Publication.BuildId || a.Publication.PublishedAtUtc != b.Publication.PublishedAtUtc || a.Publication.PreviousBuildId != b.Publication.PreviousBuildId || a.Publication.PreviousPublishedAtUtc != b.Publication.PreviousPublishedAtUtc)) return false;
            return a.Deltas.All(x => b.Deltas.Any(y => x.Path == y.Path && x.OldExists == y.OldExists && x.NewExists == y.NewExists && x.OldValue == y.OldValue && x.NewValue == y.NewValue));
        }
        private static void Report(Action<string> progress, string value) { if(progress != null) progress(value); }
    }
}
