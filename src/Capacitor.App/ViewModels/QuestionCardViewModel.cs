using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using ReactiveUI;

namespace Capacitor.App.ViewModels;

public sealed class QuestionOptionViewModel : ReactiveObject {
    readonly Action _selectionChanged;
    bool _isSelected;

    public string Label { get; }
    public string? Description { get; }
    public bool IsMulti { get; }
    public ReactiveCommand<Unit, Unit> PickCommand { get; }

    public bool IsSelected {
        get => _isSelected;
        set {
            if (_isSelected == value) return;
            this.RaiseAndSetIfChanged(ref _isSelected, value);
            _selectionChanged();
        }
    }

    // Notifies the owning group directly rather than via this.WhenAnyValue: that call routes
    // through ReactiveUI's ObservableForProperty/RxAppBuilder global init, only reliably primed
    // once some other test has already pumped the headless dispatcher first (see
    // SessionRailViewModel and RailWorktreeViewModel for the same avoidance).
    internal QuestionOptionViewModel(ElicitationOption option, bool isMulti, Func<QuestionOptionViewModel, Task> pick, IObservable<bool> idle, Action selectionChanged) {
        Label = option.Label;
        Description = option.Description;
        IsMulti = isMulti;
        _selectionChanged = selectionChanged;
        PickCommand = ReactiveCommand.CreateFromTask(() => pick(this), idle);
    }
}

public sealed class QuestionGroupViewModel : ReactiveObject {
    readonly BehaviorSubject<bool> _answered = new(false);
    string _otherText = "";
    bool _inFastPath;

    public string? Header { get; }
    public string Text { get; }
    public bool MultiSelect { get; }
    public IReadOnlyList<QuestionOptionViewModel> Options { get; }
    public int MaxOtherLength => ClaudeElicitation.MaxOtherTextChars;
    public ReactiveCommand<Unit, Unit> EnterCommand { get; }
    internal IObservable<bool> Answered => _answered;
    internal bool InFastPath {
        get => _inFastPath;
        set => this.RaiseAndSetIfChanged(ref _inFastPath, value);
    }

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
    public bool ShowsOtherAnswer => InFastPath && !string.IsNullOrWhiteSpace(OtherText);

    internal QuestionGroupViewModel(ElicitationQuestion question, Func<QuestionOptionViewModel, QuestionGroupViewModel, Task> pick,
            Func<QuestionGroupViewModel, Task> enter, IObservable<bool> idle) {
        Header = question.Header;
        Text = question.Question;
        MultiSelect = question.MultiSelect;
        Options = question.Options.Select(o => new QuestionOptionViewModel(o, question.MultiSelect, opt => pick(opt, this), idle, Refresh)).ToList();
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
        foreach (var q in Questions) q.InFastPath = IsFastPath;

        var allAnswered = Questions.Select(q => q.Answered).CombineLatest(states => states.All(x => x));
        SubmitCommand = ReactiveCommand.CreateFromTask(SubmitAsync, allAnswered.CombineLatest(idle, (a, i) => a && i));

        Disposables.Add(SubmitCommand);
        // Dispose() runs Disposables in order: cancel _lifetime BEFORE disposing it, so the
        // token passed to AnswerAsync observes cancellation rather than firing on a disposed CTS.
        Disposables.Add(Disposable.Create(() => { try { _lifetime.Cancel(); } catch (ObjectDisposedException) { } }));
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
        } catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) {
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: question submit failed unexpectedly: {ex.Message}");
            ErrorText = "Something went wrong — try again";
        } finally {
            IsBusy = false;
        }
    }
}
