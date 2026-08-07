using System.Text.Json;

namespace Capacitor.Cli.Core.Acp;

/// <summary>
/// Reads the selectable-model list out of a <c>session/new</c> result, whichever of the two shapes
/// the agent published it in, so <see cref="AcpModelResolver"/> has one input regardless of vendor.
///
/// <para><b>Why two shapes.</b> Cursor, Copilot and Kiro answer with a <c>models</c> object
/// (<see cref="SessionModelsInfo"/>). OpenCode answers with a <c>configOptions</c> array
/// (<see cref="SessionConfigOptionDto"/>) and NO <c>models</c> object — measured against
/// <c>opencode acp</c> 1.18.9, <c>docs/probes/2026-08-07-opencode-acp/</c>. Before this existed the
/// resolver looked only at <c>models</c>, so OpenCode resolution returned null on every request and
/// a caller-requested model was silently discarded — a hosted OpenCode agent always ran the vendor
/// default no matter what the launch asked for, with nothing in the logs naming the reason.</para>
///
/// <para><b>Precedence is <c>models</c> first, deliberately.</b> It is the standardized shape, and a
/// vendor that grew a <c>configOptions</c> mirror of the same fact must not have the mirror
/// reinterpreted as the authority — the two could disagree, and the shape the wire selector was
/// designed against should win. A vendor publishing neither yields an empty list, which
/// <see cref="AcpModelResolver.Resolve"/> already treats as "use the vendor's own default".</para>
///
/// <para>Extraction is deliberately total: a malformed element is skipped rather than throwing,
/// because model selection is a nice-to-have and never a launch precondition. The one thing this
/// must not do is throw <see cref="JsonException"/> past its caller's narrow catch.</para>
/// </summary>
public static class AcpSessionModelList {
    /// <summary>Matched against <c>configOptions[].id</c>. The same literal
    /// <see cref="SetConfigOptionParams.ConfigId"/> carries, so the list this reads and the value the
    /// selector writes always address one option.</summary>
    public const string ModelConfigId = "model";

    /// <summary>
    /// The available models, or an empty list when the result publishes none in either shape.
    /// Never throws for malformed JSON — a shape it cannot read contributes nothing.
    /// </summary>
    public static IReadOnlyList<AvailableModelDto> Extract(JsonElement sessionNewResult) {
        if (sessionNewResult.ValueKind != JsonValueKind.Object)
            return [];

        if (FromModelsObject(sessionNewResult) is { Count: > 0 } fromModels)
            return fromModels;

        return FromConfigOptions(sessionNewResult);
    }

    /// <summary>
    /// The <c>models.availableModels</c> shape, with junk entries dropped.
    ///
    /// <para><b>The filter is not cosmetic.</b> <see cref="AvailableModelDto.ModelId"/> is a
    /// non-nullable <c>string</c> in C#, but nothing stops an agent answering
    /// <c>{"availableModels":[{"name":"x"}]}</c> — deserialization then leaves it null, and
    /// <see cref="AcpModelResolver.Resolve"/>'s prefix arm calls <c>ModelId.StartsWith</c> on it and
    /// throws a <see cref="NullReferenceException"/> straight through a caller whose only guard is
    /// <see cref="JsonException"/>. That would turn a malformed vendor response into a failed LAUNCH,
    /// for a feature documented as never being a launch precondition. Found by review, which noticed
    /// this path did not filter while the <c>configOptions</c> path did.</para>
    /// </summary>
    static IReadOnlyList<AvailableModelDto> FromModelsObject(JsonElement result) {
        if (!result.TryGetProperty("models", out var models))
            return [];

        try {
            var available = JsonSerializer
                .Deserialize(models.GetRawText(), CapacitorJsonContext.Default.SessionModelsInfo)
                ?.AvailableModels;

            return available is null
                ? []
                : [.. available.Where(m => m is not null && !string.IsNullOrWhiteSpace(m.ModelId))];
        } catch (JsonException) {
            return [];
        }
    }

    static IReadOnlyList<AvailableModelDto> FromConfigOptions(JsonElement result) {
        if (!result.TryGetProperty("configOptions", out var options) ||
            options.ValueKind != JsonValueKind.Array)
            return [];

        foreach (var element in options.EnumerateArray()) {
            SessionConfigOptionDto? option;
            try {
                option = JsonSerializer.Deserialize(
                    element.GetRawText(), CapacitorJsonContext.Default.SessionConfigOptionDto);
            } catch (JsonException) {
                continue;   // one unreadable sibling must not hide a readable `model` entry
            }

            if (!string.Equals(option?.Id, ModelConfigId, StringComparison.Ordinal))
                continue;

            // The `value` is what set_config_option requires; `name` is the display label, which
            // AcpModelResolver's third arm lets a caller request instead.
            return [.. (option!.Options ?? [])
                .Where(choice => !string.IsNullOrWhiteSpace(choice.Value))
                .Select(choice => new AvailableModelDto(choice.Value, choice.Name))];
        }

        return [];
    }
}
