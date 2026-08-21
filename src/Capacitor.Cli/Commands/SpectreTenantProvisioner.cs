using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Telemetry;
using Spectre.Console;

namespace Capacitor.Cli.Commands;

// Interactive create-a-tenant flow for `kcap setup` when WorkOS discovery finds
// none. Prompts, provisions via kcap-web, polls until live. OWNS all user-facing
// messaging for its non-Created outcomes.
/// <param name="isInteractive">
/// Test seam. The ambient value is a property of the host the suite runs under, so a test that read it
/// directly would pass in CI and fail in a developer's terminal.
/// </param>
public sealed class SpectreTenantProvisioner(
        TenantProvisioningClient client, string baseUrl, Func<bool>? isInteractive = null) : ITenantProvisioner {
    const int PollIntervalMs = 4000;
    const int MaxPolls       = 150; // ~10 minutes (server budget is 15)

    const string CreateChoice   = "Create a new workspace";
    const string ExistingChoice = "I already have a workspace";
    const string CancelChoice   = "Cancel";

    public async Task<ProvisionOffer> OfferCreateAsync(WorkOSTokenSource tokens, CancellationToken ct = default) {
        // Says what was actually established, not more: single sign-on returned nothing. Claiming
        // "no tenant is linked to your account" is the very falsehood this prompt exists to stop —
        // a GitHub-App workspace IS linked to the user and simply cannot appear in this lane.
        AnsiConsole.MarkupLine("  [yellow]Single sign-on found no Capacitor workspace for your account.[/]");
        AnsiConsole.MarkupLine("  [dim]A workspace that signs in with the GitHub App won't appear here.[/]");

        // Every way out of this fork is a prompt, so with no terminal there is nothing to offer, and
        // Spectre throws NotSupportedException from inside a prompt rather than returning. Deliberately
        // fires no funnel event: nothing was offered, and recording a decline would attribute to the
        // user a choice they were never shown.
        if (!(isInteractive ?? (() => AnsiConsole.Profile.Capabilities.Interactive))()) {
            // Console rather than AnsiConsole, alone in this class: Spectre hard-wraps at the profile
            // width, which breaks `kcap setup <slug>` across a line and hands the reader a command that
            // does not survive being copied. stderr also matches the non-zero exit this leads to.
            Console.Error.WriteLine();
            Console.Error.WriteLine(OAuthLoginFlow.WorkspaceCreationNeedsATerminalMessage());

            return ProvisionOffer.Declined;
        }

        PromptHygiene.DiscardTypeAhead();
        SetupFunnel.WorkspaceOffered();

        // Three ways out, not two: discovery finding nothing does NOT mean the user has no
        // workspace, so offering only "create one" sends an existing member off to make a second.
        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("  How would you like to continue?")
                .AddChoices(CreateChoice, ExistingChoice, CancelChoice));

        if (choice == CancelChoice) {
            AnsiConsole.MarkupLine("  [dim]No tenant created.[/]");
            SetupFunnel.WorkspaceDeclined();
            return ProvisionOffer.Declined;
        }

        if (choice == ExistingChoice) {
            var workspace = AnsiConsole.Prompt(
                new TextPrompt<string>("  Workspace slug or URL:").Validate(v =>
                    string.IsNullOrWhiteSpace(v)
                        ? ValidationResult.Error("Enter a workspace slug (e.g. acme) or a full server URL")
                        : ValidationResult.Success()));

            SetupFunnel.WorkspaceRedirected();
            return ProvisionOffer.ExistingWorkspace(workspace.Trim());
        }

        var orgName = AnsiConsole.Prompt(
            new TextPrompt<string>("  Organization name:").Validate(n =>
                string.IsNullOrWhiteSpace(n) ? ValidationResult.Error("Enter a name") : ValidationResult.Success()));

        var slug = await PromptSlugAsync(orgName, tokens, ct);
        if (slug is null) {
            SetupFunnel.WorkspaceDeclined();
            return ProvisionOffer.Declined;
        }

        var origin = $"https://{slug}.kcap.ai";
        var confirm = AnsiConsole.Prompt(
            new ConfirmationPrompt($"  Create tenant [cyan]{Markup.Escape(orgName)}[/] at [cyan]{origin}[/]?") { DefaultValue = true });
        if (!confirm) {
            AnsiConsole.MarkupLine("  [dim]No tenant created.[/]");
            SetupFunnel.WorkspaceDeclined();
            return ProvisionOffer.Declined;
        }

        SetupFunnel.WorkspaceRequested();
        var outcome = await client.ProvisionAsync(baseUrl, await tokens.GetAsync(ct), orgName, slug, ct);
        switch (outcome.StatusCode) {
            case 200 when outcome.Body?.WorkosOrgId is { Length: > 0 } orgId:
                SetupFunnel.WorkspaceProvisioned();
                return ProvisionOffer.Created(new ProvisionedTenant(orgId, slug, orgName, outcome.Body.Url ?? origin));
            case 202 or 200:
                return await PollAsync(tokens, slug, orgName, origin, ct);
            case 400:
                AnsiConsole.MarkupLine($"  [red]✗[/] {Reason400(outcome.Body?.Reason)}");
                SetupFunnel.WorkspaceFailed(outcome.Body?.Reason ?? "invalid_request");
                return ProvisionOffer.Failed;
            case 409:
                AnsiConsole.MarkupLine($"  [red]✗[/] {Reason409(outcome.Body?.Reason, slug)}");
                SetupFunnel.WorkspaceFailed(outcome.Body?.Reason ?? "conflict");
                return ProvisionOffer.Failed;
            case 0:
                AnsiConsole.MarkupLine("  [red]✗[/] Couldn't reach the provisioning service. Check your connection and try again.");
                SetupFunnel.WorkspaceFailed("unreachable");
                return ProvisionOffer.Failed;
            default:
                AnsiConsole.MarkupLine($"  [red]✗[/] Provisioning failed (HTTP {outcome.StatusCode}). Try again later.");
                SetupFunnel.WorkspaceFailed($"http_{outcome.StatusCode}");
                return ProvisionOffer.Failed;
        }
    }

    async Task<string?> PromptSlugAsync(string orgName, WorkOSTokenSource tokens, CancellationToken ct) {
        var suggestion = SlugValidator.Derive(orgName);
        while (true) {
            var input = AnsiConsole.Prompt(
                new TextPrompt<string>("  Workspace URL slug:")
                    .DefaultValue(suggestion.Length > 0 ? suggestion : "")
                    .ShowDefaultValue());
            var slug = SlugValidator.Canonicalize(input);

            var check = SlugValidator.Validate(slug);
            if (!check.Ok) {
                AnsiConsole.MarkupLine(check.Reason == "blocked"
                    ? $"  [yellow]![/] '{Markup.Escape(slug)}' is reserved — pick another."
                    : "  [yellow]![/] Use lowercase letters, digits and single hyphens (no leading/trailing hyphen), max 40 chars.");
                continue;
            }

            var avail = await AnsiConsole.Status().StartAsync($"Checking {slug}.kcap.ai…",
                async _ => await client.CheckAvailabilityAsync(baseUrl, await tokens.GetAsync(ct), slug, ct));

            if (avail is null) {
                AnsiConsole.MarkupLine("  [yellow]![/] Couldn't check availability. Try again.");
                continue;
            }
            if (avail.Available || avail.Reason == "yours") return slug;

            AnsiConsole.MarkupLine(avail.Reason switch {
                "reserved" => $"  [yellow]![/] '{Markup.Escape(slug)}' is being provisioned by someone else — pick another.",
                "taken"    => $"  [yellow]![/] '{Markup.Escape(slug)}' is taken — pick another.",
                "blocked"  => $"  [yellow]![/] '{Markup.Escape(slug)}' is reserved — pick another.",
                _          => $"  [yellow]![/] '{Markup.Escape(slug)}' is unavailable — pick another."
            });
        }
    }

    async Task<ProvisionOffer> PollAsync(WorkOSTokenSource tokens, string slug, string orgName, string origin, CancellationToken ct) {
        var retry = $"Re-run [cyan]kcap setup {Markup.Escape(slug)}[/]";
        return await AnsiConsole.Status().StartAsync($"Provisioning {slug}.kcap.ai — this can take a few minutes…", async ctx => {
            for (var i = 0; i < MaxPolls; i++) {
                await Task.Delay(PollIntervalMs, ct);
                var status = await client.GetStatusAsync(baseUrl, await tokens.GetAsync(ct), slug, ct);

                switch (ProvisioningPoll.Classify(status.StatusCode, status.Body?.State, status.Body?.WorkosOrgId)) {
                    case PollVerdict.Active:
                        SetupFunnel.WorkspaceProvisioned();
                        return ProvisionOffer.Created(new ProvisionedTenant(status.Body!.WorkosOrgId!, slug, orgName, status.Body.Url ?? origin));
                    case PollVerdict.ActiveNoOrg:
                        AnsiConsole.MarkupLine($"  [red]✗[/] {Markup.Escape(slug)}.kcap.ai is live but isn't linked to an organization. Contact support.");
                        SetupFunnel.WorkspaceFailed("active_no_org");
                        return ProvisionOffer.Failed;
                    case PollVerdict.Failed:
                        AnsiConsole.MarkupLine($"  [red]✗[/] Provisioning failed. {retry} to retry.");
                        SetupFunnel.WorkspaceFailed("provisioning_failed");
                        return ProvisionOffer.Failed;
                    case PollVerdict.Forbidden:
                        AnsiConsole.MarkupLine($"  [red]✗[/] Verify your email address, then {retry.ToLowerInvariant()}.");
                        SetupFunnel.WorkspaceFailed("forbidden");
                        return ProvisionOffer.Failed;
                    case PollVerdict.NotFound:
                        AnsiConsole.MarkupLine($"  [red]✗[/] '{Markup.Escape(slug)}' isn't linked to your account. {retry}.");
                        SetupFunnel.WorkspaceFailed("not_found");
                        return ProvisionOffer.Failed;
                    case PollVerdict.Wait:
                        // Surface liveness so an elapsed timer never reads as a frozen CLI.
                        ctx.Status($"Provisioning {slug}.kcap.ai — waiting for it to come online… ({i + 1}/{MaxPolls})");
                        break;
                }
            }
            AnsiConsole.MarkupLine($"  [yellow]![/] Still provisioning. {retry} once it's ready.");
            SetupFunnel.WorkspaceFailed("poll_timeout");
            return ProvisionOffer.InProgress(slug);
        });
    }

    static string Reason400(string? reason) => reason switch {
        "disposable_email" => "Provisioning requires a non-disposable email address.",
        "blocked"          => "That slug is reserved. Pick another and re-run.",
        _                  => "Invalid organization name or slug."
    };

    static string Reason409(string? reason, string slug) => reason switch {
        "owned_by_other" => $"'{slug}' is owned by someone else. Pick another and re-run.",
        _                => $"'{slug}' is already taken. Pick another and re-run."
    };
}
