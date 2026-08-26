# Chat for PTY harnesses, rendered from the transcript (AI-2196)

Slice of the desktop shell (parent AI-2171; design canvas "Hosted Agents Shell",
Session artboard — the Chat tab and the composer). A Claude or interactive Codex
session gets a **Chat** tab beside its Terminal tab, rendered from the transcript
file the vendor itself writes; input still goes to the PTY. The same session, read
two ways.

Out of scope, by prior decomposition and by the canvas annotations: structured
turn frames, the "NEEDS YOU" approval card, permission-mode control and the "+"
attachment affordance (AI-2197); chat for ACP / app-server sessions, which have
no PTY and no transcript file the daemon locates (AI-2197); the work-context
sidebar (AI-2198). Deferred within this slice: markdown tables and images,
syntax highlighting in code blocks, and rendering thinking or tool-result bodies.

## Decisions

Settled with the owner during brainstorming, 2026-08-26:

1. **The daemon tells the app where the transcript is: additive `transcript_path`
   on `AgentStatusDto`.** The daemon already resolves every PTY agent's vendor
   session id by scanning the vendor's session tree (a 2 s poll for up to 3 min)
   and caches the Codex rollout path for its own turn diagnostic; the agent's
   worktree — `<repo>/.capacitor/worktrees/agent-<guid>` — never reaches the app
   and cannot be reconstructed from `RepoPath`. Rejected: shipping session id plus
   worktree path and porting both locators into Core (duplicates work the daemon
   does anyway); a client-side scan of the repo's Claude project dir (ambiguous
   with two agents on one repo, and no Codex equivalent without the session id).
   "No protocol change" is read as AI-2195 read it: no new frame family; trailing
   nullable DTO members are the sanctioned path.
2. **The transcript → chat projection lives in Core, public, and emits
   `AcpEventEnvelope`.** `AcpEventKind` is the vocabulary the daemon's ACP and
   app-server runtimes already emit live and the one AI-2197's frames would carry,
   so the Chat tab's renderer is written once. Core has the internal
   `JsonElementExtensions` the repo mandates for JSON access; BCL plus
   `JsonDocument` keeps it AOT-clean. Rejected: app-local mappers behind an
   `InternalsVisibleTo` on a production assembly; daemon-side projection pushed
   over a new frame (that *is* AI-2197).
3. **The composer sits on the Chat tab only.** The canvas draws it under both
   tabs, but the Terminal tab is its own input surface — a second one beneath it
   would be two ways to type into one process. Recorded as a deliberate
   deviation from the artboard.
4. **Markdown: Markdig plus an in-house renderer.** `Avalonia.Controls.Markdown`
   is AvaloniaUI's commercial Accelerate control (license key in the csproj — not
   for a public repository); `Markdown.Avalonia` is an Avalonia-12 alpha with no
   cut since April 2026 and a single author; `LiveMarkdown.Avalonia` brings
   TextMateSharp and its grammar bundle. Markdig (BSD-2, 74M downloads, no
   dependencies) parses; a small renderer maps the constructs agents actually
   emit to Avalonia inlines and blocks, and anything it does not know renders as
   its literal text rather than vanishing.
5. **Chat is the default tab for a PTY session** (the canvas selects it on every
   rail click). The Terminal stays attached while Chat is up — the composer needs
   the live PTY link, and a reattach would only replay scrollback anyway.
6. **Thinking and tool-result bodies are not rendered.** The canvas timeline is
   user bubbles, assistant prose and one muted row per tool call; a tool row
   carries a one-line detail from its input and an outcome mark that its result
   flips. Thinking is projected (the vocabulary has it) and dropped by the tab.

## 1. Wire change (additive only)

`AgentStatusDto` (`src/Capacitor.Cli.Core/LocalIpc/StatusIpc.cs`) gains one
trailing member after `Title`:

- `TranscriptPath` — `string? TranscriptPath = null`, serialized
  `transcript_path`, always emitted (null, never omitted — the context's own
  rule). The daemon stamps `AgentInstance.TranscriptPath` in
  `SnapshotAgentsForStatus`. `null` means any of: an older daemon, not resolved
  yet, the 3-minute poll gave up, or a runtime with nothing to read (app-server
  Codex, every ACP vendor). The app never distinguishes these; it waits.

Daemon side (`src/Capacitor.Cli.Daemon`):

- `AgentInstance.CodexRolloutPath` becomes `TranscriptPath`, populated for both
  PTY vendors; the Codex turn diagnostic reads the renamed property unchanged.
- `SessionTranscriptLocator` (`Harness/Claude/`) gains
  `TryLocateWinner(projectDir, worktreePath, spawnedAtUtc, ruledOut)` returning
  `(SessionId, Path)?`, mirroring `CodexSessionRolloutLocator.TryLocateWinner`;
  `TryLocate` delegates to it. The path is the matched file under the
  per-worktree project dir — a symlink onto the source repo's dir, which the app
  opens as-is.
- `DetectSessionIdAsync`'s Claude and Codex branches set `agent.TranscriptPath`
  from the winner, and `PollForSessionIdAsync` pulses `_statusNotifier` after
  the match lands on the agent — mutation first, pulse second, the notifier's
  own contract. The path resolves within a few seconds of launch for both
  vendors (Claude writes its first record, with `cwd`, before its first prompt
  renders).

The path is the daemon's view of the filesystem (its `CLAUDE_CONFIG_DIR` /
`CODEX_HOME`), which is the correct one: it launched the process. Nothing else
on the wire changes; `FrameType` and the capability list are untouched.

Serialization acceptance (extends `StatusIpcJsonTests`): old JSON without the
member deserializes to `null`; a value and `null` both serialize with
`transcript_path` present, last, in the declared trailing order. The daemon
snapshot test (`AgentStatusSnapshotTests`) asserts the serialized payload
carries `null` before detection and the path after.

## 2. Core: tail and projection

### `JsonlTail` (`src/Capacitor.Cli.Core/JsonlTail.cs`)

Vendor-neutral, BCL-only, one instance per file, holding a byte cursor.
`ReadAppended()` returns `TailRead(IReadOnlyList<string> Lines, bool Reset, bool Missing)`:

- Opens `new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)`
  per call and closes it after. The trap named on the issue: `File.ReadAllText`,
  `File.ReadLines` and friends open `FileShare.Read`, which on Windows denies the
  agent the write handle to its own transcript — worst during its shutdown
  drain. Invisible on macOS/Linux; only the Windows CI leg catches a violation.
- A missing file is `Missing = true` with no lines and no cursor change.
- `Length < cursor` is a truncation or replacement: the cursor resets to zero
  and the read reports `Reset = true` so the consumer clears what it rendered.
- Reads `[cursor, Length)`, splits on `\n` (a trailing `\r` is stripped), skips
  blank lines, and **holds back an unterminated final chunk**: the cursor
  advances only past the last `\n`, so the chunk is re-read whole once its
  newline lands. This is `WatchCommand`'s `Hold` policy, as a pure function
  (`SplitCompleteLines(ReadOnlySpan<byte>, out int consumed)`) the tail wraps.
  UTF-8 decoding is per complete line — a multibyte sequence cannot straddle a
  newline, so no decoder state carries over.

### Projection

Two public, stateless line mappers, each `Project(string line) →
IReadOnlyList<AcpEventEnvelope>` (empty for anything unparseable or
uninteresting), plus a registry `TranscriptProjection.For(vendor)` beside them
in Core returning the mapper or null — the one registration site, so adding a
vendor's transcript means a new `Harness/<Vendor>/` file and one line here.
Every envelope carries `TimestampIso` when the record has a timestamp; `Seq`
stays 0 — arrival order is the order.

`ClaudeTranscriptEvents` (`Harness/Claude/`), keyed on the record's root `type`:

- `user`, not `isMeta`, not `isSidechain`: a string `message.content` is one
  `user_message`; an array yields one `user_message` per `text` block and one
  `tool_result` per `tool_result` block (`ToolCallId` = `tool_use_id`,
  `ToolIsError` = `is_error`, `ToolResult` = the block's text, capped at 4 KiB).
  Before emitting user text, `<system-reminder>…</system-reminder>` blocks and
  the local-command wrappers (`<command-name>`, `<command-message>`,
  `<command-args>`, `<local-command-stdout>`, `<local-command-caveat>`) are
  stripped; text that is blank afterwards is not emitted.
- `assistant`, not `isSidechain`: per content block — `text` → `assistant_text`;
  `thinking` → `assistant_thinking` (`ThinkingEncrypted` when the block carries
  no text); `tool_use` → `tool_call` (`ToolCallId` = `id`, `ToolName` = `name`,
  `ToolInputJson` = the raw `input` object). `Model` = `message.model`.
- Everything else is skipped: `attachment`, `summary`, `system`,
  `file-history-snapshot`, `file-history-delta`, `mode`, `permission-mode`,
  `last-prompt`, `ai-title`, `atis-latch`, `worktree-state`,
  `queue-operation`, `progress`, and any type this build has never heard of.

`CodexRolloutEvents` (`Harness/Codex/`), keyed on `type == "response_item"` and
then `payload.type`; every other envelope type (`event_msg`, `turn_context`,
`session_meta`, `world_state`, `compacted`,
`inter_agent_communication_metadata`) is skipped:

- `message` with role `user`: the `input_text` blocks joined → `user_message`,
  unless the text opens with an injected prelude (`<environment_context>`,
  `# AGENTS.md instructions`, `<turn_aborted>`, `<user_instructions>`,
  `<permissions instructions>`); role `assistant`: the `output_text` blocks →
  `assistant_text`; roles `developer` and `system` are skipped.
- `function_call` → `tool_call` (`ToolCallId` = `call_id`, `ToolName` = `name`,
  `ToolInputJson` = `arguments`); `custom_tool_call` → `tool_call`
  (`ToolInputJson` = a JSON object `{"input": …}` wrapping the raw input string);
  `function_call_output` and `custom_tool_call_output` → `tool_result`
  (`call_id`; the output string, or its text blocks joined, capped at 4 KiB).
- `reasoning` → `assistant_thinking` (summary texts joined;
  `ThinkingEncrypted` when only `encrypted_content` is present).
- `agent_message` (inter-agent traffic) is skipped.

## 3. App: the Chat tab

### `ChatTabViewModel`

Constructed by `WorkspaceViewModel` for a PTY session only — the same gate as
the Terminal tab (`HostedHarnessCatalog.ShowsTerminal`) — once the first dto
resolves, because the projector is chosen by the dto's vendor. Ctor-scoped like
its siblings; `TeardownAsync` is its one exit. Inputs: agent id,
`IDaemonClientService`, the sibling `TerminalTabViewModel`, the projector from
`TranscriptProjection.For(vendor)` (null → the `Unavailable` phase), and a
`TimeProvider`.

Phases (`ChatTabPhase`): `Waiting` (no `transcript_path` yet — muted "Waiting
for the transcript…"), `Reading`, `Missing` (a path that does not exist on
disk — "The transcript is not readable from here"), `Unavailable` (a PTY vendor
with no projector — "No chat view for this harness"). A session that ends keeps
its items and keeps polling until teardown: the file outlives the process and
may still receive its final records.

Path watch: `daemon.Agents.Connect().ObserveOn(RxSchedulers.MainThreadScheduler)`
filtered to the agent id; the first non-null `TranscriptPath` starts the tail; a
later, different path restarts from zero (a re-resolution after a daemon restart).

Poll: a `TimeProvider` timer every 500 ms (a tuning constant, not a contract).
A tick with a read still in flight is skipped. The read and the projection run
on the thread pool; the apply hops to the UI thread through
`Dispatcher.UIThread.InvokeAsync`, guarded by a generation `TeardownAsync`
bumps so a late completion mutates nothing. The first read of a long transcript
produces one batch, applied under one dispatch.

Items — a `ReadOnlyObservableCollection<ChatItemViewModel>` mutated on the UI
thread — in three shapes:

- `UserTurnItem(Text)`: the canvas's right-aligned bubble, plain text.
- `AssistantTextItem(Text)`: markdown-rendered prose, one item per envelope.
- `ToolCallItem(Name, Detail, Outcome)`: the muted `›` row. `Outcome` is a
  bound property ∈ Running | Done | Error, flipped in place when the matching
  `tool_result` arrives (indexed by `ToolCallId`; an unmatched result is
  ignored). `Detail` is `ToolDetail.From(name, inputJson)`: the first present
  of `description`, `command`, `cmd`, `file_path`, `path`, `pattern`, `query`,
  `url`, `skill`, `prompt`, `input`; its first line, trimmed to 80 characters
  with an ellipsis; empty when none applies.

`assistant_thinking` and unmatched `tool_result` envelopes create no item;
`Reset` clears the items and the pairing index.

Follow-tail is a view concern: the `ScrollViewer` scrolls to the end on an add
only when it was already at the end, so a user reading history is not yanked.

### Markdown

`MarkdownView` — a `Control` with a styled `Text` property — rebuilds its
content from a Markdig AST (default CommonMark pipeline plus auto-links) on
every change. `MarkdownBlocks` maps: paragraphs and headings →
`SelectableTextBlock` with inlines (`Bold`, `Italic`, monospace `Run` for code
spans, `LineBreak`); fenced and indented code → a `Border` around a monospace
`SelectableTextBlock`; bullet and ordered lists → marker + nested content rows;
block quotes → a left rule beside the content; thematic breaks → a hairline;
links → an accent-coloured underlined run opened through `UrlOpener` on click.
HTML, tables, images and any other node render their literal source text —
degraded, never dropped. User bubbles do not go through it: what the user typed
is shown as typed.

`Markdig` is added to `Directory.Packages.props` and the app csproj. The app
is not NativeAOT, so trimming is not a concern for it.

### Tab strip and workspace

`WorkspaceViewModel`:

- `Chat` (`ChatTabViewModel?`): created from the presence stream on the first
  dto that passes the PTY gate, disposed by `TeardownAsync`. Null for a
  non-PTY session, whose tab strip keeps today's muted note.
- `ActiveTab` (`WorkspaceTab.Chat | Terminal`), default Chat;
  `ShowChatCommand` / `ShowTerminalCommand`; `IsChatActive` / `IsTerminalActive`
  drive the pill styling. A new workspace (any `OpenSession`) opens on Chat.
- `ShowsChatTab` is `ShowsTerminalTab` — one gate, exposed once.

`WorkspaceView`: `ChatTabButton` precedes `TerminalTabButton`; a `ChatTabView`
(`ChatHost`) and the existing terminal grid share the content cell. **The
terminal control stays laid out on both tabs** — hidden by opacity and
hit-test visibility, not `IsVisible` — so the pane size it reports to the PTY
is the real pane size whichever tab is up. An `IsVisible=false` control is never
measured: a workspace opened on Chat would then keep the constructor's 80×24
and, through the daemon's min-clamp across viewers, shrink the agent's terminal
for every other attacher. Focus follows the tab: the terminal takes keyboard
focus only while its tab is active; the composer takes it on Chat.

### Composer

Lives on `ChatTabViewModel`, sends through the sibling Terminal tab:

- `ComposerText`; `SendCommand` (`CreateFromTask`), executable iff the terminal
  is `Attached`, not read-only, and the text is not blank. Success clears the
  text; a failed send keeps it and surfaces the reason on the hint line.
- `ComposerHint` follows `Terminal.State`: attached read-write → "Reply to
  {vendor label} · Enter sends · Shift+Enter for a new line"; attached read-only
  → "Read-only: {reason}"; `Resolving`/`Connecting` → "Connecting to the
  terminal…"; `Detached`/`Failed` → "Reattach the terminal to send";
  `Exited`/`SessionEnded` → "This session has ended"; `NoTerminal`/`NotFound` →
  "No terminal to send to". The vendor label comes from
  `HostedHarnessCatalog.LabelFor` over the daemon's advertised options.
- Footer: model label (`HostedHarnessCatalog.ModelLabelFor`, "default" for
  none), the session's status dot (`SessionStatusDots.For`) and status word
  (the dto's `Status`, verbatim, like the Home cards). No permission-mode chip:
  a hosted launch always runs `bypassPermissions`, and switching modes is
  AI-2197's.

`TerminalTabViewModel.SendTextAsync(string text)` → `Task<bool>`: false without
a live read-write attach; otherwise writes `TerminalInputEncoder.Encode(text)`
through the current client under the same generation check as keyboard input.
`Encode` normalizes `\r\n` to `\n` and drops one trailing newline; a single line
becomes UTF-8 text plus `\r`; a multi-line text is wrapped in bracketed paste
(`ESC[200~` … `ESC[201~`) followed by `\r`, which both Claude Code and the Codex
TUI honour as one submitted message. Enter sends and Shift+Enter inserts a
newline — a view-level key handler on the composer's `TextBox`.

## 4. Testing

`test/Capacitor.Cli.Core.Tests.Unit`:
- `JsonlTailTests`: complete lines delivered once; an unterminated final line
  held, then delivered whole after its newline; CRLF; blank lines skipped;
  truncation → `Reset` and a re-read from zero; missing file → `Missing`;
  a file held open for writing by another handle is still readable (the
  sharing-mode pin — asserted through a write handle opened first).
- `ClaudeTranscriptEventsTests` / `CodexRolloutEventsTests`: one fixture line
  per shape in §2, the skip lists, wrapper stripping, prelude skipping,
  result capping, and a malformed line → empty.
- `StatusIpcJsonTests`: `transcript_path` round trip, trailing order, old JSON
  → null.

`test/Capacitor.Cli.Daemon.Tests.Unit`:
- `SessionTranscriptLocatorTests`: the winner carries the matched file's path.
- `AgentStatusSnapshotTests`: `transcript_path` null before detection, the
  value after — on the serialized payload.
- The notifier pulse on a detected session id, pinned wherever the existing
  detection tests already drive the poll.

`test/Capacitor.App.Tests.Unit` (over `FakeDaemonClientService`,
`FakeTerminalAttachClient`, `FakeTimeProvider`, a `TempDir` transcript):
- `ChatTabViewModelTests`: `Waiting` until a path; the path starts reading and
  the initial load renders items in file order; lines appended after a tick
  render; a held partial line does not render until complete; tool outcome
  pairing (Done, Error); `Reset` on truncation; a different path restarts;
  ticks stop after teardown; a projector-less vendor → `Unavailable`; a removed
  agent keeps its items.
- Composer: `SendCommand` enablement across every terminal phase and the
  read-only case; exact bytes for single-line (`text\r`) and multi-line
  (bracketed) sends via `FakeTerminalAttachClient.SentInput`; the text clears on
  success and survives a failure; every hint string.
- `TerminalInputEncoderTests`, `ToolDetailTests` (key priority, first line,
  80-character cut).
- `MarkdownBlocksTests` (headless): paragraph inlines, a fenced block, list
  items, a link, and an unsupported construct degrading to literal text.
- `WorkspaceViewModelTests`: Chat is the default tab; the switch commands; `Chat`
  is built for a PTY dto only; teardown disposes it.
- `WorkspaceViewSmokeTests`: the new names resolve (`ChatTabButton`, `ChatHost`,
  `ChatItems`, `ComposerInput`, `SendButton`); switching tabs flips which
  surface is visible while `TerminalHost` stays in the tree on both.

README: the desktop app has no section yet and this slice adds no CLI surface,
so it is unchanged. `docs/CHANGES.md` gains a "Session chat" entry.

## Risks

- **Transcript size.** The whole file is parsed at open. A multi-megabyte
  transcript costs one pool-thread pass and one batched UI apply into a
  virtualized list; accepted for now, and a tail-only initial load is the
  obvious upgrade if it ever hurts.
- **Agent-owned files.** Every open is `FileShare.ReadWrite | Delete`; a
  regression is invisible locally and reddens only the Windows CI leg. The
  sharing test in `JsonlTailTests` is the local guard.
- **Composer bytes are raw PTY input.** The agent's TUI decides what they mean;
  bracketed paste assumes the TUI enabled it (both do). Text landing during a
  redraw may echo oddly in the Terminal tab; the transcript is unaffected.
- **Rendering granularity is the transcript's.** Both vendors write complete
  blocks, not tokens, so assistant text appears per block, a few hundred
  milliseconds after the vendor writes it. That is what "rendered from the
  transcript" means; token streaming is AI-2197's frames.
- **Markdown subset.** Unknown constructs degrade to literal text. Tables and
  images are the likeliest gaps and are deferred knowingly.
- **A never-resolving path** (older daemon, or the poll gave up) leaves Chat in
  `Waiting` with the Terminal tab one click away; the note says what it is
  waiting for.
