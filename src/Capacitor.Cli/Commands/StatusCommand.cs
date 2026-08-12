using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Antigravity;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Copilot;
using Capacitor.Cli.Core.Cursor;
using Capacitor.Cli.Core.Gemini;
using Capacitor.Cli.Core.Kiro;
using Capacitor.Cli.Core.OpenCode;
using Capacitor.Cli.Core.Pi;

namespace Capacitor.Cli.Commands;

public static class StatusCommand {
    public static async Task<int> HandleAsync(string? baseUrl, string[] args) {
        // Version line reuses UpdateNotice's shared check and marks-reported so the exit footer
        // doesn't double-print; respects the same opt-outs.
        await WriteVersionLineAsync(args);

        // Server
        Console.Write("  Server:  ");

        if (baseUrl is null) {
            await Console.Out.WriteLineAsync("not configured");
        } else {
            Console.Write($"{baseUrl} ");

            try {
                // ReSharper disable once ShortLivedHttpClient
                using var http = new HttpClient();
                http.Timeout = TimeSpan.FromSeconds(5);
                var resp = await http.GetAsync($"{baseUrl}/auth/config");
                await Console.Out.WriteLineAsync(resp.IsSuccessStatusCode ? "✓ reachable" : $"✗ HTTP {(int)resp.StatusCode}");
            } catch {
                await Console.Out.WriteLineAsync("✗ unreachable");
            }
        }

        // Auth
        // A machine-credential diversion REPLACES the token-store line rather than appending to it:
        // with KCAP_CLIENT_ID/KCAP_CLIENT_SECRET in the environment, MachineAuth.Intended bypasses
        // the token store entirely, so its state is not what this CLI authenticates with — printing
        // both would show a headless runner as "records as the machine" AND "not authenticated (run:
        // kcap login)", contradictory and with irrelevant remediation.
        var machineLine = MachineAuth.DescribeDiversion(
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(MachineAuth.ClientIdVar)),
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(MachineAuth.ClientSecretVar)));

        if (machineLine is not null) {
            Console.WriteLine($"  Auth:    {machineLine}");
        } else {
            Console.Write("  Auth:    ");
            var tokens = await TokenStore.GetValidTokensAsync();

            if (tokens is not null) {
                var remaining = tokens.ExpiresAt - DateTimeOffset.UtcNow;

                var expiryText = remaining.TotalHours > 1
                    ? $"expires in {remaining.TotalHours:F0}h"
                    : $"expires in {remaining.TotalMinutes:F0}m";
                await Console.Out.WriteLineAsync($"{tokens.GitHubUsername} ({tokens.Provider}) ✓ token valid ({expiryText})");
            } else {
                var rawTokens = await TokenStore.LoadAsync();

                await Console.Out.WriteLineAsync(
                    rawTokens is not null
                        ? $"{rawTokens.GitHubUsername} ({rawTokens.Provider}) ✗ token expired (run: kcap login)"
                        : "not authenticated (run: kcap login)"
                );
            }
        }

        // Hooks
        await Console.Out.WriteAsync("  Hooks:   ");

        var line = BuildHooksStatusLine(
            claude:   IsClaudePluginInstalled(ClaudePaths.UserSettings),
            codex:    IsCodexHooksInstalled(CodexPaths.UserHooksJson),
            cursor:   CursorHooksInstaller.IsInstalled(CursorPaths.UserHooksJson()),
            copilot:  CopilotHooksInstaller.IsInstalled(CopilotPaths.KcapHooksJson()),
            gemini:   GeminiHooksInstaller.IsInstalled(GeminiPaths.SettingsJson()),
            kiro:     KiroHooksInstaller.IsInstalled(KiroPaths.KcapAgentJson()),
            pi:       PiExtensionInstaller.IsInstalled(PiPaths.KcapExtension()),
            opencode: OpenCodeExtensionInstaller.IsInstalled(OpenCodePaths.KcapPlugin()),
            antigravity: AntigravityHooksInstaller.IsInstalled(AntigravityPaths.GlobalHooksJson()));

        await Console.Out.WriteLineAsync(line);

        // Daemon: read per-name PID files under
        // ~/.config/kcap/daemons/ instead of the legacy singleton
        // at ~/.config/kcap/agent.pid. The top-level `kcap status`
        // must agree with `kcap daemon status`; previously this
        // command kept saying "not running" while `daemon status` reported
        // a healthy daemon because new daemons no longer write the legacy
        // singleton.
        Console.Write("  Daemon:  ");
        await WriteAgentStatusAsync();

        return 0;
    }

    static async Task WriteVersionLineAsync(string[] args) {
        Console.Write("  Version: ");

        var current = CapacitorVersion.CurrentDisplay();

        // Opt-out: an explicit --no-update-check flag or a disabled profile setting means no
        // check is performed at all (never force one the user turned off) — the line still
        // prints the bare version.
        if (args.Contains("--no-update-check")) {
            await Console.Out.WriteLineAsync(FormatVersionLine(current, default));

            return;
        }

        var profile = await AppConfig.GetActiveProfileAsync();

        if (profile?.UpdateCheck == false) {
            await Console.Out.WriteLineAsync(FormatVersionLine(current, default));

            return;
        }

        var channel  = UpdateCommand.ResolveChannel(args, profile?.UpdateChannel);
        var result   = await UpdateNotice.GetSharedCheckAsync(channel);

        // Cap the recommendation at the connected server's version (min(npm latest, server)).
        var advisory = UpdateAdvisoryResolver.Resolve(result, channel);

        await Console.Out.WriteLineAsync(FormatVersionLine(current, advisory));

        if (advisory.Newer) {
            // Surfaced inline already — the exit-time footer (UpdateNotice.FlushAsync) must not
            // print the same information a second time.
            UpdateNotice.MarkReported();
        }
    }

    /// <summary>
    /// Pure formatting for the Version line: <c>kcap {current}</c>, with an inline
    /// <c>(update available: {target})</c> annotation appended only when <paramref name="advisory"/>
    /// reports a newer version — and, when the target was capped at the server's version, a
    /// <c>, server version</c> marker. Split out from <see cref="WriteVersionLineAsync"/> so the exact
    /// text is unit-testable without any I/O.
    /// </summary>
    internal static string FormatVersionLine(string current, UpdateAdvisory advisory) =>
        advisory is { Newer: true, Target: { } target }
            ? advisory.ServerCapped
                ? $"kcap {current} (update available: {target}, server version)"
                : $"kcap {current} (update available: {target})"
            : $"kcap {current}";

    static async Task WriteAgentStatusAsync() {
        if (!Directory.Exists(DaemonLockPaths.Directory)) {
            await Console.Out.WriteLineAsync("not running");

            return;
        }

        var pidFiles = Directory.EnumerateFiles(DaemonLockPaths.Directory, "*.pid")
            .OrderBy(f => f)
            .ToList();

        if (pidFiles.Count == 0) {
            await Console.Out.WriteLineAsync("not running");

            return;
        }

        var entries = new List<(string Name, int Pid, bool Alive)>(pidFiles.Count);

        foreach (var pidFile in pidFiles) {
            var name = Path.GetFileNameWithoutExtension(pidFile);

            if (string.IsNullOrEmpty(name)) continue;

            var firstLine = (await File.ReadAllTextAsync(pidFile))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();

            if (!int.TryParse(firstLine, out var pid)) continue;

            var alive = false;

            try {
                System.Diagnostics.Process.GetProcessById(pid);
                alive = true;
            } catch (ArgumentException) {
                // process gone; treated as stale below
            }

            entries.Add((name, pid, alive));
        }

        var live = entries.Where(e => e.Alive).ToList();

        switch (live.Count) {
            case 0:
                await Console.Out.WriteLineAsync(
                    entries.Count == 0
                        ? "not running"
                        : "not running (stale PID files; `kcap daemon doctor --clean` to remove)"
                );

                return;
            case 1:
                await Console.Out.WriteLineAsync($"running — {live[0].Name} (PID {live[0].Pid})");

                return;
            default: {
                var summary = string.Join(", ", live.Select(e => $"{e.Name} (PID {e.Pid})"));
                await Console.Out.WriteLineAsync($"running ({live.Count}) — {summary}");

                break;
            }
        }
    }

    /// <summary>
    /// Renders the Hooks status line for every supported agent. Gemini merges its
    /// hooks into the shared <c>~/.gemini/settings.json</c>, while Pi and OpenCode
    /// track a live-ingest "extension"/plugin file rather than a hooks file (neither
    /// has shell hooks), but all share the line for at-a-glance parity. Pure — the
    /// I/O detection happens in the caller so this stays unit-testable.
    /// </summary>
    internal static string BuildHooksStatusLine(bool claude, bool codex, bool cursor, bool copilot, bool gemini, bool kiro, bool pi, bool opencode, bool antigravity = false) =>
        string.Join("  ", new[] {
            claude   ? "Claude ✓"   : "Claude ✗",
            codex    ? "Codex ✓"    : "Codex ✗",
            cursor   ? "Cursor ✓"   : "Cursor ✗",
            copilot  ? "Copilot ✓"  : "Copilot ✗",
            gemini   ? "Gemini ✓"   : "Gemini ✗",
            kiro     ? "Kiro ✓"     : "Kiro ✗",
            pi       ? "Pi ✓"       : "Pi ✗",
            opencode ? "OpenCode ✓" : "OpenCode ✗",
            antigravity ? "Antigravity ✓" : "Antigravity ✗"
        });

    /// <summary>
    /// True iff <paramref name="settingsPath"/> exists and has
    /// <c>enabledPlugins["kcap@kcap"] == true</c>.
    /// </summary>
    public static bool IsClaudePluginInstalled(string settingsPath) {
        try {
            if (!File.Exists(settingsPath)) return false;
            if (JsonNode.Parse(File.ReadAllText(settingsPath)) is not JsonObject root) return false;
            if (root["enabledPlugins"] is not JsonObject enabled) return false;

            return enabled["kcap@kcap"]?.GetValue<bool>() == true;
        } catch {
            return false;
        }
    }

    /// <summary>
    /// True iff <paramref name="hooksPath"/> exists and any hook entry under any
    /// event references the <c>kcap codex-hook</c> command.
    /// </summary>
    public static bool IsCodexHooksInstalled(string hooksPath) {
        try {
            if (!File.Exists(hooksPath)) return false;
            if (JsonNode.Parse(File.ReadAllText(hooksPath)) is not JsonObject root) return false;
            if (root["hooks"] is not JsonObject hooks) return false;

            foreach (var (_, value) in hooks) {
                if (value is not JsonArray entries) continue;

                if (entries.Any(CodexHooksParser.EntryReferencesCapacitorCodexHook)) {
                    return true;
                }
            }

            return false;
        } catch {
            return false;
        }
    }
}
