using PodCLI;

namespace PodCLI.Tests;

public sealed class QueueManagerTests : TempStoreFixture
{
    private static Library LibraryWithShow()
    {
        var library = new Library();
        library.Podcasts.Add(FakeShow());
        return library;
    }

    [Fact]
    public void AddEnqueuesAndDedupes()
    {
        var library = LibraryWithShow();
        Store.Save(library);
        var output = new StringWriter();

        Assert.Equal(0, QueueManager.Run(Store, output, ["add", "abc123", "1"]));
        Assert.Equal(0, QueueManager.Run(Store, output, ["add", "abc123", "1"])); // duplicate
        Assert.Equal(0, QueueManager.Run(Store, output, ["add", "abc123", "2"]));

        Assert.Equal(2, Store.Load().Queue.Count);
        Assert.Contains("already in the queue", output.ToString());
    }

    [Fact]
    public void AddRejectsBadEpisodeNumber()
    {
        Store.Save(LibraryWithShow());
        Assert.Equal(1, QueueManager.Run(Store, new StringWriter(), ["add", "abc123", "99"]));
        Assert.Equal(2, QueueManager.Run(Store, new StringWriter(), ["add", "abc123"]));
    }

    [Fact]
    public void ListShowsQueueInOrderWithNowPlayingMarker()
    {
        var library = LibraryWithShow();
        library.Queue.Add(new QueueItem { PodcastId = "abc123", EpisodeGuid = "e1" });
        library.Queue.Add(new QueueItem { PodcastId = "abc123", EpisodeGuid = "e2" });
        library.NowPlaying = new QueueItem { PodcastId = "abc123", EpisodeGuid = "e1" };
        Store.Save(library);

        var output = new StringWriter();
        Assert.Equal(0, QueueManager.Run(Store, output, ["list"]));
        var text = output.ToString();
        Assert.Contains("Newest", text);
        Assert.Contains("Middle", text);
        Assert.Contains("now playing", text);
        Assert.True(text.IndexOf("Newest", StringComparison.Ordinal) < text.IndexOf("Middle", StringComparison.Ordinal));
    }

    [Fact]
    public void RemoveAndMoveReorderQueue()
    {
        var library = LibraryWithShow();
        library.Queue.AddRange([
            new QueueItem { PodcastId = "abc123", EpisodeGuid = "e1" },
            new QueueItem { PodcastId = "abc123", EpisodeGuid = "e2" },
            new QueueItem { PodcastId = "abc123", EpisodeGuid = "e3" },
        ]);
        Store.Save(library);
        var output = new StringWriter();

        Assert.Equal(0, QueueManager.Run(Store, output, ["move", "3", "1"]));
        Assert.Equal("e3", Store.Load().Queue[0].EpisodeGuid);

        Assert.Equal(0, QueueManager.Run(Store, output, ["remove", "2"]));
        var queue = Store.Load().Queue;
        Assert.Equal(2, queue.Count);
        Assert.Equal("e3", queue[0].EpisodeGuid);
        Assert.Equal("e2", queue[1].EpisodeGuid);

        Assert.Equal(1, QueueManager.Run(Store, output, ["remove", "5"]));
        Assert.Equal(1, QueueManager.Run(Store, output, ["move", "1", "9"]));
    }

    [Fact]
    public void ClearEmptiesQueueAndNowPlaying()
    {
        var library = LibraryWithShow();
        library.Queue.Add(new QueueItem { PodcastId = "abc123", EpisodeGuid = "e1" });
        library.NowPlaying = new QueueItem { PodcastId = "abc123", EpisodeGuid = "e1" };
        Store.Save(library);

        Assert.Equal(0, QueueManager.Run(Store, new StringWriter(), ["clear"]));
        var loaded = Store.Load();
        Assert.Empty(loaded.Queue);
        Assert.Null(loaded.NowPlaying);
    }

    [Fact]
    public void PlaySetsNowPlayingAndProgressFinishesEpisode()
    {
        var library = LibraryWithShow();
        Store.Save(library);
        var output = new StringWriter();

        Assert.Equal(0, QueueManager.Play(Store, output, ["abc123", "1"]));
        Assert.Equal("e1", Store.Load().NowPlaying?.EpisodeGuid);
        Assert.Contains("Now playing", output.ToString());

        Assert.Equal(0, QueueManager.Progress(Store, output, ["600"]));
        Assert.Equal(600, Store.Load().Podcasts[0].Episodes[0].PositionSeconds);

        Assert.Equal(0, QueueManager.Progress(Store, output, ["1800"])); // reaches duration
        var loaded = Store.Load();
        Assert.True(loaded.Podcasts[0].Episodes[0].Played);
        Assert.Null(loaded.NowPlaying);
    }

    [Fact]
    public void ProgressWithoutNowPlayingFails()
    {
        Store.Save(LibraryWithShow());
        Assert.Equal(1, QueueManager.Progress(Store, new StringWriter(), ["30"]));
    }

    [Fact]
    public void MarkPlayedRemovesFromQueue()
    {
        var library = LibraryWithShow();
        library.Queue.Add(new QueueItem { PodcastId = "abc123", EpisodeGuid = "e2" });
        Store.Save(library);

        Assert.Equal(0, QueueManager.MarkPlayed(Store, new StringWriter(), ["abc123", "2"]));
        var loaded = Store.Load();
        Assert.True(loaded.Podcasts[0].Episodes[1].Played);
        Assert.Empty(loaded.Queue);
    }
}

public sealed class OpmlTests : TempStoreFixture
{
    [Fact]
    public void ExportWritesAllSubscriptions()
    {
        var library = new Library();
        library.Podcasts.Add(FakeShow("abc123", "Systems Chats"));
        library.Podcasts[0].FeedUrl = "https://example.com/systems.xml";
        library.Podcasts.Add(FakeShow("def456", "Kernel Panic FM"));
        library.Podcasts[1].FeedUrl = "https://example.com/kernel.xml";
        Store.Save(library);

        var path = Path.Combine(TempDir, "subs.opml");
        Assert.Equal(0, Opml.Export(Store, new StringWriter(), [path]));

        var xml = File.ReadAllText(path);
        Assert.Contains("https://example.com/systems.xml", xml);
        Assert.Contains("https://example.com/kernel.xml", xml);
        Assert.Contains("Systems Chats", xml);
    }

    [Fact]
    public async Task ImportSubscribesToEveryFeed()
    {
        var handler = new QueueHandler();
        handler.Enqueue(QueueHandler.Ok(SampleFeeds.Rss));
        handler.Enqueue(QueueHandler.Ok(SampleFeeds.Atom));
        var service = new PodcastService(handler);

        var opml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <opml version="2.0"><head><title>Subs</title></head><body>
              <outline type="rss" text="Systems Chats" xmlUrl="https://example.com/systems.xml" />
              <outline type="rss" text="Kernel Panic FM" xmlUrl="https://example.com/kernel.xml" />
            </body></opml>
            """;
        var path = Path.Combine(TempDir, "import.opml");
        File.WriteAllText(path, opml);

        var output = new StringWriter();
        Assert.Equal(0, await Opml.ImportAsync(Store, output, [path], service));
        var loaded = Store.Load();
        Assert.Equal(2, loaded.Podcasts.Count);
        Assert.Contains("Imported 2 feed(s)", output.ToString());
    }

    [Fact]
    public async Task ImportReportsMissingFile()
    {
        var service = new PodcastService(new QueueHandler());
        Assert.Equal(1, await Opml.ImportAsync(Store, new StringWriter(),
            [Path.Combine(TempDir, "nope.opml")], service));
    }
}

public sealed class CliTests : TempStoreFixture
{
    private Cli CliWith(StringWriter output, HttpMessageHandler? handler = null) =>
        new(Store, handler, output);

    [Fact]
    public async Task HelpAndUnknownCommands()
    {
        var output = new StringWriter();
        var cli = CliWith(output);
        Assert.Equal(0, await cli.RunAsync(["help"]));
        Assert.Contains("subscribe", output.ToString());
        Assert.Equal(2, await cli.RunAsync(["frobnicate"]));
    }

    [Fact]
    public async Task SubscribeListAndEpisodesEndToEnd()
    {
        var handler = new QueueHandler();
        handler.Enqueue(QueueHandler.Ok(SampleFeeds.Rss));
        var output = new StringWriter();
        var cli = CliWith(output, handler);

        Assert.Equal(0, await cli.RunAsync(["subscribe", "https://example.com/feed.xml"]));
        Assert.Contains("Systems Chats", output.ToString());

        output.GetStringBuilder().Clear();
        Assert.Equal(0, await cli.RunAsync(["list"]));
        Assert.Contains("Systems Chats", output.ToString());
        Assert.Contains("3 episode(s)", output.ToString());

        output.GetStringBuilder().Clear();
        var showId = Store.Load().Podcasts[0].Id;
        Assert.Equal(0, await cli.RunAsync(["episodes", showId]));
        Assert.Contains("Episode Three", output.ToString());
        Assert.Contains("1:02:03", output.ToString());
    }

    [Fact]
    public async Task SearchFindsEpisodesAcrossShows()
    {
        var handler = new QueueHandler();
        handler.Enqueue(QueueHandler.Ok(SampleFeeds.Rss));
        var output = new StringWriter();
        var cli = CliWith(output, handler);

        await cli.RunAsync(["subscribe", "https://example.com/feed.xml"]);
        output.GetStringBuilder().Clear();
        Assert.Equal(0, await cli.RunAsync(["search", "Consensus"]));
        Assert.Contains("Episode Three", output.ToString());

        output.GetStringBuilder().Clear();
        Assert.Equal(0, await cli.RunAsync(["search", "nothing matches this"]));
        Assert.Contains("No episodes match", output.ToString());
    }

    [Fact]
    public async Task StatsReportsLibraryTotals()
    {
        var handler = new QueueHandler();
        handler.Enqueue(QueueHandler.Ok(SampleFeeds.Rss));
        var output = new StringWriter();
        var cli = CliWith(output, handler);

        await cli.RunAsync(["subscribe", "https://example.com/feed.xml"]);
        output.GetStringBuilder().Clear();
        Assert.Equal(0, await cli.RunAsync(["stats"]));
        var text = output.ToString();
        Assert.Contains("Shows:       1", text);
        Assert.Contains("Episodes:    3", text);
        Assert.Contains("In queue:    0", text);
    }
}
