namespace Capacitor.Cli.Core.Acp;

using System.Text.Json;
using Capacitor.Cli.Core.Policy;

/// <summary>
/// Maps an ACP <c>toolCall</c> onto the vendor-neutral policy vocabulary. Never throws and never
/// skips: an unmapped kind, or one missing the field its mapping requires, yields
/// <see cref="ActionKind.Other"/> with the raw payload, so no ACP tool call escapes evaluation.
/// </summary>
public static class AcpActionNormalizer {
    public static CanonicalAction Normalize(JsonElement toolCall, string vendor, string? cwd) {
        try { return NormalizeCore(toolCall, vendor, cwd); }
        catch {
            // Other() reads the element too (GetRawText), so a payload that made NormalizeCore
            // throw — a disposed backing document, say — can make the fallback throw as well.
            try { return Other(toolCall, vendor, cwd); }
            catch { return new() { Kind = ActionKind.Other, Vendor = vendor, Cwd = cwd }; }
        }
    }

    static CanonicalAction NormalizeCore(JsonElement toolCall, string vendor, string? cwd) {
        var kind     = toolCall.Str("kind");
        var rawInput = toolCall.Obj("rawInput");

        switch (kind) {
            case "execute" when rawInput?.Str("command") is { Length: > 0 } command: {
                var analysis = ShellCommandAnalyzer.Analyze(command);

                return new() {
                    Kind = ActionKind.Shell, Vendor = vendor, Cwd = cwd,
                    Command = command, Analyzed = analysis.Analyzed, Segments = analysis.Segments,
                };
            }
            case "read" or "search" or "edit" or "move" or "delete": {
                var paths = new List<string>();

                if (toolCall.Arr("locations") is { } locations)
                    foreach (var location in locations.EnumerateArray())
                        if (location.Str("path") is { Length: > 0 } p && LexicalPaths.TryResolve(cwd, p) is { } resolved)
                            paths.Add(resolved);

                if (paths.Count == 0 && rawInput?.Str("path") is { Length: > 0 } single
                 && LexicalPaths.TryResolve(cwd, single) is { } fallback)
                    paths.Add(fallback);

                if (paths.Count == 0) break;

                return new() {
                    Kind = kind is "read" or "search" ? ActionKind.FileRead : ActionKind.FileEdit,
                    Vendor = vendor, Cwd = cwd, Paths = paths,
                };
            }
            case "fetch" when rawInput?.Str("url") is { Length: > 0 } url
                           && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IdnHost.Length > 0:
                return new() {
                    Kind = ActionKind.Network, Vendor = vendor, Cwd = cwd, Url = url,
                    Host = uri.IdnHost.ToLowerInvariant(), Port = uri.IsDefaultPort ? null : uri.Port,
                };
        }

        return Other(toolCall, vendor, cwd);
    }

    static CanonicalAction Other(JsonElement toolCall, string vendor, string? cwd) => new() {
        Kind = ActionKind.Other, Vendor = vendor, Cwd = cwd,
        RawToolName = toolCall.Str("kind") ?? toolCall.Str("title"),
        RawPayloadJson = toolCall.GetRawText(),
    };
}
