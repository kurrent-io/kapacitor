using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit;

public class AgentIdResolutionTests {
    static readonly AgentRow[] Agents = [
        new("ab12cd34ef56ab78cd90ef12ab34cd56", "Running", "/repo/one"),
        new("ab99887766554433221100aabbccddee", "Running", "/repo/two"),
        new("ff00112233445566778899aabbccddee", "Completed", "/repo/three"),
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
}
