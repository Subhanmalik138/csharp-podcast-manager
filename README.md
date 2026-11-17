# PodCLI — a CLI podcast manager

Subscribe to podcast feeds, download episodes, and keep a playback queue —
all from the terminal, with no account and no third-party service in the
middle. Feeds are plain RSS/Atom over HTTP; your library lives in one JSON
file under `~/.podcli`.

## What it does

- **Subscribe** to any RSS 2.0 or Atom podcast feed; episodes are listed
  newest-first with publish dates and durations.
- **Refresh** with conditional GET (ETag / Last-Modified), so unchanged feeds
  cost a single 304 instead of a full re-download. New episodes merge in;
  played and downloaded state is never lost.
- **Download** episodes with streaming writes and a progress display.
  Partial downloads never look finished, and re-downloading an episode you
  already have is a no-op.
- **Playback queue** with add / list / next / remove / move / clear, plus
  `play` and `progress` to record where you stopped (podcli has no built-in
  player — open the file or stream URL in your own player).
- **OPML import/export**, so subscriptions move freely between podcli and
  any other podcast app.
- **Search** episode titles and descriptions across all shows, and `stats`
  for library totals.

## Design decisions

1. **Zero dependencies in the app itself.** Feed parsing is `System.Xml.Linq`,
   HTTP is `HttpClient`, persistence is `System.Text.Json`. Nothing to
   install beyond the .NET 8 runtime, and nothing in the supply chain that
   can break a podcast client.
2. **One JSON file, written atomically.** The whole library (shows, episodes,
   queue, now-playing) is a single `library.json`. Saves go to a temp file
   and are moved over the old one, so a crash mid-write can never corrupt
   the library.
3. **Feed identity is the guid, not the position.** Episodes are matched by
   their feed-provided guid when merging refreshes, so a reordered or
   re-dated feed cannot duplicate episodes or wipe your played state.
4. **HTTP is injectable everywhere.** `FeedFetcher` and `Downloader` both
   take an `HttpMessageHandler`, which is what lets the whole suite — feeds,
   conditional GET, downloads, OPML import — run offline against canned
   responses.

## Usage

```bash
dotnet run --project src/PodCLI -- subscribe https://example.com/feed.xml
podcli list
podcli refresh                       # all shows, or: podcli refresh <show>
podcli episodes <show>               # newest first; add --unplayed to filter
podcli download <show> 1             # episode 1 = newest
podcli queue add <show> 1
podcli queue list
podcli play <show> 1                 # marks now playing, prints file/stream
podcli progress 600                  # record 10:00 into the current episode
podcli mark-played <show> 1
podcli search consensus
podcli stats
podcli export subs.opml
podcli import subs.opml
```

Shows are addressed by their short id (`[abc123…]` in `list` output) or by
any unique part of their title. Episode numbers are 1-based, newest first.
Downloads land in `~/.podcli/downloads/<show-id>/`.

Use `--data-dir <dir>` (before the command) or set `PODCLI_DATA_DIR` to keep
the library somewhere else.

## Build and test

```bash
dotnet build
dotnet test        # xUnit; 45 tests
```

Note: `dotnet test` needs vstest to reach testhost; in sandboxed environments
where that connection is blocked, run the suite on a normal machine instead —
the tests are ordinary xUnit `[Fact]`s with no special runner.

## Requirements

- .NET 8 SDK to build, .NET 8 runtime to run.

## License

MIT — see [LICENSE](LICENSE).
