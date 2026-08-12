namespace Capacitor.Cli.SessionStartMemory;

/// <summary>
/// The seam <see cref="SessionStartMemoryOrchestrator"/> depends on. Two
/// implementations exist: <see cref="SessionStartMemoryContextProvider"/>
/// (memory-index only — the Claude path) and
/// <see cref="SessionStartCompositeContextProvider"/> (memory + guidelines,
/// the eight non-Claude harnesses). The orchestrator, lease store and
/// lifecycle policy are agnostic to which one it holds.
/// </summary>
internal interface ISessionStartContextProvider {
    Task<SessionStartMemoryContextResult> GetAsync(SessionStartMemoryContextRequest request);
}
