# Compact tool calls in the Chat tab (AI-2418)

Slice of the desktop shell (parent AI-2171), on the AI-2196 chat and the AI-2308
permission lane. The Chat tab renders one row per tool call, forever: a session
that reads twenty files before answering shows twenty muted rows between two
paragraphs of prose, and the prose is what the reader came for.

What already exists: `ChatTabViewModel.Apply` turns each `ToolCall` envelope into
a `ToolCallItem` (name, one-line detail, `Outcome` of Running/Done/Error) and
flips it in place when the matching `ToolResult` arrives; the row's trailing
glyph slot shows nothing while running, an accent `✓` on success and a danger
`✕` on error. Permission requests never touch the transcript list: they arrive
from the daemon as `PermissionPendingDto` over `permission/1` and render as
cards on the NEEDS YOU row. That DTO carries no tool-use id, so the app cannot
tell which row a pending card belongs to, although the daemon builds it from the
raw hook body and Claude's `PermissionRequest` hook payload includes
`tool_use_id`. Codex's does not: its `PermissionRequest` payload is a closed set
of fields with no call id (its own tests assert the absence), while `PreToolUse`
and `PostToolUse` carry `tool_use_id` equal to the rollout's `call_id`. The
Codex desktop app correlates through the app-server protocol's item id, which
a PTY session never exposes. Codex's hook also renames the tool: a shell
approval arrives as `tool_name: "Bash"` with `{command, description}` while the
rollout names the call `shell` or `exec_command`.

This slice is the app's item model and rendering plus one optional field on the
local permission wire, read by the daemon from the hook body. The app works
against a daemon that does not send the field, and for a vendor whose hook
never sends it, by falling back to the one call that can be asking.

Out of scope: a per-category icon on rows (the reference screenshots' book and
terminal glyphs; ours keep the `›`); a "Running …" verb prefix on live rows;
persisting a group's expansion across restarts or transcript resets; showing
tool output on expansion; structured ACP vendors (no transcript projection, so
no chat rows at all); the server's own transcript rendering.

## Decisions

Settled with the owner during brainstorming, 2026-09-02:

1. **A group item in the flat list, not per-row hiding.** Consecutive tool calls
   become one `ToolGroupItem` holding its calls. The alternative (one item per
   call, hidden when folded, plus an inserted summary row) spreads group state
   across N items and leaves zero-height rows in the virtualizing panel.
2. **Uniform fold.** A group whose only completed call is one call still folds to
   a summary ("Ran a command"). One rule, one vertical rhythm; the detail is one
   click away. Keeping a lone row visible is a one-line change if it proves
   better in use.
3. **Live rows stay visible beneath the summary.** A call still running, or
   waiting on a permission, is never folded. The summary line exists only once
   the first call in the group settles; before that the group renders exactly as
   the rows do today.
4. **Correlate permissions by tool-use id when the hook gives one; otherwise by
   the sole running call.** Matching on tool name plus input fails on omitted
   large inputs, on repeated identical calls, and on Codex outright (its hook
   renames the tool and reshapes the input). Claude's id is free at the source
   and exact under parallel calls. A request with no id marks the agent's only
   running call, and marks nothing when two or more are running: a wrong marker
   is worse than none, and the NEEDS YOU card stays the affordance.
5. **A failure inside a folded group surfaces on the summary line**, as the same
   danger `✕` a row would carry. Folding never hides an error.
6. **Codex-style wording, no counts.** "Read files, ran a command, edited a
   file": singular takes an article, plural drops it, categories in order of
   first appearance.
7. **The waiting marker is an accent `?` in the outcome slot.** Same place the
   check lands, same converter, one character to change.
8. **A Codex shell command classifies through Core's port of the server's
   `CodexCommandClassifier`.** Codex reads and searches through the shell
   (`cat`, `sed -n`, `rg`), so without classification every Codex group reads
   "Ran commands". The server already carries a faithful port of Codex's own
   `parse_command` (quote-aware tokenizer, redirection detection, the
   any-unknown-collapses rule) to label the dashboard; the desktop app needs the
   same verdicts, and Core is where transcript normalization is heading. The
   classifier and its tests move into Core verbatim under `Harness/Codex/`; the
   server drops its copy when it takes the submodule bump. A category is fixed
   when the row is created, from the call's name and input; the summary counts
   categories.

## 1. Item model

`src/Capacitor.App/ViewModels/ChatItems.cs`.

### `ToolCallItem`

Gains a category, one state and one derived flag:

- `ToolCategory Category` — fixed at construction by `ToolSummary.Categorize`
  (§2) from the call's name and input JSON; the group sums these.
- `bool IsAwaitingPermission` — set from the permission cache (§4), independent
  of `Outcome`, since the two arrive from different sources in either order.
- `bool IsSettled => Outcome != ToolOutcome.Running`.
- `OutcomeGlyph` becomes: Done `✓`, Error `✕`, else awaiting `?`, else `""`. The
  existing `ToolOutcomeBrushConverter` keyed on `IsError` already paints `?`
  with the accent brush, so no view change beyond the glyph text.

Setting `Outcome` raises `OutcomeGlyph`, `IsError` and `IsSettled`; setting
`IsAwaitingPermission` raises `OutcomeGlyph`. `Outcome` only ever moves away from
Running; a result is terminal.

### `ToolGroupItem`

One row of the list holding a run of calls:

```csharp
public sealed class ToolGroupItem : ChatItemViewModel {
    public IAvaloniaReadOnlyList<ToolCallItem> Calls { get; }      // every call, transcript order
    public IAvaloniaReadOnlyList<ToolCallItem> LiveCalls { get; }  // Outcome == Running, same order
    public IAvaloniaReadOnlyList<ToolCallItem> VisibleCalls { get; } // IsExpanded ? Calls : LiveCalls
    public bool IsExpanded { get; }                                 // default false
    public ReactiveCommand<Unit, Unit> ToggleCommand { get; }
    public string Summary { get; }                                  // §2, over settled calls
    public bool HasSummary { get; }                                 // any call settled
    public bool HasFailure { get; }                                 // any settled call errored
    public void Add(ToolCallItem call);
}
```

`Add` appends to both lists and subscribes to the call's `PropertyChanged` for
`Outcome` (a plain event subscription, not `WhenAnyValue`: the rail's comment on
ReactiveUI's global init under the headless dispatcher applies here too). When
a call settles the group removes it from `LiveCalls`, recomputes `Summary` and
`HasFailure` over `Calls`, and raises the three plus `HasSummary`. Recompute is
O(n) per settlement; a group is tens of calls, hundreds at the worst, and
settlements arrive one read at a time. `VisibleCalls` returns one of the two
lists by `IsExpanded` and is raised on toggle; the view binds that one property
(§3), so a toggle swaps the rendered source rather than hiding a second list.

### Grouping in `Apply`

`ChatTabViewModel` keeps `ToolGroupItem? _openGroup`, the trailing group that
new calls join. In the envelope loop:

- `ToolCall`: if `_openGroup` is null, create one and add it to `fresh`; then
  `_openGroup.Add(item)`. The group persists across reads until something closes
  it, so calls arriving one poll apart still share a group.
- `UserMessage`, `AssistantText`, `SystemNote`: set `_openGroup = null` before
  adding the item. `AssistantThinking` produces no item and does not close the
  group.
- `ToolResult`: unchanged; the call flips, the group reacts.
- `SwitchPath` and a `Reset` read clear `_openGroup` with the items.

`_pendingTools` keeps mapping tool-call id to `ToolCallItem` for result pairing;
nothing else about the read pipeline changes.

## 2. Summary text

`src/Capacitor.App/ViewModels/ToolSummary.cs`, beside `ToolDetail`: a
`ToolCategory` enum, `Categorize(string name, string? inputJson)` run once per
call when `Apply` creates the row, and `Describe(IEnumerable<ToolCategory>)`
over the settled calls' categories.

### Categories

Each name maps to a category, case-insensitively; a name in no row is `Other`.
The table keys on the name the transcript carries, not the one the vendor's
hook reports (Codex's hook says `Bash` for a call its rollout names `shell`).
The Codex names are the handlers in `codex-rs/core/src/tools/handlers/`; the
Claude names are Claude Code's built-in tools. The map is an internal static so
the test can enumerate it rather than sample it:

| Category   | Names                                                                          | One                 | Many               |
|------------|--------------------------------------------------------------------------------|---------------------|--------------------|
| Read       | `Read`, `NotebookRead`, `read_file`, `view_image`                              | Read a file         | Read files         |
| Edit       | `Edit`, `MultiEdit`, `Write`, `NotebookEdit`, `apply_patch`, `write_file`      | Edited a file       | Edited files       |
| Command    | `Bash`, `BashOutput`, `KillShell`, `shell`, `shell_command`, `exec`, `exec_command`, `write_stdin`, `local_shell`, `container.exec` | Ran a command | Ran commands |
| Search     | `Grep`, `Glob`, `LS`                                                           | Searched files      | Searched files     |
| Web search | `WebSearch`, `web_search`                                                      | Searched the web    | Searched the web   |
| Fetch      | `WebFetch`                                                                     | Fetched a page      | Fetched pages      |
| Skill      | `Skill`                                                                        | Loaded a skill      | Loaded skills      |
| Agent      | `Task`, `Agent`, `TaskOutput`, `TaskStop`, `spawn_agent`, `wait_agent`, `send_input`, `send_message`, `resume_agent`, `interrupt_agent`, `close_agent`, `list_agents` | Ran an agent | Ran agents |
| Plan       | `TodoWrite`, `update_plan`                                                     | Updated the plan    | Updated the plan   |
| Question   | `AskUserQuestion`, `request_user_input`                                        | Asked a question    | Asked questions    |
| Other      | everything else, MCP tools (`mcp__…`) included                                 | Called a tool       | Called tools       |

Two refinements read the input, both best-effort and both falling back to the
table's answer:

- **A skill read.** A Read-category call whose `file_path` ends in `/SKILL.md`,
  or a shell read whose classified `Name` is `SKILL.md`, is `Skill`. Claude
  subagents and Codex both load a skill by reading that file.
- **A shell command's verdict.** A Command-category call's command text
  (`cmd`, or `command`; an array `command` joins with spaces) goes through
  `CodexCommandClassifier.Classify`: a `read` hint is `Read`, a `search` or
  `list_files` hint is `Search`, and null (any unknown segment, a redirection,
  a helper-only pipeline) stays `Command`. The classifier moves from the
  server into Core, `src/Capacitor.Cli.Core/Harness/Codex/`, with its tests,
  as a verbatim port: the server switches to Core's copy and deletes its own
  when it takes the submodule bump, so the two must not drift in the meantime.

Phrases join with `", "` in order of the category's first appearance; the first
keeps its capital, every later one lower-cases its first character:
`Read files, ran a command, edited a file`. An empty input yields `""`.

The table is the one place vendor tool names are known to the app; adding a
vendor's names is a row edit, and an unknown name degrades to "Called a tool"
rather than to nothing. A vendor-neutral tool kind on the canonical envelope
would replace the table's name rows; that is a separate issue, and the summary
would then sum kinds instead.

## 3. Rendering

`src/Capacitor.App/Views/ChatTabView.axaml`, a new type-keyed template beside
the four existing ones. The `ToolCallItem` template is unchanged and stays in
the outer list's `DataTemplates`; the nested lists below resolve it by walking
the logical tree.

```xml
<DataTemplate x:DataType="vm:ToolGroupItem">
    <StackPanel>
        <Button Classes="toolSummary" IsVisible="{Binding HasSummary}" Command="{Binding ToggleCommand}"
                Background="Transparent" BorderBrush="Transparent" Padding="0" Margin="0,0,0,6" Cursor="Hand">
            <StackPanel Orientation="Horizontal" Spacing="9">
                <Panel Width="12" Height="12" VerticalAlignment="Center">
                    <!-- the rail's stroked chevrons: down when expanded, right when collapsed -->
                </Panel>
                <TextBlock Text="{Binding Summary}" FontSize="11.5" Foreground="{StaticResource KcapMutedBrush}" VerticalAlignment="Center" />
                <TextBlock Text="✕" FontSize="11" Foreground="{StaticResource KcapDangerBrush}"
                           IsVisible="{Binding HasFailure}" VerticalAlignment="Center" />
            </StackPanel>
        </Button>
        <ItemsControl ItemsSource="{Binding VisibleCalls}" />
    </StackPanel>
</DataTemplate>
```

One inner list, its source swapped on toggle, rather than two lists toggled
by `IsVisible`: a hidden control keeps its template, presenter and generated
containers, so two lists would leave every row of an expanded group realized
after it is folded again. Swapping the source drops the old containers and
generates the new ones.

The chevron sits in the row prefix's column, so a folded summary and an open
row's `›` align. The summary line keeps the rows' `0,0,0,6` bottom margin and
the group adds none of its own, so the dense-stacking rule and the gap before
prose hold whether a group is folded or open.

Collapsed: the summary line (when any call has settled) and the live rows.
Expanded: the summary line and every call in transcript order, live ones
included, each with its own glyph. A group with nothing settled yet has no
summary line in either state, so a run of calls that is all still running looks
exactly as today.

Virtualization: a group is one realized item of the outer `VirtualizingStackPanel`;
its inner list is a plain stack. Expanding a group realizes every one of its
rows in one layout pass, four `TextBlock`s each, and nothing bounds a group's
size but the transcript itself: a run of a thousand calls between two prose
items is a thousand rows on expansion. That is accepted as the cost of a
click the reader chose, not of loading or following a transcript: a folded
group realizes only its live rows, whether it was never expanded or has been
folded again, and `LiveCalls` is bounded by the vendor's parallelism, not by
transcript length. The smoke test pins both halves at a thousand-call group:
expansion lays out in the headless host with every row realized, and folding
it again leaves only the live rows' containers, with no timing assertion.

Follow-tail: `OnScrollChanged` follows any extent growth while the reader is
at the bottom, so a live row joining an existing group across polls, or a row
folding away, needs no new code, although both mutate only a group's inner
lists (the outer `Items` does not notify) and the existing follow-tail tests
exercise outer additions only; §5 adds both paths. Expansion is the exception:
it also grows the extent, and following it would carry the clicked summary and
the first revealed rows out of view on a tall group. **Expanding keeps the
viewport where it is.** The view arms a one-shot hold when a `toolSummary`
button is clicked (a `Button.ClickEvent` handler on the list, class-filtered),
and the next `ScrollChanged` carrying a non-zero extent delta consumes the hold
instead of scrolling to the end. A collapse consumes it the same way; its
shrink never scrolled anyway. A hold armed by a click that changes no extent
cannot happen: the summary is visible only with a settled call, and a settled
call is hidden while folded, so expansion always adds a row.

## 4. Awaiting permission

### Wire

`PermissionPendingDto` gains `string? ToolUseId`, serialized `tool_use_id` by the
existing snake-case context. Source-gen leaves a missing member null, so an older
daemon's frame decodes unchanged and an older app ignores the extra member.
`PermissionWire` gains `MaxToolUseIdBytes = 128`; the id is opaque, not a GUID,
so it is not canonicalized. `IsPendingStructurallyValid` does not require it.

### Daemon

`LocalPermissionBridge.BuildPending` takes the id read from the hook body's
`tool_use_id`; an id over the cap is dropped to null, not a reason to refuse the
request. Nothing else in the bridge, broker or server leg reads it.

### App

`PendingPermissionRequest.ToolUseId => Dto.ToolUseId`.

`ChatTabViewModel` subscribes a second time to the already agent-filtered,
main-thread `permissions.Pending` change set and keeps
`Dictionary<string, PendingPermissionRequest> _requests`, this agent's pending
requests by request id. The marking is a pure function of two sets, the pending
requests and the running calls in `_pendingTools`, recomputed from scratch by
one `Reconcile()` whenever either set changes; no entry remembers a target. A
request's target is:

- the running call under the request's `ToolUseId`, when the request has one,
  or null while no such row exists; or
- when it has none, the agent's sole running call; two or more running calls,
  or none, give null.

A row's `IsAwaitingPermission` is whether any request targets it, so two
requests on one row keep it marked until both go. `Reconcile()` computes the
new target set, then diffs it against `HashSet<ToolCallItem> _marked`, the
rows it marked last time: a row in `_marked` but not in the new set is cleared,
a row in the new set is marked, and `_marked` becomes the new set. The diff is
what clears a row the running set no longer contains: a settling call leaves
`_pendingTools` before its outcome flips, so a recompute over the running set
alone could never reach it, and its `✓` would mask a flag still true. It runs
after the change set is applied, after `Apply` adds a read's rows (a request
whose row arrived after the card now finds it; an id-less request whose sole
call gained a sibling loses its mark), after a call settles (the settled row is
cleared; an id-less request that had two candidates now has one, the
survivor), and after a reset or path switch has rebuilt the rows, where
`_marked` is emptied with the rows since those objects are gone. `_requests` is
permission state, not transcript state: a rebuild re-derives the marks from
the requests still pending and leaves the requests alone. The recompute is
requests times a dictionary lookup plus the diff, per change; all three sets
are small.

A request that resolves leaves the row Running until the tool result flips it;
a request still pending when the result lands is a vendor oddity the glyph
resolves in favour of the outcome, since Done and Error take precedence over the
waiting marker.

With an id, correlation is an exact string match between the hook's
`tool_use_id` and the transcript's tool-call id; Claude's are the same
`toolu_…` value by construction. Codex requests arrive without one and take the
sole-running-call rule, which is exact whenever Codex runs its calls one at a
time and abstains when it does not.

## 5. Testing

Pure, `test/Capacitor.App.Tests.Unit/ToolSummaryTests.cs`: every name in the
internal map resolves to its row's category (enumerated from the map, not
sampled), the names the Codex and Claude projection tests emit (`spawn_agent`,
`exec`, `shell`, and the Claude fixtures' `tool_use` names) land in a
non-Other row, singular
against plural, first-appearance order, lower-casing after the first phrase,
`mcp__` and unknown names as Other, case-insensitive names, empty input.
Categorize: a Read of `…/SKILL.md` is Skill; a shell `sed -n '1,40p' a.cs` is
Read, `rg foo src` is Search, `ls src` is Search, `cat a && make` is Command,
a `cat SKILL.md` is Skill, an array `command` joins, a Bash call whose input
has only `description` stays Command. The classifier's pure `Classify` tests
come across with it, `test/Capacitor.Cli.Core.Tests.Unit/Harness/Codex/CodexCommandClassifierTests.cs`,
unchanged except for namespace, as the parity pin against the server's copy;
the server file's `EffectiveCodexHint` and `EffectiveCodexPatch` tests exercise
server-only accessors and stay where they are.

View-model, `ChatTabViewModelTests`:

- Consecutive calls across two reads form one group; a user turn, an assistant
  text and a system note each close it; the next call opens a new one.
- A result folds its call: `LiveCalls` shrinks, `Summary` and `HasSummary`
  update, `HasFailure` follows an error; an unmatched result changes nothing.
- A reset and a path switch clear the open group with the items.
- The waiting flag with an id: card before row, row before card, cleared on
  resolve, and an id matching no row marks nothing. Two requests naming one
  row: removing one leaves the mark, removing both clears it. A marked call
  that settles while its request stays pending and no other call qualifies is
  cleared, not merely masked by its outcome glyph.
- The waiting flag without an id: one running call is marked; a second call
  starting clears it; two running calls mark nothing until one settles, then
  the survivor is marked; a row created after the card is marked when it is
  the only running call; the mark clears on resolve.
- A request pending across a reset and across a path switch marks the rebuilt
  row again, by id and by the sole-running rule.
- The existing pairing, initial-load and reset tests move their assertions onto
  the group's `Calls`.

Rendered, `ChatTabViewSmokeTests`:

- A folded group renders one summary line and no rows; toggling renders every
  row beneath it; a live row is visible in both states.
- The summary line carries the danger `✕` when a folded call failed.
- An awaiting row paints `?` with the accent brush; a settled row's glyph still
  takes the outcome brush (the existing test, on an expanded group).
- Rows inside an open group stack densely and keep their distance from prose
  (the existing test, on an expanded group).
- Follow-tail on inner mutations: a live call appended to an existing group on
  a later poll, and a call folding away, each keep the reader at the bottom
  when they were there and leave the offset alone when they were scrolled up.
- Expanding the trailing group while at the bottom leaves the offset where it
  was; the next transcript append follows the tail again.
- A thousand-call group expands: every row is realized and laid out in the
  headless host, and the view stays a single virtualized outer item. Folding
  it again leaves only the live rows' containers realized.

Wire, `PermissionWireContractsTests`: `tool_use_id` round-trips in snake case, a
frame without it decodes to null, and the worst-case frame still fits the codec
cap with it.

Daemon, `LocalPermissionBridgeInteractiveTests`: the pending DTO carries the
hook body's `tool_use_id`; an over-cap id is dropped and the request still
registers.

## 6. Documentation

`docs/CHANGES.md` gains a "Compact tool calls in the Chat tab" section naming the
group rule, the uniform fold, the wire field and the fold-never-hides-an-error
rule. No CLI surface changes, so the README is untouched. The spec rides the
implementation PR and is posted to AI-2418 as a comment.
