using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Tests.Helpers;

/// <summary>
/// <see cref="ProfileContext"/> values for tests that construct a command directly instead of going
/// through <c>Program.cs</c>'s resolution.
/// </summary>
public static class Resolutions {
    /// <summary>Nothing resolved — the state a real process is in before it resolves, and the one a
    /// <c>--server-url</c> / <c>KCAP_URL</c> override genuinely produces, since the resolver selects
    /// no profile there. Per-profile settings come from the active profile in
    /// <paramref name="config"/>, read once here exactly as production reads it once.</summary>
    public static ProfileContext None(ConfigRoot config) =>
        new(new(null, null, null, null), AppConfig.LoadProfileConfig(config).GetAwaiter().GetResult());

    /// <summary>A server URL and nothing else — the shape a <c>--server-url</c> or <c>KCAP_URL</c>
    /// override genuinely produces, and the one a test pointing a command at its own stub server is
    /// modelling. Per-profile settings still come from the active profile in
    /// <paramref name="config"/>.</summary>
    /// <param name="source">What supplied the URL. Only remediation text reads it, so it defaults to
    /// the profile — pass it when the assertion is about which input a diagnostic names.</param>
    public static ProfileContext At(string serverUrl, ConfigRoot config, UrlSource source = UrlSource.Profile) =>
        new(new(serverUrl, null, null, null, source), AppConfig.LoadProfileConfig(config).GetAwaiter().GetResult());

    /// <summary>A resolution naming <paramref name="profile"/>, for a test that wants a per-profile
    /// setting honoured without writing config.json. No disk read: a named profile answers on its own.</summary>
    public static ProfileContext Of(Profile profile, string name = "default", string? serverUrl = null) =>
        new(new(serverUrl ?? profile.ServerUrl, name, profile, null), new ProfileConfig());
}
