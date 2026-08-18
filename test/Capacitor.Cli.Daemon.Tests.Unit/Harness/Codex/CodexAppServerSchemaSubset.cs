using System.Text.Json;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Codex;

/// <summary>
/// Extracts and canonicalizes the subset of the <c>codex app-server</c> protocol JSON schema that the
/// app-server runtime layer actually reads, so a Codex version that changes a depended-on shape is
/// caught by a diff instead of failing silently at launch. The full protocol schema drifts benignly
/// across versions (0.147 adds <c>threadSection/*</c>; the combined schema already carries 557 defs),
/// so a whole-file diff would be noise — this pins only the byte-stable set the app-server protocol
/// spike verified, closed over its <c>#/definitions/*</c> refs so a change to any referenced shape is
/// pinned too.
/// </summary>
internal static class CodexAppServerSchemaSubset {
    /// <summary>The combined all-types schema the generator emits (a <c>definitions</c> map of the
    /// whole v2 protocol). Its layout is itself part of what we depend on — a rename must fail the
    /// extractor loudly rather than silently pin nothing.</summary>
    public const string CombinedSchemaFileName = "codex_app_server_protocol.v2.schemas.json";

    /// <summary>Root defs in the combined schema the runtime reads. The extractor closes over their
    /// <c>#/definitions/*</c> refs, so a change to a referenced shape is pinned too. Mirrors the exact
    /// requests/responses/notifications <c>CodexAppServerHostedAgentRuntime</c> issues and parses.</summary>
    public static readonly IReadOnlyList<string> RootDefs = [
        "InitializeParams",
        "ThreadStartParams",   "ThreadStartResponse",
        "ThreadResumeParams",  "ThreadResumeResponse",
        "TurnStartParams",     "TurnStartResponse",
        "TurnSteerParams",     "TurnSteerResponse",
        "TurnInterruptParams", "TurnInterruptResponse",
        "TurnCompletedNotification",
        "ThreadTokenUsageUpdatedNotification",
        "HooksListParams",     "HooksListResponse",
        "SandboxPolicy",       "AskForApproval",
    ];

    /// <summary>Server→client approval / elicitation request+response shapes the decline bridge answers
    /// — emitted as self-contained schema files, NOT entries in the combined definitions map.</summary>
    public static readonly IReadOnlyList<string> StandaloneFiles = [
        "CommandExecutionRequestApprovalParams.json", "CommandExecutionRequestApprovalResponse.json",
        "FileChangeRequestApprovalParams.json",       "FileChangeRequestApprovalResponse.json",
        "PermissionsRequestApprovalParams.json",      "PermissionsRequestApprovalResponse.json",
        "McpServerElicitationRequestParams.json",     "McpServerElicitationRequestResponse.json",
        "ToolRequestUserInputParams.json",            "ToolRequestUserInputResponse.json",
    ];

    const string RefPrefix = "#/definitions/";

    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Reads a <c>generate-json-schema --out</c> directory and returns the canonical pin
    /// object <c>{ codexVersion, combinedDefs, standalone }</c>, every nested object key sorted so
    /// re-serialization is order-stable within a process.</summary>
    public static JsonObject Extract(string schemaDir, string codexVersion) {
        var combinedPath = Path.Combine(schemaDir, CombinedSchemaFileName);
        if (!File.Exists(combinedPath))
            throw new FileNotFoundException(
                $"codex app-server combined schema '{CombinedSchemaFileName}' not found in '{schemaDir}'. "
              + "The generator layout changed — re-vet the pinning extractor before trusting a pass.", combinedPath);

        var defs = (JsonNode.Parse(File.ReadAllText(combinedPath)) as JsonObject)?["definitions"] as JsonObject
            ?? throw new InvalidOperationException(
                "codex app-server combined schema has no 'definitions' map — layout changed, re-vet the extractor.");

        var combinedDefs = new JsonObject();
        foreach (var name in ComputeClosure(defs).OrderBy(x => x, StringComparer.Ordinal))
            combinedDefs[name] = Canonicalize(defs[name]);

        var standalone = new JsonObject();
        foreach (var file in StandaloneFiles.OrderBy(x => x, StringComparer.Ordinal)) {
            var path = Path.Combine(schemaDir, file);
            if (!File.Exists(path))
                throw new FileNotFoundException(
                    $"codex app-server standalone schema '{file}' not found in '{schemaDir}' — "
                  + "layout changed, re-vet the extractor.", path);
            standalone[file] = Canonicalize(JsonNode.Parse(File.ReadAllText(path)));
        }

        return new JsonObject {
            ["codexVersion"] = codexVersion,
            ["combinedDefs"] = combinedDefs,
            ["standalone"]   = standalone,
        };
    }

    /// <summary>Indented serialization — writes the committed pin and, applied to both sides through
    /// <see cref="Canonical"/>, provides a by-value comparison whose string equality is reliable
    /// because both operands are re-serialized in the same process.</summary>
    public static string Serialize(JsonNode node) => node.ToJsonString(Indented);

    /// <summary>Canonical string form of a node (keys sorted, indented) for by-value comparison.</summary>
    public static string Canonical(JsonNode? node) => Serialize(Canonicalize(node) ?? new JsonObject());

    static HashSet<string> ComputeClosure(JsonObject defs) {
        var closure  = new HashSet<string>(StringComparer.Ordinal);
        var frontier = new Queue<string>();

        foreach (var root in RootDefs) {
            if (!defs.ContainsKey(root))
                throw new InvalidOperationException(
                    $"codex app-server schema no longer defines depended-on type '{root}'. "
                  + "This is a breaking protocol change — re-vet before re-pinning.");
            if (closure.Add(root)) frontier.Enqueue(root);
        }

        while (frontier.Count > 0) {
            var name = frontier.Dequeue();
            foreach (var referenced in DefinitionRefs(defs[name]))
                if (defs.ContainsKey(referenced) && closure.Add(referenced))
                    frontier.Enqueue(referenced);
        }
        return closure;
    }

    // Every #/definitions/NAME ref reachable inside a node (recursively).
    static IReadOnlyList<string> DefinitionRefs(JsonNode? node) {
        var acc = new List<string>();
        Collect(node, acc);
        return acc;

        static void Collect(JsonNode? n, List<string> acc) {
            switch (n) {
                case JsonObject o:
                    foreach (var (key, value) in o) {
                        if (key == "$ref" && value is JsonValue v && v.TryGetValue<string>(out var s)
                            && s.StartsWith(RefPrefix, StringComparison.Ordinal))
                            acc.Add(s[RefPrefix.Length..]);
                        else
                            Collect(value, acc);
                    }
                    break;
                case JsonArray a:
                    foreach (var x in a) Collect(x, acc);
                    break;
            }
        }
    }

    // Deep copy with object keys sorted (ordinal) so serialization is order-stable.
    static JsonNode? Canonicalize(JsonNode? node) {
        switch (node) {
            case JsonObject o:
                var sorted = new JsonObject();
                foreach (var (key, value) in o.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                    sorted[key] = Canonicalize(value);
                return sorted;
            case JsonArray a:
                var arr = new JsonArray();
                foreach (var x in a) arr.Add(Canonicalize(x));
                return arr;
            default:
                return node?.DeepClone();
        }
    }
}
