using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DotaArchive
{
    // This adapter only operates the displayed browser DOM. Authentication stays
    // inside WebView2; no cookies, tokens, password values or browser profiles are read.
    public sealed class BrowserSession : IHistoryBrowser, IHistoryDateBrowser, IHistoryDateNavigationBrowser, IDisposable
    {
        private readonly WebView2 browser;
        private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 128 * 1024 * 1024 };
        private readonly SemaphoreSlim operationGate = new SemaphoreSlim(1, 1);
        private readonly int uiThread;
        private readonly string profileDirectory;
        private readonly SiteCooldown cooldown;
        private CancellationTokenSource pending;
        private TaskCompletionSource<bool> resume;
        private Task initialization;
        private bool disposed;
        private bool navigationBusy;
        private string navigationError;
        private string lastStatus;
        private bool rateLimited;
        private DateTime nextHistoryActionUtc = DateTime.MinValue;
        private const string RateLimitMessage = "SteamDB 暂时限制了请求（HTTP 429）。已停止自动查询，不会自动重试。";
        private const string ForbiddenMessage = "SteamDB 当前返回 HTTP 403（Forbidden），已停止本次查询。请查看内置浏览器提示，确认访问恢复后再手动查询；软件不会自动重试。";

        public BrowserSession() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DotaArchive", "BrowserPrototype", "SteamDBProfile")) { }

        public BrowserSession(string userDataFolder)
        {
            if (String.IsNullOrWhiteSpace(userDataFolder)) throw new ArgumentException("需要专用浏览器数据目录。", "userDataFolder");
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) throw new InvalidOperationException("内置浏览器必须在 STA 窗口线程创建。");
            uiThread = Thread.CurrentThread.ManagedThreadId;
            profileDirectory = Path.GetFullPath(userDataFolder);
            cooldown = new SiteCooldown(profileDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".cooldown.json");
            browser = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = System.Drawing.Color.White };
        }

        public Control View { get { return browser; } }
        public bool WaitingForUser { get { return resume != null; } }
        public string UserDataFolder { get { return profileDirectory; } }
        public event Action<string> StatusChanged;

        public Task InitializeAsync()
        {
            CheckUiThread();
            // A failed initialization must not poison this session forever. After
            // an older incompatible window closes, the next user operation retries.
            if (CanInitialize(initialization)) initialization = InitializeCoreAsync();
            return initialization;
        }

        private static bool CanInitialize(Task previous) { return previous == null || previous.IsFaulted || previous.IsCanceled; }
        private static bool IsIncompatibleEnvironment(Exception error)
        {
            for (Exception current = error; current != null; current = current.InnerException)
                if (current.HResult == unchecked((int)0x8007139F)) return true;
            return false;
        }

        private async Task InitializeCoreAsync()
        {
            try
            {
                SetStatus("正在初始化内置浏览器……");
                // Use installed Evergreen Runtime and a dedicated application profile.
                // No custom user agent, browser arguments, proxy or imported cookies.
                string version = CoreWebView2Environment.GetAvailableBrowserVersionString();
                if (String.IsNullOrWhiteSpace(version)) throw new InvalidOperationException("未找到 Microsoft Edge WebView2 Runtime。");
                Directory.CreateDirectory(profileDirectory);
                // Both production and probe use this exact option set, manifest
                // (system DPI aware) and .NET configuration when sharing this UDF.
                var options = new CoreWebView2EnvironmentOptions { ExclusiveUserDataFolderAccess = false };
                CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(null, profileDirectory, options);
                if (!String.Equals(Path.GetFullPath(environment.UserDataFolder).TrimEnd(Path.DirectorySeparatorChar), profileDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("WebView2 的系统设置覆盖了专用数据目录。为避免共用其他浏览器会话，原型已停止初始化。");
                await browser.EnsureCoreWebView2Async(environment);
                browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
                browser.CoreWebView2.Settings.IsStatusBarEnabled = true;
                browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
                browser.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
                browser.CoreWebView2.Settings.IsGeneralAutofillEnabled = false;
                browser.CoreWebView2.NavigationStarting += OnNavigationStarting;
                browser.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
                browser.CoreWebView2.NewWindowRequested += OnNewWindowRequested;
                browser.CoreWebView2.DownloadStarting += delegate(object sender, CoreWebView2DownloadStartingEventArgs e)
                {
                    e.Cancel = true;
                    SetStatus("浏览器文件下载已取消；游戏文件由 DepotDownloader 下载。");
                };
                browser.CoreWebView2.ProcessFailed += delegate(object sender, CoreWebView2ProcessFailedEventArgs e)
                {
                    navigationError = "浏览器进程异常：" + e.ProcessFailedKind.ToString() + "。请重新打开原型。";
                    SetStatus(navigationError);
                    CancelPending();
                };
                SetStatus("内置浏览器已就绪。");
            }
            catch (WebView2RuntimeNotFoundException ex)
            {
                throw new InvalidOperationException("系统未安装 Microsoft Edge WebView2 Runtime，请从微软官方网站安装后重试。原型不会自动安装系统组件。", ex);
            }
            catch (Exception ex)
            {
                if (!IsIncompatibleEnvironment(ex)) throw;
                throw new InvalidOperationException("专用浏览器会话仍由采用不同显示配置的旧窗口占用（0x8007139F）。请正常关闭旧版原型或浏览器实验窗口，再重试当前操作。登录资料已保留，无需删除浏览器目录。", ex);
            }
        }

        public async Task<HistoryPageSnapshot> NavigateAsync(string url, CancellationToken token)
        {
            CheckUiThread();
            Uri target = RequireAllowedUri(url);
            await InitializeAsync();
            await operationGate.WaitAsync(token);
            try
            {
                using (pending = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    pending.Token.ThrowIfCancellationRequested();
                    ThrowIfRateLimited();
                    if (target.Host == "steamdb.info" && !IsAuthPath(target.AbsolutePath)) await PaceHistoryAsync(pending.Token);
                    navigationError = null;
                    navigationBusy = true;
                    browser.CoreWebView2.Navigate(target.AbsoluteUri);
                    return await WaitForContentAsync(IsSteamHost(target.Host) || IsAuthPath(target.AbsolutePath) ? null : target, pending.Token);
                }
            }
            finally { pending = null; resume = null; operationGate.Release(); }
        }

        public async Task OpenHistoryForDateAsync(CancellationToken token)
        {
            CheckUiThread();
            await InitializeAsync();
            await operationGate.WaitAsync(token);
            try
            {
                using (pending = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    pending.Token.ThrowIfCancellationRequested();
                    ThrowIfRateLimited();
                    var target = new Uri("https://steamdb.info/app/570/history/?filterkey=2");
                    if (!navigationBusy && SamePage(target, browser.CoreWebView2.Source))
                    {
                        // Reading the already displayed DOM sends no request. Keep
                        // the selected historical neighborhood instead of resetting
                        // it to today's page for every date lookup. Capture still
                        // checks the persistent cooldown and visible restrictions.
                        CaptureState current = await CaptureStateAsync(pending.Token);
                        if (current.AccessDenied) throw new InvalidOperationException("SteamDB 当前拒绝访问或限制请求。已停止读取，请查看内置浏览器提示。");
                        if (current.Authentication || current.Challenge)
                        {
                            // Let the user finish the existing challenge/login in
                            // place; never reload it as a way of passing this check.
                            await WaitForContentAsync(target, pending.Token, true);
                            return;
                        }
                        Dictionary<string, object> controls = await ReadJsonAsync(browser.CoreWebView2.ExecuteScriptAsync(HistoryDateControlsScript), pending.Token);
                        await CheckPageRestrictionsAsync(pending.Token);
                        if (CanReuseHistoryDatePage(target, browser.CoreWebView2.Source, current, navigationBusy, navigationError, controls))
                        {
                            SetStatus("已复用当前历史页面的日期控件；将直接提交目标日期。");
                            return;
                        }
                    }
                    await PaceHistoryAsync(pending.Token);
                    navigationError = null; navigationBusy = true;
                    browser.CoreWebView2.Navigate(target.AbsoluteUri);
                    // This result is only navigation readiness, never a historical
                    // data snapshot for a parser or a completed download plan.
                    await WaitForContentAsync(target, pending.Token, true);
                }
            }
            finally { pending = null; resume = null; operationGate.Release(); }
        }

        internal static bool CanReuseHistoryDatePage(Uri expected, string source, CaptureState state, bool navigating, string error, Dictionary<string, object> controls)
        {
            return expected != null && state != null && state.Snapshot != null && controls != null &&
                !navigating && String.IsNullOrEmpty(error) && SamePage(expected, source) && SamePage(expected, state.Snapshot.Url) &&
                state.HasMain && state.Ready && !state.Authentication && !state.Challenge && !state.AccessDenied &&
                GetBool(controls, "hasHistory") && GetBool(controls, "controlsReady");
        }

        public async Task<HistoryPageSnapshot> CaptureAsync(CancellationToken token)
        {
            CheckUiThread();
            await InitializeAsync();
            token.ThrowIfCancellationRequested();
            CaptureState state = await CaptureStateAsync(token);
            if (state.Authentication) throw new InvalidOperationException("当前为登录页面，请手动完成登录并返回 SteamDB；程序不会读取登录内容。");
            if (state.Challenge) throw new InvalidOperationException("当前为浏览器验证页面，请手动完成验证后继续。");
            if (state.AccessDenied) throw new InvalidOperationException("网站当前拒绝访问或限制请求，不能将此页面作为历史数据。");
            if (!state.HasMain || state.Loading || !state.Ready) throw new InvalidOperationException("历史记录尚未完整显示，请等待加载完成后再读取。");
            return state.Snapshot;
        }

        public async Task<HistoryPageSnapshot> ClickAsync(string selector, CancellationToken token)
        {
            CheckUiThread();
            if (String.IsNullOrWhiteSpace(selector) || selector.Length > 1024) throw new ArgumentException("无效的页面元素选择器。", "selector");
            await InitializeAsync();
            await operationGate.WaitAsync(token);
            try
            {
                using (pending = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    CaptureState current = await CaptureStateAsync(pending.Token);
                    if (current.Authentication || current.Challenge) throw new InvalidOperationException("登录或验证必须由你手动完成，不能自动点击。");
                    Uri currentUri;
                    if (!Uri.TryCreate(current.Snapshot.Url, UriKind.Absolute, out currentUri) || currentUri.Host != "steamdb.info") throw new InvalidOperationException("只能操作 SteamDB 历史记录页面。");
                    bool historyMore = selector == "#js-history-load" && currentUri.AbsolutePath.TrimEnd('/') == "/app/570/history";
                    if (historyMore) await PaceHistoryAsync(pending.Token);
                    string historyBefore = historyMore ? GetString(await ReadJsonAsync(browser.CoreWebView2.ExecuteScriptAsync(HistorySignatureScript), pending.Token), "signature") : null;
                    string script = @"(function(selector,phase,originalCount) {
                        if(location.hostname!=='steamdb.info') return {ok:false,reason:'页面已经离开 SteamDB'};
                        var root=document.querySelector('#main')||document.querySelector('main');
                        if(!root) return {ok:false,reason:'页面还没有历史记录内容'};
                        var el=root.querySelector(selector);
                        if(!el) return {ok:false,reason:'页面元素不存在或布局已变化'};
                        if(!el.matches('a,button') || el.closest('form')) return {ok:false,reason:'不允许自动提交表单或操作此元素'};
                        if(el.tagName==='A') { var link=new URL(el.href,location.href); if(link.protocol!=='https:'||link.hostname!=='steamdb.info'||/\/(login|logout|account|openid)(\/|$)/i.test(link.pathname)) return {ok:false,reason:'此链接不属于可自动操作的历史记录'}; }
                        if(/log\s*in|sign\s*in|verify|captcha|验证|登录/i.test(el.textContent)) return {ok:false,reason:'登录或验证需要手动操作'};
                        var count=root.querySelectorAll('#history .panel-history').length;
                        function visible(node){if(!node)return false;for(var p=node;p;p=p.parentElement){var c=getComputedStyle(p);if(p.hidden||c.display==='none'||c.visibility==='hidden')return false;}var r=node.getBoundingClientRect();return r.width>0&&r.height>0;}
                        if(phase==='scroll'){el.scrollIntoView({block:'center'});return {ok:true,count:count};}
                        if(count!==originalCount||visible(root.querySelector('#js-history-loading')))return {ok:true,changed:true};
                        if(!visible(el))return {ok:true,visible:false,reason:'网站当前隐藏了继续加载按钮或其上层容器，请使用页面提供的日期筛选继续读取。'};
                        if(el.matches('[disabled],[aria-disabled=true]'))return {ok:true,busy:true,reason:'网站正在处理上一批历史请求，请等待加载完成。'};
                        el.click(); return {ok:true,clicked:true};
                    })";
                    Dictionary<string, object> initial = await ReadJsonAsync(browser.CoreWebView2.ExecuteScriptAsync(script + "(" + json.Serialize(selector) + ",'scroll',0)"), pending.Token);
                    if (!GetBool(initial, "ok")) throw new InvalidOperationException(GetString(initial, "reason"));
                    int initialCount = Convert.ToInt32(initial["count"]);
                    // Scrolling can itself trigger SteamDB's normal auto-load.
                    // Give that request time to become visible before any click.
                    await Task.Delay(historyMore ? 1500 : 120, pending.Token);
                    Dictionary<string, object> result = null;
                    Stopwatch clickWait = Stopwatch.StartNew();
                    for (int attempt = 0; ; attempt++)
                    {
                        await CheckPageRestrictionsAsync(pending.Token);
                        result = await ReadJsonAsync(browser.CoreWebView2.ExecuteScriptAsync(script + "(" + json.Serialize(selector) + ",'click'," + initialCount.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")"), pending.Token);
                        if (!GetBool(result, "ok")) throw new InvalidOperationException(GetString(result, "reason"));
                        if (GetBool(result, "clicked") || GetBool(result, "changed")) break;
                        if (clickWait.Elapsed > TimeSpan.FromSeconds(120) || (!GetBool(result, "busy") && attempt >= 7)) break;
                        await Task.Delay(200, pending.Token);
                    }
                    if (!GetBool(result, "clicked") && !GetBool(result, "changed") && !(historyMore && !GetBool(result, "busy"))) throw new InvalidOperationException(GetString(result, "reason"));
                    if (historyMore)
                    {
                        await WaitForHistoryAdvanceAsync(historyBefore, pending.Token);
                        return await WaitForContentAsync(currentUri, pending.Token);
                    }
                    await Task.Delay(400, pending.Token);
                    return await WaitForContentAsync(null, pending.Token);
                }
            }
            finally { pending = null; resume = null; operationGate.Release(); }
        }

        public async Task<HistoryPageSnapshot> LoadHistoryBeforeAsync(DateTime date, CancellationToken token)
        {
            CheckUiThread();
            await InitializeAsync();
            await operationGate.WaitAsync(token);
            string observationId = Guid.NewGuid().ToString("N");
            try
            {
                using (pending = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    CaptureState current = await CaptureStateAsync(pending.Token);
                    if (current.Authentication || current.Challenge) throw new InvalidOperationException("请先手动完成登录或验证，再使用历史日期筛选。");
                    Dictionary<string, object> initial = await ReadJsonAsync(browser.CoreWebView2.ExecuteScriptAsync(HistoryDateControlsScript), pending.Token);
                    if (NeedsInitialHistoryFallback(current.Loading, initial))
                    {
                        // A blank list with a loading placeholder cannot prove that
                        // an earlier automatic request has finished. Complete that
                        // one ordinary page before starting the date request, so a
                        // late current-day response cannot be called the target.
                        SetStatus("初始历史列表为空且仍显示加载状态，正在通过页面加载控件完成这一批，再提交目标日期……");
                        await WaitForContentAsync(new Uri("https://steamdb.info/app/570/history/?filterkey=2"), pending.Token);
                    }
                    await PaceHistoryAsync(pending.Token);
                    string dateValue = date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                    string filterScript = @"(function(phase,value){
                        if(location.hostname!=='steamdb.info'||!/^\/app\/570\/history\/?$/.test(location.pathname)) return {ok:false,reason:'日期筛选只允许在 Dota 2 历史页面使用'};
                        var root=document.querySelector('#history'),input=document.querySelector('#js-filter-history-date');
                        function visible(node){if(!node)return false;for(var p=node;p;p=p.parentElement){var c=getComputedStyle(p);if(p.hidden||c.display==='none'||c.visibility==='hidden')return false;}var r=node.getBoundingClientRect();return r.width>0&&r.height>0;}
                        if(!root||!input||input.type!=='date')return {ok:false,reason:'网站的日期筛选控件已经变化'};
                        if(phase==='reveal'){
                            if(visible(input))return {ok:true};
                            var toggle=document.querySelector('#js-filter-history-date-toggle');
                            if(!visible(toggle)||toggle.disabled)return {ok:false,reason:'网站没有显示可操作的日期筛选开关'};
                            toggle.scrollIntoView({block:'center'});toggle.click();return {ok:true};
                        }
                        var button=document.querySelector('#js-filter-history-date-load');
                        if(!button)return {ok:false,reason:'网站的日期加载按钮已经变化'};
                        if(!visible(input)||input.disabled)return {ok:true,waiting:true};
                        if(phase==='ready')return {ok:true,waiting:!visible(button)||button.disabled||visible(root.querySelector('#js-history-loading'))};
                        if(phase==='fill'){
                            input.value=value;
                            if(input.value!==value)return {ok:false,reason:'日期筛选控件没有接受指定日期'};
                            input.dispatchEvent(new Event('input',{bubbles:true}));input.dispatchEvent(new Event('change',{bubbles:true}));
                            if(button)button.scrollIntoView({block:'center'});return {ok:true};
                        }
                        if(phase!=='submit')return {ok:false,reason:'不支持的日期筛选操作'};
                        if(!visible(button)||button.disabled)return {ok:true,waiting:true};
                        button.click();return {ok:true};
                    })";
                    foreach (string phase in new[] { "reveal", "fill", "submit" })
                    {
                        string action = filterScript + "(" + json.Serialize(phase) + "," + json.Serialize(dateValue) + ")";
                        // Install immediately before the ordinary visible button click,
                        // in the same script task. Do not observe typing/reveal effects as
                        // completion of a later request.
                        if (phase == "submit") action = "(function(){var ready=" + filterScript + "('ready'," + json.Serialize(dateValue) + ");if(!ready.ok||ready.waiting)return ready;var started=" + HistoryDateObservationScript + "('install'," + json.Serialize(observationId) + ");if(!started.ok)return started;return " + action + ";})()";
                        var controlsWait = Stopwatch.StartNew();
                        bool reportedControlsWait = false;
                        while (true)
                        {
                            pending.Token.ThrowIfCancellationRequested();
                            if (!String.IsNullOrEmpty(navigationError)) throw new InvalidOperationException(navigationError);
                            await CheckPageRestrictionsAsync(pending.Token);
                            Dictionary<string, object> result = await ReadJsonAsync(browser.CoreWebView2.ExecuteScriptAsync(action), pending.Token);
                            if (!GetBool(result, "ok")) throw new InvalidOperationException(GetString(result, "reason"));
                            if (!GetBool(result, "waiting")) break;
                            if (!reportedControlsWait) { SetStatus("正在等待日期控件展开或历史加载完成……"); reportedControlsWait = true; }
                            // Reveal is clicked once. Subsequent polls only inspect
                            // visible/enabled controls until the page finishes expanding
                            // them or its preceding history request becomes idle.
                            if (controlsWait.Elapsed > TimeSpan.FromSeconds(120)) throw new TimeoutException("日期筛选控件没有在 120 秒内变为可操作状态。请查看页面中的加载或登录提示后重试。");
                            await Task.Delay(250, pending.Token);
                        }
                        await Task.Delay(150, pending.Token);
                    }
                    SetStatus("正在等待网页完成 " + dateValue + " 的日期筛选……");
                    var refreshWait = Stopwatch.StartNew();
                    var refreshTracker = new HistoryDateRefreshTracker();
                    while (true)
                    {
                        pending.Token.ThrowIfCancellationRequested();
                        if (!String.IsNullOrEmpty(navigationError)) throw new InvalidOperationException(navigationError);
                        await CheckPageRestrictionsAsync(pending.Token);
                        Dictionary<string, object> changed = await ReadJsonAsync(browser.CoreWebView2.ExecuteScriptAsync(HistoryDateObservationScript + "('read'," + json.Serialize(observationId) + ")"), pending.Token);
                        if (GetBool(changed, "failed")) throw new InvalidOperationException("网页报告历史加载失败，已停止本次日期筛选；没有把旧记录当作筛选结果。请查看内置浏览器提示后重试。");
                        if (refreshTracker.Observe(changed, refreshWait.Elapsed)) break;
                        if (refreshWait.Elapsed > TimeSpan.FromSeconds(120)) throw new TimeoutException("日期筛选没有在 120 秒内确认完成本次加载。请查看页面中的网络或登录提示后重试；不会把尚未刷新的旧批次当作结果。");
                        await Task.Delay(250, pending.Token);
                    }
                    await ReadJsonAsync(browser.CoreWebView2.ExecuteScriptAsync(HistoryDateObservationScript + "('clear'," + json.Serialize(observationId) + ")"), pending.Token);
                    return await WaitForContentAsync(new Uri("https://steamdb.info/app/570/history/?filterkey=2"), pending.Token);
                }
            }
            finally
            {
                // C# 5 cannot await in finally. Queue a matching-id cleanup on errors
                // and cancellation too; normal completion already awaited cleanup.
                CleanupDateObservation(observationId);
                pending = null; resume = null; operationGate.Release();
            }
        }

        private void CleanupDateObservation(string observationId)
        {
            try
            {
                if (browser.CoreWebView2 == null) return;
                Task cleanup = browser.CoreWebView2.ExecuteScriptAsync(HistoryDateObservationScript + "('clear'," + json.Serialize(observationId) + ")");
                cleanup.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            }
            catch { /* Navigation/disposal destroys the old document and its observer. */ }
        }

        // Temporary observation of the displayed history only. A date with no newer
        // changes legitimately returns the same IDs, but the old panel objects must
        // have been replaced. A loading cycle alone also happens after failed XHRs.
        // No fetch interception, hidden endpoint, form value or authentication data.
        private const string HistoryDateObservationScript = @"(function(phase,id){
            var key='__dotaArchiveHistoryDateObservation',existing=window[key];
            if(phase==='clear'){if(existing&&existing.id===id){existing.observer.disconnect();delete window[key];}return {ok:true};}
            if(location.hostname!=='steamdb.info'||!/^\/app\/570\/history\/?$/.test(location.pathname))return {ok:false,active:false,reason:'日期筛选页面已改变'};
            function visible(node){if(!node)return false;for(var p=node;p;p=p.parentElement){var c=getComputedStyle(p);if(p.hidden||c.display==='none'||c.visibility==='hidden')return false;}var r=node.getBoundingClientRect();return r.width>0&&r.height>0;}
            function root(){return document.querySelector('#history');}
            if(phase==='install'){
                if(existing){existing.observer.disconnect();delete window[key];}
                var initial=root();
                if(!initial||visible(initial.querySelector('#js-history-loading')))return {ok:false,reason:'历史页面仍在加载，请等待本次加载结束再选择日期'};
                var originalPanels=Array.from(initial.querySelectorAll('.panel-history'));
                var state={id:id,original:initial,originalPanels:originalPanels,originalResult:initial.querySelector('.history-container'),removed:false,added:false,resultChanged:false,lastLoading:false,revision:0};
                function panelIn(node){return node&&node.nodeType===1&&(node.matches('.panel-history')||!!node.querySelector('.panel-history'));}
                state.sample=function(){
                    var current=root(),loading=!!(current&&visible(current.querySelector('#js-history-loading')));
                    if(loading!==state.lastLoading){state.revision++;state.lastLoading=loading;}
                    if(current&&current!==state.original){state.removed=true;state.added=!!current.querySelector('.panel-history');state.resultChanged=true;}
                    if(current&&state.originalResult&&current.querySelector('.history-container')!==state.originalResult)state.resultChanged=true;
                    return {current:current,loading:loading};
                };
                state.observer=new MutationObserver(function(records){
                    var touched=false;
                    records.forEach(function(record){if(record.type!=='childList')return;
                        if(state.originalResult&&record.target===state.originalResult&&(record.addedNodes.length||record.removedNodes.length)){state.resultChanged=true;touched=true;}
                        Array.from(record.removedNodes||[]).forEach(function(node){if(originalPanels.some(function(panel){return node===panel||(node.nodeType===1&&node.contains(panel));})){state.removed=true;touched=true;}});
                        Array.from(record.addedNodes||[]).forEach(function(node){if(panelIn(node)){state.added=true;touched=true;}});
                    });
                    if(touched)state.revision++;
                    state.sample();
                });
                state.observer.observe(initial,{subtree:true,childList:true,attributes:true,attributeFilter:['hidden','class','style']});
                if(initial.parentElement)state.observer.observe(initial.parentElement,{childList:true});
                window[key]=state;return {ok:true};
            }
            if(!existing||existing.id!==id)return {active:false};
            var sampled=existing.sample(),current=sampled.current,panels=current?current.querySelectorAll('.panel-history'):[],button=document.querySelector('#js-filter-history-date-load');
            var first=panels.length?panels[0].getAttribute('data-changeid'):'',last=panels.length?panels[panels.length-1].getAttribute('data-changeid'):'';
            var failed=Array.from(document.querySelectorAll('[role=alert],.alert-danger,.flash-error')).some(function(notice){return visible(notice)&&!notice.closest('form')&&/failed to load history|error loading history/i.test(notice.innerText||'');});
            var oldPanelsGone=!!current&&existing.originalPanels.every(function(panel){return !current.contains(panel);});
            var replaced=panels.length>0&&existing.added&&oldPanelsGone&&(existing.originalPanels.length>0?existing.removed:existing.resultChanged);
            var result=current&&current.querySelector('.history-container');
            var explicitEmpty=panels.length===0&&result&&visible(result)&&/(^|\n)\s*No history(?: entries| records)?(?: found| available)?\.?\s*(\n|$)/i.test(result.innerText||'');
            var empty=!!(explicitEmpty&&existing.resultChanged&&oldPanelsGone&&!sampled.loading);
            return {active:true,hasHistory:!!current,loading:sampled.loading,busy:!!(button&&button.disabled),signIn:!!(current&&visible(current.querySelector('#js-history-signin'))),failed:failed,signature:panels.length+':'+first+':'+last,revision:existing.revision,completed:replaced||empty,empty:empty};
        })";

        internal sealed class HistoryDateRefreshTracker
        {
            private string stableState;
            private TimeSpan stableSince;
            internal bool Observe(Dictionary<string, object> state, TimeSpan elapsed)
            {
                if (!GetBool(state, "active") || !GetBool(state, "hasHistory") || !GetBool(state, "completed") || GetBool(state, "loading") || GetBool(state, "busy") || GetBool(state, "signIn") || GetBool(state, "failed")) { stableState = null; return false; }
                string signature = GetString(state, "signature");
                if (String.IsNullOrEmpty(signature)) { stableState = null; return false; }
                string version = signature + ":" + GetString(state, "revision");
                if (stableState != version) { stableState = version; stableSince = elapsed; return false; }
                return elapsed - stableSince >= TimeSpan.FromMilliseconds(500);
            }
        }

        private static bool NeedsInitialHistoryFallback(bool loading, Dictionary<string, object> controls)
        {
            return loading && GetBool(controls, "hasHistory") && !GetBool(controls, "hasPanels");
        }

        private const string HistoryDateControlsScript = @"(function(){
            if(location.hostname!=='steamdb.info'||!/^\/app\/570\/history\/?$/.test(location.pathname))return {controlsReady:false,hasHistory:false};
            function visible(node){if(!node)return false;for(var p=node;p;p=p.parentElement){var c=getComputedStyle(p);if(p.hidden||c.display==='none'||c.visibility==='hidden')return false;}var r=node.getBoundingClientRect();return r.width>0&&r.height>0;}
            var root=document.querySelector('#history'),input=document.querySelector('#js-filter-history-date'),button=document.querySelector('#js-filter-history-date-load'),toggle=document.querySelector('#js-filter-history-date-toggle');
            var ready=!!(root&&input&&input.type==='date'&&button&&(visible(input)?!input.disabled&&visible(button)&&!button.disabled:visible(toggle)&&!toggle.disabled));
            return {controlsReady:ready,hasHistory:!!root,hasPanels:!!(root&&root.querySelector('.panel-history'))};
        })()";

        private const string BringContentIntoViewScript = @"(function(){
            if(location.hostname!=='steamdb.info')return;
            var p=location.pathname,id=p.indexOf('/history/')>=0?'history':p.indexOf('/patchnotes/')>=0?'patchnotes':p.indexOf('/depots/')>=0?'depots':null;
            var root=id&&document.getElementById(id),el=root;if(!root)return;
            function rendered(node){if(!node)return false;for(var x=node;x;x=x.parentElement){var c=getComputedStyle(x);if(x.hidden||c.display==='none'||c.visibility==='hidden')return false;}return true;}
            if(id==='history'){var load=root.querySelector('#js-history-load');el=rendered(load)?load:root.querySelector('.history-container')||root.querySelector('#js-history-loading')||root;}
            el.scrollIntoView({block:id==='history'?'center':'start'});
        })()";

        private const string HistorySignatureScript = @"(function(){
            if(location.hostname!=='steamdb.info'||!/^\/app\/570\/history\/?$/.test(location.pathname))return {hasHistory:false};
            var root=document.querySelector('#history');if(!root)return {hasHistory:false};
            var panels=root.querySelectorAll('.panel-history'), first=panels.length?panels[0].getAttribute('data-changeid'):'',last=panels.length?panels[panels.length-1].getAttribute('data-changeid'):'';
            function visible(node){if(!node)return false;for(var p=node;p;p=p.parentElement){var c=getComputedStyle(p);if(p.hidden||c.display==='none'||c.visibility==='hidden')return false;}var r=node.getBoundingClientRect();return r.width>0&&r.height>0;}
            var load=root.querySelector('#js-history-load'),loading=root.querySelector('#js-history-loading'),signin=root.querySelector('#js-history-signin');
            return {hasHistory:true,loading:visible(loading),loadExists:!!load,loadVisible:visible(load),signIn:visible(signin),busy:!!(load&&(load.disabled||load.getAttribute('aria-disabled')==='true')),signature:panels.length+':'+first+':'+last};
        })()";

        // The site's fetch can start after a click has returned. A stable old DOM
        // is not completion. Only new change IDs, or a stable hidden continuation
        // control, let the resolver inspect a snapshot and choose date fallback.
        private async Task WaitForHistoryAdvanceAsync(string before, CancellationToken token)
        {
            var wait = Stopwatch.StartNew();
            var tracker = new HistoryAdvanceTracker(before);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (!String.IsNullOrEmpty(navigationError)) throw new InvalidOperationException(navigationError);
                await CheckPageRestrictionsAsync(token);
                Dictionary<string, object> state = await ReadJsonAsync(browser.CoreWebView2.ExecuteScriptAsync(HistorySignatureScript), token);
                if (!GetBool(state, "hasHistory") || GetBool(state, "signIn"))
                {
                    CaptureState current = await CaptureStateAsync(token);
                    if (current.Authentication || current.Challenge || GetBool(state, "signIn"))
                    {
                        if (GetBool(state, "signIn") && !current.Authentication && !current.Challenge)
                            await WaitForUserAsync("历史页面要求登录或验证。请在内置浏览器完成后点击“已完成验证，继续”。", token);
                        await WaitForContentAsync(new Uri("https://steamdb.info/app/570/history/?filterkey=2"), token);
                        wait.Restart(); tracker = new HistoryAdvanceTracker(before); continue;
                    }
                    if (current.AccessDenied) throw new InvalidOperationException("SteamDB 当前拒绝访问或限制请求，请查看内置浏览器提示。");
                }
                string decision = tracker.Observe(state, wait.Elapsed);
                if (decision == "changed") return;
                if (decision == "date")
                {
                    SetStatus("网页已隐藏继续加载控件；交由正常日期筛选继续核对历史，不把旧记录当作新批次。");
                    return;
                }
                if (wait.Elapsed > TimeSpan.FromSeconds(120)) throw new TimeoutException("点击加载历史后，120 秒内未观察到新记录，网页也没有确认结束当前批次。请检查页面网络状态后重试；没有把旧批次当作新数据。");
                SetStatus("正在等待网页返回新的历史记录……");
                await Task.Delay(250, token);
            }
        }

        internal sealed class HistoryAdvanceTracker
        {
            private readonly string original;
            private string stableSignature;
            private TimeSpan stableSince;
            private bool stableContinuationClosed;
            internal HistoryAdvanceTracker(string signature) { original = signature; }
            internal string Observe(Dictionary<string, object> state, TimeSpan elapsed)
            {
                string signature = GetString(state, "signature");
                if (!GetBool(state, "hasHistory") || GetBool(state, "loading") || (GetBool(state, "busy") && GetBool(state, "loadVisible")) || GetBool(state, "signIn") || String.IsNullOrEmpty(signature)) { stableSignature = null; return "wait"; }
                bool closed = GetBool(state, "loadExists") && !GetBool(state, "loadVisible");
                if (signature != stableSignature || closed != stableContinuationClosed) { stableSignature = signature; stableSince = elapsed; stableContinuationClosed = closed; return "wait"; }
                if (signature != original && elapsed - stableSince >= TimeSpan.FromMilliseconds(500)) return "changed";
                if (signature == original && closed && elapsed - stableSince >= TimeSpan.FromSeconds(2)) return "date";
                return "wait";
            }
        }

        public void Continue()
        {
            CheckUiThread();
            DateTime until = cooldown.UntilUtc;
            if (until > DateTime.UtcNow)
            {
                SetStatus(CooldownMessage(until));
                return;
            }
            if (rateLimited)
            {
                rateLimited = false;
                SetStatus("已解除本次限流暂停；请确认网页要求的等待时间已结束后重新查询。没有自动发送请求。");
            }
            TaskCompletionSource<bool> waiter = resume;
            if (waiter != null) waiter.TrySetResult(true);
        }

        public void CancelPending()
        {
            CheckUiThread();
            if (pending != null && !pending.IsCancellationRequested) pending.Cancel();
            if (resume != null) resume.TrySetCanceled();
            if (browser.CoreWebView2 != null) browser.CoreWebView2.Stop();
        }

        private async Task<HistoryPageSnapshot> WaitForContentAsync(Uri expected, CancellationToken token, bool dateControlsOnly = false)
        {
            Stopwatch activeWait = Stopwatch.StartNew();
            string previous = null;
            string previousUrl = null;
            int stableSamples = 0;
            bool authenticationWasSeen = false;
            bool contentBroughtIntoView = false;
            bool leavingSteamDbLogin = false;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (!String.IsNullOrEmpty(navigationError)) throw new InvalidOperationException(navigationError);
                if (leavingSteamDbLogin)
                {
                    // Source remains the previous committed URL while the next page
                    // loads. Do not ask for a second Continue during that interval.
                    if (PostLoginDestination(browser.CoreWebView2.Source, expected) == null) leavingSteamDbLogin = false;
                    else
                    {
                        if (activeWait.Elapsed > TimeSpan.FromSeconds(120)) throw new TimeoutException("返回历史页面超时。请检查内置浏览器中的登录和网络状态后重试。");
                        await Task.Delay(350, token);
                        continue;
                    }
                }
                CaptureState state = await CaptureStateAsync(token);
                if (state.Authentication || state.Challenge)
                {
                    authenticationWasSeen |= state.Authentication;
                    await WaitForUserAsync(state.Authentication ? "请在内置浏览器手动完成 Steam 登录，返回 SteamDB 后点击“已完成验证，继续”。" : "请在内置浏览器手动完成验证，然后点击“已完成验证，继续”。", token);
                    activeWait.Restart(); previous = null; previousUrl = null; stableSamples = 0;
                    await Task.Delay(400, token);
                    ThrowIfNavigationFailed();
                    // Only the user's explicit Continue can leave SteamDB's login
                    // landing page. Never redirect away from Steam authentication.
                    string returnUrl = PostLoginDestination(browser.CoreWebView2.Source, expected);
                    if (returnUrl != null)
                    {
                        SetStatus("正在返回历史记录页面……");
                        leavingSteamDbLogin = true;
                        if (dateControlsOnly) await PaceHistoryAsync(token);
                        ThrowIfNavigationFailed();
                        browser.CoreWebView2.Navigate(returnUrl);
                    }
                    continue;
                }
                if (state.AccessDenied) throw new InvalidOperationException("SteamDB 当前拒绝访问或限制请求。已停止读取，请在浏览器查看网站提示；不会尝试绕过限制。");
                if (dateControlsOnly && !navigationBusy && expected != null && SamePage(expected, state.Snapshot.Url) && state.HasMain && state.Ready)
                {
                    Dictionary<string, object> controls = await ReadJsonAsync(browser.CoreWebView2.ExecuteScriptAsync(HistoryDateControlsScript), token);
                    if (GetBool(controls, "controlsReady"))
                    {
                        SetStatus("历史日期控件已就绪；将直接提交目标日期。");
                        return state.Snapshot;
                    }
                }
                if(!dateControlsOnly && expected != null && state.Loading && !contentBroughtIntoView && SamePage(expected, state.Snapshot.Url))
                {
                    // Bring the requested visible tab into view, including on small
                    // windows where the site's content can load only near the viewport.
                    await AwaitCancellable(browser.CoreWebView2.ExecuteScriptAsync(BringContentIntoViewScript), token);
                    contentBroughtIntoView = true;
                }
                if (authenticationWasSeen && expected != null && !SamePage(expected, state.Snapshot.Url))
                {
                    authenticationWasSeen = false;
                    if (dateControlsOnly) await PaceHistoryAsync(token);
                    browser.CoreWebView2.Navigate(expected.AbsoluteUri);
                    activeWait.Restart();
                    await Task.Delay(500, token);
                    continue;
                }
                bool expectedPage = expected == null || SamePage(expected, state.Snapshot.Url);
                if (!dateControlsOnly && !navigationBusy && expectedPage && state.HasMain && !state.Loading && state.Ready && state.Snapshot.Text.Length > 20)
                {
                    if (String.Equals(state.Snapshot.Url, previousUrl, StringComparison.Ordinal) && String.Equals(state.Snapshot.Html, previous, StringComparison.Ordinal)) stableSamples++;
                    else { previous = state.Snapshot.Html; previousUrl = state.Snapshot.Url; stableSamples = 0; }
                    if (stableSamples >= 2)
                    {
                        SetStatus("已读取页面：" + state.Snapshot.Title);
                        return state.Snapshot;
                    }
                }
                else { stableSamples = 0; previous = null; previousUrl = null; }
                if (activeWait.Elapsed > TimeSpan.FromSeconds(120)) throw new TimeoutException(dateControlsOnly ? "历史日期控件在 120 秒内没有就绪。请查看内置浏览器中的登录、验证和网络提示后重试。" : "网页内容在 120 秒内没有加载完成。请查看内置浏览器页面，确认网络、登录和验证状态后重试。程序没有把加载中页面作为历史数据。");
                SetStatus(dateControlsOnly ? "正在等待历史日期控件……" : state.Loading ? "正在等待网页中的历史记录加载……" : "正在等待 SteamDB 页面……");
                await Task.Delay(350, token);
            }
        }

        private async Task WaitForUserAsync(string message, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            ThrowIfNavigationFailed();
            var waiter = new TaskCompletionSource<bool>();
            resume = waiter;
            try
            {
                SetStatus(message);
                // Capture this particular wait. Cancellation of an earlier wait
                // must not cancel a later login/challenge prompt.
                using (token.Register(delegate { waiter.TrySetCanceled(); })) await waiter.Task;
                ThrowIfNavigationFailed();
                token.ThrowIfCancellationRequested();
            }
            finally { if (Object.ReferenceEquals(resume, waiter)) resume = null; }
        }

        private void ThrowIfNavigationFailed()
        {
            if (!String.IsNullOrEmpty(navigationError)) throw new InvalidOperationException(navigationError);
        }

        private void FailNavigation(string message)
        {
            navigationError = message;
            TaskCompletionSource<bool> waiter = resume;
            SetStatus(message);
            // A login wait has no polling loop. Fault it directly so a rejected
            // navigation stops the current operation without Continue or Stop.
            if (waiter != null) waiter.TrySetException(new InvalidOperationException(message));
        }

        private async Task<CaptureState> CaptureStateAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            ThrowIfRateLimited();
            string source = browser.CoreWebView2.Source ?? "about:blank";
            Uri uri;
            if (!Uri.TryCreate(source, UriKind.Absolute, out uri) || uri.Host != "steamdb.info")
            {
                bool auth = uri != null && IsSteamHost(uri.Host);
                return new CaptureState { Authentication = auth, Snapshot = new HistoryPageSnapshot { Url = auth ? uri.GetLeftPart(UriPartial.Path) : "about:blank", Title = auth ? "Steam 登录" : "浏览器准备中", Html = "", Text = "" } };
            }
            if (IsAuthPath(uri.AbsolutePath)) return new CaptureState { Authentication = true, Snapshot = new HistoryPageSnapshot { Url = uri.GetLeftPart(UriPartial.Path), Title = "SteamDB 登录", Html = "", Text = "" } };
            if (!navigationBusy) await CheckPageRestrictionsAsync(token);
            Dictionary<string, object> data = await ReadJsonAsync(browser.CoreWebView2.ExecuteScriptAsync(CaptureScript), token);
            if (data == null) throw new InvalidOperationException("浏览器没有返回可读取的页面内容。");
            if (GetBool(data, "tooLarge")) throw new InvalidOperationException("当前页面历史记录超过 64 MiB 的安全读取上限。请缩小查询范围后重试。");
            return new CaptureState
            {
                Authentication = GetBool(data, "auth"), Challenge = GetBool(data, "challenge"), AccessDenied = GetBool(data, "denied"),
                HasMain = GetBool(data, "hasMain"), Loading = GetBool(data, "loading"), Ready = GetBool(data, "ready"),
                Snapshot = new HistoryPageSnapshot { Url = GetString(data, "url"), Title = GetString(data, "title"), Html = GetString(data, "html"), Text = GetString(data, "text"), CapturedAtUtc = DateTimeOffset.UtcNow }
            };
        }

        private static string CooldownMessage(DateTime until) { return RateLimitMessage + " 自动查询暂停至本机时间 " + until.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "。届时请先手动查看网页确认恢复，再点击“已完成验证，继续”并重新查询。"; }
        private void ThrowIfRateLimited()
        {
            DateTime until = cooldown.UntilUtc;
            if (until > DateTime.UtcNow) throw new InvalidOperationException(CooldownMessage(until));
            if (rateLimited) throw new InvalidOperationException(RateLimitMessage + "本机冷却时间已结束；请先手动查看网页确认恢复，再点击“已完成验证，继续”并重新查询。");
        }
        private async Task CheckPageRestrictionsAsync(CancellationToken token)
        {
            ThrowIfRateLimited();
            var state = await ReadJsonAsync(browser.CoreWebView2.ExecuteScriptAsync(RestrictionScript), token);
            if (GetBool(state, "rateLimited"))
            {
                string message = MarkRateLimited();
                SetStatus(message);
                throw new InvalidOperationException(message);
            }
            if (GetBool(state, "forbidden"))
            {
                SetStatus(ForbiddenMessage);
                throw new InvalidOperationException(ForbiddenMessage);
            }
        }
        private string MarkRateLimited()
        {
            rateLimited = true;
            try { return CooldownMessage(cooldown.Record(DateTime.UtcNow)); }
            catch (Exception) { return RateLimitMessage + "无法保存本机冷却记录，当前窗口仍保持暂停；请勿通过重启立即重试。"; }
        }
        private async Task PaceHistoryAsync(CancellationToken token)
        {
            ThrowIfRateLimited();
            while (DateTime.UtcNow < nextHistoryActionUtc)
            {
                SetStatus("正在按较低频率读取历史，等待下一批请求……");
                await Task.Delay(250, token);
                await CheckPageRestrictionsAsync(token);
            }
            nextHistoryActionUtc = DateTime.UtcNow.AddSeconds(10);
        }
        // Examine only visible notice text. Do not return its raw content, form
        // fields or account details; rate limiting is a stop condition, not data.
        private const string RestrictionScript = @"(function(){
            if(location.hostname!=='steamdb.info'||/\/(login|openid|account)(\/|$)/i.test(location.pathname))return {rateLimited:false,forbidden:false};
            function visible(node){if(!node||node.closest('form'))return false;for(var p=node;p;p=p.parentElement){var c=getComputedStyle(p);if(p.hidden||c.display==='none'||c.visibility==='hidden')return false;}var r=node.getBoundingClientRect();return r.width>0&&r.height>0;}
            // Browser innerText may contain line breaks and a toast's close label.
            // Remove only known boundary controls and the notice's Alert heading;
            // keep the actual error explicit and do not inspect historical body text.
            function notice(text){return (text||'').replace(/\s+/g,' ').trim().replace(/^(?:[×✕✖]\s*|(?:alert:?|x|close(?: notification)?|dismiss(?: notification)?|关闭)\s+)+/i,'').replace(/(?:\s*[×✕✖]|\s+(?:x|close(?: notification)?|dismiss(?: notification)?|关闭))+$/i,'').trim();}
            function limited(text){text=notice(text);return /you have been rate[ -]limited|rate[ -]limit(?:ed|ing)?\s+(?:on|by)\s+SteamDB|^(?:Failed to load history|Error loading history)\s*:?\s*(?:ServerError\s*:?\s*)?(?:(?:HTTP\s*)?429(?:\s*[-:]?\s*too many requests)?|too many requests)[.!]?$|^(?:HTTP\s*)?429\s*[-:]?\s*too many requests$|^(?:SteamDB\s*[-:]?\s*)?rate[ -]limit(?:ed|ing)?$/i.test(text);}
            function forbidden(text){text=notice(text);return /^(?:(?:SteamDB\s*[-:·|]\s*)?(?:(?:HTTP\s*)?403(?:\s*[-:]?\s*Forbidden)?|Forbidden)(?:\s*[-:·|]\s*SteamDB)?|(?:Failed to load history|Error loading history)\s*:?\s*(?:ServerError\s*:?\s*)?(?:(?:HTTP\s*)?403(?:\s*[-:]?\s*Forbidden)?|Forbidden)[.!]?)$/i.test(text);}
            var notices=document.querySelectorAll('[role=alert],[role=status],.alert,.toast,.toastify,.notification,#notification,#js-notification');
            return {rateLimited:limited(document.title)||Array.from(notices).some(function(n){return visible(n)&&limited((n.innerText||'').slice(0,4096));}),forbidden:forbidden(document.title)||Array.from(notices).some(function(n){return visible(n)&&forbidden((n.innerText||'').slice(0,4096));})};
        })()";

        internal sealed class SiteCooldown
        {
            private readonly string path;
            internal SiteCooldown(string file) { path = Path.GetFullPath(file); }
            internal DateTime UntilUtc
            {
                get
                {
                    if (!File.Exists(path)) return DateTime.MinValue;
                    try
                    {
                        if (new FileInfo(path).Length > 4096) throw new InvalidDataException();
                        var record = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(path, Encoding.UTF8));
                        DateTime until;
                        if (record == null || GetString(record, "reason") != "steamdb-http-429" || !DateTime.TryParseExact(GetString(record, "untilUtc"), "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out until) || until.Kind != DateTimeKind.Utc) throw new InvalidDataException();
                        return until;
                    }
                    catch (Exception ex) { throw new InvalidOperationException("本机 SteamDB 冷却记录无法读取。为避免在限流期间继续请求，已暂停自动查询。", ex); }
                }
            }
            internal DateTime Record(DateTime nowUtc)
            {
                DateTime until = nowUtc.ToUniversalTime().AddHours(1), existing = UntilUtc;
                if (existing > until) until = existing;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(new { untilUtc = until.ToString("o", CultureInfo.InvariantCulture), reason = "steamdb-http-429" }), new UTF8Encoding(false));
                    if (File.Exists(path)) File.Replace(temporary, path, null);
                    else
                    {
                        try { File.Move(temporary, path); }
                        catch (IOException) { if (!File.Exists(path)) throw; File.Replace(temporary, path, null); }
                    }
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                return until;
            }
        }

        private const string CaptureScript = @"(function(){
            var root=document.querySelector('#main')||document.querySelector('main');
            if(/^\/app\/570\/depots\/?$/i.test(location.pathname)) root=document.querySelector('#depots');
            else if(/^\/app\/570\/history\/?$/i.test(location.pathname)) root=document.querySelector('#history');
            else if(/^\/app\/570\/patchnotes\/?$/i.test(location.pathname)) root=document.querySelector('#patchnotes');
            var hasMain=!!root; root=root||document.body;
            var title=document.title||'', visible=root?(root.innerText||''):'';
            var challenge=/just a moment|checking your browser|performing security verification|verify you are human|security verification|验证您是真人|正在验证/i.test(title+'\n'+visible.slice(0,6000)) || !!document.querySelector('#challenge-running,#challenge-stage,.cf-turnstile');
            var auth=!!(root&&root.querySelector('input[type=password]')) || /\/(login|openid)(\/|$)/i.test(location.pathname);
            var denied=/access denied|you have been blocked|too many requests|rate limit exceeded|sorry, you have been blocked/i.test(title+'\n'+visible.slice(0,1500));
            if(auth) return {url:location.origin+location.pathname,title:'需要手动登录',html:'',text:'',auth:true,challenge:false,hasMain:false,ready:true,loading:false,denied:false};
            if(!hasMain&&!challenge) return {url:location.href,title:title,html:'',text:'',auth:false,challenge:false,hasMain:false,ready:document.readyState==='complete',loading:false,denied:denied};
            var copy=root?root.cloneNode(true):document.createElement('main');
            copy.querySelectorAll('script,style,noscript,iframe,svg,input,textarea,select,form,[contenteditable=true]').forEach(function(e){e.remove();});
            copy.querySelectorAll('*').forEach(function(e){
                Array.from(e.attributes).forEach(function(a){if(/^on/i.test(a.name)||/^(value|nonce|srcdoc|data-token|data-csrf|data-session|data-steamid)$/i.test(a.name))e.removeAttribute(a.name);});
                if(e.hasAttribute('href')){
                    try{
                        var u=new URL(e.getAttribute('href'),location.href);
                        var http=u.protocol==='https:'||u.protocol==='http:';
                        var auth=/\/(login|logout|openid|account|signin|auth)(\/|$)/i.test(u.pathname);
                        var dota=(u.hostname==='dota2.com'||u.hostname==='www.dota2.com')&&!auth;
                        var store=u.hostname==='store.steampowered.com'&&/^\/news\/(?:app\/570\/view\/)?[0-9]+\/?$/.test(u.pathname);
                        var announcement=u.hostname==='steamcommunity.com'&&/^\/(?:games\/dota2|ogg\/570)\/announcements\/detail\/[0-9]+\/?$/.test(u.pathname);
                        if(!http||u.port||u.username||u.password||auth)e.removeAttribute('href');
                        else if(u.hostname==='steamdb.info'){} // Preserve relative SteamDB links used by the history parser.
                        else if(dota||store||announcement)e.setAttribute('href',u.origin+u.pathname); // Public evidence only, no query/fragment or automatic navigation.
                        else e.removeAttribute('href');
                    }catch(_){e.removeAttribute('href');}
                }
            });
            var loading=!!(root&&Array.from(root.querySelectorAll('#js-history-loading,.loading,.loader,.spinner,[aria-busy=true]')).some(function(e){var r=e.getBoundingClientRect();return r.width>0&&r.height>0&&getComputedStyle(e).visibility!=='hidden';})) || /(^|\n)\s*(?:Loading(?: data| history| patchnotes| updates)?|Crunching data)(?:\.{1,3}|…)?\s*(\n|$)/i.test(visible);
            var html=copy.outerHTML,text=copy.textContent||'';
            if(html.length+text.length>64*1024*1024) return {tooLarge:true};
            return {url:location.href,title:title,html:html,text:text,auth:auth,challenge:challenge,denied:denied,hasMain:hasMain,ready:document.readyState==='complete',loading:loading};
        })()";

        private void OnNavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            Uri uri;
            if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out uri) || !IsAllowedUri(uri))
            {
                e.Cancel = true;
                SetStatus("已停止打开非 SteamDB／Steam 登录页面。");
                return;
            }
            navigationBusy = true;
            navigationError = null;
        }

        private void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            CompleteNavigation(e.HttpStatusCode, e.IsSuccess, e.WebErrorStatus);
        }

        internal void CompleteNavigation(int httpStatusCode, bool isSuccess, CoreWebView2WebErrorStatus webErrorStatus)
        {
            navigationBusy = false;
            string restriction = NavigationRestrictionMessage(httpStatusCode, MarkRateLimited);
            if (restriction != null) { FailNavigation(restriction); return; }
            if (!isSuccess && webErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
                FailNavigation("网页加载失败：" + webErrorStatus.ToString() + "。请检查网络后重试。");
        }

        internal static string NavigationRestrictionMessage(int httpStatusCode, Func<string> markRateLimited)
        {
            if (httpStatusCode == 403) return ForbiddenMessage;
            if (httpStatusCode == 429) return markRateLimited();
            return null;
        }

        private void OnNewWindowRequested(object sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            e.Handled = true;
            Uri uri;
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out uri) && IsAllowedUri(uri)) browser.CoreWebView2.Navigate(e.Uri);
            else SetStatus("已阻止非 SteamDB／Steam 登录弹出页面。");
        }

        private static Uri RequireAllowedUri(string value)
        {
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || !IsAllowedUri(uri)) throw new ArgumentException("只允许 HTTPS SteamDB 页面及 Steam 登录页面。", "url");
            return uri;
        }
        private static bool IsAllowedUri(Uri uri) { return uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && String.IsNullOrEmpty(uri.UserInfo) && (uri.Host == "steamdb.info" || IsSteamHost(uri.Host)); }
        private static bool IsSteamHost(string host) { return host == "steamcommunity.com" || host == "store.steampowered.com" || host == "login.steampowered.com"; }
        private static bool IsAuthPath(string path) { return path.StartsWith("/login", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/openid", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/account", StringComparison.OrdinalIgnoreCase); }
        private static string PostLoginDestination(string source, Uri expected)
        {
            Uri current;
            if (!Uri.TryCreate(source, UriKind.Absolute, out current) || !IsAllowedUri(current) || current.Host != "steamdb.info" || !String.Equals(current.AbsolutePath.TrimEnd('/'), "/login", StringComparison.OrdinalIgnoreCase)) return null;
            return expected == null ? "https://steamdb.info/app/570/patchnotes/" : expected.AbsoluteUri;
        }
        private static bool SamePage(Uri expected, string actual) { Uri uri; return Uri.TryCreate(actual, UriKind.Absolute, out uri) && String.Equals(expected.GetLeftPart(UriPartial.Path), uri.GetLeftPart(UriPartial.Path), StringComparison.OrdinalIgnoreCase) && String.Equals(expected.Query, uri.Query, StringComparison.Ordinal); }
        private static string GetString(Dictionary<string, object> values, string key) { object result; return values.TryGetValue(key, out result) && result != null ? Convert.ToString(result) : ""; }
        private static bool GetBool(Dictionary<string, object> values, string key) { object result; return values.TryGetValue(key, out result) && result is bool && (bool)result; }
        private static async Task<Dictionary<string, object>> ReadJsonAsync(Task<string> scriptResult, CancellationToken token)
        {
            string raw = await AwaitCancellable(scriptResult, token);
            // The browser API itself stays on the window thread. Only pure parsing
            // is dispatched, so a long history page cannot block Stop/Continue.
            return await AwaitCancellable(Task.Run(delegate
            {
                token.ThrowIfCancellationRequested();
                return new JavaScriptSerializer { MaxJsonLength = 128 * 1024 * 1024 }.Deserialize<Dictionary<string, object>>(raw);
            }, token), token);
        }
        private static async Task<T> AwaitCancellable<T>(Task<T> task, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!token.CanBeCanceled) return await task;
            var canceled = new TaskCompletionSource<bool>();
            using (token.Register(delegate { canceled.TrySetCanceled(); }))
            {
                Task finished = await Task.WhenAny(task, canceled.Task);
                if (finished != task)
                {
                    // WebView2 cannot abort ExecuteScriptAsync; cancellation stops
                    // waiting. Observe any later failure without using its result.
                    ObserveLateFailure(task);
                    token.ThrowIfCancellationRequested();
                    throw new OperationCanceledException(token);
                }
                return await task;
            }
        }
        private static void ObserveLateFailure(Task task) { task.ContinueWith(failed => GC.KeepAlive(failed.Exception), TaskContinuationOptions.OnlyOnFaulted); }
        private void SetStatus(string value) { if (lastStatus == value) return; lastStatus = value; Action<string> handler = StatusChanged; if (handler != null) handler(value); }
        private void CheckUiThread() { if (disposed) throw new ObjectDisposedException("BrowserSession"); if (Thread.CurrentThread.ManagedThreadId != uiThread) throw new InvalidOperationException("浏览器操作必须从窗口线程调用，请使用 await 而不是 Task.Run。"); }
        public void Dispose() { if (disposed) return; CheckUiThread(); CancelPending(); disposed = true; browser.Dispose(); }
        internal sealed class CaptureState { public HistoryPageSnapshot Snapshot; public bool Authentication, Challenge, AccessDenied, HasMain, Loading, Ready; }
    }
}
