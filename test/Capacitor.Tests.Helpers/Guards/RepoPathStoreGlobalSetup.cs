using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Capacitor.Tests.Helpers.Guards;

/// <summary>
/// Assembly-level setup/teardown for RepoPathStore tests.
///
/// PathHelpers.ConfigDir is static readonly — captured once per process from
/// KCAP_CONFIG_DIR. We must set that env var from a <c>[ModuleInitializer]</c>
/// method, not a TUnit <c>[Before(Assembly)]</c> hook: the runtime guarantees a module
/// initializer runs before ANY type in the module is touched, including before TUnit's
/// own discovery/bootstrap code runs, which can itself trigger the PathHelpers static
/// initializer ahead of any assembly hook. Because the field is static readonly, a read
/// that races ahead of an assembly hook captures the developer's real ~/.config/kcap for
/// the rest of the process's lifetime — this already happened in production. Do not
/// "simplify" this back to [Before(Assembly)].
/// </summary>
public class RepoPathStoreGlobalSetup {
    static readonly TempDir Dir = new();

    public static string SharedConfigDir => Dir.Path;

    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255", Justification =
        "The rule's own point -- a module initializer is for application code -- is what makes it the "
      + "right tool here: this is the only thing that runs before the test host touches PathHelpers.")]
    internal static void SetConfigDir() =>
        Environment.SetEnvironmentVariable("KCAP_CONFIG_DIR", SharedConfigDir);

    [AfterEvery(Assembly)]
    public static void CleanupConfigDir() {
        Environment.SetEnvironmentVariable("KCAP_CONFIG_DIR", null);
        Dir.Dispose();
    }
}
