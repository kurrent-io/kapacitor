using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Thin delegating shim over <see cref="AgentDetection"/> for CLI call sites that only need a
/// single-binary current-process PATH probe (e.g. <c>PluginCommand</c>'s "is kcap on PATH"
/// precheck). The composed multi-vendor detection used by <c>SetupCommand</c> lives directly in
/// Core — see <see cref="AgentDetection.Detect"/>.
/// </summary>
public static class AgentDetector {
    public static bool IsInstalled(string binaryName) =>
        AgentDetection.BinaryOnPath(binaryName, AgentDetection.FromEnvironment());
}
