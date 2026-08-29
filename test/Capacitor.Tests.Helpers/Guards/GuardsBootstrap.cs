using System.Runtime.CompilerServices;

namespace Capacitor.Tests.Helpers.Guards;

// Linked into every test assembly by test/Directory.Build.props, never compiled into this one:
// TUnit finds hooks only in an assembly it has loaded, so the hook that loads the guards cannot
// live in the assembly being loaded.
public static class GuardsBootstrap {
    [Before(TestDiscovery)]
    public static void LoadGuards() {
        RuntimeHelpers.RunClassConstructor(typeof(ConfigDirGlobalSetup).TypeHandle);
        RuntimeHelpers.RunClassConstructor(typeof(DaemonPathsGlobalSetup).TypeHandle);
        RuntimeHelpers.RunClassConstructor(typeof(ProfileOverridesGlobalSetup).TypeHandle);
        RuntimeHelpers.RunClassConstructor(typeof(GitConfigGlobalSetup).TypeHandle);
    }
}
