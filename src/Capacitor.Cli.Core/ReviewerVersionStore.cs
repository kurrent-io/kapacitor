namespace Capacitor.Cli.Core;

/// <summary>
/// The vendor build this daemon last ran an unattended reviewer under, per vendor.
///
/// <para><b>Why a version gate exists at all.</b> Each gated reviewer's containment rests on a
/// behaviour of the INSTALLED build, not on anything this repository controls — that Kiro honours
/// <c>KIRO_HOME</c> and reads no other global config source, that Gemini's
/// <c>--allowed-mcp-server-names</c> is an exclusive exact-match gate a repository's own settings
/// cannot widen. A vendor release can change either.</para>
///
/// <para><b>Why an operator affirmation and not a curated certified set.</b> A maintainer-curated set
/// takes the reviewer offline on every vendor release until a re-certification PR ships — untenable
/// against CLIs that move several versions a week, and it did exactly that: the Gemini reviewer was
/// unreachable at a version one patch ahead of the certified one. Failing closed on a version CHANGE,
/// cleared by the operator who is already the consenting party, keeps the fail-closed direction
/// without the treadmill.</para>
///
/// <para><b>Why this is state and not configuration.</b> A value the operator could set from a shell
/// profile would be re-affirmed by their dotfiles rather than by them — the same "consent that isn't
/// consent" failure the enable flag exists to avoid. The daemon writes it; only an explicit operator
/// command changes it.</para>
///
/// <para>Keyed by vendor, one file each, so affirming one vendor's build says nothing about another's.
/// Kiro's filename is unchanged from when this type was Kiro-only — renaming it would have silently
/// discarded every existing affirmation and taken shipped reviewers offline on upgrade.</para>
/// </summary>
public sealed class ReviewerVersionStore(string stateDir, string vendor) {
    public static string FileNameFor(string vendor) => $"{vendor}-reviewer-affirmed-version";

    readonly string _path = Path.Combine(stateDir, FileNameFor(vendor));

    /// <summary>Whether the record is PRESENT, whatever its content. Distinct from
    /// <see cref="Affirmed"/> being non-null: a corrupt or unreadable record exists but affirms
    /// nothing, and conflating the two lets a deleted record be silently re-seeded.</summary>
    public static bool RecordExists(string stateDir, string vendor) =>
        Path.Exists(Path.Combine(stateDir, FileNameFor(vendor)));

    /// <summary>
    /// The affirmed version, or <see langword="null"/> when none has been recorded. Never throws:
    /// missing, unreadable, or a directory sitting at the pathname all read as "not affirmed", which
    /// is the fail-closed direction, and a daemon boot must not brick on this file.
    /// </summary>
    public string? Affirmed {
        get {
            try {
                // FileShare.ReadWrite, not File.ReadAllText: the `kcap daemon reviewer affirm` verb
                // writes this file from a DIFFERENT process while a daemon may be reading it, and a
                // write-denying open blocks that writer outright on platforms with mandatory sharing.
                using var stream = new FileStream(
                    _path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);

                var text = reader.ReadToEnd().Trim();
                return text.Length == 0 ? null : text;
            } catch {
                return null;
            }
        }
    }

    public void Affirm(string version) {
        Directory.CreateDirectory(stateDir);

        // Mode set BEFORE any content exists, as LaunchConsentStore does for the same reason: a chmod
        // after the write leaves a readable window, however brief.
        var options = new FileStreamOptions {
            // ReadWrite, not None: sharing is BIDIRECTIONAL on Windows, so a writer that denies
            // readers cannot open while a daemon holds the file for reading. Fixing only the reader
            // left the affirm verb failing from the other side of the same race.
            Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.ReadWrite
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        using var stream = new FileStream(_path, options);
        using var writer = new StreamWriter(stream);
        writer.Write(version.Trim());
    }
}

/// <summary>Whether the installed build matches what the operator affirmed.</summary>
public enum ReviewerVersionAffirmation {
    Affirmed,
    /// <summary>The installed build could not be identified — denied, since it cannot be matched.</summary>
    Unresolved,
    /// <summary>Nothing affirmed, or a build other than the affirmed one is installed.</summary>
    Unaffirmed
}

/// <summary>
/// The version half of every gated reviewer's decision, written once so the vendors cannot drift
/// apart on what "the operator accepted this build" means. Each vendor keeps its own decision enum,
/// its own preconditions (Kiro's POSIX requirement) and its own consent text, because what enabling
/// a reviewer grants differs per vendor — only this comparison is common.
/// </summary>
public static class ReviewerVersionAffirmations {
    /// <summary>
    /// Pure. Both sides are trimmed before comparison because a vendor's <c>--version</c> output and
    /// a hand-written record both carry stray whitespace, and an Ordinal comparison of untrimmed text
    /// would refuse a build the operator did affirm.
    /// </summary>
    public static ReviewerVersionAffirmation Decide(string? installedVersion, string? affirmedVersion) {
        if (Normalize(installedVersion) is not { } installed) return ReviewerVersionAffirmation.Unresolved;
        if (Normalize(affirmedVersion) is not { } affirmed)   return ReviewerVersionAffirmation.Unaffirmed;

        return string.Equals(installed, affirmed, StringComparison.Ordinal)
            ? ReviewerVersionAffirmation.Affirmed
            : ReviewerVersionAffirmation.Unaffirmed;
    }

    /// <summary>Trimmed, or null when there is nothing there — whitespace-only is not a version.</summary>
    public static string? Normalize(string? version) =>
        version is { Length: > 0 } v && v.Trim() is { Length: > 0 } t ? t : null;

    /// <summary>Render for an operator-facing message.</summary>
    public static string Describe(string? version) => Normalize(version) ?? "<none>";
}
