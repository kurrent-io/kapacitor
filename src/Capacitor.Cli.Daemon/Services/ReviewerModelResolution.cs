namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Result of resolving a reviewer MODEL override for one vendor. A resolver owns its accepted
/// aliases/ids and its canonical/equivalence behavior entirely — there is deliberately NO shared,
/// central vendor→model table anywhere (vendor-neutrality is the whole point). On an ACCEPTED
/// resolution the resolver MUST return a non-null <see cref="EquivalenceKey"/> (the "anchor"): a
/// resolver that accepts but returns no anchor makes the server's post-launch echo validation fail
/// with no_anchor. The <see cref="LaunchModel"/> string is passed through to the launcher VERBATIM
/// (argument-list launch) — it is never recanonicalized.
/// </summary>
/// <param name="Disposition">One of <see cref="ReviewerModelDisposition"/>.</param>
/// <param name="CanonicalRequestedModel">A stable, display-friendly normalization of the requested
/// model (lower-cased, trimmed). Populated on <see cref="ReviewerModelDisposition.Accept"/>.</param>
/// <param name="LaunchModel">The exact string the launcher will run with (passed through verbatim).
/// Populated on <see cref="ReviewerModelDisposition.Accept"/>.</param>
/// <param name="EquivalenceKey">The stable canonical model identity (the anchor). It MUST be identical
/// for a bare alias and for the dated/concrete id that alias resolves to, so the server can validate
/// the daemon's later concrete-model report by equivalence-key EQUALITY rather than a raw string
/// compare. Non-null on accept.</param>
/// <param name="DiagnosticCode">On <see cref="ReviewerModelDisposition.VendorMismatch"/> this names the
/// ordinal-first OTHER advertised vendor that recognizes the model; on
/// <see cref="ReviewerModelDisposition.Invalid"/> it optionally carries a bounded reason token.</param>
internal sealed record ReviewerModelResolution(
    string  Disposition,
    string? CanonicalRequestedModel = null,
    string? LaunchModel             = null,
    string? EquivalenceKey          = null,
    string? DiagnosticCode          = null);

/// <summary>The internal disposition set produced by a resolver / the cross-vendor coordinator. Task 8
/// maps these onto the server's <c>ResolveReviewerModel</c> RPC wire response (accepted / unavailable /
/// invalid, plus the recognized-vendor field for a mismatch); this daemon-internal set stays clean and
/// complete so that mapping is mechanical.</summary>
internal static class ReviewerModelDisposition {
    /// <summary>The selected vendor's resolver recognizes the model.</summary>
    public const string Accept         = "accept";

    /// <summary>The selected vendor rejects it, but at least one OTHER advertised resolver recognizes
    /// it — the model belongs to a different vendor. <see cref="ReviewerModelResolution.DiagnosticCode"/>
    /// names the ordinal-first such vendor.</summary>
    public const string VendorMismatch = "vendor_mismatch";

    /// <summary>No advertised resolver recognizes the model.</summary>
    public const string Unavailable    = "unavailable";

    /// <summary>The requested model fails basic syntax hygiene (empty, whitespace, control chars, or
    /// over length) — malformed regardless of vendor.</summary>
    public const string Invalid        = "invalid";
}

/// <summary>Per-vendor reviewer-model policy: recognizes the vendor's genuinely-known model aliases/ids
/// and canonicalizes them to a stable equivalence key. Owned by the vendor's launcher/runtime — there
/// is no central model registry. <see cref="Resolve"/> returns
/// <see cref="ReviewerModelDisposition.Accept"/>, <see cref="ReviewerModelDisposition.Unavailable"/>, or
/// <see cref="ReviewerModelDisposition.Invalid"/> for a SINGLE vendor; the cross-vendor
/// <see cref="ReviewerModelDisposition.VendorMismatch"/> conclusion is drawn by
/// <see cref="ReviewerModelResolvers"/> across all advertised resolvers.</summary>
internal interface IReviewerModelResolver {
    /// <summary>Vendor token this resolver handles ("claude", "codex").</summary>
    string Vendor { get; }

    /// <summary>Version of THIS vendor's reviewer-model policy — advertised on the vendor's
    /// unattended capability and echoed by the server so a policy upgrade mid-flight is detected.
    /// Distinct from the launcher's unattended policy version.</summary>
    string PolicyVersion { get; }

    /// <summary>Resolve one requested model for this vendor. Must never throw for hostile input —
    /// malformed input returns <see cref="ReviewerModelDisposition.Invalid"/>, an unrecognized (but
    /// well-formed) model returns <see cref="ReviewerModelDisposition.Unavailable"/>, and a recognized
    /// model returns <see cref="ReviewerModelDisposition.Accept"/> WITH a non-null equivalence-key
    /// anchor.</summary>
    ReviewerModelResolution Resolve(string requestedModel);
}

/// <summary>Universal, vendor-neutral syntax hygiene for a requested reviewer-model id. This is NOT a
/// vendor→model table — it only rejects strings that no vendor could accept as a model id (empty,
/// whitespace-bearing, control-char-bearing, or absurdly long).</summary>
internal static class ReviewerModelSyntax {
    /// <summary>Upper bound on a model-id length. Any real model id is far shorter; a longer string is
    /// treated as malformed rather than fed to a launcher argument.</summary>
    public const int MaxLength = 200;

    /// <summary>True when <paramref name="requestedModel"/> is a plausible model id: non-empty after
    /// trimming, within <see cref="MaxLength"/>, and free of interior whitespace and control
    /// characters.</summary>
    public static bool IsWellFormed(string? requestedModel) {
        if (string.IsNullOrWhiteSpace(requestedModel)) return false;

        var trimmed = requestedModel.Trim();
        if (trimmed.Length == 0 || trimmed.Length > MaxLength) return false;

        foreach (var c in trimmed)
            if (char.IsWhiteSpace(c) || char.IsControl(c)) return false;

        return true;
    }
}

/// <summary>
/// Cross-vendor coordinator: given the selected vendor and every advertised per-vendor resolver, folds
/// the individual accept/unavailable/invalid dispositions into the final
/// accept/vendor_mismatch/unavailable/invalid outcome. Resolvers are consulted in a DETERMINISTIC
/// ordinal order by vendor so the "ordinal-first diagnostic vendor" reported for a mismatch is stable.
/// </summary>
internal static class ReviewerModelResolvers {
    /// <summary>The reviewer-model preflight RPC PROTOCOL version this daemon speaks — returned verbatim
    /// as <see cref="ReviewerModelResolveResponseV1.PolicyVersion"/> and compared by the server against
    /// the <see cref="ReviewerModelResolveRequestV1.ExpectedPolicyVersion"/> it sent, so a mismatched
    /// protocol version on either side fails the preflight CLOSED (never silently trusted). This is the
    /// RPC-envelope version, distinct from a per-vendor resolver's <see cref="IReviewerModelResolver.PolicyVersion"/>
    /// (which the daemon advertises on its capability and echoes on the post-launch resolved report).
    /// MUST match the server's <c>ReviewerModelResolution.PolicyVersion</c>.</summary>
    public const string RpcProtocolVersion = "reviewer_model_resolve_v1";

    /// <summary>Resolve <paramref name="requestedModel"/> for <paramref name="vendor"/> against the full
    /// advertised resolver set. Vendor-neutral: it never inspects the model string's vendor itself, only
    /// asks each resolver.</summary>
    public static ReviewerModelResolution Resolve(
            string vendor, string requestedModel, IReadOnlyList<IReviewerModelResolver> resolvers) {
        // Universal syntax hygiene first: a malformed request is invalid regardless of vendor and can
        // never become a vendor mismatch, so it short-circuits before any resolver is consulted.
        if (!ReviewerModelSyntax.IsWellFormed(requestedModel))
            return new(ReviewerModelDisposition.Invalid, DiagnosticCode: "malformed_model_id");

        // Deterministic ordinal ordering so the "ordinal-first diagnostic vendor" reported for a
        // mismatch is stable across restarts and independent of registration order.
        var ordered = resolvers
            .OrderBy(r => r.Vendor, StringComparer.Ordinal)
            .ToList();

        var selected = ordered.FirstOrDefault(r => string.Equals(r.Vendor, vendor, StringComparison.Ordinal));

        // No resolver advertised for the selected vendor — nothing here can resolve it. Fail closed as
        // unavailable rather than probing other vendors (the selected vendor is what the user picked).
        if (selected is null)
            return new(ReviewerModelDisposition.Unavailable);

        var result = selected.Resolve(requestedModel);

        // The selected vendor considered it malformed on its own terms (invalid) — terminal; do not
        // reinterpret as another vendor's model.
        if (result.Disposition == ReviewerModelDisposition.Invalid)
            return result;

        // The selected vendor ANCHORED-accepted it (accept WITH a non-null equivalence key) — terminal.
        if (IsAnchoredAccept(result))
            return result;

        // Selected vendor doesn't (validly) recognize it — a plain unavailable, OR the central anchor
        // guard demoted an anchorless "accept" to unavailable (see IsAnchoredAccept). Scan the OTHER
        // advertised resolvers in ordinal order; the first that ANCHORED-accepts it makes this a vendor
        // mismatch naming that vendor.
        foreach (var other in ordered) {
            if (ReferenceEquals(other, selected)) continue;
            if (IsAnchoredAccept(other.Resolve(requestedModel)))
                return new(ReviewerModelDisposition.VendorMismatch, DiagnosticCode: other.Vendor);
        }

        return new(ReviewerModelDisposition.Unavailable);
    }

    /// <summary>Central anchor guard (Task-7 review Minor): only an <see cref="ReviewerModelDisposition.Accept"/>
    /// carrying a non-null <see cref="ReviewerModelResolution.EquivalenceKey"/> is a REAL accept. A resolver
    /// that returns accept without an anchor would ship a silent <c>no_anchor</c> bug — the server's
    /// post-launch echo validation then has nothing to validate the report against. Enforcing the
    /// invariant HERE (in the one coordinator) rather than trusting every resolver means a future
    /// resolver can't leak an anchorless accept: it is treated as a non-recognition (demoted to
    /// unavailable) both for the selected vendor and for cross-vendor recognition.</summary>
    static bool IsAnchoredAccept(ReviewerModelResolution r) =>
        r.Disposition == ReviewerModelDisposition.Accept && r.EquivalenceKey is not null;
}
