using System;
using System.Reflection;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.Serialization;
using Microsoft.Web.WebView2.Core;
using DotaArchive;
internal static class BrowserPolicyTests
{
    private static int count;
    private static void Check(bool value,string name){if(!value)throw new Exception(name);count++;}
    private static bool Allowed(string url){return (bool)typeof(BrowserSession).GetMethod("IsAllowedUri",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[]{new Uri(url)});}
    private static bool Same(string expected,string actual){return (bool)typeof(BrowserSession).GetMethod("SamePage",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[]{new Uri(expected),actual});}
    private static int Main()
    {
        foreach(string url in new[]{"https://steamdb.info/app/570/history/?filterkey=2","https://steamdb.info/patchnotes/3938917/","https://steamdb.info:443/login/","https://steamcommunity.com/openid/login","https://store.steampowered.com/login/","https://login.steampowered.com/"})Check(Allowed(url),"Allowed normal site/auth URL: "+url);
        foreach(string url in new[]{"http://steamdb.info/","https://steamdb.info:8443/","https://steamdb.info.evil.example/","https://evil.example/?steamdb.info","https://user:password@steamdb.info/","file:///C:/Windows/System32/","javascript:alert(1)","about:blank","https://steamcommunity.com.evil.example/","https://steamdb.info@evil.example/","https://sub.steamdb.info/"})Check(!Allowed(url),"Blocked unrelated or unsafe navigation: "+url);
        Check(Same("https://steamdb.info/app/570/history/?filterkey=2","https://steamdb.info/app/570/history/?filterkey=2#history"),"Fragment does not change page identity");
        Check(!Same("https://steamdb.info/app/570/history/?filterkey=2","https://steamdb.info/app/570/history/?filterkey=1"),"Query must match requested history filter");
        Check(!Same("https://steamdb.info/patchnotes/1/","https://steamdb.info/patchnotes/2/"),"Different build not accepted as target page");
        Check(!Same("https://steamdb.info/patchnotes/1/","https://evil.example/patchnotes/1/"),"Different host not accepted");
        Check(!Same("https://steamdb.info/patchnotes/1/","invalid"),"Invalid source rejected");
        MethodInfo wait=typeof(BrowserSession).GetMethod("AwaitCancellable",BindingFlags.NonPublic|BindingFlags.Static).MakeGenericMethod(typeof(int));
        var done=(Task<int>)wait.Invoke(null,new object[]{Task.FromResult(42),CancellationToken.None});
        Check(done.GetAwaiter().GetResult()==42,"Completed browser script result preserved");
        var cts=new CancellationTokenSource();var pending=new TaskCompletionSource<int>();
        var canceled=(Task<int>)wait.Invoke(null,new object[]{pending.Task,cts.Token});cts.Cancel();
        bool cancelObserved=false;try{canceled.GetAwaiter().GetResult();}catch(OperationCanceledException){cancelObserved=true;}
        Check(cancelObserved,"Stop cancels a still-pending browser script await");
        Check(!pending.Task.IsCompleted,"Cancel does not falsely claim native script stopped");
        pending.SetException(new InvalidOperationException("late browser failure"));
        var failed=new TaskCompletionSource<int>();failed.SetException(new InvalidOperationException("actual failure"));
        var fault=(Task<int>)wait.Invoke(null,new object[]{failed.Task,CancellationToken.None});
        bool failureObserved=false;try{fault.GetAwaiter().GetResult();}catch(InvalidOperationException ex){failureObserved=ex.Message=="actual failure";}
        Check(failureObserved,"Actual browser script failure preserved");
        MethodInfo loginReturn=typeof(BrowserSession).GetMethod("PostLoginDestination",BindingFlags.NonPublic|BindingFlags.Static);
        Check((string)loginReturn.Invoke(null,new object[]{"https://steamdb.info/login/",null})=="https://steamdb.info/app/570/patchnotes/","Explicit Continue on SteamDB login has normal fallback");
        Check((string)loginReturn.Invoke(null,new object[]{"https://steamdb.info/login/?next=example",new Uri("https://steamdb.info/app/570/history/?filterkey=2")})=="https://steamdb.info/app/570/history/?filterkey=2","Explicit Continue resumes requested history page");
        foreach(string authUrl in new[]{"https://steamcommunity.com/openid/login","https://store.steampowered.com/login/","https://login.steampowered.com/","https://steamdb.info/account/","https://steamdb.info/patchnotes/3938917/","http://steamdb.info/login/","about:blank"}) Check(loginReturn.Invoke(null,new object[]{authUrl,null})==null,"Never auto-leave active Steam authentication or unrelated page: "+authUrl);
        MethodInfo retry=typeof(BrowserSession).GetMethod("CanInitialize",BindingFlags.NonPublic|BindingFlags.Static);
        Check((bool)retry.Invoke(null,new object[]{null}),"Initial environment can initialize");
        Check(!(bool)retry.Invoke(null,new object[]{Task.FromResult(true)}),"Successful environment is reused");
        Check(!(bool)retry.Invoke(null,new object[]{new TaskCompletionSource<bool>().Task}),"Concurrent initialization shares its pending task");
        var initFault=new TaskCompletionSource<bool>();initFault.SetException(new InvalidOperationException());
        Check((bool)retry.Invoke(null,new object[]{initFault.Task}),"Failed initialization can retry after other window closes");
        var initCanceled=new TaskCompletionSource<bool>();initCanceled.SetCanceled();
        Check((bool)retry.Invoke(null,new object[]{initCanceled.Task}),"Canceled initialization can retry");
        MethodInfo incompatible=typeof(BrowserSession).GetMethod("IsIncompatibleEnvironment",BindingFlags.NonPublic|BindingFlags.Static);
        Check((bool)incompatible.Invoke(null,new object[]{new System.Runtime.InteropServices.COMException("synthetic",unchecked((int)0x8007139F))}),"Correct incompatible-environment HRESULT recognized");
        Check(!(bool)incompatible.Invoke(null,new object[]{new System.Runtime.InteropServices.COMException("synthetic",unchecked((int)0x80070005))}),"Unrelated failure not mislabeled as profile conflict");
        var unchanged=History("100:9:1",true,false,false);
        var progress=new BrowserSession.HistoryAdvanceTracker("100:9:1");
        Check(progress.Observe(unchanged,TimeSpan.Zero)=="wait","Old snapshot not immediately accepted");
        Check(progress.Observe(unchanged,TimeSpan.FromSeconds(40))=="wait","Stable old snapshot with available button is never accepted");
        Check(progress.Observe(History("100:9:1",true,true,false),TimeSpan.FromSeconds(41))=="wait","Loading old snapshot waits");
        Check(progress.Observe(History("100:9:1",true,false,true),TimeSpan.FromSeconds(42))=="wait","Disabled busy load button waits");
        Check(progress.Observe(History("150:9:0",true,true,false),TimeSpan.FromSeconds(43))=="wait","New records while loading still wait");
        Check(progress.Observe(History("150:9:0",true,false,false),TimeSpan.FromSeconds(44))=="wait","New record signature requires stability");
        Check(progress.Observe(History("150:9:0",true,false,false),TimeSpan.FromSeconds(44.25))=="wait","New signature not accepted too early");
        Check(progress.Observe(History("150:9:0",true,false,false),TimeSpan.FromSeconds(44.5))=="changed","Settled new record signature accepted");
        var ending=new BrowserSession.HistoryAdvanceTracker("100:9:1");
        ending.Observe(unchanged,TimeSpan.Zero);
        Check(ending.Observe(History("100:9:1",false,false,false),TimeSpan.FromSeconds(5))=="wait","Newly hidden button cannot reuse prior stability time");
        Check(ending.Observe(History("100:9:1",false,false,false),TimeSpan.FromSeconds(6.9))=="wait","Hidden footer is observed before date fallback");
        Check(ending.Observe(History("100:9:1",false,false,false),TimeSpan.FromSeconds(7))=="date","Stable hidden continuation signals normal date fallback");
        var hiddenDisabled=new BrowserSession.HistoryAdvanceTracker("100:9:1");hiddenDisabled.Observe(History("100:9:1",false,false,true),TimeSpan.Zero);
        Check(hiddenDisabled.Observe(History("100:9:1",false,false,true),TimeSpan.FromSeconds(2))=="date","Hidden disabled footer still permits stable date fallback");
        var missing=History("100:9:1",false,false,false);missing["loadExists"]=false;
        var missingTracker=new BrowserSession.HistoryAdvanceTracker("100:9:1");missingTracker.Observe(missing,TimeSpan.Zero);
        Check(missingTracker.Observe(missing,TimeSpan.FromSeconds(9))=="wait","Missing layout element is not invented page-end evidence");
        var signin=History("150:9:0",false,false,false);signin["signIn"]=true;
        Check(progress.Observe(signin,TimeSpan.FromSeconds(50))=="wait","Authentication required is not completion");
        var replaced=new BrowserSession.HistoryAdvanceTracker("100:9:1");replaced.Observe(History("100:8:0",true,false,false),TimeSpan.Zero);
        Check(replaced.Observe(History("100:8:0",true,false,false),TimeSpan.FromSeconds(1))=="changed","Same count with different change IDs is a real update");
        var dateRefresh=new BrowserSession.HistoryDateRefreshTracker();
        var dateState=DateRefresh("100:9:1",false,0);
        Check(!dateRefresh.Observe(dateState,TimeSpan.Zero),"Date request must observe completion, not just current IDs");
        Check(!dateRefresh.Observe(dateState,TimeSpan.FromSeconds(110)),"An old static date snapshot never becomes successful by waiting");
        dateState=DateRefresh("100:9:1",true,2);
        Check(!dateRefresh.Observe(dateState,TimeSpan.FromSeconds(111)),"Completed date refresh still waits for settled DOM");
        Check(!dateRefresh.Observe(dateState,TimeSpan.FromSeconds(111.25)),"Same-ID refreshed batch does not settle too early");
        Check(dateRefresh.Observe(dateState,TimeSpan.FromSeconds(111.5)),"Same IDs are accepted after observed date refresh settles");
        dateState["revision"]=3;
        Check(!dateRefresh.Observe(dateState,TimeSpan.FromSeconds(112)),"DOM mutation resets date completion stability even with same IDs");
        dateState["loading"]=true;
        Check(!dateRefresh.Observe(dateState,TimeSpan.FromSeconds(113)),"A second loading cycle blocks completed date state");
        dateState["loading"]=false;
        Check(!dateRefresh.Observe(dateState,TimeSpan.FromSeconds(114)),"Loading reset requires another settled interval");
        Check(dateRefresh.Observe(dateState,TimeSpan.FromSeconds(114.5)),"Settled second loading cycle can complete");
        foreach(string blocked in new[]{"busy","signIn","failed"}){
            dateState[blocked]=true;
            Check(!dateRefresh.Observe(dateState,TimeSpan.FromSeconds(115)),"Date refresh respects "+blocked);
            dateState[blocked]=false;
        }
        foreach(string required in new[]{"active","hasHistory","completed"}){
            dateState[required]=false;
            Check(!dateRefresh.Observe(dateState,TimeSpan.FromSeconds(116)),"Date refresh requires "+required);
            dateState[required]=true;
        }
        Check(typeof(IHistoryDateNavigationBrowser).IsAssignableFrom(typeof(BrowserSession)),"Browser exposes optional date-only navigation contract");
        var historyTarget=new Uri("https://steamdb.info/app/570/history/?filterkey=2");
        var reuseState=new BrowserSession.CaptureState{HasMain=true,Ready=true,Snapshot=new HistoryPageSnapshot{Url=historyTarget.AbsoluteUri}};
        var reuseControls=new Dictionary<string,object>{{"hasHistory",true},{"controlsReady",true}};
        Check(BrowserSession.CanReuseHistoryDatePage(historyTarget,historyTarget.AbsoluteUri,reuseState,false,null,reuseControls),"Ready displayed history can be reused without navigating back to latest records");
        Check(BrowserSession.CanReuseHistoryDatePage(historyTarget,historyTarget.AbsoluteUri+"#history",reuseState,false,null,reuseControls),"History anchor does not invalidate the existing date control");
        foreach(string source in new[]{"https://steamdb.info/app/570/history/?filterkey=1","https://steamdb.info/app/570/history/?changeid=1624513","https://steamdb.info/app/440/history/?filterkey=2","https://steamdb.info/login/","https://steamcommunity.com/openid/login","about:blank"})
            Check(!BrowserSession.CanReuseHistoryDatePage(historyTarget,source,reuseState,false,null,reuseControls),"Different filter, app or authentication source cannot reuse stale history: "+source);
        Check(!BrowserSession.CanReuseHistoryDatePage(historyTarget,historyTarget.AbsoluteUri,reuseState,true,null,reuseControls),"In-flight navigation cannot reuse the previous committed history DOM");
        Check(!BrowserSession.CanReuseHistoryDatePage(historyTarget,historyTarget.AbsoluteUri,reuseState,false,"navigation failed",reuseControls),"A failed navigation cannot reuse leftover controls");
        reuseState.Authentication=true;
        Check(!BrowserSession.CanReuseHistoryDatePage(historyTarget,historyTarget.AbsoluteUri,reuseState,false,null,reuseControls),"Authentication UI embedded at history URL blocks reuse");reuseState.Authentication=false;
        reuseState.Challenge=true;
        Check(!BrowserSession.CanReuseHistoryDatePage(historyTarget,historyTarget.AbsoluteUri,reuseState,false,null,reuseControls),"Browser challenge at history URL blocks reuse");reuseState.Challenge=false;
        reuseState.AccessDenied=true;
        Check(!BrowserSession.CanReuseHistoryDatePage(historyTarget,historyTarget.AbsoluteUri,reuseState,false,null,reuseControls),"Access-denied history page blocks reuse");reuseState.AccessDenied=false;
        reuseState.Ready=false;
        Check(!BrowserSession.CanReuseHistoryDatePage(historyTarget,historyTarget.AbsoluteUri,reuseState,false,null,reuseControls),"Incomplete document is not ready for reuse");reuseState.Ready=true;
        reuseState.HasMain=false;
        Check(!BrowserSession.CanReuseHistoryDatePage(historyTarget,historyTarget.AbsoluteUri,reuseState,false,null,reuseControls),"Missing history content cannot reuse stale controls");reuseState.HasMain=true;
        reuseControls["controlsReady"]=false;
        Check(!BrowserSession.CanReuseHistoryDatePage(historyTarget,historyTarget.AbsoluteUri,reuseState,false,null,reuseControls),"Disabled or missing date control cannot be reused");reuseControls["controlsReady"]=true;
        reuseState.Snapshot.Url="https://steamdb.info/app/570/depots/";
        Check(!BrowserSession.CanReuseHistoryDatePage(historyTarget,historyTarget.AbsoluteUri,reuseState,false,null,reuseControls),"Captured and live page identities must agree");reuseState.Snapshot.Url=historyTarget.AbsoluteUri;
        reuseState.Loading=true;
        Check(BrowserSession.CanReuseHistoryDatePage(historyTarget,historyTarget.AbsoluteUri,reuseState,false,null,reuseControls),"Reusing date navigation does not turn an initial loading placeholder into completed history data");
        MethodInfo initialFallback=typeof(BrowserSession).GetMethod("NeedsInitialHistoryFallback",BindingFlags.NonPublic|BindingFlags.Static);
        var initialControls=new Dictionary<string,object>{{"hasHistory",true},{"hasPanels",false}};
        Check((bool)initialFallback.Invoke(null,new object[]{true,initialControls}),"An empty loading page requires ordinary UI fallback before a date submission");
        Check(!(bool)initialFallback.Invoke(null,new object[]{false,initialControls}),"An idle empty page may submit its first date directly");
        initialControls["hasPanels"]=true;
        Check(!(bool)initialFallback.Invoke(null,new object[]{true,initialControls}),"Existing panels retain the strict replacement path");
        initialControls["hasPanels"]=false;initialControls["hasHistory"]=false;
        Check(!(bool)initialFallback.Invoke(null,new object[]{true,initialControls}),"An unrelated page is not treated as an initial history list");
        int rateLimitCalls=0;
        Func<string> navigationRateLimit=delegate { rateLimitCalls++; return "HTTP 429 synthetic cooldown"; };
        string forbiddenNavigation=BrowserSession.NavigationRestrictionMessage(403,navigationRateLimit);
        Check(forbiddenNavigation!=null&&forbiddenNavigation.Contains("HTTP 403")&&forbiddenNavigation.Contains("不会自动重试"),"HTTP 403 navigation explicitly stops without generic network retry guidance");
        Check(rateLimitCalls==0,"HTTP 403 navigation never records an HTTP 429 cooldown");
        Check(BrowserSession.NavigationRestrictionMessage(429,navigationRateLimit)=="HTTP 429 synthetic cooldown"&&rateLimitCalls==1,"HTTP 429 navigation preserves its existing cooldown callback");
        foreach(int normalStatus in new[]{0,200,302,404,500})
            Check(BrowserSession.NavigationRestrictionMessage(normalStatus,navigationRateLimit)==null,"Unrelated navigation status does not fabricate a restriction: "+normalStatus);
        Check(rateLimitCalls==1,"Only actual HTTP 429 navigation invokes cooldown handling");
        TestNavigationDuringUserWait();
        string cooldownDirectory=System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"cooldown-test-runs",Guid.NewGuid().ToString("N"));
        string cooldownPath=System.IO.Path.Combine(cooldownDirectory,"synthetic.cooldown.json");
        var cooldown=new BrowserSession.SiteCooldown(cooldownPath);
        Check(cooldown.UntilUtc==DateTime.MinValue,"No existing cooldown allows first use");
        DateTime syntheticNow=new DateTime(2020,1,2,3,4,5,DateTimeKind.Utc);
        Check(cooldown.Record(syntheticNow)==syntheticNow.AddHours(1),"Rate limit records one-hour cooldown");
        Check(new BrowserSession.SiteCooldown(cooldownPath).UntilUtc==syntheticNow.AddHours(1),"Another session reads persisted cooldown");
        Check(cooldown.Record(syntheticNow.AddMinutes(-30))==syntheticNow.AddHours(1),"Existing later cooldown is not shortened");
        Check(cooldown.Record(syntheticNow.AddMinutes(10))==syntheticNow.AddMinutes(70),"New rate limit extends cooldown");
        var cooldownData=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(System.IO.File.ReadAllText(cooldownPath));
        Check(cooldownData.Count==2&&cooldownData.ContainsKey("untilUtc")&&(string)cooldownData["reason"]=="steamdb-http-429","Cooldown stores only expiry and fixed reason, without account data");
        System.IO.File.WriteAllText(cooldownPath,"not-valid-json");
        bool corruptBlocked=false;try{DateTime ignored=cooldown.UntilUtc;}catch(InvalidOperationException){corruptBlocked=true;}
        Check(corruptBlocked,"Malformed cooldown fails closed instead of sending requests");
        var options=BrowserProbeProgram.ParseArguments(new[]{"--url=https://steamdb.info/patchnotes/3863157/","--capture-dir=C:\\local test","--save-on-load"});
        Check(options.InitialUrl=="https://steamdb.info/patchnotes/3863157/"&&options.CaptureDirectory=="C:\\local test"&&options.SaveOnLoad,"Probe supports equals-style options");
        options=BrowserProbeProgram.ParseArguments(new[]{"--url","https://steamdb.info/patchnotes/3903210/","--capture-dir","C:\\local test"});
        Check(options.InitialUrl=="https://steamdb.info/patchnotes/3903210/"&&options.CaptureDirectory=="C:\\local test"&&!options.SaveOnLoad,"Probe retains separate option-value arguments");
        Console.WriteLine(new JavaScriptSerializer().Serialize(new{Result="Passed",Assertions=count,Scope="Navigation allowlist, page identity, cancellable async waiting, explicit login-return routing, initialization retry and history advance state machine; no UI, account, network, credentials or runtime launch.",Utc=DateTime.UtcNow.ToString("o")}));
        return 0;
    }
    private static Dictionary<string,object> History(string signature,bool visible,bool loading,bool busy){return new Dictionary<string,object>{{"hasHistory",true},{"signature",signature},{"loadExists",true},{"loadVisible",visible},{"loading",loading},{"busy",busy},{"signIn",false}};}
    private static Dictionary<string,object> DateRefresh(string signature,bool completed,int revision){return new Dictionary<string,object>{{"active",true},{"hasHistory",true},{"signature",signature},{"completed",completed},{"revision",revision},{"loading",false},{"busy",false},{"signIn",false}};}

    private static BrowserSession WaitOnlySession(string cooldownPath)
    {
        // Do not construct WinForms/WebView2 or access a real browser profile.
        // Exercise the production event handling and async login wait in isolation.
        var session=(BrowserSession)FormatterServices.GetUninitializedObject(typeof(BrowserSession));
        typeof(BrowserSession).GetField("uiThread",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(session,Thread.CurrentThread.ManagedThreadId);
        typeof(BrowserSession).GetField("cooldown",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(session,new BrowserSession.SiteCooldown(cooldownPath));
        return session;
    }
    private static Task StartUserWait(BrowserSession session,CancellationToken token)
    {
        return (Task)typeof(BrowserSession).GetMethod("WaitForUserAsync",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(session,new object[]{"Synthetic manual login prompt",token});
    }
    private static string WaitFailure(Task task,string name)
    {
        Check(task.IsCompleted,name+" completes immediately without Continue or Stop");
        try {task.GetAwaiter().GetResult();}catch(InvalidOperationException error){Check(task.IsFaulted&&!task.IsCanceled,name+" preserves failure instead of cancellation");return error.Message;}
        throw new Exception(name+" unexpectedly succeeded");
    }
    private static void TestNavigationDuringUserWait()
    {
        string root=System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"wait-test-runs",Guid.NewGuid().ToString("N"));
        string forbiddenPath=System.IO.Path.Combine(root,"forbidden.cooldown.json");
        var forbidden=WaitOnlySession(forbiddenPath);
        string status=null;forbidden.StatusChanged+=value=>status=value;
        var userCancel=new CancellationTokenSource();
        Task waiting=StartUserWait(forbidden,userCancel.Token);
        Check(!waiting.IsCompleted&&forbidden.WaitingForUser,"Manual login waits for the user before any failure");
        forbidden.CompleteNavigation(403,false,CoreWebView2WebErrorStatus.Unknown);
        string message=WaitFailure(waiting,"HTTP 403 while waiting for login");
        Check(message.Contains("HTTP 403")&&status==message,"HTTP 403 returns the same explicit failure that the UI displays");
        Check(!forbidden.WaitingForUser,"HTTP 403 clears the manual-wait state");
        Check(!System.IO.File.Exists(forbiddenPath),"HTTP 403 does not create an invented cooldown");
        userCancel.Cancel();forbidden.Continue();
        Check(waiting.IsFaulted&&waiting.Exception.InnerException.Message==message,"Late Stop or Continue cannot replace the existing HTTP 403 failure");

        string limitedPath=System.IO.Path.Combine(root,"limited.cooldown.json");
        var limited=WaitOnlySession(limitedPath);
        Task limitedWait=StartUserWait(limited,CancellationToken.None);
        limited.CompleteNavigation(429,false,CoreWebView2WebErrorStatus.Unknown);
        string rateMessage=WaitFailure(limitedWait,"HTTP 429 while waiting for login");
        Check(rateMessage.Contains("HTTP 429")&&!limited.WaitingForUser,"HTTP 429 preserves its reason and clears the manual-wait state");
        Check(new BrowserSession.SiteCooldown(limitedPath).UntilUtc>DateTime.UtcNow,"HTTP 429 during manual login still persists the existing cooldown");
        limited.Continue();
        Check(limitedWait.IsFaulted&&limitedWait.Exception.InnerException.Message==rateMessage,"Continue cannot resume the failed operation or bypass its HTTP 429 cooldown");

        var before=WaitOnlySession(System.IO.Path.Combine(root,"before.cooldown.json"));
        before.CompleteNavigation(403,false,CoreWebView2WebErrorStatus.Unknown);
        int prompts=0;before.StatusChanged+=value=>prompts++;
        Check(WaitFailure(StartUserWait(before,CancellationToken.None),"HTTP 403 before login wait registration").Contains("HTTP 403"),"A pre-existing rejection is retained");
        Check(!before.WaitingForUser&&prompts==0,"A pre-existing rejection does not display or install a new login prompt");

        var normal=WaitOnlySession(System.IO.Path.Combine(root,"normal.cooldown.json"));
        var oldToken=new CancellationTokenSource();
        Task normalWait=StartUserWait(normal,oldToken.Token);
        normal.CompleteNavigation(200,true,CoreWebView2WebErrorStatus.Unknown);
        Check(!normalWait.IsCompleted&&normal.WaitingForUser,"Successful navigation never automatically approves a login or challenge");
        normal.Continue();normalWait.GetAwaiter().GetResult();
        Check(!normal.WaitingForUser,"Explicit Continue completes and cleans up a normal login wait");
        var newToken=new CancellationTokenSource();Task nextWait=StartUserWait(normal,newToken.Token);
        oldToken.Cancel();
        Check(!nextWait.IsCompleted,"An old wait's token cannot cancel a later login prompt");
        normal.CompleteNavigation(0,false,CoreWebView2WebErrorStatus.OperationCanceled);
        Check(!nextWait.IsCompleted,"A canceled navigation is not fabricated into a rejection or an approval");
        newToken.Cancel();
        Check(nextWait.IsCanceled&&!normal.WaitingForUser,"User cancellation still immediately cleans up the current login wait");

        // Queue the async continuation to model Continue being pressed just before
        // the HTTP rejection reaches the UI thread. The pending continuation must
        // observe that rejection rather than allowing a return-page navigation.
        var race=WaitOnlySession(System.IO.Path.Combine(root,"race.cooldown.json"));
        var context=new QueuedContext();var previous=SynchronizationContext.Current;Task raced;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            raced=StartUserWait(race,CancellationToken.None);
            SynchronizationContext.SetSynchronizationContext(null);
            race.Continue();
            Check(!raced.IsCompleted,"Continue race test keeps the login continuation queued");
            race.CompleteNavigation(403,false,CoreWebView2WebErrorStatus.Unknown);
            context.Drain();
        }
        finally {SynchronizationContext.SetSynchronizationContext(previous);}
        Check(WaitFailure(raced,"HTTP 403 after Continue but before its continuation").Contains("HTTP 403")&&!race.WaitingForUser,"Continue race preserves the rejection and clears the waiter");
    }
    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly Queue<Action> work=new Queue<Action>();
        public override void Post(SendOrPostCallback callback,object state){work.Enqueue(delegate{callback(state);});}
        internal void Drain(){while(work.Count>0)work.Dequeue()();}
    }
}
