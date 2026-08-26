using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Core.Tests.Unit.Config;

/// <summary>
/// The distinction <see cref="ProfileContext"/> exists to make visible: <c>Resolution</c> is what
/// precedence selected, <c>Effective</c> is which profile's settings apply, and <c>Name</c> is which
/// profile a write lands on. They differ exactly when a URL override wins — and reading a setting
/// off <c>Resolution</c> in that case has silently ignored it twice now.
/// </summary>
public class ProfileContextTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    Task Write(string active, Dictionary<string, Profile> profiles) =>
        ConfigMutator.MutateAsync(Config.Root, _ => new ProfileConfig { ActiveProfile = active, Profiles = profiles });

    async Task<ProfileContext> Context(ResolvedProfile resolution) =>
        new(resolution, await AppConfig.LoadProfileConfig(Config.Root));

    [Test]
    public async Task a_resolved_profile_wins_over_the_active_one() {
        var resolved = new Profile { ExcludedPaths = ["/from/resolved"] };
        await Write("default", new() { ["default"] = new Profile { ExcludedPaths = ["/from/active"] } });

        var context = await Context(new("http://x.test", "work", resolved, null));

        await Assert.That(context.Effective).IsSameReferenceAs(resolved);
        await Assert.That(context.Name).IsEqualTo("work");
    }

    /// <summary>The override case, and the whole reason the fallback exists: <c>--server-url</c> /
    /// <c>KCAP_URL</c> make the resolver select no profile, yet `kcap ignore` and `kcap setup` still
    /// write to the active one, so that is where the settings live.</summary>
    [Test]
    public async Task no_resolved_profile_falls_back_to_the_active_one() {
        var active = new Profile { ExcludedPaths = ["/from/active"] };
        await Write("work", new() { ["work"] = active });

        var context = await Context(new("http://override.test", null, null, null));

        await Assert.That(context.Resolution.Profile).IsNull();          // the fact the two differ
        await Assert.That(context.Effective?.ExcludedPaths).IsEquivalentTo(active.ExcludedPaths);
        await Assert.That(context.Name).IsEqualTo("work");
    }

    /// <summary>With no config.json at all, <c>LoadProfileConfig</c> synthesizes a `default` entry —
    /// so the fallback answers with product defaults rather than null. A never-configured machine
    /// still gets a profile to read settings off, which is what the cold-start paths assume.</summary>
    [Test]
    public async Task an_absent_config_falls_back_to_the_synthesized_default() {
        var context = await Context(new(null, null, null, null));

        await Assert.That(context.Resolution.Profile).IsNull();
        await Assert.That(context.Effective?.UpdateCheck).IsTrue();
        await Assert.That(context.Name).IsEqualTo(ProfileConfig.DefaultName);
    }

    /// <summary>A blank <c>active_profile</c> — a hand-edited or half-migrated file — normalises
    /// rather than naming a profile no lookup can satisfy.</summary>
    [Test]
    public async Task a_blank_active_profile_normalises_to_the_default_name() {
        await Write("", new() { [ProfileConfig.DefaultName] = new Profile { UpdateCheck = false } });

        var context = await Context(new(null, null, null, null));

        await Assert.That(context.Name).IsEqualTo(ProfileConfig.DefaultName);
        await Assert.That(context.Effective?.UpdateCheck).IsFalse();
    }
}
