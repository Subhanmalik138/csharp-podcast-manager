using System.Text.Json;

namespace PodCLI;

/// <summary>
/// Persists the library as a single JSON file. Writes are atomic: the new
/// content goes to a temp file in the same directory and is then moved over
/// the old file, so a crash mid-write can never leave a half-written library.
/// </summary>
public sealed class Store
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public string DataDir { get; }
    public string LibraryPath => Path.Combine(DataDir, "library.json");
    public string DownloadsDir => Path.Combine(DataDir, "downloads");

    public Store(string dataDir)
    {
        DataDir = dataDir;
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(DownloadsDir);
    }

    /// <summary>Default location: ~/.podcli, overridable with PODCLI_DATA_DIR.</summary>
    public static Store Default()
    {
        var dir = Environment.GetEnvironmentVariable("PODCLI_DATA_DIR");
        if (string.IsNullOrWhiteSpace(dir))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            dir = Path.Combine(home, ".podcli");
        }
        return new Store(dir);
    }

    public Library Load()
    {
        if (!File.Exists(LibraryPath)) return new Library();
        var json = File.ReadAllText(LibraryPath);
        return JsonSerializer.Deserialize<Library>(json, JsonOptions) ?? new Library();
    }

    public void Save(Library library)
    {
        var json = JsonSerializer.Serialize(library, JsonOptions);
        var temp = LibraryPath + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllText(temp, json);
        File.Move(temp, LibraryPath, overwrite: true);
    }

    /// <summary>Per-show download folder, named after the show id.</summary>
    public string ShowDownloadsDir(Podcast show) =>
        Path.Combine(DownloadsDir, Sanitize(show.Id));

    /// <summary>Filesystem-safe file name for an episode download.</summary>
    public static string EpisodeFileName(Episode episode)
    {
        var ext = ".mp3";
        if (!string.IsNullOrEmpty(episode.AudioUrl))
        {
            var path = episode.AudioUrl.Split('?')[0];
            var dot = path.LastIndexOf('.');
            if (dot >= 0 && path.Length - dot is >= 2 and <= 5)
            {
                var candidate = path[dot..].ToLowerInvariant();
                if (candidate is ".mp3" or ".m4a" or ".ogg" or ".opus" or ".wav" or ".flac")
                    ext = candidate;
            }
        }

        var stamp = episode.PublishedAt?.ToString("yyyy-MM-dd") ?? "undated";
        var title = Sanitize(episode.Title);
        if (title.Length > 80) title = title[..80].TrimEnd('-');
        return $"{stamp}-{title}{ext}";
    }

    internal static string Sanitize(string value)
    {
        var cleaned = new string(value.Select(c =>
            char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-').ToArray());
        while (cleaned.Contains("--")) cleaned = cleaned.Replace("--", "-");
        cleaned = cleaned.Trim('-', '.');
        return cleaned.Length == 0 ? "untitled" : cleaned;
    }
}
