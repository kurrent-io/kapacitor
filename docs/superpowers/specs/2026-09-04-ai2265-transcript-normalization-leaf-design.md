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
  `$lineNumber` for watermark recovery. An ingested session cannot be re-projected server-side;
  only a client re-import runs a projection again. The skew rule below follows from this.

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
   pipeline feedback loop (persisted cluster members) disappears: the adapter decides pending
   versus persisted from the batch it is writing.
4. **`JsonElementExtensions` moves to the leaf and becomes public.** Core, the app and the server
   read JSON through the same tolerant accessors.
5. **Stored events the envelope cannot express are shown as notes, not dropped.** `ContextCompacted`
   is persisted exactly as today and the Chat tab renders it as a `system_note` ("Context
   compacted"). The rule for any later such event: a note when it marks something a reader should
   see, skipped in the chat only when it does not.

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
    ProjectionResult Project(string line, int lineNumber, TranscriptContext context);
}

public abstract class TranscriptContext {
    public virtual void BeginBatch() { }      // clears state that has no meaning across batches
}

public sealed record ProjectionResult(
    IReadOnlyList<CanonicalEvent>  Events,
    IReadOnlyList<EventAmendment>  Amendments,
    string?                        Rejected = null);   // the line is not a JSON object; nothing else set

public sealed record CanonicalEvent(
    string          EventType,          // the schema's EventTypeMap name, or the leaf record's fixed name
    object          Payload,            // a schema message, or CodexUsageBackfilledEvent / ContextCompactedEvent / UsageApplied
    Guid            EventId,
    DateTimeOffset? Timestamp,
    string?         CausedBy            = null,
    TokenUsage?     Usage               = null,
    bool            UsageIsEcho         = false,
    long?           CacheCreationTokens = null,
    IReadOnlyList<TranscriptAttachment>? Attachments = null);

public sealed record EventAmendment(Guid TargetEventId, string Slug, Struct Extension);

public sealed record UsageApplied(
    TokenUsage Usage, long? ModelContextWindow, Guid AnchorEventId, IReadOnlyList<UsageTarget> Targets);
public sealed record UsageTarget(Guid EventId, string EventType, string? ToolName, bool IsEcho);

public sealed record TranscriptAttachment(Guid Id, string FileName, string ContentType, byte[] Data);

public static class TranscriptProjection {
    public static ITranscriptProjection? For(string vendor);   // ordinal-ignore-case; null for an unknown vendor
}
```

`UsageApplied` is a `CanonicalEvent` like any other, with `EventType = "UsageApplied"` and an id
derived from `AnchorEventId` the way the server derives the backfill id today, so a repeated
`token_count` collapses on the same id. It is never persisted verbatim; section 5 says what the
adapter does with it. Extension amendments carry a whole extension block for one slug; the
receiver merges it over whatever that slug already holds on the target.

Timestamps are the record's own, absent when the record has none; the caller fills a missing one
with receive time, as the server does today. Every JSON read goes through the public extensions, so
a wrong-typed field reads as absent. A line that is not a JSON object is `Rejected` with a short
reason and emits nothing; the server logs it with an excerpt as it does today, the app drops it.

## 3. Claude at parity

Keyed on the record's root `type`; timestamp from the root `timestamp`; `CausedBy` from
`parentUuid`. Every event's id is the record `uuid`; a record without one hashes line number and
line with XxHash128, unchanged. Noise, emitting nothing: `progress`, `system`,
`file-history-snapshot`, `queue-operation`, `pr-link`, `last-prompt`, `ai-title` (the pipeline's
title side-channel reads it), and any type the build does not know.

- `assistant`: one event per content block, in order. `text` → `AssistantTextGenerated`;
  `tool_use` → `AssistantToolCallsGenerated` with one `ToolCallInfo` (`call_id` = `id`,
  `tool_name` = `name`, `arguments` = `input` as a `Struct`; a non-object input is wrapped as
  `{"input": …}`); `thinking` → `AssistantThinkingGenerated` (`content`, `signature`,
  `encrypted = false`). Usage from `message.usage` rides on the line's first emitted event, and
  only when it differs from the previous assistant line's usage (the context remembers it and any
  non-assistant line resets it): `input_tokens`, `output_tokens`, `cache_read_input_tokens` as
  cached input, `model` from `message.model`, and `cache_creation_input_tokens` as
  `CacheCreationTokens`. Blocks after the first take sibling ids, XxHash128 of the record id and
  the block index.
- `user`, string content → `UserMessageReceived`. Array content: `text` blocks joined with `"\n"` →
  `UserMessageReceived`, dropped when the text opens with `<available-deferred-tools`; `image`
  blocks → `TranscriptAttachment`s on that message, id = XxHash128 of the context's id scope, the
  record id and the block index, unchanged; `tool_result` blocks → one `ToolResultReceived` each
  (`call_id` = `tool_use_id`; `result` = the string, the joined `text` blocks, or the raw JSON;
  `extensions.claude_code` = `tool_use_result`, `output_raw`, `is_error`). A root `isMeta` sets
  `extensions.claude_code.is_meta`.
- `attachment` whose `attachment.type` is `queued_command` → `UserMessageReceived` from the prompt,
  string or content array.

Two additive extension fields the chat needs and the server does not write today:
`extensions.claude_code.is_sidechain` on every event from a sidechain record, and
`extensions.claude_code.origin_kind` on a `UserMessageReceived` whose record carries `origin.kind`
(Claude Code's finished-background-task injection). The schema lists `is_sidechain` under the
`claude_code` slug and declares additions under it non-breaking.

## 4. Codex at parity

Keyed on the envelope `type`, then `payload.type`; timestamp from the envelope; no `CausedBy`.
Every event's id is XxHash128 of the raw line, unchanged; sibling ids are XxHash128 of a primary
id and a suffix (`result`, `usage-backfill`), unchanged.

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
  synthesized `ToolResultReceived` (`result` = the `action` JSON, timestamp one tick later, sibling
  id `result`). The call id comes from the preceding `web_search_end`; the context keeps the queue,
  the ever-enqueued set that stops a replayed end-event stashing a duplicate, and the sticky map
  from call event id to assigned call id, all across batches.
- `tool_search_call` → `AssistantToolCallsGenerated` (`tool_search`), no synthesized result.
- `function_call_output`, `custom_tool_call_output`, `tool_search_output` → `ToolResultReceived`
  (`call_id`, `result`). Exec and patch telemetry stashed earlier in the batch merges into
  `extensions.codex.exec` and `extensions.codex.patch`.
- `event_msg.exec_command_end` and `patch_apply_end` → an `EventAmendment` for the result already
  emitted in this batch, else a stash for the output still to come. `BeginBatch()` clears both.
- `event_msg.task_complete` → an `EventAmendment` with `extensions.codex.task` (`duration_ms`,
  `time_to_first_token_ms`) for the current cluster's first event.
- `event_msg.token_count` → one `UsageApplied`: net input (`input_tokens` minus cached), output,
  cached input, reasoning, `model` from the last turn context, `model_context_window` under
  `additional_counts`, anchored on the cluster's first event, one target per cluster member with
  `is_echo` false on the first non-thinking member and true elsewhere. The cluster is every
  reasoning, assistant text, agent message and tool call since the previous finalization; results
  and the synthesized web-search result are not members; the next assistant event after a
  `token_count` opens a new cluster.
- `compacted` → `ContextCompactedEvent` (`replacement_history`, `encrypted_content`).
- Dropped: `world_state`, `inter_agent_communication_metadata`, `sub_agent_activity`,
  `thread_settings_applied`, the `event_msg` form of `agent_message`, and `web_search_end` beyond
  its queue effect.

## 5. The server adapter

One `ProjectionNormalizer : ITranscriptNormalizer` in `Sessions/Canonical/`, registered for the
`claude` and `codex` keys in place of the two deleted classes; the other seven registrations are
untouched. `ITranscriptNormalizer` gains `string Vendor { get; }` so the pipeline can key the Claude
`ai-title` side-channel on the vendor instead of on a class it no longer has.

`NormalizerContext` gains two slots and loses every Claude and Codex field: the leaf context,
created from the session and agent ids the pipeline already sets, and a per-batch map from event id
to the `NormalizedEvent` the adapter has returned in this batch, cleared where
`ClearTransientBatchState()` runs today, which now also calls `BeginBatch()`.

Per line, under the context lock the pipeline already holds, the adapter:

- calls the projection; on `Rejected` throws the same malformed-line signal the pipeline logs with
  an excerpt today, so that path is unchanged;
- turns each `CanonicalEvent` into a `NormalizedEvent`: payload as is, `$lineNumber`, `$vendor`,
  `$timestamp` when present, `$causedBy` when present, `$usage` via `UsageMetadataHelper.Write`
  with the echo flag and the cache-creation count, attachments as they are; a missing payload
  timestamp is filled with receive time. The first event is `Normalize`'s return value and the
  rest go on `PendingEmissions`, so sequence and tick ordering are unchanged;
- applies each `EventAmendment` to the pending target by merging the extension block into that
  slug; a target that is not pending is dropped, which is today's per-batch stash semantics;
- on `UsageApplied`, stamps `$usage` on every pending target with its echo flag. If any target is
  not pending, the non-pending targets become a `CodexUsageBackfilledEvent` (its id the
  `UsageApplied` id, its buckets the `TokenUsage`) and every pending stamp is forced to echo, which
  is today's rule that the backfill event is the canonical carrier whenever one is emitted. When
  every target is pending nothing is persisted from the `UsageApplied` itself.

The pipeline's high-water mark, per-line atomicity, cross-line sort, dedup set, retry, attachment
blob storage, hosted-session source guard and derived side events are untouched.

## 6. Core and the desktop app

Core gains `TranscriptEnvelopes.ToEnvelope(CanonicalEvent)`, the one place that maps a stored
event to the chat vocabulary: `UserMessageReceived` → `user_message`, `AssistantTextGenerated` →
`assistant_text`, `AssistantThinkingGenerated` → `assistant_thinking`, one `tool_call` per
`ToolCallInfo`, `ToolResultReceived` → `tool_result` with the 4096-unit cap the chat projection
applied until now, which moves here because stored results are never capped,
`ContextCompactedEvent` → `system_note`. `UsageApplied`, `SessionStarted` and anything else map to
nothing. Two vendor display rules the projections carried for the chat move beside it, under
Core's `Harness/<Vendor>/`: Claude's wrapper stripping and its task-notification note (recognised
by `origin_kind`, sidechain events skipped by `is_sidechain`), and Codex's injected-prelude filter
for user text.

`ChatTabViewModel` creates one leaf context when its projection resolves, calls `Project` per
tailed line with the line number the tail already tracks, ignores amendments and rejected lines,
and renders the envelopes as before.

## 7. Testing and parity

**Leaf tests** at `test/Capacitor.Models.Transcripts.Tests.Unit/`, mirroring `Harness/<Vendor>/`.
The existing projection tests move there and grow to the full surface. Inputs are inline JSON for
single rules plus a synthetic fixture corpus checked into the repo with golden expected output per
fixture. The repo is public, so nothing from the server's captured sessions is copied; the
synthetic fixtures cover each rule that matters: multi-block Claude lines, tool results with image
blocks, task notifications, sidechain records, Codex clusters straddling a batch boundary,
web-search pairing across batches, exec and patch telemetry in both orders, `task_complete`,
compaction, subagent rollouts. A golden file holds each event as schema JSON plus id, timestamp,
caused-by, usage and attachment ids and sizes; a re-record switch regenerates them.

**Parity suite** in the server repo at `test/Capacitor.Server.Tests.Ingest/Parity/`, run against
the real corpus under `test/data`: the Claude sessions with their subagent files and the three Codex
rollouts. For each fixture it runs the legacy normalizer and the adapter over the same lines through
the same pipeline, once as a single batch and once in small batches so cross-batch state is
exercised, and compares event for event: type, id, payload JSON, metadata including usage,
attachments by id and size. Accepted deltas are explicit exemptions in the suite, so everything
else stays exact. The accepted deltas are the multi-block Claude rule and the two additive
`claude_code` fields. Before the legacy normalizers are deleted the suite records their output as
golden files under `test/data/golden/`; after deletion it compares the adapter against those.

**Ported tests.** The server's `ClaudeCodeNormalizerTests`, `ClaudeCodeFallbackIdTests`,
`CodexNormalizerTests` and `CodexUsageBackfillTests` are ported rule by rule: projection rules to
the leaf's tests, metadata and pipeline behaviour to adapter tests. Deleting a legacy normalizer
waits on that port.

**Pins.** A server test asserts the leaf's event type names equal the Eventuous type-map
registrations; the envelope wire-compat tests stay; the schema package version is asserted equal in
both repos; both AOT binaries publish with zero IL warnings after the package lands.

## 8. Delivery and the skew rule

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

The coupling is compile-time only: the server builds the leaf at its pinned commit, so an
incompatible change fails the bump PR rather than a deploy. Three rules follow:

- **The CLI leads, the server follows in the same bump.** A leaf change never waits on the
  server; a server change that needs new leaf behaviour lands the leaf first.
- **Event id derivation is frozen per vendor.** With no raw lines on the server, an ingested
  session cannot be re-projected there, and its dedup set is keyed by event id, so a new id scheme
  would append duplicates on the next re-import. Changing it needs a migration story and is out of
  scope.
- **An output-changing projection change is read-model-visible.** Existing sessions keep their
  old shape until a client re-import; the CLI PR says so in `docs/CHANGES.md`, and the server bump
  re-records the golden files.

The desktop app and the server may run different leaf commits between releases. The chat is a
live view and nothing from it is persisted, so that divergence is cosmetic. `release.sh` tags both
repos from one commit pair, so every release ships one leaf. The schema package version is pinned
identically in both repos and bumped in the CLI first; the equality test guards it, because NuGet
would otherwise float the server silently onto the leaf's newer version.

## 9. Risks

- Parity surprises in the real corpus. The suite's exemption list is the control: a delta is
  either accepted and listed or fixed.
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
projections once this lands.
