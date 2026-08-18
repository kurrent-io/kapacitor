using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Antigravity;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Core.Harness.Copilot;
using Capacitor.Cli.Core.Harness.Cursor;
using Capacitor.Cli.Core.Harness.Gemini;
using Capacitor.Cli.Core.Harness.Kiro;
using Capacitor.Cli.Core.Harness.OpenCode;
using Capacitor.Cli.Core.Mcp;

namespace Capacitor.Cli.Commands;

/// <summary>
/// The MCP-registrations section of <c>kcap daemon doctor</c>. Deliberately runs BEFORE the
/// daemon-file early return — a machine with no daemon state still has registrations worth
/// auditing. Two checks:
///
/// <list type="number">
/// <item><b>Duplicate Claude registrations</b> — user/project-scope copies of plugin-shipped
/// kcap servers in <c>~/.claude.json</c> (each costs one extra resident server process per open
/// session). Gated on the kcap Claude plugin actually being installed; classification is
/// structural, never name-only (<see cref="McpRegistrationAudit"/>): only a semantically
/// canonical copy is removable, a divergent same-name entry is reported and preserved.
/// Read-only by default; <c>--clean</c> removes only the canonical duplicates.</item>
/// <item><b>Stale absolute binary paths</b> — Phase-2 registrations point at the native binary,
/// and an npm re-layout can strand them. "Stale" is two-tier: the registered file no longer
/// exists (broken — re-run <c>kcap setup</c>) vs. it differs from the current resolution
/// (outdated — healed by the next <c>kcap setup</c>/<c>kcap update</c>). Only kcap-named
/// entries are inspected.</item>
/// </list>
///
/// All paths are injected so tests run against fixture files in temp dirs, never the real
/// <c>~/.claude.json</c> or harness configs.
/// </summary>
static class McpDoctorSection {
    /// <summary>One JSON registration file to scan: a display label, its path, and the MCP
    /// block key its harness uses ("mcpServers" for most, "mcp" for OpenCode).</summary>
    internal sealed record RegistrationFile(string Label, string Path, string BlockKey);

    /// <summary>The user-scope registration files kcap writes (Claude's own config is handled
    /// separately — it carries the duplicate audit too, not just the stale-path scan).</summary>
    internal static IReadOnlyList<RegistrationFile> DefaultJsonRegistrations() => [
        new("Cursor",      CursorPaths.UserMcpJson(),        "mcpServers"),
        new("Copilot",     CopilotPaths.McpConfigJson(),     "mcpServers"),
        new("Kiro",        KiroPaths.SettingsMcpJson(),      "mcpServers"),
        new("Gemini",      GeminiPaths.SettingsJson(),       "mcpServers"),
        new("OpenCode",    OpenCodePaths.McpConfigJson(),    "mcp"),
        new("Antigravity", AntigravityPaths.McpConfigJson(), "mcpServers"),
    ];

    /// <summary>Returns the number of issues found (0 = healthy).</summary>
    public static async Task<int> RunAsync(TextWriter output, bool clean,
                                           string claudeConfigPath, string claudeSettingsPath,
                                           IReadOnlyList<RegistrationFile> jsonRegistrations,
                                           string? codexConfigPath,
                                           string? nativeBinaryPath) {
        var issues = 0;
        issues += await AuditClaudeDuplicatesAsync(output, clean, claudeConfigPath, claudeSettingsPath, nativeBinaryPath);
        issues += await AuditStalePathsAsync(output, claudeConfigPath, jsonRegistrations, codexConfigPath, nativeBinaryPath);

        if (issues == 0) await output.WriteLineAsync("MCP registrations: no issues found.");
        return issues;
    }

    static async Task<int> AuditClaudeDuplicatesAsync(TextWriter output, bool clean,
                                                      string claudeConfigPath, string claudeSettingsPath,
                                                      string? nativeBinaryPath) {
        if (!File.Exists(claudeConfigPath)) return 0;
        // Without an EFFECTIVE plugin (enabled registration + resolvable payload — never the
        // version marker alone) nothing is shadowed: the user-scope entry is the only
        // registration, and cleanup would delete the servers outright.
        if (!ClaudePluginInstaller.IsEffectivelyInstalled(claudeSettingsPath)) return 0;

        string json;
        try { json = await File.ReadAllTextAsync(claudeConfigPath); } catch { return 0; }

        var findings = McpRegistrationAudit.FindClaudeDuplicates(json, nativeBinaryPath);
        if (findings.Count == 0) return 0;

        var duplicates = 0;
        foreach (var f in findings) {
            if (f.Issue == McpRegistrationIssue.CanonicalDuplicate) {
                duplicates++;
                await output.WriteLineAsync(
                    $"  duplicate MCP registration '{f.Name}' ({f.Scope}) in {claudeConfigPath} shadows the " +
                    "kcap plugin entry (costs one extra server process per session)" +
                    (clean ? "" : " — run `kcap daemon doctor --clean` to remove it"));
            } else {
                await output.WriteLineAsync(
                    $"  same-named MCP registration '{f.Name}' ({f.Scope}) in {claudeConfigPath} differs from " +
                    "the kcap plugin entry — left untouched (remove it manually if unintended)");
            }
        }

        if (clean && duplicates > 0) {
            switch (TryCleanClaudeConfig(claudeConfigPath, json, nativeBinaryPath, out var error)) {
                case CleanOutcome.Cleaned:
                    await output.WriteLineAsync($"  removed {duplicates} duplicate MCP registration(s) from {claudeConfigPath}");
                    break;
                case CleanOutcome.Conflicted:
                    await output.WriteLineAsync(
                        $"  {claudeConfigPath} changed while doctor was running — skipped the clean; " +
                        "re-run `kcap daemon doctor --clean`");
                    break;
                case CleanOutcome.Failed:
                    await output.WriteLineAsync($"  could not clean {claudeConfigPath}: {error}");
                    break;
            }
        }

        return findings.Count;
    }

    internal enum CleanOutcome { Cleaned, Conflicted, Failed }

    /// <summary>
    /// Commits the duplicate removal computed from <paramref name="snapshotJson"/>. The config is
    /// shared: Claude itself and the daemon's trust write both update it, and nothing in this
    /// codebase offers a cross-process lock for it (the daemon's trust write serializes only
    /// in-process) — so kcap-side writers serialize under a named mutex keyed on the path, and the
    /// remaining window against OTHER writers is closed by re-reading INSIDE the lock immediately
    /// before committing and aborting (<see cref="CleanOutcome.Conflicted"/>, nothing written) when
    /// the file no longer matches the snapshot the findings were computed from — a blind rewrite
    /// there would silently drop their update. The write is an atomic sibling-rename that preserves
    /// the target's Unix mode (a 0600 config must not come back 0644; new files default owner-only).
    /// </summary>
    /// <param name="afterReReadForTesting">Test hook, invoked INSIDE the config lock after the
    /// snapshot re-check passes and before the write — lets a test park the commit while it
    /// proves a concurrent kcap writer blocks on the same lock. Never set in production.</param>
    internal static CleanOutcome TryCleanClaudeConfig(string claudeConfigPath, string snapshotJson,
                                                      string? nativeBinaryPath, out string? error,
                                                      Action? afterReReadForTesting = null) {
        error = null;
        try {
            // The shared cross-process lock every kcap writer of this file takes
            // (ClaudeLauncher's trust write included) — see ConfigFileLock.
            using var _ = ConfigFileLock.Acquire(claudeConfigPath);

            if (!string.Equals(File.ReadAllText(claudeConfigPath), snapshotJson, StringComparison.Ordinal))
                return CleanOutcome.Conflicted;

            afterReReadForTesting?.Invoke();

            var cleaned = McpRegistrationAudit.RemoveClaudeDuplicates(snapshotJson, nativeBinaryPath);
            WriteAtomicPreservingMode(claudeConfigPath, cleaned);
            return CleanOutcome.Cleaned;
        } catch (Exception ex) {
            error = ex.Message;
            return CleanOutcome.Failed;
        }
    }

    static void WriteAtomicPreservingMode(string path, string content) {
        var options = new FileStreamOptions {
            Mode   = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share  = FileShare.None
        };
        if (!OperatingSystem.IsWindows()) {
            UnixFileMode mode;
            try {
                mode = File.Exists(path) ? File.GetUnixFileMode(path)
                                         : UnixFileMode.UserRead | UnixFileMode.UserWrite;
            } catch {
                mode = UnixFileMode.UserRead | UnixFileMode.UserWrite; // unreadable → fail safe to owner-only
            }
            options.UnixCreateMode = mode;
        }

        var tmp = path + ".tmp-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N");
        try {
            using (var stream = new FileStream(tmp, options))
            using (var writer = new StreamWriter(stream))
                writer.Write(content);
            if (OperatingSystem.IsWindows()) {
                // File.Replace (ReplaceFile semantics) preserves the DESTINATION's ACL and
                // attributes; Move(overwrite) would publish the temp file's inherited DACL
                // over an explicitly-restricted config ACL.
                File.Replace(tmp, path, destinationBackupFileName: null);
            } else {
                File.Move(tmp, path, overwrite: true);
            }
        } catch {
            try { File.Delete(tmp); } catch { /* best-effort */ }
            throw;
        }
    }

    static async Task<int> AuditStalePathsAsync(TextWriter output, string claudeConfigPath,
                                                IReadOnlyList<RegistrationFile> jsonRegistrations,
                                                string? codexConfigPath,
                                                string? nativeBinaryPath) {
        var issues = 0;

        // Claude's own config participates in the stale scan too (top-level scope).
        IEnumerable<RegistrationFile> jsonFiles =
            [.. jsonRegistrations, new("Claude", claudeConfigPath, "mcpServers")];

        foreach (var file in jsonFiles) {
            if (!File.Exists(file.Path)) continue;

            string json;
            try { json = await File.ReadAllTextAsync(file.Path); } catch { continue; }

            foreach (var (name, command) in McpRegistrationAudit.FindAbsoluteKcapCommands(json, file.BlockKey))
                issues += await ReportStaleAsync(output, file.Label, file.Path, name, command, nativeBinaryPath);
        }

        if (codexConfigPath is not null) {
            foreach (var (name, command) in CodexConfigToml.ReadMcpServerCommands(codexConfigPath)) {
                if (!KcapMcpServers.All.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))) continue;
                if (!McpRegistrationAudit.IsAbsoluteKcapBinaryPath(command)) continue;
                issues += await ReportStaleAsync(output, "Codex", codexConfigPath, name, command, nativeBinaryPath);
            }
        }

        return issues;
    }

    static async Task<int> ReportStaleAsync(TextWriter output, string label, string configPath,
                                            string name, string command, string? nativeBinaryPath) {
        if (!File.Exists(command)) {
            await output.WriteLineAsync(
                $"  stale MCP registration '{name}' ({label}) in {configPath}: {command} no longer exists — re-run `kcap setup`");
            return 1;
        }

        if (nativeBinaryPath is not null &&
            !string.Equals(command, nativeBinaryPath, StringComparison.Ordinal)) {
            await output.WriteLineAsync(
                $"  outdated MCP registration '{name}' ({label}) in {configPath}: points at {command}, " +
                $"current binary is {nativeBinaryPath} (healed by the next `kcap setup` or `kcap update`)");
            return 1;
        }

        return 0;
    }
}
