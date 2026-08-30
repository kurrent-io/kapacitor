using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

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

public sealed record ElicitationAnswer(string Question, IReadOnlyList<string> SelectedLabels, string? OtherText);

/// The Claude AskUserQuestion contract: parsing the hook's tool_input and composing the
/// updatedInput answer. Caps bound the composed resolve payload; a payload
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

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) {
            writer.WriteStartObject();
            writer.WritePropertyName("questions");
            writer.WriteRawValue(questions.QuestionsJson.GetRawText(), skipInputValidation: true);
            writer.WritePropertyName("answers");
            answersObj.WriteTo(writer);
            writer.WriteEndObject();
        }
        using var doc = JsonDocument.Parse(buffer.ToArray());
        return doc.RootElement.Clone();
    }

    static JsonNode ComposeValue(ElicitationQuestion question, ElicitationAnswer answer) {
        var selected = new List<string>();
        foreach (var label in answer.SelectedLabels) {
            if (!question.Options.Any(o => string.Equals(o.Label, label, StringComparison.Ordinal)))
                throw new ArgumentException($"\"{label}\" is not an option of \"{question.Question}\"");
            if (selected.Contains(label))
                throw new ArgumentException($"\"{label}\" selected twice for \"{question.Question}\"");
            selected.Add(label);
        }

        var other = answer.OtherText?.Trim();
        if (other is not null) {
            if (other.Length == 0)
                throw new ArgumentException($"blank Other text for \"{question.Question}\"");
            if (other.Length > MaxOtherTextChars)
                throw new ArgumentException($"Other text over {MaxOtherTextChars} chars for \"{question.Question}\"");
            // An option label typed as Other IS that option — never a duplicate wire value.
            if (question.Options.Any(o => string.Equals(o.Label, other, StringComparison.Ordinal))) {
                if (!selected.Contains(other)) selected.Add(other);
                other = null;
            }
        }

        if (!question.MultiSelect) {
            if (selected.Count + (other is null ? 0 : 1) != 1)
                throw new ArgumentException($"single-select \"{question.Question}\" needs exactly one value");
            return JsonValue.Create(other ?? selected[0]);
        }

        if (selected.Count == 0 && other is null)
            throw new ArgumentException($"multi-select \"{question.Question}\" needs at least one value");

        var array = new JsonArray();
        foreach (var option in question.Options)
            if (selected.Contains(option.Label)) array.Add((JsonNode)JsonValue.Create(option.Label));
        if (other is not null) array.Add((JsonNode)JsonValue.Create(other));
        return array;
    }
}
