using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Daemon.Acp;

namespace Capacitor.Cli.Tests.Unit.Acp;

/// <summary>
/// Unlike <see cref="KiroReviewerHomeTests"/>'s target, this home is not empty at spawn — it carries
/// a written <c>mcp_config.json</c> for the injected servers. The absence of the kcap plugin
/// directory is what keeps capture single-lane (a probe confirmed <c>agy -p</c> loads and fires it
/// when present), so that absence is asserted directly rather than inferred from "we wrote nothing".
/// </summary>
public class AntigravityReviewerHomeTests {
    static readonly AcpMcpServerSpec ResultChannel =
        new("kcap-flow-result", "kcap", ["mcp", "flow-result"], []);

    [Test]
    public async Task The_home_carries_only_the_injected_mcp_server() {
        if (OperatingSystem.IsWindows()) return;
        using var root = new TempDir();
        var home = AntigravityReviewerHome.Create(
            root.Path, "epoch1", "agent1", [ResultChannel], grantInjectedMcpTools: true);

        var mcp = Path.Combine(home, ".gemini", "config", "mcp_config.json");
        await Assert.That(File.Exists(mcp)).IsTrue();
        await Assert.That(await File.ReadAllTextAsync(mcp)).Contains("kcap-flow-result");

        // The kcap plugin dir must NOT exist — its absence is what keeps capture single-lane.
        // If a future change seeds a fuller home, this is the assertion that catches it.
        await Assert.That(Directory.Exists(Path.Combine(home, ".gemini", "config", "plugins"))).IsFalse();
    }

    [Test]
    public async Task The_home_is_owner_only_from_creation() {
        if (OperatingSystem.IsWindows()) return;
        using var root = new TempDir();
        var home = AntigravityReviewerHome.Create(root.Path, "epoch1", "agent1", [], grantInjectedMcpTools: true);
        var mode = File.GetUnixFileMode(home);

        await Assert.That(mode.HasFlag(UnixFileMode.GroupRead)).IsFalse();
        await Assert.That(mode.HasFlag(UnixFileMode.OtherRead)).IsFalse();
    }

    [Test]
    public async Task Sweep_removes_foreign_epochs_and_keeps_the_current_one() {
        if (OperatingSystem.IsWindows()) return;
        using var root = new TempDir();
        var stale   = AntigravityReviewerHome.Create(root.Path, "old",     "a1", [], grantInjectedMcpTools: true);
        var current = AntigravityReviewerHome.Create(root.Path, "current", "a2", [], grantInjectedMcpTools: true);

        AntigravityReviewerHome.SweepStale(root.Path, "current");

        await Assert.That(Directory.Exists(stale)).IsFalse();
        await Assert.That(Directory.Exists(current)).IsTrue();
    }

    // ── permissions.allow: the reviewer can still DO ITS JOB ───────────────────────────────────────
    //
    // Everything above certifies what the reviewer cannot do. That is perfectly consistent with a
    // reviewer that never works: `agy -p` auto-denies every tool confirmation it raises, the result
    // channel IS an MCP tool, and the shipped home granted nothing — so the one call a round depends
    // on was the one call print mode refused, and every round hung until the flow timed out. These
    // assert the home grants its own result channel.

    /// <summary>Reads the launch's own settings.json by LITERAL path.
    /// <c>AntigravityPaths.CliSettingsJson</c> honours the TEST PROCESS's <c>GEMINI_CLI_HOME</c>, so it
    /// could read somewhere the launch never wrote.</summary>
    static string[] AllowRulesIn(string home) {
        var path = Path.Combine(home, ".gemini", "antigravity-cli", "settings.json");

        if (!File.Exists(path))
            throw new FileNotFoundException($"The home wrote no settings.json under '{home}'.", path);

        using var doc = JsonDocument.Parse(File.ReadAllText(path));

        return [.. doc.RootElement.GetProperty("permissions").GetProperty("allow")
                    .EnumerateArray().Select(e => e.GetString()!)];
    }

    static bool HasSettings(string home) =>
        File.Exists(Path.Combine(home, ".gemini", "antigravity-cli", "settings.json"));

    /// <summary>
    /// The regression this whole section exists for. Named exactly as agy names it on the denial —
    /// <c>kcap-flow-result/submit_review_result</c> — so the rule and the identity it admits are the
    /// same string.
    /// </summary>
    [Test]
    public async Task The_home_grants_the_reviewers_own_result_channel() {
        if (OperatingSystem.IsWindows()) return;
        using var root = new TempDir();

        var home = AntigravityReviewerHome.Create(
            root.Path, "epoch1", "agent1", [ResultChannel], grantInjectedMcpTools: true);

        await Assert.That(AllowRulesIn(home)).Contains("mcp(kcap-flow-result/submit_review_result)");
    }

    /// <summary>The channel serves more than the submit tool, and a reviewer that cannot call
    /// <c>send_flow_message</c> loses the out-of-band message lane the same silent way.</summary>
    [Test]
    public async Task The_grant_covers_every_unattended_safe_tool_the_channel_serves() {
        if (OperatingSystem.IsWindows()) return;
        using var root = new TempDir();

        var home = AntigravityReviewerHome.Create(
            root.Path, "epoch1", "agent1", [ResultChannel], grantInjectedMcpTools: true);

        var rules = AllowRulesIn(home);

        foreach (var tool in KcapMcpRegistry.ReservedResultChannelUnattendedSafeTools)
            await Assert.That(rules).Contains($"mcp(kcap-flow-result/{tool})");
    }

    /// <summary>
    /// The narrowest thing that works, asserted as an EXACT set rather than a containment: a
    /// <c>mcp(kcap-flow-result/*)</c> or <c>mcp(*)</c> would satisfy every "contains" assertion above
    /// while granting whatever the channel serves next with no reviewed decision. The exact pair was
    /// measured to work against a real agy, so nothing buys the wider form.
    /// </summary>
    [Test]
    public async Task The_grant_is_exactly_the_classified_tools_and_never_a_wildcard() {
        if (OperatingSystem.IsWindows()) return;
        using var root = new TempDir();

        var home = AntigravityReviewerHome.Create(
            root.Path, "epoch1", "agent1", [ResultChannel], grantInjectedMcpTools: true);

        var expected = KcapMcpRegistry.ReservedResultChannelUnattendedSafeTools
            .Select(t => $"mcp(kcap-flow-result/{t})")
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToArray();

        await Assert.That(AllowRulesIn(home).OrderBy(r => r, StringComparer.Ordinal).ToArray())
                    .IsEquivalentTo(expected);

        foreach (var rule in AllowRulesIn(home))
            await Assert.That(rule).DoesNotContain("*");
    }

    /// <summary>
    /// A reviewer given a read-only server it can never call is the same bug in a second place — the
    /// allowlist servers are injected into the same mcp_config.json and hit the same auto-deny.
    /// </summary>
    [Test]
    public async Task An_allowlisted_read_only_server_is_granted_its_own_tools_too() {
        if (OperatingSystem.IsWindows()) return;
        using var root = new TempDir();

        var home = AntigravityReviewerHome.Create(
            root.Path, "epoch1", "agent1",
            [ResultChannel, new AcpMcpServerSpec("kcap-review", "kcap", ["mcp", "review"], [])],
            grantInjectedMcpTools: true);

        var rules = AllowRulesIn(home);

        foreach (var tool in KcapMcpRegistry.ReviewFlowUnattendedSafeTools["kcap-review"])
            await Assert.That(rules).Contains($"mcp(kcap-review/{tool})");

        // Still the channel's own, so granting the allowlist cannot have replaced it.
        await Assert.That(rules).Contains("mcp(kcap-flow-result/submit_review_result)");
    }

    /// <summary>A hosted launch already runs with <c>--dangerously-skip-permissions</c>, so a rule for
    /// it would be dead config — and its injected list is a CALLER's, which the fail-closed classifier
    /// below would refuse outright.</summary>
    [Test]
    public async Task A_hosted_launch_home_carries_no_permission_grants() {
        if (OperatingSystem.IsWindows()) return;
        using var root = new TempDir();

        var home = AntigravityReviewerHome.Create(
            root.Path, "epoch1", "agent1",
            [new AcpMcpServerSpec("caller-supplied", "/bin/echo", ["hi"], [])],
            grantInjectedMcpTools: false);

        await Assert.That(HasSettings(home)).IsFalse();
    }

    /// <summary>Fail closed: a server with no classified tools would produce a reviewer auto-denied on
    /// its first call — a wedged round with nothing an operator can act on. The refusal names it.</summary>
    [Test]
    public async Task A_review_grant_for_an_unclassified_server_refuses_the_launch() {
        if (OperatingSystem.IsWindows()) return;
        using var root = new TempDir();

        var ex = Assert.Throws<InvalidOperationException>(() => AntigravityReviewerHome.Create(
            root.Path, "epoch1", "agent1",
            [ResultChannel, new AcpMcpServerSpec("kcap-review-context", "kcap", ["mcp", "review"], [])],
            grantInjectedMcpTools: true));

        await Assert.That(ex!.Message).StartsWith("antigravity_reviewer_permission_unknown_server");
        await Assert.That(ex.Message).Contains("kcap-review-context");
    }

    /// <summary>
    /// Both files this home writes resolve through <c>AntigravityPaths</c> → <c>GeminiPaths.Root</c>,
    /// which honours the DAEMON PROCESS's own <c>GEMINI_CLI_HOME</c> ahead of the home it was handed —
    /// so on a daemon that happens to run with it set, an unguarded write would land the reviewer's
    /// result channel and its permission grants in the operator's own Gemini tree instead of the
    /// isolated home. The guard is shared, and settings.json is the file it was extended to cover.
    /// </summary>
    [Test]
    [NotInParallel("HomeEnvVarMutation")]
    public async Task A_daemon_wide_gemini_cli_home_cannot_redirect_the_homes_writes() {
        if (OperatingSystem.IsWindows()) return;
        using var root     = new TempDir();
        using var elsewhere = new TempDir();

        var previous = Environment.GetEnvironmentVariable("GEMINI_CLI_HOME");

        try {
            Environment.SetEnvironmentVariable("GEMINI_CLI_HOME", elsewhere.Path);

            var ex = Assert.Throws<InvalidOperationException>(() => AntigravityReviewerHome.Create(
                root.Path, "epoch1", "agent1", [ResultChannel], grantInjectedMcpTools: true));

            await Assert.That(ex!.Message).StartsWith("antigravity_reviewer_home_escaped_root");

            // Refused, not merely reported: nothing was written into the operator's tree.
            await Assert.That(Directory.Exists(Path.Combine(elsewhere.Path, ".gemini"))).IsFalse();
        } finally {
            Environment.SetEnvironmentVariable("GEMINI_CLI_HOME", previous);
        }
    }

    sealed class TempDir : IDisposable {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kcap-agy-home-tests-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose() {
            try { Directory.Delete(Path, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
