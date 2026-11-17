namespace PodCLI;

/// <summary>
/// The playback queue: an ordered list of (show, episode) pairs plus the
/// episode currently marked as playing. Queue entries are resolved against
/// the library at read time, so unsubscribing a show drops its queued
/// episodes automatically (the CLI also prunes them eagerly).
/// </summary>
public static class QueueManager
{
    public static int Run(Store store, TextWriter output, string[] args)
    {
        if (args.Length == 0)
        {
            output.WriteLine("Usage: podcli queue <add|list|next|remove|move|clear> …");
            return 2;
        }

        return args[0].ToLowerInvariant() switch
        {
            "add" => Add(store, output, args[1..]),
            "list" => List(store, output),
            "next" => Next(store, output),
            "remove" => Remove(store, output, args[1..]),
            "move" => Move(store, output, args[1..]),
            "clear" => Clear(store, output),
            _ => Unknown(output, args[0]),
        };
    }

    private static int Add(Store store, TextWriter output, string[] args)
    {
        if (args.Length != 2 || !int.TryParse(args[1], out var number))
        {
            output.WriteLine("Usage: podcli queue add <show-id-or-title> <episode-number>");
            return 2;
        }

        var library = store.Load();
        var show = library.FindPodcast(args[0]);
        if (show is null)
        {
            output.WriteLine($"No show matches \"{args[0]}\".");
            return 1;
        }
        if (number < 1 || number > show.Episodes.Count)
        {
            output.WriteLine($"Episode number must be between 1 and {show.Episodes.Count}.");
            return 1;
        }

        var episode = show.Episodes[number - 1];
        if (library.Queue.Any(q => q.PodcastId == show.Id && q.EpisodeGuid == episode.Guid))
        {
            output.WriteLine($"\"{episode.Title}\" is already in the queue.");
            return 0;
        }

        library.Queue.Add(new QueueItem { PodcastId = show.Id, EpisodeGuid = episode.Guid });
        store.Save(library);
        output.WriteLine($"Queued #{library.Queue.Count}: \"{episode.Title}\" ({show.Title})");
        return 0;
    }

    private static int List(Store store, TextWriter output)
    {
        var library = store.Load();
        var rows = Resolve(library);
        if (rows.Count == 0)
        {
            output.WriteLine("Queue is empty. Run: podcli queue add <show> <n>");
            return 0;
        }

        for (var i = 0; i < rows.Count; i++)
        {
            var (show, episode) = rows[i];
            var marker = library.NowPlaying?.PodcastId == show.Id
                && library.NowPlaying?.EpisodeGuid == episode.Guid ? " ▶ now playing" : "";
            output.WriteLine($"  {i + 1,3}. {episode.Title} — {show.Title}{marker}");
        }
        return 0;
    }

    private static int Next(Store store, TextWriter output)
    {
        var library = store.Load();
        var rows = Resolve(library);
        if (rows.Count == 0)
        {
            output.WriteLine("Queue is empty.");
            return 0;
        }

        var (show, episode) = rows[0];
        output.WriteLine($"Up next: \"{episode.Title}\" — {show.Title}");
        if (episode.LocalPath is not null) output.WriteLine($"  File: {episode.LocalPath}");
        else if (episode.AudioUrl is not null) output.WriteLine($"  Stream: {episode.AudioUrl}");
        return 0;
    }

    private static int Remove(Store store, TextWriter output, string[] args)
    {
        if (args.Length != 1 || !int.TryParse(args[0], out var position))
        {
            output.WriteLine("Usage: podcli queue remove <position>");
            return 2;
        }

        var library = store.Load();
        if (position < 1 || position > library.Queue.Count)
        {
            output.WriteLine($"Position must be between 1 and {library.Queue.Count}.");
            return 1;
        }

        var removed = library.Queue[position - 1];
        library.Queue.RemoveAt(position - 1);
        store.Save(library);
        output.WriteLine($"Removed queue position {position}.");
        return 0;
    }

    private static int Move(Store store, TextWriter output, string[] args)
    {
        if (args.Length != 2 || !int.TryParse(args[0], out var from) || !int.TryParse(args[1], out var to))
        {
            output.WriteLine("Usage: podcli queue move <from> <to>");
            return 2;
        }

        var library = store.Load();
        if (from < 1 || from > library.Queue.Count || to < 1 || to > library.Queue.Count)
        {
            output.WriteLine($"Positions must be between 1 and {library.Queue.Count}.");
            return 1;
        }

        var item = library.Queue[from - 1];
        library.Queue.RemoveAt(from - 1);
        library.Queue.Insert(to - 1, item);
        store.Save(library);
        output.WriteLine($"Moved queue position {from} to {to}.");
        return 0;
    }

    private static int Clear(Store store, TextWriter output)
    {
        var library = store.Load();
        var count = library.Queue.Count;
        library.Queue.Clear();
        library.NowPlaying = null;
        store.Save(library);
        output.WriteLine($"Cleared {count} queued episode(s).");
        return 0;
    }

    /// <summary>Mark an episode as now playing and print where to listen to it.</summary>
    public static int Play(Store store, TextWriter output, string[] args)
    {
        if (args.Length != 2 || !int.TryParse(args[1], out var number))
        {
            output.WriteLine("Usage: podcli play <show-id-or-title> <episode-number>");
            return 2;
        }

        var library = store.Load();
        var show = library.FindPodcast(args[0]);
        if (show is null)
        {
            output.WriteLine($"No show matches \"{args[0]}\".");
            return 1;
        }
        if (number < 1 || number > show.Episodes.Count)
        {
            output.WriteLine($"Episode number must be between 1 and {show.Episodes.Count}.");
            return 1;
        }

        var episode = show.Episodes[number - 1];
        library.NowPlaying = new QueueItem { PodcastId = show.Id, EpisodeGuid = episode.Guid };
        store.Save(library);

        output.WriteLine($"Now playing: \"{episode.Title}\" — {show.Title}");
        if (episode.PositionSeconds > 0)
            output.WriteLine($"  Resuming from {Cli.FormatDuration(episode.PositionSeconds)}.");
        if (episode.Downloaded && episode.LocalPath is not null)
            output.WriteLine($"  File: {episode.LocalPath}");
        else if (episode.AudioUrl is not null)
            output.WriteLine($"  Stream: {episode.AudioUrl}");
        output.WriteLine("  (podcli has no built-in player — open the file or stream in your player,");
        output.WriteLine("   then record where you stopped with: podcli progress <seconds>)");
        return 0;
    }

    /// <summary>Record the playback position of the now-playing episode.</summary>
    public static int Progress(Store store, TextWriter output, string[] args)
    {
        if (args.Length != 1 || !int.TryParse(args[0], out var seconds) || seconds < 0)
        {
            output.WriteLine("Usage: podcli progress <seconds>");
            return 2;
        }

        var library = store.Load();
        if (library.NowPlaying is null)
        {
            output.WriteLine("Nothing is playing. Run: podcli play <show> <n>");
            return 1;
        }

        var episode = FindEpisode(library, library.NowPlaying);
        if (episode is null)
        {
            output.WriteLine("The now-playing episode is no longer in the library.");
            return 1;
        }

        episode.PositionSeconds = seconds;
        if (episode.DurationSeconds is int duration && seconds >= duration)
        {
            episode.Played = true;
            episode.PositionSeconds = 0;
            library.NowPlaying = null;
            library.Queue.RemoveAll(q => q.EpisodeGuid == episode.Guid);
            store.Save(library);
            output.WriteLine($"Finished \"{episode.Title}\" — marked played.");
            return 0;
        }

        store.Save(library);
        output.WriteLine($"Position saved: {Cli.FormatDuration(seconds)} into \"{episode.Title}\".");
        return 0;
    }

    /// <summary>Mark an episode played (and drop it from the queue if present).</summary>
    public static int MarkPlayed(Store store, TextWriter output, string[] args)
    {
        if (args.Length != 2 || !int.TryParse(args[1], out var number))
        {
            output.WriteLine("Usage: podcli mark-played <show-id-or-title> <episode-number>");
            return 2;
        }

        var library = store.Load();
        var show = library.FindPodcast(args[0]);
        if (show is null)
        {
            output.WriteLine($"No show matches \"{args[0]}\".");
            return 1;
        }
        if (number < 1 || number > show.Episodes.Count)
        {
            output.WriteLine($"Episode number must be between 1 and {show.Episodes.Count}.");
            return 1;
        }

        var episode = show.Episodes[number - 1];
        episode.Played = true;
        episode.PositionSeconds = 0;
        library.Queue.RemoveAll(q => q.PodcastId == show.Id && q.EpisodeGuid == episode.Guid);
        if (library.NowPlaying?.EpisodeGuid == episode.Guid) library.NowPlaying = null;
        store.Save(library);
        output.WriteLine($"Marked played: \"{episode.Title}\".");
        return 0;
    }

    /// <summary>Queue entries resolved to (show, episode) pairs; stale entries are dropped.</summary>
    internal static List<(Podcast Show, Episode Episode)> Resolve(Library library)
    {
        var rows = new List<(Podcast, Episode)>();
        foreach (var item in library.Queue)
        {
            var episode = FindEpisode(library, item);
            if (episode is null) continue;
            var show = library.Podcasts.First(p => p.Id == item.PodcastId);
            rows.Add((show, episode));
        }
        return rows;
    }

    private static Episode? FindEpisode(Library library, QueueItem item) =>
        library.Podcasts.FirstOrDefault(p => p.Id == item.PodcastId)
            ?.Episodes.FirstOrDefault(e => e.Guid == item.EpisodeGuid);

    private static int Unknown(TextWriter output, string subcommand)
    {
        output.WriteLine($"Unknown queue command: {subcommand}");
        output.WriteLine("Usage: podcli queue <add|list|next|remove|move|clear> …");
        return 2;
    }
}
