# `kcap agent` Command Group Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the top-level `kcap run-agent` / `attach` / `ls` commands with a single `kcap agent start|ls|stop|attach` group, adding a local stop path that also works for `--private` agents.

**Architecture:** A new `AgentCommand` router in the CLI dispatches four subcommands, mirroring how `daemon`/`plugin`/`profile` already work. Short agent-id prefixes are resolved client-side against the existing `List` frame. Stop is a new append-only pair of local-IPC frames (`Stop = 8`, `StopAck = 70`) handled by a new `HandleLocalStopAsync` on the daemon, which calls a `StopAgentCoreAsync` extracted from today's `HandleStopAgent`.

**Tech Stack:** .NET 10, NativeAOT, TUnit (Microsoft Testing Platform), custom length-prefixed binary IPC over a Unix domain socket.

**Spec:** `docs/superpowers/specs/2026-07-28-ai1555-agent-command-group-design.md`

## Global Constraints

- **No back-compat aliases.** `run-agent`, `attach`, and `ls` are removed from the top-level switch entirely. Do not add hidden aliases.
- **`FrameType` is append-only.** Never renumber or reuse an existing value. New values: `Stop = 8`, `StopAck = 70`.
- **Server-origin stops must keep refusing private agents.** The `if (agent.IsPrivate) return;` guard stays in `HandleStopAgent`. Only the local-socket path bypasses it.
- **Unix-only.** The whole `agent` group keeps today's explicit Windows refusal.
- **Not an offline command.** Do **not** add `"agent"` to `offlineCommands` in `Program.cs:88`.
- **Docs land in the same PR** (repo rule): `README.md` *and* `help-*.txt`. Updating help alone is insufficient.
- **AOT:** verify with `dotnet publish -c Release`, not `dotnet build` — trimming warnings only surface on publish.
- **Comment style:** self-explanatory code over prose; no Linear issue numbers in comments (GitHub numbers only).
- Run tests as executables: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj`. Filter with `--treenode-filter`, never `--filter`.

**Known, unchanged wart:** `Program.cs:83` routes *any* `--help`/`-h` after the command to per-command help, so `kcap agent start claude -- --help` prints kcap's help rather than the vendor's. This is identical to today's `kcap run-agent claude -- --help` behaviour. Out of scope.

---

### Task 1: Rename `RunAgentArgs` → `AgentStartArgs` and respell its flags

**Files:**
- Rename: `src/Capacitor.Cli.Core/RunAgentArgs.cs` → `src/Capacitor.Cli.Core/AgentStartArgs.cs`
- Rename: `test/Capacitor.Cli.Tests.Unit/RunAgentArgsTests.cs` → `test/Capacitor.Cli.Tests.Unit/AgentStartArgsTests.cs`
- Modify: `src/Capacitor.Cli/Commands/RunAgentCommand.cs:17` (call site only — keeps the build green)

**Interfaces:**
- Produces: `Capacitor.Cli.Core.AgentStartArgs` with `static AgentStartArgs Parse(string[] args)` and properties `Vendor` (string), `Worktree` (bool), `DaemonName` (string?), `Detached` (bool), `Private` (bool), `Passthrough` (string[]), `Error` (string?). Flags accepted: `--worktree`, `--private`, `-d`/`--detach`, `--daemon <name>`.

- [ ] **Step 1: Rename both files with git mv**

```bash
git mv src/Capacitor.Cli.Core/RunAgentArgs.cs src/Capacitor.Cli.Core/AgentStartArgs.cs
git mv test/Capacitor.Cli.Tests.Unit/RunAgentArgsTests.cs test/Capacitor.Cli.Tests.Unit/AgentStartArgsTests.cs
```

- [ ] **Step 2: Rewrite the test file**

Replace the entire contents of `test/Capacitor.Cli.Tests.Unit/AgentStartArgsTests.cs` with:

```csharp
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit;

public class AgentStartArgsTests {
    [Test]
    public async Task Splits_kcap_flags_from_passthrough_at_double_dash() {
        var a = AgentStartArgs.Parse(["claude", "--worktree", "--daemon", "dev", "--", "--model", "opus", "fix"]);
        await Assert.That(a.Vendor).IsEqualTo("claude");
        await Assert.That(a.Worktree).IsTrue();
        await Assert.That(a.DaemonName).IsEqualTo("dev");
        await Assert.That(a.Passthrough).IsEquivalentTo(new[] { "--model", "opus", "fix" });
        await Assert.That(a.Error).IsNull();
    }

    [Test]
    public async Task Default_is_in_place_with_no_passthrough() {
        var a = AgentStartArgs.Parse(["codex"]);
        await Assert.That(a.Vendor).IsEqualTo("codex");
        await Assert.That(a.Worktree).IsFalse();
        await Assert.That(a.Passthrough).IsEmpty();
        await Assert.That(a.Error).IsNull();
    }

    [Test]
    public async Task Empty_args_is_an_error() {
        await Assert.That(AgentStartArgs.Parse([]).Error).IsNotNull();
    }

    [Test]
    public async Task Unknown_kcap_flag_before_dash_is_an_error() {
        var a = AgentStartArgs.Parse(["claude", "--model", "opus"]);
        await Assert.That(a.Error).IsNotNull();
    }

    [Test]
    public async Task Share_is_not_a_flag_sharing_is_a_ui_action() {
        // Sharing is server/UI-authoritative (tracks a future `kcap share` command),
        // so --share is just an unknown flag and is rejected.
        await Assert.That(AgentStartArgs.Parse(["claude", "--share"]).Error).IsNotNull();
    }

    [Test]
    public async Task Private_flag_is_parsed_and_defaults_false() {
        var on = AgentStartArgs.Parse(["claude", "--private", "--", "fix"]);
        await Assert.That(on.Private).IsTrue();
        await Assert.That(on.Passthrough).IsEquivalentTo(new[] { "fix" });
        await Assert.That(on.Error).IsNull();

        var off = AgentStartArgs.Parse(["claude"]);
        await Assert.That(off.Private).IsFalse();
    }

    [Test]
    public async Task Empty_passthrough_after_dash_is_allowed() {
        var a = AgentStartArgs.Parse(["claude", "--"]);
        await Assert.That(a.Error).IsNull();
        await Assert.That(a.Passthrough).IsEmpty();
    }

    [Test]
    public async Task Detach_parses_in_both_short_and_long_form() {
        await Assert.That(AgentStartArgs.Parse(["claude", "-d"]).Detached).IsTrue();
        await Assert.That(AgentStartArgs.Parse(["claude", "--detach"]).Detached).IsTrue();
        await Assert.That(AgentStartArgs.Parse(["claude"]).Detached).IsFalse();
    }

    [Test]
    public async Task Daemon_flag_requires_a_value() {
        await Assert.That(AgentStartArgs.Parse(["claude", "--daemon"]).Error).IsNotNull();
    }

    [Test]
    public async Task Removed_spellings_are_rejected_as_unknown_flags() {
        // The group deliberately keeps one spelling each: --daemon (was --name),
        // -d/--detach (was --detached). The old ones must not silently work.
        await Assert.That(AgentStartArgs.Parse(["claude", "--name", "dev"]).Error).IsNotNull();
        await Assert.That(AgentStartArgs.Parse(["claude", "--detached"]).Error).IsNotNull();
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/AgentStartArgsTests/*"`
Expected: FAIL — compile error, `AgentStartArgs` does not exist.

- [ ] **Step 4: Rewrite the parser**

Replace the entire contents of `src/Capacitor.Cli.Core/AgentStartArgs.cs` with:

```csharp
namespace Capacitor.Cli.Core;

/// Parses `agent start &lt;vendor&gt; [kcap flags] -- [agent args]`: kcap's own flags come
/// before <c>--</c>; everything after <c>--</c> is forwarded to the agent CLI verbatim.
public sealed class AgentStartArgs {
    public string   Vendor      { get; private set; } = "";
    public bool     Worktree    { get; private set; }
    public string?  DaemonName  { get; private set; }
    public bool     Detached    { get; private set; }
    public bool     Private     { get; private set; }
    public string[] Passthrough { get; private set; } = [];
    public string?  Error       { get; private set; }

    public static AgentStartArgs Parse(string[] args) {
        var r = new AgentStartArgs();

        if (args.Length == 0) {
            r.Error = "usage: kcap agent start <vendor> [--worktree] [--private] [--daemon <name>] [-d|--detach] [-- <agent args>]";

            return r;
        }

        var dash = Array.IndexOf(args, "--");
        var kcap = dash < 0 ? args : args[..dash];
        r.Passthrough = dash < 0 ? [] : args[(dash + 1)..];

        if (kcap.Length == 0) {
            r.Error = "missing <vendor>";

            return r;
        }

        r.Vendor = kcap[0];

        for (var i = 1; i < kcap.Length; i++) {
            switch (kcap[i]) {
                case "--worktree":        r.Worktree = true; break;
                case "-d" or "--detach":  r.Detached = true; break;
                case "--private":         r.Private  = true; break;
                case "--daemon":
                    if (i + 1 >= kcap.Length) { r.Error = "--daemon requires a value"; return r; }

                    r.DaemonName = kcap[++i];

                    break;
                default:
                    r.Error = $"unknown flag {kcap[i]} (agent args go after `--`)";

                    return r;
            }
        }

        return r;
    }
}
```

- [ ] **Step 5: Update the one call site so the solution still builds**

In `src/Capacitor.Cli/Commands/RunAgentCommand.cs:17`, change:

```csharp
        var parsed = RunAgentArgs.Parse(args);
```

to:

```csharp
        var parsed = AgentStartArgs.Parse(args);
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/AgentStartArgsTests/*"`
Expected: PASS (10 tests).

- [ ] **Step 7: Commit**

```bash
git add src/Capacitor.Cli.Core/AgentStartArgs.cs test/Capacitor.Cli.Tests.Unit/AgentStartArgsTests.cs src/Capacitor.Cli/Commands/RunAgentCommand.cs
git commit -m "refactor: rename RunAgentArgs to AgentStartArgs with --daemon and -d/--detach"
```

---

### Task 2: `AgentCommand` router replaces the three top-level commands

**Files:**
- Rename: `src/Capacitor.Cli/Commands/RunAgentCommand.cs` → `src/Capacitor.Cli/Commands/AgentCommand.cs`
- Modify: `src/Capacitor.Cli/Program.cs:277-282`
- Test: `test/Capacitor.Cli.Tests.Unit/AgentCommandRoutingTests.cs` (create)

**Interfaces:**
- Consumes: `AgentStartArgs.Parse` (Task 1).
- Produces: `internal static class AgentCommand` in namespace `Capacitor.Cli.Commands`, with `public static Task<int> HandleAsync(string[] args)` (receives the **full** argv, `args[0] == "agent"`), plus two test seams: `internal static (string Sub, string[] Rest) SplitSubcommand(string[] args)` and `internal static readonly string[] KnownSubcommands`.

- [ ] **Step 1: Write the failing routing tests**

Create `test/Capacitor.Cli.Tests.Unit/AgentCommandRoutingTests.cs`:

```csharp
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit;

public class AgentCommandRoutingTests {
    [Test]
    public async Task Bare_agent_routes_to_ls() {
        var (sub, rest) = AgentCommand.SplitSubcommand(["agent"]);
        await Assert.That(sub).IsEqualTo("ls");
        await Assert.That(rest).IsEmpty();
    }

    [Test]
    public async Task Subcommand_and_its_arguments_are_split() {
        var (sub, rest) = AgentCommand.SplitSubcommand(["agent", "stop", "ab12", "--daemon", "dev"]);
        await Assert.That(sub).IsEqualTo("stop");
        await Assert.That(rest).IsEquivalentTo(new[] { "ab12", "--daemon", "dev" });
    }

    [Test]
    public async Task Start_passthrough_survives_the_split_intact() {
        var (sub, rest) = AgentCommand.SplitSubcommand(["agent", "start", "claude", "--", "--model", "opus"]);
        await Assert.That(sub).IsEqualTo("start");
        await Assert.That(rest).IsEquivalentTo(new[] { "claude", "--", "--model", "opus" });
    }

    [Test]
    public async Task Known_subcommands_are_exactly_the_four_documented_verbs() {
        await Assert.That(AgentCommand.KnownSubcommands).IsEquivalentTo(new[] { "start", "ls", "stop", "attach" });
    }

    [Test]
    public async Task Unknown_subcommand_is_not_routable() {
        var (sub, _) = AgentCommand.SplitSubcommand(["agent", "frobnicate"]);
        await Assert.That(AgentCommand.KnownSubcommands).DoesNotContain(sub);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/AgentCommandRoutingTests/*"`
Expected: FAIL — compile error, `AgentCommand` does not exist.

- [ ] **Step 3: Rename the command file**

```bash
git mv src/Capacitor.Cli/Commands/RunAgentCommand.cs src/Capacitor.Cli/Commands/AgentCommand.cs
```

- [ ] **Step 4: Convert it into a router**

In `src/Capacitor.Cli/Commands/AgentCommand.cs`:

Replace the class declaration and its XML doc (lines 9-13) with:

```csharp
/// <summary>
/// `kcap agent start|ls|stop|attach` — drive daemon-hosted agents from the local
/// terminal over the daemon's local control socket.
/// </summary>
internal static class AgentCommand {
    internal static readonly string[] KnownSubcommands = ["start", "ls", "stop", "attach"];

    /// <summary>Bare `kcap agent` lists agents; otherwise argv[1] is the subcommand.</summary>
    internal static (string Sub, string[] Rest) SplitSubcommand(string[] args) =>
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
```

Then, still in the same file:

1. Change `public static async Task<int> RunAsync` to `static async Task<int> RunAsync`, and drop its now-redundant `if (NotSupportedOnWindows(out var rc)) return rc;` line (the router checks once).
2. In `RunAsync`, change the error prefix from `$"kcap run-agent: {parsed.Error}"` to `$"kcap agent start: {parsed.Error}"`.
3. In `RunAsync`, change `--name` to `--daemon` in the `ResolveName` argument construction — see `ResolveName` below.
4. Change `public static async Task<int> AttachAsync` to `static async Task<int> AttachAsync`, drop its `NotSupportedOnWindows` line, and change its usage string to `"usage: kcap agent attach <agent-id> [--daemon <name>]"`.
5. Change `public static async Task<int> ListAsync` to `static async Task<int> ListAsync` and drop its `NotSupportedOnWindows` line.
6. In `SpawnDetachedAsync`, change the printed hint from `` $"Started agent {id} (detached). Attach with: kcap attach {id}" `` to `` $"Started agent {id} (detached). Attach with: kcap agent attach {id}" ``.

Update the two helpers that still speak `--name`:

```csharp
    static string ResolveName(string? daemonName) {
        string[] args = daemonName is null ? [] : ["--name", daemonName];

        return DaemonNameResolver.Resolve(args, AppConfig.ResolvedProfile?.Profile?.Daemon?.Name);
    }

    static string? NameFrom(string[] args) {
        var i = Array.IndexOf(args, "--daemon");

        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
```

`ResolveName` keeps `"--name"` internally: that array is the argv `DaemonNameResolver.Resolve` expects, not a user-facing flag. Only `NameFrom` — which parses what the user typed — changes to `--daemon`.

Finally, update the Windows guard message:

```csharp
    static bool NotSupportedOnWindows(out int rc) {
        if (OperatingSystem.IsWindows()) {
            Console.Error.WriteLine("kcap agent is not supported on Windows yet.");
            rc = 1;

            return true;
        }

        rc = 0;

        return false;
    }
```

- [ ] **Step 5: Replace the three top-level cases in Program.cs**

In `src/Capacitor.Cli/Program.cs`, replace lines 277-282:

```csharp
    case "run-agent":
        return await RunAgentCommand.RunAsync(args[1..]);
    case "attach":
        return await RunAgentCommand.AttachAsync(args[1..]);
    case "ls":
        return await RunAgentCommand.ListAsync(args[1..]);
```

with:

```csharp
    case "agent":
        return await AgentCommand.HandleAsync(args);
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/AgentCommandRoutingTests/*"`
Expected: PASS (5 tests).

- [ ] **Step 7: Verify the old names are gone and the new one works**

```bash
dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj
dotnet run --project src/Capacitor.Cli/Capacitor.Cli.csproj -- ls
dotnet run --project src/Capacitor.Cli/Capacitor.Cli.csproj -- agent frobnicate
```

Expected: build succeeds; `ls` prints `Unknown command: ls` (Program.cs's default case); `agent frobnicate` prints the unknown-subcommand usage and exits 1.

- [ ] **Step 8: Commit**

```bash
git add src/Capacitor.Cli/Commands/AgentCommand.cs src/Capacitor.Cli/Program.cs test/Capacitor.Cli.Tests.Unit/AgentCommandRoutingTests.cs
git commit -m "feat(cli): group agent commands under `kcap agent`"
```

---

### Task 3: Resolve short agent-id prefixes for `attach`

**Files:**
- Modify: `src/Capacitor.Cli/Commands/AgentCommand.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/AgentIdResolutionTests.cs` (create)

**Interfaces:**
- Produces: `internal readonly record struct AgentRow(string Id, string Status, string Repo)` in namespace `Capacitor.Cli.Commands`; `internal static (string? Id, string? Error) ResolveAgentId(IReadOnlyList<AgentRow> agents, string given)` on `AgentCommand`; and a private `static Task<IReadOnlyList<AgentRow>?> FetchAgentsAsync(string sock)`.
- Consumes: the existing `FrameType.List` / `FrameType.AgentList` frames.

- [ ] **Step 1: Write the failing resolution tests**

Create `test/Capacitor.Cli.Tests.Unit/AgentIdResolutionTests.cs`:

```csharp
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit;

public class AgentIdResolutionTests {
    static readonly AgentRow[] Agents = [
        new("ab12cd34ef56ab78cd90ef12ab34cd56", "Running", "/repo/one"),
        new("ab99887766554433221100aabbccddee", "Running", "/repo/two"),
        new("ff00112233445566778899aabbccddee", "Completed", "/repo/three"),
    ];

    [Test]
    public async Task Unique_prefix_resolves_to_the_full_id() {
        var (id, err) = AgentCommand.ResolveAgentId(Agents, "ab12");
        await Assert.That(id).IsEqualTo("ab12cd34ef56ab78cd90ef12ab34cd56");
        await Assert.That(err).IsNull();
    }

    [Test]
    public async Task Prefix_matching_is_case_insensitive() {
        var (id, err) = AgentCommand.ResolveAgentId(Agents, "FF00");
        await Assert.That(id).IsEqualTo("ff00112233445566778899aabbccddee");
        await Assert.That(err).IsNull();
    }

    [Test]
    public async Task Ambiguous_prefix_is_an_error_that_names_the_candidates() {
        var (id, err) = AgentCommand.ResolveAgentId(Agents, "ab");
        await Assert.That(id).IsNull();
        await Assert.That(err).Contains("ab12cd34ef56ab78cd90ef12ab34cd56");
        await Assert.That(err).Contains("ab99887766554433221100aabbccddee");
    }

    [Test]
    public async Task No_match_is_an_error() {
        var (id, err) = AgentCommand.ResolveAgentId(Agents, "dead");
        await Assert.That(id).IsNull();
        await Assert.That(err).IsNotNull();
    }

    [Test]
    public async Task Full_32_hex_id_passes_through_even_when_not_listed() {
        // A survivor of a prior daemon incarnation is not in the live list, but the
        // daemon can still reap it by PID record — so a full id must not be filtered out.
        var (id, err) = AgentCommand.ResolveAgentId([], "0123456789abcdef0123456789abcdef");
        await Assert.That(id).IsEqualTo("0123456789abcdef0123456789abcdef");
        await Assert.That(err).IsNull();
    }

    [Test]
    public async Task A_32_char_non_hex_string_is_still_treated_as_a_prefix() {
        var (id, err) = AgentCommand.ResolveAgentId(Agents, new string('z', 32));
        await Assert.That(id).IsNull();
        await Assert.That(err).IsNotNull();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/AgentIdResolutionTests/*"`
Expected: FAIL — compile error, `AgentRow` and `ResolveAgentId` do not exist.

- [ ] **Step 3: Add the row type and the resolver**

In `src/Capacitor.Cli/Commands/AgentCommand.cs`, add above the `AgentCommand` class declaration:

```csharp
/// One row of the daemon's agent table (`id\tstatus\tcwd` on the wire).
internal readonly record struct AgentRow(string Id, string Status, string Repo);
```

Then add to the class, next to the other helpers:

```csharp
    /// <summary>A full agent id as minted by `Guid.NewGuid().ToString("N")`.</summary>
    internal static bool IsFullAgentId(string s) => s.Length == 32 && s.All(char.IsAsciiHexDigit);

    /// <summary>
    /// A full 32-hex id is used verbatim — it may name an agent that survived a previous
    /// daemon incarnation and so is absent from the live list, which the daemon can still
    /// reap by PID record. Anything shorter is a prefix and must match exactly one agent.
    /// </summary>
    internal static (string? Id, string? Error) ResolveAgentId(IReadOnlyList<AgentRow> agents, string given) {
        if (IsFullAgentId(given)) return (given, null);

        var hits = agents.Where(a => a.Id.StartsWith(given, StringComparison.OrdinalIgnoreCase)).ToList();

        return hits.Count switch {
            1 => (hits[0].Id, null),
            0 => (null, $"no agent matching '{given}'"),
            _ => (null, $"'{given}' matches {hits.Count} agents:{string.Concat(hits.Select(h => $"\n  {h.Id}  {h.Repo}"))}"),
        };
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/AgentIdResolutionTests/*"`
Expected: PASS (6 tests).

- [ ] **Step 5: Extract the socket fetch so `ls` and the resolver share it**

Still in `AgentCommand.cs`, replace the body of `ListAsync` and add `FetchAgentsAsync`:

```csharp
    static async Task<int> ListAsync(string[] args) {
        var sock = LocalSocketPaths.Socket(ResolveName(NameFrom(args)));

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

    /// <summary>Asks the daemon for its agent table. Returns null after reporting a transport error.</summary>
    static async Task<IReadOnlyList<AgentRow>?> FetchAgentsAsync(string sock) {
        try {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(sock));
            await using var stream = new NetworkStream(socket, ownsSocket: false);

            await FrameCodec.WriteAsync(stream, new LocalFrame(FrameType.List), default);
            var resp = await FrameCodec.ReadAsync(stream, default);

            if (resp is null || resp.Type != FrameType.AgentList || resp.Text.Length == 0) return [];

            return [.. resp.Text.Split('\n')
                .Select(l => l.Split('\t'))
                .Where(p => p.Length == 3)
                .Select(p => new AgentRow(p[0], p[1], p[2]))];
        } catch (Exception ex) when (ex is SocketException or IOException) {
            await Console.Error.WriteLineAsync($"kcap: cannot reach daemon: {ex.Message}");

            return null;
        }
    }
```

- [ ] **Step 6: Wire prefix resolution into `attach`**

Replace `AttachAsync` with:

```csharp
    static async Task<int> AttachAsync(string[] args) {
        if (args.Length == 0 || args[0].StartsWith('-')) {
            await Console.Error.WriteLineAsync("usage: kcap agent attach <agent-id> [--daemon <name>]");

            return 1;
        }

        var sock = LocalSocketPaths.Socket(ResolveName(NameFrom(args)));

        if (!File.Exists(sock)) {
            await Console.Error.WriteLineAsync($"kcap: no daemon socket at {sock}");

            return 1;
        }

        var agentId = await ResolveOrReportAsync(sock, args[0]);
        if (agentId is null) return 1;

        return await LocalAgentClient.RunAsync(sock, new LocalFrame(FrameType.Attach) { Text = agentId }, CancellationToken.None);
    }

    /// <summary>Resolves an id or prefix, printing the reason and returning null on failure.</summary>
    static async Task<string?> ResolveOrReportAsync(string sock, string given) {
        if (IsFullAgentId(given)) return given; // skip the round-trip; ResolveAgentId agrees

        var agents = await FetchAgentsAsync(sock);
        if (agents is null) return null;

        var (id, error) = ResolveAgentId(agents, given);
        if (error is not null) await Console.Error.WriteLineAsync($"kcap: {error}");

        return id;
    }
```

- [ ] **Step 7: Verify the build and the whole unit suite**

```bash
dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj
```

Expected: build succeeds, all unit tests pass.

- [ ] **Step 8: Commit**

```bash
git add src/Capacitor.Cli/Commands/AgentCommand.cs test/Capacitor.Cli.Tests.Unit/AgentIdResolutionTests.cs
git commit -m "feat(cli): resolve short agent-id prefixes for `agent attach`"
```

---

### Task 4: `Stop` / `StopAck` frame types

**Files:**
- Modify: `src/Capacitor.Cli.Core/LocalIpc/FrameType.cs`
- Modify: `src/Capacitor.Cli.Core/LocalIpc/FrameCodec.cs:47-63` (the `Encode` and `Decode` switches)
- Modify: `src/Capacitor.Cli.Core/LocalIpc/LocalFrame.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/FrameCodecTests.cs`

**Interfaces:**
- Produces: `FrameType.Stop = 8`, `FrameType.StopAck = 70`; `LocalFrame.Stop(string agentId)` and `LocalFrame.StopAck(string ids)` factories. Both carry their payload in `Text`.

- [ ] **Step 1: Write the failing round-trip tests**

Append to `test/Capacitor.Cli.Tests.Unit/FrameCodecTests.cs`, inside the class:

```csharp
    [Test]
    public async Task Stop_round_trips_the_agent_id() {
        var r = await RoundTrip(LocalFrame.Stop("ab12cd34ef56ab78cd90ef12ab34cd56"));
        await Assert.That(r.Type).IsEqualTo(FrameType.Stop);
        await Assert.That(r.Text).IsEqualTo("ab12cd34ef56ab78cd90ef12ab34cd56");
    }

    [Test]
    public async Task Stop_with_an_empty_id_means_all_agents() {
        var r = await RoundTrip(LocalFrame.Stop(""));
        await Assert.That(r.Type).IsEqualTo(FrameType.Stop);
        await Assert.That(r.Text).IsEqualTo("");
    }

    [Test]
    public async Task StopAck_round_trips_a_newline_separated_id_list() {
        var r = await RoundTrip(LocalFrame.StopAck("id-one\nid-two"));
        await Assert.That(r.Type).IsEqualTo(FrameType.StopAck);
        await Assert.That(r.Text.Split('\n')).IsEquivalentTo(new[] { "id-one", "id-two" });
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/FrameCodecTests/*"`
Expected: FAIL — compile error, `LocalFrame.Stop` does not exist.

- [ ] **Step 3: Add the frame types**

In `src/Capacitor.Cli.Core/LocalIpc/FrameType.cs`, add `Stop` after `Restart` and `StopAck` after `RestartAck`:

```csharp
    Restart = 7,   // request restart-after-update (Text = "when-idle"|"now"|"force")
    Stop    = 8,   // stop an agent (Text = agent id; empty = every agent this daemon hosts)
    // daemon → client
    Attached  = 64,
    Stdout    = 65,
    Exited    = 66,
    Error     = 67,
    AgentList = 68, // UTF-8 table payload: one `id\tstatus\tcwd` line per agent
    RestartAck = 69, // acknowledgement for Restart (Text = short status)
    StopAck    = 70, // acknowledgement for Stop (Text = stopped ids, one per line)
```

- [ ] **Step 4: Teach the codec about them**

In `src/Capacitor.Cli.Core/LocalIpc/FrameCodec.cs`, add both to the text-payload arms of `Encode` and `Decode`:

```csharp
        FrameType.Error or FrameType.Attach or FrameType.AgentList
            or FrameType.Restart or FrameType.RestartAck
            or FrameType.Stop or FrameType.StopAck => Encoding.UTF8.GetBytes(f.Text),
```

```csharp
        FrameType.Error or FrameType.Attach or FrameType.AgentList
            or FrameType.Restart or FrameType.RestartAck
            or FrameType.Stop or FrameType.StopAck => new(t) { Text = Encoding.UTF8.GetString(p) },
```

- [ ] **Step 5: Add the factories**

In `src/Capacitor.Cli.Core/LocalIpc/LocalFrame.cs`, after `RestartAck`:

```csharp
    public static LocalFrame Stop(string agentId)       => new(FrameType.Stop)       { Text = agentId };
    public static LocalFrame StopAck(string stoppedIds) => new(FrameType.StopAck)    { Text = stoppedIds };
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/FrameCodecTests/*"`
Expected: PASS, including the three new tests.

- [ ] **Step 7: Commit**

```bash
git add src/Capacitor.Cli.Core/LocalIpc/FrameType.cs src/Capacitor.Cli.Core/LocalIpc/FrameCodec.cs src/Capacitor.Cli.Core/LocalIpc/LocalFrame.cs test/Capacitor.Cli.Tests.Unit/FrameCodecTests.cs
git commit -m "feat(ipc): add Stop/StopAck local frames"
```

---

### Task 5: Daemon-side local stop

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs:1663-1732` (extract `StopAgentCoreAsync`)
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.LocalIpc.cs` (add `HandleLocalStopAsync`)
- Modify: `src/Capacitor.Cli.Daemon/Services/LocalControlServer.cs:42-48`
- Test: `test/Capacitor.Cli.Tests.Unit/AgentOrchestratorLocalAttachTests.cs`

**Interfaces:**
- Consumes: `FrameType.Stop`, `LocalFrame.StopAck` (Task 4); existing `SeedAgentForTest`, `GetAgentForTest`, `BuildOrchestrator`, `TripwireServerConnection`, `DuplexTestStream`, `SpyPtyProcessFactory` test seams.
- Produces: `public Task HandleLocalStopAsync(string agentId, Stream stream, CancellationToken ct)` on `AgentOrchestrator`; private `async Task StopAgentCoreAsync(AgentInstance agent)`.

- [ ] **Step 1: Write the failing daemon tests**

Append to `test/Capacitor.Cli.Tests.Unit/AgentOrchestratorLocalAttachTests.cs`, inside the `AgentOrchestratorVendorTests` partial class:

```csharp
    static async Task<LocalFrame?> StopAndReadReply(AgentOrchestrator orch, string agentId) {
        using var client = new DuplexTestStream(new MemoryStream(), new MemoryStream());
        await orch.HandleLocalStopAsync(agentId, client, default);
        client.WrittenStream.Position = 0;

        return await FrameCodec.ReadAsync(client.WrittenStream, default);
    }

    [Test]
    public async Task Local_stop_stops_a_private_agent_without_touching_the_server() {
        // The server-origin path refuses private agents by design; a local stop must not,
        // or a `--private` agent could never be stopped from the CLI at all.
        var server = new TripwireServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
        orch.SeedAgentForTest("priv-1", isPrivate: true);

        var reply = await StopAndReadReply(orch, "priv-1");

        await Assert.That(reply!.Type).IsEqualTo(FrameType.StopAck);
        await Assert.That(reply.Text).IsEqualTo("priv-1");
        await Assert.That(orch.GetAgentForTest("priv-1")!.Status).IsEqualTo("Completed");
        await Assert.That(server.Calls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Local_stop_of_a_registered_agent_still_reports_to_the_server() {
        var server = new TripwireServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
        orch.SeedAgentForTest("pub-1");

        var reply = await StopAndReadReply(orch, "pub-1");

        await Assert.That(reply!.Type).IsEqualTo(FrameType.StopAck);
        await Assert.That(server.Calls).Contains(nameof(ServerConnection.AgentStatusChangedAsync));
    }

    [Test]
    public async Task Local_stop_with_an_empty_id_stops_every_agent() {
        var server = new TripwireServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
        orch.SeedAgentForTest("a-1");
        orch.SeedAgentForTest("a-2", isPrivate: true);

        var reply = await StopAndReadReply(orch, "");

        await Assert.That(reply!.Type).IsEqualTo(FrameType.StopAck);
        await Assert.That(reply.Text.Split('\n')).IsEquivalentTo(new[] { "a-1", "a-2" });
        await Assert.That(orch.GetAgentForTest("a-1")!.Status).IsEqualTo("Completed");
        await Assert.That(orch.GetAgentForTest("a-2")!.Status).IsEqualTo("Completed");
    }

    [Test]
    public async Task Local_stop_of_an_unknown_id_with_no_pid_record_is_an_error() {
        var server = new TripwireServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        var reply = await StopAndReadReply(orch, "ghost");

        await Assert.That(reply!.Type).IsEqualTo(FrameType.Error);
        await Assert.That(reply.Text).Contains("ghost");
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/AgentOrchestratorVendorTests/Local_stop*"`
Expected: FAIL — compile error, `HandleLocalStopAsync` does not exist.

- [ ] **Step 3: Extract `StopAgentCoreAsync`**

In `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs`, replace `HandleStopAgent` (lines 1663-1732) with the following. The body is unchanged apart from being moved into the core and having the two `_server.*` calls guarded — keep every existing comment in place:

```csharp
    internal async Task HandleStopAgent(string agentId) {
        if (!_agents.TryGetValue(agentId, out var agent)) {
            // Phase B (D4 §6.4(3)): no in-memory agent — this may be a survivor of a PRIOR
            // daemon incarnation the server is still trying to stop (S2). Fall back to the PID record:
            // reap by exact identity if a matching live process is still there.
            await TryStopByPidRecordAsync(agentId);
            return;
        }

        // Defence-in-depth: a --private agent is invisible to the server (unregistered, not in
        // LiveAgentIds), so never act on a server-origin command for one even if its id leaks.
        // The local-socket path (HandleLocalStopAsync) deliberately bypasses this — that request
        // comes from the owner of the 0600 socket, not from the server.
        if (agent.IsPrivate) return;

        await StopAgentCoreAsync(agent);
    }

    /// <summary>
    /// The stop itself, with no caller-authorisation policy: graceful /exit, then terminate.
    /// Server-origin stops reach this through <see cref="HandleStopAgent"/> (which refuses
    /// private agents); local-socket stops call it directly.
    /// </summary>
    async Task StopAgentCoreAsync(AgentInstance agent) {
        var agentId = agent.Id;

        try {
            LogStopping(agentId);

            // Set status BEFORE cancelling ReadCts so the read loop's finally
            // block sees "Completed" and skips its own status change / event append.
            agent.Status = "Completed";
            // Mark this as a user-initiated stop so the read-loop's finally-block
            // EndAgentSessionAsync call uses "agent_stopped" if it ends up being
            // the only successful call (e.g., transient SignalR failure here).
            // Phase B (D3): but PRESERVE a backstop reason the heartbeat already stamped
            // (reviewer_ttl_expired / reviewer_idle_expired) — only overwrite the "agent_exited"
            // default, so server-side attribution can tell a TTL/idle reap from a user stop.
            if (agent.PendingEndReason == "agent_exited") agent.PendingEndReason = "agent_stopped";

            // An unregistered agent has no server-side row to update.
            if (!agent.IsPrivate) {
                _ = _server.AgentStatusChangedAsync(agentId, "Completed", agent.SessionId);
                _ = _server.AppendAgentRunEventAsync(agentId, new AgentRunStopped("user", null));
            }

            // Try a graceful shutdown first: send /exit so claude can fire its own
            // session-end hook (drains transcript, writes SessionEnded + summary,
            // optionally schedules what's-done). Falls through to SIGTERM/SIGKILL
            // below if claude doesn't exit in time.
            //
            // Claude CLI requires the slash-command text and the Enter key to arrive
            // as separate PTY writes (with a small delay between them) — sending them
            // in a single write makes Claude treat the carriage return as part of the
            // command buffer instead of a submit. HandleSendInput uses the same split
            // pattern; matching it here makes the graceful path actually fire.
            try {
                await agent.Runtime.RequestGracefulStopAsync();
                await agent.Runtime.WaitForExitAsync(GracefulExitWait);
            } catch (Exception ex) {
                LogGracefulExitFailed(ex, agentId);
            }

            // PTY WaitForExitAsync(timeout) returns silently when the timeout elapses,
            // so a graceful-exit *timeout* doesn't throw. Check HasExited explicitly
            // so we can tell from logs whether the graceful path is actually working
            // in production or if claude is consistently being SIGTERMed instead.
            if (!agent.Runtime.HasExited) {
                LogGracefulExitTimedOut(agentId, GracefulExitWait.TotalSeconds);
            }

            // Cancel the read loop and terminate the process. We deliberately do NOT end
            // the AgentSession here: EndAgentSessionAsync now retries across SignalR
            // reconnects (so it can block while a dropped connection recovers), and a
            // user-initiated stop must not wait on that. Cancelling ReadCts unblocks the
            // read loop, whose finally block runs FinalizeAgentRunAsync once the process
            // exits — that ends the session (with retry) using agent.PendingEndReason
            // ("agent_stopped") and spawns the what's-done generator if the server asks.
            // So session-end is reliable as the post-exit backstop without delaying
            // teardown, and is idempotent: if claude already fired its own session-end
            // during the graceful window above, the backstop call is a server-side no-op.
            await agent.ReadCts.CancelAsync();
            await agent.Runtime.TerminateAsync(TimeSpan.FromSeconds(10));
        } catch (Exception ex) {
            LogStopError(ex, agentId);
        }
    }
```

- [ ] **Step 4: Add the local stop handler**

In `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.LocalIpc.cs`, add after `HandleLocalListAsync`:

```csharp
    /// <summary>
    /// Stop one agent (or every agent, when <paramref name="agentId"/> is empty) on behalf of
    /// `kcap agent stop`. Calls the stop core directly rather than <c>HandleStopAgent</c>: the
    /// private-agent guard there defends against server-origin commands, and a request arriving
    /// on the daemon's own 0600 socket is the owner's. Stops run concurrently — each can take up
    /// to 25s (graceful wait plus terminate), so serial teardown would be unusable.
    /// </summary>
    public async Task HandleLocalStopAsync(string agentId, Stream stream, CancellationToken ct) {
        if (agentId.Length == 0) {
            var all = _agents.Values.ToList();
            await Task.WhenAll(all.Select(StopAgentCoreAsync));
            await FrameCodec.WriteAsync(stream, LocalFrame.StopAck(string.Join('\n', all.Select(a => a.Id))), ct);

            return;
        }

        if (_agents.TryGetValue(agentId, out var agent)) {
            await StopAgentCoreAsync(agent);
            await FrameCodec.WriteAsync(stream, LocalFrame.StopAck(agentId), ct);

            return;
        }

        // Not live here — it may be a survivor of a previous daemon incarnation, which the PID
        // record can still reap. This is why the client sends full ids verbatim.
        var reaped = await TryStopByPidRecordAsync(agentId);

        await FrameCodec.WriteAsync(
            stream,
            reaped ? LocalFrame.StopAck(agentId) : LocalFrame.Error($"no such agent {agentId}"),
            ct);
    }
```

- [ ] **Step 5: Route the frame**

In `src/Capacitor.Cli.Daemon/Services/LocalControlServer.cs`, add a case and update the fallback message:

```csharp
                case FrameType.Spawn:  await orchestrator.HandleLocalSpawnAsync(first, stream, ct); break;
                case FrameType.Attach: await orchestrator.HandleLocalAttachAsync(first.Text, stream, ct); break;
                case FrameType.List:   await orchestrator.HandleLocalListAsync(stream, ct); break;
                case FrameType.Stop:   await orchestrator.HandleLocalStopAsync(first.Text, stream, ct); break;
                case FrameType.Restart: await HandleRestartAsync(first.Text, stream, ct); break;
                default: await FrameCodec.WriteAsync(stream, LocalFrame.Error($"expected Spawn/Attach/List/Stop/Restart, got {first.Type}"), ct); break;
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/AgentOrchestratorVendorTests/Local_stop*"`
Expected: PASS (4 tests).

- [ ] **Step 7: Run the whole unit suite for stop-path regressions**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj`
Expected: PASS. The existing `HandleStopAgentForTest` / `HandleStopAgentV2ForTest` tests exercise the server-origin path and must be unaffected — if any fail, the extraction changed behaviour and must be corrected rather than the test.

- [ ] **Step 8: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.LocalIpc.cs src/Capacitor.Cli.Daemon/Services/LocalControlServer.cs test/Capacitor.Cli.Tests.Unit/AgentOrchestratorLocalAttachTests.cs
git commit -m "feat(daemon): stop agents over the local control socket"
```

---

### Task 6: `kcap agent stop` client subcommand

**Files:**
- Modify: `src/Capacitor.Cli/Commands/AgentCommand.cs`

**Interfaces:**
- Consumes: `ResolveOrReportAsync`, `FetchAgentsAsync`, `NameFrom`, `ResolveName` (Task 3); `LocalFrame.Stop`, `FrameType.StopAck` (Task 4).
- Produces: `static Task<int> StopAsync(string[] args)` wired into the router's switch.

- [ ] **Step 1: Add the stop subcommand**

In `src/Capacitor.Cli/Commands/AgentCommand.cs`, add:

```csharp
    static async Task<int> StopAsync(string[] args) {
        var all = args.Contains("--all");
        var yes = args.Contains("--yes") || args.Contains("-y");

        if (!all && (args.Length == 0 || args[0].StartsWith('-'))) {
            await Console.Error.WriteLineAsync("usage: kcap agent stop <agent-id> [--daemon <name>]");
            await Console.Error.WriteLineAsync("       kcap agent stop --all [-y] [--daemon <name>]");

            return 1;
        }

        var sock = LocalSocketPaths.Socket(ResolveName(NameFrom(args)));

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

        return await SendStopAsync(sock, target);
    }

    static async Task<int> SendStopAsync(string sock, string agentId) {
        try {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(sock));
            await using var stream = new NetworkStream(socket, ownsSocket: false);

            await FrameCodec.WriteAsync(stream, LocalFrame.Stop(agentId), default);
            var resp = await FrameCodec.ReadAsync(stream, default);

            switch (resp?.Type) {
                case FrameType.StopAck:
                    // Explicit type: `[]` has no natural type, so `var` would not compile here.
                    string[] ids = resp.Text.Length == 0 ? [] : resp.Text.Split('\n');
                    foreach (var id in ids) Console.WriteLine($"Stopped {id}.");
                    if (ids.Length == 0) Console.WriteLine("No agents.");

                    return 0;
                case FrameType.Error:
                    await Console.Error.WriteLineAsync($"kcap: {resp.Text}");

                    return 1;
                case null:
                    // An older daemon can't decode frame type 8: it faults before its frame
                    // switch and closes without replying, so we see a clean EOF.
                    await Console.Error.WriteLineAsync(
                        "kcap: this daemon is too old for `agent stop` — restart it with `kcap daemon restart`");

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
```

- [ ] **Step 2: Wire it into the router**

In `HandleAsync`, add the case:

```csharp
            case "stop":   return await StopAsync(rest);
```

- [ ] **Step 3: Verify end to end against a live daemon**

```bash
dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj
dotnet run --project src/Capacitor.Cli/Capacitor.Cli.csproj -- daemon start -d
dotnet run --project src/Capacitor.Cli/Capacitor.Cli.csproj -- agent start claude --detach
dotnet run --project src/Capacitor.Cli/Capacitor.Cli.csproj -- agent
dotnet run --project src/Capacitor.Cli/Capacitor.Cli.csproj -- agent stop <first-4-chars-of-the-id>
dotnet run --project src/Capacitor.Cli/Capacitor.Cli.csproj -- agent stop --all
```

Expected: `agent` prints the table; the prefix stop prints `Stopped <full-id>.`; `--all` prints `No agents.` once the only agent is gone.

- [ ] **Step 4: Run the full unit suite**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli/Commands/AgentCommand.cs
git commit -m "feat(cli): add `kcap agent stop` with --all"
```

---

### Task 7: Documentation and final verification

**Files:**
- Create: `src/Capacitor.Cli.Core/Resources/help-agent.txt`
- Modify: `src/Capacitor.Cli.Core/Resources/help-usage.txt:29-36`
- Modify: `README.md:23`, `README.md:236`, `README.md:911-933`

- [ ] **Step 1: Write the per-command help**

Create `src/Capacitor.Cli.Core/Resources/help-agent.txt` (the `Resources\**\*` glob embeds it automatically — no csproj change):

```
kcap agent — Run and manage daemon-hosted coding agents

Usage: kcap agent <subcommand>

Subcommands:
  start <vendor> [flags] [-- <agent args>]
                          Start an agent the daemon hosts for you
  ls                      List this daemon's agents (id, status, repo)
  attach <agent-id>       Attach your terminal to a running agent
  stop <agent-id>         Stop an agent (graceful /exit, then terminate)
  stop --all [-y]         Stop every agent this daemon hosts

Running `kcap agent` with no subcommand lists agents.

Options for start:
  --worktree              Run in a throwaway git worktree instead of in place
  --private               Keep the agent purely local: unregistered, not streamed
                          to the server, not shown in the web UI, and permission
                          prompts answered natively in your terminal
  -d, --detach            Start without attaching; prints the agent id
  --daemon <name>         Target a specific daemon (defaults to your profile's)
  --                      Everything after this is passed to the vendor CLI verbatim

Options for stop:
  --all                   Stop every agent, including --private ones
  --yes, -y               Skip the confirmation prompt for --all

Options for ls, attach, stop:
  --daemon <name>         Target a specific daemon (defaults to your profile's)

Agent ids:
  Any unique prefix works — `kcap agent attach ab12` is enough as long as it
  matches exactly one agent. An ambiguous prefix lists the candidates.

Notes:
  The daemon owns the agent, not your terminal, so you can detach and the agent
  keeps running. Detach with the prefix key Ctrl-Q then d, and reattach later
  with `kcap agent attach`.

  By default an agent is registered with the server: it appears in your own web
  UI immediately (owner-only until you share it) and you can drive it from the
  browser. `--private` opts out entirely.

  `start` auto-starts a daemon if none is running. Unix only for now.
```

- [ ] **Step 2: Add the group to the top-level usage**

In `src/Capacitor.Cli.Core/Resources/help-usage.txt`, insert a new section immediately **before** the existing `Daemon:` block (line 29):

```
Agents:
  agent start <vendor> [-d] [--worktree] [--private] [-- <args>]  Start a daemon-hosted agent
  agent ls                         List this daemon's agents (id, status, repo)
  agent attach <id>                Attach your terminal to a running agent (id prefix ok)
  agent stop <id> | --all [-y]     Stop one agent, or all of them

```

- [ ] **Step 3: Update the README section**

In `README.md`, replace lines 911-933 (the whole `### Local agents (run-agent / attach / ls)` section) with:

````markdown
### Local agents (`kcap agent`)

Start a coding agent from your own terminal that the daemon hosts for you. Because the daemon owns the agent (not your terminal), you can **detach and the agent keeps running**, then **re-attach later** — like `tmux` for your coding agent.

```bash
kcap agent start claude                       # start Claude in the current directory, attached
kcap agent start claude -- --model opus       # everything after `--` is passed to the agent CLI verbatim
kcap agent start codex --worktree -- -m gpt-5 # run in an isolated git worktree instead of in place
kcap agent start claude -d                    # start without attaching; prints the agent id
```

- **`--` boundary:** flags before `--` are kcap's; everything after `--` is forwarded to the `claude`/`codex` CLI unchanged. kcap flags: `--worktree`, `--private`, `--daemon <name>`, `-d`/`--detach`.
- **Visibility:** by default the agent is **registered with the server**, so it appears in your own web UI immediately and you can drive it from the browser — start in the terminal, continue from anywhere. It is **visible only to you** until you share it. Pass `--private` to keep it purely local: unregistered, not streamed to the server, and not shown in the web UI.
- **Work location:** by default the agent runs **in place in your current directory** (it edits your real files). Pass `--worktree` to run in a throwaway git worktree instead.
- **Detach** without stopping the agent with the prefix key **`Ctrl-Q` then `d`**. The agent keeps running in the daemon.
- **Permissions:** for a registered agent, permission prompts appear in the web UI (the same dialog as hosted agents); with `--private`, prompts are answered natively in your terminal.

```bash
kcap agent                 # no subcommand — same as `kcap agent ls`
kcap agent ls              # list daemon-hosted agents (id, status, repo)
kcap agent attach ab12     # re-attach your terminal (any unique id prefix works)
kcap agent stop ab12       # graceful /exit, then terminate
kcap agent stop --all -y   # stop every agent this daemon hosts, no prompt
```

Agent ids are long, so `attach` and `stop` accept **any unique prefix** — an ambiguous one lists the candidates instead of guessing. `stop --all` includes `--private` agents and prompts for confirmation unless you pass `-y`.

`agent start` auto-starts the daemon if one isn't already running. It needs a configured server (like the rest of kcap) — it is not an offline command. A locally-started agent appears in **your own** web UI (owner-only until you share it from the web UI); use `--private` to opt out of registration entirely. Unix only for now.
````

- [ ] **Step 4: Update the two README cross-references**

In `README.md` line 23, replace `[run-agent / attach / ls](#local-agents-run-agent--attach--ls)` with `[agent](#local-agents-kcap-agent)`.

In `README.md` line 236, replace the table cell `` [`kcap run-agent` / `attach` / `ls`](#local-agents-run-agent--attach--ls) `` with `` [`kcap agent`](#local-agents-kcap-agent) `` and update its description to `Start, list, attach to, and stop daemon-hosted agents`.

- [ ] **Step 5: Verify the help renders**

```bash
dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj
dotnet run --project src/Capacitor.Cli/Capacitor.Cli.csproj -- agent --help
dotnet run --project src/Capacitor.Cli/Capacitor.Cli.csproj -- --help
```

Expected: `agent --help` prints the new page; `--help` shows the new *Agents* section.

- [ ] **Step 6: Confirm no stale references survive**

```bash
grep -rn "run-agent\|kcap attach\|kcap ls" README.md src/ --exclude-dir=bin --exclude-dir=obj
```

Expected: no hits in `README.md` or `src/`. Hits under `docs/superpowers/plans/` are historical records of past work and must be left alone.

- [ ] **Step 7: Run both test suites**

```bash
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj
dotnet run --project test/Capacitor.Cli.Tests.Integration/Capacitor.Cli.Tests.Integration.csproj
```

Expected: PASS.

- [ ] **Step 8: Verify AOT publishes clean**

```bash
dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'
```

Expected: no output (no IL3050/IL2026 warnings).

- [ ] **Step 9: Commit**

```bash
git add src/Capacitor.Cli.Core/Resources/help-agent.txt src/Capacitor.Cli.Core/Resources/help-usage.txt README.md
git commit -m "docs: document the kcap agent command group"
```

---

## PR

The PR description must reference both trackers (title stays clean):

- `Closes #<github-issue>` — open one if it doesn't exist yet
- `AI-1555` so Linear links the PR to the imported issue

Mention in the body that `run-agent`, `attach`, and `ls` are **removed**, not aliased, and that follow-up [#379](https://github.com/kurrent-io/kcap-cli/issues/379) tracks flow-participant awareness for this group.
