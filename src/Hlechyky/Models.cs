namespace Hlechyky;

public sealed record TrackInfo(string Id, string Title, string Artist, int DurationSec, string? ThumbUrl, string SourceUrl, string? Album)
{
    public string Label => string.IsNullOrWhiteSpace(Artist) ? Title : $"{Artist} — {Title}";
}

public enum ItemStatus { Queued, Downloading, Ready, Dispatched, Failed }

public sealed class QueueItem
{
    public string ItemId { get; init; } = Guid.NewGuid().ToString("N")[..10];
    public required TrackInfo Track { get; init; }
    public required string RequestedBy { get; init; }
    public DateTimeOffset AddedAt { get; init; } = DateTimeOffset.UtcNow;
    public ItemStatus Status { get; set; } = ItemStatus.Queued;
    public string? Error { get; set; }
    public string? FilePath { get; set; }
    public string? Rid { get; set; }
    /// <summary>user | autodj | suggestion</summary>
    public string Kind { get; init; } = "user";
    /// <summary>Why the auto-DJ picked it (also kept when a person takes a suggestion).</summary>
    public string? Reason { get; init; }
    /// <summary>"suggestion" when a person queued it from the DJ's suggestions, else null.</summary>
    public string? Via { get; init; }
}

public sealed class NowPlaying
{
    public TrackInfo? Track { get; set; }
    /// <summary>user | autodj | fallback | silence</summary>
    public string Source { get; set; } = "silence";
    public string? ItemId { get; set; }
    public string? RequestedBy { get; set; }
    public string? Reason { get; set; }
    public string? Via { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public int DurationSec { get; set; }
    public List<string> Likers { get; set; } = new();
    /// <summary>A skip was sent to liquidsoap and the next track has not started yet.</summary>
    public bool SkipPending { get; set; }
    public bool SpotifyLive { get; set; }
    public string? SpotifyTitle { get; set; }
    public long PlayId { get; set; }
}

public sealed record ChatMessage(long Id, string Nick, string Text, DateTimeOffset At, string Kind);

public sealed record SearchResult(string Id, string Title, string Artist, string? Album, int DurationSec, string? ThumbUrl);

public sealed record QueueItemDto(string ItemId, TrackInfo Track, string RequestedBy, string Status, string? Error, string Kind, string? Reason, string? Via, DateTimeOffset AddedAt);

public sealed record HistoryEntry(long PlayId, TrackInfo Track, string Source, string? RequestedBy, DateTimeOffset StartedAt, int Likes, string? Via, bool Skipped);

public sealed record PersistedQueueItem(string ItemId, TrackInfo Track, string RequestedBy, string Kind, string? Reason, string? Via, DateTimeOffset AddedAt);

public sealed record NickCount(string Nick, int Count);

public sealed class StateSnapshot
{
    public required NowPlaying Now { get; init; }
    public required List<QueueItemDto> Queue { get; init; }
    public QueueItemDto? AutoNext { get; init; }
    public List<QueueItemDto> Suggestions { get; init; } = new();
    /// <summary>The track the suggestions were built from (what is on air, or the last thing that played).</summary>
    public TrackInfo? SuggestSeed { get; init; }
    public required List<string> Online { get; init; }
    public required bool LiquidsoapOk { get; init; }
    public required int Listeners { get; init; }
    public required int StreamDelaySeconds { get; init; }
    public required string SiteName { get; init; }
    public required string DjName { get; init; }
    public required string DjNameGen { get; init; }
    public required string StreamUrl { get; init; }
    public required bool LastFmEnabled { get; init; }
}
