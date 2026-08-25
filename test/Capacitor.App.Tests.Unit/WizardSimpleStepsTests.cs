using System.Reactive.Threading.Tasks;
using System.Runtime.Versioning;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Services;
using Capacitor.App.ViewModels.Onboarding;
using Capacitor.App.Views.Onboarding;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Setup;
using TUnit.Assertions.Enums;

namespace Capacitor.App.Tests.Unit;

/// spec §3 steps 1/4/8: the PATH shim, the visibility/daemon-name defaults, and the closing
/// summary. Shim owns a ReactiveCommand (WhenAnyValue in its ctor), so it runs through the real
/// headless session like SignInStepViewModel; Defaults and Done own no commands and run directly,
/// like ConnectStepViewModel.
public class WizardSimpleStepsTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    // ── Shim: pure applicability decision ───────────────────────────────────

    [Test]
    [Arguments(false, "/opt/kcap/kcap", false, false)] // non-macOS
    [Arguments(true, null, false, false)]               // no resolved CLI
    [Arguments(true, "/opt/kcap/kcap", true, false)]     // already on PATH
    [Arguments(true, "/opt/kcap/kcap", null, false)]     // probe inconclusive — fail quiet
    [Arguments(true, "/opt/kcap/kcap", false, true)]     // macOS + CLI + positively absent
    public async Task ComputeApplicable_matches_the_spec_decision(bool isMacOs, string? target, bool? onPath, bool expected) {
        await Assert.That(ShimStepViewModel.ComputeApplicable(isMacOs, target, onPath)).IsEqualTo(expected);
    }

    // ── Shim: install / claim / outcome mapping ─────────────────────────────

    sealed class FakeProcessRunner : IProcessRunner {
        Func<Task<ProcessResult>> _step = () => Task.FromResult(new ProcessResult(0, "", "", false));

        public void Enqueue(ProcessResult result) => _step = () => Task.FromResult(result);

        public Task<ProcessResult> RunAsync(string fileName, string[] args, RunOptions options, CancellationToken ct) => _step();

        public Task<StreamingResult> RunStreamingAsync(string fileName, string[] args, RunOptions options,
            Action<StreamedLine> onLine, CancellationToken ct) => throw new NotImplementedException();
    }

    sealed class FakeAppStateStore : IAppStateStore {
        public AppState State = new();
        public int Updates;

        public Task<AppState> LoadAsync() => Task.FromResult(State);

        public Task<bool> UpdateAsync(Func<AppState, AppState> mutate) {
            Updates++;
            State = mutate(State);

            return Task.FromResult(true);
        }
    }

    sealed class ShimHarness : IDisposable {
        readonly TempDir _tmp = new();
        public string TempDir => _tmp.Path;
        public readonly FakeProcessRunner  Runner = new();
        public readonly FakeLoginShellProbe Probe = new();
        public readonly FakeAppStateStore  Store  = new();
        public readonly PathShimInstaller  Installer;
        public readonly string             Destination;
        public readonly string             Target;
        public readonly ShimStepViewModel  Vm;

        public ShimHarness() {
            Destination = Path.Combine(TempDir, "kcap");
            Target      = Path.Combine(TempDir, "target-cli");
            Installer   = new PathShimInstaller(Runner, Probe);
            Vm          = new ShimStepViewModel(true, Installer, Store, Target, Destination);
        }

        // InstallCommand's IsExecuting/CanExecute/End notifications ride the dispatcher scheduler;
        // drain them on the session thread before returning so this shared-session test leaves no
        // dispatcher-queued work a sibling's frame could later surface off the UI thread.
        public async Task Install() {
            await Vm.InstallCommand.Execute().ToTask();
            Dispatcher.UIThread.RunJobs();
        }

        public void Dispose() => _tmp.Dispose();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Install_claims_ShimOffered_exactly_once_across_two_clicks() {
        var updates = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new ShimHarness();
            h.Runner.Enqueue(new ProcessResult(0, "", "", false));
            h.Probe.KcapOnPathBehavior = _ => Task.FromResult<bool?>(true);

            await h.Install();
            await h.Install();

            return h.Store.Updates;
        });

        await Assert.That(updates).IsEqualTo(1);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Never_installing_never_claims() {
        var updates = await AvaloniaSession.DispatchAsync(() => {
            using var h = new ShimHarness();

            return h.Store.Updates;
        });

        await Assert.That(updates).IsEqualTo(0);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Installed_outcome_satisfies_the_step_with_no_message() {
        var (satisfied, message) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new ShimHarness();
            h.Runner.Enqueue(new ProcessResult(0, "", "", false));
            h.Probe.KcapOnPathBehavior = _ => Task.FromResult<bool?>(true);

            await h.Install();

            return (h.Vm.Satisfied, h.Vm.Message);
        });

        await Assert.That(satisfied).IsTrue();
        await Assert.That(message).IsNull();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task InstalledButNotOnPath_outcome_is_unsatisfied_with_the_installer_detail() {
        var (satisfied, message) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new ShimHarness();
            h.Runner.Enqueue(new ProcessResult(0, "", "", false));
            h.Probe.KcapOnPathBehavior = _ => Task.FromResult<bool?>(false);

            await h.Install();

            return (h.Vm.Satisfied, h.Vm.Message);
        });

        await Assert.That(satisfied).IsFalse();
        await Assert.That(message).IsNotNull();
        await Assert.That(message).Contains("PATH");
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Cancelled_outcome_is_unsatisfied_with_no_message() {
        var (satisfied, message) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new ShimHarness();
            h.Runner.Enqueue(new ProcessResult(1, "", "User canceled. (-128)", false));

            await h.Install();

            return (h.Vm.Satisfied, h.Vm.Message);
        });

        await Assert.That(satisfied).IsFalse();
        await Assert.That(message).IsNull();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Failed_outcome_is_unsatisfied_with_detail_and_the_sudo_fallback() {
        var (satisfied, message) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new ShimHarness();
            h.Runner.Enqueue(new ProcessResult(1, "", "Permission denied", false));

            await h.Install();

            return (h.Vm.Satisfied, h.Vm.Message);
        });

        await Assert.That(satisfied).IsFalse();
        await Assert.That(message).IsNotNull();
        await Assert.That(message).Contains("Permission denied");
        await Assert.That(message).Contains("sudo mkdir -p /usr/local/bin");
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Null_target_reports_kcap_not_found_and_claims_nothing() {
        var (satisfied, message, updates) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new ShimHarness();
            var vm = new ShimStepViewModel(true, h.Installer, h.Store, null, h.Destination);

            await vm.InstallCommand.Execute().ToTask();
            Dispatcher.UIThread.RunJobs(); // drain the command's dispatcher-scheduled notifications, as ShimHarness.Install does

            return (vm.Satisfied, vm.Message, h.Store.Updates);
        });

        await Assert.That(satisfied).IsFalse();
        await Assert.That(message).IsEqualTo("kcap CLI not found");
        await Assert.That(updates).IsEqualTo(0);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task CanLeaveAsync_never_vetoes() {
        var (next, back, skip) = await AvaloniaSession.DispatchAsync(async () => {
            var vm = new ShimStepViewModel(false, new PathShimInstaller(
                new NoopProcessRunner(), new FakeLoginShellProbe()), new NoopAppStateStore(), null);

            return (
                await vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None),
                await vm.CanLeaveAsync(WizardNavigation.Back, CancellationToken.None),
                await vm.CanLeaveAsync(WizardNavigation.Skip, CancellationToken.None));
        });

        await Assert.That(next).IsTrue();
        await Assert.That(back).IsTrue();
        await Assert.That(skip).IsTrue();
    }

    // ── templates ────────────────────────────────────────────────────────────

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_window_selects_a_template_for_each_simple_step() {
        var result = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new ShimHarness();
            var defaults = new DefaultsStepViewModel(Config.Root);
            var done = new DoneStepViewModel(() => [("Command-line tool", false, "kcap CLI not found")]);
            var vm = new OnboardingViewModel([h.Vm, defaults, done]);
            await vm.PendingEnterForTesting;

            var window = new OnboardingWindow { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            // Run the install and observe the button/success-text react — a broken Idle/Satisfied binding would leave Avalonia's own base defaults instead of tracking the VM.
            var installButton = window.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "InstallShimButton");
            var successBefore = window.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Name == "ShimSuccessText")?.IsVisible;

            h.Runner.Enqueue(new ProcessResult(0, "", "", false));
            h.Probe.KcapOnPathBehavior = _ => Task.FromResult<bool?>(true);
            await h.Install(); // Install() already drains the dispatcher

            var successAfter = window.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Name == "ShimSuccessText")?.IsVisible;
            var installEnabledAfter = installButton?.IsEnabled;

            await vm.NextCommand.Execute().ToTask(); // Shim -> Defaults
            Dispatcher.UIThread.RunJobs();

            var visibilityCombo = window.GetVisualDescendants().OfType<ComboBox>().FirstOrDefault(c => c.Name == "VisibilityCombo");
            var selectedVisibility = visibilityCombo?.SelectedValue as string;
            var daemonNameText = window.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.Name == "DaemonNameBox")?.Text;

            await vm.SkipCommand.Execute().ToTask(); // Defaults -> Done (Skip never persists — no real config write here)
            Dispatcher.UIThread.RunJobs();

            var summaryList = window.GetVisualDescendants().OfType<ItemsControl>().FirstOrDefault(i => i.Name == "SummaryList");
            var summaryTitle = window.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Name == "SummaryTitleText")?.Text;
            var summaryNote = window.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Name == "SummaryNoteText")?.Text;
            var summaryGlyph = window.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Name == "SummaryGlyphText")?.Text;

            window.Close();
            Dispatcher.UIThread.RunJobs();

            return (
                installButton, successBefore, successAfter, installEnabledAfter,
                visibilityCombo, selectedVisibility, daemonNameText,
                summaryList, summaryTitle, summaryNote, summaryGlyph);
        });

        await Assert.That(result.installButton).IsNotNull();
        await Assert.That(result.successBefore).IsFalse(); // Satisfied starts false — a broken binding would leave Avalonia's IsVisible default (true)
        await Assert.That(result.successAfter).IsTrue();   // Satisfied flips true once Installed lands
        await Assert.That(result.installEnabledAfter).IsTrue();

        await Assert.That(result.visibilityCombo).IsNotNull();
        await Assert.That(result.selectedVisibility).IsEqualTo("org_public");
        await Assert.That(result.daemonNameText).IsEqualTo(Environment.UserName.ToLowerInvariant());

        await Assert.That(result.summaryList).IsNotNull();
        await Assert.That(result.summaryTitle).IsEqualTo("Command-line tool");
        await Assert.That(result.summaryNote).IsEqualTo("kcap CLI not found");
        await Assert.That(result.summaryGlyph).IsEqualTo("—");
    }

    sealed class NoopProcessRunner : IProcessRunner {
        public Task<ProcessResult> RunAsync(string fileName, string[] args, RunOptions options, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<StreamingResult> RunStreamingAsync(string fileName, string[] args, RunOptions options,
            Action<StreamedLine> onLine, CancellationToken ct) => throw new NotImplementedException();
    }

    sealed class NoopAppStateStore : IAppStateStore {
        public Task<AppState> LoadAsync() => Task.FromResult(new AppState());
        public Task<bool> UpdateAsync(Func<AppState, AppState> mutate) => Task.FromResult(true);
    }
}

/// Real ConfigMutator against the config path.
public class DefaultsStepViewModelTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    string ConfigPath => AppConfig.GetConfigPath(Config.Root);

    [Test]
    public async Task Defaults_are_org_public_and_the_lowercased_username() {
        var vm = new DefaultsStepViewModel(Config.Root);

        await Assert.That(vm.Visibility).IsEqualTo("org_public");
        await Assert.That(vm.DaemonName).IsEqualTo(Environment.UserName.ToLowerInvariant());
        await Assert.That(vm.Applicable).IsTrue();
        await Assert.That(vm.Satisfied).IsFalse();
    }

    [Test]
    public async Task VisibilityOptions_carry_the_setup_prompts_four_labels_verbatim() {
        var options = DefaultsStepViewModel.VisibilityOptions;

        await Assert.That(options.Select(o => o.Value)).IsEquivalentTo(AppConfig.ValidVisibilities, CollectionOrdering.Matching);
        await Assert.That(options.First(o => o.Value == "private").Label)
            .IsEqualTo("All private — only you can see your sessions");
        await Assert.That(options.First(o => o.Value == "project").Label)
            .IsEqualTo("Project repos public to fellow project members, others private");
        await Assert.That(options.First(o => o.Value == "org_public").Label)
            .IsEqualTo("Org repos public, others private (default)");
        await Assert.That(options.First(o => o.Value == "public").Label)
            .IsEqualTo("All public — others can see all your sessions");
    }

    [Test]
    public async Task Next_persists_both_fields_and_preserves_unrelated_config() {
        var existing = new ProfileConfig {
            ActiveProfile = "acme",
            Profiles = new() {
                ["acme"] = new Profile {
                    ServerUrl     = "https://acme.example",
                    ExcludedRepos = ["foo/bar"],
                    ImportOrg     = "acme-org",
                    Daemon        = new DaemonSettings { MaxAgents = 9, ClaudePath = "/usr/bin/claude" },
                }
            },
            MachineId = "machine-123",
        };
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(existing, ProfileConfigJsonContext.Default.ProfileConfig));

        var vm = new DefaultsStepViewModel(Config.Root) { Visibility = "public", DaemonName = "acme-daemon" };

        var canLeave = await vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);

        await Assert.That(canLeave).IsTrue();
        await Assert.That(vm.Satisfied).IsTrue();

        var saved   = ConfigMutator.LoadPure(ConfigPath);
        var profile = saved.Profiles["acme"];

        await Assert.That(profile.DefaultVisibility).IsEqualTo("public");
        await Assert.That(profile.Daemon!.Name).IsEqualTo("acme-daemon");
        await Assert.That(profile.Daemon!.MaxAgents).IsEqualTo(9);
        await Assert.That(profile.Daemon!.ClaudePath).IsEqualTo("/usr/bin/claude");
        await Assert.That(profile.ServerUrl).IsEqualTo("https://acme.example");
        await Assert.That(profile.ImportOrg).IsEqualTo("acme-org");
        await Assert.That(profile.ExcludedRepos).IsEquivalentTo(["foo/bar"]);
        await Assert.That(saved.MachineId).IsEqualTo("machine-123");
    }

    // The persist must follow the wizard's resolved identity, not on-disk ActiveProfile (KCAP_PROFILE split).
    [Test]
    public async Task Next_persists_to_the_injected_resolved_profile_not_the_active_one() {
        var existing = new ProfileConfig {
            ActiveProfile = "acme",
            Profiles = new() {
                ["acme"] = new Profile { ServerUrl = "https://acme.example" },
                ["work"] = new Profile { ServerUrl = "https://work.example" },
            },
        };
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(existing, ProfileConfigJsonContext.Default.ProfileConfig));

        var vm = new DefaultsStepViewModel(Config.Root, resolveProfileName: () => "work")
            { Visibility = "public", DaemonName = "work-daemon" };

        var canLeave = await vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);

        await Assert.That(canLeave).IsTrue();
        await Assert.That(vm.Satisfied).IsTrue();

        var saved = ConfigMutator.LoadPure(ConfigPath);

        await Assert.That(saved.Profiles["work"].DefaultVisibility).IsEqualTo("public");
        await Assert.That(saved.Profiles["work"].Daemon!.Name).IsEqualTo("work-daemon");
        // The active profile (acme) is untouched — the mutation targeted the RESOLVED name.
        await Assert.That(saved.Profiles["acme"].DefaultVisibility).IsEqualTo("org_public");
        await Assert.That(saved.Profiles["acme"].Daemon).IsNull();
    }

    [Test]
    public async Task A_resolved_name_absent_from_config_falls_back_to_the_active_profile() {
        var existing = new ProfileConfig {
            ActiveProfile = "acme",
            Profiles = new() { ["acme"] = new Profile { ServerUrl = "https://acme.example" } },
        };
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(existing, ProfileConfigJsonContext.Default.ProfileConfig));

        var vm = new DefaultsStepViewModel(Config.Root, resolveProfileName: () => "ghost")
            { Visibility = "public", DaemonName = "acme-daemon" };

        await vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);

        var saved = ConfigMutator.LoadPure(ConfigPath);

        await Assert.That(saved.Profiles["acme"].Daemon!.Name).IsEqualTo("acme-daemon");
        await Assert.That(saved.Profiles.ContainsKey("ghost")).IsFalse();
    }

    [Test]
    public async Task A_null_resolved_name_falls_back_to_the_active_profile() {
        var existing = new ProfileConfig {
            ActiveProfile = "acme",
            Profiles = new() { ["acme"] = new Profile { ServerUrl = "https://acme.example" } },
        };
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(existing, ProfileConfigJsonContext.Default.ProfileConfig));

        var vm = new DefaultsStepViewModel(Config.Root, resolveProfileName: () => null)
            { Visibility = "public", DaemonName = "acme-daemon" };

        await vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);

        var saved = ConfigMutator.LoadPure(ConfigPath);

        await Assert.That(saved.Profiles["acme"].Daemon!.Name).IsEqualTo("acme-daemon");
    }

    [Test]
    public async Task Skip_and_back_do_not_persist_and_leave_the_step_unsatisfied() {
        var vm = new DefaultsStepViewModel(Config.Root) { Visibility = "public", DaemonName = "acme-daemon" };

        await Assert.That(await vm.CanLeaveAsync(WizardNavigation.Skip, CancellationToken.None)).IsTrue();
        await Assert.That(await vm.CanLeaveAsync(WizardNavigation.Back, CancellationToken.None)).IsTrue();

        await Assert.That(vm.Satisfied).IsFalse();
        await Assert.That(File.Exists(ConfigPath)).IsFalse();
    }

    // A real write failure (read-only config dir), not a fake, proves CanLeaveAsync's own catch.
    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task A_persist_failure_vetoes_Next_with_a_visible_message() {
        Skip.When(OperatingSystem.IsWindows(), "chmod-based read-only config dir is POSIX-only.");

        var dir = Path.GetDirectoryName(ConfigPath)!;
        var vm  = new DefaultsStepViewModel(Config.Root) { Visibility = "public", DaemonName = "acme-daemon" };

        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try {
            var canLeave = await vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);

            await Assert.That(canLeave).IsFalse();
            await Assert.That(vm.Satisfied).IsFalse();
            await Assert.That(vm.Message).IsNotNull();
            await Assert.That(vm.Message).Contains("Could not save defaults");
        } finally {
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}

/// spec §3 step 8. Owns no commands and no Rx subscriptions — runs without the headless session,
/// like ConnectStepViewModelTests.
public class DoneStepViewModelTests {
    [Test]
    public async Task Applicable_and_Satisfied_are_always_true() {
        var vm = new DoneStepViewModel(() => []);

        await Assert.That(vm.Applicable).IsTrue();
        await Assert.That(vm.Satisfied).IsTrue();
    }

    [Test]
    public async Task Summary_reflects_the_providers_current_output_including_why_skipped_notes() {
        IReadOnlyList<(string Title, bool Satisfied, string? Note)> current = [
            ("Command-line tool", false, "kcap CLI not found"),
            ("Sign in", true, null),
            ("Enable daemon", false, "requires sign-in"),
        ];
        var vm = new DoneStepViewModel(() => current);

        await vm.OnEnterAsync(CancellationToken.None);

        await Assert.That(vm.Summary.Select(e => (e.Title, e.Satisfied, e.Note)))
            .IsEquivalentTo(current, CollectionOrdering.Matching);
        await Assert.That(vm.Summary[0].Glyph).IsEqualTo("—");
        await Assert.That(vm.Summary[1].Glyph).IsEqualTo("✓");
    }

    [Test]
    public async Task Summary_re_renders_on_every_entry() {
        var callCount = 0;
        var vm = new DoneStepViewModel(() => {
            callCount++;

            return callCount == 1
                ? [("Step", false, "not yet")]
                : [("Step", true, (string?)null)];
        });

        await vm.OnEnterAsync(CancellationToken.None);
        var first = vm.Summary[0].Satisfied;

        await vm.OnEnterAsync(CancellationToken.None);
        var second = vm.Summary[0].Satisfied;

        await Assert.That(first).IsFalse();
        await Assert.That(second).IsTrue();
    }

    [Test]
    public async Task OnEnterAsync_raises_PropertyChanged_for_Summary() {
        var vm = new DoneStepViewModel(() => []);
        var raised = new List<string?>();
        vm.PropertyChanged += (_, args) => raised.Add(args.PropertyName);

        await vm.OnEnterAsync(CancellationToken.None);

        await Assert.That(raised).Contains(nameof(DoneStepViewModel.Summary));
    }

    [Test]
    public async Task CanLeaveAsync_never_vetoes() {
        var vm = new DoneStepViewModel(() => []);

        await Assert.That(await vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None)).IsTrue();
        await Assert.That(await vm.CanLeaveAsync(WizardNavigation.Back, CancellationToken.None)).IsTrue();
        await Assert.That(await vm.CanLeaveAsync(WizardNavigation.Skip, CancellationToken.None)).IsTrue();
    }
}
