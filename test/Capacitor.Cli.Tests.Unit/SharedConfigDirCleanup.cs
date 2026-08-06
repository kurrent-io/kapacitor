namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Clearing of the ONE config directory every path-based test in this assembly shares:
/// <c>RepoPathStoreGlobalSetup</c>'s <c>[ModuleInitializer]</c> points <c>KCAP_CONFIG_DIR</c> at a
/// single temp dir for the whole process, and <c>PathHelpers.ConfigDir</c> is <c>static readonly</c>,
/// captured once. So 12+ classes delete the same <c>config.json</c>, <c>tokens.json</c> and
/// <c>tokens/</c>, and any of them can be the one whose hook runs while a handle is still open —
/// which is why the retry belongs here rather than in whichever class is currently failing.
///
/// <para>Two facts worth knowing before changing this. A <c>NotInParallel</c> key cannot help: CI
/// runs this suite with <c>--maximum-parallel-tests 1</c>, so there is no concurrency to serialise,
/// and a lock at hook time therefore means a handle OUTLIVED its owning test (an undisposed stream,
/// or an unreaped child process). And it is Windows-only because Windows refuses to delete a file
/// with an open handle while Unix unlinks regardless — hence also intermittent, since whether the
/// holder has released is timing rather than ordering.</para>
/// </summary>
internal static class SharedConfigDirCleanup {
    const int Attempts = 40;
    const int DelayMs  = 25;

    /// <summary>
    /// Delete with a bounded retry over a transient sharing violation, then throw with a named cause.
    ///
    /// <para>Never swallows a persistent one: these tests assert on token and profile state, and a
    /// stale tokens directory already satisfies "a peer already refreshed it", so a swallowed lock
    /// would be a false pass that hides a regression instead of costing a rerun.</para>
    ///
    /// <para>The delete is the absence oracle — <c>Exists</c> probes report <c>false</c> for access
    /// and some I/O failures too, not only for absence, so they cannot be trusted to mean "gone".</para>
    /// </summary>
    internal static void ClearWithRetry(string what, Action delete) {
        Exception? last = null;

        for (var attempt = 1; attempt <= Attempts; attempt++) {
            try {
                delete();

                return;
            } catch (FileNotFoundException) {
                return; // definitively absent — nothing to clear
            } catch (DirectoryNotFoundException) {
                return; // definitively absent — nothing to clear
            } catch (IOException ex) {
                last = ex; // sharing violation — the holder should release shortly
            } catch (UnauthorizedAccessException ex) {
                last = ex; // how Windows reports a delete blocked by an open handle
            }

            if (attempt < Attempts) Thread.Sleep(DelayMs);
        }

        throw new InvalidOperationException(
            $"Could not clear {what} in the shared KCAP_CONFIG_DIR within {Attempts * DelayMs}ms — " +
            "something still holds it, so this test would run against another test's state.",
            last);
    }

    /// <summary>
    /// Resets the three shared artefacts every token/profile test needs absent, in the order they
    /// depend on each other. Callers add their own class-specific state afterwards.
    /// </summary>
    internal static void ClearTokenAndProfileState(string legacyTokensPath, string tokensDir) {
        ClearWithRetry("the legacy tokens file", () => File.Delete(legacyTokensPath));
        ClearWithRetry("the tokens directory", () => Directory.Delete(tokensDir, recursive: true));
        ClearWithRetry("config.json", () => File.Delete(Capacitor.Cli.Core.Config.AppConfig.GetConfigPath()));

        // Token lookup consults AppConfig.ResolvedProfile, so a value left by another test would
        // redirect reads to a different profile.
        Capacitor.Cli.Core.Config.AppConfig.ResetResolvedStateForTesting();
    }
}
