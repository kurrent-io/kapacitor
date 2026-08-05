namespace Capacitor.Cli.Daemon.Acp;

/// <summary>
/// The <c>kiro-cli</c> version this daemon last ran an unattended reviewer under.
///
/// <para><b>Why a version gate exists at all.</b> The reviewer's MCP containment is source
/// suppression: an empty per-launch <see cref="KiroReviewerHome"/> plus the worktree layer's removal
/// of branch-authored config. The second is ours and cannot regress with a vendor release; the first
/// is NOT — that Kiro honours <c>KIRO_HOME</c>, and reads no other global configuration source, are
/// behaviours of the installed build.</para>
///
/// <para><b>Why an operator affirmation and not a curated certified set</b> (the shape the Gemini
/// reviewer uses): that set is maintainer-curated, so every vendor release takes the reviewer offline
/// until a re-certification PR ships — untenable against a CLI that moved three versions in a week.
/// Failing closed on a version CHANGE, cleared by the operator who is already the consenting party,
/// gets the same fail-closed direction without the treadmill.</para>
///
/// <para><b>Why this is state and not configuration.</b> A value the operator could set from a shell
/// profile would be re-affirmed by their dotfiles rather than by them — the same "consent that isn't
/// consent" failure the enable flag exists to avoid. The daemon writes it; only an explicit operator
/// command changes it.</para>
/// </summary>
internal sealed class KiroReviewerVersionStore(string stateDir) {
    internal const string FileName = "kiro-reviewer-affirmed-version";

    readonly string _path = Path.Combine(stateDir, FileName);

    /// <summary>
    /// The affirmed version, or <see langword="null"/> when none has been recorded. Never throws:
    /// missing, unreadable, or a directory sitting at the pathname all read as "not affirmed", which
    /// is the fail-closed direction, and a daemon boot must not brick on this file.
    /// </summary>
    internal string? Affirmed {
        get {
            try {
                var text = File.ReadAllText(_path).Trim();
                return text.Length == 0 ? null : text;
            } catch {
                return null;
            }
        }
    }

    internal void Affirm(string version) {
        Directory.CreateDirectory(stateDir);

        // Mode set BEFORE any content exists, as LaunchConsentStore does for the same reason: a chmod
        // after the write leaves a readable window, however brief.
        var options = new FileStreamOptions {
            Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        using var stream = new FileStream(_path, options);
        using var writer = new StreamWriter(stream);
        writer.Write(version.Trim());
    }
}
