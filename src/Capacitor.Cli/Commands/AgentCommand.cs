using System.Net.Sockets;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Local;

namespace Capacitor.Cli.Commands;

/// <summary>
/// `kcap agent start|ls|stop|attach` — drive daemon-hosted agents from the local
/// terminal over the daemon's local control socket.
/// </summary>
internal static class AgentCommand {
    internal static readonly string[] KnownSubcommands = ["start", "ls", "stop", "attach"];

    /// <summary>Bare `kcap agent` lists agents; otherwise argv[1] is the subcommand.</summary>
    // "Rest" (capitalized) is a compiler-reserved tuple element name; "rest" is not.
    internal static (string Sub, string[] rest) SplitSubcommand(string[] args) =>
        args.Length > 1 ? (args[1], args[2..]) : ("ls", []);

    public static async Task<int> HandleAsync(string[] args) {
        if (NotSupportedOnWindows(out var rc)) return rc;

        var (sub, rest) = SplitSubcommand(args);

        switch (sub) {
            case "start":  return await RunAsync(rest);
            case "ls":     return await ListAsync(rest);
            case "attach": return await AttachAsync(rest);
            default:
                await Console.Error.WriteLineAsync($"kcap agent: unknown subcommand '{sub}'");
                await Console.Error.WriteLineAsync($"Usage: kcap agent <{string.Join('|', KnownSubcommands)}>");
                await Console.Error.WriteLineAsync("Run `kcap agent --help` for details.");

                return 1;
        }
    }

    static async Task<int> RunAsync(string[] args) {
        var parsed = AgentStartArgs.Parse(args);
        if (parsed.Error is not null) {
            await Console.Error.WriteLineAsync($"kcap agent start: {parsed.Error}");

            return 1;
        }

        var name = ResolveName(parsed.DaemonName);
        if (!await EnsureDaemonAsync(name)) return 1;

        var sock = LocalSocketPaths.Socket(name);
        var work = parsed.Worktree ? WorkLocation.OwnedWorktree : WorkLocation.BorrowedCwd;
        var (cols, rows) = TermSize();
        var spawn = FrameCodec.Spawn(parsed.Vendor, work, parsed.Private, Environment.CurrentDirectory, parsed.Passthrough, cols, rows);

        return parsed.Detached
            ? await SpawnDetachedAsync(sock, spawn)
            : await LocalAgentClient.RunAsync(sock, spawn, CancellationToken.None);
    }

    static async Task<int> AttachAsync(string[] args) {
        if (args.Length == 0 || args[0].StartsWith('-')) {
            await Console.Error.WriteLineAsync("usage: kcap agent attach <agent-id> [--daemon <name>]");

            return 1;
        }

        var agentId = args[0];
        var sock    = LocalSocketPaths.Socket(ResolveName(NameFrom(args)));

        if (!File.Exists(sock)) {
            await Console.Error.WriteLineAsync($"kcap: no daemon socket at {sock}");

            return 1;
        }

        return await LocalAgentClient.RunAsync(sock, new LocalFrame(FrameType.Attach) { Text = agentId }, CancellationToken.None);
    }

    static async Task<int> ListAsync(string[] args) {
        var sock = LocalSocketPaths.Socket(ResolveName(NameFrom(args)));

        if (!File.Exists(sock)) {
            Console.WriteLine("No local daemon running.");

            return 0;
        }

        try {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(sock));
            await using var stream = new NetworkStream(socket, ownsSocket: false);

            await FrameCodec.WriteAsync(stream, new LocalFrame(FrameType.List), default);
            var resp = await FrameCodec.ReadAsync(stream, default);

            if (resp is null || resp.Type != FrameType.AgentList || resp.Text.Length == 0) {
                Console.WriteLine("No agents.");

                return 0;
            }

            Console.WriteLine($"{"AGENT",-34} {"STATUS",-10} REPO");
            foreach (var line in resp.Text.Split('\n')) {
                var parts = line.Split('\t');
                if (parts.Length == 3) Console.WriteLine($"{parts[0],-34} {parts[1],-10} {parts[2]}");
            }

            return 0;
        } catch (Exception ex) when (ex is SocketException or IOException) {
            await Console.Error.WriteLineAsync($"kcap: cannot reach daemon: {ex.Message}");

            return 1;
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────

    static async Task<int> SpawnDetachedAsync(string sock, LocalFrame spawn) {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(sock));
        await using var stream = new NetworkStream(socket, ownsSocket: false);

        await FrameCodec.WriteAsync(stream, spawn, default);
        var f = await FrameCodec.ReadAsync(stream, default);

        switch (f?.Type) {
            case FrameType.Attached:
                var (id, _) = FrameCodec.Attached(f);
                Console.WriteLine($"Started agent {id} (detached). Attach with: kcap agent attach {id}");

                return 0;
            case FrameType.Error:
                await Console.Error.WriteLineAsync($"kcap: {f.Text}");

                return 1;
            default:
                await Console.Error.WriteLineAsync("kcap: unexpected daemon response to spawn");

                return 1;
        }
    }

    /// <summary>Connects if a daemon is up; otherwise starts one detached and waits for the socket.</summary>
    static async Task<bool> EnsureDaemonAsync(string name) {
        var sock = LocalSocketPaths.Socket(name);
        if (await CanConnectAsync(sock)) return true;

        await Console.Error.WriteLineAsync($"kcap: starting daemon '{name}'…");
        await DaemonCommands.HandleAsync(["daemon", "start", "-d", "--name", name]);

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline) {
            if (await CanConnectAsync(sock)) return true;
            await Task.Delay(250);
        }

        await Console.Error.WriteLineAsync("kcap: daemon did not come up in time (check `kcap daemon logs`).");

        return false;
    }

    static async Task<bool> CanConnectAsync(string sock) {
        if (!File.Exists(sock)) return false;

        try {
            using var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await s.ConnectAsync(new UnixDomainSocketEndPoint(sock));

            return true;
        } catch {
            return false;
        }
    }

    static string ResolveName(string? daemonName) {
        string[] args = daemonName is null ? [] : ["--name", daemonName];

        return DaemonNameResolver.Resolve(args, AppConfig.ResolvedProfile?.Profile?.Daemon?.Name);
    }

    static string? NameFrom(string[] args) {
        var i = Array.IndexOf(args, "--daemon");

        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    static (ushort Cols, ushort Rows) TermSize() {
        try { return ((ushort)Math.Max(1, Console.WindowWidth), (ushort)Math.Max(1, Console.WindowHeight)); }
        catch { return (120, 40); }
    }

    static bool NotSupportedOnWindows(out int rc) {
        if (OperatingSystem.IsWindows()) {
            Console.Error.WriteLine("kcap agent is not supported on Windows yet.");
            rc = 1;

            return true;
        }

        rc = 0;

        return false;
    }
}
