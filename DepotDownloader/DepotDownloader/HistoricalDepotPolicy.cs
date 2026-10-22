// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using System;
using System.Collections.Generic;

// Added for Dota Archive on 2026-10-22; distributed under the same GPLv2 license.
namespace DepotDownloader
{
    // Local compatibility policy only. Steam licenses, depot decryption keys,
    // manifest request codes and CDN authorization remain authoritative.
    internal static class HistoricalDepotPolicy
    {
        internal static void Validate(uint appId, List<(uint depotId, ulong manifestId)> depots,
            string branch, bool isUgc, string branchPassword)
        {
            if (appId != 570 || !string.Equals(branch, "public", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("-history only supports app 570 and the public branch.");
            if (isUgc || !string.IsNullOrEmpty(branchPassword))
                throw new ArgumentException("-history cannot be combined with workshop content or a branch password.");
            if (depots == null || depots.Count == 0)
                throw new ArgumentException("-history requires explicit depot and manifest pairs from a verified historical plan.");
            var seen = new HashSet<uint>();
            foreach (var (depotId, manifestId) in depots)
            {
                if (depotId == 0 || depotId == uint.MaxValue || manifestId == 0 || manifestId == ulong.MaxValue)
                    throw new ArgumentException("-history requires a valid nonzero depot and exact manifest ID for every depot.");
                if (!seen.Add(depotId))
                    throw new ArgumentException("-history does not accept duplicate depot IDs.");
            }
        }

        internal static void ValidateProxySource(uint appId, uint proxyAppId)
        {
            if (proxyAppId != uint.MaxValue && proxyAppId != appId)
                throw new ArgumentException("-history will not infer a historical shared-depot source from another app's current metadata.");
        }
    }
}
