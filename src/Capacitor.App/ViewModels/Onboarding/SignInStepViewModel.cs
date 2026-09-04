using System.Collections.ObjectModel;
using System.Reactive;
using Capacitor.App.Services;
using Capacitor.App.Services.Onboarding;
using Capacitor.Cli.Core.Auth;
using ReactiveUI;

namespace Capacitor.App.ViewModels.Onboarding;

/// <summary>
/// Runs ONE façade operation for the staged intent and renders its structured progress: notices,
/// the browser fallback URL, the device code, the tenant list, and the create-workspace prompts.
/// Nothing starts on entry — the step has an explicit Sign in action, because the operation is the
/// only thing on the wizard that reaches the network. Cancellation is never rendered as a failure.
/// </summary>
public sealed class SignInStepViewModel : ReactiveObject, IWizardStep {
    internal const int LogLimit = 200;

    /// One pending UI answer. The flow parks on <see cref="AskAsync"/>; the view resolves it, a
    /// cancel releases it, and the prompt is torn down either way.
    sealed class UiQuestion<T> {
        TaskCompletionSource<T>? _pending;

        public async Task<T> AskAsync(Action<Action> post, Action show, Action hide, CancellationToken ct) {
            var pending = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Exchange(ref _pending, pending);

            using var registration = ct.Register(() => pending.TrySetCanceled(ct));

            post(show);

            try {
                return await pending.Task.ConfigureAwait(false);
            } finally {
                Interlocked.CompareExchange(ref _pending, null, pending);
                post(hide);
            }
        }

        public void Answer(T value) => Interlocked.Exchange(ref _pending, null)?.TrySetResult(value);
    }

    readonly WizardAuthService       _service;
    readonly ConnectStepViewModel    _connect;
    readonly WizardTenantPicker      _picker;
    readonly ConsentFlipClaims       _claims;
    readonly IAppStateStore          _appState;
    readonly IUrlOpener              _urlOpener;
    readonly Action<Action>          _post;

    readonly UiQuestion<ProvisionMode> _mode    = new();
    readonly UiQuestion<string?>       _orgName = new();
    readonly UiQuestion<string?>       _slug    = new();
    readonly UiQuestion<bool>          _confirm = new();

    AuthAttempt?   _attempt;
    ConnectIntent? _running;
    Task?          _run;
    string?        _lastReport;

    string  _status = "";
    bool    _statusIsError;
    string? _statusDetail;
    bool    _busy;
    bool    _satisfied;
    string? _deviceCode;
    string? _verificationUri;
    string? _browserUrl;
    string? _waitingText;
    string? _quarantineNotice;
    bool    _tenantPickerVisible;
    bool    _modeChoiceVisible;
    bool    _orgNamePromptVisible;
    bool    _slugPromptVisible;
    bool    _confirmVisible;
    string  _orgNameText = "";
    string  _slugText = "";
    string? _slugError;
    string  _existingWorkspaceInput = "";
    string  _confirmText = "";
    string? _provisioningProgress;

    DiscoveredTenant? _selectedTenant;

    public SignInStepViewModel(
            WizardAuthService    service,
            ConnectStepViewModel connect,
            WizardBridges        bridges,
            ConsentFlipClaims    claims,
            IAppStateStore       appState,
            IUrlOpener           urlOpener) {
        _service   = service;
        _connect   = connect;
        _picker    = bridges.Picker;
        _claims    = claims;
        _appState  = appState;
        _urlOpener = urlOpener;
        _post      = bridges.Post;

        bridges.Progress.NoticeReceived += line => {
            // Kept as the failure detail's fallback: a decline is reported as a notice, not an error.
            _lastReport = line;
            Append(line);
        };
        bridges.Progress.ErrorReceived += line => {
            _lastReport  = line;
            StatusDetail = line;
            Append(line);
        };
        bridges.Progress.BrowserOpened      += url => {
            BrowserUrl = url;
            // StatusDetail survives here: SetStatus only clears it on the next non-error headline.
            StatusDetail = "Finish authorization in the browser, then return here. This window updates when you're done.";
            WaitingText  = "Waiting for you to authorize…";
            Append("Opened the sign-in page in your browser.");
        };
        bridges.Progress.DeviceCodeReceived += (code, verificationUri, prefilled) => {
            DeviceCode      = StripClipboardNote(code);
            VerificationUri = verificationUri;
            // Raw here, stripped above: the chip is what the user reads, the log records what was
            // actually reported - including the clipboard note. Pinned by the view-model tests.
            Append(prefilled
                ? $"Check the code shown is {code} at {verificationUri}"
                : $"Enter the code {code} at {verificationUri}");
        };
        bridges.Progress.PollTicked += () => WaitingText = "Waiting for you to authorize…";

        _picker.SelectionRequested += tenants => _post(() => {
            Tenants.Clear();
            foreach (var tenant in tenants) Tenants.Add(tenant);
            SelectedTenant      = Tenants.FirstOrDefault();
            TenantPickerVisible = true;
        });

        var provisioner = bridges.Provisioner;
        provisioner.OfferMode     = OfferModeAsync;
        provisioner.PromptOrgName = ct => _orgName.AskAsync(
            _post, () => OrgNamePromptVisible = true, () => OrgNamePromptVisible = false, ct);
        provisioner.PromptSlug = (suggestion, error, ct) => _slug.AskAsync(_post, () => {
            Slug              = suggestion;
            SlugError         = error;
            SlugPromptVisible = true;
        }, () => SlugPromptVisible = false, ct);
        provisioner.ConfirmCreate = (slug, origin, ct) => _confirm.AskAsync(_post, () => {
            ConfirmText    = $"Create the workspace '{slug}' at {origin}?";
            ConfirmVisible = true;
        }, () => ConfirmVisible = false, ct);
        provisioner.PollProgress = (attempt, max) =>
            _post(() => ProvisioningProgress = $"Setting up your workspace — this can take a few minutes… ({attempt}/{max})");

        SignInCommand = ReactiveCommand.CreateFromTask(SignInAsync);
        CancelCommand = ReactiveCommand.Create(() => _attempt?.Cancel());

        OpenSignInUrlCommand      = ReactiveCommand.Create(() => Open(BrowserUrl));
        OpenVerificationUriCommand = ReactiveCommand.Create(() => Open(VerificationUri));

        ConfirmTenantCommand = ReactiveCommand.Create(() => {
            TenantPickerVisible = false;
            _picker.Select(SelectedTenant);
        });
        CancelTenantCommand = ReactiveCommand.Create(() => {
            TenantPickerVisible = false;
            _picker.Select(null);
        });

        // Unofferable rather than declinable: a blank submit must never end the whole run.
        var hasWorkspace = this.WhenAnyValue(x => x.ExistingWorkspaceInput, input => !string.IsNullOrWhiteSpace(input));
        var hasOrgName   = this.WhenAnyValue(x => x.OrgName, name => !string.IsNullOrWhiteSpace(name));

        CreateWorkspaceCommand      = ReactiveCommand.Create(() => _mode.Answer(new ProvisionMode.Create()));
        UseExistingWorkspaceCommand = ReactiveCommand.Create(
            () => _mode.Answer(new ProvisionMode.Existing(ExistingWorkspaceInput)), hasWorkspace);
        CancelWorkspaceCommand      = ReactiveCommand.Create(() => _mode.Answer(new ProvisionMode.Cancel()));

        SubmitOrgNameCommand = ReactiveCommand.Create(() => _orgName.Answer(OrgName), hasOrgName);
        CancelOrgNameCommand = ReactiveCommand.Create(() => _orgName.Answer(null));
        SubmitSlugCommand    = ReactiveCommand.Create(() => _slug.Answer(Slug));
        CancelSlugCommand    = ReactiveCommand.Create(() => _slug.Answer(null));
        ConfirmCreateCommand = ReactiveCommand.Create(() => _confirm.Answer(true));
        DeclineCreateCommand = ReactiveCommand.Create(() => _confirm.Answer(false));

        AcknowledgeQuarantineCommand = ReactiveCommand.CreateFromTask(AcknowledgeQuarantineAsync);
    }

    public WizardStepId Id         => WizardStepId.SignIn;
    public string       Title      => "Sign in";
    public bool         Applicable => true;

    /// The Sign-in step's answer to a WorkOS "I already have a workspace": the Connect step is
    /// prefilled here, and the event lets the shell navigate back to it.
    public event Action<string>? RetargetRequested;

    public ObservableCollection<string>           Log     { get; } = [];
    public ObservableCollection<DiscoveredTenant> Tenants { get; } = [];

    public ReactiveCommand<Unit, Unit> SignInCommand                { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand                { get; }
    public ReactiveCommand<Unit, Unit> OpenSignInUrlCommand         { get; }
    public ReactiveCommand<Unit, Unit> OpenVerificationUriCommand   { get; }
    public ReactiveCommand<Unit, Unit> ConfirmTenantCommand         { get; }
    public ReactiveCommand<Unit, Unit> CancelTenantCommand          { get; }
    public ReactiveCommand<Unit, Unit> CreateWorkspaceCommand       { get; }
    public ReactiveCommand<Unit, Unit> UseExistingWorkspaceCommand  { get; }
    public ReactiveCommand<Unit, Unit> CancelWorkspaceCommand       { get; }
    public ReactiveCommand<Unit, Unit> SubmitOrgNameCommand         { get; }
    public ReactiveCommand<Unit, Unit> CancelOrgNameCommand         { get; }
    public ReactiveCommand<Unit, Unit> SubmitSlugCommand            { get; }
    public ReactiveCommand<Unit, Unit> CancelSlugCommand            { get; }
    public ReactiveCommand<Unit, Unit> ConfirmCreateCommand         { get; }
    public ReactiveCommand<Unit, Unit> DeclineCreateCommand         { get; }
    public ReactiveCommand<Unit, Unit> AcknowledgeQuarantineCommand { get; }

    public string Status {
        get => _status;
        private set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    public bool StatusIsError {
        get => _statusIsError;
        private set => this.RaiseAndSetIfChanged(ref _statusIsError, value);
    }

    /// The last error line the façade rendered — the detail behind the generic failure headline.
    public string? StatusDetail {
        get => _statusDetail;
        private set => this.RaiseAndSetIfChanged(ref _statusDetail, value);
    }

    public bool Busy {
        get => _busy;
        private set {
            this.RaiseAndSetIfChanged(ref _busy, value);
            this.RaisePropertyChanged(nameof(Idle));
        }
    }

    public bool Idle => !Busy;

    public bool Satisfied {
        get => _satisfied;
        private set => this.RaiseAndSetIfChanged(ref _satisfied, value);
    }

    public string? DeviceCode {
        get => _deviceCode;
        private set => this.RaiseAndSetIfChanged(ref _deviceCode, value);
    }

    public string? VerificationUri {
        get => _verificationUri;
        private set => this.RaiseAndSetIfChanged(ref _verificationUri, value);
    }

    public string? BrowserUrl {
        get => _browserUrl;
        private set => this.RaiseAndSetIfChanged(ref _browserUrl, value);
    }

    public string? WaitingText {
        get => _waitingText;
        private set => this.RaiseAndSetIfChanged(ref _waitingText, value);
    }

    public string? QuarantineNotice {
        get => _quarantineNotice;
        private set => this.RaiseAndSetIfChanged(ref _quarantineNotice, value);
    }

    public bool TenantPickerVisible {
        get => _tenantPickerVisible;
        private set => this.RaiseAndSetIfChanged(ref _tenantPickerVisible, value);
    }

    public DiscoveredTenant? SelectedTenant {
        get => _selectedTenant;
        set => this.RaiseAndSetIfChanged(ref _selectedTenant, value);
    }

    public bool ModeChoiceVisible {
        get => _modeChoiceVisible;
        private set => this.RaiseAndSetIfChanged(ref _modeChoiceVisible, value);
    }

    public bool OrgNamePromptVisible {
        get => _orgNamePromptVisible;
        private set => this.RaiseAndSetIfChanged(ref _orgNamePromptVisible, value);
    }

    public bool SlugPromptVisible {
        get => _slugPromptVisible;
        private set => this.RaiseAndSetIfChanged(ref _slugPromptVisible, value);
    }

    public bool ConfirmVisible {
        get => _confirmVisible;
        private set => this.RaiseAndSetIfChanged(ref _confirmVisible, value);
    }

    public string OrgName {
        get => _orgNameText;
        set => this.RaiseAndSetIfChanged(ref _orgNameText, value);
    }

    public string Slug {
        get => _slugText;
        set => this.RaiseAndSetIfChanged(ref _slugText, value);
    }

    public string? SlugError {
        get => _slugError;
        private set => this.RaiseAndSetIfChanged(ref _slugError, value);
    }

    public string ExistingWorkspaceInput {
        get => _existingWorkspaceInput;
        set => this.RaiseAndSetIfChanged(ref _existingWorkspaceInput, value);
    }

    public string ConfirmText {
        get => _confirmText;
        private set => this.RaiseAndSetIfChanged(ref _confirmText, value);
    }

    public string? ProvisioningProgress {
        get => _provisioningProgress;
        private set => this.RaiseAndSetIfChanged(ref _provisioningProgress, value);
    }

    public Task OnEnterAsync(CancellationToken ct) {
        if (!Satisfied && !Busy) SetStatus(ReadyStatus(), isError: false, ReadyDetail());

        return Task.CompletedTask;
    }

    /// <summary>
    /// Never vetoes. A live attempt is cancelled, and the RUN — not just the attempt — is awaited:
    /// pre-boundary that ends it with nothing durable, past the boundary the operation still
    /// answers Committed, and either way the step's rendered state is final before the wizard
    /// moves, regardless of which continuation registered first.
    /// </summary>
    public async Task<bool> CanLeaveAsync(WizardNavigation direction, CancellationToken ct) {
        _attempt?.Cancel();

        if (_run is { } run) {
            try {
                await run.ConfigureAwait(true);
            } catch (Exception ex) {
                // A run that failed unexpectedly must not veto the navigation.
                Console.Error.WriteLine($"kcap: wizard sign-in run failed unexpectedly: {ex.Message}");
            }
        }

        return true;
    }

    /// The command's body, reachable directly so a re-entrant call can be asserted as a no-op.
    internal Task SignInAsync() {
        // Busy is set before RunAsync's first await, so a re-entrant call never displaces _run.
        if (Busy) return Task.CompletedTask;

        return _run = RunAsync();
    }

    async Task RunAsync() {
        if (_connect.Intent is not { } intent) {
            SetStatus("Choose how to connect on the Connect step.", isError: false);

            return;
        }

        ResetForRun(intent);
        Busy = true;
        // Before Begin: a synchronously-rendered error must not have its detail wiped by this.
        SetStatus(
            "Waiting for your browser…",
            isError: false,
            "Complete authorization there, then return here — this window updates on its own.");

        AuthAttempt attempt;

        try {
            attempt = _service.Begin(intent);
        } catch (InvalidOperationException) {
            Busy = false;
            SetStatus("Finishing the previous attempt — try again in a moment.", isError: false);

            return;
        }

        _attempt = attempt;

        var result = await attempt.Result.ConfigureAwait(true);

        _attempt = null;
        Busy     = false;
        HidePrompts();
        // Nothing polls a device code or a browser wait once the attempt has settled.
        ClearTransient();
        Apply(result);
        await SurfaceQuarantineAsync().ConfigureAwait(true);
    }

    // The Connect intent IS the mode; only a discovery that found nothing has to ask.
    Task<ProvisionMode> OfferModeAsync(CancellationToken ct) =>
        _running is ConnectIntent.Create
            ? Task.FromResult<ProvisionMode>(new ProvisionMode.Create())
            : _mode.AskAsync(_post, () => ModeChoiceVisible = true, () => ModeChoiceVisible = false, ct);

    void Apply(AuthResult result) {
        switch (result) {
            case AuthResult.Committed committed:
                Satisfied = true;
                SetStatus(CommittedStatus(committed), isError: false);

                break;
            case AuthResult.Cancelled:
                SetStatus("Sign-in cancelled.", isError: false);

                break;
            case AuthResult.Retarget retarget:
                _connect.Prefill(retarget.ServerInput);
                SetStatus($"Continue with {retarget.ServerInput} from the Connect step.", isError: false);
                RetargetRequested?.Invoke(retarget.ServerInput);

                break;
            // Provisioning outran its poll window. Sign-in itself succeeded and the workspace is on its
            // way, so headlining a failure here would tell the user something untrue.
            case AuthResult.Failed { Reason: AuthFailureReason.ProvisioningInProgress } pending:
                SetStatus(pending.Message, isError: false, _lastReport);

                break;
            // Already rendered through the sink; prefer the last reported line over in-flight
            // guidance (StatusDetail holds browser-wait copy until an ErrorReceived overwrites it).
            default:
                SetStatus("Sign-in failed.", isError: true, _lastReport ?? StatusDetail);

                break;
        }
    }

    static string CommittedStatus(AuthResult.Committed committed) =>
        committed.Provider == AuthProvider.None
            ? "No sign-in required for this server."
            : committed.Username is { Length: > 0 } username ? $"Signed in as {username}" : "Signed in.";

    string ReadyStatus() => _connect.Intent switch {
        // Destination only — the window title already says "Sign in"; detail explains what happens.
        ConnectIntent.Paste paste       => paste.ServerInput,
        ConnectIntent.Discover discover => discover.Provider == AuthProvider.WorkOS
            ? "Find your workspaces with single sign-on"
            : "Find your workspaces with GitHub",
        ConnectIntent.Create => "Create a workspace",
        _                    => "Choose how to connect on the Connect step.",
    };

    string ReadyDetail() => _connect.Intent switch {
        ConnectIntent.Paste =>
            "Opens your browser to authorize this machine and stores a token for launching hosted agents.",
        ConnectIntent.Discover =>
            "Opens your browser, then lists the workspaces your account can access.",
        ConnectIntent.Create =>
            "Walks you through setup, then authorizes this machine for the new workspace.",
        _ => "Pick a connection option on the Connect step, then come back here.",
    };

    async Task SurfaceQuarantineAsync() {
        try {
            // A read is what discovers corruption, and a cancelled attempt never armed anything.
            await Task.Run(_claims.Pending).ConfigureAwait(true);

            if (_claims.Quarantine() is not { } quarantine) return;
            if ((await _appState.LoadAsync().ConfigureAwait(true)).ConsentQuarantineAcked) return;

            QuarantineNotice = ConsentFlipCoordinator.QuarantineDisclosure(quarantine.PreservedPath);
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: consent quarantine surfacing failed unexpectedly: {ex.Message}");
        }
    }

    async Task AcknowledgeQuarantineAsync() {
        QuarantineNotice = null;

        try {
            await ConsentFlipCoordinator.AckQuarantineAsync(_appState).ConfigureAwait(true);
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: consent quarantine ack failed unexpectedly: {ex.Message}");
        }
    }

    void ResetForRun(ConnectIntent intent) {
        _running    = intent;
        _lastReport = null;
        Log.Clear();
        HidePrompts();
        ClearTransient();
        SlugError    = null;
        StatusDetail = null;
    }

    void ClearTransient() {
        DeviceCode           = null;
        VerificationUri      = null;
        BrowserUrl           = null;
        WaitingText          = null;
        ProvisioningProgress = null;
    }

    void HidePrompts() {
        TenantPickerVisible  = false;
        ModeChoiceVisible    = false;
        OrgNamePromptVisible = false;
        SlugPromptVisible    = false;
        ConfirmVisible       = false;
    }

    void SetStatus(string text, bool isError, string? detail = null) {
        Status        = text;
        StatusIsError = isError;
        StatusDetail  = detail;
    }

    void Append(string line) {
        Log.Add(line);

        while (Log.Count > LogLimit) Log.RemoveAt(0);
    }

    void Open(string? url) {
        if (string.IsNullOrEmpty(url)) return;

        try {
            _urlOpener.Open(url);
        } catch (Exception ex) {
            Append($"Couldn't open the browser: {ex.Message}");
        }
    }

    /// The prominent display shows the code alone; the log keeps the line the flow emitted.
    internal static string StripClipboardNote(string code) {
        var note = code.IndexOf("  (copied", StringComparison.Ordinal);

        return note < 0 ? code.Trim() : code[..note].Trim();
    }
}
