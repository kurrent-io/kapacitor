using System.Reactive.Threading.Tasks;
using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Services;
using Capacitor.App.ViewModels.Onboarding;
using Capacitor.App.Views.Onboarding;
using Capacitor.Cli.Core.Setup;
using TUnit.Assertions.Enums;

namespace Capacitor.App.Tests.Unit;

/// Shared vendor-detection fixture builder for the Agents/Import step tests below.
static class VendorDetection {
    public static AgentDetectionResult Build(params string[] detected) {
        DetectedAgent Agent(string label) => new(detected.Contains(label), false);

        return new(
            Claude: Agent("claude"), Codex: Agent("codex"), Cursor: Agent("cursor"), Copilot: Agent("copilot"),
            Gemini: Agent("gemini"), Kiro: Agent("kiro"), Pi: Agent("pi"), OpenCode: Agent("opencode"),
            Antigravity: Agent("antigravity"), Dsh: Agent("dsh"));
    }
}

/// spec §8/decision 8: the app's detection feed overrides the process PATH with the login-shell
/// terminal PATH when the probe resolves one. Pure static helper — no AvaloniaSession needed.
public class AgentDetectionFeedTests {
    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task Uses_the_probed_terminal_PATH_not_the_process_PATH() {
        Skip.When(OperatingSystem.IsWindows(), "chmod-based executable probe is POSIX-only.");

        using var tmp = new TempDir();
        var emptyDir  = tmp.CreateDir("empty");
        var claudeDir = tmp.CreateDir("claude");
        var claudeBin = claudeDir.PathTo("claude");
        await File.WriteAllTextAsync(claudeBin, "#!/bin/sh\n");
        File.SetUnixFileMode(claudeBin, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        // Same probe, two different crafted PATHs: the outcome must track the probe, not
        // whatever the real process PATH happens to contain on this machine.
        var probe = new FakeLoginShellProbe { TerminalPathBehavior = _ => Task.FromResult<string?>(emptyDir) };
        var withoutClaude = await AgentsStepViewModel.BuildDetectionFeed(probe)(CancellationToken.None);

        probe.TerminalPathBehavior = _ => Task.FromResult<string?>(claudeDir);
        var withClaude = await AgentsStepViewModel.BuildDetectionFeed(probe)(CancellationToken.None);

        await Assert.That(withoutClaude.Claude.Detected).IsFalse();
        await Assert.That(withClaude.Claude.Detected).IsTrue();
    }

    [Test]
    public async Task Falls_back_to_the_process_PATH_when_the_probe_is_inconclusive() {
        var probe = new FakeLoginShellProbe { TerminalPathBehavior = _ => Task.FromResult<string?>(null) };

        var actual   = await AgentsStepViewModel.BuildDetectionFeed(probe)(CancellationToken.None);
        var expected = AgentDetection.Detect(AgentDetection.FromEnvironment());

        await Assert.That(actual).IsEqualTo(expected);
    }
}

/// spec §3 step 5 / decision 8. Owns ReactiveCommands (per-row Retry + Install), so every test
/// runs through the real headless session like ShimStepViewModel/SignInStepViewModel.
public class AgentsStepViewModelTests {
    sealed class Harness {
        public readonly FakeKcapCli Cli = new();
        public readonly AgentDetectionResult Detected;
        public int DetectCallCount;
        public readonly AgentsStepViewModel Vm;

        public Harness(AgentDetectionResult? detected = null) {
            Detected = detected ?? VendorDetection.Build();
            Vm = new AgentsStepViewModel(Cli, ct => {
                DetectCallCount++;
                return Task.FromResult(Detected);
            });
        }

        public AgentVendorRow Row(string label) => Vm.Rows.First(r => r.Label == label);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task OnEnterAsync_pre_checks_detected_vendors_only() {
        var (claude, codex, cursor) = await AvaloniaSession.DispatchAsync(async () => {
            var h = new Harness(VendorDetection.Build("claude", "cursor"));
            await h.Vm.OnEnterAsync(CancellationToken.None);

            return (h.Row("Claude Code").IsSelected, h.Row("Codex").IsSelected, h.Row("Cursor").IsSelected);
        });

        await Assert.That(claude).IsTrue();
        await Assert.That(codex).IsFalse();
        await Assert.That(cursor).IsTrue();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task OnEnterAsync_detects_once_and_caches_across_repeated_entries() {
        var calls = await AvaloniaSession.DispatchAsync(async () => {
            var h = new Harness();
            await h.Vm.OnEnterAsync(CancellationToken.None);
            await h.Vm.OnEnterAsync(CancellationToken.None);

            return h.DetectCallCount;
        });

        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Install_runs_selected_vendors_sequentially_Claude_first_then_flag_order() {
        var calls = await AvaloniaSession.DispatchAsync(async () => {
            var h = new Harness();
            // Selection order is deliberately scrambled — the RUN order must follow the vendor
            // list (Claude first), not click order.
            h.Row("Cursor").IsSelected      = true;
            h.Row("Claude Code").IsSelected = true;
            h.Row("Gemini").IsSelected      = true;

            await h.Vm.InstallCommand.Execute().ToTask();

            return h.Cli.PluginInstallCalls.ToList();
        });

        await Assert.That(calls).IsEquivalentTo([null, "--cursor", "--gemini"], CollectionOrdering.Matching);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_failed_vendor_does_not_block_the_next_one_successes_stand() {
        var (codexStatus, cursorStatus, callCount) = await AvaloniaSession.DispatchAsync(async () => {
            var h = new Harness();
            h.Row("Codex").IsSelected  = true;
            h.Row("Cursor").IsSelected = true;
            h.Cli.PluginInstallBehavior = (flag, _) => Task.FromResult(
                flag == "--codex" ? new ProcessResult(1, "", "boom", false) : new ProcessResult(0, "", "", false));

            await h.Vm.InstallCommand.Execute().ToTask();

            return (h.Row("Codex").Status, h.Row("Cursor").Status, h.Cli.PluginInstallCallCount);
        });

        await Assert.That(codexStatus).IsEqualTo(AgentInstallStatus.Failed);
        await Assert.That(cursorStatus).IsEqualTo(AgentInstallStatus.Succeeded);
        await Assert.That(callCount).IsEqualTo(2);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_exception_during_install_marks_the_row_failed_with_the_message() {
        var (status, message) = await AvaloniaSession.DispatchAsync(async () => {
            var h = new Harness();
            h.Row("Kiro").IsSelected = true;
            h.Cli.PluginInstallBehavior = (_, _) => throw new InvalidOperationException("spawn failed");

            await h.Vm.InstallCommand.Execute().ToTask();

            return (h.Row("Kiro").Status, h.Row("Kiro").Message);
        });

        await Assert.That(status).IsEqualTo(AgentInstallStatus.Failed);
        await Assert.That(message).IsEqualTo("spawn failed");
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Retry_only_reinstalls_the_one_row_the_other_success_stands() {
        var (codexStatus, cursorStatus, callCount, satisfied) = await AvaloniaSession.DispatchAsync(async () => {
            var h = new Harness();
            h.Row("Codex").IsSelected  = true;
            h.Row("Cursor").IsSelected = true;
            h.Cli.PluginInstallBehavior = (flag, _) => Task.FromResult(
                flag == "--codex" ? new ProcessResult(1, "", "boom", false) : new ProcessResult(0, "", "", false));
            await h.Vm.InstallCommand.Execute().ToTask();

            h.Cli.PluginInstallBehavior = (_, _) => Task.FromResult(new ProcessResult(0, "", "", false));
            await h.Row("Codex").RetryCommand.Execute().ToTask();

            return (h.Row("Codex").Status, h.Row("Cursor").Status, h.Cli.PluginInstallCallCount, h.Vm.Satisfied);
        });

        await Assert.That(codexStatus).IsEqualTo(AgentInstallStatus.Succeeded);
        await Assert.That(cursorStatus).IsEqualTo(AgentInstallStatus.Succeeded);
        await Assert.That(callCount).IsEqualTo(3); // 2 initial + 1 retry
        await Assert.That(satisfied).IsTrue(); // the retried row flipping green makes every selected row green
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Satisfied_requires_every_selected_vendor_to_succeed_and_at_least_one_selected() {
        var (noneSelected, oneFails, allSucceed) = await AvaloniaSession.DispatchAsync(async () => {
            var h1 = new Harness();
            await h1.Vm.InstallCommand.Execute().ToTask();

            var h2 = new Harness();
            h2.Row("Codex").IsSelected  = true;
            h2.Row("Cursor").IsSelected = true;
            h2.Cli.PluginInstallBehavior = (flag, _) => Task.FromResult(
                flag == "--codex" ? new ProcessResult(1, "", "", false) : new ProcessResult(0, "", "", false));
            await h2.Vm.InstallCommand.Execute().ToTask();

            var h3 = new Harness();
            h3.Row("Codex").IsSelected  = true;
            h3.Row("Cursor").IsSelected = true;
            await h3.Vm.InstallCommand.Execute().ToTask();

            return (h1.Vm.Satisfied, h2.Vm.Satisfied, h3.Vm.Satisfied);
        });

        await Assert.That(noneSelected).IsFalse();
        await Assert.That(oneFails).IsFalse();
        await Assert.That(allSucceed).IsTrue();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task No_CLI_shows_the_message_and_never_calls_install() {
        var (message, cliAvailable, callCount) = await AvaloniaSession.DispatchAsync(async () => {
            var h = new Harness();
            h.Cli.CliPath = null;
            h.Row("Claude Code").IsSelected = true;

            await h.Vm.RunInstallAsync();

            return (h.Vm.Message, h.Vm.CliAvailable, h.Cli.PluginInstallCallCount);
        });

        await Assert.That(message).IsEqualTo("kcap CLI not found");
        await Assert.That(cliAvailable).IsFalse();
        await Assert.That(callCount).IsEqualTo(0);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task CanLeaveAsync_awaits_an_in_flight_install_and_never_vetoes() {
        var (held, canLeave, status) = await AvaloniaSession.DispatchAsync(async () => {
            var h = new Harness();
            h.Row("Claude Code").IsSelected = true;
            var gate = new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            h.Cli.PluginInstallBehavior = (_, _) => gate.Task;

            var install = h.Vm.RunInstallAsync();
            var leaving = h.Vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);
            var held    = !leaving.IsCompleted; // proves it is genuinely waiting, not a fire-and-forget kill

            gate.SetResult(new ProcessResult(0, "", "", false));
            var canLeave = await leaving;
            await install;

            return (held, canLeave, h.Row("Claude Code").Status);
        });

        await Assert.That(held).IsTrue();
        await Assert.That(canLeave).IsTrue();
        await Assert.That(status).IsEqualTo(AgentInstallStatus.Succeeded);
    }
}

/// spec §3 step 6 / decision 6. Owns ReactiveCommands too, so also runs through the real headless
/// session.
public class ImportStepViewModelTests {
    sealed class Harness {
        public readonly FakeKcapCli Cli = new();
        public readonly AgentDetectionResult Detected;
        public readonly ImportStepViewModel Vm;

        public Harness(AgentDetectionResult? detected = null) {
            Detected = detected ?? VendorDetection.Build();
            Vm = new ImportStepViewModel(Cli, ct => Task.FromResult(Detected), action => action());
        }

        public ImportVendorRow Row(string label) => Vm.Vendors.First(r => r.Label == label);
    }

    static bool CanExecute<TParam, TResult>(ReactiveUI.ReactiveCommand<TParam, TResult> command) {
        var value = false;
        using var subscription = command.CanExecute.Subscribe(v => value = v); // replayed on subscribe
        return value;
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task OnEnterAsync_pre_checks_detected_vendors_only() {
        var (claude, codex) = await AvaloniaSession.DispatchAsync(async () => {
            var h = new Harness(VendorDetection.Build("claude"));
            await h.Vm.OnEnterAsync(CancellationToken.None);

            return (h.Row("Claude Code").IsSelected, h.Row("Codex").IsSelected);
        });

        await Assert.That(claude).IsTrue();
        await Assert.That(codex).IsFalse();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Run_is_disabled_until_the_scoped_text_field_is_filled_in() {
        var (everythingOk, orgBlank, orgFilled, repoBlank, repoFilled) = await AvaloniaSession.DispatchAsync(() => {
            var h = new Harness();
            h.Row("Claude Code").IsSelected = true; // isolates the scope gate from the vendor-selection gate

            var everythingOk = CanExecute(h.Vm.RunCommand); // default scope

            h.Vm.Scope = ImportScopeChoice.Org;
            var orgBlank = CanExecute(h.Vm.RunCommand);
            h.Vm.OrgText = "acme";
            var orgFilled = CanExecute(h.Vm.RunCommand);

            h.Vm.Scope = ImportScopeChoice.Repo;
            var repoBlank = CanExecute(h.Vm.RunCommand);
            h.Vm.RepoText = "acme/widgets";
            var repoFilled = CanExecute(h.Vm.RunCommand);

            return (everythingOk, orgBlank, orgFilled, repoBlank, repoFilled);
        });

        await Assert.That(everythingOk).IsTrue();
        await Assert.That(orgBlank).IsFalse();
        await Assert.That(orgFilled).IsTrue();
        await Assert.That(repoBlank).IsFalse();
        await Assert.That(repoFilled).IsTrue();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Run_is_disabled_when_no_vendor_is_selected() {
        var (noneSelected, oneSelected) = await AvaloniaSession.DispatchAsync(() => {
            var h = new Harness();

            var noneSelected = CanExecute(h.Vm.RunCommand);
            h.Row("Claude Code").IsSelected = true;
            var oneSelected = CanExecute(h.Vm.RunCommand);

            return (noneSelected, oneSelected);
        });

        // Empty VendorFlags means "import everything" to the CLI — the opposite of unchecking every box.
        await Assert.That(noneSelected).IsFalse();
        await Assert.That(oneSelected).IsTrue();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_direct_run_with_no_vendor_selected_never_calls_ImportAsync() {
        var callCount = await AvaloniaSession.DispatchAsync(async () => {
            var h = new Harness(); // valid scope (Everything) and a real CLI, but zero vendors selected

            await h.Vm.RunAsync();

            return h.Cli.ImportCallCount;
        });

        await Assert.That(callCount).IsEqualTo(0);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_direct_run_with_a_blank_scope_field_never_calls_ImportAsync() {
        var callCount = await AvaloniaSession.DispatchAsync(async () => {
            var h = new Harness();
            h.Row("Claude Code").IsSelected = true;
            h.Vm.Scope = ImportScopeChoice.Org; // OrgText left blank

            await h.Vm.RunAsync();

            return h.Cli.ImportCallCount;
        });

        await Assert.That(callCount).IsEqualTo(0);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_thrown_exception_sets_a_visible_status_with_the_retry_hint() {
        var status = await AvaloniaSession.DispatchAsync(async () => {
            var h = new Harness();
            h.Row("Claude Code").IsSelected = true;
            h.Cli.ImportBehavior = (_, _, _) => throw new InvalidOperationException("spawn failed");

            await h.Vm.RunCommand.Execute().ToTask();

            return h.Vm.Status;
        });

        await Assert.That(status).IsNotNull();
        await Assert.That(status).Contains("spawn failed");
        await Assert.That(status).Contains(ImportStepViewModel.RetryHint);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Everything_scope_argv_carries_only_the_selected_vendor_flags() {
        var request = await AvaloniaSession.DispatchAsync(async () => {
            var h = new Harness();
            h.Row("Claude Code").IsSelected = true;
            h.Row("Codex").IsSelected       = true;

            await h.Vm.RunCommand.Execute().ToTask();

            return h.Cli.ImportRequests.Single();
        });

        await Assert.That(request.Scope).IsEqualTo(ImportScopeChoice.Everything);
        await Assert.That(request.OrgOrRepo).IsNull();
        await Assert.That(request.VendorFlags).IsEquivalentTo(["--claude", "--codex"], CollectionOrdering.Matching);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Org_scope_argv_carries_the_org_text() {
        var request = await AvaloniaSession.DispatchAsync(async () => {
            var h = new Harness();
            h.Row("Claude Code").IsSelected = true;
            h.Vm.Scope   = ImportScopeChoice.Org;
            h.Vm.OrgText = "acme";

            await h.Vm.RunCommand.Execute().ToTask();

            return h.Cli.ImportRequests.Single();
        });

        await Assert.That(request.Scope).IsEqualTo(ImportScopeChoice.Org);
        await Assert.That(request.OrgOrRepo).IsEqualTo("acme");
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Repo_scope_argv_carries_the_owner_slash_name_text() {
        var request = await AvaloniaSession.DispatchAsync(async () => {
            var h = new Harness();
            h.Row("Claude Code").IsSelected = true;
            h.Vm.Scope    = ImportScopeChoice.Repo;
            h.Vm.RepoText = "acme/widgets";

            await h.Vm.RunCommand.Execute().ToTask();

            return h.Cli.ImportRequests.Single();
        });

        await Assert.That(request.Scope).IsEqualTo(ImportScopeChoice.Repo);
        await Assert.That(request.OrgOrRepo).IsEqualTo("acme/widgets");
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Log_is_bounded_to_500_lines_with_a_drop_notice_once_truncation_starts() {
        var (count, truncated, first, last) = await AvaloniaSession.DispatchAsync(async () => {
            var h = new Harness();
            h.Row("Claude Code").IsSelected = true;
            h.Cli.ImportBehavior = (_, onLine, _) => {
                for (var i = 1; i <= 510; i++) onLine(new StreamedLine(ProcessStreamKind.Stdout, $"line {i}"));

                return Task.FromResult(new StreamingResult(0, false, []));
            };

            await h.Vm.RunCommand.Execute().ToTask();

            return (h.Vm.Log.Count, h.Vm.Truncated, h.Vm.Log[0], h.Vm.Log[^1]);
        });

        await Assert.That(count).IsEqualTo(500);
        await Assert.That(truncated).IsTrue();
        await Assert.That(first).IsEqualTo("line 11"); // the first 10 of 510 fell off the bounded tail
        await Assert.That(last).IsEqualTo("line 510");
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Completion_appends_the_retry_hint_on_success() {
        var status = await AvaloniaSession.DispatchAsync(async () => {
            var h = new Harness();
            h.Row("Claude Code").IsSelected = true;
            h.Cli.ImportBehavior = (_, _, _) => Task.FromResult(new StreamingResult(0, false, []));

            await h.Vm.RunCommand.Execute().ToTask();

            return h.Vm.Status;
        });

        await Assert.That(status).Contains(ImportStepViewModel.RetryHint);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Completion_appends_the_retry_hint_on_failure_too() {
        var status = await AvaloniaSession.DispatchAsync(async () => {
            var h = new Harness();
            h.Row("Claude Code").IsSelected = true;
            h.Cli.ImportBehavior = (_, _, _) => Task.FromResult(new StreamingResult(1, false, []));

            await h.Vm.RunCommand.Execute().ToTask();

            return h.Vm.Status;
        });

        await Assert.That(status).Contains(ImportStepViewModel.RetryHint);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_failed_run_never_blocks_leaving() {
        var canLeave = await AvaloniaSession.DispatchAsync(async () => {
            var h = new Harness();
            h.Row("Claude Code").IsSelected = true;
            h.Cli.ImportBehavior = (_, _, _) => Task.FromResult(new StreamingResult(1, false, []));
            await h.Vm.RunCommand.Execute().ToTask();

            return await h.Vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);
        });

        await Assert.That(canLeave).IsTrue();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Cancel_button_stops_the_run_and_reports_cancellation_without_the_retry_hint() {
        var status = await AvaloniaSession.DispatchAsync(async () => {
            var h = new Harness();
            h.Row("Claude Code").IsSelected = true;
            var started = new TaskCompletionSource();
            h.Cli.ImportBehavior = async (_, _, ct) => {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);

                return new StreamingResult(0, false, []);
            };

            var run = h.Vm.RunAsync();
            await started.Task;
            await h.Vm.CancelCommand.Execute().ToTask();
            await run;

            return h.Vm.Status;
        });

        await Assert.That(status).IsEqualTo("Import cancelled.");
        await Assert.That(status).DoesNotContain(ImportStepViewModel.RetryHint);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task CanLeaveAsync_kills_a_running_import_and_never_vetoes() {
        var (canLeave, status) = await AvaloniaSession.DispatchAsync(async () => {
            var h = new Harness();
            h.Row("Claude Code").IsSelected = true;
            var started = new TaskCompletionSource();
            h.Cli.ImportBehavior = async (_, _, ct) => {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);

                return new StreamingResult(0, false, []);
            };

            var run = h.Vm.RunAsync();
            await started.Task;

            var canLeave = await h.Vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);
            await run;

            return (canLeave, h.Vm.Status);
        });

        await Assert.That(canLeave).IsTrue();
        await Assert.That(status).IsEqualTo("Import cancelled.");
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task No_CLI_shows_the_message_and_never_calls_import() {
        var (message, cliAvailable, callCount) = await AvaloniaSession.DispatchAsync(async () => {
            var h = new Harness();
            h.Cli.CliPath = null;

            await h.Vm.RunAsync();

            return (h.Vm.Message, h.Vm.CliAvailable, h.Cli.ImportCallCount);
        });

        await Assert.That(message).IsEqualTo("kcap CLI not found");
        await Assert.That(cliAvailable).IsFalse();
        await Assert.That(callCount).IsEqualTo(0);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Satisfied_only_after_a_run_completes_with_exit_zero() {
        var (afterFailure, afterSuccess) = await AvaloniaSession.DispatchAsync(async () => {
            var h = new Harness();
            h.Row("Claude Code").IsSelected = true;
            h.Cli.ImportBehavior = (_, _, _) => Task.FromResult(new StreamingResult(1, false, []));
            await h.Vm.RunCommand.Execute().ToTask();
            var afterFailure = h.Vm.Satisfied;

            h.Cli.ImportBehavior = (_, _, _) => Task.FromResult(new StreamingResult(0, false, []));
            await h.Vm.RunCommand.Execute().ToTask();
            var afterSuccess = h.Vm.Satisfied;

            return (afterFailure, afterSuccess);
        });

        await Assert.That(afterFailure).IsFalse();
        await Assert.That(afterSuccess).IsTrue();
    }
}

/// Template smoke coverage, mirroring WizardSimpleStepsTests: named controls per template, wired
/// through the real window and a real navigation from Agents into Import.
public class AgentsImportTemplateTests {
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_window_selects_a_template_for_Agents_and_Import_steps() {
        var result = await AvaloniaSession.DispatchAsync(async () => {
            var cli    = new FakeKcapCli();
            var detect = new Func<CancellationToken, Task<AgentDetectionResult>>(_ => Task.FromResult(VendorDetection.Build("claude")));

            var agents = new AgentsStepViewModel(cli, detect);
            var import = new ImportStepViewModel(cli, detect, action => action());
            var done   = new DoneStepViewModel(() => []);

            var vm = new OnboardingViewModel([agents, import, done]);
            await vm.PendingEnterForTesting;

            var window = new OnboardingWindow { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var installButton   = window.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "InstallAgentsButton");
            var agentCheckBoxes = window.GetVisualDescendants().OfType<CheckBox>().Where(c => c.Name == "AgentCheckBox").ToList();

            await vm.NextCommand.Execute().ToTask(); // Agents -> Import
            Dispatcher.UIThread.RunJobs();

            var runButton        = window.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "RunImportButton");
            var everythingChoice = window.GetVisualDescendants().OfType<RadioButton>().FirstOrDefault(r => r.Name == "EverythingChoice");
            var vendorCheckBoxes = window.GetVisualDescendants().OfType<CheckBox>().Where(c => c.Name == "ImportVendorCheckBox").ToList();

            window.Close();
            Dispatcher.UIThread.RunJobs();

            return (installButton, AgentRows: agentCheckBoxes.Count, runButton, everythingChoice, ImportRows: vendorCheckBoxes.Count);
        });

        await Assert.That(result.installButton).IsNotNull();
        await Assert.That(result.AgentRows).IsEqualTo(10);
        await Assert.That(result.runButton).IsNotNull();
        await Assert.That(result.everythingChoice).IsNotNull();
        await Assert.That(result.everythingChoice!.IsChecked).IsTrue();
        await Assert.That(result.ImportRows).IsEqualTo(10);
    }
}
