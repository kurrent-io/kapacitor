using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Core.FirstRun;

/// <summary>One harness the user turned something on for, mapped onto a vendor this build knows.</summary>
/// <param name="VendorId">A <see cref="HarnessCatalog"/> key. Never a string straight off the wire —
/// see <see cref="FirstRunFlowOutcomes.Agents(FirstRunFlowResponse?)"/>.</param>
/// <param name="Record">Install capture, so this harness's sessions record themselves.</param>
/// <param name="Tools">Register the MCP servers. Answerable on its own, except where a vendor's
/// install bundles the two.</param>
public sealed record FirstRunAgentsChoice(string VendorId, bool Record, bool Tools);

/// <summary>
/// The Agents screen's answer, as this build reads it.
///
/// <para><b>Empty is an answer.</b> "Not now" is a decision to install nothing, and it is only
/// distinguishable from "never asked" by whether an answer exists at all — which is why the absence is
/// a null <see cref="FirstRunAgentsAnswer"/> rather than an empty one.</para>
/// </summary>
/// <param name="Choices">Harnesses to act on. A vendor left off is absent rather than
/// present-and-false, so there is nothing to do for it and no way to mistake an untouched vendor for
/// one we were asked to uninstall.</param>
/// <param name="DecidedAt">When the answer was made, on the server's clock. Carried, not compared —
/// see the wire model.</param>
/// <param name="Unrecognised">How many entries named a vendor this build has never heard of. Dropped
/// rather than forwarded, and counted so the user can be told their CLI is behind their server rather
/// than left with a harness that silently did not get set up.</param>
public sealed record FirstRunAgentsAnswer(
        IReadOnlyList<FirstRunAgentsChoice> Choices,
        DateTimeOffset                      DecidedAt,
        int                                 Unrecognised) {
    /// <summary>The user asked for nothing, and we understood all of it. Distinct from an answer whose
    /// every entry was dropped, which asks for nothing only because this build could not read it.</summary>
    public bool IsDecline => Choices.Count == 0 && Unrecognised == 0;

    /// <summary>Install capture for this harness. False for a vendor the answer never mentions — a
    /// harness left off is absent rather than present-and-false.</summary>
    public bool Records(string vendorId) => Choices.Any(c => Is(c, vendorId) && c.Record);

    /// <summary>Register this harness's MCP servers. Where a vendor's install bundles the two, the
    /// server refuses an answer whose halves disagree, so this tracks <see cref="Records"/>.</summary>
    public bool Tools(string vendorId) => Choices.Any(c => Is(c, vendorId) && c.Tools);

    /// <summary>The harnesses to name back to the user, in catalogue order.</summary>
    public IEnumerable<string> Labels =>
        HarnessCatalog.All.Where(h => Records(h.VendorId) || Tools(h.VendorId)).Select(h => h.Label);

    static bool Is(FirstRunAgentsChoice choice, string vendorId) =>
        string.Equals(choice.VendorId, vendorId, StringComparison.Ordinal);
}
