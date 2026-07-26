using System.Text.Json;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// The flows MCP request DTO carries the optional reviewer-MODEL override on the
/// shared source-generated <see cref="McpJsonContext"/>. These pin the wire shape the v3 transport
/// depends on: a model override emits <c>model</c> + <c>client_flow_protocol_version: 3</c>, while a
/// no-model start stays byte-compatible (no <c>model</c> key at all, protocol 2).
/// </summary>
public class FlowMcpJsonContextTests {
    static StartReviewFlowDto Dto(string? vendor, int? protocolVersion, string? model) => new(
        Kind: "code-review", TargetKind: "pr", TargetRef: "123", TargetTitle: "t",
        Context: "c", Instructions: null, RequestingSessionId: null, RequestingCwd: null,
        RequestingRepoRoot: null, RepoOwner: null, RepoName: null, DaemonName: null,
        RepoPath: null, Mode: null, Async: true,
        Vendor: vendor, ClientFlowProtocolVersion: protocolVersion, Model: model);

    [Test]
    public async Task StartReviewFlowDto_type_info_is_source_generated() {
        // AOT sanity: the DTO resolves through the source-gen context, not a reflection fallback.
        await Assert.That(McpJsonContext.Default.StartReviewFlowDto).IsNotNull();
    }

    [Test]
    public async Task Model_override_serializes_model_vendor_and_protocol_3() {
        var json = JsonSerializer.Serialize(
            Dto(vendor: "claude", protocolVersion: 3, model: "opus"),
            McpJsonContext.Default.StartReviewFlowDto);

        await Assert.That(json).Contains("\"model\":\"opus\"");
        await Assert.That(json).Contains("\"vendor\":\"claude\"");
        await Assert.That(json).Contains("\"client_flow_protocol_version\":3");
    }

    [Test]
    public async Task No_model_start_omits_model_and_keeps_protocol_2() {
        var json = JsonSerializer.Serialize(
            Dto(vendor: null, protocolVersion: 2, model: null),
            McpJsonContext.Default.StartReviewFlowDto);

        // WhenWritingNull byte-compat: an omitted model must not appear on the wire at all.
        await Assert.That(json).DoesNotContain("\"model\"");
        await Assert.That(json).Contains("\"client_flow_protocol_version\":2");
    }
}
