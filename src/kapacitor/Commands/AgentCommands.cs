using System.Diagnostics;
using kapacitor.Daemon;

namespace kapacitor.Commands;

public static class AgentCommands {
    static readonly string PidPath = PathHelpers.ConfigPath("agent.pid");

    public static async Task<int> HandleAsync(string[] args) {
        if (args.Length < 2) {
            PrintUsage();

            return 1;
        }

        var subcommand = args[1];
        var remaining  = args[2..];

        return subcommand switch {
            "start"  => await StartAsync(remaining),
            "stop"   => Stop(),
            "status" => await Status(),
            "logs"   => await Logs(),
            _        => PrintUsage()
        };
    }

    static async Task<int> StartAsync(string[] args) {
        var detached = args.Contains("-d") || args.Contains("--detach");

        if (detached) return StartDetached(args);

        return await DaemonRunner.RunAsync(args);
    }

    static int StartDetached(string[] args) {
        if (File.Exists(PidPath)) {
            var pidStr = File.ReadAllText(PidPath).Trim();

            if (int.TryParse(pidStr, out var existingPid) && IsOurDaemon(existingPid)) {
                Console.Error.WriteLine($"Agent daemon already running (PID {existingPid}). Use `kapacitor agent stop` first.");

                return 1;
            }
        }

        var exePath = Environment.ProcessPath;

        if (exePath is null) {
            Console.Error.WriteLine("Cannot determine executable path for background launch.");

            return 1;
        }

        var logPath = DaemonRunner.LogPath;

        var psi = new ProcessStartInfo {
            FileName               = exePath,
            UseShellExecute        = false,
            RedirectStandardInput  = true,
            RedirectStandardOutput = false,
            RedirectStandardError  = false,
            CreateNoWindow         = true
        };
        psi.ArgumentList.Add("agent");
        psi.ArgumentList.Add("start");
        psi.ArgumentList.Add("--log-file");
        psi.ArgumentList.Add(logPath);

        foreach (var arg in args.Where(a => a is not "-d" and not "--detach")) {
            psi.ArgumentList.Add(arg);
        }

        var process = new Process { StartInfo = psi };
        process.Start();

        // Close stdin so the child doesn't wait for input
        process.StandardInput.Close();

        var dir = Path.GetDirectoryName(PidPath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(PidPath, process.Id.ToString());

        Console.Out.WriteLine($"Agent daemon started (PID {process.Id})");
        Console.Out.WriteLine($"  Log:       {logPath}");
        Console.Out.WriteLine($"  Stop with: kapacitor agent stop");
        Console.Out.WriteLine($"  Status:    kapacitor agent status");

        return 0;
    }

    static int Stop() {
        if (!File.Exists(PidPath)) {
            Console.Error.WriteLine("No agent daemon running (no PID file found).");

            return 1;
        }

        var pidStr = File.ReadAllText(PidPath).Trim();

        if (!int.TryParse(pidStr, out var pid)) {
            Console.Error.WriteLine($"Invalid PID file: {PidPath}");
            File.Delete(PidPath);

            return 1;
        }

        try {
            if (!IsOurDaemon(pid)) {
                Console.Out.WriteLine("Agent daemon was not running (stale PID file).");
                File.Delete(PidPath);

                return 0;
            }

            var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
            Console.Out.WriteLine($"Agent daemon stopped (PID {pid}).");
        } catch (ArgumentException) {
            Console.Out.WriteLine("Agent daemon was not running.");
        }

        File.Delete(PidPath);

        return 0;
    }

    static async Task<int> Status() {
        if (!File.Exists(PidPath)) {
            await Console.Out.WriteLineAsync("Agent: not running");

            return 0;
        }

        var pidStr = (await File.ReadAllTextAsync(PidPath)).Trim();

        if (!int.TryParse(pidStr, out var pid)) {
            await Console.Out.WriteLineAsync("Agent: unknown (invalid PID file)");

            return 1;
        }

        if (IsOurDaemon(pid)) {
            await Console.Out.WriteLineAsync($"Agent: running (PID {pid})");
        } else {
            await Console.Out.WriteLineAsync("Agent: not running (stale PID file)");
            File.Delete(PidPath);
        }

        return 0;
    }

    /// <summary>
    /// Verify that a PID belongs to our daemon process, not an unrelated process
    /// that reused the PID after our daemon exited.
    /// </summary>
    static bool IsOurDaemon(int pid) {
        try {
            var process = Process.GetProcessById(pid);
            var ourExe  = Environment.ProcessPath;

            if (ourExe is null) return true; // can't verify, assume ours

            // Check process name matches (best-effort — ProcessName doesn't include path)
            var ourName = Path.GetFileNameWithoutExtension(ourExe);

            return string.Equals(process.ProcessName, ourName, StringComparison.OrdinalIgnoreCase);
        } catch (ArgumentException) {
            return false; // process doesn't exist
        }
    }

    static async Task<int> Logs() {
        var logPath = DaemonRunner.LogPath;

        if (!File.Exists(logPath)) {
            await Console.Error.WriteLineAsync("No log file found.");

            return 1;
        }

        // Tail last 50 lines
        var lines = await File.ReadAllLinesAsync(logPath);
        var start = Math.Max(0, lines.Length - 50);

        for (var i = start; i < lines.Length; i++) {
            await Console.Out.WriteLineAsync(lines[i]);
        }

        await Console.Error.WriteLineAsync($"\n--- {logPath} ({lines.Length} lines total) ---");

        return 0;
    }

    static int PrintUsage() {
        Console.Error.WriteLine("Usage: kapacitor agent <start|stop|status|logs>");
        Console.Error.WriteLine();
        Console.Error.WriteLine("  start [-d]  Start the agent daemon (foreground, or -d for background)");
        Console.Error.WriteLine("  stop        Stop the background agent daemon");
        Console.Error.WriteLine("  status      Show agent daemon status");
        Console.Error.WriteLine("  logs        Show recent daemon log output");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Options for start:");
        Console.Error.WriteLine("  --name <name>         Daemon name");
        Console.Error.WriteLine("  --server-url <url>    Server URL");
        Console.Error.WriteLine("  --max-agents <n>      Max concurrent agents (default: 5)");
        Console.Error.WriteLine("  --log-file <path>     Log to file instead of console");
        Console.Error.WriteLine("  -d, --detach          Run in background (logs to file automatically)");

        return 1;
    }
}
