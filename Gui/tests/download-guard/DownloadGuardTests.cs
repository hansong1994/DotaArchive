using System;
using System.ComponentModel;
using System.IO;
using System.Linq;

namespace DotaArchive
{
    internal static class DownloadGuardTests
    {
        private static int assertions;
        private static void Check(bool value, string name) { assertions++; if(!value) throw new Exception("FAIL " + name); }
        private static void Reject(Action action, string name) { bool rejected = false; try { action(); } catch(InvalidOperationException) { rejected = true; } Check(rejected, name); }
        private static Func<string> Read(string path) { return delegate { return path; }; }
        private static void Guard(string directory, params Func<string>[] readers) { PrototypeForm.RequireDownloadTargetNotRunning(directory, readers); }

        private static int Main()
        {
            // Paths need not exist. No game process is enumerated, launched,
            // signalled or terminated, and no target directory is created.
            string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "uncreated-clients");
            string target = Path.Combine(root, "historical-build907474");
            string targetExe = Path.Combine(target, "game", "bin", "win64", "dota2.exe");
            string outside = Path.Combine(root, "modern-steam-client", "game", "bin", "win64", "dota2.exe");
            Check(PrototypeForm.IsExecutableInDirectory(targetExe, target), "target executable is protected");
            Check(PrototypeForm.IsExecutableInDirectory(targetExe.ToUpperInvariant(), target.ToLowerInvariant()), "Windows paths compare case-insensitively");
            Check(PrototypeForm.IsExecutableInDirectory(targetExe, target + Path.DirectorySeparatorChar), "trailing target separator does not alter boundary");
            Check(PrototypeForm.IsExecutableInDirectory(Path.Combine(target, "game", "..", "game", "bin", "dota.exe"), target), "canonical dot segment remains within target");
            Check(!PrototypeForm.IsExecutableInDirectory(outside, target), "unrelated current client is allowed");
            Check(!PrototypeForm.IsExecutableInDirectory(Path.Combine(target + "-other", "dota2.exe"), target), "sibling with common prefix is outside target");
            Check(!PrototypeForm.IsExecutableInDirectory(Path.Combine(target, "..", "modern-steam-client", "dota2.exe"), target), "canonical parent traversal correctly resolves outside");
            Check(!PrototypeForm.IsExecutableInDirectory(Path.Combine(root, "dota2.exe"), target), "parent directory executable is outside target");
            Guard(target);
            Guard(target, Read(outside));
            Guard(target, Read(outside), Read(Path.Combine(target + "-other", "dota.exe")));
            Check(true, "all unrelated Dota processes permit download");
            Reject(() => Guard(target, Read(targetExe)), "running target blocks modification");
            Reject(() => Guard(target, Read(outside), Read(targetExe)), "later target process also blocks modification");
            Reject(() => Guard(target, Read(null)), "null process path is unknown not unrelated");
            Reject(() => Guard(target, Read(" ")), "blank process path is unknown");
            Reject(() => Guard(target, Read("game\\bin\\win64\\dota2.exe")), "relative process path is unknown");
            Reject(() => Guard(target, Read(@"C:dota2.exe")), "drive-relative process path is unknown");
            Reject(() => Guard(target, Read(@"\\?\C:\game\dota2.exe")), "device-namespace path cannot bypass chain validation");
            int attempts = 0;
            Func<string> denied = delegate { attempts++; throw new Win32Exception(5, "Synthetic process module access denied"); };
            Reject(() => Guard(target, Read(outside), denied), "unreadable process module conservatively blocks download");
            Check(attempts == 1, "unknown module is read once without retry or termination");
            Reject(() => Guard(target, delegate { throw new InvalidOperationException("Synthetic process exited during module lookup"); }), "process lookup race is not silently treated as unrelated");
            Check(!Directory.Exists(root), "guard does not create target or helper directories");
            Console.WriteLine("PASS " + assertions + " download target guard assertions; no process, network or UI operations.");
            return 0;
        }
    }
}
