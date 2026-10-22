using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace DotaArchive
{
    public sealed class SteamDbHistoryParser
    {
        public HistoryListing ParseHistory(HistoryPageSnapshot page)
        {
            HtmlDocument doc = Load(page);
            RequireAppPath(page, "/app/570/history/");
            var result = new HistoryListing();
            foreach (HtmlNode panel in doc.DocumentNode.Descendants().Where(n => HasClass(n, "panel-history")))
            {
                string id = panel.GetAttributeValue("data-changeid", "");
                if (!HistoryDepotState.IsNumeric(id)) continue;
                HtmlNode time = panel.Descendants("relative-time").FirstOrDefault();
                if (time == null) time = panel.Descendants("time").FirstOrDefault();
                DateTimeOffset observed;
                if (time == null || !ParseDate(time.GetAttributeValue("datetime", ""), out observed))
                    throw new HistoryResolutionException("历史记录缺少时间字段：" + id);
                var change = new HistoryChange { ChangeId = id, ObservedAtUtc = observed, SourceUrl = "https://steamdb.info/app/570/history/?changeid=" + id, ParsedAllDepotChanges = true };
                List<HtmlNode> repos = panel.Descendants("li").Where(n => HasClass(n, "repo") && n.Elements("span").Any(s => OwnText(s).IndexOf("Depots", StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
                if (repos.Count == 0) { change.ParsedAllDepotChanges = false; change.UnparsedChanges.Add("missing Depots tree"); }
                var removedPublicManifests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (HtmlNode repo in repos) ParseDepotChanges(repo, change, removedPublicManifests);
                HashSet<string> prerequisites = HistoryDepotState.ProjectedPrerequisitePrefixes(change);
                prerequisites.UnionWith(HistoryDepotState.SharedManifestTransitionPrefixes(change));
                foreach (HistoryDelta delta in change.Deltas.Where(d => d.Path.EndsWith("/sharedinstall", StringComparison.Ordinal)))
                    if (!prerequisites.Contains(delta.Path.Substring(0, delta.Path.Length - "sharedinstall".Length)))
                    { change.ParsedAllDepotChanges = false; change.UnparsedChanges.Add("unproven shared installation group: " + delta.Path); }
                result.Changes.Add(change);
                if (change.Publication != null) result.Builds.Add(change.Publication);
            }
            // Steam changelist numbers provide the ordering when the rendered timestamps
            // have only second precision. Do not treat equal seconds as an ambiguous date.
            result.Changes = result.Changes.OrderByDescending(c => c.ObservedAtUtc).ThenByDescending(c => UInt64.Parse(c.ChangeId, CultureInfo.InvariantCulture)).ToList();
            ParseNavigation(doc.DocumentNode, result, page.Url);
            return result;
        }

        private void ParseDepotChanges(HtmlNode repo, HistoryChange change, HashSet<string> removedPublicManifests)
        {
            if (!repo.Descendants("li").Any()) { change.ParsedAllDepotChanges = false; change.UnparsedChanges.Add("empty Depots tree"); return; }
            var publication = new HistoryBuildEvent { ChangeId = change.ChangeId, SourceUrl = change.SourceUrl, IsPublic = true };
            bool publicBuildChanged = false;
            bool publicTimeChanged = false;
            foreach (HtmlNode item in repo.Descendants("li"))
            {
                HtmlNode line = item.Elements("span").FirstOrDefault() ?? item;
                // Only terminal changes: the parent branch/depot label is handled by its children.
                if (item.Elements("ul").Any() || line.Elements("ul").Any()) continue;
                string raw = Text(line);
                if (raw.Length == 0) continue;
                string branch = AncestorBranch(item, repo);
                HtmlNode oldNode = line.Descendants().FirstOrDefault(n => n.Name == "del" || HasClass(n, "del"));
                HtmlNode newNode = line.Descendants().FirstOrDefault(n => n.Name == "ins" || HasClass(n, "ins"));
                string oldValue = oldNode == null ? null : Text(oldNode);
                string newValue = newNode == null ? null : Text(newNode);
                HtmlNode keyNode = line.Descendants("i").FirstOrDefault();
                string label = keyNode == null ? OwnText(line) : Text(keyNode);
                label = label.Trim().TrimEnd(':').Trim();
                HtmlNode inlineDiff = line.Descendants("span").FirstOrDefault(n => HasClass(n, "diff"));
                if (inlineDiff != null && (!HasClass(item, "diff-modified") || !ReadInlineDiff(inlineDiff, out oldValue, out newValue)))
                { change.ParsedAllDepotChanges = false; change.UnparsedChanges.Add("ambiguous inline diff: " + label); continue; }

                // This App-level private-branch metadata flag is not a public
                // manifest or a Depot configuration field. Require the
                // observed top-level scalar shape; do not ignore a similarly named tree.
                if (label.Equals("privatebranches", StringComparison.OrdinalIgnoreCase) && branch == null &&
                    item.Ancestors("li").FirstOrDefault() == repo &&
                    !item.Descendants("ul").Any() && !item.Descendants("ol").Any() &&
                    (oldNode != null || newNode != null)) continue;

                // These observed App-level scalars are outside the explicit
                // non-DLC, non-UGC client Depot projection. DepotDownloader uses
                // workshopdepot only for UGC; normal downloads enumerate numeric
                // Depot IDs. Do not apply this exception to per-Depot fields,
                // branch metadata, nested trees or unrecognized scalar values.
                // The original sanitized batch retains the old/new evidence.
                bool rootScalar = branch == null && item.Ancestors("li").FirstOrDefault() == repo &&
                    !item.Descendants("ul").Any() && !item.Descendants("ol").Any() &&
                    (oldNode != null || newNode != null);
                if (rootScalar && label.Equals("appmanagesdlc", StringComparison.OrdinalIgnoreCase) &&
                    (oldValue == null || oldValue == "0" || oldValue == "1") &&
                    (newValue == null || newValue == "0" || newValue == "1")) continue;
                if (rootScalar && label.Equals("workshopdepot", StringComparison.OrdinalIgnoreCase) &&
                    (oldValue == null || oldValue == "570") && (newValue == null || newValue == "570")) continue;
                if (rootScalar && label.Equals("markdlcdepots", StringComparison.OrdinalIgnoreCase) &&
                    (oldValue == null || oldValue == "0" || oldValue == "1") &&
                    (newValue == null || newValue == "0" || newValue == "1"))
                { AddDelta(change, HistoryDepotState.LegacyDlcMarkingPath, oldValue, newValue); continue; }

                // SteamDB renders encryptedmanifest sizes/IDs under the named beta
                // branch. Only the known rendered fields below in a non-public
                // branch are outside our public-state projection. Unknown encryption
                // fields, absent branch context, and public-branch values still fail.
                Match encryptedManifest = Regex.Match(label, @"^Depot\s+(\d+)\s+encrypted\s+(download|gid|size)$", RegexOptions.IgnoreCase);
                Match encryptedManifestV2 = Regex.Match(label, @"^Depot\s+(\d+)\s+encrypted_(gid|size)_2$", RegexOptions.IgnoreCase);
                bool knownEncryptedMetadata = encryptedManifest.Success && HistoryDepotState.IsNumeric(encryptedManifest.Groups[1].Value) ||
                    encryptedManifestV2.Success && HistoryDepotState.IsNumeric(encryptedManifestV2.Groups[1].Value) ||
                    label == "encrypted_size" || label == "encrypted_gid";
                if (!String.IsNullOrEmpty(branch) && branch != "public" && knownEncryptedMetadata) continue;

                if (branch == "public" && label.Equals("timeupdated", StringComparison.OrdinalIgnoreCase))
                {
                    DateTimeOffset value;
                    if (!ParseDate(newValue, out value)) throw new HistoryResolutionException("公开分支更新时间无法解析：" + change.ChangeId);
                    publication.PublishedAtUtc = value;
                    if (ParseDate(oldValue, out value)) publication.PreviousPublishedAtUtc = value;
                    publicTimeChanged = true;
                    continue;
                }
                if (branch == "public" && label.Equals("buildid", StringComparison.OrdinalIgnoreCase))
                {
                    if (!HistoryDepotState.IsNumeric(newValue)) throw new HistoryResolutionException("公开分支 Build 编号无法解析：" + change.ChangeId);
                    publication.BuildId = newValue; publication.PreviousBuildId = oldValue; publicBuildChanged = true;
                    continue;
                }
                if (label == "timeupdated" || label == "buildid" || label == "timebuildupdated" || label == "pwdrequired" || label == "description") continue;
                Match depotManifest = Regex.Match(label, @"^Depot\s+(\d+)\s+(gid|size|download|maxsize)$", RegexOptions.IgnoreCase);
                if (depotManifest.Success)
                {
                    if (branch != "public" || !depotManifest.Groups[2].Value.Equals("gid", StringComparison.OrdinalIgnoreCase)) continue;
                    string path = "depots/" + depotManifest.Groups[1].Value + "/manifests/public/gid";
                    // Some schema migrations render a retained public manifest as
                    // Removed N followed by Added N. Preserve an explicit N -> N
                    // delta so the state reducer still checks the expected value.
                    // Only the observed ordered, exactly equal pair is eligible.
                    bool restoreSameManifest = oldValue == null && newValue != null && HasClass(item, "diff-added") && removedPublicManifests.Remove(path);
                    if (oldValue != null && newValue == null && HasClass(item, "diff-removed")) removedPublicManifests.Add(path);
                    AddDelta(change, path, oldValue, newValue, restoreSameManifest);
                    continue;
                }
                Match flatDepot = Regex.Match(label, @"^Depot\s+(\d+)\s+(.+)$", RegexOptions.IgnoreCase);
                if (flatDepot.Success)
                {
                    string field = flatDepot.Groups[2].Value.ToLowerInvariant();
                    if (field == "isdlc" || field == "config/lv")
                    {
                        if (branch != null || (oldValue != null && oldValue != "0" && oldValue != "1") ||
                            (newValue != null && newValue != "0" && newValue != "1"))
                        { change.ParsedAllDepotChanges = false; change.UnparsedChanges.Add(label); continue; }
                        AddDelta(change, "depots/" + flatDepot.Groups[1].Value + "/" + field, oldValue, newValue); continue;
                    }
                    if (field == "sharedinstall" && branch == null)
                    { AddDelta(change, "depots/" + flatDepot.Groups[1].Value + "/" + field, oldValue, newValue); continue; }
                    if (field == "name" || field == "dlcappid" || field == "depotfromapp" || field == "config/oslist" || field == "config/osarch" || field == "config/language" || field == "config/lowviolence" || field == "config/optionaldlc")
                    { AddDelta(change, "depots/" + flatDepot.Groups[1].Value + "/" + field, oldValue, newValue); continue; }
                    if (field == "systemdefined" || field == "maxsize" || field == "size" || field == "download") continue;
                }
                // Byte counts do not affect the client scope. Encrypted metadata is
                // handled above only when its non-public branch is explicit.
                if (label.Equals("maxsize", StringComparison.OrdinalIgnoreCase) || label == "size" || label == "download") continue;
                string id = AncestorDepot(item, repo);
                if (id != null)
                {
                    string field = label.ToLowerInvariant();
                    if (field == "isdlc" || field == "lv")
                    {
                        if (branch != null || (oldValue != null && oldValue != "0" && oldValue != "1") ||
                            (newValue != null && newValue != "0" && newValue != "1"))
                        { change.ParsedAllDepotChanges = false; change.UnparsedChanges.Add(label); continue; }
                        AddDelta(change, "depots/" + id + "/" + (field == "lv" ? "config/lv" : "isdlc"), oldValue, newValue); continue;
                    }
                    if (field == "sharedinstall" && branch == null) { AddDelta(change, "depots/" + id + "/" + field, oldValue, newValue); continue; }
                    if (field == "name" || field == "dlcappid" || field == "depotfromapp") { AddDelta(change, "depots/" + id + "/" + field, oldValue, newValue); continue; }
                    if (field == "oslist" || field == "osarch" || field == "language" || field == "lowviolence") { AddDelta(change, "depots/" + id + "/config/" + field, oldValue, newValue); continue; }
                }
                // Unknown relevant leaves are a hard gap; never silently produce a complete plan.
                if (oldNode != null || newNode != null || HasClass(item, "diff-added") || HasClass(item, "diff-removed"))
                { change.ParsedAllDepotChanges = false; change.UnparsedChanges.Add(label); }
            }
            if (publicBuildChanged || publicTimeChanged)
            {
                change.Publication = publication;
            }
        }

        public HistoryListing ParsePatchListing(HistoryPageSnapshot page)
        {
            HtmlDocument doc = Load(page);
            RequireAppPath(page, "/app/570/patchnotes/");
            var result = new HistoryListing();
            HtmlNode patchRows = doc.GetElementbyId("js-patchnotes") ?? doc.GetElementbyId("patchnotes") ?? doc.DocumentNode;
            foreach (HtmlNode row in patchRows.Descendants("tr"))
            {
                HtmlNode anchor = row.Descendants("a").FirstOrDefault(n => Regex.IsMatch(n.GetAttributeValue("href", ""), @"^/patchnotes/\d+/?$"));
                if (anchor == null) continue;
                string id = Regex.Match(anchor.GetAttributeValue("href", ""), @"/patchnotes/(\d+)").Groups[1].Value;
                HtmlNode time = row.Descendants().FirstOrDefault(n => n.Name == "relative-time" || n.Name == "time");
                DateTimeOffset date;
                long seconds;
                if (Int64.TryParse(row.GetAttributeValue("data-date", ""), out seconds)) date = UnixTime(seconds);
                else if (time == null || !ParseDate(time.GetAttributeValue("datetime", ""), out date)) continue;
                result.Builds.Add(new HistoryBuildEvent { BuildId = id, PublishedAtUtc = date, SourceUrl = "https://steamdb.info/patchnotes/" + id + "/", Title = Text(row) });
            }
            result.Builds = result.Builds.GroupBy(b => b.BuildId, StringComparer.Ordinal).Select(g => g.OrderBy(b => b.PublishedAtUtc).First()).ToList();
            ParseNavigation(doc.DocumentNode, result, page.Url);
            return result;
        }

        public HistoryBaseline ParseBaseline(HistoryPageSnapshot page)
        {
            HtmlDocument doc = Load(page);
            RequireAppPath(page, "/app/570/depots/");
            HtmlNode depots = doc.GetElementbyId("depots") ?? doc.DocumentNode;
            if (!Text(depots).Contains("Selected Branch: public")) throw new HistoryResolutionException("Depot 页面必须选择公开分支 public，不能使用未指定分支的当前清单。");
            string build = null, updated = null;
            foreach (HtmlNode row in depots.Descendants("tr"))
            {
                List<HtmlNode> cells = row.Elements("td").ToList();
                if (cells.Count == 2 && Text(cells[0]) == "buildid") build = Text(cells[1]);
                if (cells.Count == 2 && Text(cells[0]) == "timeupdated") updated = Text(cells[1]);
            }
            long seconds;
            if (!HistoryDepotState.IsNumeric(build) || !Int64.TryParse(updated, out seconds)) throw new HistoryResolutionException("公开分支基线缺少 Build 或精确发布时间。");
            var state = new HistoryDepotState { HasSharedPrerequisiteBaseline = true };
            int depotCount = 0;
            var seenDepots = new HashSet<string>(StringComparer.Ordinal);
            foreach (HtmlNode row in depots.Descendants("tr").Where(n => HasClass(n, "depot")))
            {
                string id = row.GetAttributeValue("data-depotid", "");
                if (!HistoryDepotState.IsNumeric(id)) throw new HistoryResolutionException("当前 Depot 基线含无法识别的编号。");
                HtmlNode config = row.Descendants("td").FirstOrDefault(n => HasClass(n, "depot-config"));
                if (config == null) throw new HistoryResolutionException("当前 Depot 基线缺少平台和语言配置。");
                if (!seenDepots.Add(id)) throw new HistoryResolutionException("当前 Depot 基线出现重复编号：" + id);
                HtmlNode manifestLink = row.Descendants("a").FirstOrDefault(n => n.GetAttributeValue("href", "").StartsWith("/depot/" + id + "/history/?changeid=M:", StringComparison.Ordinal));
                // Keep explicit shared-source metadata as part of the complete
                // baseline. Old public manifests and source transitions must be
                // replayed even when the selected client excludes current runtimes.
                string prefix = "depots/" + id + "/";
                if (Text(config).IndexOf("Depot from", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (manifestLink != null || !config.Descendants("a").Any(a => a.GetAttributeValue("href", "").Equals("/app/228980/", StringComparison.Ordinal)))
                        throw new HistoryResolutionException("当前内容包含共享 App Depot，尚未取得共享 App 的完整历史。");
                    state.Values[prefix + "depotfromapp"] = "228980";
                }
                if (manifestLink != null)
                {
                    string manifest = Text(manifestLink);
                    if (!HistoryDepotState.IsNumeric(manifest)) throw new HistoryResolutionException("当前 Depot Manifest 编号无效：" + id);
                    state.Values[prefix + "manifests/public/gid"] = manifest;
                }
                HtmlNode name = config.Descendants().FirstOrDefault(n => HasClass(n, "i") && HasClass(n, "muted"));
                // This is SteamDB's display label, not proof that AppInfo still
                // contains depots/N/name. Keep it separate from canonical fields.
                if (name != null) state.DisplayNames[prefix + "name"] = Text(name);
                var os = config.Descendants().Where(n => HasClass(n, "depot-os")).Select(n => Text(n).ToLowerInvariant()).ToList();
                if (os.Count != 0) state.Values[prefix + "config/oslist"] = String.Join(",", os);
                HtmlNode lang = config.Descendants().FirstOrDefault(n => HasClass(n, "depot-language"));
                if (lang != null) state.Values[prefix + "config/language"] = LanguageKey(Text(lang));
                if (Text(config).Contains("64-bit")) state.Values[prefix + "config/osarch"] = "64";
                if (Text(config).Contains("32-bit")) state.Values[prefix + "config/osarch"] = "32";
                if (config.Descendants("span").Any(n => Text(n).Equals("Low Violence", StringComparison.OrdinalIgnoreCase))) state.Values[prefix + "config/lowviolence"] = "1";
                HtmlNode dlc = config.Descendants("a").FirstOrDefault(n => Text(n).StartsWith("DLC ", StringComparison.Ordinal));
                if (dlc != null)
                {
                    string app = dlc.GetAttributeValue("data-appid", "");
                    if (!HistoryDepotState.IsNumeric(app)) throw new HistoryResolutionException("DLC Depot 配置无法识别：" + id);
                    state.Values[prefix + "dlcappid"] = app;
                }
                depotCount++;
            }
            if (depotCount == 0) throw new HistoryResolutionException("当前公开分支 Depot 表格尚未加载。");
            state.CompleteBaseline = true;
            return new HistoryBaseline { State = state, BuildId = build, PublishedAtUtc = UnixTime(seconds), SourceUrl = page.Url };
        }

        private static string LanguageKey(string label)
        {
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                {"English", "english"}, {"Simplified Chinese", "schinese"}, {"Traditional Chinese", "tchinese"},
                {"Russian", "russian"}, {"Korean", "koreana"}, {"Japanese", "japanese"}, {"French", "french"}, {"German", "german"},
                {"Spanish - Spain", "spanish"}, {"Spanish", "spanish"}, {"Spanish - Latin America", "latam"}, {"Portuguese", "portuguese"},
                {"Portuguese - Brazil", "brazilian"}, {"Brazilian Portuguese", "brazilian"}, {"Italian", "italian"}, {"Polish", "polish"},
                {"Thai", "thai"}, {"Turkish", "turkish"}, {"Ukrainian", "ukrainian"}, {"Vietnamese", "vietnamese"}, {"Indonesian", "indonesian"},
                {"Arabic", "arabic"}, {"Czech", "czech"}, {"Danish", "danish"}, {"Dutch", "dutch"}, {"Finnish", "finnish"}, {"Greek", "greek"},
                {"Hungarian", "hungarian"}, {"Norwegian", "norwegian"}, {"Romanian", "romanian"}, {"Swedish", "swedish"}, {"Bulgarian", "bulgarian"} };
            string value;
            if (!names.TryGetValue(label, out value)) throw new HistoryResolutionException("无法识别 Depot 语言配置：" + label);
            return value;
        }

        private static bool ReadInlineDiff(HtmlNode diff, out string oldValue, out string newValue)
        {
            oldValue = null; newValue = null;
            if (!diff.Descendants().Any(n => n.Name == "del" || n.Name == "ins")) return false;
            string[] title = diff.GetAttributeValue("title", "").Split(new[] { '\u2192' });
            if (title.Length != 2) return false;
            HtmlNode before = diff.CloneNode(true), after = diff.CloneNode(true);
            foreach (HtmlNode node in before.Descendants("ins").ToList()) node.Remove();
            foreach (HtmlNode node in after.Descendants("del").ToList()) node.Remove();
            oldValue = Text(before); newValue = Text(after);
            Func<string, string> normalize = s => Regex.Replace(HtmlEntity.DeEntitize(s).Replace('\u00a0', ' '), @"\s+", " ").Trim();
            // Common spans belong to both sides; del/ins are only the changed
            // fragments. Check the reconstructed full values against the page's
            // explicit full-value tooltip before using either side.
            return oldValue == normalize(title[0]) && newValue == normalize(title[1]);
        }

        internal static DateTimeOffset UnixTime(long seconds)
        {
            if (seconds < 0 || seconds > 253402300799L) throw new HistoryResolutionException("历史时间戳超出范围。");
            return new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(seconds);
        }

        private static void AddDelta(HistoryChange change, string path, string oldValue, string newValue, bool restoreSameManifest = false)
        {
            string[] components = path.Split('/');
            if (path != HistoryDepotState.LegacyDlcMarkingPath && (components.Length < 3 || !HistoryDepotState.IsNumeric(components[1]))) { change.ParsedAllDepotChanges = false; change.UnparsedChanges.Add(path + " (invalid Depot ID)"); return; }
            if (oldValue == null && newValue == null) { change.ParsedAllDepotChanges = false; change.UnparsedChanges.Add(path + " (no values)"); return; }
            HistoryDelta existing = change.Deltas.FirstOrDefault(d => d.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                if (restoreSameManifest && path.EndsWith("/manifests/public/gid", StringComparison.Ordinal) &&
                    existing.OldExists && !existing.NewExists && oldValue == null && HistoryDepotState.IsNumeric(newValue) && existing.OldValue == newValue)
                { existing.NewExists = true; existing.NewValue = newValue; return; }
                change.ParsedAllDepotChanges = false; change.UnparsedChanges.Add(path + " (duplicate)"); return;
            }
            change.Deltas.Add(new HistoryDelta { Path = path, OldExists = oldValue != null, OldValue = oldValue, NewExists = newValue != null, NewValue = newValue });
        }

        private static string AncestorBranch(HtmlNode node, HtmlNode stop)
        {
            for (HtmlNode parent = node.ParentNode; parent != null && parent != stop.ParentNode; parent = parent.ParentNode)
            {
                if (parent.Name != "li") continue;
                HtmlNode line = parent.Elements("span").FirstOrDefault() ?? parent;
                HtmlNode branch = line.ChildNodes.FirstOrDefault(n => HasClass(n, "branch-name"));
                if (branch != null) return Text(branch).ToLowerInvariant();
            }
            return null;
        }

        private static string AncestorDepot(HtmlNode node, HtmlNode stop)
        {
            for (HtmlNode parent = node.ParentNode; parent != null && parent != stop.ParentNode; parent = parent.ParentNode)
            {
                if (parent.Name != "li") continue;
                HtmlNode line = parent.Elements("span").FirstOrDefault() ?? parent;
                HtmlNode anchor = line.ChildNodes.SelectMany(n => n.Name == "a" ? new[] { n } : n.Name == "i" ? n.Descendants("a") : Enumerable.Empty<HtmlNode>()).FirstOrDefault(n => Regex.IsMatch(n.GetAttributeValue("href", ""), @"^/depot/\d+/?$"));
                if (anchor != null) return Regex.Match(anchor.GetAttributeValue("href", ""), @"/depot/(\d+)").Groups[1].Value;
                Match numeric = Regex.Match(OwnText(line), @"^(?:Added|Removed|Changed)\s+(?:Depot\s+)?(\d+)\b", RegexOptions.IgnoreCase);
                if (numeric.Success) return numeric.Groups[1].Value;
            }
            return null;
        }

        internal static HtmlDocument Load(HistoryPageSnapshot page)
        {
            if (page == null || String.IsNullOrEmpty(page.Html)) throw new HistoryResolutionException("浏览器没有返回可读取的页面内容。");
            Uri url;
            if (!Uri.TryCreate(page.Url, UriKind.Absolute, out url) || !url.Host.Equals("steamdb.info", StringComparison.OrdinalIgnoreCase)) throw new HistoryResolutionException("只能从正常打开的 SteamDB 页面解析历史信息。");
            if (page.Html.Length > 64 * 1024 * 1024) throw new HistoryResolutionException("页面内容过大，请缩小历史查询范围。");
            var doc = new HtmlDocument(); doc.LoadHtml(page.Html); return doc;
        }

        private static void RequireAppPath(HistoryPageSnapshot page, string path)
        {
            if (new Uri(page.Url).AbsolutePath.TrimEnd('/') != path.TrimEnd('/')) throw new HistoryResolutionException("浏览器返回的页面与所请求的 Dota 2 数据类型不一致。");
        }

        private static void ParseNavigation(HtmlNode root, HistoryListing listing, string baseUrl)
        {
            HtmlNode next = root.Descendants("a").FirstOrDefault(n => n.GetAttributeValue("rel", "").Equals("next", StringComparison.OrdinalIgnoreCase) && !IsHidden(n));
            if (next != null) listing.NextPageUrl = SafeUrl(baseUrl, next.GetAttributeValue("href", ""));
            HtmlNode more = root.Descendants().FirstOrDefault(n => (n.Name == "button" || n.Name == "a") && (Text(n).Equals("Load more", StringComparison.OrdinalIgnoreCase) || Text(n).Equals("Load more history", StringComparison.OrdinalIgnoreCase)));
            if (more == null) more = root.Descendants().FirstOrDefault(n => n.GetAttributeValue("id", "") == "js-history-load");
            if (more != null && !more.Attributes.Contains("disabled") && !IsHidden(more))
            {
                string id = more.GetAttributeValue("id", "");
                if (Regex.IsMatch(id, @"^[A-Za-z][A-Za-z0-9_-]*$")) listing.LoadMoreSelector = "#" + id;
            }
            else if (more != null) listing.NavigationBlockedReason = "网站隐藏或禁用了继续加载按钮；需要使用网站日期筛选继续读取并核实批次重叠。";
            // Absence of a button is not proof of complete history. Require an explicit endpoint marker.
            string text = String.Join(" ", root.Descendants().Where(n => !n.ChildNodes.Any(c => c.NodeType == HtmlNodeType.Element) && !IsHidden(n)).Select(Text));
            listing.EndReached = Regex.IsMatch(text, @"\b(No more (?:history|changes|updates)|End of history|No (?:changes|updates) found)\b", RegexOptions.IgnoreCase);
        }

        private static bool IsHidden(HtmlNode node)
        {
            for (HtmlNode current = node; current != null; current = current.ParentNode)
            {
                if (current.Attributes.Contains("hidden") || current.GetAttributeValue("aria-hidden", "").Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
                if (Regex.IsMatch(current.GetAttributeValue("style", ""), @"(?:^|;)\s*(?:display\s*:\s*none|visibility\s*:\s*hidden)\s*(?:!important)?(?:;|$)", RegexOptions.IgnoreCase)) return true;
            }
            return false;
        }

        internal static string SafeUrl(string baseUrl, string link)
        {
            Uri result;
            if (!Uri.TryCreate(new Uri(baseUrl), link, out result) || result.Scheme != "https" || result.Host != "steamdb.info") return null;
            return result.AbsoluteUri;
        }
        internal static bool HasClass(HtmlNode node, string name) { return (" " + node.GetAttributeValue("class", "") + " ").IndexOf(" " + name + " ", StringComparison.Ordinal) >= 0; }
        internal static string Text(HtmlNode node) { return Regex.Replace(HtmlEntity.DeEntitize(node.InnerText ?? "").Replace('\u00a0', ' '), @"\s+", " ").Trim(); }
        internal static string OwnText(HtmlNode node) { return Regex.Replace(HtmlEntity.DeEntitize(String.Concat(node.ChildNodes.Where(n => n.Name != "ul" && n.Name != "ol").Select(n => n.InnerText))).Replace('\u00a0', ' '), @"\s+", " ").Trim(); }
        internal static bool ParseDate(string text, out DateTimeOffset value)
        {
            text = (text ?? "").Replace("–", " ").Replace("—", " ").Replace("UTC", "+00:00");
            return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out value);
        }
    }
}
