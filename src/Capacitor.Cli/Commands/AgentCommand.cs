using System.Net.Sockets;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Local;

namespace Capacitor.Cli.Commands;

/// One row of the daemon's agent table (`id\tstatus\trepo` on the wire).
internal readonly record struct AgentRow(string Id, string Status, string Repo);

/// <summary>
/// `kcap agent start|ls|stop|attach` — drive daemon-hosted agents from the local
/// terminal over the daemon's local control socket.
/// </summary>
internal static class AgentCommand {
    internal static readonly string[] KnownSubcommands = ["start", "ls", "stop", "attach"];

    /// Verbs that only ever belonged to the pre-rename `agent` daemon group, minus the
    /// start/stop the new group also owns.
    internal static readonly string[] DaemonOnlySubcommands = ["restart", "status", "logs", "doctor", "service"];

    /// <summary>Bare `kcap agent` lists agents; otherwise argv[1] is the subcommand.</summary>
    internal static (string Sub, string[] Args) SplitSubcommand(string[] args) =>
        args.Length > 1 ? (args[1], args[2..]) : ("ls", []);

    public static async Task<int> HandleAsync(string[] args) {
        if (NotSupportedOnWindows(out var rc)) return rc;

        var (sub, rest) = SplitSubcommand(args);

        switch (sub) {
            case "start":  return await RunAsync(rest);
            case "ls":     return await ListAsync(rest);
            case "stop":   return await StopAsync(rest);
            case "attach": return await AttachAsync(rest);
            default:
                await Console.Error.WriteLineAsync($"kcap agent: unknown subcommand '{sub}'");

                // `agent` was the daemon verb before it was renamed to `daemon`. The two groups
                // still share start/stop, so those dispatch here; the rest only ever meant the
                // daemon, and answering them with a bare usage line is how a healthy daemon reads
                // as dead mid-diagnosis.
                if (DaemonOnlySubcommands.Contains(sub))
                    await Console.Error.WriteLineAsync($"`{sub}` manages the daemon — run `kcap daemon {sub}`.");

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

        var (daemonName, daemonError) = DaemonNameFrom(args);
        if (daemonError is not null) {
            await Console.Error.WriteLineAsync($"kcap agent: {daemonError}");

            return 1;
        }

        var sock = LocalSocketPaths.Socket(ResolveName(daemonName));

        if (!File.Exists(sock)) {
            await Console.Error.WriteLineAsync($"kcap: no daemon socket at {sock}");

            return 1;
        }

        var agentId = await ResolveOrReportAsync(sock, args[0]);
        if (agentId is null) return 1;

        return await LocalAgentClient.RunAsync(sock, new LocalFrame(FrameType.Attach) { Text = agentId }, CancellationToken.None);
    }

    static async Task<int> StopAsync(string[] args) {
        var all = args.Contains("--all");
        var yes = args.Contains("--yes") || args.Contains("-y");
        var hasId = args.Length > 0 && !args[0].StartsWith('-');

        if (all && hasId) {
            await Console.Error.WriteLineAsync("kcap agent stop: cannot combine an agent id with --all");

            return 1;
        }

        if (!all && !hasId) {
            await Console.Error.WriteLineAsync("usage: kcap agent stop <agent-id> [--daemon <name>]");
            await Console.Error.WriteLineAsync("       kcap agent stop --all [-y] [--daemon <name>]");

            return 1;
        }

        var (daemonName, daemonError) = DaemonNameFrom(args);
        if (daemonError is not null) {
            await Console.Error.WriteLineAsync($"kcap agent: {daemonError}");

            return 1;
        }

        var name = ResolveName(daemonName);
        var sock = LocalSocketPaths.Socket(name);

        if (!File.Exists(sock)) {
            await Console.Error.WriteLineAsync($"kcap: no daemon socket at {sock}");

            return 1;
        }

        string target;

        if (all) {
            var agents = await FetchAgentsAsync(sock);
            if (agents is null) return 1;

            if (agents.Count == 0) {
                Console.WriteLine("No agents.");

                return 0;
            }

            Console.WriteLine($"Found {agents.Count} agents:");
            foreach (var a in agents) Console.WriteLine($"  • {a.Id}  {a.Repo}");

            if (!yes) {
                await Console.Out.WriteAsync($"Stop all {agents.Count}? [y/N] ");
                var reply = await Console.In.ReadLineAsync();

                if (!string.Equals(reply?.Trim(), "y", StringComparison.OrdinalIgnoreCase)) {
                    await Console.Out.WriteLineAsync("Cancelled.");

                    return 0;
                }
            }

            target = "";
        } else {
            var resolved = await ResolveOrReportAsync(sock, args[0]);
            if (resolved is null) return 1;

            target = resolved;
        }

        return await SendStopAsync(sock, target, name);
    }

    static async Task<int> SendStopAsync(string sock, string agentId, string daemonName) {
        try {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(sock));
            await using var stream = new NetworkStream(socket, ownsSocket: false);

            await FrameCodec.WriteAsync(stream, LocalFrame.Stop(agentId), default);
            var resp = await FrameCodec.ReadAsync(stream, default);

            switch (resp?.Type) {
                case FrameType.StopAck:
                    if (resp.Text.Length == 0) {
                        Console.WriteLine("No agents.");

                        return 0;
                    }

                    var lines     = resp.Text.Split('\n');
                    var anyFailed = false;

                    foreach (var line in lines) {
                        var parts = line.Split('\t');
                        var id    = parts[0];

                        if (parts is [_, "stopped"]) {
                            Console.WriteLine($"Stopped {id}.");
                        } else {
                            Console.WriteLine($"Failed to stop {id} — see `kcap daemon logs`.");
                            anyFailed = true;
                        }
                    }

                    return anyFailed ? 1 : 0;
                case FrameType.Error:
                    await Console.Error.WriteLineAsync($"kcap: {resp.Text}");

                    return 1;
                case null:
                    // An older daemon can't decode frame type 8: it faults before its frame
                    // switch and closes without replying, so we see a clean EOF. `--force` is
                    // the only restart mode that bypasses the busy check, and this daemon is
                    // guaranteed busy — the agent we're trying to stop is still running.
                    await Console.Error.WriteLineAsync(
                        $"kcap: this daemon is too old for `agent stop` — restart it with `kcap daemon restart --force --name {daemonName}`");

                    return 1;
                default:
                    await Console.Error.WriteLineAsync($"kcap: unexpected daemon response to stop ({resp.Type})");

                    return 1;
            }
        } catch (Exception ex) when (ex is SocketException or IOException) {
            await Console.Error.WriteLineAsync($"kcap: cannot reach daemon: {ex.Message}");

            return 1;
        }
    }

    /// <summary>Resolves an id or prefix, printing the reason and returning null on failure.</summary>
    static async Task<string?> ResolveOrReportAsync(string sock, string given) {
        if (IsFullAgentId(given)) return given.ToLowerInvariant(); // skip the round-trip; ResolveAgentId agrees

        var agents = await FetchAgentsAsync(sock);
        if (agents is null) return null;

        var (id, error) = ResolveAgentId(agents, given);
        if (error is not null) await Console.Error.WriteLineAsync($"kcap: {error}");

        return id;
    }

    static async Task<int> ListAsync(string[] args) {
        var (daemonName, daemonError) = DaemonNameFrom(args);
        if (daemonError is not null) {
            await Console.Error.WriteLineAsync($"kcap agent: {daemonError}");

            return 1;
        }

        var sock = LocalSocketPaths.Socket(ResolveName(daemonName));

        if (!File.Exists(sock)) {
            Console.WriteLine("No local daemon running.");

            return 0;
        }

        var agents = await FetchAgentsAsync(sock);
        if (agents is null) return 1;

        if (agents.Count == 0) {
            Console.WriteLine("No agents.");

            return 0;
        }

        Console.WriteLine($"{"AGENT",-34} {"STATUS",-10} REPO");
        foreach (var a in agents) Console.WriteLine($"{a.Id,-34} {a.Status,-10} {a.Repo}");

        return 0;
    }

    /// <summary>
    /// Asks the daemon for its agent table. Returns null — after reporting the reason — for any
    /// non-answer: a closed connection, an Error frame, or an unexpected frame type. Only a real
    /// AgentList frame with an empty payload is a genuinely empty table, so only that maps to [].
    /// </summary>
    static async Task<IReadOnlyList<AgentRow>?> FetchAgentsAsync(string sock) {
        try {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(sock));
            await using var stream = new NetworkStream(socket, ownsSocket: false);

            await FrameCodec.WriteAsync(stream, new LocalFrame(FrameType.List), default);
            var resp = await FrameCodec.ReadAsync(stream, default);

            if (resp is null) {
                await Console.Error.WriteLineAsync("kcap: daemon closed the connection without replying to list");

                return null;
            }

            if (resp.Type == FrameType.Error) {
                await Console.Error.WriteLineAsync($"kcap: {resp.Text}");

                return null;
            }

            if (resp.Type != FrameType.AgentList) {
                await Console.Error.WriteLineAsync($"kcap: unexpected daemon response to list ({resp.Type})");

                return null;
            }

            if (resp.Text.Length == 0) return [];

            return [.. resp.Text.Split('\n')
                .Select(l => l.Split('\t'))
                .Where(p => p.Length == 3)
                .Select(p => new AgentRow(p[0], p[1], p[2]))];
        } catch (Exception ex) when (ex is SocketException or IOException) {
            await Console.Error.WriteLineAsync($"kcap: cannot reach daemon: {ex.Message}");

            return null;
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

    /// <summary>
    /// Parses `--daemon &lt;name&gt;` out of the shared `ls`/`attach`/`stop` arg list. Absent
    /// entirely resolves to (null, null) — meaning "use the default daemon", the correct existing
    /// behaviour. Present with no value, or with a following flag instead of a value, is an error
    /// rather than silently falling back to the default: a typo here must not retarget a
    /// destructive `stop` at the wrong (and possibly unconfirmed, with `-y`) daemon.
    /// </summary>
    internal static (string? Name, string? Error) DaemonNameFrom(string[] args) {
        var i = Array.IndexOf(args, "--daemon");
        if (i < 0) return (null, null);

        var next = i + 1 < args.Length ? args[i + 1] : null;

        return string.IsNullOrEmpty(next) || next.StartsWith('-')
            ? (null, "--daemon requires a value")
            : (next, null);
    }

    /// <summary>A full agent id as minted by `Guid.NewGuid().ToString("N")`.</summary>
    internal static bool IsFullAgentId(string s) => s.Length == 32 && s.All(char.IsAsciiHexDigit);

    /// <summary>
    /// A full 32-hex id is used verbatim — it may name an agent that survived a previous
    /// daemon incarnation and so is absent from the live list, which the daemon can still
    /// reap by PID record. Anything shorter is a prefix and must match exactly one agent.
    /// </summary>
    internal static (string? Id, string? Error) ResolveAgentId(IReadOnlyList<AgentRow> agents, string given) {
        // Lowercase: the daemon's lookups (_agents.TryGetValue, TryStopByPidRecordAsync) are
        // ordinal, so an uppercase full id must be normalized here to match the lowercase ids
        // the daemon mints — the prefix path below is already case-insensitive.
        if (IsFullAgentId(given)) return (given.ToLowerInvariant(), null);

        var hits = agents.Where(a => a.Id.StartsWith(given, StringComparison.OrdinalIgnoreCase)).ToList();

        return hits.Count switch {
            1 => (hits[0].Id, null),
            0 => (null, $"no agent matching '{given}'"),
            _ => (null, $"'{given}' matches {hits.Count} agents:{string.Concat(hits.Select(h => $"\n  {h.Id}  {h.Repo}"))}"),
        };
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
