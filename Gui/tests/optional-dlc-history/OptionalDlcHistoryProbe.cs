using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Web.Script.Serialization;
namespace DotaArchive
{
    internal static class OptionalDlcHistoryProbe
    {
        public static int Main(string[] args)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue, RecursionLimit = 200 };
            HistoryCacheStore.CacheData data = HistoryCacheStore.Parse(File.ReadAllText(args[0]));
            foreach (HistoryChange change in data.Changes) data.Baseline.State.Reverse(change);
            var snapshot = serializer.Deserialize<HistoryPageSnapshot>(File.ReadAllText(args[1]));
            var parsed = new SteamDbHistoryParser().ParseHistory(snapshot);
            var seen = new HashSet<string>(data.Changes.Select(c => c.ChangeId));
            Console.WriteLine("Cached " + seen.Count + ", boundary " + data.Changes.Last().ChangeId);
            foreach (HistoryChange change in parsed.Changes)
            {
                if (seen.Contains(change.ChangeId)) continue;
                try { data.Baseline.State.Reverse(change); }
                catch (Exception ex)
                {
                    Console.WriteLine(serializer.Serialize(new { FailingChange = change.ChangeId, Observed = change.ObservedAtUtc.ToUniversalTime().ToString("o"), Error = ex.Message, State = data.Baseline.State.Values.Where(p => p.Key.StartsWith("depots/381450/")).ToArray(), KnownOptional = data.Baseline.State.KnownOptionalDlcPaths.Contains("depots/381450/config/optionaldlc"), Deltas = change.Deltas.Where(d => d.Path.StartsWith("depots/381450/")).ToArray() }));
                    break;
                }
            }
            foreach (HistoryChange change in parsed.Changes.Where(c => c.Deltas.Any(d => d.Path == "depots/381450/config/optionaldlc")))
                Console.WriteLine(serializer.Serialize(new { ChangeId = change.ChangeId, Observed = change.ObservedAtUtc.ToUniversalTime().ToString("o"), Deltas = change.Deltas.Where(d => d.Path.StartsWith("depots/381450/")).ToArray() }));
            return 0;
        }
    }
}
