# Transcripts leaf carve-out (PR 1 of AI-2265) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Create `Capacitor.Models.Transcripts`, move the two chat projections and the JSON accessors into it on the canonical-event contract, and rewire Core and the desktop app so the Chat tab shows exactly what it shows today.

**Architecture:** A new leaf project with one job, transcript line in → canonical `Kurrent.Agent.Schema` events out, referenced by Core. Core gains an envelope adapter plus per-vendor chat display rules, so the Chat tab keeps rendering `AcpEventEnvelope`. The app resolves a `TranscriptChatProjection` per vendor and holds one projection context per tail. Nothing in this PR touches the server, the wire, or what the chat displays; ids, timestamps and the contract records land now so later PRs only add fields and rules.

**Tech Stack:** .NET 10, C# 14 (`extension` blocks), NativeAOT, `Kurrent.Agent.Schema` 0.4.1 (protobuf messages, `Google.Protobuf` 3.34.1), `System.IO.Hashing` 10.0.11, TUnit, Avalonia + ReactiveUI in the app.

**Spec:** `docs/superpowers/specs/2026-09-04-ai2265-transcript-normalization-leaf-design.md` — this plan implements step 1 of its section 8 ("carve out the leaf … No change in what the chat shows"). Steps 2–5 get their own plans.

## Global Constraints

- The leaf project is `src/Capacitor.Models.Transcripts/`, assembly and root namespace `Capacitor.Models.Transcripts`, `IsAotCompatible` and `IsTrimmable`, with exactly two package references: `Kurrent.Agent.Schema` and `System.IO.Hashing`. No project references.
- Package versions are pinned in `Directory.Packages.props` at the server's versions: `Kurrent.Agent.Schema` `0.4.1`, `System.IO.Hashing` `10.0.11`.
- Vendor code lives under `Harness/<Vendor>/` with the namespace following the directory (`Capacitor.Models.Transcripts.Harness.Claude`), one registration site per assembly: `TranscriptProjection.For` in the leaf, `TranscriptChat.For` in Core.
- Identifier derivations are persistence contracts (spec §2 "Identifiers"): `new Guid(XxHash128.Hash(bytes))`, `Guid.TryWriteBytes` layout for Guid inputs, UTF-8 without BOM for strings.
- Every JSON read goes through `JsonElementExtensions` (`Str`/`Num`/`Bool`/`Obj`/`Arr`/`Prop`/`IsObject`), which becomes public in the leaf.
- Core stays free of `Kurrent.Agent.Schema` construction: it reads payloads, never builds them.
- No reflection-based serialization anywhere in the leaf or Core; every JSON write is `Utf8JsonWriter` or a protobuf parser/formatter (the AOT probe covered those).
- Test layout: `test/Capacitor.Models.Transcripts.Tests.Unit/` mirrors the leaf's directories; it references its own prod project and `test/Capacitor.Tests.Helpers/` only.
- Comments: scarce, present-tense, no history or ticket narration (CLAUDE.md "Comments").
- Commit subjects: one imperative clause, at most 80 characters including the trailing `(#679)`.
- Both AOT binaries must publish with zero `IL2026`/`IL3050` warnings before the PR opens.

---

## File structure

**Leaf (new) — `src/Capacitor.Models.Transcripts/`**

| File | Responsibility |
|---|---|
| `Capacitor.Models.Transcripts.csproj` | AOT-compatible leaf, two package refs, `InternalsVisibleTo` its tests and Helpers |
| `JsonElementExtensions.cs` | moved from Core unchanged except namespace |
| `CanonicalEvent.cs` | `CanonicalEvent`, `ProjectionResult`, `EventAmendment`, `UsageApplied`, `UsageTarget`, `TranscriptAttachment` |
| `CanonicalEventTypes.cs` | the persisted type-name string for each payload type |
| `SchemaExtensions.cs` | read a schema message's `extensions` map and typed fields of one slug |
| `TranscriptContext.cs` | base class with `BeginBatch()` |
| `TranscriptProjection.cs` | `ITranscriptProjection` and the `For(vendor)` registry |
| `TranscriptIds.cs` | every id derivation |
| `TranscriptTime.cs` | record timestamp → effective timestamp and raw string |
| `TranscriptText.cs` | text-block joining and `Struct` construction helpers |
| `Harness/Claude/ClaudeCodeExtension.cs` | the `claude_code` slug: field names and a builder for the flags this PR writes |
| `Harness/Claude/ClaudeTranscriptEvents.cs` | Claude projection on the contract, chat-level coverage |
| `Harness/Codex/CodexRolloutEvents.cs` | Codex projection on the contract, chat-level coverage |
| `Harness/Codex/CodexCommandClassifier.cs` | moved from Core unchanged except namespace |

**Core — `src/Capacitor.Cli.Core/`**

| File | Responsibility |
|---|---|
| `TranscriptChat.cs` | `IChatDisplayRules`, `TranscriptChatProjection`, `TranscriptChat.For(vendor)` |
| `TranscriptEnvelopes.cs` | `CanonicalEvent` → `AcpEventEnvelope`s, the tool-result cap, compact JSON for `Struct` |
| `Harness/Claude/ClaudeChatRules.cs` | wrapper stripping, task-notification note, meta/sidechain skip, `is_error` |
| `Harness/Codex/CodexChatRules.cs` | injected-prelude skip |
| deleted: `TranscriptProjection.cs`, `Harness/Claude/ClaudeTranscriptEvents.cs`, `Harness/Codex/CodexRolloutEvents.cs`, `Harness/Codex/CodexCommandClassifier.cs`, `JsonElementExtensions.cs` | moved or replaced |

**App — `src/Capacitor.App/`**: `ViewModels/ChatTabViewModel.cs` (context per tail lease, line numbers, `TranscriptChatProjection`), `ViewModels/WorkspaceViewModel.cs:146` (resolve via `TranscriptChat.For`), `ViewModels/ToolSummary.cs` (using).

**Tests**: new `test/Capacitor.Models.Transcripts.Tests.Unit/` (`JsonElementExtensionsTests.cs` moved, `TranscriptIdsTests.cs`, `TranscriptTextTests.cs`, `Harness/Claude/ClaudeTranscriptEventsTests.cs`, `Harness/Codex/CodexRolloutEventsTests.cs`, `Harness/Codex/CodexCommandClassifierTests.cs` moved); Core tests gain `TranscriptEnvelopesTests.cs`, `Harness/Claude/ClaudeChatRulesTests.cs`, `Harness/Codex/CodexChatRulesTests.cs` and lose the old projection and classifier tests; `test/Capacitor.App.Tests.Unit/ChatTabViewModelTests.cs` adapts.

**Config/docs**: `Directory.Packages.props`, `Capacitor.slnx`, global `<Using>` in every project that reads JSON through the accessors, `CLAUDE.md`, `docs/CHANGES.md`.

---

### Task 1: The leaf project, the moved JSON accessors, and the solution wiring

**Files:**
- Create: `src/Capacitor.Models.Transcripts/Capacitor.Models.Transcripts.csproj`
- Create: `src/Capacitor.Models.Transcripts/JsonElementExtensions.cs` (moved from `src/Capacitor.Cli.Core/JsonElementExtensions.cs`)
- Create: `test/Capacitor.Models.Transcripts.Tests.Unit/Capacitor.Models.Transcripts.Tests.Unit.csproj`
- Create: `test/Capacitor.Models.Transcripts.Tests.Unit/JsonElementExtensionsTests.cs` (moved from `test/Capacitor.Cli.Core.Tests.Unit/JsonElementExtensionsTests.cs`)
- Modify: `Directory.Packages.props`, `Capacitor.slnx`, `src/Capacitor.Cli.Core/Capacitor.Cli.Core.csproj`, `src/Capacitor.Cli/Capacitor.Cli.csproj`, `src/Capacitor.Cli.Daemon/Capacitor.Cli.Daemon.csproj`, `src/Capacitor.App/Capacitor.App.csproj`, and the csproj of `test/Capacitor.App.Tests.Unit`, `test/Capacitor.Cli.Core.Tests.Unit`, `test/Capacitor.Cli.Daemon.Tests.Unit`, `test/Capacitor.Cli.Tests.Unit`, `test/Capacitor.Cli.Tests.Integration`
- Delete: `src/Capacitor.Cli.Core/JsonElementExtensions.cs`, `test/Capacitor.Cli.Core.Tests.Unit/JsonElementExtensionsTests.cs`

**Interfaces:**
- Produces: the `Capacitor.Models.Transcripts` assembly, referenced by Core; `JsonElementExtensions` in namespace `Capacitor.Models.Transcripts`, reachable everywhere through a global using.

- [ ] **Step 1: Pin the two packages**

In `Directory.Packages.props`, add inside the `<ItemGroup>` (alphabetical position does not matter, keep the existing style):

```xml
    <PackageVersion Include="Kurrent.Agent.Schema" Version="0.4.1" />
    <PackageVersion Include="System.IO.Hashing" Version="10.0.11" />
```

- [ ] **Step 2: Create the leaf project file**

`src/Capacitor.Models.Transcripts/Capacitor.Models.Transcripts.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
        <TargetFramework>net10.0</TargetFramework>
        <IsAotCompatible>true</IsAotCompatible>
        <IsTrimmable>true</IsTrimmable>
    </PropertyGroup>
    <ItemGroup>
        <InternalsVisibleTo Include="Capacitor.Models.Transcripts.Tests.Unit" />
        <InternalsVisibleTo Include="Capacitor.Tests.Helpers" />
    </ItemGroup>
    <ItemGroup>
        <PackageReference Include="Kurrent.Agent.Schema" />
        <PackageReference Include="System.IO.Hashing" />
    </ItemGroup>
</Project>
```

- [ ] **Step 3: Move the JSON accessors**

Move `src/Capacitor.Cli.Core/JsonElementExtensions.cs` to `src/Capacitor.Models.Transcripts/JsonElementExtensions.cs` (`git mv`). Change only the namespace line:

```csharp
namespace Capacitor.Models.Transcripts;
```

Keep the class `public static class JsonElementExtensions` and its `extension(JsonElement el)` block exactly as it is.

- [ ] **Step 4: Reference the leaf from Core and add the global using everywhere the accessors are read**

In `src/Capacitor.Cli.Core/Capacitor.Cli.Core.csproj` add, next to the existing `PackageReference` group:

```xml
    <ItemGroup>
        <ProjectReference Include="..\Capacitor.Models.Transcripts\Capacitor.Models.Transcripts.csproj" />
    </ItemGroup>
    <ItemGroup>
        <Using Include="Capacitor.Models.Transcripts" />
    </ItemGroup>
```

Add the same `<Using Include="Capacitor.Models.Transcripts" />` item (inside a new or existing `<ItemGroup>`) to: `src/Capacitor.Cli/Capacitor.Cli.csproj`, `src/Capacitor.Cli.Daemon/Capacitor.Cli.Daemon.csproj`, `src/Capacitor.App/Capacitor.App.csproj`, `test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj`, `test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj`, `test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj`, `test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj`, `test/Capacitor.Cli.Tests.Integration/Capacitor.Cli.Tests.Integration.csproj`. The test projects already carry `<Using Include="Capacitor.Tests.Helpers" />`; put the new item beside it. Cli, Daemon and App reach the leaf assembly transitively through Core; they need no `ProjectReference`.

- [ ] **Step 5: Create the leaf test project and move the accessor tests**

`test/Capacitor.Models.Transcripts.Tests.Unit/Capacitor.Models.Transcripts.Tests.Unit.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
        <TargetFramework>net10.0</TargetFramework>
        <OutputType>Exe</OutputType>
        <IsPackable>false</IsPackable>
    </PropertyGroup>
    <ItemGroup>
        <ProjectReference Include="..\Capacitor.Tests.Helpers\Capacitor.Tests.Helpers.csproj" />
        <ProjectReference Include="..\..\src\Capacitor.Models.Transcripts\Capacitor.Models.Transcripts.csproj" />
    </ItemGroup>
    <ItemGroup>
        <PackageReference Include="TUnit" />
    </ItemGroup>
    <ItemGroup>
        <Using Include="Capacitor.Tests.Helpers" />
        <Using Include="Capacitor.Models.Transcripts" />
    </ItemGroup>
</Project>
```

Move `test/Capacitor.Cli.Core.Tests.Unit/JsonElementExtensionsTests.cs` to `test/Capacitor.Models.Transcripts.Tests.Unit/JsonElementExtensionsTests.cs` and change its namespace to `Capacitor.Models.Transcripts.Tests.Unit`; drop any `using Capacitor.Cli.Core;` line it carries.

- [ ] **Step 6: Register both projects in the solution**

In `Capacitor.slnx`, add under `/src/` (keep the existing alphabetical order):

```xml
    <Project Path="src\Capacitor.Models.Transcripts\Capacitor.Models.Transcripts.csproj" />
```

and under `/test/`:

```xml
    <Project Path="test\Capacitor.Models.Transcripts.Tests.Unit\Capacitor.Models.Transcripts.Tests.Unit.csproj" />
```

- [ ] **Step 7: Build the whole solution**

Run: `dotnet build Capacitor.slnx`
Expected: succeeds with zero warnings. If any file reports `'JsonElement' does not contain a definition for 'Str'`, that file's project is missing the global using from Step 4; add it there rather than adding a per-file `using`.

- [ ] **Step 8: Run the moved tests and the Core suite**

Run: `dotnet run --project test/Capacitor.Models.Transcripts.Tests.Unit/Capacitor.Models.Transcripts.Tests.Unit.csproj`
Expected: the accessor tests pass.

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj`
Expected: green (the suite is unchanged apart from the moved file).

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "Add the Capacitor.Models.Transcripts leaf project (#679)"
```

---

### Task 2: Identifier derivations

**Files:**
- Create: `src/Capacitor.Models.Transcripts/TranscriptIds.cs`
- Test: `test/Capacitor.Models.Transcripts.Tests.Unit/TranscriptIdsTests.cs`

**Interfaces:**
- Produces: `public static class TranscriptIds` with `Guid Hash(ReadOnlySpan<byte>)`, `Guid Sibling(Guid primary, string suffix)`, `Guid ClaudeFallback(int lineNumber, string line)`, `Guid ClaudeBlock(Guid recordId, int blockIndex)`, `Guid ClaudeAttachment(string idScope, Guid recordId, int blockIndex)`, `Guid CodexRecord(string line)`.

- [ ] **Step 1: Write the framing tests**

`test/Capacitor.Models.Transcripts.Tests.Unit/TranscriptIdsTests.cs`:

```csharp
using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;

namespace Capacitor.Models.Transcripts.Tests.Unit;

/// Each derivation is a persistence contract: the server dedups by these ids, so the bytes hashed
/// are pinned here twice — once by framing (the exact byte layout) and once by fixed vectors.
public class TranscriptIdsTests {
    static readonly Guid Primary = Guid.Parse("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0");

    [Test]
    public async Task Sibling_hashes_the_primary_guid_bytes_then_the_utf8_suffix() {
        var expectedInput = new byte[16 + 6];
        Primary.TryWriteBytes(expectedInput);
        Encoding.UTF8.GetBytes("result").CopyTo(expectedInput, 16);

        await Assert.That(TranscriptIds.Sibling(Primary, "result")).IsEqualTo(new Guid(XxHash128.Hash(expectedInput)));
        await Assert.That(TranscriptIds.Sibling(Primary, "result")).IsNotEqualTo(TranscriptIds.Sibling(Primary, "usage-backfill"));
    }

    [Test]
    public async Task Claude_fallback_hashes_line_number_space_line() {
        var expected = new Guid(XxHash128.Hash(Encoding.UTF8.GetBytes("12 {\"type\":\"user\"}")));
        await Assert.That(TranscriptIds.ClaudeFallback(12, "{\"type\":\"user\"}")).IsEqualTo(expected);
        await Assert.That(TranscriptIds.ClaudeFallback(13, "{\"type\":\"user\"}")).IsNotEqualTo(expected);
    }

    [Test]
    public async Task Claude_block_is_the_sibling_with_a_block_suffix() {
        await Assert.That(TranscriptIds.ClaudeBlock(Primary, 2)).IsEqualTo(TranscriptIds.Sibling(Primary, "block:2"));
    }

    [Test]
    public async Task Claude_attachment_hashes_scope_then_record_guid_then_little_endian_index() {
        var scope = Encoding.UTF8.GetBytes("sess:agent");
        var expectedInput = new byte[scope.Length + 20];
        scope.CopyTo(expectedInput, 0);
        Primary.TryWriteBytes(expectedInput.AsSpan(scope.Length));
        BinaryPrimitives.WriteInt32LittleEndian(expectedInput.AsSpan(scope.Length + 16), 3);

        await Assert.That(TranscriptIds.ClaudeAttachment("sess:agent", Primary, 3)).IsEqualTo(new Guid(XxHash128.Hash(expectedInput)));
    }

    [Test]
    public async Task Codex_record_hashes_the_utf8_line() {
        const string line = "{\"type\":\"response_item\",\"payload\":{}}";
        await Assert.That(TranscriptIds.CodexRecord(line)).IsEqualTo(new Guid(XxHash128.Hash(Encoding.UTF8.GetBytes(line))));
    }

    /// Fixed vectors. Fill the expected literals once from the printer below, then delete the
    /// printer; a later change to any derivation fails here even if the framing test is edited too.
    [Test]
    [Arguments("sibling", "REPLACE-ME")]
    [Arguments("claude-fallback", "REPLACE-ME")]
    [Arguments("claude-block", "REPLACE-ME")]
    [Arguments("claude-attachment", "REPLACE-ME")]
    [Arguments("codex-record", "REPLACE-ME")]
    public async Task Vectors_are_fixed(string name, string expected) {
        await Assert.That(Vector(name).ToString("D")).IsEqualTo(expected);
    }

    internal static Guid Vector(string name) => name switch {
        "sibling"           => TranscriptIds.Sibling(Primary, "result"),
        "claude-fallback"   => TranscriptIds.ClaudeFallback(12, "{\"type\":\"user\"}"),
        "claude-block"      => TranscriptIds.ClaudeBlock(Primary, 2),
        "claude-attachment" => TranscriptIds.ClaudeAttachment("sess:agent", Primary, 3),
        "codex-record"      => TranscriptIds.CodexRecord("{\"type\":\"response_item\",\"payload\":{}}"),
        _                   => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Test]
    public void Print_vectors_once_then_delete_this_test() {
        foreach (var name in new[] { "sibling", "claude-fallback", "claude-block", "claude-attachment", "codex-record" })
            Console.WriteLine($"{name} = {Vector(name):D}");
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet run --project test/Capacitor.Models.Transcripts.Tests.Unit/Capacitor.Models.Transcripts.Tests.Unit.csproj --treenode-filter "/*/*/TranscriptIdsTests/*"`
Expected: build error `The name 'TranscriptIds' does not exist`.

- [ ] **Step 3: Implement the derivations**

`src/Capacitor.Models.Transcripts/TranscriptIds.cs`:

```csharp
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Hashing;
using System.Text;

namespace Capacitor.Models.Transcripts;

/// Every id a projection derives. The server's dedup set is keyed by these, so the bytes hashed
/// here are a persistence contract: a Guid contributes its own 16-byte layout, a string its UTF-8.
public static class TranscriptIds {
    public static Guid Hash(ReadOnlySpan<byte> bytes) => new(XxHash128.Hash(bytes));

    public static Guid Sibling(Guid primary, string suffix) {
        var suffixBytes = Encoding.UTF8.GetBytes(suffix);
        var input       = new byte[16 + suffixBytes.Length];
        primary.TryWriteBytes(input);
        suffixBytes.CopyTo(input, 16);
        return Hash(input);
    }

    public static Guid ClaudeFallback(int lineNumber, string line) =>
        Hash(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{lineNumber} {line}")));

    public static Guid ClaudeBlock(Guid recordId, int blockIndex) =>
        Sibling(recordId, string.Create(CultureInfo.InvariantCulture, $"block:{blockIndex}"));

    public static Guid ClaudeAttachment(string idScope, Guid recordId, int blockIndex) {
        var scopeBytes = Encoding.UTF8.GetBytes(idScope);
        var input      = new byte[scopeBytes.Length + 20];
        scopeBytes.CopyTo(input, 0);
        recordId.TryWriteBytes(input.AsSpan(scopeBytes.Length));
        BinaryPrimitives.WriteInt32LittleEndian(input.AsSpan(scopeBytes.Length + 16), blockIndex);
        return Hash(input);
    }

    public static Guid CodexRecord(string line) => Hash(Encoding.UTF8.GetBytes(line));
}
```

- [ ] **Step 4: Run the framing tests and the printer**

Run: `dotnet run --project test/Capacitor.Models.Transcripts.Tests.Unit/Capacitor.Models.Transcripts.Tests.Unit.csproj --treenode-filter "/*/*/TranscriptIdsTests/*"`
Expected: the four framing tests pass, `Vectors_are_fixed` fails five times on `REPLACE-ME`, and `Print_vectors_once_then_delete_this_test` prints five `name = guid` lines.

- [ ] **Step 5: Pin the vectors**

Copy each printed guid into the matching `[Arguments(...)]` literal of `Vectors_are_fixed`, then delete `Print_vectors_once_then_delete_this_test` and the `internal static Guid Vector` switch's `throw` stays. Re-run the filter above.
Expected: all `TranscriptIdsTests` pass.

- [ ] **Step 6: Commit**

```bash
git add src/Capacitor.Models.Transcripts/TranscriptIds.cs test/Capacitor.Models.Transcripts.Tests.Unit/TranscriptIdsTests.cs
git commit -m "Pin the transcript event id derivations (#679)"
```

---

### Task 3: The contract records, the registry, and the small helpers

**Files:**
- Create: `src/Capacitor.Models.Transcripts/CanonicalEvent.cs`, `CanonicalEventTypes.cs`, `SchemaExtensions.cs`, `TranscriptContext.cs`, `TranscriptProjection.cs`, `TranscriptTime.cs`, `TranscriptText.cs`
- Test: `test/Capacitor.Models.Transcripts.Tests.Unit/TranscriptTextTests.cs`, `TranscriptTimeTests.cs`, `SchemaExtensionsTests.cs`

**Interfaces:**
- Produces, all in namespace `Capacitor.Models.Transcripts`:
  - `public interface ITranscriptProjection { TranscriptContext CreateContext(string sessionId, string? agentId); ProjectionResult Project(string line, int lineNumber, DateTimeOffset receivedAt, TranscriptContext context); }`
  - `public abstract class TranscriptContext { public virtual void BeginBatch() { } }`
  - `public sealed record ProjectionResult(IReadOnlyList<CanonicalEvent> Events, IReadOnlyList<EventAmendment> Amendments, string? Rejected = null)` with `static ProjectionResult Empty`, `static ProjectionResult Of(IReadOnlyList<CanonicalEvent>)`, `static ProjectionResult Reject(string reason)`
  - `public sealed record CanonicalEvent(string EventType, object Payload, Guid EventId, DateTimeOffset Timestamp, string? RecordTimestamp = null, string? CausedBy = null, TokenUsage? Usage = null, bool UsageIsEcho = false, long? CacheCreationTokens = null, IReadOnlyList<TranscriptAttachment>? Attachments = null)`
  - `public sealed record EventAmendment(Guid TargetEventId, string Slug, Struct Extension)`, `public sealed record UsageApplied(TokenUsage Usage, Guid AnchorEventId, IReadOnlyList<UsageTarget> Targets)`, `public sealed record UsageTarget(Guid EventId, string EventType, string? ToolName, bool IsEcho)`, `public sealed record TranscriptAttachment(Guid Id, string FileName, string ContentType, byte[] Data)`
  - `public static class TranscriptProjection { public static ITranscriptProjection? For(string vendor); }` (registry filled in Tasks 5 and 6)
  - `public static class CanonicalEventTypes { const string UserMessageReceived, AssistantTextGenerated, AssistantThinkingGenerated, AssistantToolCallsGenerated, ToolResultReceived, SessionStarted, UsageApplied; static string Of(object payload); }`
  - `public static class SchemaExtensions { MapField<string, Struct>? Of(object payload); Struct? Slug(object payload, string slug); bool Flag(Struct? slug, string field); string? Text(Struct? slug, string field); }`
  - `public static class TranscriptTime { (DateTimeOffset At, string? Record) Resolve(string? raw, DateTimeOffset receivedAt); }`
  - `public static class TranscriptText { string JoinTextBlocks(JsonElement array, string blockType, string textProperty = "text"); Struct StructOf(JsonElement obj); Struct Wrap(string property, JsonElement value); Struct Wrap(string property, string value); }`

- [ ] **Step 1: Write the helper tests**

`test/Capacitor.Models.Transcripts.Tests.Unit/TranscriptTextTests.cs`:

```csharp
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;

namespace Capacitor.Models.Transcripts.Tests.Unit;

public class TranscriptTextTests {
    [Test]
    public async Task Text_blocks_of_the_named_type_join_with_newlines_and_others_are_skipped() {
        using var doc = JsonDocument.Parse("""[{"type":"text","text":"a"},{"type":"image"},{"type":"text","text":"b"}]""");
        await Assert.That(TranscriptText.JoinTextBlocks(doc.RootElement, "text")).IsEqualTo("a\nb");
        await Assert.That(TranscriptText.JoinTextBlocks(doc.RootElement, "input_text")).IsEqualTo("");
    }

    [Test]
    public async Task StructOf_keeps_field_order_and_nested_values() {
        using var doc = JsonDocument.Parse("""{"command":"ls","opts":{"all":true},"n":[1,2]}""");
        var s = TranscriptText.StructOf(doc.RootElement);
        await Assert.That(s.Fields.Keys.ToArray()).IsEquivalentTo(new[] { "command", "opts", "n" });
        await Assert.That(s.Fields["command"].StringValue).IsEqualTo("ls");
        await Assert.That(s.Fields["opts"].StructValue.Fields["all"].BoolValue).IsTrue();
        await Assert.That(s.Fields["n"].ListValue.Values.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Wrap_puts_a_non_object_value_or_a_string_under_one_property() {
        using var doc = JsonDocument.Parse("[1,2]");
        var wrapped = TranscriptText.Wrap("input", doc.RootElement);
        await Assert.That(wrapped.Fields["input"].KindCase).IsEqualTo(Value.KindOneofCase.ListValue);

        using var nul = JsonDocument.Parse("null");
        await Assert.That(TranscriptText.Wrap("input", nul.RootElement).Fields["input"].KindCase).IsEqualTo(Value.KindOneofCase.NullValue);

        await Assert.That(TranscriptText.Wrap("arguments", "not json").Fields["arguments"].StringValue).IsEqualTo("not json");
    }
}
```

`test/Capacitor.Models.Transcripts.Tests.Unit/TranscriptTimeTests.cs`:

```csharp
namespace Capacitor.Models.Transcripts.Tests.Unit;

public class TranscriptTimeTests {
    static readonly DateTimeOffset Received = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task A_parseable_record_timestamp_is_the_effective_time_and_is_kept_raw() {
        var (at, raw) = TranscriptTime.Resolve("2026-08-26T12:00:00Z", Received);
        await Assert.That(at).IsEqualTo(new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero));
        await Assert.That(raw).IsEqualTo("2026-08-26T12:00:00Z");
    }

    [Test]
    public async Task A_missing_timestamp_falls_back_to_the_receive_time_with_no_raw_string() {
        var (at, raw) = TranscriptTime.Resolve(null, Received);
        await Assert.That(at).IsEqualTo(Received);
        await Assert.That(raw).IsNull();
    }

    [Test]
    public async Task An_unparseable_timestamp_falls_back_but_keeps_the_raw_string() {
        var (at, raw) = TranscriptTime.Resolve("yesterday", Received);
        await Assert.That(at).IsEqualTo(Received);
        await Assert.That(raw).IsEqualTo("yesterday");
    }
}
```

`test/Capacitor.Models.Transcripts.Tests.Unit/SchemaExtensionsTests.cs`:

```csharp
using Google.Protobuf.WellKnownTypes;
using Kurrent.Agent.Schema.Events;

namespace Capacitor.Models.Transcripts.Tests.Unit;

public class SchemaExtensionsTests {
    [Test]
    public async Task Flags_and_text_read_from_one_slug_and_absent_reads_as_false_or_null() {
        var evt = new UserMessageReceived { Content = "x" };
        var slug = new Struct();
        slug.Fields["is_meta"] = Value.ForBool(true);
        slug.Fields["origin_kind"] = Value.ForString("task-notification");
        evt.Extensions["claude_code"] = slug;

        var read = SchemaExtensions.Slug(evt, "claude_code");
        await Assert.That(SchemaExtensions.Flag(read, "is_meta")).IsTrue();
        await Assert.That(SchemaExtensions.Flag(read, "is_sidechain")).IsFalse();
        await Assert.That(SchemaExtensions.Text(read, "origin_kind")).IsEqualTo("task-notification");
        await Assert.That(SchemaExtensions.Text(read, "is_meta")).IsNull();
        await Assert.That(SchemaExtensions.Slug(evt, "codex")).IsNull();
        await Assert.That(SchemaExtensions.Of(new object())).IsNull();
    }

    [Test]
    public async Task Event_type_names_are_the_persisted_names() {
        await Assert.That(CanonicalEventTypes.Of(new AssistantToolCallsGenerated())).IsEqualTo("AssistantToolCallsGenerated");
        await Assert.That(CanonicalEventTypes.Of(new ToolResultReceived())).IsEqualTo("ToolResultReceived");
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet run --project test/Capacitor.Models.Transcripts.Tests.Unit/Capacitor.Models.Transcripts.Tests.Unit.csproj`
Expected: build errors naming `TranscriptText`, `TranscriptTime`, `SchemaExtensions`, `CanonicalEventTypes`.

- [ ] **Step 3: Write the contract records**

`src/Capacitor.Models.Transcripts/CanonicalEvent.cs`:

```csharp
using Google.Protobuf.WellKnownTypes;
using Kurrent.Agent.Schema;

namespace Capacitor.Models.Transcripts;

/// One canonical event a projection derived from a transcript line. The payload is complete as
/// returned: a caller adds metadata around it, never fields inside it.
public sealed record CanonicalEvent(
        string          EventType,
        object          Payload,
        Guid            EventId,
        DateTimeOffset  Timestamp,
        string?         RecordTimestamp     = null,
        string?         CausedBy            = null,
        TokenUsage?     Usage               = null,
        bool            UsageIsEcho         = false,
        long?           CacheCreationTokens = null,
        IReadOnlyList<TranscriptAttachment>? Attachments = null
    );

/// A whole extension block for one slug, to shallow-merge over what the target already holds.
public sealed record EventAmendment(Guid TargetEventId, string Slug, Struct Extension);

/// Usage to stamp onto earlier events of the same response cluster; the caller decides which
/// targets it still holds. Never persisted as itself.
public sealed record UsageApplied(TokenUsage Usage, Guid AnchorEventId, IReadOnlyList<UsageTarget> Targets);

public sealed record UsageTarget(Guid EventId, string EventType, string? ToolName, bool IsEcho);

public sealed record TranscriptAttachment(Guid Id, string FileName, string ContentType, byte[] Data);

/// What one line projects to. Rejected is set for a line that is not a JSON object (or is
/// unusable in a way the vendor names); both lists are then empty and no context state moved.
public sealed record ProjectionResult(
        IReadOnlyList<CanonicalEvent> Events,
        IReadOnlyList<EventAmendment> Amendments,
        string?                       Rejected = null
    ) {
    public static readonly ProjectionResult Empty = new([], []);

    public static ProjectionResult Of(IReadOnlyList<CanonicalEvent> events) => events.Count == 0 ? Empty : new(events, []);

    public static ProjectionResult Reject(string reason) => new([], [], reason);
}
```

`src/Capacitor.Models.Transcripts/CanonicalEventTypes.cs`:

```csharp
using Kurrent.Agent.Schema.Events;

namespace Capacitor.Models.Transcripts;

/// The name each payload is persisted under. The server pins these against its type map.
public static class CanonicalEventTypes {
    public const string UserMessageReceived         = "UserMessageReceived";
    public const string AssistantTextGenerated      = "AssistantTextGenerated";
    public const string AssistantThinkingGenerated  = "AssistantThinkingGenerated";
    public const string AssistantToolCallsGenerated = "AssistantToolCallsGenerated";
    public const string ToolResultReceived          = "ToolResultReceived";
    public const string SessionStarted              = "SessionStarted";
    public const string UsageApplied                = "UsageApplied";

    public static string Of(object payload) => payload switch {
        Kurrent.Agent.Schema.Events.UserMessageReceived         => UserMessageReceived,
        Kurrent.Agent.Schema.Events.AssistantTextGenerated      => AssistantTextGenerated,
        Kurrent.Agent.Schema.Events.AssistantThinkingGenerated  => AssistantThinkingGenerated,
        Kurrent.Agent.Schema.Events.AssistantToolCallsGenerated => AssistantToolCallsGenerated,
        Kurrent.Agent.Schema.Events.ToolResultReceived          => ToolResultReceived,
        Kurrent.Agent.Schema.Events.SessionStarted              => SessionStarted,
        Transcripts.UsageApplied                                => UsageApplied,
        _ => throw new ArgumentException($"No canonical event type for {payload.GetType().Name}", nameof(payload)),
    };
}
```

`src/Capacitor.Models.Transcripts/SchemaExtensions.cs`:

```csharp
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;
using Kurrent.Agent.Schema.Events;

namespace Capacitor.Models.Transcripts;

/// Reads the extensions map the schema puts on every conversational message; the messages share
/// the property but no interface, so this is the one switch over their types.
public static class SchemaExtensions {
    public static MapField<string, Struct>? Of(object payload) => payload switch {
        UserMessageReceived m         => m.Extensions,
        AssistantTextGenerated m      => m.Extensions,
        AssistantThinkingGenerated m  => m.Extensions,
        AssistantToolCallsGenerated m => m.Extensions,
        ToolResultReceived m          => m.Extensions,
        SessionStarted m              => m.Extensions,
        _                             => null,
    };

    public static Struct? Slug(object payload, string slug) =>
        Of(payload) is { } extensions && extensions.TryGetValue(slug, out var block) ? block : null;

    public static bool Flag(Struct? slug, string field) =>
        slug is not null && slug.Fields.TryGetValue(field, out var v) && v.KindCase == Value.KindOneofCase.BoolValue && v.BoolValue;

    public static string? Text(Struct? slug, string field) =>
        slug is not null && slug.Fields.TryGetValue(field, out var v) && v.KindCase == Value.KindOneofCase.StringValue ? v.StringValue : null;
}
```

`src/Capacitor.Models.Transcripts/TranscriptContext.cs`:

```csharp
namespace Capacitor.Models.Transcripts;

/// Per-stream state a projection keeps between lines. The caller owns one per stream and calls
/// BeginBatch at every batch boundary; what a vendor clears there is the vendor's to define.
public abstract class TranscriptContext {
    public virtual void BeginBatch() { }
}
```

`src/Capacitor.Models.Transcripts/TranscriptProjection.cs`:

```csharp
namespace Capacitor.Models.Transcripts;

/// One transcript line in, canonical events out. Stateful only through the context the caller
/// passes; a projection never mutates an event it has returned.
public interface ITranscriptProjection {
    TranscriptContext CreateContext(string sessionId, string? agentId);
    ProjectionResult Project(string line, int lineNumber, DateTimeOffset receivedAt, TranscriptContext context);
}

/// The one registration site: a vendor's projection lives under Harness/&lt;Vendor&gt;/ and is named
/// here, nowhere else.
public static class TranscriptProjection {
    public static ITranscriptProjection? For(string vendor) => vendor.ToLowerInvariant() switch {
        _ => null,
    };
}
```

`src/Capacitor.Models.Transcripts/TranscriptTime.cs`:

```csharp
using System.Globalization;

namespace Capacitor.Models.Transcripts;

public static class TranscriptTime {
    /// The record's own instant when it parses, else the receive time; the raw string rides along
    /// whenever the record had one, parseable or not, because metadata keeps it verbatim.
    public static (DateTimeOffset At, string? Record) Resolve(string? raw, DateTimeOffset receivedAt) =>
        raw is not null && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? (parsed, raw)
            : (receivedAt, raw);
}
```

`src/Capacitor.Models.Transcripts/TranscriptText.cs`:

```csharp
using System.Text;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;

namespace Capacitor.Models.Transcripts;

public static class TranscriptText {
    public static string JoinTextBlocks(JsonElement array, string blockType, string textProperty = "text") {
        var sb = new StringBuilder();
        foreach (var block in array.EnumerateArray()) {
            if (block.Str("type") != blockType || block.Str(textProperty) is not { } text) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(text);
        }
        return sb.ToString();
    }

    public static Struct StructOf(JsonElement obj) => Struct.Parser.ParseJson(obj.GetRawText());

    public static Struct Wrap(string property, JsonElement value) {
        var s = new Struct();
        s.Fields[property] = Value.Parser.ParseJson(value.GetRawText());
        return s;
    }

    public static Struct Wrap(string property, string value) {
        var s = new Struct();
        s.Fields[property] = Value.ForString(value);
        return s;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Models.Transcripts.Tests.Unit/Capacitor.Models.Transcripts.Tests.Unit.csproj`
Expected: all pass. If `IsEquivalentTo` is not available on the string-array assertion in this TUnit version, compare `string.Join(",", s.Fields.Keys)` to `"command,opts,n"` instead.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Models.Transcripts test/Capacitor.Models.Transcripts.Tests.Unit
git commit -m "Define the transcript projection contract and helpers (#679)"
```

---

### Task 4: Move the Codex command classifier

**Files:**
- Move: `src/Capacitor.Cli.Core/Harness/Codex/CodexCommandClassifier.cs` → `src/Capacitor.Models.Transcripts/Harness/Codex/CodexCommandClassifier.cs`
- Move: `test/Capacitor.Cli.Core.Tests.Unit/Harness/Codex/CodexCommandClassifierTests.cs` → `test/Capacitor.Models.Transcripts.Tests.Unit/Harness/Codex/CodexCommandClassifierTests.cs`
- Modify: `src/Capacitor.App/ViewModels/ToolSummary.cs:1-4`

**Interfaces:**
- Produces: `Capacitor.Models.Transcripts.Harness.Codex.CodexCommandClassifier` and `CodexCommandHint`, API unchanged.

- [ ] **Step 1: Move both files with `git mv` and change their namespaces**

Classifier: `namespace Capacitor.Models.Transcripts.Harness.Codex;`. Test: `namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.Codex;` and its first line `using Capacitor.Cli.Core.Harness.Codex;` becomes `using Capacitor.Models.Transcripts.Harness.Codex;`.

- [ ] **Step 2: Fix the app's using**

In `src/Capacitor.App/ViewModels/ToolSummary.cs` replace `using Capacitor.Cli.Core.Harness.Codex;` with `using Capacitor.Models.Transcripts.Harness.Codex;`. Keep `using Capacitor.Cli.Core;`.

- [ ] **Step 3: Build and run the moved tests**

Run: `dotnet build Capacitor.slnx`
Expected: zero warnings.

Run: `dotnet run --project test/Capacitor.Models.Transcripts.Tests.Unit/Capacitor.Models.Transcripts.Tests.Unit.csproj --treenode-filter "/*/*/CodexCommandClassifierTests/*"`
Expected: all pass.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "Move the Codex command classifier into the transcripts leaf (#679)"
```

---

### Task 5: The Claude projection on the contract

**Files:**
- Create: `src/Capacitor.Models.Transcripts/Harness/Claude/ClaudeCodeExtension.cs`
- Create: `src/Capacitor.Models.Transcripts/Harness/Claude/ClaudeTranscriptEvents.cs`
- Modify: `src/Capacitor.Models.Transcripts/TranscriptProjection.cs` (registry arm)
- Test: `test/Capacitor.Models.Transcripts.Tests.Unit/Harness/Claude/ClaudeTranscriptEventsTests.cs`

**Interfaces:**
- Consumes: Task 2 `TranscriptIds`, Task 3 records and helpers.
- Produces: `ClaudeTranscriptEvents.Instance : ITranscriptProjection`; `ClaudeTranscriptContext : TranscriptContext` with `string IdScope`; `ClaudeCodeExtension.Slug = "claude_code"`, field names `IsMeta = "is_meta"`, `IsSidechain = "is_sidechain"`, `OriginKind = "origin_kind"`, `IsError = "is_error"`, and `static Struct? Flags(bool isSidechain, bool isMeta = false, string? originKind = null, bool isError = false)`.

Coverage in this PR is what the chat maps today: user text and tool results, assistant text, thinking and tool calls, plus the three flags the chat rules need. The record `uuid`, `parentUuid` and `timestamp` are carried per spec §3. Images, `queued_command` attachments, usage and the remaining extension fields come with PR 2.

- [ ] **Step 1: Write the failing tests**

`test/Capacitor.Models.Transcripts.Tests.Unit/Harness/Claude/ClaudeTranscriptEventsTests.cs`:

```csharp
using Capacitor.Models.Transcripts.Harness.Claude;
using Google.Protobuf.WellKnownTypes;
using Kurrent.Agent.Schema.Events;

namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.Claude;

public class ClaudeTranscriptEventsTests {
    static readonly DateTimeOffset Received = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);
    const string Uuid = "a1b2c3d4-0000-4000-8000-000000000001";

    static ProjectionResult P(string line, int lineNumber = 1) =>
        ClaudeTranscriptEvents.Instance.Project(line, lineNumber, Received, ClaudeTranscriptEvents.Instance.CreateContext("sess", null));

    static IReadOnlyList<CanonicalEvent> E(string line, int lineNumber = 1) => P(line, lineNumber).Events;

    [Test]
    public async Task String_user_content_is_one_user_message_on_the_record_id_with_its_timestamps() {
        var e = E($$$"""{"type":"user","uuid":"{{{Uuid}}}","parentUuid":"p1","message":{"role":"user","content":"hello"},"timestamp":"2026-08-26T12:00:00Z"}""");
        await Assert.That(e).Count().IsEqualTo(1);
        await Assert.That(e[0].EventType).IsEqualTo(CanonicalEventTypes.UserMessageReceived);
        await Assert.That(((UserMessageReceived)e[0].Payload).Content).IsEqualTo("hello");
        await Assert.That(e[0].EventId).IsEqualTo(Guid.Parse(Uuid));
        await Assert.That(e[0].CausedBy).IsEqualTo("p1");
        await Assert.That(e[0].RecordTimestamp).IsEqualTo("2026-08-26T12:00:00Z");
        await Assert.That(e[0].Timestamp).IsEqualTo(new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero));
        await Assert.That(((UserMessageReceived)e[0].Payload).Timestamp.ToDateTimeOffset()).IsEqualTo(e[0].Timestamp);
    }

    [Test]
    public async Task A_record_without_uuid_gets_the_fallback_id_and_no_timestamp_uses_the_receive_time() {
        const string line = """{"type":"user","message":{"content":"x"}}""";
        var e = E(line, 7);
        await Assert.That(e[0].EventId).IsEqualTo(TranscriptIds.ClaudeFallback(7, line));
        await Assert.That(e[0].Timestamp).IsEqualTo(Received);
        await Assert.That(e[0].RecordTimestamp).IsNull();
    }

    [Test]
    public async Task Meta_sidechain_and_origin_ride_the_claude_code_extension_instead_of_being_dropped() {
        var meta = E("""{"type":"user","isMeta":true,"message":{"content":"x"}}""");
        await Assert.That(SchemaExtensions.Flag(SchemaExtensions.Slug(meta[0].Payload, "claude_code"), "is_meta")).IsTrue();

        var side = E("""{"type":"assistant","isSidechain":true,"message":{"content":[{"type":"text","text":"x"}]}}""");
        await Assert.That(SchemaExtensions.Flag(SchemaExtensions.Slug(side[0].Payload, "claude_code"), "is_sidechain")).IsTrue();

        var task = E("""{"type":"user","origin":{"kind":"task-notification"},"message":{"content":"<task-notification><summary>done</summary></task-notification>"}}""");
        await Assert.That(SchemaExtensions.Text(SchemaExtensions.Slug(task[0].Payload, "claude_code"), "origin_kind")).IsEqualTo("task-notification");
        await Assert.That(((UserMessageReceived)task[0].Payload).Content).Contains("<summary>done</summary>");

        var plain = E("""{"type":"user","message":{"content":"x"}}""");
        await Assert.That(SchemaExtensions.Slug(plain[0].Payload, "claude_code")).IsNull();
    }

    [Test]
    public async Task User_text_blocks_join_and_wrappers_are_kept_verbatim() {
        var e = E("""{"type":"user","message":{"content":[{"type":"text","text":"<system-reminder>noise</system-reminder>real"},{"type":"text","text":"more"}]}}""");
        await Assert.That(e).Count().IsEqualTo(1);
        await Assert.That(((UserMessageReceived)e[0].Payload).Content).IsEqualTo("<system-reminder>noise</system-reminder>real\nmore");
    }

    [Test]
    public async Task Tool_results_come_one_per_block_with_text_and_error_flag_and_nothing_else_from_the_record() {
        var e = E($$$"""{"type":"user","uuid":"{{{Uuid}}}","message":{"content":[{"type":"text","text":"ignored"},{"type":"tool_result","tool_use_id":"t1","content":"done","is_error":true},{"type":"tool_result","tool_use_id":"t2","content":[{"type":"text","text":"a"},{"type":"text","text":"b"}]}]}}""");
        await Assert.That(e).Count().IsEqualTo(2);
        var first = (ToolResultReceived)e[0].Payload;
        await Assert.That(first.CallId).IsEqualTo("t1");
        await Assert.That(first.Result).IsEqualTo("done");
        await Assert.That(SchemaExtensions.Flag(SchemaExtensions.Slug(first, "claude_code"), "is_error")).IsTrue();
        await Assert.That(e[0].EventId).IsEqualTo(Guid.Parse(Uuid));

        var second = (ToolResultReceived)e[1].Payload;
        await Assert.That(second.Result).IsEqualTo("a\nb");
        await Assert.That(SchemaExtensions.Slug(second, "claude_code")).IsNull();
        await Assert.That(e[1].EventId).IsEqualTo(TranscriptIds.ClaudeBlock(Guid.Parse(Uuid), 2));
    }

    [Test]
    public async Task A_tool_result_with_non_text_blocks_keeps_the_raw_json() {
        var e = E("""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t","content":[{"type":"image","source":{}}]}]}}""");
        await Assert.That(((ToolResultReceived)e[0].Payload).Result).IsEqualTo("""[{"type":"image","source":{}}]""");
    }

    [Test]
    public async Task Assistant_blocks_map_in_order_with_the_record_id_first_and_block_siblings_after() {
        var line = $$$"""{"type":"assistant","uuid":"{{{Uuid}}}","timestamp":"2026-08-26T12:00:01Z","message":{"model":"claude-fable-5","content":[{"type":"thinking","thinking":"hmm","signature":"sig"},{"type":"text","text":"Hi"},{"type":"tool_use","id":"toolu_1","name":"Bash","input":{"command":"ls"}}]}}""";
        var e = E(line);

        await Assert.That(e).Count().IsEqualTo(3);
        var thinking = (AssistantThinkingGenerated)e[0].Payload;
        await Assert.That(thinking.Content).IsEqualTo("hmm");
        await Assert.That(thinking.Signature).IsEqualTo("sig");
        await Assert.That(thinking.Encrypted).IsFalse();
        await Assert.That(e[0].EventId).IsEqualTo(Guid.Parse(Uuid));

        await Assert.That(((AssistantTextGenerated)e[1].Payload).Content).IsEqualTo("Hi");
        await Assert.That(e[1].EventId).IsEqualTo(TranscriptIds.ClaudeBlock(Guid.Parse(Uuid), 1));

        var call = ((AssistantToolCallsGenerated)e[2].Payload).ToolCalls[0];
        await Assert.That(call.CallId).IsEqualTo("toolu_1");
        await Assert.That(call.ToolName).IsEqualTo("Bash");
        await Assert.That(call.Arguments.Fields["command"].StringValue).IsEqualTo("ls");
        await Assert.That(e[2].EventId).IsEqualTo(TranscriptIds.ClaudeBlock(Guid.Parse(Uuid), 2));
        await Assert.That(e[2].RecordTimestamp).IsEqualTo("2026-08-26T12:00:01Z");
    }

    [Test]
    public async Task Non_object_and_absent_tool_inputs_are_wrapped_into_an_object() {
        foreach (var (input, kind) in new[] { ("[1,2]", Value.KindOneofCase.ListValue), ("\"s\"", Value.KindOneofCase.StringValue), ("null", Value.KindOneofCase.NullValue) }) {
            var e = E($$$"""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t","name":"X","input":{{{input}}}}]}}""");
            var args = ((AssistantToolCallsGenerated)e[0].Payload).ToolCalls[0].Arguments;
            await Assert.That(args.Fields["input"].KindCase).IsEqualTo(kind).Because(input);
        }
        var absent = E("""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t","name":"X"}]}}""");
        await Assert.That(((AssistantToolCallsGenerated)absent[0].Payload).ToolCalls[0].Arguments.Fields.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Empty_thinking_stays_a_thinking_event_with_empty_content() {
        var e = E("""{"type":"assistant","message":{"content":[{"type":"thinking","thinking":"","signature":"abc"}]}}""");
        await Assert.That(((AssistantThinkingGenerated)e[0].Payload).Content).IsEqualTo("");
    }

    [Test]
    public async Task Deferred_tools_injection_and_empty_text_emit_nothing() {
        await Assert.That(E("""{"type":"user","message":{"content":"<available-deferred-tools>x"}}""")).IsEmpty();
        await Assert.That(E("""{"type":"user","message":{"content":[{"type":"text","text":"  <available-deferred-tools>x"}]}}""")).IsEmpty();
        await Assert.That(E("""{"type":"assistant","message":{"content":[{"type":"text","text":""}]}}""")).IsEmpty();
    }

    [Test]
    public async Task Every_other_record_type_is_ignored_not_rejected() {
        foreach (var type in new[] { "attachment", "summary", "system", "file-history-snapshot", "file-history-delta", "mode", "permission-mode", "last-prompt", "ai-title", "atis-latch", "worktree-state", "queue-operation", "progress", "unknown-future" }) {
            var r = P($$$"""{"type":"{{{type}}}","message":{"content":"x"}}""");
            await Assert.That(r.Events).IsEmpty().Because(type);
            await Assert.That(r.Rejected).IsNull().Because(type);
        }
        await Assert.That(P("""{"type":"user","message":{"content":42}}""").Events).IsEmpty();
        await Assert.That(P("""{"type":"assistant","message":{"content":[{"type":"text","text":7}]}}""").Events).IsEmpty();
    }

    [Test]
    public async Task Malformed_lines_are_rejected_with_a_reason_and_emit_nothing() {
        foreach (var line in new[] { "", "   ", "not json", "[1,2]", "\"s\"", $$$"""{"type":"user","uuid":"not-a-guid","message":{"content":"x"}}""" }) {
            var r = P(line);
            await Assert.That(r.Rejected).IsNotNull().Because(line);
            await Assert.That(r.Events).IsEmpty().Because(line);
        }
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet run --project test/Capacitor.Models.Transcripts.Tests.Unit/Capacitor.Models.Transcripts.Tests.Unit.csproj --treenode-filter "/*/*/ClaudeTranscriptEventsTests/*"`
Expected: build error, `ClaudeTranscriptEvents` does not exist in `Capacitor.Models.Transcripts.Harness.Claude`.

- [ ] **Step 3: Write the extension helper**

`src/Capacitor.Models.Transcripts/Harness/Claude/ClaudeCodeExtension.cs`:

```csharp
using Google.Protobuf.WellKnownTypes;

namespace Capacitor.Models.Transcripts.Harness.Claude;

/// The claude_code extension slug: the coding-agent fields the schema keeps out of the canonical
/// payloads. Only the fields this projection writes are named here.
public static class ClaudeCodeExtension {
    public const string Slug        = "claude_code";
    public const string IsMeta      = "is_meta";
    public const string IsSidechain = "is_sidechain";
    public const string OriginKind  = "origin_kind";
    public const string IsError     = "is_error";

    /// The block for one event, or null when nothing is set so the slug stays absent.
    public static Struct? Flags(bool isSidechain, bool isMeta = false, string? originKind = null, bool isError = false) {
        if (!isSidechain && !isMeta && originKind is null && !isError) return null;
        var s = new Struct();
        if (isSidechain) s.Fields[IsSidechain] = Value.ForBool(true);
        if (isMeta) s.Fields[IsMeta]           = Value.ForBool(true);
        if (originKind is not null) s.Fields[OriginKind] = Value.ForString(originKind);
        if (isError) s.Fields[IsError]         = Value.ForBool(true);
        return s;
    }
}
```

- [ ] **Step 4: Write the projection**

`src/Capacitor.Models.Transcripts/Harness/Claude/ClaudeTranscriptEvents.cs`:

```csharp
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kurrent.Agent.Schema.Events;
using static Capacitor.Models.Transcripts.TranscriptText;

namespace Capacitor.Models.Transcripts.Harness.Claude;

public sealed class ClaudeTranscriptContext(string idScope) : TranscriptContext {
    /// "{session}:{agent}" for a subagent stream, the bare session id otherwise; attachment ids
    /// hash it.
    public string IdScope { get; } = idScope;
}

/// Claude Code's project transcript (`~/.claude/projects/&lt;slug&gt;/&lt;session&gt;.jsonl`): one JSON
/// record per line, `type` at the root, the API message under `message`.
public sealed class ClaudeTranscriptEvents : ITranscriptProjection {
    public static readonly ClaudeTranscriptEvents Instance = new();

    ClaudeTranscriptEvents() { }

    public TranscriptContext CreateContext(string sessionId, string? agentId) =>
        new ClaudeTranscriptContext(agentId is null ? sessionId : $"{sessionId}:{agentId}");

    public ProjectionResult Project(string line, int lineNumber, DateTimeOffset receivedAt, TranscriptContext context) {
        if (string.IsNullOrWhiteSpace(line)) return ProjectionResult.Reject("empty line");
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); } catch (JsonException ex) { return ProjectionResult.Reject($"not JSON: {ex.Message}"); }

        using (doc) {
            var root = doc.RootElement;
            if (!root.IsObject) return ProjectionResult.Reject("not a JSON object");

            Guid recordId;
            if (root.Str("uuid") is { } uuid) {
                if (!Guid.TryParse(uuid, out recordId)) return ProjectionResult.Reject("uuid is not a GUID");
            } else {
                recordId = TranscriptIds.ClaudeFallback(lineNumber, line);
            }

            var (at, recordTimestamp) = TranscriptTime.Resolve(root.Str("timestamp"), receivedAt);
            var record = new Record(recordId, at, recordTimestamp, root.Str("parentUuid"), root.Bool("isSidechain") == true);

            IReadOnlyList<CanonicalEvent> events = root.Str("type") switch {
                "user"      => ProjectUser(root, record),
                "assistant" => ProjectAssistant(root, record),
                _           => [],
            };
            return ProjectionResult.Of(events);
        }
    }

    readonly record struct Record(Guid Id, DateTimeOffset At, string? RecordTimestamp, string? CausedBy, bool IsSidechain) {
        public Timestamp ProtoTimestamp => Timestamp.FromDateTimeOffset(At);
    }

    /// Assigns ids in emission order: the record id to the first event, a block sibling id to each
    /// later one, keyed by the raw index of the block that produced it.
    sealed class Emitter(Record record) {
        readonly List<CanonicalEvent> _events = [];

        public IReadOnlyList<CanonicalEvent> Events => _events;

        public void Add(int blockIndex, IMessage payload, Struct? claudeCode) {
            if (claudeCode is not null) SchemaExtensions.Of(payload)![ClaudeCodeExtension.Slug] = claudeCode;
            var id = _events.Count == 0 ? record.Id : TranscriptIds.ClaudeBlock(record.Id, blockIndex);
            _events.Add(new CanonicalEvent(CanonicalEventTypes.Of(payload), payload, id, record.At, record.RecordTimestamp, record.CausedBy));
        }
    }

    static IReadOnlyList<CanonicalEvent> ProjectUser(JsonElement root, Record record) {
        if (root.Obj("message") is not { } message) return [];
        var emitter    = new Emitter(record);
        var isMeta     = root.Bool("isMeta") == true;
        var originKind = root.Obj("origin")?.Str("kind");

        if (message.Str("content") is { } text) {
            if (!IsDeferredToolsInjection(text))
                emitter.Add(0, UserMessage(text, record), ClaudeCodeExtension.Flags(record.IsSidechain, isMeta, originKind));
            return emitter.Events;
        }
        if (message.Arr("content") is not { } blocks) return [];

        var texts = new List<string>();
        var index = 0;
        var sawResult = false;
        foreach (var block in blocks.EnumerateArray()) {
            switch (block.Str("type")) {
                case "tool_result":
                    sawResult = true;
                    emitter.Add(index, ToolResult(block, record), ClaudeCodeExtension.Flags(record.IsSidechain, isError: block.Bool("is_error") == true));
                    break;
                case "text":
                    if (block.Str("text") is { } t) texts.Add(t);
                    break;
            }
            index++;
        }
        // Text and image blocks beside tool results are dropped: the results are the record.
        if (sawResult || texts.Count == 0) return emitter.Events;

        var joined = string.Join("\n", texts);
        if (IsDeferredToolsInjection(joined)) return [];
        emitter.Add(0, UserMessage(joined, record), ClaudeCodeExtension.Flags(record.IsSidechain, isMeta, originKind));
        return emitter.Events;
    }

    static UserMessageReceived UserMessage(string text, Record record) =>
        new() { Content = text, Timestamp = record.ProtoTimestamp };

    static ToolResultReceived ToolResult(JsonElement block, Record record) {
        var evt = new ToolResultReceived { CallId = block.Str("tool_use_id") ?? "", Timestamp = record.ProtoTimestamp };
        if (ResultText(block) is { } result) evt.Result = result;
        return evt;
    }

    // A string result as is; an array by its text blocks, or verbatim when it has none.
    static string? ResultText(JsonElement block) {
        if (block.Str("content") is { } text) return text;
        if (block.Arr("content") is not { } blocks) return null;
        var joined = JoinTextBlocks(blocks, "text");
        return joined.Length > 0 || HasTextBlock(blocks) ? joined : blocks.GetRawText();
    }

    static bool HasTextBlock(JsonElement blocks) {
        foreach (var block in blocks.EnumerateArray()) if (block.Str("type") == "text") return true;
        return false;
    }

    static bool IsDeferredToolsInjection(string text) => text.AsSpan().TrimStart().StartsWith("<available-deferred-tools");

    static IReadOnlyList<CanonicalEvent> ProjectAssistant(JsonElement root, Record record) {
        if (root.Obj("message") is not { } message || message.Arr("content") is not { } blocks) return [];
        var emitter = new Emitter(record);
        var index   = 0;
        // Flags are built per block: two messages must never share one Struct instance.
        foreach (var block in blocks.EnumerateArray()) {
            switch (block.Str("type")) {
                case "text":
                    if (block.Str("text") is { Length: > 0 } text)
                        emitter.Add(index, new AssistantTextGenerated { Content = text, Timestamp = record.ProtoTimestamp }, ClaudeCodeExtension.Flags(record.IsSidechain));
                    break;
                case "thinking": {
                    var thinking = new AssistantThinkingGenerated { Content = block.Str("thinking") ?? "", Encrypted = false, Timestamp = record.ProtoTimestamp };
                    if (block.Str("signature") is { } signature) thinking.Signature = signature;
                    emitter.Add(index, thinking, ClaudeCodeExtension.Flags(record.IsSidechain));
                    break;
                }
                case "tool_use": {
                    var call = new AssistantToolCallsGenerated { Timestamp = record.ProtoTimestamp };
                    call.ToolCalls.Add(new ToolCallInfo { CallId = block.Str("id") ?? "", ToolName = block.Str("name") ?? "", Arguments = ToolInput(block) });
                    emitter.Add(index, call, ClaudeCodeExtension.Flags(record.IsSidechain));
                    break;
                }
            }
            index++;
        }
        return emitter.Events;
    }

    // Arguments is always an object: a non-object input is wrapped, an absent one is empty.
    static Struct ToolInput(JsonElement block) =>
        block.Obj("input") is { } obj ? StructOf(obj)
        : block.Prop("input") is { } value ? Wrap("input", value)
        : new Struct();
}
```

- [ ] **Step 5: Register the vendor**

In `src/Capacitor.Models.Transcripts/TranscriptProjection.cs`, add the using `using Capacitor.Models.Transcripts.Harness.Claude;` and the arm:

```csharp
        "claude" => ClaudeTranscriptEvents.Instance,
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Models.Transcripts.Tests.Unit/Capacitor.Models.Transcripts.Tests.Unit.csproj --treenode-filter "/*/*/ClaudeTranscriptEventsTests/*"`
Expected: all pass.

- [ ] **Step 7: Commit**

```bash
git add src/Capacitor.Models.Transcripts test/Capacitor.Models.Transcripts.Tests.Unit
git commit -m "Project Claude transcript records to canonical events (#679)"
```

---

### Task 6: The Codex projection on the contract

**Files:**
- Create: `src/Capacitor.Models.Transcripts/Harness/Codex/CodexRolloutEvents.cs`
- Modify: `src/Capacitor.Models.Transcripts/TranscriptProjection.cs` (registry arm)
- Test: `test/Capacitor.Models.Transcripts.Tests.Unit/Harness/Codex/CodexRolloutEventsTests.cs`

**Interfaces:**
- Produces: `CodexRolloutEvents.Instance : ITranscriptProjection`, `CodexRolloutContext : TranscriptContext` (empty in this PR; PR 3 fills it).

- [ ] **Step 1: Write the failing tests**

`test/Capacitor.Models.Transcripts.Tests.Unit/Harness/Codex/CodexRolloutEventsTests.cs`:

```csharp
using Capacitor.Models.Transcripts.Harness.Codex;
using Kurrent.Agent.Schema.Events;

namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.Codex;

public class CodexRolloutEventsTests {
    static readonly DateTimeOffset Received = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);

    static ProjectionResult P(string line) =>
        CodexRolloutEvents.Instance.Project(line, 1, Received, CodexRolloutEvents.Instance.CreateContext("sess", null));

    static IReadOnlyList<CanonicalEvent> E(string line) => P(line).Events;

    static string Item(string payload, string ts = "2026-08-25T00:00:00Z") =>
        $$$"""{"timestamp":"{{{ts}}}","ordinal":1,"type":"response_item","payload":{{{payload}}}}""";

    [Test]
    public async Task User_and_assistant_messages_join_their_text_blocks_on_the_line_hash_id() {
        var line = Item("""{"type":"message","role":"user","content":[{"type":"input_text","text":"a"},{"type":"input_text","text":"b"}]}""");
        var user = E(line);
        await Assert.That(user).Count().IsEqualTo(1);
        await Assert.That(((UserMessageReceived)user[0].Payload).Content).IsEqualTo("a\nb");
        await Assert.That(user[0].EventId).IsEqualTo(TranscriptIds.CodexRecord(line));
        await Assert.That(user[0].RecordTimestamp).IsEqualTo("2026-08-25T00:00:00Z");
        await Assert.That(user[0].CausedBy).IsNull();

        var assistant = E(Item("""{"type":"message","role":"assistant","content":[{"type":"output_text","text":"Hi"}]}"""));
        await Assert.That(((AssistantTextGenerated)assistant[0].Payload).Content).IsEqualTo("Hi");
    }

    [Test]
    public async Task Injected_preludes_are_kept_here_and_developer_and_system_roles_are_skipped() {
        var prelude = E(Item("""{"type":"message","role":"user","content":[{"type":"input_text","text":"<environment_context>\nstuff"}]}"""));
        await Assert.That(prelude).Count().IsEqualTo(1);

        await Assert.That(E(Item("""{"type":"message","role":"developer","content":[{"type":"input_text","text":"x"}]}"""))).IsEmpty();
        await Assert.That(E(Item("""{"type":"message","role":"system","content":[{"type":"input_text","text":"x"}]}"""))).IsEmpty();
        await Assert.That(E(Item("""{"type":"message","role":"assistant","content":[]}"""))).IsEmpty();
    }

    [Test]
    public async Task Tool_calls_always_carry_a_struct_argument() {
        var fn = E(Item("""{"type":"function_call","name":"spawn_agent","call_id":"c1","arguments":"{\"task\":\"t\"}"}"""));
        var call = ((AssistantToolCallsGenerated)fn[0].Payload).ToolCalls[0];
        await Assert.That(call.CallId).IsEqualTo("c1");
        await Assert.That(call.ToolName).IsEqualTo("spawn_agent");
        await Assert.That(call.Arguments.Fields["task"].StringValue).IsEqualTo("t");

        var nonObject = E(Item("""{"type":"function_call","name":"f","call_id":"c2","arguments":"not json"}"""));
        await Assert.That(((AssistantToolCallsGenerated)nonObject[0].Payload).ToolCalls[0].Arguments.Fields["arguments"].StringValue).IsEqualTo("not json");

        var custom = E(Item("""{"type":"custom_tool_call","name":"exec","call_id":"c3","input":"const r = 1;"}"""));
        var customCall = ((AssistantToolCallsGenerated)custom[0].Payload).ToolCalls[0];
        await Assert.That(customCall.ToolName).IsEqualTo("exec");
        await Assert.That(customCall.Arguments.Fields["input"].StringValue).IsEqualTo("const r = 1;");
    }

    [Test]
    public async Task Tool_outputs_carry_string_or_block_output_uncapped() {
        var str = E(Item("""{"type":"function_call_output","call_id":"c1","output":"{\"ok\":true}"}"""));
        var result = (ToolResultReceived)str[0].Payload;
        await Assert.That(result.CallId).IsEqualTo("c1");
        await Assert.That(result.Result).IsEqualTo("""{"ok":true}""");

        var blocks = E(Item("""{"type":"custom_tool_call_output","call_id":"c3","output":[{"type":"input_text","text":"Script completed"},{"type":"input_text","text":"Output:"}]}"""));
        await Assert.That(((ToolResultReceived)blocks[0].Payload).Result).IsEqualTo("Script completed\nOutput:");

        var big = new string('x', 5000);
        var uncapped = E(Item($$"""{"type":"function_call_output","call_id":"c4","output":"{{big}}"}"""));
        await Assert.That(((ToolResultReceived)uncapped[0].Payload).Result.Length).IsEqualTo(5000);
    }

    [Test]
    public async Task Reasoning_joins_summaries_and_flags_encrypted_only_content() {
        var summarized = E(Item("""{"type":"reasoning","summary":[{"type":"summary_text","text":"plan"},{"type":"summary_text","text":"more"}],"encrypted_content":"zzz"}"""));
        var thinking = (AssistantThinkingGenerated)summarized[0].Payload;
        await Assert.That(thinking.Content).IsEqualTo("plan\nmore");
        await Assert.That(thinking.Encrypted).IsFalse();

        var encrypted = (AssistantThinkingGenerated)E(Item("""{"type":"reasoning","summary":[],"encrypted_content":"zzz"}"""))[0].Payload;
        await Assert.That(encrypted.Content).IsEqualTo("");
        await Assert.That(encrypted.Encrypted).IsTrue();
    }

    [Test]
    public async Task Every_other_envelope_and_payload_type_is_ignored_and_malformed_lines_are_rejected() {
        foreach (var type in new[] { "event_msg", "turn_context", "session_meta", "world_state", "compacted", "inter_agent_communication_metadata" }) {
            var r = P($$$"""{"type":"{{{type}}}","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"x"}]}}""");
            await Assert.That(r.Events).IsEmpty().Because(type);
            await Assert.That(r.Rejected).IsNull().Because(type);
        }
        await Assert.That(E(Item("""{"type":"agent_message","content":[{"type":"input_text","text":"x"}]}"""))).IsEmpty();
        await Assert.That(E(Item("""{"type":"message","role":"user","content":"not-an-array"}"""))).IsEmpty();

        foreach (var line in new[] { "", "garbage", "[1]" }) {
            await Assert.That(P(line).Rejected).IsNotNull().Because(line);
            await Assert.That(P(line).Events).IsEmpty().Because(line);
        }
    }

    [Test]
    public async Task A_missing_timestamp_uses_the_receive_time() {
        var e = E("""{"type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"x"}]}}""");
        await Assert.That(e[0].Timestamp).IsEqualTo(Received);
        await Assert.That(e[0].RecordTimestamp).IsNull();
        await Assert.That(((AssistantTextGenerated)e[0].Payload).Timestamp.ToDateTimeOffset()).IsEqualTo(Received);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet run --project test/Capacitor.Models.Transcripts.Tests.Unit/Capacitor.Models.Transcripts.Tests.Unit.csproj --treenode-filter "/*/*/CodexRolloutEventsTests/*"`
Expected: build error on `CodexRolloutEvents`.

- [ ] **Step 3: Write the projection**

`src/Capacitor.Models.Transcripts/Harness/Codex/CodexRolloutEvents.cs`:

```csharp
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kurrent.Agent.Schema.Events;
using static Capacitor.Models.Transcripts.TranscriptText;

namespace Capacitor.Models.Transcripts.Harness.Codex;

public sealed class CodexRolloutContext : TranscriptContext { }

/// Codex's rollout (`~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl`): an envelope per line with
/// `type` and `payload`; only `response_item` payloads are conversation, the rest is telemetry.
public sealed class CodexRolloutEvents : ITranscriptProjection {
    public static readonly CodexRolloutEvents Instance = new();

    CodexRolloutEvents() { }

    public TranscriptContext CreateContext(string sessionId, string? agentId) => new CodexRolloutContext();

    public ProjectionResult Project(string line, int lineNumber, DateTimeOffset receivedAt, TranscriptContext context) {
        if (string.IsNullOrWhiteSpace(line)) return ProjectionResult.Reject("empty line");
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); } catch (JsonException ex) { return ProjectionResult.Reject($"not JSON: {ex.Message}"); }

        using (doc) {
            var root = doc.RootElement;
            if (!root.IsObject) return ProjectionResult.Reject("not a JSON object");
            if (root.Str("type") != "response_item" || root.Obj("payload") is not { } payload) return ProjectionResult.Empty;

            var (at, recordTimestamp) = TranscriptTime.Resolve(root.Str("timestamp"), receivedAt);
            var ts = Timestamp.FromDateTimeOffset(at);

            IMessage? evt = payload.Str("type") switch {
                "message"          => Message(payload, ts),
                "function_call"    => ToolCall(payload, ArgumentsStruct(payload.Str("arguments")), ts),
                "custom_tool_call" => ToolCall(payload, Wrap("input", payload.Str("input") ?? ""), ts),
                "function_call_output" or "custom_tool_call_output"
                                   => new ToolResultReceived { CallId = payload.Str("call_id") ?? "", Result = OutputText(payload), Timestamp = ts },
                "reasoning"        => Reasoning(payload, ts),
                _                  => null,
            };
            if (evt is null) return ProjectionResult.Empty;
            return ProjectionResult.Of([new CanonicalEvent(CanonicalEventTypes.Of(evt), evt, TranscriptIds.CodexRecord(line), at, recordTimestamp)]);
        }
    }

    static IMessage? Message(JsonElement payload, Timestamp ts) {
        if (payload.Arr("content") is not { } blocks) return null;
        switch (payload.Str("role")) {
            case "user": {
                var text = JoinTextBlocks(blocks, "input_text");
                return text.Length == 0 ? null : new UserMessageReceived { Content = text, Timestamp = ts };
            }
            case "assistant": {
                var text = JoinTextBlocks(blocks, "output_text");
                return text.Length == 0 ? null : new AssistantTextGenerated { Content = text, Timestamp = ts };
            }
            default:
                return null;
        }
    }

    static AssistantToolCallsGenerated ToolCall(JsonElement payload, Struct arguments, Timestamp ts) {
        var call = new AssistantToolCallsGenerated { Timestamp = ts };
        call.ToolCalls.Add(new ToolCallInfo { CallId = payload.Str("call_id") ?? "", ToolName = payload.Str("name") ?? "", Arguments = arguments });
        return call;
    }

    // `arguments` is a JSON string; an object parses as the struct, anything else is wrapped.
    static Struct ArgumentsStruct(string? arguments) {
        if (arguments is not null) {
            try {
                using var doc = JsonDocument.Parse(arguments);
                if (doc.RootElement.IsObject) return StructOf(doc.RootElement);
            } catch (JsonException) { }
        }
        return Wrap("arguments", arguments ?? "");
    }

    static string OutputText(JsonElement payload) =>
        payload.Str("output") ?? (payload.Arr("output") is { } blocks ? JoinTextBlocks(blocks, "input_text") : "");

    static AssistantThinkingGenerated Reasoning(JsonElement payload, Timestamp ts) {
        var summary = payload.Arr("summary") is { } blocks ? JoinTextBlocks(blocks, "summary_text") : "";
        return new AssistantThinkingGenerated {
            Content   = summary,
            Encrypted = summary.Length == 0 && payload.Str("encrypted_content") is not null,
            Timestamp = ts,
        };
    }
}
```

- [ ] **Step 4: Register the vendor**

In `TranscriptProjection.cs` add `using Capacitor.Models.Transcripts.Harness.Codex;` and the arm `"codex" => CodexRolloutEvents.Instance,` above the `_ => null` arm.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Models.Transcripts.Tests.Unit/Capacitor.Models.Transcripts.Tests.Unit.csproj`
Expected: the whole leaf suite passes.

- [ ] **Step 6: Commit**

```bash
git add src/Capacitor.Models.Transcripts test/Capacitor.Models.Transcripts.Tests.Unit
git commit -m "Project Codex rollout items to canonical events (#679)"
```

---

### Task 7: Core's envelope adapter

**Files:**
- Create: `src/Capacitor.Cli.Core/TranscriptEnvelopes.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/TranscriptEnvelopesTests.cs`

**Interfaces:**
- Consumes: leaf `CanonicalEvent`, schema payload types, `AcpEventEnvelope`/`AcpEventKind` (Core).
- Produces: `public static class TranscriptEnvelopes { const int ToolResultCap = 4096; IReadOnlyList<AcpEventEnvelope> From(CanonicalEvent evt); string Cap(string text); string CompactJson(Struct s); }`.

- [ ] **Step 1: Write the failing tests**

`test/Capacitor.Cli.Core.Tests.Unit/TranscriptEnvelopesTests.cs`:

```csharp
using Google.Protobuf.WellKnownTypes;
using Kurrent.Agent.Schema.Events;

namespace Capacitor.Cli.Core.Tests.Unit;

public class TranscriptEnvelopesTests {
    static readonly DateTimeOffset At = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    static CanonicalEvent Ev(object payload, string? recordTimestamp = "2026-08-26T12:00:00Z") =>
        new(CanonicalEventTypes.Of(payload), payload, Guid.NewGuid(), At, recordTimestamp);

    [Test]
    public async Task Text_payloads_map_to_their_kinds_with_the_raw_record_timestamp() {
        var user = TranscriptEnvelopes.From(Ev(new UserMessageReceived { Content = "hello" }));
        await Assert.That(user).Count().IsEqualTo(1);
        await Assert.That(user[0].Kind).IsEqualTo(AcpEventKind.UserMessage);
        await Assert.That(user[0].Text).IsEqualTo("hello");
        await Assert.That(user[0].TimestampIso).IsEqualTo("2026-08-26T12:00:00Z");

        var text = TranscriptEnvelopes.From(Ev(new AssistantTextGenerated { Content = "Hi" }, recordTimestamp: null));
        await Assert.That(text[0].Kind).IsEqualTo(AcpEventKind.AssistantText);
        await Assert.That(text[0].TimestampIso).IsEqualTo("2026-08-26T12:00:00.0000000+00:00");
    }

    [Test]
    public async Task Thinking_with_empty_content_reads_as_encrypted() {
        var plain = TranscriptEnvelopes.From(Ev(new AssistantThinkingGenerated { Content = "hmm" }))[0];
        await Assert.That(plain.Kind).IsEqualTo(AcpEventKind.AssistantThinking);
        await Assert.That(plain.Text).IsEqualTo("hmm");
        await Assert.That(plain.ThinkingEncrypted).IsFalse();

        var empty = TranscriptEnvelopes.From(Ev(new AssistantThinkingGenerated { Content = "", Encrypted = false }))[0];
        await Assert.That(empty.Text).IsNull();
        await Assert.That(empty.ThinkingEncrypted).IsTrue();
    }

    [Test]
    public async Task Each_tool_call_becomes_one_envelope_with_compact_object_json() {
        var calls = new AssistantToolCallsGenerated();
        calls.ToolCalls.Add(new ToolCallInfo { CallId = "t1", ToolName = "Bash", Arguments = Struct.Parser.ParseJson("""{"command":"ls","n":[1,2],"o":{"a":true,"b":null}}""") });
        calls.ToolCalls.Add(new ToolCallInfo { CallId = "t2", ToolName = "Read" });

        var e = TranscriptEnvelopes.From(Ev(calls));
        await Assert.That(e).Count().IsEqualTo(2);
        await Assert.That(e[0].Kind).IsEqualTo(AcpEventKind.ToolCall);
        await Assert.That(e[0].ToolCallId).IsEqualTo("t1");
        await Assert.That(e[0].ToolName).IsEqualTo("Bash");
        await Assert.That(e[0].ToolInputJson).IsEqualTo("""{"command":"ls","n":[1,2],"o":{"a":true,"b":null}}""");
        await Assert.That(e[1].ToolInputJson).IsEqualTo("{}");
    }

    [Test]
    public async Task Tool_results_are_capped_at_4096_units_marker_included_and_never_split_a_surrogate_pair() {
        var big = TranscriptEnvelopes.From(Ev(new ToolResultReceived { CallId = "t", Result = new string('x', 5000) }))[0];
        await Assert.That(big.Kind).IsEqualTo(AcpEventKind.ToolResult);
        await Assert.That(big.ToolCallId).IsEqualTo("t");
        await Assert.That(big.ToolResult!.Length).IsEqualTo(4096);
        await Assert.That(big.ToolResult).EndsWith("…");
        await Assert.That(big.ToolIsError).IsFalse();

        var pair = new string('x', 4094) + "\U0001F600" + "tail";
        var cut = TranscriptEnvelopes.Cap(pair);
        await Assert.That(cut.Length).IsEqualTo(4095);
        await Assert.That(char.IsHighSurrogate(cut[^2])).IsFalse();
    }

    [Test]
    public async Task Payloads_the_chat_cannot_show_map_to_nothing() {
        await Assert.That(TranscriptEnvelopes.From(Ev(new SessionStarted()))).IsEmpty();
        await Assert.That(TranscriptEnvelopes.From(new CanonicalEvent("Other", new object(), Guid.NewGuid(), At))).IsEmpty();
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj --treenode-filter "/*/*/TranscriptEnvelopesTests/*"`
Expected: build error on `TranscriptEnvelopes`.

- [ ] **Step 3: Write the adapter**

`src/Capacitor.Cli.Core/TranscriptEnvelopes.cs`:

```csharp
using System.Globalization;
using System.Text;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Kurrent.Agent.Schema.Events;

namespace Capacitor.Cli.Core;

/// The one place a stored canonical event becomes the chat vocabulary. Vendor display rules
/// (what to strip or skip) sit beside it under Harness/&lt;Vendor&gt;/, never here.
public static class TranscriptEnvelopes {
    public const int ToolResultCap = 4096;
    const string CapMarker = "…";

    public static IReadOnlyList<AcpEventEnvelope> From(CanonicalEvent evt) {
        var ts = evt.RecordTimestamp ?? evt.Timestamp.ToString("O", CultureInfo.InvariantCulture);
        switch (evt.Payload) {
            case UserMessageReceived m:
                return [new AcpEventEnvelope(Kind: AcpEventKind.UserMessage, Text: m.Content, TimestampIso: ts)];
            case AssistantTextGenerated m:
                return [new AcpEventEnvelope(Kind: AcpEventKind.AssistantText, Text: m.Content, TimestampIso: ts)];
            case AssistantThinkingGenerated m: {
                var empty = m.Content.Length == 0;
                return [new AcpEventEnvelope(Kind: AcpEventKind.AssistantThinking, Text: empty ? null : m.Content, ThinkingEncrypted: m.Encrypted || empty, TimestampIso: ts)];
            }
            case AssistantToolCallsGenerated m: {
                var list = new List<AcpEventEnvelope>(m.ToolCalls.Count);
                foreach (var call in m.ToolCalls)
                    list.Add(new AcpEventEnvelope(Kind: AcpEventKind.ToolCall, ToolCallId: call.CallId, ToolName: call.ToolName, ToolInputJson: call.Arguments is null ? "{}" : CompactJson(call.Arguments), TimestampIso: ts));
                return list;
            }
            case ToolResultReceived m:
                return [new AcpEventEnvelope(Kind: AcpEventKind.ToolResult, ToolCallId: m.CallId, ToolResult: Cap(m.Result), TimestampIso: ts)];
            default:
                return [];
        }
    }

    /// At most ToolResultCap units including the marker; a cut that would split a surrogate pair
    /// drops the high half too, so the result can be one unit short of the cap.
    public static string Cap(string text) {
        if (text.Length <= ToolResultCap) return text;
        var cut = ToolResultCap - CapMarker.Length;
        if (char.IsHighSurrogate(text[cut - 1])) cut--;
        return string.Concat(text.AsSpan(0, cut), CapMarker);
    }

    /// Compact JSON for a Struct, written by hand: the protobuf formatter pads its output and
    /// the chat pins exact strings.
    public static string CompactJson(Struct s) {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) Write(writer, s);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    static void Write(Utf8JsonWriter writer, Struct s) {
        writer.WriteStartObject();
        foreach (var (name, value) in s.Fields) {
            writer.WritePropertyName(name);
            Write(writer, value);
        }
        writer.WriteEndObject();
    }

    static void Write(Utf8JsonWriter writer, Value value) {
        switch (value.KindCase) {
            case Value.KindOneofCase.StringValue: writer.WriteStringValue(value.StringValue); break;
            case Value.KindOneofCase.NumberValue: writer.WriteNumberValue(value.NumberValue); break;
            case Value.KindOneofCase.BoolValue:   writer.WriteBooleanValue(value.BoolValue); break;
            case Value.KindOneofCase.StructValue: Write(writer, value.StructValue); break;
            case Value.KindOneofCase.ListValue:
                writer.WriteStartArray();
                foreach (var item in value.ListValue.Values) Write(writer, item);
                writer.WriteEndArray();
                break;
            default: writer.WriteNullValue(); break;
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj --treenode-filter "/*/*/TranscriptEnvelopesTests/*"`
Expected: all pass. If `EndsWith` is not an available string assertion, assert `big.ToolResult![^1] == '…'` instead.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/TranscriptEnvelopes.cs test/Capacitor.Cli.Core.Tests.Unit/TranscriptEnvelopesTests.cs
git commit -m "Map canonical transcript events to chat envelopes in Core (#679)"
```

---

### Task 8: Vendor chat rules and the chat projection registry

**Files:**
- Create: `src/Capacitor.Cli.Core/TranscriptChat.cs`
- Create: `src/Capacitor.Cli.Core/Harness/Claude/ClaudeChatRules.cs`
- Create: `src/Capacitor.Cli.Core/Harness/Codex/CodexChatRules.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Harness/Claude/ClaudeChatRulesTests.cs`, `test/Capacitor.Cli.Core.Tests.Unit/Harness/Codex/CodexChatRulesTests.cs`

**Interfaces:**
- Consumes: Task 7 `TranscriptEnvelopes.From`; leaf projections and `ClaudeCodeExtension`.
- Produces: `public interface IChatDisplayRules { AcpEventEnvelope? Filter(CanonicalEvent evt, AcpEventEnvelope envelope); }`; `public sealed class TranscriptChatProjection(ITranscriptProjection projection, IChatDisplayRules rules)` with `TranscriptContext CreateContext(string sessionId, string? agentId)` and `IReadOnlyList<AcpEventEnvelope> Project(string line, int lineNumber, DateTimeOffset receivedAt, TranscriptContext context)`; `public static class TranscriptChat { static TranscriptChatProjection? For(string vendor); }`; `ClaudeChatRules.Instance`, `CodexChatRules.Instance`.

These tests are the old projection tests' chat-level expectations, so they are what pins "no change in what the chat shows". Two deliberate differences from today, both in Claude records the chat has not shown well: several `text` blocks in one user record become one bubble (joined with a newline) instead of one bubble each, and text beside tool results is no longer shown as a user bubble. The envelope's `Model` is no longer populated; the renderer never read it.

- [ ] **Step 1: Write the failing tests**

`test/Capacitor.Cli.Core.Tests.Unit/Harness/Claude/ClaudeChatRulesTests.cs`:

```csharp
namespace Capacitor.Cli.Core.Tests.Unit.Harness.Claude;

/// The chat-level view of Claude records: what the Chat tab showed before the projection moved.
public class ClaudeChatRulesTests {
    static readonly DateTimeOffset Received = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);

    static IReadOnlyList<AcpEventEnvelope> P(string line) {
        var chat = TranscriptChat.For("claude")!;
        return chat.Project(line, 1, Received, chat.CreateContext("a1", null));
    }

    [Test]
    public async Task String_user_content_is_one_user_message_with_its_timestamp() {
        var e = P("""{"type":"user","message":{"role":"user","content":"hello"},"timestamp":"2026-08-26T12:00:00Z"}""");
        await Assert.That(e).Count().IsEqualTo(1);
        await Assert.That(e[0].Kind).IsEqualTo(AcpEventKind.UserMessage);
        await Assert.That(e[0].Text).IsEqualTo("hello");
        await Assert.That(e[0].TimestampIso).IsEqualTo("2026-08-26T12:00:00Z");
    }

    [Test]
    public async Task Meta_and_sidechain_records_project_to_nothing() {
        await Assert.That(P("""{"type":"user","isMeta":true,"message":{"content":"x"}}""")).IsEmpty();
        await Assert.That(P("""{"type":"user","isSidechain":true,"message":{"content":"x"}}""")).IsEmpty();
        await Assert.That(P("""{"type":"assistant","isSidechain":true,"message":{"content":[{"type":"text","text":"x"}]}}""")).IsEmpty();
    }

    [Test]
    public async Task Wrappers_are_stripped_and_a_blank_remainder_is_not_emitted() {
        var stripped = P("""{"type":"user","message":{"content":[{"type":"text","text":"<system-reminder>\nnoise\n</system-reminder>real"}]}}""");
        await Assert.That(stripped).Count().IsEqualTo(1);
        await Assert.That(stripped[0].Text).IsEqualTo("real");

        var onlyWrappers = P("""{"type":"user","message":{"content":[{"type":"text","text":"<command-name>/clear</command-name><local-command-stdout>ok</local-command-stdout>"}]}}""");
        await Assert.That(onlyWrappers).IsEmpty();
    }

    [Test]
    public async Task Tool_results_carry_string_or_block_content_capped_and_flag_errors() {
        var str = P("""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"done","is_error":true}]}}""");
        await Assert.That(str[0].Kind).IsEqualTo(AcpEventKind.ToolResult);
        await Assert.That(str[0].ToolCallId).IsEqualTo("t1");
        await Assert.That(str[0].ToolResult).IsEqualTo("done");
        await Assert.That(str[0].ToolIsError).IsTrue();

        var blocks = P("""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t2","content":[{"type":"text","text":"a"},{"type":"text","text":"b"}]}]}}""");
        await Assert.That(blocks[0].ToolResult).IsEqualTo("a\nb");
        await Assert.That(blocks[0].ToolIsError).IsFalse();

        var big = new string('x', 5000);
        var capped = P($$$"""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t3","content":"{{{big}}}"}]}}""");
        await Assert.That(capped[0].ToolResult!.Length).IsEqualTo(4096);
    }

    [Test]
    public async Task Assistant_blocks_map_to_text_thinking_and_tool_call() {
        var line = """{"type":"assistant","timestamp":"2026-08-26T12:00:01Z","message":{"model":"claude-fable-5","content":[{"type":"thinking","thinking":"hmm"},{"type":"text","text":"Hi"},{"type":"tool_use","id":"toolu_1","name":"Bash","input":{"command":"ls"}}]}}""";
        var e = P(line);

        await Assert.That(e).Count().IsEqualTo(3);
        await Assert.That(e[0].Kind).IsEqualTo(AcpEventKind.AssistantThinking);
        await Assert.That(e[0].Text).IsEqualTo("hmm");
        await Assert.That(e[1].Kind).IsEqualTo(AcpEventKind.AssistantText);
        await Assert.That(e[1].Text).IsEqualTo("Hi");
        await Assert.That(e[2].Kind).IsEqualTo(AcpEventKind.ToolCall);
        await Assert.That(e[2].ToolCallId).IsEqualTo("toolu_1");
        await Assert.That(e[2].ToolName).IsEqualTo("Bash");
        await Assert.That(e[2].ToolInputJson).IsEqualTo("""{"command":"ls"}""");
        await Assert.That(e[2].TimestampIso).IsEqualTo("2026-08-26T12:00:01Z");
    }

    [Test]
    public async Task Encrypted_thinking_and_non_object_inputs_are_normalized() {
        var enc = P("""{"type":"assistant","message":{"content":[{"type":"thinking","thinking":"","signature":"abc"}]}}""");
        await Assert.That(enc[0].ThinkingEncrypted).IsTrue();

        foreach (var (input, expected) in new[] {
            ("[1,2]", """{"input":[1,2]}"""),
            ("\"s\"", """{"input":"s"}"""),
            ("null", """{"input":null}"""),
        }) {
            var e = P($$$"""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t","name":"X","input":{{{input}}}}]}}""");
            await Assert.That(e[0].ToolInputJson).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task Every_other_record_type_and_malformed_input_project_to_nothing() {
        foreach (var type in new[] { "attachment", "summary", "system", "file-history-snapshot", "file-history-delta", "mode", "permission-mode", "last-prompt", "ai-title", "atis-latch", "worktree-state", "queue-operation", "progress", "unknown-future" })
            await Assert.That(P($$$"""{"type":"{{{type}}}","message":{"content":"x"}}""")).IsEmpty().Because(type);

        await Assert.That(P("not json")).IsEmpty();
        await Assert.That(P("[1,2]")).IsEmpty();
        await Assert.That(P("""{"type":"user","message":{"content":42}}""")).IsEmpty();
        await Assert.That(P("""{"type":"assistant","message":{"content":[{"type":"text","text":7}]}}""")).IsEmpty();
    }

    /// A record Claude Code injects for a finished background task is system-attributed, never
    /// the user's words: its summary leads in bold and its result follows as markdown.
    [Test]
    public async Task A_task_notification_projects_to_a_system_note_of_summary_and_result() {
        var line = """{"type":"user","origin":{"kind":"task-notification"},"promptSource":"system","message":{"role":"user","content":"<task-notification>\n<task-id>k1</task-id>\n<status>completed</status>\n<summary>Agent \"Review Task 15\" finished</summary>\n<result>\n## Findings\n\nNone.\n</result>\n</task-notification>"}}""";
        var note = P(line);
        await Assert.That(note).Count().IsEqualTo(1);
        await Assert.That(note[0].Kind).IsEqualTo(AcpEventKind.SystemNote);
        await Assert.That(note[0].Text).IsEqualTo("**Agent \"Review Task 15\" finished**\n\n## Findings\n\nNone.");

        var bare = P("""{"type":"user","origin":{"kind":"task-notification"},"message":{"content":"<task-notification>\n<summary>MCP task done.</summary>\n</task-notification>"}}""");
        await Assert.That(bare[0].Text).IsEqualTo("**MCP task done.**");

        var human = P("""{"type":"user","origin":{"kind":"human"},"promptSource":"typed","message":{"content":"hello"}}""");
        await Assert.That(human[0].Kind).IsEqualTo(AcpEventKind.UserMessage);
    }
}
```

`test/Capacitor.Cli.Core.Tests.Unit/Harness/Codex/CodexChatRulesTests.cs`:

```csharp
namespace Capacitor.Cli.Core.Tests.Unit.Harness.Codex;

public class CodexChatRulesTests {
    static readonly DateTimeOffset Received = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);

    static IReadOnlyList<AcpEventEnvelope> P(string line) {
        var chat = TranscriptChat.For("codex")!;
        return chat.Project(line, 1, Received, chat.CreateContext("a1", null));
    }

    static string Item(string payload, string ts = "2026-08-25T00:00:00Z") =>
        $$$"""{"timestamp":"{{{ts}}}","ordinal":1,"type":"response_item","payload":{{{payload}}}}""";

    [Test]
    public async Task User_and_assistant_messages_join_their_text_blocks() {
        var user = P(Item("""{"type":"message","role":"user","content":[{"type":"input_text","text":"a"},{"type":"input_text","text":"b"}]}"""));
        await Assert.That(user[0].Kind).IsEqualTo(AcpEventKind.UserMessage);
        await Assert.That(user[0].Text).IsEqualTo("a\nb");
        await Assert.That(user[0].TimestampIso).IsEqualTo("2026-08-25T00:00:00Z");

        var assistant = P(Item("""{"type":"message","role":"assistant","content":[{"type":"output_text","text":"Hi"}]}"""));
        await Assert.That(assistant[0].Kind).IsEqualTo(AcpEventKind.AssistantText);
        await Assert.That(assistant[0].Text).IsEqualTo("Hi");
    }

    [Test]
    public async Task Injected_preludes_developer_and_system_roles_are_skipped() {
        foreach (var prelude in new[] { "<environment_context>", "# AGENTS.md instructions", "<turn_aborted>", "<user_instructions>", "<permissions instructions>" })
            await Assert.That(P(Item($$"""{"type":"message","role":"user","content":[{"type":"input_text","text":"{{prelude}}\nstuff"}]}"""))).IsEmpty().Because(prelude);

        await Assert.That(P(Item("""{"type":"message","role":"developer","content":[{"type":"input_text","text":"x"}]}"""))).IsEmpty();
        await Assert.That(P(Item("""{"type":"message","role":"system","content":[{"type":"input_text","text":"x"}]}"""))).IsEmpty();
    }

    [Test]
    public async Task Tool_calls_always_carry_a_json_object_input() {
        var fn = P(Item("""{"type":"function_call","name":"spawn_agent","call_id":"c1","arguments":"{\"task\":\"t\"}"}"""));
        await Assert.That(fn[0].Kind).IsEqualTo(AcpEventKind.ToolCall);
        await Assert.That(fn[0].ToolCallId).IsEqualTo("c1");
        await Assert.That(fn[0].ToolName).IsEqualTo("spawn_agent");
        await Assert.That(fn[0].ToolInputJson).IsEqualTo("""{"task":"t"}""");

        var nonObject = P(Item("""{"type":"function_call","name":"f","call_id":"c2","arguments":"not json"}"""));
        await Assert.That(nonObject[0].ToolInputJson).IsEqualTo("""{"arguments":"not json"}""");

        var custom = P(Item("""{"type":"custom_tool_call","name":"exec","call_id":"c3","input":"const r = 1;"}"""));
        await Assert.That(custom[0].ToolName).IsEqualTo("exec");
        await Assert.That(custom[0].ToolInputJson).IsEqualTo("""{"input":"const r = 1;"}""");
    }

    [Test]
    public async Task Tool_outputs_carry_string_or_block_output_capped() {
        var str = P(Item("""{"type":"function_call_output","call_id":"c1","output":"{\"ok\":true}"}"""));
        await Assert.That(str[0].Kind).IsEqualTo(AcpEventKind.ToolResult);
        await Assert.That(str[0].ToolCallId).IsEqualTo("c1");
        await Assert.That(str[0].ToolResult).IsEqualTo("""{"ok":true}""");

        var blocks = P(Item("""{"type":"custom_tool_call_output","call_id":"c3","output":[{"type":"input_text","text":"Script completed"},{"type":"input_text","text":"Output:"}]}"""));
        await Assert.That(blocks[0].ToolResult).IsEqualTo("Script completed\nOutput:");

        var big = new string('x', 5000);
        var capped = P(Item($$"""{"type":"function_call_output","call_id":"c4","output":"{{big}}"}"""));
        await Assert.That(capped[0].ToolResult!.Length).IsEqualTo(4096);
    }

    [Test]
    public async Task Reasoning_joins_summaries_and_flags_encrypted_only_content() {
        var summarized = P(Item("""{"type":"reasoning","summary":[{"type":"summary_text","text":"plan"},{"type":"summary_text","text":"more"}],"encrypted_content":"zzz"}"""));
        await Assert.That(summarized[0].Kind).IsEqualTo(AcpEventKind.AssistantThinking);
        await Assert.That(summarized[0].Text).IsEqualTo("plan\nmore");
        await Assert.That(summarized[0].ThinkingEncrypted).IsFalse();

        var encrypted = P(Item("""{"type":"reasoning","summary":[],"encrypted_content":"zzz"}"""));
        await Assert.That(encrypted[0].Text).IsNull();
        await Assert.That(encrypted[0].ThinkingEncrypted).IsTrue();
    }

    [Test]
    public async Task Every_other_envelope_and_payload_type_projects_to_nothing() {
        foreach (var type in new[] { "event_msg", "turn_context", "session_meta", "world_state", "compacted", "inter_agent_communication_metadata" })
            await Assert.That(P($$$"""{"type":"{{{type}}}","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"x"}]}}""")).IsEmpty().Because(type);

        await Assert.That(P(Item("""{"type":"agent_message","content":[{"type":"input_text","text":"x"}]}"""))).IsEmpty();
        await Assert.That(P("garbage")).IsEmpty();
        await Assert.That(P(Item("""{"type":"message","role":"user","content":"not-an-array"}"""))).IsEmpty();
    }

    [Test]
    public async Task An_unknown_vendor_has_no_chat_projection() {
        await Assert.That(TranscriptChat.For("cursor")).IsNull();
        await Assert.That(TranscriptChat.For("Codex")).IsNotNull();
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj --treenode-filter "/*/*/*ChatRulesTests/*"`
Expected: build error on `TranscriptChat`.

- [ ] **Step 3: Write the registry and the chat projection**

`src/Capacitor.Cli.Core/TranscriptChat.cs`:

```csharp
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Codex;

namespace Capacitor.Cli.Core;

/// A vendor's say over how its stored events read in the chat: drop one, or rewrite the envelope.
public interface IChatDisplayRules {
    AcpEventEnvelope? Filter(CanonicalEvent evt, AcpEventEnvelope envelope);
}

/// The chat's view of a transcript: the leaf projection, the envelope mapping, one vendor's rules.
public sealed class TranscriptChatProjection(ITranscriptProjection projection, IChatDisplayRules rules) {
    public TranscriptContext CreateContext(string sessionId, string? agentId) => projection.CreateContext(sessionId, agentId);

    public IReadOnlyList<AcpEventEnvelope> Project(string line, int lineNumber, DateTimeOffset receivedAt, TranscriptContext context) {
        var result = projection.Project(line, lineNumber, receivedAt, context);
        if (result.Events.Count == 0) return [];
        var shown = new List<AcpEventEnvelope>(result.Events.Count);
        foreach (var evt in result.Events)
            foreach (var envelope in TranscriptEnvelopes.From(evt))
                if (rules.Filter(evt, envelope) is { } kept) shown.Add(kept);
        return shown;
    }
}

/// The one registration site in Core: a vendor's chat rules live under Harness/&lt;Vendor&gt;/ and
/// are paired with the leaf's projection here, nowhere else.
public static class TranscriptChat {
    public static TranscriptChatProjection? For(string vendor) =>
        TranscriptProjection.For(vendor) is not { } projection ? null
        : vendor.ToLowerInvariant() switch {
            "claude" => new TranscriptChatProjection(projection, ClaudeChatRules.Instance),
            "codex"  => new TranscriptChatProjection(projection, CodexChatRules.Instance),
            _        => null,
        };
}
```

- [ ] **Step 4: Write the Claude rules**

`src/Capacitor.Cli.Core/Harness/Claude/ClaudeChatRules.cs`:

```csharp
using System.Text.RegularExpressions;
using Capacitor.Models.Transcripts.Harness.Claude;

namespace Capacitor.Cli.Core.Harness.Claude;

/// What the chat hides or rewrites in Claude records: meta and sidechain records, the blocks
/// Claude Code injects around a user turn, and the finished-background-task record it injects as
/// if the user had spoken.
public sealed partial class ClaudeChatRules : IChatDisplayRules {
    public static readonly ClaudeChatRules Instance = new();

    ClaudeChatRules() { }

    public AcpEventEnvelope? Filter(CanonicalEvent evt, AcpEventEnvelope envelope) {
        var slug = SchemaExtensions.Slug(evt.Payload, ClaudeCodeExtension.Slug);
        if (SchemaExtensions.Flag(slug, ClaudeCodeExtension.IsSidechain) || SchemaExtensions.Flag(slug, ClaudeCodeExtension.IsMeta)) return null;

        switch (envelope.Kind) {
            case AcpEventKind.UserMessage: {
                if (SchemaExtensions.Text(slug, ClaudeCodeExtension.OriginKind) == "task-notification")
                    return TaskNotificationNote(envelope);
                var text = StripWrappers(envelope.Text ?? "");
                return text.Length == 0 ? null : envelope with { Text = text };
            }
            case AcpEventKind.ToolResult:
                return envelope with { ToolIsError = SchemaExtensions.Flag(slug, ClaudeCodeExtension.IsError) };
            default:
                return envelope;
        }
    }

    // System-attributed: the summary in bold, then the result as markdown; a notification with
    // neither shows whatever is left once the wrapper tags are gone.
    static AcpEventEnvelope? TaskNotificationNote(AcpEventEnvelope envelope) {
        var raw     = envelope.Text ?? "";
        var summary = TaskSummary().Match(raw) is { Success: true } s ? s.Groups[1].Value.Trim() : "";
        var body    = TaskResult().Match(raw) is { Success: true } r ? r.Groups[1].Value.Trim() : "";
        var parts   = new List<string>(2);
        if (summary.Length > 0) parts.Add($"**{summary}**");
        if (body.Length > 0) parts.Add(body);
        var text = parts.Count > 0 ? string.Join("\n\n", parts) : TaskWrapper().Replace(raw, "").Trim();
        return text.Length == 0 ? null : envelope with { Kind = AcpEventKind.SystemNote, Text = text };
    }

    /// Removes the blocks Claude Code injects around a user turn: reminders and slash-command
    /// echoes.
    internal static string StripWrappers(string text) => Wrappers().Replace(text, "").Trim();

    [GeneratedRegex(@"<summary>(.*?)</summary>", RegexOptions.Singleline)]
    private static partial Regex TaskSummary();

    [GeneratedRegex(@"<result>(.*?)</result>", RegexOptions.Singleline)]
    private static partial Regex TaskResult();

    [GeneratedRegex(@"</?task-notification>")]
    private static partial Regex TaskWrapper();

    [GeneratedRegex(@"<(system-reminder|command-name|command-message|command-args|local-command-stdout|local-command-caveat)>.*?</\1>", RegexOptions.Singleline)]
    private static partial Regex Wrappers();
}
```

- [ ] **Step 5: Write the Codex rules**

`src/Capacitor.Cli.Core/Harness/Codex/CodexChatRules.cs`:

```csharp
namespace Capacitor.Cli.Core.Harness.Codex;

/// Codex writes its injected preludes as user messages; the chat shows none of them.
public sealed class CodexChatRules : IChatDisplayRules {
    public static readonly CodexChatRules Instance = new();

    static readonly string[] InjectedPreludes = [
        "<environment_context>", "# AGENTS.md instructions", "<turn_aborted>", "<user_instructions>", "<permissions instructions>",
    ];

    CodexChatRules() { }

    public AcpEventEnvelope? Filter(CanonicalEvent evt, AcpEventEnvelope envelope) =>
        envelope.Kind == AcpEventKind.UserMessage && IsInjectedPrelude(envelope.Text ?? "") ? null : envelope;

    static bool IsInjectedPrelude(string text) {
        var trimmed = text.TrimStart();
        foreach (var prelude in InjectedPreludes)
            if (trimmed.StartsWith(prelude, StringComparison.Ordinal)) return true;
        return false;
    }
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj --treenode-filter "/*/*/*ChatRulesTests/*"`
Expected: all pass. The old `ClaudeTranscriptEventsTests` and `CodexRolloutEventsTests` in this suite still exist and still pass against the old Core projections; Task 10 deletes them.

- [ ] **Step 7: Commit**

```bash
git add src/Capacitor.Cli.Core/TranscriptChat.cs src/Capacitor.Cli.Core/Harness test/Capacitor.Cli.Core.Tests.Unit/Harness
git commit -m "Pair each vendor's chat display rules with its projection (#679)"
```

---

### Task 9: Rewire the desktop app

**Files:**
- Modify: `src/Capacitor.App/ViewModels/ChatTabViewModel.cs` (fields near line 33, ctor near line 153, lease creation near line 265, `OnTick`/`ReadAndApplyAsync` near lines 290–315, `Apply`'s `Reset` arm near line 325)
- Modify: `src/Capacitor.App/ViewModels/WorkspaceViewModel.cs:146`
- Test: `test/Capacitor.App.Tests.Unit/ChatTabViewModelTests.cs` (harness at line 43, `Claude()` at 66, `GatedProjection` at 286–291)

**Interfaces:**
- Consumes: Task 8 `TranscriptChatProjection`, `TranscriptChat.For`, `ClaudeChatRules.Instance`; leaf `ITranscriptProjection`, `TranscriptContext`, `ProjectionResult`, `ClaudeTranscriptEvents.Instance`.

- [ ] **Step 1: Adapt the test harness so the suite fails to compile for the right reason**

In `test/Capacitor.App.Tests.Unit/ChatTabViewModelTests.cs`:

Change the harness constructor parameter type and the `Claude()` factory:

```csharp
        public Harness(TranscriptChatProjection? projection, Action<FakePermissionService>? seed = null) {
```

```csharp
    static Harness Claude(Action<FakePermissionService>? seed = null) => new(TranscriptChat.For("claude"), seed);
```

Replace `GatedProjection` with one that implements the leaf interface, and its use site:

```csharp
    sealed class GatedProjection(ITranscriptProjection inner, string blockOn, TaskCompletionSource gate) : ITranscriptProjection {
        public TranscriptContext CreateContext(string sessionId, string? agentId) => inner.CreateContext(sessionId, agentId);

        public ProjectionResult Project(string line, int lineNumber, DateTimeOffset receivedAt, TranscriptContext context) {
            if (line.Contains(blockOn, StringComparison.Ordinal)) gate.Task.GetAwaiter().GetResult();
            return inner.Project(line, lineNumber, receivedAt, context);
        }
    }
```

```csharp
            var h = new Harness(new TranscriptChatProjection(new GatedProjection(ClaudeTranscriptEvents.Instance, "OLD", gate), ClaudeChatRules.Instance));
```

Add at the top of the file: `using Capacitor.Cli.Core.Harness.Claude;` and `using Capacitor.Models.Transcripts.Harness.Claude;`.

Add one new test anywhere in the class (there is no reset test today; this is the first):

```csharp
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_reset_starts_a_fresh_projection_context_and_line_count() {
        await RunOnUiAsync(async () => {
            var counting = new CountingProjection(ClaudeTranscriptEvents.Instance);
            var h = new Harness(new TranscriptChatProjection(counting, ClaudeChatRules.Instance));
            var path = Tmp.CreateFile("t.jsonl", [UserLine, AssistantLine]);
            await h.PushAsync(Dto(path));
            await Assert.That(counting.LineNumbers).IsEquivalentTo(new[] { 1, 2 });

            File.WriteAllLines(path, [UserLine]);    // shorter: the tail resets
            await h.TickAsync();
            await Assert.That(counting.LineNumbers).IsEquivalentTo(new[] { 1, 2, 1 });
            await Assert.That(counting.ContextsCreated).IsEqualTo(2);
            await h.TeardownAsync();
        });
    }

    sealed class CountingProjection(ITranscriptProjection inner) : ITranscriptProjection {
        public List<int> LineNumbers { get; } = [];
        public int ContextsCreated { get; private set; }

        public TranscriptContext CreateContext(string sessionId, string? agentId) {
            ContextsCreated++;
            return inner.CreateContext(sessionId, agentId);
        }

        public ProjectionResult Project(string line, int lineNumber, DateTimeOffset receivedAt, TranscriptContext context) {
            LineNumbers.Add(lineNumber);
            return inner.Project(line, lineNumber, receivedAt, context);
        }
    }
```

`UserLine`, `AssistantLine`, `Dto(path)` (`ChatTabViewModelTests.cs:29`), `Tmp`, `RunOnUiAsync` and `TickAsync` are the file's existing fixtures.

- [ ] **Step 2: Run the app tests to verify the build fails on the view model**

Run: `dotnet build test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj`
Expected: errors in `ChatTabViewModelTests.cs` because `ChatTabViewModel`'s constructor still takes `ITranscriptProjection?`.

- [ ] **Step 3: Rewire the view model**

In `src/Capacitor.App/ViewModels/ChatTabViewModel.cs`:

Field and constructor parameter:

```csharp
    readonly TranscriptChatProjection? _projection;
```

```csharp
    public ChatTabViewModel(
            string agentId, IDaemonClientService daemon, TerminalTabViewModel terminal,
            TranscriptChatProjection? projection, IUrlOpener opener, TimeProvider time, IPermissionService permissions) {
```

Replace the `TailLease` record with a class that owns the projection context and the line count:

```csharp
    /// The tail, the generation it belongs to, and the projection context and line count that
    /// live exactly as long as the file the tail is reading. Taken as one reference: reading them
    /// separately lets a switch land between them and tag a read of the old file with the new
    /// generation, which Apply's guard would then wave through onto the freshly cleared list.
    sealed class TailLease(JsonlTail tail, int generation) {
        public JsonlTail Tail { get; } = tail;
        public int Generation { get; } = generation;
        public TranscriptContext? Context { get; private set; }
        int _linesRead;

        public TranscriptContext ContextFor(TranscriptChatProjection projection, string agentId) =>
            Context ??= projection.CreateContext(agentId, null);

        public void Reset() { Context = null; _linesRead = 0; }

        public int NextLine() => ++_linesRead;
    }
```

The lease creation line stays `_lease = new TailLease(new JsonlTail(path), Interlocked.Increment(ref _generation));`.

Replace `OnTick` and `ReadAndApplyAsync`:

```csharp
    void OnTick() {
        if (_lease is not { } lease || _projection is not { } projection) return;
        if (Interlocked.CompareExchange(ref _readInFlight, 1, 0) != 0) return;
        _pendingRead = ReadAndApplyAsync(lease, projection);
    }

    async Task ReadAndApplyAsync(TailLease lease, TranscriptChatProjection projection) {
        try {
            var (read, envelopes) = await Task.Run(() => {
                var result = lease.Tail.ReadAppended();
                if (result.Status == TailStatus.Reset) lease.Reset();
                var list = new List<AcpEventEnvelope>();
                if (result.Lines.Count > 0) {
                    var context = lease.ContextFor(projection, _agentId);
                    context.BeginBatch();
                    var receivedAt = _time.GetUtcNow();
                    foreach (var line in result.Lines) {
                        var lineNumber = lease.NextLine();
                        try { list.AddRange(projection.Project(line, lineNumber, receivedAt, context)); }
                        catch (Exception ex) { LogOnce($"projection: {ex.Message}"); }
                    }
                }
                return (result, list);
            }).ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() => Apply(lease.Generation, read, envelopes));
        } catch (Exception ex) {
            LogOnce($"read: {ex.Message}");
        } finally {
            Volatile.Write(ref _readInFlight, 0);
        }
    }
```

`Apply` is unchanged: it still receives the generation and the envelope list, and its `TailStatus.Reset` arm still clears the rows.

- [ ] **Step 4: Rewire the workspace**

In `src/Capacitor.App/ViewModels/WorkspaceViewModel.cs:146`, replace `TranscriptProjection.For(p.Dto!.Vendor)` with `TranscriptChat.For(p.Dto!.Vendor)`.

- [ ] **Step 5: Build the app and run its tests**

Run: `dotnet build src/Capacitor.App/Capacitor.App.csproj`
Expected: zero warnings, including the Avalonia XAML ones (rebuild the app project, not just the CLI).

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj --treenode-filter "/*/*/ChatTabViewModelTests/*"`
Expected: all pass, the new reset test included.

- [ ] **Step 6: Commit**

```bash
git add src/Capacitor.App test/Capacitor.App.Tests.Unit
git commit -m "Read the chat through the transcripts leaf with a context per tail (#679)"
```

---

### Task 10: Delete the old Core projections, verify AOT, update the docs

**Files:**
- Delete: `src/Capacitor.Cli.Core/TranscriptProjection.cs`, `src/Capacitor.Cli.Core/Harness/Claude/ClaudeTranscriptEvents.cs`, `src/Capacitor.Cli.Core/Harness/Codex/CodexRolloutEvents.cs`, `test/Capacitor.Cli.Core.Tests.Unit/Harness/Claude/ClaudeTranscriptEventsTests.cs`, `test/Capacitor.Cli.Core.Tests.Unit/Harness/Codex/CodexRolloutEventsTests.cs`
- Modify: `CLAUDE.md` (file-paths line at the top; harness layout list; test layout line 85), `docs/CHANGES.md` (append), spec §1 dependency line

- [ ] **Step 1: Delete the superseded files**

```bash
git rm src/Capacitor.Cli.Core/TranscriptProjection.cs src/Capacitor.Cli.Core/Harness/Claude/ClaudeTranscriptEvents.cs src/Capacitor.Cli.Core/Harness/Codex/CodexRolloutEvents.cs
git rm test/Capacitor.Cli.Core.Tests.Unit/Harness/Claude/ClaudeTranscriptEventsTests.cs test/Capacitor.Cli.Core.Tests.Unit/Harness/Codex/CodexRolloutEventsTests.cs
```

- [ ] **Step 2: Build the solution and run every suite**

Run: `dotnet build Capacitor.slnx`
Expected: zero warnings; any remaining reference to `Capacitor.Cli.Core.TranscriptProjection` or the old projection types is a leftover to fix by switching it to `TranscriptChat`/the leaf types.

Run: `TMPDIR=/private/tmp dotnet test --solution Capacitor.slnx`
Expected: green across all suites.

- [ ] **Step 3: Verify both AOT publishes are warning-free**

Run:

```bash
dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}' ; echo "kcap: exit ${PIPESTATUS[0]}"
dotnet publish src/Capacitor.Cli.Daemon/Capacitor.Cli.Daemon.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}' ; echo "daemon: exit ${PIPESTATUS[0]}"
```

Expected: no `IL2026`/`IL3050` lines printed for either; both publishes succeed. A warning here means a reflection path crept in; the leaf and Core must use only `Utf8JsonWriter`, `JsonDocument` and the protobuf parsers.

- [ ] **Step 4: Update CLAUDE.md**

Top "File paths" line: after `shared core at \`src/Capacitor.Cli.Core/\``, insert `, transcript normalization at \`src/Capacitor.Models.Transcripts/\``.

Harness layout list: add a first bullet:

```markdown
- `src/Capacitor.Models.Transcripts/Harness/<Vendor>/` — transcript projections to canonical events, the one place a vendor's transcript format is read.
```

and extend the Core bullet's list with `chat display rules`.

Test layout line 85: add `test/Capacitor.Models.Transcripts.Tests.Unit/` to the list of unit suites.

- [ ] **Step 5: Append the change note**

At the end of `docs/CHANGES.md`:

```markdown
## Transcript normalization has one home

**AI-2265** (spec: `docs/superpowers/specs/2026-09-04-ai2265-transcript-normalization-leaf-design.md`)
moves transcript-to-canonical projection into `Capacitor.Models.Transcripts`, a leaf with the
`Kurrent.Agent.Schema` package and nothing else, so the desktop chat and the server read one
implementation. **Projections emit the schema's own messages**, because that is what the server
persists and the package is AOT-clean; the chat keeps its `AcpEventEnvelope` renderer through an
adapter in Core, with each vendor's display rules (Claude's wrapper stripping and task-notification
note, Codex's injected-prelude skip) beside it under `Harness/<Vendor>/`. **A projection never
mutates an event it has returned**: anything the server stamps in place today arrives as an
explicit amendment or a `UsageApplied` instruction. **Every id derivation is a persistence
contract** pinned by fixed vectors; the server dedups by them. This first step carries the chat's
coverage only; Claude and Codex parity with the server's normalizers follow, one PR each.
```

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Retire the Core chat projections in favour of the transcripts leaf (#679)"
```

- [ ] **Step 7: Open the PR**

Read `.github/PULL_REQUEST_TEMPLATE.md` and follow its comment block. Title: `Carve transcript normalization out into Capacitor.Models.Transcripts`. The description's reference line carries `Closes #679` only if the whole issue is done, which it is not: write `Part of #679` and `AI-2265` instead. Push with `git push https://github.com/kurrent-io/kcap-cli.git <branch>` if an SSH push is refused.
