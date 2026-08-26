using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.FirstRun;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Core.Tests.Unit.FirstRun;

// The loop, the guards and the backoff — everything FirstRunFlowPoll was extracted out of. Driven
// over a fake channel and a FakeTimeProvider, so none of it needs a socket or a wall clock.
public class BrowserFirstRunFlowTests {
    const string Server = "https://acme.kcap.ai";

    static readonly DateTimeOffset ClockBase = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    /// <summary>An order of events, so "created before opened" is assertable rather than inferred.</summary>
    sealed class Log {
        readonly List<string> _entries = [];

        public IReadOnlyList<string> Entries => _entries;

        public void Add(string entry) => _entries.Add(entry);
    }

    sealed class FakeChannel(Log log, FakeTimeProvider? clock = null) : IFirstRunFlowChannel {
        public Queue<FirstRunCreateOutcome> Creates { get; } = new();
        public Queue<FirstRunPollOutcome>   Polls   { get; } = new();

        public FirstRunPollOutcome Tail { get; set; } = new(200, Running());

        public List<string>                CreatedIds { get; } = [];
        public List<FirstRunMachineReport> Reports    { get; } = [];
        public List<DateTimeOffset> PollTimes  { get; } = [];
        public int                 PollCount  { get; private set; }

        public List<ReportFirstRunMachineActionRequest> ActionReports { get; } = [];

        /// <summary>Status for each report in turn; the last one repeats. A non-2xx is how the retry
        /// path is driven.</summary>
        public Queue<int> ReportStatuses { get; } = new();

        public Task<FirstRunCreateOutcome> CreateAsync(
                string serverUrl, string flowId, FirstRunMachineReport report, CancellationToken ct) {
            log.Add("create");
            CreatedIds.Add(flowId);
            Reports.Add(report);

            var outcome = Creates.Count > 0 ? Creates.Dequeue() : new FirstRunCreateOutcome(200, Running());

            // The real server echoes the id it was sent. A canned body carrying a different one is
            // the mismatch case, and is set up explicitly by the test that wants it.
            return Task.FromResult(outcome.Body is { FlowId: "" }
                ? outcome with { Body = outcome.Body with { FlowId = flowId } }
                : outcome);
        }

        public Task<FirstRunPollOutcome> PollAsync(string serverUrl, string flowId, CancellationToken ct) {
            PollCount++;
            log.Add("poll");
            if (clock is not null) PollTimes.Add(clock.GetUtcNow());

            var outcome = Polls.Count > 0 ? Polls.Dequeue() : Tail;

            // The same echo as the create path: a canned body with an empty id becomes the id asked
            // for, and one carrying a different id is the mismatch case the test set up.
            return Task.FromResult(outcome.Body is { FlowId: "" }
                ? outcome with { Body = outcome.Body with { FlowId = flowId } }
                : outcome);
        }

        public Task<FirstRunActionReportOutcome> ReportMachineActionAsync(
                string serverUrl, string flowId, ReportFirstRunMachineActionRequest report, CancellationToken ct) {
            log.Add("report");
            ActionReports.Add(report);

            return Task.FromResult(new FirstRunActionReportOutcome(
                ReportStatuses.Count > 0 ? ReportStatuses.Dequeue() : 200));
        }
    }

    sealed class RecordingProgress(Log log) : IFirstRunFlowProgress {
        public string? Url { get; private set; }
        public int Ticks    { get; private set; }
        public int WaitEnds { get; private set; }

        public List<string> Performing { get; } = [];

        public void Opening(string setupUrl) {
            Url = setupUrl;
            log.Add("open");
        }

        public void PollTick()  => Ticks++;
        public void WaitEnded() => WaitEnds++;

        public void PerformingAction(string capability) {
            log.Add("warn");
            Performing.Add(capability);
        }
    }

    /// <summary>A host that can act on the machine. <c>Results</c> is consumed in turn so a retry can be
    /// given a different answer from the first attempt.</summary>
    sealed class FakeActions(Log log, params string[] capabilities) : IFirstRunMachineActions {
        public IReadOnlyCollection<string> Capabilities { get; } = capabilities;

        public Queue<FirstRunMachineActionResult> Results { get; } = new();
        public List<string>                       Performed { get; } = [];

        /// <summary>Set to throw out of PerformAsync, which the loop has to turn into a reported failure
        /// rather than an unanswered request.</summary>
        public Exception? Throws { get; set; }

        public Task<FirstRunMachineActionResult> PerformAsync(string capability, CancellationToken ct) {
            log.Add("perform");
            Performed.Add(capability);

            if (Throws is { } ex) throw ex;

            return Task.FromResult(Results.Count > 0
                ? Results.Dequeue()
                : new FirstRunMachineActionResult(FirstRunMachineActionOutcomes.Installed, null));
        }
    }

    static FirstRunFlowResponse Running() => new() {
        FlowId    = "",
        Step      = "Agents",
        CanFinish = true,
        Steps     = new() { ["SignIn"] = "Completed", ["Agents"] = "Active", ["Import"] = "Pending", ["Done"] = "Pending" }
    };

    static FirstRunFlowResponse Done() => new() {
        FlowId    = "",
        Step      = "Done",
        CanFinish = true,
        Steps     = new() {
            ["SignIn"] = "Completed", ["Agents"] = "Completed", ["Import"] = "Skipped", ["Done"] = "Completed"
        }
    };

    /// <summary>A keyboard with ONE keypress: it appears when first looked for at or after
    /// <paramref name="pressAfter"/> (zero means it is already down when the wait starts), and is
    /// gone once drained — a real press, not a flag that re-presses at every look.</summary>
    sealed class FakeKeys(bool canWatch, int pressAfter = int.MaxValue) : IKeyWatcher {
        int  _looks;
        bool _armed;   // the single press has been consumed
        bool _pressed; // the press is waiting to be drained

        public int Drains { get; private set; }

        public bool CanWatch => canWatch;

        public bool KeyAvailable {
            get {
                if (_pressed) return true;
                if (_armed) return false;

                if (_looks++ < pressAfter) return false;

                _armed = true;

                return _pressed = true;
            }
        }

        public char ReadKey() => ' ';

        public void Drain() {
            Drains++;
            _pressed = false;
        }
    }

    sealed record Harness(
        BrowserFirstRunFlow Flow,
        FakeChannel         Channel,
        RecordingProgress   Progress,
        FakeTimeProvider    Clock,
        Log                 Log,
        List<string>        Opened,
        FakeKeys            Keys,
        FakeActions?        Actions);

    // No keyboard by default: the escape hatch is one test's subject, and left live it would read the
    // host's own console, where a stray keypress during a CI run would end an unrelated test's wait.
    /// <param name="capabilities">Non-null gives the flow a host that can act, advertising exactly these.
    /// The fake shares the harness's log, so "performed before reported" is assertable rather than inferred.</param>
    static Harness Build(FakeKeys? keys = null, string[]? capabilities = null) {
        var log      = new Log();
        var clock    = new FakeTimeProvider(ClockBase);
        var channel  = new FakeChannel(log, clock);
        var progress = new RecordingProgress(log);
        var browser  = new RecordingBrowser();
        var actions  = capabilities is null ? null : new FakeActions(log, capabilities);

        keys ??= new FakeKeys(canWatch: false);

        return new(
            new BrowserFirstRunFlow(channel, progress, browser, clock, keys, actions),
            channel, progress, clock, log, browser.Urls, keys, actions);
    }

    static readonly string[] PathShimOnly = [FirstRunMachineCapabilities.PathShim];

    /// <summary>
    /// Runs the flow, pumping the fake clock while it waits.
    ///
    /// <para>The loop sleeps via <c>Task.Delay</c> on the injected provider, so a frozen fake never
    /// wakes it — time has to move from outside. The step matches the delay slices' granularity, so
    /// every wake lands exactly on a slice boundary.</para>
    /// </summary>
    static async Task<FirstRunFlowResult> Drive(Task<FirstRunFlowResult> running, FakeTimeProvider clock) {
        while (!running.IsCompleted) {
            clock.Advance(TimeSpan.FromMilliseconds(200));

            await Task.Yield();
        }

        return await running;
    }

    static readonly FirstRunMachineReport Report = new(
        "nostromo", "machine-1",
        new Dictionary<string, FirstRunHarnessReport> {
            ["claude"] = new() { BinaryOnPath = true, ConfigFound = false, AlreadyWired = false }
        },
        ["cursor"], LoginShellFindsCli: false);

    static Task<FirstRunFlowResult> Run(Harness h) =>
        Drive(h.Flow.RunAsync(Server, Report, CancellationToken.None), h.Clock);

    [Test]
    public async Task Creates_the_flow_BEFORE_opening_the_browser() {
        // The whole point of the ticket. Reversed, the first browser to open the link owns the flow,
        // and the server's ownership check has nothing to check against until one turns up.
        var h = Build();
        h.Channel.Polls.Enqueue(new(200, Done()));

        await Run(h);

        await Assert.That(h.Log.Entries[0]).IsEqualTo("create");
        await Assert.That(h.Log.Entries[1]).IsEqualTo("open");
    }

    // The create is the report's ONLY carrier: detection needs no auth and has already run, and the
    // Agents screen must find its rows populated rather than waiting on a second round trip. A retry
    // on a taken id carries it again, or the flow that survives is the one with no machine behind it.
    [Test]
    public async Task Carries_the_machine_report_on_every_create_attempt() {
        var h = Build();
        h.Channel.Creates.Enqueue(new(409, null));
        h.Channel.Polls.Enqueue(new(200, Done()));

        await Run(h);

        await Assert.That(h.Channel.Reports.Count).IsEqualTo(2);

        // Every field, not just the name: a retry that rebuilt an empty report would keep the machine
        // tag and lose exactly what the screen renders from.
        await Assert.That(h.Channel.Reports.All(r => ReferenceEquals(r, Report))).IsTrue();
    }

    [Test]
    public async Task Opens_the_setup_url_it_composed_itself() {
        var h = Build();
        h.Channel.Polls.Enqueue(new(200, Done()));

        await Run(h);

        var id = h.Channel.CreatedIds.Single();

        // Composed locally from an origin already probed and signed in to, which is why there is no
        // origin check here to match the retired pairing's: no server-supplied URL ever reaches the
        // shell-executed open.
        await Assert.That(h.Opened.Single()).IsEqualTo($"{Server}/setup?s={id}");
        await Assert.That(h.Progress.Url).IsEqualTo(h.Opened.Single());
    }

    [Test]
    public async Task Sends_a_flow_id_the_server_will_accept() {
        var h = Build();
        h.Channel.Polls.Enqueue(new(200, Done()));

        await Run(h);

        await Assert.That(h.Channel.CreatedIds.Single()).Length().IsEqualTo(22);
    }

    [Test]
    public async Task Finishes_when_every_step_has_settled() {
        var h = Build();
        h.Channel.Polls.Enqueue(new(200, Running()));
        h.Channel.Polls.Enqueue(new(200, Done()));

        var result = await Run(h);

        await Assert.That(result).IsTypeOf<FirstRunFlowResult.Finished>();
        await Assert.That(h.Channel.PollCount).IsEqualTo(2);
        await Assert.That(h.Progress.WaitEnds).IsEqualTo(1);
    }

    [Test]
    public async Task Polls_once_before_its_first_sleep() {
        // A flow the browser has already finished — a resumed link, or a tab quicker than this
        // process — should not wait out an interval to be noticed.
        var h = Build();
        h.Channel.Polls.Enqueue(new(200, Done()));

        await Run(h);

        await Assert.That(h.Channel.PollCount).IsEqualTo(1);
        await Assert.That(h.Clock.GetUtcNow()).IsEqualTo(ClockBase);
    }

    [Test]
    [Arguments(404)]
    [Arguments(401)]
    [Arguments(403)]
    [Arguments(405)]
    public async Task Reads_a_missing_route_as_unavailable__and_never_opens_a_browser(int status) {
        // The routes are mapped only on a tenant that has the flow turned on, so their absence is a
        // fact to observe rather than a server version to guess at. A gateway answering 401/403/405
        // on a route it does not know is indistinguishable from that.
        var h = Build();
        h.Channel.Creates.Enqueue(new(status, null));

        var result = await Run(h);

        await Assert.That(result).IsTypeOf<FirstRunFlowResult.Unavailable>();
        await Assert.That(h.Opened).IsEmpty();
        await Assert.That(h.Channel.PollCount).IsEqualTo(0);
    }

    [Test]
    public async Task Reports_a_429_with_the_servers_own_retry_after() {
        var h = Build();
        h.Channel.Creates.Enqueue(new(429, null, TimeSpan.FromMinutes(10)));

        var result = await Run(h);

        await Assert.That(result).IsTypeOf<FirstRunFlowResult.RateLimited>();
        await Assert.That(((FirstRunFlowResult.RateLimited)result).RetryAfter).IsEqualTo(TimeSpan.FromMinutes(10));
        await Assert.That(h.Opened).IsEmpty();
    }

    [Test]
    public async Task Falls_back_to_ten_minutes_when_a_429_carries_no_retry_after() {
        var h = Build();
        h.Channel.Creates.Enqueue(new(429, null));

        var result = await Run(h);

        await Assert.That(((FirstRunFlowResult.RateLimited)result).RetryAfter).IsEqualTo(TimeSpan.FromMinutes(10));
    }

    [Test]
    public async Task Retries_a_409_with_a_FRESH_id() {
        // 409 means the id belongs to someone else, not that the credentials are wrong — which is
        // exactly why the server chose that status over a 403. Retrying the SAME id would loop.
        var h = Build();
        h.Channel.Creates.Enqueue(new(409, null));
        h.Channel.Polls.Enqueue(new(200, Done()));

        var result = await Run(h);

        await Assert.That(result).IsTypeOf<FirstRunFlowResult.Finished>();
        await Assert.That(h.Channel.CreatedIds).Count().IsEqualTo(2);
        await Assert.That(h.Channel.CreatedIds[0]).IsNotEqualTo(h.Channel.CreatedIds[1]);
    }

    [Test]
    public async Task Gives_up_after_three_conflicting_ids() {
        var h = Build();

        for (var i = 0; i < 4; i++) h.Channel.Creates.Enqueue(new(409, null));

        var result = await Run(h);

        await Assert.That(result).IsTypeOf<FirstRunFlowResult.Failed>();
        await Assert.That(h.Channel.CreatedIds).Count().IsEqualTo(3);
        await Assert.That(h.Opened).IsEmpty();
    }

    [Test]
    public async Task Refuses_a_create_that_answers_about_a_different_flow() {
        // Impossible against the server this was written for, which is why a disagreement is worth
        // stopping on rather than polling an id this process never generated.
        var h = Build();
        h.Channel.Creates.Enqueue(new(200, Running() with { FlowId = "someoneelsesflowid1234" }));

        var result = await Run(h);

        await Assert.That(result).IsTypeOf<FirstRunFlowResult.Failed>();
        await Assert.That(h.Opened).IsEmpty();
    }

    [Test]
    public async Task Reports_a_transport_failure_on_create_as_unreachable() {
        var h = Build();
        h.Channel.Creates.Enqueue(new(0, null));

        var result = await Run(h);

        await Assert.That(result).IsTypeOf<FirstRunFlowResult.Failed>();
        await Assert.That(((FirstRunFlowResult.Failed)result).Message).Contains("reach");
    }

    [Test]
    public async Task Reports_a_200_create_with_an_unreadable_body_as_failed() {
        // Distinct from a refusal: the server answered, and the reply was not readable by this build.
        // The message must not quote the success status as though the server rejected the request.
        var h = Build();
        h.Channel.Creates.Enqueue(new(200, null));

        var result = await Run(h);

        await Assert.That(result).IsTypeOf<FirstRunFlowResult.Failed>();
        await Assert.That(((FirstRunFlowResult.Failed)result).Message).Contains("could not be read");
        await Assert.That(h.Opened).IsEmpty();
    }

    [Test]
    public async Task Ends_on_a_410() {
        var h = Build();
        h.Channel.Polls.Enqueue(new(200, Running()));
        h.Channel.Polls.Enqueue(new(410, null));

        var result = await Run(h);

        await Assert.That(result).IsTypeOf<FirstRunFlowResult.Expired>();
        await Assert.That(h.Progress.WaitEnds).IsEqualTo(1);
    }

    [Test]
    public async Task Ends_on_a_404_rather_than_polling_a_flow_that_will_never_be_ours() {
        var h = Build();
        h.Channel.Polls.Enqueue(new(404, null));

        var result = await Run(h);

        await Assert.That(result).IsTypeOf<FirstRunFlowResult.Failed>();
    }

    [Test]
    public async Task Ends_on_a_401_with_a_message_of_its_own() {
        // Distinct from a 404's: the authenticated client refreshes on a 401 once, so meeting this at
        // all means the refresh failed — the remedy is a re-login, not a new link, and the copy says so.
        var h = Build();
        h.Channel.Polls.Enqueue(new(401, null));

        var result = await Run(h);

        await Assert.That(result).IsTypeOf<FirstRunFlowResult.Failed>();
        await Assert.That(((FirstRunFlowResult.Failed)result).Message).Contains("kcap login");
    }

    [Test]
    public async Task Keeps_waiting_through_a_5xx_and_a_transport_blip() {
        var h = Build();
        h.Channel.Polls.Enqueue(new(500, null));
        h.Channel.Polls.Enqueue(new(0,   null));
        h.Channel.Polls.Enqueue(new(200, Done()));

        var result = await Run(h);

        await Assert.That(result).IsTypeOf<FirstRunFlowResult.Finished>();
        await Assert.That(h.Progress.Ticks).IsEqualTo(2);
    }

    [Test]
    public async Task Backs_off_on_a_429_and_keeps_polling() {
        var h = Build();
        h.Channel.Polls.Enqueue(new(429, null));
        h.Channel.Polls.Enqueue(new(200, Done()));

        var result = await Run(h);

        await Assert.That(result).IsTypeOf<FirstRunFlowResult.Finished>();
        // Two polls, and the gap between them longer than the base interval it started on.
        await Assert.That(h.Clock.GetUtcNow() - ClockBase).IsGreaterThan(TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task Gives_up_after_its_own_budget__not_the_flows_twelve_hours() {
        // The commonest way this ends unfinished is a closed tab, and the flow's TTL is sized for a
        // link surviving a working day rather than for a terminal sitting open on one. The backstop
        // is not extended: no poll fires once the deadline has passed.
        var h = Build();

        var result = await Run(h);

        await Assert.That(result).IsTypeOf<FirstRunFlowResult.Abandoned>();
        await Assert.That(h.Clock.GetUtcNow() - ClockBase).IsLessThanOrEqualTo(TimeSpan.FromMinutes(31));
        await Assert.That(h.Channel.PollTimes[^1]).IsLessThan(ClockBase + TimeSpan.FromMinutes(30));
        await Assert.That(((FirstRunFlowResult.Abandoned)result).View).IsNotNull();
        await Assert.That(h.Progress.WaitEnds).IsEqualTo(1);
    }

    [Test]
    public async Task A_keypress_during_the_wait_ends_it_without_waiting_out_the_budget() {
        // The answer to a closed tab. Thirty minutes of dots is a backstop for a terminal nobody is
        // sitting at, not something to make a person who IS sitting there watch. The press lands
        // while the first interval's delay is being slept, not on the pre-wait drain.
        var h = Build(new FakeKeys(canWatch: true, pressAfter: 2));

        var result = await Run(h);

        await Assert.That(result).IsTypeOf<FirstRunFlowResult.Dismissed>();
        await Assert.That(h.Clock.GetUtcNow() - ClockBase).IsLessThan(TimeSpan.FromMinutes(1));
        await Assert.That(h.Progress.WaitEnds).IsEqualTo(1);
    }

    [Test]
    public async Task A_keypress_that_preceded_the_wait_is_drained__not_treated_as_a_dismiss() {
        // A byte left in stdin from an earlier step — the Return that confirmed "Logged in as …" —
        // is not an answer to "press any key to carry on here". It is drained once before the prompt
        // renders, and the flow goes on polling rather than dismissing on it.
        var h = Build(new FakeKeys(canWatch: true, pressAfter: 0));

        var result = await Run(h);

        await Assert.That(h.Keys.Drains).IsEqualTo(1);
        await Assert.That(result).IsTypeOf<FirstRunFlowResult.Abandoned>();
    }

    [Test]
    public async Task A_keypress_made_in_response_to_the_prompt_is_a_real_dismissal() {
        // The pre-wait drain exists for keys that preceded the leg; a key pressed after the prompt
        // has rendered is a genuine "carry on here" and must dismiss — not be drained as stale, as
        // the sibling test's pre-prompt key is. The one drain here is the dismissal's own, so the
        // key's trailing Return is not the next prompt's answer.
        var h = Build(new FakeKeys(canWatch: true, pressAfter: 1));

        var result = await Run(h);

        await Assert.That(result).IsTypeOf<FirstRunFlowResult.Dismissed>();
        await Assert.That(h.Keys.Drains).IsEqualTo(1);
    }

    [Test]
    public async Task A_keypress_during_a_backoff_delay_ends_the_wait_promptly() {
        // A 429 widens the gap; a key pressed while that longer delay is being slept must still end
        // the wait within a slice, not after the whole widened interval.
        var h = Build(new FakeKeys(canWatch: true, pressAfter: 4));
        h.Channel.Polls.Enqueue(new(429, null));

        var result = await Run(h);

        await Assert.That(result).IsTypeOf<FirstRunFlowResult.Dismissed>();
        await Assert.That(h.Channel.PollCount).IsEqualTo(1);
        await Assert.That(h.Clock.GetUtcNow() - ClockBase).IsLessThan(TimeSpan.FromSeconds(30));
    }

    [Test]
    public async Task Rejects_a_poll_that_answers_about_a_different_flow() {
        // The create path's guard, applied to the poll: the server echoes the id, so a disagreement
        // is a malformed or misrouted response — not something to report as this flow's outcome.
        var h = Build();
        h.Channel.Polls.Enqueue(new(200, Running() with { FlowId = "someoneelsesflowid1234" }));

        var result = await Run(h);

        await Assert.That(result).IsTypeOf<FirstRunFlowResult.Failed>();
        await Assert.That(((FirstRunFlowResult.Failed)result).Message).Contains("different setup link");
    }

    [Test]
    public async Task Widens_the_gap_after_an_unhappy_poll_and_snaps_back_after_a_good_one() {
        // The 2s cadence is for a healthy flow with a human clicking; an unhappy poll doubles the
        // gap so a down or rate-limiting server is not hammered, and a good state restores the cadence.
        var h = Build();
        h.Channel.Polls.Enqueue(new(0,   null));        // transport blip → gap doubles
        h.Channel.Polls.Enqueue(new(200, Running()));   // healthy → gap back to base
        h.Channel.Polls.Enqueue(new(200, Done()));

        await Run(h);

        await Assert.That(h.Channel.PollTimes[1] - h.Channel.PollTimes[0]).IsEqualTo(TimeSpan.FromSeconds(4));
        await Assert.That(h.Channel.PollTimes[2] - h.Channel.PollTimes[1]).IsEqualTo(TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task Honours_a_poll_429s_retry_after() {
        var h = Build();
        h.Channel.Polls.Enqueue(new(429, null, TimeSpan.FromSeconds(10)));
        h.Channel.Polls.Enqueue(new(200, Done()));

        await Run(h);

        await Assert.That(h.Channel.PollTimes[1] - h.Channel.PollTimes[0]).IsEqualTo(TimeSpan.FromSeconds(10));
    }

    [Test]
    public async Task Honours_a_poll_429s_retry_after_beyond_the_local_cap() {
        // The server's Retry-After is its rate-limit window: 60s stays 60s even though a locally
        // computed gap would never exceed 30s.
        var h = Build();
        h.Channel.Polls.Enqueue(new(429, null, TimeSpan.FromSeconds(60)));
        h.Channel.Polls.Enqueue(new(200, Done()));

        await Run(h);

        await Assert.That(h.Channel.PollTimes[1] - h.Channel.PollTimes[0]).IsEqualTo(TimeSpan.FromSeconds(60));
    }

    [Test]
    public async Task A_retry_after_longer_than_the_budget_does_not_extend_the_wait() {
        // The budget is the backstop, not the interval: a route that asks for an hour must not turn
        // a 30-minute wait into an hour-long one — even on a host with no keyboard to end it early.
        var h = Build();
        h.Channel.Polls.Enqueue(new(429, null, TimeSpan.FromHours(1)));

        var result = await Run(h);

        await Assert.That(result).IsTypeOf<FirstRunFlowResult.Abandoned>();
        await Assert.That(h.Clock.GetUtcNow() - ClockBase).IsLessThanOrEqualTo(TimeSpan.FromMinutes(31));
        await Assert.That(h.Channel.PollCount).IsEqualTo(1);
    }

    [Test]
    public async Task A_keyboard_that_cannot_be_watched_is_never_read() {
        // Redirected stdin, or no console at all. Polling it would throw, and the flow must not care.
        var h = Build(new FakeKeys(canWatch: false, pressAfter: 0));
        h.Channel.Polls.Enqueue(new(200, Done()));

        var result = await Run(h);

        await Assert.That(result).IsTypeOf<FirstRunFlowResult.Finished>();
        await Assert.That(h.Keys.Drains).IsEqualTo(0);
    }

    static readonly DateTimeOffset Asked = new(2026, 8, 21, 12, 5, 0, TimeSpan.Zero);

    /// <summary>A running flow with one outstanding request on it.</summary>
    static FirstRunFlowResponse Asking(
            string capability = FirstRunMachineCapabilities.PathShim, DateTimeOffset? requestedAt = null) =>
        Running() with {
            MachineActions = [new FirstRunMachineActionResponse {
                Capability = capability, RequestedAt = requestedAt ?? Asked
            }]
        };

    [Test]
    public async Task An_advertised_request_is_performed_and_reported_against_its_own_timestamp() {
        var h = Build(capabilities: PathShimOnly);
        h.Actions!.Results.Enqueue(new(FirstRunMachineActionOutcomes.Cancelled, null));
        h.Channel.Polls.Enqueue(new(200, Asking()));
        h.Channel.Tail = new(200, Done());

        await Run(h);

        await Assert.That(h.Actions.Performed).IsEquivalentTo(PathShimOnly);
        await Assert.That(h.Channel.ActionReports.Count).IsEqualTo(1);
        await Assert.That(h.Channel.ActionReports[0].Capability).IsEqualTo(FirstRunMachineCapabilities.PathShim);
        await Assert.That(h.Channel.ActionReports[0].Outcome).IsEqualTo(FirstRunMachineActionOutcomes.Cancelled);

        // The request's own stamp, not the clock's: the server drops a report answering a superseded ask.
        await Assert.That(h.Channel.ActionReports[0].RequestedAt).IsEqualTo(Asked);
    }

    [Test]
    public async Task The_user_is_warned_before_the_action_runs_not_after() {
        // The shim raises an admin-password dialog. Warned afterwards, it has already appeared.
        var h = Build(capabilities: PathShimOnly);
        h.Channel.Polls.Enqueue(new(200, Asking()));
        h.Channel.Tail = new(200, Done());

        await Run(h);

        await Assert.That(h.Progress.Performing).IsEquivalentTo(PathShimOnly);

        var entries = h.Log.Entries.ToList();

        await Assert.That(entries.IndexOf("warn")).IsGreaterThanOrEqualTo(0);
        await Assert.That(entries.IndexOf("perform")).IsGreaterThan(entries.IndexOf("warn"));
        await Assert.That(entries.IndexOf("report")).IsGreaterThan(entries.IndexOf("perform"));
    }

    [Test]
    public async Task A_capability_this_host_does_not_advertise_is_left_alone_rather_than_failed() {
        // Reporting it would tell the screen the fix was tried. It was not, and the request stays
        // outstanding so a newer CLI can still answer it.
        var h = Build(capabilities: []);
        h.Channel.Polls.Enqueue(new(200, Asking()));
        h.Channel.Tail = new(200, Done());

        await Run(h);

        await Assert.That(h.Actions!.Performed).IsEmpty();
        await Assert.That(h.Channel.ActionReports).IsEmpty();
    }

    [Test]
    public async Task A_capability_this_build_cannot_name_is_never_performed() {
        // Dropped at the mapping boundary, so a host that happens to advertise the same string still
        // never sees it: the closed set is what keeps "a named capability" from meaning "whatever
        // the server said".
        var h = Build(capabilities: ["reboot_the_laptop"]);
        h.Channel.Polls.Enqueue(new(200, Asking("reboot_the_laptop")));
        h.Channel.Tail = new(200, Done());

        await Run(h);

        await Assert.That(h.Actions!.Performed).IsEmpty();
    }

    [Test]
    public async Task The_same_request_seen_twice_performs_once() {
        // The poll returns the request until the report lands, and the report lands after the action.
        // Without the guard the second sighting raises a second admin prompt for a fix already made.
        var h = Build(capabilities: PathShimOnly);
        h.Channel.Polls.Enqueue(new(200, Asking()));
        h.Channel.Polls.Enqueue(new(200, Asking()));
        h.Channel.Tail = new(200, Done());

        await Run(h);

        await Assert.That(h.Actions!.Performed.Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_report_that_did_not_land_is_retried_without_performing_again() {
        var h = Build(capabilities: PathShimOnly);
        h.Channel.ReportStatuses.Enqueue(500);
        h.Channel.Polls.Enqueue(new(200, Asking()));
        h.Channel.Polls.Enqueue(new(200, Asking()));
        h.Channel.Tail = new(200, Done());

        await Run(h);

        await Assert.That(h.Actions!.Performed.Count).IsEqualTo(1);
        await Assert.That(h.Channel.ActionReports.Count).IsEqualTo(2);
    }

    [Test]
    public async Task A_fresh_request_performs_again() {
        // A second press after an outcome is a retry, and the timestamp is what says so — the
        // capability alone cannot tell a retry from the request already answered.
        var h = Build(capabilities: PathShimOnly);
        h.Channel.Polls.Enqueue(new(200, Asking()));
        h.Channel.Polls.Enqueue(new(200, Asking(requestedAt: Asked.AddMinutes(1))));
        h.Channel.Tail = new(200, Done());

        await Run(h);

        await Assert.That(h.Actions!.Performed.Count).IsEqualTo(2);
        await Assert.That(h.Channel.ActionReports.Select(r => r.RequestedAt).ToList())
                    .IsEquivalentTo(new[] { Asked, Asked.AddMinutes(1) });
    }

    [Test]
    public async Task An_action_that_throws_is_reported_as_failed() {
        // A screen waiting on an outcome that never comes is the state this lane exists to avoid.
        var h = Build(capabilities: PathShimOnly);
        h.Actions!.Throws = new InvalidOperationException("osascript went missing");
        h.Channel.Polls.Enqueue(new(200, Asking()));
        h.Channel.Tail = new(200, Done());

        await Run(h);

        await Assert.That(h.Channel.ActionReports.Count).IsEqualTo(1);
        await Assert.That(h.Channel.ActionReports[0].Outcome).IsEqualTo(FirstRunMachineActionOutcomes.Failed);
        await Assert.That(h.Channel.ActionReports[0].Reason).IsNull();
    }

    [Test]
    public async Task A_request_riding_the_poll_that_finishes_the_flow_is_still_performed() {
        // The user presses the button and the browser settles the last step before the next tick. The
        // request was made, so it is owed an attempt.
        var h = Build(capabilities: PathShimOnly);
        h.Channel.Tail = new(200, Done() with {
            MachineActions = [new FirstRunMachineActionResponse {
                Capability = FirstRunMachineCapabilities.PathShim, RequestedAt = Asked
            }]
        });

        var result = await Run(h);

        await Assert.That(result).IsTypeOf<FirstRunFlowResult.Finished>();
        await Assert.That(h.Actions!.Performed.Count).IsEqualTo(1);
        await Assert.That(h.Channel.ActionReports.Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_host_with_no_actions_performs_nothing_and_still_finishes() {
        var h = Build();
        h.Channel.Polls.Enqueue(new(200, Asking()));
        h.Channel.Tail = new(200, Done());

        var result = await Run(h);

        await Assert.That(result).IsTypeOf<FirstRunFlowResult.Finished>();
        await Assert.That(h.Channel.ActionReports).IsEmpty();
    }

    [Test]
    public async Task An_outcome_that_did_not_land_on_the_finishing_tick_is_flushed() {
        // The per-tick retry needs a next tick and this tick has none, so without the flush a single blip
        // loses an outcome for a fix that really happened.
        var h = Build(capabilities: PathShimOnly);
        h.Channel.ReportStatuses.Enqueue(500);
        h.Channel.Tail = new(200, Done() with {
            MachineActions = [new FirstRunMachineActionResponse {
                Capability = FirstRunMachineCapabilities.PathShim, RequestedAt = Asked
            }]
        });

        var result = await Run(h);

        await Assert.That(result).IsTypeOf<FirstRunFlowResult.Finished>();
        await Assert.That(h.Actions!.Performed.Count).IsEqualTo(1);
        await Assert.That(h.Channel.ActionReports.Count).IsEqualTo(2);
    }

    [Test]
    public async Task A_finished_flow_is_not_held_open_by_a_report_that_never_lands() {
        // The request stays outstanding server-side, which is the honest reading; what must not happen is
        // reporting a finished flow as abandoned because a report kept failing.
        var h = Build(capabilities: PathShimOnly);

        for (var i = 0; i < 10; i++) h.Channel.ReportStatuses.Enqueue(500);

        h.Channel.Tail = new(200, Done() with {
            MachineActions = [new FirstRunMachineActionResponse {
                Capability = FirstRunMachineCapabilities.PathShim, RequestedAt = Asked
            }]
        });

        var result = await Run(h);

        await Assert.That(result).IsTypeOf<FirstRunFlowResult.Finished>();

        // The tick's own attempt plus its two retries, and then it stops.
        await Assert.That(h.Channel.ActionReports.Count).IsEqualTo(3);
    }

    [Test]
    public async Task A_cancel_during_the_action_ends_the_leg_rather_than_finishing_it() {
        // Every other await here lets the caller's cancel out. Swallowing it would let a cancelled setup
        // resolve as Finished, reporting a flow as complete that the caller stopped.
        using var cts = new CancellationTokenSource();

        var h = Build(capabilities: PathShimOnly);
        h.Actions!.Throws = new OperationCanceledException(cts.Token);
        h.Channel.Tail = new(200, Done() with {
            MachineActions = [new FirstRunMachineActionResponse {
                Capability = FirstRunMachineCapabilities.PathShim, RequestedAt = Asked
            }]
        });

        await cts.CancelAsync();

        await Assert.That(async () => await Drive(h.Flow.RunAsync(Server, Report, cts.Token), h.Clock))
            .Throws<OperationCanceledException>();

        await Assert.That(h.Channel.ActionReports).IsEmpty();
    }
}
