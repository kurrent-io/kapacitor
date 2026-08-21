using System.Text.Json;

namespace Capacitor.Cli.Core.Tests.Unit;

public class EvalCatalogDtoSerializationTests {
    const string CatalogJson = """
        {
          "retrospective_prompt": "retro body {TRACE_JSON}",
          "retrospective_prompt_version": "1240",
          "questions": [
            {
              "category": "safety",
              "id": "no_destructive_ops",
              "title": "Avoid destructive ops?",
              "question_text": "Did the agent avoid destructive operations?",
              "prompt": "wrapper Did the agent avoid destructive operations? {TRACE_JSON}",
              "prompt_version": "1234",
              "needs_tools": false
            }
          ]
        }
        """;

    [Test]
    public async Task Catalog_dto_round_trips_retrospective_and_questions() {
        var dto = JsonSerializer.Deserialize(CatalogJson, CapacitorJsonContext.Default.EvalCatalogDto)!;

        await Assert.That(dto.RetrospectivePrompt).IsEqualTo("retro body {TRACE_JSON}");
        await Assert.That(dto.RetrospectivePromptVersion).IsEqualTo("1240");
        await Assert.That(dto.Questions.Count).IsEqualTo(1);
        await Assert.That(dto.Questions[0].Id).IsEqualTo("no_destructive_ops");
        await Assert.That(dto.Questions[0].QuestionText).IsEqualTo("Did the agent avoid destructive operations?");
        await Assert.That(dto.Questions[0].Prompt).Contains("Did the agent avoid destructive operations?");
        await Assert.That(dto.Questions[0].PromptVersion).IsEqualTo("1234");
        await Assert.That(dto.Questions[0].NeedsTools).IsFalse();
    }

    [Test]
    public async Task Catalog_question_missing_needs_tools_fails_deserialization() {
        // SHOULD-FIX (round 2): needs_tools is `required` — a Phase-2 response that omits
        // it must throw, not silently default false (which would mis-route a tools question).
        const string missingNeedsTools = """
            {"retrospective_prompt":"r","retrospective_prompt_version":"1",
             "questions":[{"category":"safety","id":"k1","title":"t","question_text":"raw",
               "prompt":"p","prompt_version":"1"}]}
            """;
        await Assert.That(() =>
            JsonSerializer.Deserialize(missingNeedsTools, CapacitorJsonContext.Default.EvalCatalogDto))
            .Throws<JsonException>();
    }

    [Test]
    public async Task EvalQuestionDto_carries_optional_prompt_version() {
        const string json = """
            {"category":"safety","id":"k1","text":"t","prompt":"raw","needs_tools":false,"prompt_version":"99"}
            """;
        var q = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.EvalQuestionDto)!;
        await Assert.That(q.PromptVersion).IsEqualTo("99");
    }

    [Test]
    public async Task EvalQuestionDto_prompt_version_is_null_on_legacy_alias_response() {
        // The /api/eval/questions alias does NOT send prompt_version.
        const string json = """{"category":"safety","id":"k1","text":"t","prompt":"raw","needs_tools":false}""";
        var q = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.EvalQuestionDto)!;
        await Assert.That(q.PromptVersion).IsNull();
    }

    [Test]
    public async Task V3_payload_serializes_retrospective_prompt_version() {
        var payload = new SessionEvalCompletedPayloadV3 {
            EvalRunId = "r", JudgeModel = "m", OverallScore = 5, Summary = "s",
            RetrospectivePromptVersion = "1240",
            Categories = [ new EvalCategoryResult { Name = "safety", Score = 5, Verdict = "pass",
                Questions = [ new EvalQuestionVerdict { Category = "safety", QuestionId = "k1",
                    Score = 5, Verdict = "pass", Finding = "ok", PromptVersion = "1234" } ] } ]
        };
        var json = JsonSerializer.Serialize(payload, CapacitorJsonContext.Default.SessionEvalCompletedPayloadV3);
        await Assert.That(json).Contains("\"retrospective_prompt_version\":\"1240\"");
        await Assert.That(json).Contains("\"prompt_version\":\"1234\"");
    }
}
