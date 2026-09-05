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
    bool _showsHeader;

    public int Index { get; }
    public string? Header { get; }
    public string Text { get; }
    public bool MultiSelect { get; }
    public IReadOnlyList<QuestionOptionViewModel> Options { get; }
    public int MaxOtherLength => ClaudeElicitation.MaxOtherTextChars;
    public ReactiveCommand<Unit, Unit> EnterCommand { get; }
    /// Returns the card to this question from the review step.
    public ReactiveCommand<Unit, Unit> EditCommand { get; }
    internal IObservable<bool> Answered => _answered;
    internal bool InFastPath {
        get => _inFastPath;
        set => this.RaiseAndSetIfChanged(ref _inFastPath, value);
    }

    /// The header rides the step chip once there is a series; alone, it sits above the question.
    public bool ShowsHeader {
        get => _showsHeader;
        internal set => this.RaiseAndSetIfChanged(ref _showsHeader, value);
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

    /// The review line: picked labels in option order, then any Other text; empty when unanswered.
    public string AnswerSummary {
        get {
            var parts = Options.Where(o => o.IsSelected).Select(o => o.Label).ToList();
            if (!string.IsNullOrWhiteSpace(OtherText)) parts.Add(OtherText.Trim());
            return string.Join(", ", parts);
        }
    }

    internal QuestionGroupViewModel(int index, ElicitationQuestion question, Func<QuestionOptionViewModel, QuestionGroupViewModel, Task> pick,
            Func<QuestionGroupViewModel, Task> enter, Action<QuestionGroupViewModel> edit, IObservable<bool> idle) {
        Index = index;
        Header = question.Header;
        Text = question.Question;
        MultiSelect = question.MultiSelect;
        Options = question.Options.Select(o => new QuestionOptionViewModel(o, question.MultiSelect, opt => pick(opt, this), idle, Refresh)).ToList();
        EnterCommand = ReactiveCommand.CreateFromTask(() => enter(this), idle);
        EditCommand = ReactiveCommand.Create(() => edit(this), idle);
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
        this.RaisePropertyChanged(nameof(AnswerSummary));
    }
}

/// One chip in the card's step row: a question, or the closing Review step.
public sealed class QuestionStepViewModel : ReactiveObject {
    bool _isCurrent;
    bool _isAnswered;

    public int Index { get; }
    public string Title { get; }
    public bool IsReview { get; }
    public ReactiveCommand<Unit, Unit> GoCommand { get; }

    public bool IsCurrent {
        get => _isCurrent;
        internal set => this.RaiseAndSetIfChanged(ref _isCurrent, value);
    }

    public bool IsAnswered {
        get => _isAnswered;
        internal set => this.RaiseAndSetIfChanged(ref _isAnswered, value);
    }

    internal QuestionStepViewModel(int index, string title, bool isReview, Action<int> go, IObservable<bool> idle) {
        Index = index;
        Title = title;
        IsReview = isReview;
        GoCommand = ReactiveCommand.Create(() => go(index), idle);
    }
}

/// The NEEDS YOU question card. A series is walked one question at a time — a single-select pick
/// advances, the last question leads to a Review step listing every answer, and only that step
/// submits — while a lone question keeps its Submit inline. A lone single-select question with
/// options is the fast path: the pick itself submits. No Deny and no Allow always by design.
public sealed class QuestionCardViewModel : PendingCardViewModel {
    readonly PendingPermissionRequest _entry;
    readonly IPermissionService _permissions;
    readonly CancellationTokenSource _lifetime = new();
    readonly BehaviorSubject<bool> _canBack = new(false);
    readonly BehaviorSubject<bool> _canNext = new(false);
    readonly BehaviorSubject<bool> _onSubmitStep;
    int _currentIndex;

    public IReadOnlyList<QuestionGroupViewModel> Questions { get; }
    /// Every question plus, for a series, the Review step; a lone question has no step row.
    public IReadOnlyList<QuestionStepViewModel> Steps { get; }
    public bool IsFastPath { get; }
    public bool HasReview => Questions.Count > 1;
    public bool ShowsSteps => HasReview;
    public ReactiveCommand<Unit, Unit> BackCommand { get; }
    public ReactiveCommand<Unit, Unit> NextCommand { get; }
    public ReactiveCommand<Unit, Unit> SubmitCommand { get; }

    public int CurrentIndex {
        get => _currentIndex;
        private set {
            if (_currentIndex == value) return;
            _currentIndex = value;
            foreach (var step in Steps) step.IsCurrent = step.Index == value;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(CurrentQuestion));
            this.RaisePropertyChanged(nameof(IsOnReview));
            this.RaisePropertyChanged(nameof(ShowsSubmit));
            this.RaisePropertyChanged(nameof(ShowsNext));
            this.RaisePropertyChanged(nameof(NextLabel));
            this.RaisePropertyChanged(nameof(StepLabel));
            UpdateNavigation();
        }
    }

    public QuestionGroupViewModel? CurrentQuestion => _currentIndex < Questions.Count ? Questions[_currentIndex] : null;
    public bool IsOnReview => HasReview && _currentIndex == Questions.Count;
    public bool ShowsSubmit => !IsFastPath && (!HasReview || IsOnReview);
    public bool ShowsNext => HasReview && !IsOnReview;
    public string NextLabel => _currentIndex == Questions.Count - 1 ? "Review" : "Next";
    public string StepLabel => !HasReview ? "" : IsOnReview ? "Review your answers" : $"Question {_currentIndex + 1} of {Questions.Count}";

    public QuestionCardViewModel(PendingPermissionRequest entry, IPermissionService permissions) : base(entry) {
        _entry = entry;
        _permissions = permissions;
        var parsed = entry.Questions ?? throw new ArgumentException("not an elicitation entry", nameof(entry));

        var idle = Busy.Select(b => !b);
        Questions = parsed.Questions.Select((q, i) => new QuestionGroupViewModel(i, q, PickAsync, EnterAsync, g => GoTo(g.Index), idle)).ToList();
        IsFastPath = Questions.Count == 1 && !Questions[0].MultiSelect && Questions[0].Options.Count > 0;
        foreach (var q in Questions) {
            q.InFastPath = IsFastPath;
            q.ShowsHeader = !HasReview && q.Header is not null;
        }

        var steps = new List<QuestionStepViewModel>();
        if (HasReview) {
            steps.AddRange(Questions.Select(q => new QuestionStepViewModel(q.Index, q.Header ?? $"Question {q.Index + 1}", false, GoTo, idle)));
            steps.Add(new QuestionStepViewModel(Questions.Count, "Review", true, GoTo, idle));
            steps[0].IsCurrent = true;
        }
        Steps = steps;

        _onSubmitStep = new BehaviorSubject<bool>(!HasReview);
        var allAnswered = Questions.Select(q => q.Answered).CombineLatest(states => states.All(x => x));
        SubmitCommand = ReactiveCommand.CreateFromTask(SubmitAsync,
            allAnswered.CombineLatest(idle, _onSubmitStep, (a, i, s) => a && i && s));
        BackCommand = ReactiveCommand.Create(() => GoTo(_currentIndex - 1), _canBack.CombineLatest(idle, (b, i) => b && i));
        NextCommand = ReactiveCommand.Create(() => GoTo(_currentIndex + 1), _canNext.CombineLatest(idle, (n, i) => n && i));

        foreach (var q in Questions) {
            var step = HasReview ? Steps[q.Index] : null;
            Disposables.Add(q.Answered.Subscribe(answered => {
                if (step is not null) step.IsAnswered = answered;
                if (q.Index == _currentIndex) UpdateNavigation();
            }));
        }

        Disposables.Add(SubmitCommand);
        Disposables.Add(BackCommand);
        Disposables.Add(NextCommand);
        Disposables.Add(_canBack);
        Disposables.Add(_canNext);
        Disposables.Add(_onSubmitStep);
        // Dispose() runs Disposables in order: cancel _lifetime BEFORE disposing it, so the
        // token passed to AnswerAsync observes cancellation rather than firing on a disposed CTS.
        Disposables.Add(Disposable.Create(() => { try { _lifetime.Cancel(); } catch (ObjectDisposedException) { } }));
        Disposables.Add(_lifetime);
    }

    void GoTo(int index) {
        if (IsDisposed) return;
        var last = HasReview ? Questions.Count : Questions.Count - 1;
        CurrentIndex = Math.Clamp(index, 0, last);
    }

    void UpdateNavigation() {
        _canBack.OnNext(_currentIndex > 0);
        _canNext.OnNext(ShowsNext && CurrentQuestion is { IsAnswered: true });
        _onSubmitStep.OnNext(!HasReview || IsOnReview);
    }

    Task PickAsync(QuestionOptionViewModel option, QuestionGroupViewModel group) {
        if (group.MultiSelect) { option.IsSelected = !option.IsSelected; return Task.CompletedTask; }
        group.SelectExclusively(option);
        if (IsFastPath) return SubmitAsync();
        if (HasReview && group.Index == _currentIndex) GoTo(_currentIndex + 1);
        return Task.CompletedTask;
    }

    Task EnterAsync(QuestionGroupViewModel group) {
        if (IsFastPath) return group.ShowsOtherAnswer ? SubmitAsync() : Task.CompletedTask;
        if (!group.IsAnswered) return Task.CompletedTask;
        if (HasReview) {
            if (group.Index == _currentIndex) GoTo(_currentIndex + 1);
            return Task.CompletedTask;
        }
        return SubmitAsync();
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
