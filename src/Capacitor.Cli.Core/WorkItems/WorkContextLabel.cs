namespace Capacitor.Cli.Core.WorkItems;

/// The server composes a keyed item's label as "KEY — title". A label without that separator, or
/// with an empty half, is display text alone: never a guessed key.
public static class WorkContextLabel {
    const string Separator = " — ";

    public static (string? Key, string Display) Split(string label) {
        var at = label.IndexOf(Separator, StringComparison.Ordinal);
        if (at < 0) return (null, label.Trim());

        var key     = label[..at].Trim();
        var display = label[(at + Separator.Length)..].Trim();

        return key.Length == 0 || display.Length == 0 ? (null, label.Trim()) : (key, display);
    }
}
