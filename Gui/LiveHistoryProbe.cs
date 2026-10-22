using System;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace DotaArchive
{
    internal static class LiveHistoryProbe
    {
        public static int Main(string[] args)
        {
            var parser = new SteamDbHistoryParser();
            var serializer = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue, RecursionLimit = 200 };
            HistoryBaseline baseline = null;
            foreach (string file in args)
            {
                var page = serializer.Deserialize<HistoryPageSnapshot>(File.ReadAllText(file));
                if (page.Url.Contains("/depots/"))
                {
                    baseline = parser.ParseBaseline(page);
                    Console.WriteLine("BASELINE " + baseline.BuildId + " " + baseline.PublishedAtUtc.ToString("o") + " fields=" + baseline.State.Values.Count);
                    foreach (var depot in baseline.State.SelectWindowsClient("schinese")) Console.WriteLine(depot.DepotId + "=" + depot.ManifestId);
                }
                else if (page.Url.Contains("/history/"))
                {
                    HistoryListing history = parser.ParseHistory(page);
                    Console.WriteLine("HISTORY " + history.Changes.Count + " public=" + history.Builds.Count + " next=" + history.LoadMoreSelector);
                    foreach (var c in history.Changes.Where(c => !c.ParsedAllDepotChanges)) Console.WriteLine("UNPARSED " + c.ChangeId + " " + String.Join(", ", c.UnparsedChanges));
                    if (baseline != null)
                    {
                        int reversed = 0;
                        foreach (var c in history.Changes)
                        {
                            if (c.Publication != null)
                            {
                                if ((c.Publication.BuildId ?? baseline.BuildId) != baseline.BuildId || c.Publication.PublishedAtUtc != baseline.PublishedAtUtc)
                                    throw new Exception("Public branch discontinuity at " + c.ChangeId);
                                baseline.BuildId = c.Publication.PreviousBuildId ?? baseline.BuildId;
                                baseline.PublishedAtUtc = c.Publication.PreviousPublishedAtUtc.Value;
                            }
                            baseline.State.Reverse(c); reversed++;
                        }
                        Console.WriteLine("REVERSED " + reversed + " continuous records to " + baseline.BuildId + " " + baseline.PublishedAtUtc.ToString("o"));
                    }
                }
                else if (page.Url.Contains("/patchnotes/"))
                {
                    var listing = parser.ParsePatchListing(page);
                    Console.WriteLine("PATCHES " + listing.Builds.Count);
                }
            }
            return 0;
        }
    }
}
