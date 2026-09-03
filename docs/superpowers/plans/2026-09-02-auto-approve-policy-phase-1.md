# Auto-Approve Policy — Phase 1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship the deterministic policy engine with canonical actions, a per-session policy snapshot built from the two local scopes (repo `.kcap/approvals.yaml`, user `~/.config/kcap/approvals.yaml`), both Claude seams (PreToolUse and PermissionRequest), the hosted-Claude and ACP daemon insertions, and recorded provenance events.

**Architecture:** A pure, vendor-neutral engine in `Capacitor.Cli.Core/Policy/` (canonical actions → component sets → tighten-only merge with full allow coverage), fed by per-vendor normalizers. Local seams are new branches in the existing Claude hook lane; hosted seams are pre-parking insertions at the daemon's two existing choke points. Decisions ride the existing `/hooks/{route}` + `HookSpool` transport from the CLI and `AppendAgentRunEventAsync` from the daemon. The judge, server-stored scopes, caps, and `enforcement` are later phases — but the document parser already rejects server-scope fields in local documents, and the engine already has a tighten-only evaluation mode.

**Tech Stack:** .NET 10, NativeAOT, System.Text.Json source generation, TUnit + WireMock.Net. No new package dependencies — the approvals file is parsed by a strict hand-rolled YAML-subset parser (the repo's established pattern: `CopilotWorkspaceYaml` hand-rolls YAML; the only config-format library, Tomlyn, needed a hand-written AOT type-info context).

**Spec:** `docs/superpowers/specs/2026-09-01-auto-approve-policy-design.md` — this plan implements its "Phasing" item 1. Read the spec's **Canonical actions**, **Matching semantics**, **Merge rule**, **Seams**, and **The per-call decision journal** sections before starting any task; they are normative and this plan's code follows them.

## Global Constraints

- **NativeAOT:** no reflection-based JSON. Every new wire/persisted type is registered on a source-generated `JsonSerializerContext`. `dotnet build` does NOT surface AOT warnings — run `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` after wire-type changes (Task 19 gates on it).
- **No `JsonArray` collection expressions** (`[a, b]` needs dynamic code) — use `new JsonArray(a, b)`.
- **`Environment.GetFolderPath` is banned** (`RS0030`). Take `UserHome` / `ConfigRoot` — both are resolved once in each composition root and passed down.
- **Use `JsonElementExtensions`** (`el.Str("x")`, `el.Obj("x")`, `el.Arr("x")`, `el.IsObject`…) instead of `ValueKind` checks.
- **Unanalyzed shell is never allow-eligible** (spec acceptance criterion 2). Every task touching shell matching preserves this; no task may add an allow path for an unanalyzed command.
- **Pass-through means silence.** A no-decision outcome writes nothing to stdout at a Claude seam and falls through unchanged at a daemon seam. Enabling the policy must never make a session noisier or slower when no policy files exist (`PolicySnapshot.IsEmpty` short-circuits before any I/O beyond one small file read).
- **Comments are scarce** — per the project CLAUDE.md rules: no change narration, no spec coordinates ("§", "phase 2"), no ticket ids except open TODO work. Do not imitate existing long comments.
- **Commit subjects:** one imperative clause, `(#738)` trailing reference, ≤ 80 chars total.
- **Test layout mirrors prod:** engine tests in `test/Capacitor.Cli.Core.Tests.Unit/Policy/`, CLI seam tests in `test/Capacitor.Cli.Tests.Unit/`, daemon tests in `test/Capacitor.Cli.Daemon.Tests.Unit/`. Temp dirs come from Helpers (`[TempDir]`, `[TempConfigRoot]`, `[TempHome]` injected properties — set *after* construction, so derive paths in expression-bodied members, never in constructors).
- **TUnit filtering:** `--treenode-filter '/*/*/<ClassName>/*'`, never `--filter`.

## File Structure

New production files (namespace follows directory — compiler-enforced):

```
src/Capacitor.Cli.Core/Policy/
  CanonicalAction.cs          ActionKind, ShellSegment, CanonicalAction, ActionComponent hierarchy, PolicyComponents
  GlobPattern.cs              '*'/'?' glob matcher
  ShellTokenPattern.cs        token-wise pattern matching, outcome-sensitive anchoring
  ShellCommandAnalyzer.cs     allowlist grammar → analyzed segments | unanalyzed
  ShellFragmentLexer.cs       conservative lexing of unanalyzed raw commands
  LexicalPaths.cs             cwd-relative resolution + lexical . / .. normalization
  ApprovalsYaml.cs            YamlNode model + strict subset parser + ApprovalsYamlException
  PolicyDocument.cs           PolicyScope, RuleOutcome, RuleMatcher, PolicyRule, JudgeConfig, PolicyDocument,
                              PolicyDocumentBinder, PolicyDocumentException
  RuleMatch.cs                restriction/coverage matching of one rule against one component
  PolicyEngine.cs             EvaluationMode, PolicyOutcome, MatchedRuleRef, PolicyEvaluation, Evaluate()
  PolicySnapshot.cs           PolicySnapshot, PolicyScopeDocument
  PolicySnapshotBuilder.cs    file discovery, parse/degrade, content-hash id
  PolicySnapshotStore.cs      per-session persisted snapshot under ~/.config/kcap/policy/sessions/
  PolicyJsonContext.cs        scoped JsonSerializerContext for persisted policy state
  PolicyDecisionJournal.cs    per-session journal: exact call-id mode + ask-only FIFO fallback + pass-through counter
  PolicyInputHash.cs          canonical (tool_name, tool_input) digest
  PolicyWire.cs               PolicyActionV1, PolicyMatchedRuleV1, PolicyDecisionEventV1, PolicySnapshotUploadV1,
                              PolicySnapshotDocV1, PolicySeams constants, PolicyWire.ToWire()
src/Capacitor.Cli.Core/Harness/Claude/
  ClaudeActionNormalizer.cs   Claude tool payload → CanonicalAction
src/Capacitor.Cli.Core/Acp/
  AcpActionNormalizer.cs      ACP toolCall → CanonicalAction
src/Capacitor.Cli/Policy/
  PolicyDecisionEmitter.cs    decision + snapshot upload over AgentHookPoster/HookSpool
src/Capacitor.Cli/Harness/Claude/
  ClaudePolicySeam.cs         shared seam logic for pre-tool-use and permission-request branches
src/Capacitor.Cli.Daemon/Services/
  PolicySnapshotProvider.cs   daemon-side snapshot construction at launch
```

Modified: `src/Capacitor.Cli.Core/Models.cs` (context registrations), `src/Capacitor.Cli/Commands/Harness/ClaudeHookCommand.cs`, `src/Capacitor.Cli/Commands/PermissionRequestCommand.cs`, `kcap/hooks/hooks.json`, `kcap/.claude-plugin/plugin.json`, `src/Capacitor.Cli.Daemon/Services/IHostedAgentRuntimeFactory.cs`, `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs`, `src/Capacitor.Cli.Daemon/Services/LocalPermissionBridge.cs`, `src/Capacitor.Cli.Daemon/Services/AcpHostedAgentRuntimeFactory.cs`, `src/Capacitor.Cli.Daemon/Acp/AcpInteractionBridge.cs`, `src/Capacitor.Cli.Daemon/DaemonRunner.cs`, `README.md`, `docs/CHANGES.md`.

All work happens on the existing branch `auto-approve-policy-design` (draft PR #741 becomes the implementation PR).

---

### Task 1: Canonical action model and component sets

**Files:**
- Create: `src/Capacitor.Cli.Core/Policy/CanonicalAction.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Policy/PolicyComponentsTests.cs`

**Interfaces:**
- Consumes: nothing (leaf task).
- Produces: `enum ActionKind { Shell, FileEdit, FileRead, Network, McpTool, Other }`; `sealed record ShellSegment(IReadOnlyList<string> Argv)`; `sealed record CanonicalAction` (init-only properties listed below); `abstract record ActionComponent` with subtypes `ShellSegmentComponent(ShellSegment Segment)`, `RawShellComponent(string Command)`, `PathComponent(string Path)`, `HostComponent(string Host, int? Port)`, `McpToolComponent(string Server, string Tool)`, `OtherToolComponent(string ToolName)`, `SentinelComponent()`; `static class PolicyComponents` with `RestrictionOf(CanonicalAction)` and `CoverageOf(CanonicalAction)` returning `IReadOnlyList<ActionComponent>`.

- [ ] **Step 1: Write the failing tests**

```csharp
namespace Capacitor.Cli.Core.Tests.Unit.Policy;

using Capacitor.Cli.Core.Policy;

public class PolicyComponentsTests {
    static CanonicalAction Shell(bool analyzed, params string[][] segments) => new() {
        Kind = ActionKind.Shell, Vendor = "claude", Command = "raw text",
        Analyzed = analyzed, Segments = [.. segments.Select(s => new ShellSegment(s))],
    };

    [Test]
    public async Task Analyzed_shell_restriction_and_coverage_are_the_segments() {
        var a = Shell(analyzed: true, ["git", "status"], ["rm", "-rf", "x"]);
        await Assert.That(PolicyComponents.RestrictionOf(a)).IsEquivalentTo(
            new ActionComponent[] {
                new ShellSegmentComponent(new ShellSegment(["git", "status"])),
                new ShellSegmentComponent(new ShellSegment(["rm", "-rf", "x"])),
            });
        await Assert.That(PolicyComponents.CoverageOf(a).Count).IsEqualTo(2);
    }

    [Test]
    public async Task Unanalyzed_shell_has_raw_restriction_and_empty_coverage() {
        var a = Shell(analyzed: false);
        await Assert.That(PolicyComponents.RestrictionOf(a))
            .IsEquivalentTo(new ActionComponent[] { new RawShellComponent("raw text") });
        await Assert.That(PolicyComponents.CoverageOf(a)).IsEmpty();
    }

    [Test]
    public async Task Other_without_tool_name_gets_sentinel_restriction_and_empty_coverage() {
        var a = new CanonicalAction { Kind = ActionKind.Other, Vendor = "claude" };
        await Assert.That(PolicyComponents.RestrictionOf(a))
            .IsEquivalentTo(new ActionComponent[] { new SentinelComponent() });
        await Assert.That(PolicyComponents.CoverageOf(a)).IsEmpty();
    }

    [Test]
    public async Task Other_with_tool_name_is_coverable() {
        var a = new CanonicalAction { Kind = ActionKind.Other, Vendor = "claude", RawToolName = "TodoWrite" };
        await Assert.That(PolicyComponents.CoverageOf(a))
            .IsEquivalentTo(new ActionComponent[] { new OtherToolComponent("TodoWrite") });
    }

    [Test]
    public async Task No_action_kind_yields_an_empty_restriction_set() {
        CanonicalAction[] all = [
            Shell(analyzed: false),
            new() { Kind = ActionKind.FileEdit, Vendor = "v", Paths = ["/a"] },
            new() { Kind = ActionKind.FileRead, Vendor = "v", Paths = ["/a", "/b"] },
            new() { Kind = ActionKind.Network, Vendor = "v", Host = "example.com" },
            new() { Kind = ActionKind.McpTool, Vendor = "v", Server = "kcap-flows", Tool = "start_review_flow" },
            new() { Kind = ActionKind.Other, Vendor = "v" },
        ];
        foreach (var a in all)
            await Assert.That(PolicyComponents.RestrictionOf(a)).IsNotEmpty();
    }

    [Test]
    public async Task Multi_path_file_action_yields_one_component_per_path() {
        var a = new CanonicalAction { Kind = ActionKind.FileRead, Vendor = "v", Paths = ["/a", "/b"] };
        await Assert.That(PolicyComponents.CoverageOf(a)).IsEquivalentTo(
            new ActionComponent[] { new PathComponent("/a"), new PathComponent("/b") });
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter '/*/*/PolicyComponentsTests/*'`
Expected: build failure — `Capacitor.Cli.Core.Policy` types do not exist.

- [ ] **Step 3: Implement the model**

```csharp
namespace Capacitor.Cli.Core.Policy;

public enum ActionKind { Shell, FileEdit, FileRead, Network, McpTool, Other }

public sealed record ShellSegment(IReadOnlyList<string> Argv) {
    public bool Equals(ShellSegment? other) => other is not null && Argv.SequenceEqual(other.Argv);
    public override int GetHashCode() => Argv.Aggregate(0, HashCode.Combine);
}

/// <summary>
/// A vendor-neutral view of one tool call. Normalizers guarantee the per-kind invariants
/// (non-empty Paths for file kinds, Host for network, Server+Tool for mcp_tool); a payload
/// that cannot satisfy them is emitted as kind Other instead, so no evaluation is skipped.
/// </summary>
public sealed record CanonicalAction {
    public required ActionKind Kind { get; init; }
    public required string Vendor { get; init; }
    public string? Cwd { get; init; }
    public string? Command { get; init; }
    public bool Analyzed { get; init; }
    public IReadOnlyList<ShellSegment> Segments { get; init; } = [];
    public IReadOnlyList<string> Paths { get; init; } = [];
    public string? Host { get; init; }
    public int? Port { get; init; }
    public string? Url { get; init; }
    public string? Server { get; init; }
    public string? Tool { get; init; }
    public string? RawToolName { get; init; }
    public string? RawPayloadJson { get; init; }
    public string? Justification { get; init; }
}

public abstract record ActionComponent;
public sealed record ShellSegmentComponent(ShellSegment Segment) : ActionComponent;
public sealed record RawShellComponent(string Command) : ActionComponent;
public sealed record PathComponent(string Path) : ActionComponent;
public sealed record HostComponent(string Host, int? Port) : ActionComponent;
public sealed record McpToolComponent(string Server, string Tool) : ActionComponent;
public sealed record OtherToolComponent(string ToolName) : ActionComponent;
public sealed record SentinelComponent : ActionComponent;

public static class PolicyComponents {
    /// <summary>What deny/ask rules match (any hit decides). Never empty.</summary>
    public static IReadOnlyList<ActionComponent> RestrictionOf(CanonicalAction a) => a.Kind switch {
        ActionKind.Shell when a.Analyzed && a.Segments.Count > 0 =>
            [.. a.Segments.Select(s => (ActionComponent)new ShellSegmentComponent(s))],
        ActionKind.Shell => [new RawShellComponent(a.Command ?? "")],
        ActionKind.FileEdit or ActionKind.FileRead when a.Paths.Count > 0 =>
            [.. a.Paths.Select(p => (ActionComponent)new PathComponent(p))],
        ActionKind.Network when a.Host is { Length: > 0 } => [new HostComponent(a.Host, a.Port)],
        ActionKind.McpTool when a.Server is { Length: > 0 } && a.Tool is { Length: > 0 } =>
            [new McpToolComponent(a.Server, a.Tool)],
        ActionKind.Other when a.RawToolName is { Length: > 0 } => [new OtherToolComponent(a.RawToolName)],
        _ => [new SentinelComponent()],
    };

    /// <summary>What allow rules must fully cover. Empty = never allow-eligible.</summary>
    public static IReadOnlyList<ActionComponent> CoverageOf(CanonicalAction a) => a switch {
        { Kind: ActionKind.Shell, Analyzed: false } => [],
        { Kind: ActionKind.Shell } when a.Segments.Count == 0 => [],
        { Kind: ActionKind.Other } when a.RawToolName is not { Length: > 0 } => [],
        _ when RestrictionOf(a) is [SentinelComponent] => [],
        _ => RestrictionOf(a),
    };
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter '/*/*/PolicyComponentsTests/*'`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/Policy/CanonicalAction.cs test/Capacitor.Cli.Core.Tests.Unit/Policy/PolicyComponentsTests.cs
git commit -m "Add the canonical action model with restriction and coverage sets (#738)"
```

---

### Task 2: Glob and shell token patterns

**Files:**
- Create: `src/Capacitor.Cli.Core/Policy/GlobPattern.cs`
- Create: `src/Capacitor.Cli.Core/Policy/ShellTokenPattern.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Policy/GlobPatternTests.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Policy/ShellTokenPatternTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `static class GlobPattern { static bool IsMatch(string pattern, string text); }` (case-sensitive, `*`/`?` only); `sealed record ShellTokenPattern` with `static ShellTokenPattern? Parse(string pattern)` (null for a pattern with no tokens — invalid), `bool MatchesAllow(IReadOnlyList<string> argv)` (anchored at 0, equal counts unless a trailing bare `*` rest token), `bool MatchesRestrictive(IReadOnlyList<string> argv, bool exact)` (contiguous run at any position; `exact` = allow semantics), and `bool HasRestToken { get; }`.

- [ ] **Step 1: Write the failing tests**

```csharp
namespace Capacitor.Cli.Core.Tests.Unit.Policy;

using Capacitor.Cli.Core.Policy;

public class GlobPatternTests {
    [Test]
    [Arguments("git", "git", true)]
    [Arguments("git", "Git", false)]           // case-sensitive
    [Arguments("--force*", "--force", true)]
    [Arguments("--force*", "--force-with-lease", true)]
    [Arguments("--force*", "--f", false)]
    [Arguments("*.md", "README.md", true)]
    [Arguments("a?c", "abc", true)]
    [Arguments("a?c", "ac", false)]
    [Arguments("*", "", true)]
    [Arguments("**", "anything", true)]
    [Arguments("a*b*c", "aXbYc", true)]
    [Arguments("a*b*c", "acb", false)]
    public async Task Matches(string pattern, string text, bool expected) =>
        await Assert.That(GlobPattern.IsMatch(pattern, text)).IsEqualTo(expected);
}

public class ShellTokenPatternTests {
    static IReadOnlyList<string> Argv(string joined) => joined.Split(' ');

    [Test]
    [Arguments("git status", "git status", true)]
    [Arguments("git status", "git status --porcelain", false)]   // equal counts without a rest token
    [Arguments("git status *", "git status", true)]              // rest token matches zero tokens
    [Arguments("git status *", "git status --porcelain -z", true)]
    [Arguments("git *", "git push", true)]
    [Arguments("git status", "env git status", false)]           // allow is anchored at token 0
    [Arguments("git diff*", "git diff --output=x", false)]       // glob is within one token, not across argv
    public async Task Allow_matching(string pattern, string argv, bool expected) =>
        await Assert.That(ShellTokenPattern.Parse(pattern)!.MatchesAllow(Argv(argv))).IsEqualTo(expected);

    [Test]
    [Arguments("git push --force*", "git push --force origin main", true)]
    [Arguments("git push --force*", "git push --force-with-lease", true)]
    [Arguments("git push --force*", "env FOO=1 git push --force", true)]  // any position
    [Arguments("git push --force*", "git push origin --force", false)]    // run must be contiguous
    [Arguments("rm -rf", "echo rm -rf", true)]                            // over-trigger is accepted for tighten outcomes
    public async Task Restrictive_matching(string pattern, string argv, bool expected) =>
        await Assert.That(ShellTokenPattern.Parse(pattern)!.MatchesRestrictive(Argv(argv), exact: false))
            .IsEqualTo(expected);

    [Test]
    public async Task Exact_restrictive_anchors_at_token_zero_with_equal_counts() {
        var p = ShellTokenPattern.Parse("gh pr merge")!;
        await Assert.That(p.MatchesRestrictive(Argv("gh pr merge"), exact: true)).IsTrue();
        await Assert.That(p.MatchesRestrictive(Argv("gh pr merge --squash"), exact: true)).IsFalse();
        await Assert.That(p.MatchesRestrictive(Argv("echo gh pr merge"), exact: true)).IsFalse();
    }

    [Test]
    public async Task Bare_star_pattern_is_a_universal_rest_token() {
        var p = ShellTokenPattern.Parse("*")!;
        await Assert.That(p.HasRestToken).IsTrue();
        await Assert.That(p.MatchesAllow(Argv("anything at all"))).IsTrue();
        await Assert.That(p.MatchesRestrictive(Argv("anything"), exact: false)).IsTrue();
    }

    [Test]
    public async Task Empty_pattern_is_invalid() =>
        await Assert.That(ShellTokenPattern.Parse("   ")).IsNull();
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter '/*/*/ShellTokenPatternTests/*'`
Expected: build failure — types missing.

- [ ] **Step 3: Implement**

```csharp
namespace Capacitor.Cli.Core.Policy;

public static class GlobPattern {
    public static bool IsMatch(string pattern, string text) {
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length) {
            if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == text[t])) { p++; t++; }
            else if (p < pattern.Length && pattern[p] == '*') { star = p++; mark = t; }
            else if (star >= 0) { p = star + 1; t = ++mark; }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }
}
```

```csharp
namespace Capacitor.Cli.Core.Policy;

/// <summary>
/// A shell pattern split on whitespace into per-token globs. A final bare "*" is a rest
/// token: it matches zero or more remaining argv tokens and is the only way an allow
/// pattern accepts extra argv.
/// </summary>
public sealed record ShellTokenPattern(IReadOnlyList<string> Tokens, bool HasRestToken) {
    public static ShellTokenPattern? Parse(string pattern) {
        var tokens = pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0) return null;
        var rest = tokens[^1] == "*";
        return new(rest ? tokens[..^1] : tokens, rest);
    }

    public bool MatchesAllow(IReadOnlyList<string> argv) {
        if (HasRestToken ? argv.Count < Tokens.Count : argv.Count != Tokens.Count) return false;
        for (var i = 0; i < Tokens.Count; i++)
            if (!GlobPattern.IsMatch(Tokens[i], argv[i])) return false;
        return true;
    }

    public bool MatchesRestrictive(IReadOnlyList<string> argv, bool exact) {
        if (exact) return MatchesAllow(argv);
        if (Tokens.Count == 0) return true;
        for (var start = 0; start + Tokens.Count <= argv.Count; start++) {
            var all = true;
            for (var i = 0; i < Tokens.Count; i++)
                if (!GlobPattern.IsMatch(Tokens[i], argv[start + i])) { all = false; break; }
            if (all) return true;
        }
        return false;
    }
}
```

- [ ] **Step 4: Run both test classes to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter '/*/*/GlobPatternTests/*'` and the same with `ShellTokenPatternTests`.
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/Policy/GlobPattern.cs src/Capacitor.Cli.Core/Policy/ShellTokenPattern.cs test/Capacitor.Cli.Core.Tests.Unit/Policy/GlobPatternTests.cs test/Capacitor.Cli.Core.Tests.Unit/Policy/ShellTokenPatternTests.cs
git commit -m "Add outcome-sensitive glob and shell token pattern matching (#738)"
```

---

### Task 3: Shell command analyzer (allowlist grammar)

**Files:**
- Create: `src/Capacitor.Cli.Core/Policy/ShellCommandAnalyzer.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Policy/ShellCommandAnalyzerTests.cs`

**Interfaces:**
- Consumes: `ShellSegment` (Task 1).
- Produces: `sealed record ShellAnalysis(bool Analyzed, IReadOnlyList<ShellSegment> Segments)` with `static readonly ShellAnalysis Unanalyzed`; `static class ShellCommandAnalyzer { static ShellAnalysis Analyze(string command); }`.

The grammar is an exhaustive **allowlist** (spec acceptance criterion 2): a command is analyzed only when it is simple commands of literal-word tokens joined by top-level `&&`, `;`, or `|`. Everything outside that — every construct below — yields `Unanalyzed`. The conservative direction is always safe: unanalyzed only removes allow-eligibility; deny/ask still match via Task 4's fragments.

- [ ] **Step 1: Write the failing tests** — pin the grammar construct-by-construct

```csharp
namespace Capacitor.Cli.Core.Tests.Unit.Policy;

using Capacitor.Cli.Core.Policy;

public class ShellCommandAnalyzerTests {
    [Test]
    public async Task Simple_command_is_analyzed_into_one_segment() {
        var r = ShellCommandAnalyzer.Analyze("git status --porcelain");
        await Assert.That(r.Analyzed).IsTrue();
        await Assert.That(r.Segments).IsEquivalentTo(
            new[] { new ShellSegment(["git", "status", "--porcelain"]) });
    }

    [Test]
    public async Task Top_level_operators_split_segments() {
        var r = ShellCommandAnalyzer.Analyze("git add -A && git commit -m done; git log | head");
        await Assert.That(r.Analyzed).IsTrue();
        await Assert.That(r.Segments.Count).IsEqualTo(4);
        await Assert.That(r.Segments[1].Argv).IsEquivalentTo(new[] { "git", "commit", "-m", "done" });
    }

    [Test]
    public async Task Quoted_literals_are_resolved_into_single_tokens() {
        var r = ShellCommandAnalyzer.Analyze("git commit -m 'two words' --author \"A B\"");
        await Assert.That(r.Analyzed).IsTrue();
        await Assert.That(r.Segments[0].Argv).IsEquivalentTo(
            new[] { "git", "commit", "-m", "two words", "--author", "A B" });
    }

    // The exhaustive unanalyzed table: each row is one banned construct.
    [Test]
    [Arguments("git status > out.txt")]          // redirection
    [Arguments("cat < in.txt")]                  // redirection
    [Arguments("cat <<EOF")]                     // here-doc
    [Arguments("echo $HOME")]                    // parameter expansion
    [Arguments("echo \"$HOME\"")]                // expansion inside double quotes
    [Arguments("echo `date`")]                   // command substitution
    [Arguments("diff <(sort a) <(sort b)")]      // process substitution
    [Arguments("ls *.md")]                       // glob
    [Arguments("ls ?.md")]                       // glob
    [Arguments("ls [ab].md")]                    // glob class
    [Arguments("ls ~/notes")]                    // tilde expansion at word start
    [Arguments("sleep 5 &")]                     // backgrounding
    [Arguments("a || b")]                        // || is not on the operator allowlist
    [Arguments("(cd /tmp)")]                     // subshell
    [Arguments("{ ls; }")]                       // group
    [Arguments("echo a{b,c}")]                   // brace expansion
    [Arguments("eval git status")]               // eval
    [Arguments("exec git status")]               // exec
    [Arguments("bash -c 'rm -rf x'")]            // nested shell
    [Arguments("sh script.sh")]                  // nested shell
    [Arguments("FOO=1 git push")]                // leading assignment hides the real program
    [Arguments("echo a\\ b")]                    // backslash escape
    [Arguments("git log # comment")]             // comment
    [Arguments("git status\ngit log")]           // newline separator
    [Arguments("echo 'unterminated")]            // unterminated quote
    [Arguments("git add . &&")]                  // trailing operator = empty segment
    [Arguments("&& git add .")]                  // leading operator = empty segment
    [Arguments("! git diff --quiet")]            // pipeline negation
    [Arguments("")]                              // empty command
    public async Task Banned_constructs_are_unanalyzed(string command) {
        var r = ShellCommandAnalyzer.Analyze(command);
        await Assert.That(r.Analyzed).IsFalse();
        await Assert.That(r.Segments).IsEmpty();
    }

    [Test]
    [Arguments("git log HEAD~3")]                // ~ mid-token is literal
    [Arguments("grep -n issue#5 notes.txt")]     // # mid-token is literal
    [Arguments("git log --format=%H")]           // = and % in arguments are literal
    [Arguments("env FOO=1 git push --force")]    // assignment as env's argument, not leading
    [Arguments("grep 'a*b' file.txt")]           // glob chars inside quotes are literal
    public async Task Literal_lookalikes_stay_analyzed(string command) =>
        await Assert.That(ShellCommandAnalyzer.Analyze(command).Analyzed).IsTrue();
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter '/*/*/ShellCommandAnalyzerTests/*'`
Expected: build failure.

- [ ] **Step 3: Implement the analyzer**

```csharp
namespace Capacitor.Cli.Core.Policy;

using System.Buffers;
using System.Text;

public sealed record ShellAnalysis(bool Analyzed, IReadOnlyList<ShellSegment> Segments) {
    public static readonly ShellAnalysis Unanalyzed = new(false, []);
}

/// <summary>
/// Allowlist grammar: literal-token simple commands joined by top-level '&&', ';' or '|'.
/// Anything else is unanalyzed and therefore never allow-eligible — the one guarantee
/// obfuscation cannot defeat. When in doubt, return Unanalyzed.
/// </summary>
public static class ShellCommandAnalyzer {
    static readonly SearchValues<char> UnquotedForbidden = SearchValues.Create("$`<>(){}[]*?\\");
    static readonly HashSet<string> ForbiddenPrograms = new(StringComparer.Ordinal)
        { "eval", "exec", "sh", "bash", "zsh", "dash", "ksh", "csh", "tcsh", "fish" };

    public static ShellAnalysis Analyze(string command) {
        var segments = new List<ShellSegment>();
        var argv = new List<string>();
        var token = new StringBuilder();
        var inToken = false;
        var tokenStartsQuoted = false;

        bool FlushToken() {
            if (!inToken) return true;
            var t = token.ToString();
            token.Clear();
            inToken = false;
            if (t == "!" && argv.Count == 0) return false;
            if (!tokenStartsQuoted && t.StartsWith('~')) return false;
            tokenStartsQuoted = false;
            argv.Add(t);
            return true;
        }

        bool EndSegment() {
            if (!FlushToken() || argv.Count == 0) return false;
            if (LooksLikeAssignment(argv[0]) || ForbiddenPrograms.Contains(argv[0])) return false;
            segments.Add(new ShellSegment([.. argv]));
            argv.Clear();
            return true;
        }

        for (var i = 0; i < command.Length; i++) {
            var c = command[i];
            switch (c) {
                case '\'': {
                    var close = command.IndexOf('\'', i + 1);
                    if (close < 0) return ShellAnalysis.Unanalyzed;
                    if (!inToken) tokenStartsQuoted = true;
                    token.Append(command, i + 1, close - i - 1);
                    inToken = true;
                    i = close;
                    break;
                }
                case '"': {
                    var close = command.IndexOf('"', i + 1);
                    if (close < 0) return ShellAnalysis.Unanalyzed;
                    var inner = command.AsSpan(i + 1, close - i - 1);
                    if (inner.ContainsAny('$', '`', '\\') || inner.Contains('\n')) return ShellAnalysis.Unanalyzed;
                    if (!inToken) tokenStartsQuoted = true;
                    token.Append(inner);
                    inToken = true;
                    i = close;
                    break;
                }
                case ' ' or '\t':
                    if (!FlushToken()) return ShellAnalysis.Unanalyzed;
                    break;
                case '\n' or '\r':
                    return ShellAnalysis.Unanalyzed;
                case '&':
                    if (i + 1 >= command.Length || command[i + 1] != '&') return ShellAnalysis.Unanalyzed;
                    if (!EndSegment()) return ShellAnalysis.Unanalyzed;
                    i++;
                    break;
                case '|':
                    if (i + 1 < command.Length && command[i + 1] == '|') return ShellAnalysis.Unanalyzed;
                    if (!EndSegment()) return ShellAnalysis.Unanalyzed;
                    break;
                case ';':
                    if (!EndSegment()) return ShellAnalysis.Unanalyzed;
                    break;
                case '#' when !inToken:
                    return ShellAnalysis.Unanalyzed;
                case '~' when !inToken:
                    return ShellAnalysis.Unanalyzed;
                default:
                    if (UnquotedForbidden.Contains(c)) return ShellAnalysis.Unanalyzed;
                    token.Append(c);
                    inToken = true;
                    break;
            }
        }
        if (!EndSegment()) return ShellAnalysis.Unanalyzed;
        return new ShellAnalysis(true, segments);
    }

    static bool LooksLikeAssignment(string word) {
        var eq = word.IndexOf('=');
        if (eq <= 0) return false;
        if (!(char.IsAsciiLetter(word[0]) || word[0] == '_')) return false;
        for (var i = 1; i < eq; i++)
            if (!(char.IsAsciiLetterOrDigit(word[i]) || word[i] == '_')) return false;
        return true;
    }
}
```

Note one subtlety the tests pin: `EndSegment` runs `FlushToken` first, so a lone `!` or leading-`~` token invalidates the whole command, and an operator with no preceding argv (leading/doubled/trailing operators) fails via `argv.Count == 0`.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter '/*/*/ShellCommandAnalyzerTests/*'`
Expected: PASS (all rows).

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/Policy/ShellCommandAnalyzer.cs test/Capacitor.Cli.Core.Tests.Unit/Policy/ShellCommandAnalyzerTests.cs
git commit -m "Analyze shell commands with an exhaustive allowlist grammar (#738)"
```

---

### Task 4: Fragment lexer for unanalyzed commands

**Files:**
- Create: `src/Capacitor.Cli.Core/Policy/ShellFragmentLexer.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Policy/ShellFragmentLexerTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `static class ShellFragmentLexer { static IReadOnlyList<string> Lex(string command); }` — whitespace-run splitting with simple quote resolution; an ambiguous construct (unterminated quote) abandons lexing and returns an empty list, in which case only the raw-substring-glob signal applies (Task 7 wires both).

- [ ] **Step 1: Write the failing tests**

```csharp
namespace Capacitor.Cli.Core.Tests.Unit.Policy;

using Capacitor.Cli.Core.Policy;

public class ShellFragmentLexerTests {
    [Test]
    public async Task Whitespace_runs_collapse_and_quotes_resolve() {
        var frags = ShellFragmentLexer.Lex("git   push  '--force'   \"origin\"  main");
        await Assert.That(frags).IsEquivalentTo(new[] { "git", "push", "--force", "origin", "main" });
    }

    [Test]
    public async Task Redirection_stays_in_the_stream_as_fragments() {
        var frags = ShellFragmentLexer.Lex("git status > pwn.yml && rm -rf /");
        await Assert.That(frags).IsEquivalentTo(
            new[] { "git", "status", ">", "pwn.yml", "&&", "rm", "-rf", "/" });
    }

    [Test]
    public async Task Escaped_double_quote_is_resolved() {
        var frags = ShellFragmentLexer.Lex("echo \"a \\\" b\"");
        await Assert.That(frags).IsEquivalentTo(new[] { "echo", "a \" b" });
    }

    [Test]
    public async Task Unterminated_quote_abandons_lexing() =>
        await Assert.That(ShellFragmentLexer.Lex("echo 'oops")).IsEmpty();

    [Test]
    public async Task Newlines_split_like_whitespace() {
        var frags = ShellFragmentLexer.Lex("git add .\ngit commit");
        await Assert.That(frags).IsEquivalentTo(new[] { "git", "add", ".", "git", "commit" });
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter '/*/*/ShellFragmentLexerTests/*'`
Expected: build failure.

- [ ] **Step 3: Implement**

```csharp
namespace Capacitor.Cli.Core.Policy;

using System.Text;

/// <summary>
/// Best-effort lexing of a raw (unanalyzed) command into literal fragments so deny/ask
/// token runs match across spacing and quoting differences. This is a matching aid, not
/// a parser: obfuscation can evade it, but evasion only forfeits the tighten outcomes —
/// it can never earn an allow.
/// </summary>
public static class ShellFragmentLexer {
    public static IReadOnlyList<string> Lex(string command) {
        var frags = new List<string>();
        var cur = new StringBuilder();
        var inFrag = false;
        for (var i = 0; i < command.Length; i++) {
            var c = command[i];
            if (c == '\'') {
                var close = command.IndexOf('\'', i + 1);
                if (close < 0) return [];
                cur.Append(command, i + 1, close - i - 1);
                inFrag = true;
                i = close;
            }
            else if (c == '"') {
                var j = i + 1;
                while (j < command.Length && command[j] != '"') {
                    if (command[j] == '\\' && j + 1 < command.Length && command[j + 1] is '"' or '\\') {
                        cur.Append(command[j + 1]);
                        j += 2;
                    }
                    else cur.Append(command[j++]);
                }
                if (j >= command.Length) return [];
                inFrag = true;
                i = j;
            }
            else if (char.IsWhiteSpace(c)) {
                if (inFrag) { frags.Add(cur.ToString()); cur.Clear(); inFrag = false; }
            }
            else { cur.Append(c); inFrag = true; }
        }
        if (inFrag) frags.Add(cur.ToString());
        return frags;
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter '/*/*/ShellFragmentLexerTests/*'`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/Policy/ShellFragmentLexer.cs test/Capacitor.Cli.Core.Tests.Unit/Policy/ShellFragmentLexerTests.cs
git commit -m "Lex unanalyzed commands into fragments for deny and ask matching (#738)"
```

---

### Task 5: Approvals YAML subset parser

**Files:**
- Create: `src/Capacitor.Cli.Core/Policy/ApprovalsYaml.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Policy/ApprovalsYamlTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `abstract record YamlNode`; `sealed record YamlScalar(string Value, bool Quoted) : YamlNode`; `sealed record YamlSequence(IReadOnlyList<YamlNode> Items) : YamlNode`; `sealed record YamlMapping(IReadOnlyList<KeyValuePair<string, YamlNode>> Entries) : YamlNode` with an indexer `YamlNode? this[string key]`; `static class ApprovalsYaml { static YamlMapping Parse(string text); }`; `sealed class ApprovalsYamlException(int line, string message) : Exception`.

**Why hand-rolled:** the repo has no YAML dependency and deliberately hand-rolls the two places it reads YAML today; a general YAML library under NativeAOT would need its own type-info plumbing (the Tomlyn precedent) for a file format of which we accept only a strict subset anyway. Rejecting everything outside the subset is a feature: a construct we cannot parse is a malformed document, which degrades **loudly** (spec Failure taxonomy) instead of being half-understood.

**The accepted subset** (everything else throws `ApprovalsYamlException` with a 1-based line number):
- Block mappings with space indentation (tabs in indentation throw); plain keys matching `[A-Za-z0-9_-]+` followed by `:`.
- Block sequences (`- ` items), including a mapping that starts on the dash line (`- match: …` then siblings at dash+2 indent).
- Flow sequences `[a, "b c"]` and flow mappings `{ kind: shell, command: "x" }`, nested arbitrarily within one line.
- Scalars: plain (must not start with `& * ! ? > | % @ ` `` ` `` or a `-`/`:` ambiguity), single-quoted (with `''` escape), double-quoted (with `\"`, `\\`, `\n`, `\t` escapes only).
- Literal block scalars `|` and `|-` (for the judge prompt).
- Comments (`#` at line start or preceded by whitespace, outside quotes) and blank lines.
- Rejected explicitly: `---`/`...` document markers, anchors/aliases, tags, folded `>` scalars, complex `? ` keys, duplicate keys in one mapping.

- [ ] **Step 1: Write the failing tests**

```csharp
namespace Capacitor.Cli.Core.Tests.Unit.Policy;

using Capacitor.Cli.Core.Policy;

public class ApprovalsYamlTests {
    // The spec's own example document must parse: it is the reference input for the binder.
    const string SpecExample = """
        version: 1
        rules:
          - match: { kind: shell, command: "git push --force*" }
            outcome: deny
            reason: force-push goes through the PR lane
          - match: { kind: shell, command: ["git status *", "git diff *", "dotnet build *"] }
            outcome: allow           # the trailing * is the visible opt-in to arbitrary extra argv
          - match: { kind: mcp_tool, server: "kcap-*" }
            outcome: allow
          - match: { kind: shell, command: "gh pr merge" }
            outcome: ask
        judge:
          mode: unmatched
          prompt: |
            Approve routine read-only git and build commands anywhere in the repo.
            Escalate anything touching CI config or release tags to ask.
        """;

    [Test]
    public async Task Spec_example_parses() {
        var root = ApprovalsYaml.Parse(SpecExample);
        await Assert.That(((YamlScalar)root["version"]!).Value).IsEqualTo("1");
        var rules = (YamlSequence)root["rules"]!;
        await Assert.That(rules.Items.Count).IsEqualTo(4);
        var first = (YamlMapping)rules.Items[0];
        var match = (YamlMapping)first["match"]!;
        await Assert.That(((YamlScalar)match["kind"]!).Value).IsEqualTo("shell");
        await Assert.That(((YamlScalar)match["command"]!).Value).IsEqualTo("git push --force*");
        await Assert.That(((YamlScalar)first["outcome"]!).Value).IsEqualTo("deny");
        var second = (YamlMapping)rules.Items[1];
        var patterns = (YamlSequence)((YamlMapping)second["match"]!)["command"]!;
        await Assert.That(patterns.Items.Count).IsEqualTo(3);
        await Assert.That(((YamlScalar)patterns.Items[1]).Value).IsEqualTo("git diff *");
        var judge = (YamlMapping)root["judge"]!;
        await Assert.That(((YamlScalar)judge["prompt"]!).Value)
            .Contains("Escalate anything touching CI config");
    }

    [Test]
    public async Task Literal_block_preserves_lines_and_dedents() {
        var root = ApprovalsYaml.Parse("judge:\n  prompt: |\n    line one\n    line two\n");
        var prompt = ((YamlScalar)((YamlMapping)root["judge"]!)["prompt"]!).Value;
        await Assert.That(prompt).IsEqualTo("line one\nline two\n");
    }

    [Test]
    public async Task Quoted_scalars_resolve_escapes() {
        var root = ApprovalsYaml.Parse("a: 'it''s'\nb: \"x\\\"y\"\n");
        await Assert.That(((YamlScalar)root["a"]!).Value).IsEqualTo("it's");
        await Assert.That(((YamlScalar)root["b"]!).Value).IsEqualTo("x\"y");
    }

    [Test]
    [Arguments("version: 1\nrules: *anchor\n", "alias")]
    [Arguments("---\nversion: 1\n", "document")]
    [Arguments("a: !!str x\n", "tag")]
    [Arguments("a: >\n  folded\n", "folded")]
    [Arguments("a: 1\na: 2\n", "duplicate")]
    [Arguments("\ta: 1\n", "tab")]
    [Arguments("a: 'unterminated\n", "quote")]
    [Arguments("a:\n", "value")]
    public async Task Unsupported_constructs_throw_with_a_line_number(string text, string _) {
        var ex = Assert.Throws<ApprovalsYamlException>(() => ApprovalsYaml.Parse(text));
        await Assert.That(ex.Line).IsGreaterThan(0);
    }

    [Test]
    public async Task Comments_and_blank_lines_are_ignored() {
        var root = ApprovalsYaml.Parse("# header\n\nversion: 1   # trailing\n");
        await Assert.That(((YamlScalar)root["version"]!).Value).IsEqualTo("1");
    }

    [Test]
    public async Task Hash_inside_quotes_is_not_a_comment() {
        var root = ApprovalsYaml.Parse("a: \"x # y\"\n");
        await Assert.That(((YamlScalar)root["a"]!).Value).IsEqualTo("x # y");
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter '/*/*/ApprovalsYamlTests/*'`
Expected: build failure.

- [ ] **Step 3: Implement the parser**

The complete implementation. Structure: preprocess to `(int LineNo, int Indent, string Content)` records (comments stripped quote-aware, blanks dropped, tabs-in-indent rejected), then recursive descent over the line list by indent. Literal blocks are collected from the *raw* lines (comments must not be stripped inside them), so preprocessing keeps an index into the raw array.

```csharp
namespace Capacitor.Cli.Core.Policy;

using System.Text;

public abstract record YamlNode;
public sealed record YamlScalar(string Value, bool Quoted) : YamlNode;
public sealed record YamlSequence(IReadOnlyList<YamlNode> Items) : YamlNode;
public sealed record YamlMapping(IReadOnlyList<KeyValuePair<string, YamlNode>> Entries) : YamlNode {
    public YamlNode? this[string key] {
        get {
            foreach (var e in Entries)
                if (e.Key == key) return e.Value;
            return null;
        }
    }
}

public sealed class ApprovalsYamlException(int line, string message)
    : Exception($"line {line}: {message}") {
    public int Line { get; } = line;
}

/// <summary>
/// Strict parser for the approvals-policy YAML subset. Anything outside the subset throws:
/// a construct we cannot fully understand must invalidate the document loudly rather than
/// be half-applied.
/// </summary>
public static class ApprovalsYaml {
    readonly record struct Line(int LineNo, int Indent, string Content, int RawIndex);

    public static YamlMapping Parse(string text) {
        var raw = text.Split('\n');
        var lines = new List<Line>();
        for (var r = 0; r < raw.Length; r++) {
            var full = raw[r].TrimEnd('\r');
            var indent = 0;
            while (indent < full.Length && full[indent] == ' ') indent++;
            if (indent < full.Length && full[indent] == '\t')
                throw new ApprovalsYamlException(r + 1, "tab in indentation");
            var content = StripComment(full[indent..], r + 1);
            if (content.Length == 0) continue;
            if (content is "---" or "...")
                throw new ApprovalsYamlException(r + 1, "multi-document YAML is not supported");
            lines.Add(new Line(r + 1, indent, content, r));
        }
        if (lines.Count == 0) return new YamlMapping([]);
        var i = 0;
        var map = ParseMapping(raw, lines, ref i, lines[0].Indent);
        if (i != lines.Count)
            throw new ApprovalsYamlException(lines[i].LineNo, "content outside the root mapping");
        return map;
    }

    static YamlMapping ParseMapping(string[] raw, List<Line> lines, ref int i, int indent) {
        var entries = new List<KeyValuePair<string, YamlNode>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (i < lines.Count && lines[i].Indent == indent && !lines[i].Content.StartsWith("- ", StringComparison.Ordinal) && lines[i].Content != "-") {
            var line = lines[i];
            var (key, rest) = SplitKey(line);
            if (!seen.Add(key)) throw new ApprovalsYamlException(line.LineNo, $"duplicate key '{key}'");
            i++;
            entries.Add(new(key, ParseValue(raw, lines, ref i, line, rest, indent)));
        }
        if (entries.Count == 0)
            throw new ApprovalsYamlException(lines[Math.Min(i, lines.Count - 1)].LineNo, "expected a mapping");
        return new YamlMapping(entries);
    }

    static YamlNode ParseValue(string[] raw, List<Line> lines, ref int i, Line keyLine, string rest, int indent) {
        if (rest is "|" or "|-") return ParseLiteralBlock(raw, lines, ref i, keyLine, chompFinal: rest == "|-");
        if (rest.Length > 0) {
            var pos = 0;
            var node = ParseFlow(rest, ref pos, keyLine.LineNo);
            if (pos != rest.Length)
                throw new ApprovalsYamlException(keyLine.LineNo, "trailing content after value");
            return node;
        }
        if (i >= lines.Count || lines[i].Indent <= indent)
            throw new ApprovalsYamlException(keyLine.LineNo, "missing value");
        var childIndent = lines[i].Indent;
        return lines[i].Content.StartsWith("- ", StringComparison.Ordinal) || lines[i].Content == "-"
            ? ParseSequence(raw, lines, ref i, childIndent)
            : ParseMapping(raw, lines, ref i, childIndent);
    }

    static YamlSequence ParseSequence(string[] raw, List<Line> lines, ref int i, int indent) {
        var items = new List<YamlNode>();
        while (i < lines.Count && lines[i].Indent == indent
               && (lines[i].Content.StartsWith("- ", StringComparison.Ordinal) || lines[i].Content == "-")) {
            var line = lines[i];
            var body = line.Content == "-" ? "" : line.Content[2..].TrimStart();
            if (body.Length == 0) throw new ApprovalsYamlException(line.LineNo, "empty sequence item");
            // An item whose body is "key: …" is a block mapping starting on the dash line:
            // rewrite the dash line as its first key line at indent+2 and re-enter ParseMapping.
            if (TrySplitKey(body, out _, out _)) {
                lines[i] = line with { Indent = indent + 2, Content = body };
                items.Add(ParseMapping(raw, lines, ref i, indent + 2));
            }
            else {
                var pos = 0;
                var node = ParseFlow(body, ref pos, line.LineNo);
                if (pos != body.Length)
                    throw new ApprovalsYamlException(line.LineNo, "trailing content after sequence item");
                items.Add(node);
                i++;
            }
        }
        return new YamlSequence(items);
    }

    static YamlScalar ParseLiteralBlock(string[] raw, List<Line> lines, ref int i, Line keyLine, bool chompFinal) {
        var collected = new List<string>();
        var last = keyLine.RawIndex;
        while (i < lines.Count && lines[i].Indent > keyLine.Indent) { last = lines[i].RawIndex; i++; }
        for (var r = keyLine.RawIndex + 1; r <= last; r++) collected.Add(raw[r].TrimEnd('\r'));
        while (collected.Count > 0 && collected[^1].Trim().Length == 0) collected.RemoveAt(collected.Count - 1);
        if (collected.Count == 0) throw new ApprovalsYamlException(keyLine.LineNo, "empty literal block");
        var dedent = collected.Where(l => l.Trim().Length > 0).Min(l => l.TakeWhile(c => c == ' ').Count());
        var body = string.Join('\n', collected.Select(l => l.Length >= dedent ? l[dedent..] : ""));
        return new YamlScalar(chompFinal ? body : body + "\n", Quoted: false);
    }

    static YamlNode ParseFlow(string s, ref int pos, int lineNo) {
        SkipSpaces(s, ref pos);
        if (pos >= s.Length) throw new ApprovalsYamlException(lineNo, "missing value");
        switch (s[pos]) {
            case '[': {
                pos++;
                var items = new List<YamlNode>();
                SkipSpaces(s, ref pos);
                if (pos < s.Length && s[pos] == ']') { pos++; return new YamlSequence(items); }
                while (true) {
                    items.Add(ParseFlow(s, ref pos, lineNo));
                    SkipSpaces(s, ref pos);
                    if (pos < s.Length && s[pos] == ',') { pos++; continue; }
                    if (pos < s.Length && s[pos] == ']') { pos++; return new YamlSequence(items); }
                    throw new ApprovalsYamlException(lineNo, "unterminated flow sequence");
                }
            }
            case '{': {
                pos++;
                var entries = new List<KeyValuePair<string, YamlNode>>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                SkipSpaces(s, ref pos);
                if (pos < s.Length && s[pos] == '}') { pos++; return new YamlMapping(entries); }
                while (true) {
                    SkipSpaces(s, ref pos);
                    var keyStart = pos;
                    while (pos < s.Length && (char.IsAsciiLetterOrDigit(s[pos]) || s[pos] is '_' or '-')) pos++;
                    var key = s[keyStart..pos];
                    if (key.Length == 0 || pos >= s.Length || s[pos] != ':')
                        throw new ApprovalsYamlException(lineNo, "expected 'key:' in flow mapping");
                    if (!seen.Add(key)) throw new ApprovalsYamlException(lineNo, $"duplicate key '{key}'");
                    pos++;
                    entries.Add(new(key, ParseFlow(s, ref pos, lineNo)));
                    SkipSpaces(s, ref pos);
                    if (pos < s.Length && s[pos] == ',') { pos++; continue; }
                    if (pos < s.Length && s[pos] == '}') { pos++; return new YamlMapping(entries); }
                    throw new ApprovalsYamlException(lineNo, "unterminated flow mapping");
                }
            }
            case '\'': return ParseSingleQuoted(s, ref pos, lineNo);
            case '"': return ParseDoubleQuoted(s, ref pos, lineNo);
            default: return ParsePlain(s, ref pos, lineNo);
        }
    }

    static YamlScalar ParseSingleQuoted(string s, ref int pos, int lineNo) {
        var sb = new StringBuilder();
        pos++;
        while (pos < s.Length) {
            if (s[pos] == '\'') {
                if (pos + 1 < s.Length && s[pos + 1] == '\'') { sb.Append('\''); pos += 2; continue; }
                pos++;
                return new YamlScalar(sb.ToString(), Quoted: true);
            }
            sb.Append(s[pos++]);
        }
        throw new ApprovalsYamlException(lineNo, "unterminated single-quoted scalar");
    }

    static YamlScalar ParseDoubleQuoted(string s, ref int pos, int lineNo) {
        var sb = new StringBuilder();
        pos++;
        while (pos < s.Length) {
            var c = s[pos];
            if (c == '"') { pos++; return new YamlScalar(sb.ToString(), Quoted: true); }
            if (c == '\\') {
                if (pos + 1 >= s.Length) break;
                sb.Append(s[pos + 1] switch {
                    '"' => '"', '\\' => '\\', 'n' => '\n', 't' => '\t',
                    _ => throw new ApprovalsYamlException(lineNo, $"unsupported escape '\\{s[pos + 1]}'"),
                });
                pos += 2;
                continue;
            }
            sb.Append(c);
            pos++;
        }
        throw new ApprovalsYamlException(lineNo, "unterminated double-quoted scalar");
    }

    static YamlScalar ParsePlain(string s, ref int pos, int lineNo) {
        var start = pos;
        while (pos < s.Length && s[pos] is not (',' or ']' or '}')) pos++;
        var value = s[start..pos].Trim();
        if (value.Length == 0) throw new ApprovalsYamlException(lineNo, "missing value");
        if (value[0] is '&' or '*' or '!' or '?' or '>' or '|' or '%' or '@' or '`')
            throw new ApprovalsYamlException(lineNo, $"unsupported YAML construct at '{value[0]}'");
        return new YamlScalar(value, Quoted: false);
    }

    static void SkipSpaces(string s, ref int pos) { while (pos < s.Length && s[pos] == ' ') pos++; }

    static (string Key, string Rest) SplitKey(Line line) =>
        TrySplitKey(line.Content, out var key, out var rest)
            ? (key, rest)
            : throw new ApprovalsYamlException(line.LineNo, "expected 'key:'");

    static bool TrySplitKey(string content, out string key, out string rest) {
        key = "";
        rest = "";
        var pos = 0;
        while (pos < content.Length && (char.IsAsciiLetterOrDigit(content[pos]) || content[pos] is '_' or '-')) pos++;
        if (pos == 0 || pos >= content.Length || content[pos] != ':') return false;
        if (pos + 1 < content.Length && content[pos + 1] != ' ') return false;
        key = content[..pos];
        rest = pos + 1 < content.Length ? content[(pos + 1)..].Trim() : "";
        return true;
    }

    static string StripComment(string content, int lineNo) {
        var inSingle = false;
        var inDouble = false;
        for (var i = 0; i < content.Length; i++) {
            var c = content[i];
            if (c == '\'' && !inDouble) inSingle = !inSingle;
            else if (c == '"' && !inSingle && (i == 0 || content[i - 1] != '\\')) inDouble = !inDouble;
            else if (c == '#' && !inSingle && !inDouble && (i == 0 || content[i - 1] is ' ' or '\t'))
                return content[..i].TrimEnd();
        }
        if (inSingle || inDouble) throw new ApprovalsYamlException(lineNo, "unterminated quoted scalar");
        return content.TrimEnd();
    }
}
```

Two behaviors worth noting to the implementer: (1) `ParseSequence` rewrites the dash line in place (`lines[i] = line with {…}`) so `- match: {…}` becomes the first key line of a mapping at `indent + 2` — the spec example depends on exactly this shape; (2) `|` values must be checked in `ParseValue` *before* flow parsing, since `ParsePlain` rejects `|`.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter '/*/*/ApprovalsYamlTests/*'`
Expected: PASS. If the spec-example test fails on the sequence-item indent, verify the `indent + 2` rewrite matches the example's actual two-space continuation indent.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/Policy/ApprovalsYaml.cs test/Capacitor.Cli.Core.Tests.Unit/Policy/ApprovalsYamlTests.cs
git commit -m "Parse the approvals policy YAML subset strictly (#738)"
```

---

### Task 6: Policy document model and binder

**Files:**
- Create: `src/Capacitor.Cli.Core/Policy/PolicyDocument.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Policy/PolicyDocumentBinderTests.cs`

**Interfaces:**
- Consumes: `YamlMapping`, `YamlScalar`, `YamlSequence`, `ApprovalsYaml.Parse` (Task 5); `ShellTokenPattern.Parse` (Task 2); `ActionKind` (Task 1).
- Produces:

```csharp
public enum PolicyScope { Org, Team, Project, Repo, User }
public enum RuleOutcome { Allow, Ask, Deny }
public sealed record RuleMatcher(
    ActionKind Kind,
    IReadOnlyList<string> Command,
    bool Exact,
    IReadOnlyList<string> Path,
    IReadOnlyList<string> Host,
    int? Port,
    IReadOnlyList<string> Server,
    IReadOnlyList<string> Tool);
public sealed record PolicyRule(RuleMatcher Match, RuleOutcome Outcome, string? Reason);
public sealed record JudgeConfig(string Mode, string? Prompt);   // parsed and preserved; never consulted until the judge ships
public sealed record PolicyDocument(int Version, IReadOnlyList<PolicyRule> Rules, JudgeConfig? Judge);
public sealed class PolicyDocumentException(string message) : Exception(message);
public static class PolicyDocumentBinder {
    public const int MaxRules = 500;
    public const int MaxPatternsPerMatcher = 32;
    public static PolicyDocument Bind(string yamlText, PolicyScope scope);  // throws PolicyDocumentException (wrapping ApprovalsYamlException too)
}
```

Validation is strict and total: unknown top-level keys, unknown matcher fields, matcher fields that do not belong to the declared kind, invalid outcomes, empty patterns, unparseable shell patterns, out-of-range limits, and — in `Repo`/`User` scope — the server-scope fields `caps` and `enforcement` all throw. A thrown document is *ignored + degraded* by the snapshot builder (Task 8); nothing is ever half-applied.

- [ ] **Step 1: Write the failing tests**

```csharp
namespace Capacitor.Cli.Core.Tests.Unit.Policy;

using Capacitor.Cli.Core.Policy;

public class PolicyDocumentBinderTests {
    const string SpecExample = """
        version: 1
        rules:
          - match: { kind: shell, command: "git push --force*" }
            outcome: deny
            reason: force-push goes through the PR lane
          - match: { kind: shell, command: ["git status *", "git diff *", "dotnet build *"] }
            outcome: allow
          - match: { kind: mcp_tool, server: "kcap-*" }
            outcome: allow
          - match: { kind: shell, command: "gh pr merge" }
            outcome: ask
        judge:
          mode: unmatched
          prompt: |
            Approve routine read-only git and build commands anywhere in the repo.
        """;

    [Test]
    public async Task Spec_example_binds() {
        var doc = PolicyDocumentBinder.Bind(SpecExample, PolicyScope.Repo);
        await Assert.That(doc.Version).IsEqualTo(1);
        await Assert.That(doc.Rules.Count).IsEqualTo(4);
        await Assert.That(doc.Rules[0].Outcome).IsEqualTo(RuleOutcome.Deny);
        await Assert.That(doc.Rules[0].Match.Command).IsEquivalentTo(new[] { "git push --force*" });
        await Assert.That(doc.Rules[0].Reason).IsEqualTo("force-push goes through the PR lane");
        await Assert.That(doc.Rules[1].Match.Command.Count).IsEqualTo(3);
        await Assert.That(doc.Rules[2].Match.Kind).IsEqualTo(ActionKind.McpTool);
        await Assert.That(doc.Judge!.Mode).IsEqualTo("unmatched");
    }

    [Test]
    public async Task Exact_flag_and_scalar_or_list_fields_bind() {
        var doc = PolicyDocumentBinder.Bind("""
            version: 1
            rules:
              - match: { kind: shell, command: "gh pr merge", exact: true }
                outcome: ask
              - match: { kind: file_edit, path: ["/etc/*", "*.pem"] }
                outcome: deny
              - match: { kind: network, host: "*.internal.example", port: 443 }
                outcome: ask
              - match: { kind: other, tool: "TodoWrite" }
                outcome: allow
            """, PolicyScope.User);
        await Assert.That(doc.Rules[0].Match.Exact).IsTrue();
        await Assert.That(doc.Rules[1].Match.Path.Count).IsEqualTo(2);
        await Assert.That(doc.Rules[2].Match.Port).IsEqualTo(443);
        await Assert.That(doc.Rules[3].Match.Tool).IsEquivalentTo(new[] { "TodoWrite" });
    }

    [Test]
    public async Task Kind_only_matcher_is_legal() {
        var doc = PolicyDocumentBinder.Bind("version: 1\nrules:\n  - match: { kind: shell }\n    outcome: ask\n", PolicyScope.User);
        await Assert.That(doc.Rules[0].Match.Command).IsEmpty();
    }

    [Test]
    [Arguments("version: 2\nrules: []\n", "version")]
    [Arguments("version: 1\nruels: []\n", "unknown key")]
    [Arguments("version: 1\nrules:\n  - match: { kind: shell }\n    outcome: maybe\n", "outcome")]
    [Arguments("version: 1\nrules:\n  - match: { kind: teleport }\n    outcome: ask\n", "kind")]
    [Arguments("version: 1\nrules:\n  - match: { kind: shell, path: \"/x\" }\n    outcome: ask\n", "field for kind")]
    [Arguments("version: 1\nrules:\n  - match: { kind: shell, command: \"\" }\n    outcome: ask\n", "empty pattern")]
    [Arguments("version: 1\nrules:\n  - outcome: ask\n", "missing match")]
    [Arguments("version: 1\nrules:\n  - match: { kind: shell }\n", "missing outcome")]
    [Arguments("version: 1\nrules: []\ncaps: { narrower_widening: off }\n", "server-scope field")]
    [Arguments("version: 1\nrules: []\nenforcement: strict\n", "server-scope field")]
    [Arguments("version: 1\nrules: []\njudge: { mode: always }\n", "judge mode")]
    [Arguments("not yaml: [unterminated\n", "yaml error")]
    public async Task Invalid_documents_throw(string yaml, string _) {
        var ex = Assert.Throws<PolicyDocumentException>(
            () => PolicyDocumentBinder.Bind(yaml, PolicyScope.Repo));
        await Assert.That(ex.Message).IsNotEmpty();
    }

    [Test]
    public async Task Limits_are_enforced() {
        var many = string.Join("", Enumerable.Range(0, 501).Select(i =>
            $"  - match: {{ kind: shell, command: \"cmd{i}\" }}\n    outcome: ask\n"));
        Assert.Throws<PolicyDocumentException>(
            () => PolicyDocumentBinder.Bind($"version: 1\nrules:\n{many}", PolicyScope.User));
        var patterns = string.Join(", ", Enumerable.Range(0, 33).Select(i => $"\"p{i}\""));
        Assert.Throws<PolicyDocumentException>(
            () => PolicyDocumentBinder.Bind(
                $"version: 1\nrules:\n  - match: {{ kind: shell, command: [{patterns}] }}\n    outcome: ask\n",
                PolicyScope.User));
        await Task.CompletedTask;
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter '/*/*/PolicyDocumentBinderTests/*'`
Expected: build failure.

- [ ] **Step 3: Implement the binder**

```csharp
namespace Capacitor.Cli.Core.Policy;

public enum PolicyScope { Org, Team, Project, Repo, User }
public enum RuleOutcome { Allow, Ask, Deny }

public sealed record RuleMatcher(
    ActionKind Kind,
    IReadOnlyList<string> Command,
    bool Exact,
    IReadOnlyList<string> Path,
    IReadOnlyList<string> Host,
    int? Port,
    IReadOnlyList<string> Server,
    IReadOnlyList<string> Tool);

public sealed record PolicyRule(RuleMatcher Match, RuleOutcome Outcome, string? Reason);
public sealed record JudgeConfig(string Mode, string? Prompt);
public sealed record PolicyDocument(int Version, IReadOnlyList<PolicyRule> Rules, JudgeConfig? Judge);
public sealed class PolicyDocumentException(string message) : Exception(message);

public static class PolicyDocumentBinder {
    public const int MaxRules = 500;
    public const int MaxPatternsPerMatcher = 32;

    public static PolicyDocument Bind(string yamlText, PolicyScope scope) {
        YamlMapping root;
        try { root = ApprovalsYaml.Parse(yamlText); }
        catch (ApprovalsYamlException e) { throw new PolicyDocumentException(e.Message); }

        foreach (var e in root.Entries)
            if (e.Key is not ("version" or "rules" or "judge" or "caps" or "enforcement"))
                throw new PolicyDocumentException($"unknown key '{e.Key}'");
        if (scope is PolicyScope.Repo or PolicyScope.User)
            foreach (var key in (string[])["caps", "enforcement"])
                if (root[key] is not null)
                    throw new PolicyDocumentException($"'{key}' is a server-scope field and invalid in a {ScopeName(scope)} document");

        if (root["version"] is not YamlScalar { Value: "1" })
            throw new PolicyDocumentException("'version' must be 1");

        var rules = new List<PolicyRule>();
        if (root["rules"] is { } rulesNode) {
            if (rulesNode is not YamlSequence seq) throw new PolicyDocumentException("'rules' must be a sequence");
            if (seq.Items.Count > MaxRules) throw new PolicyDocumentException($"more than {MaxRules} rules");
            foreach (var item in seq.Items) rules.Add(BindRule(item));
        }
        return new PolicyDocument(1, rules, BindJudge(root["judge"]));
    }

    static PolicyRule BindRule(YamlNode item) {
        if (item is not YamlMapping rule) throw new PolicyDocumentException("each rule must be a mapping");
        foreach (var e in rule.Entries)
            if (e.Key is not ("match" or "outcome" or "reason"))
                throw new PolicyDocumentException($"unknown rule key '{e.Key}'");
        if (rule["match"] is not YamlMapping match) throw new PolicyDocumentException("rule is missing 'match'");
        var outcome = rule["outcome"] is YamlScalar o
            ? o.Value switch {
                "allow" => RuleOutcome.Allow, "ask" => RuleOutcome.Ask, "deny" => RuleOutcome.Deny,
                _ => throw new PolicyDocumentException($"unknown outcome '{o.Value}'"),
            }
            : throw new PolicyDocumentException("rule is missing 'outcome'");
        var reason = rule["reason"] is YamlScalar r ? r.Value : null;
        return new PolicyRule(BindMatcher(match), outcome, reason);
    }

    static readonly Dictionary<ActionKind, string[]> FieldsByKind = new() {
        [ActionKind.Shell] = ["command", "exact"],
        [ActionKind.FileEdit] = ["path"],
        [ActionKind.FileRead] = ["path"],
        [ActionKind.Network] = ["host", "port"],
        [ActionKind.McpTool] = ["server", "tool"],
        [ActionKind.Other] = ["tool"],
    };

    static RuleMatcher BindMatcher(YamlMapping match) {
        var kind = match["kind"] is YamlScalar k
            ? k.Value switch {
                "shell" => ActionKind.Shell, "file_edit" => ActionKind.FileEdit, "file_read" => ActionKind.FileRead,
                "network" => ActionKind.Network, "mcp_tool" => ActionKind.McpTool, "other" => ActionKind.Other,
                _ => throw new PolicyDocumentException($"unknown kind '{k.Value}'"),
            }
            : throw new PolicyDocumentException("matcher is missing 'kind'");
        foreach (var e in match.Entries)
            if (e.Key != "kind" && !FieldsByKind[kind].Contains(e.Key))
                throw new PolicyDocumentException($"'{e.Key}' is not a matcher field for kind '{k.Value}'");

        var command = Patterns(match["command"], "command");
        foreach (var p in command)
            if (ShellTokenPattern.Parse(p) is null)
                throw new PolicyDocumentException($"empty pattern in 'command'");
        var exact = match["exact"] switch {
            null => false,
            YamlScalar { Value: "true" } => true,
            YamlScalar { Value: "false" } => false,
            _ => throw new PolicyDocumentException("'exact' must be true or false"),
        };
        int? port = match["port"] switch {
            null => null,
            YamlScalar p when int.TryParse(p.Value, out var n) && n is > 0 and <= 65535 => n,
            _ => throw new PolicyDocumentException("'port' must be a port number"),
        };
        return new RuleMatcher(kind, command, exact,
            Patterns(match["path"], "path"), Patterns(match["host"], "host"), port,
            Patterns(match["server"], "server"), Patterns(match["tool"], "tool"));
    }

    static IReadOnlyList<string> Patterns(YamlNode? node, string field) {
        var values = node switch {
            null => (List<string>)[],
            YamlScalar s => [s.Value],
            YamlSequence seq => [.. seq.Items.Select(i => i is YamlScalar s
                ? s.Value
                : throw new PolicyDocumentException($"'{field}' entries must be strings"))],
            _ => throw new PolicyDocumentException($"'{field}' must be a string or a list of strings"),
        };
        if (values.Count > MaxPatternsPerMatcher)
            throw new PolicyDocumentException($"more than {MaxPatternsPerMatcher} patterns in '{field}'");
        foreach (var v in values)
            if (string.IsNullOrWhiteSpace(v))
                throw new PolicyDocumentException($"empty pattern in '{field}'");
        return values;
    }

    static JudgeConfig? BindJudge(YamlNode? node) {
        if (node is null) return null;
        if (node is not YamlMapping judge) throw new PolicyDocumentException("'judge' must be a mapping");
        foreach (var e in judge.Entries)
            if (e.Key is not ("mode" or "prompt"))
                throw new PolicyDocumentException($"unknown judge key '{e.Key}'");
        var mode = judge["mode"] is YamlScalar m
            ? m.Value is "off" or "unmatched"
                ? m.Value
                : throw new PolicyDocumentException($"unknown judge mode '{m.Value}'")
            : throw new PolicyDocumentException("'judge' is missing 'mode'");
        return new JudgeConfig(mode, judge["prompt"] is YamlScalar p ? p.Value : null);
    }

    static string ScopeName(PolicyScope scope) => scope.ToString().ToLowerInvariant();
}
```

Note: the compiler will flag `k.Value` used in the field-check loop outside the pattern scope — hoist the kind's YAML string into a local (`var kindName = k.Value;`) when implementing; the test names the message shape, not the exact wording.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter '/*/*/PolicyDocumentBinderTests/*'`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/Policy/PolicyDocument.cs test/Capacitor.Cli.Core.Tests.Unit/Policy/PolicyDocumentBinderTests.cs
git commit -m "Bind and validate approvals policy documents per scope (#738)"
```

---

### Task 7: Policy engine — tighten-only merge with full allow coverage

**Files:**
- Create: `src/Capacitor.Cli.Core/Policy/RuleMatch.cs`
- Create: `src/Capacitor.Cli.Core/Policy/PolicyEngine.cs`
- Create: `src/Capacitor.Cli.Core/Policy/PolicySnapshot.cs` (the snapshot *shape* only — building/persistence is Task 8)
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Policy/PolicyEngineTests.cs`

**Interfaces:**
- Consumes: Tasks 1, 2, 4, 6 types.
- Produces:

```csharp
public sealed record PolicyScopeDocument(PolicyScope Scope, string SourcePath, string Content, PolicyDocument Document);
public sealed record PolicySnapshot(
    string Id, IReadOnlyList<PolicyScopeDocument> Documents, bool Degraded, IReadOnlyList<string> Degradations) {
    public bool IsEmpty => Documents.Count == 0 && !Degraded;
    public static readonly PolicySnapshot Empty = new("empty", [], false, []);
}
public enum EvaluationMode { Full, TightenOnly }
public enum PolicyOutcome { Allow, Ask, Deny, None }
public sealed record MatchedRuleRef(PolicyScope Scope, int RuleIndex, RuleOutcome Outcome, string? Reason);
public sealed record PolicyEvaluation(PolicyOutcome Outcome, IReadOnlyList<MatchedRuleRef> MatchedRules);
public static class PolicyEngine {
    public const string Version = "1";   // canonicalization version; part of the snapshot hash (Task 8)
    public static PolicyEvaluation Evaluate(PolicySnapshot snapshot, CanonicalAction action, EvaluationMode mode);
}
internal static class RuleMatch {
    internal static bool Restrictive(PolicyRule rule, CanonicalAction action, ActionComponent component);
    internal static bool Covers(PolicyRule rule, ActionComponent component, ActionKind kind);
}
```

Merge order (normative, spec "Merge rule"): (1) any deny hit on any restriction component → Deny; (2) any ask hit → Ask; (3) Full mode only: nonempty coverage set with every component covered by some allow rule → Allow; (4) else None. Scope iteration order is fixed `Org, Team, Project, Repo, User`, rules in document order — that makes the recorded first-hit deterministic. `TightenOnly` never computes step 3.

- [ ] **Step 1: Write the failing tests**

```csharp
namespace Capacitor.Cli.Core.Tests.Unit.Policy;

using Capacitor.Cli.Core.Policy;

public class PolicyEngineTests {
    static PolicySnapshot Snap(params (PolicyScope Scope, string Yaml)[] docs) => new(
        Id: "test",
        Documents: [.. docs.Select(d => new PolicyScopeDocument(
            d.Scope, $"/{d.Scope}", d.Yaml, PolicyDocumentBinder.Bind(d.Yaml, d.Scope)))],
        Degraded: false, Degradations: []);

    static CanonicalAction Bash(string command) {
        var analysis = ShellCommandAnalyzer.Analyze(command);
        return new() {
            Kind = ActionKind.Shell, Vendor = "claude", Command = command,
            Analyzed = analysis.Analyzed, Segments = analysis.Segments,
        };
    }

    const string UserDoc = """
        version: 1
        rules:
          - match: { kind: shell, command: "git push --force*" }
            outcome: deny
          - match: { kind: shell, command: "gh pr merge" }
            outcome: ask
          - match: { kind: shell, command: ["git status *", "git diff *"] }
            outcome: allow
        """;

    [Test]
    [Arguments("git status --porcelain", PolicyOutcome.Allow)]
    [Arguments("git push --force origin main", PolicyOutcome.Deny)]
    [Arguments("env FOO=1 git push --force", PolicyOutcome.Deny)]       // any-position run
    [Arguments("gh pr merge --squash", PolicyOutcome.Ask)]
    [Arguments("cargo build", PolicyOutcome.None)]
    public async Task Merge_rule_basics(string command, PolicyOutcome expected) {
        var eval = PolicyEngine.Evaluate(Snap((PolicyScope.User, UserDoc)), Bash(command), EvaluationMode.Full);
        await Assert.That(eval.Outcome).IsEqualTo(expected);
    }

    [Test]
    public async Task Partial_allow_coverage_never_authorizes() {
        // "git status" is allowed; the second segment is not covered → unmatched, not allowed.
        var eval = PolicyEngine.Evaluate(Snap((PolicyScope.User, UserDoc)),
            Bash("git status && rm -rf x"), EvaluationMode.Full);
        await Assert.That(eval.Outcome).IsEqualTo(PolicyOutcome.None);
    }

    [Test]
    public async Task Full_coverage_across_different_allow_rules_authorizes() {
        var eval = PolicyEngine.Evaluate(Snap((PolicyScope.User, UserDoc)),
            Bash("git status && git diff --stat"), EvaluationMode.Full);
        await Assert.That(eval.Outcome).IsEqualTo(PolicyOutcome.Allow);
        await Assert.That(eval.MatchedRules.Count).IsEqualTo(1);       // both segments hit rule index 2
    }

    [Test]
    public async Task Unanalyzed_command_is_never_allow_eligible_but_deny_still_bites() {
        var snap = Snap((PolicyScope.User, UserDoc));
        // Redirection makes it unanalyzed; the deny fragment run still matches.
        var denied = PolicyEngine.Evaluate(snap, Bash("git push --force > /dev/null"), EvaluationMode.Full);
        await Assert.That(denied.Outcome).IsEqualTo(PolicyOutcome.Deny);
        // A would-be-allowed command with redirection is unmatched, never allowed.
        var evaded = PolicyEngine.Evaluate(snap, Bash("git status > pwn.yml"), EvaluationMode.Full);
        await Assert.That(evaded.Outcome).IsEqualTo(PolicyOutcome.None);
    }

    [Test]
    public async Task Raw_substring_glob_matches_when_lexing_is_abandoned() {
        // Unterminated quote abandons fragment lexing; the raw substring glob still hits.
        var eval = PolicyEngine.Evaluate(Snap((PolicyScope.User, UserDoc)),
            Bash("git push --force 'oops"), EvaluationMode.Full);
        await Assert.That(eval.Outcome).IsEqualTo(PolicyOutcome.Deny);
    }

    [Test]
    public async Task Wider_scope_deny_beats_narrower_allow_and_vice_versa() {
        var repoAllow = "version: 1\nrules:\n  - match: { kind: shell, command: \"npm publish *\" }\n    outcome: allow\n";
        var userDeny = "version: 1\nrules:\n  - match: { kind: shell, command: \"npm publish\" }\n    outcome: deny\n";
        var eval = PolicyEngine.Evaluate(Snap((PolicyScope.Repo, repoAllow), (PolicyScope.User, userDeny)),
            Bash("npm publish --tag next"), EvaluationMode.Full);
        await Assert.That(eval.Outcome).IsEqualTo(PolicyOutcome.Deny);
        await Assert.That(eval.MatchedRules[0].Scope).IsEqualTo(PolicyScope.User);
    }

    [Test]
    public async Task Tighten_only_mode_never_allows() {
        var snap = Snap((PolicyScope.User, UserDoc));
        var eval = PolicyEngine.Evaluate(snap, Bash("git status"), EvaluationMode.TightenOnly);
        await Assert.That(eval.Outcome).IsEqualTo(PolicyOutcome.None);
        var deny = PolicyEngine.Evaluate(snap, Bash("git push --force"), EvaluationMode.TightenOnly);
        await Assert.That(deny.Outcome).IsEqualTo(PolicyOutcome.Deny);
    }

    [Test]
    public async Task Kind_level_matcher_hits_sentinel_but_field_matcher_does_not() {
        var kindDeny = "version: 1\nrules:\n  - match: { kind: other }\n    outcome: deny\n";
        var fieldDeny = "version: 1\nrules:\n  - match: { kind: other, tool: \"X*\" }\n    outcome: deny\n";
        var nameless = new CanonicalAction { Kind = ActionKind.Other, Vendor = "v" };
        var hit = PolicyEngine.Evaluate(Snap((PolicyScope.User, kindDeny)), nameless, EvaluationMode.Full);
        await Assert.That(hit.Outcome).IsEqualTo(PolicyOutcome.Deny);
        var miss = PolicyEngine.Evaluate(Snap((PolicyScope.User, fieldDeny)), nameless, EvaluationMode.Full);
        await Assert.That(miss.Outcome).IsEqualTo(PolicyOutcome.None);
    }

    [Test]
    public async Task Nameless_other_is_never_allowed_even_by_kind_level_allow() {
        var kindAllow = "version: 1\nrules:\n  - match: { kind: other }\n    outcome: allow\n";
        var nameless = new CanonicalAction { Kind = ActionKind.Other, Vendor = "v" };
        var eval = PolicyEngine.Evaluate(Snap((PolicyScope.User, kindAllow)), nameless, EvaluationMode.Full);
        await Assert.That(eval.Outcome).IsEqualTo(PolicyOutcome.None);
    }

    [Test]
    public async Task Scalar_kinds_match_their_fields() {
        var doc = """
            version: 1
            rules:
              - match: { kind: file_edit, path: "*.pem" }
                outcome: deny
              - match: { kind: network, host: "*.evil.example" }
                outcome: deny
              - match: { kind: network, host: "registry.example", port: 8443 }
                outcome: ask
              - match: { kind: mcp_tool, server: "kcap-*" }
                outcome: allow
            """;
        var snap = Snap((PolicyScope.User, doc));
        var pem = new CanonicalAction { Kind = ActionKind.FileEdit, Vendor = "v", Paths = ["/repo/key.pem"] };
        await Assert.That(PolicyEngine.Evaluate(snap, pem, EvaluationMode.Full).Outcome).IsEqualTo(PolicyOutcome.Deny);
        var web = new CanonicalAction { Kind = ActionKind.Network, Vendor = "v", Host = "api.evil.example" };
        await Assert.That(PolicyEngine.Evaluate(snap, web, EvaluationMode.Full).Outcome).IsEqualTo(PolicyOutcome.Deny);
        var portMiss = new CanonicalAction { Kind = ActionKind.Network, Vendor = "v", Host = "registry.example", Port = 443 };
        await Assert.That(PolicyEngine.Evaluate(snap, portMiss, EvaluationMode.Full).Outcome).IsEqualTo(PolicyOutcome.None);
        var mcp = new CanonicalAction { Kind = ActionKind.McpTool, Vendor = "v", Server = "kcap-flows", Tool = "start_review_flow" };
        await Assert.That(PolicyEngine.Evaluate(snap, mcp, EvaluationMode.Full).Outcome).IsEqualTo(PolicyOutcome.Allow);
    }

    [Test]
    public async Task Path_deny_matches_glob_against_the_absolute_path() {
        var doc = "version: 1\nrules:\n  - match: { kind: file_edit, path: \"/repo/.github/*\" }\n    outcome: ask\n";
        var a = new CanonicalAction { Kind = ActionKind.FileEdit, Vendor = "v", Paths = ["/repo/.github/workflows/ci.yml"] };
        var eval = PolicyEngine.Evaluate(Snap((PolicyScope.User, doc)), a, EvaluationMode.Full);
        await Assert.That(eval.Outcome).IsEqualTo(PolicyOutcome.Ask);
    }

    [Test]
    public async Task Empty_snapshot_yields_none() {
        var eval = PolicyEngine.Evaluate(PolicySnapshot.Empty, Bash("anything"), EvaluationMode.Full);
        await Assert.That(eval.Outcome).IsEqualTo(PolicyOutcome.None);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter '/*/*/PolicyEngineTests/*'`
Expected: build failure.

- [ ] **Step 3: Implement snapshot shape, matching, and engine**

`PolicySnapshot.cs`:

```csharp
namespace Capacitor.Cli.Core.Policy;

public sealed record PolicyScopeDocument(PolicyScope Scope, string SourcePath, string Content, PolicyDocument Document);

public sealed record PolicySnapshot(
    string Id, IReadOnlyList<PolicyScopeDocument> Documents, bool Degraded, IReadOnlyList<string> Degradations) {
    public bool IsEmpty => Documents.Count == 0 && !Degraded;
    public static readonly PolicySnapshot Empty = new("empty", [], false, []);
}
```

`RuleMatch.cs`:

```csharp
namespace Capacitor.Cli.Core.Policy;

/// <summary>
/// Matches one rule against one action component. The binder guarantees a matcher only
/// carries fields legal for its kind, so per-kind arms need not re-check foreign fields.
/// An absent field list matches anything; a present one must hit.
/// </summary>
internal static class RuleMatch {
    internal static bool Restrictive(PolicyRule rule, CanonicalAction action, ActionComponent component) {
        var m = rule.Match;
        if (m.Kind != action.Kind) return false;
        return component switch {
            ShellSegmentComponent s => MatchesArgv(m, s.Segment.Argv, restrictive: true),
            RawShellComponent raw => MatchesRaw(m, raw.Command),
            PathComponent p => AnyOrEmpty(m.Path, p.Path),
            HostComponent h => AnyOrEmpty(m.Host, h.Host) && (m.Port is null || m.Port == h.Port),
            McpToolComponent t => AnyOrEmpty(m.Server, t.Server) && AnyOrEmpty(m.Tool, t.Tool),
            OtherToolComponent o => AnyOrEmpty(m.Tool, o.ToolName),
            SentinelComponent => HasNoFieldConstraints(m),
            _ => false,
        };
    }

    internal static bool Covers(PolicyRule rule, ActionComponent component, ActionKind kind) {
        var m = rule.Match;
        if (m.Kind != kind) return false;
        return component switch {
            ShellSegmentComponent s => MatchesArgv(m, s.Segment.Argv, restrictive: false),
            PathComponent p => AnyOrEmpty(m.Path, p.Path),
            HostComponent h => AnyOrEmpty(m.Host, h.Host) && (m.Port is null || m.Port == h.Port),
            McpToolComponent t => AnyOrEmpty(m.Server, t.Server) && AnyOrEmpty(m.Tool, t.Tool),
            OtherToolComponent o => AnyOrEmpty(m.Tool, o.ToolName),
            _ => false,   // RawShellComponent and SentinelComponent are never in a coverage set
        };
    }

    static bool MatchesArgv(RuleMatcher m, IReadOnlyList<string> argv, bool restrictive) {
        if (m.Command.Count == 0) return true;
        foreach (var pattern in m.Command) {
            if (ShellTokenPattern.Parse(pattern) is not { } p) continue;
            if (restrictive ? p.MatchesRestrictive(argv, m.Exact) : p.MatchesAllow(argv)) return true;
        }
        return false;
    }

    static bool MatchesRaw(RuleMatcher m, string raw) {
        if (m.Command.Count == 0) return true;
        var fragments = ShellFragmentLexer.Lex(raw);
        foreach (var pattern in m.Command) {
            if (fragments.Count > 0 && ShellTokenPattern.Parse(pattern) is { } p
                && p.MatchesRestrictive(fragments, m.Exact)) return true;
            if (GlobPattern.IsMatch($"*{pattern}*", raw)) return true;
        }
        return false;
    }

    static bool AnyOrEmpty(IReadOnlyList<string> patterns, string value) {
        if (patterns.Count == 0) return true;
        foreach (var p in patterns)
            if (GlobPattern.IsMatch(p, value)) return true;
        return false;
    }

    static bool HasNoFieldConstraints(RuleMatcher m) =>
        m.Command.Count == 0 && m.Path.Count == 0 && m.Host.Count == 0
        && m.Server.Count == 0 && m.Tool.Count == 0 && m.Port is null;
}
```

`PolicyEngine.cs`:

```csharp
namespace Capacitor.Cli.Core.Policy;

public enum EvaluationMode { Full, TightenOnly }
public enum PolicyOutcome { Allow, Ask, Deny, None }

public sealed record MatchedRuleRef(PolicyScope Scope, int RuleIndex, RuleOutcome Outcome, string? Reason);
public sealed record PolicyEvaluation(PolicyOutcome Outcome, IReadOnlyList<MatchedRuleRef> MatchedRules) {
    public static readonly PolicyEvaluation None = new(PolicyOutcome.None, []);
}

public static class PolicyEngine {
    public const string Version = "1";
    static readonly PolicyScope[] ScopeOrder =
        [PolicyScope.Org, PolicyScope.Team, PolicyScope.Project, PolicyScope.Repo, PolicyScope.User];

    public static PolicyEvaluation Evaluate(PolicySnapshot snapshot, CanonicalAction action, EvaluationMode mode) {
        if (snapshot.Documents.Count == 0) return PolicyEvaluation.None;
        var restriction = PolicyComponents.RestrictionOf(action);
        foreach (var outcome in (RuleOutcome[])[RuleOutcome.Deny, RuleOutcome.Ask]) {
            if (FirstRestrictiveHit(snapshot, action, restriction, outcome) is { } hit)
                return new(outcome == RuleOutcome.Deny ? PolicyOutcome.Deny : PolicyOutcome.Ask, [hit]);
        }
        if (mode == EvaluationMode.TightenOnly) return PolicyEvaluation.None;

        var coverage = PolicyComponents.CoverageOf(action);
        if (coverage.Count == 0) return PolicyEvaluation.None;
        var covering = new List<MatchedRuleRef>();
        foreach (var component in coverage) {
            if (FirstCoveringAllow(snapshot, action, component) is not { } rule) return PolicyEvaluation.None;
            if (!covering.Contains(rule)) covering.Add(rule);
        }
        return new(PolicyOutcome.Allow, covering);
    }

    static MatchedRuleRef? FirstRestrictiveHit(
        PolicySnapshot snapshot, CanonicalAction action, IReadOnlyList<ActionComponent> restriction, RuleOutcome outcome) {
        foreach (var scope in ScopeOrder)
            foreach (var doc in snapshot.Documents)
                if (doc.Scope == scope)
                    for (var i = 0; i < doc.Document.Rules.Count; i++) {
                        var rule = doc.Document.Rules[i];
                        if (rule.Outcome != outcome) continue;
                        foreach (var component in restriction)
                            if (RuleMatch.Restrictive(rule, action, component))
                                return new(scope, i, outcome, rule.Reason);
                    }
        return null;
    }

    static MatchedRuleRef? FirstCoveringAllow(PolicySnapshot snapshot, CanonicalAction action, ActionComponent component) {
        foreach (var scope in ScopeOrder)
            foreach (var doc in snapshot.Documents)
                if (doc.Scope == scope)
                    for (var i = 0; i < doc.Document.Rules.Count; i++) {
                        var rule = doc.Document.Rules[i];
                        if (rule.Outcome == RuleOutcome.Allow && RuleMatch.Covers(rule, component, action.Kind))
                            return new(scope, i, RuleOutcome.Allow, rule.Reason);
                    }
        return null;
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter '/*/*/PolicyEngineTests/*'`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/Policy/RuleMatch.cs src/Capacitor.Cli.Core/Policy/PolicyEngine.cs src/Capacitor.Cli.Core/Policy/PolicySnapshot.cs test/Capacitor.Cli.Core.Tests.Unit/Policy/PolicyEngineTests.cs
git commit -m "Evaluate policies tighten-only with full allow coverage (#738)"
```

---

### Task 8: Snapshot builder and per-session store

**Files:**
- Create: `src/Capacitor.Cli.Core/Policy/PolicySnapshotBuilder.cs`
- Create: `src/Capacitor.Cli.Core/Policy/PolicySnapshotStore.cs`
- Create: `src/Capacitor.Cli.Core/Policy/PolicyJsonContext.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Policy/PolicySnapshotBuilderTests.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Policy/PolicySnapshotStoreTests.cs`

**Interfaces:**
- Consumes: `PolicySnapshot`, `PolicyScopeDocument` (Task 7), `PolicyDocumentBinder` (Task 6), `ConfigRoot` (existing: `Path(params ReadOnlySpan<string>)`, `FromEnvironment()`).
- Produces:

```csharp
public static class PolicySnapshotBuilder {
    public const string RepoRelativeDir = ".kcap";
    public const string FileName = "approvals.yaml";
    public static PolicySnapshot Build(string? repoRoot, ConfigRoot config);
}
public sealed class PolicySnapshotStore(ConfigRoot config) {
    public PolicySnapshot? TryLoad(string sessionKey);
    public void Save(string sessionKey, PolicySnapshot snapshot);              // atomic temp+rename
    public PolicySnapshot LoadOrBuild(string sessionKey, string? repoRoot);    // load, else Build + Save
}
```

The store persists under `config.Path("policy", "sessions", $"{Sanitize(sessionKey)}.json")` — the `CursorMarkers` per-session marker pattern. The persisted form keeps only `(id, degraded, degradations, documents: [scope, source_path, content])` and **re-binds documents on load** so the persisted format stays trivial; a corrupt or unreadable file falls back to a rebuild (never throws). This is what makes mid-session edits inert: seams load the session's saved snapshot, not the live files.

- [ ] **Step 1: Write the failing tests**

```csharp
namespace Capacitor.Cli.Core.Tests.Unit.Policy;

using Capacitor.Cli.Core.Policy;

public class PolicySnapshotBuilderTests {
    [TempDir] public required TempDir Tmp { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string ValidDoc = "version: 1\nrules:\n  - match: { kind: shell, command: \"rm -rf*\" }\n    outcome: deny\n";

    [Test]
    public async Task Builds_from_repo_and_user_files() {
        var repo = Tmp.CreateDir("repo");
        Tmp.CreateDir("repo/.kcap");
        Tmp.CreateFile("repo/.kcap/approvals.yaml", ValidDoc);
        File.WriteAllText(Config.Root.Path("approvals.yaml"), ValidDoc);
        var snap = PolicySnapshotBuilder.Build(repo, Config.Root);
        await Assert.That(snap.Documents.Count).IsEqualTo(2);
        await Assert.That(snap.Documents[0].Scope).IsEqualTo(PolicyScope.Repo);
        await Assert.That(snap.Documents[1].Scope).IsEqualTo(PolicyScope.User);
        await Assert.That(snap.Degraded).IsFalse();
        await Assert.That(snap.IsEmpty).IsFalse();
    }

    [Test]
    public async Task No_files_yields_the_empty_snapshot() {
        var snap = PolicySnapshotBuilder.Build(Tmp.CreateDir("repo"), Config.Root);
        await Assert.That(snap.IsEmpty).IsTrue();
    }

    [Test]
    public async Task Malformed_file_is_ignored_loudly_never_silently() {
        var repo = Tmp.CreateDir("repo");
        Tmp.CreateDir("repo/.kcap");
        Tmp.CreateFile("repo/.kcap/approvals.yaml", "version: 1\ncaps: { narrower_widening: off }\n");
        File.WriteAllText(Config.Root.Path("approvals.yaml"), ValidDoc);
        var snap = PolicySnapshotBuilder.Build(repo, Config.Root);
        await Assert.That(snap.Documents.Count).IsEqualTo(1);      // user doc survives
        await Assert.That(snap.Degraded).IsTrue();
        await Assert.That(snap.Degradations.Count).IsEqualTo(1);
        await Assert.That(snap.Degradations[0]).Contains("approvals.yaml");
    }

    [Test]
    public async Task Snapshot_id_is_content_stable_and_content_sensitive() {
        var repo = Tmp.CreateDir("repo");
        Tmp.CreateDir("repo/.kcap");
        Tmp.CreateFile("repo/.kcap/approvals.yaml", ValidDoc);
        var a = PolicySnapshotBuilder.Build(repo, Config.Root);
        var b = PolicySnapshotBuilder.Build(repo, Config.Root);
        await Assert.That(a.Id).IsEqualTo(b.Id);
        File.WriteAllText(Path.Combine(repo, ".kcap", "approvals.yaml"), ValidDoc + "  - match: { kind: shell }\n    outcome: ask\n");
        var c = PolicySnapshotBuilder.Build(repo, Config.Root);
        await Assert.That(c.Id).IsNotEqualTo(a.Id);
    }

    [Test]
    public async Task Null_repo_root_reads_only_the_user_scope() {
        File.WriteAllText(Config.Root.Path("approvals.yaml"), ValidDoc);
        var snap = PolicySnapshotBuilder.Build(repoRoot: null, Config.Root);
        await Assert.That(snap.Documents.Count).IsEqualTo(1);
        await Assert.That(snap.Documents[0].Scope).IsEqualTo(PolicyScope.User);
    }
}

public class PolicySnapshotStoreTests {
    [TempDir] public required TempDir Tmp { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string ValidDoc = "version: 1\nrules:\n  - match: { kind: shell, command: \"rm -rf*\" }\n    outcome: deny\n";

    [Test]
    public async Task Save_then_load_round_trips_and_rebinds_documents() {
        var repo = Tmp.CreateDir("repo");
        Tmp.CreateDir("repo/.kcap");
        Tmp.CreateFile("repo/.kcap/approvals.yaml", ValidDoc);
        var store = new PolicySnapshotStore(Config.Root);
        var built = PolicySnapshotBuilder.Build(repo, Config.Root);
        store.Save("abc123", built);
        var loaded = store.TryLoad("abc123")!;
        await Assert.That(loaded.Id).IsEqualTo(built.Id);
        await Assert.That(loaded.Documents[0].Document.Rules.Count).IsEqualTo(1);
    }

    [Test]
    public async Task LoadOrBuild_is_sticky_against_later_file_edits() {
        var repo = Tmp.CreateDir("repo");
        Tmp.CreateDir("repo/.kcap");
        Tmp.CreateFile("repo/.kcap/approvals.yaml", ValidDoc);
        var store = new PolicySnapshotStore(Config.Root);
        var first = store.LoadOrBuild("s1", repo);
        File.Delete(Path.Combine(repo, ".kcap", "approvals.yaml"));
        var second = store.LoadOrBuild("s1", repo);
        await Assert.That(second.Id).IsEqualTo(first.Id);
        await Assert.That(second.Documents.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Corrupt_persisted_snapshot_falls_back_to_rebuild() {
        var store = new PolicySnapshotStore(Config.Root);
        Directory.CreateDirectory(Config.Root.Path("policy", "sessions"));
        File.WriteAllText(Config.Root.Path("policy", "sessions", "bad.json"), "{not json");
        var snap = store.LoadOrBuild("bad", repoRoot: null);
        await Assert.That(snap.IsEmpty).IsTrue();
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter '/*/*/PolicySnapshotBuilderTests/*'`
Expected: build failure.

- [ ] **Step 3: Implement builder, store, and JSON context**

`PolicyJsonContext.cs` (scoped context beside its DTOs — the `SessionStartMemoryJsonContext` pattern):

```csharp
namespace Capacitor.Cli.Core.Policy;

using System.Text.Json.Serialization;

sealed record PolicySnapshotFileV1(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("degraded")] bool Degraded,
    [property: JsonPropertyName("degradations")] string[] Degradations,
    [property: JsonPropertyName("documents")] PolicySnapshotFileDocV1[] Documents);

sealed record PolicySnapshotFileDocV1(
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("source_path")] string SourcePath,
    [property: JsonPropertyName("content")] string Content);

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(PolicySnapshotFileV1))]
[JsonSerializable(typeof(PolicyJournalFileV1))]
partial class PolicyJsonContext : JsonSerializerContext;
```

(`PolicyJournalFileV1` arrives in Task 10 — add its registration then; for this task register only the snapshot record and drop the second attribute line.)

`PolicySnapshotBuilder.cs`:

```csharp
namespace Capacitor.Cli.Core.Policy;

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

public static class PolicySnapshotBuilder {
    public const string RepoRelativeDir = ".kcap";
    public const string FileName = "approvals.yaml";

    public static PolicySnapshot Build(string? repoRoot, ConfigRoot config) {
        var documents = new List<PolicyScopeDocument>();
        var degradations = new List<string>();
        if (repoRoot is not null)
            TryLoad(Path.Combine(repoRoot, RepoRelativeDir, FileName), PolicyScope.Repo, documents, degradations);
        TryLoad(config.Path(FileName), PolicyScope.User, documents, degradations);
        var id = ComputeId(documents);
        return new PolicySnapshot(id, documents, degradations.Count > 0, degradations);
    }

    static void TryLoad(string path, PolicyScope scope, List<PolicyScopeDocument> documents, List<string> degradations) {
        string content;
        try {
            if (!File.Exists(path)) return;
            content = File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            degradations.Add($"{scope.ToString().ToLowerInvariant()} policy at {path} unreadable: {e.Message}");
            return;
        }
        try {
            documents.Add(new PolicyScopeDocument(scope, path, content, PolicyDocumentBinder.Bind(content, scope)));
        }
        catch (PolicyDocumentException e) {
            degradations.Add($"{scope.ToString().ToLowerInvariant()} policy at {path} ignored: {e.Message}");
        }
    }

    static string ComputeId(List<PolicyScopeDocument> documents) {
        using var ms = new MemoryStream();
        void Write(string s) {
            var bytes = Encoding.UTF8.GetBytes(s);
            Span<byte> len = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(len, (uint)bytes.Length);
            ms.Write(len);
            ms.Write(bytes);
        }
        Write(PolicyEngine.Version);
        foreach (var d in documents) { Write(d.Scope.ToString()); Write(d.Content); }
        return Convert.ToHexStringLower(SHA256.HashData(ms.ToArray()));
    }
}
```

`PolicySnapshotStore.cs`:

```csharp
namespace Capacitor.Cli.Core.Policy;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

public sealed class PolicySnapshotStore(ConfigRoot config) {
    string PathFor(string sessionKey) => config.Path("policy", "sessions", $"{Sanitize(sessionKey)}.json");

    public PolicySnapshot? TryLoad(string sessionKey) {
        try {
            var path = PathFor(sessionKey);
            if (!File.Exists(path)) return null;
            var file = JsonSerializer.Deserialize(File.ReadAllText(path), PolicyJsonContext.Default.PolicySnapshotFileV1);
            if (file is null) return null;
            var documents = new List<PolicyScopeDocument>();
            foreach (var d in file.Documents) {
                var scope = Enum.Parse<PolicyScope>(d.Scope);
                documents.Add(new PolicyScopeDocument(scope, d.SourcePath, d.Content,
                    PolicyDocumentBinder.Bind(d.Content, scope)));
            }
            return new PolicySnapshot(file.Id, documents, file.Degraded, file.Degradations);
        }
        catch { return null; }
    }

    public void Save(string sessionKey, PolicySnapshot snapshot) {
        try {
            var path = PathFor(sessionKey);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var file = new PolicySnapshotFileV1(snapshot.Id, snapshot.Degraded, [.. snapshot.Degradations],
                [.. snapshot.Documents.Select(d => new PolicySnapshotFileDocV1(d.Scope.ToString(), d.SourcePath, d.Content))]);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(file, PolicyJsonContext.Default.PolicySnapshotFileV1));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public PolicySnapshot LoadOrBuild(string sessionKey, string? repoRoot) {
        if (TryLoad(sessionKey) is { } cached) return cached;
        var built = PolicySnapshotBuilder.Build(repoRoot, config);
        Save(sessionKey, built);
        return built;
    }

    static string Sanitize(string sessionKey) =>
        sessionKey.Length is > 0 and <= 64 && sessionKey.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')
            ? sessionKey
            : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sessionKey)))[..32];
}
```

- [ ] **Step 4: Run both test classes to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter '/*/*/PolicySnapshot*Tests/*'`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/Policy/PolicySnapshotBuilder.cs src/Capacitor.Cli.Core/Policy/PolicySnapshotStore.cs src/Capacitor.Cli.Core/Policy/PolicyJsonContext.cs test/Capacitor.Cli.Core.Tests.Unit/Policy/PolicySnapshotBuilderTests.cs test/Capacitor.Cli.Core.Tests.Unit/Policy/PolicySnapshotStoreTests.cs
git commit -m "Build and persist per-session policy snapshots from local scopes (#738)"
```

---

### Task 9: Claude action normalizer

**Files:**
- Create: `src/Capacitor.Cli.Core/Policy/LexicalPaths.cs`
- Create: `src/Capacitor.Cli.Core/Harness/Claude/ClaudeActionNormalizer.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Policy/LexicalPathsTests.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Harness/Claude/ClaudeActionNormalizerTests.cs`

**Interfaces:**
- Consumes: `CanonicalAction`, `ActionKind`, `ShellCommandAnalyzer` (Tasks 1, 3); `JsonElementExtensions` (existing: `el.Str("x")`, `el.IsObject`).
- Produces:

```csharp
public static class LexicalPaths {
    // Lexically resolves path against cwd: joins if relative, collapses '.'/'..', forward slashes.
    // No filesystem access and no symlink resolution — symlink escapes are the vendor sandbox's concern.
    public static string? TryResolve(string? cwd, string? path);   // null when unresolvable (relative path, no cwd)
}
public static class ClaudeActionNormalizer {
    // Never throws and never skips: any unmappable payload yields kind Other with the raw payload.
    public static CanonicalAction Normalize(string? toolName, JsonElement? toolInput, string? cwd);
}
```

Mapping table (everything not listed → `Other` with `RawToolName = toolName`):

| Claude tool | Kind | Fields |
|---|---|---|
| `Bash` (`command`) | shell | raw command + `ShellCommandAnalyzer.Analyze` |
| `Edit`, `Write`, `MultiEdit` (`file_path`), `NotebookEdit` (`notebook_path`) | file_edit | one resolved path |
| `Read` (`file_path`) | file_read | one resolved path |
| `Glob`, `Grep` (`path`, optional → cwd) | file_read | one resolved path |
| `WebFetch` (`url`) | network | `Uri.IdnHost` lowercased, non-default port |
| `mcp__{server}__{tool}` | mcp_tool | server, tool (second `__` split; further underscores stay in the tool name) |

A required field that is missing/empty, a relative path with no cwd, or an unparsable URL all fall to `Other` — normalization never fails open (spec "Canonical actions").

- [ ] **Step 1: Write the failing tests**

```csharp
namespace Capacitor.Cli.Core.Tests.Unit.Policy;

using Capacitor.Cli.Core.Policy;

public class LexicalPathsTests {
    [Test]
    [Arguments("/repo", "src/a.cs", "/repo/src/a.cs")]
    [Arguments("/repo", "./src/../a.cs", "/repo/a.cs")]
    [Arguments("/repo", "/abs/./b/../x", "/abs/x")]
    [Arguments("/repo/sub", "../a", "/repo/a")]
    [Arguments("/repo", "../../../etc/passwd", "/etc/passwd")]
    [Arguments(null, "/abs/x", "/abs/x")]
    public async Task Resolves_lexically(string? cwd, string path, string expected) =>
        await Assert.That(LexicalPaths.TryResolve(cwd, path)).IsEqualTo(expected);

    [Test]
    public async Task Relative_path_without_cwd_is_unresolvable() =>
        await Assert.That(LexicalPaths.TryResolve(null, "src/a.cs")).IsNull();
}
```

```csharp
namespace Capacitor.Cli.Core.Tests.Unit.Harness.Claude;

using System.Text.Json;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Policy;

public class ClaudeActionNormalizerTests {
    static JsonElement Input(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Test]
    public async Task Bash_maps_to_shell_with_analysis() {
        var a = ClaudeActionNormalizer.Normalize("Bash", Input("""{"command":"git status"}"""), "/repo");
        await Assert.That(a.Kind).IsEqualTo(ActionKind.Shell);
        await Assert.That(a.Analyzed).IsTrue();
        await Assert.That(a.Segments[0].Argv).IsEquivalentTo(new[] { "git", "status" });
        await Assert.That(a.Vendor).IsEqualTo("claude");
    }

    [Test]
    public async Task Bash_without_command_falls_to_other() {
        var a = ClaudeActionNormalizer.Normalize("Bash", Input("{}"), "/repo");
        await Assert.That(a.Kind).IsEqualTo(ActionKind.Other);
        await Assert.That(a.RawToolName).IsEqualTo("Bash");
    }

    [Test]
    [Arguments("Edit", ActionKind.FileEdit)]
    [Arguments("Write", ActionKind.FileEdit)]
    [Arguments("MultiEdit", ActionKind.FileEdit)]
    [Arguments("Read", ActionKind.FileRead)]
    public async Task File_tools_resolve_file_path(string tool, ActionKind kind) {
        var a = ClaudeActionNormalizer.Normalize(tool, Input("""{"file_path":"src/x.cs"}"""), "/repo");
        await Assert.That(a.Kind).IsEqualTo(kind);
        await Assert.That(a.Paths).IsEquivalentTo(new[] { "/repo/src/x.cs" });
    }

    [Test]
    public async Task Grep_defaults_its_path_to_cwd() {
        var a = ClaudeActionNormalizer.Normalize("Grep", Input("""{"pattern":"x"}"""), "/repo");
        await Assert.That(a.Kind).IsEqualTo(ActionKind.FileRead);
        await Assert.That(a.Paths).IsEquivalentTo(new[] { "/repo" });
    }

    [Test]
    public async Task WebFetch_normalizes_the_host() {
        var a = ClaudeActionNormalizer.Normalize("WebFetch", Input("""{"url":"https://EXAMPLE.com:8443/x"}"""), null);
        await Assert.That(a.Kind).IsEqualTo(ActionKind.Network);
        await Assert.That(a.Host).IsEqualTo("example.com");
        await Assert.That(a.Port).IsEqualTo(8443);
    }

    [Test]
    public async Task Mcp_tool_names_split_on_the_second_separator() {
        var a = ClaudeActionNormalizer.Normalize("mcp__kcap-flows__start_review_flow", Input("{}"), null);
        await Assert.That(a.Kind).IsEqualTo(ActionKind.McpTool);
        await Assert.That(a.Server).IsEqualTo("kcap-flows");
        await Assert.That(a.Tool).IsEqualTo("start_review_flow");
    }

    [Test]
    public async Task Unknown_tools_and_null_input_are_governable_as_other() {
        var a = ClaudeActionNormalizer.Normalize("TodoWrite", null, null);
        await Assert.That(a.Kind).IsEqualTo(ActionKind.Other);
        await Assert.That(a.RawToolName).IsEqualTo("TodoWrite");
        var nameless = ClaudeActionNormalizer.Normalize(null, null, null);
        await Assert.That(nameless.Kind).IsEqualTo(ActionKind.Other);
        await Assert.That(PolicyComponents.RestrictionOf(nameless)).IsNotEmpty();
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter '/*/*/ClaudeActionNormalizerTests/*'`
Expected: build failure.

- [ ] **Step 3: Implement**

`LexicalPaths.cs`:

```csharp
namespace Capacitor.Cli.Core.Policy;

public static class LexicalPaths {
    public static string? TryResolve(string? cwd, string? path) {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string joined;
        if (Path.IsPathRooted(path)) joined = path;
        else if (string.IsNullOrWhiteSpace(cwd) || !Path.IsPathRooted(cwd)) return null;
        else joined = cwd + "/" + path;
        var parts = joined.Replace('\\', '/').Split('/');
        var stack = new List<string>();
        var root = parts[0].Length == 0 ? "" : parts[0];   // "" for unix-absolute, "C:" for a drive
        foreach (var part in parts.Skip(1)) {
            if (part is "" or ".") continue;
            if (part == "..") { if (stack.Count > 0) stack.RemoveAt(stack.Count - 1); }
            else stack.Add(part);
        }
        return root + "/" + string.Join('/', stack);
    }
}
```

`ClaudeActionNormalizer.cs` (namespace `Capacitor.Cli.Core.Harness.Claude`):

```csharp
namespace Capacitor.Cli.Core.Harness.Claude;

using System.Text.Json;
using Capacitor.Cli.Core.Policy;

public static class ClaudeActionNormalizer {
    const string Vendor = "claude";

    public static CanonicalAction Normalize(string? toolName, JsonElement? toolInput, string? cwd) {
        try { return NormalizeCore(toolName, toolInput, cwd); }
        catch { return Other(toolName, toolInput, cwd); }
    }

    static CanonicalAction NormalizeCore(string? toolName, JsonElement? toolInput, string? cwd) {
        switch (toolName) {
            case "Bash": {
                if (toolInput?.Str("command") is not { Length: > 0 } command) break;
                var analysis = ShellCommandAnalyzer.Analyze(command);
                return new() {
                    Kind = ActionKind.Shell, Vendor = Vendor, Cwd = cwd,
                    Command = command, Analyzed = analysis.Analyzed, Segments = analysis.Segments,
                };
            }
            case "Edit" or "Write" or "MultiEdit":
                return FileAction(ActionKind.FileEdit, toolInput?.Str("file_path"), toolName, toolInput, cwd);
            case "NotebookEdit":
                return FileAction(ActionKind.FileEdit, toolInput?.Str("notebook_path"), toolName, toolInput, cwd);
            case "Read":
                return FileAction(ActionKind.FileRead, toolInput?.Str("file_path"), toolName, toolInput, cwd);
            case "Glob" or "Grep":
                return FileAction(ActionKind.FileRead, toolInput?.Str("path") ?? cwd, toolName, toolInput, cwd);
            case "WebFetch": {
                if (toolInput?.Str("url") is not { Length: > 0 } url) break;
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.IdnHost.Length == 0) break;
                return new() {
                    Kind = ActionKind.Network, Vendor = Vendor, Cwd = cwd, Url = url,
                    Host = uri.IdnHost.ToLowerInvariant(), Port = uri.IsDefaultPort ? null : uri.Port,
                };
            }
            default: {
                if (toolName is not null && toolName.StartsWith("mcp__", StringComparison.Ordinal)) {
                    var rest = toolName["mcp__".Length..];
                    var split = rest.IndexOf("__", StringComparison.Ordinal);
                    if (split > 0 && split + 2 < rest.Length)
                        return new() {
                            Kind = ActionKind.McpTool, Vendor = Vendor, Cwd = cwd,
                            Server = rest[..split], Tool = rest[(split + 2)..],
                        };
                }
                break;
            }
        }
        return Other(toolName, toolInput, cwd);
    }

    static CanonicalAction FileAction(ActionKind kind, string? path, string? toolName, JsonElement? toolInput, string? cwd) =>
        LexicalPaths.TryResolve(cwd, path) is { } resolved
            ? new() { Kind = kind, Vendor = Vendor, Cwd = cwd, Paths = [resolved] }
            : Other(toolName, toolInput, cwd);

    static CanonicalAction Other(string? toolName, JsonElement? toolInput, string? cwd) => new() {
        Kind = ActionKind.Other, Vendor = Vendor, Cwd = cwd,
        RawToolName = string.IsNullOrEmpty(toolName) ? null : toolName,
        RawPayloadJson = toolInput?.GetRawText(),
    };
}
```

- [ ] **Step 4: Run both test classes to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter '/*/*/LexicalPathsTests/*'` and `.../ClaudeActionNormalizerTests/*'`.
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/Policy/LexicalPaths.cs src/Capacitor.Cli.Core/Harness/Claude/ClaudeActionNormalizer.cs test/Capacitor.Cli.Core.Tests.Unit/Policy/LexicalPathsTests.cs test/Capacitor.Cli.Core.Tests.Unit/Harness/Claude/ClaudeActionNormalizerTests.cs
git commit -m "Normalize Claude tool payloads into canonical actions (#738)"
```

---

### Task 10: Decision journal and input hash

**Files:**
- Create: `src/Capacitor.Cli.Core/Policy/PolicyInputHash.cs`
- Create: `src/Capacitor.Cli.Core/Policy/PolicyDecisionJournal.cs`
- Modify: `src/Capacitor.Cli.Core/Policy/PolicyJsonContext.cs` (add the `PolicyJournalFileV1` registration)
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Policy/PolicyDecisionJournalTests.cs`

**Interfaces:**
- Consumes: `ConfigRoot`, `ConfigFileLock.Acquire(string configPath, TimeSpan?)` (existing).
- Produces:

```csharp
public static class PolicyInputHash {
    // Key-order-insensitive digest of (tool_name, tool_input): the same call re-presented at a
    // later seam must hash identically even if the vendor reserializes the object.
    public static string Compute(string? toolName, JsonElement? toolInput);
}
public readonly record struct PolicyJournalConsume(bool PendingAsk, string? ExactOutcome, bool Ambiguous);
public sealed class PolicyDecisionJournal(ConfigRoot config) {
    public void RecordAsk(string sessionKey, string? callId, string inputHash);
    public void RecordTerminal(string sessionKey, string callId, string outcome, string inputHash); // exact mode only
    public PolicyJournalConsume Consume(string sessionKey, string? callId, string inputHash);
    public void IncrementPassThrough(string sessionKey);
    public long TakePassThroughCount(string sessionKey);   // read + reset, for the session-end stamp
    public void ClearTurn(string sessionKey);              // wired to the Stop hook in Task 13
}
```

Semantics (spec "The per-call decision journal", normative):
- **With a vendor call id** every terminal decision journals under that id; `Consume(callId)` returns `(PendingAsk: outcome == "ask", ExactOutcome, Ambiguous: false)` and removes the entry.
- **Without a call id** only asks journal, in a FIFO per `(session, input hash)`; `Consume` pops the head for that hash and returns `(true, null, Ambiguous: true)`. Consume **never replaces evaluation** — the caller always evaluates fresh and aggregates most-restrictively (Tasks 12/14 implement the aggregation).
- Entries are consume-once; `ClearTurn` empties both structures (turn expiry — Claude's `Stop` hook marks the turn end). The pass-through counter survives `ClearTurn`.
- Every method takes the cross-process `ConfigFileLock` on the journal path — Claude runs parallel tool calls, so two PreToolUse hook processes can interleave.
- Every method is fail-open on I/O (a journal failure must never break a hook): catch `IOException`/`UnauthorizedAccessException` and return the zero value.

- [ ] **Step 1: Write the failing tests**

```csharp
namespace Capacitor.Cli.Core.Tests.Unit.Policy;

using Capacitor.Cli.Core.Policy;

public class PolicyDecisionJournalTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    PolicyDecisionJournal Journal => new(Config.Root);
    const string Sid = "abc123";

    [Test]
    public async Task Fallback_ask_is_fifo_per_input_hash_and_consume_once() {
        Journal.RecordAsk(Sid, callId: null, inputHash: "h1");
        Journal.RecordAsk(Sid, callId: null, inputHash: "h1");
        Journal.RecordAsk(Sid, callId: null, inputHash: "h2");
        var first = Journal.Consume(Sid, callId: null, inputHash: "h1");
        await Assert.That(first.PendingAsk).IsTrue();
        await Assert.That(first.Ambiguous).IsTrue();
        await Assert.That(Journal.Consume(Sid, null, "h1").PendingAsk).IsTrue();
        await Assert.That(Journal.Consume(Sid, null, "h1").PendingAsk).IsFalse();
        await Assert.That(Journal.Consume(Sid, null, "h2").PendingAsk).IsTrue();
    }

    [Test]
    public async Task Exact_mode_journals_all_terminals_with_exact_provenance() {
        Journal.RecordTerminal(Sid, "call-1", "deny", "h1");
        Journal.RecordAsk(Sid, "call-2", "h2");
        var deny = Journal.Consume(Sid, "call-1", "h1");
        await Assert.That(deny.ExactOutcome).IsEqualTo("deny");
        await Assert.That(deny.Ambiguous).IsFalse();
        await Assert.That(deny.PendingAsk).IsFalse();
        var ask = Journal.Consume(Sid, "call-2", "h2");
        await Assert.That(ask.PendingAsk).IsTrue();
        await Assert.That(ask.Ambiguous).IsFalse();
        await Assert.That(Journal.Consume(Sid, "call-1", "h1").ExactOutcome).IsNull();
    }

    [Test]
    public async Task Unknown_call_id_with_no_pending_hash_is_an_ordinary_fresh_call() {
        var r = Journal.Consume(Sid, "never-seen", "h9");
        await Assert.That(r).IsEqualTo(default(PolicyJournalConsume));
    }

    [Test]
    public async Task Clear_turn_expires_entries_but_keeps_the_pass_through_count() {
        Journal.RecordAsk(Sid, null, "h1");
        Journal.IncrementPassThrough(Sid);
        Journal.IncrementPassThrough(Sid);
        Journal.ClearTurn(Sid);
        await Assert.That(Journal.Consume(Sid, null, "h1").PendingAsk).IsFalse();
        await Assert.That(Journal.TakePassThroughCount(Sid)).IsEqualTo(2);
        await Assert.That(Journal.TakePassThroughCount(Sid)).IsEqualTo(0);
    }

    [Test]
    public async Task Sessions_are_isolated() {
        Journal.RecordAsk("s1", null, "h1");
        await Assert.That(Journal.Consume("s2", null, "h1").PendingAsk).IsFalse();
        await Assert.That(Journal.Consume("s1", null, "h1").PendingAsk).IsTrue();
    }
}

public class PolicyInputHashTests {
    static JsonElement Input(string json) => System.Text.Json.JsonDocument.Parse(json).RootElement.Clone();

    [Test]
    public async Task Key_order_does_not_change_the_hash() {
        var a = PolicyInputHash.Compute("Bash", Input("""{"command":"ls","description":"x"}"""));
        var b = PolicyInputHash.Compute("Bash", Input("""{"description":"x","command":"ls"}"""));
        await Assert.That(a).IsEqualTo(b);
    }

    [Test]
    public async Task Tool_name_and_values_do() {
        var a = PolicyInputHash.Compute("Bash", Input("""{"command":"ls"}"""));
        await Assert.That(PolicyInputHash.Compute("Edit", Input("""{"command":"ls"}"""))).IsNotEqualTo(a);
        await Assert.That(PolicyInputHash.Compute("Bash", Input("""{"command":"rm"}"""))).IsNotEqualTo(a);
        await Assert.That(PolicyInputHash.Compute("Bash", null)).IsNotEqualTo(a);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter '/*/*/PolicyDecisionJournalTests/*'`
Expected: build failure.

- [ ] **Step 3: Implement**

`PolicyInputHash.cs` — canonicalize like `McpFingerprint.Compute` (sorted keys, ordinal, no whitespace), then domain-separate the tool name with the length-prefixed pattern from `SessionStartMemoryIdentity`:

```csharp
namespace Capacitor.Cli.Core.Policy;

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

public static class PolicyInputHash {
    public static string Compute(string? toolName, JsonElement? toolInput) {
        using var ms = new MemoryStream();
        void Write(string s) {
            var bytes = Encoding.UTF8.GetBytes(s);
            Span<byte> len = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(len, (uint)bytes.Length);
            ms.Write(len);
            ms.Write(bytes);
        }
        Write(toolName ?? "");
        Write(toolInput is { } el ? Canonical(el) : "");
        return Convert.ToHexStringLower(SHA256.HashData(ms.ToArray()));
    }

    static string Canonical(JsonElement el) {
        var sb = new StringBuilder();
        Append(sb, el);
        return sb.ToString();
    }

    static void Append(StringBuilder sb, JsonElement el) {
        switch (el.ValueKind) {
            case JsonValueKind.Object:
                sb.Append('{');
                var first = true;
                foreach (var p in el.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)) {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(JsonSerializer.Serialize(p.Name)).Append(':');
                    Append(sb, p.Value);
                }
                sb.Append('}');
                break;
            case JsonValueKind.Array:
                sb.Append('[');
                var firstItem = true;
                foreach (var item in el.EnumerateArray()) {
                    if (!firstItem) sb.Append(',');
                    firstItem = false;
                    Append(sb, item);
                }
                sb.Append(']');
                break;
            default:
                sb.Append(el.GetRawText());
                break;
        }
    }
}
```

(`JsonSerializer.Serialize(p.Name)` on a `string` needs a registered type — use `JsonEncodedText.Encode(p.Name)` wrapped in quotes instead to stay AOT-clean: `sb.Append('"').Append(JsonEncodedText.Encode(p.Name).EncodedValue).Append('"')`.)

`PolicyDecisionJournal.cs`:

```csharp
namespace Capacitor.Cli.Core.Policy;

using System.Text.Json;

sealed record PolicyJournalFileV1(
    [property: System.Text.Json.Serialization.JsonPropertyName("pending_asks")] List<PolicyJournalAskV1> PendingAsks,
    [property: System.Text.Json.Serialization.JsonPropertyName("by_call_id")] List<PolicyJournalCallV1> ByCallId,
    [property: System.Text.Json.Serialization.JsonPropertyName("pass_through_count")] long PassThroughCount);
sealed record PolicyJournalAskV1(
    [property: System.Text.Json.Serialization.JsonPropertyName("input_hash")] string InputHash);
sealed record PolicyJournalCallV1(
    [property: System.Text.Json.Serialization.JsonPropertyName("call_id")] string CallId,
    [property: System.Text.Json.Serialization.JsonPropertyName("outcome")] string Outcome,
    [property: System.Text.Json.Serialization.JsonPropertyName("input_hash")] string InputHash);

public readonly record struct PolicyJournalConsume(bool PendingAsk, string? ExactOutcome, bool Ambiguous);

/// <summary>
/// Per-session decision journal shared by hook processes. With a vendor call id, terminal
/// decisions correlate exactly; without one, only asks journal (FIFO per input hash) so a
/// stale entry can cost at most one extra human prompt and can never weaken an outcome.
/// </summary>
public sealed class PolicyDecisionJournal(ConfigRoot config) {
    string PathFor(string sessionKey) => config.Path("policy", "journal", $"{sessionKey}.json");

    public void RecordAsk(string sessionKey, string? callId, string inputHash) => Mutate(sessionKey, f =>
        callId is { Length: > 0 }
            ? f with { ByCallId = [.. f.ByCallId, new(callId, "ask", inputHash)] }
            : f with { PendingAsks = [.. f.PendingAsks, new(inputHash)] });

    public void RecordTerminal(string sessionKey, string callId, string outcome, string inputHash) =>
        Mutate(sessionKey, f => f with { ByCallId = [.. f.ByCallId, new(callId, outcome, inputHash)] });

    public PolicyJournalConsume Consume(string sessionKey, string? callId, string inputHash) {
        PolicyJournalConsume result = default;
        Mutate(sessionKey, f => {
            if (callId is { Length: > 0 } && f.ByCallId.FirstOrDefault(e => e.CallId == callId) is { } exact) {
                result = new(exact.Outcome == "ask", exact.Outcome, Ambiguous: false);
                return f with { ByCallId = [.. f.ByCallId.Where(e => e.CallId != callId)] };
            }
            var head = f.PendingAsks.FirstOrDefault(e => e.InputHash == inputHash);
            if (head is null) return f;
            result = new(PendingAsk: true, ExactOutcome: null, Ambiguous: true);
            var remaining = new List<PolicyJournalAskV1>(f.PendingAsks);
            remaining.Remove(head);
            return f with { PendingAsks = remaining };
        });
        return result;
    }

    public void IncrementPassThrough(string sessionKey) =>
        Mutate(sessionKey, f => f with { PassThroughCount = f.PassThroughCount + 1 });

    public long TakePassThroughCount(string sessionKey) {
        long count = 0;
        Mutate(sessionKey, f => { count = f.PassThroughCount; return f with { PassThroughCount = 0 }; });
        return count;
    }

    public void ClearTurn(string sessionKey) =>
        Mutate(sessionKey, f => f with { PendingAsks = [], ByCallId = [] });

    void Mutate(string sessionKey, Func<PolicyJournalFileV1, PolicyJournalFileV1> transform) {
        try {
            var path = PathFor(sessionKey);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var _ = ConfigFileLock.Acquire(path, TimeSpan.FromSeconds(5));
            var current = Read(path);
            var next = transform(current);
            if (ReferenceEquals(next, current)) return;
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(next, PolicyJsonContext.Default.PolicyJournalFileV1));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or TimeoutException) { }
    }

    static PolicyJournalFileV1 Read(string path) {
        try {
            if (File.Exists(path)
                && JsonSerializer.Deserialize(File.ReadAllText(path), PolicyJsonContext.Default.PolicyJournalFileV1) is { } f)
                return f;
        }
        catch (JsonException) { }
        return new([], [], 0);
    }
}
```

Note: `Consume` must run its transform even when it only reads (the lock guards the read-modify decision), which is why it goes through `Mutate` and returns via the captured local. Check `ConfigFileLock.Acquire`'s timeout exception type in `src/Capacitor.Cli.Core/ConfigFileLock.cs` and match the catch. The session key here is the dashless Claude session id, already validated upstream; reuse `PolicySnapshotStore`'s `Sanitize` (make it `internal static` there) if the compiler pass shows any path where an unvalidated id can reach the journal.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter '/*/*/PolicyDecisionJournalTests/*'` and `.../PolicyInputHashTests/*'`.
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/Policy/PolicyInputHash.cs src/Capacitor.Cli.Core/Policy/PolicyDecisionJournal.cs src/Capacitor.Cli.Core/Policy/PolicyJsonContext.cs test/Capacitor.Cli.Core.Tests.Unit/Policy/PolicyDecisionJournalTests.cs
git commit -m "Journal policy decisions per session with an ask-only fallback (#738)"
```

---

### Task 11: Wire DTOs and the CLI decision emitter

**Files:**
- Create: `src/Capacitor.Cli.Core/Policy/PolicyWire.cs`
- Modify: `src/Capacitor.Cli.Core/Models.cs` (two `[JsonSerializable]` lines on `CapacitorJsonContext`)
- Create: `src/Capacitor.Cli/Policy/PolicyDecisionEmitter.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Policy/PolicyWireTests.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/Policy/PolicyDecisionEmitterTests.cs`

**Interfaces:**
- Consumes: `CanonicalAction`, `PolicyEvaluation`, `MatchedRuleRef`, `PolicySnapshot`, `PolicyEngine.Version`; existing `AgentHookPoster` (`PostOrSpoolAsync(string endpoint, string body, string agentTag, HookSpool spool, string sessionId, string route)` → `HookPostOutcome`), `HookSpool(ConfigRoot)`.
- Produces:

```csharp
public static class PolicySeams {
    public const string ClaudePreToolUse = "claude_pre_tool_use";
    public const string ClaudePermissionRequest = "claude_permission_request";
    public const string HostedClaudePermission = "hosted_claude_permission";
    public const string AcpRequestPermission = "acp_request_permission";
}
public sealed record PolicyActionV1(
    string Kind, string Vendor, string? Command, bool Analyzed, string[][]? Segments,
    string[]? Paths, string? Host, int? Port, string? Url, string? Server, string? Tool,
    string? RawToolName, string? RawPayload, bool RawPayloadTruncated, string? Justification);
public sealed record PolicyMatchedRuleV1(string Scope, int RuleIndex, string Outcome, string? Reason);
public sealed record PolicyDecisionEventV1(
    string SessionId, string? AgentId, string Vendor, string Seam, string SnapshotId, string EngineVersion,
    string EvaluationMode, string RequestedOutcome, string EffectiveOutcome, PolicyActionV1 Action,
    PolicyMatchedRuleV1[] MatchedRules, bool Degraded, string? FailureClass,
    string? CorrelationId, bool CorrelationAmbiguous, string DecidedAt);
public sealed record PolicySnapshotUploadV1(
    string SessionId, string SnapshotId, string EngineVersion, bool Degraded, string[] Degradations,
    PolicySnapshotDocV1[] Documents);
public sealed record PolicySnapshotDocV1(string Scope, string SourcePath, string Content);
public static class PolicyWire {
    public const int MaxRawPayloadBytes = 16 * 1024;
    public static PolicyActionV1 ToWire(CanonicalAction action);
    public static PolicyMatchedRuleV1[] ToWire(IReadOnlyList<MatchedRuleRef> rules);
    public static PolicySnapshotUploadV1 ToUpload(string sessionId, PolicySnapshot snapshot);
}
// src/Capacitor.Cli/Policy/ — namespace Capacitor.Cli.Policy
internal sealed class PolicyDecisionEmitter(ConfigRoot config, ProfileContext profiles) {
    public Task EmitAsync(PolicyDecisionEventV1 evt, PolicySnapshot snapshot);   // snapshot upload first (once), then decision
}
```

Both records go on `CapacitorJsonContext` (snake_case + string enums, which is the server's `/hooks/*` convention): add `[JsonSerializable(typeof(PolicyDecisionEventV1))]` and `[JsonSerializable(typeof(PolicySnapshotUploadV1))]` to the attribute stack in `Models.cs` — nested property types (`PolicyActionV1`, `string[][]`, …) are reached automatically by the source generator. Routes are `policy-decision` and `policy-snapshot` (POST `{Url}/hooks/{route}`; server-side ingestion is kcap-server work tracked in Linear — until it lands the server returns 404, which `PostOrSpoolAsync` classifies as a permanent non-2xx `Failed`, so nothing accumulates against an old server).

The emitter uploads the snapshot at most once per (session, snapshot) using a marker file `config.Path("policy", "uploaded", $"{sessionId}-{snapshotId[..16]}")` (the `CursorMarkers` pattern), written when the outcome is `Posted` or `Spooled`. Decision events always go through `PostOrSpoolAsync` so offline sessions drain later via the existing global spool drain. The emitter never throws — a recording failure must not break a hook.

- [ ] **Step 1: Write the failing tests**

`PolicyWireTests` (Core):

```csharp
namespace Capacitor.Cli.Core.Tests.Unit.Policy;

using Capacitor.Cli.Core.Policy;

public class PolicyWireTests {
    [Test]
    public async Task Raw_payload_is_capped_with_a_visible_flag() {
        var big = new string('x', 20_000);
        var a = new CanonicalAction {
            Kind = ActionKind.Other, Vendor = "claude", RawToolName = "T",
            RawPayloadJson = $"{{\"v\":\"{big}\"}}",
        };
        var wire = PolicyWire.ToWire(a);
        await Assert.That(wire.RawPayloadTruncated).IsTrue();
        await Assert.That(wire.RawPayload!.Length).IsLessThanOrEqualTo(PolicyWire.MaxRawPayloadBytes);
    }

    [Test]
    public async Task Segments_round_trip_as_string_arrays() {
        var analysis = ShellCommandAnalyzer.Analyze("git status && git diff");
        var a = new CanonicalAction {
            Kind = ActionKind.Shell, Vendor = "claude", Command = "git status && git diff",
            Analyzed = true, Segments = analysis.Segments,
        };
        var wire = PolicyWire.ToWire(a);
        await Assert.That(wire.Segments!.Length).IsEqualTo(2);
        await Assert.That(wire.Segments[1]).IsEquivalentTo(new[] { "git", "diff" });
    }

    [Test]
    public async Task Decision_event_serializes_snake_case_on_the_shared_context() {
        var evt = new PolicyDecisionEventV1(
            "sid", null, "claude", PolicySeams.ClaudePreToolUse, "snap", PolicyEngine.Version,
            "full", "deny", "deny",
            PolicyWire.ToWire(new CanonicalAction { Kind = ActionKind.Other, Vendor = "claude", RawToolName = "T" }),
            [new PolicyMatchedRuleV1("User", 0, "deny", null)],
            Degraded: false, FailureClass: null, CorrelationId: null, CorrelationAmbiguous: false,
            DecidedAt: "2026-09-02T00:00:00Z");
        var json = System.Text.Json.JsonSerializer.Serialize(evt, CapacitorJsonContext.Default.PolicyDecisionEventV1);
        await Assert.That(json).Contains("\"session_id\":\"sid\"");
        await Assert.That(json).Contains("\"requested_outcome\":\"deny\"");
        await Assert.That(json).Contains("\"engine_version\"");
    }
}
```

`PolicyDecisionEmitterTests` (CLI — follow the WireMock + `Resolutions.Of` fixture shape used by `AgentHookPosterTests` / `ClaudeHookCommandTests`; adapt the profile-context construction to whatever `Resolutions.Of(profile, serverUrl:)` requires there):

```csharp
namespace Capacitor.Cli.Tests.Unit.Policy;

using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Policy;
using Capacitor.Cli.Policy;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

public class PolicyDecisionEmitterTests : IDisposable {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    readonly WireMockServer _server = WireMockServer.Start();
    public void Dispose() => _server.Stop();

    PolicyDecisionEmitter Emitter => new(Config.Root, Resolutions.Of(new Profile(), serverUrl: _server.Url!));

    static PolicySnapshot Snapshot => new("snap1", [
        new PolicyScopeDocument(PolicyScope.User, "/u/approvals.yaml", "version: 1\n",
            PolicyDocumentBinder.Bind("version: 1\n", PolicyScope.User))], false, []);

    static PolicyDecisionEventV1 Event(string sid) => new(
        sid, null, "claude", PolicySeams.ClaudePreToolUse, "snap1", PolicyEngine.Version,
        "full", "deny", "deny",
        PolicyWire.ToWire(new CanonicalAction { Kind = ActionKind.Other, Vendor = "claude", RawToolName = "T" }),
        [], false, null, null, false, "2026-09-02T00:00:00Z");

    [Test]
    public async Task Uploads_snapshot_once_then_posts_each_decision() {
        _server.Given(Request.Create().UsingPost()).RespondWith(Response.Create().WithStatusCode(200));
        await Emitter.EmitAsync(Event("s1"), Snapshot);
        await Emitter.EmitAsync(Event("s1"), Snapshot);
        var snapshots = _server.FindLogEntries(Request.Create().WithPath("/hooks/policy-snapshot").UsingPost());
        var decisions = _server.FindLogEntries(Request.Create().WithPath("/hooks/policy-decision").UsingPost());
        await Assert.That(snapshots.Count).IsEqualTo(1);
        await Assert.That(decisions.Count).IsEqualTo(2);
        var body = JsonNode.Parse(decisions[0].RequestMessage.Body!)!;
        await Assert.That(body["session_id"]!.GetValue<string>()).IsEqualTo("s1");
        await Assert.That(body["seam"]!.GetValue<string>()).IsEqualTo("claude_pre_tool_use");
    }

    [Test]
    public async Task Server_outage_spools_instead_of_losing_the_decision() {
        _server.Given(Request.Create().UsingPost()).RespondWith(Response.Create().WithStatusCode(503));
        await Emitter.EmitAsync(Event("s2"), Snapshot);
        await Assert.That(new HookSpool(Config.Root).HasBacklog("s2")).IsTrue();
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter '/*/*/PolicyWireTests/*'`
Expected: build failure.

- [ ] **Step 3: Implement**

`PolicyWire.cs` (Core):

```csharp
namespace Capacitor.Cli.Core.Policy;

using System.Text;

public static class PolicySeams {
    public const string ClaudePreToolUse = "claude_pre_tool_use";
    public const string ClaudePermissionRequest = "claude_permission_request";
    public const string HostedClaudePermission = "hosted_claude_permission";
    public const string AcpRequestPermission = "acp_request_permission";
}

public sealed record PolicyActionV1(
    string Kind, string Vendor, string? Command, bool Analyzed, string[][]? Segments,
    string[]? Paths, string? Host, int? Port, string? Url, string? Server, string? Tool,
    string? RawToolName, string? RawPayload, bool RawPayloadTruncated, string? Justification);

public sealed record PolicyMatchedRuleV1(string Scope, int RuleIndex, string Outcome, string? Reason);

public sealed record PolicyDecisionEventV1(
    string SessionId, string? AgentId, string Vendor, string Seam, string SnapshotId, string EngineVersion,
    string EvaluationMode, string RequestedOutcome, string EffectiveOutcome, PolicyActionV1 Action,
    PolicyMatchedRuleV1[] MatchedRules, bool Degraded, string? FailureClass,
    string? CorrelationId, bool CorrelationAmbiguous, string DecidedAt);

public sealed record PolicySnapshotUploadV1(
    string SessionId, string SnapshotId, string EngineVersion, bool Degraded, string[] Degradations,
    PolicySnapshotDocV1[] Documents);

public sealed record PolicySnapshotDocV1(string Scope, string SourcePath, string Content);

public static class PolicyWire {
    public const int MaxRawPayloadBytes = 16 * 1024;

    public static PolicyActionV1 ToWire(CanonicalAction a) {
        var raw = a.RawPayloadJson;
        var truncated = false;
        if (raw is not null && Encoding.UTF8.GetByteCount(raw) > MaxRawPayloadBytes) {
            raw = raw[..Math.Min(raw.Length, MaxRawPayloadBytes)];
            truncated = true;
        }
        return new(
            a.Kind.ToString(), a.Vendor, a.Command, a.Analyzed,
            a.Kind is ActionKind.Shell && a.Analyzed ? [.. a.Segments.Select(s => s.Argv.ToArray())] : null,
            a.Paths.Count > 0 ? [.. a.Paths] : null,
            a.Host, a.Port, a.Url, a.Server, a.Tool, a.RawToolName, raw, truncated, a.Justification);
    }

    public static PolicyMatchedRuleV1[] ToWire(IReadOnlyList<MatchedRuleRef> rules) =>
        [.. rules.Select(r => new PolicyMatchedRuleV1(r.Scope.ToString(), r.RuleIndex, r.Outcome.ToString().ToLowerInvariant(), r.Reason))];

    public static PolicySnapshotUploadV1 ToUpload(string sessionId, PolicySnapshot snapshot) => new(
        sessionId, snapshot.Id, PolicyEngine.Version, snapshot.Degraded, [.. snapshot.Degradations],
        [.. snapshot.Documents.Select(d => new PolicySnapshotDocV1(d.Scope.ToString(), d.SourcePath, d.Content))]);
}
```

`PolicyDecisionEmitter.cs` (CLI, namespace `Capacitor.Cli.Policy`):

```csharp
namespace Capacitor.Cli.Policy;

using System.Text.Json;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Policy;

internal sealed class PolicyDecisionEmitter(ConfigRoot config, ProfileContext profiles) {
    public async Task EmitAsync(PolicyDecisionEventV1 evt, PolicySnapshot snapshot) {
        try {
            var spool = new HookSpool(config);
            var poster = new AgentHookPoster(config, profiles);
            await EnsureSnapshotUploadedAsync(poster, spool, evt.SessionId, snapshot);
            var body = JsonSerializer.Serialize(evt, CapacitorJsonContext.Default.PolicyDecisionEventV1);
            await poster.PostOrSpoolAsync("policy-decision", body, evt.Vendor, spool, evt.SessionId, "policy-decision");
        }
        catch { }
    }

    async Task EnsureSnapshotUploadedAsync(AgentHookPoster poster, HookSpool spool, string sessionId, PolicySnapshot snapshot) {
        var marker = config.Path("policy", "uploaded", $"{sessionId}-{snapshot.Id[..Math.Min(16, snapshot.Id.Length)]}");
        if (File.Exists(marker)) return;
        var body = JsonSerializer.Serialize(PolicyWire.ToUpload(sessionId, snapshot),
            CapacitorJsonContext.Default.PolicySnapshotUploadV1);
        var outcome = await poster.PostOrSpoolAsync("policy-snapshot", body, "policy", spool, sessionId, "policy-snapshot");
        if (outcome is HookPostOutcome.Posted or HookPostOutcome.Spooled) {
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            File.WriteAllText(marker, "");
        }
    }
}
```

Adjust `using`/namespace details to where `AgentHookPoster`, `ProfileContext`, and `HookSpool` actually live (`AgentHookPoster` is `Capacitor.Cli.Commands`; `HookSpool` and `CapacitorJsonContext` are Core). Add the two `[JsonSerializable]` lines in `Models.cs` next to the existing stack.

- [ ] **Step 4: Run to verify pass**

Run both suites' new classes:
`dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter '/*/*/PolicyWireTests/*'`
`dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter '/*/*/PolicyDecisionEmitterTests/*'`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/Policy/PolicyWire.cs src/Capacitor.Cli.Core/Models.cs src/Capacitor.Cli/Policy/PolicyDecisionEmitter.cs test/Capacitor.Cli.Core.Tests.Unit/Policy/PolicyWireTests.cs test/Capacitor.Cli.Tests.Unit/Policy/PolicyDecisionEmitterTests.cs
git commit -m "Emit policy decisions and snapshots over the hook spool lane (#738)"
```

---

### Task 12: The PreToolUse seam

**Files:**
- Create: `src/Capacitor.Cli/Harness/Claude/ClaudePolicySeam.cs`
- Modify: `src/Capacitor.Cli/Commands/Harness/ClaudeHookCommand.cs` (route `pre-tool-use`)
- Modify: `kcap/hooks/hooks.json` (PreToolUse entry)
- Modify: `kcap/.claude-plugin/plugin.json` (version bump so the plugin update propagates the new hook)
- Test: `test/Capacitor.Cli.Tests.Unit/Harness/Claude/ClaudePolicySeamTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 7–11; existing `GitRepository.FindRoot(string startDir)`; `ClaudeHookCommand.HandleCore`'s routing pattern (the `permission-request` branch is the model: gate on session exclusion, then delegate with the raw `body`).
- Produces:

```csharp
// namespace Capacitor.Cli.Harness.Claude
internal sealed class ClaudePolicySeam(ConfigRoot config, ProfileContext profiles) {
    // True until Task 20 certifies the forced-prompt round trip; if certification fails, flip to
    // false and PreToolUse ask degrades to pass-through (requested/effective both recorded).
    internal const bool PreToolUseAskEnabled = true;
    public Task<int> HandlePreToolUseAsync(string body, string sessionId, bool renderedAgent, TextWriter stdout);
    // Task 14 adds: HandlePermissionRequestAsync(...)
    internal static string BuildPreToolUseDecision(string decision, string? reason);
}
```

Flow of `HandlePreToolUseAsync` (spec "Seams" + "per-call decision journal"):
1. Parse `body`; extract `tool_name`, `tool_input` (as `JsonElement` via `JsonDocument.Parse(node["tool_input"]!.ToJsonString())` when present), `cwd`, `tool_use_id` (the call id — present or not, both paths work; Task 20 certifies which one Claude actually takes).
2. `snapshot = new PolicySnapshotStore(config).LoadOrBuild(sessionId, cwd is null ? null : GitRepository.FindRoot(cwd))`. If `snapshot.IsEmpty` → return 0 with no output and **no counter** (a session with no policy is not "pass-through", it is ungoverned — zero overhead).
3. `action = ClaudeActionNormalizer.Normalize(toolName, toolInput, cwd)`; `mode = renderedAgent ? TightenOnly : Full`; `eval = PolicyEngine.Evaluate(snapshot, action, mode)`.
4. Outcome handling — write stdout **first**, then emit the event (Claude reads stdout after process exit; the emit is bounded by the poster's spool fallback):
   - **Deny** → `stdout.Write(BuildPreToolUseDecision("deny", reason))`; journal `RecordTerminal` only when a call id exists; event requested=deny effective=deny.
   - **Ask** (and `PreToolUseAskEnabled`) → `stdout.Write(BuildPreToolUseDecision("ask", reason))`; `journal.RecordAsk(sessionId, callId, inputHash)`; event requested=ask effective=ask.
   - **Ask** (ask disabled) → no output; event requested=ask effective=pass_through.
   - **Allow** (Full mode only can produce it) → `stdout.Write(BuildPreToolUseDecision("allow", reason))`; journal `RecordTerminal` only when a call id exists; event requested=allow effective=allow.
   - **None**, Full mode → no output, no event, `journal.IncrementPassThrough(sessionId)`.
   - **None**, TightenOnly → nothing at all (the daemon owns the rendered session's full evaluation; nothing was decided here, so nothing is recorded — spec acceptance criterion 8).
5. Always return 0 (non-zero renders Claude's opaque hook-error banner; the lane is fail-open like every other kcap hook path).

The decision JSON is Claude's documented PreToolUse hook output:

```json
{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason":"…"}}
```

`permissionDecision` ∈ `allow | deny | ask`; the reason falls back to `"kcap approval policy"` when the matched rule has none. Camel-case keys are Claude's, not ours — same exemption as `BuildClaudeResponse` in `LocalPermissionBridge`.

Routing in `ClaudeHookCommand.HandleCore`: add a branch immediately after the existing `permission-request` branch (so it sits behind the same disabled-session gate), mirroring its shape:

```csharp
if (command == "pre-tool-use") {
    if (await IsSessionExcludedAsync(profiles.Effective, body, budget)) return 0;
    var rendered = Environment.GetEnvironmentVariable("KCAP_RENDERED_AGENT") is "1";
    return await new ClaudePolicySeam(config, profiles)
        .HandlePreToolUseAsync(body, sessionId, rendered, stdout ?? Console.Out);
}
```

(`sessionId` is already dashless at that point; excluded sessions are not governed because their decisions could not be recorded, and the audit contract is "every engine decision recorded".)

`kcap/hooks/hooks.json` — add before `"Notification"`:

```json
"PreToolUse": [
  { "matcher": "*", "hooks": [ { "type": "command", "command": "kcap hook --claude --no-update-check", "timeout": 5 } ] }
],
```

`kcap/.claude-plugin/plugin.json`: bump `"version"` (e.g. `1.8.0` → `1.9.0`) — the installer's marker comparison is what propagates a hooks change to existing installs.

- [ ] **Step 1: Write the failing tests**

Test the seam class directly (stdout as `StringWriter`, rendered flag as a parameter — no env mutation, no `[NotInParallel]` needed) with a WireMock server behind `Resolutions.Of` for event assertions:

```csharp
namespace Capacitor.Cli.Tests.Unit.Harness.Claude;

using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Policy;
using Capacitor.Cli.Harness.Claude;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

public class ClaudePolicySeamTests : IDisposable {
    [TempDir] public required TempDir Tmp { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    readonly WireMockServer _server = WireMockServer.Start();
    public void Dispose() => _server.Stop();

    const string Sid = "9dc2775376454e4691ecc2d69973c152";

    ClaudePolicySeam Seam => new(Config.Root, Resolutions.Of(new Profile(), serverUrl: _server.Url!));

    string Body(string toolName, string toolInputJson, string? callId = null) {
        var repo = Tmp.PathTo("repo");
        var node = new JsonObject {
            ["hook_event_name"] = "PreToolUse", ["session_id"] = Sid,
            ["tool_name"] = toolName, ["tool_input"] = JsonNode.Parse(toolInputJson),
            ["cwd"] = repo,
        };
        if (callId is not null) node["tool_use_id"] = callId;
        return node.ToJsonString();
    }

    void WriteUserPolicy(string yaml) => File.WriteAllText(Config.Root.Path("approvals.yaml"), yaml);

    [Before(Test)]
    public void Ok200() =>
        _server.Given(Request.Create().UsingPost()).RespondWith(Response.Create().WithStatusCode(200));

    [Test]
    public async Task Deny_rule_answers_deny_with_the_reason() {
        WriteUserPolicy("version: 1\nrules:\n  - match: { kind: shell, command: \"git push --force*\" }\n    outcome: deny\n    reason: use the PR lane\n");
        var stdout = new StringWriter();
        var exit = await Seam.HandlePreToolUseAsync(
            Body("Bash", """{"command":"git push --force"}"""), Sid, renderedAgent: false, stdout);
        await Assert.That(exit).IsEqualTo(0);
        var output = JsonNode.Parse(stdout.ToString())!;
        var hso = output["hookSpecificOutput"]!;
        await Assert.That(hso["hookEventName"]!.GetValue<string>()).IsEqualTo("PreToolUse");
        await Assert.That(hso["permissionDecision"]!.GetValue<string>()).IsEqualTo("deny");
        await Assert.That(hso["permissionDecisionReason"]!.GetValue<string>()).IsEqualTo("use the PR lane");
        var events = _server.FindLogEntries(Request.Create().WithPath("/hooks/policy-decision").UsingPost());
        await Assert.That(events.Count).IsEqualTo(1);
        var evt = JsonNode.Parse(events[0].RequestMessage.Body!)!;
        await Assert.That(evt["requested_outcome"]!.GetValue<string>()).IsEqualTo("deny");
        await Assert.That(evt["effective_outcome"]!.GetValue<string>()).IsEqualTo("deny");
        await Assert.That(evt["seam"]!.GetValue<string>()).IsEqualTo("claude_pre_tool_use");
    }

    [Test]
    public async Task Fully_covered_allow_answers_allow() {
        WriteUserPolicy("version: 1\nrules:\n  - match: { kind: shell, command: \"git status *\" }\n    outcome: allow\n");
        var stdout = new StringWriter();
        await Seam.HandlePreToolUseAsync(Body("Bash", """{"command":"git status"}"""), Sid, false, stdout);
        var hso = JsonNode.Parse(stdout.ToString())!["hookSpecificOutput"]!;
        await Assert.That(hso["permissionDecision"]!.GetValue<string>()).IsEqualTo("allow");
    }

    [Test]
    public async Task Redirection_evades_allow_but_stays_silent_not_denied() {
        WriteUserPolicy("version: 1\nrules:\n  - match: { kind: shell, command: \"git status *\" }\n    outcome: allow\n");
        var stdout = new StringWriter();
        await Seam.HandlePreToolUseAsync(Body("Bash", """{"command":"git status > pwn.yml"}"""), Sid, false, stdout);
        await Assert.That(stdout.ToString()).IsEmpty();
    }

    [Test]
    public async Task Ask_rule_forces_the_prompt_and_journals_a_pending_ask() {
        WriteUserPolicy("version: 1\nrules:\n  - match: { kind: shell, command: \"gh pr merge\" }\n    outcome: ask\n");
        var stdout = new StringWriter();
        await Seam.HandlePreToolUseAsync(Body("Bash", """{"command":"gh pr merge"}"""), Sid, false, stdout);
        var hso = JsonNode.Parse(stdout.ToString())!["hookSpecificOutput"]!;
        await Assert.That(hso["permissionDecision"]!.GetValue<string>()).IsEqualTo("ask");
        var hash = PolicyInputHash.Compute("Bash",
            System.Text.Json.JsonDocument.Parse("""{"command":"gh pr merge"}""").RootElement.Clone());
        var consumed = new PolicyDecisionJournal(Config.Root).Consume(Sid, null, hash);
        await Assert.That(consumed.PendingAsk).IsTrue();
    }

    [Test]
    public async Task Rendered_session_is_tighten_only() {
        WriteUserPolicy("""
            version: 1
            rules:
              - match: { kind: shell, command: "git status *" }
                outcome: allow
              - match: { kind: shell, command: "git push --force*" }
                outcome: deny
            """);
        var allowed = new StringWriter();
        await Seam.HandlePreToolUseAsync(Body("Bash", """{"command":"git status"}"""), Sid, renderedAgent: true, allowed);
        await Assert.That(allowed.ToString()).IsEmpty();                      // no allow is ever computed
        var denied = new StringWriter();
        await Seam.HandlePreToolUseAsync(Body("Bash", """{"command":"git push --force"}"""), Sid, renderedAgent: true, denied);
        await Assert.That(denied.ToString()).Contains("\"deny\"");            // deny still bites
    }

    [Test]
    public async Task No_policy_files_means_zero_output_and_zero_events() {
        var stdout = new StringWriter();
        await Seam.HandlePreToolUseAsync(Body("Bash", """{"command":"anything"}"""), Sid, false, stdout);
        await Assert.That(stdout.ToString()).IsEmpty();
        var events = _server.FindLogEntries(Request.Create().WithPath("/hooks/policy-decision").UsingPost());
        await Assert.That(events.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Unmatched_action_counts_a_pass_through() {
        WriteUserPolicy("version: 1\nrules:\n  - match: { kind: shell, command: \"git push --force*\" }\n    outcome: deny\n");
        var stdout = new StringWriter();
        await Seam.HandlePreToolUseAsync(Body("Bash", """{"command":"cargo build"}"""), Sid, false, stdout);
        await Assert.That(stdout.ToString()).IsEmpty();
        await Assert.That(new PolicyDecisionJournal(Config.Root).TakePassThroughCount(Sid)).IsEqualTo(1);
    }

    [Test]
    public async Task Call_id_journals_terminal_decisions_exactly() {
        WriteUserPolicy("version: 1\nrules:\n  - match: { kind: shell, command: \"git push --force*\" }\n    outcome: deny\n");
        await Seam.HandlePreToolUseAsync(
            Body("Bash", """{"command":"git push --force"}""", callId: "toolu_01X"), Sid, false, new StringWriter());
        var hash = PolicyInputHash.Compute("Bash",
            System.Text.Json.JsonDocument.Parse("""{"command":"git push --force"}""").RootElement.Clone());
        var consumed = new PolicyDecisionJournal(Config.Root).Consume(Sid, "toolu_01X", hash);
        await Assert.That(consumed.ExactOutcome).IsEqualTo("deny");
        await Assert.That(consumed.Ambiguous).IsFalse();
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter '/*/*/ClaudePolicySeamTests/*'`
Expected: build failure.

- [ ] **Step 3: Implement the seam, wire the route, update the plugin**

`ClaudePolicySeam.cs` skeleton (the outcome switch encodes step 4 above verbatim — the event's `EvaluationMode` is `"full"`/`"tighten_only"`, `EffectiveOutcome` is one of `allow|deny|ask|pass_through`, `DecidedAt` is `DateTimeOffset.UtcNow.ToString("O")`):

```csharp
namespace Capacitor.Cli.Harness.Claude;

using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Policy;
using Capacitor.Cli.Policy;

internal sealed class ClaudePolicySeam(ConfigRoot config, ProfileContext profiles) {
    internal const bool PreToolUseAskEnabled = true;

    public async Task<int> HandlePreToolUseAsync(string body, string sessionId, bool renderedAgent, TextWriter stdout) {
        JsonNode? node;
        try { node = JsonNode.Parse(body); } catch { return 0; }
        if (node is null) return 0;
        var toolName = node["tool_name"]?.GetValue<string>();
        var callId = node["tool_use_id"]?.GetValue<string>();
        var cwd = node["cwd"]?.GetValue<string>();
        JsonElement? toolInput = node["tool_input"] is { } ti
            ? JsonDocument.Parse(ti.ToJsonString()).RootElement.Clone() : null;

        var snapshot = new PolicySnapshotStore(config).LoadOrBuild(sessionId, cwd is null ? null : GitRepository.FindRoot(cwd));
        if (snapshot.IsEmpty) return 0;

        var journal = new PolicyDecisionJournal(config);
        var action = ClaudeActionNormalizer.Normalize(toolName, toolInput, cwd);
        var mode = renderedAgent ? EvaluationMode.TightenOnly : EvaluationMode.Full;
        var eval = PolicyEngine.Evaluate(snapshot, action, mode);
        var inputHash = PolicyInputHash.Compute(toolName, toolInput);
        var reason = eval.MatchedRules.FirstOrDefault()?.Reason ?? "kcap approval policy";

        switch (eval.Outcome) {
            case PolicyOutcome.Deny:
                stdout.Write(BuildPreToolUseDecision("deny", reason));
                if (callId is { Length: > 0 }) journal.RecordTerminal(sessionId, callId, "deny", inputHash);
                await Emit(eval, "deny", "deny");
                break;
            case PolicyOutcome.Ask when PreToolUseAskEnabled:
                stdout.Write(BuildPreToolUseDecision("ask", reason));
                journal.RecordAsk(sessionId, callId, inputHash);
                await Emit(eval, "ask", "ask");
                break;
            case PolicyOutcome.Ask:
                await Emit(eval, "ask", "pass_through");
                break;
            case PolicyOutcome.Allow:
                stdout.Write(BuildPreToolUseDecision("allow", reason));
                if (callId is { Length: > 0 }) journal.RecordTerminal(sessionId, callId, "allow", inputHash);
                await Emit(eval, "allow", "allow");
                break;
            case PolicyOutcome.None when mode == EvaluationMode.Full:
                journal.IncrementPassThrough(sessionId);
                break;
        }
        return 0;

        Task Emit(PolicyEvaluation e, string requested, string effective) =>
            new PolicyDecisionEmitter(config, profiles).EmitAsync(new PolicyDecisionEventV1(
                sessionId, node["agent_id"]?.GetValue<string>(), "claude", PolicySeams.ClaudePreToolUse,
                snapshot.Id, PolicyEngine.Version,
                mode == EvaluationMode.Full ? "full" : "tighten_only", requested, effective,
                PolicyWire.ToWire(action), PolicyWire.ToWire(e.MatchedRules),
                snapshot.Degraded, null, callId, CorrelationAmbiguous: callId is null, 
                DateTimeOffset.UtcNow.ToString("O")), snapshot);
    }

    internal static string BuildPreToolUseDecision(string decision, string? reason) =>
        new JsonObject {
            ["hookSpecificOutput"] = new JsonObject {
                ["hookEventName"] = "PreToolUse",
                ["permissionDecision"] = decision,
                ["permissionDecisionReason"] = reason ?? "kcap approval policy",
            },
        }.ToJsonString();
}
```

Then: the `ClaudeHookCommand.HandleCore` branch shown in the Interfaces block, the `hooks.json` entry, and the plugin version bump.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter '/*/*/ClaudePolicySeamTests/*'`
Expected: PASS. Then run the whole `Capacitor.Cli.Tests.Unit` suite once to catch regressions in `ClaudeHookCommandTests`.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli/Harness/Claude/ClaudePolicySeam.cs src/Capacitor.Cli/Commands/Harness/ClaudeHookCommand.cs kcap/hooks/hooks.json kcap/.claude-plugin/plugin.json test/Capacitor.Cli.Tests.Unit/Harness/Claude/ClaudePolicySeamTests.cs
git commit -m "Decide tool calls at the Claude PreToolUse seam (#738)"
```

---

### Task 13: Lifecycle integration — session-start build, stop expiry, session-end stamp

**Files:**
- Modify: `src/Capacitor.Cli/Commands/Harness/ClaudeHookCommand.cs` (three branches)
- Test: extend `test/Capacitor.Cli.Tests.Unit/Commands/Harness/ClaudeHookCommandTests.cs` (same fixture; new tests)

**Interfaces:**
- Consumes: `PolicySnapshotStore.LoadOrBuild`, `PolicyDecisionJournal.ClearTurn` / `TakePassThroughCount`, `GitRepository.FindRoot`.
- Produces: no new types — three behaviors in the Claude hook lane:

1. **session-start**: after the existing body enrichment (where `home_dir`/`repository` are stamped), build and persist the snapshot eagerly — `new PolicySnapshotStore(config).LoadOrBuild(sessionId, cwd is null ? null : GitRepository.FindRoot(cwd))` — wrapped in try/catch (never fail the hook). If the snapshot is degraded, surface it loudly in the SessionStart stdout envelope: locate where the `hookSpecificOutput` envelope node is composed (the `SessionStartAdditionalContext.BuildEnvelope` call) and set a `systemMessage` on the emitted top-level JSON object when none is present, e.g. `[kcap] approval policy degraded: {first degradation}` — a degradation the user never sees is the failure mode the spec forbids. If the session-start flow can exit without writing any envelope, write a minimal `{"systemMessage": …}` object in that case.
2. **stop**: before posting the stop event, `new PolicyDecisionJournal(config).ClearTurn(sessionId)` — the turn is over, pending asks expire (spec: "entries expire with the turn").
3. **session-end**: before posting, stamp `body["policy_pass_through_count"] = n` when `TakePassThroughCount(sessionId)` returns `n > 0`, and delete the session's snapshot and journal files (`File.Delete` in try/catch — the session is over; the server holds the uploaded snapshot).

- [ ] **Step 1: Write the failing tests**

Add to `ClaudeHookCommandTests` (reuse its `Fixture`, `Sid`, and `HandleCore(fx.Client, AuthStatus.Ok, fx.Spool, new StringReader(payload), stdout:)` pattern — copy an existing session-start test's arrangement):

```csharp
[Test, NotInParallel]
public async Task Session_start_surfaces_a_degraded_policy_snapshot() {
    File.WriteAllText(Config.Root.Path("approvals.yaml"), "version: 1\nenforcement: strict\n"); // server-scope field → malformed locally
    using var fx = new Fixture(Config.Root);
    var stdout = new StringWriter();
    var hook = new ClaudeHookCommand(Config.Root, Resolutions.Of(new Profile(), serverUrl: fx.MemoryServerUrl),
        new HookClock(TimeProvider.System), Home);
    await hook.HandleCore(fx.Client, AuthStatus.Ok, fx.Spool,
        new StringReader($$"""{"hook_event_name":"SessionStart","session_id":"{{Sid}}","cwd":"/tmp"}"""),
        stdout: stdout);
    await Assert.That(stdout.ToString()).Contains("approval policy degraded");
    await Assert.That(new PolicySnapshotStore(Config.Root).TryLoad(Sid)).IsNotNull();
}

[Test, NotInParallel]
public async Task Stop_clears_the_turn_journal() {
    var journal = new PolicyDecisionJournal(Config.Root);
    journal.RecordAsk(Sid, null, "h1");
    using var fx = new Fixture(Config.Root);
    var hook = new ClaudeHookCommand(Config.Root, Resolutions.Of(new Profile(), serverUrl: fx.MemoryServerUrl),
        new HookClock(TimeProvider.System), Home);
    await hook.HandleCore(fx.Client, AuthStatus.Ok, fx.Spool,
        new StringReader($$"""{"hook_event_name":"Stop","session_id":"{{Sid}}"}"""));
    await Assert.That(journal.Consume(Sid, null, "h1").PendingAsk).IsFalse();
}

[Test, NotInParallel]
public async Task Session_end_stamps_the_pass_through_count() {
    new PolicyDecisionJournal(Config.Root).IncrementPassThrough(Sid);
    using var fx = new Fixture(Config.Root);
    var hook = new ClaudeHookCommand(Config.Root, Resolutions.Of(new Profile(), serverUrl: fx.MemoryServerUrl),
        new HookClock(TimeProvider.System), Home);
    await hook.HandleCore(fx.Client, AuthStatus.Ok, fx.Spool,
        new StringReader($$"""{"hook_event_name":"SessionEnd","session_id":"{{Sid}}"}"""));
    var posted = fx.BodyPostedTo("session-end");   // add a body accessor to Fixture's StubHandler if absent
    await Assert.That(posted).Contains("\"policy_pass_through_count\":1");
}
```

Match the fixture's actual member names when writing these (its stub handler records routes; extend it minimally to expose the posted body when it doesn't already).

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter '/*/*/ClaudeHookCommandTests/*'`
Expected: the three new tests FAIL (assertion, not build).

- [ ] **Step 3: Implement the three branches** as described in Interfaces — each is a few lines guarded by try/catch inside the existing `session-start` / `stop` / `session-end` arms of `HandleCore`.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter '/*/*/ClaudeHookCommandTests/*'`
Expected: PASS, including all pre-existing tests in the class.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli/Commands/Harness/ClaudeHookCommand.cs test/Capacitor.Cli.Tests.Unit/Commands/Harness/ClaudeHookCommandTests.cs
git commit -m "Build snapshots at session start and expire the journal per turn (#738)"
```

---

### Task 14: The PermissionRequest seam (local interactive)

**Files:**
- Modify: `src/Capacitor.Cli/Harness/Claude/ClaudePolicySeam.cs` (add `HandlePermissionRequestAsync` + `SeamAnswer`)
- Modify: `src/Capacitor.Cli/Commands/PermissionRequestCommand.cs` (insert the evaluation before the rendered/record-only split)
- Test: extend `test/Capacitor.Cli.Tests.Unit/Harness/Claude/ClaudePolicySeamTests.cs`

**Interfaces:**
- Consumes: Task 12's seam class and everything under it; `PermissionRequestCommand.Handle(string? body, bool selfHealWatcher, TextWriter? stdout)` mechanics (session id already dashless; `HandleRecordOnly` is today's local behavior; rendered sessions forward to the daemon bridge).
- Produces:

```csharp
internal enum SeamAnswer { Answered, NotAnswered }
// on ClaudePolicySeam:
public Task<SeamAnswer> HandlePermissionRequestAsync(JsonNode node, string sessionId, TextWriter stdout);
internal static string BuildPermissionRequestDecision(string behavior);  // {"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":…}}}
```

**Placement in `PermissionRequestCommand.Handle`:** after the watcher self-heal, before the `KCAP_RENDERED_AGENT` split:

```csharp
var isRenderedAgent = Environment.GetEnvironmentVariable("KCAP_RENDERED_AGENT") is "1";
if (!isRenderedAgent
    && await new ClaudePolicySeam(config, profiles).HandlePermissionRequestAsync(node, sessionId, stdout ?? Console.Out)
        == SeamAnswer.Answered)
    return 0;
if (isRenderedAgent) return await HandleRenderedAgent(node, sessionId, stdout);
return await HandleRecordOnly(node, sessionId);
```

A **rendered** session's PermissionRequest deliberately does not evaluate here: the daemon runs the one full evaluation per raised prompt at its bridge (Task 16), and a rendered PreToolUse ask that forced this prompt hit a deny/ask rule that the daemon's full evaluation reproduces from the same files — deny/ask are checked before allow in the merge, so stickiness holds by construction, with no cross-process journal coupling.

**Aggregation in `HandlePermissionRequestAsync`** (spec acceptance criterion 10 — the fresh evaluation always runs; the journal can only tighten):
1. Load snapshot (`LoadOrBuild`); `IsEmpty` → `NotAnswered` (record-only proceeds exactly as today).
2. Normalize (`tool_name`, `tool_input`, `cwd`), evaluate **Full**, compute `inputHash`, read `tool_use_id` as the call id, then `consume = journal.Consume(sessionId, callId, inputHash)`.
3. Decide, in this order:
   - **Fresh deny** → write `BuildPermissionRequestDecision("deny")`, emit event (requested `deny`, effective `deny`), `Answered`. A pending ask that was consumed is subsumed — deny is the most restrictive.
   - **Pending ask** (`consume.PendingAsk`, exact or fallback) → never auto-answer a forced prompt: no output, emit event (requested `ask`, effective `prompt_stands`, `CorrelationAmbiguous = consume.Ambiguous`), `NotAnswered`.
   - **Fresh allow** → write `BuildPermissionRequestDecision("allow")`, emit event, `Answered`.
   - **Fresh ask** → at an at-prompt seam, leaving the prompt standing *is* the ask: no output, emit event (requested `ask`, effective `prompt_stands`), `NotAnswered`.
   - **None** → `journal.IncrementPassThrough(sessionId)`, `NotAnswered`.
4. The event's `Seam` is `PolicySeams.ClaudePermissionRequest`; the emit helper from Task 12 is reused (extract it into a private method taking the seam name).

- [ ] **Step 1: Write the failing tests** — add to `ClaudePolicySeamTests`:

```csharp
JsonNode PermissionNode(string toolName, string toolInputJson) => new JsonObject {
    ["session_id"] = Sid, ["tool_name"] = toolName,
    ["tool_input"] = JsonNode.Parse(toolInputJson), ["cwd"] = Tmp.PathTo("repo"),
};

[Test]
public async Task Permission_request_deny_rule_answers_the_prompt_deny() {
    WriteUserPolicy("version: 1\nrules:\n  - match: { kind: shell, command: \"git push --force*\" }\n    outcome: deny\n");
    var stdout = new StringWriter();
    var answer = await Seam.HandlePermissionRequestAsync(
        PermissionNode("Bash", """{"command":"git push --force"}"""), Sid, stdout);
    await Assert.That(answer).IsEqualTo(SeamAnswer.Answered);
    var decision = JsonNode.Parse(stdout.ToString())!["hookSpecificOutput"]!["decision"]!;
    await Assert.That(decision["behavior"]!.GetValue<string>()).IsEqualTo("deny");
}

[Test]
public async Task Permission_request_allow_rule_answers_allow() {
    WriteUserPolicy("version: 1\nrules:\n  - match: { kind: shell, command: \"git status *\" }\n    outcome: allow\n");
    var stdout = new StringWriter();
    var answer = await Seam.HandlePermissionRequestAsync(
        PermissionNode("Bash", """{"command":"git status"}"""), Sid, stdout);
    await Assert.That(answer).IsEqualTo(SeamAnswer.Answered);
    await Assert.That(stdout.ToString()).Contains("\"allow\"");
}

[Test]
public async Task Pending_ask_suppresses_a_fresh_allow_but_not_a_fresh_deny() {
    WriteUserPolicy("version: 1\nrules:\n  - match: { kind: shell, command: \"git status *\" }\n    outcome: allow\n");
    var hash = PolicyInputHash.Compute("Bash",
        System.Text.Json.JsonDocument.Parse("""{"command":"git status"}""").RootElement.Clone());
    new PolicyDecisionJournal(Config.Root).RecordAsk(Sid, null, hash);
    var stdout = new StringWriter();
    var answer = await Seam.HandlePermissionRequestAsync(
        PermissionNode("Bash", """{"command":"git status"}"""), Sid, stdout);
    await Assert.That(answer).IsEqualTo(SeamAnswer.NotAnswered);   // prompt stands
    await Assert.That(stdout.ToString()).IsEmpty();
    var events = _server.FindLogEntries(Request.Create().WithPath("/hooks/policy-decision").UsingPost());
    var last = JsonNode.Parse(events[^1].RequestMessage.Body!)!;
    await Assert.That(last["effective_outcome"]!.GetValue<string>()).IsEqualTo("prompt_stands");
    await Assert.That(last["correlation_ambiguous"]!.GetValue<bool>()).IsTrue();
}

[Test]
public async Task Fresh_deny_wins_even_with_a_pending_ask() {
    WriteUserPolicy("version: 1\nrules:\n  - match: { kind: shell, command: \"rm -rf*\" }\n    outcome: deny\n");
    var hash = PolicyInputHash.Compute("Bash",
        System.Text.Json.JsonDocument.Parse("""{"command":"rm -rf /"}""").RootElement.Clone());
    new PolicyDecisionJournal(Config.Root).RecordAsk(Sid, null, hash);
    var stdout = new StringWriter();
    var answer = await Seam.HandlePermissionRequestAsync(
        PermissionNode("Bash", """{"command":"rm -rf /"}"""), Sid, stdout);
    await Assert.That(answer).IsEqualTo(SeamAnswer.Answered);
    await Assert.That(stdout.ToString()).Contains("\"deny\"");
}

[Test]
public async Task Fresh_ask_leaves_the_prompt_standing() {
    WriteUserPolicy("version: 1\nrules:\n  - match: { kind: shell, command: \"gh pr merge\" }\n    outcome: ask\n");
    var stdout = new StringWriter();
    var answer = await Seam.HandlePermissionRequestAsync(
        PermissionNode("Bash", """{"command":"gh pr merge"}"""), Sid, stdout);
    await Assert.That(answer).IsEqualTo(SeamAnswer.NotAnswered);
    await Assert.That(stdout.ToString()).IsEmpty();
}

[Test]
public async Task No_policy_defers_to_record_only() {
    var answer = await Seam.HandlePermissionRequestAsync(
        PermissionNode("Bash", """{"command":"ls"}"""), Sid, new StringWriter());
    await Assert.That(answer).IsEqualTo(SeamAnswer.NotAnswered);
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter '/*/*/ClaudePolicySeamTests/*'`
Expected: new tests fail to build (`HandlePermissionRequestAsync` missing).

- [ ] **Step 3: Implement** — `HandlePermissionRequestAsync` per the aggregation order above (the body parallels `HandlePreToolUseAsync`; extract the shared normalize/evaluate/emit plumbing into private helpers rather than duplicating), `BuildPermissionRequestDecision`:

```csharp
internal static string BuildPermissionRequestDecision(string behavior) =>
    new JsonObject {
        ["hookSpecificOutput"] = new JsonObject {
            ["hookEventName"] = "PermissionRequest",
            ["decision"] = new JsonObject { ["behavior"] = behavior },
        },
    }.ToJsonString();
```

then the three-line insertion in `PermissionRequestCommand.Handle` shown in Interfaces.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter '/*/*/ClaudePolicySeamTests/*'`, then the whole `Capacitor.Cli.Tests.Unit` suite (the `PermissionRequestCommandTests` class must stay green — the inserted evaluation is inert without policy files, which those tests never create).
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli/Harness/Claude/ClaudePolicySeam.cs src/Capacitor.Cli/Commands/PermissionRequestCommand.cs test/Capacitor.Cli.Tests.Unit/Harness/Claude/ClaudePolicySeamTests.cs
git commit -m "Answer local Claude permission prompts from the policy engine (#738)"
```

---

### Task 15: Daemon snapshot at launch

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Services/PolicySnapshotProvider.cs`
- Modify: `src/Capacitor.Cli.Daemon/Services/IHostedAgentRuntimeFactory.cs` (`RuntimeStartContext` gains a trailing optional `PolicySnapshot? PolicySnapshot = null`)
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` (build at launch; store on `AgentInstance`; widen `AttributedAgent`; emit the snapshot event)
- Modify: `src/Capacitor.Cli.Daemon/Services/LocalPermissionBridge.cs` (only the `AttributedAgent` record shape — the evaluation is Task 16)
- Modify: `src/Capacitor.Cli.Daemon/DaemonRunner.cs` (register the provider)
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/PolicySnapshotProviderTests.cs`

**Interfaces:**
- Consumes: `PolicySnapshotBuilder.Build(string? repoRoot, ConfigRoot config)` (Task 8); `PolicySnapshotUploadV1` / `PolicyWire.ToUpload` (Task 11); existing `AgentInstance` (`{ get; init; }` members like `PermissionPreset`), `RuntimeStartContext` (trailing optionals like `ActivityClock`), `ServerConnection.AppendAgentRunEventAsync(string agentId, object evt)`, `AttributedAgent(string AgentId)` + `AttributeHandler` on the bridge, `AgentOrchestrator.HandleAttributePermission`.
- Produces:

```csharp
internal sealed class PolicySnapshotProvider(ConfigRoot config) {
    public PolicySnapshot BuildFor(string repoPath) => PolicySnapshotBuilder.Build(repoPath, config);
}
// widened bridge attribution:
internal readonly record struct AttributedAgent(string AgentId, PolicySnapshot? PolicySnapshot = null);
```

Wiring, one site each (all follow the `ActivityClock` launch-attachment precedent):
1. **DaemonRunner**: `builder.Services.AddSingleton<PolicySnapshotProvider>();` next to the other permission services (before `LocalPermissionBridge`). `AgentOrchestrator` gains an optional trailing ctor parameter `PolicySnapshotProvider? policySnapshots = null` — legal only because its registration is a **bare** `AddSingleton<AgentOrchestrator>()`; do not convert it to a factory delegate (the DaemonRunner comment at its registration explains why).
2. **Launch** (`HandleLaunchAgentCore`): after the worktree is resolved, `var policySnapshot = policySnapshots?.BuildFor(worktree.Path);` (the worktree checkout is what the session's repo file means — same trust boundary as the local seams); pass it into the `RuntimeStartContext` construction and set `PolicySnapshot = policySnapshot` on the `AgentInstance` construction.
3. **Attribution** (`HandleAttributePermission`): every `return new AttributedAgent(a.Id)` becomes `return new AttributedAgent(a.Id, a.PolicySnapshot)` (the record's default keeps other construction sites compiling).
4. **Registration** (`RegisterAgentAsync`, where `AgentRunStarted` is emitted): when `agent.PolicySnapshot is { IsEmpty: false } snap`, also `_ = _server.AppendAgentRunEventAsync(agent.Id, PolicyWire.ToUpload(agent.SessionId ?? agent.Id, snap));` — the hosted counterpart of the CLI's snapshot upload (`event_type` on the wire is the record's type name, `PolicySnapshotUploadV1`).

- [ ] **Step 1: Write the failing test**

```csharp
namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

using Capacitor.Cli.Core.Policy;
using Capacitor.Cli.Daemon.Services;

public class PolicySnapshotProviderTests {
    [TempDir] public required TempDir Tmp { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [Test]
    public async Task Builds_from_the_worktree_repo_file_and_daemon_user_file() {
        var repo = Tmp.CreateDir("wt");
        Tmp.CreateDir("wt/.kcap");
        Tmp.CreateFile("wt/.kcap/approvals.yaml",
            "version: 1\nrules:\n  - match: { kind: shell, command: \"rm -rf*\" }\n    outcome: deny\n");
        var snap = new PolicySnapshotProvider(Config.Root).BuildFor(repo);
        await Assert.That(snap.IsEmpty).IsFalse();
        await Assert.That(snap.Documents[0].Scope).IsEqualTo(PolicyScope.Repo);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj -- --treenode-filter '/*/*/PolicySnapshotProviderTests/*'`
Expected: build failure.

- [ ] **Step 3: Implement** the provider and the four wiring sites above. The `AttributedAgent` widening will ripple through `LocalPermissionBridge` construction of `pending` — the compiler shows every site; none change behavior yet.

- [ ] **Step 4: Run the full daemon suite to verify nothing regressed**

Run: `dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj`
Expected: PASS (plus the new test).

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/PolicySnapshotProvider.cs src/Capacitor.Cli.Daemon/Services/IHostedAgentRuntimeFactory.cs src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs src/Capacitor.Cli.Daemon/Services/LocalPermissionBridge.cs src/Capacitor.Cli.Daemon/DaemonRunner.cs test/Capacitor.Cli.Daemon.Tests.Unit/Services/PolicySnapshotProviderTests.cs
git commit -m "Attach a policy snapshot to every hosted launch (#738)"
```

---

### Task 16: Hosted-Claude insertion at LocalPermissionBridge

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/LocalPermissionBridge.cs`
- Modify: `src/Capacitor.Cli.Daemon/Services/PermissionPromptBroker.cs` (add `PermissionSettlements.SourcePolicy = "policy"` beside the other source constants)
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/LocalPermissionBridgePolicyTests.cs`

**Interfaces:**
- Consumes: `AttributedAgent.PolicySnapshot` (Task 15); `PolicyEngine.Evaluate`, `ClaudeActionNormalizer.Normalize`, `PolicyWire`, `PolicyDecisionEventV1`; the bridge's existing locals in the interactive branch of `HandleAsync` (`vendor`, `canonicalSessionId`, `toolName`, `toolInput` as `JsonElement?`, `node["cwd"]`) and its existing sinks (`BuildHookResponseJson(PermissionDecision, vendor)`, `WriteResponseAsync`, `_decisionLog?.Record`, `server.AppendAgentRunEventAsync`).
- Produces: no new public types — the insertion, placed **after the attribution/`pending` construction and before `_broker.Register(pending)`** (never before the reviewer-token split; the reviewer branch is the unattended reviewer's own containment, not policy):

```csharp
if (vendor is "claude" && attributed is { PolicySnapshot: { IsEmpty: false } snapshot } aa) {
    var action = ClaudeActionNormalizer.Normalize(toolName, toolInput, node["cwd"]?.GetValue<string>());
    var eval = PolicyEngine.Evaluate(snapshot, action, EvaluationMode.Full);
    if (eval.Outcome is PolicyOutcome.Allow or PolicyOutcome.Deny) {
        var behavior = eval.Outcome == PolicyOutcome.Allow ? "allow" : "deny";
        _decisionLog?.Record(new PermissionDecisionRecord(
            DateTimeOffset.UtcNow.ToString("O"), aa.AgentId, canonicalSessionId!, vendor,
            toolName ?? "", behavior, PermissionSettlements.SourcePolicy));
        _ = server.AppendAgentRunEventAsync(aa.AgentId, DecisionEvent(eval, behavior, behavior));
        await WriteResponseAsync(context, BuildHookResponseJson(new PermissionDecision(behavior, null, null), vendor));
        return;
    }
    if (eval.Outcome == PolicyOutcome.Ask)
        _ = server.AppendAgentRunEventAsync(aa.AgentId, DecisionEvent(eval, "ask", "parked"));
    // ask and no-decision both park to the human lane below, unchanged
}
```

with a local helper building the `PolicyDecisionEventV1` (seam `PolicySeams.HostedClaudePermission`, evaluation mode `"full"`, vendor `"claude"`, correlation id null / ambiguous false — the lane identifies the raised prompt exactly, there is one evaluation per prompt). The vendor guard is deliberate: hosted **Codex** requests keep parking untouched (the hosted-Codex insertion is a later phase), and a policy-answered call skips `Register` entirely — no desktop card appears for a call no human needs to see; the decision log and run events are the audit trail.

- [ ] **Step 1: Write the failing tests**

Follow the harness shape of `LocalPermissionBridgeInteractiveTests` (same directory): it starts the bridge's listener, sets `AttributeHandler`, POSTs a permission-request body to `/{token}/claude/permission-request`, and asserts on the HTTP response and broker interaction. Copy its setup verbatim and add these cases (adapting helper names to that file's fixture):

```csharp
public class LocalPermissionBridgePolicyTests /* : same base/fixture pattern as LocalPermissionBridgeInteractiveTests */ {
    static PolicySnapshot DenySnapshot => new("snap", [
        new PolicyScopeDocument(PolicyScope.Repo, "/wt/.kcap/approvals.yaml",
            "version: 1\n",
            PolicyDocumentBinder.Bind(
                "version: 1\nrules:\n  - match: { kind: shell, command: \"git push --force*\" }\n    outcome: deny\n  - match: { kind: shell, command: \"git status *\" }\n    outcome: allow\n  - match: { kind: shell, command: \"gh pr merge\" }\n    outcome: ask\n",
                PolicyScope.Repo))], false, []);
    // NOTE: bind the full rules text — the Content field is not consulted by evaluation, only the Document.

    [Test]
    public async Task Policy_deny_answers_without_parking() {
        // AttributeHandler returns new AttributedAgent("agent-1", DenySnapshot)
        // POST body: {"session_id":"…","tool_name":"Bash","tool_input":{"command":"git push --force"},"cwd":"/wt"}
        // Assert: response body contains "\"behavior\":\"deny\"";
        //         the broker received no Register call (no pending item was ever published).
    }

    [Test]
    public async Task Policy_allow_answers_allow() {
        // tool_input {"command":"git status"} → response contains "\"behavior\":\"allow\"".
    }

    [Test]
    public async Task Policy_ask_and_no_decision_park_as_today() {
        // tool_input {"command":"gh pr merge"} → request parks (broker Register called); settle it
        // via the broker to complete the HTTP exchange, as the existing interactive tests do.
        // Repeat with {"command":"cargo build"} (no rule) → also parks.
    }

    [Test]
    public async Task No_snapshot_means_no_evaluation() {
        // AttributeHandler returns new AttributedAgent("agent-1") → parks for any command.
    }

    [Test]
    public async Task Codex_vendor_is_untouched() {
        // POST to /{token}/codex/permission-request with a snapshot-carrying attribution → parks.
    }
}
```

Write these as real tests against the actual fixture — the sketch above fixes the *scenarios and assertions*; the existing file supplies the mechanics (listener URL, token, settle helpers).

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj -- --treenode-filter '/*/*/LocalPermissionBridgePolicyTests/*'`
Expected: FAIL (deny/allow cases park instead of answering).

- [ ] **Step 3: Implement** the insertion and the `SourcePolicy` constant.

- [ ] **Step 4: Run the full daemon suite**

Run: `dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/LocalPermissionBridge.cs src/Capacitor.Cli.Daemon/Services/PermissionPromptBroker.cs test/Capacitor.Cli.Daemon.Tests.Unit/Services/LocalPermissionBridgePolicyTests.cs
git commit -m "Evaluate policy before parking hosted Claude permissions (#738)"
```

---

### Task 17: ACP insertion — normalizer and bridge

**Files:**
- Create: `src/Capacitor.Cli.Core/Acp/AcpActionNormalizer.cs`
- Modify: `src/Capacitor.Cli.Daemon/Acp/AcpInteractionBridge.cs`
- Modify: `src/Capacitor.Cli.Daemon/Services/AcpHostedAgentRuntimeFactory.cs` + the `AcpHostedAgentRuntime` ctor chain (thread the snapshot/vendor/notify into the bridge)
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` (wire `notifyPolicyDecision` → `AppendAgentRunEventAsync`, same as `notifyAutoApproval`)
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Acp/AcpActionNormalizerTests.cs`
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Acp/AcpInteractionBridgePolicyTests.cs`

**Interfaces:**
- Consumes: `SessionRequestPermissionParams` (`SessionId`, `ToolCall` as opaque `JsonElement`, `Options`), the bridge's option pickers (`TrySelectSingleAllowOnce`, `TrySelectReject`/`Addressable`), result builders (`SelectedResult`, `CancelledResult`), and the arm order in `HandlePermissionAsync` (reviewer containment arms → **here** → preset arm → interaction park).
- Produces:

```csharp
// Core/Acp — namespace Capacitor.Cli.Core.Acp
public static class AcpActionNormalizer {
    // toolCall is the ACP ToolCallUpdate element: kind/title/toolCallId/rawInput/locations, all read defensively.
    public static CanonicalAction Normalize(JsonElement toolCall, string vendor, string? cwd);
}
// AcpInteractionBridge ctor gains trailing optionals:
//   PolicySnapshot? policySnapshot = null, string? policyVendor = null,
//   Action<PolicyDecisionEventV1>? notifyPolicyDecision = null
```

Normalizer mapping (kind strings are ACP tool-call kinds): `execute` + `rawInput.command` string → shell (with `ShellCommandAnalyzer`); `read`/`search` → file_read and `edit`/`move`/`delete` → file_edit, paths from `locations[].path` (fall back to `rawInput.path`), resolved via `LexicalPaths.TryResolve` against `cwd`; `fetch` + `rawInput.url` → network; anything else, or any missing required field → `Other` with `RawToolName` = the kind (or the title when kind is absent). Never throws.

Bridge insertion — between the `AutoApprove` arm and the preset arm, gated exactly like the preset (`unattendedPolicy == Disabled`), operating on the already-normalized `options` array:

```csharp
var policyForcedAsk = false;
if (unattendedPolicy == AcpUnattendedInteractionPolicy.Disabled
    && policySnapshot is { IsEmpty: false } snap) {
    var action = AcpActionNormalizer.Normalize(parsed.ToolCall, policyVendor ?? "unknown", cwd: null);
    var eval = PolicyEngine.Evaluate(snap, action, EvaluationMode.Full);
    switch (eval.Outcome) {
        case PolicyOutcome.Deny:
            NotifyPolicy(eval, action, "deny", "deny");
            return DenyResult(options);           // extracted from MapPermissionDecision's deny arm
        case PolicyOutcome.Allow when TrySelectSingleAllowOnce(options) is { } choice:
            NotifyPolicy(eval, action, "allow", "allow");
            return SelectedResult(choice);
        case PolicyOutcome.Allow:                  // no unambiguous allow option: degrade, never fabricate
            NotifyPolicy(eval, action, "allow", "pass_through");
            break;
        case PolicyOutcome.Ask:                    // terminal for kcap's layers: skip the preset, park to the lane
            NotifyPolicy(eval, action, "ask", "parked");
            policyForcedAsk = true;
            break;
    }
}
```

then gate the preset arm's condition with `&& !policyForcedAsk`. Extract the deny mapping into `static JsonElement DenyResult(IReadOnlyList<PermissionOptionDto> options)` reused by both `MapPermissionDecision` and the insertion (one deny mapping: explicit reject option, least-privilege `reject_once` then `reject_always`, else cancelled). `NotifyPolicy` builds a `PolicyDecisionEventV1` (seam `PolicySeams.AcpRequestPermission`, session id `parsed.SessionId`, snapshot id, wire action, matched rules, correlation id = `TryGetToolCallId(parsed.ToolCall)`, ambiguous false) and invokes `notifyPolicyDecision` — wired by the orchestrator to `_ = _server.AppendAgentRunEventAsync(agentId, evt)` exactly as `notifyAutoApproval` is. The ordering statement this encodes is the spec's: policy deny/ask are terminal, policy allow beats the preset's would-prompt, only policy no-decision falls through to the preset, and a preset can never widen a policy outcome.

- [ ] **Step 1: Write the failing tests**

`AcpActionNormalizerTests` (Core):

```csharp
namespace Capacitor.Cli.Core.Tests.Unit.Acp;

using System.Text.Json;
using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Core.Policy;

public class AcpActionNormalizerTests {
    static JsonElement ToolCall(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Test]
    public async Task Execute_with_a_command_maps_to_shell() {
        var a = AcpActionNormalizer.Normalize(
            ToolCall("""{"kind":"execute","rawInput":{"command":"git status"},"toolCallId":"tc1"}"""), "cursor", null);
        await Assert.That(a.Kind).IsEqualTo(ActionKind.Shell);
        await Assert.That(a.Analyzed).IsTrue();
        await Assert.That(a.Vendor).IsEqualTo("cursor");
    }

    [Test]
    public async Task Edit_takes_paths_from_locations() {
        var a = AcpActionNormalizer.Normalize(
            ToolCall("""{"kind":"edit","locations":[{"path":"/wt/a.cs"},{"path":"/wt/b.cs"}]}"""), "cursor", null);
        await Assert.That(a.Kind).IsEqualTo(ActionKind.FileEdit);
        await Assert.That(a.Paths).IsEquivalentTo(new[] { "/wt/a.cs", "/wt/b.cs" });
    }

    [Test]
    public async Task Fetch_maps_to_network() {
        var a = AcpActionNormalizer.Normalize(
            ToolCall("""{"kind":"fetch","rawInput":{"url":"https://example.com/x"}}"""), "gemini", null);
        await Assert.That(a.Kind).IsEqualTo(ActionKind.Network);
        await Assert.That(a.Host).IsEqualTo("example.com");
    }

    [Test]
    public async Task Unknown_or_incomplete_tool_calls_fall_to_other() {
        var noCommand = AcpActionNormalizer.Normalize(ToolCall("""{"kind":"execute"}"""), "cursor", null);
        await Assert.That(noCommand.Kind).IsEqualTo(ActionKind.Other);
        await Assert.That(noCommand.RawToolName).IsEqualTo("execute");
        var unknown = AcpActionNormalizer.Normalize(ToolCall("""{"kind":"other","title":"Weird"}"""), "cursor", null);
        await Assert.That(unknown.Kind).IsEqualTo(ActionKind.Other);
    }
}
```

`AcpInteractionBridgePolicyTests` (daemon) — the bridge is directly constructible; follow `AcpInteractionBridgePresetTests` for building the `session/request_permission` `AcpRequest` and the standard three options (`allow_once`/`allow_always`/`reject_once`). Scenarios (write them fully against that file's helpers):

```csharp
// snapshot: deny "git push --force*", allow "git status *", ask "gh pr merge"; vendor "cursor".
// 1. Deny → result selects the reject_once option; requestInteraction never invoked;
//    notifyPolicyDecision saw requested=deny effective=deny, seam "acp_request_permission".
// 2. Allow → result selects the single allow_once option; requestInteraction never invoked.
// 3. Allow with two allow_once options offered → falls through (preset disabled here → parks);
//    notify saw requested=allow effective=pass_through.
// 4. Ask → skips the preset even when a preset would auto-approve the kind
//    (construct the bridge WITH preset: AcpPermissionPresets.TryResolve("edit", …) and a toolCall
//    kind "execute" carrying rawInput.command "gh pr merge"); requestInteraction IS invoked (parks).
// 5. No decision ("cargo build") with preset present and kind "read" → preset auto-approves as today.
// 6. policySnapshot null → bridge behaves exactly as before (run one preset test unchanged).
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter '/*/*/AcpActionNormalizerTests/*'`
Expected: build failure; likewise the daemon class.

- [ ] **Step 3: Implement**

`AcpActionNormalizer.cs`:

```csharp
namespace Capacitor.Cli.Core.Acp;

using System.Text.Json;
using Capacitor.Cli.Core.Policy;

public static class AcpActionNormalizer {
    public static CanonicalAction Normalize(JsonElement toolCall, string vendor, string? cwd) {
        try { return NormalizeCore(toolCall, vendor, cwd); }
        catch { return Other(toolCall, vendor, cwd); }
    }

    static CanonicalAction NormalizeCore(JsonElement toolCall, string vendor, string? cwd) {
        var kind = toolCall.Str("kind");
        var rawInput = toolCall.Obj("rawInput");
        switch (kind) {
            case "execute" when rawInput?.Str("command") is { Length: > 0 } command: {
                var analysis = ShellCommandAnalyzer.Analyze(command);
                return new() {
                    Kind = ActionKind.Shell, Vendor = vendor, Cwd = cwd,
                    Command = command, Analyzed = analysis.Analyzed, Segments = analysis.Segments,
                };
            }
            case "read" or "search" or "edit" or "move" or "delete": {
                var paths = new List<string>();
                if (toolCall.Arr("locations") is { } locations)
                    foreach (var loc in locations.EnumerateArray())
                        if (loc.Str("path") is { Length: > 0 } p && LexicalPaths.TryResolve(cwd, p) is { } resolved)
                            paths.Add(resolved);
                if (paths.Count == 0 && rawInput?.Str("path") is { Length: > 0 } single
                    && LexicalPaths.TryResolve(cwd, single) is { } r)
                    paths.Add(r);
                if (paths.Count == 0) break;
                return new() {
                    Kind = kind is "read" or "search" ? ActionKind.FileRead : ActionKind.FileEdit,
                    Vendor = vendor, Cwd = cwd, Paths = paths,
                };
            }
            case "fetch" when rawInput?.Str("url") is { Length: > 0 } url
                              && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IdnHost.Length > 0:
                return new() {
                    Kind = ActionKind.Network, Vendor = vendor, Cwd = cwd, Url = url,
                    Host = uri.IdnHost.ToLowerInvariant(), Port = uri.IsDefaultPort ? null : uri.Port,
                };
        }
        return Other(toolCall, vendor, cwd);
    }

    static CanonicalAction Other(JsonElement toolCall, string vendor, string? cwd) => new() {
        Kind = ActionKind.Other, Vendor = vendor, Cwd = cwd,
        RawToolName = toolCall.Str("kind") ?? toolCall.Str("title"),
        RawPayloadJson = toolCall.GetRawText(),
    };
}
```

Then the bridge changes (three trailing ctor optionals, the insertion block, the `DenyResult` extraction, the `policyForcedAsk` gate on the preset arm), the factory/runtime threading (`RuntimeStartContext.PolicySnapshot` → `AcpHostedAgentRuntime` → bridge ctor, plus `_vendor` as `policyVendor`), and the orchestrator's `notifyPolicyDecision` wiring.

- [ ] **Step 4: Run both suites**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter '/*/*/AcpActionNormalizerTests/*'` and the full daemon suite.
Expected: PASS, including all existing `AcpInteractionBridge*Tests`.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/Acp/AcpActionNormalizer.cs src/Capacitor.Cli.Daemon/Acp/AcpInteractionBridge.cs src/Capacitor.Cli.Daemon/Services/AcpHostedAgentRuntimeFactory.cs src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs test/Capacitor.Cli.Core.Tests.Unit/Acp/AcpActionNormalizerTests.cs test/Capacitor.Cli.Daemon.Tests.Unit/Acp/AcpInteractionBridgePolicyTests.cs
git commit -m "Insert the policy layer ahead of ACP launch presets (#738)"
```

---

### Task 18: End-to-end integration test through the real binary

**Files:**
- Create: `test/Capacitor.Cli.Tests.Integration/PolicyHookDecisionTests.cs`

**Interfaces:**
- Consumes: Helpers' `KcapProcess.StartInfo(DaemonStore, ConfigRoot, params args)` (pins `KCAP_DAEMONS_DIR` and `KCAP_CONFIG_DIR` for the child), `[TempDaemonPaths]`/`[TempConfigRoot]` injection, WireMock; the shipped seams (Tasks 12–14).
- Produces: the phase-gate proof that the decision path works through a real spawned `kcap` process — payload on stdin, decision on stdout, event on the wire.

- [ ] **Step 1: Write the failing test**

Model the process mechanics on `UnusableUrlHookMatrixTests.RunAsync` (stdin write + close, stdout/stderr reads before `WaitForExitAsync`, 30 s cts, spawned-process cleanup in `Dispose`):

```csharp
namespace Capacitor.Cli.Tests.Integration;

using System.Diagnostics;
using System.Text.Json.Nodes;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

public class PolicyHookDecisionTests : IDisposable {
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    readonly WireMockServer _server = WireMockServer.Start();
    readonly List<Process> _spawned = [];
    public void Dispose() { _server.Stop(); foreach (var p in _spawned) { try { p.Kill(); } catch { } } }

    const string Sid = "3f8a2b1c4d5e46f7a8b9c0d1e2f3a4b5";

    async Task<(int ExitCode, string Stdout)> RunHookAsync(string payload) {
        var psi = KcapProcess.StartInfo(Daemons.Store, Config.Root, "hook", "--claude", "--no-update-check");
        psi.WorkingDirectory = Config.Directory;
        psi.Environment["KCAP_URL"] = _server.Url!;
        psi.Environment["KCAP_RENDERED_AGENT"] = "0";
        var process = Process.Start(psi) ?? throw new InvalidOperationException("failed to start kcap");
        _spawned.Add(process);
        try { await process.StandardInput.WriteAsync(payload); }
        catch (IOException) { }
        finally { try { process.StandardInput.Close(); } catch (IOException) { } }
        var stdout = await process.StandardOutput.ReadToEndAsync();
        _ = await process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(cts.Token);
        return (process.ExitCode, stdout);
    }

    [Test]
    public async Task Permission_request_is_denied_end_to_end() {
        _server.Given(Request.Create().UsingPost()).RespondWith(Response.Create().WithStatusCode(200));
        File.WriteAllText(Config.Root.Path("approvals.yaml"),
            "version: 1\nrules:\n  - match: { kind: shell, command: \"git push --force*\" }\n    outcome: deny\n");
        var payload = $$"""
            {"hook_event_name":"PermissionRequest","session_id":"{{Sid}}","tool_name":"Bash",
             "tool_input":{"command":"git push --force"},"cwd":"{{Config.Directory}}"}
            """;
        var (exit, stdout) = await RunHookAsync(payload);
        await Assert.That(exit).IsEqualTo(0);
        var decision = JsonNode.Parse(stdout)!["hookSpecificOutput"]!["decision"]!;
        await Assert.That(decision["behavior"]!.GetValue<string>()).IsEqualTo("deny");
        var events = _server.FindLogEntries(Request.Create().WithPath("/hooks/policy-decision").UsingPost());
        await Assert.That(events.Count).IsEqualTo(1);
        var snapshots = _server.FindLogEntries(Request.Create().WithPath("/hooks/policy-snapshot").UsingPost());
        await Assert.That(snapshots.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Pre_tool_use_allows_a_fully_covered_command() {
        _server.Given(Request.Create().UsingPost()).RespondWith(Response.Create().WithStatusCode(200));
        File.WriteAllText(Config.Root.Path("approvals.yaml"),
            "version: 1\nrules:\n  - match: { kind: shell, command: \"git status *\" }\n    outcome: allow\n");
        var payload = $$"""
            {"hook_event_name":"PreToolUse","session_id":"{{Sid}}","tool_name":"Bash",
             "tool_input":{"command":"git status"},"cwd":"{{Config.Directory}}"}
            """;
        var (exit, stdout) = await RunHookAsync(payload);
        await Assert.That(exit).IsEqualTo(0);
        var hso = JsonNode.Parse(stdout)!["hookSpecificOutput"]!;
        await Assert.That(hso["permissionDecision"]!.GetValue<string>()).IsEqualTo("allow");
    }

    [Test]
    public async Task No_policy_hook_stays_silent() {
        _server.Given(Request.Create().UsingPost()).RespondWith(Response.Create().WithStatusCode(200));
        var payload = $$"""
            {"hook_event_name":"PreToolUse","session_id":"{{Sid}}","tool_name":"Bash",
             "tool_input":{"command":"anything"},"cwd":"{{Config.Directory}}"}
            """;
        var (exit, stdout) = await RunHookAsync(payload);
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(stdout).IsEmpty();
    }
}
```

Two mechanics to verify while writing: (1) `Config.Directory` as `cwd` sits outside any git repo, so `GitRepository.FindRoot` returns null and only the user scope loads — which is the intent; (2) if the SessionStart auth nudge or another stdout writer fires on these events, assert on the parsed `hookSpecificOutput` member rather than full-string equality (the no-policy case may need `DoesNotContain("hookSpecificOutput")` instead of `IsEmpty` — tighten to whatever the real lane emits).

- [ ] **Step 2: Run to verify failure** (before Tasks 12–14 land this fails; in execution order it should pass immediately — run it to prove the wiring, and treat a failure as a real defect in the seams)

Run: `dotnet run --project test/Capacitor.Cli.Tests.Integration/Capacitor.Cli.Tests.Integration.csproj -- --treenode-filter '/*/*/PolicyHookDecisionTests/*'`

- [ ] **Step 3: Fix anything the spawn surfaces** (URL policy, stdout framing, spool drain interference — the throttle stamp keeps the entry drain quiet).

- [ ] **Step 4: Run the full solution**

Run: `dotnet test --solution Capacitor.slnx`
Expected: green (modulo the locally pre-existing session-start nudge failures documented in the team memory).

- [ ] **Step 5: Commit**

```bash
git add test/Capacitor.Cli.Tests.Integration/PolicyHookDecisionTests.cs
git commit -m "Prove the Claude decision path end-to-end through the binary (#738)"
```

---

### Task 19: Docs, README, and the AOT gate

**Files:**
- Modify: `README.md` (new "Approval policy" section under the CLI docs; mention `.kcap/approvals.yaml` + `~/.config/kcap/approvals.yaml`, the three outcomes, the pass-through default, and the YAML subset — user-facing CLI-surface changes must land in the same PR as the code, per the project's repeated-miss warning)
- Modify: `docs/CHANGES.md` (one entry: the invariants a future change could silently undo — unanalyzed shell never allow-eligible; allow requires full coverage and exact token counts without a trailing `*`; local documents cannot carry `caps`/`enforcement`; pass-through means silence; rendered local seams are tighten-only)
- Modify: `docs/superpowers/specs/2026-09-01-auto-approve-policy-design.md` (only if implementation forced a documented deviation — record it, don't silently diverge)

**Steps:**

- [ ] **Step 1: Write the README section and CHANGES entry** (README example = the spec's example policy file, trimmed to rules-only since the judge is not live yet; state plainly that `judge:` is accepted but inert until the server-side classifier ships).

- [ ] **Step 2: AOT publish gate**

Run: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`
Expected: no output. Any IL3050/IL2026 here traces to a serialization call missing a source-gen registration — fix the registration, never suppress.

- [ ] **Step 3: Full solution run**

Run: `dotnet test --solution Capacitor.slnx`
Expected: green.

- [ ] **Step 4: Commit**

```bash
git add README.md docs/CHANGES.md
git commit -m "Document the approvals policy files and their invariants (#738)"
```

---

### Task 20: PreToolUse seam certification (human gate)

**Files:** none in-repo (findings land as a comment on kcap-cli#738; a failed certification also flips `ClaudePolicySeam.PreToolUseAskEnabled` and re-runs Task 12's tests).

This is the phase-1 exit gate from the spec: the PreToolUse seam must be certified against a **live** Claude Code before the capability is relied on. It needs a human-driven session — schedule it with Alexey. The checklist:

- [ ] **Step 1: Stage a scratch repo with a policy**

```bash
mkdir -p /tmp/kcap-seam-cert/.kcap && cd /tmp/kcap-seam-cert && git init
cat > .kcap/approvals.yaml <<'EOF'
version: 1
rules:
  - match: { kind: shell, command: "touch denied-file" }
    outcome: deny
    reason: certification deny probe
  - match: { kind: shell, command: "touch asked-file" }
    outcome: ask
  - match: { kind: shell, command: "touch allowed-file" }
    outcome: allow
EOF
```

- [ ] **Step 2: Capture raw payloads.** Temporarily add a capture hook beside kcap's in the *local* Claude settings (`~/.claude/settings.json`, `hooks.PreToolUse` and `hooks.PermissionRequest`): `"command": "tee -a /tmp/kcap-seam-cert/payloads.jsonl >/dev/null"`. Run a Claude session in the scratch repo, have it run the three `touch` probes, answer prompts by hand, remove the capture hook.

- [ ] **Step 3: Verify against the seam model**, recording each answer in a #738 comment:
  1. Does the PreToolUse payload carry `tool_use_id` (or any stable call id)? Does the PermissionRequest payload carry the same id? (Both yes → exact journal mode is live; note the field name if it differs from `tool_use_id` and adjust the seam's reader.)
  2. Does a `deny` decision block the call with the reason surfaced to the agent, without Claude's own prompt?
  3. Does an `ask` decision force exactly one permission prompt, and does that prompt fire kcap's PermissionRequest hook exactly once? (This is the FIFO fallback's soundness condition. No → set `PreToolUseAskEnabled = false`, adjust Task 12's ask tests to the degrade branch, commit as `Degrade PreToolUse ask pending a sound correlation path (#738)`.)
  4. Does an `allow` decision skip Claude's permission check while its inner safety still applies?
  5. Is a policy-denied call visible in the session recording (the `policy-decision` event arrived server-side or spooled)?

- [ ] **Step 4: Record the outcome** — comment on kcap-cli#738 with the five answers and the captured payload field list (not the payload bodies — they may contain repo content). If everything certifies, note "PreToolUse seam certified" so phase-4 vendor work inherits the method.

---

## Execution notes

- Tasks 1–11 are pure Core/CLI plumbing with no behavioral risk; 12–14 change the live Claude hook lane (fail-open discipline is load-bearing at every early-return); 15–17 are the daemon insertions; 18–19 gate the phase; 20 needs Alexey.
- Task order is dependency order. Within 12–14, the seams are inert until a policy file exists, so partial states are shippable.
- The branch is `auto-approve-policy-design`; every commit references `#738`; draft PR #741 is the implementation PR — mark it ready only after Task 19, and leave Task 20's certification status in the PR description's Verification section.

