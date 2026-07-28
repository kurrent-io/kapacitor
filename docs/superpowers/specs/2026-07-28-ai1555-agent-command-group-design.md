# `kcap agent` command group — design

Date: 2026-07-28
Status: proposed
Issue: AI-1555

## Motivation

The local-attach feature shipped its three commands at the top level of the CLI:

```
kcap run-agent <vendor> [flags] [-- <agent args>]
kcap attach <agent-id>
kcap ls
```

Three problems follow from that shape:

1. **`attach` and `ls` are generic names occupying the top-level namespace.** `kcap ls` says
   nothing about *what* it lists; a reader would guess sessions or repos before agents.
2. **They are undocumented in `kcap --help`.** `help-usage.txt` has no entry for any of the
   three — the only place they appear is the README. A user cannot discover them from the CLI.
3. **`--name` means the *daemon* name**, not the agent name. That is already confusing on
   `kcap attach --name`, and would be a genuine trap on a `stop` subcommand.

There is also no way to stop a locally-started agent. The daemon only stops agents on a
server-origin `StopAgent`/`StopAgentV2` command, and that path explicitly refuses `--private`
agents — so a `kcap run-agent --private` agent cannot be stopped from the CLI at all today.

## Goal

One coherent `kcap agent` group covering the whole lifecycle of a daemon-hosted agent:

```
kcap agent                                          → same as `kcap agent ls`
kcap agent start <vendor> [flags] [-- <agent args>]
kcap agent ls                    [--daemon <name>]
kcap agent attach <id>           [--daemon <name>]
kcap agent stop   <id>           [--daemon <name>]
kcap agent stop   --all [-y]     [--daemon <name>]
```

## Decisions

1. **The old spellings are removed, not aliased.** `run-agent`, `attach`, and `ls` disappear
   from the top-level switch. Local attach is new enough that no scripts or muscle memory
   depend on them, and keeping two spellings would defeat the point of the regrouping.

2. **`--name` becomes `--daemon`.** On `kcap agent stop --name X`, `--name` reads like an agent
   name — a flag that silently targets the wrong thing is worse than a verbose one. `kcap
   daemon --name` is unambiguous in its own group and stays as it is.

3. **`--detached` becomes `-d, --detach`**, matching `kcap daemon start -d`. Same meaning, one
   spelling across both groups, and a short form.

4. **Bare `kcap agent` lists agents.** It does not print help. `kcap agent --help` is the
   discovery path.

5. **A full 32-hex id is sent verbatim; anything shorter is resolved as a prefix.** Agent ids
   are `Guid.NewGuid().ToString("N")`, so pasting one is painful. Resolution runs client-side
   against the existing `List` frame: zero matches errors, two or more errors and lists the
   candidates, exactly one proceeds. Sending a full id verbatim (rather than resolving it too)
   preserves the daemon's PID-record fallback in `HandleStopAgent`, which stops agents that
   survived a prior daemon incarnation and therefore are *not* in the live agent list.

6. **`stop --all` stops every agent the daemon hosts, private ones included**, prompting
   `Stop all N? [y/N]` unless `-y`/`--yes` — the same shape as `kcap daemon stop`.

7. **A local stop bypasses the `IsPrivate` guard; a server-origin stop still honours it.** The
   guard exists so a leaked agent id cannot let the server act on an unregistered agent. That
   reasoning does not apply to a request arriving on the daemon's own 0600 local socket, and
   without the bypass `--private` agents would remain unkillable except by `kill`.

8. **Unchanged:** the group stays Unix-only (Windows gets today's explicit refusal), stays
   server-requiring (not added to `offlineCommands`), and `start` still auto-starts a daemon.

## Command surface

`start` flags, all as today except where noted: `--worktree`, `--private`, `-d, --detach`
(was `--detached`), `--daemon <name>` (was `--name`), and the `--` passthrough boundary —
everything after `--` still goes to the vendor CLI verbatim.

An unknown subcommand prints usage to stderr and exits 1. `kcap agent --help` renders a new
`help-agent.txt`.

## Client structure

- `src/Capacitor.Cli/Commands/RunAgentCommand.cs` → `AgentCommand.cs`, restructured from three
  independent entry points into a subcommand router (`start|ls|stop|attach`), matching how
  `daemon`, `plugin`, and `profile` already work.
- `src/Capacitor.Cli/Program.cs` drops `case "run-agent"`, `case "attach"`, `case "ls"` and
  gains a single `case "agent": return await AgentCommand.HandleAsync(args);`.
- `src/Capacitor.Cli.Core/RunAgentArgs.cs` → `AgentStartArgs.cs`: same parser, with `--daemon`,
  `-d`/`--detach`, and an updated usage string. The removed spellings are rejected as unknown
  flags like any other typo.
- Id-prefix resolution lives in one private helper shared by `attach` and `stop`.

## Wire protocol

`FrameType` is documented as append-only. Two new values:

| Value | Direction | Payload |
|---|---|---|
| `Stop = 8` | client → daemon | `Text` = agent id, or empty = all agents |
| `StopAck = 70` | daemon → client | `Text` = one `id\tstatus` line per agent; status is `stopped` or `failed` |

An id that is not in the live agent map falls through to `TryStopByPidRecordAsync` — the same
survivor-reaping fallback the server-origin path uses, and the reason decision 5 sends full ids
verbatim. Only when that also finds nothing does the daemon reply with the existing `Error`
frame. `LocalControlServer` gets a `case FrameType.Stop:` alongside Spawn/Attach/List/Restart.

**Version skew.** A *running older* daemon that receives frame type 8 throws
`InvalidDataException` inside `FrameCodec.Decode` — before `LocalControlServer`'s switch is
reached — so it logs the fault and closes the connection with no reply. The client sees a bare
EOF. `agent stop` therefore treats "connection closed before any reply" as
`this daemon is too old for 'agent stop' — restart it with 'kcap daemon restart --force --name
<name>'`. Bare `kcap daemon restart` parses to mode `"now"`, which `HandleRestartAsync` refuses
whenever the daemon is busy — and this daemon is guaranteed busy, since the user reached this
message by trying to stop a running agent. Only `--force` bypasses that check, and the
suggestion carries the resolved daemon name (`--name`, not `--daemon` — this is a `kcap daemon`
command line) so it targets the same daemon the user was talking to.

## Daemon changes

`HandleStopAgent`'s body moves into `StopAgentCoreAsync(AgentInstance)`. `HandleStopAgent`
keeps its `if (agent.IsPrivate) return;` guard in front of the call, so server-origin stops
behave exactly as before. The core skips the two `_server.*` calls
(`AgentStatusChangedAsync`, `AppendAgentRunEventAsync`) when the agent is private — an
unregistered agent has no server-side row to update — and otherwise runs the existing sequence
unchanged: graceful `/exit` → 15s wait → cancel `ReadCts` → `TerminateAsync(10s)`. The read
loop's `finally` continues to handle session-end and owned-worktree cleanup.

A new `HandleLocalStopAsync` in `AgentOrchestrator.LocalIpc.cs` calls `StopAgentCoreAsync`
directly, bypassing the private guard per decision 7. For the stop-all case it runs the stops
concurrently via `Task.WhenAll`: each stop can take up to 25s (15s graceful + 10s terminate),
so serial teardown of five agents would take over two minutes.

## Documentation

Per the repo rule, these land in the same PR as the code:

- **New** `src/Capacitor.Cli.Core/Resources/help-agent.txt` — picked up automatically by the
  `Resources\**\*` embedded-resource glob.
- **`help-usage.txt`** gains an *Agents* section listing all four subcommands. This is a fix as
  much as a rename: the three current commands appear in `kcap --help` nowhere.
- **`README.md`** — retitle "Local agents (run-agent / attach / ls)" to "Local agents
  (`kcap agent`)", rewrite the examples, document `stop`, `--all`, and id prefixes, and update
  the two link references (contents list, CLI-command table).

## Testing

- `RunAgentArgsTests` → `AgentStartArgsTests`, extended for `--daemon` and `-d`/`--detach`, and
  asserting the removed `--name`/`--detached` spellings are rejected.
- Subcommand router: unknown subcommand exits 1; bare `agent` routes to `ls`.
- Id-prefix resolution: unique match, no match, ambiguous match, full-32-hex passthrough.
- `FrameCodecTests`: `Stop` and `StopAck` round-trips.
- `AgentOrchestratorLocalAttachTests`: local stop of a **private** agent succeeds (the case the
  server path refuses), stop-all stops every agent, unknown id yields `Error`.
- `dotnet publish -c Release` grepped for IL3050/IL2026 — `Program.cs` and the codec are
  AOT-sensitive and `dotnet build` does not surface those warnings.

## Out of scope

**Flow-participant awareness** — tracked as
[#379](https://github.com/kurrent-io/kcap-cli/issues/379). `HandleLocalListAsync` returns all of
`_agents` unfiltered and `HandleLocalAttachAsync` attaches to any id in it, so agents the daemon
launched as flow participants (e.g. review-flow reviewers) are indistinguishable from the user's
own, and attaching hands the user raw PTY stdin on a participant that is supposed to be
addressed through `send_to_participant`. Adding `agent stop` extends the same blind spot to
termination. This design deliberately leaves that behaviour unchanged.
