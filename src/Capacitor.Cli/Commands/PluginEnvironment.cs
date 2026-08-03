using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Antigravity;
using Capacitor.Cli.Core.Copilot;
using Capacitor.Cli.Core.Cursor;
using Capacitor.Cli.Core.Gemini;
using Capacitor.Cli.Core.Kiro;
using Capacitor.Cli.Core.OpenCode;
using Capacitor.Cli.Core.Pi;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Process-state seam for <see cref="PluginCommand"/>. Captures the values that
/// <c>kcap plugin install/remove</c> would otherwise read from
/// <see cref="Environment"/> / <see cref="Console"/>, so tests can supply
/// fakes without mutating shared process state.
///
/// <see cref="ResolvePluginPath"/> is a delegate (not a string) so the
/// filesystem probing in <see cref="SetupCommand.ResolvePluginPath(string?)"/>
/// only runs on the install branches that actually need it — not on
/// <c>remove</c>, <c>--cursor</c>, or early-exit invocations.
/// </summary>
public sealed record PluginEnvironment(
    string         HomeDirectory,
    Func<string?>  ResolvePluginPath,
    TextWriter     Stdout,
    TextWriter     Stderr
) {
    /// <summary>
    /// Resolves the native binary path written as the <c>command</c> of generated MCP
    /// registrations. Null → the production default (<see cref="Environment.ProcessPath"/>, see
    /// <c>KcapBinaryCommand</c>). Tests inject a deterministic path so assertions never bless
    /// whatever executable happens to be running the test.
    /// </summary>
    public Func<string?>? ResolveMcpBinaryPath { get; init; }
    public string ClaudeHome          => ClaudePaths.Home(HomeDirectory);
    public string ClaudeUserSettings  => Path.Combine(ClaudeHome, "settings.json");
    public string CodexHome           => CodexPaths.Home(HomeDirectory);
    public string CodexUserHooksJson  => Path.Combine(CodexHome, "hooks.json");
    public string CodexConfigTomlPath => Path.Combine(CodexHome, "config.toml");
    public string CursorUserHooksJson => CursorPaths.UserHooksJson(HomeDirectory);
    public string CursorMcpJson       => CursorPaths.UserMcpJson(HomeDirectory);
    public string CopilotKcapHooksJson => CopilotPaths.KcapHooksJson(HomeDirectory);
    public string CopilotMcpConfigJson => CopilotPaths.McpConfigJson(HomeDirectory);
    public string CopilotInstructionsMd => CopilotPaths.InstructionsMd(HomeDirectory);
    public string GeminiSettingsJson   => GeminiPaths.SettingsJson(HomeDirectory);
    public string GeminiInstructionsMd => GeminiPaths.GeminiMd(HomeDirectory);
    public string KiroKcapAgentJson    => KiroPaths.KcapAgentJson(HomeDirectory);
    public string KiroSettingsJson     => KiroPaths.SettingsFile(HomeDirectory);
    public string KiroMcpJson          => KiroPaths.SettingsMcpJson(HomeDirectory);
    public string KiroSkillsDir        => KiroPaths.SkillsDir(HomeDirectory);
    public string PiKcapExtension      => PiPaths.KcapExtension(HomeDirectory);
    public string PiKcapMcpExtension   => PiPaths.KcapMcpExtension(HomeDirectory);
    public string PiAgentsMd           => PiPaths.AgentsMd(HomeDirectory);
    public string OpenCodeKcapPlugin    => OpenCodePaths.KcapPlugin(HomeDirectory);
    public string OpenCodeMcpConfigJson => OpenCodePaths.McpConfigJson(HomeDirectory);
    public string OpenCodeAgentsMd      => OpenCodePaths.AgentsMd(HomeDirectory);
    public string AntigravityHooksJson  => AntigravityPaths.GlobalHooksJson(HomeDirectory);
    public string AntigravityMcpConfigJson  => AntigravityPaths.McpConfigJson(HomeDirectory);
    public string AntigravityInstructionsMd => AntigravityPaths.InstructionsMd(HomeDirectory);
    public string AntigravitySkillsDir      => AntigravityPaths.SkillsDir(HomeDirectory);
    public string AgentsSkillsDir     => Path.Combine(HomeDirectory, ".agents", "skills");
    public string LegacyCodexSkills   => Path.Combine(CodexHome, "skills");

    public static PluginEnvironment FromProcess() => new(
        HomeDirectory:     PathHelpers.HomeDirectory,
        ResolvePluginPath: () => SetupCommand.ResolvePluginPath(),
        Stdout:            Console.Out,
        Stderr:            Console.Error
    );
}
