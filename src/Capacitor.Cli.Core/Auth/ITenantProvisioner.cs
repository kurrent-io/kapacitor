namespace Capacitor.Cli.Core.Auth;

public enum ProvisionOfferStatus { Created, Declined, InProgress, Failed, ExistingWorkspace }

public sealed record ProvisionedTenant(
    string OrganizationId, string Slug, string DisplayName, string Origin);

// Result of offering to create a tenant. The provisioner OWNS all user-facing
// messaging for Declined/InProgress/Failed; the caller must not print a second,
// conflicting message (e.g. the legacy "ask your admin" dead-end).
public sealed record ProvisionOffer(
    ProvisionOfferStatus Status,
    ProvisionedTenant?   Tenant,
    // ExistingWorkspace only: the slug or URL the user typed, unresolved — surrounding whitespace
    // is removed, nothing else is interpreted. Resolving it stays the caller's job (a bare label
    // expands to {slug}.kcap.ai), so provider selection happens exactly once, against the target
    // server's own /auth/config — which is how this path reaches a GitHub-App tenant that WorkOS
    // discovery structurally cannot return.
    string?              ExistingWorkspaceInput = null,
    // InProgress only: the slug being provisioned, so a caller can name it when telling the user to
    // come back to it. Null when the poll timed out before a slug was settled on.
    string?              PendingSlug = null) {
    public static ProvisionOffer Created(ProvisionedTenant t) => new(ProvisionOfferStatus.Created, t);
    public static readonly ProvisionOffer Declined = new(ProvisionOfferStatus.Declined, null);
    public static readonly ProvisionOffer Failed   = new(ProvisionOfferStatus.Failed,   null);

    public static ProvisionOffer InProgress(string? slug = null) =>
        new(ProvisionOfferStatus.InProgress, null, PendingSlug: slug);

    public static ProvisionOffer ExistingWorkspace(string input) =>
        new(ProvisionOfferStatus.ExistingWorkspace, null, input);
}

public interface ITenantProvisioner {
    // Interactive: prompt -> provision -> poll. Returns Created (with the tenant) on success;
    // Declined/InProgress/Failed otherwise, or ExistingWorkspace when the user would rather
    // point at a workspace they already belong to than create one.
    //
    // Takes a token source rather than a bare access token: provisioning + polling can run
    // for minutes, outliving WorkOS's ~5-minute access-token TTL, so each server call pulls a
    // freshly-refreshed token via the source (see WorkOSTokenSource).
    Task<ProvisionOffer> OfferCreateAsync(WorkOSTokenSource tokens, CancellationToken ct = default);
}
