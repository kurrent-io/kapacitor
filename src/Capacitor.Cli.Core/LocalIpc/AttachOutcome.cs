namespace Capacitor.Cli.Core.LocalIpc;

/// Exactly one of these is RunAsync's result. Detached = locally initiated
/// close; Exited = agent process exit; Failed = daemon Error / refusal /
/// protocol failure / pre-attach failure; ConnectionLost = uninitiated
/// transport loss after attach.
public abstract record AttachOutcome {
    public sealed record Detached : AttachOutcome;
    public sealed record Exited(int Code) : AttachOutcome;
    public sealed record Failed(string Message) : AttachOutcome;
    public sealed record ConnectionLost : AttachOutcome;
}
