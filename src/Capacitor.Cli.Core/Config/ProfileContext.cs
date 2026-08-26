namespace Capacitor.Cli.Core.Config;

/// <summary>
/// This process's profile facts, resolved once at startup and injected wherever a per-profile
/// setting is read or written. An immutable value over the <see cref="ProfileConfig"/> the
/// resolution was computed from: nothing here touches the disk, and the two can never disagree.
///
/// <para>Two different answers, deliberately. <see cref="Resolution"/> is what precedence selected,
/// and its <c>Profile</c> is null whenever <c>--server-url</c> or <c>KCAP_URL</c> won — the
/// resolver selects no profile in that case. <see cref="Effective"/> and <see cref="Name"/> fall
/// back to the active profile, which is where <c>kcap ignore</c>, <c>kcap setup</c> and <c>kcap
/// update</c> actually write. A setting read through <see cref="Resolution"/> alone is therefore
/// silently ignored for every override user, which has been a real defect twice. Read a setting
/// through <see cref="Effective"/> and name a write target through <see cref="Name"/>; read
/// <see cref="Resolution"/> only for facts about the resolution itself — its URL, its source.</para>
/// </summary>
public sealed class ProfileContext(ResolvedProfile resolution, ProfileConfig snapshot) {
    public ResolvedProfile Resolution => resolution;

    /// <summary>The config as it stood when the resolution was computed. A long-lived process that
    /// must observe a setting written after it started re-reads that one setting itself, rather
    /// than every read here paying for a disk hit on the hook path.</summary>
    public ProfileConfig Snapshot => snapshot;

    /// <summary>The NAME of the profile that applies — the target for a write that must land on the
    /// profile the user is actually on, not blindly on <c>active_profile</c>.</summary>
    public string Name => resolution.ProfileName is { Length: > 0 } named ? named : snapshot.ActiveName;

    /// <summary>The daemon name the resolved profile supplies. Deliberately the RESOLUTION's profile
    /// and not <see cref="Effective"/>: changing that would repoint which daemon <c>stop</c> talks to.
    /// Null under <c>--server-url</c>/<c>KCAP_URL</c>, where the resolver falls through to the OS
    /// user.</summary>
    public string? DaemonName => resolution.Profile?.Daemon?.Name;

    /// <summary>The profile whose settings apply. Looked up in the snapshot by <see cref="Name"/>,
    /// which is where <see cref="ProfileResolver"/> read it from in the first place; the resolution's
    /// own copy is the fallback for a profile the snapshot has no entry for.</summary>
    public Profile? Effective => snapshot.Profiles.GetValueOrDefault(Name) ?? resolution.Profile;
}
