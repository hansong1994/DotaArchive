using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace DotaArchive
{
    public sealed class LocalPatchReleaseDate
    {
        public string Patch { get; set; }
        public string ReleaseDate { get; set; }
        public string Precision { get; set; }
        public string SourceTimeZone { get; set; }
        public string AnnouncedAtUtc { get; set; }
        public string RawTimestampUtc { get; set; }
        public string DateBoundaryUtc { get; set; }
        public string DateEndUtc { get; set; }
        public List<string> Sources { get; set; }
        public List<string> Ambiguities { get; set; }
    }

    // This catalog contains official dates, never Build/Depot/Manifest mappings.
    // Loading a missing entry must not trigger any network request.
    public sealed class LocalPatchDateCatalog
    {
        public sealed class Document
        {
            public int SchemaVersion { get; set; }
            public string AppId { get; set; }
            public List<LocalPatchReleaseDate> Patches { get; set; }
        }
        private readonly string file;
        public LocalPatchDateCatalog(string file) { this.file = Path.GetFullPath(file); }
        public IReadOnlyList<LocalPatchReleaseDate> Entries { get { return Load().AsReadOnly(); } }
        public LocalPatchReleaseDate Find(string patch)
        {
            patch = Normalize(patch);
            return Load().SingleOrDefault(p => p.Patch == patch);
        }
        public static string Normalize(string patch)
        {
            patch = (patch ?? "").Trim().ToLowerInvariant();
            if (!Regex.IsMatch(patch, @"^[0-9]+\.[0-9]{2}[a-z]?$"))
                throw new HistoryResolutionException("请输入完整补丁号，例如 7.00 或 7.22b。");
            return patch;
        }
        private List<LocalPatchReleaseDate> Load()
        {
            var info = new FileInfo(file);
            if (!info.Exists) throw new HistoryResolutionException("内置补丁发布日期表缺失，请重新解压完整软件包。版本号查询仅使用本地资料，不会打开 SteamDB。");
            if (info.Length == 0 || info.Length > 8 * 1024 * 1024) throw Invalid();
            Document data;
            try { data = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024, RecursionLimit = 64 }.Deserialize<Document>(File.ReadAllText(file, Encoding.UTF8)); }
            catch (ArgumentException) { throw Invalid(); }
            catch (InvalidOperationException) { throw Invalid(); }
            if (data == null || data.SchemaVersion != 1 || data.AppId != "570" || data.Patches == null || data.Patches.Count == 0 || data.Patches.Count > 5000) throw Invalid();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (LocalPatchReleaseDate entry in data.Patches)
            {
                DateTime day;
                if (entry == null || Normalize(entry.Patch) != entry.Patch || !seen.Add(entry.Patch) ||
                    !DateTime.TryParseExact(entry.ReleaseDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day) || day.Year < 2011 ||
                    String.IsNullOrWhiteSpace(entry.SourceTimeZone) || entry.Sources == null || entry.Sources.Count == 0 ||
                    (entry.Precision != "date-only" && entry.Precision != "announcement-time")) throw Invalid();
                foreach (string source in entry.Sources)
                {
                    Uri uri;
                    if (!Uri.TryCreate(source, UriKind.Absolute, out uri) || (uri.Scheme != "https" && uri.Scheme != "http") || uri.UserInfo.Length != 0 || !uri.IsDefaultPort ||
                        !(uri.Host == "dota2.com" || uri.Host == "www.dota2.com" || uri.Host == "blog.dota2.com" || uri.Host == "dota2.com.cn" || uri.Host == "www.dota2.com.cn" || uri.Host == "store.steampowered.com" ||
                          uri.Host == "api.steampowered.com" || uri.Host == "steamcommunity.com" || uri.Host == "steamstore-a.akamaihd.net")) throw Invalid();
                }
                DateTimeOffset start = ReadTime(entry.DateBoundaryUtc);
                DateTimeOffset end = String.IsNullOrEmpty(entry.DateEndUtc) ? start.AddDays(1) : ReadTime(entry.DateEndUtc);
                if (end <= start || end - start > TimeSpan.FromDays(2)) throw Invalid();
                if (entry.Precision == "announcement-time") ReadTime(entry.AnnouncedAtUtc);
            }
            return data.Patches;
        }
        internal static DateTimeOffset ReadTime(string value)
        {
            DateTimeOffset time;
            if (String.IsNullOrWhiteSpace(value) || !Regex.IsMatch(value, @"(Z|[+-]\d{2}:\d{2})$") ||
                !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out time) || time.Year < 2011 || time.Year > 2100) throw Invalid();
            return time.ToUniversalTime();
        }
        private static HistoryResolutionException Invalid() { return new HistoryResolutionException("内置补丁发布日期表损坏或存在重复、无效日期；已停止本地查询，不会转到 SteamDB。"); }
    }

    // The only dependencies are local file readers and a history replay engine
    // whose browser throws on every operation. There is no online fallback here.
    public sealed class LocalPatchQueryResolver
    {
        private readonly LocalPatchDateCatalog dates;
        private readonly LocalArchiveResolver[] archives;
        public LocalPatchQueryResolver(LocalPatchDateCatalog dates, LocalPatchEvidenceStore evidence, params LocalArchiveResolver[] archives)
        {
            if (dates == null || archives == null || archives.Length == 0 || archives.Any(a => a == null)) throw new ArgumentException("需要本地补丁日期和历史库。");
            // Keep the evidence argument for source compatibility. A directional
            // lookup must not be replaced by an independently proven first release.
            this.dates = dates; this.archives = archives;
        }
        public async Task<ResolvedHistoryPlan> ResolveAsync(HistoryQuery query, CancellationToken token, Action<string> progress)
        {
            if (query == null || query.RequestedUtc.HasValue || query.BeijingTime.HasValue) throw new HistoryResolutionException("本地补丁查询仅接受版本号。");
            if (query.TimeDirection != HistoryTimeDirection.Before && query.TimeDirection != HistoryTimeDirection.After)
                throw new HistoryResolutionException("版本号查询方向无效，请选择此前最近版本或此后最近版本。");
            token.ThrowIfCancellationRequested();
            string patch = LocalPatchDateCatalog.Normalize(query.Patch);
            LocalPatchReleaseDate entry = await Task.Run(() => dates.Find(patch), token);
            if (entry == null) throw new HistoryResolutionException("内置发布日期表尚未收录补丁 " + patch + "。请核对版本号，或改用时间节点查询；本次不会访问 SteamDB。");
            if (entry.Ambiguities != null && entry.Ambiguities.Count > 0)
                throw new HistoryResolutionException("补丁 " + patch + " 的官方发布日期资料存在冲突，尚不能自动选择客户端。可改用时间节点查询；本次不会访问 SteamDB。");
            DateTimeOffset requested;
            string description;
            if (entry.Precision == "announcement-time")
            {
                requested = LocalPatchDateCatalog.ReadTime(entry.AnnouncedAtUtc);
                description = "补丁 " + patch + " 的查询基准（北京）：" + Beijing(requested) +
                    "；来源精度：公告时间（announcement-time）；来源时区：" + entry.SourceTimeZone + "。公告时间不等于已核实的客户端发布时间。";
            }
            else
            {
                // A day-start boundary is a documented query convention, never
                // an invented exact release time. Use the stored zone conversion.
                requested = LocalPatchDateCatalog.ReadTime(entry.DateBoundaryUtc);
                description = "补丁 " + patch + " 的内置发布日期：" + entry.ReleaseDate +
                    "；查询基准（北京）：" + Beijing(requested) +
                    "；来源精度：仅日期（date-only）；来源时区：" + entry.SourceTimeZone + "。按目录记录的日期起点查询，该起点不是已核实的精确发布时间。";
            }
            bool before = query.TimeDirection == HistoryTimeDirection.Before;
            string direction = before ? "此前" : "此后";
            description += "匹配规则：严格" + (before ? "早于" : "晚于") + "查询基准的最近公开版本（相等排除）。";
            if (progress != null) progress(description + " 正在查询本地完整历史。");
            var timeQuery = new HistoryQuery { RequestedUtc = requested, Language = query.Language, TimeDirection = query.TimeDirection };
            foreach (LocalArchiveResolver archive in archives)
            {
                token.ThrowIfCancellationRequested();
                ResolvedHistoryPlan plan = await archive.ResolveAsync(timeQuery, token, progress);
                if (plan == null) continue;
                plan.Patch = patch;
                plan.PatchMatchKind = before ? "LocalReleaseDateBeforeCandidate" : "LocalReleaseDateAfterCandidate";
                plan.PatchDateDescription = description;
                plan.Assurance += description + " 输入的版本号用于确定查询基准；完整 Depot/Manifest 来自本地连续历史回放。";
                plan.Limitations.Add("此结果是该补丁日期基准" + direction + "的最近公开版本，可能属于另一个补丁；输入的版本号不是结果所属补丁或首发构建的认证。完整组合校验不等于补丁身份核实。");
                foreach (string source in entry.Sources) if (!plan.Sources.Contains(source)) plan.Sources.Add(source);
                if (progress != null) progress(description + " 已匹配 Build " + plan.BuildId + "，全程未访问 SteamDB。");
                return plan;
            }
            throw new HistoryResolutionException("本地历史库中没有覆盖补丁 " + patch + " 查询基准" + direction + "的可还原完整版本。查询基准（北京）：" + Beijing(requested) + "，相等排除。请先更新本地历史资料，或改用时间节点查询；不会改查另一方向，本次不会访问 SteamDB。");
        }
        private static string Beijing(DateTimeOffset time) { return time.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture); }
    }
}
