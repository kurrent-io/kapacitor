using System.Net.Sockets;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Commands;

/// <summary>
/// <c>kcap daemon consent</c> — inspect and mutate the daemon-owned launch-consent policy
/// over the local control socket. Every verb except <c>log</c> requires a running
/// daemon (the policy lives in daemon memory, mutated only through
/// <see cref="FrameType.ConsentRulesGet"/>/<see cref="FrameType.ConsentRulesPut"/>); <c>log</c>
/// reads the decision-log file directly, so it also works with the daemon stopped.
/// </summary>
public static class DaemonConsentCommand {
    static readonly string[] Verbs = ["show", "set-default", "allow", "deny", "remove", "log"];

    public static async Task<int> HandleAsync(string[] args) {
        if (args.Length == 0) return PrintConsentUsage();

        var sub  = args[0];
        var rest = args[1..];

        // `log` is the one verb that never touches the socket, so it skips the
        // running-daemon precondition below.
        if (sub == "log") return await LogAsync(rest);

        if (!Verbs.Contains(sub)) return PrintConsentUsage();

        string name;
        try {
            name = ResolveName(rest);
        } catch (ArgumentException ex) {
            await Console.Error.WriteLineAsync(ex.Message);

            return 1;
        }

        var socketPath = LocalSocketPaths.Socket(name);

        if (!File.Exists(socketPath)) {
            await Console.Error.WriteLineAsync($"daemon is not running (socket not found at {socketPath})");

            return 1;
        }

        return sub switch {
            "show"        => await ShowAsync(socketPath),
            "set-default" => await SetDefaultAsync(socketPath, rest),
            "allow"       => await AddRuleAsync(socketPath, "allow", rest),
            "deny"        => await AddRuleAsync(socketPath, "deny", rest),
            "remove"      => await RemoveAsync(socketPath, rest),
            _             => PrintConsentUsage(), // unreachable — sub is a member of Verbs here
        };
    }

    static string ResolveName(string[] args) =>
        DaemonNameResolver.Resolve(args, AppConfig.ResolvedProfile?.Profile?.Daemon?.Name);

    // ── show ────────────────────────────────────────────────────────────────

    static async Task<int> ShowAsync(string socketPath) {
        var policy = await GetPolicyAsync(socketPath);
        if (policy is null) return 1; // error already reported

        Console.WriteLine($"default: {policy.Default}");
        Console.WriteLine($"prompt timeout: {policy.PromptTimeoutSeconds}s");

        if (policy.Rules.Count == 0) {
            Console.WriteLine("rules: (none — owner is always allowed; everyone else gets the default above)");

            return 0;
        }

        Console.WriteLine("rules (first match wins):");
        for (var i = 0; i < policy.Rules.Count; i++) {
            var r = policy.Rules[i];
            Console.WriteLine(
                $"  [{i}] {r.Action} requester={r.Requester ?? "*"} kind={r.Kind ?? "*"} " +
                $"repo={r.Repo ?? "*"} vendor={r.Vendor ?? "*"}");
        }

        return 0;
    }

    // ── set-default ────────────────────────────────────────────────────────

    static async Task<int> SetDefaultAsync(string socketPath, string[] args) {
        var positional = WithoutName(args);

        if (positional.Length == 0 || positional[0] is not ("allow" or "deny" or "prompt")) {
            await Console.Error.WriteLineAsync("usage: kcap daemon consent set-default <allow|deny|prompt>");

            return 1;
        }

        var policy = await GetPolicyAsync(socketPath);
        if (policy is null) return 1;

        return await PutPolicyAsync(socketPath, policy with { Default = positional[0] });
    }

    // ── allow / deny ────────────────────────────────────────────────────────

    static async Task<int> AddRuleAsync(string socketPath, string action, string[] args) {
        var rule = TryBuildRule(action, WithoutName(args), out var error);

        if (rule is null) {
            await Console.Error.WriteLineAsync($"kcap daemon consent {action}: {error}");

            return 1;
        }

        var policy = await GetPolicyAsync(socketPath);
        if (policy is null) return 1;

        return await PutPolicyAsync(socketPath, policy with { Rules = [.. policy.Rules, rule] });
    }

    /// <summary>
    /// Parses the <c>--requester</c>/<c>--kind</c>/<c>--repo</c>/<c>--vendor</c> flag pairs for
    /// <c>allow</c>/<c>deny</c> into a <see cref="ConsentRuleDto"/>. At least one flag is
    /// required — an all-wildcard <c>allow</c> rule is a no-op (first-match-wins would let it
    /// mask every rule after it) and an all-wildcard <c>deny</c> is already expressible via
    /// <c>set-default deny</c>, so a flagless invocation is rejected with a pointer to that
    /// instead of silently doing something surprising.
    /// </summary>
    internal static ConsentRuleDto? TryBuildRule(string action, string[] flags, out string? error) {
        string? requester = null, kind = null, repo = null, vendor = null;

        for (var i = 0; i < flags.Length; i += 2) {
            if (i + 1 >= flags.Length) { error = $"missing value for {flags[i]}"; return null; }

            switch (flags[i]) {
                case "--requester": requester = flags[i + 1]; break;
                case "--kind":
                    if (flags[i + 1] is not ("agent" or "review" or "review-flow")) {
                        error = "invalid --kind (agent|review|review-flow)"; return null;
                    }
                    kind = flags[i + 1]; break;
                case "--repo": repo = flags[i + 1]; break;
                case "--vendor": vendor = flags[i + 1].ToLowerInvariant(); break;
                default: error = $"unknown flag {flags[i]}"; return null;
            }
        }

        if (requester is null && kind is null && repo is null && vendor is null) {
            error = "at least one of --requester/--kind/--repo/--vendor is required " +
                    "(for a catch-all use: kcap daemon consent set-default deny)";

            return null;
        }

        error = null;

        return new ConsentRuleDto(action, requester, kind, repo, vendor);
    }

    // ── remove ──────────────────────────────────────────────────────────────

    static async Task<int> RemoveAsync(string socketPath, string[] args) {
        var positional = WithoutName(args);

        if (positional.Length == 0 || !int.TryParse(positional[0], out var index)) {
            await Console.Error.WriteLineAsync("usage: kcap daemon consent remove <index>  (index as printed by `show`)");

            return 1;
        }

        var policy = await GetPolicyAsync(socketPath);
        if (policy is null) return 1;

        if (index < 0 || index >= policy.Rules.Count) {
            var range = policy.Rules.Count == 0 ? "no rules to remove" : $"index must be 0..{policy.Rules.Count - 1}";
            await Console.Error.WriteLineAsync($"kcap daemon consent remove: {range}");

            return 1;
        }

        var rules = policy.Rules.ToList();
        rules.RemoveAt(index);

        return await PutPolicyAsync(socketPath, policy with { Rules = rules });
    }

    // ── log ─────────────────────────────────────────────────────────────────

    static async Task<int> LogAsync(string[] args) {
        string name;
        try {
            name = ResolveName(args);
        } catch (ArgumentException ex) {
            await Console.Error.WriteLineAsync(ex.Message);

            return 1;
        }

        var n = ParseCount(args);

        var path       = Path.Combine(DaemonLockPaths.Directory, DaemonLockPaths.Sanitize(name), "consent-decisions.jsonl");
        var backupPath = path + ".1";

        List<string> tail;
        try {
            tail = ReadTail(path, backupPath, n);
        } catch (IOException ex) {
            // TryReadLines already absorbs one transient IOException with a retry; a second one
            // (e.g. a Windows sharing violation that doesn't clear) surfaces here as a controlled
            // exit rather than an unhandled crash of a human-facing command.
            await Console.Error.WriteLineAsync($"kcap: cannot read consent log: {ex.Message}");

            return 1;
        }

        // File.Exists here is a courtesy check for the friendly message only — ReadTail/
        // TryReadLines never rely on it, so a concurrent rotation can't turn this into the same
        // TOCTOU crash. An empty tail while a file DOES exist (fresh, no decisions yet) prints
        // nothing rather than a misleading "not found".
        if (tail.Count == 0 && !File.Exists(path) && !File.Exists(backupPath)) {
            await Console.Error.WriteLineAsync($"No consent decision log found at {path}.");

            return 1;
        }

        foreach (var line in tail) {
            await Console.Out.WriteLineAsync(line);
        }

        return 0;
    }

    static int ParseCount(string[] args) {
        var idx = Array.IndexOf(args, "-n");

        return idx >= 0 && idx + 1 < args.Length && int.TryParse(args[idx + 1], out var n) && n > 0 ? n : 20;
    }

    /// <summary>
    /// Last <paramref name="n"/> raw lines across the live file and its rotated <c>.1</c>
    /// backup (older entries), oldest of the selected lines first — i.e. the same order as the
    /// file itself, just windowed to the tail. The backup is only consulted when the live file
    /// doesn't have enough lines on its own.
    /// </summary>
    static List<string> ReadTail(string path, string backupPath, int n) {
        var live = TryReadLines(path);

        if (live.Count >= n) return live[^n..];

        var needed = n - live.Count;
        var backup = TryReadLines(backupPath);
        var fromBackup = backup.Count > needed ? backup[^needed..] : backup;

        return [.. fromBackup, .. live];
    }

    /// <summary>
    /// Reads the non-empty lines of <paramref name="path"/>, tolerating the check-then-open race
    /// the daemon's own rotation creates: <c>File.Exists</c> then <c>File.ReadAllLines</c> has a
    /// real window (the daemon's rotating <c>File.Move</c> to <c>.1</c>) in which the live path
    /// stops existing between the two calls, which a naive Exists-guard turns into an unhandled
    /// <see cref="FileNotFoundException"/>. Reading unconditionally and treating a not-found as
    /// "nothing here" instead converts that fault into exactly the accepted stale-snapshot
    /// semantics this tail already has (the caller's backup-file fallback picks up the rotated
    /// content). A transient <see cref="IOException"/> (e.g. a Windows sharing violation while
    /// the daemon holds the file open) gets one retry after a short delay before propagating —
    /// the caller decides how to report a persistent failure.
    /// </summary>
    internal static List<string> TryReadLines(string path) {
        try {
            return ReadNonEmptyLines(path);
        } catch (FileNotFoundException) {
            return [];
        } catch (DirectoryNotFoundException) {
            return [];
        } catch (IOException) {
            Thread.Sleep(50);

            try {
                return ReadNonEmptyLines(path);
            } catch (FileNotFoundException) {
                return [];
            } catch (DirectoryNotFoundException) {
                return [];
            }
            // A second IOException is deliberately NOT caught here — it propagates to the
            // caller, which reports a controlled failure instead of retrying forever.
        }
    }

    static List<string> ReadNonEmptyLines(string path) =>
        [.. File.ReadAllLines(path).Where(l => l.Length > 0)];

    // ── socket plumbing ─────────────────────────────────────────────────────

    static async Task<ConsentPolicyDto?> GetPolicyAsync(string socketPath) {
        try {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath));
            await using var stream = new NetworkStream(socket, ownsSocket: false);

            await FrameCodec.WriteAsync(stream, new LocalFrame(FrameType.ConsentRulesGet), default);
            var reply = await FrameCodec.ReadAsync(stream, default);

            switch (reply?.Type) {
                case FrameType.ConsentRules:
                    var dto = JsonSerializer.Deserialize(reply.Text, ConsentIpcJsonContext.Default.ConsentPolicyDto);
                    if (dto is not null) return dto;
                    await Console.Error.WriteLineAsync("kcap: malformed consent policy reply from daemon");

                    return null;
                case FrameType.Error:
                    await Console.Error.WriteLineAsync($"kcap: {reply.Text}");

                    return null;
                case null:
                    await Console.Error.WriteLineAsync(
                        "kcap: daemon closed the connection without replying (this daemon build may not support consent)");

                    return null;
                default:
                    await Console.Error.WriteLineAsync($"kcap: unexpected daemon response to consent rules get ({reply.Type})");

                    return null;
            }
        } catch (Exception ex) when (ex is SocketException or IOException) {
            await Console.Error.WriteLineAsync($"kcap: cannot reach daemon: {ex.Message}");

            return null;
        } catch (JsonException) {
            await Console.Error.WriteLineAsync("kcap: malformed consent policy reply from daemon");

            return null;
        }
    }

    static async Task<int> PutPolicyAsync(string socketPath, ConsentPolicyDto policy) {
        try {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath));
            await using var stream = new NetworkStream(socket, ownsSocket: false);

            var json = JsonSerializer.Serialize(policy, ConsentIpcJsonContext.Default.ConsentPolicyDto);
            await FrameCodec.WriteAsync(stream, LocalFrame.ConsentJson(FrameType.ConsentRulesPut, json), default);
            var reply = await FrameCodec.ReadAsync(stream, default);

            switch (reply?.Type) {
                case FrameType.ConsentAck:
                    var ack = JsonSerializer.Deserialize(reply.Text, ConsentIpcJsonContext.Default.ConsentAckDto);
                    if (ack is null) {
                        await Console.Error.WriteLineAsync("kcap: malformed consent ack from daemon");

                        return 1;
                    }

                    // Ok=true + Error = a partial-failure warning (e.g. the policy applied but a
                    // secondary detail didn't); Ok=false + Error = the failure detail. Only the
                    // latter should fail the command — see ConsentAckDto's contract.
                    if (ack.Error is not null) {
                        if (ack.Ok) await Console.Out.WriteLineAsync($"warning: {ack.Error}");
                        else await Console.Error.WriteLineAsync($"kcap: {ack.Error}");
                    }

                    return ack.Ok ? 0 : 1;
                case FrameType.Error:
                    await Console.Error.WriteLineAsync($"kcap: {reply.Text}");

                    return 1;
                case null:
                    await Console.Error.WriteLineAsync(
                        "kcap: daemon closed the connection without replying (this daemon build may not support consent)");

                    return 1;
                default:
                    await Console.Error.WriteLineAsync($"kcap: unexpected daemon response to consent rules put ({reply.Type})");

                    return 1;
            }
        } catch (Exception ex) when (ex is SocketException or IOException) {
            await Console.Error.WriteLineAsync($"kcap: cannot reach daemon: {ex.Message}");

            return 1;
        } catch (JsonException) {
            await Console.Error.WriteLineAsync("kcap: malformed consent ack from daemon");

            return 1;
        }
    }

    /// <summary>Strips a <c>--name &lt;value&gt;</c> pair (already consumed by <see cref="ResolveName"/>)
    /// out of an arg list before it's parsed further (rule flags, or a positional arg).</summary>
    static string[] WithoutName(string[] args) {
        List<string> result = [];

        for (var i = 0; i < args.Length; i++) {
            if (args[i] == "--name") { i++; continue; } // skip the flag and its value together
            result.Add(args[i]);
        }

        return [.. result];
    }

    static int PrintConsentUsage() {
        Console.Error.WriteLine("Usage: kcap daemon consent <show|set-default|allow|deny|remove|log> [--name <n>]");
        Console.Error.WriteLine();
        Console.Error.WriteLine("  show                          Print default, prompt timeout, and numbered rules");
        Console.Error.WriteLine("  set-default <allow|deny|prompt>  Set the verdict when no rule matches");
        Console.Error.WriteLine("  allow [--requester U] [--kind agent|review|review-flow] [--repo PATH] [--vendor V]");
        Console.Error.WriteLine("  deny  [--requester U] [--kind agent|review|review-flow] [--repo PATH] [--vendor V]");
        Console.Error.WriteLine("                                Append a rule (at least one flag is required)");
        Console.Error.WriteLine("  remove <index>                Remove the rule at the index shown by `show`");
        Console.Error.WriteLine("  log [-n N]                    Tail N lines (default 20) of consent-decisions.jsonl");
        Console.Error.WriteLine("                                (direct file read — works with the daemon stopped)");

        return 1;
    }
}
