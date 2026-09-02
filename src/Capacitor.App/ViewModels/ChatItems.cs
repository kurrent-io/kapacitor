using System.ComponentModel;
using System.Reactive;
using Avalonia.Collections;
using ReactiveUI;

namespace Capacitor.App.ViewModels;

/// One row of the Chat tab. Five shapes, matched by DataTemplates on the concrete type.
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

public sealed class ToolCallItem(string name, string detail, ToolCategory category) : ChatItemViewModel {
    public string Name { get; } = name;
    public string Detail { get; } = detail;
    public ToolCategory Category { get; } = category;

    ToolOutcome _outcome;
    /// Flipped in place when the matching tool_result arrives; a result is terminal.
    public ToolOutcome Outcome {
        get => _outcome;
        set {
            if (_outcome == value) return;
            _outcome = value;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(OutcomeGlyph));
            this.RaisePropertyChanged(nameof(IsError));
            this.RaisePropertyChanged(nameof(IsSettled));
        }
    }

    bool _isAwaitingPermission;
    /// Owned by the permission cache, not the transcript: the two arrive in either order.
    public bool IsAwaitingPermission {
        get => _isAwaitingPermission;
        set {
            if (_isAwaitingPermission == value) return;
            _isAwaitingPermission = value;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(OutcomeGlyph));
        }
    }

    public bool IsSettled => _outcome != ToolOutcome.Running;
    public bool IsError => _outcome == ToolOutcome.Error;
    public string OutcomeGlyph => _outcome switch {
        ToolOutcome.Done  => "✓",
        ToolOutcome.Error => "✕",
        _                 => _isAwaitingPermission ? "?" : "",
    };
}

/// A run of consecutive tool calls. Settled calls fold into Summary; live ones stay listed.
/// VisibleCalls is the one list the view binds, swapped on toggle so a folded group holds no
/// containers for its settled rows.
public sealed class ToolGroupItem : ChatItemViewModel {
    readonly AvaloniaList<ToolCallItem> _calls = new();
    readonly AvaloniaList<ToolCallItem> _live = new();

    public IAvaloniaReadOnlyList<ToolCallItem> Calls => _calls;
    public IAvaloniaReadOnlyList<ToolCallItem> LiveCalls => _live;
    public IAvaloniaReadOnlyList<ToolCallItem> VisibleCalls => _isExpanded ? _calls : _live;

    bool _isExpanded;
    public bool IsExpanded {
        get => _isExpanded;
        private set {
            if (_isExpanded == value) return;
            _isExpanded = value;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(VisibleCalls));
        }
    }

    public ReactiveCommand<Unit, Unit> ToggleCommand { get; }

    string _summary = "";
    public string Summary { get => _summary; private set => this.RaiseAndSetIfChanged(ref _summary, value); }

    bool _hasSummary;
    public bool HasSummary { get => _hasSummary; private set => this.RaiseAndSetIfChanged(ref _hasSummary, value); }

    bool _hasFailure;
    public bool HasFailure { get => _hasFailure; private set => this.RaiseAndSetIfChanged(ref _hasFailure, value); }

    public ToolGroupItem() {
        ToggleCommand = ReactiveCommand.Create(Toggle);
    }

    public void Toggle() => IsExpanded = !IsExpanded;

    public void Add(ToolCallItem call) {
        _calls.Add(call);
        if (call.IsSettled) { Recompute(); return; }
        _live.Add(call);
        call.PropertyChanged += OnCallChanged;
    }

    void OnCallChanged(object? sender, PropertyChangedEventArgs e) {
        if (e.PropertyName != nameof(ToolCallItem.Outcome) || sender is not ToolCallItem call || !call.IsSettled) return;
        call.PropertyChanged -= OnCallChanged;
        _live.Remove(call);
        Recompute();
    }

    void Recompute() {
        var settled = _calls.Where(c => c.IsSettled).ToList();
        Summary = ToolSummary.Describe(settled.Select(c => c.Category));
        HasFailure = settled.Any(c => c.IsError);
        HasSummary = settled.Count > 0;
    }
}
