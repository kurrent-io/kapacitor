# Elicitation Question Cards (AI-2361) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Render a PTY Claude session's `AskUserQuestion` as an answerable question card in the desktop app, answering over the existing `permission/1` wire with `allow` + `updatedInput.answers`.

**Architecture:** App-side classification of existing pending-permission entries — no daemon, CLI, or wire change. One Core helper (`ClaudeElicitation`) owns the parse (strict, capped, immutable model) and the answer composition (validated against the parse); the app service gains an `AnswerAsync` path and a single-snapshot `PendingSummary`; the Chat tab's NEEDS YOU row hosts mixed permission/question cards; the tray splits its wording.

**Tech Stack:** .NET 10 NativeAOT, Avalonia (headless tests), ReactiveUI, DynamicData, TUnit on Microsoft Testing Platform.

**Spec:** `docs/superpowers/specs/2026-08-30-ai2361-elicitation-question-cards-design.md` — read it first; every constraint below is argued there.

## Global Constraints

- No new `FrameType` values, no capability change, no daemon or CLI edits. The wire is `PermissionResolveDto(RequestId, "allow", ApplyPermissions: null, UpdatedInput: <composed>)`.
- Caps (UTF-16 code units), Core constants: `MaxQuestions = 8`, `MaxOptionsPerQuestion = 16`, `MaxQuestionTextChars = 4096`, `MaxOptionLabelChars = 1024`, `MaxOtherTextChars = 8192`.
- Answer shape: `{"questions": <original array verbatim>, "answers": {<question text>: string | string[]}}` — string for single-select, array for multi-select, values validated against the parsed options.
- JSON reads go through `JsonElementExtensions` (`Prop`/`Str`/`Bool`/`IsObject`/`IsArray`) — raw `ValueKind` comparisons are banned by CLAUDE.md.
- `new JsonArray(…)`/`JsonArray.Add(JsonNode)` only — a collection expression or the generic `Add<T>` requires dynamic code (AOT trap).
- Returned/retained `JsonElement`s must be detached (`Clone()` off a scoped `JsonDocument`), never tied to a disposable owner.
- Comments: scarce, per CLAUDE.md — do not imitate the long historical comments in neighboring files; never narrate the spec or review rounds.
- Commit subjects: one imperative clause, ≤80 chars, **no issue reference** (no GitHub issue number exists for AI-2361 yet — never invent one).
- Test conventions: TUnit; UI tests run under `AvaloniaSession` with `[NotInParallel("AvaloniaSession")]`; `WaitUntilAsync` from `WorkspaceFixtures` for async settling.
- After all tasks: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` must print nothing.

---

### Task 1: Core parse — `ClaudeElicitation.TryParse` and the immutable model

**Files:**
- Create: `src/Capacitor.Cli.Core/ClaudeElicitation.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/ClaudeElicitationTests.cs` (create)

**Interfaces:**
- Consumes: `JsonElementExtensions` (`Prop`, `Str`, `Bool`, `IsObject`, `IsArray` — `src/Capacitor.Cli.Core/JsonElementExtensions.cs`).
- Produces (later tasks depend on these exact names):
  - `public static class ClaudeElicitation` with `public const string ToolName = "AskUserQuestion";`, the five cap constants above, and `public static ElicitationQuestions? TryParse(string? toolInputJson)`.
  - `public sealed class ElicitationQuestions { public JsonElement QuestionsJson { get; } public ImmutableArray<ElicitationQuestion> Questions { get; } }` — internal ctor.
  - `public sealed class ElicitationQuestion { public string Question { get; } public string? Header { get; } public bool MultiSelect { get; } public ImmutableArray<ElicitationOption> Options { get; } }` — internal ctor.
  - `public sealed class ElicitationOption { public string Label { get; } public string? Description { get; } }` — internal ctor.

- [ ] **Step 1: Create the branch and commit the spec**

```bash
git switch -c alexeyzimarev/ai-2361-desktop-shell-elicitation-questions-for-pty-sessions-through
git add docs/superpowers/specs/2026-08-30-ai2361-elicitation-question-cards-design.md docs/superpowers/plans/2026-08-30-ai2361-elicitation-question-cards.md
git commit -m "Add the AI-2361 elicitation question cards spec and plan"
```

- [ ] **Step 2: Write the failing parse tests**

Create `test/Capacitor.Cli.Core.Tests.Unit/ClaudeElicitationTests.cs`. Helpers is a global using; no imports needed beyond the ones shown.

```csharp
using System.Collections.Immutable;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Core.Tests.Unit;

public class ClaudeElicitationTests {
    static string OneQuestion(string extra = "") =>
        $$"""{"questions":[{"question":"Pick one","header":"Choice","options":[{"label":"A","description":"first"},{"label":"B"}]{{extra}}}]}""";

    [Test]
    public async Task Parses_every_question_in_payload_order() {
        var parsed = ClaudeElicitation.TryParse(
            """{"questions":[{"question":"Q1","options":[{"label":"A"}]},{"question":"Q2","multiSelect":true,"options":[{"label":"X"},{"label":"Y"}]}]}""");
        await Assert.That(parsed).IsNotNull();
        await Assert.That(parsed!.Questions.Length).IsEqualTo(2);
        await Assert.That(parsed.Questions[0].Question).IsEqualTo("Q1");
        await Assert.That(parsed.Questions[0].MultiSelect).IsFalse();
        await Assert.That(parsed.Questions[1].Question).IsEqualTo("Q2");
        await Assert.That(parsed.Questions[1].MultiSelect).IsTrue();
        await Assert.That(parsed.Questions[1].Options.Select(o => o.Label)).IsEquivalentTo(["X", "Y"]);
    }

    [Test]
    public async Task Header_and_description_carry_through_and_snake_case_flag_is_honored() {
        var parsed = ClaudeElicitation.TryParse(
            """{"questions":[{"question":"Q","header":"Scope","multi_select":true,"options":[{"label":"A","description":"first"}]}]}""");
        await Assert.That(parsed!.Questions[0].Header).IsEqualTo("Scope");
        await Assert.That(parsed.Questions[0].MultiSelect).IsTrue();
        await Assert.That(parsed.Questions[0].Options[0].Description).IsEqualTo("first");
    }

    [Test]
    [Arguments("""{"questions":[]}""")]                                              // empty array
    [Arguments("""{"questions":{}}""")]                                              // not an array
    [Arguments("""{"noquestions":true}""")]                                          // missing
    [Arguments("""[1,2]""")]                                                         // input not an object
    [Arguments("""{"questions":["notanobject"]}""")]                                 // entry not an object
    [Arguments("""{"questions":[{"question":""}]}""")]                               // blank text
    [Arguments("""{"questions":[{"question":"   "}]}""")]                            // whitespace text
    [Arguments("""{"questions":[{"header":"h"}]}""")]                                // missing text
    [Arguments("""{"questions":[{"question":42}]}""")]                               // wrong-typed text
    [Arguments("""{"questions":[{"question":"Q"},{"question":"Q"}]}""")]             // duplicate texts
    [Arguments("""{"questions":[{"question":"Q","multiSelect":"yes"}]}""")]          // non-boolean flag
    [Arguments("""{"questions":[{"question":"Q","multiSelect":true,"multi_select":false}]}""")] // conflicting spellings
    [Arguments("""{"questions":[{"question":"Q","options":{}}]}""")]                 // options not an array
    [Arguments("""{"questions":[{"question":"Q","options":["x"]}]}""")]              // option not an object
    [Arguments("""{"questions":[{"question":"Q","options":[{"label":""}]}]}""")]     // blank label
    [Arguments("""{"questions":[{"question":"Q","options":[{"nolabel":1}]}]}""")]    // missing label
    [Arguments("not json")]
    [Arguments(null)]
    public async Task Malformed_protocol_fields_fail_the_parse(string? payload) {
        await Assert.That(ClaudeElicitation.TryParse(payload)).IsNull();
    }

    [Test]
    public async Task Agreeing_flag_spellings_and_absent_options_are_fine() {
        var both = ClaudeElicitation.TryParse("""{"questions":[{"question":"Q","multiSelect":true,"multi_select":true}]}""");
        await Assert.That(both!.Questions[0].MultiSelect).IsTrue();
        await Assert.That(both.Questions[0].Options.Length).IsEqualTo(0);
        var empty = ClaudeElicitation.TryParse("""{"questions":[{"question":"Q","options":[]}]}""");
        await Assert.That(empty!.Questions[0].Options.Length).IsEqualTo(0);
    }

    [Test]
    public async Task Display_fields_with_wrong_types_or_whitespace_read_as_absent() {
        var parsed = ClaudeElicitation.TryParse(
            """{"questions":[{"question":"Q","header":42,"options":[{"label":"A","description":"  "}]}]}""");
        await Assert.That(parsed!.Questions[0].Header).IsNull();
        await Assert.That(parsed.Questions[0].Options[0].Description).IsNull();
    }

    [Test]
    public async Task Duplicate_option_labels_keep_the_first() {
        var parsed = ClaudeElicitation.TryParse(
            """{"questions":[{"question":"Q","options":[{"label":"A","description":"one"},{"label":"A","description":"two"},{"label":"B"}]}]}""");
        await Assert.That(parsed!.Questions[0].Options.Select(o => o.Label)).IsEquivalentTo(["A", "B"]);
        await Assert.That(parsed.Questions[0].Options[0].Description).IsEqualTo("one");
    }

    [Test]
    public async Task Caps_pass_at_the_boundary_and_fail_one_over() {
        static string Questions(int n) =>
            $$"""{"questions":[{{string.Join(",", Enumerable.Range(0, n).Select(i => $$"""{"question":"Q{{i}}"}"""))}}]}""";
        await Assert.That(ClaudeElicitation.TryParse(Questions(ClaudeElicitation.MaxQuestions))).IsNotNull();
        await Assert.That(ClaudeElicitation.TryParse(Questions(ClaudeElicitation.MaxQuestions + 1))).IsNull();

        static string Options(int n) =>
            $$"""{"questions":[{"question":"Q","options":[{{string.Join(",", Enumerable.Range(0, n).Select(i => $$"""{"label":"L{{i}}"}"""))}}]}]}""";
        await Assert.That(ClaudeElicitation.TryParse(Options(ClaudeElicitation.MaxOptionsPerQuestion))).IsNotNull();
        await Assert.That(ClaudeElicitation.TryParse(Options(ClaudeElicitation.MaxOptionsPerQuestion + 1))).IsNull();

        static string Text(int chars) => $$"""{"questions":[{"question":"{{new string('q', chars)}}"}]}""";
        await Assert.That(ClaudeElicitation.TryParse(Text(ClaudeElicitation.MaxQuestionTextChars))).IsNotNull();
        await Assert.That(ClaudeElicitation.TryParse(Text(ClaudeElicitation.MaxQuestionTextChars + 1))).IsNull();

        static string Label(int chars) => $$"""{"questions":[{"question":"Q","options":[{"label":"{{new string('l', chars)}}"}]}]}""";
        await Assert.That(ClaudeElicitation.TryParse(Label(ClaudeElicitation.MaxOptionLabelChars))).IsNotNull();
        await Assert.That(ClaudeElicitation.TryParse(Label(ClaudeElicitation.MaxOptionLabelChars + 1))).IsNull();
    }

    [Test]
    public async Task Retained_questions_element_outlives_the_parse() {
        var parsed = ClaudeElicitation.TryParse(OneQuestion());
        GC.Collect();
        GC.WaitForPendingFinalizers();
        await Assert.That(parsed!.QuestionsJson.GetRawText()).Contains("Pick one");
    }

    [Test]
    public async Task Model_collections_are_immutable() {
        var parsed = ClaudeElicitation.TryParse(OneQuestion());
        await Assert.That(parsed!.Questions is ImmutableArray<ElicitationQuestion>).IsTrue();
        await Assert.That(parsed.Questions[0].Options is ImmutableArray<ElicitationOption>).IsTrue();
    }
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/ClaudeElicitationTests/*"`
Expected: build error — `ClaudeElicitation` does not exist.

- [ ] **Step 4: Implement the parser and model**

Create `src/Capacitor.Cli.Core/ClaudeElicitation.cs`:

```csharp
using System.Collections.Immutable;
using System.Text.Json;

namespace Capacitor.Cli.Core;

public sealed class ElicitationOption {
    internal ElicitationOption(string label, string? description) { Label = label; Description = description; }
    public string Label { get; }
    public string? Description { get; }
}

public sealed class ElicitationQuestion {
    internal ElicitationQuestion(string question, string? header, bool multiSelect, ImmutableArray<ElicitationOption> options) {
        Question = question; Header = header; MultiSelect = multiSelect; Options = options;
    }
    public string Question { get; }
    public string? Header { get; }
    public bool MultiSelect { get; }
    public ImmutableArray<ElicitationOption> Options { get; }
}

/// Parser-created only: internal ctor + immutable collections ground ComposeAnswers' validation
/// in parse output, so no caller can hand it a fabricated or mutated model.
public sealed class ElicitationQuestions {
    internal ElicitationQuestions(JsonElement questionsJson, ImmutableArray<ElicitationQuestion> questions) {
        QuestionsJson = questionsJson; Questions = questions;
    }
    /// Detached clone of the payload's questions array, replayed verbatim into the answer.
    public JsonElement QuestionsJson { get; }
    public ImmutableArray<ElicitationQuestion> Questions { get; }
}

/// The Claude AskUserQuestion contract: parsing the hook's tool_input and composing the
/// updatedInput answer. Caps bound the composed resolve payload (spec decision 6); a payload
/// over any cap is unparseable and falls back to the plain permission card.
public static class ClaudeElicitation {
    public const string ToolName = "AskUserQuestion";
    public const int MaxQuestions = 8;
    public const int MaxOptionsPerQuestion = 16;
    public const int MaxQuestionTextChars = 4096;
    public const int MaxOptionLabelChars = 1024;
    public const int MaxOtherTextChars = 8192;

    public static ElicitationQuestions? TryParse(string? toolInputJson) {
        if (string.IsNullOrEmpty(toolInputJson)) return null;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(toolInputJson); } catch (JsonException) { return null; }
        using (doc) {
            var root = doc.RootElement;
            if (!root.IsObject || root.Prop("questions") is not { } arr || !arr.IsArray) return null;
            var count = arr.GetArrayLength();
            if (count is 0 or > MaxQuestions) return null;

            var parsed = ImmutableArray.CreateBuilder<ElicitationQuestion>(count);
            var texts = new HashSet<string>(StringComparer.Ordinal);
            foreach (var q in arr.EnumerateArray()) {
                if (!q.IsObject) return null;
                var text = ProtocolString(q, "question", MaxQuestionTextChars);
                if (text is null || !texts.Add(text)) return null;
                if (!TryReadFlag(q, out var multi)) return null;
                if (!TryReadOptions(q, out var options)) return null;
                parsed.Add(new ElicitationQuestion(text, DisplayString(q, "header"), multi, options));
            }
            return new ElicitationQuestions(arr.Clone(), parsed.MoveToImmutable());
        }
    }

    // Protocol field: must be a non-blank string within the cap; anything else is null → fatal.
    static string? ProtocolString(JsonElement obj, string name, int maxChars) {
        if (obj.Prop(name) is null) return null;
        var s = obj.Str(name)?.Trim();
        return s is { Length: > 0 } && s.Length <= maxChars ? s : null;
    }

    // Display field: a non-blank string is used; wrong type or whitespace reads as absent.
    static string? DisplayString(JsonElement obj, string name) {
        var s = obj.Str(name)?.Trim();
        return s is { Length: > 0 } ? s : null;
    }

    // Both spellings accepted; a present non-boolean or a disagreement is fatal — the flag
    // decides string-versus-array in the answer, so guessing changes the answer type.
    static bool TryReadFlag(JsonElement q, out bool multi) {
        multi = false;
        bool? camel = null, snake = null;
        if (q.Prop("multiSelect") is not null && (camel = q.Bool("multiSelect")) is null) return false;
        if (q.Prop("multi_select") is not null && (snake = q.Bool("multi_select")) is null) return false;
        if (camel is not null && snake is not null && camel != snake) return false;
        multi = camel ?? snake ?? false;
        return true;
    }

    static bool TryReadOptions(JsonElement q, out ImmutableArray<ElicitationOption> options) {
        options = ImmutableArray<ElicitationOption>.Empty;
        if (q.Prop("options") is not { } el) return true;
        if (!el.IsArray || el.GetArrayLength() > MaxOptionsPerQuestion) return false;

        var builder = ImmutableArray.CreateBuilder<ElicitationOption>();
        var labels = new HashSet<string>(StringComparer.Ordinal);
        foreach (var o in el.EnumerateArray()) {
            if (!o.IsObject) return false;
            var label = ProtocolString(o, "label", MaxOptionLabelChars);
            if (label is null) return false;
            // Duplicate labels collapse to the first: the label IS the answer value.
            if (labels.Add(label)) builder.Add(new ElicitationOption(label, DisplayString(o, "description")));
        }
        options = builder.ToImmutable();
        return true;
    }
}
```

- [ ] **Step 5: Run to verify pass**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/ClaudeElicitationTests/*"`
Expected: all PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Capacitor.Cli.Core/ClaudeElicitation.cs test/Capacitor.Cli.Core.Tests.Unit/ClaudeElicitationTests.cs
git commit -m "Parse AskUserQuestion payloads into an immutable capped model"
```

---

### Task 2: Core compose — `ComposeAnswers`, the bound proof, and ownership

**Files:**
- Modify: `src/Capacitor.Cli.Core/ClaudeElicitation.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/ClaudeElicitationTests.cs` (extend); crib the codec harness from `test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/FrameCodecPermissionTests.cs`

**Interfaces:**
- Consumes: Task 1's model; `FrameCodec`, `LocalFrame.PermissionJson`, `FrameType.PermissionResolve`, `PermissionResolveDto` + `PermissionIpcJsonContext` (`src/Capacitor.Cli.Core/LocalIpc/`).
- Produces: `public sealed record ElicitationAnswer(string Question, IReadOnlyList<string> SelectedLabels, string? OtherText);` and `public static JsonElement ComposeAnswers(ElicitationQuestions questions, IReadOnlyList<ElicitationAnswer> answers)` on `ClaudeElicitation` — throws `ArgumentException` on any invalid answer set.

- [ ] **Step 1: Write the failing compose tests**

Append to `ClaudeElicitationTests.cs`:

```csharp
    static ElicitationQuestions Parsed(string json) => ClaudeElicitation.TryParse(json)!;

    const string MixedPayload =
        """{"questions":[{"question":"Q1","options":[{"label":"A"},{"label":"B"}]},{"question":"Q2","multiSelect":true,"options":[{"label":"X"},{"label":"Y"},{"label":"Z"}]}]}""";

    [Test]
    public async Task Composes_the_documented_shape_with_passthrough_and_ordered_values() {
        var q = Parsed(MixedPayload);
        var composed = ClaudeElicitation.ComposeAnswers(q, [
            new ElicitationAnswer("Q1", ["B"], null),
            new ElicitationAnswer("Q2", ["Z", "X"], "custom"),
        ]);
        await Assert.That(composed.Prop("questions")!.Value.GetRawText()).IsEqualTo(q.QuestionsJson.GetRawText());
        var answers = composed.Prop("answers")!.Value;
        await Assert.That(answers.Str("Q1")).IsEqualTo("B");
        // Multi-select: option order first, the genuine Other text last. Ordering.Matching —
        // the default equivalence ignores order and would let the ordering rule regress.
        await Assert.That(answers.Prop("Q2")!.Value.EnumerateArray().Select(v => v.GetString()))
            .IsEquivalentTo(["X", "Z", "custom"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Single_select_accepts_other_text_as_the_one_value() {
        var q = Parsed("""{"questions":[{"question":"Q","options":[{"label":"A"}]}]}""");
        var composed = ClaudeElicitation.ComposeAnswers(q, [new ElicitationAnswer("Q", [], "  my own  ")]);
        await Assert.That(composed.Prop("answers")!.Value.Str("Q")).IsEqualTo("my own");
    }

    [Test]
    public async Task Other_text_equal_to_a_label_normalizes_into_the_selection() {
        var q = Parsed("""{"questions":[{"question":"Q","multiSelect":true,"options":[{"label":"A"},{"label":"B"}]}]}""");
        var fresh = ClaudeElicitation.ComposeAnswers(q, [new ElicitationAnswer("Q", ["B"], "A")]);
        await Assert.That(fresh.Prop("answers")!.Value.Prop("Q")!.Value.EnumerateArray().Select(v => v.GetString()))
            .IsEquivalentTo(["A", "B"], CollectionOrdering.Matching);
        var already = ClaudeElicitation.ComposeAnswers(q, [new ElicitationAnswer("Q", ["A"], "A")]);
        await Assert.That(already.Prop("answers")!.Value.Prop("Q")!.Value.EnumerateArray().Select(v => v.GetString()))
            .IsEquivalentTo(["A"]);
        // Single-select: the normalized pair collapses to one value instead of throwing.
        var single = Parsed("""{"questions":[{"question":"S","options":[{"label":"A"}]}]}""");
        var collapsed = ClaudeElicitation.ComposeAnswers(single, [new ElicitationAnswer("S", ["A"], "A")]);
        await Assert.That(collapsed.Prop("answers")!.Value.Str("S")).IsEqualTo("A");
    }

    [Test]
    public async Task Invalid_answer_sets_throw() {
        var q = Parsed(MixedPayload);
        List<IReadOnlyList<ElicitationAnswer>> bad = [
            [new ElicitationAnswer("Q1", ["A"], null)],                                                 // missing Q2
            [new ElicitationAnswer("Q1", ["A"], null), new ElicitationAnswer("Q1", ["B"], null)],       // duplicate key
            [new ElicitationAnswer("Q1", ["A"], null), new ElicitationAnswer("Nope", ["X"], null)],     // unknown key
            [new ElicitationAnswer("Q1", ["C"], null), new ElicitationAnswer("Q2", ["X"], null)],       // label not an option
            [new ElicitationAnswer("Q1", ["A"], null), new ElicitationAnswer("Q2", ["X", "X"], null)],  // label twice
            [new ElicitationAnswer("Q1", ["A", "B"], null), new ElicitationAnswer("Q2", ["X"], null)],  // two for single-select
            [new ElicitationAnswer("Q1", ["A"], "extra"), new ElicitationAnswer("Q2", ["X"], null)],    // label AND other for single-select
            [new ElicitationAnswer("Q1", [], null), new ElicitationAnswer("Q2", ["X"], null)],          // neither for single-select
            [new ElicitationAnswer("Q1", ["A"], null), new ElicitationAnswer("Q2", [], null)],          // empty multi-select
            [new ElicitationAnswer("Q1", ["A"], null), new ElicitationAnswer("Q2", [], "   ")],         // blank other
            [new ElicitationAnswer("Q1", ["A"], null),
             new ElicitationAnswer("Q2", [], new string('x', ClaudeElicitation.MaxOtherTextChars + 1))], // over-cap other
        ];
        foreach (var answers in bad)
            await Assert.That(() => ClaudeElicitation.ComposeAnswers(q, answers)).Throws<ArgumentException>();
    }

    [Test]
    public async Task Composed_element_outlives_every_composer_local_owner() {
        var composed = ClaudeElicitation.ComposeAnswers(
            Parsed("""{"questions":[{"question":"Q","options":[{"label":"A"}]}]}"""),
            [new ElicitationAnswer("Q", ["A"], null)]);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        await Assert.That(composed.GetRawText()).Contains("\"answers\"");
    }
```

And the codec bound test — worst-case content is a character that JSON-escapes to `\uXXXX` (use `'\u0001'`) so every capped char costs 6 encoded bytes:

```csharp
    [Test]
    public async Task Maximal_composed_payload_fits_the_frame_codec() {
        var esc = new string('\u0001', 1);
        string Chars(int n) => string.Concat(Enumerable.Repeat(esc, n));
        var questions = string.Join(",", Enumerable.Range(0, ClaudeElicitation.MaxQuestions).Select(i => {
            var text = System.Text.Json.JsonEncodedText.Encode(Chars(ClaudeElicitation.MaxQuestionTextChars - 2) + i.ToString("00"));
            var options = string.Join(",", Enumerable.Range(0, ClaudeElicitation.MaxOptionsPerQuestion).Select(j => {
                var label = System.Text.Json.JsonEncodedText.Encode(Chars(ClaudeElicitation.MaxOptionLabelChars - 2) + j.ToString("00"));
                return $$"""{"label":"{{label}}"}""";
            }));
            return $$"""{"question":"{{text}}","multiSelect":true,"options":[{{options}}]}""";
        }));
        var parsed = ClaudeElicitation.TryParse($$"""{"questions":[{{questions}}]}""");
        await Assert.That(parsed).IsNotNull();

        var answers = parsed!.Questions
            .Select(q => new ElicitationAnswer(q.Question, q.Options.Select(o => o.Label).ToList(),
                Chars(ClaudeElicitation.MaxOtherTextChars)))
            .ToList();
        var updated = ClaudeElicitation.ComposeAnswers(parsed, answers);

        var dto = new PermissionResolveDto("r", "allow", null, updated);
        var json = System.Text.Json.JsonSerializer.Serialize(dto, PermissionIpcJsonContext.Default.PermissionResolveDto);
        var frame = LocalFrame.PermissionJson(FrameType.PermissionResolve, json);
        using var stream = new MemoryStream();
        await FrameCodec.WriteAsync(stream, frame, CancellationToken.None);
        stream.Position = 0;
        var read = await FrameCodec.ReadAsync(stream, CancellationToken.None);
        await Assert.That(read!.Value.Type).IsEqualTo(FrameType.PermissionResolve);
    }
```

Add `using Capacitor.Cli.Core.LocalIpc;` and `using TUnit.Assertions.Enums;` (for `CollectionOrdering`) to the test file. If `FrameCodec.WriteAsync`/`ReadAsync` signatures differ, mirror the exact call shape used in `LocalIpc/FrameCodecPermissionTests.cs` — the assertion that matters is a successful round-trip of the maximal frame.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/ClaudeElicitationTests/*"`
Expected: build error — `ElicitationAnswer`/`ComposeAnswers` do not exist.

- [ ] **Step 3: Implement the composer**

Append to `ClaudeElicitation.cs` (add `using System.Text.Json.Nodes;`):

```csharp
public sealed record ElicitationAnswer(string Question, IReadOnlyList<string> SelectedLabels, string? OtherText);
```

and inside `ClaudeElicitation`:

```csharp
    /// Validates against the parsed questions — every question answered, labels from the parsed
    /// options only, Other text bounded — and builds the answer updatedInput. ArgumentException
    /// here is a programming error in the caller, never a user state.
    public static JsonElement ComposeAnswers(ElicitationQuestions questions, IReadOnlyList<ElicitationAnswer> answers) {
        ArgumentNullException.ThrowIfNull(questions);
        ArgumentNullException.ThrowIfNull(answers);
        if (answers.Count != questions.Questions.Length)
            throw new ArgumentException($"expected {questions.Questions.Length} answer(s), got {answers.Count}", nameof(answers));

        var byQuestion = new Dictionary<string, ElicitationAnswer>(StringComparer.Ordinal);
        foreach (var answer in answers)
            if (!byQuestion.TryAdd(answer.Question, answer))
                throw new ArgumentException($"duplicate answer for \"{answer.Question}\"", nameof(answers));

        var answersObj = new JsonObject();
        foreach (var question in questions.Questions) {
            if (!byQuestion.TryGetValue(question.Question, out var answer))
                throw new ArgumentException($"missing answer for \"{question.Question}\"", nameof(answers));
            answersObj[question.Question] = ComposeValue(question, answer);
        }

        var payload = new JsonObject {
            ["questions"] = JsonNode.Parse(questions.QuestionsJson.GetRawText()),
            ["answers"] = answersObj,
        };
        using var doc = JsonDocument.Parse(payload.ToJsonString());
        return doc.RootElement.Clone();
    }

    static JsonNode ComposeValue(ElicitationQuestion question, ElicitationAnswer answer) {
        var selected = new List<string>();
        foreach (var label in answer.SelectedLabels) {
            if (!question.Options.Any(o => string.Equals(o.Label, label, StringComparison.Ordinal)))
                throw new ArgumentException($"\"{label}\" is not an option of \"{question.Question}\"", nameof(answer));
            if (selected.Contains(label))
                throw new ArgumentException($"\"{label}\" selected twice for \"{question.Question}\"", nameof(answer));
            selected.Add(label);
        }

        var other = answer.OtherText?.Trim();
        if (other is not null) {
            if (other.Length == 0)
                throw new ArgumentException($"blank Other text for \"{question.Question}\"", nameof(answer));
            if (other.Length > MaxOtherTextChars)
                throw new ArgumentException($"Other text over {MaxOtherTextChars} chars for \"{question.Question}\"", nameof(answer));
            // An option label typed as Other IS that option — never a duplicate wire value.
            if (question.Options.Any(o => string.Equals(o.Label, other, StringComparison.Ordinal))) {
                if (!selected.Contains(other)) selected.Add(other);
                other = null;
            }
        }

        if (!question.MultiSelect) {
            if (selected.Count + (other is null ? 0 : 1) != 1)
                throw new ArgumentException($"single-select \"{question.Question}\" needs exactly one value", nameof(answer));
            return JsonValue.Create(other ?? selected[0]);
        }

        if (selected.Count == 0 && other is null)
            throw new ArgumentException($"multi-select \"{question.Question}\" needs at least one value", nameof(answer));

        var array = new JsonArray();
        foreach (var option in question.Options)
            if (selected.Contains(option.Label)) array.Add((JsonNode)JsonValue.Create(option.Label));
        if (other is not null) array.Add((JsonNode)JsonValue.Create(other));
        return array;
    }
```

- [ ] **Step 4: Run to verify pass, then the whole Core suite**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/ClaudeElicitationTests/*"`
Expected: PASS. Then run the full project (no filter) — expected green.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/ClaudeElicitation.cs test/Capacitor.Cli.Core.Tests.Unit/ClaudeElicitationTests.cs
git commit -m "Compose validated AskUserQuestion answers into updatedInput"
```

---

### Task 3: Service — classification, `AnswerAsync`, `Summary`

**Files:**
- Modify: `src/Capacitor.App/Services/IPermissionService.cs`
- Modify: `src/Capacitor.App/Services/PermissionService.cs`
- Modify: `test/Capacitor.App.Tests.Unit/FakePermissionService.cs`
- Test: `test/Capacitor.App.Tests.Unit/PermissionServiceTests.cs` (extend)

**Interfaces:**
- Consumes: Task 1/2 (`ClaudeElicitation`, `ElicitationQuestions`, `ElicitationAnswer`).
- Produces:
  - `PendingPermissionRequest.Questions` → `ElicitationQuestions?`.
  - `public readonly record struct PendingSummary(int Permissions, int Questions) { public int Total => Permissions + Questions; }` (in `IPermissionService.cs`).
  - On `IPermissionService`: `IObservable<PendingSummary> Summary { get; }` and `Task<PermissionResolveOutcome> AnswerAsync(PendingPermissionRequest target, IReadOnlyList<ElicitationAnswer> answers, CancellationToken ct);`.

- [ ] **Step 1: Write the failing service tests**

Append to `PermissionServiceTests.cs`. Extend the local `Dto` helper with tool name/input parameters:

```csharp
    static PermissionPendingDto Dto2(string id, string agent, string vendor, string toolName, string? toolInputJson, bool omitted = false) {
        System.Text.Json.JsonElement? input = null;
        if (toolInputJson is not null) { using var d = System.Text.Json.JsonDocument.Parse(toolInputJson); input = d.RootElement.Clone(); }
        return new PermissionPendingDto(id, agent, "s1", vendor, toolName, input, null, omitted, false, "2026-08-28T10:00:00.0000000+00:00");
    }

    const string QuestionInput = """{"questions":[{"question":"Pick","options":[{"label":"A"},{"label":"B"}]}]}""";

    [Test]
    public async Task Classification_requires_claude_the_tool_name_present_input_and_a_parse() {
        using var h = new Harness();
        await h.StartAsync();
        var yes = await h.EmitAsync(Dto2("q1", "a1", "claude", ClaudeElicitation.ToolName, QuestionInput));
        await Assert.That(yes.Questions).IsNotNull();
        var codex = await h.EmitAsync(Dto2("q2", "a1", "codex", ClaudeElicitation.ToolName, QuestionInput));
        var wrongTool = await h.EmitAsync(Dto2("q3", "a1", "claude", "Bash", QuestionInput));
        var omitted = await h.EmitAsync(Dto2("q4", "a1", "claude", ClaudeElicitation.ToolName, QuestionInput, omitted: true));
        var nullInput = await h.EmitAsync(Dto2("q5", "a1", "claude", ClaudeElicitation.ToolName, null));
        var unparseable = await h.EmitAsync(Dto2("q6", "a1", "claude", ClaudeElicitation.ToolName, """{"questions":[]}"""));
        foreach (var entry in new[] { codex, wrongTool, omitted, nullInput, unparseable })
            await Assert.That(entry.Questions).IsNull();
    }

    [Test]
    public async Task Answer_sends_allow_with_updated_input_and_concludes_on_either_ack() {
        using var h = new Harness();
        await h.StartAsync();
        var entry = await h.EmitAsync(Dto2("q1", "a1", "claude", ClaudeElicitation.ToolName, QuestionInput));

        h.Ops.QueuePermissionResolve(true);
        var applied = await h.Service.AnswerAsync(entry, [new ElicitationAnswer("Pick", ["B"], null)], CancellationToken.None);
        await Assert.That(applied.Kind).IsEqualTo(PermissionResolveKind.Applied);
        var payload = h.Ops.PermissionResolvePayloads[0];
        await Assert.That(payload.Decision).IsEqualTo("allow");
        await Assert.That(payload.ApplyPermissions).IsNull();
        await Assert.That(payload.UpdatedInput!.Value.Prop("answers")!.Value.Str("Pick")).IsEqualTo("B");
        await Assert.That(h.View.Count).IsEqualTo(0);

        var second = await h.EmitAsync(Dto2("q2", "a1", "claude", ClaudeElicitation.ToolName, QuestionInput));
        h.Ops.QueuePermissionResolve(false, "no pending permission request with that id");
        var already = await h.Service.AnswerAsync(second, [new ElicitationAnswer("Pick", ["A"], null)], CancellationToken.None);
        await Assert.That(already.Kind).IsEqualTo(PermissionResolveKind.AlreadyDecided);
        await Assert.That(h.View.Count).IsEqualTo(0);

        var third = await h.EmitAsync(Dto2("q3", "a1", "claude", ClaudeElicitation.ToolName, QuestionInput));
        h.Ops.QueuePermissionResolveFailure("daemon_unreachable");
        var failed = await h.Service.AnswerAsync(third, [new ElicitationAnswer("Pick", ["A"], null)], CancellationToken.None);
        await Assert.That(failed.Kind).IsEqualTo(PermissionResolveKind.TransportFailure);
        await Assert.That(h.View.Count).IsEqualTo(1);

        // A Resolved push after the failed send clears the survivor; a ghost replay stays dropped.
        h.Stream.EmitResolved("q3", "server");
        await WaitUntilAsync(() => h.View.Count == 0, what: "push cleared the survivor");
    }

    [Test]
    public async Task Answer_rejects_an_unclassified_target_and_a_bad_answer_set_without_sending() {
        using var h = new Harness();
        await h.StartAsync();
        var plain = await h.EmitAsync(Dto2("p1", "a1", "claude", "Bash", """{"command":"ls"}"""));
        await Assert.That(() => h.Service.AnswerAsync(plain, [new ElicitationAnswer("Pick", ["A"], null)], CancellationToken.None))
            .Throws<ArgumentException>();

        var entry = await h.EmitAsync(Dto2("q1", "a1", "claude", ClaudeElicitation.ToolName, QuestionInput));
        await Assert.That(() => h.Service.AnswerAsync(entry, [], CancellationToken.None)).Throws<ArgumentException>();
        await Assert.That(h.Ops.PermissionResolveCalls).IsEqualTo(0);
        await Assert.That(h.View.Count).IsEqualTo(2);
    }

    [Test]
    public async Task A_resolved_push_landing_before_the_ack_ends_in_the_same_state() {
        using var h = new Harness();
        await h.StartAsync();
        var entry = await h.EmitAsync(Dto2("q1", "a1", "claude", ClaudeElicitation.ToolName, QuestionInput));

        var gate = h.Ops.ArmPermissionResolve();
        var run = h.Service.AnswerAsync(entry, [new ElicitationAnswer("Pick", ["A"], null)], CancellationToken.None);
        h.Stream.EmitResolved("q1", "server");
        await WaitUntilAsync(() => h.View.Count == 0, what: "push evicted while the ack is in flight");
        gate.SetResult(new PermissionAckDto(false, "no pending permission request with that id"));
        var outcome = await run;
        await Assert.That(outcome.Kind).IsEqualTo(PermissionResolveKind.AlreadyDecided);
        await Assert.That(h.View.Count).IsEqualTo(0);

        // The tombstoned id stays dead against a ghost replay.
        h.Stream.EmitPending(Dto2("q1", "a1", "claude", ClaudeElicitation.ToolName, QuestionInput));
        await Task.Delay(50);
        await Assert.That(h.View.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Summary_seeds_and_stays_a_consistent_pair() {
        using var h = new Harness();
        var summaries = new List<PendingSummary>();
        using var sub = h.Service.Summary.Subscribe(summaries.Add);
        await Assert.That(summaries[0]).IsEqualTo(new PendingSummary(0, 0));

        await h.StartAsync();
        await h.EmitAsync(Dto2("p1", "a1", "claude", "Bash", """{"command":"ls"}"""));
        await h.EmitAsync(Dto2("q1", "a1", "claude", ClaudeElicitation.ToolName, QuestionInput));
        await WaitUntilAsync(() => summaries[^1] == new PendingSummary(1, 1), what: "one of each");

        h.Stream.EmitResolved("q1", "server");
        await WaitUntilAsync(() => summaries[^1] == new PendingSummary(1, 0), what: "question settled");
        foreach (var s in summaries) {
            await Assert.That(s.Permissions).IsGreaterThanOrEqualTo(0);
            await Assert.That(s.Questions).IsGreaterThanOrEqualTo(0);
        }
    }
```

Add `using Capacitor.Cli.Core;` to the test file's usings.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/PermissionServiceTests/*"`
Expected: build error — `Questions`/`AnswerAsync`/`Summary`/`PendingSummary` missing.

- [ ] **Step 3: Implement**

`IPermissionService.cs` — add `using Capacitor.Cli.Core;`; in `PendingPermissionRequest`'s ctor, after `RequestedAt`:

```csharp
        Questions = dto.Vendor == "claude" && dto.ToolName == ClaudeElicitation.ToolName && !dto.ToolInputOmitted
            ? ClaudeElicitation.TryParse(ToolInputJson)
            : null;
```

with property `public ElicitationQuestions? Questions { get; }`. Below the class:

```csharp
public readonly record struct PendingSummary(int Permissions, int Questions) {
    public int Total => Permissions + Questions;
}
```

On the interface:

```csharp
    /// One consistent pair per emission, from a single cache snapshot; replays on subscribe.
    IObservable<PendingSummary> Summary { get; }
    /// Answers a classified AskUserQuestion entry (Questions non-null; ArgumentException otherwise,
    /// as for an invalid answer set — both thrown before anything reaches the wire).
    Task<PermissionResolveOutcome> AnswerAsync(PendingPermissionRequest target, IReadOnlyList<ElicitationAnswer> answers, CancellationToken ct);
```

`PermissionService.cs` — extract `ResolveAsync`'s tail and add the new members:

```csharp
    public IObservable<PendingSummary> Summary =>
        _cache.Connect()
            .QueryWhenChanged(q => Summarize(q.Items))
            .StartWith(Summarize(_cache.Items));

    static PendingSummary Summarize(IEnumerable<PendingPermissionRequest> items) {
        int permissions = 0, questions = 0;
        foreach (var item in items) { if (item.Questions is null) permissions++; else questions++; }
        return new PendingSummary(permissions, questions);
    }

    public async Task<PermissionResolveOutcome> ResolveAsync(PendingPermissionRequest target, PermissionAnswer answer, CancellationToken ct) {
        var decision = answer == PermissionAnswer.Deny ? "deny" : "allow";
        var apply = answer == PermissionAnswer.AllowAlways ? ClaudePermissions.AlwaysAllow(target.ToolName) : (System.Text.Json.JsonElement?)null;
        return await SendResolveAsync(new PermissionResolveDto(target.RequestId, decision, apply, null), ct).ConfigureAwait(false);
    }

    public async Task<PermissionResolveOutcome> AnswerAsync(PendingPermissionRequest target, IReadOnlyList<ElicitationAnswer> answers, CancellationToken ct) {
        if (target.Questions is null) throw new ArgumentException("not an elicitation entry", nameof(target));
        var updated = ClaudeElicitation.ComposeAnswers(target.Questions, answers);
        return await SendResolveAsync(new PermissionResolveDto(target.RequestId, "allow", null, updated), ct).ConfigureAwait(false);
    }

    async Task<PermissionResolveOutcome> SendResolveAsync(PermissionResolveDto dto, CancellationToken ct) {
        PermissionAckDto ack;
        try {
            ack = await _ops.ResolvePermissionAsync(dto, ct).ConfigureAwait(false);
        } catch (LocalControlOpsException ex) {
            return new PermissionResolveOutcome(PermissionResolveKind.TransportFailure, ex.Reason);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            Console.Error.WriteLine($"kcap: permission resolve failed unexpectedly: {ex.Message}");
            return new PermissionResolveOutcome(PermissionResolveKind.TransportFailure, ex.Message);
        }

        Conclude(dto.RequestId);
        return new PermissionResolveOutcome(ack.Ok ? PermissionResolveKind.Applied : PermissionResolveKind.AlreadyDecided, ack.Error);
    }
```

`FakePermissionService.cs` — add matching members:

```csharp
    public readonly List<(string RequestId, IReadOnlyList<ElicitationAnswer> Answers)> Answered = [];

    public IObservable<PendingSummary> Summary =>
        Cache.Connect()
            .QueryWhenChanged(q => Summarize(q.Items))
            .StartWith(new PendingSummary(0, 0));

    static PendingSummary Summarize(IEnumerable<PendingPermissionRequest> items) {
        int permissions = 0, questions = 0;
        foreach (var item in items) { if (item.Questions is null) permissions++; else questions++; }
        return new PendingSummary(permissions, questions);
    }

    public async Task<PermissionResolveOutcome> AnswerAsync(PendingPermissionRequest target, IReadOnlyList<ElicitationAnswer> answers, CancellationToken ct) {
        if (target.Questions is null) throw new ArgumentException("not an elicitation entry", nameof(target));
        Answered.Add((target.RequestId, answers));
        if (_outcomes.Count == 0) throw new InvalidOperationException("FakePermissionService: unscripted answer call");
        var outcome = await _outcomes.Dequeue().Task;
        if (outcome.Kind != PermissionResolveKind.TransportFailure) Cache.Remove(target.RequestId);
        return outcome;
    }
```

Add `using Capacitor.Cli.Core;` where missing, and give `PermissionEntries.Entry` an optional `toolName`-driven question entry helper:

```csharp
    public static PendingPermissionRequest Question(
            string requestId = "q1", string agentId = "a1",
            string toolInputJson = """{"questions":[{"question":"Pick","options":[{"label":"A"},{"label":"B"}]}]}""",
            string requestedAt = "2026-08-28T10:00:00.0000000+00:00") =>
        Entry(requestId, agentId, "claude", ClaudeElicitation.ToolName, toolInputJson, false, requestedAt);
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/PermissionServiceTests/*"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.App/Services/IPermissionService.cs src/Capacitor.App/Services/PermissionService.cs test/Capacitor.App.Tests.Unit/FakePermissionService.cs test/Capacitor.App.Tests.Unit/PermissionServiceTests.cs
git commit -m "Classify question entries and answer them over the permission resolve"
```

---

### Task 4: Card base class and the fallback card's Allow-always rule

**Files:**
- Create: `src/Capacitor.App/ViewModels/PendingCardViewModel.cs`
- Modify: `src/Capacitor.App/ViewModels/PermissionCardViewModel.cs`
- Test: `test/Capacitor.App.Tests.Unit/PermissionCardViewModelTests.cs` (extend)

**Interfaces:**
- Produces: `public abstract class PendingCardViewModel : ReactiveObject, IDisposable` with `string RequestId`, `internal DateTimeOffset RequestedAt`, `bool IsBusy { get; protected set; }`, `string? ErrorText { get; protected set; }`, `protected BehaviorSubject<bool> Busy`, `protected CompositeDisposable Disposables`, `protected bool IsDisposed`, `public void Dispose()`. Property setters are no-ops after disposal (no notification of any kind). `PermissionCardViewModel : PendingCardViewModel`.

- [ ] **Step 1: Write the failing test**

Append to `PermissionCardViewModelTests.cs`:

```csharp
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Allow_always_hides_for_the_ask_user_question_fallback_card() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var svc = new FakePermissionService();
            // Oversized questions payload: still ToolName AskUserQuestion, but unclassified.
            using var fallback = new PermissionCardViewModel(
                PermissionEntries.Entry(toolName: Capacitor.Cli.Core.ClaudeElicitation.ToolName, toolInputJson: null, omitted: true),
                svc, new System.Reactive.Subjects.BehaviorSubject<string?>(null));
            await Assert.That(fallback.ShowsAllowAlways).IsFalse();
        });
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/PermissionCardViewModelTests/*"`
Expected: the new test FAILS (`ShowsAllowAlways` is true for claude today).

- [ ] **Step 3: Implement the base and rebase the permission card**

Create `src/Capacitor.App/ViewModels/PendingCardViewModel.cs`:

```csharp
using System.Reactive.Disposables;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using ReactiveUI;

namespace Capacitor.App.ViewModels;

/// Shared shell of a NEEDS YOU card. Setters are no-ops after disposal: the pipeline disposes a
/// card the instant its entry leaves the cache, and an in-flight submit continuation must not
/// notify a removed card.
public abstract class PendingCardViewModel : ReactiveObject, IDisposable {
    protected readonly CompositeDisposable Disposables = new();
    // canExecute feed; a BehaviorSubject rather than WhenAnyValue for the reason
    // SessionRailViewModel documents (headless ReactiveUI init ordering).
    protected readonly BehaviorSubject<bool> Busy = new(false);
    bool _isBusy;
    string? _errorText;

    public string RequestId { get; }
    internal DateTimeOffset RequestedAt { get; }
    protected bool IsDisposed { get; private set; }

    public bool IsBusy {
        get => _isBusy;
        protected set {
            if (IsDisposed) return;
            this.RaiseAndSetIfChanged(ref _isBusy, value);
            Busy.OnNext(value);
        }
    }

    public string? ErrorText {
        get => _errorText;
        protected set {
            if (IsDisposed) return;
            this.RaiseAndSetIfChanged(ref _errorText, value);
        }
    }

    protected PendingCardViewModel(PendingPermissionRequest entry) {
        RequestId = entry.RequestId;
        RequestedAt = entry.RequestedAt;
        Disposables.Add(Busy);
    }

    public void Dispose() {
        IsDisposed = true;
        Disposables.Dispose();
    }
}
```

Modify `PermissionCardViewModel` to derive from it: delete its own `RequestId`, `RequestedAt`, `_busy`, `IsBusy`, `ErrorText`, `_disposables` and `Dispose` members; rename internal uses of `_busy`→`Busy` and `_disposables`→`Disposables`; ctor becomes `public PermissionCardViewModel(PendingPermissionRequest entry, IPermissionService permissions, IObservable<string?> root) : base(entry)`. Change the Allow-always rule:

```csharp
        ShowsAllowAlways = entry.Vendor == "claude" && entry.ToolName != ClaudeElicitation.ToolName;
```

with `using Capacitor.Cli.Core;`.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/PermissionCardViewModelTests/*"`
Expected: all PASS (including the pre-existing three).

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.App/ViewModels/PendingCardViewModel.cs src/Capacitor.App/ViewModels/PermissionCardViewModel.cs test/Capacitor.App.Tests.Unit/PermissionCardViewModelTests.cs
git commit -m "Lift the NEEDS YOU card shell into a base and gate Allow always"
```

---

### Task 5: `QuestionCardViewModel`

**Files:**
- Create: `src/Capacitor.App/ViewModels/QuestionCardViewModel.cs`
- Test: `test/Capacitor.App.Tests.Unit/QuestionCardViewModelTests.cs` (create)

**Interfaces:**
- Consumes: Task 3's `AnswerAsync` and Task 4's base.
- Produces:
  - `public sealed class QuestionCardViewModel : PendingCardViewModel` — ctor `(PendingPermissionRequest entry, IPermissionService permissions)`; `IReadOnlyList<QuestionGroupViewModel> Questions`; `bool IsFastPath`; `bool ShowsSubmit`; `ReactiveCommand<Unit, Unit> SubmitCommand`.
  - `public sealed class QuestionGroupViewModel : ReactiveObject` — `string? Header`, `string Text`, `bool MultiSelect`, `IReadOnlyList<QuestionOptionViewModel> Options`, `string OtherText { get; set; }`, `int MaxOtherLength`, `bool IsAnswered`, `ReactiveCommand<Unit, Unit> EnterCommand`, `bool ShowsOtherAnswer`.
  - `public sealed class QuestionOptionViewModel : ReactiveObject` — `string Label`, `string? Description`, `bool IsMulti`, `bool IsSelected { get; set; }`, `ReactiveCommand<Unit, Unit> PickCommand`.

- [ ] **Step 1: Write the failing tests**

Create `test/Capacitor.App.Tests.Unit/QuestionCardViewModelTests.cs`:

```csharp
using System.Reactive.Threading.Tasks;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

public class QuestionCardViewModelTests {
    const string SingleSelect = """{"questions":[{"question":"Pick","header":"Choice","options":[{"label":"A","description":"first"},{"label":"B"}]}]}""";
    const string FreeTextOnly = """{"questions":[{"question":"Say"}]}""";
    const string MultiAndSingle = """{"questions":[{"question":"Pick","options":[{"label":"A"},{"label":"B"}]},{"question":"Tags","multiSelect":true,"options":[{"label":"X"},{"label":"Y"}]}]}""";

    static (FakePermissionService Svc, QuestionCardViewModel Card) Make(string input, string requestId = "q1") {
        var svc = new FakePermissionService();
        var entry = PermissionEntries.Question(requestId, toolInputJson: input);
        svc.Add(entry);
        return (svc, new QuestionCardViewModel(entry, svc));
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Fast_path_submits_on_option_click() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (svc, card) = Make(SingleSelect);
            using (svc) using (card) {
                await Assert.That(card.IsFastPath).IsTrue();
                await Assert.That(card.ShowsSubmit).IsFalse();
                svc.Queue(PermissionResolveKind.Applied);
                await card.Questions[0].Options[1].PickCommand.Execute().ToTask();
                await Assert.That(svc.Answered[0].Answers[0].SelectedLabels).IsEquivalentTo(["B"]);
                await Assert.That(svc.Answered[0].Answers[0].OtherText).IsNull();
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Fast_path_other_text_submits_on_enter_and_shows_the_inline_answer() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (svc, card) = Make(SingleSelect);
            using (svc) using (card) {
                var group = card.Questions[0];
                await Assert.That(group.ShowsOtherAnswer).IsFalse();
                group.OtherText = "my own";
                await Assert.That(group.ShowsOtherAnswer).IsTrue();
                svc.Queue(PermissionResolveKind.Applied);
                await group.EnterCommand.Execute().ToTask();
                await Assert.That(svc.Answered[0].Answers[0].OtherText).IsEqualTo("my own");
                await Assert.That(svc.Answered[0].Answers[0].SelectedLabels.Count).IsEqualTo(0);
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Free_text_only_is_not_the_fast_path_and_whitespace_does_not_answer() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (svc, card) = Make(FreeTextOnly);
            using (svc) using (card) {
                await Assert.That(card.IsFastPath).IsFalse();
                await Assert.That(card.ShowsSubmit).IsTrue();
                card.Questions[0].OtherText = "   ";
                await Assert.That(card.Questions[0].IsAnswered).IsFalse();
                card.Questions[0].OtherText = "hello";
                await Assert.That(card.Questions[0].IsAnswered).IsTrue();
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Submit_gates_on_every_question_and_sends_all_answers() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (svc, card) = Make(MultiAndSingle);
            using (svc) using (card) {
                await Assert.That(await card.SubmitCommand.CanExecute.FirstAsync()).IsFalse();
                card.Questions[0].Options[0].IsSelected = true;
                await Assert.That(await card.SubmitCommand.CanExecute.FirstAsync()).IsFalse();
                card.Questions[1].Options[0].IsSelected = true;
                card.Questions[1].Options[1].IsSelected = true;
                await Assert.That(await card.SubmitCommand.CanExecute.FirstAsync()).IsTrue();

                svc.Queue(PermissionResolveKind.Applied);
                await card.SubmitCommand.Execute().ToTask();
                var answers = svc.Answered[0].Answers;
                await Assert.That(answers[0].SelectedLabels).IsEquivalentTo(["A"]);
                await Assert.That(answers[1].SelectedLabels).IsEquivalentTo(["X", "Y"]);
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Single_select_pick_and_other_text_displace_each_other() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (svc, card) = Make(MultiAndSingle);
            using (svc) using (card) {
                var group = card.Questions[0];
                group.Options[0].IsSelected = true;
                group.OtherText = "custom";
                await Assert.That(group.Options[0].IsSelected).IsFalse();
                await group.Options[1].PickCommand.Execute().ToTask();
                await Assert.That(group.OtherText).IsEqualTo("");
                await Assert.That(group.Options[1].IsSelected).IsTrue();
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Double_activation_sends_once_and_transport_failure_re_enables() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (svc, card) = Make(SingleSelect);
            using (svc) using (card) {
                var gate = svc.Arm();
                var first = card.Questions[0].Options[0].PickCommand.Execute().ToTask();
                await WaitUntilAsync(() => card.IsBusy, what: "busy in flight");
                await card.Questions[0].Options[1].PickCommand.Execute().ToTask();
                gate.SetResult(new PermissionResolveOutcome(PermissionResolveKind.TransportFailure, "daemon_unreachable"));
                await first;
                await Assert.That(svc.Answered.Count).IsEqualTo(1);
                await Assert.That(card.IsBusy).IsFalse();
                await Assert.That(card.ErrorText).IsEqualTo("Daemon unreachable — try again");
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Eviction_mid_flight_leaves_no_error_and_no_post_disposal_notification() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (svc, card) = Make(SingleSelect);
            using (svc) {
                var gate = svc.Arm();
                var run = card.Questions[0].Options[0].PickCommand.Execute().ToTask();
                await WaitUntilAsync(() => card.IsBusy, what: "busy in flight");

                var notified = new List<string>();
                card.PropertyChanged += (_, e) => notified.Add(e.PropertyName ?? "");
                card.Dispose();
                gate.SetResult(new PermissionResolveOutcome(PermissionResolveKind.TransportFailure, "daemon_unreachable"));
                await run;
                await Assert.That(notified).IsEmpty();
                await Assert.That(card.ErrorText).IsNull();
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_throwing_service_clears_busy_and_shows_the_generic_line() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (svc, card) = Make(SingleSelect);
            using (svc) using (card) {
                svc.Arm().SetException(new ArgumentException("composer rejected"));
                await card.Questions[0].Options[0].PickCommand.Execute().ToTask();
                await Assert.That(card.IsBusy).IsFalse();
                await Assert.That(card.ErrorText).IsEqualTo("Something went wrong — try again");
            }
        });
    }
}
```

(`FirstAsync` comes from `System.Reactive.Linq` — add the using if the compiler asks.)

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/QuestionCardViewModelTests/*"`
Expected: build error — `QuestionCardViewModel` missing.

- [ ] **Step 3: Implement**

Create `src/Capacitor.App/ViewModels/QuestionCardViewModel.cs`:

```csharp
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using ReactiveUI;

namespace Capacitor.App.ViewModels;

public sealed class QuestionOptionViewModel : ReactiveObject {
    bool _isSelected;

    public string Label { get; }
    public string? Description { get; }
    public bool IsMulti { get; }
    public ReactiveCommand<Unit, Unit> PickCommand { get; }

    public bool IsSelected {
        get => _isSelected;
        set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }

    internal QuestionOptionViewModel(ElicitationOption option, bool isMulti, Func<QuestionOptionViewModel, Task> pick, IObservable<bool> idle) {
        Label = option.Label;
        Description = option.Description;
        IsMulti = isMulti;
        PickCommand = ReactiveCommand.CreateFromTask(() => pick(this), idle);
    }
}

public sealed class QuestionGroupViewModel : ReactiveObject {
    readonly BehaviorSubject<bool> _answered = new(false);
    string _otherText = "";

    public string? Header { get; }
    public string Text { get; }
    public bool MultiSelect { get; }
    public IReadOnlyList<QuestionOptionViewModel> Options { get; }
    public int MaxOtherLength => ClaudeElicitation.MaxOtherTextChars;
    public ReactiveCommand<Unit, Unit> EnterCommand { get; }
    internal IObservable<bool> Answered => _answered;

    public string OtherText {
        get => _otherText;
        set {
            this.RaiseAndSetIfChanged(ref _otherText, value);
            // Single-select: typing Other displaces a picked option.
            if (!MultiSelect && !string.IsNullOrWhiteSpace(value))
                foreach (var option in Options) option.IsSelected = false;
            Refresh();
        }
    }

    public bool IsAnswered => Options.Any(o => o.IsSelected) || !string.IsNullOrWhiteSpace(OtherText);
    public bool ShowsOtherAnswer => !string.IsNullOrWhiteSpace(OtherText);

    internal QuestionGroupViewModel(ElicitationQuestion question, Func<QuestionOptionViewModel, QuestionGroupViewModel, Task> pick,
            Func<QuestionGroupViewModel, Task> enter, IObservable<bool> idle) {
        Header = question.Header;
        Text = question.Question;
        MultiSelect = question.MultiSelect;
        Options = question.Options.Select(o => new QuestionOptionViewModel(o, question.MultiSelect, opt => pick(opt, this), idle)).ToList();
        foreach (var option in Options)
            option.WhenAnyValue(o => o.IsSelected).Subscribe(_ => Refresh());
        EnterCommand = ReactiveCommand.CreateFromTask(() => enter(this), idle);
    }

    internal void SelectExclusively(QuestionOptionViewModel picked) {
        foreach (var option in Options) option.IsSelected = ReferenceEquals(option, picked);
        if (OtherText.Length != 0) { _otherText = ""; this.RaisePropertyChanged(nameof(OtherText)); }
        Refresh();
    }

    internal ElicitationAnswer ToAnswer() => new(
        Text,
        Options.Where(o => o.IsSelected).Select(o => o.Label).ToList(),
        string.IsNullOrWhiteSpace(OtherText) ? null : OtherText.Trim());

    void Refresh() {
        _answered.OnNext(IsAnswered);
        this.RaisePropertyChanged(nameof(IsAnswered));
        this.RaisePropertyChanged(nameof(ShowsOtherAnswer));
    }
}

/// The NEEDS YOU question card: renders every question of an AskUserQuestion payload and answers
/// them all in one resolve. No Deny and no Allow always by design.
public sealed class QuestionCardViewModel : PendingCardViewModel {
    readonly PendingPermissionRequest _entry;
    readonly IPermissionService _permissions;
    readonly CancellationTokenSource _lifetime = new();

    public IReadOnlyList<QuestionGroupViewModel> Questions { get; }
    public bool IsFastPath { get; }
    public bool ShowsSubmit => !IsFastPath;
    public ReactiveCommand<Unit, Unit> SubmitCommand { get; }

    public QuestionCardViewModel(PendingPermissionRequest entry, IPermissionService permissions) : base(entry) {
        _entry = entry;
        _permissions = permissions;
        var parsed = entry.Questions ?? throw new ArgumentException("not an elicitation entry", nameof(entry));

        var idle = Busy.Select(b => !b);
        Questions = parsed.Questions.Select(q => new QuestionGroupViewModel(q, PickAsync, EnterAsync, idle)).ToList();
        IsFastPath = Questions.Count == 1 && !Questions[0].MultiSelect && Questions[0].Options.Count > 0;

        var allAnswered = Questions.Select(q => q.Answered).CombineLatest(states => states.All(x => x));
        SubmitCommand = ReactiveCommand.CreateFromTask(SubmitAsync, allAnswered.CombineLatest(idle, (a, i) => a && i));

        Disposables.Add(SubmitCommand);
        Disposables.Add(_lifetime);
    }

    Task PickAsync(QuestionOptionViewModel option, QuestionGroupViewModel group) {
        if (group.MultiSelect) { option.IsSelected = !option.IsSelected; return Task.CompletedTask; }
        group.SelectExclusively(option);
        return IsFastPath ? SubmitAsync() : Task.CompletedTask;
    }

    Task EnterAsync(QuestionGroupViewModel group) {
        if (IsFastPath) return group.ShowsOtherAnswer ? SubmitAsync() : Task.CompletedTask;
        return Questions.All(q => q.IsAnswered) ? SubmitAsync() : Task.CompletedTask;
    }

    async Task SubmitAsync() {
        if (IsBusy || IsDisposed) return;
        IsBusy = true;
        ErrorText = null;
        try {
            var answers = Questions.Select(q => q.ToAnswer()).ToList();
            var outcome = await _permissions.AnswerAsync(_entry, answers, _lifetime.Token);
            if (outcome.Kind == PermissionResolveKind.TransportFailure)
                ErrorText = outcome.Error == "daemon_unreachable" ? "Daemon unreachable — try again" : $"Could not answer ({outcome.Error}) — try again";
        } catch (OperationCanceledException) {
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: question submit failed unexpectedly: {ex.Message}");
            ErrorText = "Something went wrong — try again";
        } finally {
            IsBusy = false;
        }
    }
}
```

Notes for the implementer: the base's disposal-guarded setters are what make the `finally` and the error line safe on an evicted card — do not add a second guard, and do not move `IsBusy = true` after an await (single flight depends on it being synchronous). `Dispose` on the base runs before `_lifetime` is disposed via `Disposables`, so cancel it there: add `_lifetime.Cancel()` wrapped in try/catch(ObjectDisposedException) at the START of a `Disposables.Add(Disposable.Create(...))` registration, i.e. register `Disposables.Add(Disposable.Create(() => { try { _lifetime.Cancel(); } catch (ObjectDisposedException) { } }));` BEFORE adding `_lifetime` itself.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/QuestionCardViewModelTests/*"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.App/ViewModels/QuestionCardViewModel.cs test/Capacitor.App.Tests.Unit/QuestionCardViewModelTests.cs
git commit -m "Add the question card view model with fast path and single flight"
```

---

### Task 6: Chat tab — mixed cards in the pipeline and the view

**Files:**
- Modify: `src/Capacitor.App/ViewModels/ChatTabViewModel.cs:128-160` (the permission pipeline)
- Modify: `src/Capacitor.App/Views/ChatTabView.axaml:63-92` (the NEEDS YOU row)
- Test: `test/Capacitor.App.Tests.Unit/ChatTabViewModelTests.cs`, `test/Capacitor.App.Tests.Unit/ChatTabViewSmokeTests.cs` (extend; fix `.Detail` accesses with a `PermissionCardViewModel` cast)

**Interfaces:**
- Consumes: Tasks 3–5.
- Produces: `ChatTabViewModel.PendingPermissions` becomes `ReadOnlyObservableCollection<PendingCardViewModel>`; `HasPendingPermissions` unchanged.

- [ ] **Step 1: Write the failing tests**

In `ChatTabViewModelTests.cs`, add (using its existing `Claude(seed)` harness factory and `PermissionEntries`):

```csharp
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Question_entries_become_question_cards_beside_permission_cards() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = Claude(p => {
                p.Add(PermissionEntries.Entry("r1", requestedAt: "2026-08-28T10:00:00.0000000+00:00"));
                p.Add(PermissionEntries.Question("q1", requestedAt: "2026-08-28T10:00:01.0000000+00:00"));
            });
            await WaitUntilAsync(() => h.Chat.PendingPermissions.Count == 2, what: "both cards");
            await Assert.That(h.Chat.PendingPermissions[0]).IsTypeOf<PermissionCardViewModel>();
            await Assert.That(h.Chat.PendingPermissions[1]).IsTypeOf<QuestionCardViewModel>();

            h.Permissions.Remove("q1");
            await WaitUntilAsync(() => h.Chat.PendingPermissions.Count == 1, what: "question card removed");
            await Assert.That(h.Chat.PendingPermissions[0].RequestId).IsEqualTo("r1");
        });
    }
```

(`PermissionEntries.Question` gained `requestedAt` in Task 3's helper — thread it through as `Entry` does.) In `ChatTabViewSmokeTests.cs`, add:

```csharp
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Question_card_renders_options_other_and_coexists_with_a_permission_card() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            host.Permissions.Add(PermissionEntries.Entry("r1"));
            host.Permissions.Add(PermissionEntries.Question("q1"));
            host.Settle();

            var buttons = host.View.GetVisualDescendants().OfType<Button>().Select(b => b.Content as string).ToList();
            await Assert.That(buttons).Contains("Allow");           // the permission card
            await Assert.That(buttons).Contains("A");               // a question option button
            var otherBoxes = host.View.GetVisualDescendants().OfType<TextBox>()
                .Where(t => t.Watermark == "Other…").ToList();
            await Assert.That(otherBoxes.Count).IsEqualTo(1);
            await Assert.That(host.View.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Pick")).IsTrue();
        });
    }
```

(The exact `RunOnUiAsync`/`Settle` shape: mirror the file's other tests; the assertion targets are what matter.)

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/ChatTabViewModelTests/*"`
Expected: build errors (type mismatch on `IsTypeOf<QuestionCardViewModel>` against `PermissionCardViewModel` collection).

- [ ] **Step 3: Implement the pipeline branch**

In `ChatTabViewModel.cs`, change the pipeline (line ~137):

```csharp
        var cards = permissions.Pending
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Filter(p => p.AgentId == agentId)
            .Transform(p => p.Questions is null
                ? (PendingCardViewModel)new PermissionCardViewModel(p, permissions, _rootSubject)
                : new QuestionCardViewModel(p, permissions))
            .DisposeMany()
            .SortAndBind(out var pendingPermissions, Comparer<PendingCardViewModel>.Create((a, b) => {
                var byTime = a.RequestedAt.CompareTo(b.RequestedAt);
                return byTime != 0 ? byTime : string.CompareOrdinal(a.RequestId, b.RequestId);
            }));
        PendingPermissions = pendingPermissions;
```

and retype the property: `public ReadOnlyObservableCollection<PendingCardViewModel> PendingPermissions { get; }`. Fix the existing test sites that read `.Detail` off the collection (`ChatTabViewModelTests.cs:294-296`): `((PermissionCardViewModel)h.Chat.PendingPermissions[0]).Detail`.

- [ ] **Step 4: Implement the view**

In `ChatTabView.axaml`, replace the NEEDS YOU `ItemsControl` (lines 67-89): keep the existing permission template byte-for-byte but move it from `ItemsControl.ItemTemplate` into `ItemsControl.DataTemplates`, and add the question template beside it:

```xml
                    <ItemsControl ItemsSource="{Binding PendingPermissions}">
                        <ItemsControl.DataTemplates>
                            <DataTemplate x:DataType="vm:PermissionCardViewModel">
                                <!-- existing permission card Border, unchanged -->
                            </DataTemplate>
                            <DataTemplate x:DataType="vm:QuestionCardViewModel">
                                <Border Background="{StaticResource KcapSurfaceRaisedBrush}" BorderBrush="{StaticResource KcapAccentBrush}"
                                        BorderThickness="1" CornerRadius="10" Padding="13,10" Margin="0,0,0,6">
                                    <StackPanel Spacing="8">
                                        <ItemsControl ItemsSource="{Binding Questions}">
                                            <ItemsControl.ItemTemplate>
                                                <DataTemplate x:DataType="vm:QuestionGroupViewModel">
                                                    <StackPanel Spacing="5" Margin="0,0,0,4">
                                                        <TextBlock Text="{Binding Header}" FontSize="10" FontWeight="SemiBold" Foreground="{StaticResource KcapMutedBrush}"
                                                                   IsVisible="{Binding Header, Converter={x:Static StringConverters.IsNotNullOrEmpty}}" />
                                                        <TextBlock Text="{Binding Text}" FontWeight="SemiBold" FontSize="13" TextWrapping="Wrap"
                                                                   Foreground="{StaticResource KcapTextBrush}" />
                                                        <ItemsControl ItemsSource="{Binding Options}">
                                                            <ItemsControl.ItemTemplate>
                                                                <DataTemplate x:DataType="vm:QuestionOptionViewModel">
                                                                    <StackPanel Margin="0,0,0,2">
                                                                        <Button IsVisible="{Binding !IsMulti}" Command="{Binding PickCommand}"
                                                                                HorizontalAlignment="Stretch" HorizontalContentAlignment="Left"
                                                                                Padding="10,5" FontSize="12" CornerRadius="7"
                                                                                Background="{StaticResource KcapSurfaceBrush}" BorderBrush="{StaticResource KcapBorderBrush}" BorderThickness="1">
                                                                            <StackPanel>
                                                                                <TextBlock Text="{Binding Label}" Foreground="{StaticResource KcapTextBrush}" />
                                                                                <TextBlock Text="{Binding Description}" FontSize="11" Foreground="{StaticResource KcapMutedBrush}"
                                                                                           TextWrapping="Wrap"
                                                                                           IsVisible="{Binding Description, Converter={x:Static StringConverters.IsNotNullOrEmpty}}" />
                                                                            </StackPanel>
                                                                        </Button>
                                                                        <CheckBox IsVisible="{Binding IsMulti}" IsChecked="{Binding IsSelected}" FontSize="12">
                                                                            <StackPanel>
                                                                                <TextBlock Text="{Binding Label}" Foreground="{StaticResource KcapTextBrush}" />
                                                                                <TextBlock Text="{Binding Description}" FontSize="11" Foreground="{StaticResource KcapMutedBrush}"
                                                                                           TextWrapping="Wrap"
                                                                                           IsVisible="{Binding Description, Converter={x:Static StringConverters.IsNotNullOrEmpty}}" />
                                                                            </StackPanel>
                                                                        </CheckBox>
                                                                    </StackPanel>
                                                                </DataTemplate>
                                                            </ItemsControl.ItemTemplate>
                                                        </ItemsControl>
                                                        <DockPanel>
                                                            <Button DockPanel.Dock="Right" Content="Answer" Command="{Binding EnterCommand}"
                                                                    IsVisible="{Binding ShowsOtherAnswer}" Margin="6,0,0,0"
                                                                    Padding="10,4" FontSize="12" CornerRadius="7"
                                                                    Background="{StaticResource KcapAccentBrush}" Foreground="#07120E" />
                                                            <TextBox Text="{Binding OtherText}" Watermark="Other…" MaxLength="{Binding MaxOtherLength}"
                                                                     AcceptsReturn="False" FontSize="12" MinHeight="30">
                                                                <TextBox.KeyBindings>
                                                                    <KeyBinding Gesture="Enter" Command="{Binding EnterCommand}" />
                                                                </TextBox.KeyBindings>
                                                            </TextBox>
                                                        </DockPanel>
                                                    </StackPanel>
                                                </DataTemplate>
                                            </ItemsControl.ItemTemplate>
                                        </ItemsControl>
                                        <TextBlock Text="{Binding ErrorText}" FontSize="11" Foreground="{StaticResource KcapDangerBrush}"
                                                   IsVisible="{Binding ErrorText, Converter={x:Static StringConverters.IsNotNullOrEmpty}}" />
                                        <Button Content="Submit" Command="{Binding SubmitCommand}" IsVisible="{Binding ShowsSubmit}"
                                                HorizontalAlignment="Right" Padding="12,4" FontSize="12" FontWeight="SemiBold" CornerRadius="7"
                                                Background="{StaticResource KcapAccentBrush}" Foreground="#07120E" />
                                    </StackPanel>
                                </Border>
                            </DataTemplate>
                        </ItemsControl.DataTemplates>
                    </ItemsControl>
```

Selected state for single-select option buttons: add to the question card's `Border` a `Styles` block scoping a selected look — a `Button` inside the option template gets `Classes.selected="{Binding IsSelected}"` and

```xml
        <Border.Styles>
            <Style Selector="Button.selected">
                <Setter Property="BorderBrush" Value="{StaticResource KcapAccentBrush}" />
            </Style>
        </Border.Styles>
```

- [ ] **Step 5: Run to verify pass**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/ChatTabViewModelTests/*"` then `-- --treenode-filter "/*/*/ChatTabViewSmokeTests/*"`
Expected: PASS, including the pre-existing cases.

- [ ] **Step 6: Commit**

```bash
git add src/Capacitor.App/ViewModels/ChatTabViewModel.cs src/Capacitor.App/Views/ChatTabView.axaml test/Capacitor.App.Tests.Unit/ChatTabViewModelTests.cs test/Capacitor.App.Tests.Unit/ChatTabViewSmokeTests.cs
git commit -m "Render question cards beside permission cards on the NEEDS YOU row"
```

---

### Task 7: Tray — the summary input and split wording

**Files:**
- Modify: `src/Capacitor.App/ViewModels/TrayViewModel.cs:105-108,155-245`
- Test: `test/Capacitor.App.Tests.Unit/TrayViewModelTests.cs` (extend around line 1040)

**Interfaces:**
- Consumes: Task 3's `IPermissionService.Summary` / `PendingSummary`.
- Produces: no public surface change; `Build` and `HeaderText`/`PendingBody` take a `PendingSummary` where they took `int pendingPermissions`.

- [ ] **Step 1: Write the failing tests**

Beside the existing pending-permission tray test (~line 1040), mirroring its harness setup exactly:

```csharp
    [Test]
    public async Task Pending_questions_split_the_header_wording() {
        // Same service/pause/actions/consent setup as Pending_permissions_assert_attention... above.
        using var permissions = new FakePermissionService();
        using var vm = new TrayViewModel(service, pause, actions, consent, permissions: permissions);
        service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, ["consent/1", "permission/1"]));
        // ...emit the same snapshot the sibling test emits...

        permissions.Add(PermissionEntries.Question("q1"));
        await WaitUntilAsync(() => vm.MenuModel.State == TrayState.Attention, what: "attention on a question");
        await Assert.That(vm.MenuModel.Header).IsEqualTo("daemon-a: 1 question waiting");

        permissions.Add(PermissionEntries.Entry("r1"));
        await WaitUntilAsync(() => vm.MenuModel.Header == "daemon-a: 1 question waiting, 1 permission request waiting",
            what: "mixed wording");

        permissions.Remove("q1");
        permissions.Remove("r1");
        await WaitUntilAsync(() => vm.MenuModel.State != TrayState.Attention, what: "cleared");
    }
```

Copy the sibling test's exact `service`/`pause`/`actions`/`consent` construction and snapshot emission — the file's local fixtures own those shapes. Also extend the replay-guard test (the one asserting the `InvalidOperationException` message) if it names `PendingCount`: the message text changes to name `Summary` (Step 3).

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/TrayViewModelTests/*"`
Expected: the new test FAILS (header says "1 permission request waiting" for a question).

- [ ] **Step 3: Implement**

In `TrayViewModel.cs`:

```csharp
        var pendingSummary = permissions?.Summary ?? Observable.Return(default(PendingSummary));
        var projected = service.Status.CombineLatest(snapshots, pause.State, actions.StopsInFlight, consent.PendingCount, attention, pendingSummary,
            (status, snap, pauseState, inFlight, pending, lifecycleMsg, summary) =>
                Build(service.DaemonName, status, snap, pauseState, inFlight, pending, lifecycleMsg, summary));
```

`Build`/`HeaderText`: parameter `int pendingPermissions` becomes `PendingSummary pendingSummary`; the attention predicate uses `pendingSummary.Total > 0`; `PendingBody`:

```csharp
    static string PendingBody(PendingSummary summary, int consent) {
        var parts = new List<string>(3);
        if (summary.Questions > 0) parts.Add($"{summary.Questions} question{(summary.Questions == 1 ? "" : "s")} waiting");
        if (summary.Permissions > 0) parts.Add($"{summary.Permissions} permission request{(summary.Permissions == 1 ? "" : "s")} waiting");
        if (consent > 0) parts.Add($"{consent} launch{(consent == 1 ? "" : "es")} awaiting approval");
        return string.Join(", ", parts);
    }
```

Update the replay-guard message (line ~131) to name `IPermissionService.Summary` instead of `PendingCount`, and trim the surrounding comment blocks it forces you to touch per the CLAUDE.md rewrite-as-you-go rule.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/TrayViewModelTests/*"`
Expected: all PASS, pre-existing permission wording cases included.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.App/ViewModels/TrayViewModel.cs test/Capacitor.App.Tests.Unit/TrayViewModelTests.cs
git commit -m "Split the tray wording for pending questions via one summary input"
```

---

### Task 8: Docs, full suite, AOT gate

**Files:**
- Modify: `docs/CHANGES.md` (new feature section at the position the file's ordering convention dictates)

- [ ] **Step 1: Write the CHANGES.md section**

Follow the file's existing per-feature format (open it and match the heading style). Content to carry — the reasoning, not the diff:

```markdown
## Elicitation question cards for PTY sessions (AI-2361)

A PTY Claude session's `AskUserQuestion` already reaches the app as a pending permission entry
(the `PermissionRequest` hook rides the permission lane end to end), so the desktop renders it
as an answerable question card instead of a broken Allow/Deny card — classification is app-side,
on the entry it already receives, and the answer is the existing resolve frame with `allow` plus
the documented `updatedInput` answers shape. No wire, daemon, or CLI change: the feature works
against any daemon advertising `permission/1`, and AI-2197 defines its own frames later for the
structured ACP vendors. Core's `ClaudeElicitation` owns the contract: a strict, capped,
parser-created immutable model and a composer that validates every answer against it, which is
what bounds the outgoing resolve frame by arithmetic. An unparseable or oversized payload falls
back to the permission card (Allow = let the TUI ask) with "Allow always" hidden for the
question tool.
```

- [ ] **Step 2: Run the full solution test suite**

Run: `dotnet test --solution Capacitor.slnx`
Expected: green, except the 7 unit + 1 integration session-start nudge tests that already fail on main in this environment (see auto-memory) — verify the failure list matches that pre-existing set exactly.

- [ ] **Step 3: AOT publish gate**

Run: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`
Expected: no output. (Core is compiled into the CLI; the `JsonNode`/`JsonValue.Create(string)`/`JsonArray.Add(JsonNode)` calls are the sites this gate watches.)

- [ ] **Step 4: Commit**

```bash
git add docs/CHANGES.md
git commit -m "Record the elicitation question cards feature in CHANGES"
```

---

## PR notes (for the finishing step, not a task)

- Title: `Render AskUserQuestion as an answerable card in the desktop app` (no reference in the title).
- Description: follow `.github/PULL_REQUEST_TEMPLATE.md`'s comment block; the reference line carries `AI-2361` — **ask the user** whether a GitHub issue exists or should be created before adding a `Closes #N`.
- The README is unchanged on purpose: no CLI surface changed (verify nothing in `src/Capacitor.Cli.Core/Resources/help-*.txt` was touched).
