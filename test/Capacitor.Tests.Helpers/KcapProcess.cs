using System.Diagnostics;
using System.Reflection;
using Capacitor.Cli.Core;

namespace Capacitor.Tests.Helpers;

/// <summary>
/// Starts the built <c>kcap</c> binary from its own output directory, which the Helpers project
/// stamps in at compile time. The <see cref="DaemonStore"/> is required rather than optional: a
/// child inherits nothing in-process, and unpinned it finds the real daemons directory.
/// </summary>
public static class KcapProcess {
    public static string BinaryPath { get; } = Path.Combine(
        typeof(KcapProcess).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(a => a.Key == "KcapBinaryDir").Value!,
        OperatingSystem.IsWindows() ? "kcap.exe" : "kcap");

    /// <summary>The caller adds its own working directory and environment.</summary>
    public static ProcessStartInfo StartInfo(DaemonStore store, params ReadOnlySpan<string> args) {
        var psi = new ProcessStartInfo(BinaryPath) {
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        foreach (var arg in args) psi.ArgumentList.Add(arg);
        psi.Environment[DaemonStore.DaemonsDirEnvVar] = store.Directory;

        return psi;
    }
}
