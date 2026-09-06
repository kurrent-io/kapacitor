namespace Capacitor.App.Services;

public enum InstallLocationKind { NotABundle, Applications, UserApplications, DmgVolume, Translocated, Other }

/// Where the running bundle lives. The shim symlink and the LaunchAgent bake the CLI's path, and
/// the updater cannot swap a bundle on a read-only volume, so only an installed copy may proceed.
public static class InstallLocation {
    public static string? BundleRoot(string? processPath) {
        if (string.IsNullOrEmpty(processPath)) return null;
        var index = processPath.IndexOf(".app/", StringComparison.Ordinal);
        return index > 0 ? processPath[..(index + 4)] : null;
    }

    public static InstallLocationKind Classify(string? bundleRoot, string home) {
        if (bundleRoot is null) return InstallLocationKind.NotABundle;

        var root = bundleRoot.TrimEnd('/');
        var slash = root.LastIndexOf('/');
        var parent = slash <= 0 ? "/" : root[..slash];

        if (parent == "/Applications") return InstallLocationKind.Applications;
        if (parent == home.TrimEnd('/') + "/Applications") return InstallLocationKind.UserApplications;
        if (root.StartsWith("/Volumes/", StringComparison.Ordinal)) return InstallLocationKind.DmgVolume;
        if (root.Contains("/AppTranslocation/", StringComparison.Ordinal)) return InstallLocationKind.Translocated;

        return InstallLocationKind.Other;
    }

    public static bool Passes(InstallLocationKind kind) =>
        kind is InstallLocationKind.NotABundle or InstallLocationKind.Applications or InstallLocationKind.UserApplications;
}
