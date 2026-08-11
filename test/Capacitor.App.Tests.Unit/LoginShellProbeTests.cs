using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

public class LoginShellProbeTests {
    sealed class FakeProcessRunner : IProcessRunner {
        public readonly List<(string FileName, string[] Args, RunOptions Options)> Calls = [];
        public readonly Queue<ProcessResult> Results = new();

        public Task<ProcessResult> RunAsync(string fileName, string[] args, RunOptions options, CancellationToken ct) {
            Calls.Add((fileName, args, options));
            return Task.FromResult(Results.Count > 0 ? Results.Dequeue() : new ProcessResult(0, "", "", false));
        }
    }

    static string Wrap(string path) => $"{LoginShellProbe.Sentinel}{path}{LoginShellProbe.Sentinel}";

    // --- Parse ---

    [Test]
    public async Task Parse_extracts_between_the_sentinel_pair_amid_chatter() {
        var stdout = $"motd\n{LoginShellProbe.Sentinel}/a:/b{LoginShellProbe.Sentinel}\n";

        await Assert.That(LoginShellProbe.Parse(stdout)).IsEqualTo("/a:/b");
    }

    [Test]
    public async Task Parse_no_sentinel_is_null() {
        await Assert.That(LoginShellProbe.Parse("just some motd chatter")).IsNull();
    }

    [Test]
    public async Task Parse_single_sentinel_is_null() {
        await Assert.That(LoginShellProbe.Parse($"noise{LoginShellProbe.Sentinel}/a:/b")).IsNull();
    }

    [Test]
    public async Task Parse_empty_is_null() {
        await Assert.That(LoginShellProbe.Parse("")).IsNull();
    }

    // --- TerminalPathAsync argv / fallback ---

    [Test]
    public async Task TerminalPathAsync_uses_SHELL_and_tries_lic_first() {
        var runner = new FakeProcessRunner();
        runner.Results.Enqueue(new ProcessResult(0, Wrap("/usr/bin:/bin"), "", false));
        var probe = new LoginShellProbe(runner, name => name == "SHELL" ? "/bin/bash" : null);

        var path = await probe.TerminalPathAsync(CancellationToken.None);

        await Assert.That(path).IsEqualTo("/usr/bin:/bin");
        await Assert.That(runner.Calls).HasCount().EqualTo(1);
        await Assert.That(runner.Calls[0].FileName).IsEqualTo("/bin/bash");
        await Assert.That(runner.Calls[0].Args[0]).IsEqualTo("-lic");
        await Assert.That(runner.Calls[0].Options.Timeout).IsEqualTo(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task TerminalPathAsync_falls_back_to_lc_when_lic_exits_nonzero() {
        var runner = new FakeProcessRunner();
        runner.Results.Enqueue(new ProcessResult(1, "", "boom", false));
        runner.Results.Enqueue(new ProcessResult(0, Wrap("/usr/bin:/bin"), "", false));
        var probe = new LoginShellProbe(runner, name => name == "SHELL" ? "/bin/bash" : null);

        var path = await probe.TerminalPathAsync(CancellationToken.None);

        await Assert.That(path).IsEqualTo("/usr/bin:/bin");
        await Assert.That(runner.Calls).HasCount().EqualTo(2);
        await Assert.That(runner.Calls[1].Args[0]).IsEqualTo("-lc");
    }

    [Test]
    public async Task TerminalPathAsync_falls_back_to_lc_when_lic_times_out() {
        var runner = new FakeProcessRunner();
        runner.Results.Enqueue(new ProcessResult(0, "", "", true));
        runner.Results.Enqueue(new ProcessResult(0, Wrap("/usr/bin:/bin"), "", false));
        var probe = new LoginShellProbe(runner, name => name == "SHELL" ? "/bin/bash" : null);

        var path = await probe.TerminalPathAsync(CancellationToken.None);

        await Assert.That(path).IsEqualTo("/usr/bin:/bin");
        await Assert.That(runner.Calls).HasCount().EqualTo(2);
        await Assert.That(runner.Calls[1].Args[0]).IsEqualTo("-lc");
    }

    [Test]
    public async Task TerminalPathAsync_SHELL_unset_uses_bin_zsh() {
        var runner = new FakeProcessRunner();
        runner.Results.Enqueue(new ProcessResult(0, Wrap("/usr/bin:/bin"), "", false));
        var probe = new LoginShellProbe(runner, _ => null);

        await probe.TerminalPathAsync(CancellationToken.None);

        await Assert.That(runner.Calls[0].FileName).IsEqualTo("/bin/zsh");
    }

    [Test]
    public async Task TerminalPathAsync_SHELL_empty_uses_bin_zsh() {
        var runner = new FakeProcessRunner();
        runner.Results.Enqueue(new ProcessResult(0, Wrap("/usr/bin:/bin"), "", false));
        var probe = new LoginShellProbe(runner, _ => "");

        await probe.TerminalPathAsync(CancellationToken.None);

        await Assert.That(runner.Calls[0].FileName).IsEqualTo("/bin/zsh");
    }

    [Test]
    public async Task TerminalPathAsync_both_attempts_failing_is_null() {
        var runner = new FakeProcessRunner();
        runner.Results.Enqueue(new ProcessResult(1, "", "boom", false));
        runner.Results.Enqueue(new ProcessResult(1, "", "boom again", false));
        var probe = new LoginShellProbe(runner, name => name == "SHELL" ? "/bin/bash" : null);

        var path = await probe.TerminalPathAsync(CancellationToken.None);

        await Assert.That(path).IsNull();
        await Assert.That(runner.Calls).HasCount().EqualTo(2);
    }

    [Test]
    public async Task TerminalPathAsync_result_is_cached() {
        var runner = new FakeProcessRunner();
        runner.Results.Enqueue(new ProcessResult(0, Wrap("/usr/bin:/bin"), "", false));
        var probe = new LoginShellProbe(runner, name => name == "SHELL" ? "/bin/bash" : null);

        var first = await probe.TerminalPathAsync(CancellationToken.None);
        var callsAfterFirst = runner.Calls.Count;
        var second = await probe.TerminalPathAsync(CancellationToken.None);

        await Assert.That(first).IsEqualTo(second);
        await Assert.That(callsAfterFirst).IsEqualTo(1);
        await Assert.That(runner.Calls).HasCount().EqualTo(1);
    }

    // --- KcapOnPathAsync ---

    [Test]
    public async Task KcapOnPathAsync_found_is_true() {
        var runner = new FakeProcessRunner();
        runner.Results.Enqueue(new ProcessResult(0, Wrap("FOUND"), "", false));
        var probe = new LoginShellProbe(runner, name => name == "SHELL" ? "/bin/bash" : null);

        await Assert.That(await probe.KcapOnPathAsync(CancellationToken.None)).IsTrue();
    }

    [Test]
    public async Task KcapOnPathAsync_absent_is_false() {
        var runner = new FakeProcessRunner();
        runner.Results.Enqueue(new ProcessResult(0, Wrap("ABSENT"), "", false));
        var probe = new LoginShellProbe(runner, name => name == "SHELL" ? "/bin/bash" : null);

        await Assert.That(await probe.KcapOnPathAsync(CancellationToken.None)).IsFalse();
    }

    [Test]
    public async Task KcapOnPathAsync_both_attempts_failing_is_null() {
        var runner = new FakeProcessRunner();
        runner.Results.Enqueue(new ProcessResult(1, "", "boom", false));
        runner.Results.Enqueue(new ProcessResult(1, "", "boom again", false));
        var probe = new LoginShellProbe(runner, name => name == "SHELL" ? "/bin/bash" : null);

        await Assert.That(await probe.KcapOnPathAsync(CancellationToken.None)).IsNull();
    }

    [Test]
    public async Task KcapOnPathAsync_uses_a_command_v_kcap_script() {
        var runner = new FakeProcessRunner();
        runner.Results.Enqueue(new ProcessResult(0, Wrap("FOUND"), "", false));
        var probe = new LoginShellProbe(runner, name => name == "SHELL" ? "/bin/bash" : null);

        await probe.KcapOnPathAsync(CancellationToken.None);

        await Assert.That(runner.Calls[0].Args[1]).Contains("command -v kcap");
    }

    [Test]
    public async Task KcapOnPathAsync_result_is_cached() {
        var runner = new FakeProcessRunner();
        runner.Results.Enqueue(new ProcessResult(0, Wrap("FOUND"), "", false));
        var probe = new LoginShellProbe(runner, name => name == "SHELL" ? "/bin/bash" : null);

        await probe.KcapOnPathAsync(CancellationToken.None);
        await probe.KcapOnPathAsync(CancellationToken.None);

        await Assert.That(runner.Calls).HasCount().EqualTo(1);
    }

    // --- caching is independent per question ---

    [Test]
    public async Task Caches_are_independent_per_question() {
        var runner = new FakeProcessRunner();
        runner.Results.Enqueue(new ProcessResult(0, Wrap("/usr/bin:/bin"), "", false));
        runner.Results.Enqueue(new ProcessResult(0, Wrap("FOUND"), "", false));
        var probe = new LoginShellProbe(runner, name => name == "SHELL" ? "/bin/bash" : null);

        await probe.TerminalPathAsync(CancellationToken.None);
        await probe.KcapOnPathAsync(CancellationToken.None);

        await Assert.That(runner.Calls).HasCount().EqualTo(2);

        // Both now cached independently — repeating either issues no further calls.
        await probe.TerminalPathAsync(CancellationToken.None);
        await probe.KcapOnPathAsync(CancellationToken.None);

        await Assert.That(runner.Calls).HasCount().EqualTo(2);
    }
}
