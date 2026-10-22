using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace DotaArchive {
 public enum DownloadMode { ClientFiles, ManifestOnly }
 public sealed class DownloadEngine : IDisposable {
  [DllImport("kernel32.dll")] private static extern uint GetACP();
  // CREATE_NO_WINDOW gives the .NET downloader no console. Its output falls
  // back to the Windows ANSI code page, independently of the parent's console.
  internal static Encoding GetRedirectedConsoleEncoding() {return Encoding.GetEncoding(checked((int)GetACP()));}
  private readonly string engineExe,expectedSha256;
  private readonly bool allowHistoricalDepots;
  private readonly object processLock=new object();
  private readonly CancellationTokenSource disposal=new CancellationTokenSource();
  private Process ownedProcess;
  // Account name only, learned from this verified engine's successful output.
  // Credentials remain exclusively in DepotDownloader's own storage.
  private string rememberedAccountName;
  private int running,disposed;
  public event Action<ProgressInfo> Progress;
  // Each subscriber owns its bitmap; dispose it when replacing/closing the image.
  public event Action<Bitmap> QrReady;
  public event Action<string> Log;
  public string LastErrorCode {get;private set;}
  public string LastErrorSummary {get;private set;}
  public string LastDiagnosticPath {get;private set;}
  public DownloadEngine(string engineExe,string expectedSha256) : this(engineExe,expectedSha256,false) { }
  public DownloadEngine(string engineExe,string expectedSha256,bool allowHistoricalDepots) {
   if(string.IsNullOrWhiteSpace(engineExe)) throw new ArgumentException("下载引擎路径为空。");
   if(expectedSha256==null || !Regex.IsMatch(expectedSha256,"\\A[0-9a-fA-F]{64}\\z")) throw new ArgumentException("下载引擎校验值无效。");
   this.engineExe=Path.GetFullPath(engineExe);this.expectedSha256=expectedSha256.ToLowerInvariant();
   this.allowHistoricalDepots=allowHistoricalDepots;
  }
  public Task<int> RunAsync(VersionEntry version,string targetDir,string accountName,bool directSteam,CancellationToken token) {
   return RunAsync(version,targetDir,accountName,directSteam,DownloadMode.ClientFiles,token);
  }
  public async Task<int> RunAsync(VersionEntry version,string targetDir,string accountName,bool directSteam,DownloadMode mode,CancellationToken token) {
   ValidateMode(mode);
   if(Volatile.Read(ref disposed)!=0) throw new ObjectDisposedException("DownloadEngine");
   if(Interlocked.CompareExchange(ref running,1,0)!=0) throw new InvalidOperationException("已有下载任务正在运行。");
   try {
    LastErrorCode=null;LastErrorSummary=null;LastDiagnosticPath=null;
    ValidateRequest(version,targetDir,accountName);
    accountName=(accountName??"").Trim();targetDir=Path.GetFullPath(targetDir);
    string explicitlyRequestedAccount=accountName;
    if(accountName.Length==0) accountName=RememberedAccount();
    using(CancellationTokenSource linked=CancellationTokenSource.CreateLinkedTokenSource(token,disposal.Token))
    // Holding this read-only handle denies writes/deletes between hashing and launch.
    using(FileStream engineGuard=new FileStream(engineExe,FileMode.Open,FileAccess.Read,FileShare.Read)) {
     string actual=await Task.Run(delegate {using(SHA256 sha=SHA256.Create()) return Hex(sha.ComputeHash(engineGuard));},linked.Token).ConfigureAwait(false);
     if(!string.Equals(actual,expectedSha256,StringComparison.Ordinal)) throw new InvalidDataException("下载引擎 SHA256 不匹配，已阻止运行。");
     linked.Token.ThrowIfCancellationRequested();
     bool useQr=accountName.Length==0,qrFallbackUsed=useQr,cachedRecoveryUsed=false;
     for(;;) {
      linked.Token.ThrowIfCancellationRequested();
      AttemptResult result=await RunAttemptAsync(version,targetDir,accountName,useQr,directSteam,mode,linked.Token).ConfigureAwait(false);
      LastErrorCode=result.Parser.LastErrorCode;LastErrorSummary=result.Parser.LastErrorSummary;
      if(result.ExitCode!=0 && String.IsNullOrEmpty(LastErrorSummary)) {
       LastErrorCode="unclassified_exit";
       LastErrorSummary="下载引擎退出，未识别具体原因（返回码 "+result.ExitCode+"，已隐藏 "+result.Parser.SuppressedDiagnosticLineCount+" 行未识别输出）。";
       RaiseLog(LastErrorSummary);
      }
      linked.Token.ThrowIfCancellationRequested();
      string authenticatedAccount=result.Parser.AuthenticatedAccountName;
      bool acceptableIdentity=authenticatedAccount!=null && (explicitlyRequestedAccount.Length==0 || String.Equals(explicitlyRequestedAccount,authenticatedAccount,StringComparison.OrdinalIgnoreCase));
      if(result.Parser.AuthenticationSucceeded && !result.Parser.AccountIdentityAmbiguous && (!result.Parser.AuthenticationFailure || result.Parser.AuthenticationRace)) {
       if(acceptableIdentity) {accountName=authenticatedAccount;RememberAccount(accountName);}
       else if(!useQr && accountName.Length>0 && !result.Parser.AuthenticationFailure) RememberAccount(accountName);
      }
      if(!useQr && (result.Parser.CredentialsRequired || (result.Parser.AuthenticationFailure && !result.Parser.AuthenticationRace))) ForgetAccount(accountName);
      if(!useQr && !qrFallbackUsed && (result.Parser.CredentialsRequired || result.Parser.AuthenticationFailure)) {
       qrFallbackUsed=true;useQr=true;RaiseLog("已记住的登录不可用，正在切换为扫码授权。");continue;
      }
      if(useQr && result.Parser.AuthenticationRace && result.Parser.AuthenticationSucceeded && !result.Parser.AccountIdentityAmbiguous && accountName.Length>0 && !cachedRecoveryUsed && (authenticatedAccount==null || acceptableIdentity)) {
       cachedRecoveryUsed=true;useQr=false;RaiseLog("扫码已授权，但上游认证轮询随后失败；将使用已记住的账户重试一次。");continue;
      }
      if(result.Parser.HasFatalError || result.Parser.Incomplete || result.Parser.CredentialsRequired) return result.ExitCode==0?1:result.ExitCode;
      if(result.ExitCode==0) RaiseProgress(new ProgressInfo {Phase=mode==DownloadMode.ManifestOnly?"manifests_downloaded":"downloaded",Message=mode==DownloadMode.ManifestOnly?"清单获取进程已结束，等待检查全部清单并生成文件计划":"下载进程已结束，等待精确文件校验",Percent=result.Parser.LastProgress==null?0:result.Parser.LastProgress.Percent,CompletedDepots=result.Parser.LastProgress==null?0:result.Parser.LastProgress.CompletedDepots,TotalDepots=version.Depots.Count,CurrentFile=""});
      else RaiseLog("下载引擎已退出，文件已保留，可再次继续下载。");
      return result.ExitCode;
     }
    }
   } catch(OperationCanceledException) {RaiseLog(mode==DownloadMode.ManifestOnly?"清单获取已停止，已取得的清单已保留。":"下载已停止，现有文件已保留；再次下载时会校验并复用。");throw;}
   finally {Interlocked.Exchange(ref running,0);}
  }
  private string RememberedAccount() {lock(processLock) {return rememberedAccountName??"";}}
  private void RememberAccount(string name) {lock(processLock) {if(Volatile.Read(ref disposed)==0) rememberedAccountName=name;}}
  private void ForgetAccount(string name) {lock(processLock) {if(String.Equals(rememberedAccountName,name,StringComparison.OrdinalIgnoreCase)) rememberedAccountName=null;}}
  private sealed class AttemptResult {internal int ExitCode;internal CliOutputParser Parser;}
  private async Task<AttemptResult> RunAttemptAsync(VersionEntry version,string targetDir,string accountName,bool useQr,bool directSteam,DownloadMode mode,CancellationToken token) {
   CliOutputParser parser=new CliOutputParser(version,targetDir);
   DateTimeOffset started=DateTimeOffset.UtcNow;
   List<string> diagnosticLines=new List<string>();
   parser.Log+=delegate(string message) {lock(diagnosticLines) {if(diagnosticLines.Count>=512) diagnosticLines.RemoveAt(0);diagnosticLines.Add(DateTimeOffset.UtcNow.ToString("o")+" "+message);}};
   parser.Progress+=delegate(ProgressInfo info) {
    if(mode==DownloadMode.ManifestOnly) RaiseProgress(new ProgressInfo {Phase="manifest_"+info.Phase,Message="获取下载清单："+info.Message,Percent=info.Percent,CompletedDepots=info.CompletedDepots,TotalDepots=info.TotalDepots,CurrentFile=""});
    else RaiseProgress(info);
   };
   parser.Log+=RaiseLog;parser.QrReady+=RaiseQr;
   parser.InputRequired+=KillOwnedProcess;parser.AbortRequested+=KillOwnedProcess;
   Encoding redirectedEncoding=GetRedirectedConsoleEncoding();
   ProcessStartInfo start=new ProcessStartInfo {FileName=engineExe,Arguments=BuildArguments(version,targetDir,accountName,useQr,mode,allowHistoricalDepots),WorkingDirectory=Path.GetDirectoryName(engineExe),UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,RedirectStandardInput=true,StandardOutputEncoding=redirectedEncoding,StandardErrorEncoding=redirectedEncoding};
   if(directSteam) {
    // Process-local override: a placeholder prevents falling back to Windows'
    // proxy; the known Steam endpoints explicitly bypass this placeholder.
    start.EnvironmentVariables["HTTPS_PROXY"]="http://127.0.0.1:9";
    start.EnvironmentVariables["NO_PROXY"]="api.steampowered.com,.steampowered.com,.steamserver.net,.steamcontent.com,.steamstatic.com";
   }
   using(Process process=new Process {StartInfo=start}) {
    lock(processLock) {
     token.ThrowIfCancellationRequested();
     if(!process.Start()) throw new InvalidOperationException("无法启动下载引擎。");
     ownedProcess=process;
    }
    try {
     using(token.Register(KillOwnedProcess)) {
      Task stdout=ReadAsync(process.StandardOutput,parser,0);
      Task stderr=ReadAsync(process.StandardError,parser,1);
      Task exited=Task.Run(delegate {process.WaitForExit();});
      try {await Task.WhenAll(stdout,stderr,exited).ConfigureAwait(false);}
      catch {KillOwnedProcess();throw;}
      token.ThrowIfCancellationRequested();
      return new AttemptResult {ExitCode=process.ExitCode,Parser=parser};
     }
    } finally {
     lock(processLock) {if(object.ReferenceEquals(ownedProcess,process)) ownedProcess=null;}
     int? exitCode=null;try {if(process.HasExited) exitCode=process.ExitCode;}catch(InvalidOperationException){}
     SaveAttemptDiagnostic(version,targetDir,mode,started,exitCode,parser,diagnosticLines,token.IsCancellationRequested);
    }
   }
  }
  private void SaveAttemptDiagnostic(VersionEntry version,string targetDir,DownloadMode mode,DateTimeOffset started,int? exitCode,CliOutputParser parser,List<string> lines,bool cancelled) {
   try {
    string relative=".dota-archive-browser/diagnostics/engine-"+started.ToString("yyyyMMddTHHmmssfff",CultureInfo.InvariantCulture)+"-"+Guid.NewGuid().ToString("N")+".json";
    string path=VersionCatalog.SafePath(targetDir,relative);
    Directory.CreateDirectory(Path.GetDirectoryName(path));
    var depots=new List<object>();foreach(DepotEntry depot in version.Depots) depots.Add(new {DepotId=depot.DepotId,ManifestId=depot.ManifestId});
    string[] safeLines;lock(lines) safeLines=lines.ToArray();
    bool unknownFailure=exitCode.HasValue && exitCode.Value!=0 && String.IsNullOrEmpty(parser.LastErrorSummary) && !cancelled;
    var report=new {SchemaVersion=1,AppId="570",BuildId=version.BuildId,Mode=mode.ToString(),HistoricalDepots=allowHistoricalDepots,StartedAtUtc=started.ToString("o"),EndedAtUtc=DateTimeOffset.UtcNow.ToString("o"),EngineSha256=expectedSha256,ExitCode=exitCode,Cancelled=cancelled,AuthenticationSucceeded=parser.AuthenticationSucceeded,ErrorCode=unknownFailure?"unclassified_exit":parser.LastErrorCode,ErrorSummary=unknownFailure?"下载引擎退出，未识别具体原因（返回码 "+exitCode.Value+"）。":parser.LastErrorSummary,ParserFatalError=parser.HasFatalError,CredentialsRequired=parser.CredentialsRequired,SuppressedDiagnosticLineCount=parser.SuppressedDiagnosticLineCount,Depots=depots,Messages=safeLines,RawOutputStored=false};
    using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read))
    using(var writer=new StreamWriter(file,new UTF8Encoding(false))) writer.Write(new JavaScriptSerializer().Serialize(report));
    LastDiagnosticPath=path;RaiseLog("本次下载诊断已保存："+path);
   } catch(IOException) {RaiseLog("无法保存本次下载诊断，界面任务记录仍可查看。");}
     catch(UnauthorizedAccessException) {RaiseLog("没有权限保存本次下载诊断，界面任务记录仍可查看。");}
  }
  private async Task ReadAsync(StreamReader reader,CliOutputParser parser,int stream) {
   char[] buffer=new char[4096];
   try {for(;;) {int read=await reader.ReadAsync(buffer,0,buffer.Length).ConfigureAwait(false);if(read==0) break;parser.Feed(stream,new string(buffer,0,read));}}
   catch {KillOwnedProcess();throw;}
   finally {Array.Clear(buffer,0,buffer.Length);parser.Complete(stream);}
  }
  private static string Hex(byte[] hash) {return BitConverter.ToString(hash).Replace("-","").ToLowerInvariant();}
  internal static void ValidateRequest(VersionEntry version,string targetDir,string accountName) {
   if(version==null || version.Depots==null || version.Depots.Count==0) throw new ArgumentException("版本缺少内容包计划。");
   if(string.IsNullOrWhiteSpace(targetDir) || !Path.IsPathRooted(targetDir)) throw new ArgumentException("下载目录必须为绝对路径。");
   HashSet<string> ids=new HashSet<string>(StringComparer.Ordinal);
   foreach(DepotEntry depot in version.Depots) {
    uint id;ulong manifest;
    if(depot==null || !uint.TryParse(depot.DepotId,NumberStyles.None,CultureInfo.InvariantCulture,out id) || id==0 || !ulong.TryParse(depot.ManifestId,NumberStyles.None,CultureInfo.InvariantCulture,out manifest) || manifest==0 || depot.Bytes<0 || !ids.Add(depot.DepotId)) throw new ArgumentException("版本内容包或清单编号无效。");
   }
   if(accountName!=null && (accountName.Length>128 || accountName.IndexOfAny(new char[] {'\0','\r','\n'})>=0 || accountName.TrimStart().StartsWith("-",StringComparison.Ordinal))) throw new ArgumentException("Steam 账户名格式无效。");
  }
  internal static string BuildArguments(VersionEntry version,string targetDir,string accountName,bool useQr) {
   return BuildArguments(version,targetDir,accountName,useQr,DownloadMode.ClientFiles);
  }
  private static void ValidateMode(DownloadMode mode) {
   if(mode!=DownloadMode.ClientFiles && mode!=DownloadMode.ManifestOnly) throw new ArgumentOutOfRangeException("mode");
  }
  internal static string BuildArguments(VersionEntry version,string targetDir,string accountName,bool useQr,DownloadMode mode) {
   return BuildArguments(version,targetDir,accountName,useQr,mode,false);
  }
  internal static string BuildArguments(VersionEntry version,string targetDir,string accountName,bool useQr,DownloadMode mode,bool allowHistoricalDepots) {
   ValidateMode(mode);
   List<string> args=new List<string> {"-app","570","-depot"};
   foreach(DepotEntry depot in version.Depots) args.Add(depot.DepotId);
   args.Add("-manifest");foreach(DepotEntry depot in version.Depots) args.Add(depot.ManifestId);
   args.Add("-dir");args.Add(targetDir);args.Add("-max-downloads");args.Add("8");
   args.Add(mode==DownloadMode.ManifestOnly?"-manifest-only":"-validate");
   if(allowHistoricalDepots) args.Add("-history");
   if(useQr) args.Add("-qr");else {args.Add("-username");args.Add(accountName);}
   args.Add("-remember-password");
   StringBuilder command=new StringBuilder();foreach(string arg in args) {if(command.Length>0) command.Append(' ');command.Append(QuoteWindowsArgument(arg));}
   return command.ToString();
  }
  internal static string QuoteWindowsArgument(string value) {
   if(value==null || value.IndexOf('\0')>=0) throw new ArgumentException("无效的进程参数。");
   StringBuilder result=new StringBuilder("\"");int slashes=0;
   foreach(char c in value) {
    if(c=='\\') {slashes++;continue;}
    if(c=='\"') {result.Append('\\',slashes*2+1);result.Append('\"');slashes=0;continue;}
    result.Append('\\',slashes);slashes=0;result.Append(c);
   }
   result.Append('\\',slashes*2);result.Append('\"');return result.ToString();
  }
  private void KillOwnedProcess() {
   lock(processLock) {if(ownedProcess!=null) try {if(!ownedProcess.HasExited) ownedProcess.Kill();}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}}
  }
  private void RaiseLog(string message) {Action<string> handler=Log;if(handler!=null) foreach(Action<string> callback in handler.GetInvocationList()) try {callback(message);}catch {}}
  private void RaiseProgress(ProgressInfo info) {Action<ProgressInfo> handler=Progress;if(handler!=null) foreach(Action<ProgressInfo> callback in handler.GetInvocationList()) try {callback(info);}catch {}}
  private void RaiseQr(Bitmap image) {
   try {Action<Bitmap> handler=QrReady;if(handler!=null) foreach(Action<Bitmap> callback in handler.GetInvocationList()) {Bitmap copy=(Bitmap)image.Clone();try {callback(copy);}catch {copy.Dispose();}}}
   finally {image.Dispose();}
  }
  public void Dispose() {
   if(Interlocked.Exchange(ref disposed,1)!=0) return;
   disposal.Cancel();KillOwnedProcess();
   lock(processLock) {rememberedAccountName=null;}
   // Do not dispose the CTS while a concurrent RunAsync creates/uses its token.
  }
 }
}
