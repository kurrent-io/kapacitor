using Capacitor.Cli.Core.Harness.Codex;
using Tomlyn;
using Tomlyn.Model;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Codex;

// EnableNetworkAccess/TrustWorktree take an explicit config path, so these tests
// use a temp file and never touch HOME — safe to run in parallel.
public class CodexConfigTomlTests {
    // Injected native-binary path for command assertions - never bless the test runner.
    const string TestBinaryPath = "/opt/kcap-test/bin/kcap";

    static TomlTable ReadToml(string path) =>
        TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(path))!;

    // ── BuildAllowDomains ────────────────────────────────────────────────────

    [Test]
    public async Task BuildAllowDomains_collapses_kcap_ai_tenants_to_one_wildcard() {
        var domains = CodexConfigToml.BuildAllowDomains([
            "https://acme.kcap.ai", "https://globex.kcap.ai"
        ]);

        await Assert.That(domains).IsEquivalentTo(new[] { "**.kcap.ai" });
    }

    [Test]
    public async Task BuildAllowDomains_keeps_self_hosted_hosts_exact_and_sorted_after_wildcard() {
        var domains = CodexConfigToml.BuildAllowDomains([
            "https://team.kcap.ai", "https://kcap.internal.corp", "https://capacitor.example.com"
        ]);

        // Wildcard first (kcap.ai tenant present), then self-hosted hosts sorted.
        await Assert.That(domains).IsEquivalentTo(new[] {
            "**.kcap.ai", "capacitor.example.com", "kcap.internal.corp"
        });
    }

    [Test]
    public async Task BuildAllowDomains_pure_self_hosted_has_no_kcap_wildcard() {
        var domains = CodexConfigToml.BuildAllowDomains([
            "https://capacitor.example.com:8443"
        ]);

        await Assert.That(domains).IsEquivalentTo(new[] { "capacitor.example.com" });
    }

    [Test]
    public async Task BuildAllowDomains_skips_null_blank_and_dedupes() {
        var domains = CodexConfigToml.BuildAllowDomains([
            null, "", "  ", "https://capacitor.example.com", "https://capacitor.example.com"
        ]);

        await Assert.That(domains).IsEquivalentTo(new[] { "capacitor.example.com" });
    }

    [Test]
    public async Task BuildAllowDomains_accepts_bare_host_without_scheme() {
        var domains = CodexConfigToml.BuildAllowDomains(["my-tenant.kcap.ai", "self.example.com"]);

        await Assert.That(domains).IsEquivalentTo(new[] { "**.kcap.ai", "self.example.com" });
    }

    // ── EnableNetworkAccess: default config ──────────────────────────────────

    [Test]
    public async Task EnableNetworkAccess_on_missing_config_writes_access_and_proxy_allowlist() {
        using var tmp = new TempDir();
        var path = tmp.GetResolvedPath("config.toml");

        var change = CodexConfigToml.EnableNetworkAccess(["**.kcap.ai"], path);

        await Assert.That(change).IsEqualTo(CodexConfigToml.Change.Updated);

        var root  = ReadToml(path);
        var sww   = (TomlTable)root["sandbox_workspace_write"];
        await Assert.That((bool)sww["network_access"]).IsTrue();

        var proxy = (TomlTable)((TomlTable)root["features"])["network_proxy"];
        await Assert.That((bool)proxy["enabled"]).IsTrue();
        await Assert.That((string)((TomlTable)proxy["domains"])["**.kcap.ai"]).IsEqualTo("allow");
    }

    [Test]
    public async Task EnableNetworkAccess_is_idempotent() {
        using var tmp = new TempDir();
        var path = tmp.GetResolvedPath("config.toml");

        var first  = CodexConfigToml.EnableNetworkAccess(["**.kcap.ai"], path);
        var second = CodexConfigToml.EnableNetworkAccess(["**.kcap.ai"], path);

        await Assert.That(first).IsEqualTo(CodexConfigToml.Change.Updated);
        await Assert.That(second).IsEqualTo(CodexConfigToml.Change.Unchanged);
    }

    [Test]
    [NotInParallel("CwdMutation")]
    public async Task EnableNetworkAccess_writes_when_config_path_has_no_directory_component() {
        // GetDirectoryName("config.toml") is empty; CreateDirectory("") would throw and
        // silently turn the write into Change.Failed without the guard.
        using var tmp   = new TempDir();
        var originalCwd = Environment.CurrentDirectory;

        try {
            Environment.CurrentDirectory = tmp.Path;

            var change = CodexConfigToml.EnableNetworkAccess(["**.kcap.ai"], "config.toml");

            await Assert.That(change).IsEqualTo(CodexConfigToml.Change.Updated);
            await Assert.That(File.Exists(tmp.PathTo("config.toml"))).IsTrue();
        } finally {
            Environment.CurrentDirectory = originalCwd;
        }
    }

    [Test]
    public async Task EnableNetworkAccess_empty_allowlist_is_noop() {
        using var tmp = new TempDir();
        var path = tmp.GetResolvedPath("config.toml");

        var change = CodexConfigToml.EnableNetworkAccess([], path);

        await Assert.That(change).IsEqualTo(CodexConfigToml.Change.Unchanged);
        await Assert.That(File.Exists(path)).IsFalse();
    }

    // ── EnableNetworkAccess: respect existing config ─────────────────────────

    [Test]
    public async Task EnableNetworkAccess_fully_open_no_proxy_is_left_untouched() {
        using var tmp = new TempDir();
        var path = tmp.GetResolvedPath("config.toml");
        File.WriteAllText(path,
            """
            [sandbox_workspace_write]
            network_access = true
            """);
        var before = File.ReadAllText(path);

        var change = CodexConfigToml.EnableNetworkAccess(["**.kcap.ai"], path);

        await Assert.That(change).IsEqualTo(CodexConfigToml.Change.Unchanged);
        await Assert.That(File.ReadAllText(path)).IsEqualTo(before);
    }

    [Test]
    public async Task EnableNetworkAccess_merges_into_existing_proxy_preserving_user_entries() {
        using var tmp = new TempDir();
        var path = tmp.GetResolvedPath("config.toml");
        File.WriteAllText(path,
            """
            model = "gpt-5.5"

            [sandbox_workspace_write]
            network_access = true

            [features.network_proxy]
            enabled = true

            [features.network_proxy.domains]
            "github.com" = "allow"
            """);

        var change = CodexConfigToml.EnableNetworkAccess(["**.kcap.ai", "self.example.com"], path);

        await Assert.That(change).IsEqualTo(CodexConfigToml.Change.Updated);

        var root    = ReadToml(path);
        await Assert.That((string)root["model"]).IsEqualTo("gpt-5.5");

        var domains = (TomlTable)((TomlTable)((TomlTable)root["features"])["network_proxy"])["domains"];
        await Assert.That((string)domains["github.com"]).IsEqualTo("allow");     // user's preserved
        await Assert.That((string)domains["**.kcap.ai"]).IsEqualTo("allow");     // ours added
        await Assert.That((string)domains["self.example.com"]).IsEqualTo("allow");
    }

    [Test]
    public async Task EnableNetworkAccess_existing_proxy_without_network_access_turns_it_on() {
        using var tmp = new TempDir();
        var path = tmp.GetResolvedPath("config.toml");
        File.WriteAllText(path,
            """
            [features.network_proxy]
            enabled = true

            [features.network_proxy.domains]
            "github.com" = "allow"
            """);

        var change = CodexConfigToml.EnableNetworkAccess(["**.kcap.ai"], path);

        await Assert.That(change).IsEqualTo(CodexConfigToml.Change.Updated);

        var root = ReadToml(path);
        await Assert.That((bool)((TomlTable)root["sandbox_workspace_write"])["network_access"]).IsTrue();
    }

    [Test]
    public async Task EnableNetworkAccess_malformed_config_is_not_overwritten() {
        using var    tmp     = new TempDir();
        var          path    = tmp.GetResolvedPath("config.toml");
        const string garbage = "{{{ not valid TOML";
        File.WriteAllText(path, garbage);

        var change = CodexConfigToml.EnableNetworkAccess(["**.kcap.ai"], path);

        await Assert.That(change).IsEqualTo(CodexConfigToml.Change.Failed);
        await Assert.That(File.ReadAllText(path)).IsEqualTo(garbage);
    }

    // ── RegisterKcapMcpServers ───────────────────────────────────────────────

    static string[] ArgsOf(TomlTable server) =>
        ((TomlArray)server["args"]).Select(v => (string)v!).ToArray();

    [Test]
    public async Task RegisterKcapMcpServers_on_missing_config_writes_all_servers() {
        using var tmp = new TempDir();
        var path = tmp.GetResolvedPath("config.toml");

        var change = CodexConfigToml.RegisterKcapMcpServers(path, resolveBinaryPath: () => TestBinaryPath);

        await Assert.That(change).IsEqualTo(CodexConfigToml.Change.Updated);

        var servers  = (TomlTable)ReadToml(path)["mcp_servers"];
        var review   = (TomlTable)servers["kcap-review"];
        var sessions = (TomlTable)servers["kcap-sessions"];
        var flows    = (TomlTable)servers["kcap-flows"];
        var memory   = (TomlTable)servers["kcap-memory"];

        // Registered command is the resolved native binary (injected seam), not the wrapper-resolved "kcap".
        await Assert.That((string)review["command"]).IsEqualTo(TestBinaryPath);
        await Assert.That(ArgsOf(review)).IsEquivalentTo(new[] { "mcp", "review" });
        await Assert.That((string)sessions["command"]).IsEqualTo(TestBinaryPath);
        await Assert.That(ArgsOf(sessions)).IsEquivalentTo(new[] { "mcp", "sessions" });
        await Assert.That(ArgsOf(flows)).IsEquivalentTo(new[] { "mcp", "flows" });
        // kcap-memory is now auto-registered for Codex too.
        await Assert.That((string)memory["command"]).IsEqualTo(TestBinaryPath);
        await Assert.That(ArgsOf(memory)).IsEquivalentTo(new[] { "mcp", "memory" });
        await Assert.That(File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "mcp-ownership-v1.json"))).IsTrue();
    }

    [Test]
    public async Task RegisterKcapMcpServers_auto_approves_only_read_only_servers() {
        using var tmp = new TempDir();
        var path = tmp.GetResolvedPath("config.toml");

        CodexConfigToml.RegisterKcapMcpServers(path);

        var servers  = (TomlTable)ReadToml(path)["mcp_servers"];
        var review   = (TomlTable)servers["kcap-review"];
        var sessions = (TomlTable)servers["kcap-sessions"];
        var flows    = (TomlTable)servers["kcap-flows"];
        var memory   = (TomlTable)servers["kcap-memory"];

        // Read-only servers auto-approve (never prompt); kcap-memory (writes via save) keeps the default.
        await Assert.That((string)review["default_tools_approval_mode"]).IsEqualTo("approve");
        await Assert.That((string)sessions["default_tools_approval_mode"]).IsEqualTo("approve");
        await Assert.That(flows.ContainsKey("default_tools_approval_mode")).IsFalse();
        await Assert.That(memory.ContainsKey("default_tools_approval_mode")).IsFalse();
    }

    [Test]
    public async Task RegisterKcapMcpServers_preserves_existing_entries_without_claiming_or_healing() {
        using var tmp = new TempDir();
        var path = tmp.GetResolvedPath("config.toml");
        // Pre-existing kcap entries: kcap-review with no approval mode (older install); kcap-sessions
        // where the user deliberately chose "prompt".
        File.WriteAllText(path, """
            [mcp_servers.kcap-review]
            command = "kcap"
            args = ["mcp", "review"]

            [mcp_servers.kcap-sessions]
            command = "kcap"
            args = ["mcp", "sessions"]
            default_tools_approval_mode = "prompt"
            """);

        var change = CodexConfigToml.RegisterKcapMcpServers(path);

        await Assert.That(change).IsEqualTo(CodexConfigToml.Change.Updated);
        var servers = (TomlTable)ReadToml(path)["mcp_servers"];
        await Assert.That(((TomlTable)servers["kcap-review"]).ContainsKey("default_tools_approval_mode")).IsFalse();
        await Assert.That((string)((TomlTable)servers["kcap-sessions"])["default_tools_approval_mode"]).IsEqualTo("prompt");  // user choice preserved
    }

    [Test]
    public async Task RegisterKcapMcpServers_does_not_auto_approve_a_foreign_same_named_entry() {
        using var tmp = new TempDir();
        var path = tmp.GetResolvedPath("config.toml");
        // A user-authored server that shares the name "kcap-review" but is NOT kcap (different command).
        // The heal must NOT auto-approve it — ownership is keyed off command == "kcap".
        File.WriteAllText(path, """
            [mcp_servers.kcap-review]
            command = "my-wrapper"
            args = ["review"]
            """);

        CodexConfigToml.RegisterKcapMcpServers(path);

        var review = (TomlTable)((TomlTable)ReadToml(path)["mcp_servers"])["kcap-review"];
        await Assert.That((string)review["command"]).IsEqualTo("my-wrapper");                 // untouched
        await Assert.That(review.ContainsKey("default_tools_approval_mode")).IsFalse();        // NOT auto-approved
    }

    [Test]
    public async Task RegisterKcapMcpServers_does_not_auto_approve_kcap_named_entry_with_wrong_args() {
        using var tmp = new TempDir();
        var path = tmp.GetResolvedPath("config.toml");
        // command == "kcap" but args point at the WRITE-capable memory server under the read-only
        // "kcap-review" name. The heal must NOT auto-approve it — args don't match the expected
        // read-only server's args, and the heal never rewrites args.
        File.WriteAllText(path, """
            [mcp_servers.kcap-review]
            command = "kcap"
            args = ["mcp", "memory"]
            """);

        CodexConfigToml.RegisterKcapMcpServers(path);

        var review = (TomlTable)((TomlTable)ReadToml(path)["mcp_servers"])["kcap-review"];
        await Assert.That(review.ContainsKey("default_tools_approval_mode")).IsFalse();  // NOT auto-approved
        await Assert.That(ArgsOf(review)).IsEquivalentTo(new[] { "mcp", "memory" });      // args untouched
    }

    [Test]
    public async Task RegisterKcapMcpServers_emits_snake_case_mcp_servers_table() {
        // Codex config.toml uses the snake_case `mcp_servers` table — NOT the
        // camelCase `mcpServers` key the plugin *descriptor* JSON requires.
        using var tmp = new TempDir();
        var path = tmp.GetResolvedPath("config.toml");

        CodexConfigToml.RegisterKcapMcpServers(path);

        var text = File.ReadAllText(path);
        await Assert.That(text).Contains("[mcp_servers.kcap-review]");
        await Assert.That(text).Contains("[mcp_servers.kcap-sessions]");
        await Assert.That(text).Contains("[mcp_servers.kcap-flows]");
        await Assert.That(text).Contains("[mcp_servers.kcap-memory]");
        await Assert.That(text).DoesNotContain("mcpServers");
    }

    [Test]
    public async Task RegisterKcapMcpServers_falls_back_to_kcap_when_binary_path_unresolvable() {
        using var tmp = new TempDir();
        var path = tmp.GetResolvedPath("config.toml");

        CodexConfigToml.RegisterKcapMcpServers(path, resolveBinaryPath: () => null);

        var servers = (TomlTable)ReadToml(path)["mcp_servers"];
        await Assert.That((string)((TomlTable)servers["kcap-review"])["command"]).IsEqualTo("kcap");
    }

    [Test]
    public async Task RegisterKcapMcpServers_heals_owned_entries_across_binary_relayout() {
        using var tmp = new TempDir();
        var path = tmp.GetResolvedPath("config.toml");

        // Fresh install at binary A, then an npm re-layout moves the binary to B. The
        // ownership-ledger fingerprint recorded at A still matches the on-disk entry → heal.
        CodexConfigToml.RegisterKcapMcpServers(path, resolveBinaryPath: () => "/opt/a/kcap");
        var change = CodexConfigToml.RegisterKcapMcpServers(path, resolveBinaryPath: () => "/opt/b/kcap");

        await Assert.That(change).IsEqualTo(CodexConfigToml.Change.Updated);
        var servers = (TomlTable)ReadToml(path)["mcp_servers"];
        await Assert.That((string)((TomlTable)servers["kcap-review"])["command"]).IsEqualTo("/opt/b/kcap");
        await Assert.That((string)((TomlTable)servers["kcap-sessions"])["command"]).IsEqualTo("/opt/b/kcap");

        // And the heal is idempotent once current.
        var again = CodexConfigToml.RegisterKcapMcpServers(path, resolveBinaryPath: () => "/opt/b/kcap");
        await Assert.That(again).IsEqualTo(CodexConfigToml.Change.Unchanged);
    }

    [Test]
    public async Task RegisterKcapMcpServers_never_heals_a_customized_owned_entry() {
        using var tmp = new TempDir();
        var path = tmp.GetResolvedPath("config.toml");
        CodexConfigToml.RegisterKcapMcpServers(path, resolveBinaryPath: () => "/opt/a/kcap");

        // The user edits the owned entry — fingerprint no longer matches, so the relayout
        // heal must relinquish the claim and preserve the customization.
        var root = ReadToml(path);
        var review = (TomlTable)((TomlTable)root["mcp_servers"])["kcap-review"];
        review["env"] = new TomlTable { ["KCAP_URL"] = "https://x" };
        File.WriteAllText(path, TomlSerializer.Serialize(root));

        CodexConfigToml.RegisterKcapMcpServers(path, resolveBinaryPath: () => "/opt/b/kcap");

        var after = (TomlTable)((TomlTable)ReadToml(path)["mcp_servers"])["kcap-review"];
        await Assert.That((string)after["command"]).IsEqualTo("/opt/a/kcap"); // untouched
        await Assert.That(after.ContainsKey("env")).IsTrue();                 // customization preserved
    }

    [Test]
    public async Task UnregisterKcapMcpServers_removes_absolute_registered_owned_entries() {
        using var tmp = new TempDir();
        var path = tmp.GetResolvedPath("config.toml");
        CodexConfigToml.RegisterKcapMcpServers(path, resolveBinaryPath: () => "/opt/a/kcap");

        var change = CodexConfigToml.UnregisterKcapMcpServers(path);

        await Assert.That(change).IsEqualTo(CodexConfigToml.Change.Updated);
        await Assert.That(ReadToml(path).ContainsKey("mcp_servers")).IsFalse();
    }

    [Test]
    public async Task RegisterKcapMcpServers_is_idempotent() {
        using var tmp = new TempDir();
        var path = tmp.GetResolvedPath("config.toml");

        var first  = CodexConfigToml.RegisterKcapMcpServers(path);
        var second = CodexConfigToml.RegisterKcapMcpServers(path);

        await Assert.That(first).IsEqualTo(CodexConfigToml.Change.Updated);
        await Assert.That(second).IsEqualTo(CodexConfigToml.Change.Unchanged);
    }

    [Test]
    public async Task RegisterKcapMcpServers_preserves_user_config_and_servers() {
        using var tmp = new TempDir();
        var path = tmp.GetResolvedPath("config.toml");
        File.WriteAllText(path,
            """
            model = "gpt-5.5"

            [mcp_servers.my-tool]
            command = "my-tool"
            args = ["serve"]
            """);

        var change = CodexConfigToml.RegisterKcapMcpServers(path);

        await Assert.That(change).IsEqualTo(CodexConfigToml.Change.Updated);

        var root    = ReadToml(path);
        await Assert.That((string)root["model"]).IsEqualTo("gpt-5.5");

        var servers = (TomlTable)root["mcp_servers"];
        await Assert.That((string)((TomlTable)servers["my-tool"])["command"]).IsEqualTo("my-tool"); // user's preserved
        await Assert.That(servers.ContainsKey("kcap-review")).IsTrue();
        await Assert.That(servers.ContainsKey("kcap-sessions")).IsTrue();
        await Assert.That(servers.ContainsKey("kcap-flows")).IsTrue();
        await Assert.That(servers.ContainsKey("kcap-memory")).IsTrue();
    }

    [Test]
    public async Task RegisterKcapMcpServers_does_not_clobber_existing_kcap_entry() {
        // A user who set an absolute-path command (e.g. for a GUI host) must keep it.
        using var tmp = new TempDir();
        var path = tmp.GetResolvedPath("config.toml");
        File.WriteAllText(path,
            """
            [mcp_servers.kcap-sessions]
            command = "/opt/homebrew/bin/kcap"
            args = ["mcp", "sessions"]
            """);

        var change = CodexConfigToml.RegisterKcapMcpServers(path, resolveBinaryPath: () => TestBinaryPath);

        // kcap-review added; kcap-sessions left as-is → overall Updated.
        await Assert.That(change).IsEqualTo(CodexConfigToml.Change.Updated);

        var servers = (TomlTable)ReadToml(path)["mcp_servers"];
        await Assert.That((string)((TomlTable)servers["kcap-sessions"])["command"]).IsEqualTo("/opt/homebrew/bin/kcap");
        await Assert.That((string)((TomlTable)servers["kcap-review"])["command"]).IsEqualTo(TestBinaryPath);
    }

    [Test]
    public async Task RegisterKcapMcpServers_non_table_mcp_servers_is_failure_not_clobber() {
        // A non-table `mcp_servers` value must not be silently replaced (honours the
        // non-destructive contract) — register fails and leaves the file untouched.
        using var    tmp     = new TempDir();
        var          path    = tmp.GetResolvedPath("config.toml");
        const string content = "mcp_servers = \"oops\"\n";
        File.WriteAllText(path, content);

        var change = CodexConfigToml.RegisterKcapMcpServers(path);

        await Assert.That(change).IsEqualTo(CodexConfigToml.Change.Failed);
        await Assert.That(File.ReadAllText(path)).IsEqualTo(content);
    }

    [Test]
    public async Task RegisterKcapMcpServers_malformed_config_is_not_overwritten() {
        using var    tmp     = new TempDir();
        var          path    = tmp.GetResolvedPath("config.toml");
        const string garbage = "{{{ not valid TOML";
        File.WriteAllText(path, garbage);

        var change = CodexConfigToml.RegisterKcapMcpServers(path);

        await Assert.That(change).IsEqualTo(CodexConfigToml.Change.Failed);
        await Assert.That(File.ReadAllText(path)).IsEqualTo(garbage);
    }

    // ── UnregisterKcapMcpServers ─────────────────────────────────────────────

    [Test]
    public async Task UnregisterKcapMcpServers_removes_kcap_entries_and_drops_empty_table() {
        using var tmp = new TempDir();
        var path = tmp.GetResolvedPath("config.toml");
        CodexConfigToml.RegisterKcapMcpServers(path);

        var change = CodexConfigToml.UnregisterKcapMcpServers(path);

        await Assert.That(change).IsEqualTo(CodexConfigToml.Change.Updated);
        await Assert.That(ReadToml(path).ContainsKey("mcp_servers")).IsFalse();
    }

    [Test]
    public async Task UnregisterKcapMcpServers_preserves_user_servers() {
        using var tmp = new TempDir();
        var path = tmp.GetResolvedPath("config.toml");
        File.WriteAllText(path,
            """
            [mcp_servers.my-tool]
            command = "my-tool"
            args = ["serve"]
            """);
        CodexConfigToml.RegisterKcapMcpServers(path);

        var change = CodexConfigToml.UnregisterKcapMcpServers(path);

        await Assert.That(change).IsEqualTo(CodexConfigToml.Change.Updated);

        var servers = (TomlTable)ReadToml(path)["mcp_servers"];
        await Assert.That(servers.ContainsKey("my-tool")).IsTrue();
        await Assert.That(servers.ContainsKey("kcap-review")).IsFalse();
        await Assert.That(servers.ContainsKey("kcap-sessions")).IsFalse();
        await Assert.That(servers.ContainsKey("kcap-flows")).IsFalse();
        await Assert.That(servers.ContainsKey("kcap-memory")).IsFalse();
    }

    [Test]
    public async Task UnregisterKcapMcpServers_is_noop_when_absent() {
        using var tmp = new TempDir();
        var path = tmp.GetResolvedPath("config.toml");
        File.WriteAllText(path, """model = "gpt-5.5" """);

        var change = CodexConfigToml.UnregisterKcapMcpServers(path);

        await Assert.That(change).IsEqualTo(CodexConfigToml.Change.Unchanged);
    }

    [Test]
    public async Task UnregisterKcapMcpServers_preserves_owned_entry_changed_by_user() {
        using var tmp = new TempDir();
        var path = tmp.GetResolvedPath("config.toml");
        CodexConfigToml.RegisterKcapMcpServers(path);
        var root = ReadToml(path);
        var flows = (TomlTable)((TomlTable)root["mcp_servers"])["kcap-flows"];
        flows["command"] = "/opt/homebrew/bin/kcap";
        File.WriteAllText(path, TomlSerializer.Serialize(root));

        var change = CodexConfigToml.UnregisterKcapMcpServers(path);

        await Assert.That(change).IsEqualTo(CodexConfigToml.Change.UpdatedWithPreservedEntries);
        var servers = (TomlTable)ReadToml(path)["mcp_servers"];
        await Assert.That((string)((TomlTable)servers["kcap-flows"])["command"])
            .IsEqualTo("/opt/homebrew/bin/kcap");
        await Assert.That(servers.ContainsKey("kcap-review")).IsFalse();
    }

    [Test]
    public async Task UnregisterKcapMcpServers_corrupt_ledger_preserves_everything() {
        using var tmp = new TempDir();
        var path = tmp.GetResolvedPath("config.toml");
        CodexConfigToml.RegisterKcapMcpServers(path);
        var ledger = Path.Combine(Path.GetDirectoryName(path)!, "mcp-ownership-v1.json");
        File.WriteAllText(ledger, "not json");
        var before = File.ReadAllText(path);

        var change = CodexConfigToml.UnregisterKcapMcpServers(path);

        await Assert.That(change).IsEqualTo(CodexConfigToml.Change.PreservedOwnershipUnknown);
        await Assert.That(File.ReadAllText(path)).IsEqualTo(before);
    }

    [Test]
    public async Task UnregisterKcapMcpServers_preserves_owned_entry_with_table_array_edit() {
        using var tmp = new TempDir();
        var path = tmp.GetResolvedPath("config.toml");
        CodexConfigToml.RegisterKcapMcpServers(path);
        var root = ReadToml(path);
        var flows = (TomlTable)((TomlTable)root["mcp_servers"])["kcap-flows"];
        var rules = new TomlTableArray { new TomlTable { ["name"] = "user-rule" } };
        flows["rules"] = rules;
        File.WriteAllText(path, TomlSerializer.Serialize(root));

        CodexConfigToml.RegisterKcapMcpServers(path); // relinquishes the changed claim
        var change = CodexConfigToml.UnregisterKcapMcpServers(path);

        await Assert.That(change).IsEqualTo(CodexConfigToml.Change.UpdatedWithPreservedEntries);
        var servers = (TomlTable)ReadToml(path)["mcp_servers"];
        await Assert.That(servers.ContainsKey("kcap-flows")).IsTrue();
    }

    [Test]
    public async Task Unregister_without_server_table_relinquishes_stale_claims() {
        using var tmp = new TempDir();
        var path = tmp.GetResolvedPath("config.toml");
        CodexConfigToml.RegisterKcapMcpServers(path);
        var root = ReadToml(path);
        root.Remove("mcp_servers");
        File.WriteAllText(path, TomlSerializer.Serialize(root));

        var cleared = CodexConfigToml.UnregisterKcapMcpServers(path);
        await Assert.That(cleared).IsEqualTo(CodexConfigToml.Change.Updated);

        root = ReadToml(path);
        var manual = new TomlTable {
            ["command"] = "kcap",
            ["args"] = new TomlArray { "mcp", "flows" }
        };
        root["mcp_servers"] = new TomlTable { ["kcap-flows"] = manual };
        File.WriteAllText(path, TomlSerializer.Serialize(root));

        var preserved = CodexConfigToml.UnregisterKcapMcpServers(path);
        await Assert.That(preserved).IsEqualTo(CodexConfigToml.Change.PreservedUnownedEntries);
        await Assert.That(((TomlTable)ReadToml(path)["mcp_servers"]).ContainsKey("kcap-flows")).IsTrue();
    }

    [Test]
    public async Task Register_rejects_symlinked_parent_directory() {
        if (OperatingSystem.IsWindows()) return;
        using var tmp = new TempDir();
        var real  = tmp.CreateDir("real");
        var alias = tmp.PathTo("alias");
        Directory.CreateSymbolicLink(alias, real);

        var change = CodexConfigToml.RegisterKcapMcpServers(Path.Combine(alias, "config.toml"));
        await Assert.That(change).IsEqualTo(CodexConfigToml.Change.Failed);
        await Assert.That(File.Exists(real.PathTo("config.toml"))).IsFalse();
    }

    [Test]
    public async Task RegisterKcapMcpServers_writes_owner_only_files_on_unix() {
        if (OperatingSystem.IsWindows()) return;
        using var tmp = new TempDir();
        var path = tmp.GetResolvedPath("config.toml");

        CodexConfigToml.RegisterKcapMcpServers(path);

        var ledger = Path.Combine(Path.GetDirectoryName(path)!, "mcp-ownership-v1.json");
        var expected = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await Assert.That(File.GetUnixFileMode(path)).IsEqualTo(expected);
        await Assert.That(File.GetUnixFileMode(ledger)).IsEqualTo(expected);
    }

    // ── ReadMcpServerNames: enumerate the [mcp_servers] table a reviewer would inherit ──

    [Test]
    public async Task ReadMcpServerNames_returns_table_keys_sorted_ordinal() {
        using var tmp = new TempDir();
        var path = tmp.GetResolvedPath("config.toml");
        File.WriteAllText(path, """
            [mcp_servers.node_repl]
            command = "node"
            args = ["repl.js"]

            [mcp_servers.kcap-flows]
            command = "kcap"
            args = ["mcp", "flows"]

            [mcp_servers.computer-use]
            command = "cu"
            args = []
            """);

        var names = CodexConfigToml.ReadMcpServerNames(path);

        await Assert.That(names).IsEquivalentTo(new[] { "computer-use", "kcap-flows", "node_repl" });
    }

    [Test]
    public async Task ReadMcpServerNames_missing_file_returns_empty() {
        using var tmp = new TempDir();
        var path = tmp.PathTo("does-not-exist.toml");   // the dir exists, the file does not

        await Assert.That(CodexConfigToml.ReadMcpServerNames(path)).IsEmpty();
    }

    [Test]
    public async Task ReadMcpServerNames_no_mcp_servers_table_returns_empty() {
        using var tmp = new TempDir();
        var path = tmp.GetResolvedPath("config.toml");
        File.WriteAllText(path, """
            model = "gpt-5.3-codex"
            """);

        await Assert.That(CodexConfigToml.ReadMcpServerNames(path)).IsEmpty();
    }

    [Test]
    public async Task ReadMcpServerNames_malformed_toml_returns_empty() {
        using var tmp = new TempDir();
        var path = tmp.GetResolvedPath("config.toml");
        File.WriteAllText(path, "this is = = not valid [toml");

        await Assert.That(CodexConfigToml.ReadMcpServerNames(path)).IsEmpty();
    }
}
