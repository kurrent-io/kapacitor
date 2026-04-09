using System.Reflection;

namespace kapacitor;

static class EmbeddedResources {
    static readonly Assembly Assembly = typeof(EmbeddedResources).Assembly;

    /// <summary>
    /// Loads an embedded resource by filename (e.g. "help-usage.txt").
    /// </summary>
    internal static string Load(string name) {
        var resourceName = $"kapacitor.Resources.{name}";
        using var stream = Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Tries to load an embedded resource. Returns null if not found.
    /// </summary>
    internal static string? TryLoad(string name) {
        var resourceName = $"kapacitor.Resources.{name}";
        using var stream = Assembly.GetManifestResourceStream(resourceName);
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
