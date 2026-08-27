using ReactiveUI;

namespace Capacitor.App.ViewModels;

/// One row of the Chat tab. Four shapes, matched by DataTemplates on the concrete type.
public abstract class ChatItemViewModel : ReactiveObject { }

public sealed class UserTurnItem(string text) : ChatItemViewModel {
    public string Text { get; } = text;
}

public sealed class AssistantTextItem(string text) : ChatItemViewModel {
    public string Text { get; } = text;
}

/// System-attributed text — a finished background task, a reconnect note — never anyone's speech.
public sealed class SystemNoteItem(string text) : ChatItemViewModel {
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
