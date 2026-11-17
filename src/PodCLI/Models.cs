namespace PodCLI;

/// <summary>A subscribed podcast show: one RSS/Atom feed and its episode list.</summary>
public sealed class Podcast
{
    /// <summary>Short stable id, derived from the feed URL hash. Used in CLI commands.</summary>
    public string Id { get; set; } = "";

    public string Title { get; set; } = "";
    public string FeedUrl { get; set; } = "";
    public string? SiteUrl { get; set; }
    public string? Description { get; set; }
    public string? ImageUrl { get; set; }

    /// <summary>Conditional-GET validators from the last successful refresh.</summary>
    public string? ETag { get; set; }
    public string? LastModified { get; set; }

    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastRefreshedAt { get; set; }

    /// <summary>Episodes, newest first.</summary>
    public List<Episode> Episodes { get; set; } = new();
}

/// <summary>One episode of a show. Identity is the feed-provided guid (or audio URL fallback).</summary>
public sealed class Episode
{
    public string Guid { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }

    public string? AudioUrl { get; set; }
    public string? AudioType { get; set; }
    public long? AudioLengthBytes { get; set; }
    public int? DurationSeconds { get; set; }

    public bool Downloaded { get; set; }
    public string? LocalPath { get; set; }
    public DateTimeOffset? DownloadedAt { get; set; }

    public bool Played { get; set; }
    public int PositionSeconds { get; set; }
}

/// <summary>One entry in the playback queue: a (show, episode) pair in play order.</summary>
public sealed class QueueItem
{
    public string PodcastId { get; set; } = "";
    public string EpisodeGuid { get; set; } = "";
    public DateTimeOffset EnqueuedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>The whole library: every subscription plus the playback queue. One JSON file.</summary>
public sealed class Library
{
    public int Version { get; set; } = 1;
    public List<Podcast> Podcasts { get; set; } = new();
    public List<QueueItem> Queue { get; set; } = new();

    /// <summary>The episode currently marked as playing, if any.</summary>
    public QueueItem? NowPlaying { get; set; }

    public Podcast? FindPodcast(string idOrTitlePart)
    {
        var exact = Podcasts.FirstOrDefault(p =>
            string.Equals(p.Id, idOrTitlePart, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        var matches = Podcasts.Where(p =>
            p.Title.Contains(idOrTitlePart, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }
}
