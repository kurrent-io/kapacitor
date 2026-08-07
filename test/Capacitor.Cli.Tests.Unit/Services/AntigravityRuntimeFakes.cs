// test/Capacitor.Cli.Tests.Unit/Services/AntigravityRuntimeFakes.cs
using System.Runtime.CompilerServices;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit.Services;

/// <summary>How one fake turn behaves, driving <see cref="FakeAgyTurnProcess.ReadLinesAsync"/>. Pinned
/// exactly per the design brief — a sibling plan reuses this shape, which is also why this and the
/// other fakes below live as TOP-LEVEL types here rather than nested inside a test class: a nested
/// type (even <see langword="public"/>) is only reachable through its enclosing class, and a sibling
/// test class in a different file has no reason to depend on this file's test class.</summary>
internal enum FakeTurn {
    /// <summary>Emits <c>init</c> then a <c>result</c> with <c>status: SUCCESS</c>, then EOFs —
    /// the ordinary clean-turn shape.</summary>
    Normal,

    /// <summary>Emits <c>init</c> only, then EOFs with NO <c>result</c> line at all — the "the
    /// reviewer died mid-turn" shape rule (a) exists for.</summary>
    EofWithoutResult,

    /// <summary>Emits <c>init</c>, then blocks forever until its cancellation token fires — the
    /// "a turn is genuinely still running" shape the deadlock mutation check exercises.</summary>
    NeverEnds,

    /// <summary>Emits <c>init</c> carrying <see cref="AntigravityRuntimeFakes.ChangedConversationId"/>
    /// instead of whatever id the spawner closure was constructed with — the "this turn's process
    /// reports a DIFFERENT conversation than the one already established" shape rule (a)'s
    /// conversation-id-stability check exists for. Only meaningful for a turn AFTER the first (a first
    /// turn has nothing to mismatch against yet), so tests combine this with <see cref="Normal"/> via a
    /// bespoke spawn closure rather than <see cref="AntigravityRuntimeFakes.FakeRuntime"/> (which uses
    /// one fixed <see cref="FakeTurn"/> for every spawn).</summary>
    ChangedConversationId,
}

/// <summary><see cref="IAgyTurnProcess"/> fake for ONE turn. A fresh instance is handed out by
/// the injected spawner for every turn, mirroring agy's real exec-per-turn shape — this is never
/// reused across turns.</summary>
internal sealed class FakeAgyTurnProcess(FakeTurn turn, string conversationId) : IAgyTurnProcess {
    public int  Pid            { get; } = 4242;
    public bool HasExited      { get; private set; }
    public int? ExitCode       { get; private set; }
    public int  TerminateCalls { get; private set; }
    public int  DisposeCalls   { get; private set; }

    public async IAsyncEnumerable<string> ReadLinesAsync([EnumeratorCancellation] CancellationToken ct) {
        var effectiveId = turn == FakeTurn.ChangedConversationId
            ? AntigravityRuntimeFakes.ChangedConversationId
            : conversationId;

        yield return $$$"""{"event":"init","conversation_id":"{{{effectiveId}}}","init":{"cwd":"/w"}}""";

        switch (turn) {
            case FakeTurn.Normal:
                yield return $$$"""{"event":"result","result":{"conversation_id":"{{{effectiveId}}}","status":"SUCCESS"}}""";
                HasExited = true;
                ExitCode  = 0;
                break;

            case FakeTurn.EofWithoutResult:
                // No result line — the child exits having said nothing more. The runtime must
                // not read this EOF as "turn complete".
                HasExited = true;
                ExitCode  = 1;
                break;

            case FakeTurn.NeverEnds:
                // Blocks until the runtime's own cancellation (owner-cancel or a deadline) fires —
                // simulates a turn that is genuinely still running.
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                break;

            case FakeTurn.ChangedConversationId:
                // The runtime detects the mismatch on the init line just yielded above and stops
                // consuming immediately (AntigravityHostedAgentRuntime.ProcessTurnAsync breaks out of
                // its read loop the instant HandleInit reports a mismatch) — nothing after this point
                // is ever read, so EOF here (no result line) is enough.
                HasExited = true;
                ExitCode  = 0;
                break;
        }
    }

    public Task WaitForExitAsync(TimeSpan? timeout = null) => Task.CompletedTask;

    public Task TerminateAsync(TimeSpan? timeout = null) {
        TerminateCalls++;
        HasExited = true;
        ExitCode ??= -1;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() {
        DisposeCalls++;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Shared construction helpers for <see cref="AntigravityHostedAgentRuntime"/> tests — a
/// sibling plan reuses <see cref="FakeRuntime"/> directly, so its signature is pinned exactly per the
/// design brief.</summary>
internal static class AntigravityRuntimeFakes {
    public const string FixedConversationId    = "fixed-conversation-id";
    public const string ChangedConversationId  = "changed-conversation-id";

    /// <summary>
    /// Builds a runtime whose turn spawner never touches a real process. Signature pinned exactly
    /// per the design brief — a sibling plan reuses this helper directly, so <paramref name="onSpawn"/>
    /// receiving the PROMPT (not a bare notification) and <paramref name="queueCap"/> both matter to
    /// get right here. Every spawn uses the SAME <paramref name="turn"/> behavior — a test that needs
    /// different behavior across turns (e.g. a conversation-id mismatch, which only makes sense after a
    /// first turn has already established an id) constructs <see cref="AntigravityHostedAgentRuntime"/>
    /// directly with its own spawn closure instead.
    /// </summary>
    public static AntigravityHostedAgentRuntime FakeRuntime(
            FakeTurn        turn     = FakeTurn.Normal,
            Action<string>? onSpawn  = null,
            int             queueCap = 64) {
        Func<string, string?, CancellationToken, Task<IAgyTurnProcess>> spawn = (prompt, _, _) => {
            onSpawn?.Invoke(prompt);
            return Task.FromResult<IAgyTurnProcess>(new FakeAgyTurnProcess(turn, FixedConversationId));
        };

        return new AntigravityHostedAgentRuntime(
            spawnTurn: spawn,
            logger: NullLogger.Instance,
            pendingTurnsCapacity: queueCap);
    }
}
