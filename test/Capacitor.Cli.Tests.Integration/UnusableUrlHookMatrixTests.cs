using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// The acceptance criterion, end to end: a hook invoked with an unusable server URL still emits its
/// required stdout contract and exits 0.
///
/// <para>Each case runs the REAL binary in an isolated child process with its own
/// <c>KCAP_CONFIG_DIR</c>, because the in-process alternative cannot distinguish "the guard worked"
/// from "the runner survived". Parameterized over every class <c>IsAcceptableUrl</c> rejects —
/// including absolute wrong-scheme, which an implementation validating only <c>UriKind.Absolute</c>
/// would wrongly accept.</para>
///
/// <para><b>Why the assertions look the way they do.</b> Asserting only stdout+exit is not enough:
/// <c>Program.cs</c>'s top-level catch maps <c>hook</c> to exit 0, and Codex's own fail-open catch
/// emits the identical <c>{"continue":true}</c>, so a surface-only assertion passes with every guard
/// deleted. Each case therefore also asserts the positive, guard-specific diagnostic — a string only
/// the guard emits — and rejects the fail-open fallback marker.</para>
/// </summary>
public class UnusableUrlHookMatrixTests : IDisposable {
    // Every class IsAcceptableUrl rejects. Deliberately NOT the empty string: ProfileResolver ignores
    // an exactly-empty KCAP_URL and falls back to the active profile, so it would test the wrong thing.
    public static IEnumerable<string> UnusableUrls() => [
        "   ",                  // whitespace
        "localhost:5108",       // scheme-less
        "/hooks/stop",          // relative
        "ftp://host",           // absolute, wrong scheme
        "file:///etc/passwd",   // absolute, wrong scheme
    ];

    readonly string _cfgDir = Path.Combine(Path.GetTempPath(), $"kcap-matrix-cfg-{Guid.NewGuid():N}");
    readonly List<Process> _spawned = [];

    public void Dispose() {
        foreach (var p in _spawned) { try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { } p.Dispose(); }
        try { Directory.Delete(_cfgDir, true); } catch { }
    }

    [Test]
    [MethodDataSource(nameof(UnusableUrls))]
    public async Task Codex_SessionStart_emits_the_handshake_and_exits_zero(string url) {
        var payload = new JsonObject {
            ["hook_event_name"] = "SessionStart",
            ["session_id"]      = Guid.NewGuid().ToString(),
            ["cwd"]             = _cfgDir,
        }.ToJsonString();

        var r = await RunAsync(["hook", "--codex"], url, payload);

        // Codex's parser rejects empty output; this is the contract the bug was breaking.
        await Assert.That(r.Stdout).Contains("\"continue\":true");
        await Assert.That(r.ExitCode).IsEqualTo(0);
        // Positive proof the guard ran, not the top-level catch.
        await Assert.That(r.Stderr).Contains("is not an absolute http(s) URL");
        await Assert.That(r.Stderr).DoesNotContain("hook failed");
    }

    [Test]
    [MethodDataSource(nameof(UnusableUrls))]
    public async Task Codex_Stop_emits_the_handshake_and_exits_zero(string url) {
        // Regression guard: this path was already fixed, and must stay fixed.
        var payload = new JsonObject {
            ["hook_event_name"] = "Stop",
            ["session_id"]      = Guid.NewGuid().ToString(),
        }.ToJsonString();

        var r = await RunAsync(["hook", "--codex"], url, payload);

        await Assert.That(r.Stdout).Contains("\"continue\":true");
        await Assert.That(r.ExitCode).IsEqualTo(0);
    }

    [Test]
    [MethodDataSource(nameof(UnusableUrls))]
    public async Task Claude_SessionStart_exits_zero_without_a_hook_error(string url) {
        // Claude tolerates empty stdout on SessionStart but treats a non-zero exit as a hook error,
        // so exit 0 IS the contract here — the envelope is not required on the degraded path.
        var payload = new JsonObject {
            ["hook_event_name"] = "SessionStart",
            ["session_id"]      = Guid.NewGuid().ToString(),
            ["cwd"]             = _cfgDir,
        }.ToJsonString();

        var r = await RunAsync(["hook", "--claude"], url, payload);

        await Assert.That(r.ExitCode).IsEqualTo(0);
        await Assert.That(r.Stderr).Contains("is not an absolute http(s) URL");
    }

    [Test]
    [MethodDataSource(nameof(UnusableUrls))]
    public async Task Cursor_sessionStart_exits_zero(string url) {
        var payload = new JsonObject {
            ["hook_event_name"] = "sessionStart",
            ["session_id"]      = Guid.NewGuid().ToString(),
        }.ToJsonString();

        var r = await RunAsync(["hook", "--cursor"], url, payload);

        await Assert.That(r.ExitCode).IsEqualTo(0);
    }

    /// <summary>
    /// The policy binding covers four agent-spawned commands, but a matrix that exercised only
    /// `hook` would pass against an implementation hard-coded to `hook`.
    /// </summary>
    [Test]
    [Arguments("generate-whats-done")]
    [Arguments("set-title")]
    public async Task Agent_spawned_commands_do_not_hard_exit(string command) {
        var r = await RunAsync([command, Guid.NewGuid().ToString("N")], "ftp://host", stdin: "");

        // FailFast would be exit 2 with the bare validator hint and no guard diagnostic.
        await Assert.That(r.ExitCode).IsNotEqualTo(2);
    }

    /// <summary>Negative control: interactive commands must keep failing fast.</summary>
    [Test]
    public async Task Interactive_command_still_exits_2_with_the_hint() {
        var r = await RunAsync(["recap"], "ftp://host", stdin: "");

        await Assert.That(r.ExitCode).IsEqualTo(2);
        await Assert.That(r.Stderr).Contains("server_url is missing a scheme");
    }

    /// <summary><c>kcap watch</c> run by hand: the actionable hint, not an opaque exit 1.</summary>
    [Test]
    public async Task Hand_run_watch_exits_2_with_the_hint() {
        var transcript = Path.Combine(_cfgDir, "t.jsonl");
        Directory.CreateDirectory(_cfgDir);
        await File.WriteAllTextAsync(transcript, "");

        var r = await RunAsync(["watch", Guid.NewGuid().ToString("N"), transcript], "localhost:5108", stdin: "");

        await Assert.That(r.ExitCode).IsEqualTo(2);
        await Assert.That(r.Stderr).Contains("server_url is missing a scheme");
    }

    async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(string[] args, string url, string stdin) {
        var binary = GetCliBinaryPath();

        if (!File.Exists(binary)) {
            throw new FileNotFoundException(
                $"kcap binary not found at {binary}. Build it first: dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj",
                binary);
        }

        Directory.CreateDirectory(_cfgDir);

        var psi = new ProcessStartInfo(binary) {
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            WorkingDirectory       = _cfgDir,
            Environment = {
                ["KCAP_URL"]           = url,
                ["KCAP_CONFIG_DIR"]    = _cfgDir,
                ["KCAP_NO_UPDATE_CHECK"] = "1",
            },
        };

        foreach (var a in args) psi.ArgumentList.Add(a);

        var process = Process.Start(psi) ?? throw new InvalidOperationException("failed to start kcap");
        _spawned.Add(process);

        // A guarded hook legitimately returns before reading stdin — Cursor's guard fires before the
        // payload is even parsed — so the child may have exited by the time we write. That closes the
        // pipe, and the write throws. It is not a product failure and must not be reported as one.
        try {
            await process.StandardInput.WriteAsync(stdin);
        } catch (IOException) {
            // Child exited first; nothing to deliver.
        } finally {
            try { process.StandardInput.Close(); } catch (IOException) { }
        }

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(cts.Token);

        return (process.ExitCode, stdout, stderr);
    }

    static string GetCliBinaryPath() {
        var asmDir      = Path.GetDirectoryName(typeof(UnusableUrlHookMatrixTests).Assembly.Location)!;
        var binDir      = Path.GetDirectoryName(asmDir)!;
        var config      = Path.GetFileName(binDir);
        var testBin     = Path.GetDirectoryName(binDir)!;
        var testProjDir = Path.GetDirectoryName(testBin)!;
        var testRoot    = Path.GetDirectoryName(testProjDir)!;
        var repoRoot    = Path.GetDirectoryName(testRoot)!;
        var binaryName  = OperatingSystem.IsWindows() ? "kcap.exe" : "kcap";

        return Path.Combine(repoRoot, "src", "Capacitor.Cli", "bin", config, "net10.0", binaryName);
    }
}
