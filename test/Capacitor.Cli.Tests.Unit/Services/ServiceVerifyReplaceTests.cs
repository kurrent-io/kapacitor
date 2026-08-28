using Capacitor.Cli.Core;
using Capacitor.Cli.Services;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Tests.Unit.Services;

/// <summary><c>install --replace --verify</c>'s ownership matrix (spec §3.4): unlike a fresh
/// install (which refuses to touch an existing label/unit), --replace clears/takes over whatever it
/// finds first. See <see cref="ServiceVerifyInstallTests"/> for the fresh-path/entry-recovery
/// coverage this builds on.</summary>
public class ServiceVerifyReplaceTests {
    [TempHome] public required TempHome Home { get; init; }

    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [TempDir] public required TempDir Tmp { get; init; }

    const string Id             = "svc-verify-replace";
    const string ExpectedVersion = "1.2.3";
    const string OwnPlistContent = "<plist>own-unit</plist>";

    /// <summary>Sentinel PID far above any real pid_max (same convention as
    /// <c>DaemonPidProbeTests.Null_for_dead_pid</c>): DaemonKill.KillValidatedOwner makes a REAL,
    /// unmocked <c>Process.GetProcessById</c> call on whatever pid a test's "manual owner" seam
    /// returns, so it must never coincide with an actual process on the machine running the test.</summary>
    const int ManualOwnerPid = 999_999_111;

    /// <summary>Purpose-built fake for the ownership matrix: unlike ServiceVerifyInstallTests'
    /// fake, Query needs to report a pre-existing job pid independent of the post-bootstrap
    /// RunningPid, and Bootstrapped must win over an EARLIER Uninstalled (--replace clears the old
    /// label/unit BEFORE the same transaction's own later WriteAndBootstrap runs — the reverse
    /// order of every fresh-install rollback scenario).</summary>
    sealed class FakeServiceManager(UserHome home) : IVerifyServiceManager {
        public string UnitPath(string serviceId) => LaunchdUnit.PlistPath(home, serviceId);
        public readonly List<string> Calls = [];
        public LabelProbe InitialProbe = LabelProbe.Absent;
        public bool InitialUnitPresent;
        public int? InitialJobPid;
        public bool Bootstrapped, Uninstalled;
        public bool UninstallSucceeds = true;
        public int? RunningPid = 4242;
        public string PlistPath = "/fake/agents/io.kurrent.kcap.daemon.svc-verify-replace.plist";
        public string PlistContent = OwnPlistContent;

        public int QueryCalls             => Calls.Count(c => c == "query");
        public int WriteAndBootstrapCalls => Calls.Count(c => c == "writeAndBootstrap");
        public int UninstallCalls         => Calls.Count(c => c == "uninstall");

        public IReadOnlyList<GeneratedFile> GenerateFiles(ServiceSpec spec) => [new GeneratedFile(PlistPath, PlistContent)];

        public ServiceQuery Query(string serviceId, TimeSpan timeout) {
            Calls.Add("query");
            if (Bootstrapped) return new ServiceQuery(LabelProbe.Loaded, true, ServiceState.Running, "/bin/kcap-daemon", RunningPid);
            if (Uninstalled) return new ServiceQuery(LabelProbe.Absent, false, ServiceState.NotInstalled, null, null);
            return new ServiceQuery(InitialProbe, InitialProbe != LabelProbe.Absent || InitialUnitPresent, ServiceState.NotInstalled, null, InitialJobPid);
        }

        public void WriteAndBootstrap(ServiceSpec spec, TimeSpan timeout) {
            Calls.Add("writeAndBootstrap");
            Bootstrapped = true;
        }

        public bool Uninstall(string serviceId, TimeSpan timeout, out string? error) {
            Calls.Add("uninstall");
            if (!UninstallSucceeds) {
                // Mirrors LaunchdServiceManager.Uninstall's real failure contract: bootout failed
                // and the label is STILL Loaded — the plist is retained, nothing changes.
                error = "launchctl bootout failed (exit 5) and the label is still Loaded — plist retained";
                return false;
            }

            Uninstalled  = true;
            Bootstrapped = false;
            error = null;
            return true;
        }

        public bool Start(string serviceId, TimeSpan timeout, out string? error) { error = null; return true; }
        public bool StartBootstrapOnly(string serviceId, TimeSpan timeout, out string? error) => Start(serviceId, timeout, out error);
        public bool Stop(string serviceId, TimeSpan timeout, out string? error) { error = null; return true; }
    }

    static async Task<int> Drive(Task<int> task, FakeTimeProvider time, TimeSpan step) {
        var guard = 0;
        while (!task.IsCompleted && guard++ < 500) time.Advance(step);
        return await task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    (string Dir, string DaemonPath) SetUpViableInstall() {
        var daemonPath = Tmp.PathTo("kcap-daemon");
        File.WriteAllText(daemonPath, "");
        return (Tmp.Path, daemonPath);
    }

    static ServiceSpec Spec(string daemonPath) =>
        new(Id, daemonPath, Path.Combine(Path.GetTempPath(), "daemon.log"), new Dictionary<string, string>(), []);

    static string? OwnPlist(string _) => OwnPlistContent;

    [Test]
    public async Task Owning_label_clears_via_uninstall_before_write_and_bootstrap_with_no_kill() {
        var (_, daemonPath) = SetUpViableInstall();
        // The Loaded label's own JobPid matches the validated daemon pid — ownership holds, so
        // the matrix proceeds straight to Uninstall (the label's bootout already terminates the
        // process it owns) with no separate DaemonKill/stop-verification detour. Uses the
        // ManualOwnerPid sentinel (not an arbitrary small pid) as belt-and-braces: if the
        // owning-branch early return ever regresses, this test must never risk a REAL, unmocked
        // Process.Kill(entireProcessTree: true) against whatever process a low pid happens to
        // resolve to on the machine running it.
        var manager = new FakeServiceManager(Home) { InitialProbe = LabelProbe.Loaded, InitialUnitPresent = true, InitialJobPid = ManualOwnerPid, RunningPid = ManualOwnerPid };

        Task<HelloProbeResult> Hello(string _, TimeSpan __) =>
            Task.FromResult(new HelloProbeResult(true, 1, ExpectedVersion, Id));

        // The owning daemon's pid is live until its label is booted out, then gone — the owning
        // takeover must confirm that pid released the name lock before writing the replacement.
        int? ValidatedPid(string _) =>
            manager.Bootstrapped ? manager.RunningPid
            : manager.Uninstalled ? null
            : ManualOwnerPid;

        var sut = new ServiceVerify(Daemons.Store, Config.Root, manager, ValidatedPid, Hello, TimeProvider.System, readPlist: OwnPlist);

        var exit = await sut.InstallVerifiedAsync(Spec(daemonPath), replace: true, ExpectedVersion);

        await Assert.That(exit).IsEqualTo(VerifyExit.Ok);
        await Assert.That(manager.UninstallCalls).IsEqualTo(1);
        await Assert.That(manager.Calls.IndexOf("uninstall")).IsLessThan(manager.Calls.IndexOf("writeAndBootstrap"));
        // No seam exists to directly observe "DaemonKill was never called" (it's a raw static
        // call with no injectable delegate) — but the owning branch structurally returns before
        // ever reaching that code, so a single Uninstall reaching Ok here is exactly what an
        // extra (harmless-but-unwanted) kill attempt would NOT change; what a missing early
        // return WOULD change is the call order asserted above.
        await Assert.That(ServiceTxnMarker.Exists(Daemons.Store, Id)).IsFalse();
    }

    [Test]
    public async Task Non_owning_label_with_a_manual_owner_clears_kills_then_installs() {
        var (_, daemonPath) = SetUpViableInstall();
        // Loaded label whose JobPid does NOT match the validated daemon pid (FakeServiceManager
        // reports JobPid=null pre-bootstrap either way) — a manual (non-service) daemon holds
        // the name instead. helloCalls staging: call #1 is the stop-confirmation dial (old owner
        // already dark); every call after is the freshly bootstrapped daemon answering.
        var manager = new FakeServiceManager(Home) { InitialProbe = LabelProbe.Loaded, InitialUnitPresent = true, InitialJobPid = null };

        var helloCalls = 0;
        Task<HelloProbeResult> Hello(string _, TimeSpan __) {
            helloCalls++;
            return Task.FromResult(helloCalls == 1
                ? new HelloProbeResult(false, null, null, null)
                : new HelloProbeResult(true, 1, ExpectedVersion, Id));
        }

        // Call #1 (matrix entry, before any hello): the manual owner's pid. Call #2 (the
        // stop-confirmation check, right after the dark hello): gone. Every call after bootstrap
        // succeeds: the freshly bootstrapped daemon's own pid — required for the readiness poll
        // to ever match manager.Query(...).JobPid.
        //
        // ManualOwnerPid is a sentinel far above any real pid_max (same convention as
        // DaemonPidProbeTests) — DaemonKill.KillValidatedOwner makes a REAL, unmocked
        // Process.GetProcessById call on this value, so it must be guaranteed not to resolve to
        // an actual process on the test machine.
        int? ValidatedPid(string _) =>
            manager.Bootstrapped ? manager.RunningPid
            : helloCalls == 0 ? ManualOwnerPid
            : null;

        var sut = new ServiceVerify(Daemons.Store, Config.Root, manager, ValidatedPid, Hello, TimeProvider.System, readPlist: OwnPlist);

        var exit = await sut.InstallVerifiedAsync(Spec(daemonPath), replace: true, ExpectedVersion);

        await Assert.That(exit).IsEqualTo(VerifyExit.Ok);
        await Assert.That(manager.UninstallCalls).IsEqualTo(1);
        await Assert.That(manager.WriteAndBootstrapCalls).IsEqualTo(1);
        // argv-order: uninstall (label-clear) precedes writeAndBootstrap — the kill+stop-verify
        // detour happens strictly between the two, driven entirely by the hello/pid seams above.
        await Assert.That(manager.Calls.IndexOf("uninstall")).IsLessThan(manager.Calls.IndexOf("writeAndBootstrap"));
        await Assert.That(helloCalls).IsGreaterThanOrEqualTo(2);
        await Assert.That(ServiceTxnMarker.Exists(Daemons.Store, Id)).IsFalse();
    }

    [Test]
    public async Task No_live_owner_with_a_stopped_unit_clears_without_a_kill_then_installs() {
        var (_, daemonPath) = SetUpViableInstall();
        // Absent label, plist still present (`service stop` retains it) — replace: true allows
        // clearing this (a fresh install would reject it as Contended). No validated live
        // owner at all, so the kill/stop-verification step must never engage.
        var manager = new FakeServiceManager(Home) { InitialProbe = LabelProbe.Absent, InitialUnitPresent = true, InitialJobPid = null };

        Task<HelloProbeResult> Hello(string _, TimeSpan __) =>
            Task.FromResult(new HelloProbeResult(true, 1, ExpectedVersion, Id));

        int? ValidatedPid(string _) => manager.Bootstrapped ? manager.RunningPid : null;

        var sut = new ServiceVerify(Daemons.Store, Config.Root, manager, ValidatedPid, Hello, TimeProvider.System, readPlist: OwnPlist);

        var exit = await sut.InstallVerifiedAsync(Spec(daemonPath), replace: true, ExpectedVersion);

        await Assert.That(exit).IsEqualTo(VerifyExit.Ok);
        await Assert.That(manager.UninstallCalls).IsEqualTo(1);
        await Assert.That(manager.Calls.IndexOf("uninstall")).IsLessThan(manager.Calls.IndexOf("writeAndBootstrap"));
        await Assert.That(ServiceTxnMarker.Exists(Daemons.Store, Id)).IsFalse();
    }

    [Test]
    public async Task No_unit_with_a_manual_owner_kills_without_an_uninstall_call() {
        var (_, daemonPath) = SetUpViableInstall();
        // Absent label, no plist at all — nothing to "clear" (Uninstall must never run) — but a
        // manual (non-service) daemon still holds the name, so the kill/stop-verification step
        // must engage on its own.
        var manager = new FakeServiceManager(Home) { InitialProbe = LabelProbe.Absent, InitialUnitPresent = false, InitialJobPid = null };

        var helloCalls = 0;
        Task<HelloProbeResult> Hello(string _, TimeSpan __) {
            helloCalls++;
            return Task.FromResult(helloCalls == 1
                ? new HelloProbeResult(false, null, null, null)
                : new HelloProbeResult(true, 1, ExpectedVersion, Id));
        }

        int? ValidatedPid(string _) =>
            manager.Bootstrapped ? manager.RunningPid
            : helloCalls == 0 ? ManualOwnerPid
            : null;

        var sut = new ServiceVerify(Daemons.Store, Config.Root, manager, ValidatedPid, Hello, TimeProvider.System, readPlist: OwnPlist);

        var exit = await sut.InstallVerifiedAsync(Spec(daemonPath), replace: true, ExpectedVersion);

        await Assert.That(exit).IsEqualTo(VerifyExit.Ok);
        await Assert.That(manager.UninstallCalls).IsEqualTo(0);
        await Assert.That(manager.WriteAndBootstrapCalls).IsEqualTo(1);
        await Assert.That(ServiceTxnMarker.Exists(Daemons.Store, Id)).IsFalse();
    }

    [Test]
    public async Task Live_owner_is_re_read_after_clearing_the_label_a_stale_pre_clear_pid_is_never_killed() {
        var (_, daemonPath) = SetUpViableInstall();
        // JobPid comes back null even though the label truly owns a live daemon (a
        // launchctl-print race) — "owning" is false, so the matrix takes the clear-then-kill
        // branch. Uninstall (bootout) itself terminates the process it unloads, so by the time
        // the matrix would reach the kill step, the pid captured BEFORE the clear is stale — a
        // fresh re-read afterward must see no live owner and skip the kill/stop-confirm poll
        // entirely, never signal whatever the stale pid might now belong to.
        var manager = new FakeServiceManager(Home) { InitialProbe = LabelProbe.Loaded, InitialUnitPresent = true, InitialJobPid = null };

        var helloCalls = 0;
        Task<HelloProbeResult> Hello(string _, TimeSpan __) {
            helloCalls++;
            return Task.FromResult(new HelloProbeResult(true, 1, ExpectedVersion, Id));
        }

        // Live (ManualOwnerPid) BEFORE Uninstall runs, gone the moment it does — models bootout
        // terminating the true owner as a side effect of unloading its label.
        int? ValidatedPid(string _) =>
            manager.Bootstrapped ? manager.RunningPid
            : manager.Uninstalled ? null
            : ManualOwnerPid;

        var sut = new ServiceVerify(Daemons.Store, Config.Root, manager, ValidatedPid, Hello, TimeProvider.System, readPlist: OwnPlist);

        var exit = await sut.InstallVerifiedAsync(Spec(daemonPath), replace: true, ExpectedVersion);

        await Assert.That(exit).IsEqualTo(VerifyExit.Ok);
        await Assert.That(manager.UninstallCalls).IsEqualTo(1);
        // Exactly the readiness poll's two hello calls (primary + confirm) — if the re-read fix
        // regressed to the stale pre-clear pid, the stop-confirmation poll would engage first
        // (hello is well-formed, so IsStoppedAsync never confirms) and either hang or add extra
        // hello calls before this assertion could ever see exactly 2.
        await Assert.That(helloCalls).IsEqualTo(2);
        await Assert.That(ServiceTxnMarker.Exists(Daemons.Store, Id)).IsFalse();
    }

    [Test]
    public async Task Stop_never_confirmed_reports_stop_unconfirmed_and_writes_nothing() {
        var (_, daemonPath) = SetUpViableInstall();
        // A live owner whose pid never goes away and whose hello never stops answering — the
        // stop-confirmation poll can never succeed, so it must time out against the forward
        // budget rather than hang forever.
        var manager = new FakeServiceManager(Home) { InitialProbe = LabelProbe.Loaded, InitialUnitPresent = true, InitialJobPid = null };
        var time    = new FakeTimeProvider();

        static Task<HelloProbeResult> Hello(string _, TimeSpan __) =>
            Task.FromResult(new HelloProbeResult(false, null, null, null)); // never well-formed

        var sut = new ServiceVerify(Daemons.Store, Config.Root, manager, _ => ManualOwnerPid, Hello, time, forwardBudget: TimeSpan.FromSeconds(2), readPlist: OwnPlist);

        var task = sut.InstallVerifiedAsync(Spec(daemonPath), replace: true, ExpectedVersion);
        var exit = await Drive(task, time, TimeSpan.FromMilliseconds(500));

        await Assert.That(exit).IsEqualTo(VerifyExit.StopUnconfirmed);
        await Assert.That(manager.WriteAndBootstrapCalls).IsEqualTo(0);
        await Assert.That(manager.UninstallCalls).IsEqualTo(1); // the label was still cleared
        await Assert.That(ServiceTxnMarker.Exists(Daemons.Store, Id)).IsTrue();
        await Assert.That(ServiceTxnMarker.Read(Daemons.Store, Id)!.Phase).IsEqualTo("label-cleared");
    }

    [Test]
    public async Task Clear_that_never_confirms_absent_aborts_before_writing_anything() {
        var (_, daemonPath) = SetUpViableInstall();
        // Uninstall reports failure (label still Loaded — the real LaunchdServiceManager
        // contract when bootout fails and a re-query still shows it loaded) and Query never
        // settles to Absent either. The matrix must abort rather than trust a failed/unconfirmed
        // clear and write a new unit over a label that was never actually cleared.
        var manager = new FakeServiceManager(Home) { InitialProbe = LabelProbe.Loaded, InitialUnitPresent = true, InitialJobPid = null, UninstallSucceeds = false };
        var time    = new FakeTimeProvider();

        static Task<HelloProbeResult> Hello(string _, TimeSpan __) =>
            Task.FromResult(new HelloProbeResult(false, null, null, null));

        var sut = new ServiceVerify(Daemons.Store, Config.Root, manager, _ => null, Hello, time,
            forwardBudget: TimeSpan.FromSeconds(2), rollbackReserve: TimeSpan.FromSeconds(1), readPlist: OwnPlist);

        var task = sut.InstallVerifiedAsync(Spec(daemonPath), replace: true, ExpectedVersion);
        var exit = await Drive(task, time, TimeSpan.FromMilliseconds(500));

        await Assert.That(exit).IsEqualTo(VerifyExit.RestoreVerification);
        await Assert.That(manager.WriteAndBootstrapCalls).IsEqualTo(0);
        await Assert.That(manager.UninstallCalls).IsEqualTo(1);
        // The marker never advanced past "captured" — Uninstall's failure was never treated as
        // a completed clear.
        await Assert.That(ServiceTxnMarker.Exists(Daemons.Store, Id)).IsTrue();
        await Assert.That(ServiceTxnMarker.Read(Daemons.Store, Id)!.Phase).IsEqualTo("captured");
    }

    [Test]
    public async Task Unknown_probe_aborts_before_the_matrix_runs_anything_destructive() {
        var (_, daemonPath) = SetUpViableInstall();
        var manager = new FakeServiceManager(Home) { InitialProbe = LabelProbe.Unknown };
        var sut = new ServiceVerify(Daemons.Store, Config.Root, manager, _ => ManualOwnerPid, (_, _) => Task.FromResult(new HelloProbeResult(false, null, null, null)), TimeProvider.System, readPlist: OwnPlist);

        var exit = await sut.InstallVerifiedAsync(Spec(daemonPath), replace: true, ExpectedVersion);

        await Assert.That(exit).IsEqualTo(VerifyExit.BootoutUnknown);
        await Assert.That(manager.QueryCalls).IsEqualTo(1);
        await Assert.That(manager.UninstallCalls).IsEqualTo(0);
        await Assert.That(manager.WriteAndBootstrapCalls).IsEqualTo(0);
        await Assert.That(ServiceTxnMarker.Exists(Daemons.Store, Id)).IsFalse();
    }

    [Test]
    public async Task Owning_label_whose_pid_never_exits_reports_stop_unconfirmed_and_writes_nothing() {
        var (_, daemonPath) = SetUpViableInstall();
        // Owning label (JobPid == validated), but the old daemon never releases the name — its
        // validated pid stays non-null past the forward budget, so the takeover must not write a
        // replacement over a name the old incarnation still holds.
        var manager = new FakeServiceManager(Home) { InitialProbe = LabelProbe.Loaded, InitialUnitPresent = true, InitialJobPid = ManualOwnerPid };
        var time    = new FakeTimeProvider();

        static Task<HelloProbeResult> Hello(string _, TimeSpan __) =>
            Task.FromResult(new HelloProbeResult(true, 1, ExpectedVersion, Id));

        var sut = new ServiceVerify(Daemons.Store, Config.Root, manager, _ => ManualOwnerPid, Hello, time, forwardBudget: TimeSpan.FromSeconds(2), readPlist: OwnPlist);

        var task = sut.InstallVerifiedAsync(Spec(daemonPath), replace: true, ExpectedVersion);
        var exit = await Drive(task, time, TimeSpan.FromMilliseconds(500));

        await Assert.That(exit).IsEqualTo(VerifyExit.StopUnconfirmed);
        await Assert.That(manager.WriteAndBootstrapCalls).IsEqualTo(0);
        await Assert.That(manager.UninstallCalls).IsEqualTo(1); // the label was cleared first
        await Assert.That(ServiceTxnMarker.Read(Daemons.Store, Id)!.Phase).IsEqualTo("label-cleared");
    }

    [Test]
    public async Task GenerateFiles_throwing_under_replace_is_a_viability_abort_touching_nothing() {
        var (_, daemonPath) = SetUpViableInstall();
        // The classic --replace hazard: an XML-unrepresentable captured env value makes the plist
        // render throw. That must abort as a VIABILITY failure BEFORE the old label/owner is
        // cleared — never destroy the working unit and only then discover the new one can't render.
        var manager = new ThrowingRenderManager(Home);
        var sut = new ServiceVerify(Daemons.Store, Config.Root, manager, _ => ManualOwnerPid, (_, _) => Task.FromResult(new HelloProbeResult(false, null, null, null)),
            TimeProvider.System, readPlist: OwnPlist);

        var exit = await sut.InstallVerifiedAsync(Spec(daemonPath), replace: true, ExpectedVersion);

        await Assert.That(exit).IsEqualTo(VerifyExit.Viability);
        await Assert.That(manager.DestructiveCalls).IsEqualTo(0); // no query/uninstall/bootstrap
        await Assert.That(ServiceTxnMarker.Exists(Daemons.Store, Id)).IsFalse();
    }

    [Test]
    public async Task Invalid_pinned_profile_url_under_replace_is_a_viability_abort_touching_nothing() {
        var (_, daemonPath) = SetUpViableInstall();
        var manager = new FakeServiceManager(Home) { InitialProbe = LabelProbe.Loaded, InitialUnitPresent = true, InitialJobPid = ManualOwnerPid };
        var sut = new ServiceVerify(Daemons.Store, Config.Root, manager, _ => ManualOwnerPid, (_, _) => Task.FromResult(new HelloProbeResult(false, null, null, null)),
            TimeProvider.System, readPlist: OwnPlist, profileViable: () => false);

        var exit = await sut.InstallVerifiedAsync(Spec(daemonPath), replace: true, ExpectedVersion);

        await Assert.That(exit).IsEqualTo(VerifyExit.Viability);
        await Assert.That(manager.Calls).IsEmpty();
        await Assert.That(ServiceTxnMarker.Exists(Daemons.Store, Id)).IsFalse();
    }

    [Test]
    public async Task Clearing_an_orphan_plist_re_uninstalls_until_the_file_is_gone_then_installs() {
        var (_, daemonPath) = SetUpViableInstall();
        // The first bootout fails and RETAINS the plist; the label later reads Absent while the
        // file lingers. Clearing must re-uninstall the now-unloaded unit and confirm the file gone
        // — never treat an orphan plist as a clean clear.
        var manager = new OrphanPlistManager(Home, reUninstallRemovesPlist: true);
        var time    = new FakeTimeProvider();

        static Task<HelloProbeResult> Hello(string _, TimeSpan __) =>
            Task.FromResult(new HelloProbeResult(true, 1, ExpectedVersion, Id));

        var sut = new ServiceVerify(Daemons.Store, Config.Root, manager, _ => manager.Bootstrapped ? manager.RunningPid : null, Hello, time,
            forwardBudget: TimeSpan.FromSeconds(5), readPlist: OwnPlist);

        var task = sut.InstallVerifiedAsync(Spec(daemonPath), replace: true, ExpectedVersion);
        var exit = await Drive(task, time, TimeSpan.FromMilliseconds(500));

        await Assert.That(exit).IsEqualTo(VerifyExit.Ok);
        await Assert.That(manager.UninstallCalls).IsGreaterThanOrEqualTo(2); // first failed, re-uninstall removed the file
        await Assert.That(manager.PlistPresent).IsFalse();
        await Assert.That(manager.WriteAndBootstrapCalls).IsEqualTo(1);
        await Assert.That(ServiceTxnMarker.Exists(Daemons.Store, Id)).IsFalse();
    }

    [Test]
    public async Task Clearing_an_orphan_plist_that_cannot_be_removed_fails_coded_without_installing() {
        var (_, daemonPath) = SetUpViableInstall();
        // Same orphan-plist shape, but the re-uninstall never manages to remove the file — Absent
        // label with the unit still on disk is an affirmatively wrong state, so this fails coded
        // rather than declaring success (and never writes a new unit over the residue).
        var manager = new OrphanPlistManager(Home, reUninstallRemovesPlist: false);
        var time    = new FakeTimeProvider();

        var sut = new ServiceVerify(Daemons.Store, Config.Root, manager, _ => manager.Bootstrapped ? manager.RunningPid : null,
            (_, _) => Task.FromResult(new HelloProbeResult(false, null, null, null)), time,
            forwardBudget: TimeSpan.FromSeconds(2), readPlist: OwnPlist);

        var task = sut.InstallVerifiedAsync(Spec(daemonPath), replace: true, ExpectedVersion);
        var exit = await Drive(task, time, TimeSpan.FromMilliseconds(500));

        await Assert.That(exit).IsEqualTo(VerifyExit.RestoreVerification);
        await Assert.That(manager.WriteAndBootstrapCalls).IsEqualTo(0);
        await Assert.That(manager.PlistPresent).IsTrue();
    }

    /// <summary>Renders throw (an XML-unrepresentable captured env value) and records whether ANY
    /// launchctl-touching op ran — the render-viability guard must abort before all of them.</summary>
    sealed class ThrowingRenderManager(UserHome home) : IVerifyServiceManager {
        public string UnitPath(string serviceId) => LaunchdUnit.PlistPath(home, serviceId);
        public int DestructiveCalls;
        public IReadOnlyList<GeneratedFile> GenerateFiles(ServiceSpec spec) => throw new InvalidOperationException("invalid captured env value");
        public ServiceQuery Query(string serviceId, TimeSpan timeout) { DestructiveCalls++; return new(LabelProbe.Absent, false, ServiceState.NotInstalled, null, null); }
        public void WriteAndBootstrap(ServiceSpec spec, TimeSpan timeout) => DestructiveCalls++;
        public bool Uninstall(string serviceId, TimeSpan timeout, out string? error) { DestructiveCalls++; error = null; return true; }
        public bool Start(string serviceId, TimeSpan timeout, out string? error) { DestructiveCalls++; error = null; return true; }
        public bool StartBootstrapOnly(string serviceId, TimeSpan timeout, out string? error) => Start(serviceId, timeout, out error);
        public bool Stop(string serviceId, TimeSpan timeout, out string? error) { DestructiveCalls++; error = null; return true; }
    }

    /// <summary>A non-owning Loaded label whose first bootout fails and RETAINS the plist; the label
    /// then unloads while the file lingers, so a clear must re-uninstall to remove it.</summary>
    sealed class OrphanPlistManager(UserHome home, bool reUninstallRemovesPlist) : IVerifyServiceManager {
        public string UnitPath(string serviceId) => LaunchdUnit.PlistPath(home, serviceId);
        public readonly List<string> Calls = [];
        public int UninstallCalls;
        public bool PlistPresent = true;
        public bool Bootstrapped;
        public int? RunningPid = 7777;
        int _queriesAfterFirstUninstall;

        public int WriteAndBootstrapCalls => Calls.Count(c => c == "writeAndBootstrap");

        public IReadOnlyList<GeneratedFile> GenerateFiles(ServiceSpec spec) => [new GeneratedFile("/fake/agents/orphan.plist", OwnPlistContent)];

        public ServiceQuery Query(string serviceId, TimeSpan timeout) {
            Calls.Add("query");
            if (Bootstrapped) return new(LabelProbe.Loaded, true, ServiceState.Running, "/bin/kcap-daemon", RunningPid);
            if (UninstallCalls == 0) return new(LabelProbe.Loaded, true, ServiceState.NotInstalled, null, null); // pre-clear: loaded, non-owning
            _queriesAfterFirstUninstall++;
            // Label lingers Loaded for one poll, then unloads — but the plist stays until re-uninstalled.
            var probe = _queriesAfterFirstUninstall >= 2 ? LabelProbe.Absent : LabelProbe.Loaded;
            return new(probe, PlistPresent, ServiceState.NotInstalled, null, null);
        }

        public void WriteAndBootstrap(ServiceSpec spec, TimeSpan timeout) { Calls.Add("writeAndBootstrap"); Bootstrapped = true; }

        public bool Uninstall(string serviceId, TimeSpan timeout, out string? error) {
            Calls.Add("uninstall");
            UninstallCalls++;
            if (UninstallCalls == 1) {
                error = "launchctl bootout failed (exit 5) and the label is still Loaded — plist retained";
                return false; // first bootout fails, plist retained
            }
            if (reUninstallRemovesPlist) PlistPresent = false; // re-uninstall of the unloaded label deletes the file
            error = null;
            return true;
        }

        public bool Start(string serviceId, TimeSpan timeout, out string? error) { error = null; return true; }
        public bool StartBootstrapOnly(string serviceId, TimeSpan timeout, out string? error) => Start(serviceId, timeout, out error);
        public bool Stop(string serviceId, TimeSpan timeout, out string? error) { error = null; return true; }
    }
}
