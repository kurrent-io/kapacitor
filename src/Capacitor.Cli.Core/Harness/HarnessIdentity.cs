namespace Capacitor.Cli.Core.Harness;

/// <summary>
/// One harness named, with no machine behind it: what a screen or a payload needs when no home has
/// been resolved and no file is going to be read. Everything that probes a machine takes an
/// <see cref="IHarness"/> instead.
/// </summary>
public sealed record HarnessIdentity(HarnessId Id, string Label) {
    public string VendorId => Id.VendorId;
}
