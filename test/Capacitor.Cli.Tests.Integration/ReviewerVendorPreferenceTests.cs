using System.Diagnostics;
using System.Text.Json.Nodes;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// The saved reviewer-vendor preference against the REAL binary and a REAL config file, in an
/// isolated <c>KCAP_CONFIG_DIR</c>. This is the only place the production binding is exercised: the
/// unit tests inject the preference reader (they must — reading for real would consult the
/// developer's own config), so nothing there can catch a lookup that reads the wrong profile, or a
/// value cached at process start.
///
/// <para>Deliberately NO <c>KCAP_URL</c>: the server URL comes from the profile, so the CLI takes
/// its normal profile-resolution path and holds a resolved profile — which is exactly the shape in
/// which a start-time snapshot would go stale, and therefore the shape the freshness test needs.</para>
/// </summary>
public class ReviewerVendorPreferenceTests : IDisposable {
    readonly WireMockServer _server           = WireMockServer.Start();
    readonly string         _cfgDir           = Path.Combine(Path.GetTempPath(), $"kcap-pref-cfg-{Guid.NewGuid():N}");
    readonly string         _cwdDir           = Path.Combine(Path.GetTempPath(), $"kcap-pref-cwd-{Guid.NewGuid():N}");
    readonly List<Process>  _spawnedProcesses = [];

    public ReviewerVendorPreferenceTests() {
        Directory.CreateDirectory(_cfgDir);
        Directory.CreateDirectory(_cwdDir);
    }

    public void Dispose() {
        foreach (var p in _spawnedProcesses) {
            try {
                if (!p.HasExited) p.Kill(entireProcessTree: true);
                p.Dispose();
            } catch {
                // best-effort cleanup
            }
        }

        _server.Stop();
        try { Directory.Delete(_cfgDir, recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(_cwdDir, recursive: true); } catch { /* best effort */ }
    }

    string ConfigPath => Path.Combine(_cfgDir, "config.json");

    /// <summary>Seeds the isolated profile config THROUGH THE CLI rather than by writing JSON: the
    /// point of this fixture is the real on-disk shape, and a hand-written config is not it (a
    /// minimal one omitting profile_bindings currently aborts every command — see the report).
    /// The server URL lives in the PROFILE, not KCAP_URL, so the CLI resolves a profile normally.</summary>
    async Task SeedConfigAsync(string? reviewerVendor) {
        var url = await RunAsync($"config set server_url {_server.Url} --no-probe");
        if (url.ExitCode != 0)
            throw new InvalidOperationException($"seeding server_url failed: {url.Stderr}");

        if (reviewerVendor is null) return;

        var vendor = await RunAsync($"config set flows.reviewer_vendor {reviewerVendor}");
        if (vendor.ExitCode != 0)
            throw new InvalidOperationException($"seeding flows.reviewer_vendor failed: {vendor.Stderr}");
    }

    static string GetCliBinaryPath() {
        var asmDir      = Path.GetDirectoryName(typeof(ReviewerVendorPreferenceTests).Assembly.Location)!;
        var binDir      = Path.GetDirectoryName(asmDir)!;
        var config      = Path.GetFileName(binDir);
        var testBin     = Path.GetDirectoryName(binDir)!;
        var testProjDir = Path.GetDirectoryName(testBin)!;
        var testRoot    = Path.GetDirectoryName(testProjDir)!;
        var repoRoot    = Path.GetDirectoryName(testRoot)!;
        var binaryName  = OperatingSystem.IsWindows() ? "kcap.exe" : "kcap";

        return Path.Combine(repoRoot, "src", "Capacitor.Cli", "bin", config, "net10.0", binaryName);
    }

    ProcessStartInfo BaseStartInfo(string arguments) {
        var binary = GetCliBinaryPath();

        if (!File.Exists(binary))
            throw new FileNotFoundException(
                $"kcap binary not found at {binary}. Build it first: dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj",
                binary);

        var psi = new ProcessStartInfo(binary, arguments) {
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            WorkingDirectory       = _cwdDir,
            Environment            = { ["KCAP_CONFIG_DIR"] = _cfgDir }
        };

        // The flows lane must resolve its server from the PROFILE, so no URL override may leak in
        // from the developer's or CI's environment.
        psi.Environment.Remove("KCAP_URL");
        psi.Environment.Remove("KCAP_PROFILE");
        psi.Environment.Remove("CLAUDE_CODE_SESSION_ID");
        psi.Environment.Remove("CLAUDE_PROJECT_DIR");
        psi.Environment.Remove("CODEX_THREAD_ID");

        return psi;
    }

    Process SpawnMcpFlows() {
        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"provider":"None"}"""));

        var process = Process.Start(BaseStartInfo("mcp flows"))
            ?? throw new InvalidOperationException("Failed to start kcap mcp flows");

        _spawnedProcesses.Add(process);

        return process;
    }

    async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(string arguments) {
        using var process = Process.Start(BaseStartInfo(arguments))
            ?? throw new InvalidOperationException($"Failed to start kcap {arguments}");

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(cts.Token);

        return (process.ExitCode, stdout, stderr);
    }

    static async Task<JsonObject> SendRequest(Process proc, JsonObject request) {
        await proc.StandardInput.WriteLineAsync(request.ToJsonString());
        await proc.StandardInput.FlushAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var       line = await proc.StandardOutput.ReadLineAsync(cts.Token);

        if (line is null) {
            var stderr = await proc.StandardError.ReadToEndAsync();
            throw new InvalidOperationException($"MCP server closed stdout without responding. Stderr: {stderr}");
        }

        return JsonNode.Parse(line)?.AsObject()
            ?? throw new InvalidOperationException($"Could not parse response as JSON object: {line}");
    }

    static JsonObject StartRequest(int id) => new() {
        ["jsonrpc"] = "2.0",
        ["id"]      = id,
        ["method"]  = "tools/call",
        ["params"]  = new JsonObject {
            ["name"]      = "start_review_flow",
            ["arguments"] = new JsonObject {
                ["kind"]         = "code-review",
                ["target_kind"]  = "pr",
                ["target_ref"]   = "123",
                ["target_title"] = "some PR",
                ["context"]      = "some context"
            }
        }
    };

    const string StartV2 = "/api/flows/review/start/v2";

    const string VendorRequired =
        """{"error":"reviewer_vendor_required","message":"no reviewer vendor was requested and the definition names none"}""";

    static string Text(JsonObject response) =>
        response["result"]!["content"]![0]!["text"]!.GetValue<string>();

    IReadOnlyList<string> StartBodies() =>
        _server.FindLogEntries(Request.Create().WithPath(StartV2).UsingPost())
               .Select(e => e.RequestMessage.Body ?? "")
               .ToList();

    // ── kcap mcp flows: the production preference binding ────────────────────────────────────

    /// <summary>The whole feature through the real binary: a vendor-less start is refused, the CLI
    /// reads the saved preference from the real config file, and re-sends naming it.</summary>
    [Test]
    public async Task A_saved_preference_is_read_from_disk_and_applied_to_the_retry() {
        await SeedConfigAsync("codex");

        _server.Given(Request.Create().WithPath(StartV2).UsingPost())
               .InScenario("pref").WillSetStateTo("retry")
               .RespondWith(Response.Create().WithStatusCode(400).WithBody(VendorRequired));
        _server.Given(Request.Create().WithPath(StartV2).UsingPost())
               .InScenario("pref").WhenStateIs("retry")
               .RespondWith(Response.Create().WithStatusCode(200).WithBody(
                   """{"flow_run_id":"f1","status":"running","round_id":null,"round_number":null,"applied_reviewer_vendor":"codex"}"""));

        using var proc = SpawnMcpFlows();

        var response = await SendRequest(proc, StartRequest(1));

        await Assert.That(response["result"]!["isError"]?.GetValue<bool>()).IsNotEqualTo(true);
        await Assert.That(Text(response)).Contains("applied from your saved preference");

        var bodies = StartBodies();
        await Assert.That(bodies.Count).IsEqualTo(2);
        await Assert.That(JsonNode.Parse(bodies[0])!["vendor"]).IsNull();
        await Assert.That(JsonNode.Parse(bodies[1])!["vendor"]!.GetValue<string>()).IsEqualTo("codex");
    }

    /// <summary>Freshness, which is the whole point of the ask-once loop: this process is long-lived
    /// and the `kcap config set` its own guidance asks for happens in ANOTHER process, so the next
    /// start must see the new value. Against a profile deserialized at startup the second start here
    /// re-reads nothing and asks again — forever, for the life of the session.</summary>
    [Test]
    public async Task A_preference_saved_after_startup_is_seen_by_the_next_start() {
        await SeedConfigAsync(null);

        _server.Given(Request.Create().WithPath(StartV2).UsingPost())
               .InScenario("fresh").WillSetStateTo("second-start")
               .RespondWith(Response.Create().WithStatusCode(400).WithBody(VendorRequired));
        _server.Given(Request.Create().WithPath(StartV2).UsingPost())
               .InScenario("fresh").WhenStateIs("second-start").WillSetStateTo("accept")
               .RespondWith(Response.Create().WithStatusCode(400).WithBody(VendorRequired));
        _server.Given(Request.Create().WithPath(StartV2).UsingPost())
               .InScenario("fresh").WhenStateIs("accept")
               .RespondWith(Response.Create().WithStatusCode(200).WithBody(
                   """{"flow_run_id":"f1","status":"running","round_id":null,"round_number":null,"applied_reviewer_vendor":"codex"}"""));

        using var proc = SpawnMcpFlows();

        // 1. Nothing saved: the refusal plus the ask-and-save guidance, naming the profile it read.
        var first = await SendRequest(proc, StartRequest(1));
        await Assert.That(first["result"]!["isError"]!.GetValue<bool>()).IsTrue();
        await Assert.That(Text(first)).Contains("No saved reviewer-vendor preference");
        await Assert.That(Text(first)).Contains("(profile: default)");

        // 2. The user does what the guidance said — in a different process, mid-session.
        var saved = await RunAsync("config set flows.reviewer_vendor codex");
        await Assert.That(saved.ExitCode).IsEqualTo(0);

        // 3. The SAME long-lived server picks it up on the next start.
        var second = await SendRequest(proc, StartRequest(2));
        await Assert.That(second["result"]!["isError"]?.GetValue<bool>()).IsNotEqualTo(true);
        await Assert.That(Text(second)).Contains("applied from your saved preference");

        var bodies = StartBodies();
        await Assert.That(bodies.Count).IsEqualTo(3);
        await Assert.That(JsonNode.Parse(bodies[2])!["vendor"]!.GetValue<string>()).IsEqualTo("codex");
    }

    // ── kcap config set flows.reviewer_vendor ────────────────────────────────────────────────

    /// <summary>The unknown-vendor warning is ADVISORY: the server owns the authoritative vendor
    /// list, so an unrecognized token must still be saved and must still exit 0 — otherwise the
    /// first user of a vendor newer than their CLI cannot configure it at all. The warning goes to
    /// stderr, names the tokens this build knows, and follows a completed write.</summary>
    [Test]
    public async Task Config_set_warns_about_an_unknown_vendor_without_failing_or_skipping_the_write() {
        await SeedConfigAsync(null);

        var (exitCode, stdout, stderr) = await RunAsync("config set flows.reviewer_vendor kodex");

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(stdout).Contains("Set flows.reviewer_vendor = kodex");
        await Assert.That(stderr).Contains("Warning:");
        await Assert.That(stderr).Contains("'kodex' is not a vendor this kcap version knows");
        // The tokens the user can choose from, so the warning is actionable on its own.
        await Assert.That(stderr).Contains(
            "claude, codex, copilot, cursor, gemini, kiro, opencode, pi, antigravity");
        await Assert.That(stderr).Contains("the server has the authoritative list");

        // Warned, not refused: the value really is on disk.
        var written = JsonNode.Parse(await File.ReadAllTextAsync(ConfigPath))!;
        await Assert.That(written["profiles"]!["default"]!["flows"]!["reviewer_vendor"]!.GetValue<string>())
            .IsEqualTo("kodex");
    }

    /// <summary>A known vendor is stored canonically and echoed as STORED — confirming the typed
    /// spelling back would describe a value that is not what was saved.</summary>
    [Test]
    public async Task Config_set_echoes_the_normalized_vendor_and_does_not_warn() {
        await SeedConfigAsync(null);

        var (exitCode, stdout, stderr) = await RunAsync("config set flows.reviewer_vendor   CoDeX  ");

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(stdout).Contains("Set flows.reviewer_vendor = codex");
        await Assert.That(stdout).DoesNotContain("CoDeX");
        await Assert.That(stderr).DoesNotContain("is not a vendor this kcap version knows");

        var written = JsonNode.Parse(await File.ReadAllTextAsync(ConfigPath))!;
        await Assert.That(written["profiles"]!["default"]!["flows"]!["reviewer_vendor"]!.GetValue<string>())
            .IsEqualTo("codex");
    }
}
