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
    // This is a raw evidence cache, not a patch-to-Build catalogue. Every lookup
    // runs the production official-evidence parser again. Its result is still a
    // candidate: LocalArchiveResolver must prove first release on the public chain.
    public sealed class LocalPatchEvidenceStore
    {
        private const int MaximumResourceBytes = 64 * 1024 * 1024;
        private const string PatchListingUrl = "https://steamdb.info/app/570/patchnotes/";
        private readonly string bundledDirectory, writableDirectory;
        private readonly object writeGate = new object();

        public LocalPatchEvidenceStore(string directory, string writableDirectory = null)
        {
            bundledDirectory = String.IsNullOrWhiteSpace(directory) ? null : Path.GetFullPath(directory);
            this.writableDirectory = String.IsNullOrWhiteSpace(writableDirectory) ? null : Path.GetFullPath(writableDirectory);
        }

        public async Task<OfficialPatchMatch> ResolveAsync(string patch, CancellationToken token, Action<string> progress)
        {
            patch = (patch ?? "").Trim().ToLowerInvariant();
            if (!Regex.IsMatch(patch, @"^[0-9]+\.[0-9]{2}[a-z]?$")) throw new HistoryResolutionException("请输入完整补丁号，例如 7.00 或 7.22b。");
            // Validate envelopes before entering the evidence-miss handler, so a
            // damaged local cache cannot quietly turn into an online lookup.
            Dictionary<string, Resource> bundled = await Task.Run(() => LoadDirectory(bundledDirectory, token), token);
            Dictionary<string, Resource> saved = await Task.Run(() => LoadDirectory(writableDirectory, token), token);
            var combined = new Dictionary<string, Resource>(bundled, StringComparer.Ordinal);
            foreach (var item in saved) combined[item.Key] = item.Value;
            OfficialPatchMatch result = await ReplayAsync(patch, combined, token, progress);
            if (result == null && saved.Count > 0 && bundled.Count > 0)
                result = await ReplayAsync(patch, bundled, token, progress);
            if (result != null && progress != null) progress("已从本地原始官方公告重新核实补丁 " + patch + " 的候选 Build " + result.Build.BuildId + "，继续核对完整公开分支历史。");
            return result;
        }

        public IHistoryBrowser CreateRecordingBrowser(IHistoryBrowser browser)
        {
            if (browser == null) throw new ArgumentNullException("browser");
            var previous = browser as RecordingBrowser;
            if (previous != null && Object.ReferenceEquals(previous.Owner, this)) return browser;
            if (browser is IHistoryDateBrowser && browser is IHistoryDateNavigationBrowser) return new RecordingDateNavigationBrowser(this, browser);
            if (browser is IHistoryDateBrowser) return new RecordingDateBrowser(this, browser);
            if (browser is IHistoryDateNavigationBrowser) return new RecordingNavigationBrowser(this, browser);
            return new RecordingBrowser(this, browser);
        }

        public OfficialPatchResolver CreateRecordingResolver(IHistoryBrowser browser)
        {
            return new OfficialPatchResolver(CreateRecordingBrowser(browser), async delegate(string url, CancellationToken token) {
                string body = await OfficialPatchResolver.FetchOfficialAsync(url, token);
                token.ThrowIfCancellationRequested();
                SaveOfficialResponse(url, body, DateTimeOffset.UtcNow);
                return body;
            });
        }

        public void SaveOfficialResponse(string url, string body, DateTimeOffset capturedAtUtc)
        {
            if (!IsOfficialUrl(url)) throw new HistoryResolutionException("本地补丁证据只接受 Valve 官方公开来源。");
            Save(new Resource { Kind = "OfficialResponse", Url = url, CapturedAtUtc = capturedAtUtc.ToUniversalTime().ToString("o"), Payload = body });
        }

        public void SaveBrowserSnapshot(HistoryPageSnapshot snapshot)
        {
            if (snapshot == null || !IsPatchPageUrl(snapshot.Url)) return;
            if (snapshot.Html == null) throw new HistoryResolutionException("补丁页面证据缺少正文。");
            var payload = new SnapshotPayload { Url = snapshot.Url, Title = snapshot.Title, Html = snapshot.Html, Text = snapshot.Text,
                CapturedAtUtc = snapshot.CapturedAtUtc.ToUniversalTime().ToString("o") };
            Save(new Resource { Kind = "BrowserSnapshot", Url = snapshot.Url, CapturedAtUtc = payload.CapturedAtUtc, Payload = Json().Serialize(payload) });
        }

        private async Task<OfficialPatchMatch> ReplayAsync(string patch, Dictionary<string, Resource> resources, CancellationToken token, Action<string> progress)
        {
            Resource listing;
            if (!resources.TryGetValue(PatchListingUrl, out listing)) return null;
            var browser = new EvidenceBrowser(resources);
            IList<HistoryBuildEvent> builds = await Task.Run(() => new SteamDbHistoryParser().ParsePatchListing(ToSnapshot(listing)).Builds, token);
            if (builds.Count == 0) return null;
            var resolver = new OfficialPatchResolver(browser, delegate(string url, CancellationToken cancellation) {
                cancellation.ThrowIfCancellationRequested();
                Resource found;
                if (!resources.TryGetValue(url, out found) || found.Kind != "OfficialResponse") throw new EvidenceMissingException();
                return Task.FromResult(found.Payload);
            });
            try {
                return await resolver.ResolveAsync(patch, builds, token, null);
            }
            catch (OperationCanceledException) { throw; }
            catch (EvidenceMissingException) { return null; }
            catch (HistoryResolutionException ex) {
                if (progress != null) progress("本地官方证据尚不能证明补丁 " + patch + " 的首发关联：" + ex.Message);
                return null;
            }
        }

        private static Dictionary<string, Resource> LoadDirectory(string directory, CancellationToken token)
        {
            var result = new Dictionary<string, Resource>(StringComparer.Ordinal);
            if (directory == null || !Directory.Exists(directory)) return result;
            string[] files = Directory.GetFiles(directory, "resource-*.json", SearchOption.TopDirectoryOnly);
            if (files.Length > 10000) throw new HistoryResolutionException("本地官方证据文件数量超过读取上限。");
            foreach (string path in files) {
                token.ThrowIfCancellationRequested();
                if (new FileInfo(path).Length > MaximumResourceBytes) throw new HistoryResolutionException("本地官方证据文件超过读取上限。");
                Resource resource;
                try { resource = Json().Deserialize<Resource>(File.ReadAllText(path, Encoding.UTF8)); }
                catch (Exception ex) { throw new HistoryResolutionException("本地官方证据格式损坏：" + Path.GetFileName(path) + "；" + ex.Message); }
                Validate(resource);
                if (!String.Equals(Path.GetFileName(path), FileName(resource.Url), StringComparison.OrdinalIgnoreCase)) throw new HistoryResolutionException("本地官方证据来源与文件名不一致。");
                if (result.ContainsKey(resource.Url)) throw new HistoryResolutionException("本地官方证据包含重复来源。");
                if (resource.Kind == "BrowserSnapshot") ToSnapshot(resource);
                result.Add(resource.Url, resource);
            }
            return result;
        }

        private void Save(Resource resource)
        {
            if (writableDirectory == null) return;
            resource.SchemaVersion = 1;
            resource.PayloadSha256 = Hash(resource.Payload ?? "");
            Validate(resource);
            string serialized = Json().Serialize(resource);
            if (Encoding.UTF8.GetByteCount(serialized) > MaximumResourceBytes) throw new HistoryResolutionException("补丁证据超过保存上限。");
            lock (writeGate) {
                Directory.CreateDirectory(writableDirectory);
                string path = Path.Combine(writableDirectory, FileName(resource.Url));
                string temporary = Path.Combine(writableDirectory, "write-" + Guid.NewGuid().ToString("N") + ".tmp");
                File.WriteAllText(temporary, serialized, new UTF8Encoding(false));
                try { if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path); }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }

        private static void Validate(Resource resource)
        {
            DateTimeOffset captured;
            if (resource == null || resource.SchemaVersion != 1 || String.IsNullOrEmpty(resource.Payload) || resource.Payload.Length > MaximumResourceBytes ||
                !DateTimeOffset.TryParse(resource.CapturedAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out captured) ||
                !String.Equals(resource.PayloadSha256, Hash(resource.Payload), StringComparison.OrdinalIgnoreCase) ||
                !(resource.Kind == "OfficialResponse" && IsOfficialUrl(resource.Url) || resource.Kind == "BrowserSnapshot" && IsPatchPageUrl(resource.Url)))
                throw new HistoryResolutionException("本地补丁原始证据的来源、内容校验或格式不完整，已停止复用。");
        }

        private static HistoryPageSnapshot ToSnapshot(Resource resource)
        {
            if (resource == null || resource.Kind != "BrowserSnapshot") throw new HistoryResolutionException("本地补丁页面证据类型不正确。");
            SnapshotPayload value;
            try { value = Json().Deserialize<SnapshotPayload>(resource.Payload); }
            catch (Exception ex) { throw new HistoryResolutionException("本地补丁页面证据格式损坏：" + ex.Message); }
            DateTimeOffset captured;
            if (value == null || value.Url != resource.Url || String.IsNullOrEmpty(value.Html) ||
                !DateTimeOffset.TryParse(value.CapturedAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out captured))
                throw new HistoryResolutionException("本地补丁页面证据缺少可核实的来源和正文。");
            return new HistoryPageSnapshot { Url = value.Url, Title = value.Title, Html = value.Html, Text = value.Text, CapturedAtUtc = captured };
        }

        private static bool IsOfficialUrl(string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0 || !uri.IsDefaultPort || uri.Fragment.Length != 0) return false;
            if (url == OfficialPatchResolver.PatchListUrl) return true;
            if (url == OfficialPatchResolver.NewsUrl || url == OfficialPatchResolver.UpdatesUrl) return true;
            if (Regex.IsMatch(url, "^" + Regex.Escape(OfficialPatchResolver.NewsUrl) + @"&enddate=[0-9]+$") || Regex.IsMatch(url, "^" + Regex.Escape(OfficialPatchResolver.UpdatesUrl) + @"&enddate=[0-9]+$")) return true;
            return OfficialPatchResolver.OfficialNewsId(url) != null;
        }

        private static bool IsPatchPageUrl(string url)
        {
            return url == PatchListingUrl || Regex.IsMatch(url ?? "", @"^https://steamdb\.info/patchnotes/[1-9][0-9]*/$");
        }
        private static string FileName(string url) { return "resource-" + Hash(url) + ".json"; }
        private static string Hash(string value) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant(); }
        private static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = MaximumResourceBytes, RecursionLimit = 128 }; }

        public sealed class Resource
        {
            public int SchemaVersion { get; set; }
            public string Kind { get; set; }
            public string Url { get; set; }
            public string CapturedAtUtc { get; set; }
            public string Payload { get; set; }
            public string PayloadSha256 { get; set; }
        }
        public sealed class SnapshotPayload
        {
            public string Url { get; set; }
            public string Title { get; set; }
            public string Html { get; set; }
            public string Text { get; set; }
            public string CapturedAtUtc { get; set; }
        }
        private sealed class EvidenceMissingException : Exception { }
        private sealed class EvidenceBrowser : IHistoryBrowser
        {
            private readonly Dictionary<string, Resource> resources;
            internal EvidenceBrowser(Dictionary<string, Resource> resources) { this.resources = resources; }
            public Task<HistoryPageSnapshot> NavigateAsync(string url, CancellationToken token) {
                token.ThrowIfCancellationRequested(); Resource value;
                if (!resources.TryGetValue(url, out value)) throw new EvidenceMissingException();
                return Task.FromResult(ToSnapshot(value));
            }
            public Task<HistoryPageSnapshot> CaptureAsync(CancellationToken token) { throw new EvidenceMissingException(); }
            public Task<HistoryPageSnapshot> ClickAsync(string selector, CancellationToken token) { throw new EvidenceMissingException(); }
        }
        private class RecordingBrowser : IHistoryBrowser
        {
            internal readonly LocalPatchEvidenceStore Owner;
            protected readonly IHistoryBrowser inner;
            internal RecordingBrowser(LocalPatchEvidenceStore owner, IHistoryBrowser inner) { Owner = owner; this.inner = inner; }
            public async Task<HistoryPageSnapshot> NavigateAsync(string url, CancellationToken token) { HistoryPageSnapshot page = await inner.NavigateAsync(url, token); Owner.SaveBrowserSnapshot(page); return page; }
            public async Task<HistoryPageSnapshot> CaptureAsync(CancellationToken token) { HistoryPageSnapshot page = await inner.CaptureAsync(token); Owner.SaveBrowserSnapshot(page); return page; }
            public async Task<HistoryPageSnapshot> ClickAsync(string selector, CancellationToken token) { HistoryPageSnapshot page = await inner.ClickAsync(selector, token); Owner.SaveBrowserSnapshot(page); return page; }
        }
        private class RecordingDateBrowser : RecordingBrowser, IHistoryDateBrowser
        {
            internal RecordingDateBrowser(LocalPatchEvidenceStore owner, IHistoryBrowser inner) : base(owner, inner) { }
            public Task<HistoryPageSnapshot> LoadHistoryBeforeAsync(DateTime date, CancellationToken token) {
                return ((IHistoryDateBrowser)inner).LoadHistoryBeforeAsync(date, token);
            }
        }
        private sealed class RecordingNavigationBrowser : RecordingBrowser, IHistoryDateNavigationBrowser
        {
            internal RecordingNavigationBrowser(LocalPatchEvidenceStore owner, IHistoryBrowser inner) : base(owner, inner) { }
            public Task OpenHistoryForDateAsync(CancellationToken token) { return ((IHistoryDateNavigationBrowser)inner).OpenHistoryForDateAsync(token); }
        }
        private sealed class RecordingDateNavigationBrowser : RecordingDateBrowser, IHistoryDateNavigationBrowser
        {
            internal RecordingDateNavigationBrowser(LocalPatchEvidenceStore owner, IHistoryBrowser inner) : base(owner, inner) { }
            public Task OpenHistoryForDateAsync(CancellationToken token) {
                return ((IHistoryDateNavigationBrowser)inner).OpenHistoryForDateAsync(token);
            }
        }
    }
}
