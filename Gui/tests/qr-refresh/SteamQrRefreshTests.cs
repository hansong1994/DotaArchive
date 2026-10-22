using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace DotaArchive
{
    internal static class SteamQrRefreshTests
    {
        private static readonly List<string> passed = new List<string>();
        private static int Main(string[] args)
        {
            try {
                InitialAndLifecycle(); RequestsAndCancellation(); StaleEvents(); PhaseCoverage(); ConcurrentRequests();
                File.WriteAllText(args[0], new JavaScriptSerializer().Serialize(new { Passed = passed.Count, Cases = passed, NetworkUsed = false, UserInterfaceUsed = false, DownloaderProcessesStarted = 0 }), new UTF8Encoding(false));
                Console.WriteLine("Steam QR refresh: " + passed.Count + " tests passed."); return 0;
            } catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
        private static void InitialAndLifecycle()
        {
            var s = new SteamQrRefreshState(); object owner = new object();
            Check(!s.Waiting && !s.RetryAllowed && !s.Pending && !s.CanRefresh(false,true), "initial state cannot refresh");
            Check(!s.IsCurrent(null) && !s.CanShowQr(null) && !s.Observe(null,"authentication"), "null cannot masquerade as current owner");
            try { s.Begin(null); throw new Exception("null Begin accepted"); } catch(ArgumentNullException) { Check(true,"Begin rejects null owner"); }
            s.Begin(owner);
            Check(s.IsCurrent(owner) && !s.Waiting && !s.RetryAllowed && !s.Pending, "Begin initializes a fresh authorization session");
            Check(s.Observe(owner,"authentication") && s.Waiting && s.RetryAllowed && s.CanShowQr(owner), "authentication enables current QR");
            Check(s.CanRefresh(true,true) && !s.CanRefresh(true,false), "busy refresh requires a complete plan");
            Check(s.Observe(owner,"authenticated") && !s.Waiting && !s.RetryAllowed && !s.CanShowQr(owner), "authorization success immediately removes refresh eligibility");
            s.Observe(owner,"downloading"); s.End(owner);
            Check(!s.IsCurrent(owner) && !s.CanRefresh(false,true) && !s.RetryAllowed, "successful download end cannot refresh");
            s.Begin(owner); s.Observe(owner,"manifest_authentication"); s.Observe(owner,"manifest_error");
            Check(!s.Waiting && s.RetryAllowed && !s.CanShowQr(owner) && !s.CanRefresh(true,true), "authentication error hides QR and waits for cleanup");
            s.End(owner);
            Check(s.CanRefresh(false,true) && !s.CanRefresh(false,false), "idle failed authorization can refresh with complete plan");
            s.Begin(owner); s.Observe(owner,"error"); s.End(owner);
            Check(!s.CanRefresh(false,true), "non-authentication error does not grant refresh");
            s.Begin(owner); s.Observe(owner,"authentication"); s.Observe(owner,"connecting");
            Check(!s.Waiting && s.RetryAllowed && !s.CanRefresh(true,true), "reconnecting retains retry but disables busy refresh");
            s.Observe(owner,"error"); s.End(owner);
            Check(s.CanRefresh(false,true), "failed reconnect after auth can retry after cleanup");
        }
        private static void RequestsAndCancellation()
        {
            var s = new SteamQrRefreshState(); object owner = new object(); s.Begin(owner);
            Check(!s.Request(true,true) && !s.Pending, "request outside authorization is rejected");
            s.Observe(owner,"authentication");
            Check(!s.Request(true,false) && !s.Pending, "request with incomplete plan is rejected");
            Check(s.Request(true,true) && s.Pending && !s.CanShowQr(owner), "accepted request immediately invalidates visible QR");
            Check(!s.Request(true,true) && !s.Request(false,true), "repeated clicks cannot schedule extra restarts");
            s.End(owner);
            Check(s.Pending && s.RetryAllowed && !s.Waiting, "End preserves restart request until caller completes cleanup");
            Check(s.TakePending() && !s.Pending && !s.TakePending(), "pending request can be consumed exactly once");
            Check(s.Request(false,true), "idle authorization failure can schedule refresh");
            s.CancelPending();
            Check(!s.Pending && !s.TakePending(), "stop or close can revoke scheduled restart");
            s.Begin(owner); s.Observe(owner,"authentication"); s.Request(true,true); s.Observe(owner,"authenticated");
            Check(s.Pending && !s.Waiting && !s.RetryAllowed, "late auth success cannot erase explicit refresh request");
            s.End(owner); Check(s.TakePending(), "auth success race still restarts at most once");
            s.Begin(owner); s.Observe(owner,"authentication"); s.Request(true,true); s.Begin(new object());
            Check(!s.Pending && !s.Waiting && !s.RetryAllowed, "Begin resets flags for new engine");
        }
        private static void StaleEvents()
        {
            var s = new SteamQrRefreshState(); object old = new EqualOwner(), current = new EqualOwner();
            s.Begin(old); s.Observe(old,"authentication"); s.End(old); s.Begin(current); s.Observe(current,"authentication");
            Check(!s.IsCurrent(old) && !s.CanShowQr(old) && s.CanShowQr(current), "owner identity uses reference identity rather than Equals");
            Check(!s.Observe(old,"authenticated") && s.Waiting && s.RetryAllowed, "old auth success cannot hide new QR");
            s.Observe(current,"downloading");
            Check(!s.Observe(old,"authentication") && !s.Waiting && !s.RetryAllowed, "old auth request cannot enable refresh during download");
            s.End(old); Check(s.IsCurrent(current), "old End cannot clear current engine");
            s.End(current); Check(!s.Observe(current,"authentication") && !s.CanShowQr(current), "ended engine cannot revive QR");
            s.Begin(current); s.Observe(current,"authentication");
            Check(s.Observe(current,null) && s.Waiting, "unknown null phase retains current authorization state");
            Check(s.Observe(current,"unrecognized_phase") && s.Waiting, "unknown phase does not invent an authorization transition");
        }
        private static void PhaseCoverage()
        {
            foreach(string phase in new[] { "authenticated", "manifest_authenticated", "downloading", "manifest_downloading", "preparing", "manifest_preparing", "downloaded", "manifest_downloaded", "manifests_downloaded", "preparing_manifests", "manifests_prepared", "verifying", "verified", "completed", "文件校验" }) {
                var s = new SteamQrRefreshState(); object owner = new object(); s.Begin(owner); s.Observe(owner,"manifest_authentication"); s.Observe(owner,phase);
                Check(!s.Waiting && !s.RetryAllowed && !s.CanRefresh(true,true) && !s.CanRefresh(false,true), phase + " prevents QR refresh");
                s.End(owner); Check(!s.CanRefresh(false,true), phase + " remains non-refreshable when idle");
            }
            foreach(string phase in new[] { "error", "manifest_error", "connecting", "manifest_connecting" }) {
                var s = new SteamQrRefreshState(); object owner = new object(); s.Begin(owner); s.Observe(owner,"authentication"); s.Observe(owner,phase);
                Check(!s.Waiting && s.RetryAllowed, phase + " retains previously granted auth retry");
            }
        }
        private static void ConcurrentRequests()
        {
            var s = new SteamQrRefreshState(); object old = new object(), current = new object(); s.Begin(current); s.Observe(current,"authentication");
            int accepted = 0;
            Parallel.For(0,256, delegate(int i) { if(s.Request(true,true)) Interlocked.Increment(ref accepted); });
            Check(accepted == 1 && s.Pending, "256 simultaneous refresh requests schedule exactly one restart");
            int consumed = 0;
            Parallel.For(0,256, delegate(int i) { if(s.TakePending()) Interlocked.Increment(ref consumed); });
            Check(consumed == 1 && !s.Pending, "256 concurrent consumers observe only one pending restart");
            int staleAccepted = 0;
            Parallel.For(0,256, delegate(int i) { if(s.Observe(old, i % 2 == 0 ? "authenticated" : "authentication")) Interlocked.Increment(ref staleAccepted); s.End(old); });
            Check(staleAccepted == 0 && s.IsCurrent(current) && s.Waiting, "concurrent stale observers cannot mutate current authorization");
        }
        private static void Check(bool value,string name) { if(!value) throw new Exception("Failed: " + name); passed.Add(name); }
        private sealed class EqualOwner { public override bool Equals(object value) { return value is EqualOwner; } public override int GetHashCode() { return 1; } }
    }
}
