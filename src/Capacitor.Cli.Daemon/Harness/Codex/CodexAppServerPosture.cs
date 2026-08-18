using System.Text.Json.Nodes;

namespace Capacitor.Cli.Daemon.Harness.Codex;

/// <summary>
/// Renders a resolved Codex posture — the <c>(sandbox, approval)</c> pair from
/// <see cref="CodexPosturePolicy.Resolve"/> — into the JSON shapes the <c>codex app-server</c>
/// protocol expects, mirroring how <see cref="CodexLauncher"/> renders the same pair into PTY argv.
///
/// <para>Two invariants pinned by the app-server protocol spike and enforced here:
/// (1) <c>approvalPolicy</c> is a plain kebab-case STRING — the granular <c>{granular:{…}}</c> object
/// form crashes the app-server process on <c>turn/start</c> (immediate stdout EOF); and
/// (2) approvals are always routed to the client (<c>approvalsReviewer = "user"</c>) so a user-config
/// <c>approvals_reviewer = "auto_review"</c> Guardian can never auto-approve a sandbox escalation.</para>
/// </summary>
internal static class CodexAppServerPosture {
    /// <summary>Approvals always route to the client, never Codex's own auto-review subagent.
    /// Slotted into <c>thread/start</c>'s <c>approvalsReviewer</c>.</summary>
    public const string ApprovalsReviewer = "user";

    /// <summary>The <c>sandboxPolicy</c> object for <c>turn/start</c>. <paramref name="writableRoots"/>
    /// applies only to <c>workspace-write</c> (the reviewer's owned worktree); it is ignored for the
    /// other postures. <c>networkAccess</c> is left unset (defaults false) — reviewers get no network.</summary>
    public static JsonObject RenderSandboxPolicy(string sandbox, IReadOnlyList<string> writableRoots) => sandbox switch {
        "read-only"          => new JsonObject { ["type"] = "readOnly" },
        "danger-full-access" => new JsonObject { ["type"] = "dangerFullAccess" },
        "workspace-write"    => RenderWorkspaceWrite(writableRoots),
        _ => throw new ArgumentOutOfRangeException(nameof(sandbox), sandbox,
            "Unknown Codex sandbox; app-server posture accepts only read-only, workspace-write, danger-full-access."),
    };

    static JsonObject RenderWorkspaceWrite(IReadOnlyList<string> writableRoots) {
        var roots = new JsonArray();
        foreach (var root in writableRoots) roots.Add((JsonNode?) root);
        return new JsonObject { ["type"] = "workspaceWrite", ["writableRoots"] = roots };
    }

    /// <summary>Validates a resolved sandbox token and returns it as the coarse <c>SandboxMode</c>
    /// STRING slotted into <c>thread/start</c>'s <c>sandbox</c> — a DIFFERENT wire shape from
    /// <c>turn/start</c>'s <c>sandboxPolicy</c> object (<see cref="RenderSandboxPolicy"/>), which is
    /// the load-bearing per-turn containment. The app-server rejects the object form here (verified
    /// against the pinned binary), so this must stay the plain kebab-case string.</summary>
    public static string RenderSandboxMode(string sandbox) => sandbox switch {
        "read-only" or "workspace-write" or "danger-full-access" => sandbox,
        _ => throw new ArgumentOutOfRangeException(nameof(sandbox), sandbox,
            "Unknown Codex sandbox; app-server thread/start accepts only read-only, workspace-write, danger-full-access."),
    };

    /// <summary>Validates the approval policy is one of the three known kebab-case strings and returns
    /// it verbatim — the value slotted into <c>turn/start</c>'s <c>approvalPolicy</c>. Anything else is
    /// rejected loudly: an unknown token or a granular object would either crash the app-server or
    /// silently loosen containment.</summary>
    public static string RenderApprovalPolicy(string approval) => approval switch {
        "never" or "on-request" or "untrusted" => approval,
        _ => throw new ArgumentOutOfRangeException(nameof(approval), approval,
            "Unknown Codex approval policy; app-server posture accepts only never, on-request, untrusted."),
    };
}
