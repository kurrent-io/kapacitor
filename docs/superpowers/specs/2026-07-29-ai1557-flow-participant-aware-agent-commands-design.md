# Flow-participant-aware `kcap agent` commands — design

Date: 2026-07-29
Status: proposed
Issue: AI-1557 / [#379](https://github.com/kurrent-io/kcap-cli/issues/379)

## Motivation

`kcap agent ls|attach|stop` treats every agent the daemon hosts identically.
`HandleLocalListAsync` returns all of `_agents` unfiltered and `HandleLocalAttachAsync` attaches
to any id in it, so an agent the daemon launched as a **review-flow participant** — a reviewer
mid-round — is indistinguishable from one the user started with `kcap agent start`.

Two concrete failures follow:

1. **Attach injects turns into a flow.** Participants are addressed through the flow protocol
   (`send_to_participant`), never by typing at them. `kcap agent attach <reviewer-id>` hands the
   user raw PTY stdin, with no round, no routing, and no record of the interjection.
2. **Stop kills a participant silently.** `kcap agent stop --all` stops every agent in the map.
   A reviewer vanishes mid-round and the flow has no idea why.

The second one arrived with `agent stop`, which is why this was deferred rather than solved in
the same change: the read path was already blind, and the write path made the blindness bite.

The daemon already has everything needed. `AgentInstance` carries `Kind`
(`LaunchKind.Default`/`Review`/`ReviewFlow`), plus `FlowRunId`/`FlowRole` for flow launches. None
of it reaches the CLI.

## Goal

The CLI can tell a review or flow agent from the user's own, shows it, and refuses to mutate it
by accident.

## Decisions

1. **The protected set is `Kind != Default`** — `ReviewFlow` participants and `Review` agents.
   Plain web-UI-launched agents (`Default`, not locally spawned) stay unprotected: they are the
   user's own work by another route, and protecting them would make `stop --all` nearly useless.

2. **Attach to a protected agent is read-only**, not refused. Watching a reviewer work is a real
   debugging need with no other local equivalent, and read-only preserves the flow invariant
   exactly — output flows out, nothing flows in.

3. **A read-only viewer does not resize the PTY.** It is never added to
   `AgentInstance.ClientDims`, so it cannot shrink a participant's terminal through the
   min-clamp. The cost lands on the observer: if their terminal differs from the participant's,
   their own view wraps or clips. That is the correct side to put it on.

4. **`stop` on a protected agent refuses unless `--force`.** `stop --all` stops only unprotected
   agents and *states* what it skipped — a silent omission reads as "stopped everything".

5. **Enforcement is daemon-side, not client-side.** The daemon owns the protection for both
   attach and stop, so a stale or hand-rolled client cannot bypass it, and the two subcommands
   share one model. This costs a wire change that a client-side filter would not.

6. **Against an older daemon, no protection applies.** Accepted, not mitigated — see Version
   skew.

## Wire protocol

`FrameType` is append-only. One new value:

| Value | Direction | Payload |
|---|---|---|
| `StopV2 = 10` | client → daemon | `mode⇥agentId` — mode `normal`\|`force`; empty id = all eligible |

Two payload extensions to existing frames:

- **`AgentList`** rows become `id⇥status⇥repo⇥kind⇥flowRunId⇥flowRole`. `kind` is
  `agent`\|`review`\|`review-flow`; the flow fields are empty for non-flow agents.
- **`StopAck`** rows keep the `id⇥status` shape, with `status` gaining `skipped` alongside the
  existing `stopped`\|`failed`.
- **`Attached`** gains a trailing read-only flag and reason, appended after the existing payload.
  This follows the precedent `FrameCodec.Spawn` already set for `isPrivate` ("APPENDED after
  args: older parsers ignore trailing bytes").

`Stop = 8` stays decodable so nothing regresses mid-upgrade, but the CLI only sends `StopV2`.

## Daemon changes

- `HandleLocalListAsync` emits the three new columns from `Kind`/`FlowRunId`/`FlowRole`.
- `HandleLocalAttachAsync` resolves the agent's `Kind`. For a protected agent it runs
  `AttachClientLoopAsync` in read-only mode: the sink is registered as today, the `Stdin` arm is
  dropped, the `Resize` arm is dropped, and the client is never entered into `ClientDims`.
- A new `HandleLocalStopV2Async(mode, agentId, …)` applies decision 4:
  - explicit id, protected, `normal` → `Error` naming the kind and flow;
  - explicit id, protected, `force` → stop;
  - empty id, `normal` → stop unprotected agents; protected ones appear in the ack as `skipped`;
  - empty id, `force` → stop everything, as `stop --all` does today.
- `LocalControlServer` routes `FrameType.StopV2`.

The `IsPrivate` guard on `HandleStopAgent` and the server-origin path are untouched.

## Client changes

- `AgentRow` gains `Kind`, `FlowRunId`, `FlowRole`. The parser accepts **3 or more** columns and
  defaults the new ones, so a running older daemon still lists correctly.
- `ls` renders a `KIND` column, with the flow role appended for flow agents.
- `attach` prints the read-only banner when the daemon reports the flag, and skips its own stdin
  pump — cosmetic, since the daemon already drops it.
- `stop` accepts `--force`, sends `StopV2`, and reports `skipped` rows distinctly from
  `stopped`. With `--all --force`, the confirmation prompt lists protected agents in their own
  labelled group, so the blast radius is visible before the user answers.

## Version skew

An older daemon replies with three-column `AgentList` rows, so every agent reads as `agent` and
**no protection engages** — `stop --all` behaves exactly as it does today.

This is deliberately not mitigated. A three-column reply from an old daemon is indistinguishable
from a new daemon reporting three unprotected agents, so there is nothing to detect without
adding a version probe. The window closes on its own: a daemon self-restarts onto the new binary
when idle after `kcap update`. `StopV2` is the one place skew is visible, and it already
surfaces through the existing "this daemon is too old for `agent stop`" message.

## Documentation

- `help-agent.txt` — read-only attach, `stop --force`, and what `stop --all` skips.
- `README.md` — the `### Local agents (kcap agent)` section. Its current `--all` warning
  (added when this gap was deferred, pointing at #379) is replaced by the real behaviour.

## Testing

- Daemon: `SeedAgentForTest` already takes `kind`/`flowRunId`/`flowRole`, so protection tests are
  cheap. Cover — protected attach drops stdin and resize and never enters `ClientDims`;
  protected stop without force yields `Error`; with force stops; stop-all marks protected rows
  `skipped` and stops the rest.
- Client: `AgentRow` parsing for 3-column (old daemon) and 6-column rows; `ls` rendering;
  `skipped` rows reported distinctly.
- `AgentVerbDispatchTests` gains a case pinning that a protected agent refuses `stop` without
  `--force`.

## Out of scope

- **The `--all` confirmation TOCTOU** — the client lists and prompts, then the daemon
  re-enumerates, so an agent launched during the prompt is stopped unlisted. Tracked separately;
  this design narrows but does not close it.
- **Telling the flow that a participant was force-stopped.** The flow still sees a participant
  vanish; attributing that needs server-side work.
- **Plain web-UI-launched agents**, per decision 1.
