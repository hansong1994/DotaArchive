using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace DotaArchive {
internal static class AccountReuseTests {
 private const string FixtureAccount="qr_reuse_fixture_user";
 private const string OtherAccount="explicit_fixture_user";
 private static int assertions;
 private static void Check(bool value,string message){assertions++;if(!value)throw new Exception(message);}
 private static string Hash(string value){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-","").ToLowerInvariant();}
 private static string Value(string[] args,string flag){int i=Array.IndexOf(args,flag);return i>=0&&i+1<args.Length?args[i+1]:"";}
 private static string Success(string name){return "Success! Next time you can login with -username "+name+" -remember-password instead of -qr.";}
 private static VersionEntry Plan(){return new VersionEntry{Depots=new List<DepotEntry>{new DepotEntry{DepotId="11",ManifestId="22",Bytes=0}},Files=new List<FileEntry>()};}
 private static void FakeQr(){
  Console.WriteLine("Use the Steam Mobile App to sign in with this QR code:");
  for(int y=0;y<29;y++){var row=new StringBuilder();for(int x=0;x<29;x++)row.Append(x>=4&&y>=4&&x<25&&y<25&&(x+y)%2==0?"\u2588\u2588":"  ");Console.WriteLine(row);}
 }
 private static int FakeEngine(string[] args){
  Console.OutputEncoding=DownloadEngine.GetRedirectedConsoleEncoding();
  string target=Value(args,"-dir"),scenario=File.ReadAllText(Path.Combine(target,"scenario.txt")).Trim(),records=Path.Combine(target,"invocations.jsonl");
  int invocation=File.Exists(records)?File.ReadAllLines(records).Length+1:1;
  bool qr=args.Contains("-qr");string account=Value(args,"-username");
  File.AppendAllText(records,new JavaScriptSerializer().Serialize(new{Qr=qr,AccountHash=Hash(account),Remember=args.Contains("-remember-password"),Manifest=args.Contains("-manifest-only")})+Environment.NewLine);
  if((scenario=="expired"||scenario=="race-cached-rejected")&&invocation==2&&!qr){Console.Write("Enter account password for \"synthetic fixture\": ");Console.Out.Flush();Thread.Sleep(15000);return 7;}
  if(qr){
   FakeQr();
   Console.WriteLine(Success(scenario=="invalid"?"-invalid_argument":FixtureAccount));
   if(scenario=="ambiguous")Console.WriteLine(Success(OtherAccount));
   if(scenario=="race"||scenario=="race-cached-rejected"){
    Console.WriteLine("Failed to authenticate with Steam: Failed to poll status with result FileNotFound");Console.Out.Flush();Thread.Sleep(15000);return 8;
   }
  }
  Console.WriteLine("Got 10 licenses for account!");Console.WriteLine("Total downloaded: 0 bytes (0 bytes uncompressed) from 1 depots");return 0;
 }
 private static List<Dictionary<string,object>> Records(string dir){var json=new JavaScriptSerializer();return File.ReadAllLines(Path.Combine(dir,"invocations.jsonl")).Select(s=>json.Deserialize<Dictionary<string,object>>(s)).ToList();}
 private static int Run(DownloadEngine engine,string dir,string account,DownloadMode mode){using(var timeout=new CancellationTokenSource(15000))return engine.RunAsync(Plan(),dir,account,false,mode,timeout.Token).GetAwaiter().GetResult();}
 private static string Scenario(string root,string name){string dir=Path.Combine(root,name);Directory.CreateDirectory(dir);File.WriteAllText(Path.Combine(dir,"scenario.txt"),name);return dir;}
 public static int Main(string[] args){
  if(args.Contains("-app"))return FakeEngine(args);
  try {
   string root=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"runs",DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));Directory.CreateDirectory(root);
   var exposed=new List<string>();
   var parser=new CliOutputParser(Plan(),root);parser.Log+=exposed.Add;parser.Progress+=p=>exposed.Add(p.Message+"|"+p.CurrentFile);
   string fragmented=Success(FixtureAccount)+"\r\n";foreach(char c in fragmented)parser.Feed(0,c.ToString());
   Check(parser.AuthenticationSucceeded&&parser.AuthenticatedAccountName==FixtureAccount,"Fragmented exact success learns account internally");
   foreach(string invalid in new[]{"-bad","bad value","bad\"value","bad\\value",new string('a',129),"bad\tvalue"}){
    var bad=new CliOutputParser(Plan(),root);bad.Feed(0,Success(invalid)+"\n");Check(bad.AuthenticatedAccountName==null,"Invalid account output not adopted");
   }
   foreach(string invalid in new[]{Success(FixtureAccount)+" trailing",Success(FixtureAccount).TrimEnd('.'),"untrusted prefix "+Success(FixtureAccount)}){
    var bad=new CliOutputParser(Plan(),root);bad.Feed(0,invalid+"\n");Check(bad.AuthenticatedAccountName==null,"Nonexact success output not adopted");
   }
   var ambiguous=new CliOutputParser(Plan(),root);ambiguous.Feed(0,Success(FixtureAccount)+"\n"+Success(OtherAccount)+"\n"+Success(FixtureAccount)+"\n");
   Check(ambiguous.AuthenticatedAccountName==null&&ambiguous.AccountIdentityAmbiguous,"Contradictory identities never adopted");
   var licenseOnly=new CliOutputParser(Plan(),root);licenseOnly.Feed(0,"Got 10 licenses for account!\n");Check(licenseOnly.AuthenticationSucceeded&&licenseOnly.AuthenticatedAccountName==null,"License message cannot invent account identity");
   string executable=Assembly.GetExecutingAssembly().Location,digest;using(var stream=File.OpenRead(executable))using(var sha=SHA256.Create())digest=BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant();
   int totalFakeQr=0;
   foreach(string scenario in new[]{"normal","race","invalid","ambiguous","expired","race-cached-rejected"}){
    string dir=Scenario(root,scenario);int qrCount=0;
    using(var engine=new DownloadEngine(executable,digest)){
     engine.Log+=exposed.Add;engine.Progress+=p=>exposed.Add(p.Message+"|"+p.CurrentFile);engine.QrReady+=image=>{qrCount++;image.Dispose();};
     int first=Run(engine,dir,"",DownloadMode.ManifestOnly);
     if(scenario=="race-cached-rejected"){
      Check(first!=0,"Rejected cached race recovery terminates rather than looping QR");var records=Records(dir);Check(records.Count==2&&(bool)records[0]["Qr"]&&!(bool)records[1]["Qr"],"Race recovery attempted once with cached identity");
      Check(qrCount==1,"Race failure still rendered only one QR");
     }else{
      Check(first==0,"Manifest fake process succeeds");Check(Run(engine,dir,"",DownloadMode.ClientFiles)==0,"Client fake process succeeds");
      var records=Records(dir);Check(records.All(r=>(bool)r["Remember"]),"All auth attempts use official remember-password flag");
      if(scenario=="normal"||scenario=="race"){
       Check(qrCount==1&&records.Count(r=>(bool)r["Qr"])==1,"Manifest and client stages need one QR");
       Check((string)records.Last()["AccountHash"]==Hash(FixtureAccount),"Client stage reuses authenticated identity");
       if(scenario=="race")Check(records.Count==3,"QR race recovered once before next stage");
      }else if(scenario=="invalid"||scenario=="ambiguous")Check(qrCount==2&&records.All(r=>(bool)r["Qr"]),"Invalid or ambiguous output never reused");
      else if(scenario=="expired")Check(qrCount==2&&records.Count==3,"Expired cache falls back to one fresh QR");
      if(scenario=="normal"){
       Check(Run(engine,dir,OtherAccount,DownloadMode.ManifestOnly)==0,"Explicit alternate account succeeds");records=Records(dir);Check(!(bool)records.Last()["Qr"]&&(string)records.Last()["AccountHash"]==Hash(OtherAccount),"Explicit alternate account overrides automatic identity");
       Check(Run(engine,dir,"",DownloadMode.ClientFiles)==0,"Automatic followup after explicit account succeeds");records=Records(dir);Check((string)records.Last()["AccountHash"]==Hash(OtherAccount),"Authorized explicit account becomes latest remembered identity");
      }
     }
    }
    totalFakeQr+=qrCount;
   }
   Check(exposed.All(s=>!s.Contains(FixtureAccount)&&!s.Contains(OtherAccount)&&!s.Contains("-username")&&!s.Contains("-invalid_argument")),"Public Log and Progress contain no parsed or explicit account names");
   Console.WriteLine(new JavaScriptSerializer().Serialize(new{Result="Passed",Assertions=assertions,FakeQrImages=totalFakeQr,Scope="Local fake-engine subprocesses and parser tests only. No Steam, network, real account cache, credentials or UI used.",Utc=DateTime.UtcNow.ToString("o"),SyntheticRunDirectory=root}));return 0;
  }catch(Exception ex){Console.Error.WriteLine(ex.Message);return 1;}
 }
}}
