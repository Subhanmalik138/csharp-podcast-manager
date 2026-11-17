namespace PodCLI;

internal static class Program
{
    internal static async Task<int> Main(string[] args)
    {
        // Global option: --data-dir <dir> may appear before the command.
        var dataDir = (string?)null;
        var rest = new List<string>(args);
        var dirIndex = rest.IndexOf("--data-dir");
        if (dirIndex >= 0)
        {
            if (dirIndex + 1 >= rest.Count)
            {
                Console.WriteLine("Usage: podcli --data-dir <dir> <command> …");
                return 2;
            }
            dataDir = rest[dirIndex + 1];
            rest.RemoveRange(dirIndex, 2);
        }

        var store = dataDir is null ? Store.Default() : new Store(dataDir);
        var cli = new Cli(store);
        return await cli.RunAsync(rest.ToArray());
    }
}
