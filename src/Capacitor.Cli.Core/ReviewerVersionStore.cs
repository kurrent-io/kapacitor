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
/// <para><b>Why an operator-set MINIMUM and not a curated certified set.</b> A maintainer-curated set
/// takes the reviewer offline on every vendor release until a re-certification PR ships — untenable
/// against CLIs that move several versions a week, and it did exactly that: the Gemini reviewer was
/// unreachable at a version one patch ahead of the certified one. Failing closed on a version CHANGE
/// removed the kcap-release coupling but kept the treadmill, merely relocating it onto the operator,
/// who then re-took the same acceptance on every patch. The recorded value is therefore the OLDEST
/// build this daemon will run: an upgrade needs no action, a downgrade below it is refused, and a
/// build later found to be bad is excluded by raising the floor past it.</para>
///
/// <para><b>Why this is state and not configuration.</b> A value the operator could set from a shell
/// profile would be re-affirmed by their dotfiles rather than by them — the same "consent that isn't
/// consent" failure the enable flag exists to avoid. The daemon writes it; only an explicit operator
/// command changes it.</para>
///
/// <para><b>ONE per-vendor exception: antigravity's floor is seeded from the binary resolving, not
/// from a consent event</b> — it also gates HOSTED <c>agy</c> launches, which take no consent, and a
/// consent-less daemon with no floor refuses every one as <c>version_no_minimum</c>. Seeding is
/// once-only, so a floor recorded before consent is not raised when the reviewer is later enabled:
/// a downgrade to that pre-consent build is admitted where a consent-anchored floor would refuse it.
/// Accepted — bounded to builds at or above that one, remedy is
/// <c>kcap daemon reviewer affirm --vendor antigravity</c>. Consent must not re-seed.</para>
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

/// <summary>Whether the installed build clears the recorded minimum.</summary>
public enum ReviewerVersionAffirmation {
    /// <summary>Installed is at or above the recorded minimum — allowed.</summary>
    MeetsMinimum,
    /// <summary>The installed build could not be identified — denied, since it cannot be compared.</summary>
    Unresolved,
    /// <summary>No minimum recorded at all. Distinct from <see cref="BelowMinimum"/> because the
    /// operator's remedy differs: record one, rather than change the installed build.</summary>
    NoMinimumRecorded,
    /// <summary>Installed is older than the recorded minimum.</summary>
    BelowMinimum,
    /// <summary>Exactly one of the two values orders as a version and the other does not, so there is
    /// no ordering to apply. Denied — but see <see cref="ReviewerVersionAffirmations"/> for why the
    /// affirm remedy provably terminates rather than looping.</summary>
    Incomparable
}

/// <summary>
/// The version half of every gated reviewer's decision, written once so the vendors cannot drift
/// apart on what "this build clears the bar" means. Each vendor keeps its own decision enum, its own
/// preconditions (Kiro's POSIX requirement) and its own consent text, because what enabling a
/// reviewer grants differs per vendor — only this comparison is common.
///
/// <para><b>The recorded value is a MINIMUM, not an exact match.</b> It previously required the
/// installed build to equal the recorded one, which took a reviewer offline on every vendor patch
/// release until an operator re-ran the affirm verb. That is the treadmill the maintainer-curated
/// certified set was abandoned for; affirmation only relocated it from a kcap release onto the
/// operator. The recorded value now means "the oldest build this daemon will run", and a vendor
/// upgrade needs no action at all.</para>
///
/// <para><b>What that gives up.</b> A future vendor build that weakens its own containment behaviour
/// is admitted silently — for Gemini the MCP-allowlist semantics, for Kiro the <c>KIRO_HOME</c>
/// suppression. Both were already accepted at the operator's first affirmation; what changed is that
/// the acceptance carries forward across upgrades instead of being re-taken each time. When a bad
/// build is found, the affirm verb raises the floor past it without a kcap release. See
/// <c>GeminiReviewerCapability</c>'s class doc, which records the earlier decision this reverses and
/// why the boundary sat where it did.</para>
/// </summary>
public static class ReviewerVersionAffirmations {
    /// <summary>
    /// Pure. An ORDERED cascade, not a match over two parse results — see the inline note on why the
    /// null checks must come first.
    ///
    /// <para>Both sides are trimmed because a vendor's <c>--version</c> output and a hand-written
    /// record both carry stray whitespace.</para>
    /// </summary>
    public static ReviewerVersionAffirmation Decide(string? installedVersion, string? minimumVersion) {
        // Rows 1-2 FIRST, and this ordering is load-bearing: Version.TryParse(null) returns false
        // silently, so parsing both sides up front and matching on the two results would route a
        // MISSING record into Incomparable/BelowMinimum instead of NoMinimumRecorded — the wrong
        // denial for the most common misconfiguration (a reviewer enabled against an already-running
        // daemon, which has not yet had a startup to seed one).
        if (Normalize(installedVersion) is not { } installed) return ReviewerVersionAffirmation.Unresolved;
        if (Normalize(minimumVersion) is not { } minimum)     return ReviewerVersionAffirmation.NoMinimumRecorded;

        var installedVersionParsed = TryParseVersion(installed);
        var minimumVersionParsed   = TryParseVersion(minimum);

        // Both unorderable: fall back to the ordinal equality this type used to apply unconditionally.
        // That is what keeps the change monotone — every pair the old rule ALLOWED (which is exactly
        // the ordinal-equal ones) is still allowed, including values no version parser accepts.
        if (installedVersionParsed is null && minimumVersionParsed is null)
            return string.Equals(installed, minimum, StringComparison.Ordinal)
                ? ReviewerVersionAffirmation.MeetsMinimum
                : ReviewerVersionAffirmation.BelowMinimum;

        // Exactly one orders. Ordinal equality is only sound when both values are in the same domain;
        // comparing across domains would refuse a genuine upgrade while LABELLING it "below minimum".
        // Say we cannot order them instead. The affirm remedy terminates either way: if the minimum
        // is the odd one, affirming writes the (orderable) installed value; if the installed one is
        // odd, affirming makes both that same odd string, which the ordinal branch above then allows.
        if (installedVersionParsed is null || minimumVersionParsed is null)
            return ReviewerVersionAffirmation.Incomparable;

        return installedVersionParsed >= minimumVersionParsed
            ? ReviewerVersionAffirmation.MeetsMinimum
            : ReviewerVersionAffirmation.BelowMinimum;
    }

    /// <summary>
    /// A vendor version string as an orderable <see cref="Version"/>, or null when it does not order.
    ///
    /// <para>Shared with <c>DaemonRunner.CliVersionAllowed</c> rather than duplicated, because the two
    /// gates must agree on WHAT COUNTS AS AN ORDERABLE VERSION. The same
    /// <see cref="VendorVersionResolver"/> output feeds both; if they classified parseability
    /// differently, a reviewer could be admitted running a version the rest of the daemon's version
    /// logic treats as unrecognisable. That shared classification is also what bounds the reviewer
    /// gate's scope — widening what parses here would widen what the certification gate admits, so it
    /// cannot be done as a side effect of a reviewer change.</para>
    /// </summary>
    /// <para><b>Byte-identical to the normalization <c>CliVersionAllowed</c> applied before it moved
    /// here — deliberately, and do not "tidy" a <c>.Trim()</c> back in.</b> An earlier revision added
    /// one, on the reasoning that trimming is harmless. It is not: <see cref="Version.TryParse"/>
    /// already tolerates surrounding whitespace, so the trim changes nothing on its own — but it lets
    /// <c>TrimStart('v','V')</c> reach a <c>v</c> it could not otherwise see, so <c>" v1.2.3"</c> flips
    /// from REFUSED to allowed (measured). For the certification gate that is a silent widening of what
    /// it admits, made as a side effect of a reviewer change. Every caller here already passes trimmed
    /// input anyway (<see cref="Normalize"/>'s output, <see cref="ReviewerVersionStore.Affirmed"/>, and
    /// <see cref="VendorVersionResolver"/>'s token), so the trim bought nothing and cost that.</para>
    public static Version? TryParseVersion(string? version) =>
        Version.TryParse((version ?? "").TrimStart('v', 'V').Split('-', '+')[0], out var parsed)
            ? parsed
            : null;

    /// <summary>Trimmed, or null when there is nothing there — whitespace-only is not a version.</summary>
    public static string? Normalize(string? version) =>
        version is { Length: > 0 } v && v.Trim() is { Length: > 0 } t ? t : null;

    /// <summary>Render for an operator-facing message.</summary>
    public static string Describe(string? version) => Normalize(version) ?? "<none>";
}
