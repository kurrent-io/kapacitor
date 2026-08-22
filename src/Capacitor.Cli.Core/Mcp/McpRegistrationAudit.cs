using System.Text.Json;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Mcp;

public enum McpRegistrationIssue {
    /// <summary>The entry is semantically the canonical kcap registration (recognized kcap
    /// command, exact canonical args, no customization), so a plugin-shipped entry fully
    /// shadows it and it is safe to remove.</summary>
    CanonicalDuplicate,

    /// <summary>Same name as a kcap server but structurally divergent (custom command, args,
    /// env, or extra fields). Reported so the user can decide; never removed — same name does
    /// not imply ownership.</summary>
    Conflict
}

/// <param name="Scope"><c>user</c> for the top-level <c>mcpServers</c> block, or
/// <c>projects[&lt;path&gt;]</c> for a per-project block.</param>
public sealed record McpRegistrationFinding(string Scope, string Name, McpRegistrationIssue Issue);

/// <summary>
/// Pure audit over Claude Code's user config (<c>~/.claude.json</c>) content: finds user- and
/// project-scope MCP registrations that duplicate the kcap Claude plugin's shipped servers.
/// Each duplicate costs one extra resident server process per open Claude session.
///
/// Classification is STRUCTURAL, never name-only: an entry is a removable duplicate only when
/// it is semantically canonical (a recognized kcap command, the exact canonical
/// <c>["mcp", "&lt;name&gt;"]</c> args, and no custom env/extra fields). A divergent same-name
/// entry is reported as a <see cref="McpRegistrationIssue.Conflict"/> and preserved — this
/// repo's standing policy is that same name does not imply ownership (see
/// <c>JsonMcpConfigWriter</c> / <c>CodexConfigToml</c>).
///
/// Callers own the "is the kcap Claude plugin actually present?" gate
/// (<c>ClaudePluginInstaller.IsInstalled</c>) — without the plugin nothing is shadowed and
/// nothing here is a duplicate.
/// </summary>
public static class McpRegistrationAudit {
    public const string UserScope = "user";

    /// <summary>
    /// Finds kcap-named MCP entries in both Claude config scopes: the top-level
    /// <c>mcpServers</c> block and every <c>projects[&lt;path&gt;].mcpServers</c> block (the
    /// latter is what <c>ClaudeLauncher.WriteMcpConfig</c> copies into agent worktrees).
    /// Unreadable/misshapen JSON yields no findings — the audit is diagnostics, never a gate.
    /// </summary>
    public static IReadOnlyList<McpRegistrationFinding> FindClaudeDuplicates(
        string claudeUserConfigJson, string? nativeBinaryPath = null) {
        var findings = new List<McpRegistrationFinding>();

        try {
            if (JsonNode.Parse(claudeUserConfigJson) is not JsonObject root) return findings;

            Collect(root["mcpServers"] as JsonObject, UserScope, nativeBinaryPath, projectPath: null, findings);

            if (root["projects"] is JsonObject projects)
                foreach (var (key, project) in projects)
                    Collect((project as JsonObject)?["mcpServers"] as JsonObject,
                            $"projects[{key}]", nativeBinaryPath, projectPath: key, findings);
        } catch {
            // Unreadable config — nothing to report.
        }

        return findings;
    }

    /// <summary>
    /// Returns the config with every <see cref="McpRegistrationIssue.CanonicalDuplicate"/>
    /// entry removed from both scopes; conflicts and everything else are preserved verbatim.
    /// Returns the input unchanged when it cannot be parsed (never clobber).
    /// </summary>
    public static string RemoveClaudeDuplicates(string claudeUserConfigJson, string? nativeBinaryPath = null) {
        try {
            if (JsonNode.Parse(claudeUserConfigJson) is not JsonObject root) return claudeUserConfigJson;

            RemoveCanonical(root["mcpServers"] as JsonObject, nativeBinaryPath, projectPath: null);

            if (root["projects"] is JsonObject projects)
                foreach (var (key, project) in projects)
                    RemoveCanonical((project as JsonObject)?["mcpServers"] as JsonObject, nativeBinaryPath,
                                    projectPath: key);

            return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        } catch {
            return claudeUserConfigJson;
        }
    }

    /// <summary>The <c>cwd</c> value the shipped Claude plugin uses for repo-scoped servers —
    /// Claude expands it to the project dir, so it never customizes execution context.</summary>
    public const string ClaudeProjectDirPlaceholder = "${CLAUDE_PROJECT_DIR}";

    /// <summary>
    /// True when <paramref name="entry"/> is semantically the canonical registration of the
    /// kcap server named <paramref name="name"/>: a recognized kcap command (the literal
    /// <c>kcap</c>, or exactly <paramref name="nativeBinaryPath"/> when provided), the exact
    /// canonical args, and only cosmetic extra fields (<c>type: "stdio"</c>,
    /// <c>description</c>, an EMPTY <c>env</c>). Anything else — including a same-named entry
    /// pointing at a different binary — is not canonical and must be preserved.
    ///
    /// <para><c>cwd</c> is NOT cosmetic — it changes the server's execution context (repo
    /// scoping), so an arbitrary value is a customization. It is canonical only on a server
    /// that requires a project cwd (<see cref="KcapMcpServer.NeedsProjectCwd"/>) and only when
    /// it is the shipped <see cref="ClaudeProjectDirPlaceholder"/>, or — for a project-scoped
    /// entry — exactly the project's own path (<paramref name="projectPath"/>), which is what
    /// the placeholder would expand to anyway.</para>
    /// </summary>
    public static bool IsCanonicalKcapEntry(string? name, JsonNode? entry, string? nativeBinaryPath,
                                            string? projectPath = null) {
        if (name is null || FindDescriptor(name) is not { } descriptor || entry is not JsonObject obj) return false;

        if (!IsRecognizedKcapCommand(StringValue(obj["command"]), nativeBinaryPath)) return false;

        if (obj["args"] is not JsonArray args || !ArgsAreCanonical(descriptor, name, args)) return false;

        foreach (var (key, value) in obj) {
            switch (key) {
                case "command" or "args":
                    break;
                case "type":
                    if (!string.Equals(StringValue(value), "stdio", StringComparison.Ordinal)) return false;
                    break;
                case "cwd":
                    if (!descriptor.NeedsProjectCwd) return false; // this server takes no cwd → customization
                    var cwd = StringValue(value);
                    if (cwd is null) return false;
                    if (!string.Equals(cwd, ClaudeProjectDirPlaceholder, StringComparison.Ordinal) &&
                        !(projectPath is not null && string.Equals(cwd, projectPath, StringComparison.Ordinal)))
                        return false; // any other cwd redirects execution context → preserved
                    break;
                case "description":
                    if (StringValue(value) is null) return false;
                    break;
                case "env":
                    if (value is not JsonObject env || env.Count != 0) return false;
                    break;
                default:
                    return false; // custom field → not canonical
            }
        }

        return true;
    }

    /// <summary>
    /// Extracts kcap-named entries under <paramref name="blockKey"/> whose command is an
    /// absolute path to a kcap binary (string command, or argv-array first element — the
    /// OpenCode shape). Pure: the caller decides what "stale" means (typically the file no
    /// longer existing after an npm re-layout) and owns the filesystem check.
    /// </summary>
    public static IReadOnlyList<(string Name, string Command)> FindAbsoluteKcapCommands(
        string configJson, string blockKey = "mcpServers") {
        var results = new List<(string, string)>();

        try {
            if (JsonNode.Parse(configJson) is not JsonObject root ||
                root[blockKey] is not JsonObject block)
                return results;

            foreach (var (name, entry) in block) {
                if (FindDescriptor(name) is null || entry is not JsonObject obj) continue;

                var command = StringValue(obj["command"])
                    ?? (obj["command"] is JsonArray argv && argv.Count > 0 ? StringValue(argv[0]) : null);

                if (command is not null && IsAbsoluteKcapBinaryPath(command))
                    results.Add((name, command));
            }
        } catch {
            // Unreadable config — nothing to report.
        }

        return results;
    }

    /// <summary>The literal <c>kcap</c> (PATH/wrapper resolution) or, when the caller can
    /// resolve it, exactly the current native binary path. Deliberately NOT any path whose
    /// basename happens to be <c>kcap</c> — a user pointing a same-named entry at a custom
    /// build is a conflict to preserve, not a duplicate to remove.</summary>
    static bool IsRecognizedKcapCommand(string? command, string? nativeBinaryPath) =>
        command is not null &&
        (string.Equals(command, KcapMcpServers.Command, StringComparison.Ordinal) ||
         (nativeBinaryPath is not null && string.Equals(command, nativeBinaryPath, StringComparison.Ordinal)));

    internal static bool IsAbsoluteKcapBinaryPath(string command) {
        if (!Path.IsPathRooted(command)) return false;
        var baseName = Path.GetFileNameWithoutExtension(command);
        return string.Equals(baseName, "kcap", StringComparison.OrdinalIgnoreCase);
    }

    static void Collect(JsonObject? block, string scope, string? nativeBinaryPath, string? projectPath,
                        List<McpRegistrationFinding> findings) {
        if (block is null) return;

        foreach (var (name, entry) in block) {
            if (FindDescriptor(name) is null) continue;

            findings.Add(new McpRegistrationFinding(
                scope, name,
                IsCanonicalKcapEntry(name, entry, nativeBinaryPath, projectPath)
                    ? McpRegistrationIssue.CanonicalDuplicate
                    : McpRegistrationIssue.Conflict));
        }
    }

    static void RemoveCanonical(JsonObject? block, string? nativeBinaryPath, string? projectPath) {
        if (block is null) return;

        foreach (var name in block
                     .Where(kv => IsCanonicalKcapEntry(kv.Key, kv.Value, nativeBinaryPath, projectPath))
                     .Select(kv => kv.Key)
                     .ToArray())
            block.Remove(name);
    }

    /// <summary>The registered args must equal the descriptor's exactly, with ONE tolerated
    /// extension: the <c>kcap-flows</c> entry may carry a trailing <c>--driver &lt;vendor&gt;</c> stamp,
    /// because that is part of what kcap now writes for the six JSON harnesses (see
    /// <see cref="KcapMcpServers.ForHarness"/> — kcap-flows reaches the same subcommand and
    /// schema either way). Only the exact two-token shape with a KNOWN stamped vendor qualifies; any
    /// other extra arg (a user customization) keeps the entry a conflict to preserve. The audit is
    /// harness-agnostic, so it accepts any of the stamped vendors rather than the one belonging to
    /// this particular config — a hand-written cross-vendor stamp is astronomically unlikely and is
    /// still functionally kcap's own flows server.</summary>
    static bool ArgsAreCanonical(KcapMcpServer descriptor, string name, JsonArray args) {
        if (args.Count < descriptor.Args.Length) return false;
        for (var i = 0; i < descriptor.Args.Length; i++)
            if (!string.Equals(StringValue(args[i]), descriptor.Args[i], StringComparison.Ordinal))
                return false;

        var extra = args.Count - descriptor.Args.Length;
        if (extra == 0) return true;

        return extra == 2
            && string.Equals(name, KcapMcpServers.FlowsServerName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(StringValue(args[descriptor.Args.Length]), KcapMcpServers.DriverArg, StringComparison.Ordinal)
            && StringValue(args[descriptor.Args.Length + 1]) is { } vendor
            && HarnessMcpProjections.DriverStampVendors.Contains(vendor, StringComparer.Ordinal);
    }

    static KcapMcpServer? FindDescriptor(string name) =>
        KcapMcpServers.All.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    static string? StringValue(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue(out string? s) ? s : null;
}
