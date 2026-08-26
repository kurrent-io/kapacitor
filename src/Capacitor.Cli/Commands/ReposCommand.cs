using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Commands;

public sealed class ReposCommand(ConfigRoot config) {
    RepoPathStore Repos { get; } = new(config);

    public async Task<int> HandleAsync(string[] args) {
        if (args.Length < 2)
            return await List();

        var subcommand = args[1];

        return subcommand switch {
            "add" when args.Length >= 3    => await Add(args[2]),
            "add"                          => PrintAddUsage(),
            "remove" when args.Length >= 3 => await Remove(args[2]),
            "remove"                       => PrintRemoveUsage(),
            _                              => PrintUsage()
        };
    }

    async Task<int> List() {
        var entries = await Repos.LoadAsync();

        if (entries.Length == 0) {
            await Console.Out.WriteLineAsync("No known repos. Use `kcap repos add .` to add the current directory.");

            return 0;
        }

        var now = DateTimeOffset.UtcNow;

        foreach (var entry in entries.OrderByDescending(e => e.LastUsed)) {
            var ago = FormatTimeAgo(now - entry.LastUsed);
            await Console.Out.WriteLineAsync($"  {entry.Path}   ({ago})");
        }

        return 0;
    }

    async Task<int> Add(string path) {
        var resolved = Path.GetFullPath(path);

        if (!Directory.Exists(resolved)) {
            await Console.Error.WriteLineAsync($"Directory does not exist: {resolved}");

            return 1;
        }

        await Repos.AddAsync(resolved);
        await Console.Out.WriteLineAsync($"Added: {resolved}");

        return 0;
    }

    async Task<int> Remove(string path) {
        var resolved = Path.GetFullPath(path);
        var removed  = await Repos.RemoveAsync(resolved);

        if (removed) {
            await Console.Out.WriteLineAsync($"Removed: {resolved}");
        } else {
            await Console.Error.WriteLineAsync($"Not found: {resolved}");

            return 1;
        }

        return 0;
    }

    static string FormatTimeAgo(TimeSpan elapsed) {
        if (elapsed.TotalMinutes < 1) return "just now";
        if (elapsed.TotalHours   < 1) return $"{(int)elapsed.TotalMinutes}m ago";

        return elapsed.TotalDays switch {
            < 1  => $"{(int)elapsed.TotalHours}h ago",
            < 30 => $"{(int)elapsed.TotalDays}d ago",
            _    => $"{(int)(elapsed.TotalDays / 30)}mo ago"
        };
    }

    static int PrintUsage() {
        Console.Error.WriteLine("Usage: kcap repos [add|remove] [path]");

        return 1;
    }

    static int PrintAddUsage() {
        Console.Error.WriteLine("Usage: kcap repos add <path>");

        return 1;
    }

    static int PrintRemoveUsage() {
        Console.Error.WriteLine("Usage: kcap repos remove <path>");

        return 1;
    }
}
