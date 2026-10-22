using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace DotaArchive
{
    public static class TimelineResolver
    {
        private const string UtcFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";
        private static readonly TimeSpan BeijingOffset = TimeSpan.FromHours(8);

        public static DateTimeOffset ParseUtc(string value)
        {
            DateTimeOffset result;
            if (value == null || !Regex.IsMatch(value, @"\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z\z", RegexOptions.CultureInvariant) ||
                !DateTimeOffset.TryParseExact(value, UtcFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out result))
                throw new InvalidDataException("发布时间必须是精确到秒的 UTC 时间，例如 2016-12-13T00:32:43Z。");
            return result;
        }

        public static string FormatBeijing(string utc)
        {
            return ParseUtc(utc).ToOffset(BeijingOffset).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        public static TimelineResult Resolve(CatalogDocument catalog, DateTime beijingWallTime)
        {
            // A picker supplies a wall clock, not the computer's local timezone.
            // Ignore Kind and hidden subsecond ticks: the UI and index use seconds.
            var wallTime = new DateTime(beijingWallTime.Year, beijingWallTime.Month, beijingWallTime.Day,
                beijingWallTime.Hour, beijingWallTime.Minute, beijingWallTime.Second, DateTimeKind.Unspecified);
            DateTimeOffset requested;
            try { requested = new DateTimeOffset(wallTime, BeijingOffset).ToUniversalTime(); }
            catch (ArgumentException ex) { throw new InvalidDataException("查询时间超出可表示范围。", ex); }
            var windows = ValidateCatalogTimeline(catalog);
            foreach (var transition in windows)
            {
                if (requested < ParseUtc(transition.PreviousPublishedAtUtc) || requested >= ParseUtc(transition.PublishedAtUtc)) continue;
                if (transition.VersionId == null)
                    return new TimelineResult { Status = "MissingPlan", RequestedUtc = requested, Transition = transition,
                        Message = "已确认此时间之后首次公开发布的构建为 " + transition.BuildId + "（北京时间 " + FormatBeijing(transition.PublishedAtUtc) + "），下载清单尚未验证。" };
                VersionEntry version = catalog.Versions.Find(delegate(VersionEntry item) { return String.Equals(item.Id, transition.VersionId, StringComparison.OrdinalIgnoreCase); });
                return new TimelineResult { Status = "Ready", RequestedUtc = requested, Transition = transition, Version = version,
                    Message = "已确认此时间之后首次公开发布的构建为 " + transition.BuildId + "（北京时间 " + FormatBeijing(transition.PublishedAtUtc) + "）。" };
            }
            return new TimelineResult { Status = "Uncovered", RequestedUtc = requested,
                Message = "此时间未被已核实的连续公开发布记录覆盖，无法确认之后首次发布的版本。" };
        }

        // Shared by Load and Resolve so hand-created catalogs cannot bypass the
        // same ambiguity checks. Build numbers are identities, not chronology:
        // an older build may be published again during a public rollback.
        internal static List<PublicBuildTransition> ValidateCatalogTimeline(CatalogDocument catalog)
        {
            if (catalog == null || catalog.Versions == null) throw new InvalidDataException("版本目录缺少版本列表。");
            var versions = new Dictionary<string, VersionEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var version in catalog.Versions)
            {
                if (version == null || String.IsNullOrWhiteSpace(version.Id) || versions.ContainsKey(version.Id)) throw new InvalidDataException("版本 ID 缺失或重复。");
                versions.Add(version.Id, version);
                if (version.PublishedAtUtc != null) ParseUtc(version.PublishedAtUtc);
            }
            var result = new List<PublicBuildTransition>();
            if (catalog.PublicTransitions == null) return result;
            var windows = new Dictionary<string, PublicBuildTransition>(StringComparer.Ordinal);
            foreach (var transition in catalog.PublicTransitions)
            {
                if (transition == null) throw new InvalidDataException("公开发布记录不能为空。");
                RequireBuildId(transition.PreviousBuildId);
                RequireBuildId(transition.BuildId);
                DateTimeOffset previous = ParseUtc(transition.PreviousPublishedAtUtc);
                DateTimeOffset published = ParseUtc(transition.PublishedAtUtc);
                if (previous >= published) throw new InvalidDataException("公开发布记录的前次时间必须早于本次时间。");
                Uri source;
                if (!Uri.TryCreate(transition.SourceUrl, UriKind.Absolute, out source) ||
                    (source.Scheme != Uri.UriSchemeHttps && source.Scheme != Uri.UriSchemeHttp) || String.IsNullOrEmpty(source.Host))
                    throw new InvalidDataException("公开发布记录必须包含有效的来源链接。");
                if (transition.VersionId != null)
                {
                    VersionEntry version;
                    if (!versions.TryGetValue(transition.VersionId, out version)) throw new InvalidDataException("公开发布记录引用了不存在的版本：" + transition.VersionId);
                    if (!String.Equals(version.BuildId, transition.BuildId, StringComparison.Ordinal)) throw new InvalidDataException("公开发布记录与下载版本的构建 ID 不一致。");
                    // PublishedAtUtc on a version is its first publication. A
                    // later rollback may legitimately reuse its download plan.
                }
                string key = transition.PreviousPublishedAtUtc + "/" + transition.PublishedAtUtc;
                PublicBuildTransition existing;
                if (windows.TryGetValue(key, out existing))
                {
                    if (!SameTransition(existing, transition)) throw new InvalidDataException("同一公开发布时间窗口包含冲突记录。");
                    continue; // Repeated identical evidence does not create a second match.
                }
                windows.Add(key, transition);
                result.Add(transition);
            }
            result.Sort(delegate(PublicBuildTransition left, PublicBuildTransition right) {
                return String.CompareOrdinal(left.PreviousPublishedAtUtc, right.PreviousPublishedAtUtc);
            });
            for (int i = 1; i < result.Count; i++)
            {
                var previous = result[i - 1];
                var current = result[i];
                int relation = String.CompareOrdinal(current.PreviousPublishedAtUtc, previous.PublishedAtUtc);
                if (relation < 0) throw new InvalidDataException("公开发布时间窗口重叠，无法唯一确定之后首次发布的版本。");
                if (relation == 0 && !String.Equals(current.PreviousBuildId, previous.BuildId, StringComparison.Ordinal))
                    throw new InvalidDataException("相邻公开发布记录在同一时间指向不同构建。");
            }
            return result;
        }

        private static void RequireBuildId(string value)
        {
            ulong number;
            if (value == null || !Regex.IsMatch(value, @"\A[1-9][0-9]*\z", RegexOptions.CultureInvariant) ||
                !UInt64.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number))
                throw new InvalidDataException("公开发布构建 ID 必须是正整数形式的字符串。");
        }

        private static bool SameTransition(PublicBuildTransition left, PublicBuildTransition right)
        {
            return String.Equals(left.PreviousBuildId, right.PreviousBuildId, StringComparison.Ordinal) &&
                String.Equals(left.PreviousPublishedAtUtc, right.PreviousPublishedAtUtc, StringComparison.Ordinal) &&
                String.Equals(left.BuildId, right.BuildId, StringComparison.Ordinal) &&
                String.Equals(left.PublishedAtUtc, right.PublishedAtUtc, StringComparison.Ordinal) &&
                String.Equals(left.VersionId, right.VersionId, StringComparison.OrdinalIgnoreCase) &&
                String.Equals(left.SourceUrl, right.SourceUrl, StringComparison.Ordinal);
        }
    }
}
