namespace Capacitor.Tests.Helpers;

/// <summary>
/// Sets — or clears, with a null value — an environment variable for the test's lifetime, restoring
/// the previous value on dispose.
/// </summary>
/// <remarks>
/// Vendor path resolvers consult their own env vars (<c>COPILOT_HOME</c>, <c>GEMINI_CLI_HOME</c>,
/// <c>PI_CODING_AGENT_DIR</c>, <c>XDG_CONFIG_HOME</c>) before falling back to the home they are
/// handed, so a test that seeds a fake home without clearing them writes into the developer's real
/// config instead.
/// <para>
/// Env is process-global, so the required <c>[NotInParallel]</c> is checked here rather than asked
/// for in prose: the constructor needs some constraint, and <see cref="Exclusive"/> needs an
/// unkeyed one — for variables whose readers are not an enumerable cohort, because every spawned
/// child inherits them and production path helpers read them.
/// </para>
/// </remarks>
public sealed class EnvScope : IDisposable {
    readonly string  _key;
    readonly string? _previous;

    /// <summary>
    /// For a variable whose every reader carries the same key. Requires the test to be
    /// <c>[NotInParallel]</c>, keyed or bare.
    /// </summary>
    public EnvScope(string key, string? value) : this(key, value, exclusive: false) { }

    /// <summary>
    /// For a variable read outside any enumerable cohort — inherited by spawned children, or read
    /// by a production path helper. Requires a bare <c>[NotInParallel]</c>, which is exclusive
    /// against the whole assembly.
    /// </summary>
    public static EnvScope Exclusive(string key, string? value) => new(key, value, exclusive: true);

    EnvScope(string key, string? value, bool exclusive) {
        RequireExclusion(key, exclusive);

        _key      = key;
        _previous = Environment.GetEnvironmentVariable(key);
        Environment.SetEnvironmentVariable(key, value);
    }

    public void Dispose() => Environment.SetEnvironmentVariable(_key, _previous);

    static void RequireExclusion(string key, bool exclusive) {
        var context = TestContext.Current
                   ?? throw new InvalidOperationException(
                          $"EnvScope needs a running test to read the parallel constraints for '{key}'. "
                        + "A process-wide pin that must be in place before any test runs belongs in "
                        + "Guards/, setting the variable directly.");

        var constraints = context.Parallelism.Constraints;
        // The engine takes the FIRST NotInParallelConstraint, and a method-level attribute precedes
        // the class-level one — so a method key shadows a class bare. Resolving the same way is what
        // makes a shadowed attribute visible here instead of blessed.
        var notInParallel = constraints.OfType<NotInParallelConstraint>().FirstOrDefault();
        var parallelGroup = constraints.OfType<ParallelGroupConstraint>().FirstOrDefault();

        if (notInParallel is null)
            throw new InvalidOperationException(
                $"'{key}' is process-global: mark this test [NotInParallel]"
              + (exclusive ? " (bare — no key)." : "."));

        if (!exclusive) return;

        // A NotInParallel test that ALSO carries a ParallelGroup lands in the constrained-group
        // scheduler, not the globally-exclusive bucket, so bare is not enough on its own.
        if (parallelGroup is not null)
            throw new InvalidOperationException(
                $"'{key}' needs assembly-wide exclusion, but [ParallelGroup(\"{parallelGroup.Group}\")] "
              + "puts this test in the constrained-group scheduler instead of the exclusive bucket. "
              + "Drop the group.");

        if (notInParallel.NotInParallelConstraintKeys.Count > 0)
            throw new InvalidOperationException(
                $"'{key}' is process-global: every child process this suite spawns inherits it and "
              + "production path helpers read it, so a keyed [NotInParallel(\""
              + string.Join("\", \"", notInParallel.NotInParallelConstraintKeys)
              + "\")] cannot exclude the tests that observe it. Mark this test bare [NotInParallel].");
    }
}
