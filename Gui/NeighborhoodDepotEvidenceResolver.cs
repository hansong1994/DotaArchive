using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DotaArchive
{
    // This is evidence for individual fields, deliberately not a download plan.
    public sealed class NeighborhoodDepotEvidenceResult
    {
        public bool Complete { get { return false; } }
        public bool CanDownload { get { return false; } }
        public bool HistoricalMembershipComplete { get { return false; } }
        public string Status { get { return "PartialNeighborhoodEvidence"; } }
        public bool ContiguousNeighborhoodAttested { get; internal set; }
        public DateTimeOffset? RequestedUtc { get; internal set; }
        public string CandidateChangeId { get; internal set; }
        public string CandidateBuildId { get; internal set; }
        public string CandidateSourceUrl { get; internal set; }
        public DateTimeOffset CandidateEffectiveTimeUtc { get; internal set; }
        public string CandidateTimeSource { get; internal set; }
        public DateTimeOffset ObservedFromUtc { get; internal set; }
        public DateTimeOffset ObservedThroughUtc { get; internal set; }
        public List<NeighborhoodDepotFieldEvidence> KnownFields { get; internal set; }
        public List<NeighborhoodDepotEvidence> ObservedDepots { get; internal set; }
        public string Limitation { get; internal set; }
    }

    public sealed class NeighborhoodDepotEvidence
    {
        public string DepotId { get; internal set; }
        // Observed is not a claim that this Depot belongs to the selected build.
        public List<string> UnknownFields { get; internal set; }
    }

    public sealed class NeighborhoodDepotFieldEvidence
    {
        public string DepotId { get; internal set; }
        public string Field { get; internal set; }
        public string Path { get; internal set; }
        public bool Exists { get; internal set; }
        public string Value { get; internal set; }
        public string Method { get; internal set; }
        public List<NeighborhoodFieldWitness> Evidence { get; internal set; }
    }

    public sealed class NeighborhoodFieldWitness
    {
        public string ChangeId { get; internal set; }
        public string SourceUrl { get; internal set; }
        public DateTimeOffset ObservedAtUtc { get; internal set; }
        public string Side { get; internal set; }
        public bool Exists { get; internal set; }
        public string Value { get; internal set; }
    }

    public sealed class NeighborhoodDepotEvidenceResolver
    {
        private static readonly string[] Fields = {
            "manifests/public/gid", "name", "dlcappid", "depotfromapp",
            "config/oslist", "config/osarch", "config/language",
            "config/lowviolence", "config/optionaldlc"
        };

        // The caller must attest that these are a continuous rendered history
        // neighborhood, with overlapping page boundaries checked by the locator.
        // Numeric ChangeIds alone cannot prove coverage: they are global to Steam.
        // The cut is immediately AFTER CandidateChange, as in the full reducer,
        // not the wall-clock input after subsequent metadata-only changes.
        public NeighborhoodDepotEvidenceResult Resolve(DateFirstLocation location, bool contiguousNeighborhoodVerified)
        {
            if (!contiguousNeighborhoodVerified) Fail("必须先核实日期邻域及分页边界连续；不能把任意历史记录集合当成连续证据。");
            if (location == null || location.CandidateChange == null || location.NeighborChanges == null || location.NeighborChanges.Count == 0)
                Fail("邻域证据缺少候选事件或历史记录。");
            var changes = location.NeighborChanges.OrderBy(c => c == null ? DateTimeOffset.MinValue : c.ObservedAtUtc)
                .ThenBy(c => c == null || !HistoryDepotState.IsNumeric(c.ChangeId) ? 0UL : UInt64.Parse(c.ChangeId, CultureInfo.InvariantCulture)).ToList();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (HistoryChange change in changes)
            {
                if (change == null || !HistoryDepotState.IsNumeric(change.ChangeId) || !ids.Add(change.ChangeId) || change.ObservedAtUtc.Year < 1970)
                    Fail("邻域记录编号、时间或唯一性无效。");
                if (!change.ParsedAllDepotChanges || change.UnparsedChanges == null || change.UnparsedChanges.Count != 0 || change.Deltas == null)
                    Fail("邻域包含未完整解析的 Depot 变更，不能推导字段证据：" + change.ChangeId);
                if (String.IsNullOrWhiteSpace(change.SourceUrl)) Fail("邻域记录缺少来源：" + change.ChangeId);
                var paths = new HashSet<string>(StringComparer.Ordinal);
                foreach (HistoryDelta delta in change.Deltas)
                {
                    if (delta == null || !ValidPath(delta.Path) || !paths.Add(delta.Path) ||
                        delta.OldExists != (delta.OldValue != null) || delta.NewExists != (delta.NewValue != null) ||
                        (!delta.OldExists && !delta.NewExists)) Fail("邻域字段形状无效或重复：" + change.ChangeId);
                    if (delta.Path.EndsWith("/manifests/public/gid", StringComparison.Ordinal) &&
                        (delta.OldExists && !HistoryDepotState.IsNumeric(delta.OldValue) || delta.NewExists && !HistoryDepotState.IsNumeric(delta.NewValue)))
                        Fail("邻域 Manifest 编号不是数字：" + change.ChangeId);
                }
            }
            int cut = changes.FindIndex(c => c.ChangeId == location.CandidateChange.ChangeId);
            if (cut < 0 || !SameChange(changes[cut], location.CandidateChange)) Fail("候选与邻域中的原始变更不一致。");
            HistoryChange candidate = changes[cut];
            if (candidate.Publication == null || !candidate.Publication.IsPublic) Fail("候选不是公开发布事件。");
            DateTimeOffset effective = EffectiveTime(candidate);
            if (effective != location.EffectiveTime) Fail("候选有效时间与原始记录不一致。");
            DateTimeOffset? previousEffective = null;
            int nearestBefore = -1;
            for (int i = 0; i < changes.Count; i++)
            {
                if (changes[i].Publication == null) continue;
                if (!changes[i].Publication.IsPublic) Fail("邻域发布记录不是 public 分支。");
                DateTimeOffset time = EffectiveTime(changes[i]);
                if (previousEffective.HasValue && time < previousEffective.Value) Fail("邻域公开事件有效时间倒退，无法定位最近事件。");
                previousEffective = time;
                if (location.RequestedUtc.HasValue && time < location.RequestedUtc.Value) nearestBefore = i;
            }
            if (location.RequestedUtc.HasValue && (effective >= location.RequestedUtc.Value || nearestBefore != cut))
                Fail("候选不是所给连续邻域中严格早于输入时间的最近公开事件。");

            var result = new NeighborhoodDepotEvidenceResult {
                ContiguousNeighborhoodAttested = true, RequestedUtc = location.RequestedUtc,
                CandidateChangeId = candidate.ChangeId, CandidateBuildId = candidate.Publication.BuildId,
                CandidateSourceUrl = candidate.SourceUrl, CandidateEffectiveTimeUtc = effective,
                CandidateTimeSource = Precise(candidate) ? "Valve public/timeupdated" : "SteamDB public branch change observed time",
                ObservedFromUtc = changes[0].ObservedAtUtc, ObservedThroughUtc = changes[changes.Count - 1].ObservedAtUtc,
                KnownFields = new List<NeighborhoodDepotFieldEvidence>(), ObservedDepots = new List<NeighborhoodDepotEvidence>(),
                Limitation = "仅证明调用方已核实连续邻域内各字段在候选发布刚生效后的值；未证明邻域之外没有更近发布、历史 Depot 成员全集或全部配置。没有变更记录表示未知，不表示不存在。此结果不是完整下载清单。"
            };
            var allPaths = changes.SelectMany(c => c.Deltas).Select(d => d.Path).Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToList();
            foreach (string path in allPaths)
            {
                FieldChange before = null, after = null;
                for (int i = 0; i < changes.Count; i++)
                {
                    HistoryDelta delta = changes[i].Deltas.FirstOrDefault(d => d.Path == path);
                    if (delta == null) continue;
                    if (i <= cut) before = new FieldChange { Change = changes[i], Delta = delta };
                    else if (after == null) after = new FieldChange { Change = changes[i], Delta = delta };
                }
                // Only the closest witnesses bound the cut. Distant mutations are
                // not used as a substitute for these witnesses or for a baseline.
                if (before != null && after != null &&
                    (before.Delta.NewExists != after.Delta.OldExists || before.Delta.NewValue != after.Delta.OldValue))
                    Fail("候选前后最近字段证据不连续：" + path);
                string[] components = path.Split('/');
                var evidence = new NeighborhoodDepotFieldEvidence {
                    DepotId = components[1], Field = String.Join("/", components.Skip(2)), Path = path,
                    Exists = before != null ? before.Delta.NewExists : after.Delta.OldExists,
                    Value = before != null ? before.Delta.NewValue : after.Delta.OldValue,
                    Method = before != null ? "LatestNewAtOrBeforeCandidate" : "NearestOldAfterCandidate",
                    Evidence = new List<NeighborhoodFieldWitness>()
                };
                if (before != null) evidence.Evidence.Add(Witness(before, false));
                if (after != null) evidence.Evidence.Add(Witness(after, true));
                result.KnownFields.Add(evidence);
            }
            foreach (string depotId in result.KnownFields.Select(f => f.DepotId).Distinct().OrderBy(id => UInt64.Parse(id, CultureInfo.InvariantCulture)))
            {
                var known = new HashSet<string>(result.KnownFields.Where(f => f.DepotId == depotId).Select(f => f.Field), StringComparer.Ordinal);
                result.ObservedDepots.Add(new NeighborhoodDepotEvidence { DepotId = depotId, UnknownFields = Fields.Where(f => !known.Contains(f)).ToList() });
            }
            return result;
        }

        private sealed class FieldChange { internal HistoryChange Change; internal HistoryDelta Delta; }
        private static NeighborhoodFieldWitness Witness(FieldChange value, bool old)
        {
            return new NeighborhoodFieldWitness { ChangeId = value.Change.ChangeId, SourceUrl = value.Change.SourceUrl,
                ObservedAtUtc = value.Change.ObservedAtUtc, Side = old ? "Old" : "New",
                Exists = old ? value.Delta.OldExists : value.Delta.NewExists, Value = old ? value.Delta.OldValue : value.Delta.NewValue };
        }
        private static bool ValidPath(string path)
        {
            if (path == null) return false;
            string[] parts = path.Split('/');
            return parts.Length >= 3 && parts[0] == "depots" && HistoryDepotState.IsNumeric(parts[1]) && Fields.Contains(String.Join("/", parts.Skip(2)), StringComparer.Ordinal);
        }
        private static bool Precise(HistoryChange change) { return change.Publication.PublishedAtUtc.ToUniversalTime().Year >= 1970; }
        private static DateTimeOffset EffectiveTime(HistoryChange change)
        {
            DateTimeOffset value = Precise(change) ? change.Publication.PublishedAtUtc.ToUniversalTime() : change.ObservedAtUtc.ToUniversalTime();
            if (value.Year < 1970 || value > change.ObservedAtUtc) Fail("公开事件的有效时间无效：" + change.ChangeId);
            return value;
        }
        private static bool SameChange(HistoryChange a, HistoryChange b)
        {
            if (a.ObservedAtUtc != b.ObservedAtUtc || a.ParsedAllDepotChanges != b.ParsedAllDepotChanges || a.Deltas == null || b.Deltas == null || a.Deltas.Count != b.Deltas.Count) return false;
            if (a.Publication == null || b.Publication == null) return a.Publication == b.Publication;
            if (a.Publication.IsPublic != b.Publication.IsPublic || a.Publication.BuildId != b.Publication.BuildId || a.Publication.PreviousBuildId != b.Publication.PreviousBuildId ||
                a.Publication.PublishedAtUtc != b.Publication.PublishedAtUtc || a.Publication.PreviousPublishedAtUtc != b.Publication.PreviousPublishedAtUtc) return false;
            return a.Deltas.All(x => b.Deltas.Any(y => x.Path == y.Path && x.OldExists == y.OldExists && x.NewExists == y.NewExists && x.OldValue == y.OldValue && x.NewValue == y.NewValue));
        }
        private static void Fail(string message) { throw new HistoryResolutionException(message); }
    }
}
