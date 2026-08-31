using Capacitor.Cli.Core.Dsh;

namespace Capacitor.Cli.Core.Harness.Dsh;

/// <summary>
/// DeepSeek Harness (dsh) as this process sees it. dsh is an ingested Cordis-based agent: it ships
/// no shell hooks, so kcap's live capture is the Cordis observer plugin
/// (<see cref="DshExtensionInstaller"/>) written under the dsh home. Detection mirrors the old
/// harness catalog exactly — the dsh home marks it installed, the installed plugin marks it wired,
/// and the <c>dsh</c> binary covers a fresh install seen on PATH.
/// </summary>
public sealed class DshHarness : IHarness<DshHarness> {
    // DSH_HOME is resolved ONCE at build time (mirroring the other vendors' env-read-once contract);
    // the disk-stat questions below stay lazy so a long-lived holder still sees a later install.
    readonly string? _home;
    readonly string? _dshHome;

    DshHarness(string? home, string? dshHome) {
        _home    = home;
        _dshHome = dshHome;
    }

    /// <summary>Resolves the one override dsh honours: <c>DSH_HOME</c> replaces the <c>~/.dsh</c>
    /// root outright.</summary>
    public static DshHarness FromEnvironment(UserHome home) =>
        new(home.Path, Environment.GetEnvironmentVariable("DSH_HOME"));

    public static HarnessId Id    => HarnessId.Dsh;
    public static string    Label => "DeepSeek Harness";

    // The dsh home is the marker; the PATH probe covers a fresh install. Wired = kcap's Cordis
    // plugin is present in the dsh home (the same check the old HarnessCatalog entry made).
    public HarnessSignals Signals => new() {
        Binaries  = ["dsh"],
        Installed = () => DshPaths.IsInstalledPure(_home, _dshHome),
        Wired     = () => DshExtensionInstaller.IsInstalled(DshPaths.KcapPluginPure(_home, _dshHome)),
    };
}
