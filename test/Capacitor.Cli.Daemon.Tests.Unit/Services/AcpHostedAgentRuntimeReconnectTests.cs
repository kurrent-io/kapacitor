using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Acp;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// Exercises <see cref="AcpHostedAgentRuntime"/>'s crash reconnect/resume (skip-whole-replay —
/// the design spec at <c>docs/superpowers/specs/2026-08-04-ai1325-acp-reconnect-resume-design.md</c>
/// in this repo) against
/// <see cref="FakeAcpAgent"/> incarnations: the original child plus one fake per resume candidate,
/// spawned through the same <see cref="AcpReconnectSupport.Spawn"/> seam production uses. Attempt
/// backoffs are zero so tests are deterministic without a fake clock — nothing here waits on real
/// reconnect delays.
/// </summary>
public class AcpHostedAgentRuntimeReconnectTests {
    // Every use is a ceiling on something that must finish, never an assertion that it doesn't:
    // the suite's generous bound turns a starved run into a slow pass instead of a false failure.
    static readonly TimeSpan HangGuard = WaitHarness.Bounded;

    sealed class FakeAcpProcess : IAcpProcess {
        readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int  Pid               { get; init; } = 4242;
        public bool HasExited         { get; private set; }
        public int? ExitCode          { get; private set; }
        public int  TerminateCalls    { get; private set; }
        public bool RefuseTermination { get; init; }

        public void SignalExited(int exitCode = 0) {
            HasExited = true;
            ExitCode  = exitCode;
            _exited.TrySetResult();
        }

        public async Task WaitForExitAsync(TimeSpan? timeout = null) {
            if (timeout is { } t)
                await Task.WhenAny(_exited.Task, Task.Delay(t)).ConfigureAwait(false);
            else
                await _exited.Task.ConfigureAwait(false);
        }

        public Task TerminateAsync(TimeSpan? timeout = null) {
            TerminateCalls++;
            if (!RefuseTermination)
                SignalExited();

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    sealed class Harness : IAsyncDisposable {
        public readonly List<(FakeAcpAgent Fake, FakeAcpProcess Process)> Incarnations = [];
        public readonly List<AcpEventEnvelope> Envelopes = [];
        public readonly List<int> RecordedPids = [];
        public int  ClearRecordCalls;
        public bool FailPidRecord;

        public CancellationTokenSource Cts { get; } = new();
        public AcpHostedAgentRuntime Runtime { get; }
        public AcpReconnectSupport?  Support { get; }

        /// <summary>Scripts a candidate BEFORE its read loop starts; index 1 = first candidate.</summary>
        public Action<FakeAcpAgent, int>? ConfigureCandidate { get; set; }

        readonly List<Task> _fakeLoops = [];
        readonly object     _envelopeLock = new();
        Task _envelopeDrain = Task.CompletedTask;

        public Harness(bool withSupport = true, bool refuseOriginalTermination = false, int maxResumes = 5) {
            var fake0 = new FakeAcpAgent();
            var proc0 = new FakeAcpProcess { Pid = 5000, RefuseTermination = refuseOriginalTermination };
            Incarnations.Add((fake0, proc0));

            Support = withSupport
                ? new AcpReconnectSupport {
                    Spawn                = SpawnCandidate,
                    AttemptDelays        = [TimeSpan.Zero, TimeSpan.Zero],
                    RetirementWait       = TimeSpan.FromSeconds(2),
                    SettlementWait       = TimeSpan.FromSeconds(5),
                    MaxResumesPerSession = maxResumes
                }
                : null;

            if (Support is not null) {
                Support.PidCallbacks = new AcpPidRecordCallbacks(
                    Record: pid => {
                        if (FailPidRecord) throw new IOException("simulated PID record write failure");
                        RecordedPids.Add(pid);
                    },
                    Clear: () => ClearRecordCalls++);
            }

            var connection = new AcpConnection(fake0.ClientWriteStream, fake0.ClientReadStream, NullLogger.Instance);
            Runtime = new AcpHostedAgentRuntime(connection, proc0, NullLogger.Instance, reconnect: Support);
        }

        (Stream Input, Stream Output, IAcpProcess Process) SpawnCandidate() {
            var fake = new FakeAcpAgent();
            var proc = new FakeAcpProcess { Pid = 5000 + Incarnations.Count };

            ConfigureCandidate?.Invoke(fake, Incarnations.Count);
            Incarnations.Add((fake, proc));
            _fakeLoops.Add(fake.RunAsync(Cts.Token));

            return (fake.ClientWriteStream, fake.ClientReadStream, proc);
        }

        public int CandidateSpawns => Incarnations.Count - 1;

        public async Task StartAsync(string? initialPrompt = null) {
            _fakeLoops.Add(Incarnations[0].Fake.RunAsync(Cts.Token));
            _envelopeDrain = DrainEnvelopesAsync();

            await Runtime.StartAsync("/abs/worktree", initialPrompt, Cts.Token).WaitAsync(HangGuard);
        }

        async Task DrainEnvelopesAsync() {
            await foreach (var envelope in Runtime.Envelopes.ReadAllAsync(Cts.Token)) {
                lock (_envelopeLock) Envelopes.Add(envelope);
            }
        }

        public IReadOnlyList<AcpEventEnvelope> EnvelopeSnapshot() {
            lock (_envelopeLock) return Envelopes.ToArray();
        }

        public void CrashOriginal() {
            Incarnations[0].Fake.SimulateCrash();
            Incarnations[0].Process.SignalExited(1);
        }

        /// <summary>The finalize signal the orchestrator keys on: completes only when
        /// <c>ReadOutputAsync</c>'s enumerable ends (runtime logically terminal).</summary>
        public async Task RuntimeTerminalAsync() {
            await foreach (var _ in Runtime.ReadOutputAsync(Cts.Token)) { }
        }

        public static async Task PollUntilAsync(Func<bool> condition) {
            var deadline = DateTime.UtcNow + HangGuard;
            while (!condition() && DateTime.UtcNow < deadline)
                await Task.Delay(10);

            await Assert.That(condition()).IsTrue();
        }

        public async ValueTask DisposeAsync() {
            Cts.Cancel();

            foreach (var loop in _fakeLoops) {
                try { await loop.WaitAsync(HangGuard); } catch { /* crashed/cancelled fakes */ }
            }
            try { await _envelopeDrain.WaitAsync(HangGuard); } catch { /* cancelled */ }

            await Runtime.DisposeAsync();

            foreach (var (fake, _) in Incarnations) {
                try { await fake.DisposeAsync(); } catch { /* a crashed fake may hold a dispatch fault */ }
            }

            Cts.Dispose();
        }
    }

    [Test]
    public async Task Crash_resumes_via_session_load_suppresses_replay_and_emits_system_note() {
        await using var h = new Harness();
        h.ConfigureCandidate = (fake, _) => fake.SetSessionLoadReplay([
            FakeAcpAgent.DefaultAgentMessageChunkUpdate(FakeAcpAgent.FixedSessionId, "replayed-one"),
            FakeAcpAgent.DefaultAgentMessageChunkUpdate(FakeAcpAgent.FixedSessionId, "replayed-two")
        ]);

        await h.StartAsync();

        h.CrashOriginal();

        var candidate = default(FakeAcpAgent);
        await Harness.PollUntilAsync(() => {
            if (h.CandidateSpawns == 0) return false;
            candidate = h.Incarnations[1].Fake;
            return candidate.ReceivedCalls.Any(c => c.Method == "session/load");
        });

        var load = candidate!.ReceivedCalls.Single(c => c.Method == "session/load");
        await Assert.That(load.Params!.Value.GetProperty("sessionId").GetString()).IsEqualTo(FakeAcpAgent.FixedSessionId);
        await Assert.That(load.Params!.Value.GetProperty("cwd").GetString()).IsEqualTo("/abs/worktree");

        // The resume is observable via the system_note; the replayed conversation must never
        // become envelopes (skip-whole-replay), and the note precedes any later envelope.
        await Harness.PollUntilAsync(() => h.EnvelopeSnapshot().Any(e => e.Kind == AcpEventKind.SystemNote));

        var envelopes = h.EnvelopeSnapshot();
        await Assert.That(envelopes.Any(e => e.Text?.Contains("replayed-one") == true)).IsFalse();
        await Assert.That(envelopes.Any(e => e.Text?.Contains("replayed-two") == true)).IsFalse();

        // Idle crash — nothing was in flight, so no resend sentence.
        var note = envelopes.Single(e => e.Kind == AcpEventKind.SystemNote);
        await Assert.That(note.Text).IsEqualTo("Agent process restarted; the session was resumed.");

        // The resumed session is live: a new input reaches the CANDIDATE as session/prompt.
        await h.Runtime.SendUserInputAsync("after-resume").WaitAsync(HangGuard);
        await Harness.PollUntilAsync(() => candidate.ReceivedCalls.Any(c =>
            c.Method == "session/prompt" &&
            c.Params!.Value.GetProperty("prompt")[0].GetProperty("text").GetString() == "after-resume"));

        // And the candidate's pid was durably recorded at spawn.
        await Assert.That(h.RecordedPids).Contains(5001);
    }

    [Test]
    public async Task Crash_without_reconnect_support_finalizes_as_today() {
        await using var h = new Harness(withSupport: false);
        await h.StartAsync();

        var terminal = h.RuntimeTerminalAsync();

        h.CrashOriginal();

        await terminal.WaitAsync(HangGuard);
        await Assert.That(h.CandidateSpawns).IsEqualTo(0);
    }

    [Test]
    public async Task Crash_when_load_session_not_advertised_finalizes() {
        await using var h = new Harness();
        h.Incarnations[0].Fake.SetInitializeResult(FakeAcpAgent.BuildInitializeResult(1, loadSession: false));

        await h.StartAsync();

        var terminal = h.RuntimeTerminalAsync();
        h.CrashOriginal();

        await terminal.WaitAsync(HangGuard);
        await Assert.That(h.CandidateSpawns).IsEqualTo(0);
    }

    [Test]
    public async Task Queued_turn_parks_during_reconnect_and_delivers_after_the_note() {
        await using var h = new Harness();
        await h.StartAsync();

        var original = h.Incarnations[0].Fake;

        // Park a turn mid-flight on the original (response held forever), then queue another
        // behind it and crash. The queued turn must never be sent at the dead incarnation and must
        // arrive at the candidate strictly after the resume.
        original.HoldPromptResponses = new TaskCompletionSource();
        await h.Runtime.SendUserInputAsync("in-flight-turn").WaitAsync(HangGuard);
        await Harness.PollUntilAsync(() => original.ReceivedCalls.Any(c => c.Method == "session/prompt"));

        await h.Runtime.SendUserInputAsync("queued-turn").WaitAsync(HangGuard);

        h.CrashOriginal();

        await Harness.PollUntilAsync(() => h.CandidateSpawns >= 1 && h.Incarnations[1].Fake.ReceivedCalls.Any(c =>
            c.Method == "session/prompt" &&
            c.Params!.Value.GetProperty("prompt")[0].GetProperty("text").GetString() == "queued-turn"));

        var candidate = h.Incarnations[1].Fake;

        // The queued turn went ONLY to the candidate…
        await Assert.That(original.ReceivedCalls.Count(c => c.Method == "session/prompt")).IsEqualTo(1);

        // …the interrupted turn was surfaced, never re-sent (at-most-once floor)…
        await Assert.That(candidate.ReceivedCalls.Any(c =>
            c.Method == "session/prompt" &&
            c.Params!.Value.GetProperty("prompt")[0].GetProperty("text").GetString() == "in-flight-turn")).IsFalse();

        // …the note carries the resend sentence (a turn was in flight)…
        var envelopes = h.EnvelopeSnapshot();
        var note = envelopes.Single(e => e.Kind == AcpEventKind.SystemNote);
        await Assert.That(note.Text!).Contains("resend");

        // …and ordering held: the note precedes the queued turn's UserMessage envelope.
        var noteIndex   = envelopes.ToList().FindIndex(e => e.Kind == AcpEventKind.SystemNote);
        var queuedIndex = envelopes.ToList().FindIndex(e => e.Kind == AcpEventKind.UserMessage && e.Text == "queued-turn");
        await Assert.That(queuedIndex).IsGreaterThan(noteIndex);
    }

    [Test]
    public async Task Session_load_error_is_terminal_without_further_attempts() {
        await using var h = new Harness();
        h.ConfigureCandidate = (fake, _) =>
            fake.FailSessionLoad(-32603, "Failed to start session: Session is active in another process (PID 999)");

        await h.StartAsync();

        var terminal = h.RuntimeTerminalAsync();
        h.CrashOriginal();

        await terminal.WaitAsync(HangGuard);

        // One candidate tried session/load and was refused; the refusal is terminal — no retry
        // (both measured refusal classes are durable).
        await Assert.That(h.CandidateSpawns).IsEqualTo(1);
        await Assert.That(h.ClearRecordCalls).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task Candidate_death_mid_handshake_fails_only_that_attempt() {
        await using var h = new Harness();
        h.ConfigureCandidate = (fake, index) => {
            if (index == 1) {
                // First candidate parks its initialize response; the test crashes it below.
                fake.HoldInitializeResponse = new TaskCompletionSource();
            }
        };

        await h.StartAsync();
        h.CrashOriginal();

        await Harness.PollUntilAsync(() => h.CandidateSpawns >= 1 &&
            h.Incarnations[1].Fake.ReceivedCalls.Any(c => c.Method == "initialize"));

        // Kill candidate 1 mid-initialize: only that attempt fails; candidate 2 resumes.
        h.Incarnations[1].Fake.SimulateCrash();
        h.Incarnations[1].Process.SignalExited(1);

        await Harness.PollUntilAsync(() => h.EnvelopeSnapshot().Any(e => e.Kind == AcpEventKind.SystemNote));
        await Assert.That(h.CandidateSpawns).IsEqualTo(2);
    }

    [Test]
    public async Task Pid_record_write_failure_fails_the_attempt_before_any_handshake() {
        await using var h = new Harness();
        h.FailPidRecord = true;

        await h.StartAsync();

        var terminal = h.RuntimeTerminalAsync();
        h.CrashOriginal();

        await terminal.WaitAsync(HangGuard);

        // Every attempt spawned a candidate whose record write failed → disposed pre-handshake:
        // no candidate ever received a single frame.
        await Assert.That(h.CandidateSpawns).IsEqualTo(3);
        foreach (var (fake, _) in h.Incarnations.Skip(1))
            await Assert.That(fake.ReceivedCalls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Unconfirmed_corpse_exit_is_terminal_and_no_candidate_handshake_begins() {
        await using var h = new Harness(refuseOriginalTermination: true);
        await h.StartAsync();

        var terminal = h.RuntimeTerminalAsync();

        // Pipe EOF without process exit — and the process then refuses termination, so retirement
        // cannot confirm the old tree is gone. session/load must never race a possibly-live prior
        // owner: the incident is terminal with zero candidates.
        h.Incarnations[0].Fake.SimulateCrash();

        await terminal.WaitAsync(HangGuard);
        await Assert.That(h.CandidateSpawns).IsEqualTo(0);
        await Assert.That(h.Incarnations[0].Process.TerminateCalls).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task Resume_cap_finalizes_the_next_crash() {
        await using var h = new Harness(maxResumes: 1);
        await h.StartAsync();

        h.CrashOriginal();
        await Harness.PollUntilAsync(() => h.EnvelopeSnapshot().Any(e => e.Kind == AcpEventKind.SystemNote));

        var terminal = h.RuntimeTerminalAsync();

        // The second crash exceeds the cap: finalize, no second resume.
        h.Incarnations[1].Fake.SimulateCrash();
        h.Incarnations[1].Process.SignalExited(1);

        await terminal.WaitAsync(HangGuard);
        await Assert.That(h.CandidateSpawns).IsEqualTo(1);
    }

    [Test]
    public async Task Barrier_violating_late_update_after_reopen_is_emitted_as_live() {
        await using var h = new Harness();
        h.ConfigureCandidate = (fake, _) => fake.EmitConversationUpdateAfterLoadResponse = true;

        await h.StartAsync();
        h.CrashOriginal();

        await Harness.PollUntilAsync(() => h.EnvelopeSnapshot().Any(e => e.Kind == AcpEventKind.SystemNote));

        // Pins the DOCUMENTED failure shape the per-vendor probe gate exists to exclude
        // (spec §11.13): a conversation update arriving after the load response races the short
        // suppression tail. Whichever way the race lands, the gate machinery itself stays sound —
        // the note was emitted and the runtime resumed; the late update either fell inside the
        // suppression window (dropped) or after reopen (emitted as live text). Assert the
        // machinery outcome, not the race.
        await h.Runtime.SendUserInputAsync("still-alive").WaitAsync(HangGuard);
        await Harness.PollUntilAsync(() => h.Incarnations[1].Fake.ReceivedCalls.Any(c =>
            c.Method == "session/prompt" &&
            c.Params!.Value.GetProperty("prompt")[0].GetProperty("text").GetString() == "still-alive"));
    }

    [Test]
    public async Task Terminate_during_reconnect_finalizes_once() {
        await using var h = new Harness();

        // A candidate that never answers initialize keeps the incident in-flight until the stop.
        h.ConfigureCandidate = (fake, _) => fake.HoldInitializeResponse = new TaskCompletionSource();

        await h.StartAsync();

        var terminal = h.RuntimeTerminalAsync();
        h.CrashOriginal();

        await Harness.PollUntilAsync(() => h.CandidateSpawns >= 1);

        await h.Runtime.TerminateAsync(TimeSpan.FromSeconds(2)).WaitAsync(HangGuard);

        await terminal.WaitAsync(HangGuard);

        // Stop won: no resume happened (no system_note), and the incident unwound.
        await Assert.That(h.EnvelopeSnapshot().Any(e => e.Kind == AcpEventKind.SystemNote)).IsFalse();
    }

    [Test]
    public async Task Committed_successor_death_before_reopen_chains_into_the_same_incident() {
        await using var h = new Harness();

        var chained = false;
        h.Runtime.TestHookAfterCommit = () => {
            // Fires at the exact post-commit, pre-settlement instant. Kill the FIRST committed
            // successor only: its death must take the §5.2 chained arm (installed stamp, still
            // Reconnecting, stamp ≠ lastHandledCrash), fold into the SAME incident, and the next
            // candidate must resume — one owner, one eventual note. The crash signal propagates
            // via EOF on another thread, so BLOCK here (owner thread, no lock held) until the
            // marker is observed — otherwise the owner can legally reach reopen first and the
            // death becomes a fresh incident, which is correct behavior but not the arm this test
            // exists to pin.
            if (!chained) {
                chained = true;
                h.Incarnations[1].Fake.SimulateCrash();
                h.Incarnations[1].Process.SignalExited(1);

                var deadline = DateTime.UtcNow + HangGuard;
                while (!h.Runtime.ChainedCrashPendingForTest && DateTime.UtcNow < deadline)
                    Thread.Sleep(5);
            }
        };

        await h.StartAsync();
        h.CrashOriginal();

        await Harness.PollUntilAsync(() => h.EnvelopeSnapshot().Any(e => e.Kind == AcpEventKind.SystemNote));

        // Candidate 1 committed then died (chained); candidate 2 resumed. Exactly one note — the
        // chained pass skipped its own note/reopen.
        await Assert.That(h.CandidateSpawns).IsEqualTo(2);
        await Assert.That(h.EnvelopeSnapshot().Count(e => e.Kind == AcpEventKind.SystemNote)).IsEqualTo(1);

        await h.Runtime.SendUserInputAsync("after-chained-resume").WaitAsync(HangGuard);
        await Harness.PollUntilAsync(() => h.Incarnations[2].Fake.ReceivedCalls.Any(c =>
            c.Method == "session/prompt" &&
            c.Params!.Value.GetProperty("prompt")[0].GetProperty("text").GetString() == "after-chained-resume"));
    }

    [Test]
    public async Task Terminate_from_running_completes_the_finalize_signal() {
        await using var h = new Harness();
        await h.StartAsync();

        var terminal = h.RuntimeTerminalAsync();

        // A plain intentional stop of a HEALTHY session: the child exits, and the re-keyed
        // finalize signal must still fire (the pre-reconnect implementation had this implicitly by
        // waiting on process exit) — no reconnect incident, no candidates.
        await h.Runtime.TerminateAsync(TimeSpan.FromSeconds(2)).WaitAsync(HangGuard);
        h.Incarnations[0].Fake.SimulateCrash();

        await terminal.WaitAsync(HangGuard);
        await Assert.That(h.CandidateSpawns).IsEqualTo(0);
    }

    [Test]
    public async Task Held_turn_ack_resolves_on_resumed_delivery() {
        await using var h = new Harness();
        await h.StartAsync();

        h.CrashOriginal();

        // Enqueue while the incident is in flight: the turn parks (pre-gate admission) and its
        // write-ack resolves only on the successful post-resume write.
        var ack = h.Runtime.SendUserInputAndWaitForWriteAsync("parked-during-reconnect");

        await ack.WaitAsync(HangGuard);

        await Harness.PollUntilAsync(() => h.CandidateSpawns >= 1 && h.Incarnations[1].Fake.ReceivedCalls.Any(c =>
            c.Method == "session/prompt" &&
            c.Params!.Value.GetProperty("prompt")[0].GetProperty("text").GetString() == "parked-during-reconnect"));
    }
}
