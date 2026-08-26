namespace Capacitor.Cli.Core;

public static class PathHelpers {
    public static string HomeDirectory {
        get {
            var home = Environment.GetEnvironmentVariable("HOME");
            if (string.IsNullOrWhiteSpace(home) || !Path.IsPathRooted(home))
                home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return home;
        }
    }
}
