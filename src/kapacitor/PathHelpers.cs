namespace kapacitor;

static class PathHelpers {
    public static string HomeDirectory => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string ConfigPath(string name) => Path.Combine(HomeDirectory, ".config", "kapacitor", name);
}
