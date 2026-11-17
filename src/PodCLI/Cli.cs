namespace PodCLI;

/// <summary>Argument parsing, output formatting, and exit codes. All state changes
/// go through <see cref="PodcastService"/> and <see cref="QueueManager"/> so this
/// class stays thin and testable (output is injected, HTTP is mocked).</summary>
public sealed class Cli
{
    private readonly Store _store;
    private readonly PodcastService _service;
    private readonly TextWriter _out;

    public Cli(Store store, PodcastService service, TextWriter? output = null)
    {
        _store = store;
        _service = service;
        _out = output ?? Console.Out;
    }

    public Cli(Store store, HttpMessageHandler? handler = null, TextWriter? output = null)
        : this(store, new PodcastService(handler), output)
    {
    }

    public async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            PrintHelp();
            return 0;
        }

        var command = args[0].ToLowerInvariant();
        var rest = args[1..];

        try
        {
            return command switch
            {
                "subscribe" => await SubscribeAsync(rest),
                "unsubscribe" => Unsubscribe(rest),
                "list" => List(),
                "refresh" => await RefreshAsync(rest),
                "episodes" => Episodes(rest),
                "download" => await DownloadAsync(rest),
                "search" => Search(rest),
                "stats" => Stats(),
                "queue" => await QueueAsync(rest),
                "play" => Play(rest),
                "progress" => Progress(rest),
                "mark-played" => MarkPlayed(rest),
                "import" => await ImportAsync(rest),
                "export" => Export(rest),
                _ => Unknown(command),
            };
        }
        catch (Exception ex)
        {
            _out.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    // ----- Subscriptions -----

    private async Task<int> SubscribeAsync(string[] args)
    {
        if (args.Length != 1)
        {
            _out.WriteLine("Usage: podcli subscribe <feed-url>");
            return 2;
        }

        var library = _store.Load();
        var result = await _service.SubscribeAsync(library, args[0]);
        if (result.Show is null)
        {
            _out.WriteLine($"Could not subscribe: {result.Error}");
            return 1;
        }

        _store.Save(library);
        _out.WriteLine($"Subscribed to \"{result.Show.Title}\" [{result.Show.Id}]");
        _out.WriteLine($"{result.EpisodesAdded} episode(s) found.");
        return 0;
    }

    private int Unsubscribe(string[] args)
    {
        if (args.Length != 1)
        {
            _out.WriteLine("Usage: podcli unsubscribe <show-id-or-title>");
            return 2;
        }

        var library = _store.Load();
        var show = library.FindPodcast(args[0]);
        if (show is null)
        {
            _out.WriteLine($"No show matches \"{args[0]}\".");
            return 1;
        }

        library.Podcasts.Remove(show);
        library.Queue.RemoveAll(q => q.PodcastId == show.Id);
        if (library.NowPlaying?.PodcastId == show.Id) library.NowPlaying = null;
        _store.Save(library);
        _out.WriteLine($"Unsubscribed from \"{show.Title}\".");
        return 0;
    }

    private int List()
    {
        var library = _store.Load();
        if (library.Podcasts.Count == 0)
        {
            _out.WriteLine("No subscriptions yet. Run: podcli subscribe <feed-url>");
            return 0;
        }

        foreach (var show in library.Podcasts)
        {
            var unplayed = show.Episodes.Count(e => !e.Played);
            var downloaded = show.Episodes.Count(e => e.Downloaded);
            _out.WriteLine($"[{show.Id}] {show.Title} — {show.Episodes.Count} episode(s), " +
                           $"{unplayed} unplayed, {downloaded} downloaded");
        }
        return 0;
    }

    private async Task<int> RefreshAsync(string[] args)
    {
        var library = _store.Load();
        Podcast? show = null;
        if (args.Length == 1)
        {
            show = library.FindPodcast(args[0]);
            if (show is null)
            {
                _out.WriteLine($"No show matches \"{args[0]}\".");
                return 1;
            }
        }
        else if (args.Length > 1)
        {
            _out.WriteLine("Usage: podcli refresh [show-id-or-title]");
            return 2;
        }

        var result = await _service.RefreshAsync(library, show);
        _store.Save(library);
        _out.WriteLine($"Refreshed {result.ShowsRefreshed} show(s), {result.NewEpisodes} new episode(s).");
        foreach (var error in result.Errors) _out.WriteLine($"  ! {error}");
        return result.Errors.Count == 0 ? 0 : 1;
    }

    // ----- Episodes -----

    private int Episodes(string[] args)
    {
        if (args.Length < 1 || args.Length > 2 || (args.Length == 2 && args[1] != "--unplayed"))
        {
            _out.WriteLine("Usage: podcli episodes <show-id-or-title> [--unplayed]");
            return 2;
        }

        var library = _store.Load();
        var show = library.FindPodcast(args[0]);
        if (show is null)
        {
            _out.WriteLine($"No show matches \"{args[0]}\".");
            return 1;
        }

        var unplayedOnly = args.Length == 2;
        var episodes = show.Episodes
            .Select((e, i) => (Episode: e, Number: i + 1))
            .Where(x => !unplayedOnly || !x.Episode.Played)
            .ToList();

        if (episodes.Count == 0)
        {
            _out.WriteLine(unplayedOnly ? "No unplayed episodes." : "No episodes yet — run `podcli refresh`.");
            return 0;
        }

        _out.WriteLine($"{show.Title} [{show.Id}]");
        foreach (var (episode, number) in episodes)
            _out.WriteLine($"  {number,3}. {FormatEpisode(episode)}");
        return 0;
    }

    private async Task<int> DownloadAsync(string[] args)
    {
        if (args.Length != 2 || !int.TryParse(args[1], out var number))
        {
            _out.WriteLine("Usage: podcli download <show-id-or-title> <episode-number>");
            return 2;
        }

        var library = _store.Load();
        var show = library.FindPodcast(args[0]);
        if (show is null)
        {
            _out.WriteLine($"No show matches \"{args[0]}\".");
            return 1;
        }

        var lastPercent = -1;
        var progress = new Progress<double>(fraction =>
        {
            var percent = (int)(fraction * 100);
            if (percent != lastPercent)
            {
                lastPercent = percent;
                _out.Write($"\rDownloading… {percent}%");
            }
        });

        var result = await _service.DownloadEpisodeAsync(_store, library, show, number, progress);
        _out.WriteLine();
        switch (result.Outcome)
        {
            case Downloader.DownloadOutcome.Downloaded:
                _store.Save(library);
                _out.WriteLine($"Downloaded \"{result.Episode!.Title}\"");
                _out.WriteLine($"  → {result.Episode.LocalPath}");
                return 0;
            case Downloader.DownloadOutcome.AlreadyDownloaded:
                _store.Save(library);
                _out.WriteLine($"Already downloaded: \"{result.Episode!.Title}\"");
                return 0;
            default:
                _out.WriteLine($"Download failed: {result.Error}");
                return 1;
        }
    }

    private int Search(string[] args)
    {
        if (args.Length == 0)
        {
            _out.WriteLine("Usage: podcli search <query>");
            return 2;
        }

        var query = string.Join(' ', args);
        var library = _store.Load();
        var hits = library.Podcasts
            .SelectMany(show => show.Episodes.Select((e, i) => (Show: show, Episode: e, Number: i + 1)))
            .Where(x => (x.Episode.Title + " " + (x.Episode.Description ?? ""))
                .Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (hits.Count == 0)
        {
            _out.WriteLine($"No episodes match \"{query}\".");
            return 0;
        }

        foreach (var (show, episode, number) in hits)
            _out.WriteLine($"[{show.Id}] #{number} {episode.Title} — {show.Title}");
        return 0;
    }

    private int Stats()
    {
        var library = _store.Load();
        var episodes = library.Podcasts.SelectMany(p => p.Episodes).ToList();
        var downloadedBytes = episodes
            .Where(e => e.Downloaded && e.LocalPath is not null && File.Exists(e.LocalPath))
            .Sum(e => new FileInfo(e.LocalPath!).Length);

        _out.WriteLine($"Shows:       {library.Podcasts.Count}");
        _out.WriteLine($"Episodes:    {episodes.Count} ({episodes.Count(e => !e.Played)} unplayed)");
        _out.WriteLine($"Downloaded:  {episodes.Count(e => e.Downloaded)} episode(s), {FormatBytes(downloadedBytes)} on disk");
        _out.WriteLine($"In queue:    {library.Queue.Count} episode(s)");
        return 0;
    }

    // ----- Queue / playback / OPML (stage 4) -----

    private Task<int> QueueAsync(string[] args) => Task.FromResult(QueueManager.Run(_store, _out, args));
    private int Play(string[] args) => QueueManager.Play(_store, _out, args);
    private int Progress(string[] args) => QueueManager.Progress(_store, _out, args);
    private int MarkPlayed(string[] args) => QueueManager.MarkPlayed(_store, _out, args);
    private Task<int> ImportAsync(string[] args) => Opml.ImportAsync(_store, _out, args, _service);
    private int Export(string[] args) => Opml.Export(_store, _out, args);

    // ----- Shared helpers -----

    internal static string FormatEpisode(Episode episode)
    {
        var date = episode.PublishedAt?.ToString("yyyy-MM-dd") ?? "undated";
        var duration = episode.DurationSeconds is null ? "" : $" [{FormatDuration(episode.DurationSeconds.Value)}]";
        var flags = (episode.Downloaded ? " ↓" : "") + (episode.Played ? " ✓" : "");
        return $"{episode.Title} ({date}){duration}{flags}";
    }

    internal static string FormatDuration(int totalSeconds)
    {
        var ts = TimeSpan.FromSeconds(totalSeconds);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes}:{ts.Seconds:D2}";
    }

    internal static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:F1} MB",
        _ => $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB",
    };

    private int Unknown(string command)
    {
        _out.WriteLine($"Unknown command: {command}");
        _out.WriteLine("Run `podcli help` for the command list.");
        return 2;
    }

    private void PrintHelp()
    {
        _out.WriteLine("""
            podcli — a CLI podcast manager

            Subscriptions
              podcli subscribe <feed-url>          Subscribe to an RSS/Atom podcast feed
              podcli unsubscribe <show>            Remove a subscription
              podcli list                          List subscribed shows
              podcli refresh [show]                Fetch new episodes (all shows, or one)

            Episodes
              podcli episodes <show> [--unplayed]  List episodes, newest first
              podcli download <show> <n>           Download episode number n
              podcli search <query>                Search episode titles and descriptions
              podcli stats                         Library totals

            Playback queue
              podcli queue add <show> <n>          Enqueue an episode
              podcli queue list                    Show the queue
              podcli queue next                    Show what plays next
              podcli queue remove <pos>            Remove queue position pos
              podcli queue move <from> <to>        Reorder the queue
              podcli queue clear                   Empty the queue
              podcli play <show> <n>               Mark an episode as now playing
              podcli progress <seconds>            Record playback position
              podcli mark-played <show> <n>        Mark an episode played

            OPML
              podcli import <file.opml>            Subscribe to every feed in an OPML file
              podcli export <file.opml>            Export subscriptions as OPML

            Options
              --data-dir <dir>                     Use a different library folder (default ~/.podcli)
            """);
    }
}
