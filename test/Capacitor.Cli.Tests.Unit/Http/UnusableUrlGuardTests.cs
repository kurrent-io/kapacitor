using System.Diagnostics;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Tests.Unit.Http;

/// <summary>
/// The dispositions each guard owes when the server URL cannot be used.
///
/// <para>Where a guard sits in front of an injectable seam, the assertion is that the guarded
/// operation was NEVER ENTERED — not that its effects are absent. Effects are reproducible by the
/// catch-all every one of these paths already has, so an effect-only assertion passes with the guard
/// deleted; six review rounds found exactly that, repeatedly.</para>
/// </summary>
public class UnusableUrlGuardTests : IDisposable {
    // Deliberately the wrong-scheme class: an implementation validating only UriKind.Absolute
    // accepts this while still violating the invariant.
    const string BadUrl = "ftp://host";
    const string Sid    = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    readonly string _dir  = Path.Combine(Path.GetTempPath(), $"kcap-guard-{Guid.NewGuid():N}");
    readonly string _tdir = Path.Combine(Path.GetTempPath(), $"kcap-guard-t-{Guid.NewGuid():N}");

    public void Dispose() {
        WatcherManager.ProcessStarterForTesting = null;
        foreach (var d in new[] { _dir, _tdir }) { try { Directory.Delete(d, true); } catch { } }
    }

    [Test]
    public async Task PostOrSpool_spools_the_payload_and_reports_Spooled() {
        var spool   = new HookSpool(_dir);
        var outcome = await AgentHookPoster.PostOrSpoolAsync(
            BadUrl, "session-start/codex", """{"session_id":"x"}""", "codex-hook", spool, Sid, "session-start/codex");

        await Assert.That(outcome).IsEqualTo(HookPostOutcome.Spooled);
        await Assert.That(spool.HasBacklog(Sid)).IsTrue();
        await Assert.That(File.ReadAllText(Path.Combine(_dir, $"{Sid}.jsonl"))).Contains("session-start/codex");
    }

    [Test]
    public async Task PostOrSpool_reports_Skipped_when_the_spool_write_itself_fails() {
        // An unwritable spool dir must not be reported as durably spooled — Skipped promises nothing.
        var unwritable = Path.Combine(_dir, "nope.txt");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(unwritable, "not a directory");

        var outcome = await AgentHookPoster.PostOrSpoolAsync(
            BadUrl, "session-start/codex", "{}", "codex-hook", new HookSpool(unwritable), Sid, "session-start/codex");

        await Assert.That(outcome).IsEqualTo(HookPostOutcome.Skipped);
    }

    [Test]
    public async Task PostAsync_reports_Skipped_never_Failed() {
        // Failed would make every caller exit non-zero — the hook must still exit 0.
        var outcome = await AgentHookPoster.PostAsync(BadUrl, "session-end/gemini", "{}", "gemini-hook");

        await Assert.That(outcome).IsEqualTo(HookPostOutcome.Skipped);
        await Assert.That(outcome).IsNotEqualTo(HookPostOutcome.Failed);
    }

    [Test]
    public async Task ShouldSpawn_refuses_for_an_unusable_url_whatever_the_outcome() {
        await Assert.That(AgentHookPoster.ShouldSpawnAfter(HookPostOutcome.Posted,  BadUrl)).IsFalse();
        await Assert.That(AgentHookPoster.ShouldSpawnAfter(HookPostOutcome.Spooled, BadUrl)).IsFalse();
        await Assert.That(AgentHookPoster.ShouldSpawnAfter(HookPostOutcome.Skipped, BadUrl)).IsFalse();
    }

    [Test]
    public async Task ShouldSpawn_allows_Skipped_when_the_url_is_usable() {
        // The server supports a transcript arriving before its session-start, so suppressing capture
        // after a spool write failure would guarantee loss it is designed to recover.
        await Assert.That(AgentHookPoster.ShouldSpawnAfter(HookPostOutcome.Skipped, "http://localhost:5108")).IsTrue();
        await Assert.That(AgentHookPoster.ShouldSpawnAfter(HookPostOutcome.Failed,  "http://localhost:5108")).IsFalse();
    }

    [Test]
    public async Task Drain_reaps_the_backlog_but_never_builds_a_client() {
        Directory.CreateDirectory(_dir);
        var stale = Path.Combine(_dir, $"{Sid}.jsonl");
        File.WriteAllText(stale, "{}\n");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-40));

        var entered = false;

        await AgentHookPoster.DrainSpoolsAsync(
            BadUrl, new HookSpool(_dir), new TranscriptSpool(_tdir), Sid,
            clientFactory: _ => {
                entered = true;
                throw new InvalidOperationException("the drain guard did not run");
            });

        // Non-entry is the proof: the drain's own catch would otherwise hide an exception here.
        await Assert.That(entered).IsFalse();
        // Retention still runs — Program.cs skips this call entirely while the URL is bad, so a
        // reap that lived only past the guard would never happen on a broken config.
        await Assert.That(File.Exists(stale)).IsFalse();
    }

    [Test]
    public async Task SpawnWatcher_never_starts_a_process() {
        var starts = 0;
        WatcherManager.ProcessStarterForTesting = _ => { starts++; return null; };

        await WatcherManager.SpawnWatcher(BadUrl, Sid, Path.Combine(_dir, "t.jsonl"), agentId: null);

        await Assert.That(starts).IsEqualTo(0);
    }

    [Test]
    public async Task SpawnCopilotFinalizeDrain_never_starts_a_process() {
        // This one writes no marker at all, so "no child left behind" is unfalsifiable — a deleted
        // guard merely lets Process.Start throw or return null, leaving every effect identical.
        var starts = 0;
        WatcherManager.ProcessStarterForTesting = _ => { starts++; return null; };

        WatcherManager.SpawnCopilotFinalizeDrain(BadUrl, Sid, Path.Combine(_dir, "t.jsonl"));

        await Assert.That(starts).IsEqualTo(0);
    }

    [Test]
    public async Task InlineDrain_returns_without_building_a_client() {
        // Runs on session-end BEFORE the lifecycle POST; its own client takes no baseUrl and
        // re-resolves the same unusable value. Completing quickly is the observable: an unguarded
        // call would exit the process outright.
        var sw = Stopwatch.StartNew();
        await WatcherManager.InlineDrainAsync(BadUrl, Sid, Path.Combine(_dir, "t.jsonl"), agentId: null);

        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromSeconds(2));
    }
}
