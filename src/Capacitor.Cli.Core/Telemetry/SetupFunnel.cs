using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Telemetry;

/// <summary>
/// The signup funnel: `kcap setup` -> sign-in -> "no tenant" -> workspace offer -> outcome ->
/// setup success. Every event flushes eagerly via <see cref="CliTelemetry.CaptureNow"/> EXCEPT
/// the provisioning/poll outcomes (<see cref="WorkspaceProvisioned"/>, <see cref="WorkspaceFailed"/>):
/// abandonment happens before commitment, and the population this exists to measure abandons
/// setup there and never runs kcap again, so a deferred event at that point is a lost event, not
/// a delayed one.
///
/// The two outcome events are the exception: neither can be reached by walking away from a prompt,
/// so both arrive at the process's normal exit flush. They use the batched
/// <see cref="CliTelemetry.Capture"/> path instead — they don't need the eager flush, and eager
/// flushing would block synchronously inside SpectreTenantProvisioner's interactive prompts and
/// (for the poll outcomes) inside a Spectre live-display callback, which this codebase has prior
/// form for garbling when work blocks there.
///
/// <see cref="WorkspaceFailed"/> does NOT imply a preceding <see cref="WorkspaceRequested"/>: the
/// flag-driven create rejects a bad or taken slug before asking the server for anything. Those
/// carry the reason the check produced (<c>invalid</c>, <c>blocked</c>, <c>reserved</c>,
/// <c>taken</c>, <c>unavailable</c>, <c>availability_unreachable</c>), so a funnel query can tell
/// them from a failure that had a workspace on the way.
///
/// Names deliberately avoid `cli_setup_completed`, which the SERVER already emits — a second
/// producer of that name would double-count across two different persons (the CLI user and
/// whoever the server attributes the event to).
/// </summary>
public static class SetupFunnel {
    public static void Started(bool hasExistingProfile, bool serverUrlProvided, bool noPrompt) =>
        Emit("cli_setup_started", new JsonObject {
            ["has_existing_profile"] = hasExistingProfile,
            ["server_url_provided"]  = serverUrlProvided,
            ["no_prompt"]            = noPrompt,
        });

    public static void SigninOpened(string mode, string provider) =>
        Emit("cli_setup_signin_opened", new JsonObject { ["mode"] = mode, ["provider"] = provider });

    public static void SigninCompleted(string provider) =>
        Emit("cli_setup_signin_completed", new JsonObject { ["provider"] = provider });

    public static void SigninFailed(string reason) =>
        Emit("cli_setup_signin_failed", new JsonObject { ["reason"] = reason });

    /// <summary>
    /// The denominator for "reached signup": the user authenticated but has no tenant. The single
    /// most important event in the feature.
    /// </summary>
    public static void TenantNone(string provider) =>
        Emit("cli_setup_tenant_none", new JsonObject { ["provider"] = provider });

    public static void WorkspaceOffered()   => Emit("cli_setup_workspace_offered", new JsonObject());
    public static void WorkspaceDeclined()  => Emit("cli_setup_workspace_declined", new JsonObject());
    public static void WorkspaceRequested() => Emit("cli_setup_workspace_requested", new JsonObject());

    /// <summary>
    /// Terminal event for the "I already have a workspace" branch: the user was offered a new
    /// workspace and instead redirected setup at one they already have. Deliberately distinct
    /// from <see cref="WorkspaceDeclined"/> — this is not abandonment, setup continues against a
    /// different server (see <c>AuthResult.Retarget</c>) and may still
    /// reach <see cref="Succeeded"/>. Fires before any commitment to THIS offer (no
    /// <see cref="WorkspaceRequested"/> ever follows it), so it stays on the eager path.
    /// </summary>
    public static void WorkspaceRedirected() => Emit("cli_setup_workspace_redirected", new JsonObject());

    public static void WorkspaceProvisioned() => EmitDeferred("cli_setup_workspace_provisioned", new JsonObject());

    public static void WorkspaceFailed(string reason) =>
        EmitDeferred("cli_setup_workspace_failed", new JsonObject { ["reason"] = reason });

    /// <param name="agentsConfigured">
    /// A count, not vendor names — how many coding-agent integrations were installed, not which
    /// ones. Keeping vendor identity out of this event avoids a growing set of per-vendor
    /// properties every time a new agent is supported.
    /// </param>
    public static void Succeeded(int agentsConfigured) =>
        Emit("cli_setup_succeeded", new JsonObject { ["agents_configured"] = agentsConfigured });

    static void Emit(string name, JsonObject props)         => CliTelemetry.CaptureNow(name, props);
    static void EmitDeferred(string name, JsonObject props) => CliTelemetry.Capture(name, props);
}
