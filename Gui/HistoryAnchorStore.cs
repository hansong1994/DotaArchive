using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace DotaArchive
{
    // An automatically collected proof chain, not a user-supplied download plan.
    // Hashes detect accidental corruption; they are not SteamDB signatures.
    // Every decoded chain must still pass the resolver's normal full replay.
    internal sealed class HistoryAnchorStore
    {
        internal const int MaximumBytes = 128 * 1024 * 1024;
        internal const int MaximumAnchors = 24;
        private readonly string file;
        internal HistoryAnchorStore(string directory) { file = Path.Combine(Path.GetFullPath(directory), "verified-public-anchors-v2.json"); }
        internal sealed class Anchor
        {
            internal string Identity;
            internal string Provenance;
            internal HistoryBaseline Baseline;
            internal List<HistoryChange> Changes;
        }
        public sealed class Envelope
        {
            public int SchemaVersion { get; set; }
            public string AppId { get; set; }
            public string Branch { get; set; }
            public List<Entry> Anchors { get; set; }
        }
        public sealed class Entry
        {
            public string Identity { get; set; }
            public string BaselineSourceUrl { get; set; }
            public string Provenance { get; set; }
            public string FirstChangeId { get; set; }
            public string LastChangeId { get; set; }
            public string PayloadSha256 { get; set; }
            public string Payload { get; set; }
        }
        internal List<Anchor> Load()
        {
            if (!File.Exists(file)) return new List<Anchor>();
            long length = new FileInfo(file).Length;
            if (length <= 0 || length > MaximumBytes) throw new InvalidDataException("历史锚点文件大小不符合限制。");
            Envelope envelope = Serializer().Deserialize<Envelope>(File.ReadAllText(file, Encoding.UTF8));
            if (envelope == null || envelope.SchemaVersion != 2 || envelope.AppId != "570" || envelope.Branch != "public" || envelope.Anchors == null || envelope.Anchors.Count > MaximumAnchors)
                throw new InvalidDataException("历史锚点格式、App 或分支不匹配。");
            var result = new List<Anchor>();
            var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (Entry entry in envelope.Anchors)
            {
                if (entry == null || entry.BaselineSourceUrl != "https://steamdb.info/app/570/depots/?branch=public" ||
                    (entry.Provenance != "BrowserBaselineAndContinuousHistory" && entry.Provenance != "ValidatedLegacyV1Migration") ||
                    String.IsNullOrEmpty(entry.Payload) || !String.Equals(Hash(entry.Payload), entry.PayloadSha256, StringComparison.Ordinal))
                    throw new InvalidDataException("历史锚点来源或内容校验失败。");
                HistoryCacheStore.CacheData data = HistoryCacheStore.Parse(entry.Payload);
                string identity = Identity(data.Baseline);
                if (entry.Identity != identity || !identities.Add(identity) || data.Changes.Count == 0 ||
                    entry.FirstChangeId != data.Changes[0].ChangeId || entry.LastChangeId != data.Changes[data.Changes.Count - 1].ChangeId)
                    throw new InvalidDataException("历史锚点身份或边界不一致。");
                result.Add(new Anchor { Identity = identity, Provenance = entry.Provenance, Baseline = data.Baseline, Changes = data.Changes });
            }
            return result;
        }
        internal void Save(IList<Anchor> anchors)
        {
            if (anchors.Count > MaximumAnchors) throw new InvalidDataException("历史锚点数超过保存上限；现有锚点不会被静默删除。");
            var envelope = new Envelope { SchemaVersion = 2, AppId = "570", Branch = "public", Anchors = new List<Entry>() };
            foreach (Anchor anchor in anchors)
            {
                if (anchor.Changes.Count == 0) continue;
                string payload = HistoryCacheStore.Serialize(anchor.Baseline, anchor.Changes);
                envelope.Anchors.Add(new Entry { Identity = Identity(anchor.Baseline), BaselineSourceUrl = anchor.Baseline.SourceUrl,
                    Provenance = anchor.Provenance, FirstChangeId = anchor.Changes[0].ChangeId,
                    LastChangeId = anchor.Changes[anchor.Changes.Count - 1].ChangeId, Payload = payload, PayloadSha256 = Hash(payload) });
            }
            string json = Serializer().Serialize(envelope);
            if (Encoding.UTF8.GetByteCount(json) > MaximumBytes) throw new InvalidDataException("历史锚点超过保存大小上限；现有文件保持不变。");
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            string temporary = Path.Combine(Path.GetDirectoryName(file), "anchor-" + Guid.NewGuid().ToString("N").Substring(0, 12) + ".tmp");
            try
            {
                File.WriteAllText(temporary, json, new UTF8Encoding(false));
                if (File.Exists(file)) File.Replace(temporary, file, null); else File.Move(temporary, file);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        internal static string Identity(HistoryBaseline baseline)
        {
            var canonical = new StringBuilder();
            Append(canonical, "570/public"); Append(canonical, baseline.BuildId); Append(canonical, baseline.PublishedAtUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
            // Preserve the identity of old captured baseline evidence while
            // separating its display labels from canonical download state.
            var evidence = new Dictionary<string, string>(baseline.State.Values, StringComparer.OrdinalIgnoreCase);
            foreach (var pair in baseline.State.DisplayNames) if (!evidence.ContainsKey(pair.Key)) evidence.Add(pair.Key, pair.Value);
            foreach (var pair in evidence.OrderBy(p => p.Key, StringComparer.Ordinal)) { Append(canonical, pair.Key); Append(canonical, pair.Value); }
            // Preserve old modern-baseline identities (false by default), but a
            // known legacy flag that was deleted must not collide with a baseline
            // that never observed this scope boundary.
            if (baseline.State.HasKnownLegacyDlcMarking) Append(canonical, "known-legacy-dlc-marking-v1");
            return Hash(canonical.ToString());
        }
        private static void Append(StringBuilder text, string value) { text.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value); }
        private static string Hash(string value) { using (var sha = SHA256.Create()) return String.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(value)).Select(b => b.ToString("x2", CultureInfo.InvariantCulture))); }
        private static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength = MaximumBytes, RecursionLimit = 40 }; }
    }
}
