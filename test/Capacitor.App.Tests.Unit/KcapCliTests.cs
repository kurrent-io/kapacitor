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
    }

    static KcapCli MakeCli(FakeProcessRunner runner, string? terminalPath = null) =>
        new(runner, "kcap", "daemon-a", "work", terminalPath);

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
    }

    [Test]
    public async Task ServiceStartVerifiedAsync_argv_and_timeout() {
        var runner = new FakeProcessRunner();
        var cli = MakeCli(runner);

        await cli.ServiceStartVerifiedAsync(CancellationToken.None);

        await Assert.That(runner.SeenArgs).IsEquivalentTo(
            ["daemon", "service", "start", "--name", "daemon-a", "--verify"], CollectionOrdering.Matching);
        await Assert.That(runner.SeenOptions!.Timeout).IsEqualTo(TimeSpan.FromSeconds(45));
    }

    [Test]
    public async Task ServiceInstallVerifiedAsync_argv_includes_profile_verify_and_replace() {
        var runner = new FakeProcessRunner();
        var cli = MakeCli(runner);

        await cli.ServiceInstallVerifiedAsync(replace: true, CancellationToken.None);

        await Assert.That(runner.SeenArgs).IsEquivalentTo(
            ["daemon", "service", "install", "--name", "daemon-a", "--profile", "work", "--verify", "--replace"],
            CollectionOrdering.Matching);
        await Assert.That(runner.SeenOptions!.Timeout).IsEqualTo(TimeSpan.FromSeconds(45));
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
    public async Task DetachedStartAsync_argv_uses_abandon_wait_and_no_timeout() {
        var runner = new FakeProcessRunner();
        var cli = MakeCli(runner);

        await cli.DetachedStartAsync(CancellationToken.None);

        await Assert.That(runner.SeenArgs).IsEquivalentTo(
            ["daemon", "start", "-d", "--name", "daemon-a"], CollectionOrdering.Matching);
        await Assert.That(runner.SeenOptions!.CancelMode).IsEqualTo(CancelMode.AbandonWait);
        await Assert.That(runner.SeenOptions!.Timeout).IsNull();
    }

    [Test]
    public async Task Every_call_carries_the_pinned_profile_and_no_path_when_unknown() {
        var runner = new FakeProcessRunner();
        var cli = MakeCli(runner, terminalPath: null);

        await cli.VersionAsync(CancellationToken.None);
        await Assert.That(runner.SeenOptions!.EnvOverlay!["KCAP_PROFILE"]).IsEqualTo("work");
        await Assert.That(runner.SeenOptions!.EnvOverlay!.ContainsKey("PATH")).IsFalse();

        await cli.ServiceStatusAsync(CancellationToken.None);
        await Assert.That(runner.SeenOptions!.EnvOverlay!["KCAP_PROFILE"]).IsEqualTo("work");

        await cli.ServiceStartVerifiedAsync(CancellationToken.None);
        await Assert.That(runner.SeenOptions!.EnvOverlay!["KCAP_PROFILE"]).IsEqualTo("work");

        await cli.ServiceInstallVerifiedAsync(replace: false, CancellationToken.None);
        await Assert.That(runner.SeenOptions!.EnvOverlay!["KCAP_PROFILE"]).IsEqualTo("work");

        await cli.DetachedStartAsync(CancellationToken.None);
        await Assert.That(runner.SeenOptions!.EnvOverlay!["KCAP_PROFILE"]).IsEqualTo("work");
    }

    [Test]
    public async Task PATH_overlay_present_only_when_terminal_path_given() {
        var runner = new FakeProcessRunner();
        var cli = MakeCli(runner, terminalPath: "/usr/bin:/bin");

        await cli.VersionAsync(CancellationToken.None);

        await Assert.That(runner.SeenOptions!.EnvOverlay!["PATH"]).IsEqualTo("/usr/bin:/bin");
        await Assert.That(runner.SeenOptions!.EnvOverlay!["KCAP_PROFILE"]).IsEqualTo("work");
    }

    [Test]
    public async Task Every_call_targets_the_resolved_cli_path() {
        var runner = new FakeProcessRunner();
        var cli = MakeCli(runner);

        await cli.VersionAsync(CancellationToken.None);

        await Assert.That(runner.SeenFileName).IsEqualTo("kcap");
    }
}
