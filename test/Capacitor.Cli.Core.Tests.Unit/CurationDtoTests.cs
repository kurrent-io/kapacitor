using System.Text.Json;

namespace Capacitor.Cli.Core.Tests.Unit;

public class CurationDtoTests {
    [Test]
    public async Task Deserializes_wrapper_and_snake_case_fields() {
        const string json = """
        {
          "repo_hash": "ab01",
          "items": [
            { "category": "quality", "cluster_id": "c-1",
              "promoted_text": "prefer JsonNode.Parse for AOT",
              "target_kinds": ["claude_md", "injection"], "status": "promoted" }
          ]
        }
        """;

        var dto = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.CurationApplyResponse);

        await Assert.That(dto).IsNotNull();
        await Assert.That(dto!.RepoHash).IsEqualTo("ab01");
        await Assert.That(dto.Items!.Count).IsEqualTo(1);
        await Assert.That(dto.Items[0].PromotedText).IsEqualTo("prefer JsonNode.Parse for AOT");
        await Assert.That(dto.Items[0].TargetKinds!).Contains("claude_md");
    }

    [Test]
    public async Task Ignores_unknown_fields_and_empty_items() {
        const string json = """{ "repo_hash": "x", "items": [], "extra": 1 }""";
        var dto = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.CurationApplyResponse);
        await Assert.That(dto!.Items!.Count).IsEqualTo(0);
    }
}
