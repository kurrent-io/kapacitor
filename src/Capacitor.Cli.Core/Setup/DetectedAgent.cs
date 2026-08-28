namespace Capacitor.Cli.Core.Setup;

/// <summary>
/// One vendor's two independent detection signals: a PATH binary probe and a filesystem
/// install-marker probe (a vendor's <c>*Paths.IsInstalled</c>). <see cref="Detected"/> is the
/// OR that <c>SetupCommand</c>'s wizard actually consumes — most vendors need both, because a
/// fresh install has no on-disk state yet and an IDE-launched vendor has no CLI on PATH.
/// </summary>
public sealed record DetectedAgent(bool BinaryFound, bool InstallSignalFound) {
    public bool Detected => BinaryFound || InstallSignalFound;
}
