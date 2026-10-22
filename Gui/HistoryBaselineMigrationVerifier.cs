using System;
using System.Web.Script.Serialization;

namespace DotaArchive
{
    internal static class HistoryBaselineMigrationVerifier
    {
        public static int Main(string[] args)
        {
            try
            {
                if (args.Length < 3 || args.Length > 4) throw new ArgumentException("Usage: HistoryBaselineMigrationVerifier <baseline-capture.json> <source-cache-directory> <new-output-directory> [overlapping-history-capture.json]");
                var report = HistoryBaselineMigration.Prepare(args[0], args[1], args[2], args.Length == 4 ? args[3] : null);
                Console.WriteLine(new JavaScriptSerializer().Serialize(report));
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
        }
    }
}
