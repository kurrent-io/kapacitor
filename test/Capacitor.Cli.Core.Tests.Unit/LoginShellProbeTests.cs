using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Core.Tests.Unit;

public class LoginShellProbeTests {
    sealed class FakeProcessRunner : IProcessRunner {
        public readonly List<(string FileName, string[] Args, RunOptions Options)> Calls = [];
        readonly Queue<Func<Task<ProcessResult>>> _steps = new();

        public void Enqueue(ProcessResult result) => _steps.Enqueue(() => Task.FromResult(result));
        public void EnqueueThrow(Exception ex) => _steps.Enqueue(() => throw ex);
        public void EnqueuePending(TaskCompletionSource<ProcessResult> tcs) => _steps.Enqueue(() => tcs.Task);

        public Task<ProcessResult> RunAsync(string fileName, string[] args, RunOptions options, CancellationToken ct) {
            Calls.Add((fileName, args, options));
            var step = _steps.Count > 0 ? _steps.Dequeue() : () => Task.FromResult(new ProcessResult(0, "", "", false));
            return step();
        }

        public Task<StreamingResult> RunStreamingAsync(string fileName, string[] args, RunOptions options,
            Action<StreamedLine> onLine, CancellationToken ct) => throw new NotImplementedException();
    }

    static string Wrap(string path) => $"{LoginShellProbe.Sentinel}{path}{LoginShellProbe.Sentinel}";

    static LoginShellProbe Probe(FakeProcessRunner runner, string? shell = "/bin/bash") =>
        new(runner, name => name == "SHELL" ? shell : null);

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
        runner.Enqueue(new ProcessResult(0, Wrap("/usr/bin:/bin"), "", false));
        var probe = Probe(runner);

        var path = await probe.TerminalPathAsync(CancellationToken.None);

        await Assert.That(path).IsEqualTo("/usr/bin:/bin");
        await Assert.That(runner.Calls).Count().IsEqualTo(1);
        await Assert.That(runner.Calls[0].FileName).IsEqualTo("/bin/bash");
        await Assert.That(runner.Calls[0].Args[0]).IsEqualTo("-lic");
        await Assert.That(runner.Calls[0].Options.Timeout).IsEqualTo(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task TerminalPathAsync_falls_back_to_lc_when_lic_exits_nonzero() {
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcessResult(1, "", "boom", false));
        runner.Enqueue(new ProcessResult(0, Wrap("/usr/bin:/bin"), "", false));
        var probe = Probe(runner);

        var path = await probe.TerminalPathAsync(CancellationToken.None);

        await Assert.That(path).IsEqualTo("/usr/bin:/bin");
        await Assert.That(runner.Calls).Count().IsEqualTo(2);
        await Assert.That(runner.Calls[1].Args[0]).IsEqualTo("-lc");
    }

    [Test]
    public async Task TerminalPathAsync_falls_back_to_lc_when_lic_times_out() {
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcessResult(0, "", "", true));
        runner.Enqueue(new ProcessResult(0, Wrap("/usr/bin:/bin"), "", false));
        var probe = Probe(runner);

        var path = await probe.TerminalPathAsync(CancellationToken.None);

        await Assert.That(path).IsEqualTo("/usr/bin:/bin");
        await Assert.That(runner.Calls).Count().IsEqualTo(2);
        await Assert.That(runner.Calls[1].Args[0]).IsEqualTo("-lc");
    }

    [Test]
    public async Task TerminalPathAsync_falls_back_to_lc_when_lic_fails_to_start() {
        var runner = new FakeProcessRunner();
        runner.EnqueueThrow(new InvalidOperationException("stale $SHELL, not executable"));
        runner.Enqueue(new ProcessResult(0, Wrap("/usr/bin:/bin"), "", false));
        var probe = Probe(runner);

        var path = await probe.TerminalPathAsync(CancellationToken.None);

        await Assert.That(path).IsEqualTo("/usr/bin:/bin");
        await Assert.That(runner.Calls).Count().IsEqualTo(2);
        await Assert.That(runner.Calls[1].Args[0]).IsEqualTo("-lc");
    }

    [Test]
    public async Task TerminalPathAsync_SHELL_unset_uses_bin_zsh() {
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcessResult(0, Wrap("/usr/bin:/bin"), "", false));
        var probe = Probe(runner, shell: null);

        await probe.TerminalPathAsync(CancellationToken.None);

        await Assert.That(runner.Calls[0].FileName).IsEqualTo("/bin/zsh");
    }

    [Test]
    public async Task TerminalPathAsync_SHELL_empty_uses_bin_zsh() {
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcessResult(0, Wrap("/usr/bin:/bin"), "", false));
        var probe = Probe(runner, shell: "");

        await probe.TerminalPathAsync(CancellationToken.None);

        await Assert.That(runner.Calls[0].FileName).IsEqualTo("/bin/zsh");
    }

    [Test]
    public async Task TerminalPathAsync_both_attempts_failing_is_null() {
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcessResult(1, "", "boom", false));
        runner.Enqueue(new ProcessResult(1, "", "boom again", false));
        var probe = Probe(runner);

        var path = await probe.TerminalPathAsync(CancellationToken.None);

        await Assert.That(path).IsNull();
        await Assert.That(runner.Calls).Count().IsEqualTo(2);
    }

    [Test]
    public async Task TerminalPathAsync_both_attempts_failing_to_start_is_null() {
        var runner = new FakeProcessRunner();
        runner.EnqueueThrow(new InvalidOperationException("boom"));
        runner.EnqueueThrow(new InvalidOperationException("boom again"));
        var probe = Probe(runner);

        var path = await probe.TerminalPathAsync(CancellationToken.None);

        await Assert.That(path).IsNull();
        await Assert.That(runner.Calls).Count().IsEqualTo(2);
    }

    [Test]
    public async Task TerminalPathAsync_result_is_cached() {
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcessResult(0, Wrap("/usr/bin:/bin"), "", false));
        var probe = Probe(runner);

        var first = await probe.TerminalPathAsync(CancellationToken.None);
        var callsAfterFirst = runner.Calls.Count;
        var second = await probe.TerminalPathAsync(CancellationToken.None);

        await Assert.That(first).IsEqualTo(second);
        await Assert.That(callsAfterFirst).IsEqualTo(1);
        await Assert.That(runner.Calls).Count().IsEqualTo(1);
    }

    // A determined "both ran, neither worked" null IS cacheable (unlike a process-start failure,
    // covered below) — re-probing a shell that genuinely reports failure every time wastes a
    // process spawn for no new information.
    [Test]
    public async Task TerminalPathAsync_determined_null_is_cached() {
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcessResult(1, "", "boom", false));
        runner.Enqueue(new ProcessResult(1, "", "boom again", false));
        var probe = Probe(runner);

        await probe.TerminalPathAsync(CancellationToken.None);
        var callsAfterFirst = runner.Calls.Count;
        await probe.TerminalPathAsync(CancellationToken.None);

        await Assert.That(callsAfterFirst).IsEqualTo(2);
        await Assert.That(runner.Calls).Count().IsEqualTo(2);
    }

    // A process-start failure means the question was never actually asked — unlike a determined
    // null above, it must not poison the cache; the next call retries from scratch.
    [Test]
    public async Task TerminalPathAsync_process_start_failure_is_not_cached_and_retries() {
        var runner = new FakeProcessRunner();
        runner.EnqueueThrow(new InvalidOperationException("boom"));
        runner.EnqueueThrow(new InvalidOperationException("boom again"));
        var probe = Probe(runner);

        var first = await probe.TerminalPathAsync(CancellationToken.None);
        await Assert.That(first).IsNull();
        await Assert.That(runner.Calls).Count().IsEqualTo(2);

        runner.Enqueue(new ProcessResult(0, Wrap("/usr/bin:/bin"), "", false));
        var second = await probe.TerminalPathAsync(CancellationToken.None);

        await Assert.That(second).IsEqualTo("/usr/bin:/bin");
        await Assert.That(runner.Calls).Count().IsEqualTo(3); // one more call — a real retry, not a cache hit
    }

    // A caller cancelling their own wait must not cancel or corrupt the shared probe — the next
    // caller (fresh ct) still observes the same in-flight probe's real result, and no retry occurs.
    [Test]
    public async Task First_caller_cancelling_does_not_poison_the_shared_probe_for_a_later_caller() {
        var runner = new FakeProcessRunner();
        var pending = new TaskCompletionSource<ProcessResult>();
        runner.EnqueuePending(pending);
        var probe = Probe(runner);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => probe.TerminalPathAsync(cts.Token));

        pending.SetResult(new ProcessResult(0, Wrap("/usr/bin:/bin"), "", false));
        var result = await probe.TerminalPathAsync(CancellationToken.None);

        await Assert.That(result).IsEqualTo("/usr/bin:/bin");
        await Assert.That(runner.Calls).Count().IsEqualTo(1); // the one shared attempt, never retried
    }

    // --- KcapOnPathAsync ---

    [Test]
    public async Task KcapOnPathAsync_found_is_true() {
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcessResult(0, Wrap("FOUND"), "", false));
        var probe = Probe(runner);

        await Assert.That(await probe.KcapOnPathAsync(CancellationToken.None)).IsTrue();
    }

    [Test]
    public async Task KcapOnPathAsync_absent_is_false() {
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcessResult(0, Wrap("ABSENT"), "", false));
        var probe = Probe(runner);

        await Assert.That(await probe.KcapOnPathAsync(CancellationToken.None)).IsFalse();
    }

    [Test]
    public async Task KcapOnPathAsync_both_attempts_failing_is_null() {
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcessResult(1, "", "boom", false));
        runner.Enqueue(new ProcessResult(1, "", "boom again", false));
        var probe = Probe(runner);

        await Assert.That(await probe.KcapOnPathAsync(CancellationToken.None)).IsNull();
    }

    [Test]
    public async Task KcapOnPathAsync_both_attempts_failing_to_start_is_null_and_not_cached() {
        var runner = new FakeProcessRunner();
        runner.EnqueueThrow(new InvalidOperationException("boom"));
        runner.EnqueueThrow(new InvalidOperationException("boom again"));
        var probe = Probe(runner);

        var first = await probe.KcapOnPathAsync(CancellationToken.None);
        await Assert.That(first).IsNull();
        await Assert.That(runner.Calls).Count().IsEqualTo(2);

        runner.Enqueue(new ProcessResult(0, Wrap("FOUND"), "", false));
        var second = await probe.KcapOnPathAsync(CancellationToken.None);

        await Assert.That(second).IsTrue();
        await Assert.That(runner.Calls).Count().IsEqualTo(3);
    }

    [Test]
    public async Task KcapOnPathAsync_uses_a_command_v_kcap_script() {
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcessResult(0, Wrap("FOUND"), "", false));
        var probe = Probe(runner);

        await probe.KcapOnPathAsync(CancellationToken.None);

        await Assert.That(runner.Calls[0].Args[1]).Contains("command -v kcap");
    }

    [Test]
    public async Task KcapOnPathAsync_result_is_cached() {
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcessResult(0, Wrap("FOUND"), "", false));
        var probe = Probe(runner);

        await probe.KcapOnPathAsync(CancellationToken.None);
        await probe.KcapOnPathAsync(CancellationToken.None);

        await Assert.That(runner.Calls).Count().IsEqualTo(1);
    }

    // Regression: the post-install probe must never reuse the pre-install cached
    // answer — forceRefresh bypasses it AND repopulates the cache with the fresh result.
    [Test]
    public async Task KcapOnPathAsync_forceRefresh_bypasses_and_repopulates_the_cache() {
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcessResult(0, Wrap("ABSENT"), "", false));
        runner.Enqueue(new ProcessResult(0, Wrap("FOUND"), "", false));
        var probe = Probe(runner);

        var cached = await probe.KcapOnPathAsync(CancellationToken.None);
        await Assert.That(cached).IsFalse();
        await Assert.That(runner.Calls).Count().IsEqualTo(1);

        var fresh = await probe.KcapOnPathAsync(CancellationToken.None, forceRefresh: true);
        await Assert.That(fresh).IsTrue();
        await Assert.That(runner.Calls).Count().IsEqualTo(2); // a real second runner invocation

        // Repopulated: a later non-forced call reads the fresh value without re-running.
        var second = await probe.KcapOnPathAsync(CancellationToken.None);
        await Assert.That(second).IsTrue();
        await Assert.That(runner.Calls).Count().IsEqualTo(2);
    }

    // --- caching is independent per question ---

    [Test]
    public async Task Caches_are_independent_per_question() {
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcessResult(0, Wrap("/usr/bin:/bin"), "", false));
        runner.Enqueue(new ProcessResult(0, Wrap("FOUND"), "", false));
        var probe = Probe(runner);

        await probe.TerminalPathAsync(CancellationToken.None);
        await probe.KcapOnPathAsync(CancellationToken.None);

        await Assert.That(runner.Calls).Count().IsEqualTo(2);

        // Both now cached independently — repeating either issues no further calls.
        await probe.TerminalPathAsync(CancellationToken.None);
        await probe.KcapOnPathAsync(CancellationToken.None);

        await Assert.That(runner.Calls).Count().IsEqualTo(2);
    }

    // --- KcapPathAsync ---

    [Test]
    public async Task KcapPathAsync_absolute_existing_file_is_returned_verbatim() {
        using var tmp = new TempDir();
        var target = tmp.PathTo("kcap");
        File.WriteAllText(target, "cli");
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcessResult(0, Wrap(target), "", false));
        var probe = Probe(runner);

        await Assert.That(await probe.KcapPathAsync(CancellationToken.None)).IsEqualTo(target);
    }

    [Test]
    public async Task KcapPathAsync_absolute_path_with_spaces_is_returned() {
        using var tmp = new TempDir();
        var target = tmp.PathTo("kcap cli");
        File.WriteAllText(target, "cli");
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcessResult(0, Wrap(target), "", false));
        var probe = Probe(runner);

        await Assert.That(await probe.KcapPathAsync(CancellationToken.None)).IsEqualTo(target);
    }

    [Test]
    public async Task KcapPathAsync_relative_path_is_null() {
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcessResult(0, Wrap("bin/kcap"), "", false));
        var probe = Probe(runner);

        await Assert.That(await probe.KcapPathAsync(CancellationToken.None)).IsNull();
    }

    [Test]
    public async Task KcapPathAsync_bare_word_is_null() {
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcessResult(0, Wrap("kcap"), "", false));
        var probe = Probe(runner);

        await Assert.That(await probe.KcapPathAsync(CancellationToken.None)).IsNull();
    }

    [Test]
    public async Task KcapPathAsync_alias_output_is_null() {
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcessResult(0, Wrap("alias kcap='/usr/local/bin/kcap'"), "", false));
        var probe = Probe(runner);

        await Assert.That(await probe.KcapPathAsync(CancellationToken.None)).IsNull();
    }

    [Test]
    public async Task KcapPathAsync_multiline_function_definition_is_null() {
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcessResult(0, Wrap("kcap () \n{ \n    command kcap \"$@\"\n}"), "", false));
        var probe = Probe(runner);

        await Assert.That(await probe.KcapPathAsync(CancellationToken.None)).IsNull();
    }

    [Test]
    public async Task KcapPathAsync_missing_file_is_null() {
        using var tmp = new TempDir();
        var missing = tmp.PathTo("kcap");
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcessResult(0, Wrap(missing), "", false));
        var probe = Probe(runner);

        await Assert.That(await probe.KcapPathAsync(CancellationToken.None)).IsNull();
    }

    [Test]
    public async Task KcapPathAsync_directory_is_null() {
        using var tmp    = new TempDir();
        var       runner = new FakeProcessRunner();
        runner.Enqueue(new ProcessResult(0, Wrap(tmp.Path), "", false));
        var probe = Probe(runner);

        await Assert.That(await probe.KcapPathAsync(CancellationToken.None)).IsNull();
    }

    [Test]
    public async Task KcapPathAsync_result_is_cached() {
        using var tmp = new TempDir();
        var target = tmp.PathTo("kcap");
        File.WriteAllText(target, "cli");
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcessResult(0, Wrap(target), "", false));
        var probe = Probe(runner);

        await probe.KcapPathAsync(CancellationToken.None);
        await probe.KcapPathAsync(CancellationToken.None);

        await Assert.That(runner.Calls).Count().IsEqualTo(1);
    }

    [Test]
    public async Task KcapPathAsync_forceRefresh_bypasses_and_repopulates_the_cache() {
        using var tmp = new TempDir();
        var target = tmp.PathTo("kcap");
        File.WriteAllText(target, "cli");
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcessResult(0, Wrap("kcap"), "", false)); // bare word -> null
        runner.Enqueue(new ProcessResult(0, Wrap(target), "", false));
        var probe = Probe(runner);

        var cached = await probe.KcapPathAsync(CancellationToken.None);
        await Assert.That(cached).IsNull();
        await Assert.That(runner.Calls).Count().IsEqualTo(1);

        var fresh = await probe.KcapPathAsync(CancellationToken.None, forceRefresh: true);
        await Assert.That(fresh).IsEqualTo(target);
        await Assert.That(runner.Calls).Count().IsEqualTo(2); // a real second runner invocation

        // Repopulated: a later non-forced call reads the fresh value without re-running.
        var second = await probe.KcapPathAsync(CancellationToken.None);
        await Assert.That(second).IsEqualTo(target);
        await Assert.That(runner.Calls).Count().IsEqualTo(2);
    }

    [Test]
    public async Task KcapPathAsync_process_start_failure_is_not_cached_and_retries() {
        using var tmp = new TempDir();
        var target = tmp.PathTo("kcap");
        File.WriteAllText(target, "cli");
        var runner = new FakeProcessRunner();
        runner.EnqueueThrow(new InvalidOperationException("boom"));
        runner.EnqueueThrow(new InvalidOperationException("boom again"));
        var probe = Probe(runner);

        var first = await probe.KcapPathAsync(CancellationToken.None);
        await Assert.That(first).IsNull();
        await Assert.That(runner.Calls).Count().IsEqualTo(2);

        runner.Enqueue(new ProcessResult(0, Wrap(target), "", false));
        var second = await probe.KcapPathAsync(CancellationToken.None);

        await Assert.That(second).IsEqualTo(target);
        await Assert.That(runner.Calls).Count().IsEqualTo(3);
    }
}
