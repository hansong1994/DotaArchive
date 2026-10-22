using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace DotaArchive
{
    // Opt-in local development diagnostics. Normal launches do not record pages.
    internal sealed class DiagnosticHistoryBrowser : IHistoryBrowser, IHistoryDateBrowser, IHistoryDateNavigationBrowser
    {
        private readonly IHistoryBrowser inner;
        private readonly string directory;
        internal DiagnosticHistoryBrowser(IHistoryBrowser inner, string directory)
        {
            this.inner = inner;
            this.directory = String.IsNullOrWhiteSpace(directory) ? null : Path.GetFullPath(directory);
        }
        public async Task<HistoryPageSnapshot> NavigateAsync(string url, CancellationToken token) { return await Save(await inner.NavigateAsync(url, token), token); }
        public async Task<HistoryPageSnapshot> CaptureAsync(CancellationToken token) { return await Save(await inner.CaptureAsync(token), token); }
        public async Task<HistoryPageSnapshot> ClickAsync(string selector, CancellationToken token) { return await Save(await inner.ClickAsync(selector, token), token); }
        public async Task OpenHistoryForDateAsync(CancellationToken token)
        {
            IHistoryDateNavigationBrowser navigation = inner as IHistoryDateNavigationBrowser;
            if(navigation != null) await navigation.OpenHistoryForDateAsync(token);
            else await inner.NavigateAsync("https://steamdb.info/app/570/history/?filterkey=2", token);
        }
        public async Task<HistoryPageSnapshot> LoadHistoryBeforeAsync(DateTime date, CancellationToken token)
        {
            IHistoryDateBrowser dated = inner as IHistoryDateBrowser;
            if(dated == null) throw new InvalidOperationException("浏览器不支持历史页面的日期筛选。");
            DateTimeOffset started = DateTimeOffset.UtcNow;
            HistoryPageSnapshot page = await Save(await dated.LoadHistoryBeforeAsync(date, token), token);
            if(directory != null) {
                // This is the public date control's calendar value, not a claimed UTC
                // midnight cutoff. No login/session information is recorded.
                string line = new JavaScriptSerializer().Serialize(new {
                    CalendarDate = date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                    StartedAtUtc = started.ToString("o"), CompletedAtUtc = DateTimeOffset.UtcNow.ToString("o"),
                    SourceUrl = page.Url
                });
                await Task.Run(delegate { File.AppendAllText(Path.Combine(directory, "history-date-requests.jsonl"), line + Environment.NewLine, new UTF8Encoding(false)); }, token);
            }
            return page;
        }
        private async Task<HistoryPageSnapshot> Save(HistoryPageSnapshot page, CancellationToken token)
        {
            if(directory == null) return page;
            Uri uri;
            if(!Uri.TryCreate(page.Url, UriKind.Absolute, out uri) || uri.Host != "steamdb.info" ||
               !(uri.AbsolutePath.StartsWith("/app/570/", StringComparison.Ordinal) || uri.AbsolutePath.StartsWith("/patchnotes/", StringComparison.Ordinal))) return page;
            await Task.Run(delegate {
                token.ThrowIfCancellationRequested();
                Directory.CreateDirectory(directory);
                string kind = uri.AbsolutePath.Contains("/history/") ? "history" : uri.AbsolutePath.Contains("/depots/") ? "baseline" : "patchnotes";
                string path = Path.Combine(directory, "latest-" + kind + ".json");
                string temporary = path + ".tmp";
                string json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Serialize(page);
                File.WriteAllText(temporary, json, new UTF8Encoding(false));
                if(File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
                // Preserve public sanitized batches for replaying parser regressions.
                // These opt-in diagnostics are never consulted by the online resolver.
                string digest;
                using(var sha = SHA256.Create()) digest = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(page.Html ?? ""))).Replace("-", "").Substring(0, 20);
                string archive = Path.Combine(directory, "batches");
                Directory.CreateDirectory(archive);
                string batch = Path.Combine(archive, kind + "-" + digest + ".json");
                if(!File.Exists(batch)) File.WriteAllText(batch, json, new UTF8Encoding(false));
            }, token);
            return page;
        }
    }
}
