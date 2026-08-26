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
  `SnapshotAgentsForStatus`. `null` means any of: an older daemon; not resolved
  yet; the 3-minute poll gave up; a runtime with nothing to read (app-server
  Codex, every ACP vendor); a local passthrough launch that resumes an existing
  Codex session (below); or an agent that exited before discovery found its
  file or before the status push carrying the path was delivered — a 250 ms
  debounce sits between a pulse and its snapshot, and cleanup and removal can
  land inside it. The app never distinguishes these; it waits.

Daemon side (`src/Capacitor.Cli.Daemon`):

- `AgentInstance.CodexRolloutPath` becomes `TranscriptPath`, populated for both
  PTY vendors; the Codex turn diagnostic reads the renamed property unchanged.
- `SessionTranscriptLocator` (`Harness/Claude/`) gains
  `TryLocateWinner(projectDir, worktreePath, spawnedAtUtc, ruledOut)` returning
  `(SessionId, Path)?`, mirroring `CodexSessionRolloutLocator.TryLocateWinner`;
  `TryLocate` delegates to it. **The returned path is link-resolved**: the
  per-worktree project dir is a symlink onto the source repo's dir that
  `ClaudeLauncher.Cleanup` deletes when the agent exits, so a path through the
  link would die with the process while the file lives on. The winner is
  `Path.Combine(<link target of projectDir, final>, <matched file name>)`; a
  project dir that is a real directory (a borrowed checkout, or no symlink
  because the source project dir did not exist at launch) resolves to itself.
- **Every PTY launch runs the locator**, not only server-driven ones:
  `HandleLocalSpawnAsync` (a `kcap agent start`, `--private` included) captures
  the spawn time before `Spawn` and starts the same detection. A `--private`
  agent skips the server reports as today; the path is local state.
- The poll is extracted into an internal `TranscriptDiscovery` unit over a
  `TimeProvider` (interval, deadline, cancellation, the vendor's locate
  function, and the two callbacks it drives) so it is tested directly. It runs
  **until the path is known**, not until a session id exists; on a winner it
  sets `SessionId` and `TranscriptPath` on the agent and pulses
  `_statusNotifier` **before** either awaited server report — mutation first,
  pulse second, the notifier's own contract, and the pulse must not wait behind
  a SignalR call that can stall on a reconnect. Cancellation (the agent's read
  loop ending) ends the poll without a final locate — an agent that exits
  between two scans keeps a null path, accepted rather than coordinated
  against the exit path. The path resolves within a few seconds of launch for
  both vendors (Claude writes its first record, with `cwd`, before its first
  prompt renders).
- **A resumed Codex session is outside discovery.** `kcap agent start codex --
  resume …` passes through, and Codex then appends to the rollout it resumes
  — a file stamped before this spawn. The Codex locator accepts only rollouts
  stamped at or after spawn; that rule is what keeps a user's older session in
  the same cwd from being claimed, and it is not weakened here. Such a launch
  leaves `transcript_path` null and Chat in `Waiting`. Claude `--resume` is
  discoverable by construction: its locator also accepts a fresh last write.

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
`ReadAppended()` is total — it never throws — and returns
`TailRead(IReadOnlyList<string> Lines, TailStatus Status, string? Failure)`
with `TailStatus` ∈ `Ok | Reset | Missing | Failed`:

- Opens `new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)`
  per call and closes it after. The trap named on the issue: `File.ReadAllText`,
  `File.ReadLines` and friends open `FileShare.Read`, which on Windows denies the
  agent the write handle to its own transcript — worst during its shutdown
  drain. Invisible on macOS/Linux; only the Windows CI leg catches a violation.
- `FileNotFoundException` / `DirectoryNotFoundException` → `Missing`, no
  lines, cursor unchanged. Any other exception (a sharing violation, an
  `UnauthorizedAccessException`, any `IOException`) → `Failed` with the
  message, no lines, cursor unchanged. Both are transient by contract: the
  next call retries from the same cursor, so a file that appears or becomes
  readable later is picked up without any consumer action.
- `Length < cursor` is a length regression — a truncation, or a replacement by
  a shorter file: the cursor resets to zero and the read reports `Reset` so
  the consumer clears what it rendered. **A replacement by a file of equal or
  greater length is not detected** and would be read from the old cursor; the
  tail promises no more than length regression. Both vendors append only, so
  this is a stated limitation rather than a handled case.
- Reads `[cursor, Length)`, splits on `\n` (a trailing `\r` is stripped), skips
  blank lines, and **holds back an unterminated final chunk**: the cursor
  advances only past the last `\n`, so the chunk is re-read whole once its
  newline lands. This is `WatchCommand`'s `Hold` policy, as a pure function
  (`SplitCompleteLines(ReadOnlySpan<byte>, out int consumed)`) the tail wraps.
  UTF-8 decoding is per complete line — a multibyte sequence cannot straddle a
  newline, so no decoder state carries over.

### Projection

One public seam:

```csharp
public interface ITranscriptProjection {
    IReadOnlyList<AcpEventEnvelope> Project(string line);   // stateless; empty for anything unparseable or uninteresting
}
public static class TranscriptProjection {
    public static ITranscriptProjection? For(string vendor); // ordinal-ignore-case; null for an unknown vendor
}
```

`ClaudeTranscriptEvents` (`Harness/Claude/`) and `CodexRolloutEvents`
(`Harness/Codex/`) implement it as singletons; `TranscriptProjection.For` is
the one registration site, so adding a vendor's transcript means a new
`Harness/<Vendor>/` file and one line there. Every envelope carries
`TimestampIso` when the record has a timestamp; `Seq` stays 0 — arrival order
is the order. Every JSON read goes through `JsonElementExtensions`, so a
wrong-typed field reads as absent rather than throwing; a line that is not a
JSON object projects to nothing. The extension gains one internal accessor,
`Prop(name) → JsonElement?` — the property of any kind, null when absent — for
the one place the typed accessors cannot serve: copying a non-object `input`
verbatim into its wrapper. No public surface changes.

Output invariants shared by both mappers:

- *Joining*: when several text blocks feed one envelope they are joined with
  `"\n"`.
- *Capping*: `ToolResult` longer than 4096 UTF-16 code units is cut so that
  the result **including** its trailing `…` marker is exactly 4096 long, and
  the cut never lands between the halves of a surrogate pair (the high
  surrogate goes too). Nothing else is capped.
- *`ToolInputJson` is always a JSON object string*, as `AcpEventEnvelope`
  documents. Anything that has to be built rather than copied is written with
  `Utf8JsonWriter` — never reflection-based serialization, because Core is
  `IsAotCompatible` and ships inside two AOT binaries.

`ClaudeTranscriptEvents`, keyed on the record's root `type`:

- `user`, not `isMeta`, not `isSidechain`: a string `message.content` is one
  `user_message`; an array yields one `user_message` per `text` block and one
  `tool_result` per `tool_result` block (`ToolCallId` = `tool_use_id`,
  `ToolIsError` = `is_error`, `ToolResult` = the block's `content` when it is
  a string, else its `text` blocks joined). Before emitting user text,
  `<system-reminder>…</system-reminder>` blocks and the local-command wrappers
  (`<command-name>`, `<command-message>`, `<command-args>`,
  `<local-command-stdout>`, `<local-command-caveat>`) are stripped; text that
  is blank afterwards is not emitted.
- `assistant`, not `isSidechain`: per content block — `text` → `assistant_text`;
  `thinking` → `assistant_thinking` (`ThinkingEncrypted` when the block carries
  no text); `tool_use` → `tool_call` (`ToolCallId` = `id`, `ToolName` = `name`,
  `ToolInputJson` = the `input` object's raw text; an `input` that is not an
  object becomes `{"input": <raw value>}`). `Model` = `message.model`.
- Everything else is skipped: `attachment`, `summary`, `system`,
  `file-history-snapshot`, `file-history-delta`, `mode`, `permission-mode`,
  `last-prompt`, `ai-title`, `atis-latch`, `worktree-state`,
  `queue-operation`, `progress`, and any type this build has never heard of.

`CodexRolloutEvents`, keyed on `type == "response_item"` and then
`payload.type`; every other envelope type (`event_msg`, `turn_context`,
`session_meta`, `world_state`, `compacted`,
`inter_agent_communication_metadata`) is skipped:

- `message` with role `user`: the `input_text` blocks joined → `user_message`,
  unless the text opens with an injected prelude (`<environment_context>`,
  `# AGENTS.md instructions`, `<turn_aborted>`, `<user_instructions>`,
  `<permissions instructions>`); role `assistant`: the `output_text` blocks →
  `assistant_text`; roles `developer` and `system` are skipped.
- `function_call` → `tool_call` (`ToolCallId` = `call_id`, `ToolName` = `name`,
  `ToolInputJson` = `arguments` when it parses as a JSON object, else
  `{"arguments": <the string>}`); `custom_tool_call` → `tool_call`
  (`ToolInputJson` = `{"input": <the raw input string>}`);
  `function_call_output` and `custom_tool_call_output` → `tool_result`
  (`call_id`; the `output` string, or its text blocks joined).
- `reasoning` → `assistant_thinking` (summary texts joined;
  `ThinkingEncrypted` when only `encrypted_content` is present).
- `agent_message` (inter-agent traffic) is skipped.

## 3. App: the Chat tab

### `ChatTabViewModel`

Constructed by `WorkspaceViewModel` for a PTY session only — the same gate as
the Terminal tab (`HostedHarnessCatalog.ShowsTerminal`) — once the first dto
resolves, because the projection is chosen by the dto's vendor. Ctor-scoped
like its siblings; `TeardownAsync` is its one exit and disposes every
subscription below. Inputs: agent id, `IDaemonClientService`, the sibling
`TerminalTabViewModel`, the `ITranscriptProjection?` from
`TranscriptProjection.For(vendor)` (null → the `Unavailable` phase), an
`IUrlOpener`, and a `TimeProvider`.

Phases (`ChatTabPhase`): `Waiting` (no `transcript_path` yet — muted "Waiting
for the transcript…"), `Reading`, `Missing` (the path exists on the wire but
not on disk — "The transcript file is missing"), `Unavailable` (a PTY vendor
with no projection — "No chat view for this harness"). A `Failed` tail read
keeps the current phase and items and logs its reason once per distinct
message to `Console.Error` (the app's diagnostic convention); the next tick
retries. A session that ends keeps its items and keeps polling until teardown:
the file outlives the process and may still receive its final records.

Every daemon-fed input goes through `ObserveOn(RxSchedulers.MainThreadScheduler)`
before touching bound state — the client's pump is a background thread. Two
subscriptions: `daemon.Agents.Connect()` filtered to the agent id (the path
watch, and the footer's model/status), and `daemon.Snapshots` (the advertised
vendor options behind the composer hint's vendor label). The first non-null
`TranscriptPath` starts the tail. **Path identity is part of the read
generation**: every distinct path, on the UI thread and in one step, bumps the
generation, clears the items and the pairing index, and installs a fresh
`JsonlTail`; a read or projection still in flight for the old path completes
under a stale generation and is discarded. A fresh tail starts at cursor zero
with `Ok`, which is why the reset belongs to the switch, not to the tail.

Poll: a `TimeProvider` timer every 500 ms (a tuning constant, not a contract).
A tick with a read still in flight is skipped. The read and the projection run
on the thread pool and never throw past the tick (the tail is total; a
projection fault is caught, logged once, and drops that line); the apply hops
to the UI thread through `Dispatcher.UIThread.InvokeAsync` and mutates nothing
unless the generation it captured is still current — `TeardownAsync` and every
path switch bump it.

Items — an `AvaloniaList<ChatItemViewModel>` exposed read-only and mutated on
the UI thread. A read's items are applied with one `AddRange` (one collection
notification per read, however many records the first read of a long
transcript yields), and `Reset` is one `Clear`. Three shapes:

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

The list is an `ItemsControl` whose control template is a `ScrollViewer`
around the `ItemsPresenter`, with a `VirtualizingStackPanel` items panel —
the shape Avalonia virtualizes; an `ItemsControl` dropped inside an external
`ScrollViewer` is measured at infinite height and realizes every item. Item
templates are per item type (`DataTemplates` keyed on the three shapes).

Follow-tail is a view concern: the `ScrollViewer` scrolls to the end on an add
only when it was already at the end, so a user reading history is not yanked.

### Markdown

`MarkdownView` — a `ContentControl` with a styled `Text` property and an
`OpenLink` command property — rebuilds its `Content` (a vertical panel of
block controls) from a Markdig AST (default CommonMark pipeline plus
auto-links) on every `Text` change. `MarkdownBlocks` maps: paragraphs and
headings → `SelectableTextBlock` with inlines (`Bold`, `Italic`, monospace
`Run` for code spans, `LineBreak`); fenced and indented code → a `Border`
around a monospace `SelectableTextBlock`; bullet and ordered lists → marker +
nested content rows; block quotes → a left rule beside the content; thematic
breaks → a hairline. A link is an `InlineUIContainer` hosting a link-styled
`HyperlinkButton` (accent, underlined, hand cursor) with **`NavigateUri` left
unset** — set, the control opens the URI itself and would bypass the policy
below — and `Command` bound to `OpenLink` with the URL as its parameter; a
button brings keyboard activation (Enter/Space), focus, and automation
semantics, which a bare inline `Run` has none of. HTML, tables, images and
any other node render their literal source text — degraded, never dropped.
User bubbles do not go through it: what the user typed is shown as typed.

Links are agent-authored and untrusted, and `ShellUrlOpener` hands any string
to the OS shell. The trust boundary is the item's `OpenLinkCommand` on
`AssistantTextItem`, backed by a pure `LinkPolicy.IsOpenable(url)`: only an
absolute `http` or `https` URI opens; anything else (`file:`, `javascript:`,
custom schemes, relative or malformed text) renders as plain text with no
link affordance at all. An opener exception is caught and logged to
`Console.Error` — a bad link never escapes a UI event.

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
for every other attacher.

Focus follows the tab, and the view enforces it rather than assuming it. The
**inactive surface's root** — the whole terminal grid (control, banners,
Detach/Reattach buttons) or the whole chat surface (list, composer, Send) — is
disabled (`IsEnabled=false`), non-hit-testable and transparent, so nothing in
it can hold focus, be tabbed into, or be activated; a disabled control is
still measured and arranged, which is what keeps the terminal's pane size
real. The existing focus-on-Model-assignment handler runs only while the
Terminal tab is active (a reattach while Chat is up must not steal the
composer's focus); switching to Terminal focuses the terminal, switching to
Chat — and the first open on Chat — focuses `ComposerInput`.

### Composer

Lives on `ChatTabViewModel`, sends through the sibling Terminal tab:

- `ComposerText`; `SendCommand` (`ReactiveCommand.Create`, synchronous),
  executable iff the terminal is `Attached`, not read-only, and the text is not
  blank. It calls `Terminal.TrySendText(text)` and clears the text iff that
  returns true — one synchronous step on the UI thread, no await, so
  acceptance and the clear cannot be separated by a state change. The send is
  **accepted**, not acknowledged: the attach seam's `SendInputAsync` returns
  nothing and Core's client no-ops when it is no longer writable and folds a
  transport failure into the attach outcome — so no send can learn whether
  its bytes landed. A refused send leaves the text in place and the hint
  already says why. A transport loss surfaces the way it always does — the
  attach outcome flips `Terminal.State`, and the hint follows.
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
  reading or switching the mode is AI-2197's, and the transcript does not
  carry the live prompt state anyway.

`TerminalTabViewModel.TrySendText(string text)` → `bool`, synchronous, UI
thread. It refuses (false, nothing written) unless `State` is `Attached`
read-write and a client is live; otherwise it captures the client and the
current **send epoch** and starts a fault-contained background delivery, then
returns true. The send epoch is a counter the VM bumps **synchronously, before
any await, at the start of** `TryStartAttemptAsync` (reattach), `RunDetachAsync`
and `TeardownAsync` — the attempt generation cannot serve, because reattach
bumps it only after awaiting the old client's disposal, and detach never bumps
it at all; a delayed CR must be dead the instant a detach or reattach begins,
not when it finishes.

The delivery is the daemon's own `PtyHostedAgentRuntime.SendUserInputAsync`
shape: one write of `TerminalInputEncoder.Paste(text)` — `\r\n` normalized to
`\n`, one trailing newline dropped, wrapped in bracketed paste (`ESC[200~` …
`ESC[201~`) so the TUI takes it as one block — then, after a 150 ms
`TimeProvider` delay, one write of `\r` **to the same captured client, only if
the send epoch is unchanged**. A stale epoch drops the CR: the paste already
went to the old client, and a replacement must never receive a stray Enter.
A fault in either write is observed and logged once to `Console.Error`; it
never touches VM state (the attach outcome is the transport's own reporting
channel). The delay is measured against Codex's TUI, which suppresses
Enter-as-submit for 120 ms after ingesting a paste and turns a CR inside that
window into a newline. A single CR, never a retry spray: an interactive launch
keeps its permission prompts (Claude prompts unless it is an owned review-flow
launch; Codex follows its posture), and the transcript cannot show a prompt
that is up on the PTY, so every extra Enter is a chance to answer one. Every
message takes this one path, single-line or not. Enter sends and Shift+Enter
inserts a newline — a view-level key handler on the composer's `TextBox`.

## 4. Testing

`test/Capacitor.Cli.Core.Tests.Unit`:
- `JsonlTailTests`: complete lines delivered once; an unterminated final line
  held, then delivered whole after its newline; CRLF; blank lines skipped;
  length regression → `Reset` and a re-read from zero; missing → `Missing`,
  then the same tail reads the file once it appears; a transient failure →
  `Failed` with the cursor intact, then the next read succeeds; a file held
  open for writing by another handle (sharing read/write/delete, opened first)
  is still readable — the Windows sharing pin.
- `ClaudeTranscriptEventsTests` / `CodexRolloutEventsTests`: one fixture line
  per shape in §2 — including a string `tool_result.content` and a block-array
  one, a non-object `input`, non-object `arguments`, the skip lists, wrapper
  stripping, prelude skipping, the joining separator, the cap (final
  `string.Length` exactly 4096 with the marker, and an astral character at the
  boundary kept whole or dropped whole), wrong-typed fields reading as absent,
  and a malformed line → empty. Every `ToolInputJson` in every fixture parses
  as a JSON object; the wrapper fixtures cover an array, a scalar and a null
  `input`. `TranscriptProjection.For`: case-insensitive, unknown → null.
- `JsonElementExtensionsTests`: `Prop` returns an array, a scalar and a null
  property as elements, and null for an absent one or a non-object receiver.
- `StatusIpcJsonTests`: `transcript_path` round trip, trailing order, old JSON
  → null.
- AOT: the Release publish of the CLI stays free of `IL2026`/`IL3050`
  warnings (`dotnet publish -c Release … | grep -E 'IL[23][01][0-9]{2}'`), run
  as part of the slice's acceptance, not left to CI.

`test/Capacitor.Cli.Daemon.Tests.Unit`:
- `SessionTranscriptLocatorTests`: the winner carries the matched file's
  path; a winner located through a project-dir symlink reports the
  link-resolved path, and after the symlink is deleted (what `Cleanup` does)
  that path still reads, final append included.
- `TranscriptDiscoveryTests` (over `FakeTimeProvider`): a winner sets both id
  and path and pulses before the reports; a pre-populated session id with no
  path keeps polling until the path lands; the deadline ends the poll with no
  mutation; cancellation (agent exit) ends it cleanly.
- `AgentOrchestrator`: `HandleLocalSpawnAsync` starts discovery for a local
  (and a `--private`) PTY launch — over a fake PTY factory and launcher, no
  real process. A `codex -- resume <id>` passthrough launch against a sessions
  tree holding only the resumed, older-stamped rollout leaves `transcript_path`
  null — the boundary pinned, so weakening it is a deliberate change.
- `AgentStatusSnapshotTests`: `transcript_path` null before detection, the
  value after — on the serialized payload.

`test/Capacitor.App.Tests.Unit` (over `FakeDaemonClientService`,
`FakeTerminalAttachClient`, `FakeTimeProvider`, a `TempDir` transcript):
- `ChatTabViewModelTests`: `Waiting` until a path; the path starts reading and
  the initial load renders items in file order; lines appended after a tick
  render; a held partial line does not render until complete; tool outcome
  pairing (Done, Error); `Reset` on length regression; `Missing` then
  recovery; a `Failed` read keeps items and phase; a path switch with a read
  of the old file deliberately blocked mid-flight — switch, release, and only
  the new file's items remain, with an empty pairing index; ticks stop after
  teardown; a projection-less vendor → `Unavailable`; a removed agent keeps
  its items; a dto or snapshot pushed from a pool thread lands on bound state
  without a thread-affinity fault; both subscriptions end at teardown.
- Batching: a 5,000-record initial transcript raises exactly one collection
  notification, and — in a headless window at a fixed size — the
  `ItemsControl` realizes a bounded number of containers.
- Composer: `SendCommand` enablement across every terminal phase and the
  read-only case; the exact two writes via `FakeTerminalAttachClient.SentInput`
  — the bracketed paste, then `\r` only once the `FakeTimeProvider` advances
  past the delay, on the same client; a detach or reattach that has *begun*
  before the send refuses it (text stays, nothing written); a reattach whose
  old-client disposal is held open past 150 ms (`DisposeGate`), a detach, or a
  teardown started during the delay sends no CR to any client; a faulting
  write leaves VM state untouched; the text clears on acceptance and stays on
  refusal; every hint string; a pool-thread state flip updates the hint on
  the UI thread.
- `TerminalInputEncoderTests`, `ToolDetailTests` (key priority, first line,
  80-character cut), `LinkPolicyTests` (https/http open, `file:`,
  `javascript:`, custom schemes, relative and malformed text refused).
- `MarkdownBlocksTests` (headless): paragraph inlines, a fenced block, list
  items, a link that executes `OpenLink` exactly once from a pointer click and
  once from keyboard activation, a disallowed link rendered as plain text that
  is not focusable, and an unsupported construct degrading to literal text.
- `WorkspaceViewModelTests`: Chat is the default tab; the switch commands; `Chat`
  is built for a PTY dto only; teardown disposes it.
- `WorkspaceViewSmokeTests`: the new names resolve (`ChatTabButton`, `ChatHost`,
  `ChatItems`, `ComposerInput`, `SendButton`); switching tabs flips which
  surface is visible while `TerminalHost` stays in the tree on both; a
  workspace opened on Chat with a **real** `XtermTerminalSurface` reports the
  laid-out pane size to the attach client, not 80×24, while the terminal
  surface is disabled and non-hit-testable; focus lands on `ComposerInput` on
  open, stays there through a late Model assignment, moves to the terminal on
  the Terminal switch and back on the Chat switch; tab traversal from the
  composer never reaches `DetachButton`/`ReattachButton` while Chat is active
  (terminal driven to Detached/Failed first), and from the terminal never
  reaches `ComposerInput`/`SendButton` while Terminal is active.

README: the desktop app has no section yet and this slice adds no CLI surface,
so it is unchanged. `docs/CHANGES.md` gains a "Session chat" entry.

## Risks

- **Transcript size.** The whole file is parsed at open. A multi-megabyte
  transcript costs one pool-thread pass and one `AddRange` into a virtualizing
  `ItemsControl`; accepted for now, and a tail-only initial load is the obvious
  upgrade if it ever hurts.
- **Agent-owned files.** Every open is `FileShare.ReadWrite | Delete`; a
  regression is invisible locally and reddens only the Windows CI leg. The
  sharing test in `JsonlTailTests` is the local guard.
- **Replacement is not detected.** The tail promises length regression only;
  a same-or-longer replacement reads from the old cursor. Both vendors append
  in place, so this is a documented limitation, not a handled case.
- **A send can answer a prompt.** Composer bytes are raw PTY input, and an
  interactive launch keeps its permission prompts, which the transcript never
  shows. The single delayed CR limits the exposure to one keypress, and the
  Terminal tab is where a live prompt is visible; only AI-2197's frames can
  close the gap. Bracketed paste assumes the TUI enabled it (both do), and
  the 150 ms paste-to-CR gap is calibrated to today's Codex suppression window
  — a TUI that widens it would turn a send into a newline, which the Terminal
  tab makes visible.
- **A borrowed-cwd local launch can mislink.** The locator disambiguates by
  cwd and spawn time; a user's own session started in the same checkout within
  the skew tolerance could win. Display-only, and the same exposure the
  daemon's session-id link already carries.
- **A short-lived session may never get its path.** Discovery scans every
  2 s and stops at agent exit without a final locate; and a path that lands
  at exit still has to cross the status debounce before cleanup and removal.
  Such a session's Chat stays in `Waiting` — its Terminal tab still has the
  scrollback. Coordinating discovery with the exit path was judged not worth
  its coupling for a session that ran for seconds.
- **Codex resume is not discoverable.** A local passthrough `resume` appends
  to an older-stamped rollout the locator will never accept; Chat stays in
  `Waiting`. Supporting it means reading the resumed id out of the arguments,
  which this slice leaves alone.
- **Rendering granularity is the transcript's.** Both vendors write complete
  blocks, not tokens, so assistant text appears per block, a few hundred
  milliseconds after the vendor writes it. That is what "rendered from the
  transcript" means; token streaming is AI-2197's frames.
- **Markdown subset.** Unknown constructs degrade to literal text. Tables and
  images are the likeliest gaps and are deferred knowingly.
- **A never-resolving path** (older daemon, or the poll gave up) leaves Chat in
  `Waiting` with the Terminal tab one click away; the note says what it is
  waiting for.
