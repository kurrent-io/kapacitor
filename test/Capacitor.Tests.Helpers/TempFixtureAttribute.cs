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
            // Sharing is sound only for a class that never writes into the home, and nothing here
            // checks that yet.
            : throw new NotSupportedException(
                "TempHome is per-test: a shared lifetime needs a write check that does not exist yet.");
}

/// <summary><c>[TempDaemonPaths] public required TempDaemonPaths Daemons { get; init; }</c></summary>
public sealed class TempDaemonPathsAttribute(string? hint = null)
        : TempFixtureAttribute<TempDaemonStore>(hint) {
    protected override TempDaemonStore Create(string name) => new(name);
}

/// <summary><c>[GitRepo] public required GitRepo Repo { get; init; }</c> — for a class whose every
/// test runs against a repository, typically as the working directory of a spawned process.</summary>
public sealed class GitRepoAttribute(string? hint = null) : TempFixtureAttribute<GitRepo>(hint) {
    protected override GitRepo Create(string name) =>
        Shared == SharedType.None
            ? GitRepo.Create(name)
            : throw new NotSupportedException("GitRepo is per-test: a git repository is never a read-only fixture.");
}
