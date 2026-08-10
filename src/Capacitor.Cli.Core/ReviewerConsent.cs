namespace Capacitor.Cli.Core;

/// <summary>
/// Reads a gated reviewer's opt-OUT switch (the <c>KCAP_*_UNATTENDED_REVIEWER</c> variables).
///
/// <para><b>In Core because two projects must agree on the answer.</b> The daemon parses these to build
/// its config; the CLI's <c>daemon service install</c> reports what a captured value MEANS in its
/// output. Those are the same question — "does this string enable or disable the reviewer?" — and a
/// second copy of the rules in the CLI would be free to drift from the daemon's, so the install notice
/// could tell an operator a value enables a reviewer that the daemon then disables. One parser removes
/// that class.</para>
///
/// <para><b>Unset means enabled; a value we cannot read means DISABLED.</b> The asymmetry is the point.
/// Since unset already enables, the only reason to set one of these variables at all is to turn a
/// reviewer OFF — enabling needs no variable. So an unrecognised value is not an ambiguous input, it is
/// a FAILED ATTEMPT TO SAY OFF, and honouring the evident intent means failing closed. An earlier
/// revision failed open here, reasoning from the general "a typo must not take a feature offline" rule,
/// which does not transfer to a setting whose only use is to disable.</para>
///
/// <para><b>That justification does not generalise, so do not reuse this parser for other settings.</b>
/// It rests on two facts specific to these variables: unset already means enabled, and disabling is
/// therefore the only possible intent behind setting one. A setting where unset means disabled, or
/// where fail-open was chosen deliberately, needs its own parse and its own reasoning.</para>
/// </summary>
public static class ReviewerConsent {
    /// <summary>
    /// The ONE value set: true, false, or null for "set but unrecognised". <see cref="IsEnabled"/> and
    /// <see cref="DescribeUnparseable"/> are both derived from it, so a parse and an "is this
    /// recognised?" predicate cannot disagree — two methods that must agree with nothing making them is
    /// the shape this collapses.
    ///
    /// <para><b>Matched surrounding quotes are stripped</b>, because the realistic way a DISABLE attempt
    /// becomes unrecognised is a quoting artifact — a mis-quoted service-unit entry, a <c>.cmd</c>
    /// wrapper's <c>set "K=V"</c>, a hand-edited plist — and the value then arrives as literal
    /// <c>"0"</c>. Since disabling is the operator's only lever, a fail-open there is the one mistake
    /// worth spending three lines to prevent. One level only: a value still unrecognised after stripping
    /// is reported, not stripped again.</para>
    /// </summary>
    public static bool? Recognise(string? value) {
        var v = value?.Trim();

        // DO NOT fold these two early returns into the `return null` fallthrough below. They look like
        // "nothing to recognise" and they are not: IsEnabled maps null to FALSE, so collapsing them
        // would turn all four reviewers OFF on every daemon with the variables unset — which is
        // essentially all of them. The same tidy-up was harmless while this failed open; the
        // fail-closed direction is what made it catastrophic. Unset must never reach the `??`.
        if (string.IsNullOrEmpty(v)) return true;   // unset — the default

        if (v.Length >= 2 && (v[0] == '"' || v[0] == '\'') && v[^1] == v[0])
            v = v[1..^1].Trim();

        if (v.Length == 0) return true;             // `""` / `''` — an empty value, not an unreadable one

        if (v == "0"
         || string.Equals(v, "false", StringComparison.OrdinalIgnoreCase)
         || string.Equals(v, "no",    StringComparison.OrdinalIgnoreCase)
         || string.Equals(v, "off",   StringComparison.OrdinalIgnoreCase))
            return false;

        if (v == "1"
         || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)
         || string.Equals(v, "yes",  StringComparison.OrdinalIgnoreCase)
         || string.Equals(v, "on",   StringComparison.OrdinalIgnoreCase))
            return true;

        return null;   // set, but says nothing we understand
    }

    /// <summary>Whether the reviewer is enabled. Unset/blank/enabling → true; disabling → false; an
    /// unrecognised value → false (a failed "off", honoured as off).</summary>
    public static bool IsEnabled(string? value) => Recognise(value) ?? false;

    /// <summary>
    /// A warning for a variable that is SET but says nothing recognised, or null when there is nothing
    /// to report. An operator who typed <c>flase</c> meaning to disable would otherwise get the reviewer
    /// enabled with no signal at all — the direction worth being loud about now that disabling is the
    /// only lever.
    /// </summary>
    public static string? DescribeUnparseable(string variable, string? value) =>
        Recognise(value) is null
            ? $"{variable} is set to '{value?.Trim()}', which is not recognised as true or false. "
            + "Treating it as DISABLED, because the only reason to set this variable is to turn the "
            + "reviewer off — unset already means enabled. Use 0/false/no/off to disable, or unset it "
            + "(or use 1/true/yes/on) to enable."
            : null;
}
