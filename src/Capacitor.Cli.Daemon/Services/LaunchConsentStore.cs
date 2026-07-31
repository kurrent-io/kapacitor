using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// Owns {stateDir}/consent.json. The running daemon is the SINGLE writer — the CLI and the
/// desktop app mutate it only via the local socket (Task 7). Corrupt/missing file degrades to
/// LaunchConsentPolicy.UpgradeSafe: consent must never brick a daemon boot.
internal sealed partial class LaunchConsentStore {
    static readonly string[] ValidKinds = ["agent", "review", "review-flow"];

    readonly string _path;
    readonly ILogger _log;
    readonly object _gate = new();
    LaunchConsentPolicy _current;

    public LaunchConsentStore(string stateDir, ILogger logger) {
        Directory.CreateDirectory(stateDir);
        _path = Path.Combine(stateDir, "consent.json");
        _log = logger;
        _current = Load();
    }

    public LaunchConsentPolicy Current { get { lock (_gate) return _current; } }

    public bool TryReplace(LaunchConsentPolicy next, out string? error) {
        foreach (var r in next.Rules) {
            if (r.Action is not ("allow" or "deny")) { error = $"invalid rule action '{r.Action}' (allow|deny)"; return false; }
            if (r.Kind is not null && !ValidKinds.Contains(r.Kind)) { error = $"invalid rule kind '{r.Kind}' (agent|review|review-flow)"; return false; }
        }
        var clamped = next with { PromptTimeoutSeconds = Math.Clamp(next.PromptTimeoutSeconds, 5, 300) };
        lock (_gate) {
            try {
                var doc = new PolicyDoc(
                    clamped.Default switch { LaunchConsentDefault.Deny => "deny", LaunchConsentDefault.Prompt => "prompt", _ => "allow" },
                    clamped.PromptTimeoutSeconds,
                    clamped.Rules.Select(r => new RuleDoc(r.Action, r.Requester, r.Kind, r.Repo, r.Vendor)).ToList());
                var tmp = _path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
                File.WriteAllText(tmp, JsonSerializer.Serialize(doc, LaunchConsentJsonCtx.Default.PolicyDoc));
                File.Move(tmp, _path, overwrite: true);
                _current = clamped;
                error = null;
                return true;
            } catch (Exception ex) {
                _log.LogWarning(ex, "Failed to persist consent policy to {Path}", _path);
                error = "failed to persist consent policy";
                return false;
            }
        }
    }

    LaunchConsentPolicy Load() {
        try {
            if (!File.Exists(_path)) return LaunchConsentPolicy.UpgradeSafe;
            var doc = JsonSerializer.Deserialize(File.ReadAllText(_path), LaunchConsentJsonCtx.Default.PolicyDoc);
            if (doc is null) return LaunchConsentPolicy.UpgradeSafe;
            var def = doc.Default switch { "deny" => LaunchConsentDefault.Deny, "prompt" => LaunchConsentDefault.Prompt, _ => LaunchConsentDefault.Allow };
            var rules = (doc.Rules ?? [])
                .Where(r => r.Action is "allow" or "deny")
                .Select(r => new LaunchConsentRule(r.Action, r.Requester, r.Kind, r.Repo, r.Vendor))
                .ToList();
            return new LaunchConsentPolicy(def, Math.Clamp(doc.PromptTimeoutSeconds ?? 45, 5, 300), rules);
        } catch (Exception ex) {
            _log.LogWarning(ex, "Corrupt consent policy at {Path}; using upgrade-safe default (allow)", _path);
            return LaunchConsentPolicy.UpgradeSafe;
        }
    }

    internal sealed record PolicyDoc(string? Default, int? PromptTimeoutSeconds, List<RuleDoc>? Rules);
    internal sealed record RuleDoc(string Action, string? Requester, string? Kind, string? Repo, string? Vendor);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true)]
    [JsonSerializable(typeof(PolicyDoc))]
    partial class LaunchConsentJsonCtx : JsonSerializerContext;
}
