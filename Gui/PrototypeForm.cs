using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace DotaArchive
{
    public sealed partial class PrototypeForm : Form
    {
        private const string EngineHash = "e2eb9c272d257beaa158c8a1236cc66ec0ae134015ec9550a7d44de291f198ca";
        private const string MarkerName = ".dota-archive-browser.json";
        private const string LockName = ".dota-archive-browser.lock";
        private const string MetadataName = ".dota-archive-browser";
        private const string LocalArguments = "-steam -insecure -console -condebug -novid -windowed -w 1280 -h 720 -language schinese +sv_lan 1";
        private const string DownloadFailureMessage = "此版本因未定义的错误无法下载";
        private sealed class DownloadFailureException : IOException
        {
            internal DownloadFailureException(Exception cause) : base(cause.Message, cause) { }
        }
        private readonly ComboBox queryMode = new ComboBox();
        private readonly ComboBox timeDirection = new ComboBox();
        private readonly TextBox queryValue = new TextBox();
        private readonly TextBox rootDirectory = new TextBox();
        private readonly TextBox account = new TextBox();
        private readonly CheckBox directSteam = new CheckBox();
        private readonly Button run = new ModernButton(), resolveOnly = new ModernButton(), cancel = new ModernButton();
        private readonly Button updateCatalog = new ModernButton();
        private readonly Button login = new ModernButton(), continueBrowser = new ModernButton(), selectDirectory = new ModernButton(), resume = new ModernButton();
        private readonly Button download = new ModernButton(), verify = new ModernButton(), launch = new ModernButton(), open = new ModernButton();
        private readonly Button refreshSteamQr = new ModernButton();
        private readonly SteamQrRefreshState steamQrState = new SteamQrRefreshState();
        private readonly Label status = new Label(), detail = new Label(), browserStatus = new Label(), qrCaption = new Label();
        private readonly Label catalogStatus = new Label();
        private readonly ProgressBar progress = new ProgressBar();
        private readonly TextBox planText = new TextBox(), log = new TextBox();
        private readonly PictureBox qr = new PictureBox();
        private readonly TabControl inspector = new ModernTabs();
        private readonly TabPage planTab = new TabPage("版本详情"), browserTab = new TabPage("SteamDB 浏览器"), qrTab = new TabPage("Steam 授权"), logTab = new TabPage("任务记录");
        private readonly BrowserSession browser;
        private readonly OnlineHistoryResolver resolver;
        private readonly LocalArchiveResolver bundledArchive;
        private readonly LocalArchiveResolver recentArchive;
        private readonly LocalArchiveResolver syncedArchive;
        private readonly LocalArchiveResolver bundledSyncedArchive;
        private readonly LocalPatchEvidenceStore patchEvidence;
        private readonly LocalPatchQueryResolver localPatchResolver;
        private readonly IHistoryBrowser historyBrowser;
        private readonly string bundledDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "history");
        private readonly string syncDirectory = Path.Combine(HistoryCacheStore.DefaultDirectory, "latest-sync");
        private readonly string bundledSyncDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "latest-sync");
        private readonly System.Windows.Forms.Timer progressTimer = new System.Windows.Forms.Timer();
        private readonly object progressLock = new object();
        private ProgressInfo pendingProgress;
        private CancellationTokenSource cancellation;
        private DownloadEngine engine;
        private ResolvedHistoryPlan resolved;
        private VersionEntry prepared;
        private string targetDirectory, verifiedFingerprint;
        private bool browserReady, busy, closingAfterCancel, syncingCatalog, restoredSavedPlan;
        private int catalogStatusRevision;
#if DOTA_ARCHIVE_RELEASE
        private readonly string diagnosticDirectory = null;
#else
        private readonly string diagnosticDirectory = Environment.GetEnvironmentVariable("DOTA_ARCHIVE_DIAGNOSTICS");
#endif

        public PrototypeForm()
        {
            SuspendLayout();
            Text = "Dota Archive · 历史客户端";
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 10.5F);
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(1280, 820);
            MinimumSize = new Size(1080, 680);
            BackColor = ArchivePalette.Background;
            browser = new BrowserSession();
            patchEvidence = new LocalPatchEvidenceStore(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "patch-evidence"), Path.Combine(HistoryCacheStore.DefaultDirectory, "patch-evidence"));
            historyBrowser = patchEvidence.CreateRecordingBrowser(new DiagnosticHistoryBrowser(browser, diagnosticDirectory));
            resolver = new OnlineHistoryResolver(historyBrowser, patchEvidence.CreateRecordingResolver(historyBrowser).ResolveAsync);
            bundledArchive = new LocalArchiveResolver(bundledDirectory);
            recentArchive = new LocalArchiveResolver(HistoryCacheStore.DefaultDirectory);
            syncedArchive = new LocalArchiveResolver(syncDirectory);
            bundledSyncedArchive = new LocalArchiveResolver(bundledSyncDirectory);
            localPatchResolver = new LocalPatchQueryResolver(new LocalPatchDateCatalog(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "patch-dates", "patch-release-catalog.json")),
                patchEvidence, syncedArchive, bundledSyncedArchive, bundledArchive, recentArchive);
            BuildLayout();
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            ResumeLayout(false);
            PerformLayout();
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch(ArgumentException) { }
            resolver.CandidateLocated += delegate(DateFirstLocation located) { Post(delegate { DisplayCandidate(located); }); };
            browser.StatusChanged += delegate(string message) { Post(delegate { browserStatus.Text = CleanLog(message); AppendLog(message); if(busy && browser.WaitingForUser) inspector.SelectedTab = browserTab; }); };
            queryMode.SelectedIndexChanged += delegate { queryValue.Text = queryMode.SelectedIndex == 0 ? "2019-06-19 12:00:00" : "7.00"; UpdateQueryHint(); InvalidateSelection(); };
            timeDirection.SelectedIndexChanged += delegate { UpdateQueryHint(); InvalidateSelection(); };
            queryValue.TextChanged += delegate { if(!busy) InvalidateSelection(); };
            rootDirectory.TextChanged += delegate { if(!busy) { targetDirectory = null; verifiedFingerprint = null; UpdateButtons(); } };
            run.Click += async delegate { await Resolve(true); };
            resolveOnly.Click += async delegate { await Resolve(false); };
            updateCatalog.Click += async delegate { await UpdateCatalog(); };
            download.Click += async delegate { await DownloadResolved(); };
            refreshSteamQr.Click += async delegate { await RefreshSteamQr(); };
            verify.Click += async delegate { await VerifyCurrent(); };
            launch.Click += async delegate { await LaunchCurrent(); };
            cancel.Click += delegate { CancelOperation(); };
            continueBrowser.Click += async delegate {
                if(!browserReady && !busy) await InitializeBrowser();
                else browser.Continue();
            };
            login.Click += async delegate { await OpenLogin(); };
            selectDirectory.Click += delegate { ChooseDirectory(); };
            resume.Click += async delegate {
                using(var dialog = new FolderBrowserDialog { Description = "选择要继续下载的版本文件夹", SelectedPath = rootDirectory.Text })
                    if(dialog.ShowDialog(this) == DialogResult.OK) await ResumeDirectory(dialog.SelectedPath);
            };
            open.Click += delegate { OpenDirectory(); };
            FormClosing += OnClosing;
            FormClosed += delegate { progressTimer.Stop(); progressTimer.Dispose(); ClearQr(); browser.Dispose(); if(engine != null) engine.Dispose(); };
            progressTimer.Interval = 200;
            progressTimer.Tick += delegate { FlushProgress(); };
            progressTimer.Start();
            Shown += async delegate {
                Rectangle workArea = Screen.FromControl(this).WorkingArea;
                if(Width > workArea.Width || Height > workArea.Height) {
                    Size = new Size(Math.Min(Width, workArea.Width), Math.Min(Height, workArea.Height));
                    Location = new Point(workArea.Left + Math.Max(0, (workArea.Width - Width) / 2), workArea.Top + Math.Max(0, (workArea.Height - Height) / 2));
                }
                await RefreshCatalogStatus();
                if(IsDisposed || Disposing) return;
#if !DOTA_ARCHIVE_RELEASE
                if(!String.IsNullOrWhiteSpace(diagnosticDirectory)) {
                    Directory.CreateDirectory(diagnosticDirectory);
                    using(Graphics g = CreateGraphics()) SaveJson(Path.Combine(diagnosticDirectory, "layout.json"), new { Dpi = g.DpiX, Bounds = Bounds.ToString(), Client = ClientSize.ToString(), AutoScale = AutoScaleDimensions.ToString(), Controls = DescribeLayout(this) });
                }
                string resumeDirectory = Environment.GetEnvironmentVariable("DOTA_ARCHIVE_RESUME_DIRECTORY");
                if(!String.IsNullOrWhiteSpace(resumeDirectory)) { await ResumeDirectory(resumeDirectory); return; }
                string testQuery = Environment.GetEnvironmentVariable("DOTA_ARCHIVE_TEST_QUERY");
                if(!String.IsNullOrWhiteSpace(diagnosticDirectory) && !String.IsNullOrWhiteSpace(testQuery) && !busy) {
                    queryMode.SelectedIndex = Regex.IsMatch(testQuery, @"^\d+\.\d{2}[a-z]?$", RegexOptions.IgnoreCase) ? 1 : 0;
                    string testDirection = Environment.GetEnvironmentVariable("DOTA_ARCHIVE_TEST_TIME_DIRECTION");
                    if(testDirection == "Before" || testDirection == "After") timeDirection.SelectedIndex = testDirection == "After" ? 1 : 0;
                    queryValue.Text = testQuery;
                    AppendLog("自动界面验证查询：" + testQuery);
                    await Resolve(Environment.GetEnvironmentVariable("DOTA_ARCHIVE_TEST_DOWNLOAD") == "1");
                }
#endif
            };
            UpdateButtons();
        }

        private async Task InitializeBrowser()
        {
            BeginOperation("正在初始化浏览器");
            try { await EnsureBrowserReady(); status.Text = "浏览器已就绪；需要登录时请点击“登录 SteamDB”"; }
            catch(OperationCanceledException) { ShowCancelled(); }
            catch(Exception ex) { browserStatus.Text = (browserReady ? "SteamDB 页面暂不可用：" : "浏览器初始化未完成：") + CleanLog(ex.Message); ShowError(ex); }
            finally { EndOperation(); }
        }
        private async Task OpenLogin()
        {
            if(busy) return;
            if(!BeginOperation("打开 SteamDB 登录")) return;
            try { await EnsureBrowserReady(); await browser.NavigateAsync("https://steamdb.info/login/", cancellation.Token); status.Text = "请在浏览器中完成登录"; }
            catch(OperationCanceledException) { ShowCancelled(); } catch(Exception ex) { ShowError(ex); } finally { EndOperation(); }
        }
        private async Task EnsureBrowserReady()
        {
            inspector.SelectedTab = browserTab;
            if(browserReady) return;
            await browser.InitializeAsync(); browserReady = true; UpdateButtons();
        }
        private async Task RefreshCatalogStatus()
        {
            int revision = ++catalogStatusRevision;
            try {
                SyncCatalogSnapshot snapshot = await Task.Run(delegate {
                    SyncCatalogSnapshot local = new SyncCatalogStore(syncDirectory).LoadActive();
                    SyncCatalogSnapshot bundled = new SyncCatalogStore(bundledSyncDirectory).LoadActive();
                    return local == null ? bundled : bundled != null && bundled.VerifiedThroughUtc > local.VerifiedThroughUtc ? bundled : local;
                });
                if(IsDisposed || Disposing || revision != catalogStatusRevision || snapshot == null) return;
                catalogStatus.Text = "资料检查至（北京）：" + snapshot.VerifiedThroughUtc.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "；最新 Build " + snapshot.LatestBuildId + "；共 " + snapshot.TotalRecords + " 条记录。";
            } catch(Exception ex) {
                if(IsDisposed || Disposing || revision != catalogStatusRevision) return;
                catalogStatus.Text = "本机更新库未通过校验；请查看任务记录。";
                AppendLog("读取资料同步状态失败：" + ex.Message);
            }
        }
        private async Task UpdateCatalog()
        {
            if(busy || steamQrState.Pending) return;
            catalogStatusRevision++;
            syncingCatalog = true;
            if(!BeginOperation("准备更新版本资料")) { syncingCatalog = false; return; }
            resolved = null; prepared = null; targetDirectory = null; verifiedFingerprint = null;
            progress.Style = ProgressBarStyle.Marquee;
            inspector.SelectedTab = planTab;
            planText.Text = "更新版本资料\r\n\r\n请在“SteamDB 浏览器”页签登录 SteamDB，并按网站要求完成验证。返回 SteamDB 后点击“已完成验证，继续”。已有登录会话可以继续使用。\r\n\r\n随后软件读取新增公开更新记录，核对与本地归档的衔接和完整 Depot 状态，通过后更新本地数据库。\r\n\r\n本次只更新版本资料，不下载或启动游戏。可随时点击“停止”；旧数据库和已核实断点会保留。";
            detail.Text = "请在内置浏览器完成 SteamDB 登录或验证，然后点击“已完成验证，继续”。";
            AppendLog("开始更新版本资料；需在内置浏览器完成 SteamDB 登录或验证。");
            try {
                await EnsureBrowserReady();
                await historyBrowser.NavigateAsync("https://steamdb.info/login/", cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                status.Text = "正在增量同步版本资料";
                var updater = new HistoryLatestSync(historyBrowser, bundledDirectory, HistoryCacheStore.DefaultDirectory, bundledSyncDirectory);
                HistorySyncResult result = await updater.SyncAsync(cancellation.Token, delegate(string message) {
                    Post(delegate { detail.Text = CleanLog(message); AppendLog(message); });
                });
                // Returning from SyncAsync means the new generation is committed.
                // A late Stop must not relabel an already committed update as failed.
                string checkedThrough = result.VerifiedThroughUtc.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                string latestRelease = result.LatestBuildPublishedAtUtc.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                catalogStatus.Text = "资料检查至（北京）：" + checkedThrough + "；最新 Build " + result.LatestBuildId + "；共 " + result.TotalRecords + " 条记录。";
                status.Text = result.AddedRecords == 0 ? "资料已是最新，检查时间已更新" : "版本资料更新完成";
                detail.Text = "本次新增 " + result.AddedRecords + " 条记录，已保存到本地；没有下载游戏。";
                planText.Text = "版本资料更新完成\r\n\r\n本次新增：" + result.AddedRecords + " 条\r\n累计连续记录：" + result.TotalRecords + " 条\r\n最新 Build：" + result.LatestBuildId + "\r\n版本发布时间（北京）：" + latestRelease + "\r\n资料检查至（北京）：" + checkedThrough + "\r\n最新历史记录：" + result.LatestChangeId + "\r\n\r\n新旧历史衔接、完整 Depot 状态和同步前后基线已经核实。可以输入时间节点优先从本地匹配。版本号查询使用软件内置发布日期表，全程本地匹配；本次历史同步不会新增发布日期条目。\r\n\r\n本次没有下载或启动游戏。";
                inspector.SelectedTab = planTab;
                AppendLog(detail.Text + " 资料检查至（北京）：" + checkedThrough);
                if(!String.IsNullOrWhiteSpace(diagnosticDirectory)) {
                    try {
                        Directory.CreateDirectory(diagnosticDirectory);
                        SaveJson(Path.Combine(diagnosticDirectory, "latest-sync-result.json"), result);
                    } catch(IOException ex) { AppendLog("资料已更新，但诊断报告未能保存：" + ex.Message); }
                      catch(UnauthorizedAccessException ex) { AppendLog("资料已更新，但诊断报告未能保存：" + ex.Message); }
                }
            }
            catch(OperationCanceledException) { ShowCancelled(); }
            catch(Exception ex) { ShowError(ex); AppendLog("本次同步未完成；原有数据库保留，未更新成功检查时间。"); }
            finally { progress.Style = ProgressBarStyle.Blocks; syncingCatalog = false; EndOperation(); }
        }
        private HistoryQuery ReadQuery()
        {
            string value = queryValue.Text.Trim();
            if(queryMode.SelectedIndex == 1) {
                if(!Regex.IsMatch(value, @"^[0-9]+\.[0-9]{2}[a-z]?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) throw new ArgumentException("请输入完整补丁号，例如 7.00、7.22b 或 7.38c。");
                return new HistoryQuery { Patch = value.ToLowerInvariant(), Language = "schinese", TimeDirection = timeDirection.SelectedIndex == 1 ? HistoryTimeDirection.After : HistoryTimeDirection.Before };
            }
            DateTime parsed;
            if(!DateTime.TryParseExact(value, new[] { "yyyy-MM-dd HH:mm:ss", "yyyy/M/d H:m:s", "yyyy-M-d H:m:s", "yyyy年M月d日H点m分s秒" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
                throw new ArgumentException("请输入北京时间，例如 2019-06-19 12:00:00。");
            return new HistoryQuery { BeijingTime = new DateTimeOffset(DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified), TimeSpan.FromHours(8)), Language = "schinese", TimeDirection = timeDirection.SelectedIndex == 1 ? HistoryTimeDirection.After : HistoryTimeDirection.Before };
        }
        private async Task Resolve(bool downloadAfter)
        {
            HistoryQuery query;
            try { query = ReadQuery(); ValidateRoot(); } catch(Exception ex) { ShowError(ex); return; }
            if(!BeginOperation("正在查找历史构建")) return;
            restoredSavedPlan = false;
            resolved = null; prepared = null; targetDirectory = null; verifiedFingerprint = null; planText.Text = "匹配规则：" + QueryRuleDescription() + "\r\n\r\n正在查询本地连续历史归档…"; inspector.SelectedTab = planTab;
            try {
                Action<string> report = delegate(string message) { Post(delegate { detail.Text = CleanLog(message); AppendLog(message); }); };
                if(!String.IsNullOrEmpty(query.Patch)) {
                    // The patch path has no browser/network fallback, including misses.
                    resolved = await localPatchResolver.ResolveAsync(query, cancellation.Token, report);
                } else {
                    resolved = await syncedArchive.ResolveAsync(query, cancellation.Token, report);
                    if(resolved == null) resolved = await bundledSyncedArchive.ResolveAsync(query, cancellation.Token, report);
                    if(resolved == null) resolved = await bundledArchive.ResolveAsync(query, cancellation.Token, report);
                    if(resolved == null) resolved = await recentArchive.ResolveAsync(query, cancellation.Token, report);
                }
                bool localHit = resolved != null;
                if(!localHit) {
                    if(!String.IsNullOrEmpty(query.Patch)) throw new HistoryResolutionException("本地资料未能匹配该补丁，版本号查询不会访问 SteamDB。");
                    report("本地证据尚未覆盖该查询，正在打开 SteamDB 补查；已有归档会保留。");
                    await EnsureBrowserReady();
                    resolved = await resolver.ResolveAsync(query, cancellation.Token, report);
                }
                cancellation.Token.ThrowIfCancellationRequested();
                DisplayPlan(resolved);
                if(resolved == null || !resolved.Complete || resolved.Depots == null || resolved.Depots.Count == 0) throw new InvalidDataException("历史清单尚未完整，不能开始下载。");
                status.Text = localHit ? "本地匹配成功，完整构建组合已核实" : "完整构建组合已找到";
                if(!String.IsNullOrWhiteSpace(diagnosticDirectory)) {
                    Directory.CreateDirectory(diagnosticDirectory);
                    SaveJson(Path.Combine(diagnosticDirectory, "resolved-query.json"), new { Query = query, LocalHit = localHit, BrowserInitialized = browserReady, ResolvedAtUtc = DateTimeOffset.UtcNow.ToString("o"), Plan = resolved });
                }
                if(downloadAfter) await DownloadCore(cancellation.Token);
            } catch(OperationCanceledException) {
                if(resolved == null) planText.Text = "匹配规则：" + QueryRuleDescription() + "\r\n\r\n查询已停止，尚未生成可下载的完整清单。";
                else DisplayPlan(resolved);
                ShowCancelled();
            } catch(Exception ex) {
                if(resolved == null) planText.Text = "匹配规则：" + QueryRuleDescription() + "\r\n\r\n查询未完成：" + CleanLog(ex.Message);
                ShowError(ex);
            } finally { EndOperation(); }
        }
        private void DisplayPlan(ResolvedHistoryPlan plan)
        {
            inspector.SelectedTab = planTab;
            if(plan == null) { planText.Text = "尚未得到构建组合。"; return; }
            var text = new StringBuilder();
            text.AppendLine("Build " + plan.BuildId);
            bool patchDateCandidate = (plan.PatchMatchKind ?? "").StartsWith("LocalReleaseDate", StringComparison.Ordinal);
            if(!String.IsNullOrWhiteSpace(plan.Patch)) text.AppendLine((patchDateCandidate ? "查询基准补丁号：" : "补丁号：") + plan.Patch);
            text.AppendLine("匹配规则：" + QueryRuleDescription());
            if(!String.IsNullOrWhiteSpace(plan.PatchDateDescription)) text.AppendLine(plan.PatchDateDescription);
            if(patchDateCandidate) text.AppendLine("版本号关联：按该补丁的日期基准匹配，结果可能属于相邻补丁；尚未独立核实首发构建。");
            text.AppendLine("匹配时间（北京）：" + plan.PublishedAtUtc.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm:ss"));
            text.AppendLine("时间来源：" + DisplayTimeSource(plan.TimeSource));
            if(plan.UsedObservationFallback) text.AppendLine("SteamDB 记录时间可能晚于实际发布时间。");
            text.AppendLine(plan.Complete ? "完整组合已核实" : "组合尚未完整，禁止下载");
            text.AppendLine("下载时还会核验 Steam 原始清单和实际文件。").AppendLine();
            foreach(DepotEntry depot in plan.Depots ?? new List<DepotEntry>()) text.AppendLine(depot.DepotId + " / " + depot.ManifestId).AppendLine("  " + depot.Name);
            if(plan.Limitations != null) foreach(string limitation in plan.Limitations) text.AppendLine().AppendLine(limitation);
            text.AppendLine().AppendLine("历史记录原始出处（链接仅用于溯源）：" + plan.SourceUrl);
            planText.Text = text.ToString();
            AppendLog("查询结果：Build " + plan.BuildId + "，" + (plan.Depots == null ? 0 : plan.Depots.Count) + " 个内容包。");
            if(!String.IsNullOrWhiteSpace(plan.Assurance)) AppendLog("核验依据：" + plan.Assurance);
        }

        private void DisplayCandidate(DateFirstLocation located)
        {
            if(located == null || located.CandidateChange == null) return;
            var text = new StringBuilder();
            string build = located.Publication == null ? null : located.Publication.BuildId;
            text.AppendLine("已定位版本：" + (String.IsNullOrEmpty(build) ? "Build 待历史状态核实" : "Build " + build));
            text.AppendLine("匹配规则：" + QueryRuleDescription());
            text.AppendLine("历史变更：" + located.CandidateChange.ChangeId);
            if(located.EffectiveTime.Year >= 1970)
                text.AppendLine("匹配时间（北京）：" + located.EffectiveTime.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm:ss"));
            text.AppendLine("时间来源：" + DisplayTimeSource(located.TimeSource));
            text.AppendLine("日期控件查询：" + String.Join(" → ", located.RequestedDates.Select(d => d.ToString("yyyy-MM-dd"))));
            text.AppendLine().AppendLine("正在补齐完整 Depot / Manifest。日期页只列出变更，未出现的 Depot 仍需核实；此结果尚不能作为完整客户端下载清单。");
            text.AppendLine().AppendLine(located.Assurance);
            text.AppendLine().AppendLine("来源：" + located.CandidateChange.SourceUrl);
            planText.Text = text.ToString();
            status.Text = "版本已定位，正在补齐完整清单";
            AppendLog("日期定位结果：" + (build ?? "Build 待核实") + " / Change " + located.CandidateChange.ChangeId + " / " + located.TimeSource);
            if(!String.IsNullOrWhiteSpace(diagnosticDirectory)) {
                try {
                    Directory.CreateDirectory(diagnosticDirectory);
                    File.WriteAllText(Path.Combine(diagnosticDirectory, "date-location-result.json"), new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Serialize(located), new UTF8Encoding(false));
                } catch(IOException ex) { AppendLog("无法保存本次定位诊断：" + ex.Message); }
                  catch(UnauthorizedAccessException ex) { AppendLog("无法保存本次定位诊断：" + ex.Message); }
                // Opt-in development acceptance run: exercise the real query through
                // candidate selection without starting a many-year reconstruction.
#if !DOTA_ARCHIVE_RELEASE
                if(Environment.GetEnvironmentVariable("DOTA_ARCHIVE_TEST_STOP_AFTER_LOCATE") == "1" && cancellation != null) {
                    Environment.SetEnvironmentVariable("DOTA_ARCHIVE_TEST_STOP_AFTER_LOCATE", null);
                    cancellation.Cancel();
                }
#endif
            }
        }

        private string QueryRuleDescription()
        {
            if(restoredSavedPlan) return "恢复该目录已保存的完整构建，不重新按时间匹配";
            string comparison = timeDirection.SelectedIndex == 1 ? "严格晚于" : "严格早于";
            return queryMode.SelectedIndex == 1 ? "内置补丁日期转为时间基准 → " + comparison + "该基准的最近公开版本（相等排除），全程本地查询" : comparison + "输入时刻的最近公开版本（相等排除）";
        }

        private static string DisplayTimeSource(string source)
        {
            if(source == "Valve public/timeupdated") return "Valve 公开分支原始更新时间";
            if(source == "SteamDB public branch change observed time") return "SteamDB 公开分支变更记录时间（非 Valve 原始发布时间）";
            return String.IsNullOrWhiteSpace(source) ? "待核实" : source;
        }

        private async Task ResumeDirectory(string directory)
        {
            if(!BeginOperation("正在恢复已保存的下载")) return;
            try {
                ResolvedHistoryPlan saved = SavedDownloadPlan.Load(directory);
                rootDirectory.Text = Path.GetDirectoryName(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                RestoreQuerySelection(saved);
                restoredSavedPlan = true;
                resolved = saved; prepared = null; targetDirectory = null; verifiedFingerprint = null;
                DisplayPlan(saved);
                browserStatus.Text = "已恢复完整下载计划。需要新的历史查询时可初始化浏览器。";
                AppendLog("已从版本目录恢复自动核实的 Build " + saved.BuildId + " 完整计划；复用已有文件，无需重新查询 SteamDB。");
                if(!await RestoreLocalClient(cancellation.Token)) await DownloadCore(cancellation.Token);
            } catch(OperationCanceledException) { ShowCancelled(); } catch(Exception ex) { ShowError(ex); } finally { EndOperation(); }
        }
        private void RestoreQuerySelection(ResolvedHistoryPlan saved)
        {
            queryMode.SelectedIndex = String.IsNullOrWhiteSpace(saved.Patch) ? 0 : 1;
            queryValue.Text = saved.Patch ?? "";
            // Old plans have no directional marker. Preserve their exact saved
            // client without inventing a direction; new patch plans restore it.
            if(saved.PatchMatchKind == "LocalReleaseDateBeforeCandidate") timeDirection.SelectedIndex = 0;
            else if(saved.PatchMatchKind == "LocalReleaseDateAfterCandidate") timeDirection.SelectedIndex = 1;
        }
        private async Task<bool> RestoreLocalClient(CancellationToken token)
        {
            VersionEntry entry = ToDownloadEntry(resolved);
            string directory = VersionCatalog.SafePath(ValidateRoot(), entry.FolderName);
            // Rebuild from the saved, exact Steam manifest set, never trust a saved
            // prepared-plan or compatibility offsets supplied by a local JSON file.
            if(!entry.Depots.All(d => File.Exists(VersionCatalog.SafePath(directory, "manifest_" + d.DepotId + "_" + d.ManifestId + ".txt")) &&
                File.Exists(VersionCatalog.SafePath(directory, ".DepotDownloader/" + d.DepotId + "_" + d.ManifestId + ".manifest")) &&
                File.Exists(VersionCatalog.SafePath(directory, ".DepotDownloader/" + d.DepotId + "_" + d.ManifestId + ".manifest.sha")))) return false;
            RequireDownloadTargetNotRunning(directory);
            using(AcquireLock(directory)) {
                EnsureDirectoryIdentity(directory, resolved, token);
                await Task.Run(delegate { VersionCatalog.AssertNoReparseTree(directory, token); }, token);
                ManifestPreparationResult plan = await Task.Run(delegate { return ManifestPlanBuilder.Prepare(entry.Depots, directory, token, QueueProgress); }, token);
                entry.Depots = plan.Depots; entry.Files = plan.Files; entry.Directories = plan.Directories; entry.TotalBytes = plan.TotalBytes;
                ValidatePreparedPaths(entry); entry.Executable = FindExecutable(entry);
                if(entry.Executable == null) throw new InvalidDataException("本地清单中没有受支持的 Windows 启动程序。");
                prepared = entry; targetDirectory = directory;
                DeriveClientVersion(prepared, directory);
                AppendLog("已从本地原始清单恢复客户端，开始完整校验；本次无需 Steam 登录或重新下载。");
                VerificationResult result = await VerifyCore(token);
                if(result.Success) detail.Text = "本地文件已核对，可点击“启动本地测试”；旧版 ICU 兼容处理将在启动时自动进行。";
                else detail.Text = "本地客户端尚未完整，请点击“下载 / 继续”补齐。";
                return true;
            }
        }
        private async Task RefreshSteamQr()
        {
            if(!steamQrState.Request(busy, resolved != null && resolved.Complete)) return;
            ClearQr(); UpdateButtons();
            if(busy) {
                // The current RunAsync must finish and release its process and directory
                // lock before EndOperation schedules a single replacement attempt.
                CancelOperation(true);
                status.Text = "正在结束旧授权，随后生成新二维码";
                qrCaption.Text = "正在刷新，请稍候。已有下载文件会保留。";
            } else if(steamQrState.TakePending()) await DownloadResolved(true);
        }
        private async Task DownloadResolved(bool forceQr = false)
        {
            if(resolved == null || !resolved.Complete || !BeginOperation("准备下载")) return;
            try { await DownloadCore(cancellation.Token, forceQr); } catch(OperationCanceledException) { ShowCancelled(); } catch(Exception ex) { ShowError(ex); } finally { EndOperation(); }
        }
        private async Task DownloadCore(CancellationToken token, bool forceQr = false)
        {
            try { await DownloadCoreDetailed(token, forceQr); }
            catch(OperationCanceledException) { throw; }
            catch(Exception ex) { throw new DownloadFailureException(ex); }
        }
        private async Task DownloadCoreDetailed(CancellationToken token, bool forceQr)
        {
            if(resolved == null || !resolved.Complete) throw new InvalidDataException("没有完整的历史构建组合。");
            prepared = ToDownloadEntry(resolved);
            targetDirectory = VersionCatalog.SafePath(ValidateRoot(), prepared.FolderName);
            verifiedFingerprint = null;
            RequireDownloadTargetNotRunning(targetDirectory);
            Directory.CreateDirectory(targetDirectory);
            using(AcquireLock(targetDirectory)) {
                await Task.Run(delegate { VersionCatalog.AssertNoReparseTree(targetDirectory, token); }, token);
                EnsureDirectoryIdentity(targetDirectory, resolved, token);
                engine = new DownloadEngine(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "engine", "DepotDownloader.exe"), EngineHash, true);
                DownloadEngine currentEngine = engine;
                steamQrState.Begin(currentEngine);
                engine.Progress += delegate(ProgressInfo value) { ReceiveEngineProgress(currentEngine, value); };
                engine.Log += delegate(string message) { Post(delegate { if(steamQrState.IsCurrent(currentEngine)) AppendLog(message); }); };
                engine.QrReady += delegate(Bitmap bitmap) { ReceiveQr(currentEngine, bitmap); };
                // Blank means a fresh QR on the first stage, then this engine reuses
                // the account that actually authenticated for the file stage.
                string requestedAccount = forceQr ? "" : account.Text.Trim();
                status.Text = "获取 Steam 原始清单"; detail.Text = targetDirectory; AppendLog("目标目录：" + targetDirectory);
                int manifestExit = await engine.RunAsync(prepared, targetDirectory, requestedAccount, directSteam.Checked, DownloadMode.ManifestOnly, token);
                ClearQr(); token.ThrowIfCancellationRequested();
                if(manifestExit != 0) throw new IOException("清单获取失败：" + (engine.LastErrorSummary ?? ("下载引擎返回码 " + manifestExit + "，具体原因未识别。")) + " 已保留现有文件。");
                status.Text = "核对清单并生成文件计划";
                ManifestPreparationResult plan = await Task.Run(delegate { return ManifestPlanBuilder.Prepare(prepared.Depots, targetDirectory, token, QueueProgress); }, token);
                prepared.Depots = plan.Depots; prepared.Files = plan.Files; prepared.Directories = plan.Directories; prepared.TotalBytes = plan.TotalBytes;
                ValidatePreparedPaths(prepared);
                string executable = FindExecutable(prepared);
                if(executable == null) throw new InvalidDataException("已取得的清单没有受支持的 Windows Dota 2 启动程序，已停止下载。");
                prepared.Executable = executable;
                string metadata = VersionCatalog.SafePath(targetDirectory, MetadataName);
                Directory.CreateDirectory(metadata);
                SaveJson(Path.Combine(metadata, "resolved-plan.json"), resolved);
                SaveJson(Path.Combine(metadata, "manifest-preparation.json"), plan);
                AppendLog("清单准备完成：" + prepared.Files.Count + " 个文件，" + FormatBytes(prepared.TotalBytes) + "。开始下载原版文件。");
                planText.AppendText("\r\n\r\n文件计划：" + prepared.Files.Count + " 个文件 / " + FormatBytes(prepared.TotalBytes) + "\r\n目录：" + targetDirectory);
                status.Text = "下载原版客户端";
                int downloadExit = await engine.RunAsync(prepared, targetDirectory, requestedAccount, directSteam.Checked, DownloadMode.ClientFiles, token);
                ClearQr(); token.ThrowIfCancellationRequested();
                if(downloadExit != 0) AppendLog("下载进程返回 " + downloadExit + "；将核对已取得文件，不视为下载成功。");
                DeriveClientVersion(prepared, targetDirectory);
                SaveJson(Path.Combine(metadata, "prepared-plan.json"), prepared);
                VerificationResult result = await VerifyCore(token);
                token.ThrowIfCancellationRequested();
                if(!result.Success) throw new InvalidDataException("客户端尚未完整，请点击“下载 / 继续”补齐。" + result.Summary);
                status.Text = result.UsesCompatibility ? "文件及兼容处理校验通过，可以启动本地测试" : "原版文件完整，可以启动本地测试";
                detail.Text = "点击“启动本地测试”；进入游戏与本地对局仍需实际验证。";
                inspector.SelectedTab = planTab;
            }
        }
        private async Task VerifyCurrent()
        {
            if(prepared == null || targetDirectory == null || !BeginOperation("完整文件校验")) return;
            try { using(AcquireLock(targetDirectory)) { EnsureDirectoryIdentity(targetDirectory, resolved, cancellation.Token); DeriveClientVersion(prepared, targetDirectory); await VerifyCore(cancellation.Token); } }
            catch(OperationCanceledException) { ShowCancelled(); } catch(Exception ex) { ShowError(ex); } finally { EndOperation(); }
        }
        private async Task<VerificationResult> VerifyCore(CancellationToken token)
        {
            verifiedFingerprint = null; status.Text = "逐文件校验原版 SHA1"; progress.Value = 0;
            VerificationResult result = await Task.Run(delegate {
                token.ThrowIfCancellationRequested();
                prepared.Compatibility = HistoricalIcuCompatibility.Derive(prepared, targetDirectory);
                return ManifestVerifier.Verify(prepared, targetDirectory, true, token, QueueProgress);
            }, token);
            token.ThrowIfCancellationRequested(); FlushProgress();
            string metadata = VersionCatalog.SafePath(targetDirectory, MetadataName); Directory.CreateDirectory(metadata);
            SaveJson(Path.Combine(metadata, "client-verification.json"), new { BuildId = resolved.BuildId, PlanFingerprint = Fingerprint(resolved), CheckedAtUtc = DateTimeOffset.UtcNow.ToString("o"), Result = result, RuntimeTestPassed = false });
            AppendLog(result.Summary); foreach(string error in (result.Errors ?? new List<string>()).Take(20)) AppendLog(error);
            if(result.Success) { verifiedFingerprint = Fingerprint(resolved); status.Text = "完整文件校验通过"; progress.Value = 100; detail.Text = result.CheckedFiles + (result.UsesCompatibility ? " 个文件、原版备份及 ICU 兼容处理已核对。" : " 个原版文件已核对。"); }
            else { status.Text = "文件校验未通过"; detail.Text = result.Summary; }
            return result;
        }

        private async Task LaunchCurrent()
        {
            if(prepared == null || resolved == null || verifiedFingerprint != Fingerprint(resolved) || !BeginOperation("启动本地测试")) return;
            string fingerprint = verifiedFingerprint;
            try {
                using(AcquireLock(targetDirectory)) {
                    EnsureDirectoryIdentity(targetDirectory, resolved, cancellation.Token);
                    RequireNoGame();
                    Process[] steam = Process.GetProcessesByName("steam"); bool steamRunning = steam.Length != 0; foreach(Process process in steam) process.Dispose();
                    if(!steamRunning) throw new InvalidOperationException("请先打开 Steam 客户端并登录，然后启动历史客户端。");
                    cancellation.Token.ThrowIfCancellationRequested();
                    string exe = VersionCatalog.SafePath(targetDirectory, prepared.Executable);
                    foreach(string relative in CriticalFiles(prepared)) VerifyOneFile(prepared, targetDirectory, relative);
                    prepared.Compatibility = HistoricalIcuCompatibility.Derive(prepared, targetDirectory);
                    if(prepared.Compatibility != null) {
                        RuntimeLauncher.PrepareCompatibility(prepared, targetDirectory);
                        AppendLog("已为旧版 ICU 52 启用私有库名称，避免 Windows 同名库冲突；原文件备份保存在 _compatibility/icu52/originals，游戏玩法 DLL 未改动。");
                    }
                    string executableParent = Path.GetDirectoryName(prepared.Executable).Replace('\\', '/');
                    string hint = VersionCatalog.SafePath(targetDirectory, (executableParent.Length == 0 ? "" : executableParent + "/") + "steam_appid.txt");
                    if(File.Exists(hint)) { if(File.ReadAllText(hint).Trim('\0', '\r', '\n', '\t', ' ') != "570") throw new InvalidDataException("steam_appid.txt 内容不是 570，已停止启动。"); }
                    else File.WriteAllText(hint, "570", Encoding.ASCII);
                    string launchArguments = LocalArguments;
                    string dx11Relative = (executableParent.Length == 0 ? "" : executableParent + "/") + "rendersystemdx11.dll";
                    if(File.Exists(VersionCatalog.SafePath(targetDirectory, dx11Relative))) {
                        VerifyOneFile(prepared, targetDirectory, dx11Relative);
                        launchArguments += " -dx11";
                        AppendLog("使用此版本清单内的原版 DX11 渲染器进行本地测试。");
                    }
                    string launchExe = AsciiClientLaunch.GetExecutablePath(targetDirectory, prepared.Executable, fingerprint);
                    bool usedAsciiAlias = !String.Equals(exe, launchExe, StringComparison.OrdinalIgnoreCase);
                    if(usedAsciiAlias) AppendLog("检测到非英文目录，使用已核实的 ASCII 路径启动同一份客户端，避免旧版资源加载失败。");
                    int pid;
                    using(Process game = Process.Start(new ProcessStartInfo(launchExe, launchArguments) { WorkingDirectory = Path.GetDirectoryName(launchExe), UseShellExecute = false })) {
                        if(game == null) throw new IOException("无法启动客户端。"); pid = game.Id;
                    }
                    SaveJson(VersionCatalog.SafePath(targetDirectory, MetadataName + "/launch-attempt.json"), new { BuildId = resolved.BuildId, PlanFingerprint = fingerprint, AttemptedAtUtc = DateTimeOffset.UtcNow.ToString("o"), ProcessId = pid, OriginalExecutable = exe, LaunchExecutable = launchExe, LaunchArguments = launchArguments, UsedAsciiLaunchAlias = usedAsciiAlias, UsesIcuCompatibility = prepared.Compatibility != null, MenuVerified = false, LocalGameVerified = false });
                    verifiedFingerprint = fingerprint;
                    status.Text = "已启动进程，等待实际游戏测试";
                    detail.Text = "请进入本地机器人对局，检查载入、移动和施法；进程启动不代表测试通过。";
                    AppendLog("客户端进程 " + pid + " 已启动。尚未确认主菜单和本地对局是否成功。");
                    await Task.FromResult(0);
                }
            } catch(OperationCanceledException) { ShowCancelled(); } catch(Exception ex) { ShowError(ex); } finally { EndOperation(); }
        }

        internal static VersionEntry ToDownloadEntry(ResolvedHistoryPlan plan)
        {
            if(plan == null || !plan.Complete || plan.Depots == null || plan.Depots.Count == 0 || !Regex.IsMatch(plan.BuildId ?? "", @"^[1-9][0-9]*$")) throw new InvalidDataException("历史组合不完整。");
            DateTimeOffset beijing = plan.PublishedAtUtc.ToOffset(TimeSpan.FromHours(8));
            string patch = String.IsNullOrWhiteSpace(plan.Patch) ? "build" + plan.BuildId : plan.Patch;
            string folderPatch = Regex.Replace(patch, @"[^a-zA-Z0-9._-]", "_");
            string folder = folderPatch + "-" + beijing.ToString("yyyy-MM-dd") + "-build" + plan.BuildId;
            return new VersionEntry { Id = "online-" + plan.BuildId + "-" + beijing.ToString("yyyyMMddHHmmss"), Patch = patch, Date = beijing.ToString("yyyy-MM-dd"), Title = "Build " + plan.BuildId, BuildId = plan.BuildId, PublishedAtUtc = plan.PublishedAtUtc.ToUniversalTime().ToString("o"), ClientVersion = null, FolderName = folder, SourceUrl = plan.SourceUrl, Description = plan.Assurance, Depots = plan.Depots.Select(d => new DepotEntry { DepotId = d.DepotId, ManifestId = d.ManifestId, Name = d.Name, Bytes = 0 }).ToList(), Files = new List<FileEntry>(), Directories = new List<string>(), Compatibility = null, LaunchArguments = LocalArguments };
        }
        internal static string Fingerprint(ResolvedHistoryPlan plan)
        {
            if(plan == null || plan.Depots == null) throw new InvalidDataException("缺少历史构建计划。");
            string canonical = "570\npublic\nwindows-schinese\n" + plan.BuildId + "\n" + plan.PublishedAtUtc.ToUniversalTime().ToString("o") + "\n" + String.Join("\n", plan.Depots.OrderBy(d => d.DepotId, StringComparer.Ordinal).Select(d => d.DepotId + ":" + d.ManifestId));
            using(SHA256 hash = SHA256.Create()) return Hex(hash.ComputeHash(Encoding.UTF8.GetBytes(canonical)));
        }
        private string ValidateRoot()
        {
            string root = rootDirectory.Text.Trim();
            if(root.Length == 0 || !Path.IsPathRooted(root)) throw new ArgumentException("请选择绝对路径作为版本保存根目录。");
            string result = Path.GetFullPath(root);
            if(result.Any(c => c > 127)) throw new ArgumentException("客户端完整保存路径必须使用英文、数字等 ASCII 字符，不能包含中文目录。请选择例如 D:\\DotaArchiveClients；已有中文目录不会被自动移动或继续写入。");
            VersionCatalog.SafePath(result, ".path-check"); return result;
        }
        private static string DefaultDownloadRoot()
        {
            foreach(string parent in new[] { Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments) })
                if(!String.IsNullOrWhiteSpace(parent) && !parent.Any(c => c > 127)) return Path.Combine(parent, "DotaArchiveClients");
            return String.Empty;
        }
        private static FileStream AcquireLock(string directory)
        {
            // Even verification and launch create a lock and write metadata.
            // Keep the original lock protocol; an ASCII junction is not a
            // separate storage directory and must not bypass this policy.
            if(Path.GetFullPath(directory).Any(c => c > 127))
                throw new InvalidOperationException("此客户端位于包含中文等非 ASCII 字符的目录。为避免继续写入旧目录，软件已停止校验或启动；请使用完整的纯英文客户端目录。已有文件不会自动移动或修改。");
            if(!Directory.Exists(directory)) throw new DirectoryNotFoundException("版本目录尚不存在。");
            string path = VersionCatalog.SafePath(directory, LockName);
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
            catch(IOException) { throw new IOException("这个版本目录正被其他任务使用，请稍后重试。"); }
        }
        internal static void EnsureDirectoryIdentity(string directory, ResolvedHistoryPlan plan, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string marker = VersionCatalog.SafePath(directory, MarkerName), fingerprint = Fingerprint(plan);
            if(File.Exists(marker)) {
                if(new FileInfo(marker).Length > 32768) throw new InvalidDataException("版本目录标识异常，已停止写入。");
                var data = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(marker, Encoding.UTF8)) as Dictionary<string, object>;
                object found;
                if(data == null || !data.TryGetValue("PlanFingerprint", out found) || !(found is string) || (string)found != fingerprint) throw new InvalidDataException("目录已经属于另一套构建清单，请选择其他保存根目录。");
                return;
            }
            if(Directory.EnumerateFileSystemEntries(directory).Any(path => !String.Equals(Path.GetFileName(path), LockName, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("目标目录已有文件但没有匹配的版本标识，已保留全部文件。请选择空的保存目录。");
            string json = new JavaScriptSerializer().Serialize(new { SchemaVersion = 1, AppId = "570", BuildId = plan.BuildId, PlanFingerprint = fingerprint, CreatedAtUtc = DateTimeOffset.UtcNow.ToString("o") });
            using(var stream = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { byte[] bytes = new UTF8Encoding(false).GetBytes(json); stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
        }
        internal static void ValidatePreparedPaths(VersionEntry entry)
        {
            foreach(string path in entry.Files.Select(file => file.Path).Concat(entry.Directories)) {
                string clean = VersionCatalog.NormalizeRelative(path);
                if(clean.Equals(MarkerName, StringComparison.OrdinalIgnoreCase) || clean.Equals(LockName, StringComparison.OrdinalIgnoreCase) || clean.Equals(MetadataName, StringComparison.OrdinalIgnoreCase) || clean.StartsWith(MetadataName + "/", StringComparison.OrdinalIgnoreCase) || clean.Equals(".DepotDownloader", StringComparison.OrdinalIgnoreCase) || clean.StartsWith(".DepotDownloader/", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(clean, @"^manifest_[0-9]+_[0-9]+\.txt$", RegexOptions.IgnoreCase)) throw new InvalidDataException("清单路径与下载元数据冲突：" + clean);
            }
        }
        private static string FindExecutable(VersionEntry entry)
        {
            foreach(string path in new[] { "game/bin/win64/dota2.exe", "game/bin/win32/dota2.exe", "dota.exe", "bin/dota.exe" }) if(entry.Files.Any(file => String.Equals(file.Path, path, StringComparison.OrdinalIgnoreCase))) return path;
            return null;
        }
        internal static void DeriveClientVersion(VersionEntry entry, string directory)
        {
            string relative = entry.Files.Where(file => file.Path.Equals("game/dota/steam.inf", StringComparison.OrdinalIgnoreCase) || file.Path.Equals("dota/steam.inf", StringComparison.OrdinalIgnoreCase)).Select(file => file.Path).FirstOrDefault();
            if(relative == null) throw new InvalidDataException("历史清单缺少 steam.inf，无法确认 ClientVersion。");
            VerifyOneFile(entry, directory, relative);
            string path = VersionCatalog.SafePath(directory, relative);
            if(new FileInfo(path).Length > 65536) throw new InvalidDataException("steam.inf 超过正常大小。");
            Match match = Regex.Match(File.ReadAllText(path), @"(?im)^\s*ClientVersion\s*=\s*([1-9][0-9]*)\s*$");
            if(!match.Success) throw new InvalidDataException("已下载的 steam.inf 没有可识别的 ClientVersion。");
            entry.ClientVersion = match.Groups[1].Value;
        }
        private static IEnumerable<string> CriticalFiles(VersionEntry entry)
        {
            yield return entry.Executable;
            bool source2 = entry.Executable.StartsWith("game/", StringComparison.OrdinalIgnoreCase);
            string architecture = entry.Executable.IndexOf("win64", StringComparison.OrdinalIgnoreCase) >= 0 ? "win64" : "win32";
            foreach(string path in source2 ? new[] { "game/dota/bin/" + architecture + "/client.dll", "game/dota/bin/" + architecture + "/server.dll" } : new[] { "dota/bin/client.dll", "dota/bin/server.dll" }) {
                if(!entry.Files.Any(file => String.Equals(file.Path, path, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("完整清单缺少关键客户端文件：" + path);
                yield return path;
            }
        }
        private static void VerifyOneFile(VersionEntry entry, string directory, string relative)
        {
            FileEntry expected = entry.Files.FirstOrDefault(file => String.Equals(file.Path, relative, StringComparison.OrdinalIgnoreCase));
            if(expected == null) throw new InvalidDataException("文件不在原始清单中：" + relative);
            string path = VersionCatalog.SafePath(directory, relative);
            if(!File.Exists(path) || new FileInfo(path).Length != expected.Size) throw new InvalidDataException("关键文件缺失或大小不一致：" + relative);
            using(var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) using(SHA1 hash = SHA1.Create()) if(!String.Equals(Hex(hash.ComputeHash(stream)), expected.Sha1, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("关键文件 SHA1 不一致：" + relative);
        }
        private static void RequireDownloadTargetNotRunning(string directory)
        {
            var games = new List<Process>();
            try {
                games.AddRange(Process.GetProcessesByName("dota2"));
                games.AddRange(Process.GetProcessesByName("dota"));
                RequireDownloadTargetNotRunning(directory, games.Select(game => new Func<string>(delegate { return game.MainModule.FileName; })));
            }
            finally { foreach(Process game in games) game.Dispose(); }
        }
        internal static void RequireDownloadTargetNotRunning(string directory, IEnumerable<Func<string>> executablePaths)
        {
            if(executablePaths == null) throw new ArgumentNullException("executablePaths");
            // Validate the target before creating any directory, marker or lock file.
            VersionCatalog.SafePath(directory, ".running-game-path-check");
            foreach(Func<string> readExecutablePath in executablePaths) {
                bool withinTarget;
                try { withinTarget = IsExecutableInDirectory(readExecutablePath(), directory); }
                catch(Exception ex) { throw new InvalidOperationException("无法可靠确认正在运行的 Dota 2 所在目录，已停止写入下载目录。请关闭该游戏后重试。", ex); }
                if(withinTarget) throw new InvalidOperationException("目标历史客户端正在运行，请先退出该客户端再继续下载。其他目录中的 Dota 2 可以保持运行。");
            }
        }
        internal static bool IsExecutableInDirectory(string executablePath, string directory)
        {
            if(String.IsNullOrWhiteSpace(executablePath)) throw new InvalidDataException("正在运行的游戏缺少可执行文件路径。");
            VersionCatalog.SafePath(directory, ".running-game-path-check");
            string prefix = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            // Reuse the existing path-chain checks so a junction cannot hide a
            // running target client under an apparently unrelated directory.
            string executable = VersionCatalog.SafePath(Path.GetDirectoryName(executablePath), Path.GetFileName(executablePath));
            return executable.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        private static void RequireNoGame()
        {
            Process[] games = Process.GetProcessesByName("dota2").Concat(Process.GetProcessesByName("dota")).ToArray();
            bool running = games.Length > 0; foreach(Process game in games) game.Dispose();
            if(running) throw new InvalidOperationException("已有 Dota 2 正在运行，请先退出游戏再启动历史客户端。");
        }
        private static void SaveJson(string path, object value)
        {
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllText(temp, new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue, RecursionLimit = 128 }.Serialize(value), new UTF8Encoding(false)); if(File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path); }
            finally { if(File.Exists(temp)) File.Delete(temp); }
        }
        private static string Hex(byte[] hash) { return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant(); }
        private static string FormatBytes(long bytes) { return (bytes / (1024D * 1024D * 1024D)).ToString("0.00", CultureInfo.InvariantCulture) + " GiB"; }

        private bool BeginOperation(string message)
        {
            if(busy) return false;
            busy = true; cancellation = new CancellationTokenSource(); status.Text = message; progress.Value = 0; lock(progressLock) pendingProgress = null; UpdateButtons(); return true;
        }
        private void EndOperation()
        {
            FlushProgress(); busy = false;
            if(engine != null) { steamQrState.End(engine); engine.Dispose(); engine = null; }
            if(cancellation != null) { cancellation.Dispose(); cancellation = null; }
            ClearQr();
            if(steamQrState.RetryAllowed && !steamQrState.Pending) qrCaption.Text = "Steam 授权已结束。点击“刷新登录二维码”后重新扫码。\r\n已下载文件会保留。";
            UpdateButtons();
            if(closingAfterCancel) { steamQrState.CancelPending(); Close(); }
            else if(steamQrState.Pending) BeginInvoke((Action)(async delegate {
                if(IsDisposed || Disposing || closingAfterCancel || !steamQrState.TakePending()) return;
                await DownloadResolved(true);
            }));
        }
        private void CancelOperation(bool forQrRefresh = false)
        {
            if(!forQrRefresh) steamQrState.CancelPending();
            if(cancellation == null || cancellation.IsCancellationRequested) { UpdateButtons(); return; }
            // Cancellation can synchronously finish the operation. Set the
            // temporary message first so it cannot overwrite the final outcome.
            status.Text = syncingCatalog ? "正在停止资料同步，已核实断点会保留" : "正在停止任务，已下载文件会保留";
            cancellation.Cancel(); browser.CancelPending(); UpdateButtons();
        }
        private void InvalidateSelection()
        {
            if(busy) return;
            restoredSavedPlan = false; resolved = null; prepared = null; targetDirectory = null; verifiedFingerprint = null;
            planText.Text = "匹配规则：" + QueryRuleDescription() + "\r\n\r\n查询条件已更改，请点击“仅查找”或“查找并下载”重新匹配。";
            status.Text = "等待查询"; detail.Text = "将按当前选择的查询方式和时间方向匹配版本。";
            UpdateButtons();
        }
        private void UpdateButtons()
        {
            bool idle = !busy && !steamQrState.Pending;
            run.Enabled = resolveOnly.Enabled = idle; cancel.Enabled = steamQrState.Pending || (busy && cancellation != null && !cancellation.IsCancellationRequested);
            updateCatalog.Enabled = idle;
            login.Enabled = idle; continueBrowser.Enabled = !steamQrState.Pending && (browserReady || idle);
            continueBrowser.Text = browserReady ? "已完成验证，继续" : "初始化历史浏览器"; resume.Enabled = idle;
            queryMode.Enabled = queryValue.Enabled = rootDirectory.Enabled = selectDirectory.Enabled = account.Enabled = directSteam.Enabled = idle;
            timeDirection.Enabled = idle;
            download.Enabled = idle && resolved != null && resolved.Complete;
            verify.Enabled = idle && prepared != null && prepared.Files != null && prepared.Files.Count > 0 && targetDirectory != null;
            launch.Enabled = idle && prepared != null && resolved != null && verifiedFingerprint != null && verifiedFingerprint == Fingerprint(resolved);
            open.Enabled = idle && targetDirectory != null && Directory.Exists(targetDirectory);
            refreshSteamQr.Enabled = !syncingCatalog && !closingAfterCancel && (cancellation == null || !cancellation.IsCancellationRequested) && steamQrState.CanRefresh(busy, resolved != null && resolved.Complete);
        }
        private void ChooseDirectory() { using(var dialog = new FolderBrowserDialog { Description = "选择各历史版本的保存根目录（完整路径须为英文 ASCII，不能含中文）", SelectedPath = rootDirectory.Text }) if(dialog.ShowDialog(this) == DialogResult.OK) rootDirectory.Text = dialog.SelectedPath; }
        private void OpenDirectory() { try { if(targetDirectory != null && Directory.Exists(targetDirectory)) Process.Start(new ProcessStartInfo { FileName = targetDirectory, UseShellExecute = true, Verb = "open" }); } catch(Exception ex) { ShowError(ex); } }
        private void OnClosing(object sender, FormClosingEventArgs e)
        {
            if(!busy) { steamQrState.CancelPending(); return; } e.Cancel = true; if(closingAfterCancel) return;
            if(MessageBox.Show(this, syncingCatalog ? "停止资料同步并关闭？原数据库和已核实断点会保留。" : "停止当前任务并关闭？已下载的文件会保留。", "关闭历史客户端下载", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes) { closingAfterCancel = true; CancelOperation(); }
        }
        private void ShowCancelled() { lock(progressLock) pendingProgress = null; verifiedFingerprint = null; status.Text = syncingCatalog ? "资料同步已停止" : "任务已停止"; detail.Text = syncingCatalog ? "原数据库仍可查询，已核实断点保留；未完成的同步不会标记为最新。" : "没有将未完成的任务标记为成功。已下载的文件会保留。"; AppendLog(detail.Text); }
        private void ShowError(Exception ex) { lock(progressLock) pendingProgress = null; verifiedFingerprint = null; status.Text = "操作未完成"; detail.Text = ex is DownloadFailureException ? DownloadFailureMessage : CleanLog(ex.Message); AppendLog("错误：" + ex.Message); if(!busy) UpdateButtons(); }
        private void QueueProgress(ProgressInfo value) { if(value != null) lock(progressLock) pendingProgress = value; }
        private void ReceiveEngineProgress(DownloadEngine source, ProgressInfo value)
        {
            if(value == null) return;
            bool waiting = steamQrState.Waiting, retry = steamQrState.RetryAllowed;
            if(!steamQrState.Observe(source, value.Phase)) return;
            lock(progressLock) { if(steamQrState.IsCurrent(source)) pendingProgress = value; }
            // Auth state is independent of the progress timer's coalesced last value.
            if(waiting != steamQrState.Waiting || retry != steamQrState.RetryAllowed) Post(delegate {
                if(!steamQrState.IsCurrent(source)) return;
                if(!steamQrState.Waiting) ClearQr();
                UpdateButtons();
            });
        }
        private void FlushProgress()
        {
            ProgressInfo value; lock(progressLock) { value = pendingProgress; pendingProgress = null; }
            if(value == null || !busy || cancellation == null || cancellation.IsCancellationRequested) return;
            double percent = Double.IsNaN(value.Percent) || Double.IsInfinity(value.Percent) ? 0 : Math.Max(0, Math.Min(100, value.Percent)); progress.Value = (int)percent;
            string phase = value.Phase ?? "";
            if((phase == "authenticated" || phase == "manifest_authenticated") && !steamQrState.Waiting) { ClearQr(); qrCaption.Text = "Steam 已授权。"; if(inspector.SelectedTab == qrTab) inspector.SelectedTab = planTab; }
            if(phase == "authentication" || phase == "manifest_authentication") status.Text = "等待 Steam 扫码授权";
            else if(phase.StartsWith("manifest_", StringComparison.Ordinal)) status.Text = "获取 Steam 原始清单";
            else if(phase == "文件校验") status.Text = "逐文件校验原版 SHA1 · " + percent.ToString("0.0") + "%";
            else if(phase == "downloading" || phase == "preparing" || phase == "authenticated") status.Text = "下载原版客户端 · " + percent.ToString("0.0") + "%（估计）";
            detail.Text = CleanLog(value.Message ?? value.Phase ?? "") + (String.IsNullOrWhiteSpace(value.CurrentFile) ? "" : " · " + value.CurrentFile);
        }
        private void ReceiveQr(DownloadEngine source, Bitmap bitmap)
        {
            if(bitmap == null) return;
            if(IsDisposed || Disposing || !IsHandleCreated || !steamQrState.CanShowQr(source)) { bitmap.Dispose(); return; }
            try { BeginInvoke((Action)delegate { if(!busy || cancellation == null || cancellation.IsCancellationRequested || !steamQrState.CanShowQr(source)) { bitmap.Dispose(); return; } ClearQr(); qr.Image = bitmap; qrCaption.Text = "请用 Steam 手机应用扫码授权。\r\n二维码失效时，可点击下方按钮刷新。"; inspector.SelectedTab = qrTab; status.Text = "等待 Steam 扫码授权"; UpdateButtons(); }); }
            catch(InvalidOperationException) { bitmap.Dispose(); }
        }
        private void ClearQr() { Image previous = qr.Image; qr.Image = null; if(previous != null) previous.Dispose(); }
        private void Post(Action action) { if(IsDisposed || Disposing || !IsHandleCreated) return; try { BeginInvoke((Action)delegate { if(!IsDisposed && !Disposing) action(); }); } catch(InvalidOperationException) { } }
        private static string CleanLog(string value)
        {
            if(String.IsNullOrEmpty(value)) return "";
            value = Regex.Replace(value, @"https?://[^\s]*(?:s\.team/q|steamcommunity\.com/qrlogin)[^\s]*", "[Steam 授权地址已隐藏]", RegexOptions.IgnoreCase);
            return Regex.Replace(value, "(?i)\\b(access[_ -]?token|refresh[_ -]?token|password|auth[_ -]?token|guard[_ -]?code)[\"']?\\s*[:=]\\s*[\"']?[^\\s,;\"']+", "$1=[已隐藏]");
        }
        private void AppendLog(string message)
        {
            if(String.IsNullOrWhiteSpace(message)) return; if(log.TextLength > 160000) log.Text = log.Text.Substring(log.TextLength - 90000);
            string line = "[" + DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).ToString("HH:mm:ss") + "] " + CleanLog(message).TrimEnd() + Environment.NewLine;
            log.AppendText(line); log.SelectionStart = log.TextLength; log.ScrollToCaret();
            if(!String.IsNullOrWhiteSpace(diagnosticDirectory)) { try { Directory.CreateDirectory(diagnosticDirectory); File.AppendAllText(Path.Combine(diagnosticDirectory, "progress.log"), line, new UTF8Encoding(false)); } catch(IOException) { } catch(UnauthorizedAccessException) { } }
        }
    }
}
