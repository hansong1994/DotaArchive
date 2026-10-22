using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;

namespace DotaArchive
{
    public static class ManifestVerifier
    {
        private sealed class Probe { public long Size; public string Sha1; }
        private sealed class Work
        {
            public CancellationToken Token;
            public Action<ProgressInfo> Callback;
            public long Total;
            public long Read;
            public long LastReportedBytes;
            public int Depots;
            public byte[] Buffer = new byte[1024 * 1024];
            public Stopwatch Clock = Stopwatch.StartNew();
            public void Report(string path, bool force, bool finished)
            {
                Token.ThrowIfCancellationRequested();
                if (Callback == null) return;
                if (!force && Read - LastReportedBytes < 8L * 1024 * 1024 && Clock.ElapsedMilliseconds < 250) return;
                Callback(new ProgressInfo {
                    Phase = "文件校验", CurrentFile = path, TotalDepots = Depots,
                    Message = finished ? "校验结束" : "正在校验 " + path,
                    Percent = finished ? 100 : (Total == 0 ? 0 : Math.Min(99.9, Read * 100.0 / Total))
                });
                LastReportedBytes = Read;
                Clock.Restart();
            }
        }

        public static VerificationResult Verify(VersionEntry version, string clientDir, bool allowCompatibility,
            CancellationToken token, Action<ProgressInfo> progress)
        {
            token.ThrowIfCancellationRequested();
            var result = new VerificationResult { Errors = new List<string>() };
            List<FileEntry> files;
            try { files = VersionCatalog.ValidateVersion(version); }
            catch (Exception ex) { result.Errors.Add("版本清单无效：" + ex.Message); result.Summary = "校验失败：版本清单无效。"; return result; }
            var fileMap = new Dictionary<string, FileEntry>(VersionCatalog.PathComparer);
            foreach (var file in files) fileMap.Add(file.Path, file);
            var patches = new Dictionary<string, PatchSpec>(VersionCatalog.PathComparer);
            long workBytes = version.TotalBytes;
            if (version.Compatibility != null)
            {
                foreach (var patch in version.Compatibility.Patches)
                {
                    string name = VersionCatalog.NormalizeRelative(patch.Path);
                    patches.Add(name, patch);
                    if (allowCompatibility) workBytes = AddWorkBytes(workBytes, fileMap[name].Size);
                }
                if (allowCompatibility)
                    foreach (var copy in version.Compatibility.Copies)
                        workBytes = AddWorkBytes(workBytes, fileMap[VersionCatalog.NormalizeRelative(copy.Source)].Size);
            }
            var work = new Work { Token = token, Callback = progress, Total = workBytes, Depots = version.Depots.Count };
            work.Report("", true, false);
            var directories = new HashSet<string>(VersionCatalog.PathComparer);
            foreach (string directory in version.Directories) directories.Add(VersionCatalog.NormalizeRelative(directory));
            foreach (var file in files)
            {
                string parent = file.Path;
                while (parent.LastIndexOf('/') >= 0)
                {
                    parent = parent.Substring(0, parent.LastIndexOf('/'));
                    directories.Add(parent);
                }
            }
            foreach (string directory in directories)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    string safe = VersionCatalog.SafePath(clientDir, directory);
                    if (!Directory.Exists(safe)) throw new InvalidDataException("目录缺失或不是目录。");
                }
                catch (Exception ex) { AddError(result, directory + "：" + ex.Message); }
                work.Report(directory, false, false);
            }
            var activePatches = new HashSet<string>(VersionCatalog.PathComparer);
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                result.CheckedFiles++;
                try
                {
                    Probe probe = ReadFile(clientDir, file.Path, file.Size, work);
                    bool original = VersionCatalog.HashEquals(probe.Sha1, file.Sha1) ||
                        (file.Size == 0 && probe.Size == 0 && VersionCatalog.IsZeroHash(file.Sha1));
                    PatchSpec patch;
                    bool knownPatch = !original && allowCompatibility && patches.TryGetValue(file.Path, out patch) &&
                        VersionCatalog.HashEquals(probe.Sha1, patch.PatchedSha1);
                    if (original || knownPatch)
                    {
                        result.VerifiedBytes += file.Size;
                        if (knownPatch) { activePatches.Add(file.Path); result.UsesCompatibility = true; }
                    }
                    else AddError(result, file.Path + "：SHA1 不符合原版清单" + (allowCompatibility ? "或已知兼容补丁。" : "。"));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { AddError(result, file.Path + "：" + ex.Message); }
                work.Report(file.Path, false, false);
            }

            // A partly installed overlay is not accepted. Backups and the private
            // copy are mandatory only when at least one active patched file exists.
            if (activePatches.Count > 0)
            {
                if (activePatches.Count != patches.Count) AddError(result, "ICU 兼容补丁安装不完整，两个目标文件必须同时匹配已知补丁。");
                foreach (var patch in patches.Values)
                {
                    token.ThrowIfCancellationRequested();
                    string relative = VersionCatalog.NormalizeRelative(patch.Path);
                    string backup = VersionCatalog.BackupRoot + Path.GetFileName(relative);
                    try
                    {
                        Probe probe = ReadFile(clientDir, backup, fileMap[relative].Size, work);
                        if (!VersionCatalog.HashEquals(probe.Sha1, patch.OriginalSha1)) AddError(result, backup + "：原版备份 SHA1 不匹配。");
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { AddError(result, backup + "：兼容方案缺少有效的原版备份。" + ex.Message); }
                }
                foreach (var copy in version.Compatibility.Copies)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        Probe probe = ReadFile(clientDir, copy.Target, fileMap[VersionCatalog.NormalizeRelative(copy.Source)].Size, work);
                        if (!VersionCatalog.HashEquals(probe.Sha1, copy.Sha1)) AddError(result, copy.Target + "：私有 ICU 副本 SHA1 不匹配。");
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { AddError(result, copy.Target + "：兼容方案缺少有效的私有副本。" + ex.Message); }
                }
            }
            token.ThrowIfCancellationRequested();
            result.Success = result.Errors.Count == 0 && result.CheckedFiles == files.Count;
            result.Summary = result.Success
                ? (result.UsesCompatibility ? "校验通过：原版文件及已知 ICU 兼容补丁、原版备份和私有副本均匹配；当前目录含兼容修改。" : "校验通过：清单内文件均符合原版，目录完整。")
                : "校验失败：发现 " + result.Errors.Count + " 项文件、目录或兼容方案问题。";
            work.Report("", true, true);
            return result;
        }

        private static Probe ReadFile(string root, string relative, long expectedSize, Work work)
        {
            work.Token.ThrowIfCancellationRequested();
            string full = VersionCatalog.SafePath(root, relative);
            var attributes = File.GetAttributes(full);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) throw new InvalidDataException("不是可读取的普通文件。");
            using (var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, work.Buffer.Length, FileOptions.SequentialScan))
            {
                long size = stream.Length;
                if (size != expectedSize) throw new InvalidDataException("文件大小不匹配，预期 " + expectedSize + " 字节，实际 " + size + " 字节。");
                using (SHA1 sha = SHA1.Create())
                {
                    int read;
                    while ((read = stream.Read(work.Buffer, 0, work.Buffer.Length)) > 0)
                    {
                        work.Token.ThrowIfCancellationRequested();
                        sha.TransformBlock(work.Buffer, 0, read, work.Buffer, 0);
                        work.Read += read;
                        work.Report(relative, false, false);
                    }
                    work.Token.ThrowIfCancellationRequested();
                    sha.TransformFinalBlock(new byte[0], 0, 0);
                    return new Probe { Size = size, Sha1 = BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant() };
                }
            }
        }
        private static void AddError(VerificationResult result, string error) { result.Errors.Add(error); result.FailedFiles++; }
        private static long AddWorkBytes(long a, long b)
        {
            try { return checked(a + b); }
            catch (OverflowException) { throw new InvalidDataException("校验任务大小溢出。"); }
        }
    }
}
