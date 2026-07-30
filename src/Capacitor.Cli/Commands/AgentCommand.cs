using System.Net.Sockets;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Local;

namespace Capacitor.Cli.Commands;

/// One row of the daemon's agent table (`id\tstatus\trepo\tkind\tflowRunId\tflowRole` on the
/// wire). A daemon older than #379 sends only the first three; the rest default.
internal readonly record struct AgentRow(
    string Id, string Status, string Repo, string Kind, string FlowRunId, string FlowRole);

/// <summary>
/// `kcap agent start|ls|stop|attach` — drive daemon-hosted agents from the local
/// terminal over the daemon's local control socket.
/// </summary>
internal static class AgentCommand {
    internal static readonly string[] KnownSubcommands = ["start", "ls", "stop", "attach"];

    /// Verbs that only ever belonged to the pre-rename `agent` daemon group, minus the
    /// start/stop the new group also owns.
    internal static readonly string[] DaemonOnlySubcommands = ["restart", "status", "logs", "doctor", "service"];

    /// Global flags Program.cs reads off the raw argv and leaves in place. They are not this
    /// group's to interpret, and the subcommand parsers reject unknown flags, so drop them first.
    static readonly string[] GlobalFlags = ["--no-update-check"];

    /// <summary>
    /// Bare `kcap agent` lists agents, and so does a leading flag — `kcap agent --daemon dev`
    /// is an `ls` with an option, not a subcommand named `--daemon`.
    /// </summary>
    internal static (string Sub, string[] Args) SplitSubcommand(string[] args) =>
        args.Length > 1 && !args[1].StartsWith('-') ? (args[1], args[2..]) : ("ls", args[1..]);

    public static async Task<int> HandleAsync(string[] args, string? baseUrl) {
        var (sub, rest) = SplitSubcommand([.. args.Where(a => !GlobalFlags.Contains(a))]);

        // `agent` was the daemon verb before it was renamed to `daemon`. The two groups still
        // share start/stop, so those dispatch below; the rest only ever meant the daemon, and
        // answering them with a bare usage line is how a healthy daemon reads as dead
        // mid-diagnosis. Signposted ahead of the platform guard because `kcap daemon` works on
        // Windows even though this group does not.
        if (DaemonOnlySubcommands.Contains(sub)) {
            await Console.Error.WriteLineAsync($"kcap agent: unknown subcommand '{sub}'");
            await Console.Error.WriteLineAsync($"`{sub}` manages the daemon — run `kcap daemon {sub}`.");

            return 1;
        }

        if (NotSupportedOnWindows(out var rc)) return rc;

        switch (sub) {
            case "start":  return await RunAsync(rest, baseUrl);
            case "ls":     return await ListAsync(rest);
            case "stop":   return await StopAsync(rest);
            case "attach": return await AttachAsync(rest);
            default:
                await Console.Error.WriteLineAsync($"kcap agent: unknown subcommand '{sub}'");
                await Console.Error.WriteLineAsync($"Usage: kcap agent <{string.Join('|', KnownSubcommands)}>");
                await Console.Error.WriteLineAsync("Run `kcap agent --help` for details.");

                return 1;
        }
    }

    static async Task<int> RunAsync(string[] args, string? baseUrl) {
        // ls/stop/attach only ever talk to the local socket, so the group is offline-callable.
        // Starting an agent is the one subcommand that needs a server for the daemon to record to.
        if (baseUrl is null) {
            await Console.Error.WriteLineAsync("kcap agent start: no server configured. Run `kcap setup` or set KCAP_URL.");

            return 1;
        }

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
        var force = args.Contains("--force");
        var hasId = args.Length > 0 && !args[0].StartsWith('-');

        if (all && hasId) {
            await Console.Error.WriteLineAsync("kcap agent stop: cannot combine an agent id with --all");

            return 1;
        }

        if (!all && !hasId) {
            await Console.Error.WriteLineAsync("usage: kcap agent stop <agent-id> [--force] [--daemon <name>]");
            await Console.Error.WriteLineAsync("       kcap agent stop --all [-y] [--force] [--daemon <name>]");

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

            var (stoppable, protectedIds) = PartitionByProtection(agents);
            var targets = force ? agents.Select(a => a.Id).ToArray() : stoppable;

            if (targets.Length == 0) {
                Console.WriteLine(protectedIds.Length > 0
                    ? $"No agents to stop. {protectedIds.Length} review agent(s) skipped — pass --force to include them."
                    : "No agents.");

                return 0;
            }

            Console.WriteLine($"Found {targets.Length} agents:");
            foreach (var a in agents.Where(a => targets.Contains(a.Id) && !protectedIds.Contains(a.Id)))
                Console.WriteLine($"  • {a.Id}  {a.Repo}");

            // Both branches label protected rows with their Kind before the [y/N] prompt, so the
            // blast radius of --force is as visible as the default skip list is.
            if (protectedIds.Length > 0) {
                Console.WriteLine(force
                    ? $"Including {protectedIds.Length} review agent(s):"
                    : $"Skipping {protectedIds.Length} review agent(s) — pass --force to include them:");
                foreach (var a in agents.Where(a => protectedIds.Contains(a.Id)))
                    Console.WriteLine($"  • {a.Id}  {a.Kind}  {a.Repo}");
            }

            if (!yes) {
                await Console.Out.WriteAsync($"Stop {targets.Length}? [y/N] ");
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

        return await SendStopAsync(sock, target, name, force);
    }

    static async Task<int> SendStopAsync(string sock, string agentId, string daemonName, bool force) {
        try {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(sock));
            await using var stream = new NetworkStream(socket, ownsSocket: false);

            await FrameCodec.WriteAsync(stream, LocalFrame.StopV2(force, agentId), default);
            var resp = await FrameCodec.ReadAsync(stream, default);

            switch (resp?.Type) {
                case FrameType.StopAck:
                    string[] lines = resp.Text.Length == 0 ? [] : resp.Text.Split('\n');
                    if (lines.Length == 0) { Console.WriteLine("No agents."); return 0; }

                    var failed  = 0;
                    var skipped = 0;

                    foreach (var line in lines) {
                        var parts  = line.Split('\t');
                        var id     = parts[0];
                        var status = parts.Length > 1 ? parts[1] : "failed";

                        switch (status) {
                            case "stopped": Console.WriteLine($"Stopped {id}."); break;
                            case "skipped": Console.WriteLine($"Skipped {id} — review agent; pass --force to stop it."); skipped++; break;
                            default:        Console.Error.WriteLine($"Failed to stop {id} — see `kcap daemon logs`."); failed++; break;
                        }
                    }

                    if (skipped > 0)
                        Console.WriteLine($"{skipped} review agent(s) left running — pass --force to stop them.");

                    // Skipping is the documented default, not a failure, so it does not affect
                    // the exit code.
                    return failed > 0 ? 1 : 0;
                case FrameType.Error:
                    await Console.Error.WriteLineAsync($"kcap: {resp.Text}");

                    return 1;
                case null:
                    // An older daemon can't decode frame type 10 (StopV2): it faults before its
                    // frame switch and closes without replying, so we see a clean EOF. `--force`
                    // here is the restart flag, not the stop flag — this daemon is guaranteed
                    // busy since the stop it never understood never ran, so `restart` needs its
                    // own override to proceed.
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

        Console.WriteLine($"{"AGENT",-34} {"STATUS",-10} {"KIND",-12} REPO");
        foreach (var a in agents) {
            var role = a.FlowRole.Length > 0 ? $"  [{a.FlowRole}]" : "";
            Console.WriteLine($"{a.Id,-34} {a.Status,-10} {a.Kind,-12} {a.Repo}{role}");
        }

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

            // Exactly 3 columns is an older daemon; exactly 6 is a current one. Any other width
            // means the row was corrupted in transit — most plausibly a delimiter inside a
            // free-form field — and a shifted kind column would misreport what `stop --all` is
            // about to do. Refuse the whole table rather than consent to a guess.
            string[] rows = [.. resp.Text.Split('\n').Where(l => l.Length > 0)];

            if (rows.Any(l => l.Split('\t').Length is not (3 or 6))) {
                await Console.Error.WriteLineAsync(
                    "kcap: daemon sent a malformed agent table (unexpected column count); refusing to act on it");

                return null;
            }

            return [.. rows.Select(ParseAgentRow)];
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

    /// Kinds the CLI refuses to mutate by accident: a reviewer mid-round is not the user's to
    /// type at or stop. Mirrors LaunchKind — anything that is not a plain agent is protected,
    /// including a kind this build doesn't recognise: an unrecognised kind fails safe rather
    /// than reading as stoppable.
    internal static bool IsProtectedKind(string kind) => kind is not "agent";

    /// <summary>Splits an agent list into what `stop --all` will stop and what it will skip.</summary>
    internal static (string[] Stoppable, string[] Protected) PartitionByProtection(IReadOnlyList<AgentRow> agents) => (
        [.. agents.Where(a => !IsProtectedKind(a.Kind)).Select(a => a.Id)],
        [.. agents.Where(a => IsProtectedKind(a.Kind)).Select(a => a.Id)]
    );

    /// <summary>Tolerates a short row from an older daemon by defaulting the newer columns.</summary>
    internal static AgentRow ParseAgentRow(string line) {
        var p = line.Split('\t');

        return new AgentRow(
            p[0],
            p.Length > 1 ? p[1] : "",
            p.Length > 2 ? p[2] : "",
            p.Length > 3 && p[3].Length > 0 ? p[3] : "agent",
            p.Length > 4 ? p[4] : "",
            p.Length > 5 ? p[5] : "");
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
