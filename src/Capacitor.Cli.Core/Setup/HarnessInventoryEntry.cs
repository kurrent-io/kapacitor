namespace Capacitor.Cli.Core.Setup;

/// <summary>One vendor's line in a <see cref="HarnessInventory"/>: is the harness installed on this
/// machine, and is kcap wired into it.</summary>
public sealed record HarnessInventoryEntry(bool Detected, bool Wired);
