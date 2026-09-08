namespace Capacitor.Cli.Commands;

/// <summary>
/// Progress events emitted by <see cref="SessionImporter.ImportSessionAsync"/>
/// and <see cref="SessionImporter.SendTranscriptBatches"/> for UI layers that
/// want to render a live view of an in-flight import.
/// </summary>
public abstract record ImportProgress;

/// <summary>
/// Fired after a transcript batch is flushed to the server.
/// <paramref name="AgentId"/> is non-null when the flushed batch belongs to a
/// subagent transcript, letting callers attribute lines to the right owner.
/// </summary>
public sealed record BatchFlushed(string? AgentId, int LinesAdded) : ImportProgress;

/// <summary>Fired when the importer begins streaming a subagent's transcript inline.</summary>
public sealed record SubagentStarted(string AgentId) : ImportProgress;

/// <summary>Fired after a subagent's transcript has been fully streamed.</summary>
public sealed record SubagentFinished(string AgentId, int LinesSent) : ImportProgress;

/// <summary>
/// Content the importer left behind without failing the session. Every sink must show these:
/// an import that reports success while a warning went unrendered is a silent loss. The session
/// id travels on the event because a routed source's sink is shared by every session of the run.
/// </summary>
public abstract record ImportWarning(string SessionId, string? AgentId) : ImportProgress {
    public abstract string Message { get; }

    protected string Scope => AgentId is null ? "" : $"subagent {AgentId} ";
}

/// <summary>A line that alone exceeds the batch byte budget; it cannot be split, so it is not sent.</summary>
public sealed record LineSkipped(string SessionId, string? AgentId, int LineNumber, int Bytes)
    : ImportWarning(SessionId, AgentId) {
    public override string Message =>
        $"{Scope}line {LineNumber} skipped: {Bytes} bytes exceeds the {TranscriptBatchBuffer.MaxBytes >> 20} MiB per-line limit";
}

/// <summary>A batch the server refused, or that never reached it, on a path that keeps going past the loss.</summary>
public sealed record BatchDropped(string SessionId, string? AgentId, int FirstLineNumber, int LastLineNumber, string Reason)
    : ImportWarning(SessionId, AgentId) {
    public override string Message => $"{Scope}lines {FirstLineNumber}-{LastLineNumber} dropped: {Reason}";
}
