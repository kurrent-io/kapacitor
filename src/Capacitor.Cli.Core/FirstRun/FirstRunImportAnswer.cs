namespace Capacitor.Cli.Core.FirstRun;

/// <summary>
/// The history windows the Import screen offers, and the keys they travel under.
///
/// <para><b>Duplicated from the server's own <c>FirstRunImportWindows</c> on purpose.</b> There is no
/// shared assembly, and a key this build does not know has to be droppable — which is what a closed
/// set here buys and a string passed through would not. A mismatch shows up as a picker with no
/// figures against it rather than as a wrong number.</para>
/// </summary>
public static class FirstRunImportWindows {
    public const string Last30     = "30";
    public const string Last90     = "90";
    public const string Everything = "all";

    /// <summary>Newest first, ending in "everything" — the order a report lists them in.</summary>
    public static IReadOnlyList<string> All { get; } = [Last30, Last90, Everything];

    /// <summary>The <c>--since</c> horizon in days, or null for "everything". <b>Days, never a
    /// date</b>: the server sends the key precisely so this machine's clock and timezone compute the
    /// date, and a nullable day count on the wire would make "everything" indistinguishable from a
    /// field a newer server stopped sending.</summary>
    public static int? Days(string window) => window switch {
        Last30 => 30,
        Last90 => 90,
        _      => null
    };

    public static bool IsKnown(string? window) => window is Last30 or Last90 or Everything;

    /// <summary>The window's inclusive start against <paramref name="today"/>, or null for
    /// "everything" — the value <c>--since</c> takes.</summary>
    public static DateOnly? Since(string window, DateOnly today) =>
        Days(window) is { } days ? today.AddDays(-days) : null;

    /// <summary>What to call it back to the user, beside the keys it belongs to for the same reason
    /// the days are: two lists that have to correspond are one list.</summary>
    public static string Label(string window) => window switch {
        Last30 => "last 30 days",
        Last90 => "last 90 days",
        _      => "everything"
    };
}

/// <summary>How far one repository's history travels. A closed set, so a stop a newer server invents
/// is a repository to leave alone rather than one to guess at.</summary>
public enum FirstRunImportLevel {
    /// <summary>Uploaded, owner-only — an explicit <c>--private</c> pass, never a default.</summary>
    OnlyMe,

    /// <summary>Uploaded and readable by everyone in the workspace.</summary>
    Shared
}

/// <summary>Who titles the imported sessions.</summary>
public enum FirstRunImportTitles {
    /// <summary>The server titles them, at our cost. The CLI passes <c>--skip-title</c>.</summary>
    Server,

    /// <summary>This machine titles them with the user's own agent, spending their quota. The only
    /// mode that does <i>not</i> pass <c>--skip-title</c>.</summary>
    Local,

    /// <summary>Nobody titles them. Untitled sessions are still searchable.</summary>
    None
}

/// <summary>One repository to import, and how far it goes.</summary>
public sealed record FirstRunImportChoice(string Owner, string Name, FirstRunImportLevel Level) {
    /// <summary>What <c>--repo</c> takes.</summary>
    public string Slug => $"{Owner}/{Name}";
}

/// <summary>
/// The Import screen's answer, as this build reads it.
///
/// <para><b>Empty is an answer.</b> "Import nothing" is a decision, and it is only distinguishable
/// from "never asked" by whether an answer exists at all — which is why the absence is a null
/// <see cref="FirstRunImportAnswer"/> rather than an empty one, exactly as
/// <see cref="FirstRunAgentsAnswer"/> does it.</para>
/// </summary>
/// <param name="Choices">Repositories to import. One absent from this list is not imported; there is
/// no "not imported" member on <see cref="FirstRunImportLevel"/>, because absence already says it and
/// two ways to say it are two ways for the screen and the CLI to disagree.</param>
/// <param name="Window">A <see cref="FirstRunImportWindows"/> key, already checked.</param>
/// <param name="Titles">Who titles what this imports.</param>
/// <param name="Vendors">Vendor flags to pass, or null for no filter — which is what an unflagged
/// <c>kcap import</c> does, and is different from an empty list.</param>
/// <param name="DecidedAt">When the answer was made, on the server's clock. Carried, not compared.</param>
/// <param name="Unreadable">How many repositories named a level this build does not know. Dropped
/// rather than guessed at, and counted so the user can be told their CLI is behind their server
/// rather than left wondering where a repository went.</param>
public sealed record FirstRunImportAnswer(
        IReadOnlyList<FirstRunImportChoice> Choices,
        string                              Window,
        FirstRunImportTitles                Titles,
        IReadOnlyList<string>?              Vendors,
        DateTimeOffset                      DecidedAt,
        int                                 Unreadable) {
    /// <summary>The user asked for nothing, and this build understood all of it. Distinct from an
    /// answer that asks for nothing only because none of its entries are readable here.</summary>
    public bool IsDecline => Choices.Count == 0 && Unreadable == 0;

    /// <summary>Repositories at one level, which is what makes a pass — <c>--private</c> is per
    /// invocation, so each level is its own run.</summary>
    public IReadOnlyList<FirstRunImportChoice> At(FirstRunImportLevel level) =>
        [.. Choices.Where(c => c.Level == level)];

    /// <summary>Whether to pass <c>--skip-title</c>. <b>Both non-<see cref="FirstRunImportTitles.Local"/>
    /// modes skip</b>: the server titling them and nobody titling them differ in what happens next on
    /// the server, not in what this machine does.</summary>
    public bool SkipTitle => Titles is not FirstRunImportTitles.Local;

    /// <summary>The window's <c>--since</c> against <paramref name="today"/>, or null for
    /// everything.</summary>
    public DateOnly? Since(DateOnly today) => FirstRunImportWindows.Since(Window, today);

    /// <summary>
    /// Repositories were chosen, but no agent this build can read them from.
    ///
    /// <para>Only reachable by this build having dropped every vendor named: a machine that truly
    /// scanned none would have offered no repositories to choose. Running it anyway would scan nothing
    /// and report a successful import of history that never moved, which is the one outcome worse than
    /// saying the CLI is behind the server.</para>
    /// </summary>
    public bool NoReadableVendors => Vendors is { Count: 0 } && Choices.Count > 0;
}
