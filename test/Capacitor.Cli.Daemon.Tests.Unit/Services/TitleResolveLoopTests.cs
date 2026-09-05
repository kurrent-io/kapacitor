using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// Pins the title resolution ladder: native transcript title first, the server's real title
/// as the authority once it exists, local generation only as the late fallback — and every
/// locally resolved title converges to the server via set-title so web and desktop show the
/// same string. A server title that merely echoes the launch prompt (the watcher's initial
/// truncated-prompt title) counts as "no real title yet".
/// </summary>
public class TitleResolveLoopTests {
    static readonly DateTime T0 = new(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);

    sealed class FakeServerPort : ITitleServerPort {
        public Func<string, string?> Get { get; set; } = _ => null;
        public List<(string SessionId, string Title)> Pushed { get; } = [];
        public bool PushResult { get; set; } = true;

        public Task<string?> GetTitleAsync(string sessionId, CancellationToken ct) => Task.FromResult(Get(sessionId));

        public Task<bool> PushTitleAsync(string sessionId, string title, CancellationToken ct) {
            Pushed.Add((sessionId, title));
            return Task.FromResult(PushResult);
        }
    }

    sealed class Harness {
        public List<TitleAgentView> Agents { get; } = [];
        public List<(string AgentId, string Title)> Applied { get; } = [];
        public FakeServerPort Server { get; } = new();
        public Func<TitleAgentView, string?> Native { get; set; } = _ => null;
        public Func<TitleAgentView, CancellationToken, Task<string?>> Generate { get; set; } =
            (_, _) => Task.FromResult<string?>(null);
        public int GenerateCalls;
        public FakeTimeProvider Time { get; } = new(T0);

        public TitleResolveLoop Build() => new(
            () => Agents,
            (id, title) => Applied.Add((id, title)),
            Server,
            a => Native(a),
            (a, ct) => { Interlocked.Increment(ref GenerateCalls); return Generate(a, ct); },
            Time,
            NullLogger.Instance);
    }

    static TitleAgentView Agent(
            string id = "a1", string vendor = "claude", string? prompt = "Fix the login bug in the auth flow",
            string? sessionId = "sid-1", string? transcript = "/t.jsonl", DateTime? createdAt = null) =>
        new(id, vendor, prompt, sessionId, transcript, createdAt ?? T0);

    [Test]
    public async Task Native_title_is_applied_and_pushed_once() {
        var h = new Harness();
        h.Agents.Add(Agent());
        h.Native = _ => "Native title";
        var loop = h.Build();

        await loop.TickAsync(CancellationToken.None);
        await loop.TickAsync(CancellationToken.None);

        await Assert.That(h.Applied).IsEquivalentTo([("a1", "Native title")]);
        await Assert.That(h.Server.Pushed).IsEquivalentTo([("sid-1", "Native title")]);
    }

    [Test]
    public async Task A_revised_native_title_is_re_applied() {
        var h = new Harness();
        h.Agents.Add(Agent());
        var native = "First cut";
        h.Native = _ => native;
        var loop = h.Build();

        await loop.TickAsync(CancellationToken.None);
        native = "Second cut";
        await loop.TickAsync(CancellationToken.None);

        await Assert.That(h.Applied.Select(a => a.Title)).IsEquivalentTo(["First cut", "Second cut"]);
    }

    [Test]
    public async Task The_servers_real_title_wins_over_native() {
        var h = new Harness();
        h.Agents.Add(Agent());
        h.Native = _ => "Native title";
        h.Server.Get = _ => "Server generated title";
        var loop = h.Build();

        await loop.TickAsync(CancellationToken.None);

        await Assert.That(h.Applied.Last().Title).IsEqualTo("Server generated title");
        await Assert.That(h.Server.Pushed).IsEmpty();
    }

    [Test]
    public async Task A_server_title_echoing_the_prompt_is_not_adopted_and_does_not_block_the_push() {
        var h = new Harness();
        h.Agents.Add(Agent(prompt: "Fix the login bug in the auth flow, then add tests"));
        h.Native = _ => "Native title";
        h.Server.Get = _ => "Fix the login bug in the auth flow, then...";
        var loop = h.Build();

        await loop.TickAsync(CancellationToken.None);

        await Assert.That(h.Applied).IsEquivalentTo([("a1", "Native title")]);
        await Assert.That(h.Server.Pushed).IsEquivalentTo([("sid-1", "Native title")]);
    }

    [Test]
    public async Task An_unrecorded_agent_generates_once_after_the_grace_period() {
        var h = new Harness();
        h.Agents.Add(Agent(sessionId: null));
        h.Generate = (_, _) => Task.FromResult<string?>("Generated title");
        var loop = h.Build();

        await loop.TickAsync(CancellationToken.None);
        await Assert.That(h.Applied).IsEmpty(); // still inside the grace period

        h.Time.Advance(TimeSpan.FromMinutes(6));
        await loop.TickAsync(CancellationToken.None);
        await loop.TickAsync(CancellationToken.None);

        await Assert.That(h.Applied).IsEquivalentTo([("a1", "Generated title")]);
        await Assert.That(h.GenerateCalls).IsEqualTo(1);
        await Assert.That(h.Server.Pushed).IsEmpty(); // no session to converge to
    }

    [Test]
    public async Task A_recorded_agent_generates_and_pushes_when_the_server_stays_silent() {
        var h = new Harness();
        h.Agents.Add(Agent());
        h.Generate = (_, _) => Task.FromResult<string?>("Generated title");
        h.Time.Advance(TimeSpan.FromMinutes(6));
        var loop = h.Build();

        await loop.TickAsync(CancellationToken.None);

        await Assert.That(h.Applied).IsEquivalentTo([("a1", "Generated title")]);
        await Assert.That(h.Server.Pushed).IsEquivalentTo([("sid-1", "Generated title")]);
    }

    [Test]
    public async Task A_recorded_agent_with_a_real_server_title_never_generates() {
        var h = new Harness();
        h.Agents.Add(Agent());
        h.Server.Get = _ => "Server generated title";
        h.Time.Advance(TimeSpan.FromMinutes(30));
        var loop = h.Build();

        await loop.TickAsync(CancellationToken.None);
        await loop.TickAsync(CancellationToken.None);

        await Assert.That(h.GenerateCalls).IsEqualTo(0);
        await Assert.That(h.Applied).IsEquivalentTo([("a1", "Server generated title")]);
    }

    [Test]
    public async Task An_agent_with_a_native_title_never_generates() {
        var h = new Harness();
        h.Agents.Add(Agent());
        h.Native = _ => "Native title";
        h.Time.Advance(TimeSpan.FromMinutes(30));
        var loop = h.Build();

        await loop.TickAsync(CancellationToken.None);

        await Assert.That(h.GenerateCalls).IsEqualTo(0);
    }

    [Test]
    public async Task A_failed_push_is_retried_on_the_next_tick() {
        var h = new Harness();
        h.Agents.Add(Agent());
        h.Native = _ => "Native title";
        h.Server.PushResult = false;
        var loop = h.Build();

        await loop.TickAsync(CancellationToken.None);
        h.Server.PushResult = true;
        await loop.TickAsync(CancellationToken.None);
        await loop.TickAsync(CancellationToken.None);

        await Assert.That(h.Server.Pushed.Count).IsEqualTo(2);
    }

    [Test]
    public async Task A_throwing_lane_does_not_break_the_tick_for_other_agents() {
        var h = new Harness();
        h.Agents.Add(Agent(id: "bad", transcript: "/bad.jsonl"));
        h.Agents.Add(Agent(id: "good", sessionId: "sid-good"));
        h.Native = a => a.Id == "bad" ? throw new IOException("boom") : "Good title";
        var loop = h.Build();

        await loop.TickAsync(CancellationToken.None);

        await Assert.That(h.Applied).IsEquivalentTo([("good", "Good title")]);
    }

    [Test]
    public async Task State_for_departed_agents_is_dropped() {
        var h = new Harness();
        var agent = Agent();
        h.Agents.Add(agent);
        h.Native = _ => "Native title";
        var loop = h.Build();

        await loop.TickAsync(CancellationToken.None);
        h.Agents.Clear();
        await loop.TickAsync(CancellationToken.None);
        h.Agents.Add(agent);
        await loop.TickAsync(CancellationToken.None);

        // Fresh state after re-appearance: the title is applied (and pushed) again.
        await Assert.That(h.Applied.Count).IsEqualTo(2);
    }
}
