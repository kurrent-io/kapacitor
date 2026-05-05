using System.Diagnostics;

namespace kapacitor.Commands;

public static class AgentCommands {
    static readonly string PidPath = PathHelpers.ConfigPath("agent.pid");
    static readonly string LogPath = PathHelpers.ConfigPath("agent.log");

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

        return detached ? StartDetached(args) : await StartForegroundAsync(args);
    }

    static async Task<int> StartForegroundAsync(string[] args) {
        var daemonPath = ResolveDaemonBinary();

        if (daemonPath is null) {
            await Console.Error.WriteLineAsync(DaemonNotFoundMessage());

            return 1;
        }

        var psi = new ProcessStartInfo {
            FileName        = daemonPath,
            UseShellExecute = false,
            CreateNoWindow  = true
        };

        foreach (var arg in args) {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi);

        if (process is null) {
            await Console.Error.WriteLineAsync($"Failed to start {daemonPath}");

            return 1;
        }

        await process.WaitForExitAsync();

        return process.ExitCode;
    }

    static int StartDetached(string[] args) {
        if (File.Exists(PidPath)) {
            var pidStr = File.ReadAllText(PidPath).Trim();

            if (int.TryParse(pidStr, out var existingPid) && IsOurDaemon(existingPid)) {
                Console.Error.WriteLine($"Agent daemon already running (PID {existingPid}). Use `kapacitor agent stop` first.");

                return 1;
            }
        }

        var daemonPath = ResolveDaemonBinary();

        if (daemonPath is null) {
            Console.Error.WriteLine(DaemonNotFoundMessage());

            return 1;
        }

        var psi = new ProcessStartInfo {
            FileName               = daemonPath,
            UseShellExecute        = false,
            RedirectStandardInput  = true,
            RedirectStandardOutput = false,
            RedirectStandardError  = false,
            CreateNoWindow         = true
        };
        psi.ArgumentList.Add("--log-file");
        psi.ArgumentList.Add(LogPath);

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
        Console.Out.WriteLine($"  Log:       {LogPath}");
        Console.Out.WriteLine("  Stop with: kapacitor agent stop");
        Console.Out.WriteLine("  Status:    kapacitor agent status");

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
    /// Verify that a PID belongs to a kapacitor-daemon process, not an unrelated
    /// process that reused the PID after our daemon exited.
    /// </summary>
    static bool IsOurDaemon(int pid) {
        try {
            var process    = Process.GetProcessById(pid);
            var daemonPath = ResolveDaemonBinary();
            var ourName    = daemonPath is not null
                ? Path.GetFileNameWithoutExtension(daemonPath)
                : "kapacitor-daemon";

            return string.Equals(process.ProcessName, ourName, StringComparison.OrdinalIgnoreCase);
        } catch (ArgumentException) {
            return false; // process doesn't exist
        }
    }

    static async Task<int> Logs() {
        if (!File.Exists(LogPath)) {
            await Console.Error.WriteLineAsync("No log file found.");

            return 1;
        }

        // Tail last 50 lines
        var lines = await File.ReadAllLinesAsync(LogPath);
        var start = Math.Max(0, lines.Length - 50);

        for (var i = start; i < lines.Length; i++) {
            await Console.Out.WriteLineAsync(lines[i]);
        }

        await Console.Error.WriteLineAsync($"\n--- {LogPath} ({lines.Length} lines total) ---");

        return 0;
    }

    /// <summary>
    /// Resolve the kapacitor-daemon executable shipped alongside this binary.
    /// The daemon is published as a separate AOT binary in the same directory
    /// as the main kapacitor binary (npm packages bundle both).
    /// </summary>
    static string? ResolveDaemonBinary() {
        var dir = AppContext.BaseDirectory;
        var ext = OperatingSystem.IsWindows() ? ".exe" : "";
        var sibling = Path.Combine(dir, $"kapacitor-daemon{ext}");

        return File.Exists(sibling) ? sibling : null;
    }

    static string DaemonNotFoundMessage() {
        return $"kapacitor-daemon binary not found next to {AppContext.BaseDirectory}. Reinstall the kapacitor package.";
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
