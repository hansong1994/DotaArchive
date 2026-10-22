using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace DotaArchive
{
    // This type deliberately cannot be used as a complete download plan. Seen Date
    // is an observation of a Manifest, not proof of its effective public activation.
    public sealed class DepotManifestCandidate
    {
        public string DepotId { get; private set; }
        public string CandidateManifestId { get; private set; }
        public DateTimeOffset SeenUtc { get; private set; }
        public DateTimeOffset TargetUtc { get; private set; }
        public string SourceUrl { get; private set; }
        public string ManifestHistoryUrl { get; private set; }
        public int PublicRowsScanned { get; private set; }
        public string Kind { get { return "UnverifiedSeenDateCandidate"; } }
        public bool IsActivationVerified { get { return false; } }
        public bool IsHistoricalMembershipVerified { get { return false; } }
        public string Assurance { get { return "仅为已读取页面中 Seen Date 严格早于输入的最近 public Manifest 候选。Seen Date 不证明实际启用时间，且可能遗漏旧 Manifest 再次启用的回滚；尚未核实该 Depot 在目标时刻的成员身份，也不是完整下载组合。"; } }

        internal DepotManifestCandidate(string depotId, string manifestId, DateTimeOffset seenUtc,
            DateTimeOffset targetUtc, string sourceUrl, string historyUrl, int rows)
        {
            DepotId = depotId; CandidateManifestId = manifestId; SeenUtc = seenUtc;
            TargetUtc = targetUtc; SourceUrl = sourceUrl; ManifestHistoryUrl = historyUrl; PublicRowsScanned = rows;
        }
    }

    public sealed class DepotManifestCandidateParser
    {
        private const int MaximumHtmlLength = 64 * 1024 * 1024;

        // Returns null when no public row on this page is strictly before target.
        // It scans every public row before choosing; DOM ordering is not evidence.
        public DepotManifestCandidate Parse(HistoryPageSnapshot page, string depotId, DateTimeOffset targetUtc)
        {
            uint numericDepot;
            if (!UInt32.TryParse(depotId, NumberStyles.None, CultureInfo.InvariantCulture, out numericDepot) ||
                numericDepot == 0 || numericDepot.ToString(CultureInfo.InvariantCulture) != depotId)
                throw new HistoryResolutionException("候选查询的 Depot 编号无效。");
            string source = "https://steamdb.info/depot/" + depotId + "/manifests/";
            Uri sourceUri;
            if (page == null || !TrySteamDbUri(page.Url, out sourceUri) || sourceUri.AbsolutePath != "/depot/" + depotId + "/manifests/" || sourceUri.Query.Length != 0)
                throw new HistoryResolutionException("Manifest 候选页面的来源与所查询 Depot 不一致。");
            if (String.IsNullOrWhiteSpace(page.Html) || page.Html.Length > MaximumHtmlLength)
                throw new HistoryResolutionException("Manifest 候选页面为空或超过读取上限。");
            var document = new HtmlDocument(); document.LoadHtml(page.Html);
            List<HtmlNode> sections = document.DocumentNode.Descendants().Where(n => n.GetAttributeValue("id", "") == "manifests").ToList();
            if (sections.Count != 1) throw new HistoryResolutionException("没有唯一的 Manifest 列表区域，不能从其他表格猜测候选。");
            List<HtmlNode> tables = sections[0].Descendants("table").Where(IsManifestTable).ToList();
            if (tables.Count != 1) throw new HistoryResolutionException("Manifest 表格的 Seen Date／ManifestID 列发生变化或不唯一。");
            HtmlNode table = tables[0];
            var candidates = new List<Row>();
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (HtmlNode row in table.Descendants("tr").Where(n => n.Ancestors("table").FirstOrDefault() == table && n.Elements("td").Any()))
            {
                // Explicit public membership is required. Private, external and
                // absent/unknown branch labels do not become public by inference.
                if (row.GetAttributeValue("data-branch", "") != "public") continue;
                List<HtmlNode> cells = row.Elements("td").ToList();
                if (cells.Count != 4) throw InvalidRow("列数改变");
                string timeValue = cells[1].GetAttributeValue("data-time", "");
                DateTimeOffset seen;
                if (!TryExplicitTimestamp(timeValue, out seen)) throw InvalidRow("Seen Date 时间无效或没有明确时区");
                DateTimeOffset visible;
                if (!DateTimeOffset.TryParseExact(Text(cells[0]), "d MMMM yyyy '–' HH:mm:ss 'UTC'", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out visible) ||
                    visible.UtcDateTime.Ticks != seen.UtcDateTime.Ticks - seen.UtcDateTime.Ticks % TimeSpan.TicksPerSecond)
                    throw InvalidRow("显示时间与 data-time 不一致");
                List<HtmlNode> links = cells[2].Descendants("a").ToList();
                if (links.Count != 1) throw InvalidRow("缺少唯一 Manifest 链接");
                string manifestId = Text(links[0]);
                ulong numericManifest;
                if (!UInt64.TryParse(manifestId, NumberStyles.None, CultureInfo.InvariantCulture, out numericManifest) ||
                    numericManifest == 0 || numericManifest.ToString(CultureInfo.InvariantCulture) != manifestId)
                    throw InvalidRow("Manifest 编号无效");
                Uri historyUri;
                if (!Uri.TryCreate(sourceUri, HtmlEntity.DeEntitize(links[0].GetAttributeValue("href", "")), out historyUri) ||
                    !IsSteamDbUri(historyUri) || historyUri.AbsolutePath != "/depot/" + depotId + "/history/" ||
                    Uri.UnescapeDataString(historyUri.Query) != "?changeid=M:" + manifestId || historyUri.Fragment.Length != 0)
                    throw InvalidRow("Manifest 链接与 Depot／Manifest 编号不一致");
                if (!seenIds.Add(manifestId)) throw InvalidRow("同一个 public Manifest 出现重复记录");
                candidates.Add(new Row { ManifestId = manifestId, SeenUtc = seen, HistoryUrl = "https://steamdb.info/depot/" + depotId + "/history/?changeid=M:" + manifestId });
            }
            DateTimeOffset target = targetUtc.ToUniversalTime();
            List<Row> eligible = candidates.Where(r => r.SeenUtc < target).OrderByDescending(r => r.SeenUtc).ToList();
            if (eligible.Count == 0) return null;
            if (eligible.Count > 1 && eligible[0].SeenUtc == eligible[1].SeenUtc)
                throw new HistoryResolutionException("最近 Seen Date 对应多个 public Manifest，不能凭编号或表格顺序任选一个。");
            Row selected = eligible[0];
            return new DepotManifestCandidate(depotId, selected.ManifestId, selected.SeenUtc, target, source, selected.HistoryUrl, candidates.Count);
        }

        private static bool IsManifestTable(HtmlNode table)
        {
            List<HtmlNode> heads = table.Elements("thead").ToList();
            if (heads.Count != 1) return false;
            List<HtmlNode> rows = heads[0].Elements("tr").ToList();
            if (rows.Count != 1) return false;
            List<string> labels = rows[0].Elements("th").Select(Text).ToList();
            return labels.Count == 4 && labels[0] == "Seen Date" && labels[1] == "Relative Date" && labels[2] == "ManifestID" && labels[3].Length == 0;
        }
        private static bool TryExplicitTimestamp(string text, out DateTimeOffset result)
        {
            result = default(DateTimeOffset);
            if (!Regex.IsMatch(text, @"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,7})?(?:Z|[+-][0-9]{2}:[0-9]{2})$")) return false;
            if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out result) || result.Year < 1970) return false;
            result = result.ToUniversalTime(); return true;
        }
        private static bool TrySteamDbUri(string value, out Uri uri) { return Uri.TryCreate(value, UriKind.Absolute, out uri) && IsSteamDbUri(uri); }
        private static bool IsSteamDbUri(Uri uri) { return uri.Scheme == Uri.UriSchemeHttps && uri.Host == "steamdb.info" && uri.IsDefaultPort && uri.UserInfo.Length == 0; }
        private static string Text(HtmlNode node) { return Regex.Replace(HtmlEntity.DeEntitize(node.InnerText ?? "").Replace('\u00a0', ' '), @"\s+", " ").Trim(); }
        private static HistoryResolutionException InvalidRow(string reason) { return new HistoryResolutionException("public Manifest 候选行无法核实：" + reason + "。尚未生成下载组合。"); }
        private sealed class Row { internal string ManifestId, HistoryUrl; internal DateTimeOffset SeenUtc; }
    }
}
