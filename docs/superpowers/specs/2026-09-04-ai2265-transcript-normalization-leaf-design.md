# One home for transcript normalization: `Capacitor.Models.Transcripts` (AI-2265, #679)

Transcript normalization for the file-based vendors is written twice: the server's per-vendor
normalizers turn raw JSONL lines into canonical events for persistence, and Core's per-vendor
projections turn the same lines into `AcpEventEnvelope`s for the desktop Chat tab (AI-2196). This
design moves the whole of it into one leaf project in the CLI repo, consumed by Core, the desktop
app and the server, and ports Claude and Codex to full parity so the server can delete its copies.
The remaining seven vendors follow one at a time on the same shape.

Out of scope, by the issue: any change to `TranscriptBatch` on the wire, the other seven vendors,
tool kinds (AI-2426 builds on this), the server's `AcpEventEnvelope` mirror, and read-side
extension accessors on the server.

## Problem

The server's `ClaudeCodeNormalizer` and `CodexNormalizer` produce far more than a chat needs:
deterministic event ids (Claude's record `uuid`, a content hash of the line for Codex), `$causedBy`
and `$lineNumber` metadata, `$usage` as a schema `TokenUsage` with a cache-creation companion and a
Codex-only echo flag, per-vendor extension blobs the chat, trace and stats code read, image
attachments as bytes, a `ContextCompacted` event, and a Codex usage cluster that stamps one
`token_count` onto every event of a response and spans batches. Core's projections cover text,
tool calls and results, and thinking.

Three facts found while surveying both repos shape the solution:

- **No server production assembly references `Capacitor.Cli.Core`.** Only two test projects do.
  The issue's "the Core submodule it already references" holds for tests only, and Core carries
  the Duende OIDC client and Tomlyn, which the server has no use for.
- **The server's envelope lane cannot carry transcripts.** `AcpSessionMapper` covers twelve kinds,
  writes no `$usage`, `$lineNumber` or `$causedBy`, and appends through a thin path that skips the
  transcript pipeline: no attachments, no dedup set, no high-water mark, no normalizer context.
  Whatever the leaf emits feeds the transcript pipeline through an adapter that replaces the
  per-vendor normalizers; the envelope stays the daemon's wire type.
- **The server keeps no raw transcript lines.** Only canonical events are appended, with
  `$lineNumber` for watermark recovery. An ingested session cannot be re-projected server-side.
  The immutable-history rule in section 8 follows from this.

A probe settled the one open feasibility question. A NativeAOT console app referencing
`Kurrent.Agent.Schema` 0.4.1 (and through it `Google.Protobuf` 3.34.1) publishes with zero
IL2026/IL3050 warnings, formats a message carrying a `Struct` extension to protobuf JSON with proto
field names, parses it back, and round-trips binary. The binary is 4.2 MB. The schema package is
AOT-clean and needs no special handling in the CLI binaries.

## Decisions

Settled with the owner during brainstorming, 2026-09-04:

1. **Projections emit the canonical schema types directly.** `Kurrent.Agent.Schema` is the
   persistence format already, is owned by Kurrent, and is AOT-clean. Rejected: a Core-only
   in-process event model (a third vocabulary), and widening `AcpEventEnvelope` (bytes and lists on
   a flat wire contract the daemon never sends, mirrored by hand on the server). Rejected:
   compiling the `.proto` files inside the CLI repo. protoc's C# output links against the
   Google.Protobuf runtime either way, and a message generated in a different assembly is a
   different CLR type from the one the server persists; the package is taken, pinned to the
   server's version.
2. **A leaf project, not Core itself.** `src/Capacitor.Models.Transcripts/` has one package
   reference, the schema, and no project references. Core, the app and the server reference it.
   Rejected: a production reference from the server to `Capacitor.Cli.Core`, which would import
   every Core dependency, present and future, into the server.
3. **The leaf never mutates an event it has returned.** Everything the Codex normalizer does in
   place today (usage stamping, `task_complete` timing, exec and patch telemetry that lands after
   its output) becomes an explicit amendment or a `UsageApplied` event the caller applies. The
   leaf holds no persistence knowledge; the server adapter classifies targets as pending or
   persisted from the batch it is writing and the writer's dedup set.
4. **`JsonElementExtensions` moves to the leaf and becomes public.** Core, the app and the server
   read JSON through the same tolerant accessors.
5. **Stored events the envelope cannot express are shown as notes, not dropped.** `ContextCompacted`
   is persisted exactly as today and the Chat tab renders it as a `system_note` ("Context
   compacted"). The rule for any later such event: a note when it marks something a reader should
   see, skipped in the chat only when it does not.
6. **The leaf owns every timestamp.** The caller passes the receive time into `Project`, and the
   leaf uses it wherever the server uses the clock today, so output is a pure function of the
   line, the context and that instant. Tests inject a fixed instant.

## 1. The leaf project

`src/Capacitor.Models.Transcripts/`, assembly and root namespace `Capacitor.Models.Transcripts`,
`IsAotCompatible` and `IsTrimmable`, `InternalsVisibleTo` its own test project and Helpers only.
It follows the harness layout: vendor code under `Harness/<Vendor>/`, namespaces after
directories, one registration site.

Moves in with the first PR:

- `ClaudeTranscriptEvents` → `Harness/Claude/`, `CodexRolloutEvents` and `CodexCommandClassifier`
  → `Harness/Codex/`, `TranscriptProjection` and `ITranscriptProjection` to the root. The leaf
  cannot reference Core, where `AcpEventEnvelope` lives, so the projections switch to the
  contract in section 2 in this same PR, emitting the schema messages for what they map today
  (user and assistant text, thinking, tool calls and results); Core's envelope adapter (section 6)
  reproduces today's chat output, and the existing chat tests pin that.
- `JsonElementExtensions`, made public.

Moves in from the server, when each vendor reaches parity:

- The extension records the projections construct: `ClaudeCodeToolResultExtension`,
  `ClaudeCodeUserMessageExtension`, and the Codex session, turn-context, assistant-text,
  agent-message, tool-call, exec-result, patch-result and turn-timing records, with their
  `Struct` packing. The server keeps its read-side accessors and unpacking.
- The two Capacitor-specific Codex events, `CodexUsageBackfilledEvent` and
  `ContextCompactedEvent`, as plain records. The leaf cannot carry the Eventuous `[EventType]`
  attribute, so the server registers both names in its type map beside the schema registrations.
  `ContextCompactedEvent.ReplacementHistory` is a `JsonElement`; the projection parses each line
  into a `JsonDocument` it disposes, so the element it stores must be a `Clone()`.
- `ExtractedAttachment`, as `TranscriptAttachment`; the server's record is deleted.

Core references the leaf and keeps `AcpEventEnvelope`, `AcpEventKind` and the wire-compat tests
unchanged. The CLI repo's `Directory.Packages.props` pins `Kurrent.Agent.Schema` at the server's
version. `Capacitor.slnx` and both `CLAUDE.md` layout notes name the new project.

## 2. Contracts

```csharp
public interface ITranscriptProjection {
    TranscriptContext CreateContext(string sessionId, string? agentId);
    ProjectionResult Project(string line, int lineNumber, DateTimeOffset receivedAt, TranscriptContext context);
}

public abstract class TranscriptContext {
    public virtual void BeginBatch() { }      // clears state that has no meaning across batches
}

public sealed record ProjectionResult(
    IReadOnlyList<CanonicalEvent>  Events,
    IReadOnlyList<EventAmendment>  Amendments,
    string?                        Rejected = null);   // see "Rejected lines"; when set both lists are empty

public sealed record CanonicalEvent(
    string          EventType,          // the schema's EventTypeMap name, or the leaf record's fixed name
    object          Payload,            // a schema message, or CodexUsageBackfilledEvent / ContextCompactedEvent / UsageApplied
    Guid            EventId,
    DateTimeOffset  Timestamp,          // effective: the record's own, else receivedAt (see "Timestamps")
    string?         RecordTimestamp     = null,   // the record's raw timestamp string, null when it had none
    string?         CausedBy            = null,
    TokenUsage?     Usage               = null,
    bool            UsageIsEcho         = false,
    long?           CacheCreationTokens = null,
    IReadOnlyList<TranscriptAttachment>? Attachments = null);

public sealed record EventAmendment(Guid TargetEventId, string Slug, Struct Extension);

public sealed record UsageApplied(TokenUsage Usage, Guid AnchorEventId, IReadOnlyList<UsageTarget> Targets);
public sealed record UsageTarget(Guid EventId, string EventType, string? ToolName, bool IsEcho);

public sealed record TranscriptAttachment(Guid Id, string FileName, string ContentType, byte[] Data);

public static class TranscriptProjection {
    public static ITranscriptProjection? For(string vendor);   // ordinal-ignore-case; null for an unknown vendor
}
```

**Payloads.** A schema message is complete as returned; the caller adds nothing to it. `Usage`,
when set, is the whole `$usage` block as it is written today, `additional_counts` included; the
adapter serializes it as is and adds the echo and cache-creation companions.

**`UsageApplied`** is a `CanonicalEvent` with `EventType = "UsageApplied"`, `EventId` =
`Sibling(AnchorEventId, "usage-backfill")`, and the `token_count` line's timestamps. It is never
persisted verbatim; section 5 defines how the adapter consumes it. `Targets` are in cluster
order.

**Amendments** carry one slug's block for one target. Applying one is a shallow field merge into
that slug's `Struct` on the target: the block's fields overwrite same-named fields, other existing
fields stay, the slug is created when the target has none, amendments apply in the order returned,
and the `Struct` is cloned on application so a caller holding the amendment cannot alias the
event. A target the caller does not hold is dropped by the caller. This is what the server does
today when it sets `extensions.codex.exec`, `.patch` or `.task` on an event it already emitted.

**Timestamps.** `Timestamp` is the record's own when it parses, else `receivedAt`; every event
from one line shares the same `receivedAt`. For a schema payload it is the same instant the leaf
writes into the message's own `timestamp` field, so the adapter maps nothing beyond the payload;
it exists on the record for the app and for the leaf's own events. `RecordTimestamp` is the
record's raw string, present exactly when the server writes `$timestamp` today. The synthesized
Codex web-search result carries its call's `Timestamp` plus one tick and its call's
`RecordTimestamp`, so its metadata equals the call's as today. Cross-line sorting is not fed by
the leaf at all: the pipeline parses the line's root `timestamp` itself into the batch entry's sort
key (a parsed `DateTimeOffset`, nulls last, then line number, then within-line sequence) and
offsets every secondary event of a line by one tick, unchanged.

**Rejected lines.** `Rejected` is set, with a short reason, when the line is empty or
whitespace-only, when it is not valid JSON, when it parses to something other than an object, or
when a field the projection cannot proceed without is unusable (Claude: a `uuid` that is not a
GUID). A rejected line mutates no context state. An object whose discriminators are absent,
wrong-typed or unknown is not rejected: it is a valid record the projection ignores, `Events` and
`Amendments` are empty, and it may still touch context state the way a real record of its kind
would (Claude's usage-suppression memory resets on any non-assistant record; Codex's
`turn_context` and `web_search_end` are record kinds that emit nothing). The server pipeline skips
blank lines before the projection sees them, unchanged, and treats any other rejected line exactly
as a thrown parse error today: logged with an excerpt, zero entries, the watermark steps over it,
never retried. The app drops every rejected line.

### Identifiers

Every id is `new Guid(XxHash128.Hash(bytes))` over the bytes named here, with `Guid`'s own
16-byte layout (`TryWriteBytes`, mixed-endian) wherever a `Guid` is an input, UTF-8 without a BOM
for strings, and the line's exact bytes as received, newline excluded. These are persistence
contracts: the server's dedup set is keyed by them.

| Id | Bytes hashed | Status |
|---|---|---|
| Claude record | not hashed: `Guid.Parse(uuid)` | unchanged |
| Claude fallback (no `uuid`) | UTF-8 of `"{lineNumber} {line}"`, line number in invariant decimal | unchanged |
| Claude attachment | UTF-8 of the id scope, then the record id's 16 bytes, then the block index as a 4-byte little-endian `int` | unchanged |
| Codex record | UTF-8 of the line | unchanged |
| `Sibling(primary, suffix)` | the primary id's 16 bytes, then UTF-8 of the suffix | unchanged |
| Codex synthesized web-search result | `Sibling(call id, "result")` | unchanged |
| Codex usage backfill | `Sibling(cluster first event id, "usage-backfill")` | unchanged |
| Claude, every emitted event after a record's first | `Sibling(record id, "block:{index}")`, the block's index in the record's raw `content` array, invariant decimal | new |

A Claude record that emits several events gives the record id to the first event it emits, in
raw block order, and the block sibling id to each later one, keyed by the raw index of the block
that produced it; blocks that emit nothing consume no id. The id scope is `"{sessionId}:{agentId}"`
for a subagent stream and the bare session id otherwise, computed by the leaf context from
`CreateContext`'s arguments; the ids the caller passes must be the canonical forms the server
stores. The leaf's tests carry fixed vectors for every row, including a multi-block assistant
record and a multi-result user record, and the server's parity suite proves the unchanged rows
against the legacy code on the real corpus.

## 3. Claude at parity

Keyed on the record's root `type`; `RecordTimestamp` from the root `timestamp`; `CausedBy` from
`parentUuid`. Noise, emitting nothing: `progress`, `system`, `file-history-snapshot`,
`queue-operation`, `pr-link`, `last-prompt`, `ai-title` (the pipeline's title side-channel reads
it), and any type the build does not know.

- `assistant`: one event per content block, in order. `text` → `AssistantTextGenerated`;
  `tool_use` → `AssistantToolCallsGenerated` with one `ToolCallInfo` (`call_id` = `id`,
  `tool_name` = `name`, `arguments` = `input` as a `Struct`; a non-object input is wrapped as
  `{"input": …}`); `thinking` → `AssistantThinkingGenerated` (`content`, `signature`,
  `encrypted = false`). Usage from `message.usage` rides on the line's first emitted event, and
  only when it differs from the previous assistant record's usage (the context remembers it and
  any non-assistant record resets it): `input_tokens`, `output_tokens`, `cache_read_input_tokens`
  as cached input, `model` from `message.model`, and `cache_creation_input_tokens` as
  `CacheCreationTokens`. Ids per the identifier rule: the first emitted event keeps the record id
  and later ones take block sibling ids and carry no usage. Unknown block types emit nothing.
- `user`, string content → one `UserMessageReceived`, dropped when the text opens with
  `<available-deferred-tools`. Array content, two shapes, decided by whether any `tool_result`
  block is present:
  - *With tool results:* one `ToolResultReceived` per `tool_result` block, in raw order, and
    nothing else from the record; text and image blocks beside them are dropped, as today
    (`call_id` = `tool_use_id`; `result` = the string, the joined `text` blocks, or the raw JSON;
    `extensions.claude_code` = `tool_use_result`, `output_raw`, `is_error`). The first result
    keeps the record id, exactly legacy's single event; later results are new events on block
    sibling ids.
  - *Without tool results:* one `UserMessageReceived` on the record id, `content` = the `text`
    blocks joined with `"\n"` (empty when there are none), dropped when it opens with
    `<available-deferred-tools`, with every `image` block as a `TranscriptAttachment` on it under
    the attachment id above. A record with neither text nor image blocks emits nothing.
  A root `isMeta` sets `extensions.claude_code.is_meta` on a `UserMessageReceived` only.
- `attachment` whose `attachment.type` is `queued_command` → `UserMessageReceived` from the prompt,
  string or content array.

Two additive extension fields the chat needs and the server does not write today:
`extensions.claude_code.is_sidechain` on every event from a sidechain record, and
`extensions.claude_code.origin_kind` on a `UserMessageReceived` whose record carries `origin.kind`
(Claude Code's finished-background-task injection). The schema lists `is_sidechain` under the
`claude_code` slug and declares additions under it non-breaking; the server's unpacker
deserializes the slug with System.Text.Json, which ignores properties its records do not declare.

## 4. Codex at parity

Keyed on the envelope `type`, then `payload.type`; `RecordTimestamp` from the envelope; no
`CausedBy`. Ids per the table above.

- `session_meta` → `SessionStarted` with `extensions.codex` (`cwd`, `originator`, `cli_version`,
  `source`, `model_provider`, `git`), suppressed when `payload.thread_source` is `subagent` or
  `payload.source.subagent` exists.
- `turn_context` → nothing; the context records `payload.model`.
- `response_item.message`, role `user` → `UserMessageReceived`, dropped when the text is only an
  `<environment_context>` block; role `assistant` → `AssistantTextGenerated` with
  `extensions.codex.phase` when present; `developer` and `system` → nothing.
- `response_item.agent_message` → `AssistantTextGenerated` with `extensions.codex.agent_message`
  (`author`, `recipient`).
- `response_item.reasoning` → `AssistantThinkingGenerated` (`encrypted`, `content` from `content`
  or the joined `summary`, `extensions.openai.thinking.raw` = `encrypted_content`).
- `function_call` and `custom_tool_call` → `AssistantToolCallsGenerated` (`call_id`,
  `tool_name` = namespace-qualified name, `arguments`; a custom tool's input becomes
  `{"input": …}`; `extensions.codex` `name` and `namespace` when a namespace is present).
- `web_search_call` → `AssistantToolCallsGenerated` (`web_search`, the payload as arguments) plus a
  synthesized `ToolResultReceived` (`result` = the `action` JSON, timestamps per section 2, the
  `result` sibling id). The call id comes from the preceding `web_search_end`; the context keeps
  the queue, the ever-enqueued set that stops a replayed end-event stashing a duplicate, and the
  sticky map from call event id to assigned call id, all across batches.
- `tool_search_call` → `AssistantToolCallsGenerated` (`tool_search`), no synthesized result.
- `function_call_output`, `custom_tool_call_output`, `tool_search_output` → `ToolResultReceived`
  (`call_id`, `result`). Exec and patch telemetry stashed earlier in the batch merges into
  `extensions.codex.exec` and `extensions.codex.patch` before the event is returned.
- `event_msg.exec_command_end` and `patch_apply_end` → an `EventAmendment` (`codex` slug, the
  `exec` or `patch` field) for the result already emitted in this batch, else a stash for the
  output still to come. `BeginBatch()` clears the stash and the batch's emitted-result index.
- `event_msg.task_complete` → an `EventAmendment` (`codex` slug, the `task` field: `duration_ms`,
  `time_to_first_token_ms`) for the current cluster's first event. When that event was emitted in
  an earlier batch the caller drops the amendment; the server loses the same timing today, because
  it mutates an event object that was already written.
- `event_msg.token_count` → one `UsageApplied`, or nothing. Nothing when the cluster is empty,
  when it is already finalized (a repeated `token_count` is a no-op until a new member opens the
  next cluster), or when `info.last_token_usage` is absent. Otherwise: `Usage` with net input
  (`input_tokens` minus `cached_input_tokens`, floored at zero), output, cached input, reasoning,
  `model` from the last turn context, and `additional_counts.model_context_window` from
  `info.model_context_window` when present; `AnchorEventId` = the cluster's first event; one
  target per member in cluster order, `ToolName` = the first tool call's name for an
  `AssistantToolCallsGenerated` member, `IsEcho` false on the first member that is not
  `AssistantThinkingGenerated` (the first member when all are thinking) and true elsewhere. The
  cluster is every reasoning, assistant text, agent message and tool call since the previous
  finalization; results and the synthesized web-search result are not members; the next member
  after a finalization opens a new cluster.
- `compacted` → `ContextCompactedEvent` (`replacement_history`, `encrypted_content`).
- Dropped: `world_state`, `inter_agent_communication_metadata`, `sub_agent_activity`,
  `thread_settings_applied`, the `event_msg` form of `agent_message`, and `web_search_end` beyond
  its queue effect.

State that survives batches, all of it as the server keeps it today: the last turn-context model,
the usage cluster and its finalized flag, the web-search queue, ever-enqueued set and sticky map.
State cleared by `BeginBatch()`: the exec and patch stashes and the index of results emitted this
batch. The Claude context keeps only the previous usage, across batches.

## 5. The server adapter

`ProjectionNormalizer : ITranscriptNormalizer` in `Sessions/Canonical/`, constructed with a vendor
key, its leaf projection and a `TimeProvider` (`TimeProvider.System` in production), registered
once per vendor in place of the two deleted classes; the other seven registrations are untouched. `ITranscriptNormalizer` gains `string Vendor { get; }` so
the pipeline keys the Claude `ai-title` side-channel on the vendor instead of on a class it no
longer has.

`NormalizerContext` loses every Claude and Codex field and gains: the leaf context, created from
the session and agent ids the pipeline already sets; a per-batch map from event id to the
`NormalizedEvent` the adapter has returned in this batch; and a predicate the writer supplies that
answers whether an id is already in its dedup set. `ClearTransientBatchState()` clears the map and
calls `BeginBatch()`.

**Phases.** The pipeline normalizes every surviving line of a batch, then writes. Amendments and
usage stamps are applied during the normalize phase, to `NormalizedEvent`s the adapter still holds,
before anything is serialized; the write phase sees final metadata. This is the structure today.

Per line, under the context lock the pipeline already holds, the adapter:

- calls the projection with the line, its line number, the `TimeProvider`'s current time and the
  leaf context; on `Rejected` it throws, and the pipeline's existing catch logs the excerpt and
  moves on;
- turns each `CanonicalEvent` that is not a `UsageApplied` into a `NormalizedEvent`: payload as is,
  `$lineNumber`, `$vendor`, `$timestamp` = `RecordTimestamp` when present, `$causedBy` when
  present, `$usage` from `Usage` with `$usage_echo` and `$claude_cache_creation_tokens` as today,
  attachments as they are, and records it in the batch map. The first such event is `Normalize`'s
  return value and the rest go on `PendingEmissions`, so sequence and tick ordering are unchanged;
  a line with none returns null, which is how the pipeline already treats a noise line;
- applies each `EventAmendment` to the batch-map target with the merge in section 2; a target not
  in the map is dropped;
- consumes a `UsageApplied`. Each target is one of three: **pending** when it is in the batch map
  and the writer's predicate says it is not in the dedup set; **persisted** when the predicate
  says it is; **unknown** when it is neither, which happens only when its append failed and its
  line has not been re-delivered yet. Every pending target's `NormalizedEvent` is stamped with
  `Usage` and its `IsEcho`. If any target is persisted, every pending stamp is forced to echo and
  one `CodexUsageBackfilledEvent` is queued on `PendingEmissions`, as today: id = the
  `UsageApplied` id; `Model`, `InputTokens`, `OutputTokens`, `CacheReadTokens`, `ReasoningTokens`
  from `Usage`; `ModelContextWindow` = `UsableContextWindow(additional_counts.model_context_window)`;
  `Targets` = the persisted targets in cluster order, `EventId` in `"N"` format, `EventType`,
  `ToolName`, `IsEcho`; metadata = `$lineNumber`, `$vendor` and `$timestamp` of the `token_count`
  line, no `$usage`. An unknown target is ignored: not stamped, not named in a backfill event,
  which is the legacy outcome (the server stamps an object that is never written). The cases: all
  pending → stamps only; mixed → echo stamps plus a backfill event naming the persisted ones; all
  persisted → the backfill event only; any unknown → as if absent.

Retry semantics are unchanged: the leaf context is mutated during normalize and not rolled back
on an append failure, exactly as `NormalizerContext` is today, and the replay guards the Codex
normalizer carries for that reason move with it. An adapter test drives a failed append followed
by re-delivery of the same lines and pins the outcome.

The pipeline's high-water mark, per-line atomicity, cross-line sort, dedup set, retry, attachment
blob storage, hosted-session source guard and derived side events are untouched.

## 6. Core and the desktop app

Core gains `TranscriptEnvelopes.ToEnvelope(CanonicalEvent)`, the one place that maps a stored
event to the chat vocabulary: `UserMessageReceived` → `user_message`, `AssistantTextGenerated` →
`assistant_text`, `AssistantThinkingGenerated` → `assistant_thinking`, one `tool_call` per
`ToolCallInfo`, `ToolResultReceived` → `tool_result` with the 4096-unit cap the chat projection
applied until now, which moves here because stored results are never capped,
`ContextCompactedEvent` → `system_note`. `UsageApplied`, `SessionStarted` and anything else map to
nothing; `TimestampIso` is the event's `Timestamp`. Two vendor display rules the projections
carried for the chat move beside it, under Core's `Harness/<Vendor>/`: Claude's wrapper stripping
and its task-notification note (recognised by `origin_kind`, sidechain events skipped by
`is_sidechain`), and Codex's injected-prelude filter for user text.

`ChatTabViewModel` creates one leaf context when its projection resolves and a new one whenever
the tail reports a reset or the transcript path changes. The tail's only reset signal is a length
regression, a limitation AI-2196 accepted: a same-path replacement that is already as long as the
old file by the next poll is not detected, and the chat then shows the old lines followed by the
new file's, with the projection context carried across. The consequence here is a possibly wrong
usage suppression or Codex cluster in a live view that is never persisted; it is accepted and
noted, not fixed. Each tail read that yields lines is one batch: `BeginBatch()` first, then
`Project` per line with the line number the tail tracks and the app's clock as `receivedAt`.
Amendments and rejected lines are ignored, and the envelopes render as before. There is no retry
in the app.

## 7. Testing and parity

**Leaf tests** at `test/Capacitor.Models.Transcripts.Tests.Unit/`, mirroring `Harness/<Vendor>/`.
The existing projection tests move there and grow to the full surface. Inputs are inline JSON for
single rules plus a synthetic fixture corpus checked into the repo with golden expected output per
fixture. The repo is public, so nothing from the server's captured sessions is copied; the
synthetic fixtures cover each rule that matters: multi-block Claude lines, tool results with image
blocks, task notifications, sidechain records, an invalid `uuid`, rejected and ignored lines of
every kind in section 2, Codex clusters straddling a batch boundary, a repeated `token_count`, a
thinking-only cluster, web-search pairing across batches, exec and patch telemetry in both orders,
`task_complete` in the same batch and across one, compaction, subagent rollouts. A golden file
holds every channel of every `ProjectionResult`: events as schema JSON plus id, effective and
record timestamps, caused-by, usage and its flags, attachments by id, filename, content type and a
SHA-256 of the bytes; amendments; `UsageApplied`; `Rejected`. Every fixture runs under a fixed
`receivedAt`, and the batch boundaries it uses are part of the fixture. The identifier table has
fixed vectors, one per row. A re-record switch regenerates the goldens.

**Parity suite** in the server repo at `test/Capacitor.Server.Tests.Ingest/Parity/`, run against
the real corpus under `test/data`: the Claude sessions with their subagent files and the three Codex
rollouts. For each fixture it runs the legacy normalizer and the adapter over the same lines
through the same pipeline and compares what reaches the event store, event for event: type, id,
payload JSON, every metadata key including usage, attachments by id, filename, content type and
byte digest. The legacy normalizers have no clock seam, so the suite first asserts that every
corpus line carries a timestamp (it does today); timestamp-less lines are covered by the leaf's
goldens under the fixed instant. It runs once as a single batch, once in batches of one line, and
once with boundaries placed at every sensitive pair the corpus contains: a cluster and its
`token_count`, a cluster's first event and its `task_complete`, a `web_search_end` and its call,
telemetry and its output. Accepted deltas are explicit exemptions, each at field level, and
everything else stays exact:

- *Multi-block Claude assistant record.* Legacy emits one event from block 0; new emits one per
  block. Event 0 is identical to legacy's in id, payload and every metadata key, usage included.
  Events 1..n are new: block sibling ids, no `$usage`, the same `$lineNumber`, `$timestamp` and
  `$causedBy`.
- *Multi-result Claude user record.* Legacy emits the first `tool_result` only; new emits one per
  result. Result 0 is identical to legacy's; results 1..n are new on block sibling ids with the
  same metadata.
- *`extensions.claude_code.is_sidechain` and `origin_kind`.* Present only on the events section 3
  names; no other field of the slug changes; the server's unpacker ignores them.

Two read-model assertions accompany the first two deltas, because extra canonical events can
move a read model even when `$usage` does not: for an adapter-only session and for the hybrid
session below, `SessionStatsCalculator`'s token, per-model and cost totals equal the values the
legacy events give, and `ChatTurnBuilder` and `TraceTreeBuilder` render the extra blocks and
results as rows in line order with no turn split or reordering.

**Baselines.** Before the legacy normalizers are deleted the suite records their output as
golden files under `test/data/golden/<vendor>/v1/`, and that directory is immutable: it is the
compatibility baseline every later adapter must reproduce, up to declared deltas. A later change
that alters the adapter's output adds `v2/` beside it together with a machine-readable exemption
file naming every field-level difference between `v1` and `v2`; the suite asserts that the actual
diff between the two baselines equals that file, and compares the adapter against the newest
baseline. Re-recording alone therefore never satisfies the suite: an undeclared difference fails
it. The re-record switch exists to produce a candidate baseline, not to accept one.

**Ported tests.** The server's `ClaudeCodeNormalizerTests`, `ClaudeCodeFallbackIdTests`,
`CodexNormalizerTests` and `CodexUsageBackfillTests` are ported rule by rule: projection rules to
the leaf's tests, metadata and pipeline behaviour to adapter tests. Deleting a legacy normalizer
waits on that port.

**Hybrid session test.** A Claude session ingested up to a line under the legacy normalizer, then
continued under the adapter, and then re-imported in full: no duplicate ids, every legacy event
unchanged, the new block siblings present only for lines first ingested under the adapter, and
line order preserved.

**Pins.** A server test asserts the leaf's event type names equal the Eventuous type-map
registrations; the envelope wire-compat tests stay; the schema package version is asserted equal in
both repos; both AOT binaries publish with zero IL warnings after the package lands.

## 8. Delivery, immutable history, and the skew rule

The CLI repo leads and the server follows; each server PR pins a submodule commit already on the
CLI's main.

1. CLI: carve out the leaf with the extensions, the classifier and the projections on the new
   contract at today's chat coverage, Core's envelope adapter, the app rewired, the package
   pinned, AOT verified. No change in what the chat shows.
2. CLI: Claude to parity. 3. CLI: Codex to parity, with amendments and the two moved records. Each
   with the leaf's fixtures and golden files.
4. Server: submodule bump, the adapter registered for both keys, the parity suite green against
   the legacy normalizers, golden files recorded.
5. Server: delete both legacy normalizers and their context fields, port the remaining tests, the
   parity suite now against golden files.

**Immutable history.** A persisted event is never rewritten. A projection change takes effect
for lines the server first ingests after the deploy that carries it; a session ingested before is
never upgraded, because a re-delivered line yields the same ids and the dedup set keeps the stored
event, and this design provides no rebuild. A session that straddles a change is therefore a
hybrid and must be well-formed as one: no id may change meaning, a new event may only carry a new
id, and additive fields may appear on new events only. The accepted deltas satisfy this (block 0
keeps the record id; siblings are new ids appended in line order; the two fields ride new events),
and the hybrid session test pins it.

The coupling is compile-time only: the server builds the leaf at its pinned commit, so an
incompatible change fails the bump PR rather than a deploy. Three rules follow:

- **The CLI leads, the server follows in the same bump.** A leaf change never waits on the
  server; a server change that needs new leaf behaviour lands the leaf first.
- **Event id derivation is frozen per vendor.** The identifier table is the contract; a new
  scheme would append duplicates on the next re-import and needs a migration story, out of scope.
- **An output-changing projection change is read-model-visible.** It must satisfy the hybrid
  rule above; the CLI PR says so in `docs/CHANGES.md`, and the server bump adds a new baseline
  with its declared delta, never edits an old one.

The desktop app and the server may run different leaf commits between releases. The chat is a
live view and nothing from it is persisted, so that divergence is cosmetic. `release.sh` tags both
repos from one commit pair, so every release ships one leaf. The schema package version is pinned
identically in both repos and bumped in the CLI first; the equality test guards it, because NuGet
would otherwise float the server silently onto the leaf's newer version.

## 9. Risks

- Parity surprises in the real corpus. The suite's exemption list is the control: a delta is
  either accepted, listed at field level, or fixed.
- The Linux and Windows AOT legs. The probe ran on macOS only; the protobuf runtime is managed
  code, and CI publishes all three.
- `JsonElement` to `Struct` conversion. The leaf owns one, `Struct.Parser.ParseJson` over the raw
  text, which the probe shows works under AOT.
- A submodule bump drags every CLI change since the previous pin. Normal for this pair of repos,
  but step 4 is larger than its diff suggests when the pin has lagged.

## Follow-ups

Cursor, Copilot, Gemini, Kiro, OpenCode, Pi and Antigravity, one per PR, on the same adapter and
context shell. Two of them need a hook the shell does not have yet: Gemini's and Antigravity's
replay guards are driven by persistence, so their contexts will need a `MarkPersisted(Guid)` the
pipeline calls after a successful append, and Antigravity needs the step-order sort key the server's
`IStepOrderedNormalizer` provides today. AI-2426 adds a tool kind to the envelope and the
projections once this lands. A Codex usage target whose append failed loses its usage today and
under this design; recording it in the backfill event instead needs the read models to accept a
target that may never exist, which is its own change.
