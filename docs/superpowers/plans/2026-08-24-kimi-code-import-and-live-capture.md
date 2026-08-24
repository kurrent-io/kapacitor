# Kimi Code historical import and live-capture plan

**Goal:** Add Kimi Code as a first-class kcap source: safely discover and import
historical Kimi transcripts, then optionally add live capture. Keep all Kimi
parsing, discovery, retries, and test coverage in the open-source CLI. Treat
the closed server's accepted vendor/normalizer contract as an explicit release
gate rather than an assumption.

**Scope:** This plan covers `kurrent-io/kcap-cli`. It deliberately does not
check in, upload, or reproduce a real user's Kimi transcript in fixtures.

**Architecture:** A `KimiImportSource : IImportSource`, modeled on the routed
Kiro/Pi importers, discovers the local Kimi wire logs, identifies one root
stream and zero or more child agent streams, and sends the original physical
JSONL lines with a stable line-number space. A future `kcap hook --kimi`
dispatcher and Kimi plugin/adapter would start the same watcher for active
sessions. Historical import does not need hooks.

---

## Observed Kimi local layout and wire contract

The following layout was observed locally on Windows; it must be re-probed on
macOS and Linux before hard-coding path behavior:

```text
~/.kimi-code/sessions/
  wd_<workspace-slug>_<suffix>/
    session_<dashed-uuid>/
      agents/
        main/wire.jsonl
        agent-0/wire.jsonl
        agent-1/wire.jsonl
        ...
```

Each `wire.jsonl` is append-only JSONL. The session id is the dashed UUID in
the `session_...` directory; `main` is the root stream and `agent-N` is a
child stream. The wire records use Unix epoch **milliseconds** in `time`.

Observed top-level records (field values omitted intentionally):

| Record | Relevant fields | Import use |
| --- | --- | --- |
| `metadata` | `protocol_version`, `created_at` | format/version and fallback start time |
| `profile.bind` | `modelAlias`, `environmentDisclosure.cwd`, `time` | cwd, model, start metadata |
| `turn.prompt` / `turn.steer` | `input`, `origin`, `time` | user input / steering boundary |
| `context.append_message` | `message.role`, `message.content`, `message.toolCalls`, `time` | user/assistant messages |
| `context.append_loop_event` | `event.type`, `uuid`, `turnId`, `step`, `time` | incremental content plus `tool.call` and `tool.result` |
| `turn.ended` | `turnId`, `reason`, `durationMs`, `time` | turn completion and fallback end time |
| `task.started` / `task.terminated` | `info`, `outputTail`, `time` | command-task context |
| `plugin.session_start` | `time` | evidence that Kimi records session startup; not proof of a callable plugin API |

Observed nested loop-event kinds are `step.begin`, `content.part`, `tool.call`,
`tool.result`, and `step.end`. The importer must preserve original lines and
their order; the server normalizer—not the discovery pass—owns their semantic
interpretation.

### Privacy and fixture rules

- Never add a real `~/.kimi-code` file to the kcap repository or test output.
- Build small synthetic fixtures with only invented prompts, paths, command
  names, UUIDs, and timestamps.
- Test structural variants: no `profile.bind`, malformed line, no `main`,
  empty child, incomplete final line, and a session with multiple children.
- Do not log raw transcript content in normal CLI diagnostics. Report paths,
  session IDs, line counts, and parser error categories only.

---

## Phase 0 — resolve the server contract before claiming end-to-end support

The local importer can be implemented and fully mock-tested without server
source. A real `vendor=kimi` import cannot be declared working until the
following are confirmed against an authorized test server by a maintainer.

- [ ] Confirm whether `/api/sessions/{id}/transcript` accepts `vendor=kimi`.
- [ ] Confirm the server has a Kimi wire normalizer, or identify an existing
  documented generic wire envelope that the CLI may legitimately emit.
- [ ] Confirm session lifecycle endpoints for Kimi (start/end), root and child
  transcript watermarks, and canonical session/agent identity rules.
- [ ] Confirm high-water-mark behavior: raw physical line numbers, partial
  resend, and duplicate event identity must be safe for append-only Kimi logs.
- [ ] Confirm whether an unknown vendor is rejected, silently stored without
  normalization, or normalized generically. Do not ship an importer that
  silently uploads unusable transcripts.

**Decision gate:**

- If a Kimi normalizer/generic protocol already exists, implement the client
  against that documented contract.
- If it does not, keep the PR's parser/discovery tests but do not expose a
  production `--kimi` import flag until the server-side handoff supplies the
  contract. Do not relabel Kimi lines as Codex merely to get them accepted.

---

## Phase 1 — Kimi historical importer (open-source CLI)

### 1. Add source-local path and wire helpers

**New files:**

- `src/Capacitor.Cli.Core/Harness/Kimi/KimiPaths.cs`
- `src/Capacitor.Cli/Harness/Kimi/KimiWireReader.cs`
- `src/Capacitor.Cli/Harness/Kimi/KimiImportSource.cs`

**Tests:**

- `test/Capacitor.Cli.Tests.Unit/Harness/Kimi/KimiPathsTests.cs`
- `test/Capacitor.Cli.Tests.Unit/Harness/Kimi/KimiWireReaderTests.cs`
- synthetic fixture tree below the appropriate test-fixtures directory

Implementation requirements:

1. Resolve the Kimi root from the platform home directory, with an explicit
   constructor override for tests. Do not inspect an arbitrary workspace.
2. Discover only `session_<UUID>/agents/<agent-id>/wire.jsonl` below the Kimi
   sessions root. Skip traversal errors, symlinks escaping the Kimi root, bad
   UUIDs, missing `main`, and unreadable files without failing other sessions.
3. Read only enough initial JSONL to obtain `profile.bind.environmentDisclosure.cwd`,
   `modelAlias`, and a first timestamp; use `metadata.created_at` and filesystem
   time only as ordered fallbacks.
4. Use one, documented canonical session-ID form for both historical and live
   paths. Pin it in tests after Phase 0 confirms the server's choice.
5. Make `main` the parent session. Preserve child agent directory names as
   server child-stream identifiers only after Phase 0 confirms the child route.
6. Preserve raw line order and physical line numbers. Blank lines must retain
   their line-number position; malformed/incomplete tail lines must not cause
   a later valid line to be skipped.

### 2. Implement `IImportSource`

Model the source on `KiroImportSource` for lifecycle ordering and `Gemini`/
`Antigravity` for child-stream ordering.

1. `DiscoverAsync` returns root sessions with Kimi-specific `SourceMeta`:
   root wire path, child wire paths, cwd, model, start/end estimates, and
   source-format version.
2. Apply `--session`, `--cwd`, and `--since` before classification. `--since`
   uses the source's first Kimi timestamp.
3. `ClassifyAsync` counts importable physical lines and queries the authorized
   server watermark for the root and each child stream. Respect excluded repos
   and paths via existing shared import filtering.
4. `ImportSessionAsync` posts lifecycle start before any transcript batch, then
   root content, then each child stream, then lifecycle end. A failed batch
   prevents the terminal end marker and leaves the session repairable.
5. Mark Kimi title generation unsupported initially; do not shell out to a
   model merely to invent a title. Revisit only if the normalizer/server defines
   a canonical Kimi title path.

### 3. Register the importer without falsely advertising live setup

**Likely modifications:**

- `src/Capacitor.Cli/Commands/VendorSelection.cs` — recognize `--kimi`.
- `src/Capacitor.Cli/Program.cs` and `SetupCommand.cs` — construct the source.
- `src/Capacitor.Cli.Core/Resources/help-import.txt` and relevant usage/help
  resources — accurately list Kimi as *historical import* support.
- `test/Capacitor.Cli.Tests.Unit/HarnessCatalogConformanceTests.cs` — extend or
  deliberately separate importer-only vendors from installable harnesses.

Do **not** add Kimi to `HarnessCatalog`, `plugin install`, or the status setup
nudges in this phase unless Phase 2 provides an actual installable live adapter.
If current conformance assumes every importer is installable, refactor that
assumption rather than presenting an unavailable Kimi plugin to users.

---

## Phase 1 verification

### Fast, local tests (no server and no Kimi account)

- [ ] Unit-test `KimiPaths` on Windows path shapes and home overrides.
- [ ] Unit-test each observed event's metadata extraction and timestamp
  conversion with sanitized JSONL.
- [ ] Test root/child discovery, filters, malformed lines, empty streams,
  partial final lines, and inaccessible paths.
- [ ] Test vendor selection: `--kimi` alone, combined with another vendor, and
  typo diagnostics.
- [ ] Run `kcap import --kimi --discover --json` against a synthetic home
  override; assert no network call and no transcript content on stdout.

### HTTP contract tests (mock server only)

- [ ] Use the existing integration-test HTTP harness to assert lifecycle start
  precedes all transcript batches and end follows successful root/child sends.
- [ ] Assert each batch uses the agreed `vendor` value, session ID, child agent
  ID, physical source line numbers, and no real paths beyond the selected cwd.
- [ ] Cover new, already-loaded, and partial-watermark repair paths.
- [ ] Inject a batch failure and prove no end marker or success ledger is
  recorded; rerun and prove it resumes.
- [ ] Verify `--private` and configured exclusions cover both parent and child
  streams.

### Authorized live smoke (closed-server dependency)

- [ ] Renew authentication first; do not place a token in fixtures or logs.
- [ ] Create one deliberately non-sensitive Kimi session in a disposable test
  repository and import with `--private`.
- [ ] Verify in kcap that the session is readable, Kimi-labeled, mapped to the
  correct repository/cwd, has prompt/message/tool records, and has no duplicate
  events after a second import.
- [ ] Test one child-agent session, interruption during import, then a repair
  import after restart.
- [ ] Record the exact CLI/server versions and server response in the PR.

---

## Phase 2 — live Kimi capture (separate PR after historical import ships)

Historical import requires no hooks. Live capture needs an adapter only if Kimi
exposes a stable callback/plugin configuration surface. The observed
`plugin.session_start` wire record is evidence of an event, not proof that a
shell hook can be registered.

### Discovery/probe task

- [ ] Consult Kimi's official plugin/hook documentation and inspect the locally
  installed Kimi configuration format. Record the exact supported callbacks,
  payload shape, plugin install location, and Windows/macOS/Linux differences.
- [ ] If no supported lifecycle callback exists, implement no unsupported
  background polling. Keep historical import as the supported integration.

### Required live-capture behavior

If Kimi has a supported integration surface, add a `kcap hook --kimi` command,
a parser/installer under `Harness/Kimi`, and a `plugin install --kimi` path.

1. **Start:** on Kimi session start, identify `{sessionId, agentId, cwd,
   wireFile, startedAt, model}`; post one idempotent start lifecycle event and
   launch/tell the daemon to tail the specified wire file.
2. **Streaming:** tail exact appended physical lines, including content/tool
   loop events, preserving the same line numbers used by historical import.
   Use the existing durable hook/transcript spool so temporary auth/network
   failures retry rather than dropping events.
3. **Children:** register every `agent-N` wire path as a child stream under the
   root Kimi session. A child must not end the parent.
4. **End:** use Kimi's documented end callback when available. Otherwise only
   synthesize end after a documented, conservative process-exit/idle rule; do
   not use an arbitrary timer that can close a long-running task.
5. **Safety:** hook invocation must be bounded, idempotent, silent on success,
   avoid leaking transcripts to stdout/stderr, and leave Kimi usable when kcap
   is offline or unauthenticated.

### Live-capture tests

- [ ] Installer/parser tests: install, idempotent reinstall, removal, malformed
  user config preservation, and platform path quoting.
- [ ] Hook tests: start deduplication, start-before-first-line race, child
  routing, reconnect/retry, end sequencing, expired token, and disabled repo.
- [ ] End-to-end local test: append synthetic lines while watcher runs and
  compare streamed output with historical-import output for the same fixture.
- [ ] Authorized Kimi smoke: one root session plus a child, restart kcap during
  the session, then verify no loss or duplication server-side.

---

## Upstream contribution workflow

`kurrent-io/kcap-cli` currently grants this account **READ** permission, so a
fork is required unless a maintainer grants write access. Use a fork in the
user's organization and keep both remotes explicit:

```powershell
# From the local kcap-cli checkout; preserves origin as the upstream remote.
gh repo fork kurrent-io/kcap-cli --org MooseGooseConsulting --remote --remote-name fork
git push --set-upstream fork feat/kimi-history-import
gh pr create --repo kurrent-io/kcap-cli `
  --base main --head MooseGooseConsulting:feat/kimi-history-import `
  --title "feat: import Kimi Code history"
```

Before opening the PR:

- [ ] Rebase/merge the current upstream `main` and run the focused tests,
  complete unit suite required by the repository, build, and NativeAOT publish.
- [ ] Include fixture provenance (synthetic), OS/runtime used, and all Phase 0
  server-contract answers.
- [ ] State clearly whether the PR is importer-only or includes a certified
  live Kimi integration. Do not open it as a draft once the checks pass.
- [ ] Keep live hooks in a follow-up PR unless the historical importer and
  server normalizer have already been validated together.

## Commit sequence

1. `docs: plan Kimi Code history import` (this plan).
2. `feat: discover Kimi Code session transcripts` (paths, reader, unit tests).
3. `feat: import Kimi Code session history` (source, registration, HTTP tests,
   help text).
4. `feat: capture live Kimi Code sessions` (only after Phase 2 validation).

