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
    ///
    /// <para><b>The delete itself is the oracle</b> (review fix, HIGH). An earlier version guarded
    /// with <c>File.Exists</c> / <c>Directory.Exists</c> and returned early when they reported
    /// "absent" — but those return <c>false</c> for access and some I/O failures too, not only for
    /// absence. So the very helper written to refuse false passes could report success over state
    /// that was still present and merely unreadable. Absence is now established only by the delete
    /// operation itself reporting it: a missing file makes <c>File.Delete</c> a no-op, and a missing
    /// directory raises <c>DirectoryNotFoundException</c>.</para>
    ///
    /// <para><b>No GC pass</b> (review fix, MEDIUM). An earlier version ran
    /// <c>GC.Collect(); GC.WaitForPendingFinalizers();</c> after the first failure, claiming it
    /// discriminated an undisposed stream from a live holder. It does not: a child process can close
    /// during the pause, so any apparent effect is confounded by the delay it adds; GC runs
    /// arbitrary finalizers, not uniquely a leaked stream; and it can CONCEAL a genuine
    /// undisposed-handle defect by making it pass. It has been removed rather than reworded — the
    /// message it justified asserted something it could not establish.</para>
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
