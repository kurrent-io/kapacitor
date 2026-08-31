# Chat for PTY harnesses Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give a Claude or interactive Codex session in the desktop app a Chat tab rendered from the vendor's own transcript file, with a composer that sends to the PTY, without any new local-IPC frame.

**Architecture:** The daemon already locates each PTY agent's transcript; it now stamps the link-resolved path onto the existing `AgentStatusDto` as a trailing nullable member. Core gains a total, sharing-safe `JsonlTail` and two public per-vendor line→`AcpEventEnvelope` projections behind one `ITranscriptProjection` seam. The app's `WorkspaceViewModel` grows a `ChatTabViewModel` (poll → project → `AvaloniaList.AddRange`), a Markdig-backed `MarkdownView`, and a composer that delivers text through the sibling `TerminalTabViewModel` behind an opening-token send gate.

**Tech Stack:** .NET 10, Avalonia 12.1.1 + ReactiveUI.Avalonia + DynamicData, TUnit on MTP, `Microsoft.Extensions.TimeProvider.Testing`, Markdig 1.3.2 (app only), System.Text.Json (Core, AOT-safe).

**Spec:** `docs/superpowers/specs/2026-08-26-ai2196-chat-for-pty-harnesses-design.md` — read it first; every task below cites the section it implements.

## Global Constraints

- Core (`src/Capacitor.Cli.Core`) is `IsAotCompatible`/`IsTrimmable` and compiles into two NativeAOT binaries: BCL + `System.Text.Json` only, `Utf8JsonWriter` for anything built rather than copied, `[GeneratedRegex]` for any regex, never reflection-based serialization. Final acceptance runs `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` and expects no output.
- Every JSON read in Core goes through `JsonElementExtensions` (`Str/Num/Bool/Obj/Arr/Prop`), never raw `GetProperty`/`ValueKind` checks.
- Every open of an agent-owned transcript is `new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)`. Never `File.ReadAllText`/`File.ReadLines` on a transcript.
- `AgentStatusDto` members are trailing, nullable, always emitted (JSON null, never omitted). The context must never gain a `DefaultIgnoreCondition`.
- Daemon status mutations: mutate first, `_statusNotifier.Pulse()` second, never a pulse behind an awaited server call.
- App: every daemon-fed observable goes through `ObserveOn(RxSchedulers.MainThreadScheduler)` before touching bound state; pool-thread completions hop through `Dispatcher.UIThread.InvokeAsync`; VMs are ctor-scoped with `TeardownAsync` as the one exit; no state lives in views.
- Tests: `[TempDir] public required TempDir Tmp { get; init; }` or `using var tmp = new TempDir();`; build paths with `tmp.PathTo(…)`, `tmp.CreateFile(…)`, `tmp.CreateDir(…)`. UI tests carry `[NotInParallel("AvaloniaSession")]` and run inside `RunOnUiAsync`. Never assert an env var is absent from a `ProcessStartInfo`.
- Comments: scarce; never design/ticket coordinates, review artifacts, or change narration. State the constraint that would otherwise be undone.
- Commit subjects: one imperative clause, ≤ 80 chars, no issue reference (none is known), body ≤ 5 lines, end with `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.
- Composer delivery constants (verbatim from the spec): bracketed paste `ESC[200~` … `ESC[201~`, then `\r` after **150 ms**; poll interval **500 ms**; tool-result cap **4096** UTF-16 units including the `…` marker; tool detail cut at **80** characters.

## File structure

**Core (`src/Capacitor.Cli.Core`)**
- `LocalIpc/StatusIpc.cs` — modify: `AgentStatusDto.TranscriptPath`.
- `JsonElementExtensions.cs` — modify: `Prop(name)`.
- `JsonlTail.cs` — create: `JsonlTail`, `TailRead`, `TailStatus`.
- `TranscriptProjection.cs` — create: `ITranscriptProjection`, `TranscriptProjection.For`, internal `TranscriptProjectionText` (cap, join, object wrapping).
- `Harness/Claude/ClaudeTranscriptEvents.cs` — create.
- `Harness/Codex/CodexRolloutEvents.cs` — create.

**Daemon (`src/Capacitor.Cli.Daemon`)**
- `Harness/Claude/SessionTranscriptLocator.cs` — modify: `TryLocateWinner`, link resolution.
- `Services/TranscriptDiscovery.cs` — create: the `TimeProvider`-driven poll.
- `Services/AgentOrchestrator.cs` — modify: `AgentInstance.TranscriptPath`, discovery wiring, test seams.
- `Services/AgentOrchestrator.LocalIpc.cs` — modify: local spawn starts discovery; snapshot stamps the path.

**App (`src/Capacitor.App`)**
- `Directory.Packages.props`, `Capacitor.App.csproj` — modify: Markdig.
- `Services/TerminalInputEncoder.cs`, `Services/LinkPolicy.cs` — create.
- `ViewModels/ToolDetail.cs`, `ViewModels/ChatItems.cs`, `ViewModels/ChatTabViewModel.cs` — create.
- `ViewModels/TerminalTabViewModel.cs` — modify: send gate.
- `ViewModels/WorkspaceViewModel.cs` — modify: `Chat`, `ActiveTab`.
- `Views/MarkdownBlocks.cs`, `Views/MarkdownView.cs`, `Views/ChatTabView.axaml(.cs)` — create.
- `Views/WorkspaceView.axaml(.cs)`, `Views/Converters.cs` — modify.
- `App.axaml.cs` — modify: workspace factory passes the opener.
- `docs/CHANGES.md` — modify.

**Tests** mirror the prod directories: `test/Capacitor.Cli.Core.Tests.Unit/{JsonlTailTests,TranscriptProjectionTests,JsonElementExtensionsTests}.cs`, `test/Capacitor.Cli.Core.Tests.Unit/Harness/{Claude/ClaudeTranscriptEventsTests,Codex/CodexRolloutEventsTests}.cs`, `test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/StatusIpcJsonTests.cs`; `test/Capacitor.Cli.Daemon.Tests.Unit/Harness/Claude/SessionTranscriptLocatorTests.cs`, `.../Services/{TranscriptDiscoveryTests,AgentStatusSnapshotTests,AgentOrchestratorLocalAttachTests}.cs`; `test/Capacitor.App.Tests.Unit/{TerminalInputEncoderTests,LinkPolicyTests,ToolDetailTests,TerminalSendGateTests,ChatTabViewModelTests,ChatComposerTests,MarkdownBlocksTests,WorkspaceViewModelTests,WorkspaceViewSmokeTests,ChatTabViewSmokeTests}.cs`.

**Running one suite while iterating** (faster than the solution run):

```bash
dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/JsonlTailTests/*"
```

The filter is `/<assembly>/<namespace>/<class>/<test>` with globs; `--filter` is NOT supported.

---

### Task 1: `transcript_path` on the status wire

**Files:**
- Modify: `src/Capacitor.Cli.Core/LocalIpc/StatusIpc.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/StatusIpcJsonTests.cs`

**Interfaces:**
- Produces: `AgentStatusDto(..., bool? HasTerminal = null, string? Title = null, string? TranscriptPath = null)`; serialized member `transcript_path`, last.

- [ ] **Step 1: Write the failing tests**

Append to `StatusIpcJsonTests`:

```csharp
    [Test]
    public async Task Transcript_path_serializes_last_and_null_is_emitted() {
        var withPath = new AgentStatusDto(
            "a1", "agent", "claude", "/repo", "Running",
            null, null, null, new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), null, null,
            HasTerminal: true, Title: "t", TranscriptPath: "/home/u/.claude/projects/-repo/abc.jsonl");
        var without = withPath with { TranscriptPath = null };

        var json = JsonSerializer.Serialize(withPath, StatusIpcJsonContext.Default.AgentStatusDto);
        var jsonNull = JsonSerializer.Serialize(without, StatusIpcJsonContext.Default.AgentStatusDto);

        await Assert.That(json).EndsWith(""","has_terminal":true,"title":"t","transcript_path":"/home/u/.claude/projects/-repo/abc.jsonl"}""");
        await Assert.That(jsonNull).EndsWith(""","has_terminal":true,"title":"t","transcript_path":null}""");
    }

    [Test]
    public async Task Old_agent_json_without_transcript_path_deserializes_to_null() {
        var dto = new AgentStatusDto(
            "a1", "agent", "claude", "/repo", "Running",
            null, null, null, DateTime.UtcNow, null, null, TranscriptPath: "/x.jsonl");
        var json = JsonSerializer.Serialize(dto, StatusIpcJsonContext.Default.AgentStatusDto);
        var stripped = System.Text.RegularExpressions.Regex.Replace(json, ",\"transcript_path\":[^,}]+", "");

        var back = JsonSerializer.Deserialize(stripped, StatusIpcJsonContext.Default.AgentStatusDto);

        await Assert.That(back!.TranscriptPath).IsNull();
    }
```

Also extend the exact-JSON pin `DaemonStatus_serializes_exactly_with_nulls_present_and_pinned_field_order`: append `,"transcript_path":null` after `"title":"Fix the flaky test"` and after `"title":null` in the expected string.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/StatusIpcJsonTests/*"`
Expected: build error — `TranscriptPath` is not a member of `AgentStatusDto`.

- [ ] **Step 3: Add the member**

In `StatusIpc.cs`, after `string? Title = null` inside `AgentStatusDto`:

```csharp
    string? Title = null,
    // Where the daemon found the agent's own transcript (Claude's project .jsonl, Codex's
    // rollout), link-resolved. Trailing + nullable: null is "older daemon", "not found yet",
    // "no transcript for this runtime", or "found nothing before the agent exited" alike —
    // a client waits, it never distinguishes them.
    string? TranscriptPath = null);
```

- [ ] **Step 4: Run the tests to verify they pass**

Run the same command. Expected: all `StatusIpcJsonTests` PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/LocalIpc/StatusIpc.cs test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/StatusIpcJsonTests.cs
git commit -m "Carry the agent's transcript path on the status wire

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 2: `JsonElementExtensions.Prop`

**Files:**
- Modify: `src/Capacitor.Cli.Core/JsonElementExtensions.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/JsonElementExtensionsTests.cs` (create)

**Interfaces:**
- Produces: `JsonElement? Prop(string property)` — the named property of any kind (arrays, scalars, JSON null included) as an element; null when the receiver is not an object or the property is absent.

- [ ] **Step 1: Write the failing test**

```csharp
using System.Text.Json;

namespace Capacitor.Cli.Core.Tests.Unit;

public class JsonElementExtensionsTests {
    static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Test]
    public async Task Prop_returns_any_kind_and_null_when_absent_or_not_an_object() {
        var el = Parse("""{"arr":[1,2],"num":3,"nil":null,"str":"s"}""");

        await Assert.That(el.Prop("arr")!.Value.ValueKind).IsEqualTo(JsonValueKind.Array);
        await Assert.That(el.Prop("num")!.Value.GetInt32()).IsEqualTo(3);
        await Assert.That(el.Prop("nil")!.Value.ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(el.Prop("str")!.Value.GetString()).IsEqualTo("s");
        await Assert.That(el.Prop("absent")).IsNull();
        await Assert.That(Parse("[1]").Prop("x")).IsNull();
        await Assert.That(Parse("\"scalar\"").Prop("x")).IsNull();
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/JsonElementExtensionsTests/*"`
Expected: build error — no `Prop` member.

- [ ] **Step 3: Add the accessor**

Inside the `extension(JsonElement el)` block, after `Arr`:

```csharp
        // The property as-is, whatever its kind — for the one caller that has to copy a value
        // verbatim (a non-object tool input wrapped into an object). Every other read wants a
        // typed accessor above; this one deliberately answers "present" for JSON null too.
        public JsonElement? Prop(string property) => el.IsObject && el.TryGetProperty(property, out var v) ? v : null;
```

- [ ] **Step 4: Run the test to verify it passes**

Same command. Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/JsonElementExtensions.cs test/Capacitor.Cli.Core.Tests.Unit/JsonElementExtensionsTests.cs
git commit -m "Add an any-kind property accessor to JsonElementExtensions

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 3: `JsonlTail`

**Files:**
- Create: `src/Capacitor.Cli.Core/JsonlTail.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/JsonlTailTests.cs`

**Interfaces:**
- Produces:
  - `public enum TailStatus { Ok, Reset, Missing, Failed }`
  - `public sealed record TailRead(IReadOnlyList<string> Lines, TailStatus Status, string? Failure = null)`
  - `public sealed class JsonlTail(string path)` with `string Path`, `long Cursor`, `TailRead ReadAppended()` (never throws), `static List<string> SplitCompleteLines(ReadOnlySpan<byte> bytes, out int consumed)`.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Text;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Core.Tests.Unit;

public class JsonlTailTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task Complete_lines_are_delivered_once_and_a_partial_final_line_is_held() {
        var path = Tmp.CreateFile("t.jsonl", "{\"a\":1}\n{\"b\":2}\n{\"c\":");
        var tail = new JsonlTail(path);

        var first = tail.ReadAppended();
        await Assert.That(first.Status).IsEqualTo(TailStatus.Ok);
        await Assert.That(first.Lines).IsEquivalentTo(new[] { "{\"a\":1}", "{\"b\":2}" });

        var second = tail.ReadAppended();
        await Assert.That(second.Lines).IsEmpty();

        File.AppendAllText(path, "3}\n");
        var third = tail.ReadAppended();
        await Assert.That(third.Lines).IsEquivalentTo(new[] { "{\"c\":3}" });
    }

    [Test]
    public async Task Crlf_is_stripped_and_blank_lines_are_skipped() {
        var path = Tmp.CreateFile("t.jsonl", "{\"a\":1}\r\n\n   \n{\"b\":2}\r\n");
        var read = new JsonlTail(path).ReadAppended();

        await Assert.That(read.Lines).IsEquivalentTo(new[] { "{\"a\":1}", "{\"b\":2}" });
    }

    [Test]
    public async Task Length_regression_resets_and_rereads_from_zero() {
        var path = Tmp.CreateFile("t.jsonl", "{\"a\":1}\n{\"b\":2}\n");
        var tail = new JsonlTail(path);
        tail.ReadAppended();

        File.WriteAllText(path, "{\"z\":9}\n");
        var read = tail.ReadAppended();

        await Assert.That(read.Status).IsEqualTo(TailStatus.Reset);
        await Assert.That(read.Lines).IsEquivalentTo(new[] { "{\"z\":9}" });
        await Assert.That(tail.Cursor).IsEqualTo(8);
    }

    [Test]
    public async Task Missing_file_is_reported_then_read_once_it_appears() {
        var path = Tmp.PathTo("later.jsonl");
        var tail = new JsonlTail(path);

        await Assert.That(tail.ReadAppended().Status).IsEqualTo(TailStatus.Missing);

        File.WriteAllText(path, "{\"a\":1}\n");
        var read = tail.ReadAppended();
        await Assert.That(read.Status).IsEqualTo(TailStatus.Ok);
        await Assert.That(read.Lines).IsEquivalentTo(new[] { "{\"a\":1}" });
    }

    [Test]
    public async Task A_transient_failure_keeps_the_cursor_and_the_next_read_succeeds() {
        var path = Tmp.CreateFile("t.jsonl", "{\"a\":1}\n");
        var tail = new JsonlTail(path);
        tail.ReadAppended();

        // A directory in the file's place is the one failure every OS reports as neither
        // FileNotFound nor DirectoryNotFound when opened for read.
        File.Delete(path);
        Directory.CreateDirectory(path);
        var failed = tail.ReadAppended();
        await Assert.That(failed.Status).IsEqualTo(TailStatus.Failed);
        await Assert.That(failed.Failure).IsNotNull();
        await Assert.That(tail.Cursor).IsEqualTo(8);

        Directory.Delete(path);
        File.WriteAllText(path, "{\"a\":1}\n{\"b\":2}\n");
        var read = tail.ReadAppended();
        await Assert.That(read.Status).IsEqualTo(TailStatus.Ok);
        await Assert.That(read.Lines).IsEquivalentTo(new[] { "{\"b\":2}" });
    }

    [Test]
    public async Task Reads_a_file_another_handle_holds_open_for_writing() {
        var path = Tmp.PathTo("live.jsonl");
        using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        writer.Write(Encoding.UTF8.GetBytes("{\"a\":1}\n"));
        writer.Flush();

        var read = new JsonlTail(path).ReadAppended();

        await Assert.That(read.Status).IsEqualTo(TailStatus.Ok);
        await Assert.That(read.Lines).IsEquivalentTo(new[] { "{\"a\":1}" });
    }

    [Test]
    public async Task Split_consumes_only_through_the_last_newline() {
        var bytes = Encoding.UTF8.GetBytes("x\ny\nzz");
        var lines = JsonlTail.SplitCompleteLines(bytes, out var consumed);

        await Assert.That(lines).IsEquivalentTo(new[] { "x", "y" });
        await Assert.That(consumed).IsEqualTo(4);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/JsonlTailTests/*"`
Expected: build error — `JsonlTail` does not exist.

- [ ] **Step 3: Implement `JsonlTail`**

```csharp
using System.Text;

namespace Capacitor.Cli.Core;

public enum TailStatus { Ok, Reset, Missing, Failed }

public sealed record TailRead(IReadOnlyList<string> Lines, TailStatus Status, string? Failure = null);

/// Appended-lines reader over a JSONL file another process is writing. Every open shares
/// read/write/delete: a FileShare.Read open would deny the agent its own write handle on
/// Windows. Only a length regression resets the cursor — a replacement by a same-or-longer
/// file is read from the old cursor, which both vendors' append-only transcripts never produce.
public sealed class JsonlTail(string path) {
    long _cursor;

    public string Path { get; } = path;
    public long Cursor => _cursor;

    public TailRead ReadAppended() {
        try {
            using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var status = TailStatus.Ok;
            var length = stream.Length;
            if (length < _cursor) {
                _cursor = 0;
                status = TailStatus.Reset;
            }
            if (length == _cursor) return new TailRead([], status);

            stream.Position = _cursor;
            var buffer = new byte[length - _cursor];
            var read = 0;
            while (read < buffer.Length) {
                var n = stream.Read(buffer, read, buffer.Length - read);
                if (n == 0) break;
                read += n;
            }

            var lines = SplitCompleteLines(buffer.AsSpan(0, read), out var consumed);
            _cursor += consumed;
            return new TailRead(lines, status);
        } catch (FileNotFoundException) {
            return new TailRead([], TailStatus.Missing);
        } catch (DirectoryNotFoundException) {
            return new TailRead([], TailStatus.Missing);
        } catch (Exception ex) {
            return new TailRead([], TailStatus.Failed, ex.Message);
        }
    }

    /// Complete lines only; `consumed` stops after the last '\n' so an unterminated tail is
    /// re-read whole once its newline lands.
    public static List<string> SplitCompleteLines(ReadOnlySpan<byte> bytes, out int consumed) {
        var lines = new List<string>();
        consumed = 0;
        var start = 0;
        for (var i = 0; i < bytes.Length; i++) {
            if (bytes[i] != (byte)'\n') continue;
            var line = bytes[start..i];
            if (line.Length > 0 && line[^1] == (byte)'\r') line = line[..^1];
            if (!IsBlank(line)) lines.Add(Encoding.UTF8.GetString(line));
            start = i + 1;
            consumed = start;
        }
        return lines;
    }

    static bool IsBlank(ReadOnlySpan<byte> line) {
        foreach (var b in line) {
            if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r')) return false;
        }
        return true;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Same command. Expected: 7 PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/JsonlTail.cs test/Capacitor.Cli.Core.Tests.Unit/JsonlTailTests.cs
git commit -m "Add a sharing-safe appended-lines reader for agent transcripts

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 4: Projection seam and the Claude transcript projection

**Files:**
- Create: `src/Capacitor.Cli.Core/TranscriptProjection.cs`
- Create: `src/Capacitor.Cli.Core/Harness/Claude/ClaudeTranscriptEvents.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/TranscriptProjectionTests.cs`, `test/Capacitor.Cli.Core.Tests.Unit/Harness/Claude/ClaudeTranscriptEventsTests.cs`

**Interfaces:**
- Consumes: `AcpEventEnvelope`, `AcpEventKind` (`Capacitor.Cli.Core`, `Models.cs`), `JsonElementExtensions` (Task 2).
- Produces:
  - `public interface ITranscriptProjection { IReadOnlyList<AcpEventEnvelope> Project(string line); }`
  - `public static class TranscriptProjection { public static ITranscriptProjection? For(string vendor); }` (Task 5 registers Codex; this task registers Claude only)
  - `internal static class TranscriptProjectionText` with `const int ToolResultCap = 4096`, `string Cap(string)`, `string JoinTextBlocks(JsonElement array, string blockType, string textProperty = "text")`, `string WrapAsObject(string property, JsonElement value)`, `string WrapAsObject(string property, string value)`.
  - `public sealed class ClaudeTranscriptEvents : ITranscriptProjection { public static readonly ClaudeTranscriptEvents Instance; }` with `internal static string StripWrappers(string)`.

- [ ] **Step 1: Write the failing tests**

`TranscriptProjectionTests.cs`:

```csharp
namespace Capacitor.Cli.Core.Tests.Unit;

public class TranscriptProjectionTests {
    [Test]
    public async Task For_is_case_insensitive_and_null_for_an_unknown_vendor() {
        await Assert.That(TranscriptProjection.For("claude")).IsNotNull();
        await Assert.That(TranscriptProjection.For("Claude")).IsSameReferenceAs(TranscriptProjection.For("claude")!);
        await Assert.That(TranscriptProjection.For("gemini")).IsNull();
    }

    [Test]
    public async Task Cap_keeps_at_most_4096_units_including_the_marker_and_never_splits_a_pair() {
        var plain = new string('a', 5000);
        var capped = TranscriptProjectionText.Cap(plain);
        await Assert.That(capped.Length).IsEqualTo(4096);
        await Assert.That(capped[^1]).IsEqualTo('…');

        // 4094 units, then an astral pair straddling the cut position 4095.
        var astral = new string('a', 4094) + "😀" + new string('b', 100);
        var cappedAstral = TranscriptProjectionText.Cap(astral);
        await Assert.That(cappedAstral.Length).IsEqualTo(4095);
        await Assert.That(char.IsHighSurrogate(cappedAstral[^2])).IsFalse();
        await Assert.That(cappedAstral[^1]).IsEqualTo('…');

        await Assert.That(TranscriptProjectionText.Cap("short")).IsEqualTo("short");
        await Assert.That(TranscriptProjectionText.Cap(new string('a', 4096)).Length).IsEqualTo(4096);
    }

    [Test]
    public async Task WrapAsObject_builds_a_json_object_around_any_value() {
        using var doc = System.Text.Json.JsonDocument.Parse("""[1,"x",null]""");
        await Assert.That(TranscriptProjectionText.WrapAsObject("input", doc.RootElement)).IsEqualTo("""{"input":[1,"x",null]}""");
        await Assert.That(TranscriptProjectionText.WrapAsObject("arguments", "raw \"q\"")).IsEqualTo("""{"arguments":"raw "q""}""");
    }
}
```

`Harness/Claude/ClaudeTranscriptEventsTests.cs`:

```csharp
using System.Text.Json;
using Capacitor.Cli.Core.Harness.Claude;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Claude;

public class ClaudeTranscriptEventsTests {
    static IReadOnlyList<AcpEventEnvelope> P(string line) => ClaudeTranscriptEvents.Instance.Project(line);

    [Test]
    public async Task String_user_content_is_one_user_message_with_its_timestamp() {
        var e = P("""{"type":"user","message":{"role":"user","content":"hello"},"timestamp":"2026-08-26T12:00:00Z"}""");
        await Assert.That(e).HasCount().EqualTo(1);
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
        await Assert.That(stripped).HasCount().EqualTo(1);
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
        var capped = P($$"""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t3","content":"{{big}}"}]}}""");
        await Assert.That(capped[0].ToolResult!.Length).IsEqualTo(4096);
    }

    [Test]
    public async Task Assistant_blocks_map_to_text_thinking_and_tool_call() {
        var line = """{"type":"assistant","timestamp":"2026-08-26T12:00:01Z","message":{"model":"claude-fable-5","content":[{"type":"thinking","thinking":"hmm"},{"type":"text","text":"Hi"},{"type":"tool_use","id":"toolu_1","name":"Bash","input":{"command":"ls"}}]}}""";
        var e = P(line);

        await Assert.That(e).HasCount().EqualTo(3);
        await Assert.That(e[0].Kind).IsEqualTo(AcpEventKind.AssistantThinking);
        await Assert.That(e[0].Text).IsEqualTo("hmm");
        await Assert.That(e[1].Kind).IsEqualTo(AcpEventKind.AssistantText);
        await Assert.That(e[1].Text).IsEqualTo("Hi");
        await Assert.That(e[1].Model).IsEqualTo("claude-fable-5");
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
            var e = P($$"""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t","name":"X","input":{{input}}}]}}""");
            await Assert.That(e[0].ToolInputJson).IsEqualTo(expected);
            using var doc = JsonDocument.Parse(e[0].ToolInputJson!);
            await Assert.That(doc.RootElement.ValueKind).IsEqualTo(JsonValueKind.Object);
        }
    }

    [Test]
    public async Task Every_other_record_type_and_malformed_input_project_to_nothing() {
        foreach (var type in new[] { "attachment", "summary", "system", "file-history-snapshot", "file-history-delta", "mode", "permission-mode", "last-prompt", "ai-title", "atis-latch", "worktree-state", "queue-operation", "progress", "unknown-future" })
            await Assert.That(P($$"""{"type":"{{type}}","message":{"content":"x"}}""")).IsEmpty().Because(type);

        await Assert.That(P("not json")).IsEmpty();
        await Assert.That(P("[1,2]")).IsEmpty();
        await Assert.That(P("""{"type":"user","message":{"content":42}}""")).IsEmpty();
        await Assert.That(P("""{"type":"assistant","message":{"content":[{"type":"text","text":7}]}}""")).IsEmpty();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/*Transcript*/*"`
Expected: build errors — the types do not exist.

- [ ] **Step 3: Implement the seam and shared text helpers**

`src/Capacitor.Cli.Core/TranscriptProjection.cs`:

```csharp
using System.Text;
using System.Text.Json;
using Capacitor.Cli.Core.Harness.Claude;

namespace Capacitor.Cli.Core;

/// One transcript line in, zero or more canonical chat events out. Stateless: the consumer
/// orders by arrival and pairs tool calls to results by id itself.
public interface ITranscriptProjection {
    IReadOnlyList<AcpEventEnvelope> Project(string line);
}

/// The one registration site: a vendor's transcript projection lives under Harness/<Vendor>/
/// and is named here, nowhere else.
public static class TranscriptProjection {
    public static ITranscriptProjection? For(string vendor) => vendor.ToLowerInvariant() switch {
        "claude" => ClaudeTranscriptEvents.Instance,
        _        => null,
    };
}

/// Output rules both projections share, so an envelope reads the same whichever vendor wrote it.
internal static class TranscriptProjectionText {
    public const int ToolResultCap = 4096;
    const string CapMarker = "…";

    /// At most ToolResultCap units including the marker; a cut that would split a surrogate
    /// pair drops the high half too, so the result can be one unit short of the cap.
    public static string Cap(string text) {
        if (text.Length <= ToolResultCap) return text;
        var cut = ToolResultCap - CapMarker.Length;
        if (char.IsHighSurrogate(text[cut - 1])) cut--;
        return string.Concat(text.AsSpan(0, cut), CapMarker);
    }

    public static string JoinTextBlocks(JsonElement array, string blockType, string textProperty = "text") {
        var sb = new StringBuilder();
        foreach (var block in array.EnumerateArray()) {
            if (block.Str("type") != blockType || block.Str(textProperty) is not { } text) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(text);
        }
        return sb.ToString();
    }

    public static string WrapAsObject(string property, JsonElement value) {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) {
            writer.WriteStartObject();
            writer.WritePropertyName(property);
            value.WriteTo(writer);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static string WrapAsObject(string property, string value) {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) {
            writer.WriteStartObject();
            writer.WriteString(property, value);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
```

`src/Capacitor.Cli.Core/Harness/Claude/ClaudeTranscriptEvents.cs`:

```csharp
using System.Text.Json;
using System.Text.RegularExpressions;
using static Capacitor.Cli.Core.TranscriptProjectionText;

namespace Capacitor.Cli.Core.Harness.Claude;

/// Claude Code's project transcript (`~/.claude/projects/<slug>/<session>.jsonl`): one JSON
/// record per line, `type` at the root, the API message under `message`.
public sealed partial class ClaudeTranscriptEvents : ITranscriptProjection {
    public static readonly ClaudeTranscriptEvents Instance = new();

    ClaudeTranscriptEvents() { }

    public IReadOnlyList<AcpEventEnvelope> Project(string line) {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); } catch (JsonException) { return []; }

        using (doc) {
            var root = doc.RootElement;
            if (!root.IsObject || root.Bool("isSidechain") == true) return [];
            var ts = root.Str("timestamp");
            return root.Str("type") switch {
                "user"      => root.Bool("isMeta") == true ? [] : ProjectUser(root, ts),
                "assistant" => ProjectAssistant(root, ts),
                _           => [],
            };
        }
    }

    static List<AcpEventEnvelope> ProjectUser(JsonElement root, string? ts) {
        var result = new List<AcpEventEnvelope>();
        if (root.Obj("message") is not { } message) return result;

        if (message.Str("content") is { } text) {
            AddUserText(result, text, ts);
            return result;
        }
        if (message.Arr("content") is not { } blocks) return result;

        foreach (var block in blocks.EnumerateArray()) {
            switch (block.Str("type")) {
                case "text":
                    if (block.Str("text") is { } t) AddUserText(result, t, ts);
                    break;
                case "tool_result":
                    result.Add(new AcpEventEnvelope(
                        Kind: AcpEventKind.ToolResult,
                        ToolCallId: block.Str("tool_use_id"),
                        ToolResult: Cap(ToolResultText(block)),
                        ToolIsError: block.Bool("is_error") == true,
                        TimestampIso: ts));
                    break;
            }
        }
        return result;
    }

    static string ToolResultText(JsonElement block) =>
        block.Str("content") ?? (block.Arr("content") is { } blocks ? JoinTextBlocks(blocks, "text") : "");

    static void AddUserText(List<AcpEventEnvelope> result, string raw, string? ts) {
        var text = StripWrappers(raw);
        if (text.Length == 0) return;
        result.Add(new AcpEventEnvelope(Kind: AcpEventKind.UserMessage, Text: text, TimestampIso: ts));
    }

    static List<AcpEventEnvelope> ProjectAssistant(JsonElement root, string? ts) {
        var result = new List<AcpEventEnvelope>();
        if (root.Obj("message") is not { } message || message.Arr("content") is not { } blocks) return result;
        var model = message.Str("model");

        foreach (var block in blocks.EnumerateArray()) {
            switch (block.Str("type")) {
                case "text":
                    if (block.Str("text") is { Length: > 0 } text)
                        result.Add(new AcpEventEnvelope(Kind: AcpEventKind.AssistantText, Text: text, Model: model, TimestampIso: ts));
                    break;
                case "thinking": {
                    var thinking = block.Str("thinking");
                    result.Add(new AcpEventEnvelope(
                        Kind: AcpEventKind.AssistantThinking,
                        Text: string.IsNullOrEmpty(thinking) ? null : thinking,
                        ThinkingEncrypted: string.IsNullOrEmpty(thinking),
                        Model: model, TimestampIso: ts));
                    break;
                }
                case "tool_use":
                    result.Add(new AcpEventEnvelope(
                        Kind: AcpEventKind.ToolCall,
                        ToolCallId: block.Str("id"),
                        ToolName: block.Str("name"),
                        ToolInputJson: InputJson(block),
                        Model: model, TimestampIso: ts));
                    break;
            }
        }
        return result;
    }

    // ToolInputJson must always be a JSON object string: a non-object input is wrapped
    // rather than copied, and an absent one becomes the empty object.
    static string InputJson(JsonElement block) =>
        block.Obj("input") is { } obj ? obj.GetRawText()
        : block.Prop("input") is { } value ? WrapAsObject("input", value)
        : "{}";

    /// Removes the blocks Claude Code injects around a user turn — reminders and slash-command
    /// echoes — so only what the user typed remains.
    internal static string StripWrappers(string text) => Wrappers().Replace(text, "").Trim();

    [GeneratedRegex(@"<(system-reminder|command-name|command-message|command-args|local-command-stdout|local-command-caveat)>.*?</\1>", RegexOptions.Singleline)]
    private static partial Regex Wrappers();
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Same command. Expected: all tests in both classes PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/TranscriptProjection.cs src/Capacitor.Cli.Core/Harness/Claude/ClaudeTranscriptEvents.cs test/Capacitor.Cli.Core.Tests.Unit/TranscriptProjectionTests.cs test/Capacitor.Cli.Core.Tests.Unit/Harness/Claude/ClaudeTranscriptEventsTests.cs
git commit -m "Project Claude transcript lines to chat envelopes

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 5: Codex rollout projection, registry, AOT check

**Files:**
- Create: `src/Capacitor.Cli.Core/Harness/Codex/CodexRolloutEvents.cs`
- Modify: `src/Capacitor.Cli.Core/TranscriptProjection.cs` (register `"codex"`)
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Harness/Codex/CodexRolloutEventsTests.cs`, extend `TranscriptProjectionTests`

**Interfaces:**
- Produces: `public sealed class CodexRolloutEvents : ITranscriptProjection { public static readonly CodexRolloutEvents Instance; }`; `TranscriptProjection.For("codex")` returns it.

- [ ] **Step 1: Write the failing tests**

`Harness/Codex/CodexRolloutEventsTests.cs`:

```csharp
using System.Text.Json;
using Capacitor.Cli.Core.Harness.Codex;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Codex;

public class CodexRolloutEventsTests {
    static IReadOnlyList<AcpEventEnvelope> P(string line) => CodexRolloutEvents.Instance.Project(line);

    static string Item(string payload, string ts = "2026-08-25T00:00:00Z") =>
        $$"""{"timestamp":"{{ts}}","ordinal":1,"type":"response_item","payload":{{payload}}}""";

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

        foreach (var e in new[] { fn[0], nonObject[0], custom[0] }) {
            using var doc = JsonDocument.Parse(e.ToolInputJson!);
            await Assert.That(doc.RootElement.ValueKind).IsEqualTo(JsonValueKind.Object);
        }
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
            await Assert.That(P($$"""{"type":"{{type}}","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"x"}]}}""")).IsEmpty().Because(type);

        await Assert.That(P(Item("""{"type":"agent_message","content":[{"type":"input_text","text":"x"}]}"""))).IsEmpty();
        await Assert.That(P("garbage")).IsEmpty();
        await Assert.That(P(Item("""{"type":"message","role":"user","content":"not-an-array"}"""))).IsEmpty();
    }
}
```

Append to `TranscriptProjectionTests`:

```csharp
    [Test]
    public async Task For_registers_codex() {
        await Assert.That(TranscriptProjection.For("CODEX")).IsSameReferenceAs(Capacitor.Cli.Core.Harness.Codex.CodexRolloutEvents.Instance);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/*Codex*/*"`
Expected: build error — `CodexRolloutEvents` does not exist.

- [ ] **Step 3: Implement the Codex projection and register it**

`src/Capacitor.Cli.Core/Harness/Codex/CodexRolloutEvents.cs`:

```csharp
using System.Text.Json;
using static Capacitor.Cli.Core.TranscriptProjectionText;

namespace Capacitor.Cli.Core.Harness.Codex;

/// Codex's rollout (`~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl`): an envelope per line with
/// `type` and `payload`; only `response_item` payloads are conversation, the rest is telemetry.
public sealed class CodexRolloutEvents : ITranscriptProjection {
    public static readonly CodexRolloutEvents Instance = new();

    static readonly string[] InjectedPreludes = [
        "<environment_context>", "# AGENTS.md instructions", "<turn_aborted>", "<user_instructions>", "<permissions instructions>",
    ];

    CodexRolloutEvents() { }

    public IReadOnlyList<AcpEventEnvelope> Project(string line) {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); } catch (JsonException) { return []; }

        using (doc) {
            var root = doc.RootElement;
            if (!root.IsObject || root.Str("type") != "response_item" || root.Obj("payload") is not { } payload) return [];
            var ts = root.Str("timestamp");

            return payload.Str("type") switch {
                "message"          => ProjectMessage(payload, ts),
                "function_call"    => [new AcpEventEnvelope(Kind: AcpEventKind.ToolCall, ToolCallId: payload.Str("call_id"), ToolName: payload.Str("name"), ToolInputJson: ArgumentsJson(payload.Str("arguments")), TimestampIso: ts)],
                "custom_tool_call" => [new AcpEventEnvelope(Kind: AcpEventKind.ToolCall, ToolCallId: payload.Str("call_id"), ToolName: payload.Str("name"), ToolInputJson: WrapAsObject("input", payload.Str("input") ?? ""), TimestampIso: ts)],
                "function_call_output" or "custom_tool_call_output"
                                   => [new AcpEventEnvelope(Kind: AcpEventKind.ToolResult, ToolCallId: payload.Str("call_id"), ToolResult: Cap(OutputText(payload)), TimestampIso: ts)],
                "reasoning"        => [Reasoning(payload, ts)],
                _                  => [],
            };
        }
    }

    static List<AcpEventEnvelope> ProjectMessage(JsonElement payload, string? ts) {
        if (payload.Arr("content") is not { } blocks) return [];
        switch (payload.Str("role")) {
            case "user": {
                var text = JoinTextBlocks(blocks, "input_text");
                if (text.Length == 0 || IsInjectedPrelude(text)) return [];
                return [new AcpEventEnvelope(Kind: AcpEventKind.UserMessage, Text: text, TimestampIso: ts)];
            }
            case "assistant": {
                var text = JoinTextBlocks(blocks, "output_text");
                return text.Length == 0 ? [] : [new AcpEventEnvelope(Kind: AcpEventKind.AssistantText, Text: text, TimestampIso: ts)];
            }
            default:
                return [];
        }
    }

    static bool IsInjectedPrelude(string text) {
        var trimmed = text.TrimStart();
        foreach (var prelude in InjectedPreludes) {
            if (trimmed.StartsWith(prelude, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    static string ArgumentsJson(string? arguments) {
        if (arguments is not null) {
            try {
                using var doc = JsonDocument.Parse(arguments);
                if (doc.RootElement.IsObject) return doc.RootElement.GetRawText();
            } catch (JsonException) { }
        }
        return WrapAsObject("arguments", arguments ?? "");
    }

    static string OutputText(JsonElement payload) =>
        payload.Str("output") ?? (payload.Arr("output") is { } blocks ? JoinTextBlocks(blocks, "input_text") : "");

    static AcpEventEnvelope Reasoning(JsonElement payload, string? ts) {
        var summary = payload.Arr("summary") is { } blocks ? JoinTextBlocks(blocks, "summary_text") : "";
        return new AcpEventEnvelope(
            Kind: AcpEventKind.AssistantThinking,
            Text: summary.Length == 0 ? null : summary,
            ThinkingEncrypted: summary.Length == 0 && payload.Str("encrypted_content") is not null,
            TimestampIso: ts);
    }
}
```

In `TranscriptProjection.cs` add `using Capacitor.Cli.Core.Harness.Codex;` and the switch arm `"codex" => CodexRolloutEvents.Instance,`.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/*Transcript*/*"` and the Codex filter above. Expected: all PASS.

- [ ] **Step 5: AOT check**

Run: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`
Expected: no output (no IL2026/IL3050 warnings). If a warning names `ClaudeTranscriptEvents`/`CodexRolloutEvents`/`TranscriptProjectionText`, the offending call is reflection-based and must be replaced (only `JsonDocument`, `Utf8JsonWriter`, `GeneratedRegex` are allowed).

- [ ] **Step 6: Commit**

```bash
git add src/Capacitor.Cli.Core/Harness/Codex/CodexRolloutEvents.cs src/Capacitor.Cli.Core/TranscriptProjection.cs test/Capacitor.Cli.Core.Tests.Unit/Harness/Codex/CodexRolloutEventsTests.cs test/Capacitor.Cli.Core.Tests.Unit/TranscriptProjectionTests.cs
git commit -m "Project Codex rollout lines to chat envelopes

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 6: Claude locator returns a link-resolved winner

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Harness/Claude/SessionTranscriptLocator.cs`
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Harness/Claude/SessionTranscriptLocatorTests.cs`

**Interfaces:**
- Produces: `internal static (string SessionId, string Path)? TryLocateWinner(string projectDir, string worktreePath, DateTime spawnedAtUtc, ISet<string>? ruledOut = null)`; `TryLocate` keeps its signature and delegates. `internal static string ResolveDirectory(string projectDir)` (final link target, or the input).

- [ ] **Step 1: Write the failing tests**

Append to `SessionTranscriptLocatorTests`:

```csharp
    // ── TryLocateWinner: the matched file, link-resolved ────────────────

    static string TranscriptLine(string cwd) => $$"""{"type":"user","cwd":"{{cwd}}","sessionId":"abc","message":{"content":"hi"}}""";

    [Test]
    public async Task TryLocateWinner_returns_id_and_the_matched_path() {
        using var tmp = new TempDir();
        var projectDir = tmp.CreateDir("projects", "-repo").Path;
        var worktree = tmp.PathTo("wt");
        var file = tmp.CreateFile(["projects", "-repo", "0123456789abcdef0123456789abcdef.jsonl"], TranscriptLine(worktree) + "\n");

        var winner = SessionTranscriptLocator.TryLocateWinner(projectDir, worktree, DateTime.UtcNow.AddMinutes(-1));

        await Assert.That(winner?.SessionId).IsEqualTo("0123456789abcdef0123456789abcdef");
        await Assert.That(winner?.Path).IsEqualTo(file);
    }

    [Test]
    public async Task Winner_through_a_symlinked_project_dir_survives_the_symlink_going_away() {
        using var tmp = new TempDir();
        var real = tmp.CreateDir("projects", "-source").Path;
        var link = tmp.PathTo("projects", "-worktree");
        Directory.CreateSymbolicLink(link, real);
        var worktree = tmp.PathTo("wt");
        tmp.CreateFile(["projects", "-source", "0123456789abcdef0123456789abcdef.jsonl"], TranscriptLine(worktree) + "\n");

        var winner = SessionTranscriptLocator.TryLocateWinner(link, worktree, DateTime.UtcNow.AddMinutes(-1));

        await Assert.That(winner?.Path).IsEqualTo(Path.Combine(real, "0123456789abcdef0123456789abcdef.jsonl"));

        Directory.Delete(link);
        File.AppendAllText(winner!.Value.Path, TranscriptLine(worktree) + "\n");
        await Assert.That(File.ReadLines(winner.Value.Path).Count()).IsEqualTo(2);
    }

    [Test]
    public async Task A_freshly_appended_transcript_with_a_foreign_cwd_is_never_a_winner() {
        // The boundary a Claude --resume into a new worktree hits: the resumed transcript's
        // early records keep their original cwd, and no amount of fresh writes changes that.
        using var tmp = new TempDir();
        var projectDir = tmp.CreateDir("projects", "-repo").Path;
        tmp.CreateFile(["projects", "-repo", "0123456789abcdef0123456789abcdef.jsonl"], TranscriptLine(tmp.PathTo("original-cwd")) + "\n");
        var ruledOut = new HashSet<string>();

        var winner = SessionTranscriptLocator.TryLocateWinner(projectDir, tmp.PathTo("new-worktree"), DateTime.UtcNow.AddMinutes(-1), ruledOut);

        await Assert.That(winner).IsNull();
        await Assert.That(ruledOut).HasCount().EqualTo(1);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj -- --treenode-filter "/*/*/SessionTranscriptLocatorTests/*"`
Expected: build error — `TryLocateWinner` does not exist.

- [ ] **Step 3: Implement the winner and link resolution**

Replace `TryLocate` in `SessionTranscriptLocator.cs` with:

```csharp
    public static string? TryLocate(string projectDir, string worktreePath, DateTime spawnedAtUtc, ISet<string>? ruledOut = null) =>
        TryLocateWinner(projectDir, worktreePath, spawnedAtUtc, ruledOut)?.SessionId;

    /// The matched transcript's id AND its path, link-resolved: the per-worktree project dir is
    /// a symlink the launcher deletes at cleanup, and a path through it would die with the
    /// process while the file lives on in the source repo's project dir.
    internal static (string SessionId, string Path)? TryLocateWinner(string projectDir, string worktreePath, DateTime spawnedAtUtc, ISet<string>? ruledOut = null) {
        if (!Directory.Exists(projectDir)) return null;

        foreach (var file in Directory.EnumerateFiles(projectDir, "*.jsonl")) {
            if (ruledOut?.Contains(file) == true) continue;

            try {
                if (SessionIdFromFileName(file) is not { } sessionId) {
                    ruledOut?.Add(file);
                    continue;
                }

                if (!IsNewEnough(File.GetCreationTimeUtc(file), File.GetLastWriteTimeUtc(file), spawnedAtUtc)) continue;

                switch (MatchTranscript(ReadFirstLines(file), worktreePath, DefaultPathComparison)) {
                    case CwdMatch.Yes: return (sessionId, Path.Combine(ResolveDirectory(projectDir), Path.GetFileName(file)));
                    case CwdMatch.No:  ruledOut?.Add(file); break;
                }
            } catch {
                // Candidate vanished mid-scan, is locked, or is otherwise unreadable — the
                // caller polls, so it is retried next tick.
            }
        }

        return null;
    }

    /// The directory a symlinked project dir points at (final target), or the directory itself.
    internal static string ResolveDirectory(string projectDir) {
        try {
            return new DirectoryInfo(projectDir).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? projectDir;
        } catch (IOException) {
            return projectDir;
        }
    }
```

Keep the existing doc comment on `TryLocate`'s `ruledOut` parameter; move it onto `TryLocateWinner`.

- [ ] **Step 4: Run the tests to verify they pass**

Same command. Expected: every `SessionTranscriptLocatorTests` PASS (the pre-existing ones included).

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Harness/Claude/SessionTranscriptLocator.cs test/Capacitor.Cli.Daemon.Tests.Unit/Harness/Claude/SessionTranscriptLocatorTests.cs
git commit -m "Return the link-resolved transcript path from the Claude locator

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 7: `TranscriptDiscovery`

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Services/TranscriptDiscovery.cs`
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/TranscriptDiscoveryTests.cs`

**Interfaces:**
- Produces: `internal sealed class TranscriptDiscovery(TimeProvider time, TimeSpan interval, TimeSpan timeout)` with `Task<bool> RunAsync(Func<ISet<string>, (string SessionId, string Path)?> locate, Func<(string SessionId, string Path), Task> onFound, CancellationToken ct)` — true when a winner was handed to `onFound`; false on deadline or cancellation; never throws.

- [ ] **Step 1: Write the failing tests**

```csharp
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class TranscriptDiscoveryTests {
    static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
    static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    [Test]
    public async Task A_winner_on_a_later_tick_is_handed_over_once() {
        var time = new FakeTimeProvider();
        var discovery = new TranscriptDiscovery(time, Interval, Timeout);
        var calls = 0;
        var found = new List<(string, string)>();

        var run = discovery.RunAsync(
            _ => ++calls >= 3 ? ("sid", "/t.jsonl") : null,
            w => { found.Add(w); return Task.CompletedTask; },
            CancellationToken.None);

        time.Advance(Interval);
        time.Advance(Interval);
        await Assert.That(await run).IsTrue();
        await Assert.That(found).IsEquivalentTo(new[] { ("sid", "/t.jsonl") });
        await Assert.That(calls).IsEqualTo(3);
    }

    [Test]
    public async Task The_ruled_out_set_persists_across_ticks() {
        var time = new FakeTimeProvider();
        var discovery = new TranscriptDiscovery(time, Interval, Timeout);
        ISet<string>? first = null, second = null;

        var run = discovery.RunAsync(
            set => { if (first is null) { first = set; set.Add("x"); return null; } second = set; return ("sid", "/p"); },
            _ => Task.CompletedTask, CancellationToken.None);

        time.Advance(Interval);
        await run;
        await Assert.That(second).IsSameReferenceAs(first!);
        await Assert.That(second!.Contains("x")).IsTrue();
    }

    [Test]
    public async Task The_deadline_ends_the_poll_without_a_handover() {
        var time = new FakeTimeProvider();
        var discovery = new TranscriptDiscovery(time, Interval, Timeout);
        var handed = false;

        var run = discovery.RunAsync(_ => null, _ => { handed = true; return Task.CompletedTask; }, CancellationToken.None);
        for (var elapsed = TimeSpan.Zero; elapsed <= Timeout; elapsed += Interval) time.Advance(Interval);

        await Assert.That(await run).IsFalse();
        await Assert.That(handed).IsFalse();
    }

    [Test]
    public async Task Cancellation_ends_the_poll_cleanly_without_a_final_locate() {
        var time = new FakeTimeProvider();
        var discovery = new TranscriptDiscovery(time, Interval, Timeout);
        using var cts = new CancellationTokenSource();
        var calls = 0;

        var run = discovery.RunAsync(_ => { calls++; return null; }, _ => Task.CompletedTask, cts.Token);
        cts.Cancel();

        await Assert.That(await run).IsFalse();
        await Assert.That(calls).IsEqualTo(1);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj -- --treenode-filter "/*/*/TranscriptDiscoveryTests/*"`
Expected: build error — `TranscriptDiscovery` does not exist.

- [ ] **Step 3: Implement**

```csharp
namespace Capacitor.Cli.Daemon.Services;

/// Polls a vendor's session tree for a freshly spawned agent's transcript until the file is
/// known, the deadline passes, or the agent goes away. Runs until the PATH is known — a
/// session id learned some other way is not a reason to stop, since the path is what the
/// desktop app reads.
internal sealed class TranscriptDiscovery(TimeProvider time, TimeSpan interval, TimeSpan timeout) {
    public async Task<bool> RunAsync(
            Func<ISet<string>, (string SessionId, string Path)?> locate,
            Func<(string SessionId, string Path), Task> onFound,
            CancellationToken ct) {
        var deadline = time.GetUtcNow() + timeout;
        var ruledOut = new HashSet<string>();

        try {
            while (time.GetUtcNow() < deadline) {
                if (ct.IsCancellationRequested) return false;
                if (locate(ruledOut) is { } winner) {
                    await onFound(winner).ConfigureAwait(false);
                    return true;
                }
                await Task.Delay(interval, time, ct).ConfigureAwait(false);
            }
        } catch (OperationCanceledException) {
            // The agent exited or the daemon is shutting down — nothing left to find.
        }
        return false;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Same command. Expected: 4 PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/TranscriptDiscovery.cs test/Capacitor.Cli.Daemon.Tests.Unit/Services/TranscriptDiscoveryTests.cs
git commit -m "Extract transcript discovery into a clock-driven poll

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 8: Orchestrator wiring — path on the agent, every PTY launch, the snapshot

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` (`AgentInstance.CodexRolloutPath` → `TranscriptPath`; `DetectSessionIdAsync`/`PollForSessionIdAsync` → discovery; test seams)
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.LocalIpc.cs` (`HandleLocalSpawnAsync`, `SnapshotAgentsForStatus`)
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/AgentStatusSnapshotTests.cs`, `test/Capacitor.Cli.Daemon.Tests.Unit/Services/AgentOrchestratorLocalAttachTests.cs`

**Interfaces:**
- Consumes: `TranscriptDiscovery` (Task 7), `SessionTranscriptLocator.TryLocateWinner` (Task 6), `CodexSessionRolloutLocator.TryLocateWinner` (existing).
- Produces: `AgentInstance.TranscriptPath` (`string?`, set once discovery wins); `AgentStatusDto.TranscriptPath` stamped in `SnapshotAgentsForStatus`; test seams `internal Task RunDiscoveryForTest(AgentInstance agent, Func<ISet<string>, (string SessionId, string Path)?> locate)` and `internal int DiscoveryStartsForTest`.

- [ ] **Step 1: Write the failing tests**

Append to `AgentStatusSnapshotTests` (inside the class; it already has the `Build()` fixture with a notifier):

```csharp
    [Test]
    public async Task Status_payload_carries_transcript_path_null_before_discovery_and_the_value_after() {
        var fixture = Build();
        var orch    = fixture.Orchestrator;
        try {
            var agent = orch.SeedAgentForTest("pty-1");

            var before = System.Text.Json.JsonSerializer.Serialize(orch.SnapshotAgentsForStatus()[0], StatusIpcJsonContext.Default.AgentStatusDto);
            await Assert.That(before).Contains("\"transcript_path\":null");

            var versionBefore = fixture.Notifier.Version;
            await orch.RunDiscoveryForTest(agent, _ => ("0123456789abcdef0123456789abcdef", "/home/u/.claude/projects/-repo/t.jsonl"));

            var after = System.Text.Json.JsonSerializer.Serialize(orch.SnapshotAgentsForStatus()[0], StatusIpcJsonContext.Default.AgentStatusDto);
            await Assert.That(after).Contains("\"transcript_path\":\"/home/u/.claude/projects/-repo/t.jsonl\"");
            await Assert.That(agent.SessionId).IsEqualTo("0123456789abcdef0123456789abcdef");
            await Assert.That(fixture.Notifier.Version).IsGreaterThan(versionBefore);
        } finally {
            await fixture.CleanupAsync();
        }
    }

    /// A session id learned elsewhere must not stop discovery: the path is the obligation.
    [Test]
    public async Task Discovery_sets_the_path_even_when_the_session_id_is_already_known() {
        var fixture = Build();
        var orch    = fixture.Orchestrator;
        try {
            var agent = orch.SeedAgentForTest("pty-2");
            agent.SessionId = "pre-known";

            await orch.RunDiscoveryForTest(agent, _ => ("other", "/t.jsonl"));

            await Assert.That(agent.SessionId).IsEqualTo("pre-known");
            await Assert.That(agent.TranscriptPath).IsEqualTo("/t.jsonl");
        } finally {
            await fixture.CleanupAsync();
        }
    }

    /// A private agent gets the path and the pulse with no server call in the way.
    [Test]
    public async Task A_private_agent_gets_its_path_and_pulse_without_server_reports() {
        var fixture = Build();
        var orch    = fixture.Orchestrator;
        try {
            var agent = orch.SeedAgentForTest("priv-1", isPrivate: true);
            var versionBefore = fixture.Notifier.Version;

            await orch.RunDiscoveryForTest(agent, _ => ("sid", "/p.jsonl"));

            await Assert.That(agent.TranscriptPath).IsEqualTo("/p.jsonl");
            await Assert.That(fixture.Notifier.Version).IsGreaterThan(versionBefore);
        } finally {
            await fixture.CleanupAsync();
        }
    }
```

Append to `AgentOrchestratorLocalAttachTests`:

```csharp
    [Test]
    public async Task Local_spawns_start_transcript_discovery_private_included() {
        using var tmp = new TempDir();
        var server    = new TripwireServerConnection();
        var pty       = new EnvCapturingPtyFactory();
        var launchers = new Dictionary<string, IHostedAgentLauncher> { ["claude"] = new SpyHostedAgentLauncher("claude", "spy-claude") };
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(server, pty, launchers);

        foreach (var isPrivate in new[] { false, true }) {
            var readBuf = new MemoryStream();
            await FrameCodec.WriteAsync(readBuf, LocalFrame.Detach(), default);
            readBuf.Position = 0;
            using var client = new DuplexTestStream(readBuf, new MemoryStream());

            var spawn = FrameCodec.Spawn("claude", WorkLocation.BorrowedCwd, isPrivate, tmp.Path, [], 80, 24);
            await orch.HandleLocalSpawnAsync(spawn, client, default);
        }

        await Assert.That(orch.DiscoveryStartsForTest).IsEqualTo(2);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj -- --treenode-filter "/*/*/AgentStatusSnapshotTests/*"`
Expected: build errors — `RunDiscoveryForTest`, `TranscriptPath`, `DiscoveryStartsForTest` do not exist.

- [ ] **Step 3: Rename the cached path and rewire discovery**

In `AgentOrchestrator.cs`:

1. On `AgentInstance`, replace `CodexRolloutPath` with:

```csharp
    /// The agent's own transcript — Claude's project .jsonl or Codex's rollout — resolved once
    /// by discovery and cached: the status payload and the Codex send-path probe both read it,
    /// and neither may scan a directory to do so. Null until discovery lands, and forever for a
    /// runtime that writes nothing the daemon locates.
    public string? TranscriptPath { get; set; }
```

   and update both Codex probe reads (`if (agent.CodexRolloutPath is { } rolloutPath)` and `if (agent.CodexRolloutPath is not { } rolloutPath …)`) to `agent.TranscriptPath`. Fix the `Title` doc comment's mention of `CodexRolloutPath` to `TranscriptPath`.

2. Add the seam counter beside the other `*ForTest` members:

```csharp
    int _discoveryStarts;
    internal int DiscoveryStartsForTest => Volatile.Read(ref _discoveryStarts);
```

3. Replace `DetectSessionIdAsync` and `PollForSessionIdAsync` (keep the two timing constants) with:

```csharp
    /// Locates the transcript a freshly spawned PTY agent writes — Claude's per-worktree
    /// project dir (a symlink onto the source repo's, shared with the user's own sessions,
    /// so the locator disambiguates by cwd), Codex's rollout tree (disambiguated by cwd and
    /// spawn time). A vendor without a locator is a no-op. Best-effort background work,
    /// cancelled with the agent; it never blocks a launch.
    Task DetectSessionIdAsync(AgentInstance agent, string vendor, DateTime spawnedAtUtc) {
        Func<ISet<string>, (string SessionId, string Path)?>? locate = vendor.ToLowerInvariant() switch {
            "claude" => ruledOut => SessionTranscriptLocator.TryLocateWinner(
                ClaudePaths.ProjectDir(agent.Worktree.Path), agent.Worktree.Path, spawnedAtUtc, ruledOut),
            "codex"  => ruledOut => CodexSessionRolloutLocator.TryLocateWinner(
                CodexPaths.Sessions, agent.Worktree.Path, spawnedAtUtc, ruledOut),
            _        => null,
        };
        if (locate is null) return Task.CompletedTask;

        Interlocked.Increment(ref _discoveryStarts);
        return RunDiscoveryAsync(agent, locate);
    }

    internal Task RunDiscoveryForTest(AgentInstance agent, Func<ISet<string>, (string SessionId, string Path)?> locate) =>
        RunDiscoveryAsync(agent, locate);

    async Task RunDiscoveryAsync(AgentInstance agent, Func<ISet<string>, (string SessionId, string Path)?> locate) {
        try {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(agent.ReadCts.Token, _shutdownCts.Token);
            var discovery = new TranscriptDiscovery(TimeProvider.System, SessionIdPollInterval, SessionIdPollTimeout);

            var found = await discovery.RunAsync(locate, async winner => {
                // Mutation first, pulse second — and the pulse before any server call, which can
                // stall on a reconnect and must never hold the app's status push hostage.
                agent.SessionId ??= winner.SessionId;
                agent.TranscriptPath = winner.Path;
                _statusNotifier.Pulse();
                LogSessionIdDetected(agent.Id, winner.SessionId);

                if (agent.IsPrivate) return;
                await _server.AppendAgentRunEventAsync(agent.Id, new AgentRunHeartbeat(winner.SessionId));
                await _server.AgentStatusChangedAsync(agent.Id, agent.Status, winner.SessionId);
            }, cts.Token);

            if (!found && !agent.ReadCts.IsCancellationRequested) LogSessionIdNotDetected(agent.Id, SessionIdPollTimeout.TotalSeconds);
        } catch (Exception ex) {
            LogSessionIdDetectFailed(ex, agent.Id);
        }
    }
```

   Check the two `Log*` method names still match the existing `[LoggerMessage]` declarations; keep them unchanged.

4. In `AgentOrchestrator.LocalIpc.cs`, `HandleLocalSpawnAsync`: capture `var spawnedAtUtc = DateTime.UtcNow;` immediately before `var pty = _ptyFactory.Spawn(...)`, and after `_ = ReadAgentOutputAsync(agent);` add `_ = DetectSessionIdAsync(agent, vendor, spawnedAtUtc);`.

5. In `SnapshotAgentsForStatus`, extend the DTO construction: `HasTerminal: a.Runtime.EmitsTerminalOutput, Title: a.Title, TranscriptPath: a.TranscriptPath))];`.

- [ ] **Step 4: Run the tests to verify they pass**

Run both filters (`AgentStatusSnapshotTests`, `AgentOrchestratorLocalAttachTests`), then the whole daemon suite: `dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj`. Expected: green.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.LocalIpc.cs test/Capacitor.Cli.Daemon.Tests.Unit/Services/AgentStatusSnapshotTests.cs test/Capacitor.Cli.Daemon.Tests.Unit/Services/AgentOrchestratorLocalAttachTests.cs
git commit -m "Discover every PTY agent's transcript and stamp it on the status wire

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 9: App pure helpers — Markdig reference, input encoder, link policy, tool detail

**Files:**
- Modify: `Directory.Packages.props`, `src/Capacitor.App/Capacitor.App.csproj`
- Create: `src/Capacitor.App/Services/TerminalInputEncoder.cs`, `src/Capacitor.App/Services/LinkPolicy.cs`, `src/Capacitor.App/ViewModels/ToolDetail.cs`
- Test: `test/Capacitor.App.Tests.Unit/TerminalInputEncoderTests.cs`, `LinkPolicyTests.cs`, `ToolDetailTests.cs`

**Interfaces:**
- Produces:
  - `public static class TerminalInputEncoder { public static readonly byte[] Submit; public static byte[] Paste(string text); }`
  - `public static class LinkPolicy { public static bool IsOpenable(string? url); }`
  - `public static class ToolDetail { public static string From(string? inputJson); }`

- [ ] **Step 1: Add the package**

In `Directory.Packages.props` add `<PackageVersion Include="Markdig" Version="1.3.2" />` (alphabetical, after `DynamicData`). In `Capacitor.App.csproj` add `<PackageReference Include="Markdig" />` after `DynamicData`. Run `dotnet build src/Capacitor.App/Capacitor.App.csproj` — expected: success.

- [ ] **Step 2: Write the failing tests**

`TerminalInputEncoderTests.cs`:

```csharp
using System.Text;
using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

public class TerminalInputEncoderTests {
    [Test]
    public async Task Paste_wraps_in_bracketed_paste_normalizes_crlf_and_drops_one_trailing_newline() {
        await Assert.That(Encoding.UTF8.GetString(TerminalInputEncoder.Paste("hi"))).IsEqualTo("\x1b[200~hi\x1b[201~");
        await Assert.That(Encoding.UTF8.GetString(TerminalInputEncoder.Paste("a\r\nb\n"))).IsEqualTo("\x1b[200~a\nb\x1b[201~");
        await Assert.That(Encoding.UTF8.GetString(TerminalInputEncoder.Paste("a\n\n"))).IsEqualTo("\x1b[200~a\n\x1b[201~");
        await Assert.That(TerminalInputEncoder.Submit).IsEquivalentTo("\r"u8.ToArray());
    }
}
```

`LinkPolicyTests.cs`:

```csharp
using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

public class LinkPolicyTests {
    [Test]
    public async Task Only_absolute_http_and_https_open() {
        await Assert.That(LinkPolicy.IsOpenable("https://example.com/x?y=1")).IsTrue();
        await Assert.That(LinkPolicy.IsOpenable("http://example.com")).IsTrue();
        foreach (var refused in new[] { "file:///etc/passwd", "javascript:alert(1)", "kcap://open", "docs/readme.md", "not a url", "", null })
            await Assert.That(LinkPolicy.IsOpenable(refused)).IsFalse().Because(refused ?? "null");
    }
}
```

`ToolDetailTests.cs`:

```csharp
using Capacitor.App.ViewModels;

namespace Capacitor.App.Tests.Unit;

public class ToolDetailTests {
    [Test]
    public async Task Picks_the_first_present_key_in_priority_order() {
        await Assert.That(ToolDetail.From("""{"command":"ls","description":"List files"}""")).IsEqualTo("List files");
        await Assert.That(ToolDetail.From("""{"file_path":"/a/b.cs","command":"x"}""")).IsEqualTo("x");
        await Assert.That(ToolDetail.From("""{"pattern":"*.cs"}""")).IsEqualTo("*.cs");
        await Assert.That(ToolDetail.From("""{"input":"const r = 1;"}""")).IsEqualTo("const r = 1;");
    }

    [Test]
    public async Task Keeps_the_first_line_and_cuts_at_80_characters() {
        await Assert.That(ToolDetail.From("""{"command":"first line\nsecond"}""")).IsEqualTo("first line");
        var longLine = new string('x', 100);
        var detail = ToolDetail.From($$"""{"command":"{{longLine}}"}""");
        await Assert.That(detail.Length).IsEqualTo(80);
        await Assert.That(detail[^1]).IsEqualTo('…');
    }

    [Test]
    public async Task Empty_when_nothing_applies() {
        await Assert.That(ToolDetail.From("""{"other":"x"}""")).IsEqualTo("");
        await Assert.That(ToolDetail.From("""{"command":"   "}""")).IsEqualTo("");
        await Assert.That(ToolDetail.From("not json")).IsEqualTo("");
        await Assert.That(ToolDetail.From(null)).IsEqualTo("");
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/{TerminalInputEncoderTests,LinkPolicyTests,ToolDetailTests}/*"`
Expected: build errors — types missing.

- [ ] **Step 4: Implement**

`Services/TerminalInputEncoder.cs`:

```csharp
using System.Text;

namespace Capacitor.App.Services;

/// The composer's bytes, in the shape the daemon's own PTY input path uses: one bracketed
/// paste so the TUI takes the text as a block, and a separate carriage return to submit it.
public static class TerminalInputEncoder {
    public static readonly byte[] Submit = "\r"u8.ToArray();

    public static byte[] Paste(string text) {
        var normalized = text.Replace("\r\n", "\n");
        if (normalized.EndsWith('\n')) normalized = normalized[..^1];
        return Encoding.UTF8.GetBytes("\x1b[200~" + normalized + "\x1b[201~");
    }
}
```

`Services/LinkPolicy.cs`:

```csharp
namespace Capacitor.App.Services;

/// The trust boundary for agent-authored links: the shell opener launches whatever it is
/// handed, so only absolute web URLs ever reach it.
public static class LinkPolicy {
    public static bool IsOpenable(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
```

`ViewModels/ToolDetail.cs`:

```csharp
using System.Text.Json;

namespace Capacitor.App.ViewModels;

/// The one-line detail a tool row shows beside its name, read from the call's input object.
public static class ToolDetail {
    const int MaxLength = 80;

    static readonly string[] Keys = [
        "description", "command", "cmd", "file_path", "path", "pattern", "query", "url", "skill", "prompt", "input",
    ];

    public static string From(string? inputJson) {
        if (string.IsNullOrEmpty(inputJson)) return "";
        try {
            using var doc = JsonDocument.Parse(inputJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return "";
            foreach (var key in Keys) {
                if (doc.RootElement.TryGetProperty(key, out var value)
                    && value.ValueKind == JsonValueKind.String
                    && value.GetString() is { } s && s.Trim().Length > 0)
                    return FirstLine(s);
            }
        } catch (JsonException) { }
        return "";
    }

    static string FirstLine(string text) {
        var line = text.Trim();
        var newline = line.IndexOfAny(['\r', '\n']);
        if (newline >= 0) line = line[..newline].TrimEnd();
        return line.Length <= MaxLength ? line : string.Concat(line.AsSpan(0, MaxLength - 1), "…");
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Same command. Expected: all PASS.

- [ ] **Step 6: Commit**

```bash
git add Directory.Packages.props src/Capacitor.App/Capacitor.App.csproj src/Capacitor.App/Services/TerminalInputEncoder.cs src/Capacitor.App/Services/LinkPolicy.cs src/Capacitor.App/ViewModels/ToolDetail.cs test/Capacitor.App.Tests.Unit/TerminalInputEncoderTests.cs test/Capacitor.App.Tests.Unit/LinkPolicyTests.cs test/Capacitor.App.Tests.Unit/ToolDetailTests.cs
git commit -m "Add the composer's input encoder, link policy and tool detail

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 10: The terminal's send gate

**Files:**
- Modify: `src/Capacitor.App/ViewModels/TerminalTabViewModel.cs`
- Modify: `src/Capacitor.App/Services/TerminalAttach.cs` (add `SendAvailability`)
- Test: `test/Capacitor.App.Tests.Unit/TerminalSendGateTests.cs` (create), `test/Capacitor.App.Tests.Unit/TerminalTabViewModelTests.cs` (extend the back-to-back reattach test)

**Interfaces:**
- Consumes: `TerminalInputEncoder` (Task 9), `FakeTerminalAttachClient` (`SentInput`, `DisposeGate`, `HangDetachForever`, `DetachGate`, `Result`, `TriggerAttached`).
- Produces on `TerminalTabViewModel`: `bool TrySendText(string text)`, `bool CanAcceptText`, `bool SendInFlight`, `SendAvailability SendAvailability`, `internal int OpeningTokenForTesting`, `internal bool SendGateOpenForTesting`, `internal Task? PendingDeliveryForTesting`. In `TerminalAttach.cs`: `public enum SendAvailability { Ready, Sending, Transitioning, ReadOnly, Connecting, Reattach, Ended, NoTerminal }`.

- [ ] **Step 1: Write the failing tests**

`TerminalSendGateTests.cs`:

```csharp
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

/// The composer's send gate on TerminalTabViewModel: acceptance, the two-write delivery, and
/// every window in which a send must be refused or a pending CR dropped. Same RunOnUiAsync +
/// [NotInParallel("AvaloniaSession")] discipline as TerminalTabViewModelTests.
public class TerminalSendGateTests {
    static readonly byte[] Paste = TerminalInputEncoder.Paste("hello");
    static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(150);

    static async Task<(FakeDaemonClientService Daemon, FakeTerminalAttachClientFactory Factory, FakeTimeProvider Time, TerminalTabViewModel Vm, FakeTerminalAttachClient Client)>
            BuildAttachedAsync(string? readOnlyReason = null, Action<FakeTerminalAttachClient>? configureNext = null) {
        var daemon = new FakeDaemonClientService();
        var factory = new FakeTerminalAttachClientFactory { ConfigureNext = configureNext };
        var time = new FakeTimeProvider();
        var vm = new TerminalTabViewModel("a1", daemon, factory.Factory, () => new FakeTerminalSurface(), time);
        daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true));
        await (vm.PendingResolveWorkForTesting ?? Task.CompletedTask);
        var client = factory.Created.Single();
        await client.TriggerAttached([], readOnlyReason);
        return (daemon, factory, time, vm, client);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Accepted_send_writes_the_paste_then_the_cr_after_the_delay_on_the_same_client() {
        await RunOnUiAsync(async () => {
            var (_, _, time, vm, client) = await BuildAttachedAsync();
            await Assert.That(vm.CanAcceptText).IsTrue();
            await Assert.That(vm.SendAvailability).IsEqualTo(SendAvailability.Ready);

            await Assert.That(vm.TrySendText("hello")).IsTrue();
            await Assert.That(vm.SendInFlight).IsTrue();
            await Assert.That(vm.SendAvailability).IsEqualTo(SendAvailability.Sending);
            await WaitUntilAsync(() => client.SentInput.Count == 1, what: "paste written");
            await Assert.That(client.SentInput[0]).IsEquivalentTo(Paste);

            time.Advance(Delay);
            await vm.PendingDeliveryForTesting!;
            await Assert.That(client.SentInput).HasCount().EqualTo(2);
            await Assert.That(client.SentInput[1]).IsEquivalentTo(TerminalInputEncoder.Submit);
            await Assert.That(vm.SendInFlight).IsFalse();
            await Assert.That(vm.CanAcceptText).IsTrue();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_second_send_is_refused_while_one_is_in_flight_then_goes_through() {
        await RunOnUiAsync(async () => {
            var (_, _, time, vm, client) = await BuildAttachedAsync();

            await Assert.That(vm.TrySendText("A")).IsTrue();
            await Assert.That(vm.TrySendText("B")).IsFalse();
            await Assert.That(vm.CanAcceptText).IsFalse();

            time.Advance(Delay);
            await vm.PendingDeliveryForTesting!;
            await Assert.That(vm.TrySendText("B")).IsTrue();
            time.Advance(Delay);
            await vm.PendingDeliveryForTesting!;

            await Assert.That(client.SentInput.Select(b => System.Text.Encoding.UTF8.GetString(b))).IsEquivalentTo(
                new[] { "\x1b[200~A\x1b[201~", "\r", "\x1b[200~B\x1b[201~", "\r" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Refused_unless_attached_read_write() {
        await RunOnUiAsync(async () => {
            var (_, _, _, ro, _) = await BuildAttachedAsync(readOnlyReason: "review");
            await Assert.That(ro.TrySendText("x")).IsFalse();
            await Assert.That(ro.SendAvailability).IsEqualTo(SendAvailability.ReadOnly);

            var daemon = new FakeDaemonClientService();
            var factory = new FakeTerminalAttachClientFactory();
            var connecting = new TerminalTabViewModel("a2", daemon, factory.Factory, () => new FakeTerminalSurface(), new FakeTimeProvider());
            daemon.Agents.AddOrUpdate(Agent("a2", "claude", hasTerminal: true));
            await (connecting.PendingResolveWorkForTesting ?? Task.CompletedTask);
            await Assert.That(connecting.TrySendText("x")).IsFalse();
            await Assert.That(connecting.SendAvailability).IsEqualTo(SendAvailability.Connecting);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_send_during_a_reattach_disposal_or_a_detach_write_is_refused_while_state_still_reads_attached() {
        await RunOnUiAsync(async () => {
            // Reattach: the old client's disposal is held open, State still Attached.
            var gate = new TaskCompletionSource();
            var (_, factory, _, vm, client) = await BuildAttachedAsync(configureNext: c => c.DisposeGate = gate);
            var reattach = vm.ReattachCommand.Execute().ToTask();
            await WaitUntilAsync(() => client.DisposeCalls == 1, what: "old client disposing");

            await Assert.That(vm.State.Phase).IsEqualTo(TerminalSessionPhase.Attached);
            await Assert.That(vm.CanAcceptText).IsFalse();
            await Assert.That(vm.SendAvailability).IsEqualTo(SendAvailability.Transitioning);
            await Assert.That(vm.TrySendText("x")).IsFalse();

            gate.SetResult();
            await reattach;
            var client2 = factory.Created[^1];
            await Assert.That(vm.CanAcceptText).IsFalse(); // Connecting: still closed
            await client2.TriggerAttached([]);
            await Assert.That(vm.CanAcceptText).IsTrue();

            // Detach: the detach write is held open, State still Attached.
            var (_, _, _, vm2, client3) = await BuildAttachedAsync(configureNext: c => c.HangDetachForever = true);
            var detach = vm2.DetachCommand.Execute().ToTask();
            await WaitUntilAsync(() => client3.DetachCalls == 1, what: "detach in flight");
            await Assert.That(vm2.State.Phase).IsEqualTo(TerminalSessionPhase.Attached);
            await Assert.That(vm2.TrySendText("x")).IsFalse();
            await Assert.That(vm2.SendAvailability).IsEqualTo(SendAvailability.Transitioning);
            client3.DetachGate.SetResult();
            await detach;
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Invalidation_during_the_delay_drops_the_cr_and_clears_in_flight() {
        await RunOnUiAsync(async () => {
            foreach (var invalidate in new Func<TerminalTabViewModel, FakeTerminalAttachClient, FakeDaemonClientService, Task>[] {
                async (vm, _, _) => { await vm.DetachCommand.Execute(); },
                async (vm, client, _) => { client.Result.SetResult(new AttachOutcome.Exited(0)); await vm.CurrentRunForTesting!; },
                async (vm, client, _) => { client.Result.SetResult(new AttachOutcome.ConnectionLost()); await vm.CurrentRunForTesting!; },
                (_, _, daemon) => { daemon.Agents.Remove("a1"); return Task.CompletedTask; },
                async (vm, _, _) => { await vm.TeardownAsync(); },
            }) {
                var (daemon, _, time, vm, client) = await BuildAttachedAsync();
                await Assert.That(vm.TrySendText("hello")).IsTrue();
                await WaitUntilAsync(() => client.SentInput.Count == 1, what: "paste written");

                await invalidate(vm, client, daemon);
                await Assert.That(vm.SendInFlight).IsFalse();
                await Assert.That(vm.CanAcceptText).IsFalse();
                await Assert.That(vm.TrySendText("again")).IsFalse();

                time.Advance(Delay);
                await (vm.PendingDeliveryForTesting ?? Task.CompletedTask);
                await Assert.That(client.SentInput).HasCount().EqualTo(1);
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_late_attached_publish_cannot_reopen_after_a_removal_or_detach() {
        await RunOnUiAsync(async () => {
            // (b) removal while Connecting: the attach callback's publish is queued, then the agent goes.
            var daemon = new FakeDaemonClientService();
            var factory = new FakeTerminalAttachClientFactory();
            var vm = new TerminalTabViewModel("a1", daemon, factory.Factory, () => new FakeTerminalSurface(), new FakeTimeProvider());
            daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true));
            await (vm.PendingResolveWorkForTesting ?? Task.CompletedTask);
            var client = factory.Created.Single();
            var tokenWhileConnecting = vm.OpeningTokenForTesting;

            var attached = client.TriggerAttached([]);   // queued for the UI dispatch, not yet run
            daemon.Agents.Remove("a1");                  // lands first: SessionEnded, ownership advanced
            await attached;

            await Assert.That(vm.State.Phase).IsEqualTo(TerminalSessionPhase.SessionEnded);
            await Assert.That(vm.OpeningTokenForTesting).IsNotEqualTo(tokenWhileConnecting);
            await Assert.That(vm.CanAcceptText).IsFalse();
            await Assert.That(vm.TrySendText("x")).IsFalse();

            // (c) removal during a reattach's pre-Connecting disposal aborts that attempt.
            var gate = new TaskCompletionSource();
            var (daemon2, factory2, _, vm2, client2) = await BuildAttachedAsync(configureNext: c => c.DisposeGate = gate);
            var reattach = vm2.ReattachCommand.Execute().ToTask();
            await WaitUntilAsync(() => client2.DisposeCalls == 1, what: "old client disposing");
            daemon2.Agents.Remove("a1");
            gate.SetResult();
            await reattach;

            await Assert.That(vm2.State.Phase).IsEqualTo(TerminalSessionPhase.SessionEnded);
            await Assert.That(factory2.Created.Count).IsEqualTo(1); // no second client was ever created
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_write_fault_clears_in_flight_and_leaves_state_alone() {
        await RunOnUiAsync(async () => {
            var (_, _, time, vm, client) = await BuildAttachedAsync();
            client.ThrowOnSendInput = new IOException("pipe closed");

            await Assert.That(vm.TrySendText("hello")).IsTrue();
            await vm.PendingDeliveryForTesting!;

            await Assert.That(vm.SendInFlight).IsFalse();
            await Assert.That(vm.State.Phase).IsEqualTo(TerminalSessionPhase.Attached);
            await Assert.That(vm.CanAcceptText).IsTrue();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Teardown_during_the_delay_queues_no_dispatcher_work_afterwards() {
        await RunOnUiAsync(async () => {
            var (_, _, time, vm, client) = await BuildAttachedAsync();
            await Assert.That(vm.TrySendText("hello")).IsTrue();
            await WaitUntilAsync(() => client.SentInput.Count == 1, what: "paste written");
            var delivery = vm.PendingDeliveryForTesting!;

            await vm.TeardownAsync();
            var changes = 0;
            vm.PropertyChanged += (_, _) => changes++;
            time.Advance(Delay);
            await delivery;
            Dispatcher.UIThread.RunJobs();

            await Assert.That(changes).IsEqualTo(0);
            await Assert.That(client.SentInput).HasCount().EqualTo(1);
        });
    }
}
```

Add to `FakeTerminalAttachClient` a `public Exception? ThrowOnSendInput;` field and make `SendInputAsync` throw it when set:

```csharp
    public Task SendInputAsync(byte[] bytes) {
        if (ThrowOnSendInput is { } ex) return Task.FromException(ex);
        SentInput.Add(bytes);
        return Task.CompletedTask;
    }
```

Extend `Explicit_detach_stays_in_place_with_single_flight_reattach` in `TerminalTabViewModelTests` (case (d)) — after `await Task.WhenAll(t1, t2);` add:

```csharp
            // The losing call changed neither the token nor the gate; the winner opens on Attached.
            var winner = factory.Created[^1];
            var token = vm.OpeningTokenForTesting;
            await Assert.That(vm.SendGateOpenForTesting).IsFalse();
            await winner.TriggerAttached([]);
            await Assert.That(vm.OpeningTokenForTesting).IsEqualTo(token);
            await Assert.That(vm.CanAcceptText).IsTrue();
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/TerminalSendGateTests/*"`
Expected: build errors — `TrySendText`, `CanAcceptText`, `SendAvailability` missing.

- [ ] **Step 3: Implement the gate**

In `TerminalAttach.cs`, after `TerminalSessionPhase`:

```csharp
/// What the composer can do right now, folding the send gate into the terminal state so a hint
/// built from it is true in every window — including the ones where State still reads Attached
/// while a reattach or detach is under way.
public enum SendAvailability { Ready, Sending, Transitioning, ReadOnly, Connecting, Reattach, Ended, NoTerminal }
```

In `TerminalTabViewModel.cs`:

1. Add fields and the bound projections (near `State`):

```csharp
    static readonly TimeSpan SubmitDelay = TimeSpan.FromMilliseconds(150);

    // The send gate. Only BeginAttempt and Invalidate advance the token; an attempt carries the
    // token its BeginAttempt produced and may open the gate only while that token is current.
    int _openingToken;
    bool _gateOpen;
    bool _sendInFlight;
    Task? _delivery;

    public bool SendInFlight {
        get => _sendInFlight;
        private set {
            if (_sendInFlight == value) return;
            _sendInFlight = value;
            this.RaisePropertyChanged();
            RaiseSendProjections();
        }
    }

    public bool CanAcceptText => _gateOpen && !_sendInFlight;

    public SendAvailability SendAvailability {
        get {
            if (_sendInFlight) return SendAvailability.Sending;
            if (State is { Phase: TerminalSessionPhase.Attached, ReadOnly: false })
                return _gateOpen ? SendAvailability.Ready : SendAvailability.Transitioning;
            return State.Phase switch {
                TerminalSessionPhase.Attached => SendAvailability.ReadOnly,
                TerminalSessionPhase.Resolving or TerminalSessionPhase.Connecting => SendAvailability.Connecting,
                TerminalSessionPhase.Detached or TerminalSessionPhase.Failed => SendAvailability.Reattach,
                TerminalSessionPhase.Exited or TerminalSessionPhase.SessionEnded => SendAvailability.Ended,
                _ => SendAvailability.NoTerminal,
            };
        }
    }

    internal int OpeningTokenForTesting => Volatile.Read(ref _openingToken);
    internal bool SendGateOpenForTesting => _gateOpen;
    internal Task? PendingDeliveryForTesting => _delivery;

    void RaiseSendProjections() {
        this.RaisePropertyChanged(nameof(CanAcceptText));
        this.RaisePropertyChanged(nameof(SendAvailability));
    }

    int BeginAttempt() {
        _gateOpen = false;
        SendInFlight = false;
        var token = Interlocked.Increment(ref _openingToken);
        RaiseSendProjections();
        return token;
    }

    void Invalidate() {
        _gateOpen = false;
        SendInFlight = false;
        Interlocked.Increment(ref _openingToken);
        RaiseSendProjections();
    }

    /// The one place State is assigned. An attempt-owned publish (Connecting, Attached) carries
    /// its token and is discarded when that token is stale; every other state is an invalidation.
    void Publish(TerminalSessionState state, int? ownerToken) {
        if (state.Phase is TerminalSessionPhase.Connecting or TerminalSessionPhase.Attached) {
            if (ownerToken != Volatile.Read(ref _openingToken)) return;
            State = state;
            if (state is { Phase: TerminalSessionPhase.Attached, ReadOnly: false }) _gateOpen = true;
            RaiseSendProjections();
            return;
        }
        Invalidate();
        State = state;
        RaiseSendProjections();
    }
```

   Make `State`'s setter `private` stay as is; every former `State = …` assignment below becomes a `Publish(...)` call.

2. `TrySendText` and the delivery:

```csharp
    /// Synchronous acceptance on the UI thread: true means the text is on its way through the
    /// current client and the composer may clear; false means nothing was written.
    public bool TrySendText(string text) {
        if (!CanAcceptText || _client is not { } client) return false;
        var token = Volatile.Read(ref _openingToken);
        SendInFlight = true;
        _delivery = DeliverAsync(client, token, TerminalInputEncoder.Paste(text));
        return true;
    }

    // Paste, wait out the TUI's post-paste Enter suppression, then one CR — only if nothing
    // advanced the token meanwhile. A fault ends the delivery and never touches State: the
    // attach outcome is the transport's own channel.
    async Task DeliverAsync(ITerminalAttachClient client, int token, byte[] paste) {
        try {
            await client.SendInputAsync(paste).ConfigureAwait(false);
            await Task.Delay(SubmitDelay, _time).ConfigureAwait(false);
            if (Volatile.Read(ref _openingToken) != token) return;
            await client.SendInputAsync(TerminalInputEncoder.Submit).ConfigureAwait(false);
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: composer send failed: {ex.Message}");
        } finally {
            if (Volatile.Read(ref _resolveState) != ResolveDisposed && Volatile.Read(ref _openingToken) == token) {
                await Dispatcher.UIThread.InvokeAsync(() => {
                    if (Volatile.Read(ref _resolveState) == ResolveDisposed) return;
                    if (Volatile.Read(ref _openingToken) != token) return;
                    SendInFlight = false;
                });
            }
        }
    }
```

3. Wire the existing lifecycle to the gate:

- `HandleAgentRemoved`: replace `State = TerminalSessionState.SessionEnded;` with `Publish(TerminalSessionState.SessionEnded, null);`.
- `OnResolveTimeout`: `RxSchedulers.MainThreadScheduler.Schedule(() => Publish(TerminalSessionState.NotFound, null));`.
- `RunResolveWorkAsync`'s catch: `Publish(TerminalSessionState.Failed($"couldn't open the terminal: {ex.Message}"), null);`.
- `ApplyResolvedDtoAsync`'s NoTerminal branch: `Publish(TerminalSessionState.NoTerminal(...), null);`.
- `TryStartAttemptAsync`: after `if (Retired(0)) return;` (the first guard inside the try) insert `var token = BeginAttempt();`. After the previous client's disposal await and its `if (Retired(0)) return;`, add `if (Volatile.Read(ref _openingToken) != token) return;`. Inside the UI swap dispatch replace `State = TerminalSessionState.Connecting;` with `Publish(TerminalSessionState.Connecting, token);`. Thread `token` into `OnAttachedAsync` (new parameter after `generation`) and into the factory lambda that captures it.
- `OnAttachedAsync(int generation, int token, ...)`: replace `State = TerminalSessionState.Attached(reason);` with `Publish(TerminalSessionState.Attached(reason), token);`.
- `FinishAttemptAsync`: replace `State = state;` with `Publish(state, null);`.
- `RunDetachAsync`: first statement `Invalidate();`.
- `TeardownAsync`: right after the idempotent `Interlocked.Exchange`, `Invalidate();`.
- Add `_delivery = null;` nowhere — the field is a test seam; `TeardownAsync` leaves it for the test to await.

4. Keep every existing `State.Phase`-based check (`Retired`, the Exited/Failed precedence in `HandleAgentRemoved`) unchanged.

- [ ] **Step 4: Run the tests to verify they pass**

Run `TerminalSendGateTests` and `TerminalTabViewModelTests`, then the whole app suite: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj`. Expected: green. If `Removal_after_first_observation_is_session_ended_not_resolving` or `Exited_is_not_overwritten_by_session_ended` regress, the precedence check in `HandleAgentRemoved` was lost — restore it before `Publish`.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.App/ViewModels/TerminalTabViewModel.cs src/Capacitor.App/Services/TerminalAttach.cs test/Capacitor.App.Tests.Unit/TerminalSendGateTests.cs test/Capacitor.App.Tests.Unit/TerminalTabViewModelTests.cs test/Capacitor.App.Tests.Unit/FakeTerminalAttachClient.cs
git commit -m "Gate composer sends on the terminal's attach ownership

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 11: Chat items and the tail-driven `ChatTabViewModel`

**Files:**
- Create: `src/Capacitor.App/ViewModels/ChatItems.cs`, `src/Capacitor.App/ViewModels/ChatTabViewModel.cs`
- Test: `test/Capacitor.App.Tests.Unit/ChatTabViewModelTests.cs`

**Interfaces:**
- Consumes: `JsonlTail`/`TailRead`/`TailStatus` (Task 3), `ITranscriptProjection` (Task 4), `ToolDetail` (Task 9), `TerminalTabViewModel` (Task 10), `IUrlOpener`, `IDaemonClientService`.
- Produces:
  - `public abstract class ChatItemViewModel : ReactiveObject`; `UserTurnItem(string Text)`, `AssistantTextItem(string Text)`, `ToolCallItem(string Name, string Detail)` with `ToolOutcome Outcome` (bound), `string OutcomeGlyph`, `bool IsError`; `public enum ToolOutcome { Running, Done, Error }`.
  - `public enum ChatTabPhase { Waiting, Reading, Missing, Unavailable }`.
  - `public sealed class ChatTabViewModel(string agentId, IDaemonClientService daemon, TerminalTabViewModel terminal, ITranscriptProjection? projection, IUrlOpener opener, TimeProvider time)` with `IAvaloniaReadOnlyList<ChatItemViewModel> Items`, `ChatTabPhase Phase`, `string PhaseNote`, `Task TeardownAsync()`, `internal Task? PendingReadForTesting`, `internal static readonly TimeSpan PollInterval` (500 ms). Composer/footer/link members come in Task 12.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Reactive.Linq;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.Enums;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

/// ChatTabViewModel's tail: phases, the poll, item projection and pairing, path switches and
/// teardown. Every test runs under RunOnUiAsync (the apply hops through Dispatcher.UIThread) and
/// carries [NotInParallel("AvaloniaSession")], like every other VM suite touching the dispatcher.
public class ChatTabViewModelTests {
    [TempDir] public required TempDir Tmp { get; init; }

    const string UserLine = """{"type":"user","message":{"role":"user","content":"hello"}}""";
    const string AssistantLine = """{"type":"assistant","message":{"content":[{"type":"text","text":"Hi there"}]}}""";
    const string ToolCallLine = """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"ls -la"}}]}}""";
    const string ToolResultLine = """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"ok"}]}}""";
    const string ToolErrorLine = """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"boom","is_error":true}]}}""";

    static AgentStatusDto Dto(string? transcriptPath, string vendor = "claude") =>
        Agent("a1", vendor, hasTerminal: true, repoPath: "/repo/x") with { TranscriptPath = transcriptPath };

    sealed class Harness {
        public FakeDaemonClientService Daemon { get; } = new();
        public FakeTerminalAttachClientFactory Factory { get; } = new();
        public FakeTimeProvider Time { get; } = new();
        public RecordingOpener Opener { get; } = new();
        public TerminalTabViewModel Terminal { get; }
        public ChatTabViewModel Chat { get; }

        public Harness(ITranscriptProjection? projection) {
            Terminal = new TerminalTabViewModel("a1", Daemon, Factory.Factory, () => new FakeTerminalSurface(), Time);
            Chat = new ChatTabViewModel("a1", Daemon, Terminal, projection, Opener, Time);
        }

        public async Task PushAsync(AgentStatusDto dto) {
            Daemon.Agents.AddOrUpdate(dto);
            await (Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
            await (Chat.PendingReadForTesting ?? Task.CompletedTask);
        }

        public async Task TickAsync() {
            Time.Advance(ChatTabViewModel.PollInterval);
            await (Chat.PendingReadForTesting ?? Task.CompletedTask);
        }

        public async Task TeardownAsync() {
            await Chat.TeardownAsync();
            await Terminal.TeardownAsync();
        }
    }

    static Harness Claude() => new(TranscriptProjection.For("claude"));

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Waits_until_a_path_then_renders_the_initial_load_in_file_order() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            await Assert.That(h.Chat.Phase).IsEqualTo(ChatTabPhase.Waiting);
            await Assert.That(h.Chat.PhaseNote).IsEqualTo("Waiting for the transcript…");

            await h.PushAsync(Dto(transcriptPath: null));
            await Assert.That(h.Chat.Phase).IsEqualTo(ChatTabPhase.Waiting);

            var path = Tmp.CreateFile("t.jsonl", [UserLine, AssistantLine, ToolCallLine]);
            await h.PushAsync(Dto(path));

            await Assert.That(h.Chat.Phase).IsEqualTo(ChatTabPhase.Reading);
            await Assert.That(h.Chat.Items.Select(i => i.GetType().Name)).IsEquivalentTo(
                new[] { nameof(UserTurnItem), nameof(AssistantTextItem), nameof(ToolCallItem) }, CollectionOrdering.Matching);
            await Assert.That(((UserTurnItem)h.Chat.Items[0]).Text).IsEqualTo("hello");
            await Assert.That(((ToolCallItem)h.Chat.Items[2]).Detail).IsEqualTo("ls -la");
            await Assert.That(((ToolCallItem)h.Chat.Items[2]).Outcome).IsEqualTo(ToolOutcome.Running);
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Appended_lines_render_after_a_tick_and_a_partial_line_waits_for_its_newline() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [UserLine]);
            await h.PushAsync(Dto(path));
            await Assert.That(h.Chat.Items).HasCount().EqualTo(1);

            File.AppendAllText(path, AssistantLine + "\n" + ToolCallLine[..20]);
            await h.TickAsync();
            await Assert.That(h.Chat.Items).HasCount().EqualTo(2);

            File.AppendAllText(path, ToolCallLine[20..] + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.Items).HasCount().EqualTo(3);
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Tool_results_flip_their_call_in_place_and_unmatched_results_are_ignored() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [ToolCallLine, ToolResultLine]);
            await h.PushAsync(Dto(path));
            var call = (ToolCallItem)h.Chat.Items.Single();
            await Assert.That(call.Outcome).IsEqualTo(ToolOutcome.Done);
            await Assert.That(call.OutcomeGlyph).IsEqualTo("✓");

            File.AppendAllText(path, ToolCallLine + "\n" + ToolErrorLine + "\n" + ToolErrorLine.Replace("t1", "unknown") + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.Items).HasCount().EqualTo(2);
            await Assert.That(((ToolCallItem)h.Chat.Items[1]).Outcome).IsEqualTo(ToolOutcome.Error);
            await Assert.That(((ToolCallItem)h.Chat.Items[1]).IsError).IsTrue();
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Length_regression_resets_items_missing_recovers_and_failed_keeps_items() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.PathTo("t.jsonl");
            await h.PushAsync(Dto(path));
            await Assert.That(h.Chat.Phase).IsEqualTo(ChatTabPhase.Missing);
            await Assert.That(h.Chat.PhaseNote).IsEqualTo("The transcript file is missing");

            File.WriteAllLines(path, [UserLine, AssistantLine]);
            await h.TickAsync();
            await Assert.That(h.Chat.Phase).IsEqualTo(ChatTabPhase.Reading);
            await Assert.That(h.Chat.Items).HasCount().EqualTo(2);

            File.WriteAllLines(path, [ToolCallLine]);
            await h.TickAsync();
            await Assert.That(h.Chat.Items).HasCount().EqualTo(1);
            await Assert.That(h.Chat.Items[0]).IsTypeOf<ToolCallItem>();

            File.Delete(path);
            Directory.CreateDirectory(path);
            await h.TickAsync();
            await Assert.That(h.Chat.Phase).IsEqualTo(ChatTabPhase.Reading);
            await Assert.That(h.Chat.Items).HasCount().EqualTo(1);
            Directory.Delete(path);
            await h.TeardownAsync();
        });
    }

    sealed class GatedProjection(ITranscriptProjection inner, string blockOn, TaskCompletionSource gate) : ITranscriptProjection {
        public IReadOnlyList<AcpEventEnvelope> Project(string line) {
            if (line.Contains(blockOn, StringComparison.Ordinal)) gate.Task.GetAwaiter().GetResult();
            return inner.Project(line);
        }
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_path_switch_discards_a_read_still_in_flight_for_the_old_file() {
        await RunOnUiAsync(async () => {
            var gate = new TaskCompletionSource();
            var h = new Harness(new GatedProjection(TranscriptProjection.For("claude")!, "OLD", gate));
            var oldPath = Tmp.CreateFile("old.jsonl", [UserLine.Replace("hello", "OLD"), ToolCallLine]);
            var newPath = Tmp.CreateFile("new.jsonl", [AssistantLine]);

            h.Daemon.Agents.AddOrUpdate(Dto(oldPath));
            await (h.Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
            var oldRead = h.Chat.PendingReadForTesting!;

            h.Daemon.Agents.AddOrUpdate(Dto(newPath));
            gate.SetResult();
            await oldRead;
            await (h.Chat.PendingReadForTesting ?? Task.CompletedTask);
            await h.TickAsync();

            await Assert.That(h.Chat.Items.Select(i => i.GetType().Name)).IsEquivalentTo(new[] { nameof(AssistantTextItem) });
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Unavailable_for_a_vendor_without_a_projection_and_no_ticks_after_teardown() {
        await RunOnUiAsync(async () => {
            var unavailable = new Harness(projection: null);
            await Assert.That(unavailable.Chat.Phase).IsEqualTo(ChatTabPhase.Unavailable);
            await Assert.That(unavailable.Chat.PhaseNote).IsEqualTo("No chat view for this harness");
            await unavailable.TeardownAsync();

            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [UserLine]);
            await h.PushAsync(Dto(path));
            await h.TeardownAsync();
            File.AppendAllText(path, AssistantLine + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.Items).HasCount().EqualTo(1);

            // A removed agent keeps its items.
            var kept = Claude();
            var keptPath = Tmp.CreateFile("kept.jsonl", [UserLine]);
            await kept.PushAsync(Dto(keptPath));
            kept.Daemon.Agents.Remove("a1");
            await Assert.That(kept.Chat.Items).HasCount().EqualTo(1);
            await kept.TeardownAsync();
        });
    }

    /// Thread identity, so deliberately not under WithImmediateRxScheduler.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_dto_pushed_from_a_pool_thread_lands_on_the_ui_thread() {
        var onUi = await DispatchAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [UserLine]);
            bool? phaseChangedOnUi = null;
            h.Chat.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ChatTabViewModel.Phase)) phaseChangedOnUi = Dispatcher.UIThread.CheckAccess(); };

            await Task.Run(() => h.Daemon.Agents.AddOrUpdate(Dto(path)));
            await WaitUntilAsync(() => h.Chat.Phase == ChatTabPhase.Reading, what: "reading");
            await h.TeardownAsync();
            return phaseChangedOnUi;
        });
        await Assert.That(onUi).IsEqualTo(true);
    }
}
```

`WaitUntilAsync` inside a `DispatchAsync` body: it uses `await Task.Delay(10)`, which posts back onto the pumped dispatcher frame — the same idiom `WorkspaceFixtures` documents.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/ChatTabViewModelTests/*"`
Expected: build errors — the types do not exist.

- [ ] **Step 3: Implement the items**

`ViewModels/ChatItems.cs`:

```csharp
using ReactiveUI;

namespace Capacitor.App.ViewModels;

/// One row of the Chat tab. Three shapes, matched by DataTemplates on the concrete type.
public abstract class ChatItemViewModel : ReactiveObject { }

public sealed class UserTurnItem(string text) : ChatItemViewModel {
    public string Text { get; } = text;
}

public sealed class AssistantTextItem(string text) : ChatItemViewModel {
    public string Text { get; } = text;
}

public enum ToolOutcome { Running, Done, Error }

public sealed class ToolCallItem(string name, string detail) : ChatItemViewModel {
    public string Name { get; } = name;
    public string Detail { get; } = detail;

    ToolOutcome _outcome;
    /// Flipped in place when the matching tool_result arrives; never rebuilt.
    public ToolOutcome Outcome {
        get => _outcome;
        set {
            if (_outcome == value) return;
            _outcome = value;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(OutcomeGlyph));
            this.RaisePropertyChanged(nameof(IsError));
        }
    }

    public string OutcomeGlyph => _outcome switch { ToolOutcome.Done => "✓", ToolOutcome.Error => "✕", _ => "" };
    public bool IsError => _outcome == ToolOutcome.Error;
}
```

- [ ] **Step 4: Implement the view model's tail**

`ViewModels/ChatTabViewModel.cs` (the composer, footer and link members are added in Task 12; leave the marked region for them):

```csharp
using System.Collections.Concurrent;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Avalonia.Collections;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;
using ReactiveUI;

namespace Capacitor.App.ViewModels;

public enum ChatTabPhase { Waiting, Reading, Missing, Unavailable }

/// The Chat tab: the session's transcript, tailed and projected into chat rows, plus the composer
/// that sends through the sibling terminal. Ctor-scoped; TeardownAsync is the one exit.
///
/// Path identity is part of the read generation: a distinct transcript_path clears the rows and
/// installs a fresh tail in one UI-thread step, and any read still in flight for the old file
/// completes under a stale generation and is discarded.
public sealed class ChatTabViewModel : ReactiveObject {
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    readonly string _agentId;
    readonly TerminalTabViewModel _terminal;
    readonly ITranscriptProjection? _projection;
    readonly IUrlOpener _opener;
    readonly TimeProvider _time;
    readonly CompositeDisposable _disposables = new();
    readonly AvaloniaList<ChatItemViewModel> _items = new();
    readonly Dictionary<string, ToolCallItem> _pendingTools = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, byte> _loggedFailures = new(StringComparer.Ordinal);

    int _generation;
    int _readInFlight;
    string? _path;
    JsonlTail? _tail;
    ITimer? _timer;
    Task? _pendingRead;

    public IAvaloniaReadOnlyList<ChatItemViewModel> Items => _items;

    ChatTabPhase _phase;
    public ChatTabPhase Phase {
        get => _phase;
        private set {
            this.RaiseAndSetIfChanged(ref _phase, value);
            this.RaisePropertyChanged(nameof(PhaseNote));
        }
    }

    public string PhaseNote => Phase switch {
        ChatTabPhase.Waiting     => "Waiting for the transcript…",
        ChatTabPhase.Missing     => "The transcript file is missing",
        ChatTabPhase.Unavailable => "No chat view for this harness",
        _                        => "",
    };

    internal Task? PendingReadForTesting => _pendingRead;

    public ChatTabViewModel(
            string agentId, IDaemonClientService daemon, TerminalTabViewModel terminal,
            ITranscriptProjection? projection, IUrlOpener opener, TimeProvider time) {
        _agentId = agentId;
        _terminal = terminal;
        _projection = projection;
        _opener = opener;
        _time = time;
        _phase = projection is null ? ChatTabPhase.Unavailable : ChatTabPhase.Waiting;

        daemon.Agents.Connect()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(OnAgentsChanged)
            .DisposeWith(_disposables);

        if (projection is not null)
            _timer = time.CreateTimer(_ => OnTick(), null, PollInterval, PollInterval);

        // Task 12: composer, footer and link members are constructed here.
    }

    void OnAgentsChanged(IChangeSet<AgentStatusDto, string> changes) {
        foreach (var change in changes) {
            if (change.Key != _agentId || change.Reason is not (ChangeReason.Add or ChangeReason.Update)) continue;
            OnDto(change.Current);
        }
    }

    void OnDto(AgentStatusDto dto) {
        if (_projection is not null && dto.TranscriptPath is { } path && path != _path) SwitchPath(path);
    }

    void SwitchPath(string path) {
        Interlocked.Increment(ref _generation);
        _items.Clear();
        _pendingTools.Clear();
        _path = path;
        Volatile.Write(ref _tail, new JsonlTail(path));
        Phase = ChatTabPhase.Waiting;
        OnTick();
    }

    void OnTick() {
        if (Volatile.Read(ref _tail) is not { } tail || _projection is not { } projection) return;
        if (Interlocked.CompareExchange(ref _readInFlight, 1, 0) != 0) return;
        _pendingRead = ReadAndApplyAsync(tail, projection, Volatile.Read(ref _generation));
    }

    async Task ReadAndApplyAsync(JsonlTail tail, ITranscriptProjection projection, int generation) {
        try {
            var (read, envelopes) = await Task.Run(() => {
                var result = tail.ReadAppended();
                var list = new List<AcpEventEnvelope>();
                foreach (var line in result.Lines) {
                    try { list.AddRange(projection.Project(line)); }
                    catch (Exception ex) { LogOnce($"projection: {ex.Message}"); }
                }
                return (result, list);
            }).ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() => Apply(generation, read, envelopes));
        } catch (Exception ex) {
            LogOnce($"read: {ex.Message}");
        } finally {
            Volatile.Write(ref _readInFlight, 0);
        }
    }

    void Apply(int generation, TailRead read, List<AcpEventEnvelope> envelopes) {
        if (generation != Volatile.Read(ref _generation)) return;

        switch (read.Status) {
            case TailStatus.Missing:
                Phase = ChatTabPhase.Missing;
                return;
            case TailStatus.Failed:
                LogOnce(read.Failure ?? "read failed");
                return;
            case TailStatus.Reset:
                _items.Clear();
                _pendingTools.Clear();
                break;
        }

        Phase = ChatTabPhase.Reading;
        if (envelopes.Count == 0) return;

        var fresh = new List<ChatItemViewModel>();
        foreach (var e in envelopes) {
            switch (e.Kind) {
                case AcpEventKind.UserMessage:
                    fresh.Add(new UserTurnItem(e.Text ?? ""));
                    break;
                case AcpEventKind.AssistantText:
                    fresh.Add(new AssistantTextItem(e.Text ?? ""));
                    break;
                case AcpEventKind.ToolCall: {
                    var item = new ToolCallItem(e.ToolName ?? "tool", ToolDetail.From(e.ToolInputJson));
                    if (e.ToolCallId is { } id) _pendingTools[id] = item;
                    fresh.Add(item);
                    break;
                }
                case AcpEventKind.ToolResult:
                    if (e.ToolCallId is { } resultId && _pendingTools.Remove(resultId, out var call))
                        call.Outcome = e.ToolIsError ? ToolOutcome.Error : ToolOutcome.Done;
                    break;
            }
        }
        if (fresh.Count > 0) _items.AddRange(fresh);
    }

    void LogOnce(string reason) {
        if (_loggedFailures.TryAdd(reason, 0)) Console.Error.WriteLine($"kcap: chat transcript: {reason}");
    }

    public Task TeardownAsync() {
        Interlocked.Increment(ref _generation);
        _timer?.Dispose();
        _timer = null;
        _disposables.Dispose();
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Same command. Expected: 7 PASS. If `A_path_switch_discards_a_read_still_in_flight_for_the_old_file` sees the OLD user row, the generation captured by `OnTick` is being read after the switch — it must be captured before `ReadAndApplyAsync` starts, as written.

- [ ] **Step 6: Commit**

```bash
git add src/Capacitor.App/ViewModels/ChatItems.cs src/Capacitor.App/ViewModels/ChatTabViewModel.cs test/Capacitor.App.Tests.Unit/ChatTabViewModelTests.cs
git commit -m "Tail the session transcript into chat rows

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 12: Composer, footer and link command on `ChatTabViewModel`

**Files:**
- Modify: `src/Capacitor.App/ViewModels/ChatTabViewModel.cs`
- Test: `test/Capacitor.App.Tests.Unit/ChatComposerTests.cs`

**Interfaces:**
- Consumes: `TerminalTabViewModel.TrySendText/CanAcceptText/SendAvailability/State` (Task 10), `LinkPolicy` (Task 9), `HostedHarnessCatalog.Build/LabelFor/ModelLabelFor`, `SessionStatusDots.For`.
- Produces on `ChatTabViewModel`: `string ComposerText` (bound, settable), `ReactiveCommand<Unit, Unit> SendCommand`, `ReactiveCommand<string, Unit> OpenLinkCommand`, `string ComposerHint`, `string VendorLabel`, `string ModelLabel`, `string StatusText`, `IBrush StatusDot`; `internal static string HintFor(SendAvailability, TerminalSessionState, string vendorLabel)`.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

public class ChatComposerTests {
    static async Task<(FakeDaemonClientService Daemon, FakeTimeProvider Time, TerminalTabViewModel Terminal, ChatTabViewModel Chat, FakeTerminalAttachClient Client, RecordingOpener Opener)>
            BuildAttachedAsync() {
        var daemon = new FakeDaemonClientService();
        var factory = new FakeTerminalAttachClientFactory();
        var time = new FakeTimeProvider();
        var opener = new RecordingOpener();
        var terminal = new TerminalTabViewModel("a1", daemon, factory.Factory, () => new FakeTerminalSurface(), time);
        var chat = new ChatTabViewModel("a1", daemon, terminal, TranscriptProjection.For("claude"), opener, time);
        daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(supportedVendors: ["claude", "codex"]));
        daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true, repoPath: "/repo", model: "claude-opus-5") with { Status = "Running" });
        await (terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
        var client = factory.Created.Single();
        await client.TriggerAttached([]);
        return (daemon, time, terminal, chat, client, opener);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Send_clears_the_text_on_acceptance_and_keeps_it_on_refusal() {
        await RunOnUiAsync(async () => {
            var (_, time, terminal, chat, client, _) = await BuildAttachedAsync();
            await Assert.That(await chat.SendCommand.CanExecute.FirstAsync()).IsFalse();

            chat.ComposerText = "hello";
            await Assert.That(await chat.SendCommand.CanExecute.FirstAsync()).IsTrue();
            await chat.SendCommand.Execute();
            await Assert.That(chat.ComposerText).IsEqualTo("");
            await Assert.That(chat.ComposerHint).IsEqualTo("Sending…");

            chat.ComposerText = "second";
            await Assert.That(await chat.SendCommand.CanExecute.FirstAsync()).IsFalse();
            time.Advance(TimeSpan.FromMilliseconds(150));
            await terminal.PendingDeliveryForTesting!;
            await Assert.That(chat.ComposerText).IsEqualTo("second");
            await Assert.That(await chat.SendCommand.CanExecute.FirstAsync()).IsTrue();
            await Assert.That(client.SentInput).HasCount().EqualTo(2);
            await chat.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Hint_follows_send_availability_and_the_vendor_label() {
        await RunOnUiAsync(async () => {
            var (daemon, _, terminal, chat, client, _) = await BuildAttachedAsync();
            await Assert.That(chat.VendorLabel).IsEqualTo("Claude Code");
            await Assert.That(chat.ComposerHint).IsEqualTo("Reply to Claude Code · Enter sends · Shift+Enter for a new line");

            client.Result.SetResult(new AttachOutcome.Detached());
            await terminal.CurrentRunForTesting!;
            await Assert.That(chat.ComposerHint).IsEqualTo("Reattach the terminal to send");

            daemon.Agents.Remove("a1");
            await Assert.That(chat.ComposerHint).IsEqualTo("Reattach the terminal to send"); // Detached outranks removal

            var attached = TerminalSessionState.Attached(null);
            await Assert.That(ChatTabViewModel.HintFor(SendAvailability.Transitioning, attached, "Claude Code")).IsEqualTo("Updating the terminal connection…");
            await Assert.That(ChatTabViewModel.HintFor(SendAvailability.ReadOnly, TerminalSessionState.Attached("review"), "x")).IsEqualTo("Read-only: review");
            await Assert.That(ChatTabViewModel.HintFor(SendAvailability.Connecting, TerminalSessionState.Connecting, "x")).IsEqualTo("Connecting to the terminal…");
            await Assert.That(ChatTabViewModel.HintFor(SendAvailability.Ended, TerminalSessionState.SessionEnded, "x")).IsEqualTo("This session has ended");
            await Assert.That(ChatTabViewModel.HintFor(SendAvailability.NoTerminal, TerminalSessionState.NotFound, "x")).IsEqualTo("No terminal to send to");
            await chat.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Footer_reflects_the_dto() {
        await RunOnUiAsync(async () => {
            var (daemon, _, _, chat, _, _) = await BuildAttachedAsync();
            await Assert.That(chat.ModelLabel).IsEqualTo("Claude Opus 5");
            await Assert.That(chat.StatusText).IsEqualTo("Running");
            await Assert.That(chat.StatusDot).IsSameReferenceAs(SessionStatusDots.For("Running"));

            daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true, repoPath: "/repo") with { Status = "Failed" });
            await Assert.That(chat.StatusText).IsEqualTo("Failed");
            await Assert.That(chat.ModelLabel).IsEqualTo("default");
            await chat.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Links_open_only_through_the_policy_and_an_opener_fault_is_contained() {
        await RunOnUiAsync(async () => {
            var (_, _, _, chat, _, opener) = await BuildAttachedAsync();

            await chat.OpenLinkCommand.Execute("https://example.com/a");
            await chat.OpenLinkCommand.Execute("file:///etc/passwd");
            await chat.OpenLinkCommand.Execute("javascript:alert(1)");
            await Assert.That(opener.Opened).IsEquivalentTo(new[] { "https://example.com/a" });

            opener.ThrowOnOpen = new InvalidOperationException("no browser");
            await chat.OpenLinkCommand.Execute("https://example.com/b");
            await Assert.That(opener.Opened).HasCount().EqualTo(2);
            await chat.TeardownAsync();
        });
    }

    /// Thread identity: the hint's own change lands on the UI thread even when the terminal's
    /// state flips from a pool thread.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_pool_thread_state_flip_updates_the_hint_on_the_ui_thread() {
        var onUi = await DispatchAsync(async () => {
            var (_, _, terminal, chat, client, _) = await BuildAttachedAsync();
            bool? hintChangedOnUi = null;
            chat.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ChatTabViewModel.ComposerHint)) hintChangedOnUi = Dispatcher.UIThread.CheckAccess(); };

            await Task.Run(() => client.Result.SetResult(new AttachOutcome.Exited(0)));
            await terminal.CurrentRunForTesting!;
            await WaitUntilAsync(() => chat.ComposerHint == "This session has ended", what: "hint");
            await chat.TeardownAsync();
            return hintChangedOnUi;
        });
        await Assert.That(onUi).IsEqualTo(true);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/ChatComposerTests/*"`
Expected: build errors — `ComposerText`, `SendCommand`, `HintFor` missing.

- [ ] **Step 3: Add the composer, footer and link members**

In `ChatTabViewModel.cs` add the usings `using System.Reactive;` and `using Avalonia.Media;`, these members, and the constructor block at the marked spot:

```csharp
    string _composerText = "";
    public string ComposerText {
        get => _composerText;
        set => this.RaiseAndSetIfChanged(ref _composerText, value);
    }

    public ReactiveCommand<Unit, Unit> SendCommand { get; }
    public ReactiveCommand<string, Unit> OpenLinkCommand { get; }

    readonly ObservableAsPropertyHelper<string> _composerHint;
    public string ComposerHint => _composerHint.Value;

    string _vendor = "";
    IReadOnlyList<HarnessOption> _options = HostedHarnessCatalog.Build(null);

    string _vendorLabel = "";
    public string VendorLabel { get => _vendorLabel; private set => this.RaiseAndSetIfChanged(ref _vendorLabel, value); }

    string _modelLabel = "default";
    public string ModelLabel { get => _modelLabel; private set => this.RaiseAndSetIfChanged(ref _modelLabel, value); }

    string _statusText = "";
    public string StatusText { get => _statusText; private set => this.RaiseAndSetIfChanged(ref _statusText, value); }

    IBrush _statusDot = SessionStatusDots.For("");
    public IBrush StatusDot { get => _statusDot; private set => this.RaiseAndSetIfChanged(ref _statusDot, value); }

    /// The hint is built from the terminal's own availability, so it is true in the windows
    /// where State alone would lie (a reattach or detach under way while State reads Attached).
    internal static string HintFor(SendAvailability availability, TerminalSessionState state, string vendorLabel) => availability switch {
        SendAvailability.Ready         => $"Reply to {vendorLabel} · Enter sends · Shift+Enter for a new line",
        SendAvailability.Sending       => "Sending…",
        SendAvailability.Transitioning => "Updating the terminal connection…",
        SendAvailability.ReadOnly      => $"Read-only: {state.Detail}",
        SendAvailability.Connecting    => "Connecting to the terminal…",
        SendAvailability.Reattach      => "Reattach the terminal to send",
        SendAvailability.Ended         => "This session has ended",
        _                              => "No terminal to send to",
    };
```

Constructor additions (replace the `// Task 12` comment):

```csharp
        daemon.Snapshots
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(snapshot => {
                _options = HostedHarnessCatalog.Build(snapshot.Daemon.SupportedVendors);
                VendorLabel = HostedHarnessCatalog.LabelFor(_options, _vendor);
            })
            .DisposeWith(_disposables);

        _composerHint = Observable.CombineLatest(
                terminal.WhenAnyValue(t => t.SendAvailability, t => t.State, (availability, state) => (availability, state)),
                this.WhenAnyValue(x => x.VendorLabel),
                (t, label) => HintFor(t.availability, t.state, label))
            .ToProperty(this, x => x.ComposerHint, HintFor(terminal.SendAvailability, terminal.State, ""))
            .DisposeWith(_disposables);

        var canSend = Observable.CombineLatest(
            this.WhenAnyValue(x => x.ComposerText),
            terminal.WhenAnyValue(t => t.CanAcceptText),
            (text, can) => can && !string.IsNullOrWhiteSpace(text));
        SendCommand = ReactiveCommand.Create(() => {
            if (_terminal.TrySendText(ComposerText)) ComposerText = "";
        }, canSend);
        _disposables.Add(SendCommand);

        OpenLinkCommand = ReactiveCommand.Create<string>(url => {
            if (!LinkPolicy.IsOpenable(url)) return;
            try { _opener.Open(url); }
            catch (Exception ex) { Console.Error.WriteLine($"kcap: open link failed: {ex.Message}"); }
        });
        _disposables.Add(OpenLinkCommand);
```

Extend `OnDto` so the footer follows every dto:

```csharp
    void OnDto(AgentStatusDto dto) {
        _vendor = dto.Vendor;
        VendorLabel = HostedHarnessCatalog.LabelFor(_options, dto.Vendor);
        ModelLabel = HostedHarnessCatalog.ModelLabelFor(dto.Vendor, dto.Model ?? "");
        StatusText = dto.Status;
        StatusDot = SessionStatusDots.For(dto.Status);
        if (_projection is not null && dto.TranscriptPath is { } path && path != _path) SwitchPath(path);
    }
```

`FakeDaemonClientService.Agent` fixtures are records, so `with { Status = "Failed" }` and `with { TranscriptPath = … }` work as the tests use them.

- [ ] **Step 4: Run the tests to verify they pass**

Same command, then `ChatTabViewModelTests` again. Expected: all PASS. `VendorLabel` "Claude Code" comes from `HarnessCatalog.All`'s label for `claude` — if the catalog spells it differently, use the catalog's actual label in the assertion, never a literal that drifts.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.App/ViewModels/ChatTabViewModel.cs test/Capacitor.App.Tests.Unit/ChatComposerTests.cs
git commit -m "Add the chat composer, footer and link command

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 13: Markdown rendering

**Files:**
- Create: `src/Capacitor.App/Views/MarkdownBlocks.cs`, `src/Capacitor.App/Views/MarkdownView.cs`
- Test: `test/Capacitor.App.Tests.Unit/MarkdownBlocksTests.cs`

**Interfaces:**
- Consumes: Markdig (`Markdown.Parse`, block/inline AST), `LinkPolicy` (Task 9).
- Produces: `public static class MarkdownBlocks { public static Control Build(string markdown, ICommand? openLink); }`; `public sealed class MarkdownView : ContentControl` with styled `string? Text` and `ICommand? OpenLink`.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Reactive;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Views;
using ReactiveUI;
using static Capacitor.App.Tests.Unit.AvaloniaSession;

namespace Capacitor.App.Tests.Unit;

public class MarkdownBlocksTests {
    static (Window Window, Control Root, List<string> Opened) Show(string markdown) {
        var opened = new List<string>();
        ICommand open = ReactiveCommand.Create<string>(opened.Add);
        var view = new MarkdownView { Text = markdown, OpenLink = open, Width = 400 };
        var window = new Window { Content = view, Width = 500, Height = 400 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view, opened);
    }

    static IEnumerable<T> All<T>(Control root) where T : Visual => root.GetVisualDescendants().OfType<T>();

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Paragraph_inlines_code_blocks_lists_and_quotes_render() {
        await RunOnUiAsync(async () => {
            var (window, root, _) = Show("Some **bold** and *em* and `code`.\n\n```\nvar x = 1;\n```\n\n- one\n- two\n\n> quoted\n\n---\n");

            var paragraph = All<SelectableTextBlock>(root).First();
            await Assert.That(paragraph.Inlines!.OfType<Bold>().Count()).IsEqualTo(1);
            await Assert.That(paragraph.Inlines!.OfType<Italic>().Count()).IsEqualTo(1);
            await Assert.That(paragraph.Inlines!.OfType<Run>().Any(r => r.Text == "code")).IsTrue();
            await Assert.That(All<SelectableTextBlock>(root).Any(t => t.Text == "var x = 1;")).IsTrue();
            await Assert.That(All<TextBlock>(root).Count(t => t.Text == "•")).IsEqualTo(2);
            await Assert.That(All<SelectableTextBlock>(root).Any(t => t.Inlines!.Any(i => i is Run { Text: "quoted" }))).IsTrue();
            window.Close();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_allowed_link_is_a_button_that_opens_once_by_pointer_and_once_by_keyboard() {
        await RunOnUiAsync(async () => {
            var (window, root, opened) = Show("See [docs](https://example.com/docs) now.");
            var button = All<HyperlinkButton>(root).Single();
            await Assert.That(button.NavigateUri).IsNull();
            await Assert.That(button.CommandParameter).IsEqualTo("https://example.com/docs");

            var origin = button.TranslatePoint(new Avalonia.Point(2, 2), window)!.Value;
            window.MouseDown(origin, MouseButton.Left);
            window.MouseUp(origin, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            await Assert.That(opened).IsEquivalentTo(new[] { "https://example.com/docs" });

            button.Focus();
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            await Assert.That(opened).HasCount().EqualTo(2);
            window.Close();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_disallowed_link_and_unknown_constructs_render_as_plain_text() {
        await RunOnUiAsync(async () => {
            var (window, root, opened) = Show("Bad [link](javascript:alert(1)) and <b>html</b>\n\n| a | b |\n|---|---|\n| 1 | 2 |\n");
            await Assert.That(All<HyperlinkButton>(root)).IsEmpty();
            await Assert.That(All<SelectableTextBlock>(root).Any(t => t.Inlines!.Any(i => i is Run { Text: "link" }))).IsTrue();
            await Assert.That(All<SelectableTextBlock>(root).Any(t => (t.Text ?? "").Contains("| a | b |"))).IsTrue();
            await Assert.That(opened).IsEmpty();
            await Assert.That(root.GetVisualDescendants().OfType<Control>().Any(c => c.Focusable && c is not SelectableTextBlock)).IsFalse();
            window.Close();
        });
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/MarkdownBlocksTests/*"`
Expected: build errors — `MarkdownView`/`MarkdownBlocks` missing.

- [ ] **Step 3: Implement the renderer**

`Views/MarkdownBlocks.cs`:

```csharp
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Capacitor.App.Services;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Capacitor.App.Views;

/// Maps the markdown constructs agents actually emit to Avalonia controls. Anything else
/// renders as its literal source text — degraded, never dropped.
public static class MarkdownBlocks {
    static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseAutoLinks().Build();
    static readonly FontFamily Mono = new("Menlo,Monaco,Consolas,Cascadia Mono,DejaVu Sans Mono,monospace");

    static IBrush Brush(string key) => Application.Current?.FindResource(key) as IBrush ?? Brushes.Gray;

    public static Control Build(string markdown, ICommand? openLink) {
        var document = Markdown.Parse(markdown, Pipeline);
        var panel = new StackPanel { Spacing = 8 };
        foreach (var block in document) panel.Children.Add(BuildBlock(markdown, block, openLink));
        return panel;
    }

    static Control BuildBlock(string source, Block block, ICommand? openLink) => block switch {
        ParagraphBlock p     => InlineText(p.Inline, openLink, 13.5, bold: false),
        HeadingBlock h       => InlineText(h.Inline, openLink, h.Level switch { 1 => 18, 2 => 16, _ => 14.5 }, bold: true),
        FencedCodeBlock f    => CodeBlock(f),
        CodeBlock c          => CodeBlock(c),
        ListBlock list       => List(source, list, openLink),
        QuoteBlock quote     => Quote(source, quote, openLink),
        ThematicBreakBlock   => new Border { Height = 1, Background = Brush("KcapBorderBrush"), Margin = new Thickness(0, 4) },
        _                    => Literal(source, block),
    };

    static SelectableTextBlock InlineText(ContainerInline? inlines, ICommand? openLink, double fontSize, bool bold) {
        var text = new SelectableTextBlock {
            TextWrapping = TextWrapping.Wrap,
            FontSize = fontSize,
            FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
            LineHeight = fontSize * 1.6,
            Foreground = Brush("KcapTextBrush"),
        };
        AddInlines(text.Inlines!, inlines, openLink);
        return text;
    }

    static void AddInlines(InlineCollection target, ContainerInline? container, ICommand? openLink) {
        if (container is null) return;
        foreach (var inline in container) {
            switch (inline) {
                case LiteralInline literal:
                    target.Add(new Run(literal.Content.ToString()));
                    break;
                case EmphasisInline emphasis: {
                    Span span = emphasis.DelimiterCount >= 2 ? new Bold() : new Italic();
                    AddInlines(span.Inlines, emphasis, openLink);
                    target.Add(span);
                    break;
                }
                case CodeInline code:
                    target.Add(new Run(code.Content) { FontFamily = Mono });
                    break;
                case LineBreakInline:
                    target.Add(new LineBreak());
                    break;
                case LinkInline link when !link.IsImage && LinkPolicy.IsOpenable(link.Url):
                    target.Add(new InlineUIContainer { Child = LinkButton(PlainText(link), link.Url!, openLink) });
                    break;
                case LinkInline link:
                    AddInlines(target, link, openLink);
                    break;
                case AutolinkInline auto when LinkPolicy.IsOpenable(auto.Url):
                    target.Add(new InlineUIContainer { Child = LinkButton(auto.Url, auto.Url, openLink) });
                    break;
                case AutolinkInline auto:
                    target.Add(new Run(auto.Url));
                    break;
                case HtmlInline html:
                    target.Add(new Run(html.Tag));
                    break;
                case ContainerInline nested:
                    AddInlines(target, nested, openLink);
                    break;
                default:
                    target.Add(new Run(inline.ToString() ?? ""));
                    break;
            }
        }
    }

    // NavigateUri stays unset on purpose: set, the control opens the URI itself and the policy
    // in the command would never run.
    static HyperlinkButton LinkButton(string label, string url, ICommand? openLink) => new() {
        Content = label,
        Command = openLink,
        CommandParameter = url,
        Padding = new Thickness(0),
        Cursor = new Cursor(StandardCursorType.Hand),
        Foreground = Brush("KcapAccentBrush"),
        VerticalAlignment = VerticalAlignment.Center,
    };

    static string PlainText(ContainerInline container) =>
        string.Concat(container.Select(i => i is LiteralInline l ? l.Content.ToString() : i is ContainerInline c ? PlainText(c) : ""));

    static Control CodeBlock(LeafBlock code) => new Border {
        Background = Brush("KcapSurfaceBrush"),
        BorderBrush = Brush("KcapBorderBrush"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(12, 8),
        Child = new SelectableTextBlock {
            Text = code.Lines.ToString().TrimEnd('\n', '\r'),
            FontFamily = Mono,
            FontSize = 12.5,
            TextWrapping = TextWrapping.NoWrap,
            Foreground = Brush("KcapTextBrush"),
        },
    };

    static Control List(string source, ListBlock list, ICommand? openLink) {
        var panel = new StackPanel { Spacing = 4 };
        var index = list.IsOrdered && int.TryParse(list.OrderedStart, out var start) ? start : 1;
        foreach (var item in list.OfType<ListItemBlock>()) {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("22,*") };
            var marker = new TextBlock {
                Text = list.IsOrdered ? $"{index++}." : "•",
                Foreground = Brush("KcapMutedBrush"),
                FontSize = 13.5,
                VerticalAlignment = VerticalAlignment.Top,
            };
            var content = new StackPanel { Spacing = 4 };
            foreach (var child in item) content.Children.Add(BuildBlock(source, child, openLink));
            Grid.SetColumn(content, 1);
            row.Children.Add(marker);
            row.Children.Add(content);
            panel.Children.Add(row);
        }
        return panel;
    }

    static Control Quote(string source, QuoteBlock quote, ICommand? openLink) {
        var content = new StackPanel { Spacing = 6 };
        foreach (var child in quote) content.Children.Add(BuildBlock(source, child, openLink));
        return new Border {
            BorderBrush = Brush("KcapBorderBrush"),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(12, 0, 0, 0),
            Child = content,
        };
    }

    static Control Literal(string source, Block block) {
        var span = block.Span;
        var text = span.Start >= 0 && span.End < source.Length && span.End >= span.Start
            ? source.Substring(span.Start, span.Length)
            : block.ToString() ?? "";
        return new SelectableTextBlock {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13.5,
            Foreground = Brush("KcapTextBrush"),
        };
    }
}
```

`Views/MarkdownView.cs`:

```csharp
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;

namespace Capacitor.App.Views;

/// Assistant prose: a ContentControl whose content is rebuilt from the markdown on every change.
public sealed class MarkdownView : ContentControl {
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<MarkdownView, string?>(nameof(Text));

    public static readonly StyledProperty<ICommand?> OpenLinkProperty =
        AvaloniaProperty.Register<MarkdownView, ICommand?>(nameof(OpenLink));

    static MarkdownView() {
        TextProperty.Changed.AddClassHandler<MarkdownView>((view, _) => view.Rebuild());
        OpenLinkProperty.Changed.AddClassHandler<MarkdownView>((view, _) => view.Rebuild());
    }

    public string? Text {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public ICommand? OpenLink {
        get => GetValue(OpenLinkProperty);
        set => SetValue(OpenLinkProperty, value);
    }

    void Rebuild() => Content = MarkdownBlocks.Build(Text ?? "", OpenLink);
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Same command. Expected: 3 PASS. If the pipe-table assertion fails because Markdig's default pipeline parses the table rows as a paragraph, the literal text still contains `| a | b |` — keep the assertion on `Contains`.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.App/Views/MarkdownBlocks.cs src/Capacitor.App/Views/MarkdownView.cs test/Capacitor.App.Tests.Unit/MarkdownBlocksTests.cs
git commit -m "Render assistant markdown with Markdig behind a link policy

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 14: `WorkspaceViewModel` — the Chat tab and the tab switch

**Files:**
- Modify: `src/Capacitor.App/ViewModels/WorkspaceViewModel.cs`
- Modify: `src/Capacitor.App/App.axaml.cs` (`BuildWorkspace`), every `new WorkspaceViewModel(` in tests (`grep -rn "new WorkspaceViewModel(" src test`)
- Test: `test/Capacitor.App.Tests.Unit/WorkspaceViewModelTests.cs`

**Interfaces:**
- Consumes: `ChatTabViewModel` (Tasks 11–12), `TranscriptProjection.For`.
- Produces on `WorkspaceViewModel`: constructor gains a trailing `IUrlOpener opener` parameter; `ChatTabViewModel? Chat` (bound); `WorkspaceTab ActiveTab` (default `Chat`); `bool IsChatActive`, `bool IsTerminalActive`; `ReactiveCommand<Unit, Unit> ShowChatCommand`, `ShowTerminalCommand`; `public enum WorkspaceTab { Chat, Terminal }`.

- [ ] **Step 1: Write the failing tests**

Append to `WorkspaceViewModelTests` (its `Build` helper gains `new RecordingOpener()` as the last constructor argument; update it):

```csharp
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Chat_is_the_default_tab_and_the_switch_commands_flip_it() {
        await RunOnUiAsync(async () => {
            var vm = Build(new FakeDaemonClientService(), NewActions(new ScriptedLocalControlOps(), new RecordingNotifier(), new RecordingOpener()), new FakeTerminalAttachClientFactory(), new FakeTimeProvider());

            await Assert.That(vm.ActiveTab).IsEqualTo(WorkspaceTab.Chat);
            await Assert.That(vm.IsChatActive).IsTrue();
            await Assert.That(vm.IsTerminalActive).IsFalse();

            await vm.ShowTerminalCommand.Execute();
            await Assert.That(vm.IsTerminalActive).IsTrue();
            await vm.ShowChatCommand.Execute();
            await Assert.That(vm.IsChatActive).IsTrue();
            await vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Chat_is_built_for_a_pty_dto_only_and_torn_down_with_the_workspace() {
        await RunOnUiAsync(async () => {
            var daemon = new FakeDaemonClientService();
            var vm = Build(daemon, NewActions(new ScriptedLocalControlOps(), new RecordingNotifier(), new RecordingOpener()), new FakeTerminalAttachClientFactory(), new FakeTimeProvider());
            await Assert.That(vm.Chat).IsNull();

            daemon.Agents.AddOrUpdate(Agent("a1", "gemini", hasTerminal: false));
            await (vm.Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
            await Assert.That(vm.Chat).IsNull();

            daemon.Agents.AddOrUpdate(Agent("a1", "gemini", hasTerminal: true));
            await (vm.Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
            await Assert.That(vm.Chat).IsNotNull();
            await Assert.That(vm.Chat!.Phase).IsEqualTo(ChatTabPhase.Unavailable); // gemini has no transcript projection

            var chat = vm.Chat;
            daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true));
            await Assert.That(vm.Chat).IsSameReferenceAs(chat); // built once

            await vm.TeardownAsync();
            await Assert.That(chat.PendingReadForTesting).IsNull();
        });
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/WorkspaceViewModelTests/*"`
Expected: build errors — `ActiveTab`, `Chat` missing.

- [ ] **Step 3: Implement**

In `WorkspaceViewModel.cs`:

```csharp
public enum WorkspaceTab { Chat, Terminal }
```

Add members:

```csharp
    ChatTabViewModel? _chat;
    /// Built once, on the first dto that passes the PTY gate — the projection is chosen by the
    /// dto's vendor. Null for a non-PTY session.
    public ChatTabViewModel? Chat {
        get => _chat;
        private set => this.RaiseAndSetIfChanged(ref _chat, value);
    }

    WorkspaceTab _activeTab = WorkspaceTab.Chat;
    public WorkspaceTab ActiveTab {
        get => _activeTab;
        private set {
            this.RaiseAndSetIfChanged(ref _activeTab, value);
            this.RaisePropertyChanged(nameof(IsChatActive));
            this.RaisePropertyChanged(nameof(IsTerminalActive));
        }
    }
    public bool IsChatActive => ActiveTab == WorkspaceTab.Chat;
    public bool IsTerminalActive => ActiveTab == WorkspaceTab.Terminal;

    public ReactiveCommand<Unit, Unit> ShowChatCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowTerminalCommand { get; }
```

Constructor: add the trailing parameter `IUrlOpener opener`, and after the existing `_sessionEnded` projection:

```csharp
        presence
            .Where(p => p.Dto is not null && HostedHarnessCatalog.ShowsTerminal(p.Dto.HasTerminal, p.Dto.Vendor))
            .Take(1)
            .Subscribe(p => Chat = new ChatTabViewModel(
                agentId, daemon, Terminal, TranscriptProjection.For(p.Dto!.Vendor), opener, time))
            .DisposeWith(_disposables);

        ShowChatCommand = ReactiveCommand.Create(() => { ActiveTab = WorkspaceTab.Chat; });
        ShowTerminalCommand = ReactiveCommand.Create(() => { ActiveTab = WorkspaceTab.Terminal; });
        _disposables.Add(ShowChatCommand);
        _disposables.Add(ShowTerminalCommand);
```

`TeardownAsync`:

```csharp
    public async Task TeardownAsync() {
        _disposables.Dispose();
        if (Chat is { } chat) await chat.TeardownAsync();
        await Terminal.TeardownAsync();
    }
```

Add `using Capacitor.Cli.Core;` for `TranscriptProjection`.

Composition root (`App.axaml.cs`, `BuildWorkspace`): append `new ShellUrlOpener()` — reuse the instance already built for `AgentActionService` by hoisting it into a local `var opener = new ShellUrlOpener();` used by both. Update every test constructing `WorkspaceViewModel` (grep) to pass `new RecordingOpener()`.

- [ ] **Step 4: Run the tests to verify they pass**

Run `WorkspaceViewModelTests`, then the whole app suite. Expected: green (the smoke and navigation suites compile against the new parameter).

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.App/ViewModels/WorkspaceViewModel.cs src/Capacitor.App/App.axaml.cs test/Capacitor.App.Tests.Unit
git commit -m "Give the workspace a Chat tab and make it the default

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 15: The views — `ChatTabView`, the tab strip, focus, follow-tail

**Files:**
- Create: `src/Capacitor.App/Views/ChatTabView.axaml`, `src/Capacitor.App/Views/ChatTabView.axaml.cs`
- Modify: `src/Capacitor.App/Views/WorkspaceView.axaml`, `src/Capacitor.App/Views/WorkspaceView.axaml.cs`, `src/Capacitor.App/Views/Converters.cs`
- Test: `test/Capacitor.App.Tests.Unit/WorkspaceViewSmokeTests.cs` (extend), `test/Capacitor.App.Tests.Unit/ChatTabViewSmokeTests.cs` (create)

**Interfaces:**
- Consumes: `WorkspaceViewModel.Chat/ActiveTab/IsChatActive/IsTerminalActive/ShowChatCommand/ShowTerminalCommand` (Task 14), `ChatTabViewModel` members (Tasks 11–12), `MarkdownView` (Task 13).
- Produces: named controls `ChatTabButton`, `ChatHost`, `ChatItems`, `ChatPhaseNote`, `ComposerInput`, `SendButton`, `TerminalBanners`; `ChatTabView.FocusComposer()`; converters `BoolToOpacityConverter`, `OffscreenWhenInactiveConverter`, `ToolOutcomeBrushConverter`.

- [ ] **Step 1: Write the failing smoke tests**

Extend `WorkspaceViewSmokeTests`: change `Build` to pass `new RecordingOpener()` and to accept an optional `Func<ITerminalSurface>? surface`; add the names to `WorkspaceView_resolves_all_eight_named_controls` (rename it to `..._named_controls`), and append:

```csharp
    static async Task<(Window Window, WorkspaceViewModel Vm, FakeDaemonClientService Daemon, FakeTerminalAttachClientFactory Attach)> ShowPtyAsync(Func<ITerminalSurface>? surface = null) {
        var (view, vm, daemon, attach) = Build(surface: surface);
        var window = new Window { Content = view, Width = 900, Height = 600 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        daemon.Agents.AddOrUpdate(Agent(AgentId, hasTerminal: true));
        await (vm.Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return (window, vm, daemon, attach);
    }

    static bool IsOffscreen(Control control) =>
        Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(control).IsOffscreen();

    static IEnumerable<IInputElement> TabRing(IInputElement start) {
        var seen = new HashSet<IInputElement>();
        var current = start;
        while (current is not null && seen.Add(current)) {
            yield return current;
            current = KeyboardNavigationHandler.GetNext(current, NavigationDirection.Next);
        }
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Chat_opens_first_and_the_tabs_swap_the_surfaces_while_the_terminal_stays_in_the_tree() {
        await RunOnUiAsync(async () => {
            var (window, vm, _, _) = await ShowPtyAsync();
            var chatHost = Find<Control>(window, "ChatHost")!;
            var banners = Find<Control>(window, "TerminalBanners")!;
            var terminalHost = Find<Control>(window, "TerminalHost")!;

            await Assert.That(vm.IsChatActive).IsTrue();
            await Assert.That(chatHost.IsEffectivelyVisible).IsTrue();
            await Assert.That(banners.IsEffectivelyVisible).IsFalse();
            await Assert.That(IsOffscreen(banners)).IsTrue();
            await Assert.That(terminalHost.IsEnabled).IsFalse();
            await Assert.That(terminalHost.IsHitTestVisible).IsFalse();
            await Assert.That(terminalHost.IsVisible).IsTrue();
            await Assert.That(IsOffscreen(terminalHost)).IsTrue();

            await vm.ShowTerminalCommand.Execute();
            Dispatcher.UIThread.RunJobs();
            await Assert.That(chatHost.IsEffectivelyVisible).IsFalse();
            await Assert.That(IsOffscreen(chatHost)).IsTrue();
            await Assert.That(terminalHost.IsEnabled).IsTrue();
            await Assert.That(IsOffscreen(terminalHost)).IsFalse();
            await Assert.That(Find<Control>(window, "TerminalHost")).IsNotNull();

            window.Close();
            Dispatcher.UIThread.RunJobs();
            await vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_workspace_opened_on_chat_still_reports_the_laid_out_pane_size() {
        await RunOnUiAsync(async () => {
            var (window, vm, _, attach) = await ShowPtyAsync(surface: () => new XtermTerminalSurface(80, 24));
            var client = attach.Created[^1];

            await Assert.That((client.Cols, client.Rows)).IsNotEqualTo((80, 24));
            await Assert.That(client.Cols).IsGreaterThan(80);

            window.Close();
            Dispatcher.UIThread.RunJobs();
            await vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Focus_follows_the_tab_and_survives_a_late_model_assignment() {
        await RunOnUiAsync(async () => {
            var (window, vm, _, attach) = await ShowPtyAsync();
            var composer = Find<TextBox>(window, "ComposerInput")!;
            var terminalHost = Find<Control>(window, "TerminalHost")!;
            await Assert.That(composer.IsFocused).IsTrue();

            await attach.Created[^1].TriggerAttached([]);
            Dispatcher.UIThread.RunJobs();
            await Assert.That(composer.IsFocused).IsTrue();

            await vm.ShowTerminalCommand.Execute();
            Dispatcher.UIThread.RunJobs();
            await Assert.That(terminalHost.IsFocused).IsTrue();

            await vm.ShowChatCommand.Execute();
            Dispatcher.UIThread.RunJobs();
            await Assert.That(composer.IsFocused).IsTrue();

            window.Close();
            Dispatcher.UIThread.RunJobs();
            await vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Tab_traversal_never_reaches_the_inactive_surface() {
        await RunOnUiAsync(async () => {
            var (window, vm, _, attach) = await ShowPtyAsync();
            attach.Created[^1].Result.SetResult(new AttachOutcome.Detached());
            await vm.Terminal.CurrentRunForTesting!;
            Dispatcher.UIThread.RunJobs();

            var composer = Find<TextBox>(window, "ComposerInput")!;
            var detach = Find<Control>(window, "DetachButton")!;
            var reattach = Find<Control>(window, "ReattachButton")!;
            var send = Find<Control>(window, "SendButton")!;
            var terminalHost = Find<Control>(window, "TerminalHost")!;

            var ringFromComposer = TabRing(composer).ToList();
            await Assert.That(ringFromComposer).DoesNotContain(detach);
            await Assert.That(ringFromComposer).DoesNotContain(reattach);

            await vm.ShowTerminalCommand.Execute();
            Dispatcher.UIThread.RunJobs();
            var ringFromTerminal = TabRing(terminalHost).ToList();
            await Assert.That(ringFromTerminal).DoesNotContain(composer);
            await Assert.That(ringFromTerminal).DoesNotContain(send);

            window.Close();
            Dispatcher.UIThread.RunJobs();
            await vm.TeardownAsync();
        });
    }
```

`ChatTabViewSmokeTests.cs` (batching and follow-tail; the view is hosted directly with a `ChatTabViewModel` DataContext):

```csharp
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

public class ChatTabViewSmokeTests {
    [TempDir] public required TempDir Tmp { get; init; }

    const string UserLine = """{"type":"user","message":{"role":"user","content":"hello"}}""";

    sealed class Host {
        public FakeDaemonClientService Daemon { get; } = new();
        public FakeTimeProvider Time { get; } = new();
        public TerminalTabViewModel Terminal { get; }
        public ChatTabViewModel Chat { get; }
        public ChatTabView View { get; }
        public Window Window { get; }
        public ScrollViewer Scroll => View.GetVisualDescendants().OfType<ScrollViewer>().First();

        public Host() {
            Terminal = new TerminalTabViewModel("a1", Daemon, new FakeTerminalAttachClientFactory().Factory, () => new FakeTerminalSurface(), Time);
            Chat = new ChatTabViewModel("a1", Daemon, Terminal, TranscriptProjection.For("claude"), new RecordingOpener(), Time);
            View = new ChatTabView { DataContext = Chat };
            Window = new Window { Content = View, Width = 800, Height = 600 };
            Window.Show();
            Dispatcher.UIThread.RunJobs();
        }

        public async Task LoadAsync(string path) {
            Daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true) with { TranscriptPath = path });
            await (Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
            await (Chat.PendingReadForTesting ?? Task.CompletedTask);
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
        }

        public async Task AppendAndTickAsync(string path, int lines) {
            File.AppendAllLines(path, Enumerable.Repeat(UserLine, lines));
            Time.Advance(ChatTabViewModel.PollInterval);
            await (Chat.PendingReadForTesting ?? Task.CompletedTask);
        }

        public bool AtBottom() => Scroll.Offset.Y + Scroll.Viewport.Height >= Scroll.Extent.Height - 1;

        public async Task CloseAsync() {
            Window.Close();
            Dispatcher.UIThread.RunJobs();
            await Chat.TeardownAsync();
            await Terminal.TeardownAsync();
        }
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_large_initial_load_is_one_notification_into_a_bounded_number_of_containers() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            var path = Tmp.CreateFile("big.jsonl", Enumerable.Repeat(UserLine, 5000).ToArray());
            var notifications = 0;
            ((INotifyCollectionChanged)host.Chat.Items).CollectionChanged += (_, _) => notifications++;

            await host.LoadAsync(path);

            await Assert.That(host.Chat.Items).HasCount().EqualTo(5000);
            await Assert.That(notifications).IsEqualTo(1);
            var items = host.View.FindControl<ItemsControl>("ChatItems")!;
            await Assert.That(items.GetRealizedContainers().Count()).IsLessThan(200);
            await host.CloseAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Follow_tail_tracks_the_bottom_and_leaves_a_scrolled_up_reader_alone() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            var path = Tmp.CreateFile("t.jsonl", Enumerable.Repeat(UserLine, 60).ToArray());
            await host.LoadAsync(path);
            await Assert.That(host.AtBottom()).IsTrue();

            await host.AppendAndTickAsync(path, 20);
            Dispatcher.UIThread.RunJobs();
            host.Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            await Assert.That(host.AtBottom()).IsTrue();

            host.Scroll.Offset = new Vector(0, 0);
            host.Window.UpdateLayout();
            await host.AppendAndTickAsync(path, 20);
            Dispatcher.UIThread.RunJobs();
            host.Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            await Assert.That(host.Scroll.Offset.Y).IsEqualTo(0);

            // At the bottom, append, then scroll up before the layout pass completes.
            host.Scroll.ScrollToEnd();
            host.Window.UpdateLayout();
            await host.AppendAndTickAsync(path, 20);
            host.Scroll.Offset = new Vector(0, 0);
            Dispatcher.UIThread.RunJobs();
            host.Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            await Assert.That(host.Scroll.Offset.Y).IsEqualTo(0);
            await host.CloseAsync();
        });
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/*Smoke*/*"`
Expected: build errors (`ChatTabView`, converters) and, once compiling, failing name lookups.

- [ ] **Step 3: Converters**

Append to `Views/Converters.cs`:

```csharp
/// Opacity for the off-tab terminal: it must stay measured (so the PTY gets the real pane size),
/// so it is faded rather than collapsed.
public sealed class BoolToOpacityConverter : IValueConverter {
    public static readonly BoolToOpacityConverter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? 1.0 : 0.0;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// A visible-but-faded control is still announced as onscreen by default; the inactive terminal
/// must be reported offscreen instead.
public sealed class OffscreenWhenInactiveConverter : IValueConverter {
    public static readonly OffscreenWhenInactiveConverter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Avalonia.Automation.IsOffscreenBehavior.Default : Avalonia.Automation.IsOffscreenBehavior.Offscreen;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class ToolOutcomeBrushConverter : IValueConverter {
    public static readonly ToolOutcomeBrushConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Avalonia.Application.Current?.FindResource(value is true ? "KcapDangerBrush" : "KcapAccentBrush");
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
```

- [ ] **Step 4: `ChatTabView.axaml`**

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="clr-namespace:Capacitor.App.ViewModels"
             xmlns:views="clr-namespace:Capacitor.App.Views"
             x:Class="Capacitor.App.Views.ChatTabView"
             x:DataType="vm:ChatTabViewModel">
    <Grid RowDefinitions="*,Auto">
        <!-- The template owns the ScrollViewer: that is the shape Avalonia virtualizes. An
             ItemsControl inside an external ScrollViewer is measured at infinite height. -->
        <ItemsControl x:Name="ChatItems" Grid.Row="0" ItemsSource="{Binding Items}">
            <ItemsControl.Template>
                <ControlTemplate>
                    <ScrollViewer VerticalScrollBarVisibility="Auto">
                        <ItemsPresenter Name="PART_ItemsPresenter" Margin="22,26,22,8" />
                    </ScrollViewer>
                </ControlTemplate>
            </ItemsControl.Template>
            <ItemsControl.ItemsPanel>
                <ItemsPanelTemplate>
                    <VirtualizingStackPanel />
                </ItemsPanelTemplate>
            </ItemsControl.ItemsPanel>
            <ItemsControl.DataTemplates>
                <DataTemplate x:DataType="vm:UserTurnItem">
                    <Border HorizontalAlignment="Right" MaxWidth="520" Margin="0,0,0,20" Padding="14,11" CornerRadius="10"
                            Background="{StaticResource KcapSurfaceRaisedBrush}">
                        <SelectableTextBlock Text="{Binding Text}" TextWrapping="Wrap" FontSize="13.5" LineHeight="20"
                                             Foreground="{StaticResource KcapTextBrush}" />
                    </Border>
                </DataTemplate>
                <DataTemplate x:DataType="vm:AssistantTextItem">
                    <views:MarkdownView Text="{Binding Text}" MaxWidth="660" HorizontalAlignment="Left" Margin="0,0,0,20"
                                        OpenLink="{Binding $parent[ItemsControl].((vm:ChatTabViewModel)DataContext).OpenLinkCommand}" />
                </DataTemplate>
                <DataTemplate x:DataType="vm:ToolCallItem">
                    <StackPanel Orientation="Horizontal" Spacing="9" Margin="0,0,0,20">
                        <TextBlock Text="›" FontSize="13" Foreground="{StaticResource KcapFaintBrush}" VerticalAlignment="Center" />
                        <TextBlock Text="{Binding Name}" FontSize="11.5" Foreground="{StaticResource KcapMutedBrush}" VerticalAlignment="Center" />
                        <TextBlock Text="{Binding Detail}" FontSize="11" Foreground="{StaticResource KcapFaintBrush}"
                                   TextTrimming="CharacterEllipsis" MaxWidth="480" VerticalAlignment="Center" />
                        <TextBlock Text="{Binding OutcomeGlyph}" FontSize="11" VerticalAlignment="Center"
                                   Foreground="{Binding IsError, Converter={x:Static views:ToolOutcomeBrushConverter.Instance}}" />
                    </StackPanel>
                </DataTemplate>
            </ItemsControl.DataTemplates>
        </ItemsControl>

        <TextBlock x:Name="ChatPhaseNote" Grid.Row="0" Text="{Binding PhaseNote}"
                   IsVisible="{Binding PhaseNote, Converter={x:Static StringConverters.IsNotNullOrEmpty}}"
                   Foreground="{StaticResource KcapMutedBrush}" FontSize="12.5"
                   HorizontalAlignment="Center" VerticalAlignment="Center" />

        <Border Grid.Row="1" Margin="22,8,22,18" MaxWidth="660" HorizontalAlignment="Left"
                Background="{StaticResource KcapSurfaceBrush}" BorderBrush="{StaticResource KcapBorderBrush}"
                BorderThickness="1" CornerRadius="10" Padding="13,12">
            <StackPanel Spacing="6">
                <TextBox x:Name="ComposerInput" Text="{Binding ComposerText}" AcceptsReturn="True" TextWrapping="Wrap"
                         MinHeight="38" MaxHeight="160" Background="Transparent" BorderThickness="0"
                         Watermark="Write a message" KeyDown="OnComposerKeyDown" />
                <Grid ColumnDefinitions="*,Auto,Auto,Auto,Auto">
                    <TextBlock Text="{Binding ComposerHint}" FontSize="11" Foreground="{StaticResource KcapFaintBrush}"
                               VerticalAlignment="Center" TextTrimming="CharacterEllipsis" />
                    <TextBlock Grid.Column="1" Text="{Binding ModelLabel}" FontSize="11" Foreground="{StaticResource KcapFaintBrush}"
                               VerticalAlignment="Center" Margin="12,0,0,0" />
                    <Ellipse Grid.Column="2" Width="7" Height="7" Fill="{Binding StatusDot}" VerticalAlignment="Center" Margin="12,0,6,0" />
                    <TextBlock Grid.Column="3" Text="{Binding StatusText}" FontSize="11" Foreground="{StaticResource KcapMutedBrush}"
                               VerticalAlignment="Center" />
                    <Button x:Name="SendButton" Grid.Column="4" Content="Send" Command="{Binding SendCommand}" Margin="12,0,0,0"
                            Padding="12,4" FontSize="12" FontWeight="SemiBold" CornerRadius="7"
                            Background="{StaticResource KcapAccentBrush}" Foreground="#07120E" />
                </Grid>
            </StackPanel>
        </Border>
    </Grid>
</UserControl>
```

- [ ] **Step 5: `ChatTabView.axaml.cs`**

```csharp
using System.Collections.Specialized;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

/// Follow-tail lives here, stateless across events: "was at end" is decided at the collection
/// change from the OLD extent (the new rows are not measured yet), and the scroll is applied
/// after the layout pass that establishes the new extent — only if the reader has not moved.
public partial class ChatTabView : UserControl {
    INotifyCollectionChanged? _observed;

    public ChatTabView() {
        InitializeComponent();
        DataContextChanged += (_, _) => Observe((DataContext as ChatTabViewModel)?.Items as INotifyCollectionChanged);
    }

    void Observe(INotifyCollectionChanged? items) {
        if (_observed is not null) _observed.CollectionChanged -= OnItemsChanged;
        _observed = items;
        if (_observed is not null) _observed.CollectionChanged += OnItemsChanged;
    }

    ScrollViewer? Scroll => ChatItems.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

    void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) {
        if (e.Action != NotifyCollectionChangedAction.Add || Scroll is not { } scroll) return;
        var wasAtEnd = scroll.Offset.Y + scroll.Viewport.Height >= scroll.Extent.Height - 1;
        if (!wasAtEnd) return;

        var captured = scroll.Offset;
        void OnLayoutUpdated(object? _, EventArgs __) {
            scroll.LayoutUpdated -= OnLayoutUpdated;
            if (scroll.Offset == captured) scroll.ScrollToEnd();
        }
        scroll.LayoutUpdated += OnLayoutUpdated;
    }

    // Enter sends, Shift+Enter is the TextBox's own newline.
    void OnComposerKeyDown(object? sender, KeyEventArgs e) {
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
        e.Handled = true;
        if (DataContext is ChatTabViewModel vm && ((ICommand)vm.SendCommand).CanExecute(null))
            vm.SendCommand.Execute().Subscribe();
    }

    public void FocusComposer() => ComposerInput.Focus();
}
```

- [ ] **Step 6: `WorkspaceView.axaml` changes**

Replace the tab-strip `Grid` content with:

```xml
            <Grid Margin="20,0">
                <StackPanel Orientation="Horizontal" Spacing="6" IsVisible="{Binding ShowsTerminalTab}" HorizontalAlignment="Left" VerticalAlignment="Center">
                    <Button x:Name="ChatTabButton" Content="Chat" Classes="tab" Classes.active="{Binding IsChatActive}" Command="{Binding ShowChatCommand}" />
                    <Button x:Name="TerminalTabButton" Content="Terminal" Classes="tab" Classes.active="{Binding IsTerminalActive}" Command="{Binding ShowTerminalCommand}" />
                </StackPanel>
                <TextBlock x:Name="NoTerminalNote" Text="{Binding NoTerminalNote}" IsVisible="{Binding !ShowsTerminalTab}"
                           Foreground="{StaticResource KcapMutedBrush}" FontSize="12.5" VerticalAlignment="Center" />
            </Grid>
```

Add to the UserControl a `<UserControl.Styles>` block:

```xml
    <UserControl.Styles>
        <Style Selector="Button.tab">
            <Setter Property="Background" Value="Transparent" />
            <Setter Property="BorderThickness" Value="0" />
            <Setter Property="CornerRadius" Value="7" />
            <Setter Property="Foreground" Value="{StaticResource KcapMutedBrush}" />
            <Setter Property="FontSize" Value="12.5" />
            <Setter Property="Padding" Value="12,5" />
        </Style>
        <Style Selector="Button.tab.active">
            <Setter Property="Background" Value="{StaticResource KcapSurfaceRaisedBrush}" />
            <Setter Property="Foreground" Value="{StaticResource KcapTextBrush}" />
            <Setter Property="FontWeight" Value="SemiBold" />
        </Style>
    </UserControl.Styles>
```

In the content `Grid` (row 2): give `TerminalHost` the inactive-tab treatment and wrap the banners:

```xml
            <terminal:TerminalControl x:Name="TerminalHost" IsVisible="{Binding ShowsTerminalTab}"
                                       IsEnabled="{Binding IsTerminalActive}"
                                       IsHitTestVisible="{Binding IsTerminalActive}"
                                       Opacity="{Binding IsTerminalActive, Converter={x:Static views:BoolToOpacityConverter.Instance}}"
                                       AutomationProperties.IsOffscreenBehavior="{Binding IsTerminalActive, Converter={x:Static views:OffscreenWhenInactiveConverter.Instance}}"
                                       FontFamily="Menlo,Monaco,Consolas,Cascadia Mono,DejaVu Sans Mono,monospace"
                                       CaretBrush="{StaticResource KcapAccentBrush}"
                                       Model="{Binding Terminal.Surface, Converter={x:Static views:TerminalSurfaceModelConverter.Instance}}" />

            <Panel x:Name="TerminalBanners" IsVisible="{Binding IsTerminalActive}">
                <!-- the five existing banner Borders, unchanged, move inside this Panel -->
            </Panel>

            <Panel IsVisible="{Binding IsChatActive}">
                <views:ChatTabView x:Name="ChatHost" DataContext="{Binding Chat}" />
            </Panel>
```

Keep the existing comments about `FontFamily`/`CaretBrush`; drop the "the strip gains Chat beside it" comment.

- [ ] **Step 7: `WorkspaceView.axaml.cs` focus wiring**

Replace the constructor body:

```csharp
    IDisposable? _tabFocus;

    public WorkspaceView() {
        InitializeComponent();
        // The control draws its caret and takes keystrokes only while focused; a Model assignment
        // is the "terminal became live" moment — but only the Terminal tab may take focus, or a
        // reattach under the Chat tab would steal it from the composer.
        TerminalHost.PropertyChanged += (_, e) => {
            if (e.Property == SvcSystems.UI.Terminal.TerminalControl.ModelProperty && TerminalHost.Model is not null
                && DataContext is WorkspaceViewModel { IsTerminalActive: true })
                TerminalHost.Focus();
        };
        DataContextChanged += (_, _) => {
            _tabFocus?.Dispose();
            _tabFocus = (DataContext as WorkspaceViewModel)?
                .WhenAnyValue(vm => vm.ActiveTab)
                .Subscribe(tab => Dispatcher.UIThread.Post(() => {
                    if (tab == WorkspaceTab.Chat) ChatHost.FocusComposer();
                    else TerminalHost.Focus();
                }, DispatcherPriority.Loaded));
        };
    }
```

Add `using Avalonia.Threading;`, `using Capacitor.App.ViewModels;`, `using ReactiveUI;`.

- [ ] **Step 8: Run the tests to verify they pass**

Run the smoke filter, then the whole app suite. Expected: green. Known sharp edges: if `ChatTabButton` is not found, the `StackPanel` wrapper hides it before a dto arrives — the names test pushes no dto and the buttons are still instantiated (`IsVisible=false` keeps them in the tree), so `Find` must still resolve them; if `IsOffscreen(terminalHost)` reads false while faded, the converter binding is not applied — `AutomationProperties.IsOffscreenBehavior` is an attached property and must be set exactly as written.

- [ ] **Step 9: Commit**

```bash
git add src/Capacitor.App/Views test/Capacitor.App.Tests.Unit/WorkspaceViewSmokeTests.cs test/Capacitor.App.Tests.Unit/ChatTabViewSmokeTests.cs
git commit -m "Render the Chat tab beside the terminal with focus and follow-tail

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 16: CHANGES entry, full acceptance, PR

**Files:**
- Modify: `docs/CHANGES.md`
- No new tests; this task runs the whole acceptance set.

- [ ] **Step 1: `docs/CHANGES.md`**

Insert before `## Launch and stop command routing`:

```markdown
## Session chat

**AI-2196** (spec: `docs/superpowers/specs/2026-08-26-ai2196-chat-for-pty-harnesses-design.md`)
renders a Claude or interactive Codex session's own transcript as the workspace's Chat tab and sends
composer text to the PTY. **The daemon, not the app, knows where the transcript is**: every PTY launch
runs the same transcript discovery the server-driven path used, and the link-resolved path rides
`AgentStatusDto.transcript_path` — link-resolved because the per-worktree Claude project dir is a
symlink the launcher deletes at cleanup. Discovery runs until the *path* is known and pulses the
status notifier before any server report. Every transcript open shares read/write/delete; the tail
promises only length-regression reset. **Composer sends are accepted, never acknowledged**, and one
at a time: bracketed paste, a 150 ms wait past Codex's post-paste Enter suppression, then one CR —
only if the terminal's opening token is unchanged. The token advances only through `BeginAttempt`
(after the attach lane is won) and `Invalidate` (detach, teardown, removal, every terminal outcome);
an attempt's own `Connecting`/`Attached` publishes never advance it, and a stale token discards a
late `Attached`, so a queued attach callback cannot reopen a terminal the daemon already dropped.
`TerminalHost` stays laid out under the Chat tab (faded, disabled, reported offscreen) so the PTY
clamp sees the real pane size; everything else collapses with `IsVisible`. Links open only through
`LinkPolicy` (absolute http/https) via one tab-level command.
```

- [ ] **Step 2: Full acceptance**

```bash
dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj
dotnet test --solution Capacitor.slnx
dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'
```

Expected: build clean; every suite green except the pre-existing local nudge failures noted in memory (7 unit + 1 integration `session-start` tests) — verify by name that nothing else fails; the publish grep prints nothing.

- [ ] **Step 3: Manual QA (one live session)**

`dotnet run --project src/Capacitor.App/Capacitor.App.csproj` against a running daemon; start a Claude session from the launcher; confirm: Chat opens first and shows "Waiting for the transcript…" for a few seconds, then the rows; the Terminal tab still works and reports a real size (`stty size` inside the session is not 80 24); typing in the composer and pressing Enter reaches the agent (the reply appears in Chat within a second of the transcript write); a markdown reply renders lists/code/links; a link click opens the browser; Shift+Enter adds a line.

- [ ] **Step 4: Commit and open the PR**

```bash
git add docs/CHANGES.md
git commit -m "Record the session chat design constraints

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
git push -u origin alexeyzimarev/ai-2196-desktop-shell-chat-for-pty-harnesses-rendered-from-the
```

Open the PR with title `Desktop shell: chat for PTY harnesses` and a description written from `.github/PULL_REQUEST_TEMPLATE.md`'s comment block; the reference line carries `AI-2196` and the GitHub issue with a closing keyword if one exists (ask the owner for the number — never invent one).

---

## Self-review notes

- **Spec coverage:** §1 → Tasks 1, 6, 7, 8; §2 → Tasks 2–5; §3 `ChatTabViewModel` → Tasks 11–12; Markdown → Task 13; tab strip and focus → Tasks 14–15; composer and send gate → Tasks 10, 12, 15; §4 tests → each task's Step 1 plus Task 16's acceptance; Risks → CHANGES entry.
- **Type consistency:** `TranscriptPath` (DTO and `AgentInstance`), `ITranscriptProjection.Project`, `TailRead(Lines, Status, Failure)`, `TrySendText`/`CanAcceptText`/`SendAvailability`, `ChatTabViewModel(agentId, daemon, terminal, projection, opener, time)`, `WorkspaceViewModel(..., time, opener)`, `ToolDetail.From(inputJson)` are used with the same shapes in every task that names them.
- **Deliberate deviations from the spec's test list, and why:** the resume boundaries are pinned at the locator level (a foreign-cwd Claude transcript is never a winner; the Codex older-stamp rule already has its own test) plus an orchestrator test that local spawns start discovery — the same guarantee without a real worktree or an env pin; `ToolDetail.From` takes only the input JSON, since the name contributed nothing to the detail.
