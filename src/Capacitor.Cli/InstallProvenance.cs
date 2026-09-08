namespace Capacitor.Cli;

/// Whether this CLI is the copy inside the Kurrent Capacitor app bundle. A bundled CLI is updated
/// by the app, so npm-oriented update surfaces switch themselves off on this answer.
public static class InstallProvenance {
    static readonly Lazy<bool> Cached = new(() => IsAppBundled(Environment.ProcessPath, File.Exists));

    public static bool IsAppBundled() => Cached.Value;

    internal static bool IsAppBundled(string? processPath, Func<string, bool> fileExists) {
        if (string.IsNullOrEmpty(processPath)) return false;

        var macos = Path.GetDirectoryName(processPath);
        if (macos is null || Path.GetFileName(macos) != "MacOS") return false;

        var contents = Path.GetDirectoryName(macos);
        if (contents is null || Path.GetFileName(contents) != "Contents") return false;

        var bundle = Path.GetDirectoryName(contents);
        if (bundle is null || !bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) return false;

        return fileExists(Path.Combine(contents, "Info.plist"));
    }
}
