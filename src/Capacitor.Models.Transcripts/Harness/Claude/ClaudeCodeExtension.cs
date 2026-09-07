using Google.Protobuf.WellKnownTypes;

namespace Capacitor.Models.Transcripts.Harness.Claude;

/// The claude_code extension slug: the coding-agent fields the schema keeps out of the canonical
/// payloads. Only the fields this projection writes are named here.
public static class ClaudeCodeExtension {
    public const string Slug        = "claude_code";
    public const string IsMeta      = "is_meta";
    public const string IsSidechain = "is_sidechain";
    public const string OriginKind  = "origin_kind";
    public const string IsError     = "is_error";

    /// The block for one event, or null when nothing is set so the slug stays absent.
    public static Struct? Flags(bool isSidechain, bool isMeta = false, string? originKind = null, bool isError = false) {
        if (!isSidechain && !isMeta && originKind is null && !isError) return null;
        var s = new Struct();
        if (isSidechain) s.Fields[IsSidechain] = Value.ForBool(true);
        if (isMeta) s.Fields[IsMeta]           = Value.ForBool(true);
        if (originKind is not null) s.Fields[OriginKind] = Value.ForString(originKind);
        if (isError) s.Fields[IsError]         = Value.ForBool(true);
        return s;
    }
}
