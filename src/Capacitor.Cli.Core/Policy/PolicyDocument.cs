namespace Capacitor.Cli.Core.Policy;

public enum PolicyScope { Org, Team, Project, Repo, User }
public enum RuleOutcome { Allow, Ask, Deny }

public sealed record RuleMatcher(
    ActionKind Kind,
    IReadOnlyList<string> Command,
    bool Exact,
    IReadOnlyList<string> Path,
    IReadOnlyList<string> Host,
    int? Port,
    IReadOnlyList<string> Server,
    IReadOnlyList<string> Tool);

public sealed record PolicyRule(RuleMatcher Match, RuleOutcome Outcome, string? Reason);

/// <summary>Parsed and preserved; never consulted until the judge ships.</summary>
public sealed record JudgeConfig(string Mode, string? Prompt);

public sealed record PolicyDocument(int Version, IReadOnlyList<PolicyRule> Rules, JudgeConfig? Judge);
public sealed class PolicyDocumentException(string message) : Exception(message);

public static class PolicyDocumentBinder {
    public const int MaxRules = 500;
    public const int MaxPatternsPerMatcher = 32;

    public static PolicyDocument Bind(string yamlText, PolicyScope scope) {
        YamlMapping root;
        try { root = ApprovalsYaml.Parse(yamlText); }
        catch (ApprovalsYamlException e) { throw new PolicyDocumentException(e.Message); }

        foreach (var e in root.Entries)
            if (e.Key is not ("version" or "rules" or "judge" or "caps" or "enforcement"))
                throw new PolicyDocumentException($"unknown key '{e.Key}'");
        if (scope is PolicyScope.Repo or PolicyScope.User)
            foreach (var key in (string[])["caps", "enforcement"])
                if (root[key] is not null)
                    throw new PolicyDocumentException($"'{key}' is a server-scope field and invalid in a {ScopeName(scope)} document");

        if (root["version"] is not YamlScalar { Value: "1" })
            throw new PolicyDocumentException("'version' must be 1");

        var rules = new List<PolicyRule>();
        if (root["rules"] is { } rulesNode) {
            if (rulesNode is not YamlSequence seq) throw new PolicyDocumentException("'rules' must be a sequence");
            if (seq.Items.Count > MaxRules) throw new PolicyDocumentException($"more than {MaxRules} rules");
            foreach (var item in seq.Items) rules.Add(BindRule(item));
        }
        return new PolicyDocument(1, rules, BindJudge(root["judge"]));
    }

    static PolicyRule BindRule(YamlNode item) {
        if (item is not YamlMapping rule) throw new PolicyDocumentException("each rule must be a mapping");
        foreach (var e in rule.Entries)
            if (e.Key is not ("match" or "outcome" or "reason"))
                throw new PolicyDocumentException($"unknown rule key '{e.Key}'");
        if (rule["match"] is not YamlMapping match) throw new PolicyDocumentException("rule is missing 'match'");
        var outcome = rule["outcome"] is YamlScalar o
            ? o.Value switch {
                "allow" => RuleOutcome.Allow, "ask" => RuleOutcome.Ask, "deny" => RuleOutcome.Deny,
                _ => throw new PolicyDocumentException($"unknown outcome '{o.Value}'"),
            }
            : throw new PolicyDocumentException("rule is missing 'outcome'");
        var reason = rule["reason"] switch {
            null => null,
            YamlScalar s => s.Value,
            _ => throw new PolicyDocumentException("'reason' must be a string"),
        };
        return new PolicyRule(BindMatcher(match), outcome, reason);
    }

    static readonly Dictionary<ActionKind, string[]> FieldsByKind = new() {
        [ActionKind.Shell] = ["command", "exact"],
        [ActionKind.FileEdit] = ["path"],
        [ActionKind.FileRead] = ["path"],
        [ActionKind.Network] = ["host", "port"],
        [ActionKind.McpTool] = ["server", "tool"],
        [ActionKind.Other] = ["tool"],
    };

    static RuleMatcher BindMatcher(YamlMapping match) {
        var kindName = match["kind"] is YamlScalar k
            ? k.Value
            : throw new PolicyDocumentException("matcher is missing 'kind'");
        var kind = kindName switch {
            "shell" => ActionKind.Shell, "file_edit" => ActionKind.FileEdit, "file_read" => ActionKind.FileRead,
            "network" => ActionKind.Network, "mcp_tool" => ActionKind.McpTool, "other" => ActionKind.Other,
            _ => throw new PolicyDocumentException($"unknown kind '{kindName}'"),
        };
        foreach (var e in match.Entries)
            if (e.Key != "kind" && !FieldsByKind[kind].Contains(e.Key))
                throw new PolicyDocumentException($"'{e.Key}' is not a matcher field for kind '{kindName}'");

        var command = Patterns(match["command"], "command");
        foreach (var p in command)
            if (ShellTokenPattern.Parse(p) is null)
                throw new PolicyDocumentException("empty pattern in 'command'");
        var exact = match["exact"] switch {
            null => false,
            YamlScalar { Value: "true" } => true,
            YamlScalar { Value: "false" } => false,
            _ => throw new PolicyDocumentException("'exact' must be true or false"),
        };
        int? port = match["port"] switch {
            null => null,
            YamlScalar p when int.TryParse(p.Value, out var n) && n is > 0 and <= 65535 => n,
            _ => throw new PolicyDocumentException("'port' must be a port number"),
        };
        return new RuleMatcher(kind, command, exact,
            Patterns(match["path"], "path"), Patterns(match["host"], "host"), port,
            Patterns(match["server"], "server"), Patterns(match["tool"], "tool"));
    }

    static IReadOnlyList<string> Patterns(YamlNode? node, string field) {
        var values = node switch {
            null => (List<string>)[],
            YamlScalar s => [s.Value],
            YamlSequence seq => [.. seq.Items.Select(i => i is YamlScalar s
                ? s.Value
                : throw new PolicyDocumentException($"'{field}' entries must be strings"))],
            _ => throw new PolicyDocumentException($"'{field}' must be a string or a list of strings"),
        };
        if (values.Count > MaxPatternsPerMatcher)
            throw new PolicyDocumentException($"more than {MaxPatternsPerMatcher} patterns in '{field}'");
        foreach (var v in values)
            if (string.IsNullOrWhiteSpace(v))
                throw new PolicyDocumentException($"empty pattern in '{field}'");
        return values;
    }

    static JudgeConfig? BindJudge(YamlNode? node) {
        if (node is null) return null;
        if (node is not YamlMapping judge) throw new PolicyDocumentException("'judge' must be a mapping");
        foreach (var e in judge.Entries)
            if (e.Key is not ("mode" or "prompt"))
                throw new PolicyDocumentException($"unknown judge key '{e.Key}'");
        var mode = judge["mode"] is YamlScalar m
            ? m.Value is "off" or "unmatched"
                ? m.Value
                : throw new PolicyDocumentException($"unknown judge mode '{m.Value}'")
            : throw new PolicyDocumentException("'judge' is missing 'mode'");
        var prompt = judge["prompt"] switch {
            null => null,
            YamlScalar p => p.Value,
            _ => throw new PolicyDocumentException("'prompt' must be a string"),
        };
        return new JudgeConfig(mode, prompt);
    }

    static string ScopeName(PolicyScope scope) => scope.ToString().ToLowerInvariant();
}
