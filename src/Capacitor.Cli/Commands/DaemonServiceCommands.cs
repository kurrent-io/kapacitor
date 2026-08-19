using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Commands;

/// <summary>
/// The <c>kcap daemon service</c> verbs. The OS service manager and the service id are fixed for a
/// whole invocation — <see cref="DispatchAsync"/> resolves both once — so they are state here rather
/// than two arguments threaded through every verb.
/// </summary>
sealed class DaemonServiceCommands(DaemonStore store, IServiceManager manager, string id) {
    /// <summary>Resolves the OS service manager and the service id once, then runs one verb.</summary>
    public static async Task<int> DispatchAsync(DaemonStore store, string[] args) {
        if (args.Length == 0) return Usage();

        var action  = args[0];
        var rest    = args[1..];
        var noStart = rest.Contains("--no-start");

        IServiceManager manager;
        try {
            manager = ServiceManagerFactory.ForCurrentOs();
        } catch (PlatformNotSupportedException ex) {
            await Console.Error.WriteLineAsync(ex.Message);
            return 1;
        }

        var verbs = new DaemonServiceCommands(store, manager, DaemonStore.Sanitize(DaemonCommands.ResolveName(rest)));

        return action switch {
            "install"   => await verbs.Install(rest, startNow: !noStart),
            "uninstall" => await verbs.Uninstall(),
            "start"     => await verbs.Start(rest),
            "stop"      => await verbs.Stop(),
            "status"    => rest.Contains("--json") ? await verbs.StatusJson() : await verbs.Status(),
            _           => Usage(),
        };
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

        var profileName = DaemonCommands.ExtractFlagValue(args, "--profile") ?? AppConfig.ResolvedProfile?.ProfileName;
        var env = new Dictionary<string, string>(ServiceEnvironment.Capture(profileName)) {
            ["KCAP_DAEMON_SUPERVISED"] = id,   // name-specific; daemon honors it only when == its sanitized --name
        };

        List<string> extra;
        try {
            extra = ExtraArgs(DaemonCommands.ExtractFlagValue(args, "--max-agents"));
        } catch (ArgumentException ex) {
            await Console.Error.WriteLineAsync(ex.Message);

            return 1;
        }

        var logPath = PathHelpers.ConfigPath($"daemon-{id}.log");
        var spec    = new ServiceSpec(id, daemonPath, logPath, env, extra);

        if (verify) {
            // The CLI is the safety boundary for §4.1's precondition: prove the pinned profile resolves
            // to a valid server URL here too, so the transaction never destroys a working unit only to
            // install one whose daemon would exit config-invalid and never satisfy readiness.
            var profileUrlValid = await ServiceInstallViability.PinnedProfileServerUrlValidAsync(env);
            var engine = new ServiceVerify(store, (LaunchdServiceManager)manager,
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
        var engine = new ServiceVerify(store, (LaunchdServiceManager)manager,
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
            var env = LaunchdUnit.EnvFromPlist(File.ReadAllText(LaunchdUnit.PlistPath(id)));
            env.TryGetValue("KCAP_PROFILE", out var profile);
            env.TryGetValue("KCAP_EXPECT_SERVER_URL", out var expectedServer);
            env.TryGetValue("KCAP_CONSENT_SEED_DEFAULT", out var consentSeed);

            var serverUrl = env.TryGetValue("KCAP_URL", out var bakedUrl)
                ? bakedUrl
                : BakedProfileServerUrl(env, profile);

            return (profile, serverUrl, expectedServer, consentSeed);
        } catch {
            return (null, null, null, null);
        }
    }

    /// <summary>The <c>KCAP_URL</c>-absent fallback for <see cref="UnitEnvEvidence"/>: the baked
    /// profile's <c>server_url</c>, read from the baked <c>KCAP_CONFIG_DIR</c> (or the default config
    /// root when none was baked) — null on any ambiguity (no baked profile) or miss.</summary>
    static string? BakedProfileServerUrl(IReadOnlyDictionary<string, string> env, string? profile) {
        if (string.IsNullOrEmpty(profile)) return null;
        try {
            var config = ConfigMutator.LoadPure(UnitIdentity.ConfigPathFromUnitEnv(env));
            return config.Profiles.TryGetValue(profile, out var p) ? p.ServerUrl : null;
        } catch {
            return null;
        }
    }

    static int Usage() {
        Console.Error.WriteLine("Usage: kcap daemon service <install|uninstall|start|stop|status> [--name N]");
        Console.Error.WriteLine();
        Console.Error.WriteLine("  install [--name N] [--profile P] [--max-agents N] [--no-start] [--replace] [--verify]");
        Console.Error.WriteLine("                          --verify (macOS/launchd only) polls readiness/version/ownership and rolls back on failure");
        Console.Error.WriteLine("                          --replace (requires --verify) takes over an existing label/unit/live owner");
        Console.Error.WriteLine("                          --no-start is incompatible with --verify");
        Console.Error.WriteLine("  uninstall [--name N]   Stop and remove the service unit");
        Console.Error.WriteLine("  start [--name N] [--verify]   Start the installed service now");
        Console.Error.WriteLine("                          --verify (macOS/launchd only) polls readiness/ownership and rolls back on failure");
        Console.Error.WriteLine("  stop [--name N]        Stop the running service (stays installed)");
        Console.Error.WriteLine("  status [--name N] [--json]   Show installed/running state (--json for machine-readable output)");
        return 1;
    }
}
