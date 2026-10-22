using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace DotaArchive
{
    public sealed class HistorySyncResult
    {
        public int AddedRecords { get; set; }
        public int TotalRecords { get; set; }
        public string LatestBuildId { get; set; }
        public string LatestChangeId { get; set; }
        public string AnchorIdentity { get; set; }
        public string GenerationDirectory { get; set; }
        public DateTimeOffset LatestBuildPublishedAtUtc { get; set; }
        public DateTimeOffset LatestChangeObservedAtUtc { get; set; }
        public DateTimeOffset VerifiedThroughUtc { get; set; }
    }

    public sealed class HistoryLatestSync
    {
        private readonly IHistoryBrowser browser;
        private readonly string bundled, cache, bundledSync;
        public HistoryLatestSync(IHistoryBrowser browser, string bundledDirectory, string cacheDirectory, string bundledSyncDirectory = null)
        {
            if (browser == null) throw new ArgumentNullException("browser");
            if (String.IsNullOrWhiteSpace(bundledDirectory) || String.IsNullOrWhiteSpace(cacheDirectory)) throw new ArgumentException("需要指定归档和本机缓存目录。");
            this.browser = browser; bundled = Path.GetFullPath(bundledDirectory); cache = Path.GetFullPath(cacheDirectory);
            bundledSync = String.IsNullOrWhiteSpace(bundledSyncDirectory) ? null : Path.GetFullPath(bundledSyncDirectory);
        }
        public async Task<HistorySyncResult> SyncAsync(CancellationToken token, Action<string> progress)
        {
            token.ThrowIfCancellationRequested();
            var store = new SyncCatalogStore(Path.Combine(cache, "latest-sync"));
            Directory.CreateDirectory(store.RootDirectory);
            FileStream writer;
            try { writer = new FileStream(Path.Combine(store.RootDirectory, "sync.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException ex) { throw new IOException("另一项版本资料更新正在进行；本次没有读取网站或修改已发布数据库。", ex); }
            using (writer)
            {
                var resolver = new OnlineHistoryResolver(browser, null, null, false);
                return await resolver.SyncLatestReadOnlyBaseAsync(bundled, cache, store, token, progress, bundledSync);
            }
        }
    }

    public sealed partial class OnlineHistoryResolver
    {
        private sealed class LatestSyncRun
        {
            internal HistoryBaseline Baseline;
            internal HistoryPageSnapshot FirstBaseline;
            internal string Directory;
            internal readonly List<HistoryChange> Prefix = new List<HistoryChange>();
            internal readonly List<string> Evidence = new List<string>();
        }
        public sealed class SyncPageEvidence
        {
            public string Url { get; set; }
            public string Title { get; set; }
            public string Html { get; set; }
            public string Text { get; set; }
            public string CapturedAtUtc { get; set; }
        }
        public sealed class SyncCheckpoint
        {
            public int SchemaVersion { get; set; }
            public string BaselineIdentity { get; set; }
            public string Payload { get; set; }
            public string PayloadSha256 { get; set; }
            public List<string> Evidence { get; set; }
        }

        internal async Task<HistorySyncResult> SyncLatestReadOnlyBaseAsync(string bundledDirectory, string cacheDirectory,
            SyncCatalogStore store, CancellationToken token, Action<string> progress, string bundledSyncDirectory)
        {
            Report(progress, "正在校验内置归档和本机已保存的完整历史；只抓取尚缺的最新变化……");
            SyncCatalogSnapshot active = await Task.Run(() => store.LoadActive(token), token);
            List<HistoryAnchorStore.Anchor> bundled = await Task.Run(() => ReadSyncAnchors(bundledDirectory), token);
            if (bundled.Count == 0) throw new HistoryResolutionException("内置完整归档缺失，不能把新网页局部历史当作完整数据库。");
            var all = new List<HistoryAnchorStore.Anchor>(bundled);
            if(!String.IsNullOrWhiteSpace(bundledSyncDirectory)) {
                SyncCatalogSnapshot packaged = await Task.Run(() => new SyncCatalogStore(bundledSyncDirectory).LoadActive(token), token);
                if(packaged != null) all.AddRange(packaged.Anchors);
            }
            all.AddRange(await Task.Run(() => ReadSyncAnchors(cacheDirectory), token));
            if (active != null) all.AddRange(await Task.Run(() => ReadSyncAnchors(active.GenerationDirectory), token));
            foreach (HistoryAnchorStore.Anchor anchor in all) { token.ThrowIfCancellationRequested(); await ValidateAnchorAsync(anchor, token); MergeAnchor(anchor); }
            HistoryAnchorStore.Anchor required = bundled.OrderBy(a => a.Changes.Last().ObservedAtUtc).First();
            HistoryAnchorStore.Anchor target = null;
            foreach (HistoryAnchorStore.Anchor candidate in anchors.OrderByDescending(a => UInt64.Parse(a.Changes[0].ChangeId, CultureInfo.InvariantCulture)))
                if (SyncContainsArchive(candidate, required, token)) { target = candidate; break; }
            if (target == null) throw new HistoryResolutionException("本机锚点无法连接到内置完整归档，已停止增量更新。");
            var run = new LatestSyncRun();
            Exception failure = null;
            HistorySyncResult result = null;
            try { result = await SyncLatestCoreAsync(target, store, run, token, progress); }
            catch (Exception ex) { failure = ex; }
            // Save only successfully replayed records. A failed/aborted run never
            // publishes current.json or a new successful coverage timestamp.
            if (failure != null)
            {
                try { await Task.Run(() => SaveSyncCheckpoint(run)); }
                catch (Exception saving) { throw new AggregateException("更新已停止，且本次断点无法保存；已发布数据库保持不变。", failure, saving); }
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
            return result;
        }

        private static List<HistoryAnchorStore.Anchor> ReadSyncAnchors(string directory)
        {
            var result = new HistoryAnchorStore(directory).Load();
            HistoryCacheStore.CacheData old = new HistoryCacheStore(directory).Load();
            if (old != null && old.Changes.Count > 0) result.Add(new HistoryAnchorStore.Anchor { Identity = HistoryAnchorStore.Identity(old.Baseline), Provenance = "ValidatedLegacyV1Migration", Baseline = old.Baseline, Changes = old.Changes });
            return result;
        }

        private bool SyncContainsArchive(HistoryAnchorStore.Anchor candidate, HistoryAnchorStore.Anchor required, CancellationToken token)
        {
            int index = candidate.Changes.FindIndex(c => c.ChangeId == required.Changes[0].ChangeId);
            if (index < 0 || candidate.Changes.Count - index < required.Changes.Count) return false;
            for (int i = 0; i < required.Changes.Count; i++)
                if (!SameChange(candidate.Changes[index + i], required.Changes[i])) throw new HistoryResolutionException("本机与内置归档的重叠记录冲突，不能更新数据库。");
            var state = CloneState(candidate.Baseline.State); string build = candidate.Baseline.BuildId; DateTimeOffset time = candidate.Baseline.PublishedAtUtc; int events = 0;
            for (int i = 0; i < index; i++) { token.ThrowIfCancellationRequested(); ApplyChange(candidate.Changes[i], state, null, null, null, null, null, "", null, ref build, ref time, ref events); }
            RequireSyncBoundary(state, build, time, required.Baseline);
            return true;
        }

        private static void RequireSyncBoundary(HistoryDepotState state, string build, DateTimeOffset time, HistoryBaseline baseline)
        {
            if (!SameState(state, baseline.State) || build != baseline.BuildId ||
                HasPublicTime(time) && HasPublicTime(baseline.PublishedAtUtc) && time != baseline.PublishedAtUtc)
                throw new HistoryResolutionException("相同历史边界的完整 Depot 状态、Build 或发布时间不一致，不能拼接新旧资料。");
        }

        private async Task<HistorySyncResult> SyncLatestCoreAsync(HistoryAnchorStore.Anchor target, SyncCatalogStore store,
            LatestSyncRun run, CancellationToken token, Action<string> progress)
        {
            run.FirstBaseline = await browser.NavigateAsync(BaselineUrl, token);
            RequireSyncCapture(run.FirstBaseline);
            run.Baseline = await Task.Run(() => parser.ParseBaseline(run.FirstBaseline), token);
            run.Directory = Path.Combine(store.RootDirectory, "checkpoints", HistoryAnchorStore.Identity(run.Baseline));
            await Task.Run(() => SaveSyncEvidence(run, run.FirstBaseline), token);
            HistoryPageSnapshot page = await browser.NavigateAsync(HistoryUrl, token);
            RequireSyncCapture(page);
            await Task.Run(() => SaveSyncEvidence(run, page), token);
            HistoryListing first = await Task.Run(() => ParseSyncHistory(page), token);
            if (first.Changes.Count == 0) throw new HistoryResolutionException("最新历史页没有已加载记录，不能确认数据库已更新。");
            var state = CloneState(run.Baseline.State); string build = run.Baseline.BuildId; DateTimeOffset time = run.Baseline.PublishedAtUtc; int events = 0;
            var known = new Dictionary<string, HistoryChange>(StringComparer.Ordinal);
            HistoryChange requiredOverlap = null;
            bool dateBatch = false;
            List<HistoryChange> resumed = await Task.Run(() => LoadSyncCheckpoint(run, first), token);
            if (resumed.Count > 0 && !resumed.Any(c => c.ChangeId == target.Changes[0].ChangeId))
            {
                foreach (HistoryChange change in resumed) { token.ThrowIfCancellationRequested(); ApplyChange(change, state, null, null, null, null, null, "", null, ref build, ref time, ref events); known.Add(change.ChangeId, change); }
                run.Prefix.AddRange(resumed);
                IHistoryDateBrowser dated = browser as IHistoryDateBrowser;
                if (dated == null) throw new HistoryResolutionException("存在已验证更新断点，但当前浏览器不支持日期接续；不会重新抓取整段旧历史。");
                requiredOverlap = run.Prefix.Last(); dateBatch = true;
                Report(progress, "已重新校验 " + run.Prefix.Count + " 条增量断点；从 " + requiredOverlap.ChangeId + " 使用日期控件接续。");
                page = await dated.LoadHistoryBeforeAsync(requiredOverlap.ObservedAtUtc.UtcDateTime.Date.AddDays(1), token);
            }
            while (true)
            {
                token.ThrowIfCancellationRequested(); RequireSyncCapture(page);
                await Task.Run(() => SaveSyncEvidence(run, page), token);
                HistoryListing listing = await Task.Run(() => ParseSyncHistory(page), token);
                if (requiredOverlap != null) { VerifyBatchOverlap(requiredOverlap, listing, new HashSet<string>(known.Keys, StringComparer.Ordinal)); requiredOverlap = null; }
                foreach (HistoryChange repeated in listing.Changes.Where(c => known.ContainsKey(c.ChangeId)))
                    if (!SameChange(known[repeated.ChangeId], repeated)) throw new HistoryResolutionException("更新批次的重叠记录内容冲突，已停止。");
                int before = run.Prefix.Count;
                foreach (HistoryChange change in listing.Changes)
                {
                    if (known.ContainsKey(change.ChangeId)) continue;
                    token.ThrowIfCancellationRequested();
                    if (run.Prefix.Count > 0)
                    {
                        HistoryChange last = run.Prefix.Last();
                        if (change.ObservedAtUtc > last.ObservedAtUtc || UInt64.Parse(change.ChangeId, CultureInfo.InvariantCulture) >= UInt64.Parse(last.ChangeId, CultureInfo.InvariantCulture))
                            throw new HistoryResolutionException("增量记录没有按顺序接续已验证边界，不能跳过空档。");
                    }
                    if (change.ChangeId == target.Changes[0].ChangeId)
                    {
                        if (!SameChange(change, target.Changes[0])) throw new HistoryResolutionException("新网页与已保存首条历史内容冲突，不能连接数据库。");
                        RequireSyncBoundary(state, build, time, target.Baseline);
                        var oldById = target.Changes.ToDictionary(c => c.ChangeId, StringComparer.Ordinal);
                        foreach (HistoryChange overlap in listing.Changes.Where(c => oldById.ContainsKey(c.ChangeId)))
                            if (!SameChange(overlap, oldById[overlap.ChangeId])) throw new HistoryResolutionException("新旧数据库重叠段的原始记录不一致。");
                        var combined = new HistoryAnchorStore.Anchor { Identity = HistoryAnchorStore.Identity(run.Baseline), Provenance = "BrowserBaselineAndContinuousHistory", Baseline = CloneBaseline(run.Baseline), Changes = run.Prefix.Concat(target.Changes).ToList() };
                        await ValidateAnchorAsync(combined, token);
                        await Task.Run(() => SaveSyncCheckpoint(run), token);
                        Report(progress, "新增 " + run.Prefix.Count + " 条记录已接回完整旧归档，正在复核最新基线……");
                        HistoryPageSnapshot confirmed = await browser.NavigateAsync(BaselineUrl, token);
                        RequireSyncCapture(confirmed);
                        HistoryBaseline after = await Task.Run(() => parser.ParseBaseline(confirmed), token);
                        await Task.Run(() => SaveSyncEvidence(run, confirmed), token);
                        if (!SameBaseline(run.Baseline, after) || HistoryAnchorStore.Identity(run.Baseline) != HistoryAnchorStore.Identity(after))
                            throw new HistoryResolutionException("更新期间公开分支基线发生变化，本次未发布成功数据库；已保留可重新核验的增量断点。");
                        token.ThrowIfCancellationRequested();
                        string generation = Path.Combine(store.RootDirectory, "generations", Guid.NewGuid().ToString("N"));
                        await Task.Run(delegate {
                            token.ThrowIfCancellationRequested(); Directory.CreateDirectory(generation);
                            new HistoryCacheStore(generation).Save(combined.Baseline, combined.Changes);
                            new HistoryAnchorStore(generation).Save(new[] { combined });
                            string evidence = Path.Combine(generation, "history-evidence"); Directory.CreateDirectory(evidence);
                            foreach (string name in run.Evidence) File.Copy(Path.Combine(run.Directory, "evidence", name), Path.Combine(evidence, name));
                        }, token);
                        token.ThrowIfCancellationRequested();
                        SyncCatalogSnapshot published = await Task.Run(() => store.Publish(generation, run.FirstBaseline, confirmed, run.FirstBaseline.CapturedAtUtc, run.Prefix.Count, token), token);
                        return new HistorySyncResult {
                            AddedRecords = published.AddedRecords, TotalRecords = published.TotalRecords, LatestBuildId = published.LatestBuildId,
                            LatestChangeId = published.LatestChangeId, AnchorIdentity = published.AnchorIdentity, GenerationDirectory = published.GenerationDirectory,
                            LatestBuildPublishedAtUtc = published.LatestBuildPublishedAtUtc, LatestChangeObservedAtUtc = published.LatestChangeObservedAtUtc, VerifiedThroughUtc = published.VerifiedThroughUtc
                        };
                    }
                    if (UInt64.Parse(change.ChangeId, CultureInfo.InvariantCulture) < UInt64.Parse(target.Changes[0].ChangeId, CultureInfo.InvariantCulture))
                        throw new HistoryResolutionException("最新历史跳过了已保存首条边界；不会继续重抓整段旧历史。");
                    ApplyChange(change, state, null, null, null, null, null, "", null, ref build, ref time, ref events);
                    run.Prefix.Add(change); known.Add(change.ChangeId, change);
                }
                await Task.Run(() => SaveSyncCheckpoint(run), token);
                if (listing.EndReached || listing.Changes.Count == 0) throw new HistoryResolutionException("尚未连接旧归档就到达空页或历史末尾，未发布更新。");
                HistoryChange boundary = run.Prefix.LastOrDefault();
                if (boundary == null) throw new HistoryResolutionException("没有取得可接续的增量记录。");
                IHistoryDateBrowser dateBrowser = browser as IHistoryDateBrowser;
                bool noProgress = run.Prefix.Count == before;
                if (noProgress && dateBatch) throw new HistoryResolutionException("日期接续没有新增记录，已保留断点并停止重复请求。");
                requiredOverlap = boundary;
                if (dateBrowser != null && (noProgress || listing.Changes.First().ObservedAtUtc.UtcDateTime.Date > boundary.ObservedAtUtc.UtcDateTime.Date || String.IsNullOrEmpty(listing.LoadMoreSelector) && String.IsNullOrEmpty(listing.NextPageUrl)))
                {
                    dateBatch = true;
                    page = await dateBrowser.LoadHistoryBeforeAsync(boundary.ObservedAtUtc.UtcDateTime.Date.AddDays(1), token);
                }
                else if (!String.IsNullOrEmpty(listing.LoadMoreSelector)) { dateBatch = false; page = await browser.ClickAsync(listing.LoadMoreSelector, token); }
                else if (!String.IsNullOrEmpty(listing.NextPageUrl)) { dateBatch = false; page = await browser.NavigateAsync(listing.NextPageUrl, token); }
                else throw new HistoryResolutionException("历史页面没有可接续控件，已保留更新断点。");
                Report(progress, "已校验 " + run.Prefix.Count + " 条新增记录，继续接回旧归档首条 " + target.Changes[0].ChangeId + "……");
            }
        }

        private HistoryListing ParseSyncHistory(HistoryPageSnapshot page)
        {
            Uri uri;
            if (!Uri.TryCreate(page.Url, UriKind.Absolute, out uri) || uri.Scheme != "https" || uri.Host != "steamdb.info" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
                uri.AbsolutePath.TrimEnd('/') != "/app/570/history" || uri.Fragment.Length != 0 || uri.Query != "?filterkey=2")
                throw new HistoryResolutionException("增量更新必须读取 App 570 的最新 Depot 历史筛选页面。");
            HistoryListing listing = parser.ParseHistory(page);
            if (listing.Changes.GroupBy(c => c.ChangeId).Any(g => g.Count() != 1)) throw new HistoryResolutionException("同一更新批次包含重复记录。");
            if (listing.Changes.Any(c => !c.ParsedAllDepotChanges)) throw new HistoryResolutionException("最新记录含尚未识别的 Depot 变更，未把本批次发布为完整资料。");
            return listing;
        }
        private static void RequireSyncCapture(HistoryPageSnapshot page)
        {
            if (page == null || page.CapturedAtUtc.Year < 2000 || page.CapturedAtUtc > DateTimeOffset.UtcNow.AddMinutes(1))
                throw new HistoryResolutionException("网页没有有效的实际采集时间，不能确认资料已检查至何时。");
        }
        private static string SyncHash(string value)
        { using (var sha = SHA256.Create()) return String.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(value)).Select(b => b.ToString("x2", CultureInfo.InvariantCulture))); }
        private static JavaScriptSerializer SyncJson() { return new JavaScriptSerializer { MaxJsonLength = 128 * 1024 * 1024, RecursionLimit = 48 }; }
        private static void SaveSyncEvidence(LatestSyncRun run, HistoryPageSnapshot page)
        {
            RequireSyncCapture(page);
            Uri uri;
            if (!Uri.TryCreate(page.Url, UriKind.Absolute, out uri) || uri.Scheme != "https" || uri.Host != "steamdb.info" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
                !(uri.AbsolutePath.TrimEnd('/') == "/app/570/history" && uri.Query == "?filterkey=2" || uri.AbsolutePath.TrimEnd('/') == "/app/570/depots" && uri.Query == "?branch=public"))
                throw new HistoryResolutionException("不会把登录页面或其他来源保存为历史更新证据。");
            string json = SyncJson().Serialize(new SyncPageEvidence { Url = page.Url, Title = page.Title, Html = page.Html, Text = page.Text, CapturedAtUtc = page.CapturedAtUtc.ToUniversalTime().ToString("o") });
            string name = SyncHash(json) + ".json", evidence = Path.Combine(run.Directory, "evidence"); Directory.CreateDirectory(evidence);
            string path = Path.Combine(evidence, name); if (!File.Exists(path)) File.WriteAllText(path, json, new UTF8Encoding(false));
            if (!run.Evidence.Contains(name)) run.Evidence.Add(name);
        }
        private static void SaveSyncCheckpoint(LatestSyncRun run)
        {
            if (run.Baseline == null || run.Directory == null) return;
            // A restriction/cancellation before the old checkpoint has been
            // revalidated must not replace its longer evidence with an empty run.
            string dest = Path.Combine(run.Directory, "checkpoint.json");
            if (run.Prefix.Count == 0 && File.Exists(dest)) return;
            string payload = HistoryCacheStore.Serialize(run.Baseline, run.Prefix);
            var checkpoint = new SyncCheckpoint { SchemaVersion = 1, BaselineIdentity = HistoryAnchorStore.Identity(run.Baseline), Payload = payload, PayloadSha256 = SyncHash(payload), Evidence = run.Evidence.ToList() };
            Directory.CreateDirectory(run.Directory);
            string temp = Path.Combine(run.Directory, Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(temp, SyncJson().Serialize(checkpoint), new UTF8Encoding(false));
            if (File.Exists(dest)) File.Replace(temp, dest, null); else File.Move(temp, dest);
        }
        private List<HistoryChange> LoadSyncCheckpoint(LatestSyncRun run, HistoryListing first)
        {
            string path = Path.Combine(run.Directory, "checkpoint.json");
            if (!File.Exists(path)) return new List<HistoryChange>();
            if (new FileInfo(path).Length > 128 * 1024 * 1024) throw new HistoryResolutionException("更新断点超过大小限制。");
            SyncCheckpoint saved = SyncJson().Deserialize<SyncCheckpoint>(File.ReadAllText(path, Encoding.UTF8));
            if (saved == null || saved.SchemaVersion != 1 || saved.BaselineIdentity != HistoryAnchorStore.Identity(run.Baseline) || saved.PayloadSha256 != SyncHash(saved.Payload ?? "") || saved.Evidence == null)
                throw new HistoryResolutionException("更新断点的来源或内容校验失败，已保留文件但不会使用。");
            HistoryCacheStore.CacheData prefix = HistoryCacheStore.Parse(saved.Payload);
            if (!SameBaseline(run.Baseline, prefix.Baseline) || HistoryAnchorStore.Identity(prefix.Baseline) != saved.BaselineIdentity)
                throw new HistoryResolutionException("更新断点的完整基线不匹配。");
            var rawChanges = new Dictionary<string, HistoryChange>(StringComparer.Ordinal); bool hasBaseline = false;
            foreach (string name in saved.Evidence)
            {
                if (name == null || name.Length != 69 || name.Substring(64) != ".json" || !name.Take(64).All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f')) throw new HistoryResolutionException("更新断点证据路径无效。");
                string raw = File.ReadAllText(Path.Combine(run.Directory, "evidence", name), Encoding.UTF8);
                if (SyncHash(raw) != name.Substring(0, 64)) throw new HistoryResolutionException("更新断点原始页面校验失败。");
                SyncPageEvidence evidence = SyncJson().Deserialize<SyncPageEvidence>(raw);
                var snapshot = new HistoryPageSnapshot { Url = evidence.Url, Title = evidence.Title, Html = evidence.Html, Text = evidence.Text, CapturedAtUtc = DateTimeOffset.ParseExact(evidence.CapturedAtUtc, "o", CultureInfo.InvariantCulture) };
                RequireSyncCapture(snapshot);
                if (snapshot.Url == BaselineUrl) { HistoryBaseline b = parser.ParseBaseline(snapshot); if (HistoryAnchorStore.Identity(b) == saved.BaselineIdentity) hasBaseline = true; }
                else foreach (HistoryChange change in ParseSyncHistory(snapshot).Changes)
                {
                    HistoryChange old; if (rawChanges.TryGetValue(change.ChangeId, out old) && !SameChange(old, change)) throw new HistoryResolutionException("断点原始页面包含冲突记录。");
                    rawChanges[change.ChangeId] = change;
                }
            }
            if (!hasBaseline || prefix.Changes.Any(c => !rawChanges.ContainsKey(c.ChangeId) || !SameChange(c, rawChanges[c.ChangeId])))
                throw new HistoryResolutionException("更新断点缺少完整基线或原始差分页面证据。");
            if (prefix.Changes.Count == 0 || !SameChange(first.Changes[0], prefix.Changes[0])) return new List<HistoryChange>();
            foreach (HistoryChange seen in first.Changes)
            {
                HistoryChange old = prefix.Changes.FirstOrDefault(c => c.ChangeId == seen.ChangeId);
                if (old != null && !SameChange(old, seen)) throw new HistoryResolutionException("断点与本次最新页内容发生冲突。");
            }
            foreach (string name in saved.Evidence) if (!run.Evidence.Contains(name)) run.Evidence.Add(name);
            return prefix.Changes;
        }
    }
}
