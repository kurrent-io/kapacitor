using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Telemetry;

/// <summary>
/// The signup funnel: `kcap setup` -> sign-in -> "no tenant" -> workspace offer -> outcome ->
/// setup success. Every step flushes eagerly via <see cref="CliTelemetry.CaptureNow"/> rather than
/// waiting for the exit-time flush: the population this exists to measure abandons setup and
/// never runs kcap again, so a deferred event — batched for a process that never runs
/// <see cref="CliTelemetry.FlushAndClose"/> again — is a lost event, not a delayed one.
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

    public static void WorkspaceOffered()     => Emit("cli_setup_workspace_offered", new JsonObject());
    public static void WorkspaceDeclined()    => Emit("cli_setup_workspace_declined", new JsonObject());
    public static void WorkspaceRequested()   => Emit("cli_setup_workspace_requested", new JsonObject());
    public static void WorkspaceProvisioned() => Emit("cli_setup_workspace_provisioned", new JsonObject());

    public static void WorkspaceFailed(string reason) =>
        Emit("cli_setup_workspace_failed", new JsonObject { ["reason"] = reason });

    /// <param name="agentsConfigured">
    /// A count, not vendor names — how many coding-agent integrations were installed, not which
    /// ones. Keeping vendor identity out of this event avoids a growing set of per-vendor
    /// properties every time a new agent is supported.
    /// </param>
    public static void Succeeded(int agentsConfigured) =>
        Emit("cli_setup_succeeded", new JsonObject { ["agents_configured"] = agentsConfigured });

    static void Emit(string name, JsonObject props) => CliTelemetry.CaptureNow(name, props);
}
