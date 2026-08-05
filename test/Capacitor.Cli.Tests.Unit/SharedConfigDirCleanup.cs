namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Clearing of the ONE config directory every path-based test in this assembly shares.
///
/// <para><b>Why this is shared rather than per-class.</b> <c>RepoPathStoreGlobalSetup</c>'s
/// <c>[Before(Assembly)]</c> hook points <c>KCAP_CONFIG_DIR</c> at a single temp directory for the
/// whole process, and <c>PathHelpers.ConfigDir</c> is <c>static readonly</c> — captured once, on
/// first touch. So every class that resets token/profile state is deleting the SAME
/// <c>config.json</c>, <c>tokens.json</c> and <c>tokens/</c>, and any of them can be the one whose
/// <c>[Before(Test)]</c> hook happens to run while a handle is still open.</para>
///
/// <para>That is why this lives here. The previous round of this fix hardened the single class that
/// was failing at the time, and the identical exception resurfaced in the next class to touch the
/// same files. Hardening the resource is what stops the sequence; hardening a class only moves it.
/// A <c>NotInParallel</c> key cannot help either way: CI runs this suite with
/// <c>--maximum-parallel-tests 1</c>, so there is no concurrency to serialise, and a lock at
/// <c>[Before(Test)]</c> time therefore means a handle OUTLIVED its owning test — an undisposed
/// stream awaiting finalization, or a child process (watcher/daemon) not yet reaped.</para>
///
/// <para><b>Windows-only by nature.</b> Windows refuses to delete a file with an open handle; Unix
/// unlinks regardless, so the identical leak is invisible there. Intermittent for the same reason:
/// whether the holder has released by the time the next hook runs is timing, not ordering.</para>
/// </summary>
internal static class SharedConfigDirCleanup {
    const int Attempts = 40;
    const int DelayMs  = 25;

    /// <summary>
    /// Delete with a bounded retry over a TRANSIENT sharing violation, then throw with a named
    /// cause.
    ///
    /// <para>Deliberately does not swallow a persistent one. These tests assert on token and profile
    /// state, so running against leftovers can pass for the WRONG reason — a stale tokens directory
    /// already satisfies "a peer already refreshed it". A false pass is worse than the flake,
    /// because it hides a real regression instead of costing a rerun.</para>
    /// </summary>
    internal static void ClearWithRetry(string what, Action delete, Func<bool> exists) {
        Exception? last = null;

        for (var attempt = 1; attempt <= Attempts; attempt++) {
            if (!exists()) return;

            try {
                delete();

                return;
            } catch (IOException ex) {
                last = ex; // sharing violation — the holder should release shortly
            } catch (UnauthorizedAccessException ex) {
                last = ex; // how Windows reports a delete blocked by an open handle
            }

            if (attempt == 1) {
                // Only an in-process undisposed stream can be released this way, so whether this
                // helps discriminates the two candidate causes: if the retry budget stops being
                // exhausted after this change, the holder was an unreferenced stream awaiting
                // finalization; if failures continue, it is a live holder (a child process) and the
                // owner is still running. Cheap, and it runs once rather than every attempt.
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            if (attempt < Attempts) Thread.Sleep(DelayMs);
        }

        // Re-check: the holder may have released after the last failed attempt.
        if (exists()) {
            throw new InvalidOperationException(
                $"Could not clear {what} in the shared KCAP_CONFIG_DIR within {Attempts * DelayMs}ms "  +
                "(a GC + finalizer pass was attempted, so an unreferenced undisposed stream is ruled " +
                "out) — something still holds it, so this test would run against another test's state.",
                last);
        }
    }

    /// <summary>
    /// Resets the three shared artefacts every token/profile test needs absent, in the order they
    /// depend on each other. Callers add their own class-specific state afterwards.
    /// </summary>
    internal static void ClearTokenAndProfileState(string legacyTokensPath, string tokensDir) {
        ClearWithRetry("the legacy tokens file", () => File.Delete(legacyTokensPath), () => File.Exists(legacyTokensPath));
        ClearWithRetry("the tokens directory", () => Directory.Delete(tokensDir, recursive: true), () => Directory.Exists(tokensDir));

        var cfg = Capacitor.Cli.Core.Config.AppConfig.GetConfigPath();

        ClearWithRetry("config.json", () => File.Delete(cfg), () => File.Exists(cfg));

        // Token lookup consults AppConfig.ResolvedProfile, so a value left by another test would
        // redirect reads to a different profile.
        Capacitor.Cli.Core.Config.AppConfig.ResetResolvedStateForTesting();
    }
}
