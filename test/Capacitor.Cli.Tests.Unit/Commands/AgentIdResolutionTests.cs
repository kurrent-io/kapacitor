using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class AgentIdResolutionTests {
    static readonly AgentRow[] Agents = [
        new("ab12cd34ef56ab78cd90ef12ab34cd56", "Running", "/repo/one", "agent", "", ""),
        new("ab99887766554433221100aabbccddee", "Running", "/repo/two", "agent", "", ""),
        new("ff00112233445566778899aabbccddee", "Completed", "/repo/three", "agent", "", ""),
    ];

    [Test]
    public async Task Unique_prefix_resolves_to_the_full_id() {
        var (id, err) = AgentCommand.ResolveAgentId(Agents, "ab12");
        await Assert.That(id).IsEqualTo("ab12cd34ef56ab78cd90ef12ab34cd56");
        await Assert.That(err).IsNull();
    }

    [Test]
    public async Task Prefix_matching_is_case_insensitive() {
        var (id, err) = AgentCommand.ResolveAgentId(Agents, "FF00");
        await Assert.That(id).IsEqualTo("ff00112233445566778899aabbccddee");
        await Assert.That(err).IsNull();
    }

    [Test]
    public async Task Ambiguous_prefix_is_an_error_that_names_the_candidates() {
        var (id, err) = AgentCommand.ResolveAgentId(Agents, "ab");
        await Assert.That(id).IsNull();
        await Assert.That(err).Contains("ab12cd34ef56ab78cd90ef12ab34cd56");
        await Assert.That(err).Contains("ab99887766554433221100aabbccddee");
    }

    [Test]
    public async Task No_match_is_an_error() {
        var (id, err) = AgentCommand.ResolveAgentId(Agents, "dead");
        await Assert.That(id).IsNull();
        await Assert.That(err).IsNotNull();
    }

    [Test]
    public async Task Full_32_hex_id_passes_through_even_when_not_listed() {
        // A survivor of a prior daemon incarnation is not in the live list, but the
        // daemon can still reap it by PID record — so a full id must not be filtered out.
        var (id, err) = AgentCommand.ResolveAgentId([], "0123456789abcdef0123456789abcdef");
        await Assert.That(id).IsEqualTo("0123456789abcdef0123456789abcdef");
        await Assert.That(err).IsNull();
    }

    [Test]
    public async Task A_32_char_non_hex_string_is_still_treated_as_a_prefix() {
        var (id, err) = AgentCommand.ResolveAgentId(Agents, new string('z', 32));
        await Assert.That(id).IsNull();
        await Assert.That(err).IsNotNull();
    }

    [Test]
    public async Task An_uppercase_full_id_is_lowercased_so_the_ordinal_daemon_lookup_still_finds_it() {
        // The daemon's own lookups (_agents.TryGetValue, TryStopByPidRecordAsync) are ordinal, so
        // an uppercase full id must be normalized here — the same string truncated to a prefix
        // already resolves fine via the case-insensitive StartsWith path above.
        var (id, err) = AgentCommand.ResolveAgentId([], "0123456789ABCDEF0123456789ABCDEF");
        await Assert.That(id).IsEqualTo("0123456789abcdef0123456789abcdef");
        await Assert.That(err).IsNull();
    }

    [Test]
    public async Task Row_from_a_current_daemon_carries_kind_and_flow() {
        var row = AgentCommand.ParseAgentRow("ab12\tRunning\t/repo\treview-flow\tflow-7f3a\treviewer");
        await Assert.That(row.Id).IsEqualTo("ab12");
        await Assert.That(row.Status).IsEqualTo("Running");
        await Assert.That(row.Repo).IsEqualTo("/repo");
        await Assert.That(row.Kind).IsEqualTo("review-flow");
        await Assert.That(row.FlowRunId).IsEqualTo("flow-7f3a");
        await Assert.That(row.FlowRole).IsEqualTo("reviewer");
    }

    [Test]
    public async Task Row_from_an_older_daemon_defaults_to_an_unprotected_agent() {
        // An older daemon sends three columns. Treating the missing kind as `agent` is what
        // makes the group keep working against it — protection simply does not engage.
        var row = AgentCommand.ParseAgentRow("ab12\tRunning\t/repo");
        await Assert.That(row.Kind).IsEqualTo("agent");
        await Assert.That(row.FlowRunId).IsEqualTo("");
        await Assert.That(row.FlowRole).IsEqualTo("");
        await Assert.That(AgentCommand.IsProtectedKind(row.Kind)).IsFalse();
    }

    [Test]
    public async Task Review_and_review_flow_are_protected_and_agent_is_not() {
        await Assert.That(AgentCommand.IsProtectedKind("review")).IsTrue();
        await Assert.That(AgentCommand.IsProtectedKind("review-flow")).IsTrue();
        await Assert.That(AgentCommand.IsProtectedKind("agent")).IsFalse();
    }
}
