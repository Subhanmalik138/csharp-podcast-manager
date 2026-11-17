namespace PodCLI;

internal static class Program
{
    internal static int Main(string[] args)
    {
        // Stage 1 scaffold: the domain model and store exist; the CLI commands
        // land in the next patch. Until then, show help and exit cleanly.
        Console.WriteLine("podcli — a CLI podcast manager");
        Console.WriteLine("Run `podcli help` once the commands land (next patch).");
        return 0;
    }
}
