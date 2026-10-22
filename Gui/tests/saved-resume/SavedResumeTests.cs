using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace DotaArchive
{
    internal static class SavedResumeTests
    {
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 2 * 1024 * 1024 };
        static string root, fixture, markerFixture, run;
        static int count, sequence;
        static readonly List<string> passed = new List<string>();
        static int Main(string[] args)
        {
            try {
                root = Path.GetFullPath(args[0]); fixture = File.ReadAllText(Path.Combine(root, "evidence/resolved-plan.json"));
                markerFixture = File.ReadAllText(Path.Combine(root, "evidence/directory-marker.json"));
                run = Path.GetFullPath(Path.Combine(root, "../../../resume-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8))); Directory.CreateDirectory(run);
                string actual = Make(null, false); File.WriteAllText(Path.Combine(actual, ".dota-archive-browser/resolved-plan.json"), fixture);
                File.WriteAllText(Path.Combine(actual, ".dota-archive-browser.json"), markerFixture);
                var loaded = SavedDownloadPlan.Load(actual);
                Check(loaded.Complete && loaded.BuildId == "907474" && loaded.Depots.Count == 10, "actual saved full plan loads");
                Check(loaded.PublishedAtUtc == new DateTimeOffset(2015, 12, 20, 18, 43, 55, TimeSpan.Zero), "legacy JS date preserves exact UTC instant");
                Check(PrototypeForm.Fingerprint(loaded) == "4b8863724c9e4e162db5aee2ba4124fd927370a16321e2b9ccaf69498e670e8f", "actual marker fingerprint roundtrip");
                Check(loaded.UsedObservationFallback && loaded.TimeSource == "SteamDB public branch change observed time", "old observed timestamp provenance retained");
                Check(((Dictionary<string, object>)Json.DeserializeObject(fixture))["PublishedAtUtc"] is DateTime, "DeserializeObject recognizes legacy JS date");
                var roundtrip = SavedDownloadPlan.Load(Make(null, false));
                Check(PrototypeForm.Fingerprint(roundtrip) == PrototypeForm.Fingerprint(loaded), "reserialized dictionary legacy date roundtrip");
                string huge = Make(raw => Row(raw)["ManifestId"] = "18446744073709551615", false);
                Check(SavedDownloadPlan.Load(huge).Depots[0].ManifestId == "18446744073709551615", "maximum ulong manifest remains exact string");
                string iso = Make(raw => raw["PublishedAtUtc"] = "2015-12-20T18:43:55.0000000+00:00", false);
                Check(SavedDownloadPlan.Load(iso).PublishedAtUtc == loaded.PublishedAtUtc, "ISO UTC date supported");
                string isoOffset = Make(raw => raw["PublishedAtUtc"] = "2015-12-21T02:43:55.0000000+08:00", false);
                Check(SavedDownloadPlan.Load(isoOffset).PublishedAtUtc == loaded.PublishedAtUtc, "ISO offset normalizes without shifting instant");
                Reject("incomplete plan", raw => raw["Complete"] = false);
                Reject("complete string cannot coerce", raw => raw["Complete"] = "true");
                Reject("numeric build ID", raw => raw["BuildId"] = 907474);
                Reject("numeric depot ID", raw => Row(raw)["DepotId"] = 373301);
                Reject("numeric manifest ID", raw => Row(raw)["ManifestId"] = 2427714153971932438L);
                Reject("manifest overflow", raw => Row(raw)["ManifestId"] = "18446744073709551616");
                Reject("manifest zero", raw => Row(raw)["ManifestId"] = "0");
                Reject("depot overflow", raw => Row(raw)["DepotId"] = "4294967296");
                Reject("duplicate depots", raw => { var a=(object[])raw["Depots"]; ((Dictionary<string,object>)a[1])["DepotId"]=Row(raw)["DepotId"]; });
                Reject("empty depots", raw => raw["Depots"] = new object[0]);
                Reject("invalid source URL", raw => raw["SourceUrl"] = "https://example.com/app/570/history/?changeid=1624513");
                Reject("source time mismatch", raw => raw["UsedObservationFallback"] = false);
                Reject("unrecognized time provenance", raw => raw["TimeSource"] = "unknown");
                Reject("null publication", raw => raw["PublishedAtUtc"] = null);
                Reject("numeric publication", raw => raw["PublishedAtUtc"] = 1450637035L);
                Reject("oversized plan", null, null, false, true);
                Reject("marker wrong app", null, m => m["AppId"] = "730");
                Reject("marker wrong build", null, m => m["BuildId"] = "909005");
                Reject("marker unsupported schema", null, m => m["SchemaVersion"] = 2);
                Reject("marker fingerprint mismatch", null, m => m["PlanFingerprint"] = new string('0',64));
                Reject("folder mismatch", null, null, true);
                Check(!Directory.EnumerateFiles(run, "*", SearchOption.AllDirectories).Any(p => p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)), "tests wrote only metadata to isolated cases");
                File.WriteAllText(Path.Combine(root, "results.json"), Json.Serialize(new { Passed=count, Cases=passed, FixtureBuild=loaded.BuildId, FixtureFingerprint=PrototypeForm.Fingerprint(loaded), ReadOnlyActualClient=true, NetworkUsed=false, UserInterfaceUsed=false, RunDirectory=run }), new UTF8Encoding(false));
                Console.WriteLine("Saved resume: " + count + " tests passed."); return 0;
            } catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
        static Dictionary<string,object> Row(Dictionary<string,object> raw) { return (Dictionary<string,object>)((object[])raw["Depots"])[0]; }
        static string Make(Action<Dictionary<string,object>> mutate, bool wrongFolder)
        {
            var raw=(Dictionary<string,object>)Json.DeserializeObject(fixture); if(mutate!=null) mutate(raw);
            string dir=Path.Combine(run, (++sequence).ToString("D3"), wrongFolder ? "wrong-folder" : "build907474-2015-12-21-build907474");
            Directory.CreateDirectory(Path.Combine(dir,".dota-archive-browser"));
            File.WriteAllText(Path.Combine(dir,".dota-archive-browser/resolved-plan.json"), Json.Serialize(raw), new UTF8Encoding(false));
            var marker=(Dictionary<string,object>)Json.DeserializeObject(markerFixture);
            try {
                object date=raw["PublishedAtUtc"];
                DateTimeOffset when=date is DateTime ? new DateTimeOffset(((DateTime)date).ToUniversalTime()) : DateTimeOffset.Parse((string)date, CultureInfo.InvariantCulture);
                var p=new ResolvedHistoryPlan { BuildId=Convert.ToString(raw["BuildId"],CultureInfo.InvariantCulture), PublishedAtUtc=when };
                foreach(var o in (object[])raw["Depots"]) { var d=(Dictionary<string,object>)o; p.Depots.Add(new DepotEntry { DepotId=Convert.ToString(d["DepotId"],CultureInfo.InvariantCulture), ManifestId=Convert.ToString(d["ManifestId"],CultureInfo.InvariantCulture) }); }
                marker["PlanFingerprint"]=PrototypeForm.Fingerprint(p);
            } catch(ArgumentException) { } catch(FormatException) { } catch(InvalidCastException) { }
            File.WriteAllText(Path.Combine(dir,".dota-archive-browser.json"), Json.Serialize(marker),new UTF8Encoding(false)); return dir;
        }
        static void Reject(string name, Action<Dictionary<string,object>> mutate, Action<Dictionary<string,object>> markerMutate=null, bool wrongFolder=false, bool oversize=false)
        {
            string dir=Make(mutate,wrongFolder);
            if(markerMutate!=null) { string path=Path.Combine(dir,".dota-archive-browser.json"); var mark=(Dictionary<string,object>)Json.DeserializeObject(File.ReadAllText(path)); markerMutate(mark); File.WriteAllText(path,Json.Serialize(mark)); }
            if(oversize) File.WriteAllText(Path.Combine(dir,".dota-archive-browser/resolved-plan.json"),new string(' ',1024*1024+1));
            try { SavedDownloadPlan.Load(dir); } catch(InvalidDataException) { Check(true,name); return; }
            throw new Exception("Expected rejection: "+name);
        }
        static void Check(bool okay,string name) { if(!okay) throw new Exception("Failed: "+name); count++; passed.Add(name); }
    }
}
