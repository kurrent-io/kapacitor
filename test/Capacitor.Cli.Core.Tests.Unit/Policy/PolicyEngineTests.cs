namespace Capacitor.Cli.Core.Tests.Unit.Policy;

using System.Text.Json;
using Capacitor.Cli.Core.Harness.Claude;
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

    static JsonElement Fetch(string url) =>
        JsonDocument.Parse($$"""{"url":"{{url}}"}""").RootElement.Clone();

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
    public async Task Full_coverage_by_one_rule_dedupes_matched_refs() {
        var eval = PolicyEngine.Evaluate(Snap((PolicyScope.User, UserDoc)),
            Bash("git status && git diff --stat"), EvaluationMode.Full);
        await Assert.That(eval.Outcome).IsEqualTo(PolicyOutcome.Allow);
        await Assert.That(eval.MatchedRules.Count).IsEqualTo(1);       // both segments hit rule index 2
    }

    [Test]
    public async Task Full_coverage_across_different_rules_and_scopes_authorizes() {
        var repoStatusAllow = "version: 1\nrules:\n  - match: { kind: shell, command: \"git status *\" }\n    outcome: allow\n";
        var userDiffAllow = "version: 1\nrules:\n  - match: { kind: shell, command: \"git diff *\" }\n    outcome: allow\n";
        var eval = PolicyEngine.Evaluate(
            Snap((PolicyScope.Repo, repoStatusAllow), (PolicyScope.User, userDiffAllow)),
            Bash("git status --porcelain && git diff --stat"), EvaluationMode.Full);
        await Assert.That(eval.Outcome).IsEqualTo(PolicyOutcome.Allow);
        await Assert.That(eval.MatchedRules.Count).IsEqualTo(2);
        await Assert.That(eval.MatchedRules[0].Scope).IsEqualTo(PolicyScope.Repo);
        await Assert.That(eval.MatchedRules[1].Scope).IsEqualTo(PolicyScope.User);
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

    /// `env -S` re-splits its literal argument into a command line, so an `env *` allow must not
    /// reach the shell hiding in that token — while ordinary `env` use stays allow-eligible.
    [Test]
    public async Task Env_allow_does_not_authorize_a_split_string_argument() {
        var doc = "version: 1\nrules:\n  - match: { kind: shell, command: \"env *\" }\n    outcome: allow\n";
        var snap = Snap((PolicyScope.User, doc));
        var hidden = PolicyEngine.Evaluate(snap, Bash("env -S 'bash -c \"rm -rf /tmp/x\"'"), EvaluationMode.Full);
        await Assert.That(hidden.Outcome).IsEqualTo(PolicyOutcome.None);
        var plain = PolicyEngine.Evaluate(snap, Bash("env FOO=1 git status"), EvaluationMode.Full);
        await Assert.That(plain.Outcome).IsEqualTo(PolicyOutcome.Allow);
    }

    /// The boundary for an interpreter the shell-name list does not carry: a universal allow
    /// authorizes it exactly as it authorizes any other analyzed program, and still cannot reach a
    /// command the analyzer refused.
    [Test]
    public async Task Universal_shell_allow_covers_an_unlisted_interpreter_but_not_an_unanalyzed_command() {
        var doc = "version: 1\nrules:\n  - match: { kind: shell }\n    outcome: allow\n";
        var snap = Snap((PolicyScope.User, doc));
        var unlisted = PolicyEngine.Evaluate(snap, Bash("someshell -c script.txt"), EvaluationMode.Full);
        await Assert.That(unlisted.Outcome).IsEqualTo(PolicyOutcome.Allow);
        var known = PolicyEngine.Evaluate(snap, Bash("bash -c x"), EvaluationMode.Full);
        await Assert.That(known.Outcome).IsEqualTo(PolicyOutcome.None);
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

        var repoDeny = "version: 1\nrules:\n  - match: { kind: shell, command: \"npm publish\" }\n    outcome: deny\n";
        var userAllow = "version: 1\nrules:\n  - match: { kind: shell, command: \"npm publish *\" }\n    outcome: allow\n";
        var reversed = PolicyEngine.Evaluate(Snap((PolicyScope.Repo, repoDeny), (PolicyScope.User, userAllow)),
            Bash("npm publish --tag next"), EvaluationMode.Full);
        await Assert.That(reversed.Outcome).IsEqualTo(PolicyOutcome.Deny);
        await Assert.That(reversed.MatchedRules[0].Scope).IsEqualTo(PolicyScope.Repo);
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

    /// The rule names the port the operator would write; the URL omits it. Both must meet, or a
    /// port-scoped ask over an ordinary https URL never fires.
    [Test]
    public async Task Port_scoped_rule_matches_a_url_that_omits_the_default_port() {
        var doc = """
            version: 1
            rules:
              - match: { kind: network, host: "registry.example", port: 443 }
                outcome: ask
              - match: { kind: network, host: "blocked.example", port: 443 }
                outcome: deny
            """;
        var snap = Snap((PolicyScope.User, doc));
        var ask = ClaudeActionNormalizer.Normalize("WebFetch", Fetch("https://registry.example/"), null);
        await Assert.That(PolicyEngine.Evaluate(snap, ask, EvaluationMode.Full).Outcome).IsEqualTo(PolicyOutcome.Ask);
        var deny = ClaudeActionNormalizer.Normalize("WebFetch", Fetch("https://blocked.example:443/x"), null);
        await Assert.That(PolicyEngine.Evaluate(snap, deny, EvaluationMode.Full).Outcome).IsEqualTo(PolicyOutcome.Deny);
    }

    [Test]
    public async Task Path_ask_matches_glob_against_the_absolute_path() {
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
