namespace PodCLI.Tests;

/// <summary>Canned feeds and a mock HTTP handler shared by the test classes.</summary>
public static class SampleFeeds
{
    public const string Rss = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0" xmlns:itunes="http://www.itunes.com/dtds/podcast-1.0.dtd">
          <channel>
            <title>Systems Chats</title>
            <link>https://example.com/systems</link>
            <description>Conversations about distributed systems.</description>
            <image><url>https://example.com/cover.jpg</url></image>
            <item>
              <title>Episode Three</title>
              <description>Consensus, finally.</description>
              <guid>ep-3</guid>
              <pubDate>Mon, 16 Nov 2026 10:00:00 GMT</pubDate>
              <enclosure url="https://cdn.example.com/ep3.mp3" type="audio/mpeg" length="12345678" />
              <itunes:duration>1:02:03</itunes:duration>
            </item>
            <item>
              <title>Episode Two</title>
              <guid>ep-2</guid>
              <pubDate>Sun, 15 Nov 2026 10:00:00 GMT</pubDate>
              <enclosure url="https://cdn.example.com/ep2.mp3" type="audio/mpeg" length="8765432" />
              <itunes:duration>45:30</itunes:duration>
            </item>
            <item>
              <title>Episode One</title>
              <guid>ep-1</guid>
              <pubDate>Sat, 14 Nov 2026 10:00:00 GMT</pubDate>
              <link>https://cdn.example.com/ep1.mp3</link>
              <itunes:duration>3723</itunes:duration>
            </item>
            <item>
              <title>Episode Three (duplicate)</title>
              <guid>ep-3</guid>
              <pubDate>Mon, 16 Nov 2026 10:00:00 GMT</pubDate>
              <enclosure url="https://cdn.example.com/ep3.mp3" type="audio/mpeg" length="12345678" />
            </item>
          </channel>
        </rss>
        """;

    public const string Atom = """
        <?xml version="1.0" encoding="utf-8"?>
        <feed xmlns="http://www.w3.org/2005/Atom">
          <title>Kernel Panic FM</title>
          <subtitle>Operating systems, weekly.</subtitle>
          <link rel="alternate" href="https://example.com/kernel" />
          <entry>
            <title>Second Boot</title>
            <id>atom-2</id>
            <updated>2026-11-16T09:00:00Z</updated>
            <summary>Schedulers and regret.</summary>
            <link rel="enclosure" href="https://cdn.example.com/k2.m4a" type="audio/mp4" length="5550000" />
          </entry>
          <entry>
            <title>First Boot</title>
            <id>atom-1</id>
            <published>2026-11-09T09:00:00Z</published>
            <summary>Why boot at all.</summary>
            <link rel="enclosure" href="https://cdn.example.com/k1.m4a" type="audio/mp4" length="4440000" />
          </entry>
        </feed>
        """;

    /// <summary>RSS with one extra episode, for refresh-merge tests.</summary>
    public static string RssWithNewEpisode
    {
        get
        {
            const string marker = "<title>Episode Two</title>";
            var idx = Rss.IndexOf(marker, StringComparison.Ordinal);
            var itemStart = Rss.LastIndexOf("<item>", idx, StringComparison.Ordinal);
            const string newItem = """
                <item>
                  <title>Episode Four</title>
                  <guid>ep-4</guid>
                  <pubDate>Tue, 17 Nov 2026 10:00:00 GMT</pubDate>
                  <enclosure url="https://cdn.example.com/ep4.mp3" type="audio/mpeg" length="9999999" />
                  <itunes:duration>30:00</itunes:duration>
                </item>
                """;
            return Rss[..itemStart] + newItem + Rss[itemStart..];
        }
    }
}

/// <summary>Returns canned responses in order, capturing the requests sent.</summary>
public sealed class QueueHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new();
    public List<HttpRequestMessage> Requests { get; } = new();

    public void Enqueue(HttpResponseMessage response) => _responses.Enqueue(response);

    public static HttpResponseMessage Ok(string body, string? eTag = null, long? length = null)
    {
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(body),
        };
        if (eTag is not null) response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(eTag);
        if (length is not null) response.Content.Headers.ContentLength = length;
        return response;
    }

    public static HttpResponseMessage Bytes(byte[] data)
    {
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(data),
        };
        response.Content.Headers.ContentLength = data.Length;
        return response;
    }

    public static HttpResponseMessage Status(System.Net.HttpStatusCode code) =>
        new(code);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        return Task.FromResult(_responses.Dequeue());
    }
}

/// <summary>Synchronous IProgress: records values on the calling thread.</summary>
public sealed class SyncProgress : IProgress<double>
{
    public List<double> Values { get; } = new();
    public void Report(double value) => Values.Add(value);
}

/// <summary>Temp-directory fixture: a fresh Store per test, cleaned up afterwards.</summary>
public abstract class TempStoreFixture : IDisposable
{
    protected readonly string TempDir;
    protected readonly Store Store;

    protected TempStoreFixture()
    {
        TempDir = Path.Combine(Path.GetTempPath(), "podcli-tests-" + Guid.NewGuid().ToString("N")[..12]);
        Store = new Store(TempDir);
    }

    protected static Podcast FakeShow(string id = "abc123", string title = "Test Show") => new()
    {
        Id = id,
        Title = title,
        FeedUrl = "https://example.com/feed.xml",
        Episodes =
        [
            new Episode { Guid = "e1", Title = "Newest", PublishedAt = new DateTimeOffset(2026, 11, 17, 0, 0, 0, TimeSpan.Zero), AudioUrl = "https://cdn.example.com/e1.mp3", DurationSeconds = 1800 },
            new Episode { Guid = "e2", Title = "Middle", PublishedAt = new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero), AudioUrl = "https://cdn.example.com/e2.mp3", DurationSeconds = 900 },
            new Episode { Guid = "e3", Title = "Oldest", PublishedAt = new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero), AudioUrl = "https://cdn.example.com/e3.mp3", DurationSeconds = 600 },
        ],
    };

    public void Dispose()
    {
        try { Directory.Delete(TempDir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }
}
