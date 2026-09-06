using System.Net;
using System.Net.Http.Json;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// The input-wait relay: a PTY vendor's hook tells the daemon its turn ended or a new one began,
/// and the bridge hands the attributed agent's verdict to the orchestrator.
public class LocalPermissionBridgeInputWaitTests {
    const string Session = "6ba7b8109dad11d180b400c04fd430c8";

    sealed class Harness : IAsyncDisposable {
        public LocalPermissionBridge Bridge { get; }
        public HttpClient Client { get; } = new() { Timeout = TimeSpan.FromSeconds(30) };
        public List<(string AgentId, bool Waiting)> Seen { get; } = [];

        public Harness(string? attributeTo = "agent-1") {
            Bridge = new LocalPermissionBridge(new FakeServerConnection(respond: null), NullLogger<LocalPermissionBridge>.Instance) {
                AttributeHandler = attributeTo is null ? _ => null : _ => new AttributedAgent(attributeTo),
                InputWaitHandler = (id, waiting) => Seen.Add((id, waiting)),
            };
        }

        public Task StartAsync() => Bridge.StartAsync(CancellationToken.None);

        public Task<HttpResponseMessage> PostAsync(object body, string vendor = "claude", string? token = null) {
            var baseUrl = token is null ? Bridge.BaseUrl! : $"http://127.0.0.1:{new Uri(Bridge.BaseUrl!).Port}/{token}";
            return Client.PostAsync($"{baseUrl}/{vendor}/input-wait", JsonContent.Create(body));
        }

        public async ValueTask DisposeAsync() { await Bridge.DisposeAsync(); Client.Dispose(); }
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeInputWaitTests))]
    public async Task A_stop_relay_marks_the_attributed_agent_awaiting_input() {
        await using var h = new Harness();
        await h.StartAsync();

        var response = await h.PostAsync(new { session_id = Session, agent_id = "agent-1", cwd = "/repo", waiting = true });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(h.Seen.Single()).IsEqualTo(("agent-1", true));
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeInputWaitTests))]
    public async Task A_prompt_relay_clears_it_for_codex_too() {
        await using var h = new Harness();
        await h.StartAsync();

        var response = await h.PostAsync(new { session_id = Session, agent_id = "agent-1", waiting = false }, vendor: "codex");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(h.Seen.Single()).IsEqualTo(("agent-1", false));
    }

    /// A relay the ladder cannot place is not an error the hook can act on, so it is acknowledged
    /// and dropped rather than refused.
    [Test, NotInParallel(nameof(LocalPermissionBridgeInputWaitTests))]
    public async Task An_unattributed_relay_is_acknowledged_and_dropped() {
        await using var h = new Harness(attributeTo: null);
        await h.StartAsync();

        var response = await h.PostAsync(new { session_id = Session, waiting = true });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(h.Seen).IsEmpty();
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeInputWaitTests))]
    public async Task A_relay_without_a_session_or_a_verdict_is_a_bad_request() {
        await using var h = new Harness();
        await h.StartAsync();

        var noSession = await h.PostAsync(new { agent_id = "agent-1", waiting = true });
        var noVerdict = await h.PostAsync(new { session_id = Session, agent_id = "agent-1" });

        await Assert.That(noSession.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(noVerdict.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(h.Seen).IsEmpty();
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeInputWaitTests))]
    public async Task A_body_that_is_not_an_object_is_a_bad_request() {
        await using var h = new Harness();
        await h.StartAsync();

        var array  = await h.PostAsync(new[] { 1 });
        var scalar = await h.PostAsync(true);

        await Assert.That(array.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(scalar.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(h.Seen).IsEmpty();
    }

    /// Only the PTY vendors relay through hooks; every other runtime attests its own turns.
    [Test, NotInParallel(nameof(LocalPermissionBridgeInputWaitTests))]
    public async Task A_vendor_that_attests_its_own_turns_has_no_relay_route() {
        await using var h = new Harness();
        await h.StartAsync();

        var response = await h.PostAsync(new { session_id = Session, agent_id = "agent-1", waiting = true }, vendor: "cursor");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(h.Seen).IsEmpty();
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeInputWaitTests))]
    public async Task An_unknown_token_has_no_relay_route() {
        await using var h = new Harness();
        await h.StartAsync();

        var response = await h.PostAsync(new { session_id = Session, agent_id = "agent-1", waiting = true }, token: "nope");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(h.Seen).IsEmpty();
    }
}
