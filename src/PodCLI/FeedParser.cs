using System.Globalization;
using System.Xml.Linq;

namespace PodCLI;

/// <summary>What a parsed feed looks like, before it is merged into a subscription.</summary>
public sealed class ParsedFeed
{
    public string Title { get; set; } = "";
    public string? SiteUrl { get; set; }
    public string? Description { get; set; }
    public string? ImageUrl { get; set; }
    public List<Episode> Episodes { get; set; } = new();
}

/// <summary>
/// Zero-dependency RSS 2.0 and Atom feed parser, built on System.Xml.Linq.
/// Handles the fields podcast clients actually need: enclosure audio,
/// iTunes duration, guids, and publish dates. Unknown elements are ignored,
/// so malformed-but-readable feeds still parse.
/// </summary>
public static class FeedParser
{
    public static ParsedFeed Parse(string xml)
    {
        var doc = XDocument.Parse(xml);
        var root = doc.Root ?? throw new InvalidDataException("Feed has no root element");

        return root.Name.LocalName switch
        {
            "rss" => ParseRss(root),
            "feed" => ParseAtom(root),
            _ => throw new InvalidDataException(
                $"Unsupported feed format: root element <{root.Name.LocalName}> (expected <rss> or <feed>)"),
        };
    }

    // ----- RSS 2.0 -----

    private static ParsedFeed ParseRss(XElement root)
    {
        var channel = root.Element("channel")
            ?? throw new InvalidDataException("RSS feed has no <channel> element");

        var feed = new ParsedFeed
        {
            Title = ChildText(channel, "title") ?? "Untitled podcast",
            SiteUrl = ChildText(channel, "link"),
            Description = ChildText(channel, "description") ?? ChildText(channel, "summary"),
            ImageUrl = channel.Element("image")?.Element("url")?.Value,
        };

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in channel.Elements("item"))
        {
            var episode = ParseRssItem(item);
            if (episode is null) continue;
            if (!seen.Add(episode.Guid)) continue; // same episode twice in one feed
            feed.Episodes.Add(episode);
        }

        SortNewestFirst(feed);
        return feed;
    }

    private static Episode? ParseRssItem(XElement item)
    {
        var enclosure = item.Element("enclosure");
        var guid = ChildText(item, "guid");
        var link = ChildText(item, "link");
        var audioUrl = enclosure?.Attribute("url")?.Value ?? link;
        if (string.IsNullOrWhiteSpace(audioUrl)) return null;

        return new Episode
        {
            Guid = string.IsNullOrWhiteSpace(guid) ? audioUrl : guid.Trim(),
            Title = ChildText(item, "title") ?? "Untitled episode",
            Description = ChildText(item, "description") ?? ChildText(item, "summary"),
            PublishedAt = ParseDate(ChildText(item, "pubDate")),
            AudioUrl = audioUrl,
            AudioType = enclosure?.Attribute("type")?.Value,
            AudioLengthBytes = ParseLong(enclosure?.Attribute("length")?.Value),
            DurationSeconds = ParseDuration(ChildText(item, "duration")),
        };
    }

    // ----- Atom -----

    private static ParsedFeed ParseAtom(XElement root)
    {
        var ns = root.GetDefaultNamespace();
        var feed = new ParsedFeed
        {
            Title = ChildText(root, "title", ns) ?? "Untitled podcast",
            SiteUrl = LinkHref(root, "alternate", ns),
            Description = ChildText(root, "subtitle", ns),
            ImageUrl = ChildText(root, "logo", ns) ?? ChildText(root, "icon", ns),
        };

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in root.Elements(ns + "entry"))
        {
            var episode = ParseAtomEntry(entry, ns);
            if (episode is null) continue;
            if (!seen.Add(episode.Guid)) continue;
            feed.Episodes.Add(episode);
        }

        SortNewestFirst(feed);
        return feed;
    }

    private static Episode? ParseAtomEntry(XElement entry, XNamespace ns)
    {
        var id = ChildText(entry, "id", ns);
        var audioUrl = LinkHref(entry, "enclosure", ns);
        if (string.IsNullOrWhiteSpace(audioUrl)) return null;

        var enclosure = entry.Elements(ns + "link")
            .FirstOrDefault(l => (string?)l.Attribute("rel") == "enclosure");

        return new Episode
        {
            Guid = string.IsNullOrWhiteSpace(id) ? audioUrl : id.Trim(),
            Title = ChildText(entry, "title", ns) ?? "Untitled episode",
            Description = ChildText(entry, "summary", ns) ?? ChildText(entry, "content", ns),
            PublishedAt = ParseDate(ChildText(entry, "published", ns))
                ?? ParseDate(ChildText(entry, "updated", ns)),
            AudioUrl = audioUrl,
            AudioType = (string?)enclosure?.Attribute("type"),
            AudioLengthBytes = ParseLong((string?)enclosure?.Attribute("length")),
        };
    }

    // ----- Helpers -----

    private static void SortNewestFirst(ParsedFeed feed) =>
        feed.Episodes.Sort((a, b) =>
            Nullable.Compare(b.PublishedAt, a.PublishedAt));

    /// <summary>Direct child by local name, ignoring namespaces (covers itunes:duration etc.).</summary>
    private static string? ChildText(XElement parent, string localName, XNamespace? ns = null)
    {
        var el = ns is null
            ? parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName)
            : parent.Element(ns + localName);
        var text = el?.Value.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string? LinkHref(XElement parent, string rel, XNamespace ns)
    {
        var link = parent.Elements(ns + "link")
            .FirstOrDefault(l => (string?)l.Attribute("rel") == rel
                || (rel == "alternate" && l.Attribute("rel") is null));
        return (string?)link?.Attribute("href");
    }

    internal static DateTimeOffset? ParseDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        // TryParse handles both RFC 1123 (RSS pubDate) and ISO 8601 (Atom).
        if (DateTimeOffset.TryParse(text.Trim(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var parsed))
            return parsed;
        return null;
    }

    /// <summary>Accepts "3723", "62:03" and "1:02:03". Returns null when unparseable.</summary>
    internal static int? ParseDuration(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        if (int.TryParse(text, out var seconds)) return seconds;

        var parts = text.Split(':');
        if (parts.Length is < 2 or > 3) return null;
        if (!parts.All(p => int.TryParse(p, out _))) return null;

        var nums = parts.Select(int.Parse).ToArray();
        return nums.Length == 2
            ? nums[0] * 60 + nums[1]
            : nums[0] * 3600 + nums[1] * 60 + nums[2];
    }

    private static long? ParseLong(string? text) =>
        long.TryParse(text, out var value) ? value : null;
}
