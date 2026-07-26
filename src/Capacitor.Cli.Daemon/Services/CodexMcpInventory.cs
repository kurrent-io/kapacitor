using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Authoritative enumeration of the MCP servers a spawned Codex session would inherit, used by
/// <see cref="CodexLauncher"/> to disable every non-whitelisted server for an unattended
/// review-flow reviewer (the recursion guard).
///
/// Codex 0.144.3 composes its effective MCP registry from TWO sources: the user-level
/// <c>$CODEX_HOME/config.toml</c> <c>[mcp_servers]</c> table AND active native plugins (each
/// plugin manifest can register servers). Reading <c>config.toml</c> alone (the pre-hardening
/// behaviour) therefore MISSED plugin-provided servers. A project/cwd-level
/// <c>.codex/config.toml</c> is verified NOT to be an MCP source in 0.144.3 (it carries hooks /
/// skills / AGENTS.md only), so the two sources above are the complete set.
///
/// <c>codex mcp list --json</c> is the one command that reports the fully-composed effective list
/// (config + plugins), honouring <c>CODEX_HOME</c> exactly as the spawned reviewer will (both
/// inherit the daemon's environment), so it is the enumeration authority. Failure to run or parse
/// it is FAIL-CLOSED: the caller must reject the launch rather than proceed with an incomplete
/// (fail-open) view of what the reviewer would inherit.
/// </summary>
internal static class CodexMcpInventory {
    /// <summary>Max time to wait for <c>codex mcp list --json</c> before treating enumeration as
    /// failed (fail-closed). Generous: the command only reads config + plugin manifests.</summary>
    const int TimeoutMs = 15_000;

    /// <summary>
    /// Runs <c>{codexPath} mcp list --json</c> (inheriting this process's environment, so the same
    /// <c>CODEX_HOME</c> the reviewer will use is honoured) and returns the effective MCP server
    /// names (config.toml + plugins). Throws <see cref="CodexReviewerMcpIsolationException"/> if the
    /// process can't be started, times out, exits non-zero, or emits output that isn't a parseable
    /// JSON array — never returns a partial/empty list to mask such a failure.
    /// </summary>
    public static IReadOnlyList<string> ListInheritedServerNames(string codexPath) {
        string stdout;
        string stderr;
        int    exitCode;

        try {
            using var process = Process.Start(new ProcessStartInfo {
                FileName               = codexPath,
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                ArgumentList           = { "mcp", "list", "--json" }
            }) ?? throw new CodexReviewerMcpIsolationException(
                $"Could not start '{codexPath} mcp list --json' to enumerate the reviewer's inherited MCP servers.");

            // Read to end BEFORE WaitForExit to avoid a full-pipe deadlock on large output.
            stdout = process.StandardOutput.ReadToEnd();
            stderr = process.StandardError.ReadToEnd();

            if (!process.WaitForExit(TimeoutMs)) {
                try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }

                throw new CodexReviewerMcpIsolationException(
                    $"'{codexPath} mcp list --json' timed out while enumerating the reviewer's inherited MCP servers.");
            }

            exitCode = process.ExitCode;
        } catch (CodexReviewerMcpIsolationException) {
            throw;
        } catch (Exception ex) {
            throw new CodexReviewerMcpIsolationException(
                $"Failed to enumerate the reviewer's inherited Codex MCP servers via '{codexPath} mcp list --json': {ex.Message}");
        }

        if (exitCode != 0) {
            var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;

            throw new CodexReviewerMcpIsolationException(
                $"'{codexPath} mcp list --json' exited {exitCode} while enumerating the reviewer's inherited MCP servers: {detail.Trim()}");
        }

        return ParseServerNames(stdout);
    }

    /// <summary>
    /// Parses the <c>codex mcp list --json</c> payload — a JSON array of <c>{ "name": string, … }</c>
    /// objects — into the list of server names. Pure and side-effect free (unit-testable without the
    /// codex binary). Throws <see cref="CodexReviewerMcpIsolationException"/> when the payload isn't a
    /// JSON array or an element has no string <c>name</c>: an unparseable enumeration must fail closed,
    /// not silently drop servers. A valid empty array (<c>[]</c>) returns an empty list.
    /// </summary>
    public static IReadOnlyList<string> ParseServerNames(string json) {
        JsonNode? root;

        try {
            root = JsonNode.Parse(json);
        } catch (Exception ex) {
            throw new CodexReviewerMcpIsolationException(
                $"Could not parse 'codex mcp list --json' output as JSON: {ex.Message}");
        }

        if (root is not JsonArray array) {
            throw new CodexReviewerMcpIsolationException(
                "'codex mcp list --json' output was not a JSON array.");
        }

        var names = new List<string>(array.Count);

        foreach (var element in array) {
            if (element?["name"] is not JsonValue nameNode ||
                !nameNode.TryGetValue<string>(out var name) ||
                string.IsNullOrEmpty(name)) {
                throw new CodexReviewerMcpIsolationException(
                    "'codex mcp list --json' returned an MCP server entry with no usable name.");
            }

            names.Add(name);
        }

        return names;
    }
}
