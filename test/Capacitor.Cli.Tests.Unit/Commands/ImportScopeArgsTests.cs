using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class ImportScopeArgsTests {
    [Test]
    public async Task ParseFlags_reads_all() {
        var f = ImportScopeArgs.ParseFlags(["import", "--all"]);
        await Assert.That(f.All).IsTrue();
        await Assert.That(f.Org).IsFalse();
        await Assert.That(f.RepoArgs).IsEmpty();
        await Assert.That(f.Yes).IsFalse();
        await Assert.That(f.Private).IsFalse();
    }

    [Test]
    public async Task ParseFlags_reads_repo_value() {
        var f = ImportScopeArgs.ParseFlags(["import", "--repo", "EventStore/kcap"]);
        await Assert.That(f.RepoArgs).IsEquivalentTo(["EventStore/kcap"]);
    }

    [Test]
    public async Task ParseFlags_reads_org_value() {
        var f = ImportScopeArgs.ParseFlags(["import", "--org", "EventStore"]);
        await Assert.That(f.Org).IsTrue();
        await Assert.That(f.OrgArg).IsEqualTo("EventStore");
    }

    [Test]
    public async Task ParseFlags_bare_org_has_null_orgarg() {
        var f = ImportScopeArgs.ParseFlags(["import", "--org"]);
        await Assert.That(f.Org).IsTrue();
        await Assert.That(f.OrgArg).IsNull();
    }

    [Test]
    public async Task ParseFlags_org_followed_by_flag_stays_bare() {
        var f = ImportScopeArgs.ParseFlags(["import", "--org", "--yes"]);
        await Assert.That(f.Org).IsTrue();
        await Assert.That(f.OrgArg).IsNull();
        await Assert.That(f.Yes).IsTrue();
    }

    [Test]
    public async Task ParseFlags_reads_yes_short_form() {
        var f = ImportScopeArgs.ParseFlags(["import", "--all", "-y"]);
        await Assert.That(f.Yes).IsTrue();
    }

    [Test]
    public async Task ParseFlags_reads_private() {
        var f = ImportScopeArgs.ParseFlags(["import", "--all", "--private"]);
        await Assert.That(f.Private).IsTrue();
    }

    [Test]
    public async Task Resolve_errors_when_two_scope_flags_set() {
        var input = new ImportScopeArgs.ResolveInput(
            Flags: new(All: true, Org: true, RepoArgs: [], Yes: false, Private: false),
            ActiveProfile: "EventStore",
            IsInteractive: true,
            CurrentRepo: null);

        var r = ImportScopeArgs.Resolve(input);

        await Assert.That(r.Scope).IsNull();
        await Assert.That(r.Error).IsNotNull();
        await Assert.That(r.Error!).Contains("mutually exclusive");
    }

    [Test]
    public async Task Resolve_returns_All_for_all_flag() {
        var input = new ImportScopeArgs.ResolveInput(
            Flags: new(All: true, Org: false, RepoArgs: [], Yes: false, Private: false),
            ActiveProfile: "default",
            IsInteractive: true,
            CurrentRepo: null);

        var r = ImportScopeArgs.Resolve(input);

        await Assert.That(r.Scope).IsTypeOf<ImportScope.All>();
        await Assert.That(r.Error).IsNull();
    }

    [Test]
    public async Task Resolve_returns_Org_for_explicit_org_value() {
        // The org comes from the flag value, NOT the profile name — so it works
        // identically under GitHub and WorkOS sign-in.
        var input = new ImportScopeArgs.ResolveInput(
            Flags: new(All: false, Org: true, RepoArgs: [], Yes: false, Private: false, OrgArg: "EventStore"),
            ActiveProfile: "acme-tenant-slug",
            IsInteractive: true,
            CurrentRepo: null);

        var r = ImportScopeArgs.Resolve(input);

        await Assert.That(r.Scope).IsTypeOf<ImportScope.Org>();
        await Assert.That(((ImportScope.Org)r.Scope!).OrgLogin).IsEqualTo("EventStore");
        await Assert.That(r.NeedOrgPick).IsFalse();
    }

    [Test]
    public async Task Resolve_trims_explicit_org_value() {
        var input = new ImportScopeArgs.ResolveInput(
            Flags: new(All: false, Org: true, RepoArgs: [], Yes: false, Private: false, OrgArg: "  EventStore  "),
            ActiveProfile: "acme-tenant-slug",
            IsInteractive: true,
            CurrentRepo: null);

        var r = ImportScopeArgs.Resolve(input);

        await Assert.That(((ImportScope.Org)r.Scope!).OrgLogin).IsEqualTo("EventStore");
    }

    [Test]
    public async Task Resolve_whitespace_only_org_value_falls_through_to_pick() {
        var input = new ImportScopeArgs.ResolveInput(
            Flags: new(All: false, Org: true, RepoArgs: [], Yes: false, Private: false, OrgArg: "   "),
            ActiveProfile: "acme-tenant-slug",
            IsInteractive: true,
            CurrentRepo: null);

        var r = ImportScopeArgs.Resolve(input);

        await Assert.That(r.Scope).IsNull();
        await Assert.That(r.NeedOrgPick).IsTrue();
    }

    [Test]
    public async Task Resolve_bare_org_uses_remembered_stored_org() {
        var input = new ImportScopeArgs.ResolveInput(
            Flags: new(All: false, Org: true, RepoArgs: [], Yes: false, Private: false),
            ActiveProfile: "acme-tenant-slug",
            IsInteractive: true,
            CurrentRepo: null,
            StoredOrg: "EventStore");

        var r = ImportScopeArgs.Resolve(input);

        await Assert.That(((ImportScope.Org)r.Scope!).OrgLogin).IsEqualTo("EventStore");
        await Assert.That(r.NeedOrgPick).IsFalse();
    }

    [Test]
    public async Task Resolve_explicit_org_value_overrides_stored_org() {
        var input = new ImportScopeArgs.ResolveInput(
            Flags: new(All: false, Org: true, RepoArgs: [], Yes: false, Private: false, OrgArg: "kurrent"),
            ActiveProfile: "acme-tenant-slug",
            IsInteractive: true,
            CurrentRepo: null,
            StoredOrg: "EventStore");

        var r = ImportScopeArgs.Resolve(input);

        await Assert.That(((ImportScope.Org)r.Scope!).OrgLogin).IsEqualTo("kurrent");
    }

    [Test]
    public async Task Resolve_bare_org_interactive_without_stored_org_needs_pick() {
        var input = new ImportScopeArgs.ResolveInput(
            Flags: new(All: false, Org: true, RepoArgs: [], Yes: false, Private: false),
            ActiveProfile: "default",
            IsInteractive: true,
            CurrentRepo: null);

        var r = ImportScopeArgs.Resolve(input);

        await Assert.That(r.Scope).IsNull();
        await Assert.That(r.Error).IsNull();
        await Assert.That(r.NeedOrgPick).IsTrue();
    }

    [Test]
    public async Task Resolve_bare_org_non_interactive_without_stored_org_errors() {
        var input = new ImportScopeArgs.ResolveInput(
            Flags: new(All: false, Org: true, RepoArgs: [], Yes: true, Private: false),
            ActiveProfile: "default",
            IsInteractive: false,
            CurrentRepo: null);

        var r = ImportScopeArgs.Resolve(input);

        await Assert.That(r.Scope).IsNull();
        await Assert.That(r.NeedOrgPick).IsFalse();
        await Assert.That(r.Error!).Contains("--org <owner>");
    }

    [Test]
    public async Task Resolve_returns_Repo_for_owner_slash_name() {
        var input = new ImportScopeArgs.ResolveInput(
            Flags: new(All: false, Org: false, RepoArgs: ["EventStore/kcap"], Yes: false, Private: false),
            ActiveProfile: "EventStore",
            IsInteractive: true,
            CurrentRepo: null);

        var r = ImportScopeArgs.Resolve(input);

        await Assert.That(((ImportScope.Repo)r.Scope!).Repos).IsEquivalentTo([("EventStore", "kcap")]);
    }

    [Test]
    public async Task ParseFlags_collects_every_repo_flag() {
        var f = ImportScopeArgs.ParseFlags(["import", "--repo", "a/one", "--repo", "b/two", "--yes"]);

        await Assert.That(f.RepoArgs).IsEquivalentTo(["a/one", "b/two"]);
    }

    [Test]
    public async Task ParseFlags_records_a_trailing_repo_as_a_missing_value() {
        var f = ImportScopeArgs.ParseFlags(["import", "--repo", "a/one", "--repo"]);

        await Assert.That(f.RepoArgs).IsEquivalentTo(["a/one", ""]);
    }

    [Test]
    public async Task ParseFlags_does_not_take_a_following_flag_as_the_repo() {
        // `--org` has always guarded this; `--repo` did not, and repeating it makes the slip likelier.
        var f = ImportScopeArgs.ParseFlags(["import", "--repo", "--yes"]);

        await Assert.That(f.RepoArgs).IsEquivalentTo([""]);
        await Assert.That(f.Yes).IsTrue();
    }

    [Test]
    public async Task Resolve_reports_a_repo_flag_with_no_value() {
        var r = ImportScopeArgs.Resolve(new(
            Flags: new(All: false, Org: false, RepoArgs: ["a/one", ""], Yes: true, Private: false),
            ActiveProfile: "EventStore",
            IsInteractive: false,
            CurrentRepo: null));

        await Assert.That(r.Scope).IsNull();
        await Assert.That(r.Error).Contains("--repo needs a value");
    }

    [Test]
    [Arguments("a/one", "a/one")]
    [Arguments("a/one", "A/ONE")]
    public async Task Resolve_dedupes_the_same_repo_however_it_is_spelled(string first, string second) {
        var r = ImportScopeArgs.Resolve(new(
            Flags: new(All: false, Org: false, RepoArgs: [first, second], Yes: true, Private: false),
            ActiveProfile: "EventStore",
            IsInteractive: false,
            CurrentRepo: null));

        await Assert.That(((ImportScope.Repo)r.Scope!).Repos).Count().IsEqualTo(1);
    }

    [Test]
    public async Task Resolve_keeps_every_named_repo() {
        var r = ImportScopeArgs.Resolve(new(
            Flags: new(All: false, Org: false, RepoArgs: ["a/one", "b/two"], Yes: true, Private: false),
            ActiveProfile: "EventStore",
            IsInteractive: false,
            CurrentRepo: null));

        await Assert.That(((ImportScope.Repo)r.Scope!).Repos)
                    .IsEquivalentTo([("a", "one"), ("b", "two")]);
    }

    [Test]
    public async Task Resolve_dedupes_a_repo_named_twice() {
        // `--repo . --repo EventStore/kcap` from inside that repo is one selection, not two.
        var r = ImportScopeArgs.Resolve(new(
            Flags: new(All: false, Org: false, RepoArgs: [".", "eventstore/KCAP"], Yes: true, Private: false),
            ActiveProfile: "EventStore",
            IsInteractive: false,
            CurrentRepo: ("EventStore", "kcap")));

        await Assert.That(((ImportScope.Repo)r.Scope!).Repos).Count().IsEqualTo(1);
    }

    [Test]
    public async Task Resolve_rejects_the_whole_run_when_one_repo_is_malformed() {
        // Importing the valid four and silently dropping the typo would be worse than saying so.
        var r = ImportScopeArgs.Resolve(new(
            Flags: new(All: false, Org: false, RepoArgs: ["a/one", "not-a-repo"], Yes: true, Private: false),
            ActiveProfile: "EventStore",
            IsInteractive: false,
            CurrentRepo: null));

        await Assert.That(r.Scope).IsNull();
        await Assert.That(r.Error).Contains("not-a-repo");
    }

    [Test]
    public async Task Resolve_repo_dot_uses_current_repo() {
        var input = new ImportScopeArgs.ResolveInput(
            Flags: new(All: false, Org: false, RepoArgs: ["."], Yes: false, Private: false),
            ActiveProfile: "EventStore",
            IsInteractive: true,
            CurrentRepo: ("EventStore", "kcap"));

        var r = ImportScopeArgs.Resolve(input);

        await Assert.That(((ImportScope.Repo)r.Scope!).Repos).IsEquivalentTo([("EventStore", "kcap")]);
    }

    [Test]
    public async Task Resolve_repo_current_alias_uses_current_repo() {
        var input = new ImportScopeArgs.ResolveInput(
            Flags: new(All: false, Org: false, RepoArgs: ["current"], Yes: false, Private: false),
            ActiveProfile: "EventStore",
            IsInteractive: true,
            CurrentRepo: ("EventStore", "kcap"));

        var r = ImportScopeArgs.Resolve(input);

        await Assert.That(((ImportScope.Repo)r.Scope!).Repos).IsEquivalentTo([("EventStore", "kcap")]);
    }

    [Test]
    public async Task Resolve_repo_dot_errors_when_cwd_has_no_repo() {
        var input = new ImportScopeArgs.ResolveInput(
            Flags: new(All: false, Org: false, RepoArgs: ["."], Yes: false, Private: false),
            ActiveProfile: "EventStore",
            IsInteractive: true,
            CurrentRepo: null);

        var r = ImportScopeArgs.Resolve(input);

        await Assert.That(r.Scope).IsNull();
        await Assert.That(r.Error!).Contains("git repo with an origin remote");
    }

    [Test]
    public async Task Resolve_errors_on_malformed_repo_arg() {
        var input = new ImportScopeArgs.ResolveInput(
            Flags: new(All: false, Org: false, RepoArgs: ["no-slash"], Yes: false, Private: false),
            ActiveProfile: "EventStore",
            IsInteractive: true,
            CurrentRepo: null);

        var r = ImportScopeArgs.Resolve(input);

        await Assert.That(r.Scope).IsNull();
        await Assert.That(r.Error!).Contains("owner/name");
    }

    [Test]
    public async Task Resolve_returns_null_scope_and_null_error_when_no_flag_and_interactive() {
        var input = new ImportScopeArgs.ResolveInput(
            Flags: new(All: false, Org: false, RepoArgs: [], Yes: false, Private: false),
            ActiveProfile: "EventStore",
            IsInteractive: true,
            CurrentRepo: null);

        var r = ImportScopeArgs.Resolve(input);

        await Assert.That(r.Scope).IsNull();
        await Assert.That(r.Error).IsNull();
    }

    [Test]
    public async Task Resolve_errors_when_no_flag_and_non_interactive() {
        var input = new ImportScopeArgs.ResolveInput(
            Flags: new(All: false, Org: false, RepoArgs: [], Yes: false, Private: false),
            ActiveProfile: "EventStore",
            IsInteractive: false,
            CurrentRepo: null);

        var r = ImportScopeArgs.Resolve(input);

        await Assert.That(r.Scope).IsNull();
        await Assert.That(r.Error!).Contains("--all, --org, or --repo");
    }

    [Test]
    public async Task Resolve_errors_when_flag_set_and_non_interactive_without_yes() {
        var input = new ImportScopeArgs.ResolveInput(
            Flags: new(All: true, Org: false, RepoArgs: [], Yes: false, Private: false),
            ActiveProfile: "EventStore",
            IsInteractive: false,
            CurrentRepo: null);

        var r = ImportScopeArgs.Resolve(input);

        await Assert.That(r.Scope).IsNull();
        await Assert.That(r.Error!).Contains("--yes");
    }
}
