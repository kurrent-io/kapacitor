namespace Capacitor.Cli.Tests.Unit.SessionStartMemory;

/// <summary>
/// Ungated, CI-safe coverage for the parts of the shared live-cert scaffold that do NOT need a live
/// harness process. The certs themselves are gated and therefore never exercised by CI, so without
/// these the scaffold would be entirely untested — a parser or cleanup bug would surface only during
/// a manual certification run, i.e. after spending a real model turn.
/// </summary>
public class MemoryIndexLiveCertHarnessTests {
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
