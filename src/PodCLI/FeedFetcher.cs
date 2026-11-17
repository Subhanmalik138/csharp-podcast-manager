namespace PodCLI;

public enum FetchStatus
{
    /// <summary>200 OK — body contains fresh feed XML.</summary>
    Ok,
    /// <summary>304 Not Modified — the feed has not changed since the last refresh.</summary>
    NotModified,
    /// <summary>Anything else: network error, non-2xx/304 status, or bad content.</summary>
    Failed,
}

/// <summary>Result of one conditional feed fetch.</summary>
public sealed class FetchResult
{
    public required FetchStatus Status { get; init; }
    public string? Xml { get; init; }
    public string? ETag { get; init; }
    public string? LastModified { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Fetches podcast feeds over HTTP with conditional GET: the ETag and
/// Last-Modified validators from the last refresh are sent back, so a feed
/// that has not changed costs one 304 instead of a full re-download.
/// The HttpMessageHandler is injectable so tests can run offline.
/// </summary>
public sealed class FeedFetcher
{
    private const string UserAgent =
        "PodCLI/1.0 (+https://github.com/Subhanmalik138/csharp-podcast-manager)";

    private readonly HttpClient _http;

    public FeedFetcher(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(30);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/rss+xml, application/atom+xml, application/xml, text/xml, */*");
    }

    public async Task<FetchResult> FetchAsync(
        string feedUrl, string? etag, string? lastModified, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, feedUrl);
        if (!string.IsNullOrWhiteSpace(etag))
            request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        if (!string.IsNullOrWhiteSpace(lastModified))
            request.Headers.TryAddWithoutValidation("If-Modified-Since", lastModified);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            return new FetchResult { Status = FetchStatus.Failed, Error = $"Network error: {ex.Message}" };
        }
        catch (TaskCanceledException)
        {
            return new FetchResult { Status = FetchStatus.Failed, Error = "Request timed out after 30s" };
        }

        using (response)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
                return new FetchResult { Status = FetchStatus.NotModified };

            if (!response.IsSuccessStatusCode)
                return new FetchResult
                {
                    Status = FetchStatus.Failed,
                    Error = $"Feed server returned {(int)response.StatusCode} {response.ReasonPhrase}",
                };

            var xml = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(xml))
                return new FetchResult { Status = FetchStatus.Failed, Error = "Feed server returned an empty body" };

            return new FetchResult
            {
                Status = FetchStatus.Ok,
                Xml = xml,
                ETag = response.Headers.ETag?.Tag,
                LastModified = response.Content.Headers.LastModified?.ToString("R"),
            };
        }
    }
}
