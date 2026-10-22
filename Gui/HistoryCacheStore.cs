using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace DotaArchive
{
    // Only reconstructed public history is persisted. No browser HTML or account data.
    internal sealed class HistoryCacheStore
    {
        internal const int MaximumBytes = 64 * 1024 * 1024;
        internal const int MaximumChanges = 50000;
        private readonly string file;
        internal static string DefaultDirectory { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DotaArchive", "BrowserPrototype"); } }
        internal HistoryCacheStore(string directory) { file = Path.Combine(Path.GetFullPath(directory), "verified-public-history-v1.json"); }

        internal sealed class CacheData
        {
            internal HistoryBaseline Baseline;
            internal List<HistoryChange> Changes;
        }
        // String dates deliberately avoid JavaScriptSerializer's DateTimeOffset format.
        public sealed class FileData
        {
            public int SchemaVersion { get; set; }
            public string AppId { get; set; }
            public string Branch { get; set; }
            public string SavedAtUtc { get; set; }
            public BaselineData Baseline { get; set; }
            public List<ChangeData> Changes { get; set; }
        }
        public sealed class BaselineData
        {
            public string BuildId { get; set; }
            public string PublishedAtUtc { get; set; }
            public Dictionary<string, string> Values { get; set; }
            public string NameProjection { get; set; }
            public Dictionary<string, string> DisplayNames { get; set; }
            public List<string> KnownNamePaths { get; set; }
            public string SharedPrerequisiteProjection { get; set; }
            public List<string> KnownSharedInstallPaths { get; set; }
            public List<string> KnownOptionalDlcPaths { get; set; }
            public bool HasKnownLegacyDlcMarking { get; set; }
        }
        public sealed class ChangeData
        {
            public string ChangeId { get; set; }
            public string ObservedAtUtc { get; set; }
            public List<HistoryDelta> Deltas { get; set; }
            public PublicationData Publication { get; set; }
        }
        public sealed class PublicationData
        {
            public string BuildId { get; set; }
            public string PublishedAtUtc { get; set; }
            public string PreviousBuildId { get; set; }
            public string PreviousPublishedAtUtc { get; set; }
        }

        internal CacheData Load()
        {
            if (!File.Exists(file)) return null;
            long length = new FileInfo(file).Length;
            if (length <= 0 || length > MaximumBytes) throw new InvalidDataException("历史缓存大小不符合限制。");
            string json = File.ReadAllText(file, Encoding.UTF8);
            return Parse(json);
        }

        // Reused by the historical anchor envelope. Decoding never bypasses the
        // same schema, source scope, size, ordering, or field checks as v1.
        internal static CacheData Parse(string json)
        {
            if (Encoding.UTF8.GetByteCount(json) > MaximumBytes) throw new InvalidDataException("历史缓存超过大小限制。");
            FileData data = Serializer().Deserialize<FileData>(json);
            if (data == null || data.SchemaVersion != 1 || data.AppId != "570" || data.Branch != "public") throw new InvalidDataException("历史缓存版本或 App/分支不匹配。");
            if (data.Baseline == null || !HistoryDepotState.IsNumeric(data.Baseline.BuildId) || data.Baseline.Values == null || data.Baseline.Values.Count == 0 || data.Baseline.Values.Count > 4096 || data.Changes == null || data.Changes.Count > MaximumChanges)
                throw new InvalidDataException("历史缓存缺少有效基线或超出记录上限。");
            var state = new HistoryDepotState { CompleteBaseline = true };
            bool legacyNames = data.Baseline.NameProjection == null;
            if (!legacyNames && data.Baseline.NameProjection != "display-labels-separated-v1") throw new InvalidDataException("历史缓存名称投影版本不受支持。");
            if (legacyNames && (data.Baseline.DisplayNames != null || data.Baseline.KnownNamePaths != null)) throw new InvalidDataException("历史缓存名称字段缺少投影标记。");
            foreach (var pair in data.Baseline.Values)
            {
                ValidateField(pair.Key, pair.Value);
                // Old baselines populated this exact key from .i.muted display
                // labels. Reclassify only that baseline metadata, not raw deltas.
                if (legacyNames && HistoryDepotState.IsNamePath(pair.Key)) state.DisplayNames.Add(pair.Key, pair.Value);
                else state.Values.Add(pair.Key, pair.Value);
            }
            if (!legacyNames)
            {
                if (data.Baseline.DisplayNames == null || data.Baseline.KnownNamePaths == null || data.Baseline.DisplayNames.Count > 4096 || data.Baseline.KnownNamePaths.Count > 4096)
                    throw new InvalidDataException("历史缓存名称投影字段无效。");
                foreach (var pair in data.Baseline.DisplayNames)
                {
                    ValidateField(pair.Key, pair.Value);
                    if (!HistoryDepotState.IsNamePath(pair.Key)) throw new InvalidDataException("展示名称中包含非名称配置字段。");
                    state.DisplayNames.Add(pair.Key, pair.Value);
                }
                foreach (string path in data.Baseline.KnownNamePaths)
                    if (!HistoryDepotState.IsNamePath(path) || !state.KnownNamePaths.Add(path)) throw new InvalidDataException("名称历史已知状态无效或重复。");
                if (state.Values.Keys.Any(p => HistoryDepotState.IsNamePath(p) && !state.KnownNamePaths.Contains(p)))
                    throw new InvalidDataException("规范名称值缺少已知状态标记。");
            }
            if (data.Baseline.SharedPrerequisiteProjection != null && data.Baseline.SharedPrerequisiteProjection != "explicit-steamworks-sources-v1")
                throw new InvalidDataException("历史缓存共享运行库投影版本不受支持。");
            state.HasSharedPrerequisiteBaseline = data.Baseline.SharedPrerequisiteProjection != null;
            if (!state.HasSharedPrerequisiteBaseline && data.Baseline.KnownSharedInstallPaths != null)
                throw new InvalidDataException("共享安装已知值缺少来源投影标记。");
            if (state.HasSharedPrerequisiteBaseline)
            {
                if (data.Baseline.KnownSharedInstallPaths == null || data.Baseline.KnownSharedInstallPaths.Count > 4096)
                    throw new InvalidDataException("共享安装已知值字段无效。");
                foreach (string path in data.Baseline.KnownSharedInstallPaths)
                    if (!HistoryDepotState.IsSharedInstallPath(path) || !state.KnownSharedInstallPaths.Add(path))
                        throw new InvalidDataException("共享安装已知状态无效或重复。");
                if (state.Values.Keys.Any(p => HistoryDepotState.IsSharedInstallPath(p) && !state.KnownSharedInstallPaths.Contains(p)))
                    throw new InvalidDataException("共享安装值缺少已知状态标记。");
            }
            if (data.Baseline.KnownOptionalDlcPaths != null)
            {
                if (data.Baseline.KnownOptionalDlcPaths.Count > 4096) throw new InvalidDataException("可选 DLC 已知状态超过上限。");
                foreach (string path in data.Baseline.KnownOptionalDlcPaths)
                    if (!HistoryDepotState.IsOptionalDlcPath(path) || !state.KnownOptionalDlcPaths.Add(path))
                        throw new InvalidDataException("可选 DLC 已知状态无效或重复。");
            }
            state.HasKnownLegacyDlcMarking = data.Baseline.HasKnownLegacyDlcMarking;
            if (state.Values.ContainsKey(HistoryDepotState.LegacyDlcMarkingPath) && !state.HasKnownLegacyDlcMarking)
                throw new InvalidDataException("旧 DLC 标记字段缺少已知状态标记。");
            var result = new CacheData { Baseline = new HistoryBaseline { BuildId = data.Baseline.BuildId, PublishedAtUtc = ParseDate(data.Baseline.PublishedAtUtc), SourceUrl = "https://steamdb.info/app/570/depots/?branch=public", State = state }, Changes = new List<HistoryChange>() };
            var ids = new HashSet<string>(StringComparer.Ordinal);
            HistoryChange previous = null;
            foreach (ChangeData item in data.Changes)
            {
                if (item == null || !HistoryDepotState.IsNumeric(item.ChangeId) || !ids.Add(item.ChangeId) || item.Deltas == null || item.Deltas.Count > 4096) throw new InvalidDataException("历史缓存变更记录无效或重复。");
                var change = new HistoryChange { ChangeId = item.ChangeId, ObservedAtUtc = ParseDate(item.ObservedAtUtc), SourceUrl = "https://steamdb.info/app/570/history/?changeid=" + item.ChangeId, ParsedAllDepotChanges = true };
                if (previous != null && (change.ObservedAtUtc > previous.ObservedAtUtc || (change.ObservedAtUtc == previous.ObservedAtUtc && UInt64.Parse(change.ChangeId, CultureInfo.InvariantCulture) >= UInt64.Parse(previous.ChangeId, CultureInfo.InvariantCulture))))
                    throw new InvalidDataException("历史缓存顺序不连续。");
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (HistoryDelta delta in item.Deltas)
                {
                    if (delta == null || !paths.Add(delta.Path) || (!delta.OldExists && !delta.NewExists)) throw new InvalidDataException("历史缓存含无效字段变更。");
                    if (delta.OldExists) ValidateField(delta.Path, delta.OldValue); else if (delta.OldValue != null) throw new InvalidDataException("历史缓存旧值存在标记不匹配。");
                    if (delta.NewExists) ValidateField(delta.Path, delta.NewValue); else if (delta.NewValue != null) throw new InvalidDataException("历史缓存新值存在标记不匹配。");
                    change.Deltas.Add(delta);
                }
                if (item.Publication != null)
                {
                    PublicationData pub = item.Publication;
                    if (pub.BuildId != null && !HistoryDepotState.IsNumeric(pub.BuildId) || pub.PreviousBuildId != null && !HistoryDepotState.IsNumeric(pub.PreviousBuildId)) throw new InvalidDataException("历史缓存公开 Build 编号无效。");
                    change.Publication = new HistoryBuildEvent { BuildId = pub.BuildId, PublishedAtUtc = ParseDate(pub.PublishedAtUtc), PreviousBuildId = pub.PreviousBuildId, PreviousPublishedAtUtc = pub.PreviousPublishedAtUtc == null ? (DateTimeOffset?)null : ParseDate(pub.PreviousPublishedAtUtc), ChangeId = change.ChangeId, SourceUrl = change.SourceUrl, IsPublic = true };
                }
                result.Changes.Add(change); previous = change;
            }
            return result;
        }

        internal void Save(HistoryBaseline baseline, IList<HistoryChange> changes)
        {
            if (baseline == null || changes == null || changes.Count == 0) return;
            string json = Serialize(baseline, changes);
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            string temporary = Path.Combine(Path.GetDirectoryName(file), "history-" + Guid.NewGuid().ToString("N").Substring(0, 12) + ".tmp");
            try
            {
                File.WriteAllText(temporary, json, new UTF8Encoding(false));
                if (File.Exists(file)) File.Replace(temporary, file, null); else File.Move(temporary, file);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        internal static string Serialize(HistoryBaseline baseline, IList<HistoryChange> changes)
        {
            if (!baseline.State.CompleteBaseline || changes.Count > MaximumChanges) throw new InvalidDataException("已核实历史超过缓存记录上限，本次查询仍可继续。");
            var data = new FileData { SchemaVersion = 1, AppId = "570", Branch = "public", SavedAtUtc = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture), Baseline = new BaselineData { BuildId = baseline.BuildId, PublishedAtUtc = baseline.PublishedAtUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture), Values = new Dictionary<string, string>(baseline.State.Values, StringComparer.OrdinalIgnoreCase), NameProjection = "display-labels-separated-v1", DisplayNames = new Dictionary<string, string>(baseline.State.DisplayNames, StringComparer.OrdinalIgnoreCase), KnownNamePaths = baseline.State.KnownNamePaths.OrderBy(p => p, StringComparer.Ordinal).ToList(), SharedPrerequisiteProjection = baseline.State.HasSharedPrerequisiteBaseline ? "explicit-steamworks-sources-v1" : null, KnownSharedInstallPaths = baseline.State.HasSharedPrerequisiteBaseline ? baseline.State.KnownSharedInstallPaths.OrderBy(p => p, StringComparer.Ordinal).ToList() : null }, Changes = new List<ChangeData>() };
            data.Baseline.KnownOptionalDlcPaths = baseline.State.KnownOptionalDlcPaths.OrderBy(p => p, StringComparer.Ordinal).ToList();
            data.Baseline.HasKnownLegacyDlcMarking = baseline.State.HasKnownLegacyDlcMarking;
            foreach (HistoryChange change in changes)
            {
                if (!change.ParsedAllDepotChanges) throw new InvalidDataException("尚未核实的记录不能写入历史缓存。");
                var item = new ChangeData { ChangeId = change.ChangeId, ObservedAtUtc = change.ObservedAtUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture), Deltas = change.Deltas.ToList() };
                if (change.Publication != null)
                {
                    HistoryBuildEvent pub = change.Publication;
                    item.Publication = new PublicationData { BuildId = pub.BuildId, PublishedAtUtc = pub.PublishedAtUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture), PreviousBuildId = pub.PreviousBuildId, PreviousPublishedAtUtc = pub.PreviousPublishedAtUtc.HasValue ? pub.PreviousPublishedAtUtc.Value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) : null };
                }
                data.Changes.Add(item);
            }
            string json = Serializer().Serialize(data);
            if (Encoding.UTF8.GetByteCount(json) > MaximumBytes) throw new InvalidDataException("已核实历史超过缓存文件大小上限，本次查询仍可继续。");
            return json;
        }
        internal void Invalidate() { if (File.Exists(file)) File.Delete(file); }
        private static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength = MaximumBytes, RecursionLimit = 32 }; }
        private static DateTimeOffset ParseDate(string value)
        {
            DateTimeOffset parsed;
            if (value == null || !DateTimeOffset.TryParseExact(value, "o", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed) || parsed.Offset != TimeSpan.Zero) throw new InvalidDataException("历史缓存时间不是标准 UTC 字符串。");
            return parsed;
        }
        private static void ValidateField(string path, string value)
        {
            if (path == null || value == null || value.Length > 4096) throw new InvalidDataException("历史缓存字段为空或过长。");
            if (path == HistoryDepotState.LegacyDlcMarkingPath)
            {
                if (value != "0" && value != "1") throw new InvalidDataException("历史缓存中的旧 DLC 标记值无效。");
                return;
            }
            string[] components = path.Split('/');
            if (components.Length < 3 || components[0] != "depots" || !HistoryDepotState.IsNumeric(components[1])) throw new InvalidDataException("历史缓存字段路径无效。");
            string field = String.Join("/", components.Skip(2));
            if (!(new[] { "manifests/public/gid", "name", "dlcappid", "isdlc", "depotfromapp", "sharedinstall", "config/oslist", "config/osarch", "config/language", "config/lowviolence", "config/lv", "config/optionaldlc" }).Contains(field, StringComparer.Ordinal)) throw new InvalidDataException("历史缓存含不支持的字段。");
            if ((field == "isdlc" || field == "config/lv") && value != "0" && value != "1") throw new InvalidDataException("历史缓存中的旧布尔字段值无效。");
        }
    }
}
