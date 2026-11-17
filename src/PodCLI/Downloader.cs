namespace PodCLI;

/// <summary>
/// Downloads episode audio with a streaming GET: the file is written to a
/// temp file while bytes arrive, progress is reported as a 0–1 fraction, and
/// the temp file is moved into place only when the download completes, so a
/// half-downloaded episode never looks finished. Re-downloads are skipped
/// when the file already exists with the expected size.
/// </summary>
public sealed class Downloader
{
    private readonly HttpClient _http;

    public Downloader(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromMinutes(30);
    }

    public enum DownloadOutcome
    {
        Downloaded,
        AlreadyDownloaded,
        Failed,
    }

    public sealed record DownloadResult(DownloadOutcome Outcome, string? LocalPath, string? Error);

    public async Task<DownloadResult> DownloadAsync(
        Episode episode, string folder, IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(episode.AudioUrl))
            return new DownloadResult(DownloadOutcome.Failed, null, "Episode has no audio URL");

        Directory.CreateDirectory(folder);
        var fileName = Store.EpisodeFileName(episode);
        var target = Path.Combine(folder, fileName);

        if (File.Exists(target) && episode.AudioLengthBytes is long expected && expected > 0)
        {
            if (new FileInfo(target).Length == expected)
                return new DownloadResult(DownloadOutcome.AlreadyDownloaded, target, null);
        }
        else if (File.Exists(target) && episode.AudioLengthBytes is null)
        {
            return new DownloadResult(DownloadOutcome.AlreadyDownloaded, target, null);
        }

        var temp = target + ".part-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            using var response = await _http.GetAsync(
                episode.AudioUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                return new DownloadResult(DownloadOutcome.Failed, null,
                    $"Audio server returned {(int)response.StatusCode} {response.ReasonPhrase}");

            var total = response.Content.Headers.ContentLength
                ?? episode.AudioLengthBytes;
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            await using var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

            var buffer = new byte[81920];
            long received = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
                received += read;
                if (total is > 0) progress?.Report((double)received / total.Value);
            }

            File.Move(temp, target, overwrite: true);
            progress?.Report(1.0);
            return new DownloadResult(DownloadOutcome.Downloaded, target, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
            return new DownloadResult(DownloadOutcome.Failed, null, ex.Message);
        }
    }
}
