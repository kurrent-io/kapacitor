namespace Capacitor.Cli.Core.Harness.Claude;

/// <summary>The Claude CLI's <c>--permission-mode</c> tokens the product offers, most to least
/// prompting. They reach argv verbatim, so this spelling is the contract the desktop chip and the
/// daemon's launch guard share; <c>plan</c> and <c>dontAsk</c> exist upstream but are not offered.</summary>
public static class ClaudePermissionModes {
    public const string Manual            = "manual";
    public const string AcceptEdits       = "acceptEdits";
    public const string Auto              = "auto";
    public const string BypassPermissions = "bypassPermissions";

    public static readonly IReadOnlyList<string> Offered = [Manual, AcceptEdits, Auto, BypassPermissions];

    public static bool IsOffered(string token) => Offered.Contains(token, StringComparer.Ordinal);
}
