namespace Capacitor.Tests.Helpers;

/// <summary>
/// Injects a throwaway directory into a test-class property, so the class neither owns nor disposes
/// one. Named after the test class unless given a hint.
/// </summary>
public abstract class TempFixtureAttribute<T>(string? hint) : Attribute, IDataSourceAttribute {
    /// <summary>Fixture lifetime; one per test by default. Anything wider shares a directory with
    /// concurrently running tests, so it fits a read-only fixture only.</summary>
    public SharedType Shared { get; init; } = SharedType.None;

    /// <summary>Sharing key, required by <see cref="SharedType.Keyed"/> and ignored otherwise.</summary>
    public string Key { get; init; } = "";

    public bool SkipIfEmpty { get; set; }
    public bool DeferEnumeration { get; set; }

    protected abstract T Create(string hint);

    public async IAsyncEnumerable<Func<Task<object?[]?>>> GetDataRowsAsync(DataGeneratorMetadata metadata) {
        var name = hint ?? TempDir.Stem(metadata.TestInformation.Class.Type.Name);

        yield return () => Task.FromResult<object?[]?>(
            [SharedDataSources.GetOrCreate(Shared, metadata, Key, () => Create(name))]);

        await Task.CompletedTask; // one row; the interface is async-shaped
    }
}

/// <summary><c>[TempDir] public required TempDir Tmp { get; init; }</c></summary>
public sealed class TempDirAttribute(string? hint = null) : TempFixtureAttribute<TempDir>(hint) {
    protected override TempDir Create(string name) => new(name);
}

/// <summary><c>[TempConfigRoot] public required TempConfigRoot Config { get; init; }</c></summary>
public sealed class TempConfigRootAttribute(string? hint = null)
        : TempFixtureAttribute<TempConfigRoot>(hint) {
    protected override TempConfigRoot Create(string name) => new(name);
}

/// <summary><c>[TempHome] public required TempHome Home { get; init; }</c></summary>
public sealed class TempHomeAttribute(string? hint = null) : TempFixtureAttribute<TempHome>(hint) {
    protected override TempHome Create(string name) =>
        Shared == SharedType.None
            ? new(name)
            // A shared home is sound only for a class that never writes into it, and nothing here
            // proves that yet: one directory handed to concurrently running tests is the
            // passes-alone-interferes-in-parallel bug this fixture exists to remove.
            : throw new NotSupportedException(
                "TempHome is per-test: a shared lifetime needs a write check that does not exist yet.");
}

/// <summary><c>[TempDaemonPaths] public required TempDaemonPaths Daemons { get; init; }</c></summary>
public sealed class TempDaemonPathsAttribute(string? hint = null)
        : TempFixtureAttribute<TempDaemonStore>(hint) {
    protected override TempDaemonStore Create(string name) => new(name);
}
