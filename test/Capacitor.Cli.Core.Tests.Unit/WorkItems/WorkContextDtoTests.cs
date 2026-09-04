using System.Text.Json;
using Capacitor.Cli.Core.WorkItems;

namespace Capacitor.Cli.Core.Tests.Unit.WorkItems;

/// The three server shapes the sidebar reads, pinned against literal server-shaped bodies, plus
/// the source-generated metadata for each root: a round trip alone would pass under reflection
/// and only fail on the AOT binary.
public class WorkContextDtoTests {
    [Test]
    public async Task Assignments_deserialize_from_the_server_shape_and_ignore_extra_members() {
        const string body = """[{"work_item_id":"w1","label":"WK-2198 — Desktop shell: work-context sidebar","source":"mcp","confidence":1.0,"is_primary":true,"future":{"x":1}}]""";

        var rows = JsonSerializer.Deserialize(body, CapacitorJsonContext.Default.ListSessionWorkItemAssignmentDto)!;

        await Assert.That(rows).Count().IsEqualTo(1);
        await Assert.That(rows[0].WorkItemId).IsEqualTo("w1");
        await Assert.That(rows[0].Label).IsEqualTo("WK-2198 — Desktop shell: work-context sidebar");
        await Assert.That(rows[0].Source).IsEqualTo("mcp");
        await Assert.That(rows[0].IsPrimary).IsTrue();
    }

    [Test]
    public async Task Topology_deserializes_parts_relations_cycle_and_a_null_item() {
        const string body = """{"parts":[{"work_item_id":"p1","title":"Move the override","ordinal":0}],"part_of":{"work_item_id":"w0","title":"Parent"},"blocks":[],"blocked_by":[{"work_item_id":"b1","title":"Pin the helper"}],"cycle":"none","item":null}""";

        var topology = JsonSerializer.Deserialize(body, CapacitorJsonContext.Default.WorkItemTopologyDto)!;

        await Assert.That(topology.Parts[0].Title).IsEqualTo("Move the override");
        await Assert.That(topology.PartOf!.Title).IsEqualTo("Parent");
        await Assert.That(topology.BlockedBy[0].WorkItemId).IsEqualTo("b1");
        await Assert.That(topology.Cycle).IsEqualTo("none");
        await Assert.That(topology.Item).IsNull();
    }

    [Test]
    public async Task Summary_deserializes_the_subset_the_pane_reads_and_tolerates_the_rest() {
        const string body = """{"session_id":"s1","title":"t","vendor":"claude","model":"claude-opus-5","status":"active","cwd":"/repo","repo_branch":"feature/x","repo_owner":"kurrent-io","repo_name":"kcap-cli","pr_number":629,"pr_url":"https://github.com/kurrent-io/kcap-cli/pull/629","pr_title":"Pin the env scope","repositories":[{"repo_hash":"h","owner":"kurrent-io","repo_name":"kcap-cli","branch":"feature/x","is_primary":true,"first_seen_at":"2026-09-01T00:00:00Z"}],"pull_requests":[{"repo_hash":"h","owner":"kurrent-io","repo_name":"kcap-cli","number":629,"url":"https://github.com/kurrent-io/kcap-cli/pull/629","title":"Pin the env scope","head_ref":"feature/x"}],"stats":{"events":3}}""";

        var summary = JsonSerializer.Deserialize(body, CapacitorJsonContext.Default.SessionSummaryDto)!;

        await Assert.That(summary.SessionId).IsEqualTo("s1");
        await Assert.That(summary.RepoBranch).IsEqualTo("feature/x");
        await Assert.That(summary.PrNumber).IsEqualTo(629);
        await Assert.That(summary.Repositories[0].Branch).IsEqualTo("feature/x");
        await Assert.That(summary.PullRequests[0].Number).IsEqualTo(629);
        await Assert.That(summary.PullRequests[0].HeadRef).IsEqualTo("feature/x");
    }

    [Test]
    public async Task A_null_list_member_deserializes_to_empty_rather_than_null() {
        const string topologyBody = """{"parts":null,"part_of":null,"blocks":null,"blocked_by":null,"cycle":"none","item":null}""";
        const string summaryBody = """{"session_id":"s1","repositories":null,"pull_requests":null}""";

        var topology = JsonSerializer.Deserialize(topologyBody, CapacitorJsonContext.Default.WorkItemTopologyDto)!;
        var summary = JsonSerializer.Deserialize(summaryBody, CapacitorJsonContext.Default.SessionSummaryDto)!;

        await Assert.That(topology.Parts).IsEmpty();
        await Assert.That(topology.Blocks).IsEmpty();
        await Assert.That(topology.BlockedBy).IsEmpty();
        await Assert.That(summary.Repositories).IsEmpty();
        await Assert.That(summary.PullRequests).IsEmpty();
    }

    [Test]
    public async Task Error_body_parses() {
        var error = JsonSerializer.Deserialize("""{"error":"work_items_not_in_plan","message":"Upgrade the plan."}""", CapacitorJsonContext.Default.WorkItemErrorDto)!;

        await Assert.That(error.Error).IsEqualTo("work_items_not_in_plan");
        await Assert.That(error.Message).IsEqualTo("Upgrade the plan.");
    }

    [Test]
    public async Task Every_root_the_client_reads_has_generated_metadata() {
        await Assert.That(CapacitorJsonContext.Default.GetTypeInfo(typeof(List<SessionWorkItemAssignmentDto>))).IsNotNull();
        await Assert.That(CapacitorJsonContext.Default.GetTypeInfo(typeof(WorkItemTopologyDto))).IsNotNull();
        await Assert.That(CapacitorJsonContext.Default.GetTypeInfo(typeof(SessionSummaryDto))).IsNotNull();
        await Assert.That(CapacitorJsonContext.Default.GetTypeInfo(typeof(WorkItemErrorDto))).IsNotNull();
    }
}
