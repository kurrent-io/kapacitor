using System.Text.Json;
using System.Text.Json.Serialization;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Locks in the SignalR JSON wire format for <see cref="LaunchAgentCommand"/>:
/// the server (Kurrent.Capacitor) configures its SignalR JSON protocol with
/// <c>JsonStringEnumConverter(JsonNamingPolicy.CamelCase)</c> and snake_case
/// property names, so <see cref="LaunchKind"/> rides the wire as a camelCase
/// string. The daemon's <see cref="CapacitorJsonContext"/> must be able to
/// parse that — historically it couldn't, and the SignalR client silently
/// dropped every <c>LaunchAgent</c> invocation (DEV-1665).
/// </summary>
public class LaunchAgentCommandWireFormatTests {
    /// <summary>
    /// Mirrors src/Kurrent.Capacitor/Program.cs SignalR <c>AddJsonProtocol</c>
    /// configuration. If the server changes its options, update this here too —
    /// the test is meaningless if it doesn't match production.
    /// </summary>
    static readonly JsonSerializerOptions ServerWireOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters           = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    [Test]
    public async Task DaemonDeserialises_DefaultKind() {
        var cmd = new LaunchAgentCommand(
            AgentId:       "abc12345",
            Prompt:        null,
            Model:         "opus",
            Effort:        null,
            RepoPath:      "/tmp/repo",
            Tools:         null,
            AttachmentIds: null,
            Vendor:        "claude"
        );

        var wire   = JsonSerializer.Serialize(cmd, ServerWireOptions);
        var parsed = JsonSerializer.Deserialize(wire, CapacitorJsonContext.Default.LaunchAgentCommand);

        await Assert.That(parsed.Kind).IsEqualTo(LaunchKind.Default);
        await Assert.That(parsed.AgentId).IsEqualTo("abc12345");
    }

    [Test]
    public async Task DaemonDeserialises_ReviewKind_WithReviewInfo() {
        var cmd = new LaunchAgentCommand(
            AgentId:       "def67890",
            Prompt:        null,
            Model:         "opus",
            Effort:        null,
            RepoPath:      "/tmp/repo",
            Tools:         null,
            AttachmentIds: null,
            Vendor:        "claude",
            Kind:          LaunchKind.Review,
            Review:        new ReviewLaunchInfo("kurrent-io", "kcap", 42)
        );

        var wire   = JsonSerializer.Serialize(cmd, ServerWireOptions);
        var parsed = JsonSerializer.Deserialize(wire, CapacitorJsonContext.Default.LaunchAgentCommand);

        await Assert.That(parsed.Kind).IsEqualTo(LaunchKind.Review);
        await Assert.That(parsed.Review).IsNotNull();
        await Assert.That(parsed.Review!.Value.Owner).IsEqualTo("kurrent-io");
        await Assert.That(parsed.Review!.Value.Repo).IsEqualTo("kcap");
        await Assert.That(parsed.Review!.Value.PrNumber).IsEqualTo(42);
    }

    [Test]
    public async Task ServerWire_EncodesKindAsCamelCaseString() {
        var cmd = new LaunchAgentCommand(
            AgentId:       "x",
            Prompt:        null,
            Model:         "opus",
            Effort:        null,
            RepoPath:      "/r",
            Tools:         null,
            AttachmentIds: null,
            Vendor:        "claude",
            Kind:          LaunchKind.Review
        );

        var wire = JsonSerializer.Serialize(cmd, ServerWireOptions);

        await Assert.That(wire).Contains("\"kind\":\"review\"");
    }

    [Test]
    public async Task Mcp_allowlist_round_trips_snake_case() {
        // D-c: the server sends the flow definition's MCP allowlist so the daemon can
        // thread it to the launcher (Task 6 materializes it). Appended last after
        // BaseRef so older daemons/servers stay wire-compatible.
        var cmd = new LaunchAgentCommand(
            AgentId:       "mcp00001",
            Prompt:        null,
            Model:         "opus",
            Effort:        null,
            RepoPath:      "/tmp/repo",
            Tools:         null,
            AttachmentIds: null,
            Vendor:        "codex",
            Kind:          LaunchKind.ReviewFlow,
            McpAllowlist:  ["kcap-sessions", "kcap-review"]
        );

        var wire   = JsonSerializer.Serialize(cmd, ServerWireOptions);
        var parsed = JsonSerializer.Deserialize(wire, CapacitorJsonContext.Default.LaunchAgentCommand);

        await Assert.That(wire).Contains("\"mcp_allowlist\":[\"kcap-sessions\",\"kcap-review\"]");
        await Assert.That(parsed.McpAllowlist).IsEquivalentTo(["kcap-sessions", "kcap-review"]);
    }

    [Test]
    public async Task Legacy_payload_without_mcp_allowlist_deserializes_null() {
        // Version skew: a server predating D-c never sends mcp_allowlist. The daemon
        // must still bind the command (positional SignalR binding) and default the field to
        // null — i.e. no allowlist materialization — rather than failing to invoke LaunchAgent.
        const string legacyWire =
            """
            {"agent_id":"legacy02","prompt":null,"model":"opus","effort":null,"repo_path":"/tmp/repo","tools":null,"attachment_ids":null,"vendor":"claude","kind":"reviewFlow"}
            """;

        var parsed = JsonSerializer.Deserialize(legacyWire, CapacitorJsonContext.Default.LaunchAgentCommand);

        await Assert.That(parsed.McpAllowlist).IsNull();
        await Assert.That(parsed.Kind).IsEqualTo(LaunchKind.ReviewFlow);
        await Assert.That(parsed.RepoPath).IsEqualTo("/tmp/repo");
    }

    [Test]
    public async Task Old_reader_ignores_mcp_allowlist() {
        // D-c: a new server sends mcp_allowlist to an old daemon that predates this
        // task. The old reader must ignore the unknown field and still bind everything else —
        // launches must not break just because the server got the new field first.
        var cmd = new LaunchAgentCommand(
            AgentId:       "old0001",
            Prompt:        null,
            Model:         "opus",
            Effort:        null,
            RepoPath:      "/tmp/repo",
            Tools:         null,
            AttachmentIds: null,
            Vendor:        "claude",
            McpAllowlist:  ["kcap-sessions"]
        );

        var wire   = JsonSerializer.Serialize(cmd, ServerWireOptions);
        var parsed = JsonSerializer.Deserialize(wire, OldWireJsonContext.Default.OldLaunchAgentCommand);

        await Assert.That(wire).Contains("\"mcp_allowlist\":[\"kcap-sessions\"]");
        await Assert.That(parsed.AgentId).IsEqualTo("old0001");
        await Assert.That(parsed.Vendor).IsEqualTo("claude");
    }

    [Test]
    public async Task Borrowed_and_BorrowCwd_round_trip_snake_case() {
        // Phase A: the server tells the daemon to launch against the user's own checkout
        // (skip worktree creation) instead of a fresh daemon-owned worktree. Appended last after
        // McpAllowlist, same wire-compat rule as the fields before it.
        var cmd = new LaunchAgentCommand(
            AgentId:       "borrow001",
            Prompt:        null,
            Model:         "opus",
            Effort:        null,
            RepoPath:      "/tmp/repo",
            Tools:         null,
            AttachmentIds: null,
            Vendor:        "claude",
            Borrowed:      true,
            BorrowCwd:     "/some/path"
        );

        var wire   = JsonSerializer.Serialize(cmd, ServerWireOptions);
        var parsed = JsonSerializer.Deserialize(wire, CapacitorJsonContext.Default.LaunchAgentCommand);

        await Assert.That(wire).Contains("\"borrowed\":true");
        await Assert.That(wire).Contains("\"borrow_cwd\":\"/some/path\"");
        await Assert.That(parsed.Borrowed).IsTrue();
        await Assert.That(parsed.BorrowCwd).IsEqualTo("/some/path");
    }

    [Test]
    public async Task Legacy_payload_without_borrowed_fields_deserializes_defaults() {
        // Version skew: an older server that predates the borrow feature never sends borrowed/borrow_cwd. The
        // daemon must still bind the command (positional SignalR binding) and default Borrowed to
        // false / BorrowCwd to null — i.e. behave exactly as an owned-worktree launch.
        const string legacyWire =
            """
            {"agent_id":"legacy03","prompt":null,"model":"opus","effort":null,"repo_path":"/tmp/repo","tools":null,"attachment_ids":null,"vendor":"claude"}
            """;

        var parsed = JsonSerializer.Deserialize(legacyWire, CapacitorJsonContext.Default.LaunchAgentCommand);

        await Assert.That(parsed.Borrowed).IsFalse();
        await Assert.That(parsed.BorrowCwd).IsNull();
        await Assert.That(parsed.RepoPath).IsEqualTo("/tmp/repo");
    }

    [Test]
    public async Task Vendor_field_round_trips_through_json_serializer() {
        var cmd = new LaunchAgentCommand(
            AgentId:       "agent-1",
            Prompt:        null,
            Model:         "claude-sonnet-4-6",
            Effort:        null,
            RepoPath:      "/tmp/repo",
            Tools:         null,
            AttachmentIds: null,
            Vendor:        "codex"
        );

        var json = JsonSerializer.Serialize(cmd, CapacitorJsonContext.Default.LaunchAgentCommand);
        var back = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.LaunchAgentCommand);

        await Assert.That(back.Vendor).IsEqualTo("codex");
    }

    [Test]
    public async Task AgentRunStarted_vendor_serialises_into_json_body() {
        var evt = new AgentRunStarted(
            Prompt: "do a thing",
            Model: "claude-sonnet-4-6",
            Effort: null,
            RepoPath: "/tmp/repo",
            WorktreePath: "/tmp/wt",
            Vendor: "codex"
        );

        var json = JsonSerializer.Serialize(evt, CapacitorJsonContext.Default.AgentRunStarted);
        await Assert.That(json).Contains("codex");
        await Assert.That(json.ToLowerInvariant()).Contains("\"vendor\"");
    }

    [Test]
    public async Task Requester_fields_roundtrip_and_default_null_when_absent() {
        // Old-server payload without the new fields → nulls (wire compat).
        var legacyJson = """{"agent_id":"a1","model":"m","repo_path":"/r","vendor":"claude"}""";
        var legacy = JsonSerializer.Deserialize(legacyJson, CapacitorJsonContext.Default.LaunchAgentCommand);
        await Assert.That(legacy.RequesterUserId).IsNull();
        await Assert.That(legacy.RequesterIsOwner).IsNull();

        // New fields serialize snake_case and roundtrip.
        var cmd = new LaunchAgentCommand(
            AgentId: "a1",
            Prompt: null,
            Model: "m",
            Effort: null,
            RepoPath: "/r",
            Tools: null,
            AttachmentIds: null,
            Vendor: "claude",
            RequesterUserId: "user_x",
            RequesterIsOwner: true
        );
        var json = JsonSerializer.Serialize(cmd, ServerWireOptions);
        await Assert.That(json).Contains("\"requester_user_id\":\"user_x\"");
        await Assert.That(json).Contains("\"requester_is_owner\":true");
        var back = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.LaunchAgentCommand);
        await Assert.That(back.RequesterUserId).IsEqualTo("user_x");
        await Assert.That(back.RequesterIsOwner).IsTrue();
    }

    [Test]
    public async Task InactivityBoundSeconds_roundtrips_and_defaults_null_when_absent() {
        // Task 12 / liveness-supervision spec §3 decision 6: an old server never sends this field —
        // the daemon must default to null (⇒ FindReviewersToReap's no-bound legacy path), never fail
        // to bind the command.
        var legacyJson = """{"agent_id":"a1","model":"m","repo_path":"/r","vendor":"claude"}""";
        var legacy = JsonSerializer.Deserialize(legacyJson, CapacitorJsonContext.Default.LaunchAgentCommand);
        await Assert.That(legacy.InactivityBoundSeconds).IsNull();

        // A new server sends it snake_case and it round-trips.
        var cmd = new LaunchAgentCommand(
            AgentId: "a1",
            Prompt: null,
            Model: "m",
            Effort: null,
            RepoPath: "/r",
            Tools: null,
            AttachmentIds: null,
            Vendor: "claude",
            Kind: LaunchKind.ReviewFlow,
            InactivityBoundSeconds: 180
        );
        var json = JsonSerializer.Serialize(cmd, ServerWireOptions);
        await Assert.That(json).Contains("\"inactivity_bound_seconds\":180");
        var back = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.LaunchAgentCommand);
        await Assert.That(back.InactivityBoundSeconds).IsEqualTo(180);
    }

    [Test]
    public async Task RequesterDisplay_roundtrips_and_defaults_null_when_absent() {
        // An old server never sends this field (issue #481) — the daemon must default to null,
        // never fail to bind the command.
        var legacyJson = """{"agent_id":"a1","model":"m","repo_path":"/r","vendor":"claude"}""";
        var legacy = JsonSerializer.Deserialize(legacyJson, CapacitorJsonContext.Default.LaunchAgentCommand);
        await Assert.That(legacy.RequesterDisplay).IsNull();

        var cmd = new LaunchAgentCommand(
            AgentId: "a1",
            Prompt: null,
            Model: "m",
            Effort: null,
            RepoPath: "/r",
            Tools: null,
            AttachmentIds: null,
            Vendor: "claude",
            RequesterUserId: "github:2821205",
            RequesterDisplay: "Ada Lovelace"
        );
        var json = JsonSerializer.Serialize(cmd, ServerWireOptions);
        await Assert.That(json).Contains("\"requester_display\":\"Ada Lovelace\"");
        var back = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.LaunchAgentCommand);
        await Assert.That(back.RequesterDisplay).IsEqualTo("Ada Lovelace");
        // Display-only: never used for consent matching, which stays on RequesterUserId.
        await Assert.That(back.RequesterUserId).IsEqualTo("github:2821205");
    }
}

/// <summary>
/// Frozen snapshot of <see cref="LaunchAgentCommand"/>'s shape from BEFORE D-c added
/// <c>McpAllowlist</c> — used by <see cref="LaunchAgentCommandWireFormatTests.Old_reader_ignores_mcp_allowlist"/>
/// to prove an old daemon build tolerates the new wire field rather than failing to bind.
/// </summary>
readonly record struct OldLaunchAgentCommand(
        string            AgentId,
        string?           Prompt,
        string            Model,
        string?           Effort,
        string            RepoPath,
        string[]?         Tools,
        string[]?         AttachmentIds,
        string            Vendor,
        LaunchKind        Kind             = LaunchKind.Default,
        ReviewLaunchInfo? Review           = null,
        string?           BaseRef          = null,
        string?           SyncFromRepoRoot = null
    );

/// <summary>
/// Mirrors <see cref="CapacitorJsonContext"/>'s <see cref="JsonSourceGenerationOptionsAttribute"/>
/// exactly (snake_case properties, camelCase string enums) but over the frozen
/// <see cref="OldLaunchAgentCommand"/> shape — a real source-generated reader, not a bare
/// reflection-based <see cref="JsonSerializer"/> default, so the test proves what an actual old
/// AOT-compiled daemon binary would do.
/// </summary>
[JsonSerializable(typeof(OldLaunchAgentCommand))]
[JsonSerializable(typeof(LaunchKind))]
[JsonSerializable(typeof(ReviewLaunchInfo))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UseStringEnumConverter = true
)]
sealed partial class OldWireJsonContext : JsonSerializerContext;
