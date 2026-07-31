using System.Reflection;
using System.Runtime.InteropServices;
using System.Diagnostics;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Pty;
using Capacitor.Cli.Daemon.Pty.Unix;
using Capacitor.Cli.Daemon.Pty.Windows;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon;

public static partial class DaemonRunner {
    /// <summary>
    /// Daemon binary version from <c>[AssemblyInformationalVersion]</c>,
    /// baked at build time by MSBuild's git-info integration. Surfaces on
    /// <c>DaemonConnect</c> so the server's <c>Daemon connected:</c> log
    /// line and <c>DaemonInfo</c> can show "v0.4.11+sha.abc1234".
    /// </summary>
    public static string ResolveDaemonVersion() =>
        typeof(DaemonRunner).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown";

    public static async Task<int> RunAsync(string[] args) {
        string?    logFile     = null;
        string?    stderrFile  = null;
        LogLevel?  logLevelArg = null;
        var        config      = new DaemonConfig();

        // Captured for self-respawn (detached restart-after-update) and to detect
        // the successor's --await-lock handoff flag.
        config.OriginalArgs = args;
        var awaitLock = args.Contains("--await-lock");

        // Resolve server URL + active profile. The CLI does this in its own
        // Program.cs, but the daemon is a separate process so its statics start
        // empty. Skips repo discovery (the daemon isn't bound to a working dir);
        // honors --server-url, KCAP_URL, KCAP_PROFILE.
        await AppConfig.ResolveActiveProfile(args);
        config.ServerUrl = AppConfig.ResolvedServerUrl ?? "";

        // CLI arg overrides for daemon-specific settings — parse before host builder.
        // --name is consumed below by DaemonNameResolver (shared with the CLI
        // supervisor), so we don't parse it here.
        for (var i = 0; i < args.Length - 1; i++) {
            switch (args[i]) {
                case "--log-file": logFile = args[++i]; break;
                case "--stderr-file": stderrFile = args[++i]; break;
                case "--log-level": logLevelArg = ParseLogLevel(args[++i]); break;
                case "--max-agents" when int.TryParse(args[i + 1], out var n) && n >= 1:
                    config.MaxConcurrentAgents = n;
                    i++;

                    break;
                case "--max-agents":
                    await Console.Error.WriteLineAsync($"Invalid --max-agents value: {args[i + 1]} (must be a positive integer)");

                    return 1;
            }
        }

        // Phase B (D3): reviewer lifetime/idle backstop overrides from env (seconds; 0 disables).
        config.ReviewerMaxLifetime = ParseSecondsEnv("KCAP_REVIEWER_MAX_LIFETIME", config.ReviewerMaxLifetime);
        config.ReviewerIdleTimeout = ParseSecondsEnv("KCAP_REVIEWER_IDLE_TIMEOUT", config.ReviewerIdleTimeout);

        // reopen fds 1/2 onto the capture file BEFORE building the host,
        // so even a crash during construction lands somewhere. On the detached
        // launch path the CLI closed our std pipes; without this a runtime/native
        // fatal message would go to a broken pipe and vanish. No-op under launchd
        // (StandardErrorPath) and foreground (no --stderr-file passed).
        if (StdErrCapture.ResolveTarget(stderrFile) is { } capturePath) {
            StdErrCapture.Apply(capturePath);
        }

        // Strip our custom args before passing to host builder
        var hostArgs = Array.Empty<string>();
        var builder  = Host.CreateApplicationBuilder(hostArgs);

        // Configure logging: file when detached, console when foreground.
        // Minimum level defaults to Information; raise verbosity for transport
        // diagnostics (e.g. per-tick DaemonPing RTT, which logs at Debug) via
        // --log-level or KCAP_DAEMON_LOG_LEVEL=debug. The --log-level arg wins
        // over the env var when both are set.
        var minLevel = logLevelArg
                    ?? ParseLogLevel(Environment.GetEnvironmentVariable("KCAP_DAEMON_LOG_LEVEL"))
                    ?? LogLevel.Information;

        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(minLevel);

        if (logFile is not null) {
            builder.Logging.AddProvider(new RollingFileLoggerProvider(logFile, minLevel: minLevel));
        } else {
            builder.Logging.AddSimpleConsole(opts => {
                    opts.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff ";
                    opts.UseUtcTimestamp = false;
                }
            );
        }

        // Daemon settings from the active profile, with env overrides
        var profileDaemon = AppConfig.ResolvedProfile?.Profile?.Daemon;

        if (config.MaxConcurrentAgents == 5 && profileDaemon is { MaxAgents: var mx and not 5 })
            config.MaxConcurrentAgents = mx;

        if (!string.IsNullOrEmpty(profileDaemon?.ClaudePath))
            config.ClaudePath = profileDaemon.ClaudePath;

        if (!string.IsNullOrEmpty(profileDaemon?.CodexPath))
            config.CodexPath = profileDaemon.CodexPath;

        if (Environment.GetEnvironmentVariable("KCAP_MAX_AGENTS") is { } maxAgents) {
            if (int.TryParse(maxAgents, out var n) && n >= 1)
                config.MaxConcurrentAgents = n;
            else
                await Console.Error.WriteLineAsync($"Warning: ignoring invalid KCAP_MAX_AGENTS={maxAgents}");
        }

        if (Environment.GetEnvironmentVariable("KCAP_CLAUDE_PATH") is { Length: > 0 } envClaudePath)
            config.ClaudePath = envClaudePath;

        if (Environment.GetEnvironmentVariable("KCAP_CODEX_PATH") is { Length: > 0 } envCodexPath)
            config.CodexPath = envCodexPath;

        if (Environment.GetEnvironmentVariable("KCAP_CURSOR_PATH") is { Length: > 0 } envCursorPath)
            config.CursorPath = envCursorPath;

        if (Environment.GetEnvironmentVariable("KCAP_CURSOR_MODEL") is { Length: > 0 } envCursorModel)
            config.CursorModel = envCursorModel;

        if (Environment.GetEnvironmentVariable("KCAP_COPILOT_PATH") is { Length: > 0 } envCopilotPath)
            config.CopilotPath = envCopilotPath;

        if (Environment.GetEnvironmentVariable("KCAP_KIRO_PATH") is { Length: > 0 } envKiroPath)
            config.KiroPath = envKiroPath;

        if (Environment.GetEnvironmentVariable("KCAP_OPENCODE_PATH") is { Length: > 0 } envOpenCodePath)
            config.OpenCodePath = envOpenCodePath;

        if (Environment.GetEnvironmentVariable("KCAP_GEMINI_PATH") is { Length: > 0 } envGeminiPath)
            config.GeminiPath = envGeminiPath;

        config.DebugFrames = ParseDebugFramesFlag(Environment.GetEnvironmentVariable("KCAP_ACP_DEBUG_FRAMES"));

        // Shared name resolution with the CLI supervisor — the CLI's
        // DaemonCommands and the daemon binary must agree on the name so
        // the per-name PID file the CLI inspects is the one the daemon
        // writes via DaemonLock. Resolve throws on `--name <missing value>`
        // / `--name <next-is-flag>`; refuse to start in that case rather
        // than silently defaulting to the OS username.
        try {
            config.Name = DaemonNameResolver.Resolve(args, profileDaemon?.Name);
        } catch (ArgumentException ex) {
            await Console.Error.WriteLineAsync(ex.Message);
            return 1;
        }

        var errors = config.Validate();

        if (errors.Count > 0) {
            await Console.Error.WriteLineAsync("Configuration errors:");

            foreach (var e in errors) {
                await Console.Error.WriteLineAsync($"  - {e}");
            }

            return 1;
        }

        // Resolve our version before acquiring the lock so DaemonLock can stamp
        // it into the freely-readable <name>.version marker that `kcap daemon
        // status` reads to report the running daemon's version.
        config.Version = ResolveDaemonVersion();

        // Acquire the per-name flock that prevents another daemon from
        // running under the same name on this machine. The lock content is
        // a fresh instance id that we'll also send over DaemonConnect so
        // the server can refuse a second daemon claiming the same
        // (owner, name) slot.
        var daemonLock = awaitLock
            ? DaemonLock.TryAcquire(config.Name, TimeSpan.FromSeconds(5), config.Version)
            : DaemonLock.TryAcquire(config.Name, config.Version);

        if (daemonLock is null) {
            await Console.Error.WriteLineAsync(
                $"Another kcap-daemon is already running under the name '{config.Name}' on this machine. "
                + $"Either stop it (`kcap daemon stop --name {config.Name}`) or start this one with a different `--name`."
            );

            return 2;
        }

        config.InstanceId = daemonLock.InstanceId;

        // Phase B2-b (sequenced-settlement design): pin the per-boot epoch here, before any service is
        // built, so the epoch advertised on DaemonConnect and the orchestrator's own _daemonEpoch (which
        // falls back to config.DaemonEpoch) are provably the same value.
        config.DaemonEpoch ??= Guid.NewGuid().ToString("N");

        // Phase B2-b (sequenced-settlement design §4.2.3): fold the durable coverage boot-chain BEFORE any
        // Connect/spawn. this_epoch_contained is true only where OS containment leaves NO recordless
        // survivor class (the Windows Job Object). Fail-closed inside RecordBoot. NullLogger is acceptable
        // this early — the host's logging pipeline isn't built yet.
        var coverageStateDir = Path.Combine(
            config.StateDir ?? DaemonLockPaths.Directory, DaemonLockPaths.Sanitize(config.Name));
        config.RecordlessSurvivorsImpossible = new CoverageJournal(coverageStateDir, NullLogger.Instance)
            .RecordBoot(daemonLock.InstanceId, daemonLock.PriorInstanceId,
                priorLockReadFailed: daemonLock.PriorLockIndeterminate, thisEpochContained: OperatingSystem.IsWindows());

        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton(daemonLock);
        builder.Services.AddSingleton<ServerConnection>();

        // AI-1623: the owner consent gate — policy store + append-only decision log share the
        // per-daemon state root with the coverage journal above; the prompter is null until
        // Task 6 registers the broker (a Prompt-default policy then denies with "prompt_no_ui").
        builder.Services.AddSingleton(sp => new LaunchConsentStore(
            coverageStateDir, sp.GetRequiredService<ILogger<LaunchConsentStore>>()));
        builder.Services.AddSingleton(sp => new LaunchConsentDecisionLog(
            coverageStateDir, sp.GetRequiredService<ILogger<LaunchConsentDecisionLog>>()));
        builder.Services.AddSingleton(sp => new LaunchConsentGate(
            sp.GetRequiredService<LaunchConsentStore>(),
            sp.GetRequiredService<LaunchConsentDecisionLog>(),
            sp.GetService<ILaunchConsentPrompter>(),   // null until Task 6 registers the broker
            sp.GetRequiredService<ILogger<LaunchConsentGate>>()));

        // Local HTTP bridge that fronts the server's permission flow. Registered as a
        // singleton so AgentOrchestrator can read its bound URL at agent-spawn time, AND
        // as a hosted service so its IHostedService lifecycle starts the listener before
        // any agent is spawned.
        builder.Services.AddSingleton<LocalPermissionBridge>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<LocalPermissionBridge>());

        if (OperatingSystem.IsWindows()) {
            builder.Services.AddSingleton<IPtyProcessFactory, WinPtyProcessFactory>();
        } else {
            // L1-managed(a)/(b): one dedicated, daemon-lifetime native thread runs EVERY Unix
            // pty_spawn call (never a thread-pool thread — see UnixSpawnerThread's own doc
            // comment for why). Registered with no factory delegate so DI resolves it via its
            // parameterless constructor, which already starts the thread. host.StopAsync() only
            // stops registered IHostedServices — it does NOT dispose the ServiceProvider, so a
            // plain AddSingleton<T>() IDisposable like this one is NOT disposed by StopAsync
            // alone. Disposal (and therefore the thread's retirement, via UnixSpawnerThread.Dispose
            // completing its queue) happens when disposing the host disposes the ServiceProvider —
            // which is why the shutdown sequence below awaits host.StopAsync() and THEN
            // DisposeHostAsync(host) on every exit path, after every hosted agent is already
            // stopped, so normal shutdown retires the thread only once nothing needs it.
            builder.Services.AddSingleton<UnixSpawnerThread>();
            builder.Services.AddSingleton<IPtyProcessFactory, UnixPtyProcessFactory>();
        }

        builder.Services.AddSingleton<WorktreeManager>();
        builder.Services.AddSingleton<RepoMatcher>();

        builder.Services.AddHttpClient("Attachments", client => client.BaseAddress = new Uri(config.ServerUrl));

        builder.Services.AddSingleton<IHostedAgentLauncher, ClaudeLauncher>();
        builder.Services.AddSingleton<IHostedAgentLauncher, CodexLauncher>();

        builder.Services.AddSingleton<IReadOnlyDictionary<string, IHostedAgentLauncher>>(sp =>
            sp.GetServices<IHostedAgentLauncher>().ToDictionary(l => l.Vendor)
        );

        // Runtime-selection seam: one IHostedAgentRuntimeFactory per vendor.
        // AgentOrchestrator selects by vendor from the resulting dictionary instead of driving
        // Prepare/BuildArgs/Spawn inline. PtyHostedAgentRuntimeFactory wraps each registered PTY
        // launcher (Claude, Codex); AcpHostedAgentRuntimeFactory speaks ACP JSON-RPC over stdio for
        // Cursor (no IHostedAgentLauncher — Cursor never went through the PTY launcher contract).
        builder.Services.AddSingleton<IHostedAgentRuntimeFactory>(sp =>
            new PtyHostedAgentRuntimeFactory(
                sp.GetServices<IHostedAgentLauncher>().SingleOrDefault(l => l.Vendor == "claude")
                    ?? throw new InvalidOperationException("No IHostedAgentLauncher registered for vendor 'claude'"),
                sp.GetRequiredService<IPtyProcessFactory>(),
                sp.GetRequiredService<ILogger<PtyHostedAgentRuntimeFactory>>()
            )
        );
        builder.Services.AddSingleton<IHostedAgentRuntimeFactory>(sp =>
            new PtyHostedAgentRuntimeFactory(
                sp.GetServices<IHostedAgentLauncher>().SingleOrDefault(l => l.Vendor == "codex")
                    ?? throw new InvalidOperationException("No IHostedAgentLauncher registered for vendor 'codex'"),
                sp.GetRequiredService<IPtyProcessFactory>(),
                sp.GetRequiredService<ILogger<PtyHostedAgentRuntimeFactory>>()
            )
        );
        builder.Services.AddSingleton<IHostedAgentRuntimeFactory>(sp =>
            new AcpHostedAgentRuntimeFactory(
                AcpVendorDescriptors.Cursor,
                sp.GetRequiredService<DaemonConfig>(),
                sp.GetRequiredService<ILoggerFactory>(),
                sp.GetRequiredService<ServerConnection>() // spec-review Finding 4 — real production wiring
            )
        );
        builder.Services.AddSingleton<IHostedAgentRuntimeFactory>(sp =>
            new AcpHostedAgentRuntimeFactory(
                AcpVendorDescriptors.Copilot,
                sp.GetRequiredService<DaemonConfig>(),
                sp.GetRequiredService<ILoggerFactory>(),
                sp.GetRequiredService<ServerConnection>()
            )
        );
        builder.Services.AddSingleton<IHostedAgentRuntimeFactory>(sp =>
            new AcpHostedAgentRuntimeFactory(
                AcpVendorDescriptors.Kiro,
                sp.GetRequiredService<DaemonConfig>(),
                sp.GetRequiredService<ILoggerFactory>(),
                sp.GetRequiredService<ServerConnection>()
            )
        );

        builder.Services.AddSingleton<IReadOnlyDictionary<string, IHostedAgentRuntimeFactory>>(sp =>
            sp.GetServices<IHostedAgentRuntimeFactory>().ToDictionary(f => f.Vendor)
        );

        builder.Services.AddSingleton<AgentOrchestrator>();
        builder.Services.AddSingleton<EvalContextCache>();
        builder.Services.AddSingleton<EvalRunner>();

        // Restart-after-update: a coordinator polls the on-disk binary and, when idle,
        // applies a queued restart via the strategy chosen by supervision detection.
        // Strategies are concrete singletons (AOT-safe; same pattern as the services
        // above) and only the selected one is ever constructed.
        builder.Services.AddSingleton<RestartState>();
        builder.Services.AddSingleton<SupervisedExitStrategy>();
        builder.Services.AddSingleton<DetachedRespawnStrategy>();
        builder.Services.AddSingleton<ForegroundNoopStrategy>();
        builder.Services.AddSingleton<IRestartStrategy>(sp => {
            var cfg        = sp.GetRequiredService<DaemonConfig>();
            var hasLogFile = cfg.OriginalArgs.Contains("--log-file");
            var mode       = SupervisionDetector.DetectCurrent(DaemonLockPaths.Sanitize(cfg.Name), hasLogFile);

            return mode switch {
                SupervisionMode.Supervised => sp.GetRequiredService<SupervisedExitStrategy>(),
                SupervisionMode.Detached   => sp.GetRequiredService<DetachedRespawnStrategy>(),
                _                          => sp.GetRequiredService<ForegroundNoopStrategy>(),
            };
        });
        builder.Services.AddSingleton<RestartCoordinator>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<RestartCoordinator>());

        // Local control socket: lets `kcap agent start`/`attach`/`ls`/`stop` drive daemon-hosted
        // agents from the user's own terminal (AI local-attach Phase 1).
        builder.Services.AddSingleton<LocalControlServer>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<LocalControlServer>());

        var host   = builder.Build();
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("kcap.Daemon");

        // Set by the supervised restart strategy so we exit non-zero for a supervisor relaunch.
        var restartState = host.Services.GetRequiredService<RestartState>();

        // probe each registered runtime factory's CLI binary
        // so the DaemonConnect payload only advertises vendors this daemon can actually spawn —
        // now via IHostedAgentRuntimeFactory.IsAvailable() rather than IHostedAgentLauncher, so
        // Cursor (which has no IHostedAgentLauncher) is advertised once cursor-agent is installed.
        // The launch dialog filters its vendor selector by this list. Ordered alphabetically so the
        // wire format is stable across restarts.
        var runtimeFactories = host.Services.GetServices<IHostedAgentRuntimeFactory>().ToArray();

        config.SupportedVendors = runtimeFactories
            .Where(f => f.IsAvailable())
            .Select(f => f.Vendor)
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToArray();

        // Reviewer vendor override support: a strict subset of SupportedVendors — only vendors that
        // can also run fully unattended without routing an interaction to a human. The server gates
        // a review-flow vendor override on this list rather than SupportedVendors alone, so a vendor
        // that's merely installed but has no unattended launcher is never offered as an override
        // target.
        config.UnattendedVendors = ComputeUnattendedVendors(runtimeFactories);
        config.UnattendedVendorCapabilities = ComputeUnattendedVendorCapabilities(runtimeFactories, config);

        // Which build of each unattended vendor was installed when this daemon started. Recorded at
        // startup (like the Cursor-unavailable warning below) rather than per launch, and reported
        // without any comparison against a validated-build record.
        LogUnattendedVendorIdentities(logger, config.UnattendedVendorCapabilities);

        // IsAvailable()==false silently omits cursor from SupportedVendors above — correct
        // behavior (the launch dialog just won't offer Cursor), but gave operators no clue WHY. One
        // Warning at startup (not per-launch) so a missing/misconfigured cursor-agent install is
        // visible in the daemon's own logs instead of only showing up as an absent vendor downstream.
        if (ShouldWarnCursorUnavailable(runtimeFactories))
            LogCursorUnavailable(logger, config.CursorPath);

        // KCAP_ACP_DEBUG_FRAMES is a static, daemon-wide setting read once above — warn once here,
        // at the point it takes effect, rather than lazily the first time some ACP call site actually
        // logs full content (which could fire dozens of times across one busy session).
        if (config.DebugFrames)
            LogAcpDebugFramesEnabled(logger);

        LogDaemonStarting(logger, config.Name, config.ServerUrl);

        // if the previous daemon under this name vanished without
        // releasing its lock, it was SIGKILLed (macOS jetsam/OOM, `kill -9`),
        // lost power, or crashed hard — none of which the dying process can
        // log. Emit a breadcrumb now so the otherwise-silent death is on the
        // record. This is safe even for a `--await-lock` restart-after-update
        // handoff: DaemonLock.Dispose now deletes the outgoing daemon's PID
        // file *before* releasing the flock, so a clean handoff leaves nothing
        // for us to find — a leftover PID file here is a real hard death (e.g.
        // the outgoing daemon was OOM-killed mid-handoff) and worth recording.
        if (daemonLock.PriorExitWasUnclean) {
            LogPriorUncleanExit(logger, config.Name, daemonLock.PriorHolderPid?.ToString() ?? "unknown");
        }

        var lifetime   = host.Services.GetRequiredService<IHostApplicationLifetime>();
        var connection = host.Services.GetRequiredService<ServerConnection>();

        // Death-rattle instrumentation: without these, a daemon dying mid-run
        // (signal, OOM, unobserved-task FailFast, native crash) leaves no trace
        // in the log and we can't tell a clean shutdown from a kill. Each hook
        // best-effort logs *why* the process is going away before the runtime
        // tears down the logging pipeline. Lifetime is captured so SIGHUP
        // (terminal closed) can be turned into a cooperative StopApplication
        // — without that, the host's finally-block cleanup never runs.
        RegisterDeathRattle(logger, lifetime);

        // Lifetime-driven log lines — pair with the AppDomain/signal hooks so
        // we can distinguish a cooperative StopApplication (e.g. NameInUse,
        // Ctrl+C consumed by ConsoleLifetime) from an outside-the-runtime kill.
        lifetime.ApplicationStopping.Register(() => {
            DeathRattle("Lifetime: ApplicationStopping fired");
            LogLifetimeStopping(logger);
        });
        lifetime.ApplicationStopped.Register(() => {
            DeathRattle("Lifetime: ApplicationStopped fired");
            LogLifetimeStopped(logger);
        });

        // if the server rejects our DaemonConnect because another
        // live daemon owns the (owner, name) slot, signal host shutdown
        // and remember to return exit code 3 instead of 0. Subscribe
        // before ConnectAsync so the initial-connect path is covered.
        var nameInUse = false;

        connection.OnNameInUse += _ => {
            nameInUse = true;
            lifetime.StopApplication();
        };

        // Start hosted services (LocalPermissionBridge in particular) BEFORE the SignalR
        // connection comes up. Otherwise an early LaunchAgent message can arrive while
        // BaseUrl is still null and the spawned Claude falls back to the HTTPS path —
        // exactly what this bridge is meant to avoid.
        await host.StartAsync(lifetime.ApplicationStopping);

        // Phase B (D4 §6.4(3)): resolve the orchestrator (which wires OnLaunchAgent +
        // GetLiveAgents in its ctor) and reap any hosted-agent children that outlived a PRIOR daemon run
        // — all BEFORE ConnectAsync advertises this daemon and the server can dispatch launches. Doing
        // it after connect would let new work be admitted while old capacity is still being reclaimed
        // (those survivors aren't yet in EffectiveCount), and would leave a window where a launch races
        // an unwired handler. Under the daemon lock; best-effort (swallows its own faults).
        var orchestrator = host.Services.GetRequiredService<AgentOrchestrator>();

        // Once the orchestrator is resolved, the Unix IPtyProcessFactory has pulled in
        // UnixSpawnerThread — whose foreground (non-background) OS thread is now running and
        // parks forever until the ServiceProvider is disposed (host.StopAsync() alone does NOT
        // retire it; see the block comment in the finally below). So EVERY exit path from here on
        // — success, the nameInUse early-return, OR any exception out of ReapOrphansOnceAsync,
        // ConnectAsync (non-nameInUse), CleanupOrphanedAsync, or EvalRunner resolution — MUST run
        // the unified finally so DisposeHostAsync(host) fires and the process can actually exit
        // instead of hanging. That is why everything below is wrapped in this single try/finally.
        // Structural fix; backed by DaemonHostDisposalTests, which proves StopAsync-then-dispose
        // is what retires the DI-owned UnixSpawnerThread.
        try {
            // Phase B (D4 §6.4(3)): reap any hosted-agent children that outlived a PRIOR daemon run
            // BEFORE ConnectAsync advertises this daemon (see the orchestrator-resolution comment
            // above). Under the daemon lock; best-effort (swallows its own faults).
            await orchestrator.ReapOrphansOnceAsync();

            try {
                await connection.ConnectAsync(lifetime.ApplicationStopping);
            } catch (Exception ex) when (nameInUse) {
                // ConnectAsync's initial-connect path threw because of NameInUse. OnNameInUse
                // already fired and set our flag; the host hasn't started its main loop yet, so
                // just exit with code 3 — the unified finally below disposes daemonLock,
                // orchestrator, connection, and the host (retiring the spawner thread).
                _ = ex;

                return 3;
            }

            var worktreeManager = host.Services.GetRequiredService<WorktreeManager>();
            await worktreeManager.CleanupOrphanedAsync();

            // Instantiate EvalRunner so it wires the per-phase eval handlers
            // (PrepareEval / RunQuestion / FinalizeEval / CancelEval) on the
            // ServerConnection. It's stateless beyond the handler assignment —
            // cached context lives in EvalContextCache — so no disposal dance.
            _ = host.Services.GetRequiredService<EvalRunner>();

            // Wait without passing the lifetime token: WaitForShutdownAsync(token) treats
            // token cancellation as a fault, so a normal Ctrl+C / lifetime.StopApplication()
            // would surface as OperationCanceledException. The no-arg overload listens
            // internally for ApplicationStopping and returns cleanly.
            await host.WaitForShutdownAsync();
            LogWaitForShutdownReturned(logger);
        } finally {
            LogEnteringCleanup(logger);
            daemonLock.Dispose();
            await orchestrator.DisposeAsync();
            await connection.DisposeAsync();
            await host.StopAsync();

            // host.StopAsync() only STOPS IHostedServices — it does NOT dispose the
            // ServiceProvider, so a plain AddSingleton<T>() IDisposable (e.g.
            // UnixSpawnerThread, whose foreground, non-background OS thread parks
            // forever on its queue until Dispose() completes it) is never released by
            // StopAsync alone. Disposing the host is what disposes the ServiceProvider —
            // and therefore every registered IDisposable singleton — so it must run
            // after StopAsync on every shutdown path or the spawner thread's foreground
            // thread keeps the process alive past WaitForShutdownAsync forever.
            await DisposeHostAsync(host);
            LogCleanupCompleted(logger);
        }

        // Restart-after-update (supervised): exit non-zero so the unit's
        // failure-restart policy relaunches the now-updated binary.
        if (restartState.SupervisedRestart) return ExitCodes.RestartRequested;

        // if the daemon was shut down because the server told us
        // our (owner, name) slot is contested mid-run (heartbeat-triggered
        // path), exit with code 3 so wrappers (systemd, npm, CI) can tell
        // this apart from a normal Ctrl+C exit.
        return nameInUse ? 3 : 0;
    }

    /// <summary>
    /// Disposes the host so its ServiceProvider — and therefore every registered
    /// <see cref="IDisposable"/> singleton (e.g. <see cref="UnixSpawnerThread"/>) — is released.
    /// <see cref="IHost"/> only surfaces <see cref="IDisposable"/>, but the concrete host built by
    /// <see cref="HostApplicationBuilder"/> also implements <see cref="IAsyncDisposable"/>; prefer
    /// that when present so any <c>IAsyncDisposable</c> singletons get their async path too, and
    /// fall back to the synchronous <see cref="IDisposable.Dispose"/> otherwise. Must be called
    /// AFTER <c>host.StopAsync()</c> on every shutdown path — <c>StopAsync()</c> only stops
    /// <see cref="IHostedService"/>s, it never disposes the ServiceProvider. <c>internal</c> (not
    /// <c>private</c>) so the regression test can drive the exact StopAsync-then-dispose sequence
    /// against a minimal host and prove the ordering is what actually retires a DI-owned
    /// <see cref="UnixSpawnerThread"/>.
    /// </summary>
    internal static async Task DisposeHostAsync(IHost host) {
        if (host is IAsyncDisposable asyncDisposableHost) {
            await asyncDisposableHost.DisposeAsync();
        } else {
            host.Dispose();
        }
    }

    /// <summary>
    /// Parses a daemon log-level string (case-insensitive, e.g. "debug",
    /// "trace", "warning") to a <see cref="LogLevel"/>. Returns null for a
    /// null/blank/unrecognised value so callers can fall through to the next
    /// source or the Information default rather than silently logging nothing.
    /// </summary>
    internal static LogLevel? ParseLogLevel(string? value) => value?.Trim().ToLowerInvariant() switch {
        "trace" or "trce"              => LogLevel.Trace,
        "debug" or "dbug"              => LogLevel.Debug,
        "information" or "info"        => LogLevel.Information,
        "warning" or "warn"            => LogLevel.Warning,
        "error" or "fail"              => LogLevel.Error,
        "critical" or "crit"           => LogLevel.Critical,
        "none"                         => LogLevel.None,
        _                              => null
    };

    /// <summary>Phase B (D3): parse a seconds-valued env var into a <see cref="TimeSpan"/>
    /// (<c>0</c> → <see cref="TimeSpan.Zero"/>, which disables the bound). Unset/blank/invalid/negative
    /// → the supplied <paramref name="fallback"/>.</summary>
    internal static TimeSpan ParseSecondsEnv(string name, TimeSpan fallback) {
        var raw = Environment.GetEnvironmentVariable(name);

        return int.TryParse(raw, out var secs) && secs >= 0 ? TimeSpan.FromSeconds(secs) : fallback;
    }

    /// <summary>
    /// Parses <c>KCAP_ACP_DEBUG_FRAMES</c> ("1"/"true", case-insensitive, are On; anything else —
    /// including unset/blank — is Off) into <see cref="DaemonConfig.DebugFrames"/>. Pulled out as a
    /// pure predicate (mirroring <see cref="ParseLogLevel"/>) so it's testable without an env var.
    /// </summary>
    internal static bool ParseDebugFramesFlag(string? value) =>
        value?.Trim() is { } v && (v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when a "cursor" <see cref="IHostedAgentRuntimeFactory"/> is registered but
    /// reports itself unavailable — the signal for <see cref="RunAsync"/>'s one-time startup
    /// Warning. Pulled out as a pure predicate over the factory list (rather than inlined in
    /// <see cref="RunAsync"/>) so it's testable without spinning up the whole DI host that method
    /// builds.
    /// </summary>
    internal static bool ShouldWarnCursorUnavailable(IEnumerable<IHostedAgentRuntimeFactory> factories) =>
        factories.FirstOrDefault(f => f.Vendor == "cursor") is { } cursorFactory && !cursorFactory.IsAvailable();

    /// <summary>
    /// Vendor tokens this daemon can run fully unattended — a strict subset of
    /// <c>SupportedVendors</c> (installed) further filtered by
    /// <see cref="IHostedAgentRuntimeFactory.SupportsUnattended"/>. Pulled out as a pure
    /// function over the factory list (same reasoning as <see cref="ShouldWarnCursorUnavailable"/>)
    /// so the reviewer-vendor-override capability advertisement is testable without spinning up
    /// the whole DI host <see cref="RunAsync"/> builds.
    /// </summary>
    internal static string[] ComputeUnattendedVendors(IEnumerable<IHostedAgentRuntimeFactory> factories) =>
        factories
            .Where(f => f.IsAvailable() && f.SupportsUnattended)
            .Select(f => f.Vendor)
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToArray();

    internal const string ClaudeLauncherPolicyVersion = "claude-unattended-v1";
    internal const string CursorLauncherPolicyVersion = "cursor-unattended-v4";
    internal const string CodexLauncherPolicyVersion = "codex-unattended-v1";
    internal const string CopilotLauncherPolicyVersion = "copilot-unattended-v1";

    internal static IReadOnlyList<UnattendedVendorCapability> ComputeUnattendedVendorCapabilities(
            IEnumerable<IHostedAgentRuntimeFactory> factories, DaemonConfig config) {
        var unattended = ComputeUnattendedVendors(factories);
        var capabilities = new List<UnattendedVendorCapability>();
        foreach (var vendor in unattended) {
            var factory = factories.First(f => string.Equals(f.Vendor, vendor, StringComparison.Ordinal));
            var (cliPath, policyVersion) = vendor switch {
                "claude"  => (config.ClaudePath, ClaudeLauncherPolicyVersion),
                "cursor"  => (config.CursorPath, CursorLauncherPolicyVersion),
                "codex"   => (config.CodexPath, CodexLauncherPolicyVersion),
                "copilot" => (config.CopilotPath, CopilotLauncherPolicyVersion),
                _         => ("", $"{vendor}-unattended-v1")
            };
            // Trust-by-default: a vendor's borrowed-review capability is a property of its FACTORY,
            // never of the installed build's identity. Gating this on an exact-build match made an
            // ordinary vendor auto-update silently withdraw the capability (and the server then
            // resolved workspace_mode=fallback, reviewing a stale committed base with nobody told).
            // See docs/superpowers/specs/2026-07-27-ai1528-trust-by-default-borrowed-review-design.md.
            var borrowedSupported = factory.SupportsBorrowedReviewFlow;
            // Reviewer MODEL override support: advertised true ONLY when this vendor — already known
            // installed + unattended-certified by ComputeUnattendedVendors above — also carries a
            // runtime-owned resolver. Vendor-neutral: we read the factory's resolver + its policy
            // version, never a vendor→model table. A vendor with no resolver advertises false/null,
            // keeping its vendor-only unattended support intact.
            var modelResolver = factory.ReviewerModelResolver;
            capabilities.Add(new(
                vendor,
                string.IsNullOrEmpty(cliPath) ? null : ProbeCliVersion(cliPath),
                policyVersion,
                borrowedSupported,
                borrowedSupported ? factory.BorrowedReviewContainment : null,
                SupportsReviewerModelResolution: modelResolver is not null,
                ReviewerModelPolicyVersion: modelResolver?.PolicyVersion));
        }
        return capabilities;
    }

    /// <summary>Rendered in place of a <c>CliVersion</c> the probe could not determine (spawn
    /// failure, timeout, empty output, unparseable output). It is a reporting placeholder ONLY — an
    /// unidentifiable build is still a trusted build and its capabilities are unaffected.</summary>
    internal const string UnknownCliVersion = "unknown";

    /// <summary>
    /// One Information line per unattended vendor recording the CLI version probed at daemon
    /// startup, so an operator reading the daemon's own log can tell which build was installed when
    /// this daemon came up. Deliberately reports the version and NOTHING else: it does not compare
    /// the installed build against any validated-build record (that would be exactly the automated
    /// version-drift detection this design rejects), and no equivalent line is emitted per launch.
    /// Pure over the computed capabilities — same reasoning as
    /// <see cref="ShouldWarnCursorUnavailable"/> — so it is testable without booting the DI host.
    /// See docs/superpowers/specs/2026-07-27-ai1528-trust-by-default-borrowed-review-design.md.
    /// </summary>
    internal static void LogUnattendedVendorIdentities(
            ILogger logger, IEnumerable<UnattendedVendorCapability> capabilities) {
        foreach (var capability in capabilities)
            LogUnattendedVendorIdentity(
                logger,
                capability.Vendor,
                string.IsNullOrWhiteSpace(capability.CliVersion) ? UnknownCliVersion : capability.CliVersion);
    }

    /// <summary>Registration probe budget. Generous and retried because this result is cached for the
    /// daemon's lifetime — one transient miss durably disables the vendor.</summary>
    const int VersionProbeTimeoutMs = 10_000;
    const int VersionProbeAttempts  = 3;

    /// <summary>Launch probe budget, deliberately the ORIGINAL 3s and a single attempt: this runs on
    /// the sequenced command lane, whose single serial consumer holds every later launch and stop
    /// behind it. A miss here is transient and retryable, so it does not need the registration
    /// budget — and inheriting it would have tripled the lane stall this change was meant to
    /// improve.</summary>
    const int LaunchVersionProbeTimeoutMs = 3_000;

    internal static string? ProbeCliVersion(string cliPath) =>
        ProbeCliVersion(cliPath, VersionProbeAttempts, VersionProbeTimeoutMs);

    /// <summary>Single attempt on the original short budget — see LaunchVersionProbeTimeoutMs.</summary>
    internal static string? ProbeCliVersionForLaunch(string cliPath) =>
        ProbeCliVersion(cliPath, attempts: 1, LaunchVersionProbeTimeoutMs);

    static string? ProbeCliVersion(string cliPath, int attempts, int timeoutMs) {
        for (var attempt = 1; attempt <= attempts; attempt++) {
            if (ProbeCliVersionOnce(cliPath, timeoutMs) is { } version) return version;
            // A cold Node start under load is the usual cause; pause rather than hammering.
            if (attempt < attempts) Thread.Sleep(250 * attempt);
        }
        return null;
    }

    static string? ProbeCliVersionOnce(string cliPath, int timeoutMs) {
        try {
            using var process = Process.Start(new ProcessStartInfo {
                FileName = cliPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "--version" }
            });
            if (process is null || !process.WaitForExit(timeoutMs)) {
                try { process?.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            var output = process.StandardOutput.ReadToEnd().Trim();
            if (output.Length == 0) output = process.StandardError.ReadToEnd().Trim();
            return ParseProbedVersion(output);
        } catch { return null; }
    }

    /// <summary>
    /// Extracts the version token from a vendor CLI's <c>--version</c> output. Pure (no process
    /// spawn) so every vendor's real output shape is unit-testable.
    ///
    /// <para>Tokenises on ALL whitespace, not just spaces: a vendor whose <c>--version</c> prints more
    /// than one line (GitHub Copilot CLI appends an "update available" line) would otherwise have the
    /// newline and the next line's first word glued onto the version — e.g.
    /// <c>"1.0.75.\nRun"</c> — because a line break is not a token separator. That value is then
    /// advertised verbatim in the daemon's capability payload and fails
    /// <see cref="CliVersionAllowed"/> outright, since it cannot parse as a version.</para>
    ///
    /// <para>Trailing sentence punctuation is stripped for the same reason: Copilot's version sits at
    /// the end of a sentence ("GitHub Copilot CLI 1.0.75."), so the raw token carries a full stop
    /// that no version parser accepts. Only trailing '.' / ',' are removed — a leading dot is never
    /// part of a version, and every other vendor's token is unaffected.</para>
    /// </summary>
    internal static string? ParseProbedVersion(string output) {
        var token = output
            .Split(WhitespaceSeparators, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(part => part.Any(char.IsDigit));
        return token?.TrimStart('v', 'V').TrimEnd('.', ',');
    }

    static readonly char[] WhitespaceSeparators = [' ', '\t', '\r', '\n'];

    internal static bool CliVersionAllowed(string? rawVersion, string ranges) {
        var normalized = (rawVersion ?? "").TrimStart('v', 'V').Split('-', '+')[0];
        if (!Version.TryParse(normalized, out var version)) return false;
        if (string.IsNullOrWhiteSpace(ranges)) return false;
        return ranges.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(range => range.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .All(term => MatchVersionTerm(version, term)));
    }

    static bool MatchVersionTerm(Version version, string term) {
        var op = term.StartsWith(">=") || term.StartsWith("<=") ? term[..2]
            : term.StartsWith('>') || term.StartsWith('<') || term.StartsWith('=') ? term[..1]
            : "=";
        var start = op == "=" && !term.StartsWith('=') ? 0 : op.Length;
        if (!Version.TryParse(term[start..], out var boundary)) return false;
        var comparison = version.CompareTo(boundary);
        return op switch {
            ">=" => comparison >= 0,
            "<=" => comparison <= 0,
            ">" => comparison > 0,
            "<" => comparison < 0,
            _ => comparison == 0
        };
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "kcap daemon '{Name}' starting, connecting to {ServerUrl}")]
    static partial void LogDaemonStarting(ILogger logger, string name, string serverUrl);

    [LoggerMessage(Level = LogLevel.Information, Message = "Unattended vendor '{Vendor}': CLI version {CliVersion}, as observed by probing the configured binary at daemon startup. That is a startup observation, not the build a later reviewer runs — if the vendor updates while this daemon keeps running, launches pick up the new build and this line stays stale until the daemon restarts.")]
    static partial void LogUnattendedVendorIdentity(ILogger logger, string vendor, string cliVersion);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cursor ACP runtime unavailable: cursor-agent CLI not found (looked for '{CursorPath}'). Cursor will not be offered as a hosted-agent vendor until this is fixed. Set KCAP_CURSOR_PATH to the cursor-agent executable, or install the Cursor CLI, then restart the daemon.")]
    static partial void LogCursorUnavailable(ILogger logger, string cursorPath);

    [LoggerMessage(Level = LogLevel.Warning, Message = "KCAP_ACP_DEBUG_FRAMES is enabled — ACP Debug logs may now contain full prompts, tool arguments, and file contents from every hosted Cursor session. Disable in any shared or persistently-logged environment.")]
    static partial void LogAcpDebugFramesEnabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Previous '{Name}' daemon (PID {Pid}) exited WITHOUT a graceful shutdown — its lock was left for the kernel to release. That is the signature of an uncatchable kill (macOS jetsam/OOM, `kill -9`), a power loss, or a hard native crash; an in-process signal handler cannot record it. If this recurs, run the daemon as a supervised service (`kcap daemon service install`) so it auto-restarts.")]
    static partial void LogPriorUncleanExit(ILogger logger, string name, string pid);

    [LoggerMessage(Level = LogLevel.Information, Message = "Lifetime: ApplicationStopping fired")]
    static partial void LogLifetimeStopping(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Lifetime: ApplicationStopped fired")]
    static partial void LogLifetimeStopped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "WaitForShutdownAsync returned — entering cleanup")]
    static partial void LogWaitForShutdownReturned(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Cleanup: disposing daemon resources")]
    static partial void LogEnteringCleanup(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Cleanup: completed, daemon exiting")]
    static partial void LogCleanupCompleted(ILogger logger);

    [LoggerMessage(Level = LogLevel.Critical, Message = "AppDomain.UnhandledException (terminating={IsTerminating})")]
    static partial void LogUnhandledException(ILogger logger, Exception ex, bool isTerminating);

    [LoggerMessage(Level = LogLevel.Error, Message = "TaskScheduler.UnobservedTaskException — observed and swallowed")]
    static partial void LogUnobservedTaskException(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "AppDomain.ProcessExit fired (this is the last log line)")]
    static partial void LogProcessExit(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Received POSIX signal {Signal} — requesting cooperative shutdown")]
    static partial void LogPosixSignal(ILogger logger, PosixSignal signal);

    /// <summary>
    /// Wires AppDomain + TaskScheduler + POSIX-signal hooks so whenever the
    /// daemon process is going away we get a last log line before the runtime
    /// tears down. Without these, the only signal we'd see is "the log ends" —
    /// indistinguishable between SIGTERM, SIGHUP (terminal closed), OOM kill,
    /// an unobserved Task FailFast, or a clean StopApplication. SIGHUP is the
    /// top suspect for "daemon dies silently after a foreground run", since
    /// ConsoleLifetime doesn't register for it and the OS default is SIGTERM
    /// the process. Routing it through <paramref name="lifetime"/> turns the
    /// hard kill into a cooperative shutdown so the cleanup finally-block
    /// gets to run.
    /// </summary>
    static void RegisterDeathRattle(ILogger logger, IHostApplicationLifetime lifetime) {
        AppDomain.CurrentDomain.UnhandledException += (_, args) => {
            if (args.ExceptionObject is Exception ex) {
                DeathRattle($"AppDomain.UnhandledException (terminating={args.IsTerminating}): {ex.GetType().Name}: {ex.Message}");
                LogUnhandledException(logger, ex, args.IsTerminating);
            }
        };

        TaskScheduler.UnobservedTaskException += (_, args) => {
            DeathRattle($"TaskScheduler.UnobservedTaskException: {args.Exception.GetType().Name}: {args.Exception.Message}");
            LogUnobservedTaskException(logger, args.Exception);
            // Mark observed so the default policy (a no-op in .NET 5+, but a
            // process-killing rethrow on legacy/AOT configurations) can't
            // escalate this past the logging step.
            args.SetObserved();
        };

        AppDomain.CurrentDomain.ProcessExit += (_, _) => {
            DeathRattle("AppDomain.ProcessExit fired (this is the last log line)");
            LogProcessExit(logger);
        };

        // POSIX signal hooks. ConsoleLifetime already wires SIGINT and SIGTERM
        // to lifetime.StopApplication(); our registration is additive (multiple
        // PosixSignalRegistration handlers all run) so it just guarantees a
        // log line lands before the cooperative shutdown begins. SIGHUP and
        // SIGQUIT are NOT caught by ConsoleLifetime, so for those we both log
        // AND call StopApplication ourselves — otherwise the OS would terminate
        // us before the host's finally-block could run.
        //
        // The returned PosixSignalRegistration IS the registration's lifetime
        // anchor — if it's GC'd and finalized the handler unregisters silently
        // and the signal goes back to its default OS action (terminate the
        // process for SIGHUP, etc). Root them in a static list so they live
        // as long as the DaemonRunner type — i.e. the process.
        foreach (var signal in new[] { PosixSignal.SIGINT, PosixSignal.SIGTERM, PosixSignal.SIGHUP, PosixSignal.SIGQUIT }) {
            try {
                _signalRegistrations.Add(PosixSignalRegistration.Create(signal, ctx => {
                    DeathRattle($"Received POSIX signal {ctx.Signal} — requesting cooperative shutdown");
                    LogPosixSignal(logger, ctx.Signal);
                    ctx.Cancel = true;
                    lifetime.StopApplication();
                }));
            } catch (PlatformNotSupportedException) {
                // Signal not supported on this OS — skip silently. SIGHUP/SIGQUIT
                // are unsupported on Windows; SIGINT/SIGTERM are supported
                // everywhere.
            }
        }
    }

    /// <summary>
    /// Roots <see cref="PosixSignalRegistration"/> instances for the lifetime
    /// of the process. <see cref="PosixSignalRegistration.Create"/> returns an
    /// <see cref="IDisposable"/> whose finalizer unregisters the handler;
    /// without a strong reference the registration is eligible for GC the
    /// moment <see cref="RegisterDeathRattle"/> returns, which would silently
    /// re-arm the OS default for SIGHUP/SIGQUIT (= terminate the daemon
    /// before the finally-block runs). Static field on a static class = same
    /// lifetime as the process.
    /// </summary>
    static readonly List<PosixSignalRegistration> _signalRegistrations = [];

    /// <summary>
    /// Synchronous stderr backstop for death-rattle messages. The default
    /// <c>AddSimpleConsole</c> provider uses a background-thread queue
    /// (<c>ConsoleLoggerProcessor</c>) that can drop messages enqueued during
    /// runtime teardown — so a <c>ProcessExit</c> or signal-handler log line
    /// may never reach the terminal even though the hook fired. Writing
    /// directly to <see cref="Console.Error"/> bypasses the queue and lands
    /// on stderr immediately. Best-effort: a closed terminal or broken pipe
    /// must not throw out of an exit hook.
    /// </summary>
    static void DeathRattle(string message) {
        try {
            Console.Error.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [death-rattle] {message}");
            Console.Error.Flush();
        } catch {
            // Stderr might be redirected to a closed pipe, the terminal
            // might be gone, etc. Already exiting — nothing useful to do.
        }
    }
}
