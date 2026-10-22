using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DotaArchive
{
    // Only the ProcessStartInfo path uses this alias. Downloads, hashes and locks
    // must keep using the original, reparse-free client directory.
    internal static class AsciiClientLaunch
    {
        private const string RootMarker = ".dota-archive-launch-root-v1";
        private const string RootMarkerText = "DotaArchive controlled launch aliases v1\n";
        private const uint MountPointTag = 0xA0000003;

        internal static string GetExecutablePath(string clientDirectory, string relativeExe, string stableIdentity)
        {
            return GetExecutablePathCore(clientDirectory, relativeExe, stableIdentity,
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        }

        internal static string GetExecutablePathCore(string clientDirectory, string relativeExe, string stableIdentity, string localData)
        {
            string originalExe = VersionCatalog.SafePath(clientDirectory, relativeExe);
            string client = Path.GetFullPath(clientDirectory).TrimEnd('\\', '/');
            if(!Directory.Exists(client) || !File.Exists(originalExe)) throw new FileNotFoundException("客户端可执行文件不存在。", originalExe);
            if((File.GetAttributes(originalExe) & FileAttributes.Directory) != 0) throw new InvalidDataException("客户端可执行路径不是普通文件。");
            if(IsAscii(originalExe)) return originalExe;
            string relative = VersionCatalog.NormalizeRelative(relativeExe);
            if(!IsAscii(relative)) throw new InvalidDataException("客户端内部可执行路径包含非 ASCII 字符，无法用目录启动别名兼容。");
            if(String.IsNullOrWhiteSpace(stableIdentity) || stableIdentity.Length > 512 || stableIdentity.IndexOfAny(new[] {'\r','\n','\0'}) >= 0)
                throw new InvalidDataException("缺少有效的启动目录身份。");
            string root = SelectRoot(client, localData);
            EnsureOwnedRoot(root);
            string identity = Hash(client.ToUpperInvariant() + "\n" + stableIdentity);
            string alias = Path.Combine(root, identity);
            string marker = VersionCatalog.SafePath(root, identity + ".owner");
            string markerText = "DotaArchive launch alias v1\n" + client + "\n" + stableIdentity + "\n";
            // SafePath deliberately rejects existing links. Inspect only this
            // exact controlled leaf after validating its entire parent chain.
            alias = Path.Combine(root, identity);
            bool exists = EntryExists(alias);
            if(exists) {
                RequireMarker(marker, markerText);
                RequireMatchingJunction(alias, client);
            } else {
                EnsureMarker(marker, markerText);
                string temporary = VersionCatalog.SafePath(root, ".creating-" + Guid.NewGuid().ToString("N"));
                bool created = false;
                try {
                    Directory.CreateDirectory(temporary); created = true;
                    SetJunction(temporary, client);
                    RequireMatchingJunction(temporary, client);
                    Directory.Move(temporary, alias); created = false;
                } finally {
                    // RemoveDirectory removes this single temporary junction,
                    // never its target. There is no recursive cleanup here.
                    if(created && !RemoveDirectory(temporary)) throw NativeError("无法移除未完成的启动别名");
                }
                RequireMatchingJunction(alias, client);
            }
            string launchExe = Path.Combine(alias, relative.Replace('/', Path.DirectorySeparatorChar));
            if(!IsAscii(launchExe) || !SamePath(FinalPath(launchExe), originalExe))
                throw new InvalidDataException("启动别名未指向已校验的客户端可执行文件。");
            return launchExe;
        }

        internal static string SelectRoot(string client, string localData)
        {
            if(!String.IsNullOrWhiteSpace(localData) && Path.IsPathRooted(localData) && IsAscii(localData))
                return VersionCatalog.SafePath(localData, "DotaArchive/ClientLaunch");
            string ancestor = client;
            while(!IsAscii(ancestor)) {
                DirectoryInfo parent = Directory.GetParent(ancestor);
                if(parent == null) throw new InvalidDataException("没有可用的 ASCII 启动目录，请选择纯英文路径保存客户端。");
                ancestor = parent.FullName;
            }
            if(ancestor.StartsWith(@"\\", StringComparison.Ordinal)) throw new InvalidDataException("无法为网络目录自动创建本地启动别名，请使用本地 ASCII 保存路径。");
            return VersionCatalog.SafePath(ancestor, ".dota-archive-launch");
        }

        private static void EnsureOwnedRoot(string root)
        {
            VersionCatalog.SafePath(root, ".path-check");
            string marker = VersionCatalog.SafePath(root, RootMarker);
            if(Directory.Exists(root)) {
                if(!File.Exists(marker) && Directory.GetFileSystemEntries(root).Length != 0)
                    throw new InvalidDataException("启动别名目录已存在且不属于本程序，已停止：" + root);
            } else Directory.CreateDirectory(root);
            VersionCatalog.SafePath(root, ".path-check");
            EnsureMarker(marker, RootMarkerText);
        }

        private static void EnsureMarker(string path, string text)
        {
            if(EntryExists(path)) { RequireMarker(path, text); return; }
            using(var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                byte[] bytes = new UTF8Encoding(false).GetBytes(text); stream.Write(bytes, 0, bytes.Length); stream.Flush(true);
            }
            RequireMarker(path, text);
        }

        private static void RequireMarker(string path, string text)
        {
            VersionCatalog.SafePath(Path.GetDirectoryName(path), Path.GetFileName(path));
            var info = new FileInfo(path);
            if(!info.Exists || info.Length > 16384 || File.ReadAllText(path, Encoding.UTF8) != text)
                throw new InvalidDataException("启动别名身份记录缺失或不匹配，拒绝复用：" + path);
        }

        private static bool EntryExists(string path)
        {
            try { File.GetAttributes(path); return true; }
            catch(FileNotFoundException) { return false; }
            catch(DirectoryNotFoundException) { return false; }
        }

        private static void RequireMatchingJunction(string path, string target)
        {
            FileAttributes attributes = File.GetAttributes(path);
            if((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != (FileAttributes.Directory | FileAttributes.ReparsePoint))
                throw new InvalidDataException("启动别名不是受控目录联接，拒绝覆盖：" + path);
            byte[] buffer = new byte[16384]; uint returned;
            using(SafeFileHandle handle = Open(path, 0, true)) {
                if(!DeviceIoControl(handle, 0x000900A8, null, 0, buffer, (uint)buffer.Length, out returned, IntPtr.Zero))
                    throw NativeError("无法检查启动目录联接");
            }
            if(returned < 16 || BitConverter.ToUInt32(buffer, 0) != MountPointTag)
                throw new InvalidDataException("启动别名不是 NTFS 目录联接。");
            int offset = BitConverter.ToUInt16(buffer, 8), length = BitConverter.ToUInt16(buffer, 10);
            if((offset & 1) != 0 || (length & 1) != 0 || 16 + offset + length > returned)
                throw new InvalidDataException("目录联接数据无效。");
            string substitute = Encoding.Unicode.GetString(buffer, 16 + offset, length);
            string expected = @"\??\" + target;
            if(!String.Equals(substitute, expected, StringComparison.OrdinalIgnoreCase) || !SamePath(FinalPath(path), target))
                throw new InvalidDataException("既有启动联接指向另一个目录，拒绝使用：" + path);
        }

        internal static void SetJunction(string emptyDirectory, string target)
        {
            // Internal for offline fixtures; the production caller always owns a
            // newly-created empty temporary directory validated with SafePath.
            VersionCatalog.SafePath(emptyDirectory, ".path-check");
            VersionCatalog.SafePath(target, ".path-check");
            if(Directory.GetFileSystemEntries(emptyDirectory).Length != 0) throw new InvalidDataException("目录联接只能建立在空目录中。");
            if(target.StartsWith(@"\\", StringComparison.Ordinal)) throw new InvalidDataException("目录联接目标必须是本地路径。");
            byte[] substitute = Encoding.Unicode.GetBytes(@"\??\" + target);
            byte[] print = Encoding.Unicode.GetBytes(target);
            int dataLength = 8 + substitute.Length + 2 + print.Length + 2;
            if(dataLength + 8 > 16384) throw new PathTooLongException("目录联接目标路径过长。");
            byte[] data = new byte[dataLength + 8];
            Put(data, 0, BitConverter.GetBytes(MountPointTag)); Put(data, 4, BitConverter.GetBytes((ushort)dataLength));
            Put(data, 10, BitConverter.GetBytes((ushort)substitute.Length));
            Put(data, 12, BitConverter.GetBytes((ushort)(substitute.Length + 2))); Put(data, 14, BitConverter.GetBytes((ushort)print.Length));
            Put(data, 16, substitute); Put(data, 18 + substitute.Length, print);
            uint returned;
            using(SafeFileHandle handle = Open(emptyDirectory, 0x40000000, true)) {
                if(!DeviceIoControl(handle, 0x000900A4, data, (uint)data.Length, null, 0, out returned, IntPtr.Zero))
                    throw NativeError("无法创建 ASCII 启动目录联接");
            }
        }

        private static void Put(byte[] destination, int offset, byte[] value) { Buffer.BlockCopy(value, 0, destination, offset, value.Length); }
        private static bool IsAscii(string value) { foreach(char c in value) if(c > 127) return false; return true; }
        private static bool SamePath(string a, string b) { return String.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
        private static string Hash(string value) { using(var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant(); }
        private static Win32Exception NativeError(string message) { int code = Marshal.GetLastWin32Error(); return new Win32Exception(code, message + " (Win32 " + code + ": " + new Win32Exception(code).Message + ")"); }
        private static SafeFileHandle Open(string path, uint access, bool reparse)
        {
            SafeFileHandle handle = CreateFile(path, access, 7, IntPtr.Zero, 3, 0x02000000U | (reparse ? 0x00200000U : 0), IntPtr.Zero);
            if(handle.IsInvalid) { var error = NativeError("无法打开启动路径"); handle.Dispose(); throw error; }
            return handle;
        }
        private static string FinalPath(string path)
        {
            using(SafeFileHandle handle = Open(path, 0, false)) {
                var buffer = new StringBuilder(32768);
                uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
                if(length == 0 || length >= buffer.Capacity) throw NativeError("无法核实启动路径最终目标");
                string result = buffer.ToString();
                if(result.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + result.Substring(8);
                return result.StartsWith(@"\\?\", StringComparison.Ordinal) ? result.Substring(4) : result;
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeviceIoControl(SafeFileHandle handle, uint control, byte[] input, uint inputSize, byte[] output, uint outputSize, out uint returned, IntPtr overlapped);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint size, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RemoveDirectory(string path);
    }
}
