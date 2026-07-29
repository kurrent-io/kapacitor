using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Mcp;
using Capacitor.Cli.Core.Antigravity;
using Capacitor.Cli.Core.Copilot;
using Capacitor.Cli.Core.Gemini;
using Capacitor.Cli.Core.Kiro;
using Capacitor.Cli.Core.Pi;
using TUnit.Assertions.Enums;
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
        // Structure as well as prose: a `model` retyped to boolean, or promoted into Required (which
        // would break every caller relying on the vendor's default model), passed the prose check.
        await Assert.That(props["model"].Type).IsEqualTo("string");
        await Assert.That(Tool(toolName).InputSchema.Required).DoesNotContain("model");
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

        // Assert the RENDERED labels, not the bare values. FormatStatusResponse catches formatter
        // exceptions and returns the original JSON body, so "contains claude" was satisfied by the
        // fallback — the test passed even if formatting failed completely or the audit rendering were
        // deleted outright.
        await Assert.That(text).Contains("applied_reviewer_vendor: claude");
        await Assert.That(text).Contains("applied_reviewer_model: sonnet");
        await Assert.That(text).Contains("requested_reviewer_vendor: claude");
        await Assert.That(text).DoesNotContain("\"flow_run_id\"")
            .Because("raw JSON in the output means the formatter fell back rather than rendering");
    }

    // The POLLED round path renders the same audit fields from its OWN copy of the code, and it is
    // the one an agent reads on nearly every flow — status is the path you go to when something looks
    // wrong. Found while mutation-testing the test above: mutating the audit block in
    // FormatPolledRoundResult left it green, because the two formatters do not share the rendering.
    [Test]
    public async Task The_polled_round_path_also_surfaces_the_applied_participant_vendor() {
        const string roundJson = """
            {"flow_run_id":"f1","status":"clean","round_result_kind":"clean",
             "requested_reviewer_vendor":"cursor","applied_reviewer_vendor":"cursor",
             "reviewer_vendor_source":"explicit","applied_reviewer_model":"composer"}
            """;

        var text = McpFlowsServer.FormatPolledRoundResult(
            System.Text.Json.Nodes.JsonNode.Parse(roundJson)!.AsObject(), "f1");

        await Assert.That(text).Contains("applied_reviewer_vendor: cursor");
        await Assert.That(text).Contains("applied_reviewer_model: composer");
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

    /// <summary>One harness arm: how to make the installer believe the harness is present, where its
    /// config lands, and how to read the <c>kcap-flows</c> entry back out.</summary>
    public sealed record Arm(
        string                          Name,
        string                          Flag,
        Action<PluginEnvironment>       OptIn,
        Func<PluginEnvironment, string> ConfigPath,
        Func<string, Projection>        Extract,
        bool                            BareInstall = false);

    /// <summary>Every environment variable any kcap path resolver consults. Kept together so a new
    /// override cannot be added without this list being the obvious place to add it.</summary>
    static readonly string[] PathOverrideVariables = [
        "CODEX_HOME",            // CodexPaths
        "COPILOT_HOME",          // CopilotPaths
        "GEMINI_CLI_HOME",       // GeminiPaths — and AntigravityPaths, which reuses GeminiPaths.Root
        "KIRO_HOME",             // KiroPaths
        "OPENCODE_CONFIG_DIR",   // OpenCodePaths
        "XDG_CONFIG_HOME",       // OpenCodePaths fallback
        "XDG_DATA_HOME",         // OpenCodePaths plugin dir fallback
    ];

    sealed class EnvScopes(IEnumerable<string> keys) : IDisposable {
        readonly List<EnvScope> _scopes = [.. keys.Select(k => new EnvScope(k, null))];
        public void Dispose() { foreach (var s in _scopes) s.Dispose(); }
    }

    sealed class EnvScope : IDisposable {
        readonly string  _key;
        readonly string? _prev;
        public EnvScope(string key, string? value) {
            _key = key; _prev = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, value);
        }
        public void Dispose() => Environment.SetEnvironmentVariable(_key, _prev);
    }

    static PluginEnvironment TestEnv(string home, string? pluginRoot = null) =>
        new(HomeDirectory: home, ResolvePluginPath: () => pluginRoot,
            Stdout: TextWriter.Null, Stderr: TextWriter.Null);

    /// <summary>Codex is the one arm that does not use the `--if-installed` refresh branch (it
    /// installs unconditionally), so it needs a resolvable plugin root carrying the skills source —
    /// mirroring PluginCommandCodexTests.PlantFakePlugin.</summary>
    static string PlantFakePlugin() {
        var root = Scratch("codex-plugin-").FullName;
        foreach (var name in AgentsSkillsInstaller.SourceNames) {
            var dir = Path.Combine(root, "skills", name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "SKILL.md"), $"---\nname: {name}\n---\n# {name}");
        }
        return root;
    }

    /// <summary>Reads the flows entry from a written JSON config. <paramref name="argvArray"/> covers
    /// OpenCode, which folds command and args into one array.</summary>
    static Func<string, Projection> Json(string harness, string blockKey, bool argvArray = false) => path => {
        var root  = (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
        var block = root[blockKey] as JsonObject
                 ?? throw new InvalidOperationException($"{harness}: no '{blockKey}' block written to {path}");
        var entry = block["kcap-flows"] as JsonObject
                 ?? throw new InvalidOperationException($"{harness}: kcap-flows not registered");

        if (argvArray) {
            var argv = entry["command"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
            return new(harness, argv[0], argv[1..]);
        }
        return new(harness, entry["command"]!.GetValue<string>(),
            [.. entry["args"]!.AsArray().Select(n => n!.GetValue<string>())]);
    };

    static Projection FromToml(string path) {
        var servers = (TomlTable)TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(path))!["mcp_servers"]!;
        var entry   = (TomlTable)servers["kcap-flows"]!;
        return new("Codex", (string)entry["command"]!,
            [.. ((TomlArray)entry["args"]!).Select(a => (string)a!)]);
    }

    // Each arm seeds "installed but stale" so `--if-installed` takes the refresh branch and registers
    // MCP, mirroring each harness's own PluginCommand*Tests. That branch also skips the AgentDetector
    // "kcap on PATH" precheck, which a bare install would fail in CI.
    static readonly Arm[] Arms = [
        new("Cursor", "--cursor",
            env => {
                Directory.CreateDirectory(Path.GetDirectoryName(env.CursorUserHooksJson)!);
                File.WriteAllText(env.CursorUserHooksJson,
                    """{"version":1,"hooks":{"sessionStart":[{"command":"kcap hook --cursor"}]}}""");
            },
            env => env.CursorMcpJson, Json("Cursor", "mcpServers")),

        new("Copilot", "--copilot",
            env => { PluginCommand.InstallCopilotHooks(env.CopilotKcapHooksJson);
                     CopilotHooksInstaller.DeleteMarker(env.CopilotKcapHooksJson); },
            env => env.CopilotMcpConfigJson, Json("Copilot", "mcpServers")),

        new("Gemini", "--gemini",
            env => { PluginCommand.InstallGeminiHooks(env.GeminiSettingsJson);
                     GeminiHooksInstaller.DeleteMarker(env.GeminiSettingsJson); },
            env => env.GeminiSettingsJson, Json("Gemini", "mcpServers")),

        new("Kiro", "--kiro",
            env => { Directory.CreateDirectory(Path.GetDirectoryName(env.KiroKcapAgentJson)!);
                     File.WriteAllText(env.KiroKcapAgentJson, """{"name":"kcap","hooks":{}}""");
                     KiroHooksInstaller.WriteMarker(env.KiroKcapAgentJson, "kiro_default"); },
            env => env.KiroMcpJson, Json("Kiro", "mcpServers")),

        new("Antigravity", "--antigravity",
            env => { AntigravityHooksInstaller.Install(env.AntigravityHooksJson);
                     File.WriteAllText(Path.Combine(Path.GetDirectoryName(env.AntigravityHooksJson)!,
                         AntigravityHooksInstaller.MarkerFileName), "0.0.0-stale"); },
            env => env.AntigravityMcpConfigJson, Json("Antigravity", "mcpServers")),

        new("OpenCode", "--opencode",
            env => { Directory.CreateDirectory(Path.GetDirectoryName(env.OpenCodeKcapPlugin)!);
                     File.WriteAllText(env.OpenCodeKcapPlugin, "// stale"); },
            env => env.OpenCodeMcpConfigJson, Json("OpenCode", "mcp", argvArray: true)),

        // Codex installs unconditionally rather than through the `--if-installed` refresh branch, so
        // it takes the bare-install path and needs a planted plugin root. Flagged here rather than
        // hidden, because it is the one arm whose invocation differs.
        new("Codex", "--codex", _ => { }, env => env.CodexConfigTomlPath, FromToml,
            BareInstall: true),
    ];

    /// <summary>Runs the REAL installer for one harness against a fake home and reads back what it
    /// wrote. Going through <c>PluginCommand.HandleAsync</c> rather than reconstructing the writer
    /// call is the whole point: a reconstruction stays green when the production wiring changes the
    /// server subset, the shape, or drops flows entirely — which is exactly the drift this suite
    /// claims to catch.</summary>
    static async Task<Projection> InstallAndRead(Arm arm) {
        // EVERY known path override, cleared for EVERY arm — deliberately not a per-arm list. If any
        // one is missed, the opt-in, the installer and the extractor all resolve OUTSIDE the fake
        // home: the test then reads and rewrites the developer's real harness config, and can pass
        // against a pre-existing entry. The names are easy to get individually wrong (GeminiPaths
        // reads GEMINI_CLI_HOME, not GEMINI_HOME; OpenCode falls back to XDG_CONFIG_HOME; CodexPaths
        // gives ambient CODEX_HOME precedence), which is exactly why the list is not per-arm.
        using var overrides = new EnvScopes(PathOverrideVariables);
        using var home      = new FakeUserHome();
        var env = TestEnv(home.Path, arm.BareInstall ? PlantFakePlugin() : null);

        arm.OptIn(env);

        string[] argv = arm.BareInstall
            // --skip-codex-network-access: a schema conformance test has no business reading or
            // rewriting the profile's network-access config.
            ? ["plugin", "install", arm.Flag, "--skip-codex-network-access"]
            : ["plugin", "install", arm.Flag, "--if-installed"];
        var exit = await PluginCommand.HandleAsync(argv, env);
        if (exit != 0) throw new InvalidOperationException($"{arm.Name}: installer exited {exit}");

        var path = arm.ConfigPath(env);
        if (!File.Exists(path)) throw new InvalidOperationException($"{arm.Name}: no config at {path}");

        return arm.Extract(path);
    }

    public static IEnumerable<Func<Arm>> InstallerArms() =>
        Arms.Select(a => (Func<Arm>)(() => a));

    [Test]
    [NotInParallel("HomeEnvVarMutation")]
    [MethodDataSource(nameof(InstallerArms))]
    public async Task Every_installed_driver_launches_the_same_flows_server(Arm arm) {
        var p = await InstallAndRead(arm);

        await Assert.That(p.Command).IsEqualTo(KcapMcpServers.Command)
            .Because($"{p.Harness} must launch the same executable as every other driver");
        // ORDERED: argv order is semantic. An unordered comparison passes ["flows","mcp"], which
        // launches nothing.
        await Assert.That(p.Args).IsEquivalentTo(new[] { "mcp", "flows" }, CollectionOrdering.Matching)
            .Because($"{p.Harness} must reach the same subcommand, and therefore the same tool schema");
    }

    // The two bundled static files are not written by an installer — Claude Code and Codex's native
    // plugin loader read them straight from the package — so they get their own arm.
    /// <summary>The bundled static configs, kept as a list rather than inline [Arguments] so the
    /// coverage assertion below can see them. Both reduce to --codex in the tripwire, so an
    /// inline arm could be deleted while one of two INDEPENDENT Codex registration mechanisms went
    /// untested.</summary>
    static readonly (string File, string Harness)[] BundledConfigs = [
        (".mcp.json",       "Claude Code"),
        (".codex-mcp.json", "Codex plugin"),
    ];

    public static IEnumerable<Func<(string File, string Harness)>> Bundled() =>
        BundledConfigs.Select(b => (Func<(string, string)>)(() => b));

    [Test]
    [MethodDataSource(nameof(Bundled))]
    public async Task Every_bundled_config_names_the_same_flows_server((string File, string Harness) bundled) {
        var (file, harness) = bundled;
        var p = Json(harness, "mcpServers")(Path.Combine(RepoKcapDir(), file));

        await Assert.That(p.Command).IsEqualTo(KcapMcpServers.Command);
        await Assert.That(p.Args).IsEquivalentTo(new[] { "mcp", "flows" }, CollectionOrdering.Matching);
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

    // ── one definition, both routes ───────────────────────────────────────────────────────────
    //
    // `kcap setup` registers MCP through its own delegates, independently of `kcap plugin install`.
    // Without a shared definition a user could get a different tool surface depending on which
    // command they ran, and the installer-driven arms above would not notice.
    //
    // The (subset, shape, marker) tuple lives once, in HarnessMcpProjections, and BOTH call sites
    // consume it, so there is no second definition to diverge. These assertions pin that definition.

    public static IEnumerable<Func<HarnessMcpProjection>> Projections() =>
        HarnessMcpProjections.All.Select(p => (Func<HarnessMcpProjection>)(() => p));

    [Test]
    [MethodDataSource(nameof(Projections))]
    public async Task Every_harness_projection_includes_the_flows_server(HarnessMcpProjection projection) {
        var flows = projection.Servers.SingleOrDefault(s => s.Name == "kcap-flows");

        await Assert.That(flows).IsNotNull()
            .Because($"{projection.Harness} would silently lose the ability to start a flow");
        await Assert.That(flows!.Args).IsEquivalentTo(new[] { "mcp", "flows" }, CollectionOrdering.Matching);
        // Flows launches a PAID hosted reviewer, so it must never be auto-approved on registration.
        await Assert.That(flows.ReadOnly).IsFalse();
    }

    // Install/uninstall must read the SAME ownership tuple. If the remove paths or Kiro's "is the MCP
    // half already installed?" probe hard-code shape and marker instead of going through the
    // projection, changing a projection makes new installs write under one ownership tuple while
    // uninstall looks under the old one — stranding entries kcap can no longer see — and the refresh
    // reads an existing install as absent. Round-trip per harness.
    [Test]
    [MethodDataSource(nameof(Projections))]
    public async Task A_registered_harness_config_is_fully_unregistered_again(HarnessMcpProjection projection) {
        var dir  = Scratch($"roundtrip-{projection.Harness}-");
        var path = Path.Combine(dir.FullName, "config.json");
        try {
            projection.Register(path, cwd: "/repo");
            await Assert.That(projection.OwnsAnything(path)).IsTrue()
                .Because("the probe must see what the writer just wrote");

            projection.Unregister(path);

            await Assert.That(projection.OwnsAnything(path)).IsFalse();
            var root = (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
            var left = (root[projection.Shape.BlockKey] as JsonObject)?.Count ?? 0;
            await Assert.That(left).IsEqualTo(0)
                .Because($"{projection.Harness} stranded kcap entries that uninstall could not see");
        } finally {
            dir.Delete(recursive: true);
        }
    }

    // Deleting a bundled arm can no longer go unnoticed: the covered set is compared against what is
    // actually shipped in kcap/. A third bundled config, or a deleted arm, fails here.
    [Test]
    public async Task Every_bundled_mcp_config_shipped_in_the_package_is_covered() {
        var shipped = Directory.EnumerateFiles(RepoKcapDir())
            .Select(Path.GetFileName)
            .Where(f => f!.EndsWith("mcp.json", StringComparison.Ordinal))
            .ToArray();

        await Assert.That(BundledConfigs.Select(b => b.File)).IsEquivalentTo(shipped);
    }

    // The projection list must cover exactly the JSON harnesses the installer table drives — no more,
    // no fewer. A harness added to one and not the other is the same divergence in a new place.
    [Test]
    public async Task The_projection_list_matches_the_json_installer_arms() {
        var projected = HarnessMcpProjections.All.Select(p => p.Harness).ToArray();
        var installed = Arms.Where(a => !a.BareInstall).Select(a => a.Flag.TrimStart('-')).ToArray();

        await Assert.That(projected).IsEquivalentTo(installed);
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
        // ORDERED -- ["flows","mcp"] is not the same command as ["mcp","flows"].
        await Assert.That(registry!.Args).IsEquivalentTo(canonical.Args, CollectionOrdering.Matching);
        // And the recursion guard still knows flows starts flows — a hosted reviewer must never
        // receive it.
        await Assert.That(registry.StartsFlows).IsTrue();
    }

    // BOTH directions. Canonical-only leaves a server registered with every harness but unresolvable
    // as an allowlist entry; registry-only leaves one allowlistable but never registered anywhere.
    // Checking one direction catches half the drift and reads like it catches all of it.
    [Test]
    public async Task The_two_server_lists_contain_exactly_the_same_servers() {
        var canonical = KcapMcpServers.All.Select(s => s.Name).ToArray();
        var registry  = KcapMcpRegistry.AllIds.ToArray();

        await Assert.That(registry).IsEquivalentTo(canonical);
    }

    [Test]
    public async Task Every_server_agrees_on_its_arguments_across_both_lists() {
        foreach (var s in KcapMcpServers.All) {
            var d = KcapMcpRegistry.Resolve(s.Name);
            await Assert.That(d).IsNotNull().Because($"{s.Name} is unresolvable as an allowlist entry");
            await Assert.That(d!.Args).IsEquivalentTo(s.Args, CollectionOrdering.Matching)
                .Because($"{s.Name} launches different arguments depending on which list you read");
        }
    }

    // The projection table above is hand-written, because no enumeration of supported harnesses
    // exists in production code — the list is spread across at least four separate string arrays.
    // Without this, adding a tenth harness would leave the conformance suite quietly passing on nine.
    // Pinning against the flag arrays makes the omission fail here instead.
    [Test]
    public async Task The_projection_table_covers_every_harness_the_cli_claims_to_support() {
        // Match on the FLAG each arm actually drives, not on a display name split on whitespace —
        // name-splitting matched by accident and would have kept passing for a harness whose arm was
        // renamed rather than added.
        var covered = Arms.Select(a => a.Flag)
            .Append("--claude")   // bundled kcap/.mcp.json, covered by the static-config test
            .Append("--pi")       // no MCP config at all, covered by the bridge test
            .ToHashSet(StringComparer.Ordinal);

        foreach (var flag in VendorSelection.KnownVendorFlags)
            await Assert.That(covered.Contains(flag)).IsTrue()
                .Because($"{flag} is an installable target with no driver-schema conformance coverage");
    }
}
