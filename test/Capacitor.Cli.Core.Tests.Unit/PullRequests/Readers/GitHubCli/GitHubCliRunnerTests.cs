using Capacitor.Cli.Core.PullRequests.Readers.GitHubCli;

namespace Capacitor.Cli.Core.Tests.Unit.PullRequests.Readers.GitHubCli;

public class GitHubCliRunnerTests {
    [TempDir] public required TempDir Tmp { get; init; }

    static string Executable => OperatingSystem.IsWindows() ? "gh.exe" : "gh";

    string InstallGh(string directory) {
        var path = Tmp.CreateFile([directory, Executable]);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    [Test]
    public async Task Resolves_gh_from_the_terminal_path_before_the_process_path() {
        var terminal = InstallGh("terminal");
        var process = InstallGh("process");
        var shell = new FakeLoginShellProbe(Path.GetDirectoryName(terminal));
        var runner = new GitHubCliRunner(new FakeGhProcessRunner(), shell, name => name == "PATH" ? Path.GetDirectoryName(process) : null);
        var expected = OperatingSystem.IsWindows() ? process : terminal;
        await Assert.That(await runner.LocateAsync(false, default)).IsEqualTo(expected);
        await runner.LocateAsync(false, default);
        await Assert.That(shell.Probes).IsEqualTo(OperatingSystem.IsWindows() ? 0 : 1);
    }

    [Test]
    public async Task Falls_back_to_the_process_path_and_reports_null_when_nothing_has_gh() {
        var process = InstallGh("process");
        var runner = new GitHubCliRunner(new FakeGhProcessRunner(), new FakeLoginShellProbe(Tmp.CreateDir("empty").Path), name => name == "PATH" ? Path.GetDirectoryName(process) : null);
        await Assert.That(await runner.LocateAsync(false, default)).IsEqualTo(process);
        string nothing = Tmp.CreateDir("nothing");
        var missing = new GitHubCliRunner(new FakeGhProcessRunner(), new FakeLoginShellProbe(null), _ => nothing);
        await Assert.That(await missing.LocateAsync(false, default)).IsNull();
        var result = await missing.RunAsync(["auth", "status"], default);
        await Assert.That(result.Outcome).IsEqualTo(GitHubCliOutcome.NotStarted);
    }

    [Test]
    public async Task Runs_with_the_fixed_overlay_deadline_and_kill_mode() {
        var gh = InstallGh("bin");
        var process = new FakeGhProcessRunner();
        process.When(["auth", "status"], """{"hosts":{}}""");
        var runner = new GitHubCliRunner(process, null, name => name == "PATH" ? Path.GetDirectoryName(gh) : null);
        var result = await runner.RunAsync(["auth", "status", "--json", "hosts"], default);
        await Assert.That(result.Outcome).IsEqualTo(GitHubCliOutcome.Ok);
        await Assert.That(result.Stdout).IsEqualTo("""{"hosts":{}}""");
        var call = process.Calls.Single();
        await Assert.That(call.FileName).IsEqualTo(gh);
        await Assert.That(call.Options.Timeout).IsEqualTo(TimeSpan.FromSeconds(20));
        await Assert.That(call.Options.CancelMode).IsEqualTo(CancelMode.KillTree);
        var overlay = call.Options.EnvOverlay!;
        await Assert.That(overlay["GH_PROMPT_DISABLED"]).IsEqualTo("1");
        await Assert.That(overlay["GH_NO_UPDATE_NOTIFIER"]).IsEqualTo("1");
        await Assert.That(overlay["NO_COLOR"]).IsEqualTo("1");
        await Assert.That(overlay["GH_PAGER"]).IsEqualTo("cat");
        await Assert.That(overlay.ContainsKey("GH_TOKEN")).IsFalse();
        await Assert.That(overlay.ContainsKey("GH_HOST")).IsFalse();
    }

    [Test]
    public async Task Timeouts_failures_and_oversized_output_map_to_outcomes() {
        var gh = InstallGh("bin");
        var process = new FakeGhProcessRunner();
        process.When(["slow"], "", exitCode: -1, timedOut: true);
        process.When(["bad"], "", exitCode: 1, stderr: "GraphQL: Could not resolve to a PullRequest");
        process.When(["big"], new string('x', GitHubCliRunner.OutputLimit + 1));
        var runner = new GitHubCliRunner(process, null, name => name == "PATH" ? Path.GetDirectoryName(gh) : null);
        await Assert.That((await runner.RunAsync(["slow"], default)).Outcome).IsEqualTo(GitHubCliOutcome.TimedOut);
        var failed = await runner.RunAsync(["bad"], default);
        await Assert.That(failed.Outcome).IsEqualTo(GitHubCliOutcome.Failed);
        await Assert.That(failed.Stderr).Contains("Could not resolve");
        var big = await runner.RunAsync(["big"], default);
        await Assert.That(big.Outcome).IsEqualTo(GitHubCliOutcome.Oversized);
        await Assert.That(big.Stdout).IsEmpty();
    }

    [Test]
    public async Task A_start_failure_forgets_the_located_path_so_the_next_call_relocates() {
        var gh = InstallGh("bin");
        var process = new FakeGhProcessRunner { StartFailure = new InvalidOperationException("Failed to start") };
        var shell = new FakeLoginShellProbe(Path.GetDirectoryName(gh));
        var runner = new GitHubCliRunner(process, shell, name => name == "PATH" ? Path.GetDirectoryName(gh) : null);
        await Assert.That((await runner.RunAsync(["auth", "status"], default)).Outcome).IsEqualTo(GitHubCliOutcome.NotStarted);
        process.StartFailure = null;
        process.When(["auth", "status"], "{}");
        await Assert.That((await runner.RunAsync(["auth", "status"], default)).Outcome).IsEqualTo(GitHubCliOutcome.Ok);
        await Assert.That(shell.Probes).IsEqualTo(OperatingSystem.IsWindows() ? 0 : 2);
    }

    [Test]
    public async Task At_most_two_processes_run_at_once() {
        var gh = InstallGh("bin");
        var process = new FakeGhProcessRunner();
        var a = new TaskCompletionSource<ProcessResult>(); var b = new TaskCompletionSource<ProcessResult>();
        process.WhenPending(["a"], a); process.WhenPending(["b"], b); process.When(["c"], "");
        var runner = new GitHubCliRunner(process, null, name => name == "PATH" ? Path.GetDirectoryName(gh) : null);
        var first = runner.RunAsync(["a"], default); var second = runner.RunAsync(["b"], default); var third = runner.RunAsync(["c"], default);
        await Task.Delay(50);
        await Assert.That(process.Calls.Count).IsEqualTo(2);
        a.SetResult(new(0, "", "", false));
        await first;
        await third;
        await Assert.That(process.Calls.Count).IsEqualTo(3);
        b.SetResult(new(0, "", "", false));
        await second;
    }

    [Test]
    [Arguments("octocat", true)] [Arguments("-octo", false)] [Arguments("octo cat", false)] [Arguments("", false)]
    [Arguments("a-very-long-owner-name-that-exceeds-the-github-maximum", false)]
    public async Task Owner_validation(string owner, bool valid) => await Assert.That(GitHubCliRunner.ValidOwner(owner)).IsEqualTo(valid);

    [Test]
    [Arguments("kcap-cli", true)] [Arguments("re.po_x", true)] [Arguments("..", false)] [Arguments("a/b", false)] [Arguments("", false)]
    public async Task Repository_validation(string repo, bool valid) => await Assert.That(GitHubCliRunner.ValidRepo(repo)).IsEqualTo(valid);

    [Test]
    [Arguments("feature/x", true)] [Arguments("-bad", false)] [Arguments("has space", false)] [Arguments("a..b", false)] [Arguments("x.lock", false)]
    [Arguments("a\tb", false)] [Arguments("a~b", false)] [Arguments("/lead", false)]
    public async Task Branch_validation(string branch, bool valid) => await Assert.That(GitHubCliRunner.ValidBranch(branch)).IsEqualTo(valid);

    [Test]
    [Arguments("github.com", true)] [Arguments("ghe.example", true)] [Arguments("bad host", false)] [Arguments("", false)] [Arguments("a/b", false)]
    public async Task Host_validation(string host, bool valid) => await Assert.That(GitHubCliRunner.ValidHost(host)).IsEqualTo(valid);

    [Test]
    [Arguments("PRRT_kwDOR9HOJ86gJOag", true)] [Arguments("Y3Vyc29yOnYyOpK0MjAyNi0wOS0wOFQwNzo1MTozOVrOoCTmpA==", true)] [Arguments("", false)] [Arguments("a b", false)]
    public async Task Node_id_validation(string id, bool valid) => await Assert.That(GitHubCliRunner.ValidNodeId(id)).IsEqualTo(valid);

    [Test]
    [Arguments("Y3Vyc29yOnYyOpK0+A/=", true)] [Arguments("has space", false)] [Arguments("", false)]
    public async Task Cursor_validation(string cursor, bool valid) => await Assert.That(GitHubCliRunner.ValidCursor(cursor)).IsEqualTo(valid);
}
