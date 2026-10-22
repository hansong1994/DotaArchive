using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace DotaArchive {
 internal sealed class CliOutputParser {
  private sealed class StreamState {
   internal readonly StringBuilder Line=new StringBuilder();
   internal readonly List<string> QrRows=new List<string>();
   internal bool ReadingQr, DiscardLongLine;
   internal int ExpectedRows;
  }
  private readonly object sync=new object();
  private readonly StreamState[] streams={new StreamState(),new StreamState()};
  private readonly Dictionary<string,double> weights=new Dictionary<string,double>(StringComparer.Ordinal);
  private readonly Dictionary<string,double> percentages=new Dictionary<string,double>(StringComparer.Ordinal);
  private readonly Dictionary<string,string> requestedManifests=new Dictionary<string,string>(StringComparer.Ordinal);
  private readonly HashSet<string> completed=new HashSet<string>(StringComparer.Ordinal);
  private readonly HashSet<string> knownFiles=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  private readonly string targetPrefix;
  private readonly double totalWeight;
  private string currentDepot, currentFile="";
  internal event Action<ProgressInfo> Progress;
  internal event Action<Bitmap> QrReady;
  internal event Action<string> Log;
  internal event Action InputRequired;
  internal event Action AbortRequested;
  internal bool CredentialsRequired {get;private set;}
  internal bool AuthenticationSucceeded {get;private set;}
  // Kept inside the engine only. Never publish this through Log or Progress.
  internal string AuthenticatedAccountName {get;private set;}
  internal bool AccountIdentityAmbiguous {get;private set;}
  internal bool AuthenticationFailure {get;private set;}
  internal bool AuthenticationRace {get;private set;}
  internal bool Incomplete {get;private set;}
  internal bool HasFatalError {get;private set;}
  internal string LastErrorCode {get;private set;}
  internal string LastErrorSummary {get;private set;}
  internal int SuppressedDiagnosticLineCount {get;private set;}
  private int lastErrorPriority;
  internal ProgressInfo LastProgress {get;private set;}

  internal CliOutputParser(VersionEntry version,string targetDirectory) {
   targetPrefix=Path.GetFullPath(targetDirectory).TrimEnd('\\','/')+Path.DirectorySeparatorChar;
   foreach(DepotEntry depot in version.Depots) { weights.Add(depot.DepotId,Math.Max(0,(double)depot.Bytes)); percentages.Add(depot.DepotId,0); requestedManifests.Add(depot.DepotId,depot.ManifestId); }
   foreach(double weight in weights.Values) totalWeight+=weight;
   if(version.Files!=null) foreach(FileEntry file in version.Files) if(file!=null && file.Path!=null) knownFiles.Add(file.Path.Replace('\\','/'));
   Emit("connecting","正在连接 Steam",null);
  }
  internal void Feed(int stream,string text) {
   if(stream<0 || stream>1) throw new ArgumentOutOfRangeException("stream");
   if(text==null) return;
   lock(sync) {
    StreamState state=streams[stream];
    for(int i=0;i<text.Length;i++) {
     char c=text[i];
     if(c=='\n' || c=='\r') {
      if(!state.DiscardLongLine && state.Line.Length>0) ProcessLine(state,state.Line.ToString());
      state.Line.Clear(); state.DiscardLongLine=false;
     } else if(!state.DiscardLongLine) {
      if(state.Line.Length>=32768) {state.Line.Clear();state.DiscardLongLine=true; ResetQr(state);}
      else state.Line.Append(c);
     }
    }
    // Password and Guard prompts use Console.Write rather than WriteLine.
    if(!CredentialsRequired && state.Line.Length>0) DetectInteractivePrompt(state.Line.ToString());
   }
  }
  internal void Complete(int stream) {
   lock(sync) {
    StreamState state=streams[stream];
    if(!state.DiscardLongLine && state.Line.Length>0) ProcessLine(state,state.Line.ToString());
    state.Line.Clear(); ResetQr(state);
   }
  }
  private void DetectInteractivePrompt(string line) {
   if(line.IndexOf("Enter account password for",StringComparison.OrdinalIgnoreCase)<0 &&
      line.IndexOf("Please enter your 2-factor auth code",StringComparison.OrdinalIgnoreCase)<0 &&
      line.IndexOf("Please enter your 2 factor auth code",StringComparison.OrdinalIgnoreCase)<0 &&
      line.IndexOf("Please enter the auth code sent",StringComparison.OrdinalIgnoreCase)<0 &&
      line.IndexOf("Please enter the authentication code",StringComparison.OrdinalIgnoreCase)<0) return;
   CredentialsRequired=true;
   Emit("authentication","需要重新扫码授权；本工具不会收集密码或验证码",null);
   Action handler=InputRequired; if(handler!=null) handler();
  }
  private static void ResetQr(StreamState state) {state.ReadingQr=false;state.ExpectedRows=0;state.QrRows.Clear();}
  private void ProcessLine(StreamState state,string raw) {
   string line=Regex.Replace(raw,"\u001b\\[[0-?]*[ -/]*[@-~]","");
   line=Regex.Replace(line,"\u001b\\][^\u0007]*(?:\u0007|\u001b\\\\)","");
   if(line=="Use the Steam Mobile App to sign in with this QR code:") {
    ResetQr(state);state.ReadingQr=true;Emit("authentication","请使用 Steam 手机应用扫描二维码",null);return;
   }
   if(line=="The QR code has changed:") {ResetQr(state);Emit("authentication","Steam 已更新登录二维码",null);return;}
   if(state.ReadingQr) {
    if(state.ExpectedRows==0 && line.Length>=58 && line.Length<=370 && line.Length%2==0) state.ExpectedRows=line.Length/2;
    bool valid=state.ExpectedRows>0 && line.Length==state.ExpectedRows*2;
    if(valid) for(int i=0;i<line.Length;i++) if(line[i]!=' ' && line[i]!='\u2588') {valid=false;break;}
    if(valid) {
     state.QrRows.Add(line);
     if(state.QrRows.Count==state.ExpectedRows) {
      Bitmap qr=QrRenderer.Render(state.QrRows);ResetQr(state);
      if(qr!=null) {Action<Bitmap> handler=QrReady;if(handler!=null) handler(qr);else qr.Dispose();}
      else SafeLog("登录二维码不完整，请重新请求扫码。");
     }
     return;
    }
    ResetQr(state);
   }
   DetectInteractivePrompt(line);
   if(line.StartsWith("Failed to authenticate with Steam: Failed to poll status with result FileNotFound",StringComparison.Ordinal)) {
    AuthenticationRace=true;AuthenticationFailure=true;
    SetError("authentication_poll_failed","Steam 认证轮询失败（FileNotFound）；可重新扫码或使用已记住的账户重试。",80);
    Emit("error","认证会话失效；已下载文件保留",null);RequestAbort();return;
   }
   if(line.StartsWith("Failed to authenticate with Steam:",StringComparison.Ordinal) || line.StartsWith("Access token was rejected",StringComparison.Ordinal) || line.StartsWith("Unable to login to Steam3:",StringComparison.Ordinal)) {
    AuthenticationFailure=true;SetError("authentication_failed","Steam 登录未完成"+SafeAuthenticationResult(line)+"，请重新扫码授权。",80);Emit("error","Steam 登录失败",null);RequestAbort();return;
   }
   Match authenticated=Regex.Match(line,@"\ASuccess! Next time you can login with -username ([A-Za-z0-9_][A-Za-z0-9_.@-]{0,127}) -remember-password instead of -qr\.\z",RegexOptions.CultureInvariant);
   if(authenticated.Success) {
    string name=authenticated.Groups[1].Value;
    if(AuthenticatedAccountName!=null && !String.Equals(AuthenticatedAccountName,name,StringComparison.OrdinalIgnoreCase)) {AuthenticatedAccountName=null;AccountIdentityAmbiguous=true;}
    else if(!AccountIdentityAmbiguous) AuthenticatedAccountName=name;
    AuthenticationSucceeded=true;SafeLog("Steam 已授权成功。");Emit("authenticated","Steam 已授权，正在读取版本信息",null);return;
   }
   if(Regex.IsMatch(line,"^Got [0-9]+ licenses for account!$")) {
    AuthenticationSucceeded=true;SafeLog("Steam 已授权成功。");Emit("authenticated","Steam 已授权，正在读取版本信息",null);return;
   }
   if(TryClassifyFailure(line)) return;
   Match match=Regex.Match(line,"^Downloading depot ([0-9]+)$");
   if(match.Success && weights.ContainsKey(match.Groups[1].Value)) {
    currentDepot=match.Groups[1].Value;currentFile="";SafeLog("正在下载内容包 "+currentDepot+"。");Emit("downloading","正在下载；总进度为估计值",null);return;
   }
   match=Regex.Match(line,"^Depot ([0-9]+) - Downloaded [0-9]+ bytes \\([0-9]+ bytes uncompressed\\)$");
   if(match.Success && weights.ContainsKey(match.Groups[1].Value)) {
    string id=match.Groups[1].Value;completed.Add(id);percentages[id]=100;Emit("downloading","内容包传输结束，最终文件校验尚未开始",null);return;
   }
   match=Regex.Match(line,"^\\s*([0-9]+(?:\\.[0-9]+)?)% (.+)$");
   if(match.Success && currentDepot!=null) {
    double percent;
    if(double.TryParse(match.Groups[1].Value,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out percent)) {
     percentages[currentDepot]=Math.Max(percentages[currentDepot],Math.Max(0,Math.Min(100,percent)));
     Emit("downloading","正在下载；总进度为估计值",SafeFile(match.Groups[2].Value));
    }
    return;
   }
   if(line.StartsWith("Validating ",StringComparison.Ordinal) || line.StartsWith("Pre-allocating ",StringComparison.Ordinal)) {
    int split=line.IndexOf(' ');Emit("preparing","正在检查或准备文件",SafeFile(line.Substring(split+1)));return;
   }
   if(Regex.IsMatch(line,"^App [0-9]+ was not completely downloaded\\.$")) {
    Incomplete=true;SetError("download_incomplete","上游程序报告下载不完整；已取得的文件已保留。",20);Emit("error","下载不完整",null);return;
   }
   if(line.StartsWith("Total downloaded:",StringComparison.Ordinal)) {Emit("downloaded","传输阶段结束，仍需精确文件校验",null);return;}
   if(line.StartsWith("Connecting to Steam3",StringComparison.Ordinal) || line=="Lost connection to Steam. Reconnecting") {Emit("connecting","正在连接或重新连接 Steam",null);return;}
   if(line.StartsWith("Logging in with QR code",StringComparison.Ordinal)) {Emit("authentication","正在请求 Steam 登录二维码",null);return;}
   if(line.StartsWith("STEAM GUARD! Use the Steam Mobile App",StringComparison.Ordinal)) {Emit("authentication","请在 Steam 手机应用中确认登录",null);return;}
   if(line.StartsWith("Connection timeout downloading",StringComparison.Ordinal)) {SafeLog("下载连接超时，上游程序正在重试。");return;}
   if(line.StartsWith("Error:",StringComparison.Ordinal) || line.StartsWith("Unable to download manifest",StringComparison.Ordinal) || line.StartsWith("Failed to find any server with chunk",StringComparison.Ordinal) || line.StartsWith("Download failed",StringComparison.Ordinal) || line.StartsWith("Could not connect to Steam after",StringComparison.Ordinal) || line.StartsWith("Unhandled exception.",StringComparison.Ordinal)) {
    string exception=SafeExceptionType(line);
    string code=line.StartsWith("Could not connect to Steam after",StringComparison.Ordinal)?"steam_connection_failed":exception.Length>0?"engine_exception":"engine_error";
    SetError(code,code=="steam_connection_failed"?"多次连接 Steam 失败。":"下载引擎报告错误"+(exception.Length>0?"（异常类型："+exception+"）":"；原始详情未能安全分类，已隐藏")+"。",10);
    Emit("error",LastErrorSummary,null);return;
   }
   // Deliberate whitelist: account names, Steam IDs, request codes, tokens,
   // raw QR rows, unknown diagnostics and exception details never leave here.
   if(!String.IsNullOrWhiteSpace(line) && SuppressedDiagnosticLineCount<Int32.MaxValue) SuppressedDiagnosticLineCount++;
  }
  private void SetError(string code,string summary,int priority) {
   HasFatalError=true;
   if(LastErrorSummary==null || priority>lastErrorPriority) {LastErrorCode=code;LastErrorSummary=summary;lastErrorPriority=priority;}
   SafeLog("["+code+"] "+summary);
  }
  private bool Requested(string depot) {return weights.ContainsKey(depot);}
  private bool Requested(string depot,string manifest) {string expected;return requestedManifests.TryGetValue(depot,out expected)&&expected==manifest;}
  private bool RequestedDepotList(string value,out string safeIds) {
   safeIds="";string[] ids=value.Split(new[]{", "},StringSplitOptions.None);
   if(ids.Length==0 || ids.Length>weights.Count)return false;
   var seen=new HashSet<string>(StringComparer.Ordinal);
   foreach(string id in ids)if(!Requested(id)||!seen.Add(id))return false;
   safeIds=String.Join(", ",ids);return true;
  }
  private static Match Exact(string line,string pattern) {return Regex.Match(line,"\\A"+pattern+"\\z",RegexOptions.CultureInvariant);}
  private bool TryClassifyFailure(string line) {
   string safeIds;
   Match m=Exact(line,@"Historical depot compatibility: app 570, removed depot\(s\) ([0-9]+(?:, [0-9]+)*); checking Steam access for the explicitly supplied manifests\.");
   if(m.Success&&RequestedDepotList(m.Groups[1].Value,out safeIds)) {SafeLog("已启用历史 Depot 兼容模式："+safeIds+" 已不在当前 App 570 列表中，正在按指定的精确历史 Manifest 请求 Steam 授权。");return true;}
   m=Exact(line,@"Error: Historical depot ([0-9]+) could not be prepared; the complete requested depot set was not downloaded\.");
   if(m.Success&&Requested(m.Groups[1].Value)) {SetError("historical_depot_incomplete","历史 Depot "+m.Groups[1].Value+" 未能完成准备，完整请求组合尚未取得；请查看前面的具体错误。",30);return true;}
   if(line=="Error: -history will not infer a historical shared-depot source from another app's current metadata.") {SetError("historical_source_unverified","当前共享 Depot 指向其他 App，无法据此确定历史来源；已停止，未改用其他 App 或当前 Manifest。",90);return true;}
   m=Exact(line,@"Depot ([0-9]+(?:, [0-9]+)*) not listed for app 570");
   if(m.Success&&RequestedDepotList(m.Groups[1].Value,out safeIds)) {SetError("depot_not_listed","Steam 当前 App 570 配置未列出所请求的 Depot："+safeIds+"。这条错误本身不证明历史 Manifest 已删除。",100);return true;}
   if(Exact(line,@"App 570 \([^\r\n]*\) is not available from this account\.").Success) {SetError("app_access_denied","Steam 未确认当前账户对 App 570 的访问许可。",100);return true;}
   if(line=="Couldn't find any depots to download for app 570") {SetError("depot_not_listed","Steam 当前 App 570 配置没有可用的下载 Depot。",100);return true;}
   m=Exact(line,@"Depot ([0-9]+) is not available from this account\.");
   if(m.Success&&Requested(m.Groups[1].Value)) {SetError("depot_access_denied","Steam 未确认当前账户对 Depot "+m.Groups[1].Value+" 的访问许可。",100);return true;}
   m=Exact(line,@"No valid depot key for ([0-9]+), unable to download\.");
   if(m.Success&&Requested(m.Groups[1].Value)) {SetError("depot_key_unavailable","Steam 未返回 Depot "+m.Groups[1].Value+" 的有效解密密钥，无法获取该内容包。",90);return true;}
   m=Exact(line,@"Got depot key for ([0-9]+) result: ([A-Za-z0-9]+)");
   if(m.Success&&Requested(m.Groups[1].Value)) {
    string result=SafeResult(m.Groups[2].Value);
    if(result=="OK") return true;
    if(result.Length>0) {SetError("depot_key_unavailable","Steam 请求 Depot "+m.Groups[1].Value+" 解密密钥失败（EResult："+result+"）。",100);return true;}
   }
   m=Exact(line,@"No manifest request code was returned for depot ([0-9]+) from app 570, manifest ([0-9]+)");
   if(m.Success&&Requested(m.Groups[1].Value,m.Groups[2].Value)) {SetError("manifest_request_code_unavailable","Steam 未返回 Depot "+m.Groups[1].Value+" / Manifest "+m.Groups[2].Value+" 的清单访问授权码；具体原因未由上游说明。",100);return true;}
   m=Exact(line,@"Encountered ([1-5][0-9]{2}) for depot manifest ([0-9]+) ([0-9]+)\. Aborting\.");
   if(m.Success&&Requested(m.Groups[2].Value,m.Groups[3].Value)) {SetError("manifest_http_error","Steam CDN 获取 Depot "+m.Groups[2].Value+" / Manifest "+m.Groups[3].Value+" 时返回 HTTP "+m.Groups[1].Value+"，上游已停止该清单请求。",100);return true;}
   m=Exact(line,@"Unable to download manifest ([0-9]+) for depot ([0-9]+)");
   if(m.Success&&Requested(m.Groups[2].Value,m.Groups[1].Value)) {SetError("manifest_unavailable","未能获取 Depot "+m.Groups[2].Value+" / Manifest "+m.Groups[1].Value+"；请查看前面的具体错误。",40);return true;}
   m=Exact(line,@"Depot ([0-9]+) missing public subsection or manifest section\.");
   if(m.Success&&Requested(m.Groups[1].Value)) {SetError("manifest_unavailable","Depot "+m.Groups[1].Value+" 当前配置缺少 public 分支或 Manifest。",90);return true;}
   m=Exact(line,@"Got CDN auth token for [^\r\n]+ result: ([A-Za-z0-9]+) \(expires [^\r\n]*\)");
   if(m.Success) {string result=SafeResult(m.Groups[1].Value);if(result=="OK")return true;if(result.Length>0){SetError("cdn_auth_failed","Steam CDN 授权请求失败（EResult："+result+"）。",95);return true;}}
   if(Exact(line,@"Insufficient privileges to get access token for app 570").Success) {SafeLog("Steam 暂未授予 App 570 元数据访问令牌；等待后续权限检查结果。");return true;}
   if(Exact(line,@"Failed to request FreeOnDemand license for app 570: [^\r\n]*").Success) {SafeLog("Steam 的 App 570 免费许可请求失败；原始异常详情已隐藏，等待后续权限检查结果。");return true;}
   if(line.StartsWith("Failed to allocate file ",StringComparison.Ordinal) || line.StartsWith("Failed to resize file to expected size ",StringComparison.Ordinal) || line=="Error: Unable to create install directories!") {SetError("file_operation_failed","下载引擎无法创建、分配或调整客户端文件；路径与原始异常消息已隐藏。",80);return true;}
   m=Exact(line,@"Connection timeout downloading depot manifest ([0-9]+) ([0-9]+)\. Retrying\.");
   if(m.Success&&Requested(m.Groups[1].Value,m.Groups[2].Value)) {SafeLog("Steam CDN 获取 Depot "+m.Groups[1].Value+" / Manifest "+m.Groups[2].Value+" 超时，上游正在重试。");return true;}
   m=Exact(line,@"Encountered error downloading depot manifest ([0-9]+) ([0-9]+): ([A-Za-z0-9]+)");
   if(m.Success&&Requested(m.Groups[1].Value,m.Groups[2].Value)) {string status=SafeHttpStatus(m.Groups[3].Value);SafeLog("Steam CDN 获取 Depot "+m.Groups[1].Value+" / Manifest "+m.Groups[2].Value+" 失败"+(status.Length>0?"（HTTP "+status+"）":"（状态详情已隐藏）")+"，上游可能继续重试。");return true;}
   m=Exact(line,@"Encountered error downloading manifest for depot ([0-9]+) ([0-9]+): [^\r\n]*");
   if(m.Success&&Requested(m.Groups[1].Value,m.Groups[2].Value)) {string type=SafeExceptionType(line);SafeLog("获取 Depot "+m.Groups[1].Value+" / Manifest "+m.Groups[2].Value+" 时发生异常"+(type.Length>0?"（"+type+"）":"（详情已隐藏）")+"，上游可能继续重试。");return true;}
   return false;
  }
  private static string SafeResult(string value) {
   const string allowed="|Invalid|OK|Fail|NoConnection|InvalidPassword|LoggedInElsewhere|InvalidProtocolVer|InvalidParam|FileNotFound|Busy|InvalidState|AccessDenied|Timeout|Banned|AccountNotFound|InvalidSteamID|ServiceUnavailable|NotLoggedOn|Pending|EncryptionFailure|InsufficientPrivilege|Expired|Revoked|LimitExceeded|PersistFailed|RemoteDisconnect|IOFailure|RemoteCallFailed|InvalidSignature|NoMatch|AccountDisabled|AccountLogonDenied|AccountLoginDeniedNeedTwoFactor|TryAnotherCM|RateLimitExceeded|AccountLoginDeniedThrottle|TwoFactorCodeMismatch|TwoFactorActivationCodeMismatch|NoMobileDevice|NeedCaptcha|DiskFull|Cancelled|DataCorruption|RegionLocked|RestrictedDevice|AccountLockedDown|GSLTDenied|LicenseExpired|NoLauncherSpecified|MustAgreeToSSA|";
   if(allowed.IndexOf("|"+value+"|",StringComparison.Ordinal)>=0)return value;
   int numeric;if(Int32.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out numeric)&&numeric>=0&&numeric<=255)return numeric.ToString(CultureInfo.InvariantCulture);
   return "";
  }
  private static string SafeHttpStatus(string value) {
   int status;if(Int32.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out status)&&status>=100&&status<=599)return status.ToString(CultureInfo.InvariantCulture);
   switch(value) {case "Unauthorized":return "401";case "Forbidden":return "403";case "NotFound":return "404";case "RequestTimeout":return "408";case "TooManyRequests":return "429";case "InternalServerError":return "500";case "BadGateway":return "502";case "ServiceUnavailable":return "503";case "GatewayTimeout":return "504";default:return "";}
  }
  private static string SafeAuthenticationResult(string line) {
   Match m=Exact(line,@"Access token was rejected \(([A-Za-z0-9]+)\)\.");
   if(!m.Success)m=Exact(line,@"Unable to login to Steam3: ([A-Za-z0-9]+)");
   if(!m.Success)m=Exact(line,@"Failed to authenticate with Steam: Failed to poll status with result ([A-Za-z0-9]+)");
   string result=m.Success?SafeResult(m.Groups[1].Value):"";return result.Length>0?"（EResult："+result+"）":"";
  }
  private static string SafeExceptionType(string line) {
   foreach(string type in new[]{"UnauthorizedAccessException","IOException","SocketException","HttpRequestException","WebException","TimeoutException","InvalidDataException","CryptographicException","TaskCanceledException","OperationCanceledException","DirectoryNotFoundException","FileNotFoundException","ArgumentException","InvalidOperationException","SteamKitWebRequestException"})
    if(Regex.IsMatch(line,@"(?:\A|[ .:])"+type+@"(?:\z|[ :])",RegexOptions.CultureInvariant))return type;
   return "";
  }
  private string SafeFile(string value) {
   try {
    string full=Path.GetFullPath(value);
    if(!full.StartsWith(targetPrefix,StringComparison.OrdinalIgnoreCase)) return "";
    string relative=full.Substring(targetPrefix.Length).Replace('\\','/');
    return knownFiles.Contains(relative)?relative:"";
   } catch {return "";}
  }
  private void SafeLog(string message) {Action<string> handler=Log;if(handler!=null) handler(message);}
  private void RequestAbort() {Action handler=AbortRequested;if(handler!=null) handler();}
  private void Emit(string phase,string message,string file) {
   if(file!=null) currentFile=file;
   double weighted=0;
   if(totalWeight>0) foreach(KeyValuePair<string,double> pair in weights) weighted+=pair.Value*percentages[pair.Key]/totalWeight;
   else if(weights.Count>0) foreach(double value in percentages.Values) weighted+=value/weights.Count;
   LastProgress=new ProgressInfo {Phase=phase,Message=message,CurrentFile=currentFile,Percent=Math.Min(99.9,weighted),CompletedDepots=completed.Count,TotalDepots=weights.Count};
   Action<ProgressInfo> handler=Progress;if(handler!=null) handler(LastProgress);
  }
 }
}
