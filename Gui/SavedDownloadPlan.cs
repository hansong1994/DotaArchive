using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace DotaArchive
{
    // Restores only a completed resolver result already bound to this directory.
    // This is a download checkpoint, never an alternate history/version index.
    internal static class SavedDownloadPlan
    {
        internal static ResolvedHistoryPlan Load(string directory)
        {
            if(String.IsNullOrWhiteSpace(directory) || !Path.IsPathRooted(directory)) throw new InvalidDataException("请选择已下载版本的绝对路径。");
            directory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var raw = Read(VersionCatalog.SafePath(directory, ".dota-archive-browser/resolved-plan.json"), 1024 * 1024);
            if(!Boolean(raw, "Complete")) throw new InvalidDataException("保存的历史清单尚未完整，不能恢复下载。");
            var plan = new ResolvedHistoryPlan {
                Complete = true, BuildId = Identifier(raw, "BuildId"), PublishedAtUtc = Date(raw, "PublishedAtUtc").Value,
                Patch = Text(raw, "Patch"), SourceUrl = Text(raw, "SourceUrl"), Assurance = Text(raw, "Assurance"),
                TimeSource = Text(raw, "TimeSource"), UsedObservationFallback = Boolean(raw, "UsedObservationFallback"),
                Sources = Strings(raw, "Sources"), Limitations = Strings(raw, "Limitations"),
                PatchMatchKind = Text(raw, "PatchMatchKind"), PatchSourceChangeId = Text(raw, "PatchSourceChangeId"),
                PatchDateDescription = raw.ContainsKey("PatchDateDescription") ? Text(raw, "PatchDateDescription") : null,
                PatchSourceObservedAtUtc = Date(raw, "PatchSourceObservedAtUtc"),
                PatchWindowNotAvailableAtUtc = Date(raw, "PatchWindowNotAvailableAtUtc"),
                PatchWindowAvailableByUtc = Date(raw, "PatchWindowAvailableByUtc")
            };
            object count = Value(raw, "PatchWindowPublicEvents");
            if(count != null) { if(!(count is int) || (int)count < 0) throw Invalid(); plan.PatchWindowPublicEvents = (int)count; }
            if(!Regex.IsMatch(plan.SourceUrl ?? "", @"^https://steamdb\.info/app/570/history/\?changeid=[1-9][0-9]*$")) throw Invalid();
            if(plan.TimeSource != "Valve public/timeupdated" && plan.TimeSource != "SteamDB public branch change observed time") throw Invalid();
            if(plan.UsedObservationFallback != (plan.TimeSource == "SteamDB public branch change observed time")) throw Invalid();
            var depots = Value(raw, "Depots") as object[];
            if(depots == null || depots.Length == 0 || depots.Length > 1000) throw Invalid();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach(object item in depots) {
                var row = item as Dictionary<string, object>; if(row == null) throw Invalid();
                string id = Identifier(row, "DepotId"), manifest = Identifier(row, "ManifestId");
                uint depotNumber; ulong manifestNumber;
                if(!UInt32.TryParse(id, out depotNumber) || !UInt64.TryParse(manifest, out manifestNumber) || !ids.Add(id)) throw Invalid();
                plan.Depots.Add(new DepotEntry { DepotId = id, ManifestId = manifest, Name = Text(row, "Name"), Bytes = 0 });
            }
            var marker = Read(VersionCatalog.SafePath(directory, ".dota-archive-browser.json"), 32768);
            if(!(Value(marker, "SchemaVersion") is int) || (int)Value(marker, "SchemaVersion") != 1 || Text(marker, "AppId") != "570" ||
                Text(marker, "BuildId") != plan.BuildId || Text(marker, "PlanFingerprint") != PrototypeForm.Fingerprint(plan))
                throw new InvalidDataException("保存计划与版本目录标识不一致，已保留原文件。");
            string expected = VersionCatalog.SafePath(Path.GetDirectoryName(directory), PrototypeForm.ToDownloadEntry(plan).FolderName);
            if(!String.Equals(expected, directory, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("恢复目录名与保存计划不一致。");
            return plan;
        }
        private static Dictionary<string, object> Read(string path, int maximum)
        {
            var info = new FileInfo(path);
            if(!info.Exists || info.Length == 0 || info.Length > maximum) throw new InvalidDataException("下载断点不存在或大小异常：" + Path.GetFileName(path));
            var result = new JavaScriptSerializer { MaxJsonLength = maximum, RecursionLimit = 32 }.DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>;
            if(result == null) throw Invalid(); return result;
        }
        private static object Value(Dictionary<string, object> raw, string key) { object value; if(!raw.TryGetValue(key, out value)) throw Invalid(); return value; }
        private static string Text(Dictionary<string, object> raw, string key) { object value = Value(raw, key); if(value != null && !(value is string)) throw Invalid(); return (string)value; }
        private static string Identifier(Dictionary<string, object> raw, string key) { string value = Text(raw, key); if(!Regex.IsMatch(value ?? "", @"^[1-9][0-9]{0,19}$")) throw Invalid(); return value; }
        private static bool Boolean(Dictionary<string, object> raw, string key) { object value = Value(raw, key); if(!(value is bool)) throw Invalid(); return (bool)value; }
        private static List<string> Strings(Dictionary<string, object> raw, string key) { var values = Value(raw, key) as object[]; if(values == null || values.Any(v => !(v is string))) throw Invalid(); return values.Cast<string>().ToList(); }
        private static DateTimeOffset? Date(Dictionary<string, object> raw, string key)
        {
            object value = Value(raw, key);
            if(value == null) { if(key == "PublishedAtUtc") throw Invalid(); return null; }
            DateTimeOffset date;
            if(value is DateTime) date = new DateTimeOffset(((DateTime)value).ToUniversalTime());
            else if(value is string && DateTimeOffset.TryParseExact((string)value, "o", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) { }
            else throw Invalid();
            if(date.Year < 2003 || date > DateTimeOffset.UtcNow.AddDays(1)) throw Invalid();
            return date.ToUniversalTime();
        }
        private static InvalidDataException Invalid() { return new InvalidDataException("保存的下载计划格式或来源不符合要求，已停止恢复。"); }
    }
}
