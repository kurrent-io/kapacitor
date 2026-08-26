namespace Capacitor.App.Services;

using Capacitor.Cli.Core.LocalIpc;

/// App-side seam over Core's AgentAttachClient so VM tests script attachment.
public interface ITerminalAttachClient : IAsyncDisposable {
    Task<AttachOutcome> RunAsync(int initialCols, int initialRows, CancellationToken ct);
    Task SendInputAsync(byte[] bytes);
    Task ResizeAsync(int cols, int rows);
    Task DetachAsync();
}

/// One client per attach attempt — the factory is the unit tests' scripting seam.
public delegate ITerminalAttachClient TerminalAttachClientFactory(
    string agentId,
    Func<byte[], string?, CancellationToken, Task> onAttached,
    Func<byte[], CancellationToken, Task> onOutput);

/// Production adapter: Core client, diagnostics to Console.Error (the app's
/// teardown-diagnostic convention — never AppNotifier toasts).
public sealed class CoreTerminalAttachClient(AgentAttachClient inner) : ITerminalAttachClient {
    public static TerminalAttachClientFactory Factory(Func<string> socketPath) =>
        (agentId, onAttached, onOutput) => new CoreTerminalAttachClient(new AgentAttachClient(
            socketPath(), agentId, onAttached, onOutput,
            (ctx, ex) => Console.Error.WriteLine($"kcap: terminal attach {ctx}: {ex.Message}")));
    public Task<AttachOutcome> RunAsync(int c, int r, CancellationToken ct) => inner.RunAsync(c, r, ct);
    public Task SendInputAsync(byte[] b) => inner.SendInputAsync(b);
    public Task ResizeAsync(int c, int r) => inner.ResizeAsync(c, r);
    public Task DetachAsync() => inner.DetachAsync();
    public ValueTask DisposeAsync() => inner.DisposeAsync();
}

/// Deliberately NOT named *Attach*State — AttachStatus/AttachState already
/// describe the daemon status subscription (spec naming note).
public enum TerminalSessionPhase { Resolving, NoTerminal, NotFound, Connecting, Attached, Detached, Exited, Failed, SessionEnded }

/// What the composer can do right now, folding the send gate into the terminal state so a hint
/// built from it is true in every window — including the ones where State still reads Attached
/// while a reattach or detach is under way.
public enum SendAvailability { Ready, Sending, Transitioning, ReadOnly, Connecting, Reattach, Ended, NoTerminal }

public sealed record TerminalSessionState(TerminalSessionPhase Phase, string? Detail = null, bool ReadOnly = false, int? ExitCode = null) {
    public static readonly TerminalSessionState Resolving = new(TerminalSessionPhase.Resolving);
    public static TerminalSessionState NoTerminal(string? familyNote) => new(TerminalSessionPhase.NoTerminal, familyNote);
    public static readonly TerminalSessionState NotFound = new(TerminalSessionPhase.NotFound, "Session not found");
    public static readonly TerminalSessionState Connecting = new(TerminalSessionPhase.Connecting);
    public static TerminalSessionState Attached(string? readOnlyReason) => new(TerminalSessionPhase.Attached, readOnlyReason, ReadOnly: readOnlyReason is not null);
    public static readonly TerminalSessionState DetachedState = new(TerminalSessionPhase.Detached);
    public static TerminalSessionState Exited(int code) => new(TerminalSessionPhase.Exited, ExitCode: code);
    public static TerminalSessionState Failed(string message) => new(TerminalSessionPhase.Failed, message);
    public static readonly TerminalSessionState SessionEnded = new(TerminalSessionPhase.SessionEnded);
}
