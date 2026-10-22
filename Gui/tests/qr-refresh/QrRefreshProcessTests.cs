using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace DotaArchive
{
    internal static class QrRefreshProcessTests
    {
        private const string SyntheticAccount = "offline_qr_refresh_fixture";
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        private static readonly List<string> Passed = new List<string>();
        private static string Value(string[] args,string key) { int index=Array.IndexOf(args,key); return index>=0 && index+1<args.Length ? args[index+1] : ""; }
        private static string Digest(byte[] bytes) { using(var sha=SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant(); }
        private static string ImageDigest(Bitmap image) { using(var stream=new MemoryStream()) { image.Save(stream,ImageFormat.Png); return Digest(stream.ToArray()); } }
        private static VersionEntry Plan() { return new VersionEntry { Depots=new List<DepotEntry> { new DepotEntry { DepotId="11",ManifestId="22" } }, Files=new List<FileEntry>() }; }
        private static int FakeDownloader(string[] args)
        {
            Console.OutputEncoding=DownloadEngine.GetRedirectedConsoleEncoding();
            string target=Value(args,"-dir");
            FileStream guard;
            try { guard=new FileStream(Path.Combine(target,"exclusive.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None); }
            catch(IOException) { File.WriteAllText(Path.Combine(target,"overlap-detected.txt"),"A second fake downloader overlapped the previous exclusive lock."); return 83; }
            using(guard) {
                string recordPath=Path.Combine(target,"invocations.jsonl");
                int invocation=File.Exists(recordPath) ? File.ReadAllLines(recordPath).Length+1 : 1;
                bool qr=args.Contains("-qr");
                File.AppendAllText(recordPath,Json.Serialize(new { Invocation=invocation, ProcessId=Process.GetCurrentProcess().Id, Qr=qr, Account=Value(args,"-username"), ManifestOnly=args.Contains("-manifest-only"), Validate=args.Contains("-validate"), StartedAtUtc=DateTime.UtcNow.ToString("o") })+Environment.NewLine);
                if(qr) {
                    Console.WriteLine("Use the Steam Mobile App to sign in with this QR code:");
                    // Synthetic patterns exercise QR rendering; no real authorization
                    // URL, account cache, credentials or Steam service is involved.
                    for(int y=0;y<29;y++) { var line=new StringBuilder(); for(int x=0;x<29;x++) line.Append(x>=4 && y>=4 && x<25 && y<25 && (x+y+invocation)%2==0 ? "\u2588\u2588" : "  "); Console.WriteLine(line); }
                    Console.Out.Flush();
                    if(invocation==1) { Thread.Sleep(15000); return 91; }
                    Console.WriteLine("Success! Next time you can login with -username "+SyntheticAccount+" -remember-password instead of -qr.");
                }
                Console.WriteLine("Got 10 licenses for account!");
                Console.WriteLine("Total downloaded: 0 bytes (0 bytes uncompressed) from 1 depots");
                return 0;
            }
        }
        public static int Main(string[] args)
        {
            if(args.Contains("-app")) return FakeDownloader(args);
            try {
                string root=Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"../../../qr-process-"+Guid.NewGuid().ToString("N").Substring(0,8)));
                Directory.CreateDirectory(root);
                string sentinel=Path.Combine(root,"existing-client-file.bin"); byte[] original=Encoding.UTF8.GetBytes("existing partially downloaded historical game fixture"); File.WriteAllBytes(sentinel,original);
                string executable=Assembly.GetExecutingAssembly().Location, hash=Digest(File.ReadAllBytes(executable));
                var state=new SteamQrRefreshState(); string oldQr=null,newQr=null; int oldPid=0; DateTime oldExitedAt;
                using(var first=new DownloadEngine(executable,hash)) using(var cancel=new CancellationTokenSource(10000)) using(var gotQr=new ManualResetEventSlim()) {
                    state.Begin(first);
                    first.Progress+=p=>state.Observe(first,p.Phase);
                    first.QrReady+=image=>{ oldQr=ImageDigest(image); image.Dispose(); gotQr.Set(); };
                    var running=first.RunAsync(Plan(),root,"",false,DownloadMode.ManifestOnly,cancel.Token);
                    Check(gotQr.Wait(5000),"first fake downloader exposes QR while alive");
                    var firstRecord=Records(root).Single(); oldPid=Convert.ToInt32(firstRecord["ProcessId"]);
                    bool lockHeld=false; try { using(File.Open(Path.Combine(root,"exclusive.lock"),FileMode.Open,FileAccess.ReadWrite,FileShare.None)) {} } catch(IOException) { lockHeld=true; }
                    Check(lockHeld,"first fake downloader holds exclusive file lock during auth");
                    Check(state.Request(true,true),"refresh accepted only during pending auth");
                    Check(!state.Request(true,true),"second refresh click does not create another attempt");
                    cancel.Cancel(); bool cancelled=false; try { running.GetAwaiter().GetResult(); } catch(OperationCanceledException) { cancelled=true; }
                    Check(cancelled,"await RunAsync observes cancellation after owned process cleanup");
                    bool exited; try { using(var process=Process.GetProcessById(oldPid)) exited=process.HasExited; } catch(ArgumentException) { exited=true; }
                    Check(exited,"old process is dead before replacement begins"); oldExitedAt=DateTime.UtcNow;
                    using(File.Open(Path.Combine(root,"exclusive.lock"),FileMode.Open,FileAccess.ReadWrite,FileShare.None)) { Check(true,"old exclusive file lock released after awaited exit"); }
                    state.End(first);
                }
                Check(state.TakePending() && !state.TakePending(),"cleanup consumes exactly one scheduled restart");
                using(var second=new DownloadEngine(executable,hash)) {
                    state.Begin(second); second.Progress+=p=>state.Observe(second,p.Phase);
                    int qrCount=0; second.QrReady+=image=>{ newQr=ImageDigest(image); image.Dispose(); Interlocked.Increment(ref qrCount); };
                    using(var timeout=new CancellationTokenSource(10000)) Check(second.RunAsync(Plan(),root,"",false,DownloadMode.ManifestOnly,timeout.Token).GetAwaiter().GetResult()==0,"replacement downloader obtains lock and completes manifest stage");
                    Check(oldQr!=null && newQr!=null && oldQr!=newQr,"replacement rendered a different synthetic QR");
                    using(var timeout=new CancellationTokenSource(10000)) Check(second.RunAsync(Plan(),root,"",false,DownloadMode.ClientFiles,timeout.Token).GetAwaiter().GetResult()==0,"file stage completes using refreshed authorized account");
                    Check(qrCount==1,"manifest and file stages after refresh render only one QR");
                    state.End(second);
                }
                var records=Records(root);
                Check(records.Count==3,"exactly old auth, refreshed auth, and reused-account file processes launched");
                Check((bool)records[1]["Qr"] && (bool)records[1]["ManifestOnly"],"replacement manifest stage explicitly requests QR");
                Check(!(bool)records[2]["Qr"] && (string)records[2]["Account"]==SyntheticAccount && (bool)records[2]["Validate"],"file stage uses learned synthetic account and validate flag");
                Check(DateTime.Parse((string)records[1]["StartedAtUtc"]).ToUniversalTime()>=oldExitedAt,"replacement start timestamp follows confirmed prior exit");
                Check(!File.Exists(Path.Combine(root,"overlap-detected.txt")),"no fake downloader ever overlapped another process lock");
                Check(Digest(File.ReadAllBytes(sentinel))==Digest(original),"existing partial client file remains byte-for-byte unchanged");
                Check(!state.CanRefresh(false,true) && !state.Pending,"successful download does not leave refresh enabled");
                File.WriteAllText(args[0],Json.Serialize(new { Passed=Passed.Count, Cases=Passed, FakeDownloaderProcesses=records.Count, OldProcessId=oldPid, ConfirmedOldExitUtc=oldExitedAt.ToString("o"), RunDirectory=root, NetworkUsed=false, RealSteamAuthorizationRead=false, UserInterfaceUsed=false, ExistingFilePreserved=true }),new UTF8Encoding(false));
                Console.WriteLine("QR refresh fake-process integration: "+Passed.Count+" tests passed."); return 0;
            } catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
        private static List<Dictionary<string,object>> Records(string root) { return File.ReadAllLines(Path.Combine(root,"invocations.jsonl")).Select(line=>Json.Deserialize<Dictionary<string,object>>(line)).ToList(); }
        private static void Check(bool value,string name) { if(!value) throw new Exception("Failed: "+name); Passed.Add(name); }
    }
}
