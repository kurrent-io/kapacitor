using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Mcp;
using Capacitor.Cli.Core.Pi;
using Tomlyn;
using Tomlyn.Model;

namespace Capacitor.Cli.Tests.Unit.Mcp;

/// <summary>
/// Driver-schema conformance: every supported coding harness must reach the SAME vendor-capable
/// <c>kcap-flows</c> tool schema, so reviewer choice is a property of the request and not of whichever
/// harness happens to be driving.
///
/// <para><b>Why this needs its own suite.</b> The registration path is not one path. Six harnesses
/// (Cursor, Copilot, Gemini, Kiro, OpenCode, Antigravity) converge on one writer and differ only by a
/// <see cref="McpConfigShape"/>; Codex writes TOML through a separate engine with its own ownership
/// ledger; Claude Code loads a hand-maintained static <c>kcap/.mcp.json</c>; and Pi gets a hard-coded
/// server list inside an embedded TypeScript bridge. Four mechanisms, one contract. The existing
/// per-harness tests each assert <c>Contains("kcap-flows")</c> in isolation — that a server by that
/// name was written, not that it resolves to the same executable and therefore the same schema.</para>
///
/// <para><b>The failure this prevents</b> is a driver that appears to support named reviewer intent
/// but cannot express it: a harness whose registration drifts to a different command, or a tool that
/// quietly gains or loses <c>vendor</c>, leaves a caller believing it named a reviewer when it sent
/// nothing. A stale-schema driver must never be able to claim it launched the named vendor.</para>
/// </summary>
public class FlowsDriverSchemaConformanceTests {
    // ── the canonical descriptor ──────────────────────────────────────────────────────────────

    static McpTool Tool(string name) =>
        McpFlowsServer.BuildToolsList().Single(t => t.Name == name);

    /// <summary>The two START tools — the only ones that may route a vendor, because they are the
    /// only ones that select a reviewer. Everything else addresses a run that already has one.</summary>
    public static IEnumerable<Func<string>> StartTools() { yield return () => "start_review_flow"; yield return () => "start_flow"; }

    /// <summary>Every tool that operates on an EXISTING run. A vendor here would be either ignored
    /// (misleading) or a mid-run vendor switch (incoherent) — the applied vendor is pinned at start.</summary>
    public static IEnumerable<Func<string>> FollowUpTools() {
        yield return () => "submit_review_round";
        yield return () => "get_review_flow_status";
        yield return () => "close_review_flow";
        yield return () => "send_to_participant";
        yield return () => "get_flow_status";
        yield return () => "close_flow";
    }

    [Test]
    [MethodDataSource(nameof(StartTools))]
    public async Task A_start_tool_declares_vendor_as_an_optional_string(string toolName) {
        var tool = Tool(toolName);

        await Assert.That(tool.InputSchema.Properties.ContainsKey("vendor")).IsTrue()
            .Because($"{toolName} must be able to name a reviewer vendor");
        await Assert.That(tool.InputSchema.Properties["vendor"].Type).IsEqualTo("string");
        // Optional, not required: omitting it is how a caller asks for the server's default, which is
        // a legitimate and common request. Making it required would break every existing caller.
        await Assert.That(tool.InputSchema.Required).DoesNotContain("vendor");
    }

    [Test]
    [MethodDataSource(nameof(FollowUpTools))]
    public async Task A_follow_up_tool_does_not_accept_vendor_or_model(string toolName) {
        var props = Tool(toolName).InputSchema.Properties;

        await Assert.That(props.ContainsKey("vendor")).IsFalse()
            .Because($"{toolName} addresses an existing run whose vendor was pinned at start");
        await Assert.That(props.ContainsKey("model")).IsFalse();
    }

    // The description is the whole mechanism by which a driver LLM learns to pass the parameter.
    // A correct schema with a description that never mentions naming a reviewer produces a driver
    // that silently takes the default — which is the exact failure this contract exists to prevent,
    // and one no structural assertion would catch.
    [Test]
    [MethodDataSource(nameof(StartTools))]
    public async Task The_vendor_description_tells_the_driver_that_named_intent_must_pass_it(string toolName) {
        var description = Tool(toolName).InputSchema.Properties["vendor"].Description;

        // It must say what omitting it does (either tool's wording -- "Omit to..." or "when omitted")...
        await Assert.That(description.Contains("omit", StringComparison.OrdinalIgnoreCase)).IsTrue();
        // ...that the value is a canonical lowercase token, not a display name the driver invents...
        await Assert.That(description.Contains("lowercase", StringComparison.OrdinalIgnoreCase)).IsTrue();
        // ...and that there is no silent fallback, so a driver cannot treat it as a hint.
        await Assert.That(description.Contains("no silent fallback", StringComparison.OrdinalIgnoreCase)
                       || description.Contains("there is no silent fallback", StringComparison.OrdinalIgnoreCase)).IsTrue();
    }

    // `model` is meaningless without `vendor` (there is no vendor->model table anywhere), and the
    // schema cannot express that dependency — so the description has to, or a driver will send a
    // model alone and get a local rejection it was never warned about.
    [Test]
    [MethodDataSource(nameof(StartTools))]
    public async Task The_model_description_states_its_dependency_on_vendor(string toolName) {
        var props = Tool(toolName).InputSchema.Properties;

        await Assert.That(props.ContainsKey("model")).IsTrue();
        await Assert.That(props["model"].Description.Contains("requires 'vendor'", StringComparison.OrdinalIgnoreCase)).IsTrue()
            .Because("the schema cannot express the dependency, so the description must");
    }

    // Status output has to be able to name who actually ran. Reviewer self-identification in prose is
    // explicitly not acceptable evidence, so the vendor must be legible from structured status.
    [Test]
    public async Task The_status_tools_can_surface_the_applied_participant_vendor() {
        const string statusJson = """
            {"flow_run_id":"f1","definition_id":"spec-review","status":"running",
             "requested_reviewer_vendor":"claude","applied_reviewer_vendor":"claude",
             "reviewer_vendor_source":"explicit","applied_reviewer_model":"sonnet"}
            """;

        var text = McpFlowsServer.FormatStatusResponse(statusJson);

        await Assert.That(text).Contains("claude");
        await Assert.That(text).Contains("sonnet");
    }

    // ── every driver projection reaches the same server ───────────────────────────────────────

    /// <summary>One harness's registration, reduced to the only thing that determines which schema it
    /// gets: the command and arguments its <c>kcap-flows</c> entry resolves to.</summary>
    public sealed record Projection(string Harness, string Command, string[] Args);

    /// <summary>A scratch dir under the test assembly's own output rather than the system temp root.
    /// On macOS <c>/var</c> is a symlink, and <c>CodexConfigToml</c>'s path guard rejects any symlinked
    /// component — so a temp-rooted Codex registration silently returns <c>Failed</c> and writes
    /// nothing. (The pre-existing <c>CodexConfigTomlTests</c> hit the same wall on macOS.) The
    /// assembly output directory is a real path on every platform.</summary>
    static DirectoryInfo Scratch(string prefix) =>
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory,
            "conformance-scratch", prefix + Guid.NewGuid().ToString("N")[..8]));

    static string RepoKcapDir() {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (; d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "kcap", ".mcp.json")))
                return Path.Combine(d.FullName, "kcap");
        throw new DirectoryNotFoundException("kcap/ not found above the test base dir");
    }

    /// <summary>Registers through the REAL writer into a temp file, then reads the entry back. Going
    /// through the writer rather than asserting on the descriptor is the point: a shape that mangles
    /// the command (an argv array, a type field, a different block key) is exactly the drift that
    /// would give one harness a different server.</summary>
    static Projection ViaJsonWriter(string harness, McpConfigShape shape) {
        var dir  = Scratch("json-");
        var path = Path.Combine(dir.FullName, "config.json");
        try {
            var change = JsonMcpConfigWriter.Register(path, KcapMcpServers.ForCursor, shape,
                cwd: "/repo", marker: new McpMarker(harness));
            if (change == JsonMcpConfigWriter.Change.Failed)
                throw new InvalidOperationException($"{harness}: writer failed");

            var root  = (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
            var entry = (JsonObject)root[shape.BlockKey]!["kcap-flows"]!;

            // OpenCode folds the command and args into one argv array; everyone else splits them.
            if (shape.CommandAsArgvArray) {
                var argv = entry["command"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
                return new(harness, argv[0], argv[1..]);
            }

            return new(harness,
                entry["command"]!.GetValue<string>(),
                [.. entry["args"]!.AsArray().Select(n => n!.GetValue<string>())]);
        } finally {
            dir.Delete(recursive: true);
        }
    }

    static Projection FromStaticJson(string harness, string file) {
        var root  = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(RepoKcapDir(), file)))!;
        var entry = (JsonObject)root["mcpServers"]!["kcap-flows"]!;
        return new(harness,
            entry["command"]!.GetValue<string>(),
            [.. entry["args"]!.AsArray().Select(n => n!.GetValue<string>())]);
    }

    static Projection FromCodexToml() {
        var dir  = Scratch("codex-");
        var path = Path.Combine(dir.FullName, "config.toml");
        try {
            var change = CodexConfigToml.RegisterKcapMcpServers(path);
            if (!File.Exists(path)) throw new InvalidOperationException($"codex register -> {change}, no file at {path}");
            var toml = File.ReadAllText(path);

            // Parsed shallowly on purpose: the assertion is which executable Codex will launch, and
            // CodexConfigTomlTests already covers the TOML structure in depth.
            var model = TomlSerializer.Deserialize<TomlTable>(toml)!;
            var table = (TomlTable)((TomlTable)model["mcp_servers"]!)["kcap-flows"]!;
            return new("Codex",
                (string)table["command"]!,
                [.. ((TomlArray)table["args"]!).Select(a => (string)a!)]);
        } finally {
            dir.Delete(recursive: true);
        }
    }

    public static IEnumerable<Func<Projection>> DriverProjections() {
        // The six harnesses that share the JSON writer, each through its own shape.
        yield return () => ViaJsonWriter("Cursor",      McpConfigShape.Standard);
        yield return () => ViaJsonWriter("Kiro",        McpConfigShape.Standard);
        yield return () => ViaJsonWriter("Antigravity", McpConfigShape.Standard);
        yield return () => ViaJsonWriter("Gemini",      McpConfigShape.Gemini);
        yield return () => ViaJsonWriter("Copilot",     McpConfigShape.Copilot);
        yield return () => ViaJsonWriter("OpenCode",    McpConfigShape.OpenCode);
        // The three that do not.
        yield return () => FromCodexToml();
        yield return () => FromStaticJson("Claude Code",  ".mcp.json");
        yield return () => FromStaticJson("Codex plugin", ".codex-mcp.json");
    }

    [Test]
    [MethodDataSource(nameof(DriverProjections))]
    public async Task Every_driver_projection_launches_the_same_flows_server(Projection p) {
        await Assert.That(p.Command).IsEqualTo(KcapMcpServers.Command)
            .Because($"{p.Harness} must launch the same executable as every other driver");
        await Assert.That(p.Args).IsEquivalentTo(new[] { "mcp", "flows" })
            .Because($"{p.Harness} must reach the same subcommand, and therefore the same tool schema");
    }

    // Pi is the outlier worth its own assertion: it does not write an MCP config at all. It emits a
    // TypeScript bridge that discovers tools over `tools/list` at runtime and re-exposes them, so its
    // schema is whatever the server reports — but only for the servers named in a hard-coded literal
    // inside that blob. If `flows` were dropped from that literal, Pi would silently lose the ability
    // to start a flow while every other test here still passed.
    [Test]
    public async Task The_pi_bridge_still_lists_the_flows_server() {
        var dir = Scratch("pi-");
        try {
            var extension = Path.Combine(dir.FullName, "kcap-mcp.ts");
            PiMcpExtensionInstaller.Install(extension);
            var ts = File.ReadAllText(extension);

            await Assert.That(ts).Contains("\"flows\"")
                .Because("Pi resolves its servers from a literal, so dropping flows is silent");
        } finally {
            dir.Delete(recursive: true);
        }
    }

    // ── drift tripwires ───────────────────────────────────────────────────────────────────────

    // Two independent copies of the kcap server list exist: KcapMcpServers.All (what gets registered
    // with harnesses) and KcapMcpRegistry (what a flow definition's mcp: allowlist resolves against,
    // and the recursion guard's authority). Nothing keeps them in sync. A server added to one and not
    // the other is either unregistered-but-allowlistable or registered-but-unresolvable, and both
    // fail far from the edit.
    [Test]
    public async Task The_two_server_lists_agree_on_flows_arguments() {
        var canonical = KcapMcpServers.All.Single(s => s.Name == "kcap-flows");
        var registry  = KcapMcpRegistry.Resolve("kcap-flows");

        await Assert.That(registry).IsNotNull();
        await Assert.That(registry!.Args).IsEquivalentTo(canonical.Args);
        // And the recursion guard still knows flows starts flows — a hosted reviewer must never
        // receive it.
        await Assert.That(registry.StartsFlows).IsTrue();
    }

    [Test]
    public async Task Every_canonical_server_resolves_in_the_flow_allowlist_registry() {
        foreach (var s in KcapMcpServers.All)
            await Assert.That(KcapMcpRegistry.Resolve(s.Name)).IsNotNull()
                .Because($"{s.Name} is registered with harnesses but unresolvable as an allowlist entry");
    }

    // The projection table above is hand-written, because no enumeration of supported harnesses
    // exists in production code — the list is spread across at least four separate string arrays.
    // Without this, adding a tenth harness would leave the conformance suite quietly passing on nine.
    // Pinning against the flag arrays makes the omission fail here instead.
    [Test]
    public async Task The_projection_table_covers_every_harness_the_cli_claims_to_support() {
        var covered = DriverProjections().Select(f => f().Harness)
            .Select(h => h.Split(' ')[0].ToLowerInvariant())
            .Append("pi")                                    // covered by its own bridge test above
            .ToHashSet(StringComparer.Ordinal);

        var claimed = VendorSelection.KnownVendorFlags
            .Select(f => f.TrimStart('-').ToLowerInvariant());

        foreach (var harness in claimed)
            await Assert.That(covered.Contains(harness)).IsTrue()
                .Because($"--{harness} is an installable target with no driver-schema conformance coverage");
    }
}
