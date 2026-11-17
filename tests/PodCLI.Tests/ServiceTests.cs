using PodCLI;

namespace PodCLI.Tests;

public sealed class StoreTests : TempStoreFixture
{
    [Fact]
    public void RoundTripsLibraryWithEpisodesAndQueue()
    {
        var library = new Library();
        var show = FakeShow();
        show.Episodes[0].Played = true;
        show.Episodes[0].PositionSeconds = 42;
        library.Podcasts.Add(show);
        library.Queue.Add(new QueueItem { PodcastId = show.Id, EpisodeGuid = "e2" });

        Store.Save(library);
        var loaded = Store.Load();

        Assert.Single(loaded.Podcasts);
        Assert.Equal("Test Show", loaded.Podcasts[0].Title);
        Assert.Equal(3, loaded.Podcasts[0].Episodes.Count);
        Assert.True(loaded.Podcasts[0].Episodes[0].Played);
        Assert.Equal(42, loaded.Podcasts[0].Episodes[0].PositionSeconds);
        Assert.Single(loaded.Queue);
        Assert.Equal("e2", loaded.Queue[0].EpisodeGuid);
    }

    [Fact]
    public void ReturnsEmptyLibraryWhenNoFileExists()
    {
        var loaded = Store.Load();
        Assert.Empty(loaded.Podcasts);
        Assert.Empty(loaded.Queue);
    }

    [Fact]
    public void LeavesNoTempFilesAfterSave()
    {
        Store.Save(new Library { Podcasts = [FakeShow()] });
        var leftovers = Directory.GetFiles(TempDir, "*.tmp-*");
        Assert.Empty(leftovers);
        Assert.True(File.Exists(Store.LibraryPath));
    }

    [Fact]
    public void EpisodeFileNameSanitizesAndKeepsExtension()
    {
        var episode = new Episode
        {
            Title = "Hello: World? / Special * Episode!",
            PublishedAt = new DateTimeOffset(2026, 11, 17, 0, 0, 0, TimeSpan.Zero),
            AudioUrl = "https://cdn.example.com/ep.m4a?token=abc",
        };
        var name = Store.EpisodeFileName(episode);
        Assert.Equal("2026-11-17-Hello-World-Special-Episode.m4a", name);
    }

    [Fact]
    public void EpisodeFileNameDefaultsToMp3AndUndated()
    {
        var episode = new Episode { Title = "No date", AudioUrl = "https://cdn.example.com/stream" };
        Assert.Equal("undated-No-date.mp3", Store.EpisodeFileName(episode));
    }

    [Fact]
    public void FindPodcastMatchesByIdOrUniqueTitle()
    {
        var library = new Library { Podcasts = [FakeShow("abc123", "Systems Chats"), FakeShow("def456", "Kernel Panic FM")] };
        Assert.Equal("abc123", library.FindPodcast("abc123")?.Id);
        Assert.Equal("Systems Chats", library.FindPodcast("systems")?.Title);
        Assert.Null(library.FindPodcast("no such show"));
        Assert.Null(library.FindPodcast("a")); // ambiguous: matches nothing uniquely? "a" appears in both titles
    }
}

public sealed class PodcastServiceTests : TempStoreFixture
{
    [Fact]
    public async Task SubscribeAddsShowWithEpisodes()
    {
        var handler = new QueueHandler();
        handler.Enqueue(QueueHandler.Ok(SampleFeeds.Rss, eTag: "\"v1\""));
        var service = new PodcastService(handler);

        var library = new Library();
        var result = await service.SubscribeAsync(library, "https://example.com/feed.xml");

        Assert.NotNull(result.Show);
        Assert.Equal("Systems Chats", result.Show!.Title);
        Assert.Equal(3, result.EpisodesAdded);
        Assert.Equal("\"v1\"", result.Show.ETag);
        Assert.Single(library.Podcasts);
    }

    [Fact]
    public async Task SubscribeRejectsInvalidUrl()
    {
        var service = new PodcastService(new QueueHandler());
        var result = await service.SubscribeAsync(new Library(), "not a url");
        Assert.Null(result.Show);
        Assert.Contains("valid", result.Error);
    }

    [Fact]
    public async Task SubscribeRejectsDuplicateFeed()
    {
        var handler = new QueueHandler();
        handler.Enqueue(QueueHandler.Ok(SampleFeeds.Rss));
        var service = new PodcastService(handler);

        var library = new Library();
        await service.SubscribeAsync(library, "https://example.com/feed.xml");
        var again = await service.SubscribeAsync(library, "https://example.com/feed.xml");

        Assert.Null(again.Show);
        Assert.Contains("Already subscribed", again.Error);
        Assert.Single(library.Podcasts);
    }

    [Fact]
    public async Task SubscribeRejectsUnparseableFeed()
    {
        var handler = new QueueHandler();
        handler.Enqueue(QueueHandler.Ok("<html>nope</html>"));
        var service = new PodcastService(handler);

        var result = await service.SubscribeAsync(new Library(), "https://example.com/feed.xml");
        Assert.Null(result.Show);
        Assert.Contains("Could not parse", result.Error);
    }

    [Fact]
    public async Task RefreshMergesNewEpisodesAndPreservesState()
    {
        var handler = new QueueHandler();
        handler.Enqueue(QueueHandler.Ok(SampleFeeds.Rss, eTag: "\"v1\""));
        handler.Enqueue(QueueHandler.Ok(SampleFeeds.RssWithNewEpisode, eTag: "\"v2\""));
        var service = new PodcastService(handler);

        var library = new Library();
        await service.SubscribeAsync(library, "https://example.com/feed.xml");
        var show = library.Podcasts[0];
        show.Episodes[0].Played = true; // mark newest played before refresh

        var result = await service.RefreshAsync(library, show);
        Assert.Equal(1, result.NewEpisodes);
        Assert.Equal(4, show.Episodes.Count);
        Assert.Equal("ep-4", show.Episodes[0].Guid); // newest first
        Assert.True(show.Episodes.First(e => e.Guid == "ep-3").Played); // state kept
        Assert.Equal("\"v2\"", show.ETag);
    }

    [Fact]
    public async Task RefreshHandlesNotModifiedWithoutChanges()
    {
        var handler = new QueueHandler();
        handler.Enqueue(QueueHandler.Ok(SampleFeeds.Rss, eTag: "\"v1\""));
        handler.Enqueue(QueueHandler.Status(System.Net.HttpStatusCode.NotModified));
        var service = new PodcastService(handler);

        var library = new Library();
        await service.SubscribeAsync(library, "https://example.com/feed.xml");
        var result = await service.RefreshAsync(library, null);

        Assert.Equal(0, result.NewEpisodes);
        Assert.Equal(3, library.Podcasts[0].Episodes.Count);
        Assert.NotNull(library.Podcasts[0].LastRefreshedAt);
    }

    [Fact]
    public async Task DownloadMarksEpisodeDownloaded()
    {
        var audio = new byte[] { 1, 2, 3, 4, 5 };
        var handler = new QueueHandler();
        handler.Enqueue(QueueHandler.Ok(SampleFeeds.Rss));
        handler.Enqueue(QueueHandler.Bytes(audio));
        var service = new PodcastService(handler);

        var library = new Library();
        await service.SubscribeAsync(library, "https://example.com/feed.xml");
        var show = library.Podcasts[0];

        var result = await service.DownloadEpisodeAsync(Store, library, show, 1);
        Assert.Equal(Downloader.DownloadOutcome.Downloaded, result.Outcome);
        Assert.True(result.Episode!.Downloaded);
        Assert.NotNull(result.Episode.LocalPath);
        Assert.True(File.Exists(result.Episode.LocalPath));
        Assert.Equal(audio, File.ReadAllBytes(result.Episode.LocalPath!));
    }

    [Fact]
    public async Task DownloadRejectsOutOfRangeEpisodeNumber()
    {
        var service = new PodcastService(new QueueHandler());
        var library = new Library { Podcasts = [FakeShow()] };
        var result = await service.DownloadEpisodeAsync(Store, library, library.Podcasts[0], 99);
        Assert.Equal(Downloader.DownloadOutcome.Failed, result.Outcome);
        Assert.Contains("between 1 and 3", result.Error);
    }
}

public sealed class DownloaderTests : TempStoreFixture
{
    [Fact]
    public async Task DownloadsBytesAndReportsProgress()
    {
        var data = Enumerable.Range(0, 1000).Select(i => (byte)(i % 256)).ToArray();
        var handler = new QueueHandler();
        handler.Enqueue(QueueHandler.Bytes(data));
        var downloader = new Downloader(handler);

        var episode = new Episode { Title = "Ep", AudioUrl = "https://cdn.example.com/ep.mp3", AudioLengthBytes = data.Length };
        var progress = new SyncProgress();
        var result = await downloader.DownloadAsync(episode, TempDir, progress);

        Assert.Equal(Downloader.DownloadOutcome.Downloaded, result.Outcome);
        Assert.Equal(data, File.ReadAllBytes(result.LocalPath!));
        Assert.Contains(1.0, progress.Values);
    }

    [Fact]
    public async Task SkipsDownloadWhenFileAlreadyMatches()
    {
        var data = new byte[] { 9, 9, 9 };
        var handler = new QueueHandler();
        var downloader = new Downloader(handler); // no response enqueued: must not fetch

        var episode = new Episode { Title = "Ep", AudioUrl = "https://cdn.example.com/ep.mp3", AudioLengthBytes = 3 };
        var path = Path.Combine(TempDir, Store.EpisodeFileName(episode));
        File.WriteAllBytes(path, data);

        var result = await downloader.DownloadAsync(episode, TempDir);
        Assert.Equal(Downloader.DownloadOutcome.AlreadyDownloaded, result.Outcome);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ReportsFailureOnHttpError()
    {
        var handler = new QueueHandler();
        handler.Enqueue(QueueHandler.Status(System.Net.HttpStatusCode.NotFound));
        var downloader = new Downloader(handler);

        var episode = new Episode { Title = "Ep", AudioUrl = "https://cdn.example.com/missing.mp3" };
        var result = await downloader.DownloadAsync(episode, TempDir);

        Assert.Equal(Downloader.DownloadOutcome.Failed, result.Outcome);
        Assert.Contains("404", result.Error);
        Assert.Empty(Directory.GetFiles(TempDir, "*.part-*")); // no partial file left
    }
}
