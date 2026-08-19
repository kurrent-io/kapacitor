namespace Capacitor.Cli.Commands;

/// <summary>
/// Pure flag parser and resolver for `kcap import` scope selection.
/// Performs no I/O — current-repo lookup is the caller's job; the resolved
/// result is passed in via <see cref="ResolveInput.CurrentRepo"/>.
/// </summary>
public static class ImportScopeArgs {
    public sealed record ParsedFlags(
            bool                  All,
            bool                  Org,
            IReadOnlyList<string> RepoArgs,
            bool                  Yes,
            bool                  Private,
            string?               OrgArg = null // explicit owner after `--org`, or null for bare `--org`
        );

    public sealed record ResolveInput(
            ParsedFlags                  Flags,
            string                       ActiveProfile,
            bool                         IsInteractive,
            (string Owner, string Name)? CurrentRepo,
            string?                      StoredOrg = null // org remembered on the active profile
        );

    public sealed record ResolveResult(
            ImportScope? Scope, // null => picker needed, org-pick needed, or error
            bool         Yes,
            bool         Private,
            string?      Error,
            bool         NeedOrgPick = false // bare `--org`, no value/stored org: pick from discovered repos
        );

    public static ParsedFlags ParseFlags(string[] args) {
        // Repeatable: one occurrence per repo. A trailing `--repo`, or one followed by another flag,
        // records the empty string so Resolve can say the value is missing — dropping it silently would
        // let `--repo a/b --repo` import half of what was asked for and report nothing.
        var repos = new List<string>();

        for (var i = 0; i < args.Length; i++) {
            if (args[i] != "--repo") continue;

            var value = i + 1 < args.Length ? args[i + 1] : "";

            repos.Add(value.StartsWith('-') ? "" : value);
        }

        // `--org` may be bare or take an explicit owner. Only treat the next token
        // as the owner when it isn't another flag (so `--org --yes` stays bare).
        string? org    = null;
        var     orgIdx = Array.IndexOf(args, "--org");
        if (orgIdx >= 0 && orgIdx + 1 < args.Length && !args[orgIdx + 1].StartsWith('-')) org = args[orgIdx + 1];

        return new(
            All: args.Contains("--all"),
            Org: args.Contains("--org"),
            RepoArgs: repos,
            Yes: args.Contains("--yes") || args.Contains("-y"),
            Private: args.Contains("--private"),
            OrgArg: org
        );
    }

    public static ResolveResult Resolve(ResolveInput input) {
        var f     = input.Flags;
        var count = (f.All ? 1 : 0) + (f.Org ? 1 : 0) + (f.RepoArgs.Count == 0 ? 0 : 1);

        switch (count) {
            case > 1:
                return new(
                    null,
                    f.Yes,
                    f.Private,
                    "--all, --org, and --repo are mutually exclusive."
                );
            case 0 when input.IsInteractive:
                return new(null, f.Yes, f.Private, Error: null);
            case 0:
                return new(
                    null,
                    f.Yes,
                    f.Private,
                    "--all, --org, or --repo <owner/name> is required for non-interactive use."
                );
        }

        // A scope flag is set: enforce --yes for non-interactive runs.
        if (!input.IsInteractive && !f.Yes) {
            return new(
                null,
                f.Yes,
                f.Private,
                "--yes is required for non-interactive use."
            );
        }

        if (f.All) {
            return new(new ImportScope.All(), f.Yes, f.Private, null);
        }

        if (f.Org) {
            // Explicit `--org <owner>` wins. The owner is the GitHub org/owner to
            // match against each session's git-remote owner — NOT the profile name
            // (which under WorkOS is a tenant slug, not a GitHub org). Trim so a
            // padded value (e.g. "EventStore ") can't produce a scope that never
            // matches a detected owner.
            var org = (!string.IsNullOrWhiteSpace(f.OrgArg) ? f.OrgArg : input.StoredOrg)?.Trim();

            if (!string.IsNullOrEmpty(org)) {
                return new(new ImportScope.Org(org), f.Yes, f.Private, null);
            }

            // Bare `--org` with nothing remembered: pick an org from discovered repos
            // when interactive; otherwise we have no owner to scope on.
            if (input.IsInteractive) {
                return new(null, f.Yes, f.Private, Error: null, NeedOrgPick: true);
            }

            return new(
                null,
                f.Yes,
                f.Private,
                "--org needs an owner: pass `--org <owner>`, or run `kcap import --org` once interactively to choose and remember one."
            );
        }

        // Every value must parse before any is used: a malformed one is a usage error, and importing
        // the rest would act on a command the user did not write.
        var repos = new List<(string Owner, string Name)>();

        foreach (var value in f.RepoArgs) {
            if (value is "." or "current") {
                if (input.CurrentRepo is null) {
                    return new(
                        null,
                        f.Yes,
                        f.Private,
                        "--repo . requires the current directory to be in a git repo with an origin remote."
                    );
                }

                repos.Add(input.CurrentRepo.Value);

                continue;
            }

            if (value.Length == 0) {
                return new(null, f.Yes, f.Private, "--repo needs a value: pass `--repo <owner/name>`.");
            }

            var parts = value.Split('/');

            if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0) {
                return new(
                    null,
                    f.Yes,
                    f.Private,
                    $"--repo expects owner/name (got '{value}')."
                );
            }

            repos.Add((parts[0], parts[1]));
        }

        // `--repo . --repo owner/name` naming the same repo twice is one selection; the scope dedupes.
        return new(new ImportScope.Repo(repos), f.Yes, f.Private, null);
    }
}
