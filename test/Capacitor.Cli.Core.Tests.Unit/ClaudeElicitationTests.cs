using System.Collections.Immutable;

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
#pragma warning disable CS0183
        await Assert.That(parsed!.Questions is ImmutableArray<ElicitationQuestion>).IsTrue();
        await Assert.That(parsed.Questions[0].Options is ImmutableArray<ElicitationOption>).IsTrue();
#pragma warning restore CS0183
    }
}
