namespace kapacitor;

static class PathHelpers {
    public static string HomeDirectory => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    static readonly string ConfigDir = Environment.GetEnvironmentVariable("KAPACITOR_CONFIG_DIR") ?? Path.Combine(HomeDirectory, ".config", "kapacitor");

    public static string ConfigPath(string name) => Path.Combine(ConfigDir, name);
}
