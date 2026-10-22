using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DotaArchive
{
    public interface IHistoryBrowser
    {
        Task<HistoryPageSnapshot> NavigateAsync(string url, CancellationToken token);
        Task<HistoryPageSnapshot> CaptureAsync(CancellationToken token);
        Task<HistoryPageSnapshot> ClickAsync(string selector, CancellationToken token);
    }

    public interface IHistoryDateBrowser
    {
        Task<HistoryPageSnapshot> LoadHistoryBeforeAsync(DateTime date, CancellationToken token);
    }

    public interface IHistoryDateNavigationBrowser
    {
        Task OpenHistoryForDateAsync(CancellationToken token);
    }

    public sealed class HistoryPageSnapshot
    {
        public string Url { get; set; }
        public string Title { get; set; }
        public string Html { get; set; }
        public string Text { get; set; }
        public DateTimeOffset CapturedAtUtc { get; set; }
    }

    public enum HistoryTimeDirection
    {
        Before = 0,
        After = 1
    }

    public sealed class HistoryQuery
    {
        // Timestamp queries exclude the requested instant in both directions.
        // Patch-name queries translate the bundled publication date/announcement
        // into a local timestamp and apply this same strict direction.
        public string Patch { get; set; }
        public DateTimeOffset? BeijingTime { get; set; }
        public DateTimeOffset? RequestedUtc { get; set; }
        public string Language { get; set; }
        public HistoryTimeDirection TimeDirection { get; set; }
        public HistoryQuery() { Language = "schinese"; TimeDirection = HistoryTimeDirection.Before; }
    }

    public sealed class ResolvedHistoryPlan
    {
        public string BuildId { get; set; }
        public DateTimeOffset PublishedAtUtc { get; set; }
        public string Patch { get; set; }
        public string SourceUrl { get; set; }
        public List<DepotEntry> Depots { get; set; }
        public bool Complete { get; set; }
        public string Assurance { get; set; }
        public string TimeSource { get; set; }
        public bool UsedObservationFallback { get; set; }
        public List<string> Sources { get; set; }
        public List<string> Limitations { get; set; }
        public string PatchMatchKind { get; set; }
        public string PatchDateDescription { get; set; }
        public string PatchSourceChangeId { get; set; }
        public DateTimeOffset? PatchSourceObservedAtUtc { get; set; }
        public DateTimeOffset? PatchWindowNotAvailableAtUtc { get; set; }
        public DateTimeOffset? PatchWindowAvailableByUtc { get; set; }
        public int? PatchWindowPublicEvents { get; set; }
        public ResolvedHistoryPlan()
        {
            Depots = new List<DepotEntry>(); Sources = new List<string>(); Limitations = new List<string>();
        }
    }

    public sealed class HistoryResolutionException : Exception
    {
        public HistoryResolutionException(string message) : base(message) { }
    }

    // Dates belong to branch publication events, never manifest creation or tracker observations.
    public sealed class HistoryBuildEvent
    {
        public string BuildId { get; set; }
        public DateTimeOffset PublishedAtUtc { get; set; }
        public string SourceUrl { get; set; }
        public string ChangeId { get; set; }
        public string Title { get; set; }
        public string PreviousBuildId { get; set; }
        public DateTimeOffset? PreviousPublishedAtUtc { get; set; }
        public string PreviousUrl { get; set; }
        public string NextUrl { get; set; }
        public bool IsPublic { get; set; }
    }

    public sealed class HistoryDelta
    {
        public string Path { get; set; }
        public bool OldExists { get; set; }
        public string OldValue { get; set; }
        public bool NewExists { get; set; }
        public string NewValue { get; set; }
    }

    public sealed class HistoryChange
    {
        public string ChangeId { get; set; }
        public DateTimeOffset ObservedAtUtc { get; set; }
        public string SourceUrl { get; set; }
        public List<HistoryDelta> Deltas { get; set; }
        public HistoryBuildEvent Publication { get; set; }
        public bool ParsedAllDepotChanges { get; set; }
        public List<string> UnparsedChanges { get; set; }
        public HistoryChange() { Deltas = new List<HistoryDelta>(); UnparsedChanges = new List<string>(); }
    }

    public sealed class HistoryListing
    {
        public List<HistoryBuildEvent> Builds { get; set; }
        public List<HistoryChange> Changes { get; set; }
        public string NextPageUrl { get; set; }
        public string LoadMoreSelector { get; set; }
        public bool EndReached { get; set; }
        public string NavigationBlockedReason { get; set; }
        public HistoryListing() { Builds = new List<HistoryBuildEvent>(); Changes = new List<HistoryChange>(); }
    }

    public sealed class HistoryBaseline
    {
        public HistoryDepotState State { get; set; }
        public string BuildId { get; set; }
        public DateTimeOffset PublishedAtUtc { get; set; }
        public string SourceUrl { get; set; }
    }
}
