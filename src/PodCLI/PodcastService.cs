using System.Security.Cryptography;
using System.Text;

namespace PodCLI;

/// <summary>
/// The library operations behind the CLI: subscribing, refreshing feeds, and
/// downloading episodes. Keeps all state changes in one place so the CLI is
/// just argument parsing and printing, and tests can drive the service with a
/// mocked HTTP handler instead of a terminal.
/// </summary>
public sealed class PodcastService
{
    private readonly FeedFetcher _fetcher;
    private readonly Downloader _downloader;

    public PodcastService(FeedFetcher fetcher, Downloader downloader)
    {
        _fetcher = fetcher;
        _downloader = downloader;
    }

    public PodcastService(HttpMessageHandler? handler = null)
        : this(new FeedFetcher(handler), new Downloader(handler))
    {
    }

    /// <summary>Stable 12-hex-char id for a feed URL, so ids are reproducible across machines.</summary>
    public static string IdForFeedUrl(string feedUrl)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(feedUrl.Trim()));
        return Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    public sealed record SubscribeResult(Podcast? Show, int EpisodesAdded, string? Error);

    public async Task<SubscribeResult> SubscribeAsync(Library library, string feedUrl, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(feedUrl.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
            return new SubscribeResult(null, 0, "Not a valid http(s) feed URL");

        if (library.Podcasts.Any(p =>
                string.Equals(p.FeedUrl, feedUrl.Trim(), StringComparison.OrdinalIgnoreCase)))
            return new SubscribeResult(null, 0, "Already subscribed to this feed");

        var result = await _fetcher.FetchAsync(feedUrl.Trim(), null, null, ct);
        if (result.Status != FetchStatus.Ok)
            return new SubscribeResult(null, 0, result.Error ?? "Feed fetch failed");

        ParsedFeed parsed;
        try
        {
            parsed = FeedParser.Parse(result.Xml!);
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException)
        {
            return new SubscribeResult(null, 0, $"Could not parse feed: {ex.Message}");
        }

        var show = new Podcast
        {
            Id = IdForFeedUrl(feedUrl),
            Title = parsed.Title,
            FeedUrl = feedUrl.Trim(),
            SiteUrl = parsed.SiteUrl,
            Description = parsed.Description,
            ImageUrl = parsed.ImageUrl,
            ETag = result.ETag,
            LastModified = result.LastModified,
            LastRefreshedAt = DateTimeOffset.UtcNow,
            Episodes = parsed.Episodes,
        };
        library.Podcasts.Add(show);
        return new SubscribeResult(show, parsed.Episodes.Count, null);
    }

    public sealed record RefreshResult(int ShowsRefreshed, int NewEpisodes, List<string> Errors);

    /// <summary>Refresh one show (by id/title) or all shows when <paramref name="show"/> is null.</summary>
    public async Task<RefreshResult> RefreshAsync(Library library, Podcast? show, CancellationToken ct = default)
    {
        var targets = show is null ? library.Podcasts : new List<Podcast> { show };
        var errors = new List<string>();
        var newEpisodes = 0;

        foreach (var target in targets)
        {
            var result = await _fetcher.FetchAsync(target.FeedUrl, target.ETag, target.LastModified, ct);
            switch (result.Status)
            {
                case FetchStatus.NotModified:
                    target.LastRefreshedAt = DateTimeOffset.UtcNow;
                    break;
                case FetchStatus.Failed:
                    errors.Add($"{target.Title}: {result.Error}");
                    break;
                case FetchStatus.Ok:
                    try
                    {
                        var parsed = FeedParser.Parse(result.Xml!);
                        newEpisodes += MergeEpisodes(target, parsed.Episodes);
                        target.Title = parsed.Title;
                        target.SiteUrl = parsed.SiteUrl ?? target.SiteUrl;
                        target.Description = parsed.Description ?? target.Description;
                        target.ImageUrl = parsed.ImageUrl ?? target.ImageUrl;
                        target.ETag = result.ETag;
                        target.LastModified = result.LastModified;
                        target.LastRefreshedAt = DateTimeOffset.UtcNow;
                    }
                    catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException)
                    {
                        errors.Add($"{target.Title}: could not parse feed: {ex.Message}");
                    }
                    break;
            }
        }

        return new RefreshResult(targets.Count, newEpisodes, errors);
    }

    /// <summary>Adds fresh episodes the library does not have yet (matched by guid),
    /// keeps the played/downloaded state of episodes already known, newest first.</summary>
    public static int MergeEpisodes(Podcast show, List<Episode> fresh)
    {
        var known = new HashSet<string>(show.Episodes.Select(e => e.Guid), StringComparer.Ordinal);
        var added = 0;
        foreach (var episode in fresh)
        {
            if (!known.Add(episode.Guid)) continue;
            show.Episodes.Add(episode);
            added++;
        }
        show.Episodes.Sort((a, b) => Nullable.Compare(b.PublishedAt, a.PublishedAt));
        return added;
    }

    public sealed record DownloadEpisodeResult(Downloader.DownloadOutcome Outcome, Episode? Episode, string? Error);

    /// <summary>Downloads episode number <paramref name="episodeNumber"/> (1 = newest).</summary>
    public async Task<DownloadEpisodeResult> DownloadEpisodeAsync(
        Store store, Library library, Podcast show, int episodeNumber,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (episodeNumber < 1 || episodeNumber > show.Episodes.Count)
            return new DownloadEpisodeResult(Downloader.DownloadOutcome.Failed, null,
                $"Episode number must be between 1 and {show.Episodes.Count}");

        var episode = show.Episodes[episodeNumber - 1];
        var folder = store.ShowDownloadsDir(show);
        var result = await _downloader.DownloadAsync(episode, folder, progress, ct);
        if (result.Outcome != Downloader.DownloadOutcome.Failed)
        {
            episode.Downloaded = true;
            episode.LocalPath = result.LocalPath;
            episode.DownloadedAt = DateTimeOffset.UtcNow;
        }
        return new DownloadEpisodeResult(result.Outcome, episode, result.Error);
    }
}
