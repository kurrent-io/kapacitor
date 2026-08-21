# Daemon Launch-Consent Engine + Control IPC (AI-1623) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The daemon decides allow/deny/prompt for every server-driven agent launch from a daemon-owned policy file, records every decision to a local JSONL log, exposes consent over the local socket for a future desktop app, and denies with the coded `launch_denied_by_owner:` reason — headless-complete, upgrade-safe (default `allow`).

**Architecture:** A pure rule engine (`LaunchConsentEngine`, modeled on `UnattendedLaunchPolicy`) evaluated at the single launch choke point `AgentOrchestrator.HandleLaunchAgentCore`, backed by a daemon-owned policy file (`LaunchConsentStore`, state-dir idiom of `CoverageJournal`) and an append-only decision log (`LaunchConsentDecisionLog`, `FailedLaunchLog` discipline). A prompt broker (`LaunchConsentBroker`) offers unmatched requests to local-socket subscribers with a bounded timeout. New `FrameType` values carry JSON payloads over the existing Unix-socket `LocalControlServer`. A `kcap daemon consent` subcommand makes headless operation complete. A small **kcap-server** companion (separate repo, Task 9) stamps requester identity onto the launch command.

**Tech Stack:** .NET 10 AOT, System.Text.Json source-gen, TUnit 1.18 on Microsoft Testing Platform, Unix domain sockets.

**Spec:** `docs/superpowers/specs/2026-07-31-desktop-supervisor-app-design.md` (§4, §5, §9 slice 1). Linear: AI-1623 (parent AI-1622).

**Branch:** work on `spec/desktop-supervisor-app` (holds the spec commit), or rename it to `alexeyzimarev/ai-1623-daemon-consent-ipc` — the spec rides this PR per team convention.

## Global Constraints

- Repo is `/Users/alexey/dev/temp/kcap-cli` (the sibling checkout — NEVER the `src/cli` submodule of kcap-server).
- AOT: JSON via source-gen contexts only (`JsonSerializer.Serialize(x, Ctx.Default.T)`), never reflection overloads. `JsonArray` collection expressions `[a,b]` are forbidden (dynamic code) — use `new JsonArray(a, b)`.
- Verify AOT with **publish, not build**: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` must print nothing (same for `Capacitor.Cli.Daemon`).
- `FrameType` values are one wire byte, **append-only**: new client→daemon values start at **11** (do not resurrect the hole at 9), new daemon→client values start at **72**.
- Daemon-local state files: private nested `JsonSerializerContext` with `SnakeCaseLower` inside the owning class; atomic writes via temp file + `File.Move(..., overwrite: true)`; 0700 dirs / 0600 files when content may be sensitive (mirror `FailedLaunchLog.cs:38-75`).
- Tests: TUnit. Run a single test class with `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/<ClassName>/*"` — **never `--filter`**.
- Any test overriding `DaemonLockPaths.OverrideDirectoryForTesting` needs `[NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]`.
- Name collision: `ConsentDialogDetector` already exists (PTY output scanner, unrelated). All new types use the `LaunchConsent*` prefix.
- README sync is mandatory in the same PR: `README.md` (`## CLI commands`) + `src/Capacitor.Cli.Core/Resources/help-daemon.txt` + `DaemonCommands.PrintUsage()`.
- Commit after every task (at minimum); prefix `feat:`/`test:`/`docs:` as appropriate; end commit messages with the Co-Authored-By line configured for this environment.

---

### Task 1: Pure rule engine (`LaunchConsentEngine` + policy records)

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Services/LaunchConsentPolicy.cs`
- Create: `src/Capacitor.Cli.Daemon/Services/LaunchConsentEngine.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/Daemon/LaunchConsentEngineTests.cs`

**Interfaces:**
- Consumes: `LaunchKind` (`src/Capacitor.Cli.Core/Models.cs:1387` — `Default=0, Review=1, ReviewFlow=2`).
- Produces (later tasks depend on these exact shapes):
  - `enum LaunchConsentDefault { Allow, Deny, Prompt }`
  - `sealed record LaunchConsentRule(string Action, string? Requester, string? Kind, string? Repo, string? Vendor)`
  - `sealed record LaunchConsentPolicy(LaunchConsentDefault Default, int PromptTimeoutSeconds, IReadOnlyList<LaunchConsentRule> Rules)` with `static readonly LaunchConsentPolicy UpgradeSafe`
  - `readonly record struct LaunchConsentInput(string? RequesterUserId, bool RequesterIsOwner, string Kind, string RepoPath, string Vendor)`
  - `enum LaunchConsentVerdict { Allow, Deny, Prompt }`
  - `readonly record struct LaunchConsentDecision(LaunchConsentVerdict Verdict, string Source)`
  - `static class LaunchConsentEngine { static string KindToken(LaunchKind kind); static LaunchConsentDecision Evaluate(LaunchConsentPolicy policy, in LaunchConsentInput input); }`

- [ ] **Step 1: Write the failing tests**

`test/Capacitor.Cli.Tests.Unit/Daemon/LaunchConsentEngineTests.cs`:

```csharp
using Capacitor.Cli.Daemon.Services;
using Capacitor.Core;

namespace Capacitor.Cli.Tests.Unit.Daemon;

public class LaunchConsentEngineTests {
    static LaunchConsentInput Input(
        string? requester = "user_abc", bool owner = false, string kind = "agent",
        string repo = "/Users/me/dev/proj", string vendor = "claude")
        => new(requester, owner, kind, repo, vendor);

    static LaunchConsentPolicy Policy(
        LaunchConsentDefault def = LaunchConsentDefault.Allow, params LaunchConsentRule[] rules)
        => new(def, 45, rules);

    [Test]
    public async Task Owner_is_always_allowed_even_with_matching_deny_rule() {
        var policy = Policy(LaunchConsentDefault.Deny,
            new LaunchConsentRule("deny", null, null, null, null));
        var d = LaunchConsentEngine.Evaluate(policy, Input(owner: true));
        await Assert.That(d.Verdict).IsEqualTo(LaunchConsentVerdict.Allow);
        await Assert.That(d.Source).IsEqualTo("owner");
    }

    [Test]
    public async Task First_matching_rule_wins_in_file_order() {
        var policy = Policy(LaunchConsentDefault.Prompt,
            new LaunchConsentRule("deny", null, "review-flow", null, null),
            new LaunchConsentRule("allow", null, null, null, null));
        var d = LaunchConsentEngine.Evaluate(policy, Input(kind: "review-flow"));
        await Assert.That(d.Verdict).IsEqualTo(LaunchConsentVerdict.Deny);
        await Assert.That(d.Source).IsEqualTo("rule[0]");
    }

    [Test]
    public async Task Null_rule_fields_are_wildcards() {
        var policy = Policy(LaunchConsentDefault.Deny,
            new LaunchConsentRule("allow", null, null, null, null));
        var d = LaunchConsentEngine.Evaluate(policy, Input());
        await Assert.That(d.Verdict).IsEqualTo(LaunchConsentVerdict.Allow);
    }

    [Test]
    public async Task Requester_specific_rule_does_not_match_null_requester() {
        var policy = Policy(LaunchConsentDefault.Prompt,
            new LaunchConsentRule("allow", "user_abc", null, null, null));
        var d = LaunchConsentEngine.Evaluate(policy, Input(requester: null));
        await Assert.That(d.Verdict).IsEqualTo(LaunchConsentVerdict.Prompt);
        await Assert.That(d.Source).IsEqualTo("default");
    }

    [Test]
    public async Task Vendor_match_is_case_insensitive() {
        var policy = Policy(LaunchConsentDefault.Deny,
            new LaunchConsentRule("allow", null, null, null, "Claude"));
        var d = LaunchConsentEngine.Evaluate(policy, Input(vendor: "claude"));
        await Assert.That(d.Verdict).IsEqualTo(LaunchConsentVerdict.Allow);
    }

    [Test]
    public async Task Repo_prefix_glob_matches_subpaths_exact_matches_only_itself() {
        var glob = Policy(LaunchConsentDefault.Deny,
            new LaunchConsentRule("allow", null, null, "/Users/me/dev/*", null));
        await Assert.That(LaunchConsentEngine.Evaluate(glob, Input(repo: "/Users/me/dev/proj")).Verdict)
            .IsEqualTo(LaunchConsentVerdict.Allow);
        await Assert.That(LaunchConsentEngine.Evaluate(glob, Input(repo: "/Users/me/other")).Verdict)
            .IsEqualTo(LaunchConsentVerdict.Deny);

        var exact = Policy(LaunchConsentDefault.Deny,
            new LaunchConsentRule("allow", null, null, "/Users/me/dev/proj", null));
        await Assert.That(LaunchConsentEngine.Evaluate(exact, Input(repo: "/Users/me/dev/proj/")).Verdict)
            .IsEqualTo(LaunchConsentVerdict.Allow);
        await Assert.That(LaunchConsentEngine.Evaluate(exact, Input(repo: "/Users/me/dev/proj2")).Verdict)
            .IsEqualTo(LaunchConsentVerdict.Deny);
    }

    [Test]
    [Arguments(LaunchConsentDefault.Allow, LaunchConsentVerdict.Allow)]
    [Arguments(LaunchConsentDefault.Deny, LaunchConsentVerdict.Deny)]
    [Arguments(LaunchConsentDefault.Prompt, LaunchConsentVerdict.Prompt)]
    public async Task Unmatched_falls_through_to_default(LaunchConsentDefault def, LaunchConsentVerdict expected) {
        var d = LaunchConsentEngine.Evaluate(Policy(def), Input());
        await Assert.That(d.Verdict).IsEqualTo(expected);
        await Assert.That(d.Source).IsEqualTo("default");
    }

    [Test]
    public async Task KindToken_maps_all_launch_kinds() {
        await Assert.That(LaunchConsentEngine.KindToken(LaunchKind.Default)).IsEqualTo("agent");
        await Assert.That(LaunchConsentEngine.KindToken(LaunchKind.Review)).IsEqualTo("review");
        await Assert.That(LaunchConsentEngine.KindToken(LaunchKind.ReviewFlow)).IsEqualTo("review-flow");
    }
}
```

Note: the namespace for `LaunchKind` — check the `namespace` declaration at the top of `src/Capacitor.Cli.Core/Models.cs` and use whatever the existing daemon tests import (see the `using` lines of `test/Capacitor.Cli.Tests.Unit/LaunchAgentCommandWireFormatTests.cs`). Adjust `using Capacitor.Core;` above accordingly.

- [ ] **Step 2: Run tests to verify they fail to compile**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/LaunchConsentEngineTests/*"`
Expected: build error — `LaunchConsentEngine` does not exist.

- [ ] **Step 3: Implement the records and engine**

`src/Capacitor.Cli.Daemon/Services/LaunchConsentPolicy.cs` (match the namespace of neighbors like `UnattendedLaunchPolicy.cs`):

```csharp
namespace Capacitor.Cli.Daemon.Services;

/// The policy the daemon enforces before any SERVER-driven launch. Launches arriving on the
/// daemon's own 0600 local socket (kcap agent start) never consult this — that socket is the
/// owner's by construction (see AgentOrchestrator.LocalIpc trust note).
internal enum LaunchConsentDefault { Allow, Deny, Prompt }

/// Null field = wildcard. Action is "allow" or "deny" (validated at the store boundary).
/// Repo uses DaemonConfig.IsRepoAllowed semantics: exact path or "/prefix/*" glob.
internal sealed record LaunchConsentRule(
    string Action,
    string? Requester,
    string? Kind,
    string? Repo,
    string? Vendor);

internal sealed record LaunchConsentPolicy(
    LaunchConsentDefault Default,
    int PromptTimeoutSeconds,
    IReadOnlyList<LaunchConsentRule> Rules)
{
    // Default allow preserves pre-consent behavior for every existing daemon on upgrade;
    // the desktop app (slice 2) flips managed daemons to Prompt during onboarding.
    public static readonly LaunchConsentPolicy UpgradeSafe = new(LaunchConsentDefault.Allow, 45, []);
}
```

`src/Capacitor.Cli.Daemon/Services/LaunchConsentEngine.cs`:

```csharp
using Capacitor.Core; // adjust to Models.cs namespace

namespace Capacitor.Cli.Daemon.Services;

internal readonly record struct LaunchConsentInput(
    string? RequesterUserId,
    bool RequesterIsOwner,
    string Kind,
    string RepoPath,
    string Vendor);

internal enum LaunchConsentVerdict { Allow, Deny, Prompt }

/// Source: "owner" | "rule[i]" | "default" — recorded verbatim in the decision log.
internal readonly record struct LaunchConsentDecision(LaunchConsentVerdict Verdict, string Source);

internal static class LaunchConsentEngine {
    public static string KindToken(LaunchKind kind) => kind switch {
        LaunchKind.Review => "review",
        LaunchKind.ReviewFlow => "review-flow",
        _ => "agent",
    };

    public static LaunchConsentDecision Evaluate(LaunchConsentPolicy policy, in LaunchConsentInput input) {
        if (input.RequesterIsOwner) return new(LaunchConsentVerdict.Allow, "owner");
        for (var i = 0; i < policy.Rules.Count; i++) {
            var r = policy.Rules[i];
            if (!Matches(r, input)) continue;
            var verdict = string.Equals(r.Action, "deny", StringComparison.Ordinal)
                ? LaunchConsentVerdict.Deny : LaunchConsentVerdict.Allow;
            return new(verdict, $"rule[{i}]");
        }
        return new(policy.Default switch {
            LaunchConsentDefault.Deny => LaunchConsentVerdict.Deny,
            LaunchConsentDefault.Prompt => LaunchConsentVerdict.Prompt,
            _ => LaunchConsentVerdict.Allow,
        }, "default");
    }

    static bool Matches(LaunchConsentRule r, in LaunchConsentInput x) =>
        (r.Requester is null || string.Equals(r.Requester, x.RequesterUserId, StringComparison.Ordinal)) &&
        (r.Kind is null || string.Equals(r.Kind, x.Kind, StringComparison.Ordinal)) &&
        (r.Vendor is null || string.Equals(r.Vendor, x.Vendor, StringComparison.OrdinalIgnoreCase)) &&
        (r.Repo is null || RepoMatches(r.Repo, x.RepoPath));

    static bool RepoMatches(string pattern, string repoPath) {
        if (pattern.EndsWith("/*", StringComparison.Ordinal)) {
            var prefix = pattern[..^1];
            return repoPath.StartsWith(prefix, StringComparison.Ordinal);
        }
        return string.Equals(
            Path.TrimEndingDirectorySeparator(pattern),
            Path.TrimEndingDirectorySeparator(repoPath),
            StringComparison.Ordinal);
    }
}
```

Before finalizing `RepoMatches`, read `DaemonConfig.IsRepoAllowed` (`src/Capacitor.Cli.Daemon/DaemonConfig.cs:189`) and mirror its exact glob semantics (including any case-sensitivity choice) so operators learn one rule form, not two.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/LaunchConsentEngineTests/*"`
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/LaunchConsentPolicy.cs \
        src/Capacitor.Cli.Daemon/Services/LaunchConsentEngine.cs \
        test/Capacitor.Cli.Tests.Unit/Daemon/LaunchConsentEngineTests.cs
git commit -m "feat: pure launch-consent rule engine (AI-1623)"
```

---

### Task 2: Policy store (`LaunchConsentStore`)

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Services/LaunchConsentStore.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/Daemon/LaunchConsentStoreTests.cs`

**Interfaces:**
- Consumes: Task 1 records.
- Produces:
  - `internal sealed partial class LaunchConsentStore` — ctor `(string stateDir, ILogger logger)`; file `{stateDir}/consent.json`.
  - `LaunchConsentPolicy Current { get; }` (thread-safe snapshot)
  - `bool TryReplace(LaunchConsentPolicy next, out string? error)` — validates, clamps `PromptTimeoutSeconds` to `[5, 300]`, atomically persists, updates `Current`. Validation failures: action not `allow`/`deny`; kind not null/`agent`/`review`/`review-flow`.

- [ ] **Step 1: Write the failing tests**

`test/Capacitor.Cli.Tests.Unit/Daemon/LaunchConsentStoreTests.cs` — pattern-match the temp-dir harness of `Daemon/CoverageJournalTests.cs`:

```csharp
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit.Daemon;

public class LaunchConsentStoreTests {
    static string TempDir() =>
        Directory.CreateTempSubdirectory("kcap-consent-").FullName;

    [Test]
    public async Task Missing_file_yields_upgrade_safe_policy() {
        var store = new LaunchConsentStore(TempDir(), NullLogger.Instance);
        await Assert.That(store.Current.Default).IsEqualTo(LaunchConsentDefault.Allow);
        await Assert.That(store.Current.PromptTimeoutSeconds).IsEqualTo(45);
        await Assert.That(store.Current.Rules.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Corrupt_file_yields_upgrade_safe_policy() {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "consent.json"), "{not json");
        var store = new LaunchConsentStore(dir, NullLogger.Instance);
        await Assert.That(store.Current.Default).IsEqualTo(LaunchConsentDefault.Allow);
    }

    [Test]
    public async Task Replace_persists_and_reloads() {
        var dir = TempDir();
        var store = new LaunchConsentStore(dir, NullLogger.Instance);
        var next = new LaunchConsentPolicy(LaunchConsentDefault.Prompt, 60,
            [new LaunchConsentRule("deny", "user_x", "review-flow", null, "codex")]);
        var ok = store.TryReplace(next, out var error);
        await Assert.That(ok).IsTrue();
        await Assert.That(error).IsNull();

        var reloaded = new LaunchConsentStore(dir, NullLogger.Instance);
        await Assert.That(reloaded.Current.Default).IsEqualTo(LaunchConsentDefault.Prompt);
        await Assert.That(reloaded.Current.PromptTimeoutSeconds).IsEqualTo(60);
        await Assert.That(reloaded.Current.Rules[0].Requester).IsEqualTo("user_x");
    }

    [Test]
    public async Task Replace_rejects_invalid_action_and_kind() {
        var store = new LaunchConsentStore(TempDir(), NullLogger.Instance);
        var badAction = new LaunchConsentPolicy(LaunchConsentDefault.Allow, 45,
            [new LaunchConsentRule("maybe", null, null, null, null)]);
        await Assert.That(store.TryReplace(badAction, out var e1)).IsFalse();
        await Assert.That(e1).Contains("action");

        var badKind = new LaunchConsentPolicy(LaunchConsentDefault.Allow, 45,
            [new LaunchConsentRule("allow", null, "flows", null, null)]);
        await Assert.That(store.TryReplace(badKind, out var e2)).IsFalse();
        await Assert.That(e2).Contains("kind");
    }

    [Test]
    public async Task Replace_clamps_prompt_timeout() {
        var store = new LaunchConsentStore(TempDir(), NullLogger.Instance);
        await Assert.That(store.TryReplace(
            new LaunchConsentPolicy(LaunchConsentDefault.Allow, 1, []), out _)).IsTrue();
        await Assert.That(store.Current.PromptTimeoutSeconds).IsEqualTo(5);
        await Assert.That(store.TryReplace(
            new LaunchConsentPolicy(LaunchConsentDefault.Allow, 9999, []), out _)).IsTrue();
        await Assert.That(store.Current.PromptTimeoutSeconds).IsEqualTo(300);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/LaunchConsentStoreTests/*"`
Expected: build error — `LaunchConsentStore` does not exist.

- [ ] **Step 3: Implement the store**

`src/Capacitor.Cli.Daemon/Services/LaunchConsentStore.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// Owns {stateDir}/consent.json. The running daemon is the SINGLE writer — the CLI and the
/// desktop app mutate it only via the local socket (Task 7). Corrupt/missing file degrades to
/// LaunchConsentPolicy.UpgradeSafe: consent must never brick a daemon boot.
internal sealed partial class LaunchConsentStore {
    static readonly string[] ValidKinds = ["agent", "review", "review-flow"];

    readonly string _path;
    readonly ILogger _log;
    readonly object _gate = new();
    LaunchConsentPolicy _current;

    public LaunchConsentStore(string stateDir, ILogger logger) {
        Directory.CreateDirectory(stateDir);
        _path = Path.Combine(stateDir, "consent.json");
        _log = logger;
        _current = Load();
    }

    public LaunchConsentPolicy Current { get { lock (_gate) return _current; } }

    public bool TryReplace(LaunchConsentPolicy next, out string? error) {
        foreach (var r in next.Rules) {
            if (r.Action is not ("allow" or "deny")) { error = $"invalid rule action '{r.Action}' (allow|deny)"; return false; }
            if (r.Kind is not null && !ValidKinds.Contains(r.Kind)) { error = $"invalid rule kind '{r.Kind}' (agent|review|review-flow)"; return false; }
        }
        var clamped = next with { PromptTimeoutSeconds = Math.Clamp(next.PromptTimeoutSeconds, 5, 300) };
        lock (_gate) {
            try {
                var doc = new PolicyDoc(
                    clamped.Default switch { LaunchConsentDefault.Deny => "deny", LaunchConsentDefault.Prompt => "prompt", _ => "allow" },
                    clamped.PromptTimeoutSeconds,
                    clamped.Rules.Select(r => new RuleDoc(r.Action, r.Requester, r.Kind, r.Repo, r.Vendor)).ToList());
                var tmp = _path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
                File.WriteAllText(tmp, JsonSerializer.Serialize(doc, Ctx.Default.PolicyDoc));
                File.Move(tmp, _path, overwrite: true);
                _current = clamped;
                error = null;
                return true;
            } catch (Exception ex) {
                _log.LogWarning(ex, "Failed to persist consent policy to {Path}", _path);
                error = "failed to persist consent policy";
                return false;
            }
        }
    }

    LaunchConsentPolicy Load() {
        try {
            if (!File.Exists(_path)) return LaunchConsentPolicy.UpgradeSafe;
            var doc = JsonSerializer.Deserialize(File.ReadAllText(_path), Ctx.Default.PolicyDoc);
            if (doc is null) return LaunchConsentPolicy.UpgradeSafe;
            var def = doc.Default switch { "deny" => LaunchConsentDefault.Deny, "prompt" => LaunchConsentDefault.Prompt, _ => LaunchConsentDefault.Allow };
            var rules = (doc.Rules ?? [])
                .Where(r => r.Action is "allow" or "deny")
                .Select(r => new LaunchConsentRule(r.Action, r.Requester, r.Kind, r.Repo, r.Vendor))
                .ToList();
            return new LaunchConsentPolicy(def, Math.Clamp(doc.PromptTimeoutSeconds ?? 45, 5, 300), rules);
        } catch (Exception ex) {
            _log.LogWarning(ex, "Corrupt consent policy at {Path}; using upgrade-safe default (allow)", _path);
            return LaunchConsentPolicy.UpgradeSafe;
        }
    }

    internal sealed record PolicyDoc(string? Default, int? PromptTimeoutSeconds, List<RuleDoc>? Rules);
    internal sealed record RuleDoc(string Action, string? Requester, string? Kind, string? Repo, string? Vendor);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true)]
    [JsonSerializable(typeof(PolicyDoc))]
    partial class Ctx : JsonSerializerContext;
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/LaunchConsentStoreTests/*"`
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/LaunchConsentStore.cs \
        test/Capacitor.Cli.Tests.Unit/Daemon/LaunchConsentStoreTests.cs
git commit -m "feat: daemon-owned consent policy store with atomic persistence (AI-1623)"
```

---

### Task 3: Decision log (`LaunchConsentDecisionLog`)

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Services/LaunchConsentDecisionLog.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/Daemon/LaunchConsentDecisionLogTests.cs`

**Interfaces:**
- Produces:
  - `internal sealed partial class LaunchConsentDecisionLog` — ctor `(string stateDir, ILogger logger, long maxBytes = 1_048_576)`; file `{stateDir}/consent-decisions.jsonl`.
  - `void Record(LaunchConsentRecord rec)` — append one snake_case JSON line; at cap, rotate current file to `consent-decisions.jsonl.1` (overwrite) and start fresh (the `RollingFileLoggerProvider` one-backup discipline); never throws.
  - `internal sealed record LaunchConsentRecord(string DecidedAt, string AgentId, string? Requester, bool RequesterIsOwner, string Kind, string RepoPath, string Vendor, string Outcome, string Source)` — `Outcome`: `"allowed"|"denied"`; `Source`: `"owner"|"rule[i]"|"default"|"prompt_user"|"prompt_timeout"|"prompt_no_ui"`; `DecidedAt` ISO-8601 UTC (`DateTimeOffset.UtcNow.ToString("O")` at the caller).
- File modes: 0700 state dir / 0600 log files on non-Windows (mirror `FailedLaunchLog.cs:38-75` — the log carries repo paths and requester ids).

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Text.Json;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit.Daemon;

public class LaunchConsentDecisionLogTests {
    static LaunchConsentRecord Rec(string agent = "a1") => new(
        DateTimeOffset.UtcNow.ToString("O"), agent, "user_x", false,
        "agent", "/tmp/repo", "claude", "denied", "default");

    [Test]
    public async Task Records_append_as_parseable_snake_case_jsonl() {
        var dir = Directory.CreateTempSubdirectory("kcap-cdl-").FullName;
        var log = new LaunchConsentDecisionLog(dir, NullLogger.Instance);
        log.Record(Rec("a1"));
        log.Record(Rec("a2"));
        var lines = File.ReadAllLines(Path.Combine(dir, "consent-decisions.jsonl"));
        await Assert.That(lines.Length).IsEqualTo(2);
        using var parsed = JsonDocument.Parse(lines[0]);
        await Assert.That(parsed.RootElement.GetProperty("agent_id").GetString()).IsEqualTo("a1");
        await Assert.That(parsed.RootElement.GetProperty("outcome").GetString()).IsEqualTo("denied");
    }

    [Test]
    public async Task Rotates_to_backup_at_cap() {
        var dir = Directory.CreateTempSubdirectory("kcap-cdl-").FullName;
        var log = new LaunchConsentDecisionLog(dir, NullLogger.Instance, maxBytes: 512);
        for (var i = 0; i < 20; i++) log.Record(Rec($"agent-{i}"));
        await Assert.That(File.Exists(Path.Combine(dir, "consent-decisions.jsonl.1"))).IsTrue();
        var live = new FileInfo(Path.Combine(dir, "consent-decisions.jsonl"));
        await Assert.That(live.Length <= 512).IsTrue();
    }

    [Test]
    public async Task Unwritable_directory_never_throws() {
        var log = new LaunchConsentDecisionLog("/nonexistent/deeply/nested", NullLogger.Instance);
        log.Record(Rec());
        await Assert.That(true).IsTrue();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/LaunchConsentDecisionLogTests/*"`
Expected: build error.

- [ ] **Step 3: Implement**

```csharp
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// Append-only JSONL audit of every consent decision (rule-matched and human), rendered by the
/// desktop app as the Activity feed and by `kcap daemon consent log`. Best-effort: an I/O fault
/// is logged and swallowed — audit must never fail a launch decision.
internal sealed partial class LaunchConsentDecisionLog(string stateDir, ILogger logger, long maxBytes = 1_048_576) {
    readonly string _path = Path.Combine(stateDir, "consent-decisions.jsonl");
    readonly object _gate = new();

    public void Record(LaunchConsentRecord rec) {
        lock (_gate) {
            try {
                Directory.CreateDirectory(stateDir);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(stateDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                var line = JsonSerializer.Serialize(rec, Ctx.Default.LaunchConsentRecord) + "\n";
                var incoming = Encoding.UTF8.GetByteCount(line);
                if (File.Exists(_path) && new FileInfo(_path).Length + incoming > maxBytes)
                    File.Move(_path, _path + ".1", overwrite: true);
                var existed = File.Exists(_path);
                File.AppendAllText(_path, line);
                if (!existed && !OperatingSystem.IsWindows())
                    File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            } catch (Exception ex) {
                logger.LogWarning(ex, "Failed to append consent decision for {AgentId}", rec.AgentId);
            }
        }
    }

    internal sealed record LaunchConsentRecord(
        string DecidedAt, string AgentId, string? Requester, bool RequesterIsOwner,
        string Kind, string RepoPath, string Vendor, string Outcome, string Source);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
    [JsonSerializable(typeof(LaunchConsentRecord))]
    partial class Ctx : JsonSerializerContext;
}
```

Note: `LaunchConsentRecord` is declared nested-in-file here for the source-gen context; if the compiler placement makes consumption awkward from Task 5, hoist the record to the namespace level in the same file and keep the context nested — match what `CoverageJournal.cs` does with its DTO.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/LaunchConsentDecisionLogTests/*"`
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/LaunchConsentDecisionLog.cs \
        test/Capacitor.Cli.Tests.Unit/Daemon/LaunchConsentDecisionLogTests.cs
git commit -m "feat: append-only consent decision log (AI-1623)"
```

---

### Task 4: Requester identity wire fields on `LaunchAgentCommand` (Core)

**Files:**
- Modify: `src/Capacitor.Cli.Core/Models.cs:1251` (`LaunchAgentCommand`)
- Test: extend `test/Capacitor.Cli.Tests.Unit/LaunchAgentCommandWireFormatTests.cs`

**Interfaces:**
- Produces: two trailing optional parameters on `LaunchAgentCommand`:
  - `string? RequesterUserId = null` (wire `requester_user_id`) — the Capacitor user id of whoever initiated this launch (hub caller, flow driver owner).
  - `bool? RequesterIsOwner = null` (wire `requester_is_owner`) — server-computed: requester == this daemon's owner. `null` (old server) means unknown; the engine treats it as `false` and the launch falls through rules to the default (`allow` upgrade-safe).
- The server companion that populates them is Task 9.

- [ ] **Step 1: Write the failing test**

Add to `LaunchAgentCommandWireFormatTests.cs` (match its existing serialize/deserialize helpers — it exercises `CapacitorJsonContext` snake_case):

```csharp
[Test]
public async Task Requester_fields_roundtrip_and_default_null_when_absent() {
    // Old-server payload without the new fields → nulls (wire compat).
    var legacyJson = """{"agent_id":"a1","model":"m","repo_path":"/r","vendor":"claude"}""";
    var legacy = JsonSerializer.Deserialize(legacyJson, CapacitorJsonContext.Default.LaunchAgentCommand);
    await Assert.That(legacy.RequesterUserId).IsNull();
    await Assert.That(legacy.RequesterIsOwner).IsNull();

    // New fields serialize snake_case and roundtrip.
    var cmd = new LaunchAgentCommand("a1", null, "m", null, "/r", null, null, "claude") {
        RequesterUserId = "user_x", RequesterIsOwner = true };
    var json = JsonSerializer.Serialize(cmd, CapacitorJsonContext.Default.LaunchAgentCommand);
    await Assert.That(json).Contains("\"requester_user_id\":\"user_x\"");
    await Assert.That(json).Contains("\"requester_is_owner\":true");
    var back = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.LaunchAgentCommand);
    await Assert.That(back.RequesterUserId).IsEqualTo("user_x");
    await Assert.That(back.RequesterIsOwner).IsEqualTo(true);
}
```

Positional-args note: `LaunchAgentCommand` is a positional record struct — the object-initializer form above works only if the new members are `init` properties; since we add them as trailing constructor parameters with defaults, construct instead with named arguments: `new LaunchAgentCommand("a1", null, "m", null, "/r", null, null, "claude", RequesterUserId: "user_x", RequesterIsOwner: true)`. Use whichever form the existing tests in that file use for trailing fields.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/LaunchAgentCommandWireFormatTests/*"`
Expected: build error — no such parameter.

- [ ] **Step 3: Add the fields**

In `Models.cs`, append after `ExplicitReviewerModelLaunch? ExplicitReviewerModel = null` (keep the file's own trailing-field comment style — there is an explicit wire-compat comment block for previously appended fields; extend it):

```csharp
        // AI-1623 consent: who asked for this launch. Appended last, same wire-compat rule as the
        // fields above — old daemons ignore them, old servers never set them (null ⇒ unknown ⇒
        // the consent engine falls through rules to the configured default).
        string?           RequesterUserId       = null,
        bool?             RequesterIsOwner      = null
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/LaunchAgentCommandWireFormatTests/*"`
Expected: all PASS (including pre-existing tests in the class — they prove no positional break).

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/Models.cs test/Capacitor.Cli.Tests.Unit/LaunchAgentCommandWireFormatTests.cs
git commit -m "feat: requester identity fields on LaunchAgentCommand wire (AI-1623)"
```

---

### Task 5: Consent gate in the launch choke point

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Services/LaunchConsentGate.cs`
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` (ctor + top of `HandleLaunchAgentCore`, before the vendor check at ~line 898)
- Modify: `src/Capacitor.Cli.Daemon/DaemonRunner.cs` (DI registrations, next to the `coverageStateDir` computation at ~line 211)
- Modify: `test/Capacitor.Cli.Tests.Unit/AgentOrchestratorVendorTests.cs` (`BuildOrchestrator` factory)
- Test: `test/Capacitor.Cli.Tests.Unit/Daemon/LaunchConsentGateTests.cs` + orchestrator-level tests in the same file

**Interfaces:**
- Consumes: Tasks 1–4 types; `ServerConnection.LaunchFailedAsync(string agentId, string reason)` (`ServerConnection.cs:786`); `CommandOutcome`/`CommandRejectedReason.Semantic` (`SequencedCommandProcessor.cs:13`, `Models.cs:1555`); test seam `AgentOrchestrator.HandleLaunchAgentForTest` (`AgentOrchestrator.cs:2973`); test double `CaptureServerConnection` (`AgentOrchestratorVendorTests.cs:1461`).
- Produces:
  - `internal interface ILaunchConsentPrompter { bool HasSubscriber { get; } Task<bool?> PromptAsync(LaunchConsentPromptRequest req, TimeSpan timeout, CancellationToken ct); }` (`null` result = timeout)
  - `internal sealed record LaunchConsentPromptRequest(string RequestId, string? Requester, string Kind, string RepoPath, string Vendor, string RequestedAt, int TimeoutSeconds)`
  - `internal sealed class LaunchConsentGate` — ctor `(LaunchConsentStore store, LaunchConsentDecisionLog log, ILaunchConsentPrompter? prompter, ILogger<LaunchConsentGate> logger)`; method `Task<LaunchConsentOutcome> DecideAsync(string agentId, LaunchConsentInput input, CancellationToken ct)`
  - `internal readonly record struct LaunchConsentOutcome(bool Allowed, string Source, string Detail)`
  - The coded reason prefix constant: `public const string DeniedReasonPrefix = "launch_denied_by_owner"` on `LaunchConsentGate`.

- [ ] **Step 1: Write the failing gate unit tests**

`test/Capacitor.Cli.Tests.Unit/Daemon/LaunchConsentGateTests.cs`:

```csharp
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit.Daemon;

public class LaunchConsentGateTests {
    static (LaunchConsentGate gate, LaunchConsentStore store, string dir) Build(
        LaunchConsentDefault def = LaunchConsentDefault.Allow, ILaunchConsentPrompter? prompter = null) {
        var dir = Directory.CreateTempSubdirectory("kcap-gate-").FullName;
        var store = new LaunchConsentStore(dir, NullLogger.Instance);
        store.TryReplace(new LaunchConsentPolicy(def, 5, []), out _);
        var log = new LaunchConsentDecisionLog(dir, NullLogger.Instance);
        var gate = new LaunchConsentGate(store, log, prompter, NullLogger<LaunchConsentGate>.Instance);
        return (gate, store, dir);
    }

    static LaunchConsentInput Input(bool owner = false) =>
        new("user_x", owner, "agent", "/tmp/repo", "claude");

    sealed class FakePrompter(bool? answer, bool hasSubscriber = true) : ILaunchConsentPrompter {
        public LaunchConsentPromptRequest? Seen;
        public bool HasSubscriber => hasSubscriber;
        public Task<bool?> PromptAsync(LaunchConsentPromptRequest req, TimeSpan timeout, CancellationToken ct) {
            Seen = req;
            return Task.FromResult(answer);
        }
    }

    [Test]
    public async Task Allow_default_allows_and_logs() {
        var (gate, _, dir) = Build(LaunchConsentDefault.Allow);
        var o = await gate.DecideAsync("a1", Input(), CancellationToken.None);
        await Assert.That(o.Allowed).IsTrue();
        var lines = File.ReadAllLines(Path.Combine(dir, "consent-decisions.jsonl"));
        await Assert.That(lines.Length).IsEqualTo(1);
        await Assert.That(lines[0]).Contains("\"outcome\":\"allowed\"");
    }

    [Test]
    public async Task Deny_default_denies_with_source_default() {
        var (gate, _, _) = Build(LaunchConsentDefault.Deny);
        var o = await gate.DecideAsync("a1", Input(), CancellationToken.None);
        await Assert.That(o.Allowed).IsFalse();
        await Assert.That(o.Source).IsEqualTo("default");
    }

    [Test]
    public async Task Prompt_without_subscriber_denies_no_ui() {
        var (gate, _, _) = Build(LaunchConsentDefault.Prompt, new FakePrompter(true, hasSubscriber: false));
        var o = await gate.DecideAsync("a1", Input(), CancellationToken.None);
        await Assert.That(o.Allowed).IsFalse();
        await Assert.That(o.Source).IsEqualTo("prompt_no_ui");
    }

    [Test]
    public async Task Prompt_user_allow_and_deny_and_timeout() {
        var (allowGate, _, _) = Build(LaunchConsentDefault.Prompt, new FakePrompter(true));
        await Assert.That((await allowGate.DecideAsync("a1", Input(), CancellationToken.None)).Allowed).IsTrue();

        var (denyGate, _, _) = Build(LaunchConsentDefault.Prompt, new FakePrompter(false));
        var denied = await denyGate.DecideAsync("a1", Input(), CancellationToken.None);
        await Assert.That(denied.Allowed).IsFalse();
        await Assert.That(denied.Source).IsEqualTo("prompt_user");

        var (timeoutGate, _, _) = Build(LaunchConsentDefault.Prompt, new FakePrompter(null));
        var timedOut = await timeoutGate.DecideAsync("a1", Input(), CancellationToken.None);
        await Assert.That(timedOut.Allowed).IsFalse();
        await Assert.That(timedOut.Source).IsEqualTo("prompt_timeout");
    }

    [Test]
    public async Task Owner_bypasses_deny_default() {
        var (gate, _, _) = Build(LaunchConsentDefault.Deny);
        var o = await gate.DecideAsync("a1", Input(owner: true), CancellationToken.None);
        await Assert.That(o.Allowed).IsTrue();
        await Assert.That(o.Source).IsEqualTo("owner");
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/LaunchConsentGateTests/*"`
Expected: build error.

- [ ] **Step 3: Implement `LaunchConsentGate`**

`src/Capacitor.Cli.Daemon/Services/LaunchConsentGate.cs`:

```csharp
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

internal sealed record LaunchConsentPromptRequest(
    string RequestId, string? Requester, string Kind, string RepoPath, string Vendor,
    string RequestedAt, int TimeoutSeconds);

/// Implemented by LaunchConsentBroker (Task 6). Null answer = timeout / subscriber vanished.
internal interface ILaunchConsentPrompter {
    bool HasSubscriber { get; }
    Task<bool?> PromptAsync(LaunchConsentPromptRequest req, TimeSpan timeout, CancellationToken ct);
}

internal sealed class LaunchConsentGate(
    LaunchConsentStore store,
    LaunchConsentDecisionLog log,
    ILaunchConsentPrompter? prompter,
    ILogger<LaunchConsentGate> logger) {

    public const string DeniedReasonPrefix = "launch_denied_by_owner";

    public async Task<LaunchConsentOutcome> DecideAsync(string agentId, LaunchConsentInput input, CancellationToken ct) {
        var policy = store.Current;
        var decision = LaunchConsentEngine.Evaluate(policy, input);

        if (decision.Verdict is LaunchConsentVerdict.Allow)
            return Done(agentId, input, allowed: true, decision.Source, "allowed by daemon owner policy");
        if (decision.Verdict is LaunchConsentVerdict.Deny)
            return Done(agentId, input, allowed: false, decision.Source, "denied by daemon owner policy");

        if (prompter is not { HasSubscriber: true })
            return Done(agentId, input, allowed: false, "prompt_no_ui",
                "owner approval required and no approval UI is attached to this daemon");

        var req = new LaunchConsentPromptRequest(agentId, input.RequesterUserId, input.Kind,
            input.RepoPath, input.Vendor, DateTimeOffset.UtcNow.ToString("O"), policy.PromptTimeoutSeconds);
        logger.LogInformation("Launch {AgentId} awaiting owner consent (timeout {Timeout}s)", agentId, req.TimeoutSeconds);
        var answer = await prompter.PromptAsync(req, TimeSpan.FromSeconds(policy.PromptTimeoutSeconds), ct);
        return answer switch {
            true  => Done(agentId, input, allowed: true,  "prompt_user", "approved by daemon owner"),
            false => Done(agentId, input, allowed: false, "prompt_user", "declined by daemon owner"),
            null  => Done(agentId, input, allowed: false, "prompt_timeout",
                          $"owner did not respond within {policy.PromptTimeoutSeconds}s"),
        };
    }

    LaunchConsentOutcome Done(string agentId, in LaunchConsentInput input, bool allowed, string source, string detail) {
        log.Record(new LaunchConsentDecisionLog.LaunchConsentRecord(
            DateTimeOffset.UtcNow.ToString("O"), agentId, input.RequesterUserId, input.RequesterIsOwner,
            input.Kind, input.RepoPath, input.Vendor, allowed ? "allowed" : "denied", source));
        return new LaunchConsentOutcome(allowed, source, detail);
    }
}

internal readonly record struct LaunchConsentOutcome(bool Allowed, string Source, string Detail);
```

- [ ] **Step 4: Run gate tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/LaunchConsentGateTests/*"`
Expected: all PASS.

- [ ] **Step 5: Wire the gate into `AgentOrchestrator` + `DaemonRunner`**

1. `AgentOrchestrator` ctor: add a `LaunchConsentGate consentGate` parameter (store in `readonly LaunchConsentGate _consentGate;`). Find the ctor by its parameter list near the field declarations; keep parameter ordering consistent with its neighbors.
2. Top of `HandleLaunchAgentCore` (`AgentOrchestrator.cs:879`), immediately BEFORE the unknown-vendor check (~line 898) so a denial does no worktree/vendor work:

```csharp
// AI-1623: owner consent gate. Server-driven launches only — the local 0600 socket path
// (HandleLocalSpawnAsync) is the owner's by construction and never consults this.
// NOTE: in prompt mode this can hold the sequenced slot up to PromptTimeoutSeconds (≤300s,
// default 45s ≤ the server's 60s launch-admission patience); commands queued behind it wait.
var consentInput = new LaunchConsentInput(
    cmd.RequesterUserId, cmd.RequesterIsOwner ?? false,
    LaunchConsentEngine.KindToken(cmd.Kind), cmd.RepoPath, cmd.Vendor ?? "");
var consent = await _consentGate.DecideAsync(cmd.AgentId, consentInput, _shutdownCts.Token);
if (!consent.Allowed) {
    _logger.LogWarning("Launch {AgentId} denied by consent policy ({Source})", cmd.AgentId, consent.Source);
    await _server.LaunchFailedAsync(cmd.AgentId,
        $"{LaunchConsentGate.DeniedReasonPrefix}: {consent.Detail}");
    return new CommandOutcome(CommandOutcomeKind.LaunchRejected, cmd.AgentId,
        RejectReason: CommandRejectedReason.Semantic);
}
```

Use the same logger field name the method already uses (check surrounding code — it may be `logger` not `_logger`), and the same cancellation token the method body uses for server calls.

3. `DaemonRunner.RunAsync`: next to the existing `coverageStateDir` computation (~line 211, `Path.Combine(config.StateDir ?? DaemonLockPaths.Directory, DaemonLockPaths.Sanitize(config.Name))`), register:

```csharp
builder.Services.AddSingleton(sp => new LaunchConsentStore(
    coverageStateDir, sp.GetRequiredService<ILogger<LaunchConsentStore>>()));
builder.Services.AddSingleton(sp => new LaunchConsentDecisionLog(
    coverageStateDir, sp.GetRequiredService<ILogger<LaunchConsentDecisionLog>>()));
builder.Services.AddSingleton(sp => new LaunchConsentGate(
    sp.GetRequiredService<LaunchConsentStore>(),
    sp.GetRequiredService<LaunchConsentDecisionLog>(),
    sp.GetService<ILaunchConsentPrompter>(),   // null until Task 6 registers the broker
    sp.GetRequiredService<ILogger<LaunchConsentGate>>()));
```

(If `AgentOrchestrator` is registered via `AddSingleton<AgentOrchestrator>()`, the new ctor param resolves from DI automatically.)

4. Test factory `BuildOrchestrator` (`AgentOrchestratorVendorTests.cs:57`): add an optional parameter `LaunchConsentGate? consentGate = null` and default it inside to a gate built over the factory's temp `StateDir` with an upgrade-safe store and null prompter — so every existing test keeps passing unchanged:

```csharp
consentGate ??= new LaunchConsentGate(
    new LaunchConsentStore(stateDir, NullLogger.Instance),
    new LaunchConsentDecisionLog(stateDir, NullLogger.Instance),
    prompter: null, NullLogger<LaunchConsentGate>.Instance);
```

(`stateDir` = the same temp state dir the factory already creates at line ~76.)

- [ ] **Step 6: Write the failing orchestrator-level tests**

Append to `LaunchConsentGateTests.cs` (or a new partial next to the orchestrator tests if the factory is `private` — in that case put these in `AgentOrchestratorVendorTests.cs` following its in-class conventions):

```csharp
[Test]
public async Task Server_launch_denied_under_deny_default_sends_coded_launch_failed() {
    // Build orchestrator with a deny-default gate via BuildOrchestrator(consentGate: ...),
    // a CaptureServerConnection, and a SpyPtyProcessFactory.
    // Invoke: await orch.HandleLaunchAgentForTest(new LaunchAgentCommand("a1", ...vendor: "claude"));
    // Assert: capture.LaunchFailures contains ("a1", reason) with reason.StartsWith("launch_denied_by_owner:");
    // Assert: the pty/runtime spy saw zero starts.
}

[Test]
public async Task Owner_launch_proceeds_under_deny_default() {
    // Same setup, but LaunchAgentCommand(..., RequesterIsOwner: true).
    // Assert: no LaunchFailed with the consent prefix (the launch proceeds to the normal
    // vendor path — reuse whatever success/failure assertion the neighboring vendor tests use).
}

[Test]
public async Task Local_spawn_bypasses_consent_under_deny_default() {
    // Use the existing local-spawn test path (HandleLocalSpawnAsync harness from
    // AgentOrchestratorLocalAttachTests) with a deny-default gate.
    // Assert: spawn succeeds — consent is never consulted on the owner socket path.
}
```

These three tests must be written as REAL tests: copy the arrange/act/assert mechanics from the nearest existing test in `AgentOrchestratorVendorTests.cs` (for the first two) and `AgentOrchestratorLocalAttachTests.cs` (for the third), changing only the gate and the asserted reason. The comments above specify the behavior contract; the mechanics (which spy fields to read, how `CaptureServerConnection` records `LaunchFailed`) must match the existing doubles — read them first.

- [ ] **Step 7: Run all touched test classes**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/LaunchConsentGateTests/*"`
Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/AgentOrchestratorVendorTests/*"`
Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/AgentOrchestratorLocalAttachTests/*"`
Expected: all PASS (pre-existing orchestrator tests prove the default-allow gate is invisible).

- [ ] **Step 8: Commit**

```bash
git add -A src/Capacitor.Cli.Daemon test/Capacitor.Cli.Tests.Unit
git commit -m "feat: consent gate at the launch choke point with coded denial (AI-1623)"
```

---

### Task 6: Prompt broker (`LaunchConsentBroker`)

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Services/LaunchConsentBroker.cs`
- Modify: `src/Capacitor.Cli.Daemon/DaemonRunner.cs` (register broker as `ILaunchConsentPrompter`)
- Test: `test/Capacitor.Cli.Tests.Unit/Daemon/LaunchConsentBrokerTests.cs`

**Interfaces:**
- Consumes: `ILaunchConsentPrompter`, `LaunchConsentPromptRequest` (Task 5).
- Produces:
  - `internal sealed class LaunchConsentBroker : ILaunchConsentPrompter`
  - `(Guid id, ChannelReader<LaunchConsentPromptRequest> reader) Subscribe()` — unbounded channel; on subscribe, all currently-pending requests are replayed into the new channel first.
  - `void Unsubscribe(Guid id)`
  - `bool TryResolve(string requestId, bool allow)` — first resolution wins; `false` if unknown/already resolved.
  - `IReadOnlyList<LaunchConsentPromptRequest> PendingSnapshot()`

- [ ] **Step 1: Write the failing tests**

```csharp
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Tests.Unit.Daemon;

public class LaunchConsentBrokerTests {
    static LaunchConsentPromptRequest Req(string id = "a1") =>
        new(id, "user_x", "agent", "/tmp/repo", "claude", DateTimeOffset.UtcNow.ToString("O"), 5);

    [Test]
    public async Task No_subscriber_reports_HasSubscriber_false() {
        var broker = new LaunchConsentBroker();
        await Assert.That(broker.HasSubscriber).IsFalse();
        var (id, _) = broker.Subscribe();
        await Assert.That(broker.HasSubscriber).IsTrue();
        broker.Unsubscribe(id);
        await Assert.That(broker.HasSubscriber).IsFalse();
    }

    [Test]
    public async Task Prompt_delivers_to_subscriber_and_resolution_completes_it() {
        var broker = new LaunchConsentBroker();
        var (_, reader) = broker.Subscribe();
        var pending = broker.PromptAsync(Req(), TimeSpan.FromSeconds(30), CancellationToken.None);
        var delivered = await reader.ReadAsync(new CancellationTokenSource(5000).Token);
        await Assert.That(delivered.RequestId).IsEqualTo("a1");
        await Assert.That(broker.TryResolve("a1", allow: true)).IsTrue();
        await Assert.That(await pending).IsEqualTo(true);
        await Assert.That(broker.TryResolve("a1", allow: true)).IsFalse(); // already resolved
    }

    [Test]
    public async Task Prompt_times_out_to_null() {
        var broker = new LaunchConsentBroker();
        broker.Subscribe();
        var result = await broker.PromptAsync(Req(), TimeSpan.FromMilliseconds(50), CancellationToken.None);
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Late_subscriber_receives_pending_snapshot_replay() {
        var broker = new LaunchConsentBroker();
        broker.Subscribe(); // HasSubscriber must be true for the gate to even prompt
        var pending = broker.PromptAsync(Req("a2"), TimeSpan.FromSeconds(30), CancellationToken.None);
        await Assert.That(broker.PendingSnapshot().Count).IsEqualTo(1);
        var (_, lateReader) = broker.Subscribe();
        var replayed = await lateReader.ReadAsync(new CancellationTokenSource(5000).Token);
        await Assert.That(replayed.RequestId).IsEqualTo("a2");
        broker.TryResolve("a2", false);
        await Assert.That(await pending).IsEqualTo(false);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/LaunchConsentBrokerTests/*"`
Expected: build error.

- [ ] **Step 3: Implement the broker**

```csharp
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Capacitor.Cli.Daemon.Services;

/// In-memory rendezvous between the consent gate (awaiting a verdict) and local-socket
/// subscribers (the desktop app / kcap daemon consent). First resolution wins; a request
/// vanishes on resolve or timeout. Never persisted — a daemon restart clears pending prompts
/// (the server retries or fails the launch with the coded timeout denial).
internal sealed class LaunchConsentBroker : ILaunchConsentPrompter {
    sealed record Pending(LaunchConsentPromptRequest Request, TaskCompletionSource<bool> Tcs);

    readonly ConcurrentDictionary<string, Pending> _pending = new();
    readonly ConcurrentDictionary<Guid, Channel<LaunchConsentPromptRequest>> _subscribers = new();

    public bool HasSubscriber => !_subscribers.IsEmpty;

    public (Guid id, ChannelReader<LaunchConsentPromptRequest> reader) Subscribe() {
        var id = Guid.NewGuid();
        var ch = Channel.CreateUnbounded<LaunchConsentPromptRequest>(
            new UnboundedChannelOptions { SingleReader = true });
        foreach (var p in _pending.Values) ch.Writer.TryWrite(p.Request);
        _subscribers[id] = ch;
        return (id, ch.Reader);
    }

    public void Unsubscribe(Guid id) {
        if (_subscribers.TryRemove(id, out var ch)) ch.Writer.TryComplete();
    }

    public async Task<bool?> PromptAsync(LaunchConsentPromptRequest req, TimeSpan timeout, CancellationToken ct) {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(req.RequestId, new Pending(req, tcs))) return null;
        try {
            foreach (var ch in _subscribers.Values) ch.Writer.TryWrite(req);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try { return await tcs.Task.WaitAsync(cts.Token); }
            catch (OperationCanceledException) { return null; }
        } finally {
            _pending.TryRemove(req.RequestId, out _);
        }
    }

    public bool TryResolve(string requestId, bool allow) =>
        _pending.TryGetValue(requestId, out var p) && p.Tcs.TrySetResult(allow);

    public IReadOnlyList<LaunchConsentPromptRequest> PendingSnapshot() =>
        _pending.Values.Select(p => p.Request).ToList();
}
```

- [ ] **Step 4: Register in DI**

In `DaemonRunner.RunAsync`, next to the Task 5 registrations:

```csharp
builder.Services.AddSingleton<LaunchConsentBroker>();
builder.Services.AddSingleton<ILaunchConsentPrompter>(sp => sp.GetRequiredService<LaunchConsentBroker>());
```

(The Task 5 gate registration already resolves `ILaunchConsentPrompter` via `GetService`, so it now receives the broker.)

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/LaunchConsentBrokerTests/*"`
Expected: all PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/LaunchConsentBroker.cs \
        src/Capacitor.Cli.Daemon/DaemonRunner.cs \
        test/Capacitor.Cli.Tests.Unit/Daemon/LaunchConsentBrokerTests.cs
git commit -m "feat: consent prompt broker with timeout and pending replay (AI-1623)"
```

---

### Task 7: Local-socket consent frames + handlers

**Files:**
- Modify: `src/Capacitor.Cli.Core/LocalIpc/FrameType.cs` (append values)
- Modify: `src/Capacitor.Cli.Core/LocalIpc/FrameCodec.cs` (encode/decode arms)
- Modify: `src/Capacitor.Cli.Core/LocalIpc/LocalFrame.cs` (factories)
- Create: `src/Capacitor.Cli.Core/LocalIpc/ConsentIpc.cs` (payload DTOs + context)
- Create: `src/Capacitor.Cli.Daemon/Services/LaunchConsentIpc.cs` (handlers)
- Modify: `src/Capacitor.Cli.Daemon/Services/LocalControlServer.cs` (ctor + routing)
- Test: `test/Capacitor.Cli.Tests.Unit/FrameCodecConsentTests.cs`, `test/Capacitor.Cli.Tests.Unit/Daemon/LaunchConsentIpcTests.cs`

**Interfaces:**
- Consumes: `LocalControlServer` routing switch (`LocalControlServer.cs:42-54`); `FrameCodec.WriteAsync/ReadAsync`; broker + store + Task 5 types; real-socket test harness pattern (`AgentOrchestratorLocalAttachTests.cs:472-560`).
- Produces:
  - `FrameType` additions (client→daemon): `ConsentSubscribe = 11, ConsentResolve = 12, ConsentRulesGet = 13, ConsentRulesPut = 14`; (daemon→client): `ConsentPending = 72, ConsentRules = 73, ConsentAck = 74`.
  - All consent payloads are UTF-8 JSON in the frame's `Text` (snake_case via `ConsentIpcJsonContext`).
  - Core DTOs (public — the CLI and future app consume them):
    - `ConsentPendingDto(string RequestId, string? Requester, string Kind, string RepoPath, string Vendor, string RequestedAt, int TimeoutSeconds)`
    - `ConsentResolveDto(string RequestId, string Decision, ConsentRuleDto? SaveRule)` — `Decision`: `"allow"|"deny"`
    - `ConsentRuleDto(string Action, string? Requester, string? Kind, string? Repo, string? Vendor)`
    - `ConsentPolicyDto(string Default, int PromptTimeoutSeconds, List<ConsentRuleDto> Rules)`
    - `ConsentAckDto(bool Ok, string? Error)`
  - `internal sealed class LaunchConsentIpc` — ctor `(LaunchConsentBroker broker, LaunchConsentStore store, ILogger<LaunchConsentIpc> logger)`; handlers `HandleSubscribeAsync(Stream, CancellationToken)`, `HandleResolveAsync(string payload, Stream, CancellationToken)`, `HandleRulesGetAsync(Stream, CancellationToken)`, `HandleRulesPutAsync(string payload, Stream, CancellationToken)`.
- Protocol behavior:
  - `ConsentSubscribe`: long-lived. Daemon replays pending then pushes each new request as a `ConsentPending` frame. The handler also reads from the stream; clean EOF (client gone) unsubscribes. Any client→daemon frame received on this connection is ignored except EOF.
  - `ConsentResolve`: one-shot. Parses `ConsentResolveDto`; if `SaveRule` is present, appends it to the policy via `store.TryReplace(current with rule appended)` BEFORE resolving; replies `ConsentAck` (`Ok=false` + error when the request id is unknown or the rule invalid); connection closes.
  - `ConsentRulesGet` → one `ConsentRules` frame carrying `ConsentPolicyDto`.
  - `ConsentRulesPut` → full-document replace via `store.TryReplace`, reply `ConsentAck`.

- [ ] **Step 1: Write the failing codec round-trip test**

`test/Capacitor.Cli.Tests.Unit/FrameCodecConsentTests.cs`, pattern-matching `FrameCodecTests.cs:6-13`:

```csharp
using Capacitor.Core.LocalIpc; // match FrameCodecTests' usings

namespace Capacitor.Cli.Tests.Unit;

public class FrameCodecConsentTests {
    static async Task<LocalFrame> RoundTrip(LocalFrame f) {
        using var ms = new MemoryStream();
        await FrameCodec.WriteAsync(ms, f, CancellationToken.None);
        ms.Position = 0;
        return (await FrameCodec.ReadAsync(ms, CancellationToken.None))!;
    }

    [Test]
    [Arguments(FrameType.ConsentSubscribe)]
    [Arguments(FrameType.ConsentResolve)]
    [Arguments(FrameType.ConsentRulesGet)]
    [Arguments(FrameType.ConsentRulesPut)]
    [Arguments(FrameType.ConsentPending)]
    [Arguments(FrameType.ConsentRules)]
    [Arguments(FrameType.ConsentAck)]
    public async Task Consent_frames_roundtrip_with_text_payload(FrameType type) {
        var f = await RoundTrip(new LocalFrame(type) { Text = """{"k":"v"}""" });
        await Assert.That(f.Type).IsEqualTo(type);
        await Assert.That(f.Text).IsEqualTo("""{"k":"v"}""");
    }

    [Test]
    public async Task Consent_frame_values_are_stable_wire_bytes() {
        await Assert.That((byte)FrameType.ConsentSubscribe).IsEqualTo((byte)11);
        await Assert.That((byte)FrameType.ConsentResolve).IsEqualTo((byte)12);
        await Assert.That((byte)FrameType.ConsentRulesGet).IsEqualTo((byte)13);
        await Assert.That((byte)FrameType.ConsentRulesPut).IsEqualTo((byte)14);
        await Assert.That((byte)FrameType.ConsentPending).IsEqualTo((byte)72);
        await Assert.That((byte)FrameType.ConsentRules).IsEqualTo((byte)73);
        await Assert.That((byte)FrameType.ConsentAck).IsEqualTo((byte)74);
    }
}
```

Adjust the `LocalFrame` construction to the record's actual shape (`LocalFrame` is `public sealed record LocalFrame(FrameType Type)` with `Text` as a settable/init member — read `LocalFrame.cs` first; if `Text` is a positional or init-only property use the appropriate syntax, or add a static factory like `LocalFrame.ConsentJson(FrameType, string)`).

- [ ] **Step 2: Run to verify failure, then extend the enum + codec**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/FrameCodecConsentTests/*"`
Expected: build error (enum members missing).

Then:
1. `FrameType.cs`: append the seven values with a comment `// AI-1623 consent control frames — values append-only`.
2. `FrameCodec.cs`: add the seven types to the `Encode`/`Decode` arms as plain UTF-8 text payloads — copy exactly how an existing text-payload frame (e.g. `Attach`) is encoded/decoded.
3. `LocalFrame.cs`: if needed, add a factory `public static LocalFrame ConsentJson(FrameType type, string json) => new(type) { Text = json };` (match the record's construction idiom).

Re-run the filter above. Expected: PASS.

- [ ] **Step 3: Add the Core payload DTOs**

`src/Capacitor.Cli.Core/LocalIpc/ConsentIpc.cs` (namespace = the same as `FrameCodec`):

```csharp
using System.Text.Json.Serialization;

namespace Capacitor.Core.LocalIpc; // match FrameCodec.cs

/// JSON payloads for the consent control frames (AI-1623). snake_case on the wire; shared
/// verbatim by the daemon, the CLI, and the future desktop app.
public sealed record ConsentPendingDto(
    string RequestId, string? Requester, string Kind, string RepoPath, string Vendor,
    string RequestedAt, int TimeoutSeconds);

public sealed record ConsentResolveDto(string RequestId, string Decision, ConsentRuleDto? SaveRule);

public sealed record ConsentRuleDto(string Action, string? Requester, string? Kind, string? Repo, string? Vendor);

public sealed record ConsentPolicyDto(string Default, int PromptTimeoutSeconds, List<ConsentRuleDto> Rules);

public sealed record ConsentAckDto(bool Ok, string? Error);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ConsentPendingDto))]
[JsonSerializable(typeof(ConsentResolveDto))]
[JsonSerializable(typeof(ConsentPolicyDto))]
[JsonSerializable(typeof(ConsentAckDto))]
public partial class ConsentIpcJsonContext : JsonSerializerContext;
```

- [ ] **Step 4: Write the failing daemon-side integration test**

`test/Capacitor.Cli.Tests.Unit/Daemon/LaunchConsentIpcTests.cs` — copy the real-socket harness from `AgentOrchestratorLocalAttachTests.cs:472-560` verbatim (temp `DaemonLockPaths.OverrideDirectoryForTesting`, `[NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]`, `if (OperatingSystem.IsWindows()) return;`, socket-file poll). Tests:

```csharp
[Test]
public async Task RulesGet_returns_current_policy_and_RulesPut_replaces_it() {
    // Arrange: LocalControlServer wired with a LaunchConsentIpc over a fresh store.
    // Act 1: write ConsentRulesGet, read one frame.
    //   Assert: frame.Type == ConsentRules; deserialize ConsentPolicyDto → default "allow", 0 rules.
    // Act 2: write ConsentRulesPut with {"default":"deny","prompt_timeout_seconds":30,"rules":[]}.
    //   Assert: reply ConsentAck ok=true; store.Current.Default == Deny.
    // Act 3: ConsentRulesPut with an invalid rule action → ConsentAck ok=false, error mentions "action".
}

[Test]
public async Task Subscribe_receives_pending_and_Resolve_unblocks_the_gate() {
    // Arrange: server + broker + a gate built with prompt-default policy (timeout 30s).
    // Act: start gate.DecideAsync("a9", ...) on a background task (it awaits the prompt).
    //   Connect socket A, write ConsentSubscribe, read one frame → ConsentPending with request_id "a9".
    //   Connect socket B, write ConsentResolve {"request_id":"a9","decision":"allow"} → read ConsentAck ok=true.
    // Assert: the background DecideAsync returns Allowed=true, Source == "prompt_user".
}

[Test]
public async Task Resolve_with_save_rule_appends_to_policy() {
    // Same as above but decision "deny" + save_rule {"action":"deny","kind":"review-flow"}.
    // Assert: ack ok, store.Current.Rules contains the appended rule, gate returns denied.
}

[Test]
public async Task Resolve_unknown_request_acks_false() {
    // ConsentResolve {"request_id":"nope","decision":"allow"} → ConsentAck ok=false.
}
```

Write these as real tests with the harness mechanics filled in — every `// Arrange/Act/Assert` line above is a behavior contract, and the socket read/write plumbing comes from the existing harness. `LocalControlServer` construction gains the `LaunchConsentIpc` argument (Step 5); until then this file fails to compile, which is the failing state we want.

- [ ] **Step 5: Implement `LaunchConsentIpc` + routing**

`src/Capacitor.Cli.Daemon/Services/LaunchConsentIpc.cs`:

```csharp
using System.Text.Json;
using Capacitor.Core.LocalIpc;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// Local-socket handlers for the consent frames. Trust model: anything on the daemon's own
/// 0600 socket is the owner (same rule as HandleLocalSpawnAsync) — no further auth.
internal sealed class LaunchConsentIpc(
    LaunchConsentBroker broker, LaunchConsentStore store, ILogger<LaunchConsentIpc> logger) {

    public async Task HandleSubscribeAsync(Stream stream, CancellationToken ct) {
        var (id, reader) = broker.Subscribe();
        try {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // EOF watcher: a subscriber that disappears must flip HasSubscriber promptly,
            // otherwise prompt-mode launches would wait the full timeout for nobody.
            var eof = Task.Run(async () => {
                try { while (await FrameCodec.ReadAsync(stream, cts.Token) is not null) { } }
                catch { }
                cts.Cancel();
            }, cts.Token);
            await foreach (var req in reader.ReadAllAsync(cts.Token)) {
                var json = JsonSerializer.Serialize(ToDto(req), ConsentIpcJsonContext.Default.ConsentPendingDto);
                await FrameCodec.WriteAsync(stream, LocalFrame.ConsentJson(FrameType.ConsentPending, json), cts.Token);
            }
        } catch (OperationCanceledException) {
        } finally {
            broker.Unsubscribe(id);
        }
    }

    public async Task HandleResolveAsync(string payload, Stream stream, CancellationToken ct) {
        ConsentAckDto ack;
        try {
            var dto = JsonSerializer.Deserialize(payload, ConsentIpcJsonContext.Default.ConsentResolveDto);
            if (dto is null || dto.Decision is not ("allow" or "deny")) {
                ack = new ConsentAckDto(false, "invalid resolve payload (decision must be allow|deny)");
            } else {
                string? saveError = null;
                if (dto.SaveRule is { } r) {
                    var current = store.Current;
                    var next = current with {
                        Rules = [.. current.Rules, new LaunchConsentRule(r.Action, r.Requester, r.Kind, r.Repo, r.Vendor)] };
                    if (!store.TryReplace(next, out saveError))
                        logger.LogWarning("Consent save_rule rejected: {Error}", saveError);
                }
                var resolved = broker.TryResolve(dto.RequestId, dto.Decision == "allow");
                ack = resolved
                    ? new ConsentAckDto(saveError is null, saveError)
                    : new ConsentAckDto(false, "no pending consent request with that id");
            }
        } catch (JsonException) {
            ack = new ConsentAckDto(false, "malformed resolve payload");
        }
        await WriteAck(stream, ack, ct);
    }

    public async Task HandleRulesGetAsync(Stream stream, CancellationToken ct) {
        var p = store.Current;
        var dto = new ConsentPolicyDto(
            p.Default switch { LaunchConsentDefault.Deny => "deny", LaunchConsentDefault.Prompt => "prompt", _ => "allow" },
            p.PromptTimeoutSeconds,
            p.Rules.Select(r => new ConsentRuleDto(r.Action, r.Requester, r.Kind, r.Repo, r.Vendor)).ToList());
        var json = JsonSerializer.Serialize(dto, ConsentIpcJsonContext.Default.ConsentPolicyDto);
        await FrameCodec.WriteAsync(stream, LocalFrame.ConsentJson(FrameType.ConsentRules, json), ct);
    }

    public async Task HandleRulesPutAsync(string payload, Stream stream, CancellationToken ct) {
        ConsentAckDto ack;
        try {
            var dto = JsonSerializer.Deserialize(payload, ConsentIpcJsonContext.Default.ConsentPolicyDto);
            if (dto is null) {
                ack = new ConsentAckDto(false, "malformed policy payload");
            } else {
                var def = dto.Default switch {
                    "deny" => LaunchConsentDefault.Deny, "prompt" => LaunchConsentDefault.Prompt,
                    "allow" => LaunchConsentDefault.Allow,
                    _ => (LaunchConsentDefault?)null };
                if (def is null) {
                    ack = new ConsentAckDto(false, "invalid default (allow|deny|prompt)");
                } else {
                    var next = new LaunchConsentPolicy(def.Value, dto.PromptTimeoutSeconds,
                        dto.Rules.Select(r => new LaunchConsentRule(r.Action, r.Requester, r.Kind, r.Repo, r.Vendor)).ToList());
                    ack = store.TryReplace(next, out var error)
                        ? new ConsentAckDto(true, null) : new ConsentAckDto(false, error);
                }
            }
        } catch (JsonException) {
            ack = new ConsentAckDto(false, "malformed policy payload");
        }
        await WriteAck(stream, ack, ct);
    }

    static ConsentPendingDto ToDto(LaunchConsentPromptRequest r) =>
        new(r.RequestId, r.Requester, r.Kind, r.RepoPath, r.Vendor, r.RequestedAt, r.TimeoutSeconds);

    static Task WriteAck(Stream stream, ConsentAckDto ack, CancellationToken ct) {
        var json = JsonSerializer.Serialize(ack, ConsentIpcJsonContext.Default.ConsentAckDto);
        return FrameCodec.WriteAsync(stream, LocalFrame.ConsentJson(FrameType.ConsentAck, json), ct);
    }
}
```

`LocalControlServer.cs`: add `LaunchConsentIpc consentIpc` to the primary-constructor parameter list and route:

```csharp
case FrameType.ConsentSubscribe: await consentIpc.HandleSubscribeAsync(stream, ct); break;
case FrameType.ConsentResolve:   await consentIpc.HandleResolveAsync(first.Text, stream, ct); break;
case FrameType.ConsentRulesGet:  await consentIpc.HandleRulesGetAsync(stream, ct); break;
case FrameType.ConsentRulesPut:  await consentIpc.HandleRulesPutAsync(first.Text, stream, ct); break;
```

Also update the `default:` arm's expected-frames message, register `LaunchConsentIpc` in `DaemonRunner` (`builder.Services.AddSingleton<LaunchConsentIpc>();`), and update every `LocalControlServer` construction site (DaemonRunner DI resolves automatically; the test harnesses construct it explicitly — add the new argument there).

- [ ] **Step 6: Run all touched test classes**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/FrameCodecConsentTests/*"`
Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/LaunchConsentIpcTests/*"`
Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/AgentOrchestratorLocalAttachTests/*"`
Expected: all PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Capacitor.Cli.Core/LocalIpc src/Capacitor.Cli.Daemon/Services \
        src/Capacitor.Cli.Daemon/DaemonRunner.cs test/Capacitor.Cli.Tests.Unit
git commit -m "feat: consent frames over the local control socket (AI-1623)"
```

---

### Task 8: `kcap daemon consent` CLI subcommand + docs

**Files:**
- Create: `src/Capacitor.Cli/Commands/DaemonConsentCommand.cs`
- Modify: `src/Capacitor.Cli/Commands/DaemonCommands.cs:23-32` (switch arm) + `PrintUsage()` (~line 934)
- Modify: `src/Capacitor.Cli.Core/Resources/help-daemon.txt`
- Modify: `README.md` (`## CLI commands` — daemon section)
- Test: `test/Capacitor.Cli.Tests.Unit/DaemonConsentCommandTests.cs`

**Interfaces:**
- Consumes: `LocalSocketPaths.Socket(name)`, `FrameCodec`, `ConsentIpcJsonContext` DTOs (Task 7); the request/response socket idiom of `AgentCommand.cs:220-226`; `DaemonCommands.ResolveName` (`DaemonCommands.cs:35`); the state-dir derivation `Path.Combine(DaemonLockPaths.Directory, DaemonLockPaths.Sanitize(name))` for `log`.
- Produces: `public static class DaemonConsentCommand { public static Task<int> HandleAsync(string[] args); internal static ConsentRuleDto? TryBuildRule(string action, string[] flags, out string? error); }`

Subcommands (all take `--name <daemon>` like the rest of `DaemonCommands`):

```
kcap daemon consent show                       # ConsentRulesGet → print default, timeout, numbered rules
kcap daemon consent set-default <allow|deny|prompt>
kcap daemon consent allow [--requester U] [--kind agent|review|review-flow] [--repo PATH] [--vendor V]
kcap daemon consent deny  [--requester U] [--kind ...] [--repo PATH] [--vendor V]
kcap daemon consent remove <index>             # index as printed by `show`
kcap daemon consent log [-n N]                 # tail N (default 20) lines of consent-decisions.jsonl (direct file read — works with the daemon stopped)
```

`show`/`set-default`/`allow`/`deny`/`remove` mutate via Get→modify→`ConsentRulesPut` over the socket and require a running daemon (print `daemon is not running (socket not found at <path>)` and return 1 otherwise). At least one flag is required for `allow`/`deny` (an all-wildcard allow rule is a no-op; an all-wildcard deny is expressible via `set-default deny` — reject flagless invocations with a hint saying exactly that).

- [ ] **Step 1: Write the failing tests for the pure helper**

```csharp
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit;

public class DaemonConsentCommandTests {
    [Test]
    public async Task BuildRule_maps_flags_to_rule_fields() {
        var rule = DaemonConsentCommand.TryBuildRule("deny",
            ["--requester", "user_x", "--kind", "review-flow", "--vendor", "codex"], out var error);
        await Assert.That(error).IsNull();
        await Assert.That(rule!.Action).IsEqualTo("deny");
        await Assert.That(rule.Requester).IsEqualTo("user_x");
        await Assert.That(rule.Kind).IsEqualTo("review-flow");
        await Assert.That(rule.Repo).IsNull();
        await Assert.That(rule.Vendor).IsEqualTo("codex");
    }

    [Test]
    public async Task BuildRule_rejects_flagless_and_unknown_flags_and_bad_kind() {
        await Assert.That(DaemonConsentCommand.TryBuildRule("allow", [], out var e1)).IsNull();
        await Assert.That(e1).Contains("at least one");
        await Assert.That(DaemonConsentCommand.TryBuildRule("allow", ["--nope", "x"], out var e2)).IsNull();
        await Assert.That(e2).Contains("--nope");
        await Assert.That(DaemonConsentCommand.TryBuildRule("allow", ["--kind", "flows"], out var e3)).IsNull();
        await Assert.That(e3).Contains("kind");
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/DaemonConsentCommandTests/*"`
Expected: build error.

- [ ] **Step 3: Implement the command**

`DaemonConsentCommand.cs` skeleton — socket plumbing copied from `AgentCommand.cs:220-226` (connect `UnixDomainSocketEndPoint`, `NetworkStream`, one write + one read):

```csharp
using System.Net.Sockets;
using System.Text.Json;
using Capacitor.Core.LocalIpc;

namespace Capacitor.Cli.Commands;

public static class DaemonConsentCommand {
    public static async Task<int> HandleAsync(string[] args) {
        // args = everything after "consent". Dispatch on args[0]:
        //   show | set-default | allow | deny | remove | log — else PrintConsentUsage() + return 1.
        // Shared pre-step for socket verbs: resolve daemon name (same helper DaemonCommands uses),
        // socket path = LocalSocketPaths.Socket(name); if !File.Exists → error message + 1.
        ...
    }

    internal static ConsentRuleDto? TryBuildRule(string action, string[] flags, out string? error) {
        string? requester = null, kind = null, repo = null, vendor = null;
        for (var i = 0; i < flags.Length; i += 2) {
            if (i + 1 >= flags.Length) { error = $"missing value for {flags[i]}"; return null; }
            switch (flags[i]) {
                case "--requester": requester = flags[i + 1]; break;
                case "--kind":
                    if (flags[i + 1] is not ("agent" or "review" or "review-flow")) {
                        error = "invalid --kind (agent|review|review-flow)"; return null; }
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

    static async Task<ConsentPolicyDto?> GetPolicyAsync(string socketPath) { /* ConsentRulesGet → read ConsentRules frame → deserialize */ }
    static async Task<int> PutPolicyAsync(string socketPath, ConsentPolicyDto policy) { /* ConsentRulesPut → read ConsentAck; print error when !ok */ }
}
```

Fill the `...` and helper bodies completely: `show` prints `default`, `prompt timeout`, then `  [i] action requester=… kind=… repo=… vendor=…` per rule (wildcards printed as `*`); `set-default`/`allow`/`deny`/`remove` do Get → transform → Put; `log` resolves `Path.Combine(DaemonLockPaths.Directory, DaemonLockPaths.Sanitize(name), "consent-decisions.jsonl")`, prints the last N raw lines (plus `.1` backup lines first if N exceeds the live file), `-n` parsed like other commands do. Follow the output/error style of the surrounding `DaemonCommands` verbs (stderr for errors, exit 1).

Wire the dispatch: in `DaemonCommands.HandleAsync` switch add `"consent" => await DaemonConsentCommand.HandleAsync(remaining),`.

- [ ] **Step 4: Update docs**

- `PrintUsage()` in `DaemonCommands.cs`: add the consent verbs.
- `src/Capacitor.Cli.Core/Resources/help-daemon.txt`: add a `consent` section documenting all six verbs, the three defaults (`allow` upgrade-safe / `deny` / `prompt`), rule matching fields, first-match-wins, and the owner-always-allowed built-in.
- `README.md` `## CLI commands`: add `kcap daemon consent` with a two-sentence description and a pointer that denials surface to the server as `launch_denied_by_owner`.

- [ ] **Step 5: Run tests + build**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/DaemonConsentCommandTests/*"`
Expected: PASS.
Run: `dotnet build Capacitor.slnx`
Expected: no errors.

- [ ] **Step 6: Manual smoke (optional but cheap)**

```bash
dotnet run --project src/Capacitor.Cli -- daemon consent show
```
Expected: `daemon is not running (socket not found at ...)` (unless a dev daemon is up, in which case the default policy prints).

- [ ] **Step 7: Commit**

```bash
git add src/Capacitor.Cli src/Capacitor.Cli.Core/Resources/help-daemon.txt README.md \
        test/Capacitor.Cli.Tests.Unit/DaemonConsentCommandTests.cs
git commit -m "feat: kcap daemon consent subcommand (AI-1623)"
```

---

### Task 9: Server companion — stamp requester identity (kcap-server repo)

**Files (different repo — `/Users/alexey/dev/temp/kcap-server`, its own branch + PR titled `[AI-1623] …`):**
- Modify: the server-side `LaunchAgentCommand` DTO mirror + every `LaunchAgent` hub dispatch site.
- Test: the owning suites (`test/Capacitor.Server.Tests.Agents`, `test/Capacitor.Server.Tests.Flows`).

This task is deliberately exploratory-first because the server seams were not surveyed for this plan. It ships independently of Tasks 1–8 and in either order (both sides are null-safe).

- [ ] **Step 1: Locate the seams**

In the kcap-server repo: `rg -n '"LaunchAgent"' src/ --type cs` and `rg -n 'LaunchAgentCommand|launch_agent' src/ --type cs`. Expected sites (from CLAUDE.md context): `CapacitorHub` (hosted-agent launch), `AgentStoreDataService` (in-process Blazor launch), and the flow launch path (`FlowOrchestratorService` / the AI-1526 `TryPrepareLaunchAsync` transport start). Identify (a) the server's launch-command DTO, (b) where the daemon's owner user id is known (daemon registration/`DaemonRegistry`), (c) the initiating principal at each dispatch site (hub caller user id; flow driver session owner for flow-launched participants).

- [ ] **Step 2: Write failing tests in the owning suites**

One test per dispatch site asserting the serialized launch command now carries `requester_user_id` = the initiating user and `requester_is_owner` = (initiator == daemon owner). Follow each suite's existing launch-dispatch test patterns (they already assert command payload fields for AI-1417 model overrides).

- [ ] **Step 3: Add the fields and stamp them**

Add `RequesterUserId`/`RequesterIsOwner` (snake_case wire) as trailing optional members of the server's DTO. At each dispatch site, populate from the authenticated principal / flow-run owner, and compute `RequesterIsOwner` against the target daemon's registered owner. Never guess: when the initiating principal is genuinely unavailable (system-initiated relaunch/heal), send `null` — the daemon treats null as unknown.

- [ ] **Step 4: Run the two suites, commit, PR**

```bash
dotnet run --project test/Capacitor.Server.Tests.Agents/Capacitor.Server.Tests.Agents.csproj
dotnet run --project test/Capacitor.Server.Tests.Flows/Capacitor.Server.Tests.Flows.csproj
```
Expected: PASS (note: these are heavy Testcontainers suites — if local Docker can't carry them, control-run and rely on CI per team practice). Commit `feat: stamp requester identity on daemon launch commands (AI-1623)`, open the PR referencing AI-1623.

---

### Task 10: Full verification, AOT publish, docs sync

**Files:**
- Modify: `CLAUDE.md` (kcap-cli repo — add a consent bullet to the daemon section)
- No new code.

- [ ] **Step 1: Run the full unit + integration suites**

```bash
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --maximum-parallel-tests 1
dotnet run --project test/Capacitor.Cli.Tests.Integration/Capacitor.Cli.Tests.Integration.csproj
```
Expected: all PASS.

- [ ] **Step 2: AOT publish check (both binaries)**

```bash
dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}' || echo CLEAN
dotnet publish src/Capacitor.Cli.Daemon/Capacitor.Cli.Daemon.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}' || echo CLEAN
```
Expected: `CLEAN` from both (no IL2026/IL3050-class warnings from the new JSON contexts).

- [ ] **Step 3: Update kcap-cli `CLAUDE.md`**

Add one bullet under the daemon section: consent engine location (`LaunchConsent*` in `Capacitor.Cli.Daemon/Services`), the policy file (`{state}/consent.json`, daemon single-writer, upgrade-safe default `allow`), the decision log, the coded `launch_denied_by_owner:` reason, the new consent `FrameType` values 11–14/72–74, and the `kcap daemon consent` verbs. Two–four sentences, matching the file's existing density.

- [ ] **Step 4: Final commit**

```bash
git add CLAUDE.md
git commit -m "docs: consent engine notes in CLAUDE.md (AI-1623)"
```

- [ ] **Step 5: Post the plan + spec to Linear**

Per team convention: post this plan as a comment on AI-1623 (the umbrella spec is already a comment on AI-1622). The PR title: `[AI-1623] Daemon launch-consent engine + control IPC`.

---

## Deferred / out of scope (do not build)

- The `subscribe` daemon-state/agent-list IPC stream from the umbrella spec §4 — slice 2 defines its exact shape when the app consumes it; `List`/`StopV2` frames already cover polling needs.
- The umbrella spec §4 describes the control channel as "length-prefixed JSON frames with a versioned hello". This slice deliberately deviates: it extends the EXISTING binary `FrameCodec` (`[1B type][4B BE len][payload]`) with JSON payloads inside the new frame types, and adds no hello. One protocol, append-only evolution. The versioned hello lands in slice 2 with the app's attach flow, where version-skew detection is actually consumed.
- Windows named-pipe transport and Unix peer-credential checks — the existing 0600-socket trust model is unchanged (Windows keeps its documented weaker boundary).
- Any interactive prompting in the CLI (`kcap daemon consent` never blocks a launch waiting for terminal input).
- Auto-relaunch of a launch denied then later approved — the server's existing retry/heal machinery owns retries.
- Config hot-reload generality — only the consent file is runtime-mutable, via its own store.
