namespace Capacitor.Cli.Core;

/// <summary>
/// This process's home directory, resolved at an entry point and passed down. The single home
/// resolution in the codebase, so a module never depends on being at home — it is at home because
/// its composer said so.
///
/// <para>The roots with an environment override of their own (<see cref="ConfigRoot"/>,
/// <see cref="DaemonStore"/>) call <see cref="FromEnvironment"/> themselves; everything else takes
/// the value.</para>
/// </summary>
public sealed class UserHome(string path) {
    /// <summary>The home directory itself. Not guaranteed to exist.</summary>
    public string Path { get; } = path;

    /// <summary>This process's home. Call once, in <c>Main</c> or the composition root.</summary>
    // A rooted $HOME wins: GetFolderPath ignores it on Windows, so consulting it first is what stops
    // one redirected home resolving two ways on that leg.
    public static UserHome FromEnvironment() {
        var home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrWhiteSpace(home) || !System.IO.Path.IsPathRooted(home))
#pragma warning disable RS0030 // the home resolution the ban points every other site at
            home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
#pragma warning restore RS0030

        return new(home);
    }
}
