using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Telemetry;

namespace Capacitor.App.Services.Onboarding;

/// <summary>
/// The façade's progress sink, re-published as events on the UI scheduler.
/// <paramref name="post"/> is <c>Dispatcher.UIThread.Post</c> in production and an immediate
/// invoke in tests — every event crosses it, because the flows raise them from their own threads.
/// </summary>
public sealed class UiAuthProgress(Action<Action> post) : IAuthProgress {
    public event Action<string>?         NoticeReceived;
    public event Action<string>?         ErrorReceived;
    public event Action<string>?         BrowserOpened;
    public event Action<string, string>? DeviceCodeReceived;
    public event Action?                 PollTicked;

    public void Notice(string message) => post(() => NoticeReceived?.Invoke(message));

    public void Error(string message) => post(() => ErrorReceived?.Invoke(message));

    public void BrowserOpening(string url) => post(() => BrowserOpened?.Invoke(url));

    public void DeviceCode(string code, string verificationUri) =>
        post(() => DeviceCodeReceived?.Invoke(code, verificationUri));

    public void PollTick() => post(() => PollTicked?.Invoke());
}

/// <summary>
/// The tenant pick as a UI round trip: <see cref="PickAsync"/> publishes the tenants and parks on
/// a completion source the view resolves. A null selection is the user backing out — Core renders
/// that as "No tenant selected."
/// </summary>
public sealed class WizardTenantPicker : ITenantPicker {
    readonly Lock _gate = new();

    TaskCompletionSource<DiscoveredTenant?>? _pending;

    /// Raised on the flow's own thread; the view model marshals it.
    public event Action<DiscoveredTenant[]>? SelectionRequested;

    public DiscoveredTenant? Pick(DiscoveredTenant[] tenants) =>
        throw new NotSupportedException("The wizard picker is asynchronous — the façade consumes PickAsync.");

    public async Task<DiscoveredTenant?> PickAsync(DiscoveredTenant[] tenants, CancellationToken ct) {
        var pending = new TaskCompletionSource<DiscoveredTenant?>(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource<DiscoveredTenant?>? displaced;

        lock (_gate) {
            displaced = _pending;
            _pending  = pending;
        }

        // A displaced pick has no view left to resolve it — end it rather than orphan its await.
        displaced?.TrySetCanceled(CancellationToken.None);

        using var registration = ct.Register(() => pending.TrySetCanceled(ct));

        SelectionRequested?.Invoke(tenants);

        try {
            return await pending.Task.ConfigureAwait(false);
        } finally {
            lock (_gate) {
                if (ReferenceEquals(_pending, pending)) _pending = null;
            }
        }
    }

    public void Select(DiscoveredTenant? tenant) {
        TaskCompletionSource<DiscoveredTenant?>? pending;

        lock (_gate) {
            pending  = _pending;
            _pending = null;
        }

        pending?.TrySetResult(tenant);
    }
}

/// The three ways out of a zero-tenant discovery — the CLI's mode menu, GUI-shaped.
public abstract record ProvisionMode {
    public sealed record Create : ProvisionMode;

    /// The slug or URL the user would rather point at; resolved by the caller, not here.
    public sealed record Existing(string Input) : ProvisionMode;

    public sealed record Cancel : ProvisionMode;
}

/// <summary>
/// The CLI's Spectre provisioner flow with its prompts hoisted into hooks the Sign-in step
/// implements: same funnel events, same slug/availability loop, same 4 s × 150 poll contract
/// and the same terminal outcomes. Messaging for every non-Created outcome stays here (the
/// interface's contract) and reaches the user through <paramref name="progress"/>.
/// </summary>
public sealed class WizardTenantProvisioner(
        TenantProvisioningClient client,
        string                   baseUrl,
        IAuthProgress            progress,
        TimeProvider?            time = null) : ITenantProvisioner {
    internal const int PollIntervalMs = 4000;
    internal const int MaxPolls       = 150; // ~10 minutes (server budget is 15)

    readonly TimeProvider _time = time ?? TimeProvider.System;

    /// Null hooks answer as "backed out": an unwired provisioner must never provision.
    public Func<CancellationToken, Task<ProvisionMode>>?               OfferMode     { get; set; }
    public Func<CancellationToken, Task<string?>>?                     PromptOrgName { get; set; }
    public Func<string, string?, CancellationToken, Task<string?>>?    PromptSlug    { get; set; }
    public Func<string, string, CancellationToken, Task<bool>>?        ConfirmCreate { get; set; }
    public Action<int, int>?                                           PollProgress  { get; set; }

    public async Task<ProvisionOffer> OfferCreateAsync(WorkOSTokenSource tokens, CancellationToken ct = default) {
        // Says what was actually established, not more: single sign-on returned nothing.
        progress.Notice("Single sign-on found no Capacitor workspace for your account.");
        progress.Notice("A workspace that signs in with the GitHub App won't appear here.");
        SetupFunnel.WorkspaceOffered();

        var mode = OfferMode is null ? new ProvisionMode.Cancel() : await OfferMode(ct);

        switch (mode) {
            case ProvisionMode.Existing existing when !string.IsNullOrWhiteSpace(existing.Input):
                SetupFunnel.WorkspaceRedirected();

                return ProvisionOffer.ExistingWorkspace(existing.Input.Trim());
            case ProvisionMode.Create:
                break;
            default:
                return Declined();
        }

        var orgName = PromptOrgName is null ? null : await PromptOrgName(ct);

        if (string.IsNullOrWhiteSpace(orgName)) return Declined();

        orgName = orgName.Trim();

        var slug = await ResolveSlugAsync(orgName, tokens, ct);

        if (slug is null) return Declined();

        var origin = $"https://{slug}.kcap.ai";

        if (ConfirmCreate is null || !await ConfirmCreate(slug, origin, ct)) return Declined();

        SetupFunnel.WorkspaceRequested();

        var outcome = await client.ProvisionAsync(baseUrl, await tokens.GetAsync(ct), orgName, slug, ct);

        switch (outcome.StatusCode) {
            case 200 when outcome.Body?.WorkosOrgId is { Length: > 0 } orgId:
                SetupFunnel.WorkspaceProvisioned();

                return ProvisionOffer.Created(new ProvisionedTenant(orgId, slug, orgName, outcome.Body.Url ?? origin));
            case 202 or 200:
                return await PollAsync(tokens, slug, orgName, origin, ct);
            case 400:
                return Failed(Reason400(outcome.Body?.Reason), outcome.Body?.Reason ?? "invalid_request");
            case 409:
                return Failed(Reason409(outcome.Body?.Reason, slug), outcome.Body?.Reason ?? "conflict");
            case 0:
                return Failed("Couldn't reach the provisioning service. Check your connection and try again.", "unreachable");
            default:
                return Failed($"Provisioning failed (HTTP {outcome.StatusCode}). Try again later.", $"http_{outcome.StatusCode}");
        }
    }

    async Task<string?> ResolveSlugAsync(string orgName, WorkOSTokenSource tokens, CancellationToken ct) {
        var     suggestion = SlugValidator.Derive(orgName);
        string? error      = null;

        while (true) {
            if (PromptSlug is null) return null;

            var input = await PromptSlug(suggestion, error, ct);

            if (input is null) return null;

            var slug  = SlugValidator.Canonicalize(input);
            var check = SlugValidator.Validate(slug);

            suggestion = slug;

            if (!check.Ok) {
                error = check.Reason == "blocked"
                    ? $"'{slug}' is reserved — pick another."
                    : "Use lowercase letters, digits and single hyphens (no leading/trailing hyphen), max 40 characters.";

                continue;
            }

            var availability = await client.CheckAvailabilityAsync(baseUrl, await tokens.GetAsync(ct), slug, ct);

            if (availability is null) {
                error = "Couldn't check availability. Try again.";

                continue;
            }

            if (availability.Available || availability.Reason == "yours") return slug;

            error = availability.Reason switch {
                "reserved" => $"'{slug}' is being provisioned by someone else — pick another.",
                "taken"    => $"'{slug}' is taken — pick another.",
                "blocked"  => $"'{slug}' is reserved — pick another.",
                _          => $"'{slug}' is unavailable — pick another."
            };
        }
    }

    async Task<ProvisionOffer> PollAsync(
            WorkOSTokenSource tokens, string slug, string orgName, string origin, CancellationToken ct) {
        var retry = $"join '{slug}' from the Connect step";

        for (var attempt = 0; attempt < MaxPolls; attempt++) {
            await Task.Delay(TimeSpan.FromMilliseconds(PollIntervalMs), _time, ct);

            var status = await client.GetStatusAsync(baseUrl, await tokens.GetAsync(ct), slug, ct);

            switch (ProvisioningPoll.Classify(status.StatusCode, status.Body?.State, status.Body?.WorkosOrgId)) {
                case PollVerdict.Active:
                    SetupFunnel.WorkspaceProvisioned();

                    return ProvisionOffer.Created(
                        new ProvisionedTenant(status.Body!.WorkosOrgId!, slug, orgName, status.Body.Url ?? origin));
                case PollVerdict.ActiveNoOrg:
                    return Failed($"{slug}.kcap.ai is live but isn't linked to an organization. Contact support.", "active_no_org");
                case PollVerdict.Failed:
                    return Failed($"Provisioning failed — {retry} to retry.", "provisioning_failed");
                case PollVerdict.Forbidden:
                    return Failed($"Verify your email address, then {retry}.", "forbidden");
                case PollVerdict.NotFound:
                    return Failed($"'{slug}' isn't linked to your account — {retry}.", "not_found");
                case PollVerdict.Wait:
                    PollProgress?.Invoke(attempt + 1, MaxPolls);

                    break;
            }
        }

        progress.Error($"Still provisioning — finish later by joining '{slug}' from the Connect step.");
        SetupFunnel.WorkspaceFailed("poll_timeout");

        return ProvisionOffer.InProgress;
    }

    // The interface hands the provisioner every non-Created message, so a decline must say so here
    // or the step renders a bare failure for something the user chose.
    ProvisionOffer Declined() {
        progress.Notice("No workspace created.");
        SetupFunnel.WorkspaceDeclined();

        return ProvisionOffer.Declined;
    }

    ProvisionOffer Failed(string message, string reason) {
        progress.Error(message);
        SetupFunnel.WorkspaceFailed(reason);

        return ProvisionOffer.Failed;
    }

    static string Reason400(string? reason) => reason switch {
        "disposable_email" => "Provisioning requires a non-disposable email address.",
        "blocked"          => "That slug is reserved. Pick another and try again.",
        _                  => "Invalid organization name or slug."
    };

    static string Reason409(string? reason, string slug) => reason switch {
        "owned_by_other" => $"'{slug}' is owned by someone else. Pick another and try again.",
        _                => $"'{slug}' is already taken. Pick another and try again."
    };
}

/// <summary>
/// The bridge set one wizard run shares; the composition root builds the façade over exactly
/// these. The sink is built from this object's own <see cref="Post"/> and handed to the
/// provisioner factory, so a bridge marshalling through a different dispatcher than the rest is
/// not representable.
/// </summary>
public sealed class WizardBridges {
    public WizardBridges(Action<Action> post, Func<IAuthProgress, WizardTenantProvisioner> provisioner) {
        Post        = post;
        Progress    = new UiAuthProgress(post);
        Picker      = new WizardTenantPicker();
        Provisioner = provisioner(Progress);
    }

    public Action<Action>          Post        { get; }
    public UiAuthProgress          Progress    { get; }
    public WizardTenantPicker      Picker      { get; }
    public WizardTenantProvisioner Provisioner { get; }
}

/// <summary>
/// The one mapping from a Connect intent to a façade call, used by the composition root to build
/// <see cref="WizardAuthService"/>'s operation. Wizard sign-ins adopt the server — that is what
/// carries <see cref="OnboardingGate"/> to Complete.
/// </summary>
public static class WizardSignInOperation {
    public static Func<ConnectIntent, CancellationToken, Task<AuthResult>> For(OnboardingFacade facade) =>
        async (intent, ct) => intent switch {
            ConnectIntent.Paste paste => await facade.LoginAsync(
                ResolveServer(paste.ServerInput), forceDevice: false, profile: null, ct, adoptServer: true),
            ConnectIntent.Discover discover => await facade.DiscoverAsync(discover.Provider, forceDevice: false, ct),
            // Creation runs inside WorkOS discovery, after the org-less sign-in finds no tenant.
            ConnectIntent.Create => await facade.DiscoverAsync(AuthProvider.WorkOS, forceDevice: false, ct),
            _                    => new AuthResult.Failed("No connection was chosen.")
        };

    /// <summary>
    /// Origin first, then slug expansion: a pasted page URL must lose its path before
    /// <see cref="ServerInput.ResolveTenantArg"/> decides it already looks like a host. A
    /// scheme-less host or host:port then gets Core's own scheme rule — the pure half of the
    /// normalizer the CLI probes with, so a loopback server lands on http here too.
    /// </summary>
    public static string ResolveServer(string input) {
        var resolved = ServerInput.ResolveTenantArg(ServerInput.ToServerOrigin(input));

        return HostOnly(resolved) ? ServerUrlNormalizer.WithLoopbackDefault(resolved) : resolved;
    }

    // A host or host:port (bracketed IPv6 included); anything naming an unusable scheme ("file:")
    // is left alone to fail the server-URL validator.
    static bool HostOnly(string value) {
        var bracketEnd = value.StartsWith('[') ? value.IndexOf(']') : -1;
        var colon      = value.IndexOf(':', bracketEnd > 0 ? bracketEnd + 1 : 0);

        return colon < 0 || (colon + 1 < value.Length && value[(colon + 1)..].All(char.IsAsciiDigit));
    }
}
