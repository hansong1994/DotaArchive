using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace DotaArchive
{
    // Tests operate only on copies beneath this directory. No DLL is executed,
    // no game is launched and no Steam account or host client is touched.
    internal static class HistoricalIcuTests
    {
        private const string Bin = "game/bin/win64/";
        private static readonly string[] Names = { "icui18n.dll", "v8.dll", "icuuc.dll" };
        private static readonly string[] OriginalHashes = {
            "33441dbcc754e32de93ea47e70036d670087557d", "d45b785c1c8227597fe290fcdf9e533d4d5095fa", "0faa3b0fed45c220519f2c591882dad8e3058809" };
        private static readonly string[] PatchedHashes = {
            "728c8b70e5a4a9e1effef993465fbf547c833355", "0e57a99b8b2c94943d9c22f42fd6276c43b73473" };
        private static string testRoot, fixtures, runRoot;
        private static int assertions;
        private static List<object> cases = new List<object>();
        private static List<string> failures = new List<string>();

        public static int Main(string[] args)
        {
            testRoot = Path.GetFullPath(args[0]); fixtures = Path.GetFullPath(args[1]);
            runRoot = Path.Combine(testRoot, "r", Guid.NewGuid().ToString("N").Substring(0,8));
            Directory.CreateDirectory(runRoot);
            Case("frozen-real-binary-fixtures", CheckFixtures);
            Case("derive-install-verify-repeat-restore", RoundTrip);
            Case("unknown-consumer-original", delegate { RejectChangedInput("icui18n.dll"); });
            Case("unknown-second-consumer-original", delegate { RejectChangedInput("v8.dll"); });
            Case("unknown-provider-original", delegate { RejectChangedInput("icuuc.dll"); });
            Case("backup-tampering-with-patched-target", delegate { RejectBadBackup(false); });
            Case("backup-tampering-with-original-target", delegate { RejectBadBackup(true); });
            Case("private-copy-tampering", RejectBadPrivateCopy);
            Case("patched-target-without-original-backup", RejectMissingBackup);
            Case("partly-installed-overlay", RejectPartialOverlay);
            Case("non-win64-unaffected", NonWin64);
            Case("no-provider-unaffected", NoProvider);
            Case("later-icu-provider-unaffected", LaterProvider);
            Case("later-consumers-with-private-provider-unaffected", LaterConsumers);
            Case("truncated-consumer-pe", TruncatedConsumer);
            Case("malformed-consumer-pe-pointer", InvalidPePointer);
            Case("provider-missing-required-export", MissingExport);
            Case("additional-unsupported-consumer", AdditionalConsumer);
            var report = new {
                Passed = failures.Count == 0, Assertions = assertions, Cases = cases, Failures = failures,
                FixtureDirectory = fixtures, RunDirectory = runRoot, CompletedAtUtc = DateTime.UtcNow.ToString("o"),
                SteamRequests = 0, GameLaunches = 0, HostClientWrites = 0,
                Scope = "Real ICU 52 PE files copied into isolated test directories; deterministic import repair, manifest integrity, reversible installation and refusal paths."
            };
            File.WriteAllText(Path.Combine(testRoot,"results.json"), new JavaScriptSerializer { MaxJsonLength=4*1024*1024 }.Serialize(report), new UTF8Encoding(false));
            Console.WriteLine("Assertions: " + assertions + "; case failures: " + failures.Count);
            foreach(string failure in failures) Console.Error.WriteLine(failure);
            return failures.Count == 0 ? 0 : 1;
        }

        private static void Case(string name, Action action)
        {
            int start=assertions;
            try { action(); cases.Add(new { Name=name, Passed=true, Assertions=assertions-start }); Console.WriteLine("PASS " + name); }
            catch(Exception ex) { failures.Add(name + ": " + ex.GetType().Name + " " + ex.Message); cases.Add(new { Name=name, Passed=false, Assertions=assertions-start, Error=ex.ToString() }); Console.WriteLine("FAIL " + name); }
        }
        private static void Check(bool value, string message) { assertions++; if(!value)throw new Exception(message); }
        private static void Reject(Action action, string message)
        {
            bool rejected=false;
            try { action(); } catch(InvalidDataException) { rejected=true; } catch(FileNotFoundException) { rejected=true; }
            Check(rejected,message);
        }
        private static string Hash(byte[] value) { using(var sha=SHA1.Create()) return BitConverter.ToString(sha.ComputeHash(value)).Replace("-","").ToLowerInvariant(); }
        private static string Hash(string path) { return Hash(File.ReadAllBytes(path)); }
        private static string Full(string root, string relative) { return VersionCatalog.SafePath(root, relative); }
        private static void Write(string root, string relative, byte[] bytes) { string file=Full(root,relative);Directory.CreateDirectory(Path.GetDirectoryName(file));File.WriteAllBytes(file,bytes); }
        private static byte[] Read(string root,string relative) { return File.ReadAllBytes(Full(root,relative)); }
        private static string Snapshot(string root, bool receipt)
        {
            return String.Join("\n", Directory.GetFiles(root,"*",SearchOption.AllDirectories).Where(f=>receipt || !f.EndsWith("gui-installation.json",StringComparison.Ordinal)).OrderBy(f=>f,StringComparer.Ordinal).Select(f=>f.Substring(root.Length)+"="+Hash(f)));
        }
        private static string Fingerprint(CompatibilitySpec spec) { return new JavaScriptSerializer().Serialize(spec); }
        private static void Modify(string root, string relative) { byte[] bytes=Read(root,relative);bytes[bytes.Length-1]^=0x40;Write(root,relative,bytes); }
        private static void SetManifest(VersionEntry version,string root,string relative)
        {
            var item=version.Files.Single(f=>f.Path==relative);item.Size=new FileInfo(Full(root,relative)).Length;item.Sha1=Hash(Full(root,relative));version.TotalBytes=version.Files.Sum(f=>f.Size);
        }
        private static VersionEntry Create(string name, out string root)
        {
            root=Path.Combine(runRoot,cases.Count.ToString("D2"));Directory.CreateDirectory(root);
            var v=new VersionEntry {
                Id="icu-test-1532815", Patch="7.01",Date="2016-12-21",BuildId="1532815",PublishedAtUtc="2016-12-21T00:00:00Z",ClientVersion="1",FolderName="icu-test-1532815",
                Depots=new List<DepotEntry>{new DepotEntry{DepotId="373303",ManifestId="1",Name="Isolated fixture"}},Files=new List<FileEntry>(),Directories=new List<string>(),Executable=Bin+"dota2.exe"
            };
            foreach(string namePart in Names) {byte[] bytes=File.ReadAllBytes(Path.Combine(fixtures,namePart));Write(root,Bin+namePart,bytes);v.Files.Add(new FileEntry{Path=Bin+namePart,Size=bytes.Length,Sha1=Hash(bytes)});}
            // A valid PE image at the executable path, never executed. The provider
            // has no ICU imports so it does not add another compatibility target.
            byte[] executable=File.ReadAllBytes(Path.Combine(fixtures,"icuuc.dll"));
            Write(root,v.Executable,executable);v.Files.Add(new FileEntry{Path=v.Executable,Size=executable.Length,Sha1=Hash(executable)});
            v.TotalBytes=v.Files.Sum(f=>f.Size);VersionCatalog.ValidateVersion(v);return v;
        }
        private static void CheckFixtures()
        {
            for(int i=0;i<Names.Length;i++) Check(Hash(Path.Combine(fixtures,Names[i]))==OriginalHashes[i],"Fixture differs from separately verified 7.00/7.01 bytes: "+Names[i]);
        }
        private static void RoundTrip()
        {
            string root;var v=Create("round-trip",out root);string original=Snapshot(root,true);
            CompatibilitySpec spec=HistoricalIcuCompatibility.Derive(v,root);
            Check(spec!=null,"Applicable ICU52 was not detected");Check(spec.Patches.Count==2,"Must repair both consumers");Check(spec.Copies.Count==1,"Must use exactly one private provider");
            Check(Snapshot(root,true)==original,"Derivation must be read-only");
            Check(Fingerprint(spec)==Fingerprint(HistoricalIcuCompatibility.Derive(v,root)),"Derivation not deterministic");
            for(int i=0;i<2;i++)
            {
                var p=spec.Patches.Single(patch=>patch.Path==Bin+Names[i]);
                Check(p.OriginalSha1==OriginalHashes[i],"Wrong manifest original hash");Check(p.PatchedSha1==PatchedHashes[i],"Generated PE differs from independent frozen candidate");
                int first=i==0?1700555:5756231;Check(p.Offsets.SequenceEqual(new[]{first,first+1}),"Changes must address known PE import string");
                Check(p.BeforeBytes.SequenceEqual(new[]{117,99}) && p.AfterBytes.SequenceEqual(new[]{53,50}),"Unexpected import replacement bytes");
            }
            Check(spec.Copies[0].Source==Bin+"icuuc.dll" && spec.Copies[0].Target==Bin+"icu52.dll","Private provider must stay in client directory");Check(spec.Copies[0].Sha1==OriginalHashes[2],"Private provider must remain byte-identical");
            v.Compatibility=spec;VersionCatalog.ValidateVersion(v);
            Check(ManifestVerifier.Verify(v,root,false,CancellationToken.None,null).Success,"Original fixture must verify before install");
            RuntimeLauncher.PrepareCompatibility(v,root);
            for(int i=0;i<2;i++)
            {
                byte[] before=File.ReadAllBytes(Path.Combine(fixtures,Names[i])),after=Read(root,Bin+Names[i]);
                Check(Hash(after)==PatchedHashes[i],"Installed target mismatch");Check(before.Length==after.Length,"Repair changed image length");
                Check(before.Where((b,index)=>b!=after[index]).Count()==2,"Repair altered bytes outside import basename");
                Check(Hash(Full(root,VersionCatalog.BackupRoot+Names[i]))==OriginalHashes[i],"Original backup not retained");
            }
            Check(Hash(Full(root,Bin+"icuuc.dll"))==OriginalHashes[2],"Original provider changed");Check(Hash(Full(root,Bin+"icu52.dll"))==OriginalHashes[2],"Private provider mismatch");
            var compatible=ManifestVerifier.Verify(v,root,true,CancellationToken.None,null);Check(compatible.Success && compatible.UsesCompatibility,"Compatibility-aware verification rejected installed overlay");
            Check(!ManifestVerifier.Verify(v,root,false,CancellationToken.None,null).Success,"Strict original verifier must detect repaired DLLs");
            string installed=Snapshot(root,false);Check(Fingerprint(HistoricalIcuCompatibility.Derive(v,root))==Fingerprint(spec),"Derivation from repaired files and backups changed plan");
            RuntimeLauncher.PrepareCompatibility(v,root);Check(Snapshot(root,false)==installed,"Repeated install changed stable files");
            RuntimeLauncher.RestoreOriginals(v,root);
            for(int i=0;i<Names.Length;i++) Check(Hash(Full(root,Bin+Names[i]))==OriginalHashes[i],"Restoration failed: "+Names[i]);
            var restored=ManifestVerifier.Verify(v,root,false,CancellationToken.None,null);Check(restored.Success && !restored.UsesCompatibility,"Restored files did not pass original manifest verification");
            string restoredSnapshot=Snapshot(root,false);RuntimeLauncher.RestoreOriginals(v,root);Check(Snapshot(root,false)==restoredSnapshot,"Restore is not idempotent");
            Check(Fingerprint(HistoricalIcuCompatibility.Derive(v,root))==Fingerprint(spec),"Derivation from restored files changed plan");
        }
        private static void RejectChangedInput(string file)
        {
            string root;var v=Create("changed-"+file,out root);Modify(root,Bin+file);string snapshot=Snapshot(root,true);
            Reject(delegate {HistoricalIcuCompatibility.Derive(v,root);},"Unrecognized input was accepted: "+file);
            Check(Snapshot(root,true)==snapshot,"Rejected input was modified");
        }
        private static void RejectBadBackup(bool originalTarget)
        {
            string root;var v=Create("bad-backup-"+originalTarget,out root);v.Compatibility=HistoricalIcuCompatibility.Derive(v,root);RuntimeLauncher.PrepareCompatibility(v,root);
            if(originalTarget) RuntimeLauncher.RestoreOriginals(v,root);
            Modify(root,VersionCatalog.BackupRoot+"v8.dll");string snapshot=Snapshot(root,true);
            Reject(delegate {HistoricalIcuCompatibility.Derive(v,root);},"Corrupt original backup accepted by derivation");
            Reject(delegate {RuntimeLauncher.PrepareCompatibility(v,root);},"Corrupt backup accepted by installer");
            if(!originalTarget) {Reject(delegate {RuntimeLauncher.RestoreOriginals(v,root);},"Corrupt backup accepted by restore");Check(!ManifestVerifier.Verify(v,root,true,CancellationToken.None,null).Success,"Verifier accepted corrupt active backup");}
            Check(Snapshot(root,true)==snapshot,"Backup rejection partially changed client");
        }
        private static void RejectBadPrivateCopy()
        {
            string root;var v=Create("bad-private",out root);v.Compatibility=HistoricalIcuCompatibility.Derive(v,root);RuntimeLauncher.PrepareCompatibility(v,root);Modify(root,Bin+"icu52.dll");string snapshot=Snapshot(root,true);
            Reject(delegate {RuntimeLauncher.PrepareCompatibility(v,root);},"Unknown private provider was overwritten");Reject(delegate {RuntimeLauncher.RestoreOriginals(v,root);},"Unknown private provider accepted by restore");
            Check(!ManifestVerifier.Verify(v,root,true,CancellationToken.None,null).Success,"Verifier accepted modified private provider");Check(Snapshot(root,true)==snapshot,"Private-provider rejection modified files");
        }
        private static void RejectMissingBackup()
        {
            string root;var v=Create("missing-backup",out root);v.Compatibility=HistoricalIcuCompatibility.Derive(v,root);RuntimeLauncher.PrepareCompatibility(v,root);
            File.Delete(Full(root,VersionCatalog.BackupRoot+"icui18n.dll"));string snapshot=Snapshot(root,true);
            Reject(delegate {HistoricalIcuCompatibility.Derive(v,root);},"Repaired target without original proof was accepted");Reject(delegate {RuntimeLauncher.PrepareCompatibility(v,root);},"Installer accepted missing original backup");
            Check(!ManifestVerifier.Verify(v,root,true,CancellationToken.None,null).Success,"Verifier accepted missing original backup");Check(Snapshot(root,true)==snapshot,"Missing-backup rejection modified files");
        }
        private static void RejectPartialOverlay()
        {
            string root;var v=Create("partial",out root);v.Compatibility=HistoricalIcuCompatibility.Derive(v,root);RuntimeLauncher.PrepareCompatibility(v,root);
            Write(root,Bin+"v8.dll",File.ReadAllBytes(Path.Combine(fixtures,"v8.dll")));
            Check(!ManifestVerifier.Verify(v,root,true,CancellationToken.None,null).Success,"Partial overlay should not verify");
            RuntimeLauncher.PrepareCompatibility(v,root);Check(ManifestVerifier.Verify(v,root,true,CancellationToken.None,null).Success,"Installer could not finish recognized partial overlay");
        }
        private static void NonWin64()
        {
            string root;var v=Create("non-win64",out root);v.Executable="game/bin/win32/dota2.exe";string before=Snapshot(root,true);
            Check(HistoricalIcuCompatibility.Derive(v,root)==null,"Non-win64 should not receive ICU52 plan");Check(Snapshot(root,true)==before,"Non-win64 input modified");
        }
        private static void NoProvider()
        {
            string root;var v=Create("no-provider",out root);v.Files.RemoveAll(f=>f.Path==Bin+"icuuc.dll");v.TotalBytes=v.Files.Sum(f=>f.Size);string before=Snapshot(root,true);
            Check(HistoricalIcuCompatibility.Derive(v,root)==null,"Absent manifest provider should not receive plan");Check(Snapshot(root,true)==before,"Absent-provider input modified");
        }
        private static int ReplaceAscii(byte[] bytes,string oldText,string newText)
        {
            if(oldText.Length!=newText.Length)throw new Exception("Fixture edit must retain length");byte[] a=Encoding.ASCII.GetBytes(oldText),b=Encoding.ASCII.GetBytes(newText);int count=0;
            for(int i=0;i<=bytes.Length-a.Length;i++) {bool same=true;for(int j=0;j<a.Length;j++)if(bytes[i+j]!=a[j]){same=false;break;}if(!same)continue;Buffer.BlockCopy(b,0,bytes,i,b.Length);count++;i+=a.Length-1;}return count;
        }
        private static void LaterProvider()
        {
            string root;var v=Create("later-provider",out root);byte[] provider=Read(root,Bin+"icuuc.dll");Check(ReplaceAscii(provider,"_52","_99")>100,"Fixture needs many ICU52 exports");Write(root,Bin+"icuuc.dll",provider);SetManifest(v,root,Bin+"icuuc.dll");string before=Snapshot(root,true);
            Check(HistoricalIcuCompatibility.Derive(v,root)==null,"Provider without ICU52 exports should remain untouched");Check(Snapshot(root,true)==before,"Later provider modified");
        }
        private static void LaterConsumers()
        {
            string root;var v=Create("later-consumers",out root);
            for(int i=0;i<2;i++) {byte[] bytes=Read(root,Bin+Names[i]);int offset=i==0?1700555:5756231;bytes[offset]=53;bytes[offset+1]=50;Write(root,Bin+Names[i],bytes);SetManifest(v,root,Bin+Names[i]);}
            string before=Snapshot(root,true);Check(HistoricalIcuCompatibility.Derive(v,root)==null,"Consumers already using a private name should not be repaired");Check(Snapshot(root,true)==before,"Later renamed consumers modified");
        }
        private static void TruncatedConsumer()
        {
            string root;var v=Create("truncated",out root);Write(root,Bin+"v8.dll",Read(root,Bin+"v8.dll").Take(96).ToArray());SetManifest(v,root,Bin+"v8.dll");string before=Snapshot(root,true);
            Reject(delegate {HistoricalIcuCompatibility.Derive(v,root);},"Truncated matched PE should be rejected safely");Check(Snapshot(root,true)==before,"Truncated PE modified");
        }
        private static void InvalidPePointer()
        {
            string root;var v=Create("invalid-pointer",out root);byte[] bytes=Read(root,Bin+"v8.dll");bytes[0x3c]=0xff;bytes[0x3d]=0xff;bytes[0x3e]=0xff;bytes[0x3f]=0x7f;Write(root,Bin+"v8.dll",bytes);SetManifest(v,root,Bin+"v8.dll");string before=Snapshot(root,true);
            Reject(delegate {HistoricalIcuCompatibility.Derive(v,root);},"Invalid PE header pointer should fail safely");Check(Snapshot(root,true)==before,"Malformed PE modified");
        }
        private static void MissingExport()
        {
            string root;var v=Create("missing-export",out root);byte[] bytes=Read(root,Bin+"icuuc.dll");Check(ReplaceAscii(bytes,"udata_setCommonData_52","xdata_setCommonData_52")==1,"Expected one required ICU export");Write(root,Bin+"icuuc.dll",bytes);SetManifest(v,root,Bin+"icuuc.dll");string before=Snapshot(root,true);
            Reject(delegate {HistoricalIcuCompatibility.Derive(v,root);},"Provider missing an imported symbol should fail");Check(Snapshot(root,true)==before,"Missing-export rejection modified files");
        }
        private static void AdditionalConsumer()
        {
            string root;var v=Create("additional-consumer",out root);byte[] bytes=Read(root,Bin+"v8.dll");string relative=Bin+"unknown-consumer.dll";Write(root,relative,bytes);v.Files.Add(new FileEntry{Path=relative,Size=bytes.Length,Sha1=Hash(bytes)});v.TotalBytes=v.Files.Sum(f=>f.Size);string before=Snapshot(root,true);
            Reject(delegate {HistoricalIcuCompatibility.Derive(v,root);},"Unknown ICU consumer must not leave an incomplete repair");Check(Snapshot(root,true)==before,"Unknown-consumer rejection modified files");
        }
    }
}
