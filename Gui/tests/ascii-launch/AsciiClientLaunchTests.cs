using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Generic;
using System.Web.Script.Serialization;

namespace DotaArchive
{
    internal static class AsciiClientLaunchTests
    {
        private static readonly List<object> Results = new List<object>();
        private static string root;
        private static void Check(string name, bool condition) { Results.Add(new { Name = name, Passed = condition }); if(!condition) throw new Exception(name); }
        private static void Reject(string name, Action action) { bool rejected = false; try { action(); } catch(InvalidDataException) { rejected = true; } catch(ArgumentException) { rejected = true; } Check(name, rejected); }
        private static string MakeClient(string name) { string path = Path.Combine(root, name); Directory.CreateDirectory(Path.Combine(path, "game/bin/win64")); File.WriteAllText(Path.Combine(path, "game/bin/win64/dota2.exe"), "Offline inert test fixture. Never execute."); return path; }
        private static string Hash(string path) { using(var sha = SHA256.Create()) using(var input = File.OpenRead(path)) return Convert.ToBase64String(sha.ComputeHash(input)); }
        private static string Resolve(string client, string identity, string data) { return AsciiClientLaunch.GetExecutablePathCore(client, "game/bin/win64/dota2.exe", identity, data); }
        private static string AliasRoot(string executable) { return Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(executable)))); }

        public static int Main(string[] args)
        {
            string output = Path.GetFullPath(args[0]);
            root = Path.Combine(Path.GetTempPath(), "dota-ascii-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            string error = null;
            try {
                string ascii = MakeClient("ascii-client");
                string untouched = Path.Combine(root, "unused-local-data");
                Check("ASCII client returns exact original executable", Resolve(ascii, "ascii", untouched) == Path.GetFullPath(Path.Combine(ascii, "game/bin/win64/dota2.exe")));
                Check("ASCII client creates no alias directory", !Directory.Exists(untouched));
                string client = MakeClient("中文客户端");
                string source = Path.Combine(client, "game/bin/win64/dota2.exe");
                string before = Hash(source); DateTime beforeWrite = File.GetLastWriteTimeUtc(source);
                string data = Path.Combine(root, "local-data");
                string launch = Resolve(client, "build-test-1", data);
                Check("Unicode client returns entirely ASCII path", launch.All(c => c < 128));
                Check("Alias executable reads identical bytes", Hash(launch) == before);
                Check("Original executable bytes unchanged", Hash(source) == before);
                Check("Original executable write time unchanged", File.GetLastWriteTimeUtc(source) == beforeWrite);
                string alias = AliasRoot(launch);
                Check("Actual NTFS reparse directory created", (File.GetAttributes(alias) & FileAttributes.ReparsePoint) != 0);
                Check("Repeat invocation safely reuses same alias", Resolve(client, "build-test-1", data) == launch);
                Reject("Parent traversal rejected", delegate { AsciiClientLaunch.GetExecutablePathCore(client, "../outside.exe", "test", data); });
                Reject("Absolute relative path rejected", delegate { AsciiClientLaunch.GetExecutablePathCore(client, source, "test", data); });
                Reject("Alternate data stream rejected", delegate { AsciiClientLaunch.GetExecutablePathCore(client, "game/bin/win64/dota2.exe:bad", "test", data); });
                Reject("Identity newlines rejected", delegate { Resolve(client, "bad\nidentity", data); });

                string fallback = Resolve(client, "fallback-test", Path.Combine(root, "中文用户"));
                Check("Non-ASCII LocalAppData uses nearest ASCII ancestor", fallback.StartsWith(Path.Combine(root, ".dota-archive-launch") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
                Check("Fallback alias also resolves unchanged file", Hash(fallback) == before);

                string wrongTarget = MakeClient("wrong-target");
                string wrongLaunch = Resolve(client, "wrong-target-test", data);
                string wrongAlias = AliasRoot(wrongLaunch);
                Directory.Move(wrongAlias, wrongAlias + "-saved"); Directory.CreateDirectory(wrongAlias);
                AsciiClientLaunch.SetJunction(wrongAlias, wrongTarget);
                Reject("Preexisting junction with wrong target rejected", delegate { Resolve(client, "wrong-target-test", data); });
                Check("Wrong target executable unchanged", File.ReadAllText(Path.Combine(wrongTarget, "game/bin/win64/dota2.exe")).StartsWith("Offline inert"));

                string ordinaryLaunch = Resolve(client, "ordinary-directory-test", data);
                string ordinaryAlias = AliasRoot(ordinaryLaunch);
                Directory.Move(ordinaryAlias, ordinaryAlias + "-saved"); Directory.CreateDirectory(ordinaryAlias);
                File.WriteAllText(Path.Combine(ordinaryAlias, "preserve.txt"), "preserve");
                Reject("Existing ordinary directory never overwritten", delegate { Resolve(client, "ordinary-directory-test", data); });
                Check("Existing unrelated file preserved", File.ReadAllText(Path.Combine(ordinaryAlias, "preserve.txt")) == "preserve");

                string unownedData = Path.Combine(root, "unowned-data");
                string unownedRoot = Path.Combine(unownedData, "DotaArchive/ClientLaunch"); Directory.CreateDirectory(unownedRoot);
                File.WriteAllText(Path.Combine(unownedRoot, "existing.txt"), "existing");
                Reject("Nonempty unowned alias root rejected", delegate { Resolve(client, "unowned", unownedData); });

                string rootLink = Path.Combine(root, "data-link"); Directory.CreateDirectory(rootLink); AsciiClientLaunch.SetJunction(rootLink, data);
                Reject("LocalAppData ancestor reparse point rejected", delegate { Resolve(client, "parent-link", rootLink); });
                string clientLink = Path.Combine(root, "client-link"); Directory.CreateDirectory(clientLink); AsciiClientLaunch.SetJunction(clientLink, client);
                Reject("Original client reparse point rejected", delegate { Resolve(clientLink, "source-link", data); });
                string inside = Path.Combine(client, "nested"); Directory.CreateDirectory(inside); AsciiClientLaunch.SetJunction(inside, wrongTarget);
                Reject("Executable nested reparse point rejected", delegate { AsciiClientLaunch.GetExecutablePathCore(client, "nested/game/bin/win64/dota2.exe", "nested", data); });
                string unicodeExe = Path.Combine(client, "游戏.exe"); File.WriteAllText(unicodeExe, "inert");
                Reject("Non-ASCII internal executable path fails explicitly", delegate { AsciiClientLaunch.GetExecutablePathCore(client, "游戏.exe", "unicode-exe", data); });
            } catch(Exception ex) { error = ex.ToString(); }
            File.WriteAllText(Path.Combine(output, "results.json"), new JavaScriptSerializer().Serialize(new { Success = error == null, Checks = Results, Error = error, FixtureRoot = root, Scope = "Offline inert files only; real NTFS junctions, no process launch, no network, no client writes." }), new UTF8Encoding(false));
            Console.WriteLine((error == null ? "PASS " : "FAIL ") + Results.Count + " checks"); if(error != null) Console.WriteLine(error);
            return error == null ? 0 : 1;
        }
    }
}
