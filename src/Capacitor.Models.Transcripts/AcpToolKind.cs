namespace Capacitor.Models.Transcripts;

/// <summary>
/// The vendor-neutral vocabulary for a tool call's kind, on the daemon envelope and the persisted
/// <c>ToolCallInfo</c> alike: the ACP <c>ToolKind</c> tokens, which ACP vendors already put on the
/// wire and the other vendors' lanes map their raw tool names onto. Closed set: a consumer may switch
/// on these ten and need no per-vendor name table.
/// </summary>
public static class AcpToolKind {
    public const string Read       = "read";
    public const string Edit       = "edit";
    public const string Delete     = "delete";
    public const string Move       = "move";
    public const string Search     = "search";
    public const string Execute    = "execute";
    public const string Think      = "think";
    public const string Fetch      = "fetch";
    public const string SwitchMode = "switch_mode";
    public const string Other      = "other";

    /// <summary>Maps an agent-supplied kind onto the closed set: a recognised token passes through,
    /// anything else present becomes <see cref="Other"/>, and an absent one stays null. Null is what
    /// tells a consumer no lane classified this call — never that the call was none-of-the-above.</summary>
    public static string? Normalize(string? kind) =>
        string.IsNullOrWhiteSpace(kind)                                                                        ? null
        : kind is Read or Edit or Delete or Move or Search or Execute or Think or Fetch or SwitchMode or Other ? kind
        : Other;
}
