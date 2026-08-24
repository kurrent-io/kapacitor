using System.Text.Json;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// The <c>kcap daemon shim ensure</c> ladder: a fresh login-shell probe decides already-on-path /
/// install / fail-closed refusal (pure classifier rows), and the verb's JSON contract carries the
/// outcome + coded reason the flow keys off. The installer's own mechanics
/// (preflight taxonomy, osascript, post-install re-probe) are covered in Core.Tests — here the
/// install arm is driven through the injected seams so the verb's wire contract is testable without
/// a real admin prompt.
/// </summary>
[NotInParallel] // most tests capture Console (process-global); the dispatch test writes usage to the real stderr
public class DaemonShimCommandsTests {
    sealed class FakeProbe(bool? onPath) : ILoginShellProbe {
        public Task<string?> TerminalPathAsync(CancellationToken ct) => Task.FromResult<string?>("/usr/bin:/bin");
        public Task<bool?> KcapOnPathAsync(CancellationToken ct, bool forceRefresh = false) => Task.FromResult(onPath);
        public Task<string?> KcapPathAsync(CancellationToken ct, bool forceRefresh = false) => Task.FromResult<string?>(null);
    }

    static async Task<(int Exit, ShimEnsureJson? Json, string? Out)> Run(
            string[] args, bool? onPath = null, string? target = "/usr/local/lib/kcap",
            Func<string, CancellationToken, Task<ShimResult>>? install = null,
            Func<string, ShimPreflight>? preflight = null, bool isMacOs = false) {
        using var capture = ConsoleOutput.StartFullCapture();
        var exit = await DaemonShimCommands.Ensure(args, resolveTarget: () => target,
            probe: new FakeProbe(onPath), install: install, preflight: preflight, isMacOs: isMacOs);
        var text = capture.GetCapturedOutput() + capture.GetCapturedError();
        ShimEnsureJson? json = null;
        if (text.Contains("{\"capability\"")) json = JsonSerializer.Deserialize<ShimEnsureJson>(text, ShimJsonContext.Default.ShimEnsureJson);
        return (exit, json, text);
    }

    // --- Classifier rows ---

    [Test]
    public async Task Classify_already_on_path_is_terminal_when_probe_finds_kcap() {
        var d = ShimEnsureClassifier.Classify(onPath: true, isMacOs: true);
        await Assert.That(d.Action).IsEqualTo(ShimEnsureAction.AlreadyOnPath);
        await Assert.That(d.Reason).IsNull();
    }

    [Test]
    public async Task Classify_installable_only_on_macos_with_positive_absence() {
        var d = ShimEnsureClassifier.Classify(onPath: false, isMacOs: true);
        await Assert.That(d.Action).IsEqualTo(ShimEnsureAction.Install);
    }

    [Test]
    public async Task Classify_unknown_probe_fails_closed_even_on_macos() {
        var d = ShimEnsureClassifier.Classify(onPath: null, isMacOs: true);
        await Assert.That(d.Action).IsEqualTo(ShimEnsureAction.Refuse);
        await Assert.That(d.Reason).IsEqualTo("probe_unknown");
    }

    [Test]
    public async Task Classify_off_macos_refuses_even_on_positive_absence() {
        var d = ShimEnsureClassifier.Classify(onPath: false, isMacOs: false);
        await Assert.That(d.Action).IsEqualTo(ShimEnsureAction.Refuse);
        await Assert.That(d.Reason).IsEqualTo("unsupported_platform");
    }

    [Test]
    public async Task Classify_already_on_path_is_terminal_off_macos_too() {
        var d = ShimEnsureClassifier.Classify(onPath: true, isMacOs: false);
        await Assert.That(d.Action).IsEqualTo(ShimEnsureAction.AlreadyOnPath);
    }

    // --- Verb ladder / JSON contract ---

    [Test]
    public async Task Ensure_already_on_path_exits_zero_and_reports_already_on_path() {
        var (exit, json, _) = await Run(["--json"], onPath: true);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(json).IsNotNull();
        await Assert.That(json!.Outcome).IsEqualTo("already_on_path");
        await Assert.That(json.OnPath).IsTrue();
        await Assert.That(json.Action).IsEqualTo("none");
    }

    [Test]
    public async Task Ensure_probe_unknown_fails_closed_with_coded_reason() {
        var (exit, json, _) = await Run(["--json"], onPath: null);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(json).IsNotNull();
        await Assert.That(json!.Outcome).IsEqualTo("refused");
        await Assert.That(json.Reason).IsEqualTo("probe_unknown");
    }

    [Test]
    public async Task Ensure_off_macos_refuses_with_unsupported_platform() {
        var (exit, json, _) = await Run(["--json"], onPath: false, isMacOs: false);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(json).IsNotNull();
        await Assert.That(json!.Outcome).IsEqualTo("refused");
        await Assert.That(json.Reason).IsEqualTo("unsupported_platform");
    }

    [Test]
    public async Task Ensure_no_cli_path_refuses_before_any_probe() {
        var (exit, json, _) = await Run(["--json"], target: null);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(json).IsNotNull();
        await Assert.That(json!.Outcome).IsEqualTo("refused");
        await Assert.That(json.Reason).IsEqualTo("no_cli_path");
        await Assert.That(json.Target).IsNull();
    }

    [Test]
    public async Task Ensure_installed_exits_zero_with_installed_outcome() {
        var (exit, json, _) = await Run(["--json"], onPath: false, isMacOs: true,
            install: (_, _) => Task.FromResult(new ShimResult(ShimOutcome.Installed, null, null)));

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(json).IsNotNull();
        await Assert.That(json!.Outcome).IsEqualTo("installed");
        await Assert.That(json.OnPath).IsTrue();
        await Assert.That(json.Action).IsEqualTo("install");
    }

    [Test]
    public async Task Ensure_installed_but_not_on_path_exits_nonzero_with_detail() {
        var (exit, json, _) = await Run(["--json"], onPath: false, isMacOs: true,
            install: (_, _) => Task.FromResult(new ShimResult(ShimOutcome.InstalledButNotOnPath, "Add the line", null)));

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(json).IsNotNull();
        await Assert.That(json!.Outcome).IsEqualTo("installed_not_on_path");
        await Assert.That(json.Detail).IsEqualTo("Add the line");
        await Assert.That(json.OnPath).IsFalse();
    }

    [Test]
    public async Task Ensure_cancelled_reports_cancelled() {
        var (exit, json, _) = await Run(["--json"], onPath: false, isMacOs: true,
            install: (_, _) => Task.FromResult(new ShimResult(ShimOutcome.Cancelled, null, null)));

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(json).IsNotNull();
        await Assert.That(json!.Outcome).IsEqualTo("cancelled");
    }

    [Test]
    public async Task Ensure_failed_carries_detail_and_sudo_fallback() {
        var (exit, json, _) = await Run(["--json"], onPath: false, isMacOs: true,
            install: (_, _) => Task.FromResult(new ShimResult(ShimOutcome.Failed, "osascript failed", "sudo ln -s ...")));

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(json).IsNotNull();
        await Assert.That(json!.Outcome).IsEqualTo("failed");
        await Assert.That(json.Detail).IsEqualTo("osascript failed");
        await Assert.That(json.SudoFallback).IsEqualTo("sudo ln -s ...");
    }

    [Test]
    public async Task Ensure_failed_without_sudo_fallback_still_exits_nonzero() {
        var (exit, json, _) = await Run(["--json"], onPath: false, isMacOs: true,
            install: (_, _) => Task.FromResult(new ShimResult(ShimOutcome.Failed, "could not re-verify", null)));

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(json).IsNotNull();
        await Assert.That(json!.Outcome).IsEqualTo("failed");
        await Assert.That(json.Detail).IsEqualTo("could not re-verify");
        await Assert.That(json.SudoFallback).IsNull();
        // A null re-probe means the PATH was never positively re-read — OnPath must not claim false.
        await Assert.That(json.OnPath).IsNull();
    }

    [Test]
    public async Task Ensure_conflict_preflight_is_a_coded_refusal_with_what_was_found() {
        var (exit, json, _) = await Run(["--json"], onPath: false, isMacOs: true,
            preflight: _ => ShimPreflight.Conflict);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(json).IsNotNull();
        await Assert.That(json!.Outcome).IsEqualTo("refused");
        await Assert.That(json.Reason).IsEqualTo("conflict");
        await Assert.That(json.Detail).Contains("left untouched");
    }

    [Test]
    public async Task Ensure_unknown_flag_is_rejected_before_any_probe_or_prompt() {
        var (exit, _, text) = await Run(["--bogus"], onPath: false, isMacOs: true,
            install: (_, _) => throw new InvalidOperationException("the install seam must not run"));
        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(text).Contains("Usage: kcap daemon shim");
    }

    // --- Human output (no --json) ---

    [Test]
    public async Task Ensure_human_output_states_already_on_path_without_json() {
        var (exit, _, text) = await Run([], onPath: true);
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(text).Contains("already on your terminal PATH");
    }

    [Test]
    public async Task Ensure_human_refusal_prints_the_coded_reason_line() {
        var (exit, _, text) = await Run([], onPath: null);
        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(text).Contains("Could not determine whether kcap is on your terminal PATH");
    }

    [Test]
    public async Task Ensure_human_installed_does_not_claim_a_fresh_link() {
        var (exit, _, text) = await Run([], onPath: false, isMacOs: true,
            install: (_, _) => Task.FromResult(new ShimResult(ShimOutcome.Installed, null, null)));
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(text).Contains("kcap is now on your terminal PATH");
        await Assert.That(text).DoesNotContain("Linked");
    }

    [Test]
    public async Task Ensure_human_installed_not_on_path_prints_the_actionable_detail() {
        var (exit, _, text) = await Run([], onPath: false, isMacOs: true,
            install: (_, _) => Task.FromResult(new ShimResult(ShimOutcome.InstalledButNotOnPath, "Add: export PATH=\"/usr/local/bin:$PATH\"", null)));
        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(text).Contains("export PATH");
    }

    [Test]
    public async Task Ensure_human_cancelled_prints_the_cancel_line() {
        var (exit, _, text) = await Run([], onPath: false, isMacOs: true,
            install: (_, _) => Task.FromResult(new ShimResult(ShimOutcome.Cancelled, null, null)));
        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(text).Contains("canceled");
    }

    [Test]
    public async Task Ensure_human_failed_prints_detail_and_sudo_fallback() {
        var (exit, _, text) = await Run([], onPath: false, isMacOs: true,
            install: (_, _) => Task.FromResult(new ShimResult(ShimOutcome.Failed, "osascript failed", "sudo ln -s ...")));
        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(text).Contains("osascript failed");
        await Assert.That(text).Contains("sudo ln -s ...");
    }

    [Test]
    public async Task Dispatch_unknown_verb_prints_usage() {
        var exit = await DaemonShimCommands.DispatchAsync(["bogus"]);
        await Assert.That(exit).IsEqualTo(1);
    }
}
