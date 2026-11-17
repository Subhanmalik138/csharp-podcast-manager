using System.Xml.Linq;

namespace PodCLI;

/// <summary>
/// OPML import/export (the format every podcast app uses for subscription
/// lists), so a podcli library can move to and from other clients.
/// </summary>
public static class Opml
{
    /// <summary>Subscribe to every feed listed in an OPML file.</summary>
    public static async Task<int> ImportAsync(Store store, TextWriter output, string[] args, PodcastService service)
    {
        if (args.Length != 1)
        {
            output.WriteLine("Usage: podcli import <file.opml>");
            return 2;
        }

        var path = args[0];
        if (!File.Exists(path))
        {
            output.WriteLine($"File not found: {path}");
            return 1;
        }

        List<string> feedUrls;
        try
        {
            var doc = XDocument.Load(path);
            feedUrls = doc.Descendants("outline")
                .Select(o => (string?)o.Attribute("xmlUrl"))
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .Select(u => u!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or InvalidOperationException)
        {
            output.WriteLine($"Could not parse OPML file: {ex.Message}");
            return 1;
        }

        if (feedUrls.Count == 0)
        {
            output.WriteLine("No feeds found in that OPML file.");
            return 0;
        }

        var library = store.Load();
        var added = 0;
        var failed = 0;
        foreach (var url in feedUrls)
        {
            var result = await service.SubscribeAsync(library, url);
            if (result.Show is null)
            {
                output.WriteLine($"  ! {url}: {result.Error}");
                failed++;
            }
            else
            {
                added++;
            }
        }

        store.Save(library);
        output.WriteLine($"Imported {added} feed(s), {failed} failed.");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>Write every subscription to an OPML file.</summary>
    public static int Export(Store store, TextWriter output, string[] args)
    {
        if (args.Length != 1)
        {
            output.WriteLine("Usage: podcli export <file.opml>");
            return 2;
        }

        var library = store.Load();
        var doc = new XDocument(
            new XElement("opml",
                new XAttribute("version", "2.0"),
                new XElement("head", new XElement("title", "podcli subscriptions")),
                new XElement("body",
                    library.Podcasts.Select(show => new XElement("outline",
                        new XAttribute("type", "rss"),
                        new XAttribute("text", show.Title),
                        new XAttribute("title", show.Title),
                        new XAttribute("xmlUrl", show.FeedUrl),
                        new XAttribute("htmlUrl", show.SiteUrl ?? show.FeedUrl))))));

        doc.Save(args[0]);
        output.WriteLine($"Exported {library.Podcasts.Count} subscription(s) to {args[0]}.");
        return 0;
    }
}
