using System.Runtime.CompilerServices;

namespace Capacitor.Tests.Helpers.Guards;

// Linked into every test assembly by test/Directory.Build.props, never compiled into this one:
// TUnit finds hooks only in an assembly it has loaded, so the hook that loads the guards cannot
// live in the assembly being loaded. Running the class constructors also runs this assembly's
// [ModuleInitializer], which has to beat the first read of PathHelpers.ConfigDir — a static
// readonly captured once per process, otherwise pinned to the developer's real ~/.config/kcap.
public static class GuardsBootstrap {
    [Before(TestDiscovery)]
    public static void LoadGuards() {
        RuntimeHelpers.RunClassConstructor(typeof(RepoPathStoreGlobalSetup).TypeHandle);
        RuntimeHelpers.RunClassConstructor(typeof(DaemonPathsGlobalSetup).TypeHandle);
        RuntimeHelpers.RunClassConstructor(typeof(McpMarkerGlobalSetup).TypeHandle);
        RuntimeHelpers.RunClassConstructor(typeof(AuthProviderCacheGlobalSetup).TypeHandle);
    }
}
