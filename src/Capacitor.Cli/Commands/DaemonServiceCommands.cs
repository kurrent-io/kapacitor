using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Commands;

/// <summary>
/// The <c>kcap daemon service</c> verbs. The OS service manager and the service id are fixed for a
/// whole invocation — <see cref="DispatchAsync"/> resolves both once — so they are state here rather
/// than two arguments threaded through every verb.
/// </summary>
sealed class DaemonServiceCommands(
        DaemonStore store, ConfigRoot root, ProfileContext profiles, IServiceManager manager, string id,
        UserHome home) {
    /// <summary>Resolves the OS service manager and the service id once, then runs one verb.</summary>
    public static async Task<int> DispatchAsync(
            DaemonStore store, ConfigRoot root, ProfileContext profiles, UserHome home, string[] args) {
        if (args.Length == 0) return Usage();

        var action  = args[0];
        var rest    = args[1..];
        var noStart = rest.Contains("--no-start");

        IServiceManager manager;
        try {
            manager = ServiceManagerFactory.ForCurrentOs(root, home);
        } catch (PlatformNotSupportedException ex) {
            await Console.Error.WriteLineAsync(ex.Message);
            return 1;
        }

        var verbs = new DaemonServiceCommands(
            store, root, profiles, manager,
            DaemonStore.Sanitize(DaemonNameResolver.Resolve(rest, profiles.DaemonName)), home);

        return action switch {
            "install"   => await verbs.Install(rest, startNow: !noStart),
            "uninstall" => await verbs.Uninstall(),
            "start"     => await verbs.Start(rest),
            "stop"      => await verbs.Stop(),
            "ensure"    => await verbs.Ensure(rest),
            "status"    => rest.Contains("--json") ? await verbs.StatusJson() : await verbs.Status(),
            _           => Usage(),
        };
    }

    /// <summary>
    /// Runs the ensure ladder for the browser flow, or <b>null on a platform with no service
    /// manager</b> — the caller renders that as its own refusal rather than as a failed transaction.
    /// </summary>
    internal static async Task<ServiceEnsureJson?> FlowEnsureAsync(
            ConfigRoot root, ProfileContext profiles, UserHome home) {
        DaemonServiceCommands verbs;

        try {
            verbs = ForFlow(root, profiles, home);
        } catch (PlatformNotSupportedException) {
            return null;
        }

        var (result, _) = await verbs.EvaluateEnsureAsync(profiles.Resolution.ProfileName);

        return result;
    }

    /// <summary>
    /// Whether the service is enabled, as the flow reports it once on the create: <c>true</c> reachable,
    /// <c>false</c> not and <c>ensure</c> has a verb for it, <c>null</c> nothing claimable.
    ///
    /// <para><b>Never throws.</b> It resolves a service manager and runs a manager query — a subprocess —
    /// and the caller's own catch drops the machine's whole harness report, so a failure here has to cost
    /// one field rather than all of them.</para>
    /// </summary>
    internal static async Task<bool?> FlowServiceEnabledAsync(
            ConfigRoot root, ProfileContext profiles, UserHome home) {
        try {
            return await ForFlow(root, profiles, home).ServiceEnabledAsync();
        } catch (Exception) {
            return null;
        }
    }

    /// <summary>The flow's own resolution of the manager and the service id, matching
    /// <see cref="DispatchAsync"/>'s with no command-line arguments to draw a daemon name from.</summary>
    static DaemonServiceCommands ForFlow(ConfigRoot root, ProfileContext profiles, UserHome home) =>
        new(DaemonStore.FromEnvironment(), root, profiles, ServiceManagerFactory.ForCurrentOs(root, home),
            DaemonStore.Sanitize(DaemonNameResolver.Resolve([], profiles.DaemonName)), home);

    /// <summary>
    /// The fact behind the flow's offer, from the same evidence <see cref="Ensure"/> classifies.
    ///
    /// <para><b>Offerable, not merely absent.</b> The classifier is half of the ladder's refusal surface:
    /// the rest is decided in the arms, so a fact taken from the classifier alone draws a button on a
    /// machine whose only possible answer is no.</para>
    /// </summary>
    async Task<bool?> ServiceEnabledAsync() {
        var profileName = profiles.Resolution.ProfileName;
        var query       = manager.Query(id);

        var decision = EnsureClassifier.Classify(
            query.Probe, query.State, query.UnitPresent,
            DaemonPidProbe.ValidatedPid(store, id),
            ServiceTxnMarker.Exists(store, id), ServiceTxnLock.IsHeld(store, id),
            query.JobPid, manager is LaunchdServiceManager);

        if (decision.Action is EnsureAction.AlreadyEnabled) return true;
        if (decision.Action is not (EnsureAction.Install or EnsureAction.Start)) return null;

        if (LaunchdProfileRefusal(manager is LaunchdServiceManager, profileName, decision.Action)) return null;

        // The same resolution the arms run, not a second reading of it: off launchd the profile name can
        // be null, where this falls through to a URL the environment may supply — and a fact that missed
        // that arm would withhold an offer the action would have taken.
        if (await ResolveServerUrlAsync(profileName) is null) return null;

        // An install bakes the daemon's path, and launchd's start gate compares that path against the
        // unit's — so a missing daemon binary refuses on both. A plain start elsewhere restarts a unit
        // that already names one, and demanding it there would withhold an offer that would have worked.
        if ((decision.Action is EnsureAction.Install || manager is LaunchdServiceManager)
            && UnitIdentity.ResolveDaemonBinary() is null) return null;

        return false;
    }

    /// <summary>
    /// The <c>ExtraArgs</c> baked into a service unit, from the raw <c>--max-agents</c> flag value.
    ///
    /// <para>Validated here rather than only escaped at the unit writers. This is the ONLY caller-supplied
    /// entry in <c>ExtraArgs</c>, it arrives unparsed off the command line, and it is persisted into a file
    /// the OS executes at every logon — so a value that is not the integer the flag documents has no
    /// legitimate reading, and accepting one turns a typo into a durable startup artifact. Review raised the
    /// sink; this is the matching narrowing at the source, so the two are independent.</para>
    /// </summary>
    internal static List<string> ExtraArgs(string? maxAgentsFlag) {
        if (maxAgentsFlag is null) return [];

        if (!int.TryParse(maxAgentsFlag, out var maxAgents) || maxAgents < 1)
            throw new ArgumentException($"--max-agents must be a positive integer (got '{maxAgentsFlag}').");

        return ["--max-agents", maxAgents.ToString()];
    }

    internal async Task<int> Install(string[] args, bool startNow) {
        var verify  = args.Contains("--verify");
        var replace = args.Contains("--replace");

        // --no-start withholds the start; --verify's whole job is to prove the started daemon is
        // ready. The two contradict — reject rather than silently start anyway (or silently skip
        // verification).
        if (verify && !startNow) {
            await Console.Error.WriteLineAsync("--no-start is incompatible with --verify.");
            return 1;
        }

        // --replace only has meaning inside the verify transaction engine (it selects the
        // ownership matrix in ServiceVerify.InstallVerifiedAsync) — a plain (non-verified) install
        // has no transaction to hand it to.
        if (replace && !verify) {
            await Console.Error.WriteLineAsync("install --replace requires --verify.");
            return 1;
        }

        // --verify is a launchd-only slice for now: the engine's readiness/version check needs a
        // manager that actually implements a verify-aware WriteAndBootstrap, and the on-disk
        // recheck needs GenerateFiles to return exactly one file (Windows returns two). Reject
        // early and clearly rather than let either assumption fail deep inside the transaction.
        if (verify && manager is not LaunchdServiceManager) {
            await Console.Error.WriteLineAsync("install --verify is only supported on macOS (launchd) in this release.");
            return 1;
        }

        var daemonPath = UnitIdentity.ResolveDaemonBinary();
        if (daemonPath is null) { await Console.Error.WriteLineAsync(DaemonCommands.DaemonNotFoundMessage()); return 1; }

        var profileName = DaemonCommands.ExtractFlagValue(args, "--profile") ?? profiles.Resolution.ProfileName;
        var env = new Dictionary<string, string>(ServiceEnvironment.Capture(profileName, root)) {
            ["KCAP_DAEMON_SUPERVISED"] = id,   // name-specific; daemon honors it only when == its sanitized --name
        };

        List<string> extra;
        try {
            extra = ExtraArgs(DaemonCommands.ExtractFlagValue(args, "--max-agents"));
        } catch (ArgumentException ex) {
            await Console.Error.WriteLineAsync(ex.Message);

            return 1;
        }

        var logPath = root.Path($"daemon-{id}.log");
        var spec    = new ServiceSpec(id, daemonPath, logPath, env, extra);

        if (verify) {
            // The CLI is the safety boundary for §4.1's precondition: prove the pinned profile resolves
            // to a valid server URL here too, so the transaction never destroys a working unit only to
            // install one whose daemon would exit config-invalid and never satisfy readiness.
            var profileUrlValid = await ServiceInstallViability.PinnedProfileServerUrlValidAsync(env, root);
            var engine = new ServiceVerify(store, root, (LaunchdServiceManager)manager,
                n => DaemonPidProbe.ValidatedPid(store, n), (n, t) => HelloProbe.RunAsync(store, n, t),
                TimeProvider.System, profileViable: () => profileUrlValid, gateEnv: Environment.GetEnvironmentVariable);
            var exit   = await engine.InstallVerifiedAsync(spec, replace: replace, CapacitorVersion.Current());
            if (exit != VerifyExit.Ok) return exit;
        } else {
            // Plain (non-verify) install serializes on the same per-label lock every other mutating
            // verb takes — a terminal install must not race an app-driven --replace --verify.
            var exit = await InstallPlain(spec, startNow);
            if (exit != 0) return exit;
        }

        // Closed-stdio tolerance for the entire success tail (not just the first line): the npm
        // grandchild shares the GUI's pipes, and by this point the real mutation already happened
        // — a broken pipe on any of these purely informational lines must never turn an
        // already-successful install into a crash/non-zero exit.
        try {
            if (verify) {
                await Console.Out.WriteLineAsync($"Service '{id}' installed (verified, {manager.Describe()}).");
            } else {
                await Console.Out.WriteLineAsync($"Service '{id}' installed ({manager.Describe()}).");
                await Console.Out.WriteLineAsync("  Auto-restarts on crash/SIGKILL; starts at login.");
            }

            // A reviewer switch frozen into a unit is worth saying out loud: the unit outlives the shell it
            // was captured from, so an operator who later changes the variable has no effect until they
            // reinstall. The line must state what the captured VALUE does, not assume it enables — since
            // these became opt-outs, a captured `0` DISABLES, and the old "the reviewer it enables stays on"
            // text was exactly backwards for that case. Classified through the same Core parser the daemon
            // reads, so the notice and the daemon can never disagree about a value's meaning.
            foreach (var flag in ServiceEnvironment.CarriedConsentFlags(env)) {
                var effect = ReviewerConsent.IsEnabled(env[flag])
                    ? "keeps that reviewer ENABLED (already the default)"
                    : "DISABLES that reviewer";
                await Console.Out.WriteLineAsync(
                    $"  Reviewer:  {flag}={env[flag]} captured into the unit — {effect} for this service "
                  + "until you reinstall with a different value.");
            }

            await Console.Out.WriteLineAsync($"  Log:       {logPath}");
            await Console.Out.WriteLineAsync($"  Stop:      kcap daemon service stop --name {id}");
            await Console.Out.WriteLineAsync($"  Remove:    kcap daemon service uninstall --name {id}");
        } catch (IOException) { }

        return 0;
    }

    /// <summary>Plain install under the per-label service lock, matching stop/start/uninstall:
    /// null lock → the same coded-contention message, exit 1, without calling <c>Install</c>. Internal so
    /// the lock-contention path is testable without <see cref="UnitIdentity.ResolveDaemonBinary"/> in the loop.</summary>
    internal async Task<int> InstallPlain(ServiceSpec spec, bool startNow) {
        using var txn = ServiceTxnLock.TryAcquire(store, spec.ServiceId, TimeSpan.FromSeconds(10));

        if (txn is null) {
            await Console.Error.WriteLineAsync($"Another service operation is in progress for '{spec.ServiceId}'. Try again shortly.");
            return 1;
        }

        manager.Install(spec, startNow);
        return 0;
    }

    async Task<int> Uninstall() {
        using var txn = ServiceTxnLock.TryAcquire(store, id, TimeSpan.FromSeconds(10));

        if (txn is null) {
            await Console.Error.WriteLineAsync($"Another service operation is in progress for '{id}'. Try again shortly.");
            return 1;
        }

        if (!manager.Uninstall(id, out var error)) {
            await Console.Error.WriteLineAsync($"Could not uninstall service '{id}': {error}");
            return 1;
        }

        await Console.Out.WriteLineAsync($"Service '{id}' uninstalled ({manager.Describe()}).");
        return 0;
    }

    /// <summary>
    /// Routes plain vs <c>--verify</c> starts. <c>--verify</c> is gated to launchd like
    /// <see cref="Install"/>'s own gate — the engine's readiness/ownership poll needs a
    /// manager whose WriteAndBootstrap/Query actually implement the verify algorithm.
    /// </summary>
    internal async Task<int> Start(string[] args) {
        if (!args.Contains("--verify")) return await StartPlain();

        if (manager is not LaunchdServiceManager) {
            await Console.Error.WriteLineAsync("start --verify is only supported on macOS (launchd) in this release.");
            return 1;
        }

        return await StartVerified();
    }

    async Task<int> StartPlain() {
        using var txn = ServiceTxnLock.TryAcquire(store, id, TimeSpan.FromSeconds(10));

        if (txn is null) {
            await Console.Error.WriteLineAsync($"Another service operation is in progress for '{id}'. Try again shortly.");
            return 1;
        }

        if (!manager.Start(id, out var error)) {
            await Console.Error.WriteLineAsync($"Could not start service '{id}': {error}");
            return 1;
        }

        await Console.Out.WriteLineAsync($"Service '{id}' started.");
        return 0;
    }

    /// <summary>
    /// <c>--verify</c>: hands off to the <see cref="ServiceVerify"/> transaction engine, which
    /// acquires the <see cref="ServiceTxnLock"/> itself — no double-acquire here.
    /// </summary>
    async Task<int> StartVerified() {
        var engine = new ServiceVerify(store, root, (LaunchdServiceManager)manager,
            n => DaemonPidProbe.ValidatedPid(store, n), (n, t) => HelloProbe.RunAsync(store, n, t), TimeProvider.System,
            gateEnv: Environment.GetEnvironmentVariable);
        var exit = await engine.StartVerifiedAsync(id);

        // Same closed-stdio tolerance as the engine's own Say: a broken pipe on this purely
        // informational line must not turn an already-successful verified start into a crash.
        if (exit == VerifyExit.Ok) {
            try { await Console.Out.WriteLineAsync($"Service '{id}' started (verified)."); }
            catch (IOException) { }
        }

        return exit;
    }

    async Task<int> Stop() {
        using var txn = ServiceTxnLock.TryAcquire(store, id, TimeSpan.FromSeconds(10));

        if (txn is null) {
            await Console.Error.WriteLineAsync($"Another service operation is in progress for '{id}'. Try again shortly.");
            return 1;
        }

        if (!manager.Stop(id, out var error)) {
            await Console.Error.WriteLineAsync($"Could not stop service '{id}': {error}");
            return 1;
        }

        await Console.Out.WriteLineAsync($"Service '{id}' stopped (still installed).");
        return 0;
    }

    async Task<int> Status() {
        var status = manager.Status(id);
        await Console.Out.WriteLineAsync($"Service '{id}': {status.State} ({manager.Describe()})");
        if (status.BinaryPath is { } bin) await Console.Out.WriteLineAsync($"  binary: {bin}");
        return 0;
    }

    async Task<int> StatusJson() {
        var query           = manager.Query(id);
        var installBinary   = UnitIdentity.ResolveDaemonBinary();
        var daemonPid       = DaemonPidProbe.ValidatedPid(store, id);
        var txnActive       = ServiceTxnLock.IsHeld(store, id);
        var txnMarker       = ServiceTxnMarker.Exists(store, id);
        var (unitProfile, unitServerUrl, unitExpectedServer, unitConsentSeed) = UnitEnvEvidence(query.UnitPresent);

        var (json, exitCode) = ServiceStatusRender.Render(
            query, id, installBinary, daemonPid, txnMarker, txnActive,
            unitProfile, unitServerUrl, unitExpectedServer, unitConsentSeed);

        if (json is null) {
            await Console.Error.WriteLineAsync($"Could not determine service status for '{id}' ({manager.Describe()}).");
            return exitCode;
        }

        await Console.Out.WriteLineAsync(json);
        return exitCode;
    }

    /// <summary>
    /// The flow's daemon-install ladder: from a fresh status read, install or start so the machine is
    /// reachable, baking the born-<c>prompt</c> consent-seed directive on install and gating the start
    /// exactly as an app-managed start is. A gate refusal exits with the verify transaction's coded
    /// exit plus one <c>start_gate_reason=</c> line, mapped machine-readably to
    /// <c>recovery_surface=takeover|reinstall|attention</c> (the pinned <see cref="ReasonRouting"/>
    /// table) — never guessed at from prose. On non-launchd the ladder degrades to plain install/start
    /// (no gates, no rollback); the JSON reports <c>verified:false</c> so the flow's copy can say so.
    /// On launchd a resolvable profile is required up front — a unit baked without one could never
    /// pass the start gate's identity half, so ensure refuses rather than report a dead-end install.
    /// </summary>
    internal async Task<int> Ensure(string[] args) {
        var json        = args.Contains("--json");
        // Same explicit-flag-wins-then-resolved default as Install: the gate's identity half needs a
        // non-empty KCAP_PROFILE, so a bare `ensure` on launchd must still carry the resolved name.
        var profileName = DaemonCommands.ExtractFlagValue(args, "--profile") ?? profiles.Resolution.ProfileName;

        var (result, exit) = await EvaluateEnsureAsync(profileName);

        return await Report(result, exit, json);
    }

    /// <summary>
    /// The ladder alone: what <see cref="Ensure"/> decided and did, with no console output of its own
    /// and no reporting.
    ///
    /// <para><b>The one place the operation is composed</b>, so the browser flow and the verb cannot end
    /// up running different ladders — the same split <c>DaemonShimCommands.EvaluateAsync</c> has, and for
    /// the same reason.</para>
    ///
    /// <para><b>Not silent, though.</b> The mutating arms run the verify engine, which writes its coded
    /// tokens to stderr; what this method drops is the verb's own reporting, not the transaction's
    /// diagnostics. A caller that owns the terminal has to stop competing for it first.</para>
    /// </summary>
    internal async Task<(ServiceEnsureJson Result, int Exit)> EvaluateEnsureAsync(string? profileName) {
        var query     = manager.Query(id);
        var daemonPid = DaemonPidProbe.ValidatedPid(store, id);
        var txnActive = ServiceTxnLock.IsHeld(store, id);
        var txnMarker = ServiceTxnMarker.Exists(store, id);

        // The classifier gets the job pid plus whether THIS manager can supply one: on launchd,
        // already-enabled additionally requires the running job to own the validated daemon pid
        // (systemd/Windows cannot report one, so their coarser verified:false arm stands).
        var decision = EnsureClassifier.Classify(
            query.Probe, query.State, query.UnitPresent, daemonPid, txnMarker, txnActive,
            query.JobPid, manager is LaunchdServiceManager);

        var state = ServiceStateToken(query);

        // On launchd the start gate's identity half demands a non-empty invoking KCAP_PROFILE, so a
        // unit baked without one can never be gated-started — installing it bakes a dead end. Fail
        // closed with a coded reason rather than letting the install report success. (A URL-only
        // unit is only coherent off-macOS, where there is no gate; there the plain arms stand.)
        if (LaunchdProfileRefusal(manager is LaunchdServiceManager, profileName, decision.Action)) {
            var refusedAction = decision.Action == EnsureAction.Install ? "install" : "start";
            return (new ServiceEnsureJson(id, state, refusedAction, "refused", null, "no_profile_configured"), 1);
        }

        switch (decision.Action) {
            case EnsureAction.AlreadyEnabled:
                return (new ServiceEnsureJson(id, state, "none", "already_enabled"), 0);
            case EnsureAction.Attention:
                return (new ServiceEnsureJson(id, state, "none", "attention", null, decision.Reason), 1);
            case EnsureAction.Install:
                return await EnsureInstall(profileName, state);
            case EnsureAction.Start:
                return await EnsureStart(profileName, state);
            default:
                // Unreachable: Classify is total over EnsureAction. Kept as a fail-closed tail so an
                // added enum member can never fall through to exit 0.
                return (new ServiceEnsureJson(id, state, "none", "attention", null, "status_unknown"), 1);
        }
    }

    /// <summary>
    /// The server URL the unit bakes as <c>KCAP_EXPECT_SERVER_URL</c>, or null when none resolves.
    /// An explicit <c>--profile P</c> resolves P's own URL (a flag naming a different profile than
    /// the active one must not bake the active profile's URL — the unit would refuse to boot);
    /// otherwise the resolved profile's.
    /// </summary>
    async Task<string?> ResolveServerUrlAsync(string? profileName) =>
        profileName is null
            ? profiles.Resolution.ServerUrl
            : await ResolveNamedProfileServerUrlAsync(profileName);

    async Task<string?> ResolveNamedProfileServerUrlAsync(string profileName) {
        var config = await AppConfig.LoadProfileConfig(root);
        return config.Profiles.TryGetValue(profileName, out var p) ? p.ServerUrl : null;
    }

    /// <summary>Wire token for the fresh-read state. An Unknown probe is reported as "unknown" rather
    /// than falling through to a state value — the same never-masquerade rule status --json applies.
    /// A unit that is present but stopped reads <c>NotInstalled</c> on launchd (the label is not
    /// loaded) yet is genuinely installed — derive from unit presence so the JSON never pairs
    /// <c>state:"not_installed"</c> with <c>action:"start"</c>.</summary>
    internal static string ServiceStateToken(ServiceQuery q) => q.Probe == LabelProbe.Unknown
        ? "unknown"
        : q.State switch {
            ServiceState.NotInstalled when q.UnitPresent => "installed",
            ServiceState.NotInstalled => "not_installed",
            ServiceState.Installed    => "installed",
            ServiceState.Running      => "running",
            _                         => "unknown",
        };

    /// <summary>Emit one ensure result: JSON on stdout when <c>--json</c>, a human line otherwise.
    /// The exit code is decided by the caller, never derived from the outcome string.
    ///
    /// <para>The two stderr lines are derived from the result rather than written where the row was
    /// decided, so the ladder itself reports nothing: <c>recovery_surface=</c> wherever a surface was
    /// mapped, and the install hint for the one refusal a user can act on directly.</para></summary>
    async Task<int> Report(ServiceEnsureJson result, int exit, bool json) {
        if (result.Reason == "daemon_not_found")
            await Console.Error.WriteLineAsync(DaemonCommands.DaemonNotFoundMessage());

        if (result.Recovery is { } surface)
            await Console.Error.WriteLineAsync($"recovery_surface={surface}");

        if (json) {
            await Console.Out.WriteLineAsync(ServiceEnsureRender.RenderJson(result));
            return exit;
        }

        var line = result.Outcome switch {
            "already_enabled" => $"Service '{id}' is already enabled.",
            "attention"       => $"Service '{id}': {result.Reason} — no changes made.",
            "installed"       => $"Service '{id}' installed ({(result.Verified ? "verified" : "plain")}).",
            "started"         => $"Service '{id}' started ({(result.Verified ? "verified" : "plain")}).",
            "refused"         => $"Service '{id}': {result.Reason}{(result.Recovery is { } r ? $" — {r} needed" : "")}.",
            _                 => $"Service '{id}': unexpected outcome '{result.Outcome}'.",
        };
        await Console.Out.WriteLineAsync(line);
        return exit;
    }

    /// <summary>Install arm of <see cref="Ensure"/>: force-bake the born-<c>prompt</c> directive (and the
    /// expected-server pin the identity half of the start gate re-reads), then run the verified
    /// transaction on launchd or a plain install elsewhere.</summary>
    async Task<(ServiceEnsureJson, int)> EnsureInstall(string? profileName, string state) {
        var serverUrl = await ResolveServerUrlAsync(profileName);
        if (serverUrl is null)
            return (new ServiceEnsureJson(id, state, "install", "refused", null, "no_server_configured"), 1);

        var daemonPath = UnitIdentity.ResolveDaemonBinary();
        if (daemonPath is null)
            return (new ServiceEnsureJson(id, state, "install", "refused", null, "daemon_not_found"), 1);

        // The app's MutationEnv overlay, in-process: seed born-prompt + the expected server the gate
        // and the daemon's own boot both re-read. Everything else comes from the ambient capture.
        var env = EnsureUnitEnv(profileName, serverUrl, ServiceEnvironment.Capture(profileName, root));
        env["KCAP_DAEMON_SUPERVISED"] = id;
        var logPath = root.Path($"daemon-{id}.log");
        var spec    = new ServiceSpec(id, daemonPath, logPath, env, []);

        StartGateReason? gateReason = null;
        string? viabilityReason = null;
        string? bootRefusalToken = null;
        int exit;
        if (manager is LaunchdServiceManager) {
            var profileUrlValid = await ServiceInstallViability.PinnedProfileServerUrlValidAsync(env, root);
            var engine = new ServiceVerify(store, root, (LaunchdServiceManager)manager,
                n => DaemonPidProbe.ValidatedPid(store, n), (n, t) => HelloProbe.RunAsync(store, n, t),
                TimeProvider.System, profileViable: () => profileUrlValid, gateEnv: EnsureGateEnv(profileName, serverUrl));
            exit = await engine.InstallVerifiedAsync(spec, replace: false, CapacitorVersion.Current());
            // InstallVerifiedAsync never returns StartGate — its gated refusals are viability/drift —
            // so gateReason stays null here and StartGate recovery never fires from the install arm.
            gateReason       = engine.LastGateReason;
            viabilityReason  = engine.LastViabilityReason;
            bootRefusalToken = engine.LastBootRefusalToken;
        } else {
            exit = await InstallPlain(spec, startNow: true);
        }

        if (exit != 0) return EnsureFailure(exit, gateReason, state, "install", viabilityReason, bootRefusalToken);
        return (new ServiceEnsureJson(id, state, "install", "installed", Verified: manager is LaunchdServiceManager), 0);
    }

    /// <summary>Pure: whether a launchd ensure run must refuse for want of a profile name. The start
    /// gate's identity half demands a non-empty invoking <c>KCAP_PROFILE</c>, so a unit baked without
    /// one can never be gated-started — installing it would bake a dead end. Only launchd gates at
    /// all, so URL-only installs stay coherent off-macOS (no gate); the predicate takes
    /// <c>isLaunchd</c> rather than the manager so it is testable without a real launchd manager.</summary>
    internal static bool LaunchdProfileRefusal(bool isLaunchd, string? profileName, EnsureAction action) =>
        isLaunchd && string.IsNullOrEmpty(profileName) && action is EnsureAction.Install or EnsureAction.Start;

    /// <summary>Pure: the unit env for an ensure install — the ambient capture, overlaid with the
    /// born-<c>prompt</c> directive and the expected-server pin, so both are deliberate unit content
    /// regardless of what the installing shell exported. <c>prompt</c> WINS over an ambient value
    /// (a refusal exported as <c>deny</c>/<c>allow</c> must not survive into the unit): the flow's
    /// contract is "an app-installed daemon is born prompt", and the gate's identity half re-reads
    /// the expect pin, so a unit without it can never pass a later gated start.
    /// <para>When a profile IS pinned, the ambient <c>KCAP_URL</c> is stripped: URL resolution
    /// outranks profile resolution (ProfileResolver), so a unit that bakes both would resolve the
    /// ambient URL and refuse on the expectation mismatch — the pin must be the sole URL authority.</para></summary>
    internal static Dictionary<string, string> EnsureUnitEnv(
            string? profileName, string serverUrl, IReadOnlyDictionary<string, string> ambient) {
        var env = new Dictionary<string, string>(ambient) {
            ["KCAP_CONSENT_SEED_DEFAULT"] = "prompt",
            ["KCAP_EXPECT_SERVER_URL"]    = serverUrl,
        };
        // Same "explicit pin wins" rule as ServiceEnvironment.Build — only a real profile is pinned.
        if (!string.IsNullOrEmpty(profileName)) {
            env["KCAP_PROFILE"] = profileName;
            env.Remove("KCAP_URL");
        }
        return env;
    }

    /// <summary>Start arm of <see cref="Ensure"/>: the gated, app-managed start on launchd (the gate
    /// fires because <see cref="EnsureGateEnv"/> carries the directive), plain elsewhere.</summary>
    async Task<(ServiceEnsureJson, int)> EnsureStart(string? profileName, string state) {
        var serverUrl = await ResolveServerUrlAsync(profileName);
        if (serverUrl is null)
            return (new ServiceEnsureJson(id, state, "start", "refused", null, "no_server_configured"), 1);

        StartGateReason? gateReason = null;
        string? bootRefusalToken = null;
        int exit;
        if (manager is LaunchdServiceManager) {
            var engine = new ServiceVerify(store, root, (LaunchdServiceManager)manager,
                n => DaemonPidProbe.ValidatedPid(store, n), (n, t) => HelloProbe.RunAsync(store, n, t),
                TimeProvider.System, gateEnv: EnsureGateEnv(profileName, serverUrl));
            exit = await engine.StartVerifiedAsync(id);
            gateReason       = engine.LastGateReason;
            bootRefusalToken = engine.LastBootRefusalToken;
        } else {
            exit = await StartPlain();
        }

        if (exit != 0) return EnsureFailure(exit, gateReason, state, "start", bootRefusalToken: bootRefusalToken);
        return (new ServiceEnsureJson(id, state, "start", "started", Verified: manager is LaunchdServiceManager), 0);
    }

    /// <summary>Gate env for ensure's in-process engine: the directive the app's MutationEnv would
    /// overlay on a child, plus the profile/expect the identity half re-reads. Falls through to the
    /// process env for anything else, so an operator's own KCAP_* exports still apply.</summary>
    static Func<string, string?> EnsureGateEnv(string? profileName, string? serverUrl) => k => k switch {
        "KCAP_CONSENT_SEED_DEFAULT" => "prompt",
        "KCAP_PROFILE"              => profileName,
        "KCAP_EXPECT_SERVER_URL"    => serverUrl,
        _                           => Environment.GetEnvironmentVariable(k),
    };

    /// <summary>Shared non-success tail: the verify transaction already emitted its coded token and
    /// <c>start_gate_reason=</c> line; the pure <see cref="EnsureFailureMap"/> derives the JSON's
    /// recovery/reason fields — including the engine's structured viability/boot-refusal reasons —
    /// and the <c>recovery_surface=</c> line is re-emitted when a surface is mapped. The verified
    /// flag matches this run's transaction (true on the launchd arms), and the exit code always
    /// passes through unchanged.</summary>
    (ServiceEnsureJson, int) EnsureFailure(int exit, StartGateReason? gateReason, string state, string action,
            string? viabilityReason = null, string? bootRefusalToken = null) {
        var verified = manager is LaunchdServiceManager;
        var (recovery, reason) = EnsureFailureMap.Map(exit, gateReason, verified, viabilityReason, bootRefusalToken);
        return (new ServiceEnsureJson(id, state, action, "refused", recovery, reason, Verified: verified), exit);
    }

    /// <summary>
    /// UX-evidence-only re-read of the installed unit's baked environment (spec §3): which profile,
    /// server URL, expectation and consent-seed default it was installed with. Sourced by re-reading the
    /// plist rather than threading it through <see cref="ServiceQuery"/> so the query type stays a pure
    /// lifecycle probe. All four are null when no unit is present or the re-read/parse fails for any
    /// reason (moved/corrupt plist, permissions) — this is evidence for an operator, never load-bearing.
    /// </summary>
    (string? Profile, string? ServerUrl, string? ExpectedServer, string? ConsentSeed)
            UnitEnvEvidence(bool unitPresent) {
        if (!unitPresent) return (null, null, null, null);

        try {
            var env = LaunchdUnit.EnvFromPlist(File.ReadAllText(LaunchdUnit.PlistPath(home, id)));
            env.TryGetValue("KCAP_PROFILE", out var profile);
            env.TryGetValue("KCAP_EXPECT_SERVER_URL", out var expectedServer);
            env.TryGetValue("KCAP_CONSENT_SEED_DEFAULT", out var consentSeed);

            var serverUrl = env.TryGetValue("KCAP_URL", out var bakedUrl)
                ? bakedUrl
                : BakedProfileServerUrl(env, profile, root);

            return (profile, serverUrl, expectedServer, consentSeed);
        } catch {
            return (null, null, null, null);
        }
    }

    /// <summary>The <c>KCAP_URL</c>-absent fallback for <see cref="UnitEnvEvidence"/>: the baked
    /// profile's <c>server_url</c>, read from the baked <c>KCAP_CONFIG_DIR</c> (or the default config
    /// root when none was baked) — null on any ambiguity (no baked profile) or miss.</summary>
    static string? BakedProfileServerUrl(IReadOnlyDictionary<string, string> env, string? profile, ConfigRoot root) {
        if (string.IsNullOrEmpty(profile)) return null;
        try {
            var config = ConfigMutator.LoadPure(UnitIdentity.ConfigPathFromUnitEnv(env, root));
            return config.Profiles.TryGetValue(profile, out var p) ? p.ServerUrl : null;
        } catch {
            return null;
        }
    }

    static int Usage() {
        Console.Error.WriteLine("Usage: kcap daemon service <install|uninstall|start|stop|ensure|status> [--name N]");
        Console.Error.WriteLine();
        Console.Error.WriteLine("  install [--name N] [--profile P] [--max-agents N] [--no-start] [--replace] [--verify]");
        Console.Error.WriteLine("                          --verify (macOS/launchd only) polls readiness/version/ownership and rolls back on failure");
        Console.Error.WriteLine("                          --replace (requires --verify) takes over an existing label/unit/live owner");
        Console.Error.WriteLine("                          --no-start is incompatible with --verify");
        Console.Error.WriteLine("  uninstall [--name N]   Stop and remove the service unit");
        Console.Error.WriteLine("  start [--name N] [--verify]   Start the installed service now");
        Console.Error.WriteLine("                          --verify (macOS/launchd only) polls readiness/ownership and rolls back on failure");
        Console.Error.WriteLine("  stop [--name N]        Stop the running service (stays installed)");
        Console.Error.WriteLine("  ensure [--name N] [--profile P] [--json]   Install-or-start from a fresh status read");
        Console.Error.WriteLine("                          (bakes the born-prompt consent seed; gate refusals emit recovery_surface=)");
        Console.Error.WriteLine("  status [--name N] [--json]   Show installed/running state (--json for machine-readable output)");
        return 1;
    }
}
