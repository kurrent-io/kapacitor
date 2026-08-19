namespace Capacitor.Cli.Commands;

/// <summary>
/// Selected import scope, resolved from CLI flags or the interactive picker.
/// </summary>
public abstract record ImportScope {
    /// <summary>
    /// Owner and name compared the way git remotes are: case-insensitively, both halves. Shared so a
    /// scope's own equality and the filter's lookup can never disagree about what "the same repo" is.
    /// </summary>
    public static readonly IEqualityComparer<(string Owner, string Name)> RepoComparer = new RepoEquality();

    public sealed record All : ImportScope;
    public sealed record Org (string OrgLogin) : ImportScope;

    /// <summary>One or more repositories, matched as a set.</summary>
    public sealed record Repo : ImportScope {
        public Repo(IReadOnlyList<(string Owner, string Name)> repos) {
            // Empty would have to mean either "everything" or "nothing", and a scope that silently
            // means one when the caller meant the other is worth refusing outright.
            if (repos.Count == 0) throw new ArgumentException("A repo scope needs at least one repository.", nameof(repos));

            Repos = repos;
        }

        public Repo(string owner, string name) : this([(owner, name)]) { }

        public IReadOnlyList<(string Owner, string Name)> Repos { get; }

        // A record over a list compares by reference, which would quietly drop the value equality the
        // single-repo shape had before it held a collection.
        public bool Equals(Repo? other) =>
            other is not null && Repos.Count == other.Repos.Count && !Repos.Except(other.Repos, RepoComparer).Any();

        public override int GetHashCode() =>
            Repos.Aggregate(0, (acc, r) => acc ^ RepoComparer.GetHashCode(r));
    }

    private ImportScope() { }

    sealed class RepoEquality : IEqualityComparer<(string Owner, string Name)> {
        public bool Equals((string Owner, string Name) a, (string Owner, string Name) b) =>
            string.Equals(a.Owner, b.Owner, StringComparison.OrdinalIgnoreCase)
         && string.Equals(a.Name,  b.Name,  StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Owner, string Name) r) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(r.Owner),
                StringComparer.OrdinalIgnoreCase.GetHashCode(r.Name));
    }
}
