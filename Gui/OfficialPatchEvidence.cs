using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using HtmlAgilityPack;

namespace DotaArchive
{
    public sealed class OfficialPatchMatch
    {
        public HistoryBuildEvent Build { get; set; }
        public List<string> Sources { get; set; }
        public string Assurance { get; set; }
        public string Kind { get; set; }
        public DateTimeOffset? NotAvailableAtUtc { get; set; }
        public DateTimeOffset? AvailableByUtc { get; set; }
        public List<OfficialReleaseStatement> WindowEvidence { get; set; }
        public OfficialPatchMatch() { Sources = new List<string>(); WindowEvidence = new List<OfficialReleaseStatement>(); Kind = "OfficialArticle"; }
    }

    public sealed class OfficialReleaseStatement
    {
        public string Patch { get; set; }
        public string State { get; set; }
        public DateTimeOffset AnnouncedAtUtc { get; set; }
        public string Statement { get; set; }
        public string SourceUrl { get; set; }
        public string ArticleUrl { get; set; }
        public string NewsId { get; set; }
        public string Title { get; set; }
    }

    // Official release news identifies the patch; SteamDB's ordinary patch page
    // identifies a candidate Build. Branch history must still establish publication.
    public sealed class OfficialPatchResolver
    {
        public const string PatchListUrl = "https://www.dota2.com/datafeed/patchnoteslist?language=english";
        public const string NewsUrl = "https://api.steampowered.com/ISteamNews/GetNewsForApp/v2/?appid=570&count=1000&maxlength=0&format=json&feeds=steam_community_announcements";
        public const string UpdatesUrl = "https://api.steampowered.com/ISteamNews/GetNewsForApp/v2/?appid=570&count=1000&maxlength=0&format=json&feeds=steam_updates";
        private const int MaximumOfficialBytes = 32 * 1024 * 1024;
        private readonly IHistoryBrowser browser;
        private readonly Func<string, CancellationToken, Task<string>> fetch;

        public OfficialPatchResolver(IHistoryBrowser browser) : this(browser, FetchOfficialAsync) { }
        public OfficialPatchResolver(IHistoryBrowser browser, Func<string, CancellationToken, Task<string>> officialFetch)
        {
            if(browser == null) throw new ArgumentNullException("browser");
            if(officialFetch == null) throw new ArgumentNullException("officialFetch");
            this.browser = browser; fetch = officialFetch;
        }

        public async Task<OfficialPatchMatch> ResolveAsync(string patch, IList<HistoryBuildEvent> builds, CancellationToken token, Action<string> progress)
        {
            patch = (patch ?? "").Trim().ToLowerInvariant();
            if(!Regex.IsMatch(patch, @"^[0-9]+\.[0-9]{2}[a-z]?$")) throw new HistoryResolutionException("官方更新关联需要精确补丁号，例如 7.00 或 7.22b。");
            if(builds == null || builds.Count == 0) throw new HistoryResolutionException("没有可用于关联的 SteamDB 公开构建列表。");
            var anchors = new List<PatchAnchor>();
            var statements = new List<OfficialReleaseStatement>();
            var failures = new List<string>();
            Report(progress, "正在查询 Valve 官方补丁列表和发布公告，核实版本关联……");
            try {
                string source = await fetch(PatchListUrl, token);
                anchors.AddRange(await Task.Run(delegate { return ParsePatchList(source, patch); }, token));
            } catch(OperationCanceledException) { throw; }
            catch(Exception ex) { failures.Add("官方补丁列表：" + ex.Message); }
            foreach(string feed in new[] { NewsUrl, UpdatesUrl }) {
                try { NewsData news = await LoadNewsAsync(patch, feed, token, progress); anchors.AddRange(news.Anchors); statements.AddRange(news.Statements); }
                catch(OperationCanceledException) { throw; }
                catch(Exception ex) { failures.Add((feed == NewsUrl ? "官方公告：" : "官方产品更新：") + ex.Message); }
            }
            token.ThrowIfCancellationRequested();
            anchors = Deduplicate(anchors);
            if(anchors.Count == 0) throw new HistoryResolutionException("没有取得版本 " + patch + " 的官方发布依据。" + String.Join("；", failures));

            OfficialPatchMatch window = MatchReleaseWindow(patch, builds, statements);
            if(window != null) {
                Report(progress, "官方公告明确限定了版本 " + patch + " 从尚未公开到已经公开的区间；其中只有一个网页候选 Build " + window.Build.BuildId + "，接下来必须核对整个区间的连续公开分支历史。");
                return window;
            }
            // Publication dates in the official feed can denote a whole local day.
            // They only bound the pages to inspect; they never become Build timestamps.
            var candidates = builds.Where(b => b != null && IsBuildPage(b) && anchors.Any(a => Math.Abs((b.PublishedAtUtc - a.AnnouncedAtUtc).TotalDays) <= 4))
                .GroupBy(b => b.BuildId, StringComparer.Ordinal).Select(g => g.OrderBy(b => b.PublishedAtUtc).First()).OrderBy(b => b.PublishedAtUtc).ToList();
            if(candidates.Count == 0) throw new HistoryResolutionException("已找到官方版本公告，但当前 SteamDB 构建列表没有公告附近的记录；无法只凭公告日期选择构建。");
            var matches = new List<OfficialPatchMatch>();
            for(int index = 0; index < candidates.Count; index++) {
                token.ThrowIfCancellationRequested();
                HistoryBuildEvent candidate = candidates[index];
                if(IsPreview(candidate.Title)) continue;
                Report(progress, "正在核对官方版本 " + patch + " 与 SteamDB Build " + candidate.BuildId + " 的正文关联（" + (index + 1) + "/" + candidates.Count + "）……");
                HistoryPageSnapshot snapshot = await browser.NavigateAsync(candidate.SourceUrl, token);
                List<PatchAnchor> matched = await Task.Run(delegate { return MatchPatchPage(snapshot, candidate, patch, anchors); }, token);
                List<PatchAnchor> sourceMatches = await Task.Run(delegate { return MatchOfficialSourceBar(snapshot, candidate, patch, anchors); }, token);
                OfficialPatchMatch explicitBuild = await TryOfficialEventBuildAsync(patch, anchors, new[] { candidate }, snapshot, token, progress);
                if(explicitBuild != null) { matches.Add(explicitBuild); continue; }
                if(sourceMatches.Count > 0) matched = sourceMatches;
                if(matched.Count == 0) continue;
                var match = new OfficialPatchMatch { Kind = sourceMatches.Count > 0 ? "OfficialPatchSource" : "OfficialArticle", Build = candidate, Assurance = sourceMatches.Count > 0 ? "Valve 官方补丁目录或公告提供精确补丁身份；SteamDB 当前 Build 的独立更新来源栏明确指向同一官方补丁页面，并与同页 Build 元数据和补丁标题交叉核对。公告日期只定位页面，实际公开发布时间仍须由分支历史核实。" : "依据 Valve 官方补丁号、公开发布公告与 SteamDB 构建正文中的官方链接建立候选关联；公告时间仅用于定位网页，实际首次公开发布时间仍须由分支历史核实。" };
                match.Sources.Add(candidate.SourceUrl);
                if(sourceMatches.Count > 0 && !String.IsNullOrEmpty(candidate.ChangeId)) match.Sources.Add("https://steamdb.info/app/570/history/?changeid=" + candidate.ChangeId);
                foreach(PatchAnchor anchor in matched) foreach(string source in anchor.Sources) if(!match.Sources.Contains(source)) match.Sources.Add(source);
                matches.Add(match);
            }
            if(matches.Count == 0) throw new HistoryResolutionException("已取得版本 " + patch + " 的官方发布依据，但附近构建正文没有可核实的官方链接关联。没有把公告日期当作发布构建。");
            if(matches.Count > 1) throw new HistoryResolutionException("官方版本依据同时关联到多个 SteamDB 构建（" + String.Join("、", matches.Select(m => m.Build.BuildId)) + "），还不能证明哪一个是首发，已停止自动选择。");
            return matches[0];
        }

        private async Task<OfficialPatchMatch> TryOfficialEventBuildAsync(string patch, IList<PatchAnchor> anchors, IList<HistoryBuildEvent> builds, HistoryPageSnapshot snapshot, CancellationToken token, Action<string> progress)
        {
            var result = new List<OfficialPatchMatch>();
            var document = new HtmlDocument(); document.LoadHtml(snapshot.Html);
            var articles = document.DocumentNode.Descendants("article").ToList();
            SourceBarInfo bar = builds.Count == 1 ? ReadSourceBar(snapshot, builds[0], patch) : null;
            var urls = new List<string>();
            if(articles.Count == 1) urls.AddRange(articles[0].Descendants("a").Select(a => CanonicalCommunityArticleUrl(HtmlEntity.DeEntitize(a.GetAttributeValue("href", "")))).Where(u => u != null));
            var sourceUrls = bar == null ? new List<string>() : bar.Node.Descendants("a").Select(a => CanonicalCommunityArticleUrl(HtmlEntity.DeEntitize(a.GetAttributeValue("href", "")))).Where(u => u != null).Distinct().ToList();
            urls.AddRange(sourceUrls); urls = urls.Distinct().ToList();
            foreach(string url in urls) {
                token.ThrowIfCancellationRequested();
                Report(progress, "正在核对来源栏的 Valve 官方公告身份及公开 Build 字段……");
                try {
                    string html = await fetch(url, token);
                    foreach(PatchAnchor anchor in anchors.Where(a => !String.IsNullOrEmpty(a.PlainText))) {
                        string sourceChangeId = bar != null && sourceUrls.Count == 1 && sourceUrls[0] == url && String.Equals(bar.Heading, NormalizeText(anchor.Title), StringComparison.OrdinalIgnoreCase) ? bar.ChangeId : null;
                        result.AddRange(await Task.Run(delegate { return ParseOfficialEventBuilds(html, url, patch, anchor, builds, sourceChangeId); }, token));
                    }
                } catch(OperationCanceledException) { throw; }
                catch(HistoryResolutionException) { throw; }
                catch(Exception ex) { Report(progress, "这条官方公告未能提供可读取的 Build 元数据：" + ex.Message); }
            }
            var unique = result.GroupBy(m => m.Build.BuildId, StringComparer.Ordinal).Select(g => g.First()).ToList();
            if(unique.Count > 1) throw new HistoryResolutionException("同一补丁的官方公告填写了不同 Build，无法证明首发构建。");
            return unique.SingleOrDefault();
        }

        internal static List<OfficialPatchMatch> ParseOfficialEventBuilds(string html, string url, string patch, PatchAnchor anchor, IList<HistoryBuildEvent> builds, string verifiedSourceChangeId = null)
        {
            if(html == null || html.Length > MaximumOfficialBytes) throw new HistoryResolutionException("官方公告页面为空或过大。");
            var document = new HtmlDocument(); document.LoadHtml(html);
            var result = new List<OfficialPatchMatch>();
            foreach(HtmlNode node in document.DocumentNode.Descendants().Where(n => n.Attributes["data-partnereventstore"] != null)) {
                string json = HtmlEntity.DeEntitize(node.GetAttributeValue("data-partnereventstore", ""));
                object[] events = new JavaScriptSerializer { MaxJsonLength = MaximumOfficialBytes, RecursionLimit = 64 }.DeserializeObject(json) as object[];
                if(events == null) throw new HistoryResolutionException("官方公告事件元数据格式无效。");
                foreach(object raw in events) {
                    var value = raw as Dictionary<string, object>; if(value == null) continue;
                    string title = NormalizeText(StringValue(value, "event_name"));
                    if(!ContainsPatch(title, patch) || title != anchor.Title || IsPreview(title) || IsRetrospective(title) || IsFollowup(title)) continue;
                    if(IntegerValue(value, "appid") != 570) continue;
                    long number = IntegerValue(value, "build_id");
                    string branch = StringValue(value, "build_branch");
                    if(IntegerValue(value, "published") != 1 || IntegerValue(value, "hidden") != 0) continue;
                    var body = ObjectValue(value, "announcement_body");
                    if(NormalizeText(StringValue(body, "headline")) != anchor.Title || !ContainsPatch(StringValue(body, "headline"), patch) || IntegerValue(body, "posttime") != (long)(anchor.AnnouncedAtUtc - new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero)).TotalSeconds) continue;
                    if(number <= 0) {
                        if(number != 0 || (branch != "" && branch != "public") || String.IsNullOrEmpty(verifiedSourceChangeId) || !Regex.IsMatch(verifiedSourceChangeId, @"^[1-9][0-9]*$") || builds.Count != 1 || !IsBuildPage(builds[0]) || Math.Abs((builds[0].PublishedAtUtc - anchor.AnnouncedAtUtc).TotalDays) > 4) continue;
                        SetSourceChangeId(builds[0], verifiedSourceChangeId);
                        result.Add(new OfficialPatchMatch { Kind = "OfficialPatchSource", Build = builds[0], Sources = anchor.Sources.Concat(new[] { url, builds[0].SourceUrl, "https://steamdb.info/app/570/history/?changeid=" + verifiedSourceChangeId }).Distinct().ToList(), Assurance = "当前 Build 的独立来源栏指向 Valve 官方发布公告；公告 App570、标题和原始发布时间与官方新闻 API 相互核对，并保留该 Build 页面明确指向的历史 ChangeId。公告未填写 BuildId，该字段没有被猜测；实际 public 发布时间继续由分支历史核实。" });
                        continue;
                    }
                    if(branch != "public") continue;
                    string id = number.ToString(CultureInfo.InvariantCulture);
                    HistoryBuildEvent build = builds.Where(b => b.BuildId == id && IsBuildPage(b) && Math.Abs((b.PublishedAtUtc - anchor.AnnouncedAtUtc).TotalDays) <= 4).OrderBy(b => b.PublishedAtUtc).FirstOrDefault();
                    if(build == null) throw new HistoryResolutionException("官方公告关联的 Build 不在当前 SteamDB 公开构建列表中。");
                    if(!String.IsNullOrEmpty(verifiedSourceChangeId)) SetSourceChangeId(build, verifiedSourceChangeId);
                    result.Add(new OfficialPatchMatch { Kind = "OfficialEventBuild", Build = build, Sources = anchor.Sources.Concat(new[] { url, build.SourceUrl }).Distinct().ToList(), Assurance = "同一补丁的官方公开公告事件明确填写了 App 570、public 分支与 BuildId，并与 SteamDB 构建列表交叉核对；精确发布时间仍由公开分支历史核实。" });
                }
            }
            return result;
        }

        private sealed class NewsData
        {
            internal readonly List<PatchAnchor> Anchors = new List<PatchAnchor>();
            internal readonly List<OfficialReleaseStatement> Statements = new List<OfficialReleaseStatement>();
        }
        private async Task<NewsData> LoadNewsAsync(string patch, string feedUrl, CancellationToken token, Action<string> progress)
        {
            var result = new NewsData();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            long? endDate = null;
            for(int page = 0; page < 64; page++) {
                token.ThrowIfCancellationRequested();
                string url = feedUrl + (endDate.HasValue ? "&enddate=" + endDate.Value.ToString(CultureInfo.InvariantCulture) : "");
                string source = await fetch(url, token);
                NewsPage parsed = await Task.Run(delegate { return ParseNews(source, patch, url); }, token);
                result.Anchors.AddRange(parsed.Matches); result.Statements.AddRange(parsed.Statements);
                if(parsed.ItemCount < 1000) return result;
                if(parsed.OldestTimestamp <= 0 || (endDate.HasValue && parsed.OldestTimestamp >= endDate.Value)) throw new HistoryResolutionException("官方公告分页没有向更早日期推进，无法确认完整公告范围。");
                string signature = parsed.OldestTimestamp.ToString(CultureInfo.InvariantCulture) + ":" + parsed.LastId;
                if(!seen.Add(signature)) throw new HistoryResolutionException("官方公告接口重复返回同一页，已停止继续请求。");
                endDate = parsed.OldestTimestamp;
                Report(progress, "已读取 " + ((page + 1) * 1000) + " 条 Valve 官方公告，正在检查更早发布记录……");
            }
            throw new HistoryResolutionException("官方公告超出本次查询的分页范围；没有将不完整结果当作首发证据。");
        }

        internal sealed class PatchAnchor
        {
            internal DateTimeOffset AnnouncedAtUtc;
            internal string Title, PlainText;
            internal readonly HashSet<string> PatchUrls = new HashSet<string>(StringComparer.Ordinal);
            internal readonly HashSet<string> NewsIds = new HashSet<string>(StringComparer.Ordinal);
            internal readonly HashSet<string> NewsUrls = new HashSet<string>(StringComparer.Ordinal);
            internal readonly List<string> Sources = new List<string>();
        }
        internal sealed class NewsPage
        {
            internal int ItemCount;
            internal long OldestTimestamp;
            internal string LastId;
            internal readonly List<PatchAnchor> Matches = new List<PatchAnchor>();
            internal readonly List<OfficialReleaseStatement> Statements = new List<OfficialReleaseStatement>();
        }

        internal static List<PatchAnchor> ParsePatchList(string source, string patch)
        {
            Dictionary<string, object> root = JsonObject(source);
            object success;
            if(root.TryGetValue("success", out success) && (!(success is bool) || !(bool)success)) throw new HistoryResolutionException("Valve 补丁接口未返回成功结果。");
            var matches = new List<PatchAnchor>();
            foreach(object raw in JsonArray(root, "patches")) {
                var item = raw as Dictionary<string, object>;
                if(item == null || !String.Equals(StringValue(item, "patch_number"), patch, StringComparison.OrdinalIgnoreCase)) continue;
                long timestamp = IntegerValue(item, "patch_timestamp");
                var anchor = new PatchAnchor { AnnouncedAtUtc = UnixTime(timestamp), Title = StringValue(item, "patch_name") ?? patch, PlainText = "" };
                anchor.PatchUrls.Add("dota2.com/patches/" + patch);
                string website = StringValue(item, "patch_website");
                if(!String.IsNullOrEmpty(website)) {
                    if(!Regex.IsMatch(website, @"^[A-Za-z0-9_-]+$")) throw new HistoryResolutionException("官方补丁页面路径不符合公开接口格式。");
                    anchor.PatchUrls.Add("dota2.com/" + website.ToLowerInvariant());
                }
                anchor.Sources.Add(PatchListUrl); matches.Add(anchor);
            }
            if(matches.Count > 1 && matches.Select(a => a.AnnouncedAtUtc).Distinct().Count() > 1) throw new HistoryResolutionException("官方补丁接口包含互相矛盾的同名版本日期。");
            return matches;
        }
        internal static NewsPage ParseNews(string source, string patch, string sourceUrl)
        {
            Dictionary<string, object> root = JsonObject(source), app = ObjectValue(root, "appnews");
            if(IntegerValue(app, "appid") != 570) throw new HistoryResolutionException("官方公告响应不属于 Dota 2 App 570。");
            var page = new NewsPage { OldestTimestamp = Int64.MaxValue };
            foreach(object raw in JsonArray(app, "newsitems")) {
                var item = raw as Dictionary<string, object>;
                if(item == null) throw new HistoryResolutionException("官方公告格式无效。");
                string id = StringValue(item, "gid"), title = NormalizeText(StringValue(item, "title")), contents = StringValue(item, "contents") ?? "";
                long timestamp = IntegerValue(item, "date");
                if(id == null || !Regex.IsMatch(id, @"^[0-9]+$") || timestamp <= 0) throw new HistoryResolutionException("官方公告编号或时间无效。");
                page.ItemCount++; if(timestamp < page.OldestTimestamp) { page.OldestTimestamp = timestamp; page.LastId = id; }
                string feedName = StringValue(item, "feedname");
                if((feedName != "steam_community_announcements" && feedName != "steam_updates") || !ContainsPatch(title, patch) || IsRetrospective(title) || IsFollowup(title)) continue;
                if(IntegerValue(item, "appid") != 570) throw new HistoryResolutionException("官方新闻条目的 AppId 不是 Dota 2。");
                string articleUrl = feedName == "steam_community_announcements" ? CanonicalApiAnnouncementUrl(StringValue(item, "url")) : CanonicalLegacyUpdateUrl(StringValue(item, "url"));
                if(articleUrl == null) throw new HistoryResolutionException("官方产品更新未提供有效的 Steam 新闻链接。");
                string plain = NormalizeText(contents);
                var releaseStatements = ExtractReleaseStatements(patch, title, plain, UnixTime(timestamp), id, sourceUrl);
                foreach(OfficialReleaseStatement statement in releaseStatements) statement.ArticleUrl = articleUrl;
                page.Statements.AddRange(releaseStatements);
                if(IsPreview(title)) continue;
                // A future announcement is not evidence that the patch shipped.
                if(Regex.IsMatch(plain, @"\b(will (?:go live|be (?:available|released))|coming (?:soon|tomorrow)|countdown to launch|on the (?:dota 2 )?test client)\b", RegexOptions.IgnoreCase)
                    && !Regex.IsMatch(plain, @"\b(now available|released today|now live|is now live|today(?:'s)? (?:update|release))\b", RegexOptions.IgnoreCase)) continue;
                var anchor = new PatchAnchor { AnnouncedAtUtc = UnixTime(timestamp), Title = title, PlainText = plain };
                if(feedName == "steam_community_announcements") {
                    string directArticleId = OfficialNewsId(articleUrl); if(directArticleId != null) anchor.NewsIds.Add(directArticleId);
                } else {
                    anchor.NewsUrls.Add(articleUrl);
                }
                foreach(Match match in Regex.Matches(contents, @"https?://[^\s\]<>""']+", RegexOptions.IgnoreCase)) {
                    string canonical = CanonicalDotaUrl(match.Value); if(canonical != null) anchor.PatchUrls.Add(canonical);
                }
                // The API source is authoritative; the CDN wrapper is not used for navigation.
                anchor.Sources.Add(sourceUrl); anchor.Sources.Add(articleUrl);
                page.Matches.Add(anchor);
            }
            if(page.ItemCount == 0) page.OldestTimestamp = 0;
            return page;
        }
        internal static List<OfficialReleaseStatement> ExtractReleaseStatements(string patch, string title, string plain, DateTimeOffset timestamp, string newsId, string sourceUrl)
        {
            var result = new List<OfficialReleaseStatement>();
            if(!ContainsPatch(title, patch) || IsRetrospective(title) || IsFollowup(title)) return result;
            plain = Regex.Replace(plain ?? "", "[\"“].*?[\"”]", " [quoted material] ", RegexOptions.Singleline);
            string subject = @"(?<![0-9A-Za-z.])(?:the\s+)?(?:patch\s+)?" + Regex.Escape(patch) + @"(?![0-9A-Za-z.])(?:\s+(?:gameplay\s+)?update)?";
            string futurePattern = subject + @"\s+(?:will|is\s+scheduled\s+to)\s+(?:go\s+live|launch|be\s+released|be\s+made\s+available)\s+(?:to\s+(?:the\s+world|everyone|all\s+players)|worldwide|globally|on\s+(?:the\s+)?(?:public|live)\s+(?:servers?|client))\b";
            string releasedSubject = subject;
            // A named update can be the grammatical subject, but its name must be
            // explicitly tied to this patch by the same announcement's title.
            Match named = Regex.Match(title, @"^\s*(?:patch\s+)?" + Regex.Escape(patch) + @"\s+(?:gameplay\s+)?update\s*[-–—:]\s*(?<name>[A-Za-z][A-Za-z '\u2019-]*?)(?:\s+(?:begins|has\s+arrived|is\s+here|is\s+now\s+available))?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if(named.Success) {
                string name = named.Groups["name"].Value.Trim();
                if(name.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length >= 2 && !RestrictedScope(name)) {
                    if(!Regex.IsMatch(name, @"\bupdate$", RegexOptions.IgnoreCase)) name += " Update";
                    releasedSubject = "(?:" + subject + @"|(?<![0-9A-Za-z])" + Regex.Escape(name) + ")";
                }
            }
            string releasedPattern = releasedSubject + @"\s*(?:,\s*|\s+(?:is\s+)?)(?:now\s+available\s+to\s+(?:everyone|all\s+players)|now\s+live\s+(?:for\s+(?:everyone|all\s+players)|on\s+(?:the\s+)?(?:public|live)\s+(?:servers?|client)))\b";
            foreach(string sentence in Regex.Split(plain, @"(?<=[.!?])\s+")) {
                if(UnreliableStatementContext(sentence)) continue;
                foreach(Match match in Regex.Matches(sentence, futurePattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) {
                    string clause = SurroundingClause(sentence, match.Index, match.Length);
                    if(RestrictedScope(clause)) continue;
                    result.Add(new OfficialReleaseStatement { Patch = patch, State = "NotYetPublic", AnnouncedAtUtc = timestamp, Statement = sentence, SourceUrl = sourceUrl, NewsId = newsId, Title = title });
                }
                if(IsPreview(title) || RestrictedScope(sentence) || Regex.IsMatch(sentence, @"\b(will|tomorrow|scheduled)\b", RegexOptions.IgnoreCase)) continue;
                if(!Regex.IsMatch(sentence, releasedPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) continue;
                result.Add(new OfficialReleaseStatement { Patch = patch, State = "AvailablePublic", AnnouncedAtUtc = timestamp, Statement = sentence, SourceUrl = sourceUrl, NewsId = newsId, Title = title });
            }
            return result;
        }
        private static string SurroundingClause(string text, int index, int length)
        {
            int start = index, end = index + length;
            while(start > 0 && ",;!?\n".IndexOf(text[start - 1]) < 0 && !(text[start - 1] == '.' && start < text.Length && Char.IsWhiteSpace(text[start]))) start--;
            while(end < text.Length && ",;!?\n".IndexOf(text[end]) < 0 && !(text[end] == '.' && (end + 1 == text.Length || Char.IsWhiteSpace(text[end + 1])))) end++;
            return text.Substring(start, end - start).Trim();
        }
        private static bool RestrictedScope(string text) { return Regex.IsMatch(text ?? "", @"\b(test\s*(?:client|server|build|realm)|dota\s*2\s*test|beta|ptr|preview|early\s+access|selected\s+players|invite[- ]only)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant); }
        private static bool UnreliableStatementContext(string text)
        {
            return Regex.IsMatch(text ?? "", @"\b(not|never|no\s+longer|isn't|wasn't|won't|cannot|can't|if|unless|whether|would|could|might|suppose|imagine|hypothetical|expected|believed|planned|announced|said|reported|wrote|claimed|quoted|promised|discussed|remember|recall|previously|last\s+(?:year|month|week)|yesterday|years?\s+ago|used\s+to|was\s+once|at\s+the\s+time|in\s+(?:19|20)[0-9]{2})\b|[""“”]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) || IsRetrospective(text);
        }
        private static bool IsRetrospective(string title) { return Regex.IsMatch(title ?? "", @"\b(anniversary|retrospective|looking\s+back|throwback|flashback|revisited|recap|roundup|round-up|repost)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant); }
        private static bool IsFollowup(string title) { return Regex.IsMatch(title ?? "", @"\b(hotfix|bug[- ]?fix(?:es)?|fixup|maintenance|follow[- ]?up|correction|stability|small\s+update|minor\s+update)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant); }
        internal static OfficialPatchMatch MatchReleaseWindow(string patch, IList<HistoryBuildEvent> builds, IList<OfficialReleaseStatement> statements)
        {
            var available = statements.Where(s => s.Patch == patch && s.State == "AvailablePublic").OrderBy(s => s.AnnouncedAtUtc).FirstOrDefault();
            if(available == null) return null;
            var before = statements.Where(s => s.Patch == patch && s.State == "NotYetPublic" && s.AnnouncedAtUtc < available.AnnouncedAtUtc).OrderByDescending(s => s.AnnouncedAtUtc).FirstOrDefault();
            if(before == null) return null;
            var candidates = builds.Where(b => b != null && IsBuildPage(b) && b.PublishedAtUtc > before.AnnouncedAtUtc && b.PublishedAtUtc <= available.AnnouncedAtUtc)
                .GroupBy(b => b.BuildId + ":" + b.PublishedAtUtc.ToUniversalTime().ToString("o"), StringComparer.Ordinal).Select(g => g.First()).ToList();
            if(candidates.Count != 1) return null;
            return new OfficialPatchMatch {
                Kind = "OfficialReleaseWindow", Build = candidates[0], NotAvailableAtUtc = before.AnnouncedAtUtc, AvailableByUtc = available.AnnouncedAtUtc,
                Sources = new List<string> { before.SourceUrl, before.ArticleUrl, available.SourceUrl, available.ArticleUrl, candidates[0].SourceUrl }.Where(s => !String.IsNullOrEmpty(s)).Distinct().ToList(),
                WindowEvidence = new List<OfficialReleaseStatement> { before, available },
                Assurance = "Valve 对同一补丁的官方公告分别明确尚未公开和已经公开；网页构建列表在该区间只显示一个候选。必须继续核对区间内完整公开分支历史只有一次发布（含回滚），此处不把公告时间当作构建发布时间。"
            };
        }
        private static List<PatchAnchor> Deduplicate(IEnumerable<PatchAnchor> anchors)
        {
            var result = new List<PatchAnchor>();
            foreach(PatchAnchor anchor in anchors) {
                PatchAnchor existing = result.FirstOrDefault(a => a.AnnouncedAtUtc == anchor.AnnouncedAtUtc && a.Title == anchor.Title && a.PlainText == anchor.PlainText);
                if(existing == null) { result.Add(anchor); continue; }
                existing.PatchUrls.UnionWith(anchor.PatchUrls); existing.NewsIds.UnionWith(anchor.NewsIds); existing.NewsUrls.UnionWith(anchor.NewsUrls);
                foreach(string source in anchor.Sources) if(!existing.Sources.Contains(source)) existing.Sources.Add(source);
            }
            return result;
        }
        internal static List<PatchAnchor> MatchPatchPage(HistoryPageSnapshot snapshot, HistoryBuildEvent build, string patch, IList<PatchAnchor> anchors)
        {
            if(snapshot == null || snapshot.Url == null || snapshot.Html == null) throw new HistoryResolutionException("SteamDB 构建正文未加载。");
            Uri page;
            if(!Uri.TryCreate(snapshot.Url, UriKind.Absolute, out page) || page.Scheme != "https" || page.Host != "steamdb.info" || page.AbsolutePath.TrimEnd('/') != "/patchnotes/" + build.BuildId)
                throw new HistoryResolutionException("浏览器返回的页面不属于待关联的 SteamDB 构建。");
            if(snapshot.Html.Length > 64 * 1024 * 1024) throw new HistoryResolutionException("SteamDB 构建正文超出读取范围。");
            var doc = new HtmlDocument(); doc.LoadHtml(snapshot.Html);
            List<HtmlNode> articles = doc.DocumentNode.Descendants("article").ToList();
            if(articles.Count != 1) return new List<PatchAnchor>();
            HtmlNode article = articles[0];
            // Do not use sidebar suggestions, navigation or unrelated update lists as evidence.
            foreach(HtmlNode unwanted in article.Descendants().Where(n => n.Name == "script" || n.Name == "style" || n.Name == "nav" || n.Name == "aside" || n.Name == "footer" || n.Name == "blockquote").ToList()) unwanted.Remove();
            string text = NormalizeText(article.InnerText);
            HtmlNode headingNode = article.Descendants().FirstOrDefault(n => n.Name == "h1" || n.Name == "h2");
            string heading = headingNode == null ? "" : Regex.Replace(NormalizeText(headingNode.InnerText), @"\s+\(ClientVersion\s+[1-9][0-9]*\)\s*$", "", RegexOptions.CultureInvariant);
            var urls = new HashSet<string>(StringComparer.Ordinal);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var articlesByUrl = new HashSet<string>(StringComparer.Ordinal);
            foreach(HtmlNode link in article.Descendants("a")) {
                string href = HtmlEntity.DeEntitize(link.GetAttributeValue("href", ""));
                string canonical = CanonicalDotaUrl(href); if(canonical != null) urls.Add(canonical);
                string id = OfficialNewsId(href); if(id != null) ids.Add(id);
                string articleUrl = CanonicalLegacyUpdateUrl(href); if(articleUrl != null) articlesByUrl.Add(articleUrl);
            }
            var matched = new List<PatchAnchor>();
            foreach(PatchAnchor anchor in anchors) {
                if(Math.Abs((build.PublishedAtUtc - anchor.AnnouncedAtUtc).TotalDays) > 4) continue;
                bool articleIdentity = ids.Overlaps(anchor.NewsIds) || articlesByUrl.Overlaps(anchor.NewsUrls);
                bool patchWebsite = urls.Overlaps(anchor.PatchUrls);
                bool exactPatch = ContainsPatch(text, patch);
                // A later hotfix can cite the release article or website. Require
                // this article's own heading to identify the release as well.
                bool officialHeading = String.Equals(heading, NormalizeText(anchor.Title), StringComparison.OrdinalIgnoreCase);
                bool patchHeading = Regex.IsMatch(heading, @"^\s*(?:Dota\s*2\s*[-:]?\s*)?(?:(?:patch|update)\s+)?" + Regex.Escape(patch) + @"(?:\s+(?:gameplay\s+)?update)?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if((officialHeading || patchHeading) && !IsPreview(heading) && !IsRetrospective(heading) && !IsFollowup(heading) && !IsFollowup(anchor.Title) && (articleIdentity || (patchWebsite && exactPatch))) matched.Add(anchor);
            }
            return matched;
        }
        internal static List<PatchAnchor> MatchOfficialSourceBar(HistoryPageSnapshot snapshot, HistoryBuildEvent build, string patch, IList<PatchAnchor> anchors)
        {
            var result = new List<PatchAnchor>();
            SourceBarInfo bar = ReadSourceBar(snapshot, build, patch);
            if(bar == null) return result;
            var officialUrls = bar.Node.Descendants("a").Select(a => CanonicalDotaUrl(HtmlEntity.DeEntitize(a.GetAttributeValue("href", "")))).Where(u => u != null).Distinct().ToList();
            if(officialUrls.Count != 1) return result;
            foreach(PatchAnchor anchor in anchors) {
                if(Math.Abs((build.PublishedAtUtc - anchor.AnnouncedAtUtc).TotalDays) > 4 || IsPreview(anchor.Title) || IsRetrospective(anchor.Title) || IsFollowup(anchor.Title) || !anchor.PatchUrls.Contains(officialUrls[0])) continue;
                bool exactPatchPage = officialUrls[0] == "dota2.com/patches/" + patch;
                bool officialTitle = String.Equals(bar.Heading, NormalizeText(anchor.Title), StringComparison.OrdinalIgnoreCase);
                if(exactPatchPage || officialTitle) result.Add(anchor);
            }
            if(result.Count > 0) SetSourceChangeId(build, bar.ChangeId);
            return result;
        }
        private sealed class SourceBarInfo { internal HtmlNode Node; internal string Heading, ChangeId; }
        private static SourceBarInfo ReadSourceBar(HistoryPageSnapshot snapshot, HistoryBuildEvent build, string patch)
        {
            if(snapshot == null || snapshot.Html == null || snapshot.Html.Length > 64 * 1024 * 1024 || !IsBuildPage(build)) return null;
            Uri current;
            if(!Uri.TryCreate(snapshot.Url, UriKind.Absolute, out current) || current.Scheme != "https" || current.Host != "steamdb.info" || current.AbsolutePath.TrimEnd('/') != "/patchnotes/" + build.BuildId) return null;
            var doc = new HtmlDocument(); doc.LoadHtml(snapshot.Html);
            // Observed SteamDB layout: this explicit source marker immediately
            // follows metadata for the same Build. An arbitrary body link is not it.
            var sources = doc.DocumentNode.Descendants("div").Where(n => HasClass(n, "patchnotes-official-url") && !n.Ancestors().Any(a => a.Name == "aside" || a.Name == "nav" || a.Name == "footer" || a.Name == "blockquote")).ToList();
            if(sources.Count != 1) return null;
            HtmlNode source = sources[0], metadata = source.PreviousSibling;
            while(metadata != null && metadata.NodeType != HtmlNodeType.Element) metadata = metadata.PreviousSibling;
            if(metadata == null || metadata.Name != "div" || !HasClass(metadata, "patchnotes-metadata") || metadata.ParentNode != source.ParentNode) return null;
            var buildLinks = metadata.Descendants("a").Where(a => Regex.IsMatch(NormalizeText(a.InnerText), @"^Build\s+" + Regex.Escape(build.BuildId) + "$", RegexOptions.CultureInvariant)).ToList();
            if(buildLinks.Count != 1) return null;
            Uri history;
            if(!Uri.TryCreate(new Uri("https://steamdb.info"), HtmlEntity.DeEntitize(buildLinks[0].GetAttributeValue("href", "")), out history) || history.Scheme != "https" || history.Host != "steamdb.info" || history.UserInfo.Length > 0 || !history.IsDefaultPort || history.AbsolutePath != "/app/570/history/" || !Regex.IsMatch(history.Query, @"^\?changeid=[1-9][0-9]*$")) return null;
            HtmlNode header = source.Ancestors("div").FirstOrDefault(n => HasClass(n, "patchnotes-header"));
            if(header == null) return null;
            var headings = header.Descendants("h1").Where(h => HasClass(h, "header-title") && h.GetAttributeValue("itemprop", "") == "headline" && !h.Ancestors().Any(a => a.Name == "aside" || a.Name == "nav" || a.Name == "footer" || a.Name == "blockquote")).Select(h => NormalizeText(h.InnerText)).ToList();
            if(headings.Count != 1) return null;
            string heading = Regex.Replace(headings[0], @"\s+\(ClientVersion\s+[1-9][0-9]*\)\s*$", "", RegexOptions.CultureInvariant);
            if(IsPreview(heading) || IsRetrospective(heading) || IsFollowup(heading) || !ContainsPatch(heading, patch) || !NormalizeText(source.InnerText).StartsWith("Update notes via ", StringComparison.Ordinal)) return null;
            return new SourceBarInfo { Node = source, Heading = heading, ChangeId = history.Query.Substring("?changeid=".Length) };
        }
        private static void SetSourceChangeId(HistoryBuildEvent build, string changeId) { if(!String.IsNullOrEmpty(build.ChangeId) && build.ChangeId != changeId) throw new HistoryResolutionException("同一 Build 的来源页历史编号与已读取记录矛盾，已停止版本关联。"); build.ChangeId = changeId; }
        private static bool HasClass(HtmlNode node, string name) { return node.GetAttributeValue("class", "").Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Contains(name); }
        private static bool IsBuildPage(HistoryBuildEvent build)
        {
            Uri uri;
            return Regex.IsMatch(build.BuildId ?? "", @"^[1-9][0-9]*$") && Uri.TryCreate(build.SourceUrl, UriKind.Absolute, out uri) && uri.Scheme == "https" && uri.Host == "steamdb.info" && uri.AbsolutePath.TrimEnd('/') == "/patchnotes/" + build.BuildId;
        }
        internal static string CanonicalDotaUrl(string value)
        {
            Uri uri;
            if(!Uri.TryCreate(HtmlEntity.DeEntitize(value ?? ""), UriKind.Absolute, out uri) || (uri.Scheme != "https" && uri.Scheme != "http") || (uri.Host != "dota2.com" && uri.Host != "www.dota2.com") || uri.UserInfo.Length != 0 || !uri.IsDefaultPort) return null;
            string path = Uri.UnescapeDataString(uri.AbsolutePath).TrimEnd('/').ToLowerInvariant();
            if(path.Length == 0 || Regex.IsMatch(path, @"/(?:login|logout|openid|account)(?:/|$)", RegexOptions.IgnoreCase)) return null;
            return "dota2.com" + path;
        }
        internal static string OfficialNewsId(string value)
        {
            Uri uri;
            if(!Uri.TryCreate(value ?? "", UriKind.Absolute, out uri) || (uri.Scheme != "https" && uri.Scheme != "http") || uri.UserInfo.Length != 0 || !uri.IsDefaultPort) return null;
            string pattern = uri.Host == "steamcommunity.com" ? @"^/(?:games/(?:dota2|570)|ogg/570)/announcements/detail/([0-9]+)/?$" : uri.Host == "store.steampowered.com" ? @"^/news/app/570/view/([0-9]+)/?$" : null;
            if(pattern == null) return null;
            Match match = Regex.Match(uri.AbsolutePath, pattern, RegexOptions.IgnoreCase); return match.Success ? match.Groups[1].Value : null;
        }
        private static string CanonicalCommunityArticleUrl(string value)
        {
            if(OfficialNewsId(value) == null) return null;
            var uri = new Uri(value); return "https://" + uri.Host + uri.AbsolutePath.TrimEnd('/');
        }
        private static string CanonicalApiAnnouncementUrl(string value)
        {
            string direct = CanonicalCommunityArticleUrl(value); if(direct != null) return direct;
            Uri uri;
            if(!Uri.TryCreate(value ?? "", UriKind.Absolute, out uri) || (uri.Scheme != "https" && uri.Scheme != "http") || uri.Host != "steamstore-a.akamaihd.net" || uri.UserInfo.Length != 0 || !uri.IsDefaultPort || !Regex.IsMatch(uri.AbsolutePath, @"^/news/externalpost/steam_community_announcements/[0-9]+/?$")) return null;
            return "https://" + uri.Host + uri.AbsolutePath.TrimEnd('/');
        }
        internal static string CanonicalLegacyUpdateUrl(string value)
        {
            Uri uri;
            if(!Uri.TryCreate(value ?? "", UriKind.Absolute, out uri) || (uri.Scheme != "https" && uri.Scheme != "http") || uri.Host != "store.steampowered.com" || uri.UserInfo.Length != 0 || !uri.IsDefaultPort || !Regex.IsMatch(uri.AbsolutePath, @"^/news/[0-9]+/?$")) return null;
            return "https://store.steampowered.com" + uri.AbsolutePath.TrimEnd('/');
        }
        internal static bool ContainsPatch(string value, string patch) { return Regex.IsMatch(value ?? "", @"(?<![0-9A-Za-z.])" + Regex.Escape(patch) + @"(?![0-9A-Za-z.])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant); }
        internal static bool IsPreview(string value) { return Regex.IsMatch(value ?? "", @"\b(countdown|preview|upcoming|test\s+(?:client|server|build|realm)|dota\s*2\s*test|beta|ptr|coming\s+soon|sneak\s+peek)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant); }
        private static string NormalizeText(string value)
        {
            string unquoted = Regex.Replace(value ?? "", @"\[quote(?:=[^\]]*)?\].*?\[/quote\]", " [quoted material] ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            return Regex.Replace(HtmlEntity.DeEntitize(Regex.Replace(unquoted, @"\[(?:/?[A-Za-z]+)(?:=[^\]]*)?\]", " ")).Replace('\u00a0', ' '), @"\s+", " ").Trim();
        }
        private static Dictionary<string, object> JsonObject(string source)
        {
            if(String.IsNullOrWhiteSpace(source) || source.Length > MaximumOfficialBytes) throw new HistoryResolutionException("官方接口响应为空或过大。");
            var root = new JavaScriptSerializer { MaxJsonLength = MaximumOfficialBytes, RecursionLimit = 64 }.DeserializeObject(source) as Dictionary<string, object>;
            if(root == null) throw new HistoryResolutionException("官方接口没有返回 JSON 对象。"); return root;
        }
        private static Dictionary<string, object> ObjectValue(Dictionary<string, object> obj, string key) { object value; if(!obj.TryGetValue(key, out value) || !(value is Dictionary<string, object>)) throw new HistoryResolutionException("官方接口缺少字段：" + key); return (Dictionary<string, object>)value; }
        private static IEnumerable JsonArray(Dictionary<string, object> obj, string key) { object value; if(!obj.TryGetValue(key, out value) || !(value is object[])) throw new HistoryResolutionException("官方接口数组格式无效：" + key); return (object[])value; }
        private static string StringValue(Dictionary<string, object> obj, string key) { object value; if(!obj.TryGetValue(key, out value) || value == null) return null; if(!(value is string)) throw new HistoryResolutionException("官方接口字符串格式无效：" + key); return (string)value; }
        private static long IntegerValue(Dictionary<string, object> obj, string key)
        {
            object value;
            if(!obj.TryGetValue(key, out value) || !(value is int || value is long)) throw new HistoryResolutionException("官方接口整数格式无效：" + key);
            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        private static DateTimeOffset UnixTime(long seconds) { if(seconds < 0 || seconds > 253402300799L) throw new HistoryResolutionException("官方接口时间超出范围。"); return new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(seconds); }
        private static void Report(Action<string> progress, string message) { if(progress != null) progress(message); }

        internal static async Task<string> FetchOfficialAsync(string url, CancellationToken token)
        {
            Uri uri;
            bool allowed = Uri.TryCreate(url, UriKind.Absolute, out uri) && uri.Scheme == "https" && uri.UserInfo.Length == 0 && uri.IsDefaultPort &&
                ((uri.Host == "www.dota2.com" && uri.AbsolutePath == "/datafeed/patchnoteslist") || (uri.Host == "api.steampowered.com" && uri.AbsolutePath == "/ISteamNews/GetNewsForApp/v2/") ||
                (OfficialNewsId(url) != null));
            if(!allowed) throw new InvalidOperationException("只允许读取 Valve 公开补丁和 App 新闻接口。");
            using(var timeout = CancellationTokenSource.CreateLinkedTokenSource(token)) {
                timeout.CancelAfter(TimeSpan.FromSeconds(45));
                var request = (HttpWebRequest)WebRequest.Create(uri);
                request.Method = "GET"; request.AllowAutoRedirect = false; request.Proxy = null; request.UseDefaultCredentials = false;
                request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                request.Accept = OfficialNewsId(url) != null ? "text/html" : "application/json"; request.UserAgent = "DotaArchivePrototype/0.3";
                request.Headers[HttpRequestHeader.AcceptLanguage] = "en-US,en;q=0.9";
                using(timeout.Token.Register(request.Abort)) {
                    try {
                        using(var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false)) {
                            if(response.StatusCode != HttpStatusCode.OK) throw new IOException("官方接口返回 HTTP " + (int)response.StatusCode + "。");
                            if(response.ContentLength > MaximumOfficialBytes) throw new IOException("官方接口响应超过读取上限。");
                            using(var stream = response.GetResponseStream()) using(var reader = new StreamReader(stream, Encoding.UTF8, true)) {
                                var result = new StringBuilder(); char[] buffer = new char[16384]; int read;
                                while((read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0) { timeout.Token.ThrowIfCancellationRequested(); if(result.Length + read > MaximumOfficialBytes) throw new IOException("官方接口响应超过读取上限。"); result.Append(buffer, 0, read); }
                                return result.ToString();
                            }
                        }
                    } catch(WebException) { token.ThrowIfCancellationRequested(); if(timeout.IsCancellationRequested) throw new TimeoutException("Valve 官方接口读取超时。"); throw; }
                }
            }
        }
    }
}
