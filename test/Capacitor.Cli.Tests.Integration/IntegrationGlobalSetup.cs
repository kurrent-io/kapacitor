using System.Runtime.CompilerServices;
using Capacitor.Cli.Commands.Harness;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// Assembly-level setup that pins <c>KCAP_CONFIG_DIR</c> to an isolated
/// temp directory before any in-process test code triggers the
/// <c>PathHelpers</c> static initializer. <c>PathHelpers.ConfigDir</c> is
/// <c>static readonly</c> and captured once per process from the
/// environment, so any test that calls into <see cref="ClaudeHookCommand"/>
/// (or anything else that reads <c>AppConfig</c>, profile state, repo
/// exclusions, token store, …) would otherwise read the developer's real
/// <c>~/.config/kcap</c>. A user-side exclusion (e.g. <c>excluded_paths</c>
/// covering <c>/tmp/test</c> or a CI repo path) would then make the test
/// silently emit nothing and pass for the wrong reason.
///
/// The env var is set from a <c>[ModuleInitializer]</c> method,
/// not a TUnit <c>[Before(Assembly)]</c> hook. The runtime guarantees a module
/// initializer runs before ANY type in the module is touched — including
/// before TUnit's own test discovery/bootstrap code runs, which can itself
/// trigger <c>PathHelpers</c>' static initializer ahead of any assembly hook.
/// Because that field is <c>static readonly</c>, it is captured exactly once
/// per process: if anything reads it before this env var is set, the process
/// is permanently pinned to the developer's real <c>~/.config/kcap</c> for
/// its whole lifetime, and no later hook can undo it. This already happened
/// in production — do not "simplify" this back to <c>[Before(Assembly)]</c>.
///
/// Subprocess-based tests (see <see cref="McpSessionsServerTests"/>) set
/// <c>KCAP_CONFIG_DIR</c> on the child process explicitly and are not
/// affected by this parent-process value; this setup just makes the
/// in-process tests as safe as the subprocess-based ones.
/// </summary>
public class IntegrationGlobalSetup {
    static readonly TempDir Tmp = new();
    internal static string SharedConfigDir => Tmp.Path;

    [ModuleInitializer]
    internal static void SetConfigDir() {
        Environment.SetEnvironmentVariable("KCAP_CONFIG_DIR", SharedConfigDir);
    }

    [After(Assembly)]
    public static void CleanupConfigDir() {
        Environment.SetEnvironmentVariable("KCAP_CONFIG_DIR", null);
        Tmp.Dispose();
    }
}
