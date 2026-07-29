namespace Capacitor.Cli.Core.LocalIpc;

/// Wire contract for the daemon↔local-client socket. Values are explicit and
/// MUST be append-only — they are serialized as a single byte (see FrameCodec).
public enum FrameType : byte {
    // client → daemon
    Spawn   = 1,
    Attach  = 2,
    Stdin   = 3,
    Resize  = 4,
    Detach  = 5,
    List    = 6,   // request the daemon's agent list (for `kcap agent ls`)
    Restart = 7,   // request restart-after-update (Text = "when-idle"|"now"|"force")
    Stop    = 8,   // stop an agent (Text = agent id; empty = every agent this daemon hosts)
    StopV2  = 10,  // stop with a force flag (see FrameCodec.StopV2); supersedes Stop
    // daemon → client
    Attached  = 64,
    Stdout    = 65,
    Exited    = 66,
    Error     = 67,
    AgentList = 68, // UTF-8 table payload: one `id\tstatus\tcwd` line per agent
    RestartAck = 69, // acknowledgement for Restart (Text = short status)
    StopAck    = 70, // acknowledgement for Stop (Text = one `id\tstatus` line per agent; status is "stopped", "skipped", or "failed")
    AttachedReadOnly = 71, // Attached for a protected agent: id + reason + snapshot, no input accepted
}
