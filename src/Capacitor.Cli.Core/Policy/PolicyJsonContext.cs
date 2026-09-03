namespace Capacitor.Cli.Core.Policy;

using System.Text.Json.Serialization;

sealed record PolicySnapshotFileV1(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("engine_version")] string EngineVersion,
    [property: JsonPropertyName("degraded")] bool Degraded,
    [property: JsonPropertyName("degradations")] string[] Degradations,
    [property: JsonPropertyName("documents")] PolicySnapshotFileDocV1[] Documents);

sealed record PolicySnapshotFileDocV1(
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("source_path")] string SourcePath,
    [property: JsonPropertyName("content")] string Content);

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(PolicySnapshotFileV1))]
[JsonSerializable(typeof(PolicyJournalFileV1))]
partial class PolicyJsonContext : JsonSerializerContext;
