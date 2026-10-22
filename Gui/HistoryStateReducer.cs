using System;
using System.Collections.Generic;
using System.Linq;

namespace DotaArchive
{
    // A complete current app depot snapshot plus a continuous reverse history reconstructs
    // the exact retained manifests, including rollback activations and removed depots.
    public sealed class HistoryDepotState
    {
        internal const string LegacyDlcMarkingPath = "app/depots/markdlcdepots";
        public Dictionary<string, string> Values { get; private set; }
        public Dictionary<string, string> DisplayNames { get; private set; }
        public HashSet<string> KnownNamePaths { get; private set; }
        public HashSet<string> KnownSharedInstallPaths { get; private set; }
        public HashSet<string> KnownOptionalDlcPaths { get; private set; }
        public bool HasSharedPrerequisiteBaseline { get; set; }
        public bool HasKnownLegacyDlcMarking { get; set; }
        public bool CompleteBaseline { get; set; }
        public HistoryDepotState()
        {
            Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            DisplayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            KnownNamePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            KnownSharedInstallPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            KnownOptionalDlcPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        public void Reverse(HistoryChange change)
        {
            Validate(change);
            HashSet<string> prerequisites = HasSharedPrerequisiteBaseline ? new HashSet<string>() : ProjectedPrerequisitePrefixes(change);
            foreach (HistoryDelta delta in change.Deltas)
            {
                if (prerequisites.Contains(DepotPrefix(delta.Path)) && !IsNamePath(delta.Path)) continue;
                if (IsNamePath(delta.Path)) KnownNamePaths.Add(delta.Path);
                if (IsSharedInstallPath(delta.Path)) KnownSharedInstallPaths.Add(delta.Path);
                if (IsOptionalDlcPath(delta.Path)) KnownOptionalDlcPaths.Add(delta.Path);
                if (delta.Path == LegacyDlcMarkingPath) HasKnownLegacyDlcMarking = true;
                if (delta.OldExists) Values[delta.Path] = delta.OldValue;
                else Values.Remove(delta.Path);
            }
        }

        public void Validate(HistoryChange change)
        {
            if (change == null || !change.ParsedAllDepotChanges)
                throw new HistoryResolutionException("历史记录中存在未能完整解析的 Depot 变更，已停止，避免混合不同构建的文件。");
            HashSet<string> prerequisites = HasSharedPrerequisiteBaseline ? new HashSet<string>() : ProjectedPrerequisitePrefixes(change);
            HashSet<string> referenceGroups = ProjectedPrerequisitePrefixes(change);
            HashSet<string> transitions = SharedManifestTransitionPrefixes(change);
            foreach (string prefix in prerequisites)
                if (Values.Keys.Any(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !IsNamePath(p)))
                    throw new HistoryResolutionException("共享运行库增删组与已有客户端字段冲突：" + prefix + "。不能把游戏内容转移当作独立运行库引用。");
            // Validate first, then mutate: malformed records cannot leave a half-applied state.
            foreach (HistoryDelta delta in change.Deltas)
            {
                if (prerequisites.Contains(DepotPrefix(delta.Path)) && !IsNamePath(delta.Path)) continue;
                if (delta.Path == LegacyDlcMarkingPath && !HasKnownLegacyDlcMarking)
                {
                    // Older table baselines omitted this App-level flag. Only
                    // the observed enable-marking event plus an explicit isdlc
                    // creation whose later value is already proved can establish
                    // it. Keep the flag's history, without treating its absence
                    // or disabled state as a prohibition on all public Depots.
                    if (!Values.ContainsKey(delta.Path) && delta.OldExists && delta.OldValue == "0" &&
                        delta.NewExists && delta.NewValue == "1" && change.Deltas.Any(d =>
                            IsLegacyDlcPath(d.Path) && !d.OldExists && d.NewExists && d.NewValue == "1" && Value(d.Path) == "1")) continue;
                    throw new HistoryResolutionException("旧 DLC 标记状态缺少完整基线或明确启用事件，不能建立下载范围。");
                }
                if (IsSharedInstallPath(delta.Path))
                {
                    string prefix = DepotPrefix(delta.Path);
                    // The table proves the source App but its Shared Install label
                    // does not prove the numeric AppInfo value. The first raw delta
                    // establishes that value; later deltas retain ordinary checks.
                    bool sourceProven = Value(prefix + "depotfromapp") == "228980" ||
                        referenceGroups.Contains(prefix) && !delta.NewExists;
                    if (!HasSharedPrerequisiteBaseline || !sourceProven ||
                        !referenceGroups.Contains(prefix) && !transitions.Contains(prefix))
                        throw new HistoryResolutionException("共享安装迁移缺少来自原始完整基线的 Steamworks 来源证明：" + prefix + "。旧缓存需用同一基线原始页面补全后重新回放。");
                    if (!KnownSharedInstallPaths.Contains(delta.Path)) continue;
                }
                // The table's muted Depot label can survive removal of AppInfo's
                // name field. It does not prove canonical presence or absence.
                // After an actual historical name delta has established a value,
                // retain normal checks for subsequent name history too.
                if (IsNamePath(delta.Path) && !KnownNamePaths.Contains(delta.Path)) continue;
                // The Depot table renders one DLC App link, not a separate
                // optionaldlc field. A paired creation or identical DLC-source
                // migration can establish that unrendered value only when the
                // matching DLC App is already proven. Every existing side must
                // remain a nonzero DLC App, so this cannot change client scope.
                // Do not infer a current optional value for arbitrary Depots or
                // accept mismatches after raw history established the field.
                if (IsOptionalDlcPath(delta.Path) && !KnownOptionalDlcPaths.Contains(delta.Path) &&
                    !Values.ContainsKey(delta.Path) && delta.NewExists &&
                    IsNumeric(delta.NewValue) && delta.NewValue != "0" &&
                    (!delta.OldExists || IsNumeric(delta.OldValue) && delta.OldValue != "0"))
                {
                    string dlcPath = DepotPrefix(delta.Path) + "dlcappid";
                    if (Value(dlcPath) == delta.NewValue && change.Deltas.Any(d => d.Path == dlcPath &&
                        d.OldExists == delta.OldExists && d.NewExists && d.NewValue == delta.NewValue &&
                        d.OldValue == delta.OldValue)) continue;
                }
                string current;
                bool exists = Values.TryGetValue(delta.Path, out current);
                if (delta.NewExists && (!exists || !String.Equals(current, delta.NewValue, StringComparison.Ordinal)))
                    throw new HistoryResolutionException("历史记录与后续状态不连续：" + delta.Path + "。无法证明中间没有遗漏或回滚。");
                if (!delta.NewExists && exists)
                    throw new HistoryResolutionException("已删除的历史字段仍出现在后续状态：" + delta.Path + "。");
            }
        }

        public List<DepotEntry> SelectWindowsClient(string language)
        {
            if (!CompleteBaseline) throw new HistoryResolutionException("尚未取得完整 Depot 基线，不能把只包含变更的页面当作完整客户端下载清单。");
            string marking = Value(LegacyDlcMarkingPath);
            if (marking != null && marking != "0" && marking != "1")
                throw new HistoryResolutionException("历史 DLC 标记值无效，无法确定 Depot 配置。");
            // Scope comes from each Depot's configuration at this exact historical
            // state. The App-level flag is retained and checked during replay;
            // it is not a download prerequisite. In particular, never backfill a
            // later DLC classification into an earlier unmarked Depot.
            var result = new List<DepotEntry>();
            var ids = Values.Keys.Where(p => p.StartsWith("depots/", StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Split('/')).Where(p => p.Length >= 3 && IsNumeric(p[1])).Select(p => p[1]).Distinct().OrderBy(p => UInt64.Parse(p));
            foreach (string id in ids)
            {
                string prefix = "depots/" + id + "/";
                // Keep the historical source field and any old public manifests
                // in the replay. Only the source at this exact state decides scope.
                // A reverse source transition can therefore return a Depot to the
                // game client; a modern shared label never excludes it forever.
                if (Value(prefix + "depotfromapp") == "228980") continue;
                string manifest = Value(prefix + "manifests/public/gid");
                if (manifest == null) continue; // No public branch content in this historical state.
                string os = Value(prefix + "config/oslist") ?? "";
                string depotLanguage = Value(prefix + "config/language") ?? "";
                string dlc = Value(prefix + "dlcappid") ?? "";
                string legacyDlc = Value(prefix + "isdlc") ?? "";
                string optionalDlc = Value(prefix + "config/optionaldlc") ?? "";
                string lowViolence = Value(prefix + "config/lowviolence") ?? "";
                string legacyLowViolence = Value(prefix + "config/lv") ?? "";
                if (os.Length != 0 && !os.Split(',').Any(p => String.Equals(p.Trim(), "windows", StringComparison.OrdinalIgnoreCase))) continue;
                if (dlc.Length != 0 && dlc != "0") continue;
                if (legacyDlc == "1") continue;
                if (optionalDlc.Length != 0 && optionalDlc != "0") continue;
                if (lowViolence == "1") continue;
                if (legacyLowViolence == "1") continue;
                if (depotLanguage.Length != 0 && !String.Equals(depotLanguage, language, StringComparison.OrdinalIgnoreCase) && !String.Equals(depotLanguage, "english", StringComparison.OrdinalIgnoreCase)) continue;
                string sharedApp = Value(prefix + "depotfromapp");
                if (!String.IsNullOrEmpty(sharedApp) && sharedApp != "570")
                    throw new HistoryResolutionException("构建包含来自其他 App 的共享 Depot " + id + "，需要还原该共享 App 的历史状态。");
                if (!IsNumeric(manifest) || manifest == "0") throw new HistoryResolutionException("Depot " + id + " 的公开 Manifest 编号无效。");
                string namePath = prefix + "name", name = Value(namePath);
                if (name == null && !KnownNamePaths.Contains(namePath)) DisplayNames.TryGetValue(namePath, out name);
                result.Add(new DepotEntry { DepotId = id, ManifestId = manifest, Name = name ?? ("Depot " + id) });
            }
            if (result.Count == 0) throw new HistoryResolutionException("历史状态中没有找到适用于 Windows 的公开 Depot。");
            return result;
        }

        public string Value(string path) { string value; return Values.TryGetValue(path, out value) ? value : null; }

        // Legacy baselines omitted Steamworks Common Redistributables. This narrow
        // compatibility projection permits only whole-reference additions/removals.
        // Enriched baselines keep these exact fields and replay them normally.
        // A source transition, partial edit, public manifest, or unsupported field
        // does not qualify. sharedinstall alone never makes arbitrary content optional.
        internal static HashSet<string> ProjectedPrerequisitePrefixes(HistoryChange change)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in change.Deltas.GroupBy(d => DepotPrefix(d.Path)))
            {
                if (group.Key == null) continue;
                List<HistoryDelta> deltas = group.ToList();
                HistoryDelta source = deltas.SingleOrDefault(d => d.Path == group.Key + "depotfromapp");
                HistoryDelta install = deltas.SingleOrDefault(d => d.Path == group.Key + "sharedinstall");
                if (source == null || install == null || source.OldExists == source.NewExists) continue;
                bool addition = source.NewExists;
                if ((addition ? source.NewValue : source.OldValue) != "228980" ||
                    install.OldExists != source.OldExists || install.NewExists != source.NewExists ||
                    (addition ? install.NewValue : install.OldValue) != "1") continue;
                if (deltas.Any(d => d.OldExists != source.OldExists || d.NewExists != source.NewExists ||
                    !new[] { "depotfromapp", "sharedinstall", "name", "config/oslist" }.Contains(d.Path.Substring(group.Key.Length), StringComparer.Ordinal))) continue;
                result.Add(group.Key);
            }
            return result;
        }

        internal static HashSet<string> SharedManifestTransitionPrefixes(HistoryChange change)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in change.Deltas.GroupBy(d => DepotPrefix(d.Path)))
            {
                if (group.Key == null || group.Count() != 2) continue;
                HistoryDelta install = group.SingleOrDefault(d => IsSharedInstallPath(d.Path));
                HistoryDelta manifest = group.SingleOrDefault(d => d.Path == group.Key + "manifests/public/gid");
                if (install == null || manifest == null || install.OldExists == install.NewExists ||
                    manifest.OldExists != install.NewExists || manifest.NewExists != install.OldExists ||
                    (install.NewExists ? install.NewValue : install.OldValue) != "1" ||
                    !IsNumeric(manifest.OldExists ? manifest.OldValue : manifest.NewValue)) continue;
                result.Add(group.Key);
            }
            return result;
        }

        internal static bool IsSharedInstallPath(string path)
        {
            string prefix = DepotPrefix(path);
            return prefix != null && path == prefix + "sharedinstall";
        }

        internal static bool IsOptionalDlcPath(string path)
        {
            string prefix = DepotPrefix(path);
            return prefix != null && path == prefix + "config/optionaldlc";
        }

        private static bool IsLegacyDlcPath(string path)
        {
            string prefix = DepotPrefix(path);
            return prefix != null && path == prefix + "isdlc";
        }

        private static string DepotPrefix(string path)
        {
            string[] parts = (path ?? "").Split('/');
            return parts.Length >= 3 && parts[0] == "depots" && IsNumeric(parts[1]) ? "depots/" + parts[1] + "/" : null;
        }
        internal static bool IsNamePath(string path)
        {
            string[] parts = (path ?? "").Split('/');
            return parts.Length == 3 && parts[0] == "depots" && IsNumeric(parts[1]) && parts[2] == "name";
        }
        internal static bool IsNumeric(string value) { ulong n; return !String.IsNullOrEmpty(value) && UInt64.TryParse(value, out n) && n.ToString(System.Globalization.CultureInfo.InvariantCulture) == value; }
    }
}
