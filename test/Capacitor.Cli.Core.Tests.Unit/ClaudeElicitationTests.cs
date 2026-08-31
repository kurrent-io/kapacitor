using System.Collections.Immutable;
using Capacitor.Cli.Core.LocalIpc;
using TUnit.Assertions.Enums;

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
    public async Task Question_text_cap_is_measured_on_the_raw_untrimmed_string() {
        // Cap-length content plus one leading space: trimmed length is under the cap, but the raw
        // (protocol) length is one over — must still fail.
        var padded = " " + new string('q', ClaudeElicitation.MaxQuestionTextChars);
        var overCap = $$"""{"questions":[{"question":"{{padded}}"}]}""";
        await Assert.That(ClaudeElicitation.TryParse(overCap)).IsNull();

        // Exactly the cap INCLUDING padding passes.
        var atCap = " " + new string('q', ClaudeElicitation.MaxQuestionTextChars - 1);
        var underCap = $$"""{"questions":[{"question":"{{atCap}}"}]}""";
        var parsed = ClaudeElicitation.TryParse(underCap);
        await Assert.That(parsed).IsNotNull();
        await Assert.That(parsed!.Questions[0].Question).IsEqualTo(atCap);
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
#pragma warning disable CS0183
        await Assert.That(parsed!.Questions is ImmutableArray<ElicitationQuestion>).IsTrue();
        await Assert.That(parsed.Questions[0].Options is ImmutableArray<ElicitationOption>).IsTrue();
#pragma warning restore CS0183
    }

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
        await Assert.That(answers.Prop("Q2")!.Value.EnumerateArray().Select(v => v.GetString()!))
            .IsEquivalentTo(["X", "Z", "custom"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Preserves_the_questions_bytes_with_non_ascii_content() {
        var q = Parsed("""{"questions":[{"question":"Café — décider?","options":[{"label":"Oui"},{"label":"Non"}]}]}""");
        var composed = ClaudeElicitation.ComposeAnswers(q, [
            new ElicitationAnswer("Café — décider?", ["Oui"], null),
        ]);
        await Assert.That(composed.Prop("questions")!.Value.GetRawText()).IsEqualTo(q.QuestionsJson.GetRawText());
    }

    [Test]
    public async Task Padded_question_and_label_text_round_trips_untrimmed() {
        var q = Parsed("""{"questions":[{"question":"  Pick one  ","options":[{"label":" A "},{"label":"B"}]}]}""");
        await Assert.That(q.Questions[0].Question).IsEqualTo("  Pick one  ");
        await Assert.That(q.Questions[0].Options[0].Label).IsEqualTo(" A ");

        var composed = ClaudeElicitation.ComposeAnswers(q, [new ElicitationAnswer("  Pick one  ", [" A "], null)]);
        var answers = composed.Prop("answers")!.Value;
        await Assert.That(answers.Prop("  Pick one  ")).IsNotNull();
        await Assert.That(answers.Str("  Pick one  ")).IsEqualTo(" A ");
    }

    [Test]
    public async Task Preserves_pre_escaped_sequences_in_questions() {
        var q = Parsed("""{"questions":[{"question":"café","options":[{"label":"A"}]}]}""");
        var composed = ClaudeElicitation.ComposeAnswers(q, [
            new ElicitationAnswer("café", ["A"], null),
        ]);
        await Assert.That(composed.Prop("questions")!.Value.GetRawText()).IsEqualTo(q.QuestionsJson.GetRawText());
    }

    [Test]
    public async Task Preserves_astral_characters_in_questions() {
        var q = Parsed("""{"questions":[{"question":"😀","options":[{"label":"B"}]}]}""");
        var composed = ClaudeElicitation.ComposeAnswers(q, [
            new ElicitationAnswer("😀", ["B"], null),
        ]);
        await Assert.That(composed.Prop("questions")!.Value.GetRawText()).IsEqualTo(q.QuestionsJson.GetRawText());
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
        await Assert.That(fresh.Prop("answers")!.Value.Prop("Q")!.Value.EnumerateArray().Select(v => v.GetString()!))
            .IsEquivalentTo(["A", "B"], CollectionOrdering.Matching);
        var already = ClaudeElicitation.ComposeAnswers(q, [new ElicitationAnswer("Q", ["A"], "A")]);
        await Assert.That(already.Prop("answers")!.Value.Prop("Q")!.Value.EnumerateArray().Select(v => v.GetString()!))
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

    [Test]
    public async Task Maximal_composed_payload_fits_the_frame_codec() {
        var esc = new string('\u0001', 1);
        string Chars(int n) => string.Concat(Enumerable.Repeat(esc, n));
        var questions = string.Join(",", Enumerable.Range(0, ClaudeElicitation.MaxQuestions).Select(i => {
            var text = System.Text.Json.JsonEncodedText.Encode(Chars(ClaudeElicitation.MaxQuestionTextChars - 2) + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture));
            var options = string.Join(",", Enumerable.Range(0, ClaudeElicitation.MaxOptionsPerQuestion).Select(j => {
                var label = System.Text.Json.JsonEncodedText.Encode(Chars(ClaudeElicitation.MaxOptionLabelChars - 2) + j.ToString("00", System.Globalization.CultureInfo.InvariantCulture));
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
        await Assert.That(read!.Type).IsEqualTo(FrameType.PermissionResolve);
    }
}
