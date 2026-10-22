using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]

namespace DotaArchive
{
    internal static class HistoryArchiveProgram
    {
        [STAThread]
        private static void Main(string[] args)
        {
            if (args.Length != 4) throw new ArgumentException("Expected: cache-directory output-directory lower-inclusive-UTC upper-exclusive-UTC");
            var lower = DateTimeOffset.Parse(args[2], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime();
            var upper = DateTimeOffset.Parse(args[3], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime();
            if (lower >= upper) throw new ArgumentException("The archive range must be increasing.");
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new HistoryArchiveForm(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]), lower, upper));
        }
    }

    internal sealed class HistoryArchiveForm : Form
    {
        private readonly BrowserSession session;
        private readonly string cacheDirectory, outputDirectory;
        private readonly DateTimeOffset lower, upper;
        private readonly TextBox log;
        private readonly Label browserStatus, progressStatus;
        private readonly Button start, cancel;
        private CancellationTokenSource cancellation;
        private bool closeAfterSave;
        private string phase = "Ready", latestMessage = "", browserMessage = "", failure;
        private HistoryArchiveResult result;
        private FileStream taskLock;

        internal HistoryArchiveForm(string cache, string output, DateTimeOffset from, DateTimeOffset until)
        {
            cacheDirectory = cache; outputDirectory = output; lower = from; upper = until;
            Directory.CreateDirectory(outputDirectory);
            Directory.CreateDirectory(cacheDirectory);
            taskLock = new FileStream(Path.Combine(cacheDirectory, "archive-task.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            Text = "Dota 历史记录归档 · 2013—2015";
            Width = 1280; Height = 920; MinimumSize = new Size(900, 650);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9F);
            session = new BrowserSession();
            var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48, Padding = new Padding(6) };
            start = new Button { Text = "从已保存断点继续", AutoSize = true, Height = 30 };
            cancel = new Button { Text = "暂停并保存", AutoSize = true, Height = 30, Enabled = false };
            var resume = new Button { Text = "已完成验证，继续", AutoSize = true, Height = 30 };
            toolbar.Controls.AddRange(new Control[] { start, resume, cancel });
            browserStatus = new Label { Dock = DockStyle.Bottom, Height = 34, Padding = new Padding(8), AutoEllipsis = true };
            progressStatus = new Label { Dock = DockStyle.Top, Height = 56, Padding = new Padding(8), AutoEllipsis = true };
            log = new TextBox { Dock = DockStyle.Bottom, Height = 150, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
            Controls.Add(session.View); Controls.Add(log); Controls.Add(browserStatus); Controls.Add(progressStatus); Controls.Add(toolbar);
            session.StatusChanged += delegate(string message) {
                browserMessage = message; browserStatus.Text = message; SaveStatus();
            };
            resume.Click += delegate { session.Continue(); };
            cancel.Click += delegate { if (cancellation != null) cancellation.Cancel(); session.CancelPending(); };
            start.Click += async delegate { await RunArchive(); };
            Shown += async delegate { await RunArchive(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e) {
                if (cancellation != null) {
                    e.Cancel = true; closeAfterSave = true; cancellation.Cancel(); session.CancelPending();
                    progressStatus.Text = "正在保存已核实记录，随后关闭……";
                }
            };
            FormClosed += delegate { session.Dispose(); taskLock.Dispose(); taskLock = null; };
        }

        private async Task RunArchive()
        {
            if (cancellation != null) return;
            cancellation = new CancellationTokenSource();
            start.Enabled = false; cancel.Enabled = true;
            phase = "Running"; failure = null; result = null;
            try {
                Report("归档 UTC " + lower.ToString("yyyy-MM-dd HH:mm:ss") + " 至 " + upper.ToString("yyyy-MM-dd HH:mm:ss") + "（结束时刻不包含）。");
                var browser = new DiagnosticHistoryBrowser(session, Path.Combine(outputDirectory, "evidence"));
                var resolver = new OnlineHistoryResolver(browser, null, cacheDirectory, true);
                result = await resolver.ArchiveRangeAsync(lower, upper, cancellation.Token, Report);
                phase = result.RangeCovered ? "Completed" : "Incomplete";
                Report("保存结果：范围内 " + result.RangeRecords + " 条；本次新增 " + result.NewRecords + " 条；连续历史合计 " + result.TotalRecords + " 条。");
            }
            catch (OperationCanceledException) { phase = "Cancelled"; Report("已暂停。已核实的连续前缀和网页批次保留，下次从保存边界继续。"); }
            catch (Exception ex) { phase = "Stopped"; failure = ex.Message; Report("归档已停止：" + ex.Message); }
            finally {
                cancellation.Dispose(); cancellation = null;
                start.Enabled = true; cancel.Enabled = false;
                SaveStatus();
                if (closeAfterSave) BeginInvoke(new Action(Close));
            }
        }

        private void Report(string message)
        {
            latestMessage = message; progressStatus.Text = message;
            string line = "[" + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz") + "] " + message + Environment.NewLine;
            log.AppendText(line);
            File.AppendAllText(Path.Combine(outputDirectory, "progress.log"), line, new UTF8Encoding(false));
            SaveStatus();
        }

        private void SaveStatus()
        {
            var data = new {
                SchemaVersion = 1, UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("o"),
                Status = session.WaitingForUser && phase == "Running" ? "WaitingForUser" : phase,
                LowerInclusiveUtc = lower.ToString("o"), UpperExclusiveUtc = upper.ToString("o"),
                Filter = "root_depots", AppId = "570", CacheDirectory = cacheDirectory,
                LatestMessage = latestMessage, BrowserMessage = browserMessage, Error = failure, Result = result,
                Scope = "SteamDB app 570 Depot history. Public state replay; not downloaded or runtime-tested clients."
            };
            string file = Path.Combine(outputDirectory, "archive-status.json"), temporary = file + ".tmp";
            File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(data), new UTF8Encoding(false));
            if (File.Exists(file)) File.Replace(temporary, file, null); else File.Move(temporary, file);
        }
    }
}
