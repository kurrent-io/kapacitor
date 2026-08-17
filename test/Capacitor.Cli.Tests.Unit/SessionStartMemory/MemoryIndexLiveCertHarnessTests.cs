namespace Capacitor.Cli.Tests.Unit.SessionStartMemory;

/// <summary>
/// Ungated, CI-safe coverage for the parts of the shared live-cert scaffold that do NOT need a live
/// harness process. The certs themselves are gated and therefore never exercised by CI, so without
/// these the scaffold would be entirely untested — a parser or cleanup bug would surface only during
/// a manual certification run, i.e. after spending a real model turn.
/// </summary>
public class MemoryIndexLiveCertHarnessTests {
    // ── command resolution ────────────────────────────────────────────────────
    //
    // The Unix-semantics cases are skipped on Windows rather than adapted: the branch under test is the
    // one guarded by `isWindows: false`, it asks the kernel `access(X_OK)`, and its probes need real
    // execute bits — none of which Windows can provide. Adapting them there would test a different
    // resolver. Only the isWindows: true passthrough runs everywhere, and it touches no file mode.
    // The bug these cover: `Process.Start` tries a separator-free filename against the WORKING
    // DIRECTORY before PATH, and this assembly's working directory is its own output folder — which
    // contains a `kcap` copied there by the Capacitor.Cli project reference. Every harness
    // `RunProcessAsync("kcap", …)` therefore ran the test build, while the cert's own `which kcap` line
    // reported the PATH build the hook would actually run. The version a cert records is the whole
    // point of recording it, so the two silently describing different binaries is the failure mode
    // that recording exists to prevent.

    [Test]
    public async Task A_bare_command_resolves_to_the_first_PATH_entry_that_has_it() {
        Skip.Unless(!OperatingSystem.IsWindows(), "Unix PATH semantics: needs real execute bits and access(X_OK).");

        using var probe = new PathProbe();

        var resolved = MemoryIndexLiveCertHarness.ResolveOnPath(
            probe.CommandName, $"{probe.EmptyDir}{Path.PathSeparator}{probe.BinDir}", isWindows: false);

        await Assert.That(resolved).IsEqualTo(probe.ExecutablePath);
    }

    /// <summary>An earlier PATH entry wins — resolution order is first-match, as the shell's is.</summary>
    [Test]
    public async Task Earlier_PATH_entries_win_over_later_ones() {
        Skip.Unless(!OperatingSystem.IsWindows(), "Unix PATH semantics: needs real execute bits and access(X_OK).");

        using var first  = new PathProbe();
        using var second = new PathProbe(first.CommandName);

        var resolved = MemoryIndexLiveCertHarness.ResolveOnPath(
            first.CommandName, $"{first.BinDir}{Path.PathSeparator}{second.BinDir}", isWindows: false);

        await Assert.That(resolved).IsEqualTo(first.ExecutablePath);
    }

    /// <summary>Existence is not the test a shell applies: a non-executable match is skipped and the
    /// walk continues, exactly as <c>which</c> and <c>execvp</c> do. Resolving on existence alone would
    /// stop here and hand back a file that cannot be launched — or, worse for this harness, disagree
    /// with the <c>which kcap</c> line recorded beside it.</summary>
    [Test]
    public async Task A_non_executable_match_is_skipped_for_a_later_executable_one() {
        Skip.Unless(!OperatingSystem.IsWindows(), "Unix PATH semantics: needs real execute bits and access(X_OK).");

        using var notExecutable = new PathProbe(executable: false);
        using var executable    = new PathProbe(notExecutable.CommandName);

        var resolved = MemoryIndexLiveCertHarness.ResolveOnPath(
            notExecutable.CommandName,
            $"{notExecutable.BinDir}{Path.PathSeparator}{executable.BinDir}",
            isWindows: false);

        await Assert.That(resolved).IsEqualTo(executable.ExecutablePath);
    }

    /// <summary>...and when the only match is non-executable there is nothing to resolve to, so this
    /// throws for the same reason an absent command does.</summary>
    [Test]
    public async Task A_non_executable_only_match_does_not_resolve() {
        Skip.Unless(!OperatingSystem.IsWindows(), "Unix PATH semantics: needs real execute bits and access(X_OK).");

        using var probe = new PathProbe(executable: false);

        await Assert.That(() => MemoryIndexLiveCertHarness.ResolveOnPath(probe.CommandName, probe.BinDir, isWindows: false))
            .Throws<FileNotFoundException>();
    }

    /// <summary>A name that is already a path is the caller's explicit choice; never rewritten.</summary>
    [Test]
    [Arguments("/usr/bin/env")]
    [Arguments("./local-thing")]
    public async Task An_explicit_path_is_passed_through_untouched(string fileName) {
        Skip.Unless(!OperatingSystem.IsWindows(), "Unix PATH semantics: needs real execute bits and access(X_OK).");

        using var probe = new PathProbe();

        await Assert.That(MemoryIndexLiveCertHarness.ResolveOnPath(fileName, probe.BinDir, isWindows: false))
            .IsEqualTo(fileName);
    }

    /// <summary>An unresolvable bare name must THROW, not pass through. Passing it through hands the
    /// problem to <c>Process.Start</c>, which consults the working directory first — so on the one
    /// command that matters it would silently run the `kcap` in this assembly's output folder rather
    /// than fail, reintroducing the wrong-binary bug on the path where resolution already failed.</summary>
    [Test]
    public async Task An_unresolvable_command_throws_rather_than_falling_back_to_the_working_directory() {
        Skip.Unless(!OperatingSystem.IsWindows(), "Unix PATH semantics: needs real execute bits and access(X_OK).");

        using var probe = new PathProbe();

        await Assert.That(() => MemoryIndexLiveCertHarness.ResolveOnPath("kcap-no-such-command", probe.BinDir, isWindows: false))
            .Throws<FileNotFoundException>();
    }

    /// <summary>Windows is deliberately left on the platform's own resolution: doing it correctly needs
    /// PATHEXT handling, and a half-right implementation would be worse than the documented status quo.
    /// These certs are gated and are not run there.</summary>
    [Test]
    public async Task Windows_resolution_is_deliberately_left_to_the_platform() {
        using var probe = new PathProbe();

        await Assert.That(MemoryIndexLiveCertHarness.ResolveOnPath(probe.CommandName, probe.BinDir, isWindows: true))
            .IsEqualTo(probe.CommandName);
    }

    /// <summary>A throwaway PATH entry holding one uniquely named file, plus an empty sibling directory
    /// to prove a miss is skipped rather than treated as a match. <c>executable</c> controls
    /// the execute bit, because "exists" and "is executable" are different questions and the resolver
    /// must answer the second one.</summary>
    sealed class PathProbe : IDisposable {
        readonly string _root;

        public PathProbe(string? commandName = null, bool executable = true) {
            _root       = Directory.CreateTempSubdirectory("kcap-path-probe-").FullName;
            BinDir      = Directory.CreateDirectory(Path.Combine(_root, "bin")).FullName;
            EmptyDir    = Directory.CreateDirectory(Path.Combine(_root, "empty")).FullName;
            CommandName = commandName ?? $"kcap-probe-{Guid.NewGuid():N}";

            ExecutablePath = Path.Combine(BinDir, CommandName);
            File.WriteAllText(ExecutablePath, "#!/bin/sh\nexit 0\n");

            // `File.SetUnixFileMode` THROWS on Windows, and the probe is constructed by the one test
            // that does run there (the isWindows: true passthrough), which never looks at the mode.
            if (!OperatingSystem.IsWindows()) {
                File.SetUnixFileMode(ExecutablePath, executable
                    ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    : UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }

        public string BinDir         { get; }
        public string EmptyDir       { get; }
        public string CommandName    { get; }
        public string ExecutablePath { get; }

        public void Dispose() {
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Test]
    public async Task Plain_text_output_is_returned_as_is() {
        await Assert.That(MemoryIndexLiveCertHarness.ExtractAssistantAnswer("  kcap-live-nonce-abc123  \n"))
            .IsEqualTo("kcap-live-nonce-abc123");
    }

    [Test]
    public async Task Single_json_object_with_a_result_field_extracts_the_text() {
        await Assert.That(MemoryIndexLiveCertHarness.ExtractAssistantAnswer(
            """{"type":"result","result":"kcap-live-nonce-abc123"}"""))
            .IsEqualTo("kcap-live-nonce-abc123");
    }

    [Test]
    public async Task Newline_delimited_json_stream_extracts_the_first_matching_text_field() {
        var stdout = """
                     {"type":"system","subtype":"init"}
                     {"type":"assistant","message":{"content":[{"type":"text","text":"kcap-live-nonce-abc123"}]}}
                     """;

        await Assert.That(MemoryIndexLiveCertHarness.ExtractAssistantAnswer(stdout))
            .IsEqualTo("kcap-live-nonce-abc123");
    }

    [Test]
    public async Task Empty_output_returns_empty() {
        await Assert.That(MemoryIndexLiveCertHarness.ExtractAssistantAnswer("   \n  ")).IsEqualTo("");
    }

    // A cert asserts Contains(nonce) / DoesNotContain(nonce) on this parser's output, so a parser that
    // silently dropped the answer would make the NEGATIVE control pass vacuously. Pin that plain
    // markdown prose (Kiro's `--format plain` shape) survives intact.
    [Test]
    public async Task Markdown_prose_containing_the_nonce_is_preserved() {
        var stdout = "> I found this in the team memory block:\n\nkcap-live-nonce-deadbeef\n";

        await Assert.That(MemoryIndexLiveCertHarness.ExtractAssistantAnswer(stdout))
            .Contains("kcap-live-nonce-deadbeef");
    }

    [Test]
    public async Task Leading_json_block_extraction_splits_on_an_lf_blank_line() {
        var stdout = "{\"active_profile\":\"default\"}\n\nPath: ~/.config/kcap/config.json\n";

        await Assert.That(MemoryIndexLiveCertHarness.ExtractLeadingJsonBlock(stdout))
            .IsEqualTo("{\"active_profile\":\"default\"}");
    }

    [Test]
    public async Task Leading_json_block_extraction_tolerates_a_crlf_blank_line() {
        var stdout = "{\"active_profile\":\"default\"}\r\n\r\nPath: C:\\Users\\me\\config.json\r\n";

        await Assert.That(MemoryIndexLiveCertHarness.ExtractLeadingJsonBlock(stdout))
            .IsEqualTo("{\"active_profile\":\"default\"}");
    }

    [Test]
    public async Task Each_nonce_is_unique_and_matches_the_shape_the_prompts_ask_for() {
        var a = MemoryIndexLiveCertHarness.NewNonce();
        var b = MemoryIndexLiveCertHarness.NewNonce();

        await Assert.That(a).IsNotEqualTo(b);
        await Assert.That(a).Matches("^kcap-live-nonce-[0-9a-f]{32}$");
        await Assert.That(MemoryIndexLiveCertHarness.PositivePrompt).Contains("kcap-live-nonce-");
    }

    // The negative control MUST ask the identical question, or a false positive could come from the
    // wording rather than from the index being absent.
    [Test]
    public async Task The_negative_control_asks_the_identical_question() {
        await Assert.That(MemoryIndexLiveCertHarness.NegativePrompt)
            .IsEqualTo(MemoryIndexLiveCertHarness.PositivePrompt);
    }

    [Test]
    public async Task A_cert_worktree_is_a_fresh_empty_directory() {
        var first  = MemoryIndexLiveCertHarness.NewCertWorktree("probe");
        var second = MemoryIndexLiveCertHarness.NewCertWorktree("probe");

        try {
            await Assert.That(first.FullName).IsNotEqualTo(second.FullName);
            await Assert.That(first.EnumerateFileSystemInfos()).IsEmpty();
        } finally {
            try { first.Delete(recursive: true); } catch { /* best-effort */ }
            try { second.Delete(recursive: true); } catch { /* best-effort */ }
        }
    }

    // 13 cert memories leaked into the live index because archive_memory was sent `memory_id` (what
    // save_memory RETURNS) instead of `id` (what archive_memory ACCEPTS), and the failure was
    // swallowed. A leaked nonce is not cosmetic — it enters the REAL injected index, so a later
    // positive cert can pass on a stale nonce rather than the one it just saved.
    [Test]
    public async Task An_explicit_ok_is_the_only_thing_counted_as_archived() {
        await Assert.That(MemoryIndexLiveCertHarness.ArchiveSucceeded(
            """{"jsonrpc":"2.0","id":2,"result":{"content":[{"type":"text","text":"{\"ok\":true}"}]}}"""))
            .IsTrue();
    }

    [Test]
    [Arguments("""{"jsonrpc":"2.0","id":2,"result":{"content":[{"type":"text","text":"Not logged in."}],"isError":true}}""")]
    [Arguments("""{"jsonrpc":"2.0","id":2,"result":{"content":[{"type":"text","text":"{\"ok\":false}"}]}}""")]
    [Arguments("""{"jsonrpc":"2.0","id":2,"result":{"content":[]}}""")]
    [Arguments("""{"jsonrpc":"2.0","id":2,"error":{"code":-32602,"message":"unknown argument memory_id"}}""")]
    [Arguments("not json at all")]
    public async Task Anything_short_of_an_explicit_ok_is_treated_as_a_leak(string frame) {
        await Assert.That(MemoryIndexLiveCertHarness.ArchiveSucceeded(frame)).IsFalse();
    }

    // Restoring the REAL profile flag is this file's highest-consequence action: a wrong value leaves a
    // developer's machine with memory injection in the wrong state, silently, for days. So the read
    // must never guess — an unreadable config has to be distinguishable from a genuinely unset flag,
    // because the two imply opposite restores.
    [Test]
    public async Task A_missing_active_profile_is_a_read_failure_not_an_absent_flag() {
        var source = await File.ReadAllTextAsync(HarnessSourcePath());

        var start = source.IndexOf("public static async Task<bool?> ReadDisableMemoryIndexAsync()", StringComparison.Ordinal);
        await Assert.That(start).IsGreaterThan(-1);

        var body = source.Substring(start, Math.Min(1600, source.Length - start));

        // Every failure branch throws; only the genuinely-unset case may return null.
        await Assert.That(body).Contains("throw new InvalidOperationException");
        await Assert.That(body).DoesNotContain("return null;");
    }

    // A discarded exit code here makes a negative control pass vacuously (injection was never actually
    // disabled) or leaves the real profile disabled after the run. The pre-existing Claude cert asserts
    // it; an earlier draft of this shared harness dropped that guard.
    [Test]
    public async Task Setting_the_real_flag_fails_loudly_rather_than_discarding_the_exit_code() {
        var source = await File.ReadAllTextAsync(HarnessSourcePath());

        var start = source.IndexOf("public static async Task SetDisableMemoryIndexAsync(bool value)", StringComparison.Ordinal);
        await Assert.That(start).IsGreaterThan(-1);

        var body = source.Substring(start, Math.Min(900, source.Length - start));

        await Assert.That(body).Contains("exitCode != 0");
        await Assert.That(body).Contains("throw new InvalidOperationException");
    }

    // A restore that silently no-ops is indistinguishable from success without a read-back.
    [Test]
    public async Task Restoring_the_real_flag_reads_it_back_to_confirm() {
        var source = await File.ReadAllTextAsync(HarnessSourcePath());

        var start = source.IndexOf("public static async Task RestoreDisableMemoryIndexAsync(bool? original)", StringComparison.Ordinal);
        await Assert.That(start).IsGreaterThan(-1);

        var body = source.Substring(start, Math.Min(900, source.Length - start));

        await Assert.That(body).Contains("ReadDisableMemoryIndexAsync");
        await Assert.That(body).Contains("readBack != target");
    }

    static string HarnessSourcePath() => Path.Combine(
        RepoRoot(), "test", "Capacitor.Cli.Tests.Unit", "SessionStartMemory", "MemoryIndexLiveCertHarness.cs");

    /// <summary>Walks up from this file's compile-time path to the repo root.</summary>
    static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "") {
        var dir = Path.GetDirectoryName(here);

        while (dir is not null && !File.Exists(Path.Combine(dir, "Capacitor.slnx")))
            dir = Path.GetDirectoryName(dir);

        return dir ?? throw new InvalidOperationException($"repo root not found from {here}");
    }

    // The other half of the leak, which no assertion on a response frame can catch: the ARGUMENT NAME.
    // save_memory returns `memory_id`; archive_memory accepts `id`. Sending the former archived nothing
    // and returned a frame that looked fine. Pinned at the source, since reaching it for real needs a
    // live authenticated server.
    [Test]
    public async Task Archive_is_called_with_id_not_the_memory_id_that_save_returns() {
        var source = await File.ReadAllTextAsync(Path.Combine(
            RepoRoot(), "test", "Capacitor.Cli.Tests.Unit", "SessionStartMemory",
            "MemoryIndexLiveCertHarness.cs"));

        var start = source.IndexOf("CallMemoryToolAsync(\"archive_memory\"", StringComparison.Ordinal);
        await Assert.That(start).IsGreaterThan(-1);

        var callSite = source.Substring(start, Math.Min(160, source.Length - start));

        await Assert.That(callSite).Contains("[\"id\"]");
        await Assert.That(callSite).DoesNotContain("memory_id");
    }

    [Test]
    public async Task A_timed_out_process_is_killed_rather_than_left_running() {
        var (fileName, args) = OperatingSystem.IsWindows()
            ? ("cmd.exe", (IReadOnlyList<string>) ["/c", "ping", "-n", "120", "127.0.0.1"])
            : ("sleep", (IReadOnlyList<string>) ["120"]);

        int? pid = null;
        MemoryIndexLiveCertHarness.OnProcessStarted = p => pid = p;

        try {
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                MemoryIndexLiveCertHarness.RunProcessAsync(
                    fileName, args, workingDirectory: null, timeout: TimeSpan.FromMilliseconds(200)));
        } finally {
            MemoryIndexLiveCertHarness.OnProcessStarted = null;
        }

        await Assert.That(pid).IsNotNull();

        var deadline   = DateTime.UtcNow.AddSeconds(5);
        var stillAlive = true;

        while (DateTime.UtcNow < deadline) {
            try {
                using var proc = System.Diagnostics.Process.GetProcessById(pid!.Value);
                if (proc.HasExited) { stillAlive = false; break; }
            } catch (ArgumentException) {
                stillAlive = false;
                break;
            }
            await Task.Delay(50);
        }

        await Assert.That(stillAlive).IsFalse();
    }

    // stdin is how Codex receives its prompt; if the harness failed to write-and-close it, `codex exec -`
    // would block forever and the cert would time out after spending nothing but wall-clock.
    [Test]
    public async Task Supplied_stdin_is_written_and_closed_so_a_reader_sees_eof() {
        var (fileName, args) = OperatingSystem.IsWindows()
            ? ("cmd.exe", (IReadOnlyList<string>) ["/c", "more"])
            : ("cat", (IReadOnlyList<string>) []);

        var (exitCode, stdout, _) = await MemoryIndexLiveCertHarness.RunProcessAsync(
            fileName, args, workingDirectory: null, timeout: TimeSpan.FromSeconds(20), stdin: "kcap-stdin-probe");

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(stdout).Contains("kcap-stdin-probe");
    }
}
