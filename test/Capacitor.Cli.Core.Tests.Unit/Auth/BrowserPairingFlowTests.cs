using Capacitor.Cli.Core.Auth;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

// The loop, the guards and the back-off — everything PairingPoll was extracted out of. Driven over a
// fake channel and a FakeTimeProvider so none of it needs a socket or a wall clock.
public class BrowserPairingFlowTests {
    const string Server  = "https://acme.kcap.ai";
    const string Machine = "machine-1";

    sealed class FakeChannel(FakeTimeProvider clock) : IPairingChannel {
        public MintOutcome Mint { get; set; } = Ok();
        public Queue<PollOutcome> Polls { get; } = new();
        public PollOutcome Tail { get; set; } = new(200, new PairingStatusResponse { Status = "pending" });

        public int PollCount { get; private set; }

        readonly List<TimeSpan> _gaps = [];
        DateTimeOffset _last;

        public IReadOnlyList<TimeSpan> Gaps => _gaps;

        public static MintOutcome Ok(int pollIntervalSeconds = 2) =>
            new(201, new MintPairingResponse {
                PairingId           = "p1",
                UserCode            = "7Q2F-KX9M",
                Secret              = "s3cret",
                ExpiresAt           = clockBase.AddMinutes(15),
                PollIntervalSeconds = pollIntervalSeconds,
                SetupUrl            = $"{Server}/setup?p=p1"
            });

        public Task<MintOutcome> MintAsync(string serverUrl, string machineId, string machineName, CancellationToken ct) {
            _last = clock.GetUtcNow();

            return Task.FromResult(Mint);
        }

        public Task<PollOutcome> PollAsync(string serverUrl, string pairingId, string secret, CancellationToken ct) {
            PollCount++;
            _gaps.Add(clock.GetUtcNow() - _last);
            _last = clock.GetUtcNow();

            return Task.FromResult(Polls.Count > 0 ? Polls.Dequeue() : Tail);
        }

        // Setup completes the pairing at the very end of the wizard, not from the flow.
        public Task<int> CompleteAsync(string serverUrl, string pairingId, string secret, string? accessToken, CancellationToken ct) =>
            Task.FromResult(204);
    }

    sealed class RecordingProgress : IPairingProgress {
        public string? Code { get; private set; }
        public string? Url  { get; private set; }
        public int Ticks    { get; private set; }
        public int WaitEnds { get; private set; }

        public void AwaitingApproval(string userCode, string setupUrl) => (Code, Url) = (userCode, setupUrl);
        public void PollTick()  => Ticks++;
        public void WaitEnded() => WaitEnds++;
    }

    static readonly DateTimeOffset clockBase = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Runs the flow to completion, pumping the fake clock while it waits.
    ///
    /// <para>The flow sleeps via <c>Task.Delay</c> on the injected provider, so a frozen fake never
    /// wakes it — time has to move from outside, exactly as <c>ServiceVerifyStartTests.Drive</c>
    /// does. The step divides every interval the flow uses, so each wake lands on its deadline and
    /// the recorded gaps stay exact rather than approximate.</para>
    /// </summary>
    static async Task<PairingResult> Drive(Task<PairingResult> running, FakeTimeProvider clock) {
        while (!running.IsCompleted) {
            clock.Advance(TimeSpan.FromMilliseconds(250));

            await Task.Yield();
        }

        return await running;
    }

    static (BrowserPairingFlow Flow, FakeChannel Channel, RecordingProgress Progress, FakeTimeProvider Clock, List<string> Opened) Build() {
        var clock    = new FakeTimeProvider(clockBase);
        var channel  = new FakeChannel(clock);
        var progress = new RecordingProgress();
        var opened   = new List<string>();

        return (new BrowserPairingFlow(channel, progress, clock, opened.Add), channel, progress, clock, opened);
    }

    static PollOutcome Approved(string user = "github:4242", string? server = Server) =>
        new(200, new PairingStatusResponse {
            Status = "approved", ServerUrl = server, User = new PairingUser { Id = user }
        });

    // ── THE AVAILABILITY ORACLE ──

    // Every one of these means "no pairing channel here", and the remedy is identical: carry on with
    // the sign-in that already worked. Reporting them would alarm a user with nothing to fix.
    [Test]
    [Arguments(404)]
    [Arguments(401)]
    [Arguments(403)]
    [Arguments(405)]
    public async Task A_server_that_does_not_serve_the_routes_is_unavailable_not_a_failure(int status) {
        var (flow, channel, progress, clock, opened) = Build();
        channel.Mint = new(status, null);

        await Assert.That(await Drive(flow.RunAsync(Server, Machine, "nostromo", default), clock))
            .IsTypeOf<PairingResult.Unavailable>();

        await Assert.That(opened).IsEmpty();
        await Assert.That(progress.Code).IsNull();
    }

    // ── THE MINT GUARD ──

    // A response missing any of these cannot drive the flow. It must land on Failed, which degrades
    // to sign-in — not on Expired, which aborts setup with a message that misdescribes what happened.
    [Test]
    public async Task A_mint_missing_its_expiry_degrades_rather_than_claiming_expiry() {
        var (flow, channel, _, clock, _) = Build();
        channel.Mint = new(201, new MintPairingResponse {
            PairingId = "p1", UserCode = "AAAA-BBBB", Secret = "s", SetupUrl = $"{Server}/setup?p=p1"
        });

        await Assert.That(await Drive(flow.RunAsync(Server, Machine, "nostromo", default), clock))
            .IsTypeOf<PairingResult.Failed>();
    }

    [Test]
    public async Task A_transport_failure_on_mint_says_so() {
        var (flow, channel, _, clock, _) = Build();
        channel.Mint = new(0, null);

        var result = await Drive(flow.RunAsync(Server, Machine, "nostromo", default), clock) as PairingResult.Failed;

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Message).Contains("Could not reach");
    }

    // ── THE CODE AND THE LINK ──

    // Both are mandatory: the terminal's copy of the code is the only thing tying the page to this
    // machine, and nothing can confirm the browser actually opened.
    [Test]
    public async Task The_code_and_the_fallback_link_are_shown_before_any_polling() {
        var (flow, channel, progress, clock, opened) = Build();
        channel.Polls.Enqueue(Approved());

        await Drive(flow.RunAsync(Server, Machine, "nostromo", default), clock);

        await Assert.That(progress.Code).IsEqualTo("7Q2F-KX9M");
        await Assert.That(progress.Url).IsEqualTo($"{Server}/setup?p=p1");
        await Assert.That(opened).IsEquivalentTo([$"{Server}/setup?p=p1"]);
    }

    [Test]
    public async Task The_wait_is_always_closed() {
        var (flow, channel, progress, clock, _) = Build();
        channel.Polls.Enqueue(Approved());

        await Drive(flow.RunAsync(Server, Machine, "nostromo", default), clock);

        await Assert.That(progress.WaitEnds).IsEqualTo(1);
    }

    // ── OUTCOMES ──

    [Test]
    public async Task An_approval_carries_the_approver_back() {
        var (flow, channel, _, clock, _) = Build();
        channel.Polls.Enqueue(Approved());

        var result = await Drive(flow.RunAsync(Server, Machine, "nostromo", default), clock) as PairingResult.Approved;

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.UserId).IsEqualTo("github:4242");
        await Assert.That(result.PairingId).IsEqualTo("p1");
        await Assert.That(result.Secret).IsEqualTo("s3cret");
    }

    [Test]
    public async Task A_denial_is_its_own_outcome() {
        var (flow, channel, _, clock, _) = Build();
        channel.Polls.Enqueue(new(200, new PairingStatusResponse { Status = "denied" }));

        await Assert.That(await Drive(flow.RunAsync(Server, Machine, "nostromo", default), clock))
            .IsTypeOf<PairingResult.Denied>();
    }

    // Untrusted, not Failed: Failed degrades to "carry on with sign-in", which is exactly the thing
    // that must not happen when the response is the one the identity check depends on.
    [Test]
    public async Task An_approval_naming_no_approver_is_untrusted() {
        var (flow, channel, _, clock, _) = Build();
        channel.Polls.Enqueue(new(200, new PairingStatusResponse { Status = "approved", ServerUrl = Server }));

        await Assert.That(await Drive(flow.RunAsync(Server, Machine, "nostromo", default), clock))
            .IsTypeOf<PairingResult.Untrusted>();
    }

    [Test]
    public async Task An_approval_from_another_tenant_is_untrusted() {
        var (flow, channel, _, clock, _) = Build();
        channel.Polls.Enqueue(Approved(server: "https://evil.kcap.ai"));

        await Assert.That(await Drive(flow.RunAsync(Server, Machine, "nostromo", default), clock))
            .IsTypeOf<PairingResult.Untrusted>();
    }

    // ── THE LOOP ──

    // A human who approves instantly should not wait out an interval to be noticed.
    [Test]
    public async Task The_first_poll_happens_before_the_first_sleep() {
        var (flow, channel, _, clock, _) = Build();
        channel.Polls.Enqueue(Approved());

        await Drive(flow.RunAsync(Server, Machine, "nostromo", default), clock);

        await Assert.That(channel.PollCount).IsEqualTo(1);
        await Assert.That(channel.Gaps[0]).IsEqualTo(TimeSpan.Zero);
    }

    [Test]
    public async Task Pending_polls_tick_and_keep_waiting() {
        var (flow, channel, progress, clock, _) = Build();
        channel.Polls.Enqueue(new(200, new PairingStatusResponse { Status = "pending" }));
        channel.Polls.Enqueue(new(200, new PairingStatusResponse { Status = "pending" }));
        channel.Polls.Enqueue(Approved());

        await Drive(flow.RunAsync(Server, Machine, "nostromo", default), clock);

        await Assert.That(channel.PollCount).IsEqualTo(3);
        await Assert.That(progress.Ticks).IsEqualTo(2);
    }

    // The server advertises an interval and enforces 80% of it, so a 429 means back off — and it must
    // still print, or a run of them leaves the terminal frozen with no sign of life.
    [Test]
    public async Task A_slow_down_backs_off_and_still_reports_progress() {
        var (flow, channel, progress, clock, _) = Build();
        channel.Polls.Enqueue(new(429, null));
        channel.Polls.Enqueue(new(429, null));
        channel.Polls.Enqueue(Approved());

        await Drive(flow.RunAsync(Server, Machine, "nostromo", default), clock);

        await Assert.That(progress.Ticks).IsEqualTo(2);
        // 2s advertised, +1s per 429: the second gap is the first back-off, the third the second.
        await Assert.That(channel.Gaps[1]).IsEqualTo(TimeSpan.FromSeconds(3));
        await Assert.That(channel.Gaps[2]).IsEqualTo(TimeSpan.FromSeconds(4));
    }

    // A server-named interval is server-controlled input. Left unclamped, a misconfigured one leaves
    // the terminal on "Waiting…" for however long it said, indistinguishable from a hang.
    [Test]
    public async Task An_absurd_advertised_interval_is_clamped() {
        var (flow, channel, _, clock, _) = Build();
        channel.Mint = FakeChannel.Ok(pollIntervalSeconds: 3600);
        channel.Polls.Enqueue(new(200, new PairingStatusResponse { Status = "pending" }));
        channel.Polls.Enqueue(Approved());

        await Drive(flow.RunAsync(Server, Machine, "nostromo", default), clock);

        await Assert.That(channel.Gaps[1]).IsLessThanOrEqualTo(TimeSpan.FromSeconds(31));
    }

    // expires_at is the SERVER's wall clock. A machine whose clock runs fast would compute a deadline
    // already in the past and give up without polling once — for a pairing that is perfectly alive.
    [Test]
    public async Task A_fast_local_clock_does_not_abandon_a_live_pairing() {
        var clock    = new FakeTimeProvider(clockBase.AddHours(2)); // two hours ahead of the server
        var channel  = new FakeChannel(clock);
        var progress = new RecordingProgress();
        var flow     = new BrowserPairingFlow(channel, progress, clock, _ => { });

        channel.Polls.Enqueue(Approved());

        await Assert.That(await Drive(flow.RunAsync(Server, Machine, "nostromo", default), clock))
            .IsTypeOf<PairingResult.Approved>();

        await Assert.That(channel.PollCount).IsEqualTo(1);
    }

    // The server owns expiry and says 410; this only bounds the loop when that never arrives.
    [Test]
    public async Task A_pairing_nobody_ever_answers_gives_up() {
        var (flow, channel, _, clock, _) = Build();
        channel.Tail = new(200, new PairingStatusResponse { Status = "pending" });

        await Assert.That(await Drive(flow.RunAsync(Server, Machine, "nostromo", default), clock))
            .IsTypeOf<PairingResult.Expired>();
    }

    [Test]
    public async Task A_server_reported_expiry_ends_the_wait() {
        var (flow, channel, _, clock, _) = Build();
        channel.Polls.Enqueue(new(410, null));

        await Assert.That(await Drive(flow.RunAsync(Server, Machine, "nostromo", default), clock))
            .IsTypeOf<PairingResult.Expired>();
    }
}
