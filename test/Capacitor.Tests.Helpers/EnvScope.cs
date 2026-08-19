namespace Capacitor.Tests.Helpers;

/// <summary>
/// Sets — or clears, with a null value — an environment variable for the test's lifetime, restoring
/// the previous value on dispose.
/// </summary>
/// <remarks>
/// Vendor path resolvers consult their own env vars (<c>COPILOT_HOME</c>, <c>GEMINI_CLI_HOME</c>,
/// <c>PI_CODING_AGENT_DIR</c>, <c>XDG_CONFIG_HOME</c>) before falling back to the home they are
/// handed, so a test that seeds a fake home without clearing them writes into the developer's real
/// config instead. Env is process-global: callers need <c>[NotInParallel]</c>.
/// </remarks>
public sealed class EnvScope : IDisposable {
    readonly string  _key;
    readonly string? _previous;

    public EnvScope(string key, string? value) {
        _key      = key;
        _previous = Environment.GetEnvironmentVariable(key);
        Environment.SetEnvironmentVariable(key, value);
    }

    public void Dispose() => Environment.SetEnvironmentVariable(_key, _previous);
}
