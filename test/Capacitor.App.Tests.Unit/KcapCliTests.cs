using Capacitor.App.Services;
using TUnit.Assertions.Enums;

namespace Capacitor.App.Tests.Unit;

public class KcapCliTests {
    sealed class FakeProcessRunner : IProcessRunner {
        public string? SeenFileName;
        public string[]? SeenArgs;
        public RunOptions? SeenOptions;
        public Func<CancellationToken, Task<ProcessResult>>? Behavior;

        public Task<ProcessResult> RunAsync(string fileName, string[] args, RunOptions options, CancellationToken ct) {
            SeenFileName = fileName;
            SeenArgs     = args;
            SeenOptions  = options;
            return (Behavior ?? (_ => Task.FromResult(new ProcessResult(0, "", "", false))))(ct);
        }

        public IReadOnlyList<StreamedLine> ScriptedStreamLines = [];

        public Task<StreamingResult> RunStreamingAsync(string fileName, string[] args, RunOptions options,
                Action<StreamedLine> onLine, CancellationToken ct) {
            SeenFileName = fileName;
            SeenArgs     = args;
            SeenOptions  = options;
            foreach (var line in ScriptedStreamLines) onLine(line);
            return Task.FromResult(new StreamingResult(0, false, ScriptedStreamLines));
        }
    }

    const string CanonicalServer = "https://cap.example.com:443";

    static KcapCli MakeCli(FakeProcessRunner runner, string? terminalPath = null, string? canonicalServer = CanonicalServer) =>
        new(runner, "kcap", "daemon-a", "work", _ => Task.FromResult(terminalPath), canonicalServer);

    [Test]
    public async Task CliPath_reflects_the_constructor_value() {
        var cli = MakeCli(new FakeProcessRunner());

        await Assert.That(cli.CliPath).IsEqualTo("kcap");
    }

    [Test]
    public async Task VersionAsync_builds_argv_and_parses_stdout() {
        var runner = new FakeProcessRunner { Behavior = _ => Task.FromResult(new ProcessResult(0, "kcap 9.9.9\n", "", false)) };
        var cli = MakeCli(runner);

        var version = await cli.VersionAsync(CancellationToken.None);

        await Assert.That(version).IsEqualTo("9.9.9");
        await Assert.That(runner.SeenArgs).IsEquivalentTo(["--version", "--no-update-check"], CollectionOrdering.Matching);
        await Assert.That(runner.SeenOptions!.Timeout).IsEqualTo(TimeSpan.FromSeconds(10));
    }

    [Test]
    public async Task VersionAsync_nonzero_exit_is_null() {
        var runner = new FakeProcessRunner { Behavior = _ => Task.FromResult(new ProcessResult(1, "kcap 9.9.9\n", "", false)) };
        var cli = MakeCli(runner);

        await Assert.That(await cli.VersionAsync(CancellationToken.None)).IsNull();
    }

    [Test]
    public async Task VersionAsync_timed_out_is_null() {
        var runner = new FakeProcessRunner { Behavior = _ => Task.FromResult(new ProcessResult(0, "kcap 9.9.9\n", "", true)) };
        var cli = MakeCli(runner);

        await Assert.That(await cli.VersionAsync(CancellationToken.None)).IsNull();
    }

    [Test]
    public async Task ServiceStatusAsync_argv_and_parses_every_field_including_nulls() {
        const string json = """
            {"service_id":"default","unit_present":true,"state":"running","binary_path":null,"install_binary_path":"/usr/local/bin/kcap-daemon","job_pid":111,"daemon_pid":111,"txn_marker":false,"txn_active":true}
            """;
        var runner = new FakeProcessRunner { Behavior = _ => Task.FromResult(new ProcessResult(0, json, "", false)) };
        var cli = MakeCli(runner);

        var snapshot = await cli.ServiceStatusAsync(CancellationToken.None);

        await Assert.That(runner.SeenArgs).IsEquivalentTo(
            ["daemon", "service", "status", "--name", "daemon-a", "--json"], CollectionOrdering.Matching);
        await Assert.That(snapshot).IsEqualTo(new ServiceSnapshot(
            "default", true, "running", null, "/usr/local/bin/kcap-daemon", 111, 111, false, true));
        await Assert.That(runner.SeenOptions!.Timeout).IsEqualTo(TimeSpan.FromSeconds(10));
    }

    [Test]
    public async Task ServiceStatusAsync_all_null_optional_fields() {
        const string json = """
            {"service_id":"default","unit_present":false,"state":"not_installed","binary_path":null,"install_binary_path":null,"job_pid":null,"daemon_pid":null,"txn_marker":false,"txn_active":false}
            """;
        var runner = new FakeProcessRunner { Behavior = _ => Task.FromResult(new ProcessResult(0, json, "", false)) };
        var cli = MakeCli(runner);

        var snapshot = await cli.ServiceStatusAsync(CancellationToken.None);

        await Assert.That(snapshot).IsEqualTo(new ServiceSnapshot(
            "default", false, "not_installed", null, null, null, null, false, false));
    }

    [Test]
    public async Task ServiceStatusAsync_nonzero_exit_is_null() {
        var runner = new FakeProcessRunner { Behavior = _ => Task.FromResult(new ProcessResult(1, "", "boom", false)) };
        var cli = MakeCli(runner);

        await Assert.That(await cli.ServiceStatusAsync(CancellationToken.None)).IsNull();
    }

    [Test]
    public async Task ServiceStatusAsync_garbage_stdout_is_null() {
        var runner = new FakeProcessRunner { Behavior = _ => Task.FromResult(new ProcessResult(0, "not json", "", false)) };
        var cli = MakeCli(runner);

        await Assert.That(await cli.ServiceStatusAsync(CancellationToken.None)).IsNull();
    }

    [Test]
    public async Task ServiceStatusAsync_timed_out_is_null() {
        var runner = new FakeProcessRunner { Behavior = _ => Task.FromResult(new ProcessResult(0, "not read anyway", "", true)) };
        var cli = MakeCli(runner);

        await Assert.That(await cli.ServiceStatusAsync(CancellationToken.None)).IsNull();
        // Confirms the query is actually bounded — a hung `launchctl print` must not be able to
        // block this forever and deadlock the §3.2 per-mutation gate.
        await Assert.That(runner.SeenOptions!.Timeout).IsEqualTo(TimeSpan.FromSeconds(10));
    }

    [Test]
    public async Task ServiceStartVerifiedAsync_argv_and_timeout() {
        var runner = new FakeProcessRunner();
        var cli = MakeCli(runner);

        await cli.ServiceStartVerifiedAsync(CancellationToken.None);

        await Assert.That(runner.SeenArgs).IsEquivalentTo(
            ["daemon", "service", "start", "--name", "daemon-a", "--verify"], CollectionOrdering.Matching);
        await Assert.That(runner.SeenOptions!.Timeout).IsEqualTo(TimeSpan.FromSeconds(60));
    }

    [Test]
    public async Task ServiceInstallVerifiedAsync_argv_includes_profile_verify_and_replace() {
        var runner = new FakeProcessRunner();
        var cli = MakeCli(runner);

        await cli.ServiceInstallVerifiedAsync(replace: true, CancellationToken.None);

        await Assert.That(runner.SeenArgs).IsEquivalentTo(
            ["daemon", "service", "install", "--name", "daemon-a", "--profile", "work", "--verify", "--replace"],
            CollectionOrdering.Matching);
        await Assert.That(runner.SeenOptions!.Timeout).IsEqualTo(TimeSpan.FromSeconds(60));
    }

    [Test]
    public async Task ServiceInstallVerifiedAsync_without_replace_omits_the_flag() {
        var runner = new FakeProcessRunner();
        var cli = MakeCli(runner);

        await cli.ServiceInstallVerifiedAsync(replace: false, CancellationToken.None);

        await Assert.That(runner.SeenArgs).IsEquivalentTo(
            ["daemon", "service", "install", "--name", "daemon-a", "--profile", "work", "--verify"],
            CollectionOrdering.Matching);
    }

    [Test]
    public async Task DetachedStartAsync_argv_uses_abandon_wait_and_a_bounded_process_only_timeout() {
        var runner = new FakeProcessRunner();
        var cli = MakeCli(runner);

        await cli.DetachedStartAsync("boot-attempt-test", CancellationToken.None);

        await Assert.That(runner.SeenArgs).IsEquivalentTo(
            ["daemon", "start", "-d", "--name", "daemon-a"], CollectionOrdering.Matching);
        await Assert.That(runner.SeenOptions!.CancelMode).IsEqualTo(CancelMode.AbandonWait);
        await Assert.That(runner.SeenOptions!.Timeout).IsEqualTo(TimeSpan.FromSeconds(75));
        await Assert.That(runner.SeenOptions!.TimeoutKill).IsEqualTo(TimeoutKillScope.ProcessOnly);
    }

    [Test]
    public async Task DetachedStartAsync_with_boot_attempt_id_stamps_it_verbatim() {
        var runner = new FakeProcessRunner();
        var cli = MakeCli(runner);

        await cli.DetachedStartAsync("boot-attempt-123", CancellationToken.None);

        await Assert.That(runner.SeenArgs).IsEquivalentTo(
            ["daemon", "start", "-d", "--name", "daemon-a"], CollectionOrdering.Matching);
        await Assert.That(runner.SeenOptions!.EnvOverlay![KcapCli.BootAttemptVar]).IsEqualTo("boot-attempt-123");
        await Assert.That(runner.SeenOptions!.Timeout).IsEqualTo(TimeSpan.FromSeconds(75));
        await Assert.That(runner.SeenOptions!.TimeoutKill).IsEqualTo(TimeoutKillScope.ProcessOnly);
        await Assert.That(runner.SeenOptions!.CancelMode).IsEqualTo(CancelMode.AbandonWait);
    }

    [Test]
    public async Task DetachedStartAsync_with_null_CliPath_returns_exit_127_without_calling_the_runner() {
        var runner = new FakeProcessRunner();
        var cli = MakeCliWithNullPath(runner);

        var result = await cli.DetachedStartAsync("boot-attempt-123", CancellationToken.None);

        await Assert.That(result.ExitCode).IsEqualTo(127);
        await Assert.That(runner.SeenFileName).IsNull();
    }

    // Every spawn (read-only or mutating) carries the telemetry-suppression marker — Plan A's
    // Program.cs consumes and removes it before dispatch.
    [Test]
    public async Task Every_call_carries_the_app_spawn_no_telemetry_marker() {
        var runner = new FakeProcessRunner();
        var cli = MakeCli(runner);

        await cli.VersionAsync(CancellationToken.None);
        await Assert.That(runner.SeenOptions!.EnvOverlay![KcapCli.SpawnNoTelemetryVar]).IsEqualTo("1");

        await cli.ServiceStatusAsync(CancellationToken.None);
        await Assert.That(runner.SeenOptions!.EnvOverlay![KcapCli.SpawnNoTelemetryVar]).IsEqualTo("1");

        await cli.ServiceStartVerifiedAsync(CancellationToken.None);
        await Assert.That(runner.SeenOptions!.EnvOverlay![KcapCli.SpawnNoTelemetryVar]).IsEqualTo("1");

        await cli.ServiceInstallVerifiedAsync(replace: false, CancellationToken.None);
        await Assert.That(runner.SeenOptions!.EnvOverlay![KcapCli.SpawnNoTelemetryVar]).IsEqualTo("1");

        await cli.DetachedStartAsync("boot-attempt-test", CancellationToken.None);
        await Assert.That(runner.SeenOptions!.EnvOverlay![KcapCli.SpawnNoTelemetryVar]).IsEqualTo("1");
    }

    [Test]
    public async Task Mutation_calls_carry_the_consent_seed_and_server_expectation_overlays() {
        var runner = new FakeProcessRunner();
        var cli = MakeCli(runner);

        await cli.ServiceStartVerifiedAsync(CancellationToken.None);
        await Assert.That(runner.SeenOptions!.EnvOverlay![KcapCli.ConsentSeedDefaultVar]).IsEqualTo("prompt");
        await Assert.That(runner.SeenOptions!.EnvOverlay![KcapCli.ExpectServerUrlVar]).IsEqualTo(CanonicalServer);

        await cli.ServiceInstallVerifiedAsync(replace: false, CancellationToken.None);
        await Assert.That(runner.SeenOptions!.EnvOverlay![KcapCli.ConsentSeedDefaultVar]).IsEqualTo("prompt");
        await Assert.That(runner.SeenOptions!.EnvOverlay![KcapCli.ExpectServerUrlVar]).IsEqualTo(CanonicalServer);

        await cli.DetachedStartAsync("boot-attempt-test", CancellationToken.None);
        await Assert.That(runner.SeenOptions!.EnvOverlay![KcapCli.ConsentSeedDefaultVar]).IsEqualTo("prompt");
        await Assert.That(runner.SeenOptions!.EnvOverlay![KcapCli.ExpectServerUrlVar]).IsEqualTo(CanonicalServer);
    }

    [Test]
    public async Task Status_and_version_calls_never_carry_the_seed_or_expectation_overlays() {
        var runner = new FakeProcessRunner();
        var cli = MakeCli(runner);

        await cli.VersionAsync(CancellationToken.None);
        await Assert.That(runner.SeenOptions!.EnvOverlay!.ContainsKey(KcapCli.ConsentSeedDefaultVar)).IsFalse();
        await Assert.That(runner.SeenOptions!.EnvOverlay!.ContainsKey(KcapCli.ExpectServerUrlVar)).IsFalse();

        await cli.ServiceStatusAsync(CancellationToken.None);
        await Assert.That(runner.SeenOptions!.EnvOverlay!.ContainsKey(KcapCli.ConsentSeedDefaultVar)).IsFalse();
        await Assert.That(runner.SeenOptions!.EnvOverlay!.ContainsKey(KcapCli.ExpectServerUrlVar)).IsFalse();
    }

    // Mutations are action-scoped (the lane always binds a server); a null canonicalServer here is
    // a construction bug, so it throws before touching the runner at all — never a degraded result.
    [Test]
    public async Task ServiceStartVerifiedAsync_with_null_canonicalServer_throws_before_any_spawn() {
        var runner = new FakeProcessRunner();
        var cli = MakeCli(runner, canonicalServer: null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => cli.ServiceStartVerifiedAsync(CancellationToken.None));
        await Assert.That(runner.SeenFileName).IsNull();
    }

    [Test]
    public async Task ServiceInstallVerifiedAsync_with_null_canonicalServer_throws_before_any_spawn() {
        var runner = new FakeProcessRunner();
        var cli = MakeCli(runner, canonicalServer: null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => cli.ServiceInstallVerifiedAsync(replace: false, CancellationToken.None));
        await Assert.That(runner.SeenFileName).IsNull();
    }

    [Test]
    public async Task DetachedStartAsync_with_null_canonicalServer_throws_before_any_spawn() {
        var runner = new FakeProcessRunner();
        var cli = MakeCli(runner, canonicalServer: null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => cli.DetachedStartAsync("boot-attempt-123", CancellationToken.None));
        await Assert.That(runner.SeenFileName).IsNull();
    }

    // The overlay is additive-only over the documented keys — it must never carry the user's own
    // KCAP_TELEMETRY choice (or anything else ambient) onto a child spawn.
    [Test]
    public async Task Read_only_overlay_carries_only_the_documented_keys() {
        var runner = new FakeProcessRunner();
        var cli = MakeCli(runner);

        await cli.VersionAsync(CancellationToken.None);

        await Assert.That(runner.SeenOptions!.EnvOverlay!.Keys).IsEquivalentTo(
            ["KCAP_PROFILE", KcapCli.SpawnNoTelemetryVar], CollectionOrdering.Any);
        await Assert.That(runner.SeenOptions!.EnvOverlay!.ContainsKey("KCAP_TELEMETRY")).IsFalse();
    }

    [Test]
    public async Task Mutation_overlay_carries_only_the_documented_keys() {
        var runner = new FakeProcessRunner();
        var cli = MakeCli(runner);

        await cli.ServiceStartVerifiedAsync(CancellationToken.None);

        await Assert.That(runner.SeenOptions!.EnvOverlay!.Keys).IsEquivalentTo(
            ["KCAP_PROFILE", KcapCli.SpawnNoTelemetryVar, KcapCli.ConsentSeedDefaultVar, KcapCli.ExpectServerUrlVar],
            CollectionOrdering.Any);
        await Assert.That(runner.SeenOptions!.EnvOverlay!.ContainsKey("KCAP_TELEMETRY")).IsFalse();
    }

    [Test]
    public async Task Every_call_carries_the_pinned_profile() {
        var runner = new FakeProcessRunner();
        var cli = MakeCli(runner, terminalPath: null);

        await cli.VersionAsync(CancellationToken.None);
        await Assert.That(runner.SeenOptions!.EnvOverlay!["KCAP_PROFILE"]).IsEqualTo("work");

        await cli.ServiceStatusAsync(CancellationToken.None);
        await Assert.That(runner.SeenOptions!.EnvOverlay!["KCAP_PROFILE"]).IsEqualTo("work");

        await cli.ServiceStartVerifiedAsync(CancellationToken.None);
        await Assert.That(runner.SeenOptions!.EnvOverlay!["KCAP_PROFILE"]).IsEqualTo("work");

        await cli.ServiceInstallVerifiedAsync(replace: false, CancellationToken.None);
        await Assert.That(runner.SeenOptions!.EnvOverlay!["KCAP_PROFILE"]).IsEqualTo("work");

        await cli.DetachedStartAsync("boot-attempt-test", CancellationToken.None);
        await Assert.That(runner.SeenOptions!.EnvOverlay!["KCAP_PROFILE"]).IsEqualTo("work");
    }

    // Decision 7: the PATH overlay belongs on the unit-writing mutation (install) only — starting
    // an already-installed unit recaptures nothing, and read-only queries never need it. Even a
    // probe that DOES know the terminal PATH must not leak it onto these calls.
    [Test]
    public async Task Read_only_and_start_verify_calls_never_carry_a_path_overlay_even_when_the_probe_knows_it() {
        var runner = new FakeProcessRunner();
        var cli = MakeCli(runner, terminalPath: "/usr/bin:/bin");

        await cli.VersionAsync(CancellationToken.None);
        await Assert.That(runner.SeenOptions!.EnvOverlay!.ContainsKey("PATH")).IsFalse();

        await cli.ServiceStatusAsync(CancellationToken.None);
        await Assert.That(runner.SeenOptions!.EnvOverlay!.ContainsKey("PATH")).IsFalse();

        await cli.ServiceStartVerifiedAsync(CancellationToken.None);
        await Assert.That(runner.SeenOptions!.EnvOverlay!.ContainsKey("PATH")).IsFalse();

        await cli.DetachedStartAsync("boot-attempt-test", CancellationToken.None);
        await Assert.That(runner.SeenOptions!.EnvOverlay!.ContainsKey("PATH")).IsFalse();
    }

    [Test]
    public async Task ServiceInstallVerifiedAsync_carries_the_resolved_terminal_path() {
        var runner = new FakeProcessRunner();
        var cli = MakeCli(runner, terminalPath: "/usr/bin:/bin");

        await cli.ServiceInstallVerifiedAsync(replace: false, CancellationToken.None);

        await Assert.That(runner.SeenOptions!.EnvOverlay!["PATH"]).IsEqualTo("/usr/bin:/bin");
        await Assert.That(runner.SeenOptions!.EnvOverlay!["KCAP_PROFILE"]).IsEqualTo("work");
    }

    [Test]
    public async Task ServiceInstallVerifiedAsync_omits_path_overlay_when_the_probe_resolves_unknown() {
        var runner = new FakeProcessRunner();
        var cli = MakeCli(runner, terminalPath: null);

        await cli.ServiceInstallVerifiedAsync(replace: false, CancellationToken.None);

        await Assert.That(runner.SeenOptions!.EnvOverlay!.ContainsKey("PATH")).IsFalse();
        await Assert.That(runner.SeenOptions!.EnvOverlay!["KCAP_PROFILE"]).IsEqualTo("work");
    }

    [Test]
    public async Task ServiceInstallVerifiedAsync_with_no_resolver_omits_path_overlay() {
        var runner = new FakeProcessRunner();
        var cli = new KcapCli(runner, "kcap", "daemon-a", "work", terminalPathAsync: null, CanonicalServer);

        await cli.ServiceInstallVerifiedAsync(replace: false, CancellationToken.None);

        await Assert.That(runner.SeenOptions!.EnvOverlay!.ContainsKey("PATH")).IsFalse();
    }

    // The probe is queried lazily, per install call, with the mutation's own token — never a
    // detached/None token that could outlive the caller's cancellation.
    [Test]
    public async Task ServiceInstallVerifiedAsync_resolves_the_path_with_the_calls_own_cancellation_token() {
        var runner = new FakeProcessRunner();
        CancellationToken? seenToken = null;
        var cli = new KcapCli(runner, "kcap", "daemon-a", "work", ct => {
            seenToken = ct;
            return Task.FromResult<string?>("/usr/bin:/bin");
        }, CanonicalServer);
        using var cts = new CancellationTokenSource();

        await cli.ServiceInstallVerifiedAsync(replace: false, cts.Token);

        await Assert.That(seenToken).IsEqualTo(cts.Token);
    }

    [Test]
    public async Task Every_call_targets_the_resolved_cli_path() {
        var runner = new FakeProcessRunner();
        var cli = MakeCli(runner);

        await cli.VersionAsync(CancellationToken.None);

        await Assert.That(runner.SeenFileName).IsEqualTo("kcap");
    }

    // Fix: a broken KCAP_APP_CLI_PATH (CliResolver.ResolvePath returned null) used to make Run
    // throw via a null-forgiving `CliPath!` — the app treats null CliPath as "no CLI", so every
    // call must degrade the same honest way instead of crashing whichever caller hits it.
    static KcapCli MakeCliWithNullPath(FakeProcessRunner runner) =>
        new(runner, null, "daemon-a", "work", _ => Task.FromResult<string?>(null), CanonicalServer);

    [Test]
    public async Task VersionAsync_with_null_CliPath_returns_null_without_calling_the_runner() {
        var runner = new FakeProcessRunner();
        var cli = MakeCliWithNullPath(runner);

        await Assert.That(await cli.VersionAsync(CancellationToken.None)).IsNull();
        await Assert.That(runner.SeenFileName).IsNull();
    }

    [Test]
    public async Task ServiceStatusAsync_with_null_CliPath_returns_null_without_calling_the_runner() {
        var runner = new FakeProcessRunner();
        var cli = MakeCliWithNullPath(runner);

        await Assert.That(await cli.ServiceStatusAsync(CancellationToken.None)).IsNull();
        await Assert.That(runner.SeenFileName).IsNull();
    }

    [Test]
    public async Task ServiceStartVerifiedAsync_with_null_CliPath_returns_exit_127_without_calling_the_runner() {
        var runner = new FakeProcessRunner();
        var cli = MakeCliWithNullPath(runner);

        var result = await cli.ServiceStartVerifiedAsync(CancellationToken.None);

        await Assert.That(result.ExitCode).IsEqualTo(127);
        await Assert.That(runner.SeenFileName).IsNull();
    }

    [Test]
    public async Task ServiceInstallVerifiedAsync_with_null_CliPath_returns_exit_127_without_calling_the_runner() {
        var runner = new FakeProcessRunner();
        var cli = MakeCliWithNullPath(runner);

        var result = await cli.ServiceInstallVerifiedAsync(replace: false, CancellationToken.None);

        await Assert.That(result.ExitCode).IsEqualTo(127);
        await Assert.That(runner.SeenFileName).IsNull();
    }

    [Test]
    public async Task PluginInstallAsync_null_vendor_flag_is_the_flagless_claude_default() {
        var runner = new FakeProcessRunner();
        var cli = MakeCli(runner);

        await cli.PluginInstallAsync(null, CancellationToken.None);

        await Assert.That(runner.SeenArgs).IsEquivalentTo(["plugin", "install"], CollectionOrdering.Matching);
        await Assert.That(runner.SeenOptions!.Timeout).IsEqualTo(TimeSpan.FromSeconds(60));
    }

    [Test]
    public async Task PluginInstallAsync_with_vendor_flag_appends_it() {
        var runner = new FakeProcessRunner();
        var cli = MakeCli(runner);

        await cli.PluginInstallAsync("--codex", CancellationToken.None);

        await Assert.That(runner.SeenArgs).IsEquivalentTo(["plugin", "install", "--codex"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task PluginInstallAsync_env_overlay_carries_profile_and_no_telemetry_but_not_mutation_keys() {
        var runner = new FakeProcessRunner();
        var cli = MakeCli(runner);

        await cli.PluginInstallAsync(null, CancellationToken.None);

        await Assert.That(runner.SeenOptions!.EnvOverlay!["KCAP_PROFILE"]).IsEqualTo("work");
        await Assert.That(runner.SeenOptions!.EnvOverlay![KcapCli.SpawnNoTelemetryVar]).IsEqualTo("1");
        await Assert.That(runner.SeenOptions!.EnvOverlay!.ContainsKey(KcapCli.ConsentSeedDefaultVar)).IsFalse();
        await Assert.That(runner.SeenOptions!.EnvOverlay!.ContainsKey(KcapCli.ExpectServerUrlVar)).IsFalse();
    }

    [Test]
    public async Task PluginInstallAsync_with_null_CliPath_returns_exit_127_without_calling_the_runner() {
        var runner = new FakeProcessRunner();
        var cli = MakeCliWithNullPath(runner);

        var result = await cli.PluginInstallAsync(null, CancellationToken.None);

        await Assert.That(result.ExitCode).IsEqualTo(127);
        await Assert.That(runner.SeenFileName).IsNull();
    }

    [Test]
    public async Task ImportAsync_everything_scope_with_two_vendor_flags_builds_argv_in_order() {
        var runner = new FakeProcessRunner();
        var cli = MakeCli(runner);
        var request = new ImportRequest(ImportScopeChoice.Everything, null, ["--codex", "--cursor"]);

        await cli.ImportAsync(request, _ => { }, CancellationToken.None);

        await Assert.That(runner.SeenArgs).IsEquivalentTo(
            ["import", "--all", "--yes", "--codex", "--cursor"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task ImportAsync_org_scope_builds_argv() {
        var runner = new FakeProcessRunner();
        var cli = MakeCli(runner);
        var request = new ImportRequest(ImportScopeChoice.Org, "my-org", []);

        await cli.ImportAsync(request, _ => { }, CancellationToken.None);

        await Assert.That(runner.SeenArgs).IsEquivalentTo(
            ["import", "--org", "my-org", "--yes"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task ImportAsync_repo_scope_builds_argv() {
        var runner = new FakeProcessRunner();
        var cli = MakeCli(runner);
        var request = new ImportRequest(ImportScopeChoice.Repo, "my-org/my-repo", []);

        await cli.ImportAsync(request, _ => { }, CancellationToken.None);

        await Assert.That(runner.SeenArgs).IsEquivalentTo(
            ["import", "--repo", "my-org/my-repo", "--yes"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task ImportAsync_env_overlay_carries_profile_and_no_telemetry_but_not_mutation_keys() {
        var runner = new FakeProcessRunner();
        var cli = MakeCli(runner);
        var request = new ImportRequest(ImportScopeChoice.Everything, null, []);

        await cli.ImportAsync(request, _ => { }, CancellationToken.None);

        await Assert.That(runner.SeenOptions!.EnvOverlay!["KCAP_PROFILE"]).IsEqualTo("work");
        await Assert.That(runner.SeenOptions!.EnvOverlay![KcapCli.SpawnNoTelemetryVar]).IsEqualTo("1");
        await Assert.That(runner.SeenOptions!.EnvOverlay!.ContainsKey(KcapCli.ConsentSeedDefaultVar)).IsFalse();
        await Assert.That(runner.SeenOptions!.EnvOverlay!.ContainsKey(KcapCli.ExpectServerUrlVar)).IsFalse();
    }

    [Test]
    public async Task ImportAsync_passes_no_internal_timeout() {
        var runner = new FakeProcessRunner();
        var cli = MakeCli(runner);
        var request = new ImportRequest(ImportScopeChoice.Everything, null, []);

        await cli.ImportAsync(request, _ => { }, CancellationToken.None);

        await Assert.That(runner.SeenOptions!.Timeout).IsNull();
    }

    [Test]
    public async Task ImportAsync_streams_lines_and_returns_the_scripted_result() {
        var runner = new FakeProcessRunner {
            ScriptedStreamLines = [
                new StreamedLine(ProcessStreamKind.Stdout, "importing repo one"),
                new StreamedLine(ProcessStreamKind.Stderr, "warning: skip"),
            ],
        };
        var cli = MakeCli(runner);
        var request = new ImportRequest(ImportScopeChoice.Everything, null, []);
        List<StreamedLine> seen = [];

        var result = await cli.ImportAsync(request, seen.Add, CancellationToken.None);

        await Assert.That(seen).IsEquivalentTo(runner.ScriptedStreamLines, CollectionOrdering.Matching);
        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.TimedOut).IsFalse();
    }

    [Test]
    public async Task ImportAsync_with_null_CliPath_returns_a_synthesized_no_cli_result_without_calling_the_runner() {
        var runner = new FakeProcessRunner();
        var cli = MakeCliWithNullPath(runner);
        var request = new ImportRequest(ImportScopeChoice.Everything, null, []);

        var result = await cli.ImportAsync(request, _ => { }, CancellationToken.None);

        await Assert.That(result.ExitCode).IsEqualTo(-1);
        await Assert.That(result.TimedOut).IsFalse();
        await Assert.That(result.Tail).IsEquivalentTo(
            [new StreamedLine(ProcessStreamKind.Stderr, "kcap CLI not found")], CollectionOrdering.Matching);
        await Assert.That(runner.SeenFileName).IsNull();
    }
}
