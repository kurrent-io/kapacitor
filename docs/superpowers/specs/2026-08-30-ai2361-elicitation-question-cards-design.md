# Elicitation question cards for PTY sessions (AI-2361)

Slice of the desktop shell (parent AI-2171), riding the AI-2308 rails. A PTY
Claude session blocked on an `AskUserQuestion` shows nothing useful in the
desktop app: the request reaches the app — since AI-2308 it is a pending
permission entry — but renders as an Allow / Allow always / Deny card, none of
which answers the question.

What already exists, end to end: Claude fires the `PermissionRequest` hook for
`AskUserQuestion` with the `questions[]` payload as `tool_input`; the hook posts
to the daemon's `/claude/permission-request`; `LocalPermissionBridge` attributes
it, registers it with `PermissionPromptBroker`, and streams it to the app as a
`PermissionPendingDto` over `permission/1`. The server classifies the same
request as an elicitation (tool name plus `questions[]` parse) and the web
answers it via `RespondToPermission(behavior: "allow", updatedInput:
{"answers": …})` — which returns to the daemon as an ordinary
`PermissionResolved` push, settles through the broker's one claim point, and
reaches the hook through the existing Claude response builder, `updatedInput`
included. The app's `PermissionResolveDto` already carries `UpdatedInput`, and
the daemon relays it verbatim into the hook response and the server leg.

So the wire, broker, settlement, rail pips and tray Attention all carry the
question already; only the app's rendering and answer shape are wrong. This
slice is app-side plus one Core helper. No daemon, CLI, or wire change; the
feature works against any daemon advertising `permission/1`.

Out of scope: ACP elicitations from structured vendors (Cursor, Copilot, Kiro,
OpenCode, Gemini — their `elicitation/create` goes daemon → server → web and
never touches the local socket; AI-2197 owns bringing structured interactions
to the app); Codex (no elicitation reaches the bridge, and its hook response
builder strips `updatedInput`); detecting an answer given in the TUI;
a deny/dismiss affordance on the question card; server-side multi-question
rendering (AI-1052).

## Decisions

Settled with the owner during brainstorming, 2026-08-30:

1. **App-side classification over a new frame family.** The issue sketched a
   sibling `question/1` frame pair, written before the discovery that the
   elicitation already rides the permission lane end to end. New frames would
   re-buy the whole Core/daemon/app stack, make an older app show nothing for
   a question, and still need an app-side fallback against the shipped daemon.
   Classification therefore happens in the app, on the pending entry it
   already receives; the answer travels as the existing resolve frame with
   `Decision: "allow"` plus `UpdatedInput`. AI-2197 defines its own frames
   later, with real ACP requirements in hand.
2. **The answer shape is the documented Agent SDK contract**:
   `{"questions": <original questions array, verbatim>, "answers":
   {<question text>: <string | string[]>}}` — keys are the question texts,
   multi-select values are arrays of labels, free text is a first-class value
   (the "Other" affordance; the custom text is the answer, never the word
   "Other"). Every question is answered — partial answers are undocumented and
   never sent — and the composer enforces that, not just the card (§1). The
   web omits the `questions` passthrough and works; this app composes the
   documented form.
3. **One fast path**: a payload with exactly one single-select question *that
   has options* submits on option click. A free-text-only payload always has a
   visible submit affordance (§3). Any multi-select or multi-question payload
   gets selection state and one Submit, enabled once every question has an
   answer.
4. **The tray splits the wording**: "1 question waiting", "2 permission
   requests waiting", both parts when mixed. The Attention rule is unchanged —
   any pending entry asserts it. The split comes from one cache snapshot per
   emission, never from combining two independently emitted counts (§4).
5. **No Deny and no Allow always on the question card.** Deny's effect on
   `AskUserQuestion` is undocumented, and a standing always-allow rule for the
   question tool is never what the user means. The fallback permission card
   (unparseable or oversized payload) keeps Allow/Deny — Allow means "let the
   TUI ask" — but hides Allow always for this tool.
6. **Core-enforced caps make the outgoing legs bounded by arithmetic.** The
   incoming 64 KiB `MaxElementBytes` bound covers only the pending leg; the
   resolve frame repeats the `questions` array and adds the answers, and its
   own limit is `FrameCodec`'s 8 MiB frame cap. The parse caps and the
   composer's answer validation (§1) bound every value that can reach the
   composed `UpdatedInput` — the UI's input limit is a convenience mirror of
   a Core constant, never the enforcement point. In encoded bytes, worst
   case: the caps are UTF-16 code-unit counts and JSON escaping expands a
   unit to at most 6 bytes (`\uXXXX`), so answers keys ≤ 8 × 4096 × 6 ≈
   192 KiB, selected labels ≤ 8 × 16 × 1024 × 6 ≈ 768 KiB, Other texts ≤
   8 × 8192 × 6 ≈ 384 KiB, plus the retained `questions` element (its source
   was bounded at 64 KiB of raw UTF-8) and structural overhead — under 2 MiB
   against the 8 MiB cap. No preflight or truncation path is needed, and the
   same arithmetic bounds the hook response and the server relay (whose
   free-text path already accepts the web's unbounded text today).

## 1. Core: `ClaudeElicitation`

Beside `ClaudePermissions` in `Capacitor.Cli.Core`, owning the Claude
elicitation contract in one place.

- `ToolName = "AskUserQuestion"`.
- Caps, as constants beside the parser: `MaxQuestions = 8`,
  `MaxOptionsPerQuestion = 16`, `MaxQuestionTextChars = 4096`,
  `MaxOptionLabelChars = 1024`, and `MaxOtherTextChars = 8192` for the
  composer's free-text values (the UI's `MaxLength` references this constant
  rather than restating it). All are UTF-16 code-unit counts; decision 6
  carries the byte conversion. Claude emits at most 4 questions of at most 4
  options, so the caps are headroom, not a fit; they exist to bound the
  resolve payload (decision 6) and the rendered control count (§3). A payload
  over any cap fails the parse and falls back to the permission card.

`TryParse(string? toolInputJson) → ElicitationQuestions?` reads **every**
entry of `questions[]`, in payload order — never only `questions[0]`, the
server parser's trap (AI-1052). `ElicitationQuestions` retains the original
`questions` `JsonElement` (for the compose passthrough) plus the parsed list:
per question `Question`, `Header`, `MultiSelect`,
`Options[{Label, Description?}]`. The retained element is **detached** — a
`Clone()` independent of any `JsonDocument` the parser created — because the
result sits in the pending cache and is composed much later, long after every
parser-local owner is gone. The model is **parser-created and immutable**:
constructors are internal (only `TryParse` can produce one — Core already
exposes internals to its own test assembly and to nothing else) and every
collection is an immutable snapshot taken at parse. The composer's validation
is therefore grounded in parse output by construction; no App code can
fabricate an over-cap model or mutate one after the fact.

Field rules — protocol fields are strict (any violation fails the whole
parse → fallback card), display-only fields are tolerant:

- **Protocol, strict**: input must be an object; `questions` must be a
  non-empty array of objects, ≤ `MaxQuestions`. `question` must be a string,
  non-blank after trim, ≤ `MaxQuestionTextChars`; two questions sharing a text
  fail the parse (the answers map is keyed by text — duplicates cannot be
  answered unambiguously). `multiSelect`/`multi_select`: absent means false;
  when present it must be a boolean; when both spellings are present they must
  agree, else the parse fails (the flag decides string-versus-array in the
  answer — guessing changes the answer type). `options`, when present, must be
  an array of objects, ≤ `MaxOptionsPerQuestion`, each `label` a non-blank
  string ≤ `MaxOptionLabelChars`; an empty or absent array means free-text
  only. Duplicate labels within one question are deduplicated keeping the
  first — the label is the answer value, so the duplicates were
  indistinguishable on the wire anyway.
- **Display-only, tolerant**: `header` and `description` are used when they
  are non-blank strings and treated as absent otherwise (wrong type,
  whitespace); they never fail the parse and are trimmed with an ellipsis at
  render, so they need no cap.

`ComposeAnswers(ElicitationQuestions questions, IReadOnlyList<ElicitationAnswer>
answers) → JsonElement`, with `ElicitationAnswer(string Question,
IReadOnlyList<string> SelectedLabels, string? OtherText)`. The answer is
validated **against the parsed question**, so the bound is structural rather
than delegated to the UI: every entry of `SelectedLabels` must equal one of
that question's parsed option labels (already capped in count and length by
the parse); `OtherText`, when present, must be non-blank after trim and ≤
`MaxOtherTextChars`. String-versus-array is derived from the **parsed**
question's `MultiSelect`, never from a caller flag. The composer throws
`ArgumentException` (a programming error, not a user state) when: the answer
count differs from the question count; the answer keys are not exactly the
parsed question texts (parse already guarantees the texts are unique, so set
equality plus count is a bijection); a label is not among the question's
options or appears twice; a single-select answer has other than exactly one
of (one label, `OtherText`); a multi-select answer has neither a label nor
`OtherText`; or `OtherText` is blank or over the cap. An `OtherText` that
(after trim) exactly equals one of the question's option labels is
**normalized to that option's selection** rather than emitted as a second
copy: it merges into `SelectedLabels` (a no-op when already selected) — the
same collapse the parser applies to duplicate option labels, so the composed
value array never carries duplicates. The composer also owns value ordering:
selected labels in the question's option order, a genuine `OtherText` last. Output is decision 2's shape — the retained `questions` element copied
verbatim, and per answer a string or a `JsonArray` of the values — returned
as a **detached** `JsonElement`, valid independently of any document or node
the composer built it from. `new JsonArray(…)` constructor, never a
collection expression (AOT).

The classification rule, evaluated once per entry (§2): `Vendor == "claude"`
and `ToolName == ClaudeElicitation.ToolName` and `!ToolInputOmitted` and
`TryParse` succeeds.

## 2. Service: the answer path

`PendingPermissionRequest` gains `ElicitationQuestions? Questions`, computed in
the constructor by the classification rule. Both the chat tab and the tray
summary read this one property, so the two can never classify differently.

`IPermissionService` gains:

- `Task<PermissionResolveOutcome> AnswerAsync(PendingPermissionRequest target,
  IReadOnlyList<ElicitationAnswer> answers, CancellationToken ct)`. Requires a
  classified entry (`Questions` non-null; throw `ArgumentException`
  otherwise). Builds `PermissionResolveDto(target.RequestId, "allow",
  ApplyPermissions: null, UpdatedInput: ClaudeElicitation.ComposeAnswers(
  target.Questions, answers))` — the composer's validation (§1) runs before
  anything touches the wire — and shares `ResolveAsync`'s send/ack/tombstone
  tail; the two methods differ only in how the DTO is built, so the tail is
  extracted rather than duplicated. The exception boundary, exactly: the
  unclassified-target check and the composer run synchronously **before** the
  try that wraps the socket exchange (the same placement `ResolveAsync` gives
  its DTO build), so their `ArgumentException`s propagate to the caller;
  faults from the exchange itself map to `TransportFailure` as today; caller
  cancellation propagates.
- `IObservable<PendingSummary> Summary` with `PendingSummary(int Permissions,
  int Questions)`. Derived from **one** cache query per change
  (`QueryWhenChanged` counting classified and unclassified entries in the same
  snapshot, with the `StartWith` seed pattern `AgentsWithPending` uses), so
  every emission is a consistent pair — never a subtraction of two
  independently emitted counts, which can interleave across cache states and
  transiently miscount. Replays on subscribe.

Cache ownership on an answer, stated precisely (it is `ResolveAsync`'s
existing behaviour, now shared): **either ack concludes the entry** — under
the service lock, tombstone the id and evict — so `Applied` and
`AlreadyDecided` both remove the card at the ack, not at some later push. The
`Resolved` push is idempotent against that (tombstone re-add and remove of an
absent key are no-ops), whichever order push and ack land in. A
`TransportFailure` (including a sent-but-ack-lost socket death, which surfaces
as a transport error) leaves the entry in place; if the daemon did apply the
decision, the settlement push clears it.
`PendingCount` and `AgentsWithPending` stay totals: the rail pips and the tray
Attention rule aggregate both kinds and need no change.
`FakePermissionService` extends accordingly.

## 3. Chat tab: mixed cards on the NEEDS YOU row

`ChatTabViewModel`'s transform branches per entry: `Questions != null` →
`QuestionCardViewModel`, else `PermissionCardViewModel`. Both derive from a
small `PendingCardViewModel` base carrying what the pipeline and the row need
(`RequestId`, `RequestedAt`, `IsBusy`, `ErrorText`, disposal), so one sorted
collection — the existing `(RequestedAt, RequestId)` comparer — feeds the row.
`PendingPermissions` keeps its name and becomes
`ReadOnlyObservableCollection<PendingCardViewModel>`; `HasPendingPermissions`
is unchanged. The view's `ItemsControl` moves from a single `DataTemplate` to
type-keyed `ItemsControl.DataTemplates` — the pattern the same file already
uses for chat items. §1's caps bound a worst-case card at 8 question groups of
16 options — a scale the row's existing `ScrollViewer` handles without
virtualization.

`QuestionCardViewModel(PendingPermissionRequest, IPermissionService)`:

- One group per question, in payload order: `Header` (muted chip when
  present), `Question` (semibold), and either option buttons (single-select)
  or checkboxes (multi-select), each option's `Description` beneath its label
  in the muted style; plus an "Other…" free-text box per question —
  single-line (`AcceptsReturn` off), `MaxLength` bound to
  `ClaudeElicitation.MaxOtherTextChars`, trimmed before use; whitespace-only
  text does not count as an answer.
- Selection state: for single-select, picking an option clears the Other text
  and typing Other text clears the picked option — exactly one of the two
  holds at a time. For multi-select, the Other text joins the checked labels
  as one more value. The card hands the service each question's
  `SelectedLabels` and `OtherText` as they stand; ordering and validation are
  the composer's (§1).
- A question is answered when it has one selected option, ≥ 1 checked option,
  or non-blank Other text. `SubmitCommand` is enabled only when every question
  is answered and the card is not busy.
- Decision 3's fast path — exactly one question, single-select, **with
  options**: option buttons submit on click; the Other box submits on Enter,
  and an inline "Answer" button appears beside it once the text is non-blank,
  so mouse and touch users always have a visible, clickable affordance. A
  free-text-only payload (zero options) is not the fast path: it renders the
  standard Submit button.
- **Single flight**: every submitting action runs on the UI thread and sets
  `IsBusy` synchronously before its first await; each entry point returns
  immediately when `IsBusy` is already set. A double click or click-plus-Enter
  therefore cannot start two sends, with or without command requery. Controls
  are disabled while busy.
- Outcomes: `TransportFailure` clears `IsBusy` and sets `ErrorText`;
  `Applied`/`AlreadyDecided` need no UI action — the ack already evicted the
  entry (§2) and the card leaves with it. The submit continuation runs against
  a card the pipeline may have disposed mid-flight (an ack or a `Resolved`
  push evicts and disposes it — in the normal `Applied` path the card is
  already disposed when `AnswerAsync` returns). Disposal and the continuation
  both run on the UI thread, so the rule is race-free: **every property write
  after the await is guarded by a disposal check** — a disposed card gets no
  `IsBusy` clear, no `ErrorText`, no notification of any kind; a live card
  always gets `IsBusy` cleared in the guarded `finally`. The command's catch
  boundary, exactly: cancellation on the card's own lifetime token (cancelled
  in `Dispose`) is swallowed; any other exception — which per §2's service
  contract can only be a programming error such as the composer's
  `ArgumentException` — is caught as a last resort, logged to stderr, and
  rendered as a generic `ErrorText` line on a live card. Nothing escapes an
  async command as an unobserved exception, and a bug stays visible in the UI
  and the log rather than vanishing; the composer's rejections themselves are
  pinned by direct Core tests, not by this backstop.
- No Deny, no Allow always (decision 5).

`PermissionCardViewModel` changes only twice: it derives from the base, and
`ShowsAllowAlways` becomes `Vendor == "claude" && ToolName !=
ClaudeElicitation.ToolName` — the fallback card for an unrenderable question
must not offer a standing rule for the question tool.

## 4. Tray

The tray's `CombineLatest` is at its 7-source overload limit, so the
`permissionCount` input becomes the service's `Summary` (§2) — one
already-consistent `PendingSummary` per emission, replay-on-subscribe like the
input it replaces; the replay guard extends to it. `PendingBody` renders the
question part first, then permissions, then consent: "1 question waiting",
"2 permission requests waiting", comma-joined when mixed. The Attention rule
reads the summary's total, unchanged in effect. A null `IPermissionService`
yields an empty summary, as it yields 0 today.

## 5. Settlement: unchanged by construction

Nothing here adds a settlement path or a cache transition the permission flow
does not already have. The card-side interleavings and the ack-concludes rule
are §2 and §3's contracts; beyond those:

- An answer given on the web arrives as the existing `PermissionResolved`
  push; agent withdrawal, daemon shutdown and session end settle exactly as
  for a permission. Every settlement clears the card, the pips and the tray
  from the one pending cache.
- The app's answer wins or loses the broker claim exactly as an allow/deny
  does. A withdrawal or session end racing the send loses it: the ack comes
  back `Ok=false` → `AlreadyDecided`, and the push — before or after the ack —
  is idempotent against the eviction.
- The daemon's decision log records `allow`/`app` — the same outcome the web's
  elicitation answer produces server-side.

## 6. Edge cases

- **Multi-question payloads**: all questions rendered, all required, one
  resolve carries every answer. The web card still shows only `questions[0]`
  (AI-1052, untouched here); the desktop is strictly better, and whichever
  surface settles first wins the claim as today.
- **Oversized (`ToolInputOmitted`), unparseable, or over-cap payload**:
  permission fallback card — Allow lets the TUI ask, Deny blocks, Allow always
  hidden (decision 5). Without a successful parse no answers object can be
  composed, so a question card is impossible by construction.
- **Answered in the TUI while the card is up**: the vendor proceeds; the entry
  lingers until session end settles it — AI-2308's accepted limitation,
  unchanged in either direction.
- **Two surfaces answer**: the broker's claim decides; the loser's ack is
  `Ok=false` → `AlreadyDecided` → the card leaves with no error shown.
- **Options with empty labels / no options at all**: an empty label fails the
  parse (fallback card); a question with zero options renders free-text only,
  with a visible Submit (§3).
- **Codex**: never classifies — the vendor gate is part of the rule.
- **Older daemon without `permission/1`**: nothing lights up, exactly as for
  permissions today.
- **Older app, newer payloads**: an app without this slice keeps showing the
  broken allow/deny card — no worse than today, and no protocol skew exists
  because the wire did not change.

## 7. Testing

- **Core.Tests.Unit**, `ClaudeElicitation`:
  - Parse: every question read (a two-question payload yields two), payload
    order kept, `multiSelect` and `multi_select` both honored and their
    disagreement fatal, non-boolean flag fatal, empty options → free-text
    only, non-array `options` / non-object option / blank label fatal,
    duplicate option labels deduplicated keeping the first, duplicate question
    texts fatal, blank or missing question text fatal, non-object input and
    non-array `questions` fatal, each §1 cap exercised at the boundary (at the
    cap passes, one over fails), tolerant header/description (wrong type and
    whitespace treated as absent, never fatal); the model's collections are
    immutable (mutation attempts fail at the type) and independent of the
    caller's input string.
  - Compose: string vs array derived from the parsed flag, `questions` passed
    through verbatim, the exact `answers` key set, values ordered by option
    order with `OtherText` last; rejection of a missing, extra, unknown-key,
    or duplicate-key answer, a label not among the question's options, a
    label selected twice, a single-select answer with both a label and
    `OtherText` or with neither, an empty multi-select answer, and a blank or
    over-cap `OtherText`; an `OtherText` equal to an option label normalized
    into the selection with no duplicate value emitted (both when that label
    is already selected and when it is not); a maximal composed payload — every cap at its
    bound, filled with worst-case content (characters that JSON-escape to
    `\uXXXX` and multi-byte UTF-8, not plain ASCII) — stays under
    `FrameCodec`'s frame cap, asserted through the codec itself so the bound
    survives a DTO change.
  - Ownership: parse, let every parser-local JSON owner go out of scope (and
    force a GC in the test), then compose and serialize — both the retained
    `questions` element and the composed output must still read back intact.
- **App.Tests.Unit**:
  - `PermissionServiceTests` — `AnswerAsync` sends `allow` with the composed
    `UpdatedInput` and no `ApplyPermissions`; concludes (tombstone + evict) on
    `Ok=true` and on `Ok=false`; leaves the entry on transport failure; a
    `Resolved` push before the ack and after the ack both end in the same
    state; throws on an unclassified entry and lets a composer rejection
    propagate (nothing sent in either case); classification matrix (codex
    vendor, wrong tool name, omitted input, null input, unparseable input all
    stay unclassified); `Summary` seeds on subscribe and every emission is a
    consistent pair while entries of both kinds are added and settled.
  - `ChatTabViewModelTests` — an `AskUserQuestion` entry becomes a question
    card and a plain tool a permission card in the same row, ordering across
    kinds by `(RequestedAt, RequestId)`, removal on `Resolved` for both kinds.
  - `QuestionCardViewModelTests` — fast path submits on option click, on
    Other-text Enter, and via the inline Answer button; free-text-only payload
    shows the standard Submit; multi-select and multi-question payloads gate
    Submit on every question answered; single-select option pick and Other
    text displace each other; whitespace-only Other does not count; double
    activation sends once; a `Resolved` push evicting the card mid-flight
    leaves no error, no exception, and raises no property-change notification
    after disposal; transport failure shows the reason and
    re-enables; a service that throws (the §3 backstop) clears `IsBusy`, sets
    the generic error line and leaks no exception; the resolve payload shape.
  - `PermissionCardViewModelTests` — `ShowsAllowAlways` false for the
    `AskUserQuestion` fallback card, true for other Claude tools.
  - `TrayViewModelTests` — wording for questions only, permissions only and
    mixed; the replay-on-subscribe guard covers the summary.
  - `ChatTabViewSmokeTests` — a question card renders headless (options,
    checkboxes, Other, Submit) and both card kinds coexist on the row.
- `docs/CHANGES.md` gains this feature's section. No README change — no CLI
  surface changed.

## Risks

- **Undocumented contract corners.** The Agent SDK documents the answer shape,
  multi-select arrays and free text; it does not document partial answers or
  deny-for-`AskUserQuestion`. The composer makes partial answers
  unrepresentable, the card never sends deny, and the fallback card is the
  escape hatch if a payload defeats the parser.
- **Contract drift.** If Claude Code reshapes `AskUserQuestion`'s input, the
  parse fails and every question degrades to the fallback permission card —
  ugly but answerable (the TUI asks), never silent.
- **The `questions` passthrough.** The web answers without it and works; the
  documented shape includes it and this app sends it. Both forms are accepted
  today; if a future Claude Code rejects one, the documented form is the safer
  side to be on.
