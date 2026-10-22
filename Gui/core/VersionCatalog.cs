using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace DotaArchive
{
    public static class VersionCatalog
    {
        internal static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
        internal const string IcuSource = "game/bin/win64/icuuc.dll";
        internal const string IcuPrivate = "game/bin/win64/icu52.dll";
        internal const string BackupRoot = "_compatibility/icu52/originals/";

        public static CatalogDocument Load(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("未指定版本目录文件。");
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 32L * 1024 * 1024) throw new InvalidDataException("版本目录不存在或超过大小限制。");
            var serializer = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024, RecursionLimit = 64 };
            CatalogDocument catalog;
            try
            {
                string json = File.ReadAllText(path);
                CheckJsonTypes(serializer.DeserializeObject(json));
                catalog = serializer.Deserialize<CatalogDocument>(json);
            }
            catch (Exception ex) { throw new InvalidDataException("无法读取版本目录 JSON。", ex); }
            if (catalog == null || catalog.SchemaVersion != 1) throw new InvalidDataException("不支持的版本目录格式。");
            RequireHash(catalog.EngineSha256, 64, "下载引擎 SHA256");
            if (catalog.Versions == null || catalog.Versions.Count == 0) throw new InvalidDataException("版本目录为空。");
            var ids = new HashSet<string>(PathComparer);
            var folders = new HashSet<string>(PathComparer);
            foreach (var version in catalog.Versions)
            {
                var files = ValidateVersion(version);
                if (!ids.Add(version.Id) || !folders.Add(version.FolderName)) throw new InvalidDataException("版本 ID 或保存目录重复。");
                version.Files = files;
            }
            TimelineResolver.ValidateCatalogTimeline(catalog);
            return catalog;
        }

        // JavaScriptSerializer otherwise coerces JSON numbers into string fields,
        // which would conceal precision loss in externally generated manifest IDs.
        private static void CheckJsonTypes(object raw)
        {
            var root = raw as Dictionary<string, object>;
            if (root == null) throw new InvalidDataException("版本目录根节点必须是对象。");
            RequireJsonInteger(root, "SchemaVersion");
            RequireJsonString(root, "EngineSha256");
            CheckAppId(root);
            foreach (object item in RequireJsonArray(root, "Versions"))
            {
                var version = item as Dictionary<string, object>;
                if (version == null) throw new InvalidDataException("版本记录必须是对象。");
                foreach (string name in new[] { "Id", "Patch", "Date", "FolderName", "BuildId", "ClientVersion", "Executable" }) RequireJsonString(version, name);
                CheckOptionalJsonString(version, "PublishedAtUtc");
                RequireJsonInteger(version, "TotalBytes");
                CheckAppId(version);
                foreach (object depotObject in RequireJsonArray(version, "Depots"))
                {
                    var depot = depotObject as Dictionary<string, object>;
                    if (depot == null) throw new InvalidDataException("内容库记录必须是对象。");
                    RequireJsonString(depot, "DepotId");
                    RequireJsonString(depot, "ManifestId");
                    RequireJsonInteger(depot, "Bytes");
                }
                foreach (object fileObject in RequireJsonArray(version, "Files"))
                {
                    var file = fileObject as Dictionary<string, object>;
                    if (file == null) throw new InvalidDataException("文件记录必须是对象。");
                    RequireJsonString(file, "Path");
                    RequireJsonString(file, "Sha1");
                    RequireJsonInteger(file, "Size");
                }
                foreach (object directory in RequireJsonArray(version, "Directories"))
                    if (!(directory is string)) throw new InvalidDataException("目录路径必须是字符串。");
            }
            object transitions;
            if (root.TryGetValue("PublicTransitions", out transitions) && transitions != null)
            {
                foreach (object item in RequireJsonArray(root, "PublicTransitions"))
                {
                    var transition = item as Dictionary<string, object>;
                    if (transition == null) throw new InvalidDataException("公开发布记录必须是对象。");
                    foreach (string name in new[] { "PreviousBuildId", "PreviousPublishedAtUtc", "BuildId", "PublishedAtUtc", "SourceUrl" }) RequireJsonString(transition, name);
                    CheckOptionalJsonString(transition, "VersionId");
                }
            }
        }
        private static void CheckOptionalJsonString(Dictionary<string, object> map, string name)
        {
            object value;
            if (map.TryGetValue(name, out value) && value != null && !(value is string)) throw new InvalidDataException(name + " 必须是 JSON 字符串，不能是数字。");
        }
        private static object[] RequireJsonArray(Dictionary<string, object> map, string name)
        {
            object value;
            if (!map.TryGetValue(name, out value) || !(value is object[])) throw new InvalidDataException(name + " 必须是数组。");
            return (object[])value;
        }
        private static void RequireJsonString(Dictionary<string, object> map, string name)
        {
            object value;
            if (!map.TryGetValue(name, out value) || !(value is string)) throw new InvalidDataException(name + " 必须是 JSON 字符串，不能是数字。");
        }
        private static void RequireJsonInteger(Dictionary<string, object> map, string name)
        {
            object value;
            if (!map.TryGetValue(name, out value) || (!(value is int) && !(value is long))) throw new InvalidDataException(name + " 必须是 JSON 整数。");
        }
        private static void CheckAppId(Dictionary<string, object> map)
        {
            object value;
            if (map.TryGetValue("AppId", out value) && Convert.ToString(value, CultureInfo.InvariantCulture) != "570") throw new InvalidDataException("版本目录的 AppId 必须为 Dota 2 的 570。");
        }

        // Validates lexical paths before any filesystem call, then rejects every
        // existing reparse point from the drive/share root to the final target.
        public static string SafePath(string root, string relative)
        {
            if (String.IsNullOrWhiteSpace(root) || !Path.IsPathRooted(root)) throw new ArgumentException("根目录必须是绝对路径。");
            string windowsRoot = root.Replace('/', '\\');
            if (windowsRoot.StartsWith(@"\\?\", StringComparison.Ordinal) || windowsRoot.StartsWith(@"\\.\", StringComparison.Ordinal) ||
                (!windowsRoot.StartsWith(@"\\", StringComparison.Ordinal) && (windowsRoot.Length < 3 || windowsRoot[1] != ':' || windowsRoot[2] != '\\')))
                throw new ArgumentException("根目录不能使用驱动器相对路径或设备命名空间。");
            string normalized = NormalizeRelative(relative);
            string fullRoot = Path.GetFullPath(root);
            string prefix = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string result = Path.GetFullPath(Path.Combine(prefix, normalized.Replace('/', Path.DirectorySeparatorChar)));
            if (!result.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("路径超出目标目录。");
            CheckExistingChain(result);
            return result;
        }

        // Downloaders can write staging/legacy paths absent from the selected
        // manifest, so inspect the entire existing tree before handing it over.
        // Only attributes are read; a reparse entry is rejected before traversal.
        public static void AssertNoReparseTree(string root, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            SafePath(root, ".dota-archive-tree-validation"); // Checks the root and its existing ancestor chain.
            string fullRoot = Path.GetFullPath(root);
            var pending = new Stack<string>();
            pending.Push(fullRoot);
            while (pending.Count != 0)
            {
                token.ThrowIfCancellationRequested();
                string directory = pending.Pop();
                FileAttributes directoryAttributes = File.GetAttributes(directory);
                if ((directoryAttributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("下载目录树包含重解析点：" + directory);
                if ((directoryAttributes & FileAttributes.Directory) == 0) throw new InvalidDataException("下载目录路径不是目录：" + directory);
                foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    token.ThrowIfCancellationRequested();
                    FileAttributes attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("下载目录树包含重解析点：" + entry);
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                }
            }
            token.ThrowIfCancellationRequested();
        }

        internal static string NormalizeRelative(string relative)
        {
            if (String.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.IndexOf(':') >= 0)
                throw new InvalidDataException("不安全的相对路径：" + relative);
            string value = relative.Replace('\\', '/');
            foreach (string part in value.Split('/'))
            {
                if (part.Length == 0 || part == "." || part == ".." || part.EndsWith(".", StringComparison.Ordinal) || part.EndsWith(" ", StringComparison.Ordinal))
                    throw new InvalidDataException("路径包含空段、父目录或 Windows 路径别名：" + relative);
                foreach (char c in part)
                    if (c < 32 || "<>\"|?*".IndexOf(c) >= 0) throw new InvalidDataException("路径包含无效字符：" + relative);
                string stem = part.Split('.')[0];
                if (Regex.IsMatch(stem, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9]|CONIN\$|CONOUT\$)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    throw new InvalidDataException("路径包含 Windows 设备名称：" + relative);
            }
            return value;
        }

        private static void CheckExistingChain(string fullPath)
        {
            var chain = new List<string>();
            string current = fullPath;
            while (!String.IsNullOrEmpty(current))
            {
                chain.Add(current);
                DirectoryInfo parent = Directory.GetParent(current);
                current = parent == null ? null : parent.FullName;
            }
            chain.Reverse();
            for (int i = 0; i < chain.Count; i++)
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(chain[i]); }
                catch (FileNotFoundException) { continue; }
                catch (DirectoryNotFoundException) { continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("拒绝通过重解析点访问：" + chain[i]);
                if (i != chain.Count - 1 && (attributes & FileAttributes.Directory) == 0) throw new InvalidDataException("父路径不是目录：" + chain[i]);
            }
        }

        internal static List<FileEntry> ValidateVersion(VersionEntry version)
        {
            if (version == null) throw new InvalidDataException("版本记录为空。");
            if (String.IsNullOrWhiteSpace(version.Id) || !Regex.IsMatch(version.Id, @"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")) throw new InvalidDataException("版本 ID 无效。");
            if (String.IsNullOrWhiteSpace(version.Patch)) throw new InvalidDataException("补丁号为空。");
            DateTime date;
            if (!DateTime.TryParseExact(version.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) throw new InvalidDataException("版本日期必须是有效的 yyyy-MM-dd 日期。");
            RequireNumber(version.BuildId, false, "BuildId");
            RequireNumber(version.ClientVersion, false, "ClientVersion");
            string folder = NormalizeRelative(version.FolderName);
            if (folder.IndexOf('/') >= 0) throw new InvalidDataException("保存目录名称只能包含一级目录。");
            if (version.TotalBytes < 0 || version.Files == null || version.Files.Count == 0) throw new InvalidDataException("文件清单为空或总大小无效。");
            if (version.Depots == null || version.Depots.Count == 0) throw new InvalidDataException("缺少内容库清单。");
            var depotIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var depot in version.Depots)
            {
                if (depot == null) throw new InvalidDataException("内容库记录为空。");
                RequireNumber(depot.DepotId, true, "DepotId");
                RequireNumber(depot.ManifestId, false, "ManifestId");
                if (!depotIds.Add(depot.DepotId)) throw new InvalidDataException("内容库 ID 重复，不能选择两个清单版本。");
                if (depot.Bytes < 0) throw new InvalidDataException("内容库大小无效。");
            }
            var unique = new Dictionary<string, FileEntry>(PathComparer);
            long total = 0;
            foreach (var file in version.Files)
            {
                if (file == null || file.Size < 0) throw new InvalidDataException("文件记录无效。");
                string name = NormalizeRelative(file.Path);
                RequireHash(file.Sha1, 40, name);
                if (file.Size != 0 && IsZeroHash(file.Sha1)) throw new InvalidDataException("非空文件不能使用空文件哈希哨兵：" + name);
                FileEntry earlier;
                if (unique.TryGetValue(name, out earlier))
                {
                    if (earlier.Size != file.Size || !HashEquals(earlier.Sha1, file.Sha1)) throw new InvalidDataException("重复路径的文件内容冲突：" + name);
                    continue;
                }
                unique.Add(name, new FileEntry { Path = name, Size = file.Size, Sha1 = file.Sha1.ToLowerInvariant() });
                try { checked { total += file.Size; } }
                catch (OverflowException ex) { throw new InvalidDataException("文件大小总和溢出。", ex); }
            }
            if (total != version.TotalBytes) throw new InvalidDataException("TotalBytes 与去重后的文件总大小不一致。");
            foreach (var file in unique.Values)
            {
                string ancestor = file.Path;
                while (ancestor.LastIndexOf('/') >= 0)
                {
                    ancestor = ancestor.Substring(0, ancestor.LastIndexOf('/'));
                    if (unique.ContainsKey(ancestor)) throw new InvalidDataException("同一路径同时用作文件和父目录：" + ancestor);
                }
            }
            if (version.Directories == null) throw new InvalidDataException("缺少目录清单。");
            foreach (string directory in version.Directories)
            {
                string ancestor = NormalizeRelative(directory);
                for (;;)
                {
                    if (unique.ContainsKey(ancestor)) throw new InvalidDataException("同一路径同时是目录和文件：" + ancestor);
                    if (ancestor.LastIndexOf('/') < 0) break;
                    ancestor = ancestor.Substring(0, ancestor.LastIndexOf('/'));
                }
            }
            string executable = NormalizeRelative(version.Executable);
            if (!PathComparer.Equals(executable, "game/bin/win64/dota2.exe") && !PathComparer.Equals(executable, "game/bin/win32/dota2.exe") &&
                !PathComparer.Equals(executable, "dota.exe") && !PathComparer.Equals(executable, "bin/dota.exe"))
                throw new InvalidDataException("此版本目录只接受 Dota 2 客户端启动程序。");
            if (!unique.ContainsKey(executable)) throw new InvalidDataException("启动程序未包含在文件清单中。");
            if (version.LaunchArguments != null)
                foreach (char c in version.LaunchArguments) if (c < 32) throw new InvalidDataException("启动参数包含控制字符。");
            ValidateCompatibility(version.Compatibility, unique);
            return new List<FileEntry>(unique.Values);
        }

        private static void ValidateCompatibility(CompatibilitySpec spec, Dictionary<string, FileEntry> files)
        {
            if (spec == null) return;
            if (spec.Patches == null || spec.Patches.Count != 2 || spec.Copies == null || spec.Copies.Count != 1)
                throw new InvalidDataException("ICU 兼容方案必须包含两个补丁和一个私有副本。");
            var paths = new HashSet<string>(PathComparer);
            foreach (var patch in spec.Patches)
            {
                if (patch == null) throw new InvalidDataException("补丁记录为空。");
                string path = NormalizeRelative(patch.Path);
                if (!PathComparer.Equals(path, "game/bin/win64/icui18n.dll") && !PathComparer.Equals(path, "game/bin/win64/v8.dll")) throw new InvalidDataException("不支持的兼容补丁目标。");
                if (!paths.Add(path)) throw new InvalidDataException("兼容补丁目标重复。");
                FileEntry original;
                if (!files.TryGetValue(path, out original)) throw new InvalidDataException("兼容补丁目标不在原版文件清单中。");
                RequireHash(patch.OriginalSha1, 40, "原版补丁哈希");
                RequireHash(patch.PatchedSha1, 40, "兼容补丁哈希");
                if (!HashEquals(original.Sha1, patch.OriginalSha1) || HashEquals(patch.OriginalSha1, patch.PatchedSha1) || IsZeroHash(patch.PatchedSha1)) throw new InvalidDataException("兼容补丁哈希与原版清单不匹配。");
                if (patch.Offsets == null || patch.Offsets.Length != 2 || patch.BeforeBytes == null || patch.BeforeBytes.Length != 2 || patch.AfterBytes == null || patch.AfterBytes.Length != 2 ||
                    patch.Offsets[0] < 3 || patch.Offsets[1] != patch.Offsets[0] + 1 || patch.Offsets[1] >= original.Size ||
                    patch.BeforeBytes[0] != 117 || patch.BeforeBytes[1] != 99 || patch.AfterBytes[0] != 53 || patch.AfterBytes[1] != 50)
                    throw new InvalidDataException("兼容补丁必须是已知的 ICU 导入名称两字节修正。");
                string backup = BackupRoot + Path.GetFileName(path);
                if (files.ContainsKey(backup)) throw new InvalidDataException("兼容备份路径与游戏文件冲突。");
            }
            CopySpec copy = spec.Copies[0];
            if (copy == null || !PathComparer.Equals(NormalizeRelative(copy.Source), IcuSource) || !PathComparer.Equals(NormalizeRelative(copy.Target), IcuPrivate)) throw new InvalidDataException("不支持的 ICU 私有副本路径。");
            RequireHash(copy.Sha1, 40, "ICU 副本哈希");
            FileEntry source;
            if (!files.TryGetValue(IcuSource, out source) || !HashEquals(source.Sha1, copy.Sha1) || files.ContainsKey(IcuPrivate)) throw new InvalidDataException("ICU 副本与原版文件清单冲突。");
        }

        private static void RequireNumber(string text, bool uint32, string label)
        {
            ulong number;
            if (String.IsNullOrEmpty(text) || !Regex.IsMatch(text, @"^[1-9][0-9]*$") || !UInt64.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number) || (uint32 && number > UInt32.MaxValue))
                throw new InvalidDataException(label + " 必须是有效的正整数字符串。");
        }

        private static void RequireHash(string text, int length, string label)
        {
            if (text == null || text.Length != length || !Regex.IsMatch(text, @"\A[0-9a-fA-F]+\z")) throw new InvalidDataException(label + " 哈希无效。");
        }
        internal static bool HashEquals(string a, string b) { return String.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        internal static bool IsZeroHash(string hash) { return hash != null && hash == new string('0', 40); }
    }
}
