using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Setup;
using TUnit.Assertions.Enums;

namespace Capacitor.App.Tests.Unit;

/// spec §5 offer surface: the once-ever auto-offer gated on PhaseClosed + a positive
/// probe result, and the "Install command-line tool…" tray item's independent visibility/manual
/// retry. FakeLoginShellProbe and FakeLifecycleSurface are shared from
/// DaemonLifecycleControllerTests.cs (same namespace); PathShimInstaller is real (sealed, no
/// interface — Task 23), driven through the internal destination-override constructor so nothing
/// here ever touches the real /usr/local/bin/kcap.
public class ShimOfferCoordinatorTests {
    static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null, string what = "condition") {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!condition()) {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }

    sealed class FakeProcessRunner : IProcessRunner {
        public readonly List<(string FileName, string[] Args, RunOptions Options)> Calls = [];
        Func<Task<ProcessResult>> _step = () => Task.FromResult(new ProcessResult(0, "", "", false));

        public void Enqueue(ProcessResult result) => _step = () => Task.FromResult(result);

        public Task<ProcessResult> RunAsync(string fileName, string[] args, RunOptions options, CancellationToken ct) {
            Calls.Add((fileName, args, options));
            return _step();
        }

        public Task<StreamingResult> RunStreamingAsync(string fileName, string[] args, RunOptions options,
            Action<StreamedLine> onLine, CancellationToken ct) => throw new NotImplementedException();
    }

    /// Applies mutations to State only when UpdateSucceeds is true — mirrors AppStateStore's real
    /// disk-write-failure contract (LoadAsync keeps returning the last successfully written
    /// value), so "persist failure" tests exercise the same shape a real write failure would.
    sealed class FakeAppStateStore : IAppStateStore {
        public AppState State = new();
        public bool UpdateSucceeds = true;
        public Func<AppState, Task>? OnUpdateAttempt;

        public Task<AppState> LoadAsync() => Task.FromResult(State);

        public async Task<bool> UpdateAsync(Func<AppState, AppState> mutate) {
            var next = mutate(State);
            if (OnUpdateAttempt is not null) await OnUpdateAttempt(next).ConfigureAwait(false);
            if (!UpdateSucceeds) return false;
            State = next;
            return true;
        }
    }

    sealed class Harness : IDisposable {
        public readonly FakeProcessRunner Runner = new();
        public readonly FakeLoginShellProbe Probe = new();
        public readonly FakeAppStateStore Store = new();
        public readonly FakeLifecycleSurface Surface = new();
        readonly TempDir _tmp = new();
        public string TempDir => _tmp.Path;
        public readonly TaskCompletionSource<bool> PhaseClosedSource = new();
        public readonly List<bool> OfferableValues = [];

        public readonly string? Target;
        public readonly string Destination;
        public readonly PathShimInstaller Installer;
        public readonly ShimOfferCoordinator Coordinator;

        public Harness(
                bool immediatePhaseClosed = false, bool noTarget = false, Func<bool>? isMacOs = null,
                bool autoOfferSuppressed = false) {
            Target      = noTarget ? null : Path.Combine(TempDir, "target-cli");
            Destination = Path.Combine(TempDir, "kcap");
            Installer   = new PathShimInstaller(Runner, Probe);

            var phaseClosed = immediatePhaseClosed ? Task.CompletedTask : PhaseClosedSource.Task;
            Coordinator = new ShimOfferCoordinator(
                phaseClosed, Probe, Installer, Store, Surface, Target, CancellationToken.None, Destination,
                isMacOs ?? (() => true), autoOfferSuppressed);
            Coordinator.Offerable.Subscribe(OfferableValues.Add);
        }

        public void ClosePhase() => PhaseClosedSource.TrySetResult(true);

        public void Dispose() => _tmp.Dispose();
    }

    // ---- offer waits for PhaseClosed ----

    [Test]
    public async Task Offer_waits_for_PhaseClosed_before_probing() {
        using var h = new Harness();
        var probed = false;
        h.Probe.KcapOnPathBehavior = _ => { probed = true; return Task.FromResult<bool?>(false); };
        h.Coordinator.Start();

        await Task.Delay(50); // give a wrongly-firing probe every chance to appear
        await Assert.That(probed).IsFalse();

        h.ClosePhase();
        await WaitUntilAsync(() => probed, what: "the probe to run once the phase closes");
    }

    [Test]
    public async Task Offer_proceeds_immediately_when_PhaseClosed_is_already_completed() {
        using var h = new Harness(immediatePhaseClosed: true);
        h.Probe.KcapOnPathBehavior = _ => Task.FromResult<bool?>(false);
        h.Coordinator.Start();

        await WaitUntilAsync(() => h.OfferableValues.Contains(true), what: "the item to become visible");
    }

    // ---- spec §3.3: macOS-only, no-op elsewhere ----

    [Test]
    public async Task Off_macOS_never_probes_never_offers_and_the_menu_item_stays_hidden() {
        using var h = new Harness(immediatePhaseClosed: true, isMacOs: () => false);
        var probed = false;
        h.Probe.KcapOnPathBehavior = _ => { probed = true; return Task.FromResult<bool?>(false); };
        h.Coordinator.Start();

        await Task.Delay(100); // give a wrongly-firing probe/offer every chance to appear
        await Assert.That(probed).IsFalse();
        await Assert.That(h.OfferableValues).DoesNotContain(true);
        await Assert.That(h.Surface.Prompts).IsEmpty();
    }

    [Test]
    public async Task Off_macOS_manual_install_is_a_no_op_no_installer_spawn() {
        using var h = new Harness(isMacOs: () => false);

        await h.Coordinator.RunManualInstallAsync();

        await Assert.That(h.Runner.Calls).IsEmpty();
        await Assert.That(h.Surface.StatusMessages).IsEmpty();
    }

    // ---- detection: absolute target required, positive probe required ----

    [Test]
    public async Task Null_target_never_offers_and_the_menu_item_stays_hidden() {
        using var h = new Harness(immediatePhaseClosed: true, noTarget: true);
        var probed = false;
        h.Probe.KcapOnPathBehavior = _ => { probed = true; return Task.FromResult<bool?>(false); };
        h.Coordinator.Start();

        await Task.Delay(100); // give a wrongly-firing probe/offer every chance to appear
        await Assert.That(probed).IsFalse();
        await Assert.That(h.OfferableValues).DoesNotContain(true);
        await Assert.That(h.Surface.Prompts).IsEmpty();
    }

    [Test]
    public async Task KcapOnPath_true_never_offers_and_the_menu_item_stays_hidden() {
        using var h = new Harness(immediatePhaseClosed: true);
        h.Probe.KcapOnPathBehavior = _ => Task.FromResult<bool?>(true);
        h.Coordinator.Start();

        await Task.Delay(100); // give a wrongly-firing offer every chance to appear
        await Assert.That(h.OfferableValues).DoesNotContain(true);
        await Assert.That(h.Surface.Prompts).IsEmpty();
    }

    [Test]
    public async Task KcapOnPath_unknown_never_offers_and_the_menu_item_stays_hidden() {
        using var h = new Harness(immediatePhaseClosed: true);
        h.Probe.KcapOnPathBehavior = _ => Task.FromResult<bool?>(null);
        h.Coordinator.Start();

        await Task.Delay(100);
        await Assert.That(h.OfferableValues).DoesNotContain(true);
        await Assert.That(h.Surface.Prompts).IsEmpty();
    }

    [Test]
    public async Task KcapOnPath_false_is_the_offer_case_and_shows_the_menu_item() {
        using var h = new Harness(immediatePhaseClosed: true);
        h.Probe.KcapOnPathBehavior = _ => Task.FromResult<bool?>(false);
        h.Coordinator.Start();

        await WaitUntilAsync(() => h.Surface.Prompts.Count == 1, what: "the shim offer dialog");
        await Assert.That(h.OfferableValues).Contains(true);
        await Assert.That(h.Surface.Prompts[0].Kind).IsEqualTo(LifecyclePrompt.KindShim);
        await Assert.That(h.Surface.Prompts[0].Disclosure).IsEqualTo(ShimOfferCoordinator.ShimDisclosure);
        await Assert.That(h.Surface.Prompts[0].PathDegraded).IsFalse();
    }

    // ---- Task 15 round-1 review: autoOfferSuppressed (carve-out mode) ----

    [Test]
    public async Task AutoOfferSuppressed_still_becomes_offerable_but_never_shows_the_dialog() {
        using var h = new Harness(immediatePhaseClosed: true, autoOfferSuppressed: true);
        h.Probe.KcapOnPathBehavior = _ => Task.FromResult<bool?>(false);
        h.Coordinator.Start();

        await WaitUntilAsync(() => h.OfferableValues.Contains(true), what: "the item to become visible");
        await Task.Delay(100); // give a wrongly-firing auto-offer dialog (ConfirmAsync) every chance to appear
        await Assert.That(h.Surface.Prompts).IsEmpty(); // Prompts records every ConfirmAsync call
        // The once-ever offer claim must survive a suppressed run — a later complete-gate run still auto-offers.
        await Assert.That(h.Store.State.ShimOffered).IsFalse();
        await Assert.That(h.Store.State.ShimDenied).IsFalse();
    }

    // Manual install is a separate code path from the suppressed auto-offer — it must still work.
    [Test]
    public async Task AutoOfferSuppressed_manual_install_still_works() {
        using var h = new Harness(autoOfferSuppressed: true);
        h.Runner.Enqueue(new ProcessResult(0, "", "", false));

        await h.Coordinator.RunManualInstallAsync();

        await Assert.That(h.Runner.Calls.Count).IsEqualTo(1);
        await Assert.That(h.Surface.Prompts).IsEmpty(); // never a dialog — a direct manual click
    }

    // ---- already resolved on a prior run ----

    [Test]
    public async Task Already_offered_state_skips_auto_offer_but_the_menu_item_still_shows() {
        using var h = new Harness(immediatePhaseClosed: true);
        h.Store.State = h.Store.State with { ShimOffered = true };
        h.Probe.KcapOnPathBehavior = _ => Task.FromResult<bool?>(false);
        h.Coordinator.Start();

        await WaitUntilAsync(() => h.OfferableValues.Contains(true), what: "the item to become visible");
        await Task.Delay(100); // give a wrongly-firing dialog every chance to appear
        await Assert.That(h.Surface.Prompts).IsEmpty();
    }

    [Test]
    public async Task Already_denied_state_skips_auto_offer_but_the_menu_item_still_shows() {
        using var h = new Harness(immediatePhaseClosed: true);
        h.Store.State = h.Store.State with { ShimDenied = true };
        h.Probe.KcapOnPathBehavior = _ => Task.FromResult<bool?>(false);
        h.Coordinator.Start();

        await WaitUntilAsync(() => h.OfferableValues.Contains(true), what: "the item to become visible");
        await Task.Delay(100);
        await Assert.That(h.Surface.Prompts).IsEmpty();
    }

    // ---- preflight ----

    [Test]
    public async Task Conflict_preflight_skips_auto_offer_but_the_menu_item_still_shows() {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");
        using var h = new Harness(immediatePhaseClosed: true);
        File.WriteAllText(h.Destination, "pre-existing, not a symlink");
        h.Probe.KcapOnPathBehavior = _ => Task.FromResult<bool?>(false);
        h.Coordinator.Start();

        await WaitUntilAsync(() => h.OfferableValues.Contains(true), what: "the item to become visible");
        await Task.Delay(100);
        await Assert.That(h.Surface.Prompts).IsEmpty();
        await Assert.That(h.Store.State.ShimOffered).IsFalse();
    }

    [Test]
    public async Task AlreadyInstalled_preflight_marks_offered_without_a_dialog() {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");
        using var h = new Harness(immediatePhaseClosed: true);
        File.WriteAllText(h.Target!, "cli");
        File.CreateSymbolicLink(h.Destination, h.Target!);
        h.Probe.KcapOnPathBehavior = _ => Task.FromResult<bool?>(false);
        h.Coordinator.Start();

        await WaitUntilAsync(() => h.Store.State.ShimOffered, what: "the idempotent offered claim");
        await Task.Delay(100);
        await Assert.That(h.Surface.Prompts).IsEmpty();
        await Assert.That(h.Runner.Calls).IsEmpty();
    }

    // ---- claim-before-show ----

    [Test]
    public async Task ShimOffered_is_persisted_before_ConfirmAsync_resolves() {
        using var h = new Harness(immediatePhaseClosed: true);
        h.Probe.KcapOnPathBehavior = _ => Task.FromResult<bool?>(false);
        // Declining (below) also persists ShimDenied — a SECOND store write, whose resulting
        // record still carries ShimOffered=true from the first write, so a naive "ShimOffered"
        // check would double-count. Guard on "not already recorded" instead: this test only
        // cares that the OFFERED claim's write precedes ConfirmAsync, not about writes after it.
        var order = new List<string>();
        var offeredRecorded = false;
        h.Store.OnUpdateAttempt = next => {
            if (next.ShimOffered && !offeredRecorded) {
                offeredRecorded = true;
                order.Add("persisted");
            }
            return Task.CompletedTask;
        };
        h.Surface.ConfirmBehavior = (_, _) => {
            order.Add("confirm");
            return Task.FromResult(false);
        };
        h.Coordinator.Start();

        await WaitUntilAsync(() => order.Count >= 2, what: "both the persist and the confirm call");
        await Assert.That(order.Take(2)).IsEquivalentTo(["persisted", "confirm"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Persist_failure_still_lets_the_offer_proceed_this_run() {
        using var h = new Harness(immediatePhaseClosed: true);
        h.Store.UpdateSucceeds = false;
        h.Probe.KcapOnPathBehavior = _ => Task.FromResult<bool?>(false);
        h.Coordinator.Start();

        await WaitUntilAsync(() => h.Surface.Prompts.Count == 1, what: "the offer to proceed despite the write failure");
        await Task.Delay(100); // give a duplicate offer every chance to appear
        await Assert.That(h.Surface.Prompts.Count).IsEqualTo(1);
        await Assert.That(h.Store.State.ShimOffered).IsFalse(); // the write never actually landed
    }

    // ---- accept / decline outcomes ----

    [Test]
    public async Task Accept_then_Installed_surfaces_a_success_status() {
        using var h = new Harness(immediatePhaseClosed: true);
        var probeCalls = 0;
        h.Probe.KcapOnPathBehavior = _ => Task.FromResult<bool?>(++probeCalls == 1 ? false : true);
        h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(true);
        h.Runner.Enqueue(new ProcessResult(0, "", "", false));
        h.Coordinator.Start();

        await WaitUntilAsync(() => h.Surface.StatusMessages.Count == 1, what: "the installed status line");
        await Assert.That(h.Surface.StatusMessages[0]).Contains("PATH");
        await Assert.That(h.Store.State.ShimDenied).IsFalse();
    }

    // Regression: a confirmed on-PATH install used to leave Offerable stuck at true forever, so
    // the "Install command-line tool…" tray item never disappeared after a successful install.
    [Test]
    public async Task Accept_then_Installed_resets_Offerable_to_false() {
        using var h = new Harness(immediatePhaseClosed: true);
        var probeCalls = 0;
        h.Probe.KcapOnPathBehavior = _ => Task.FromResult<bool?>(++probeCalls == 1 ? false : true);
        h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(true);
        h.Runner.Enqueue(new ProcessResult(0, "", "", false));
        h.Coordinator.Start();

        await WaitUntilAsync(() => h.Surface.StatusMessages.Count == 1, what: "the installed status line");
        await Assert.That(h.OfferableValues.Last()).IsFalse();
    }

    [Test]
    public async Task Accept_then_InstalledButNotOnPath_surfaces_the_detail_line() {
        using var h = new Harness(immediatePhaseClosed: true);
        h.Probe.KcapOnPathBehavior = _ => Task.FromResult<bool?>(false); // stays false on every call, incl. post-install
        h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(true);
        h.Runner.Enqueue(new ProcessResult(0, "", "", false));
        h.Coordinator.Start();

        await WaitUntilAsync(() => h.Surface.StatusMessages.Count == 1, what: "the not-on-PATH status line");
        await Assert.That(h.Surface.StatusMessages[0]).Contains("PATH");
    }

    // kcap is still absent from the terminal PATH after InstalledButNotOnPath — unlike a
    // confirmed Installed, the tray item must stay offerable so the user can retry.
    [Test]
    public async Task Accept_then_InstalledButNotOnPath_leaves_Offerable_true() {
        using var h = new Harness(immediatePhaseClosed: true);
        h.Probe.KcapOnPathBehavior = _ => Task.FromResult<bool?>(false);
        h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(true);
        h.Runner.Enqueue(new ProcessResult(0, "", "", false));
        h.Coordinator.Start();

        await WaitUntilAsync(() => h.Surface.StatusMessages.Count == 1, what: "the not-on-PATH status line");
        await Assert.That(h.OfferableValues.Last()).IsTrue();
    }

    [Test]
    public async Task Accept_then_Cancelled_persists_ShimDenied_and_surfaces_status() {
        using var h = new Harness(immediatePhaseClosed: true);
        h.Probe.KcapOnPathBehavior = _ => Task.FromResult<bool?>(false);
        h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(true);
        h.Runner.Enqueue(new ProcessResult(1, "", "execution error: User canceled. (-128)", false));
        h.Coordinator.Start();

        await WaitUntilAsync(() => h.Surface.StatusMessages.Count == 1, what: "the canceled status line");
        await Assert.That(h.Store.State.ShimDenied).IsTrue();
    }

    [Test]
    public async Task Accept_then_Failed_surfaces_the_sudo_fallback() {
        using var h = new Harness(immediatePhaseClosed: true);
        h.Probe.KcapOnPathBehavior = _ => Task.FromResult<bool?>(false);
        h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(true);
        h.Runner.Enqueue(new ProcessResult(1, "", "administrator privileges denied", false));
        h.Coordinator.Start();

        await WaitUntilAsync(() => h.Surface.StatusMessages.Count == 1, what: "the failed status line");
        await Assert.That(h.Surface.StatusMessages[0]).Contains("sudo mkdir -p /usr/local/bin");
        await Assert.That(h.Store.State.ShimDenied).IsFalse(); // Failed is not the same as declined/canceled
    }

    // Failed never confirmed anything about PATH — the tray item must stay offerable.
    [Test]
    public async Task Accept_then_Failed_leaves_Offerable_true() {
        using var h = new Harness(immediatePhaseClosed: true);
        h.Probe.KcapOnPathBehavior = _ => Task.FromResult<bool?>(false);
        h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(true);
        h.Runner.Enqueue(new ProcessResult(1, "", "administrator privileges denied", false));
        h.Coordinator.Start();

        await WaitUntilAsync(() => h.Surface.StatusMessages.Count == 1, what: "the failed status line");
        await Assert.That(h.OfferableValues.Last()).IsTrue();
    }

    [Test]
    public async Task Decline_persists_ShimDenied_without_calling_InstallAsync() {
        using var h = new Harness(immediatePhaseClosed: true);
        h.Probe.KcapOnPathBehavior = _ => Task.FromResult<bool?>(false);
        h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(false);
        h.Coordinator.Start();

        await WaitUntilAsync(() => h.Store.State.ShimDenied, what: "the decline claim");
        await Assert.That(h.Runner.Calls).IsEmpty();
        await Assert.That(h.Surface.StatusMessages).IsEmpty();
    }

    // ---- manual tray-item retry ----

    [Test]
    public async Task Manual_install_runs_even_when_already_offered() {
        using var h = new Harness(immediatePhaseClosed: true);
        h.Store.State = h.Store.State with { ShimOffered = true };
        h.Probe.KcapOnPathBehavior = _ => Task.FromResult<bool?>(false);
        h.Runner.Enqueue(new ProcessResult(0, "", "", false));

        await h.Coordinator.RunManualInstallAsync();

        await Assert.That(h.Runner.Calls.Count).IsEqualTo(1);
        await Assert.That(h.Surface.StatusMessages.Count).IsEqualTo(1);
        // The auto-offer never ran (no confirm dialog) — this was a direct manual click.
        await Assert.That(h.Surface.Prompts).IsEmpty();
    }

    [Test]
    public async Task Manual_install_is_a_no_op_without_a_target() {
        using var h = new Harness(noTarget: true);

        await h.Coordinator.RunManualInstallAsync();

        await Assert.That(h.Runner.Calls).IsEmpty();
        await Assert.That(h.Surface.StatusMessages).IsEmpty();
    }
}
