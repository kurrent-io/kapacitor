using Capacitor.Cli.Core;

namespace Capacitor.Tests.Helpers.Guards;

/// <summary>
/// Assembly-level safety net for daemon lock/PID paths.
///
/// <para><see cref="DaemonLockPaths"/> deliberately ignores <c>KCAP_CONFIG_DIR</c> and pins its
/// directory under the real home (<c>~/.config/kcap/daemons/</c>), and the default daemon name is the
/// OS username — so the per-test <c>OverrideDirectoryForTesting</c> seam is not enough: any window
/// where the process-wide override is <c>null</c> (a teardown reset, a test that never sets it, or a
/// parallel test) exposes the developer's <b>real</b> daemon files. A daemon test that read them once
/// <c>SIGKILL</c>ed the developer's live daemon (and its hosted agents).</para>
///
/// <para>Pinning <see cref="DaemonLockPaths.DaemonsDirEnvVar"/> to a throwaway temp dir for the whole
/// process makes the real directory unreachable regardless of test order, parallelism, or a missing
/// per-test override. Individual tests may still layer their own <c>OverrideDirectoryForTesting</c>
/// on top; when they reset it to <c>null</c> the default now falls back here, never to the real dir.
/// See <c>DaemonPathsIsolationTests</c> for the regression this guards.</para>
/// </summary>
public class DaemonPathsGlobalSetup {
    static readonly TempDir Dir = new();

    public static string SharedDaemonsDir => Dir.Path;

    [BeforeEvery(Assembly)]
    public static void PinDaemonsDir() =>
        Environment.SetEnvironmentVariable(DaemonLockPaths.DaemonsDirEnvVar, SharedDaemonsDir);

    [AfterEvery(Assembly)]
    public static void CleanupDaemonsDir() {
        Environment.SetEnvironmentVariable(DaemonLockPaths.DaemonsDirEnvVar, null);
        Dir.Dispose();
    }
}
