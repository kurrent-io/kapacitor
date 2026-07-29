using Capacitor.Cli.Daemon.Acp;

namespace Capacitor.Cli.Tests.Unit.Acp;

/// <summary>
/// Where a contained borrowed reviewer's credential comes from.
///
/// <para>The point of brokering is that the sandbox no longer grants <c>~/Library/Keychains</c> — a
/// recursive, credential-bearing tree that was reachable with no ACP interaction frame, so the
/// <c>Fail</c> policy never fired. Verified live: with a brokered token and no keychain grant
/// <c>session/new</c> succeeds, and without one it answers <c>Authentication required</c>, which is
/// what establishes the token is genuinely carrying the authentication rather than something else
/// having cached it.</para>
/// </summary>
public class BorrowedReviewAuthBrokerTests {
    static Func<string, string?> Env(params (string Name, string? Value)[] entries) =>
        name => entries.FirstOrDefault(e => e.Name == name).Value;

    /// <summary>A token command that returns <paramref name="output"/> and records that it ran, so a
    /// test can tell "the command was not consulted" from "it was consulted and produced nothing".</summary>
    static (Func<string, string?> Run, Func<int> Calls) Command(string? output) {
        var calls = 0;

        return (_ => { calls++; return output; }, () => calls);
    }

    [Test]
    public async Task No_configured_variable_resolves_to_null() {
        await Assert.That(BorrowedReviewAuthBroker.TryResolve(Env())).IsNull();
    }

    [Test]
    [Arguments("COPILOT_GITHUB_TOKEN")]
    [Arguments("GH_TOKEN")]
    [Arguments("GITHUB_TOKEN")]
    public async Task Any_single_configured_variable_resolves(string name) {
        await Assert.That(BorrowedReviewAuthBroker.TryResolve(Env((name, "tok-1")))).IsEqualTo("tok-1");
    }

    /// <summary>Precedence matches the vendor's own, so brokering cannot select a different credential
    /// than an unsandboxed run would have used — a reviewer authenticating as a different identity
    /// than the user expects is its own kind of surprise.</summary>
    [Test]
    public async Task Resolution_follows_the_vendors_own_precedence_order() {
        var all = BorrowedReviewAuthBroker.TryResolve(Env(
            ("COPILOT_GITHUB_TOKEN", "vendor-specific"),
            ("GH_TOKEN",             "gh"),
            ("GITHUB_TOKEN",         "github")));
        var withoutVendorSpecific = BorrowedReviewAuthBroker.TryResolve(Env(
            ("GH_TOKEN",     "gh"),
            ("GITHUB_TOKEN", "github")));

        await Assert.That(all).IsEqualTo("vendor-specific");
        await Assert.That(withoutVendorSpecific).IsEqualTo("gh");
        await Assert.That(BorrowedReviewAuthBroker.SourceVariables.ToArray()).IsEquivalentTo(
            ["COPILOT_GITHUB_TOKEN", "GH_TOKEN", "GITHUB_TOKEN"]);
    }

    /// <summary>A variable set to whitespace is not a credential. Treating it as one would advertise
    /// borrowed review on a daemon that cannot authenticate, which is the failure mode gating support
    /// on the broker exists to avoid.</summary>
    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("\t")]
    public async Task A_blank_variable_is_not_a_credential(string blank) {
        await Assert.That(BorrowedReviewAuthBroker.TryResolve(Env(("GH_TOKEN", blank)))).IsNull();
    }

    [Test]
    public async Task A_blank_variable_does_not_shadow_a_real_one_later_in_the_order() {
        var resolved = BorrowedReviewAuthBroker.TryResolve(Env(
            ("COPILOT_GITHUB_TOKEN", ""),
            ("GH_TOKEN",             "real")));

        await Assert.That(resolved).IsEqualTo("real");
    }

    // ── token command (the supervised-daemon path) ───────────────────────────────────────────────
    //
    // A service unit is a file on disk, so the token cannot live there. The unit carries a COMMAND
    // that prints one instead, which is not a secret.

    [Test]
    public async Task A_command_supplies_the_token_when_no_variable_does() {
        var (run, calls) = Command("from-command");

        var resolved = BorrowedReviewAuthBroker.TryResolve(
            Env((BorrowedReviewAuthBroker.CommandVariable, "print-token")), run);

        await Assert.That(resolved).IsEqualTo("from-command");
        await Assert.That(calls()).IsEqualTo(1);
    }

    /// <summary>A directly-set variable wins, and the command is NOT run. Asserting the call count is
    /// the point: preferring the variable is only meaningful if the command is not also executed, since
    /// running it could prompt, cost money, or mint a credential nobody asked for.</summary>
    [Test]
    public async Task A_directly_set_variable_wins_and_the_command_is_not_run() {
        var (run, calls) = Command("from-command");

        var resolved = BorrowedReviewAuthBroker.TryResolve(
            Env(("GH_TOKEN", "from-variable"),
                (BorrowedReviewAuthBroker.CommandVariable, "print-token")), run);

        await Assert.That(resolved).IsEqualTo("from-variable");
        await Assert.That(calls()).IsEqualTo(0);
    }

    /// <summary>A command that produces nothing usable is indistinguishable from no token at all, so a
    /// broken one degrades to the same honest not-advertised state rather than its own failure mode.</summary>
    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task A_command_producing_nothing_usable_resolves_to_null(string? output) {
        var (run, _) = Command(output);

        await Assert.That(BorrowedReviewAuthBroker.TryResolve(
            Env((BorrowedReviewAuthBroker.CommandVariable, "print-token")), run)).IsNull();
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    public async Task A_blank_command_is_not_run(string commandLine) {
        var (run, calls) = Command("from-command");

        await Assert.That(BorrowedReviewAuthBroker.TryResolve(
            Env((BorrowedReviewAuthBroker.CommandVariable, commandLine)), run)).IsNull();
        await Assert.That(calls()).IsEqualTo(0);
    }

    // ── the real runner ──────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task The_real_runner_returns_what_the_command_printed() {
        Skip.When(OperatingSystem.IsWindows(), "POSIX shell command");

        await Assert.That(BorrowedReviewTokenCommand.Run("printf 'tok-abc\\n'")).IsEqualTo("tok-abc");
    }

    /// <summary>Trailing newlines and follow-on lines are stripped: `gh auth token` emits a newline, and
    /// a token is never multi-line, so anything after the first line is noise rather than credential.</summary>
    [Test]
    public async Task The_real_runner_takes_the_first_non_empty_line_only() {
        Skip.When(OperatingSystem.IsWindows(), "POSIX shell command");

        await Assert.That(BorrowedReviewTokenCommand.Run("printf '\\n  tok-xyz  \\nnoise\\n'"))
            .IsEqualTo("tok-xyz");
    }

    /// <summary>A failing command yields null rather than throwing — this runs inside a static
    /// initializer on the daemon's startup path, where an escaping exception would take the daemon
    /// down.</summary>
    [Test]
    [Arguments("exit 1")]
    [Arguments("printf 'tok\\n'; exit 3")]
    [Arguments("this-command-does-not-exist-9f7c99")]
    public async Task The_real_runner_treats_a_failing_command_as_no_token(string commandLine) {
        Skip.When(OperatingSystem.IsWindows(), "POSIX shell command");

        await Assert.That(BorrowedReviewTokenCommand.Run(commandLine)).IsNull();
    }

    /// <summary>A command that prints a secret to stderr and fails must not have that secret surface
    /// anywhere. The runner returns only null, so there is no channel for it — asserted because the
    /// obvious "helpful" change is to include stderr in a diagnostic, and a credential command is
    /// exactly the thing likeliest to print one there.</summary>
    [Test]
    public async Task The_real_runner_never_surfaces_command_output_on_failure() {
        Skip.When(OperatingSystem.IsWindows(), "POSIX shell command");

        await Assert.That(BorrowedReviewTokenCommand.Run("echo LEAKED-SECRET-abc123 1>&2; exit 1")).IsNull();
    }

    /// <summary>A hanging command is bounded, or it would wedge daemon startup and every review.</summary>
    [Test]
    public async Task The_real_runner_gives_up_on_a_hanging_command() {
        Skip.When(OperatingSystem.IsWindows(), "POSIX shell command");

        var started = DateTime.UtcNow;
        var resolved = BorrowedReviewTokenCommand.Run("sleep 120");
        var elapsed = DateTime.UtcNow - started;

        await Assert.That(resolved).IsNull();
        await Assert.That(elapsed).IsLessThan(BorrowedReviewTokenCommand.Timeout + TimeSpan.FromSeconds(20));
    }

    /// <summary>The PRODUCTION default wires to the real runner.
    ///
    /// <para>Every other command test injects a fake, which is right for asserting precedence and the
    /// blank/failure cases and wrong as the only coverage: it would all stay green if
    /// <c>TryResolve</c>'s default never reached <see cref="BorrowedReviewTokenCommand"/> at all, and a
    /// supervised daemon would then resolve nothing while every test passed.</para></summary>
    [Test]
    public async Task The_production_default_runs_the_real_command() {
        Skip.When(OperatingSystem.IsWindows(), "POSIX shell command");

        // No runCommand argument — exactly what production passes.
        var resolved = BorrowedReviewAuthBroker.TryResolve(
            Env((BorrowedReviewAuthBroker.CommandVariable, "printf 'tok-real\\n'")));

        await Assert.That(resolved).IsEqualTo("tok-real");
    }

    /// <summary>And a directly-set variable still short-circuits the real runner, so the precedence rule
    /// is not an artifact of the fake.</summary>
    [Test]
    public async Task The_production_default_still_prefers_a_directly_set_variable() {
        Skip.When(OperatingSystem.IsWindows(), "POSIX shell command");

        var resolved = BorrowedReviewAuthBroker.TryResolve(
            Env(("GITHUB_TOKEN", "from-variable"),
                (BorrowedReviewAuthBroker.CommandVariable, "printf 'from-command\\n'")));

        await Assert.That(resolved).IsEqualTo("from-variable");
    }
}
