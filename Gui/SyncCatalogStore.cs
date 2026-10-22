using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace DotaArchive
{
    public sealed class SyncCatalogSnapshot
    {
        public int AddedRecords { get; internal set; }
        public int TotalRecords { get; internal set; }
        public string LatestBuildId { get; internal set; }
        public string LatestChangeId { get; internal set; }
        public string AnchorIdentity { get; internal set; }
        public string GenerationDirectory { get; internal set; }
        public DateTimeOffset LatestBuildPublishedAtUtc { get; internal set; }
        public DateTimeOffset LatestChangeObservedAtUtc { get; internal set; }
        public DateTimeOffset VerifiedThroughUtc { get; internal set; }
        public DateTimeOffset FirstBaselineCapturedAtUtc { get; internal set; }
        internal List<HistoryAnchorStore.Anchor> Anchors { get; set; }
    }

    // A complete generation is immutable after publication. current.json is the
    // only commit point: interrupted runs and checkpoints never extend coverage.
    public sealed class SyncCatalogStore
    {
        private const string BaselineUrl = "https://steamdb.info/app/570/depots/?branch=public";
        private const int MaximumManifestBytes = 64 * 1024;
        public string RootDirectory { get; private set; }
        public SyncCatalogStore(string syncRootDirectory)
        {
            if (String.IsNullOrWhiteSpace(syncRootDirectory)) throw new ArgumentException("需要指定同步数据库目录。");
            RootDirectory = Path.GetFullPath(syncRootDirectory);
            if (RootDirectory.Length > Path.GetPathRoot(RootDirectory).Length)
                RootDirectory = RootDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        public SyncCatalogSnapshot LoadActive(CancellationToken token = default(CancellationToken))
        {
            token.ThrowIfCancellationRequested();
            string current = Path.Combine(RootDirectory, "current.json");
            if (!File.Exists(current)) return null;
            Current pointer = Read<Current>(current, MaximumManifestBytes);
            if (pointer == null || pointer.SchemaVersion != 1 || pointer.AppId != "570" || pointer.Branch != "public" || pointer.Status != "Completed" ||
                !Regex.IsMatch(pointer.Generation ?? "", @"^[a-f0-9]{32}$") || !IsHash(pointer.ManifestSha256))
                throw new HistoryResolutionException("同步数据库发布指针损坏或尚未完整提交。");
            string generation = GenerationPath(pointer.Generation);
            string manifestPath = Path.Combine(generation, "manifest.json");
            RequireHash(manifestPath, pointer.ManifestSha256, MaximumManifestBytes);
            return ValidateGeneration(generation, Read<Manifest>(manifestPath, MaximumManifestBytes), token);
        }

        internal SyncCatalogSnapshot Publish(string generationDirectory, HistoryPageSnapshot firstBaseline,
            HistoryPageSnapshot confirmedBaseline, DateTimeOffset verifiedThroughUtc, int addedRecords, CancellationToken token = default(CancellationToken))
        {
            token.ThrowIfCancellationRequested();
            string generation = Path.GetFullPath(generationDirectory);
            string name = new DirectoryInfo(generation).Name;
            if (!Regex.IsMatch(name, @"^[a-f0-9]{32}$") || !String.Equals(generation, GenerationPath(name), StringComparison.OrdinalIgnoreCase))
                throw new HistoryResolutionException("同步数据库只能发布独立 generations/GUID 目录。");
            if (File.Exists(Path.Combine(generation, "manifest.json"))) throw new HistoryResolutionException("已经写入发布清单的同步代次不能再次修改。");
            HistoryAnchorStore.Anchor anchor = ReadConsistentAnchor(generation);
            if (addedRecords < 0 || addedRecords > anchor.Changes.Count) throw new HistoryResolutionException("同步新增记录数量无效。");
            ValidateBaselineSnapshots(anchor, firstBaseline, confirmedBaseline, verifiedThroughUtc);
            ValidateReplay(anchor, token);
            WriteSnapshot(Path.Combine(generation, "baseline-first.json"), firstBaseline);
            WriteSnapshot(Path.Combine(generation, "baseline-confirmed.json"), confirmedBaseline);
            var manifest = new Manifest {
                SchemaVersion = 1, AppId = "570", Branch = "public", Status = "Completed", FullChainVerified = true,
                Generation = name, AnchorIdentity = anchor.Identity, AddedRecords = addedRecords, TotalRecords = anchor.Changes.Count,
                LatestBuildId = anchor.Baseline.BuildId, LatestBuildPublishedAtUtc = Time(anchor.Baseline.PublishedAtUtc),
                LatestChangeId = anchor.Changes[0].ChangeId, LatestChangeObservedAtUtc = Time(anchor.Changes[0].ObservedAtUtc),
                FirstBaselineCapturedAtUtc = Time(firstBaseline.CapturedAtUtc), ConfirmedBaselineCapturedAtUtc = Time(confirmedBaseline.CapturedAtUtc),
                VerifiedThroughUtc = Time(verifiedThroughUtc), OldestChangeId = anchor.Changes[anchor.Changes.Count - 1].ChangeId,
                CompletedAtUtc = Time(DateTimeOffset.UtcNow),
                HistorySha256 = FileHash(Path.Combine(generation, "verified-public-history-v1.json")),
                AnchorsSha256 = FileHash(Path.Combine(generation, "verified-public-anchors-v2.json")),
                FirstBaselineSha256 = FileHash(Path.Combine(generation, "baseline-first.json")),
                ConfirmedBaselineSha256 = FileHash(Path.Combine(generation, "baseline-confirmed.json"))
            };
            string manifestPath = Path.Combine(generation, "manifest.json");
            AtomicWrite(manifestPath, Json().Serialize(manifest));
            // Re-read the exact bytes that current will publish, rather than
            // treating in-memory success as proof that all files were committed.
            SyncCatalogSnapshot result = ValidateGeneration(generation, manifest, token);
            token.ThrowIfCancellationRequested();
            var pointer = new Current { SchemaVersion = 1, AppId = "570", Branch = "public", Status = "Completed", Generation = name, ManifestSha256 = FileHash(manifestPath) };
            AtomicWrite(Path.Combine(RootDirectory, "current.json"), Json().Serialize(pointer));
            return result;
        }

        private SyncCatalogSnapshot ValidateGeneration(string generation, Manifest manifest, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (manifest == null || manifest.SchemaVersion != 1 || manifest.AppId != "570" || manifest.Branch != "public" || manifest.Status != "Completed" || !manifest.FullChainVerified ||
                manifest.Generation != new DirectoryInfo(generation).Name || !IsHash(manifest.AnchorIdentity))
                throw new HistoryResolutionException("同步数据库代次没有通过完整发布校验。");
            RequireHash(Path.Combine(generation, "verified-public-history-v1.json"), manifest.HistorySha256, HistoryCacheStore.MaximumBytes);
            RequireHash(Path.Combine(generation, "verified-public-anchors-v2.json"), manifest.AnchorsSha256, HistoryAnchorStore.MaximumBytes);
            RequireHash(Path.Combine(generation, "baseline-first.json"), manifest.FirstBaselineSha256, HistoryCacheStore.MaximumBytes);
            RequireHash(Path.Combine(generation, "baseline-confirmed.json"), manifest.ConfirmedBaselineSha256, HistoryCacheStore.MaximumBytes);
            HistoryAnchorStore.Anchor anchor = ReadConsistentAnchor(generation);
            token.ThrowIfCancellationRequested();
            HistoryPageSnapshot first = ReadSnapshot(Path.Combine(generation, "baseline-first.json"));
            HistoryPageSnapshot confirmed = ReadSnapshot(Path.Combine(generation, "baseline-confirmed.json"));
            DateTimeOffset through = ParseTime(manifest.VerifiedThroughUtc);
            DateTimeOffset completed = ParseTime(manifest.CompletedAtUtc);
            if (manifest.AnchorIdentity != anchor.Identity || manifest.TotalRecords != anchor.Changes.Count || manifest.AddedRecords < 0 || manifest.AddedRecords > manifest.TotalRecords ||
                manifest.LatestBuildId != anchor.Baseline.BuildId || ParseTime(manifest.LatestBuildPublishedAtUtc) != anchor.Baseline.PublishedAtUtc ||
                manifest.LatestChangeId != anchor.Changes[0].ChangeId || ParseTime(manifest.LatestChangeObservedAtUtc) != anchor.Changes[0].ObservedAtUtc ||
                manifest.OldestChangeId != anchor.Changes[anchor.Changes.Count - 1].ChangeId ||
                ParseTime(manifest.FirstBaselineCapturedAtUtc) != first.CapturedAtUtc || ParseTime(manifest.ConfirmedBaselineCapturedAtUtc) != confirmed.CapturedAtUtc ||
                completed < confirmed.CapturedAtUtc || completed > DateTimeOffset.UtcNow.AddMinutes(1))
                throw new HistoryResolutionException("同步数据库记录数量、身份或发布时间边界不一致。");
            ValidateBaselineSnapshots(anchor, first, confirmed, through);
            ValidateReplay(anchor, token);
            return new SyncCatalogSnapshot { AddedRecords = manifest.AddedRecords, TotalRecords = anchor.Changes.Count,
                LatestBuildId = anchor.Baseline.BuildId, LatestChangeId = anchor.Changes[0].ChangeId,
                LatestBuildPublishedAtUtc = anchor.Baseline.PublishedAtUtc, LatestChangeObservedAtUtc = anchor.Changes[0].ObservedAtUtc,
                AnchorIdentity = anchor.Identity, GenerationDirectory = generation, VerifiedThroughUtc = through,
                FirstBaselineCapturedAtUtc = first.CapturedAtUtc, Anchors = new List<HistoryAnchorStore.Anchor> { anchor } };
        }

        private static HistoryAnchorStore.Anchor ReadConsistentAnchor(string directory)
        {
            List<HistoryAnchorStore.Anchor> anchors = new HistoryAnchorStore(directory).Load();
            HistoryCacheStore.CacheData history = new HistoryCacheStore(directory).Load();
            if (anchors.Count != 1 || history == null || history.Changes.Count == 0 || anchors[0].Identity != HistoryAnchorStore.Identity(history.Baseline) ||
                !HaveIdenticalChanges(anchors[0].Changes, history.Changes))
                throw new HistoryResolutionException("同步数据库的两份完整历史证据不一致。");
            return anchors[0];
        }

        // Compare the complete parsed evidence rather than reflection-serializing
        // DateTimeOffset and its derived properties. File and payload hashes are
        // checked independently above; no timestamp, delta, or record is omitted.
        internal static bool HaveIdenticalChanges(IList<HistoryChange> left, IList<HistoryChange> right)
        {
            if (left == null || right == null || left.Count != right.Count) return false;
            for (int index = 0; index < left.Count; index++)
            {
                HistoryChange a = left[index], b = right[index];
                if (a == null || b == null || a.ChangeId != b.ChangeId || a.ObservedAtUtc != b.ObservedAtUtc || a.SourceUrl != b.SourceUrl ||
                    a.ParsedAllDepotChanges != b.ParsedAllDepotChanges || a.Deltas == null || b.Deltas == null || a.Deltas.Count != b.Deltas.Count ||
                    a.UnparsedChanges == null || b.UnparsedChanges == null || !a.UnparsedChanges.SequenceEqual(b.UnparsedChanges, StringComparer.Ordinal)) return false;
                HistoryBuildEvent x = a.Publication, y = b.Publication;
                if ((x == null) != (y == null)) return false;
                if (x != null && (x.BuildId != y.BuildId || x.PublishedAtUtc != y.PublishedAtUtc || x.SourceUrl != y.SourceUrl || x.ChangeId != y.ChangeId ||
                    x.Title != y.Title || x.PreviousBuildId != y.PreviousBuildId || x.PreviousPublishedAtUtc != y.PreviousPublishedAtUtc ||
                    x.PreviousUrl != y.PreviousUrl || x.NextUrl != y.NextUrl || x.IsPublic != y.IsPublic)) return false;
                for (int deltaIndex = 0; deltaIndex < a.Deltas.Count; deltaIndex++)
                {
                    HistoryDelta old = a.Deltas[deltaIndex], current = b.Deltas[deltaIndex];
                    if (old == null || current == null || old.Path != current.Path || old.OldExists != current.OldExists || old.OldValue != current.OldValue ||
                        old.NewExists != current.NewExists || old.NewValue != current.NewValue) return false;
                }
            }
            return true;
        }

        private static void ValidateBaselineSnapshots(HistoryAnchorStore.Anchor anchor, HistoryPageSnapshot first, HistoryPageSnapshot confirmed, DateTimeOffset through)
        {
            if (first == null || confirmed == null || first.Url != BaselineUrl || confirmed.Url != BaselineUrl ||
                first.CapturedAtUtc < anchor.Baseline.PublishedAtUtc || confirmed.CapturedAtUtc < first.CapturedAtUtc || confirmed.CapturedAtUtc > DateTimeOffset.UtcNow.AddMinutes(1) ||
                through < anchor.Baseline.PublishedAtUtc || through > first.CapturedAtUtc)
                throw new HistoryResolutionException("同步覆盖时间必须位于公开构建发布时间与首次完整基线采集时间之间。");
            var parser = new SteamDbHistoryParser();
            if (HistoryAnchorStore.Identity(parser.ParseBaseline(first)) != anchor.Identity || HistoryAnchorStore.Identity(parser.ParseBaseline(confirmed)) != anchor.Identity)
                throw new HistoryResolutionException("同步前后完整网页基线与发布锚点不一致，不能扩展离线时间范围。");
        }

        private static void ValidateReplay(HistoryAnchorStore.Anchor anchor, CancellationToken token)
        { new OnlineHistoryResolver(new OfflineBrowser(), null, null, false).ValidateAnchorAsync(anchor, token).GetAwaiter().GetResult(); }

        private string GenerationPath(string name)
        {
            string path = Path.GetFullPath(Path.Combine(RootDirectory, "generations", name));
            string root = RootDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new HistoryResolutionException("同步数据库代次目录越界。");
            // A junction inside the expected lexical path must not redirect the
            // database to a checkpoint, another profile, or another volume.
            string current = path;
            while (!String.Equals(current, RootDirectory, StringComparison.OrdinalIgnoreCase)) {
                if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new HistoryResolutionException("同步数据库代次不能通过目录链接重定向。");
                current = Path.GetDirectoryName(current);
            }
            return path;
        }

        private static void WriteSnapshot(string path, HistoryPageSnapshot snapshot)
        {
            AtomicWrite(path, Json().Serialize(new Snapshot { Url = snapshot.Url, Title = snapshot.Title, Html = snapshot.Html, Text = snapshot.Text, CapturedAtUtc = Time(snapshot.CapturedAtUtc) }));
        }
        private static HistoryPageSnapshot ReadSnapshot(string path)
        {
            Snapshot value = Read<Snapshot>(path, HistoryCacheStore.MaximumBytes);
            if (value == null || String.IsNullOrWhiteSpace(value.Html)) throw new HistoryResolutionException("同步数据库缺少原始基线网页。");
            return new HistoryPageSnapshot { Url = value.Url, Title = value.Title, Html = value.Html, Text = value.Text, CapturedAtUtc = ParseTime(value.CapturedAtUtc) };
        }
        private static DateTimeOffset ParseTime(string value)
        {
            DateTimeOffset result;
            if (String.IsNullOrEmpty(value) || !DateTimeOffset.TryParseExact(value, "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out result) || result.Offset != TimeSpan.Zero)
                throw new HistoryResolutionException("同步数据库的 UTC 时间格式无效。");
            return result;
        }
        private static string Time(DateTimeOffset value) { return value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture); }
        private static bool IsHash(string value) { return Regex.IsMatch(value ?? "", @"^[a-f0-9]{64}$"); }
        private static void RequireHash(string path, string expected, long maximum)
        {
            if (!IsHash(expected) || !File.Exists(path) || new FileInfo(path).Length <= 0 || new FileInfo(path).Length > maximum || FileHash(path) != expected)
                throw new HistoryResolutionException("同步数据库原始证据内容校验失败：" + Path.GetFileName(path));
        }
        private static string FileHash(string path)
        { using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
        private static T Read<T>(string path, long maximum)
        {
            try {
                if (!File.Exists(path) || new FileInfo(path).Length <= 0 || new FileInfo(path).Length > maximum) throw new IOException("文件缺失或大小超限");
                return Json().Deserialize<T>(File.ReadAllText(path, Encoding.UTF8));
            } catch (Exception ex) { throw new HistoryResolutionException("同步数据库文件无法读取：" + Path.GetFileName(path) + "；" + ex.Message); }
        }
        private static void AtomicWrite(string path, string value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = Path.Combine(Path.GetDirectoryName(path), "write-" + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(temp, value, new UTF8Encoding(false));
            try { if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        private static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = HistoryAnchorStore.MaximumBytes, RecursionLimit = 128 }; }

        public sealed class Current
        {
            public int SchemaVersion { get; set; }
            public string AppId { get; set; }
            public string Branch { get; set; }
            public string Status { get; set; }
            public string Generation { get; set; }
            public string ManifestSha256 { get; set; }
        }
        public sealed class Manifest
        {
            public int SchemaVersion { get; set; }
            public string AppId { get; set; }
            public string Branch { get; set; }
            public string Status { get; set; }
            public bool FullChainVerified { get; set; }
            public string Generation { get; set; }
            public string AnchorIdentity { get; set; }
            public int AddedRecords { get; set; }
            public int TotalRecords { get; set; }
            public string LatestBuildId { get; set; }
            public string LatestBuildPublishedAtUtc { get; set; }
            public string LatestChangeId { get; set; }
            public string LatestChangeObservedAtUtc { get; set; }
            public string OldestChangeId { get; set; }
            public string VerifiedThroughUtc { get; set; }
            public string FirstBaselineCapturedAtUtc { get; set; }
            public string ConfirmedBaselineCapturedAtUtc { get; set; }
            public string CompletedAtUtc { get; set; }
            public string HistorySha256 { get; set; }
            public string AnchorsSha256 { get; set; }
            public string FirstBaselineSha256 { get; set; }
            public string ConfirmedBaselineSha256 { get; set; }
        }
        public sealed class Snapshot
        {
            public string Url { get; set; }
            public string Title { get; set; }
            public string Html { get; set; }
            public string Text { get; set; }
            public string CapturedAtUtc { get; set; }
        }
        private sealed class OfflineBrowser : IHistoryBrowser
        {
            public Task<HistoryPageSnapshot> NavigateAsync(string url, CancellationToken token) { throw new InvalidOperationException("同步数据库离线校验禁止访问网页。"); }
            public Task<HistoryPageSnapshot> CaptureAsync(CancellationToken token) { throw new InvalidOperationException("同步数据库离线校验禁止访问网页。"); }
            public Task<HistoryPageSnapshot> ClickAsync(string selector, CancellationToken token) { throw new InvalidOperationException("同步数据库离线校验禁止访问网页。"); }
        }
    }
}
