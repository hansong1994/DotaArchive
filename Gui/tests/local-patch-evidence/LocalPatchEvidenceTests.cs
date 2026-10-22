using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace DotaArchive
{
    internal static class LocalPatchEvidenceTests
    {
        private static int checks;
        private static void Check(bool value, string message) { if(!value) throw new Exception("FAILED: " + message); checks++; }
        private static void Reject(Action action, string message) { try { action(); } catch(HistoryResolutionException) { checks++; return; } throw new Exception("Expected rejection: " + message); }
        public static int Main(string[] args)
        {
            try {
                string bundled = args[0], archive = args[1], scratch = args[2];
                Directory.CreateDirectory(scratch);
                var store = new LocalPatchEvidenceStore(bundled, Path.Combine(scratch, "writable"));
                OfficialPatchMatch match = store.ResolveAsync("7.00", CancellationToken.None, null).GetAwaiter().GetResult();
                Check(match != null && match.Kind == "OfficialReleaseWindow", "real 7.00 raw evidence replays without a browser or network provider");
                Check(match.Build.BuildId == "1514733", "original 7.00 build follows evidence rather than a saved patch mapping");
                Check(match.WindowEvidence.Count == 2 && match.NotAvailableAtUtc.HasValue && match.AvailableByUtc.HasValue, "both affirmative official before and after statements retained");
                Console.WriteLine("Replayed 7.00 proof sources: " + String.Join(" | ", match.Sources));
                var plan = new LocalArchiveResolver(archive).ResolveAsync(new HistoryQuery { Patch = "7.00" }, match, CancellationToken.None, null).GetAwaiter().GetResult();
                Check(plan != null && plan.Complete && plan.BuildId == "1514733", "real full 4412-change chain validates 7.00 release window and complete manifests");
                Check(plan.PatchWindowPublicEvents == 1 && plan.PatchMatchKind == "OfficialReleaseWindow", "full chain proves exactly one public release in official window");
                Check(plan.Depots.Count > 1 && plan.Depots.All(d => !String.IsNullOrEmpty(d.ManifestId)), "complete multi-depot plan produced");
                Check(store.ResolveAsync("6.86", CancellationToken.None, null).GetAwaiter().GetResult() == null, "unsupported proof is a miss; date alone cannot select 6.86");
                Reject(delegate { store.ResolveAsync("7.0", CancellationToken.None, null).GetAwaiter().GetResult(); }, "imprecise patch names rejected");
                Check(new LocalPatchEvidenceStore(Path.Combine(scratch, "absent")).ResolveAsync("7.00", CancellationToken.None, null).GetAwaiter().GetResult() == null, "absent optional bundle is a local miss");
                var fake = new FakeBrowser();
                var recorder = store.CreateRecordingBrowser(fake);
                var page = new HistoryPageSnapshot { Url = "https://steamdb.info/app/570/history/?changeid=1", Html = "history", CapturedAtUtc = DateTimeOffset.UtcNow };
                store.SaveBrowserSnapshot(page);
                Check(!Directory.Exists(Path.Combine(scratch, "writable")), "history and account DOM excluded from patch evidence store");
                Check(Object.ReferenceEquals(recorder, store.CreateRecordingBrowser(recorder)), "recording wrapper not nested");
                ((IHistoryDateBrowser)recorder).LoadHistoryBeforeAsync(new DateTime(2015,12,22), CancellationToken.None).GetAwaiter().GetResult();
                ((IHistoryDateNavigationBrowser)recorder).OpenHistoryForDateAsync(CancellationToken.None).GetAwaiter().GetResult();
                Check(fake.DateCalls == 1 && fake.OpenCalls == 1, "existing date navigation capabilities preserved");
                Reject(delegate { store.SaveOfficialResponse("https://api.steampowered.com/ISteamNews/GetNewsForApp/v2/?appid=730", "{}", DateTimeOffset.UtcNow); }, "other app response source rejected");
                var json = new JavaScriptSerializer { MaxJsonLength = 64*1024*1024 };
                foreach (string path in Directory.GetFiles(bundled, "resource-*.json")) {
                    var resource = json.Deserialize<LocalPatchEvidenceStore.Resource>(File.ReadAllText(path));
                    if(resource.Kind == "OfficialResponse") store.SaveOfficialResponse(resource.Url, resource.Payload, DateTimeOffset.Parse(resource.CapturedAtUtc));
                    else {
                        var raw = json.Deserialize<LocalPatchEvidenceStore.SnapshotPayload>(resource.Payload);
                        fake.Next = new HistoryPageSnapshot { Url = raw.Url, Title = raw.Title, Html = raw.Html, Text = raw.Text, CapturedAtUtc = DateTimeOffset.Parse(raw.CapturedAtUtc) };
                        recorder.NavigateAsync(raw.Url, CancellationToken.None).GetAwaiter().GetResult();
                    }
                }
                var learned = new LocalPatchEvidenceStore(null, Path.Combine(scratch, "writable")).ResolveAsync("7.00", CancellationToken.None, null).GetAwaiter().GetResult();
                Check(learned != null && learned.Build.BuildId == match.Build.BuildId && fake.NavigateCalls == 1, "newly captured raw sources replay after restart without bundled mapping");
                string corrupt = Path.Combine(scratch, "corrupt"); Directory.CreateDirectory(corrupt);
                foreach (string path in Directory.GetFiles(bundled, "resource-*.json")) File.Copy(path, Path.Combine(corrupt, Path.GetFileName(path)), true);
                string target = Directory.GetFiles(corrupt, "resource-*.json")[0];
                var data = json.Deserialize<Dictionary<string,object>>(File.ReadAllText(target)); data["PayloadSha256"] = new string('0',64); File.WriteAllText(target, json.Serialize(data));
                Reject(delegate { new LocalPatchEvidenceStore(corrupt).ResolveAsync("7.00", CancellationToken.None, null).GetAwaiter().GetResult(); }, "damaged raw evidence fails closed before replay fallback");
                Console.WriteLine("Local patch evidence tests passed: " + checks);
                Console.WriteLine("7.00 -> Build " + plan.BuildId + ", " + plan.Depots.Count + " depots, " + plan.PublishedAtUtc.ToString("o") + ", " + match.Kind);
                return 0;
            } catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
        private sealed class FakeBrowser : IHistoryBrowser, IHistoryDateBrowser, IHistoryDateNavigationBrowser
        {
            public int DateCalls, OpenCalls, NavigateCalls;
            public HistoryPageSnapshot Next;
            public Task<HistoryPageSnapshot> NavigateAsync(string url, CancellationToken token) { if(Next == null || Next.Url != url) throw new Exception("Unexpected browser access"); NavigateCalls++; return Task.FromResult(Next); }
            public Task<HistoryPageSnapshot> CaptureAsync(CancellationToken token) { throw new Exception("Unexpected browser access"); }
            public Task<HistoryPageSnapshot> ClickAsync(string selector, CancellationToken token) { throw new Exception("Unexpected browser access"); }
            public Task<HistoryPageSnapshot> LoadHistoryBeforeAsync(DateTime date, CancellationToken token) { DateCalls++; return Task.FromResult(new HistoryPageSnapshot()); }
            public Task OpenHistoryForDateAsync(CancellationToken token) { OpenCalls++; return Task.FromResult(0); }
        }
    }
}
