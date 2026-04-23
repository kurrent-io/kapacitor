namespace kapacitor.Commands;

/// <summary>
/// Progress events emitted by <see cref="SessionImporter.ImportSessionAsync"/>
/// and <see cref="SessionImporter.SendTranscriptBatches"/> for UI layers that
/// want to render a live view of an in-flight import.
/// </summary>
public abstract record ImportProgress;

/// <summary>Fired after a transcript batch is flushed to the server.</summary>
public sealed record BatchFlushed(int LinesAdded) : ImportProgress;

/// <summary>Fired when the importer begins streaming a subagent's transcript inline.</summary>
public sealed record SubagentStarted(string AgentId) : ImportProgress;

/// <summary>Fired after a subagent's transcript has been fully streamed.</summary>
public sealed record SubagentFinished(string AgentId, int LinesSent) : ImportProgress;
