using PodCLI;

namespace PodCLI.Tests;

public sealed class FeedParserTests
{
    [Fact]
    public void ParsesRssChannelMetadata()
    {
        var feed = FeedParser.Parse(SampleFeeds.Rss);
        Assert.Equal("Systems Chats", feed.Title);
        Assert.Equal("https://example.com/systems", feed.SiteUrl);
        Assert.Equal("Conversations about distributed systems.", feed.Description);
        Assert.Equal("https://example.com/cover.jpg", feed.ImageUrl);
    }

    [Fact]
    public void ParsesRssEpisodesNewestFirstWithEnclosures()
    {
        var feed = FeedParser.Parse(SampleFeeds.Rss);
        Assert.Equal(3, feed.Episodes.Count); // duplicate guid dropped

        var newest = feed.Episodes[0];
        Assert.Equal("ep-3", newest.Guid);
        Assert.Equal("Episode Three", newest.Title);
        Assert.Equal("https://cdn.example.com/ep3.mp3", newest.AudioUrl);
        Assert.Equal("audio/mpeg", newest.AudioType);
        Assert.Equal(12345678, newest.AudioLengthBytes);
        Assert.Equal(3723, newest.DurationSeconds); // 1:02:03
        Assert.Equal(new DateTimeOffset(2026, 11, 16, 10, 0, 0, TimeSpan.Zero), newest.PublishedAt);
    }

    [Fact]
    public void ParsesRssDurationFormats()
    {
        var feed = FeedParser.Parse(SampleFeeds.Rss);
        Assert.Equal(2730, feed.Episodes[1].DurationSeconds); // 45:30
        Assert.Equal(3723, feed.Episodes[2].DurationSeconds); // plain seconds
    }

    [Fact]
    public void FallsBackToLinkWhenNoEnclosure()
    {
        var feed = FeedParser.Parse(SampleFeeds.Rss);
        var oldest = feed.Episodes[2];
        Assert.Equal("ep-1", oldest.Guid);
        Assert.Equal("https://cdn.example.com/ep1.mp3", oldest.AudioUrl);
    }

    [Fact]
    public void ParsesAtomFeed()
    {
        var feed = FeedParser.Parse(SampleFeeds.Atom);
        Assert.Equal("Kernel Panic FM", feed.Title);
        Assert.Equal("https://example.com/kernel", feed.SiteUrl);
        Assert.Equal(2, feed.Episodes.Count);

        var newest = feed.Episodes[0];
        Assert.Equal("atom-2", newest.Guid);
        Assert.Equal("https://cdn.example.com/k2.m4a", newest.AudioUrl);
        Assert.Equal(5550000, newest.AudioLengthBytes);
        Assert.Equal(new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.Zero), newest.PublishedAt);
    }

    [Fact]
    public void RejectsUnsupportedRootElement()
    {
        Assert.Throws<InvalidDataException>(() =>
            FeedParser.Parse("<html><body>not a feed</body></html>"));
    }

    [Fact]
    public void RejectsMalformedXml()
    {
        Assert.Throws<System.Xml.XmlException>(() =>
            FeedParser.Parse("<rss><channel><unclosed>"));
    }

    [Fact]
    public void DurationParserHandlesEdgeCases()
    {
        Assert.Null(FeedParser.ParseDuration(null));
        Assert.Null(FeedParser.ParseDuration(""));
        Assert.Null(FeedParser.ParseDuration("abc"));
        Assert.Null(FeedParser.ParseDuration("1:2:3:4"));
        Assert.Equal(0, FeedParser.ParseDuration("0"));
        Assert.Equal(61, FeedParser.ParseDuration("1:01"));
    }

    [Fact]
    public void DateParserHandlesRssAndAtomFormats()
    {
        Assert.Equal(new DateTimeOffset(2026, 11, 16, 10, 0, 0, TimeSpan.Zero),
            FeedParser.ParseDate("Mon, 16 Nov 2026 10:00:00 GMT"));
        Assert.Equal(new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.Zero),
            FeedParser.ParseDate("2026-11-16T09:00:00Z"));
        Assert.Null(FeedParser.ParseDate("not a date"));
        Assert.Null(FeedParser.ParseDate(null));
    }
}

public sealed class FeedFetcherTests
{
    [Fact]
    public async Task ReturnsBodyAndValidatorsOn200()
    {
        var handler = new QueueHandler();
        handler.Enqueue(QueueHandler.Ok(SampleFeeds.Rss, eTag: "\"v1\""));
        var fetcher = new FeedFetcher(handler);

        var result = await fetcher.FetchAsync("https://example.com/feed.xml", null, null);
        Assert.Equal(FetchStatus.Ok, result.Status);
        Assert.Equal(SampleFeeds.Rss, result.Xml);
        Assert.Equal("\"v1\"", result.ETag);
    }

    [Fact]
    public async Task SendsConditionalHeadersOnRefresh()
    {
        var handler = new QueueHandler();
        handler.Enqueue(QueueHandler.Status(System.Net.HttpStatusCode.NotModified));
        var fetcher = new FeedFetcher(handler);

        var result = await fetcher.FetchAsync("https://example.com/feed.xml", "\"v1\"", "Mon, 16 Nov 2026 10:00:00 GMT");
        Assert.Equal(FetchStatus.NotModified, result.Status);

        var request = handler.Requests[0];
        Assert.True(request.Headers.TryGetValues("If-None-Match", out var etag));
        Assert.Equal("\"v1\"", etag.First());
        Assert.True(request.Headers.TryGetValues("If-Modified-Since", out var modified));
        Assert.Equal("Mon, 16 Nov 2026 10:00:00 GMT", modified.First());
    }

    [Fact]
    public async Task ReportsNonSuccessStatusAsFailed()
    {
        var handler = new QueueHandler();
        handler.Enqueue(QueueHandler.Status(System.Net.HttpStatusCode.NotFound));
        var fetcher = new FeedFetcher(handler);

        var result = await fetcher.FetchAsync("https://example.com/feed.xml", null, null);
        Assert.Equal(FetchStatus.Failed, result.Status);
        Assert.Contains("404", result.Error);
    }

    [Fact]
    public async Task ReportsNetworkErrorsAsFailed()
    {
        var handler = new FailingHandler();
        var fetcher = new FeedFetcher(handler);

        var result = await fetcher.FetchAsync("https://example.com/feed.xml", null, null);
        Assert.Equal(FetchStatus.Failed, result.Status);
        Assert.Contains("Network error", result.Error);
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("connection refused");
    }
}
