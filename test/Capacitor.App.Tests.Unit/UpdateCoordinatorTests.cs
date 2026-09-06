using Capacitor.App.Services;
using Capacitor.App.Services.Update;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

public class UpdateCoordinatorTests {
    static async Task WaitUntilAsync(Func<bool> condition, string what) {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition()) {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }

    sealed class Harness {
        public readonly FakeAppUpdater Updater = new();
        public readonly FakeLifecycleSurface Surface = new();
        public readonly FakeTimeProvider Clock = new(new DateTimeOffset(2026, 9, 6, 9, 0, 0, TimeSpan.Zero));
        public readonly CancellationTokenSource Lifetime = new();
        public int QuitCalls;
        public UpdateMenuItem? Menu;
        public readonly UpdateCoordinator Coordinator;

        public Harness() {
            Coordinator = new UpdateCoordinator(Updater, Surface, Clock, () => QuitCalls++, Lifetime.Token);
            Coordinator.MenuItem.Subscribe(m => Menu = m);
        }
    }

    static UpdateCandidate Beta3 => new("0.12.0-beta.3", IsPrerelease: true);

    [Test]
    public async Task Unavailable_updater_hides_the_item_and_never_checks() {
        var h = new Harness();
        h.Updater.IsAvailable = false;
        var coordinator = new UpdateCoordinator(h.Updater, h.Surface, h.Clock, () => { }, h.Lifetime.Token);
        UpdateMenuItem? menu = null;
        coordinator.MenuItem.Subscribe(m => menu = m);

        coordinator.Start();
        h.Clock.Advance(TimeSpan.FromHours(9));
        await Task.Delay(50);

        await Assert.That(menu!.Visible).IsFalse();
        await Assert.That(h.Updater.CheckCalls).IsEqualTo(0);
    }

    [Test]
    public async Task First_check_waits_for_the_initial_delay_then_repeats_on_the_interval() {
        var h = new Harness();
        h.Coordinator.Start();

        h.Clock.Advance(TimeSpan.FromSeconds(29));
        await Task.Delay(50);
        await Assert.That(h.Updater.CheckCalls).IsEqualTo(0);

        h.Clock.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => h.Updater.CheckCalls == 1, "the first check");

        h.Clock.Advance(TimeSpan.FromHours(4));
        await WaitUntilAsync(() => h.Updater.CheckCalls == 2, "the second check");
        await Assert.That(h.Menu!.Label).IsEqualTo(UpdateCoordinator.CheckLabel);
    }

    [Test]
    public async Task Found_update_downloads_then_prompts_once() {
        var h = new Harness();
        h.Updater.NextCheck = Beta3;
        h.Coordinator.Start();
        h.Clock.Advance(TimeSpan.FromSeconds(30));

        await WaitUntilAsync(() => h.Surface.Prompts.Count == 1, "the ready prompt");
        await Assert.That(h.Updater.DownloadCalls).IsEqualTo(1);
        await Assert.That(h.Surface.Prompts[0].Kind).IsEqualTo(LifecyclePrompt.KindUpdateReady);
        await Assert.That(h.Surface.Prompts[0].DaemonVersion).IsEqualTo("0.12.0-beta.3");
    }

    [Test]
    public async Task Later_relabels_the_item_and_stops_further_checks() {
        var h = new Harness();
        h.Updater.NextCheck = Beta3;
        h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(false);
        h.Coordinator.Start();
        h.Clock.Advance(TimeSpan.FromSeconds(30));
        await WaitUntilAsync(() => h.Menu?.Label == "Restart to update to 0.12.0-beta.3", "the ready label");

        h.Clock.Advance(TimeSpan.FromHours(9));
        await Task.Delay(50);

        await Assert.That(h.Updater.CheckCalls).IsEqualTo(1);
        await Assert.That(h.Surface.Prompts.Count).IsEqualTo(1);
        await Assert.That(h.QuitCalls).IsEqualTo(0);
    }

    [Test]
    public async Task Restart_now_records_the_pending_apply_and_quits_and_applies_on_exit_once() {
        var h = new Harness();
        h.Updater.NextCheck = Beta3;
        h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(true);
        h.Coordinator.Start();
        h.Clock.Advance(TimeSpan.FromSeconds(30));
        await WaitUntilAsync(() => h.QuitCalls == 1, "the quit request");

        await Assert.That(h.Updater.ApplyOnExitCalls).IsEmpty();
        h.Coordinator.ApplyPendingOnExit();
        h.Coordinator.ApplyPendingOnExit();

        await Assert.That(h.Updater.ApplyOnExitCalls.Select(c => c.Version)).IsEquivalentTo(["0.12.0-beta.3"]);
    }

    [Test]
    public async Task Menu_action_while_ready_restarts_instead_of_checking() {
        var h = new Harness();
        h.Updater.NextCheck = Beta3;
        h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(false);
        h.Coordinator.Start();
        h.Clock.Advance(TimeSpan.FromSeconds(30));
        await WaitUntilAsync(() => h.Surface.Prompts.Count == 1, "the ready prompt");

        await h.Coordinator.RunMenuActionAsync();

        await Assert.That(h.QuitCalls).IsEqualTo(1);
        await Assert.That(h.Updater.CheckCalls).IsEqualTo(1);
    }

    [Test]
    public async Task Manual_check_reports_up_to_date_in_an_info_prompt() {
        var h = new Harness();

        await h.Coordinator.RunMenuActionAsync();

        await Assert.That(h.Surface.Prompts.Count).IsEqualTo(1);
        await Assert.That(h.Surface.Prompts[0].Kind).IsEqualTo(LifecyclePrompt.KindUpdateInfo);
        await Assert.That(h.Surface.Prompts[0].Disclosure).Contains("up to date");
    }

    [Test]
    public async Task Manual_check_failure_is_reported_and_automatic_failure_is_silent() {
        var h = new Harness();
        h.Updater.CheckFailure = new HttpRequestException("feed down");
        h.Coordinator.Start();
        h.Clock.Advance(TimeSpan.FromSeconds(30));
        await WaitUntilAsync(() => h.Updater.CheckCalls == 1, "the automatic check");
        await Assert.That(h.Surface.Prompts).IsEmpty();

        await h.Coordinator.RunMenuActionAsync();

        await Assert.That(h.Surface.Prompts.Count).IsEqualTo(1);
        await Assert.That(h.Surface.Prompts[0].Disclosure).Contains("Could not check for updates");
    }

    [Test]
    public async Task Concurrent_manual_checks_coalesce_onto_one_call() {
        var h = new Harness();
        h.Updater.NextCheck = Beta3;
        h.Updater.HoldDownload = new TaskCompletionSource();
        h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(false);

        var first  = h.Coordinator.RunMenuActionAsync();
        var second = h.Coordinator.RunMenuActionAsync();
        h.Updater.HoldDownload.SetResult();
        await Task.WhenAll(first, second);

        await Assert.That(h.Updater.CheckCalls).IsEqualTo(1);
    }

    [Test]
    public async Task Startup_pending_apply_applies_an_eligible_package() {
        var updater = new FakeAppUpdater { PendingRestart = Beta3 };

        var applied = UpdateCoordinator.TryApplyPendingAtStartup(updater, updateRelaunch: false);

        await Assert.That(applied).IsTrue();
        await Assert.That(updater.ApplyNowCalls.Select(c => c.Version)).IsEquivalentTo(["0.12.0-beta.3"]);
    }

    [Test]
    public async Task Startup_pending_apply_ignores_a_prerelease_under_a_stable_install() {
        var updater = new FakeAppUpdater { InstalledVersion = "0.12.0", PendingRestart = new("0.13.0-beta.1", IsPrerelease: true) };

        await Assert.That(UpdateCoordinator.TryApplyPendingAtStartup(updater, updateRelaunch: false)).IsFalse();
        await Assert.That(updater.ApplyNowCalls).IsEmpty();
    }

    [Test]
    public async Task Startup_pending_apply_ignores_an_older_package() {
        var updater = new FakeAppUpdater { InstalledVersion = "0.12.0-beta.4", PendingRestart = Beta3 };

        await Assert.That(UpdateCoordinator.TryApplyPendingAtStartup(updater, updateRelaunch: false)).IsFalse();
    }

    [Test]
    public async Task Startup_pending_apply_is_skipped_on_an_update_relaunch() {
        var updater = new FakeAppUpdater { PendingRestart = Beta3 };

        await Assert.That(UpdateCoordinator.TryApplyPendingAtStartup(updater, updateRelaunch: true)).IsFalse();
        await Assert.That(updater.ApplyNowCalls).IsEmpty();
    }

    [Test]
    public async Task Startup_pending_apply_is_inert_when_unavailable() {
        var updater = new FakeAppUpdater { IsAvailable = false, PendingRestart = Beta3 };

        await Assert.That(UpdateCoordinator.TryApplyPendingAtStartup(updater, updateRelaunch: false)).IsFalse();
    }
}
