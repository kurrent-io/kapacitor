using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// Outcome of a boot-time <see cref="LaunchConsentStore.BootSeed"/> classification (spec §6).
internal enum SeedOutcome { Seeded, Respected, Rewritten, Quarantined, RefusedInvalidDirective, RefusedUnwritable }

/// <see cref="SeedResult.RefusalToken"/> is non-null exactly when Outcome is one of the
/// Refused* arms — it is the coded reason the daemon writes to the boot-refusal marker.
internal sealed record SeedResult(SeedOutcome Outcome, string? RefusalToken);

/// Owns {stateDir}/consent.json. The running daemon is the SINGLE writer — the CLI and the
/// desktop app mutate it only via the local socket. Corrupt/missing file degrades to
/// LaunchConsentPolicy.UpgradeSafe: consent must never brick a daemon boot.
internal sealed partial class LaunchConsentStore {
    static readonly string[] ValidKinds = ["agent", "review", "review-flow"];

    readonly string _path;
    readonly ILogger _log;
    readonly object _gate = new();
    LaunchConsentPolicy _current;

    public LaunchConsentStore(string stateDir, ILogger logger) {
        Directory.CreateDirectory(stateDir);

        // Owner-only directory: consent.json holds requester ids and repo paths, so no other
        // local user should be able to traverse in even if a file mode slips.
        if (!OperatingSystem.IsWindows()) {
            try { File.SetUnixFileMode(stateDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            catch { /* best-effort */ }
        }

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
        var doc = new PolicyDoc(
            clamped.Default switch { LaunchConsentDefault.Deny => "deny", LaunchConsentDefault.Prompt => "prompt", _ => "allow" },
            clamped.PromptTimeoutSeconds,
            clamped.Rules.Select(r => new RuleDoc(r.Action, r.Requester, r.Kind, r.Repo, r.Vendor)).ToList(),
            "operator");
        lock (_gate) return Persist(doc, out error);
    }

    /// Boot-time seed classification (spec §6). Re-parses the raw file itself — unlike
    /// <see cref="Load"/>, which silently collapses an unrecognized `default` to Allow, this
    /// distinguishes absent / unreadable / malformed / null-doc / unrecognized-default so each
    /// gets quarantined rather than quietly defaulting to the most permissive policy.
    public SeedResult BootSeed(string directive) {
        if (directive != "prompt")
            return new SeedResult(SeedOutcome.RefusedInvalidDirective, "consent_seed_invalid");

        lock (_gate) {
            if (!File.Exists(_path))
                return Persist(FreshSeedDoc(), out _)
                    ? new SeedResult(SeedOutcome.Seeded, null)
                    : new SeedResult(SeedOutcome.RefusedUnwritable, "consent_seed_unwritable");

            PolicyDoc? doc;
            bool recognized;
            try {
                doc = JsonSerializer.Deserialize(File.ReadAllText(_path), LaunchConsentJsonCtx.Default.PolicyDoc);
                recognized = doc is not null && doc.Default is "allow" or "deny" or "prompt" && RulesStructurallyValid(doc.Rules);
            } catch (Exception ex) {
                _log.LogWarning(ex, "Unreadable/malformed consent policy at {Path} during boot seed", _path);
                doc = null;
                recognized = false;
            }

            if (!recognized) {
                var quarantinePath = _path + ".quarantined-" + DateTime.UtcNow.Ticks;
                try {
                    File.Move(_path, quarantinePath, overwrite: true);
                } catch (Exception ex) {
                    _log.LogWarning(ex, "Failed to quarantine consent policy at {Path}", _path);
                    return new SeedResult(SeedOutcome.RefusedUnwritable, "consent_seed_unwritable");
                }
                return Persist(FreshSeedDoc(), out _)
                    ? new SeedResult(SeedOutcome.Quarantined, null)
                    : new SeedResult(SeedOutcome.RefusedUnwritable, "consent_seed_unwritable");
            }

            if (doc!.Default is "prompt" or "deny")
                return new SeedResult(SeedOutcome.Respected, null);

            // doc.Default == "allow" from here.
            if ((doc.Rules ?? []).Count > 0 || doc.DefaultSource == "operator")
                return new SeedResult(SeedOutcome.Respected, null);

            // Zero-rule allow stamped "seed" (or unstamped, i.e. a pre-Task-11 file) is
            // indistinguishable from the pre-consent factory default — rewrite it to Prompt
            // rather than let it silently keep the daemon wide open.
            var rewritten = doc with { Default = "prompt", DefaultSource = "seed" };
            return Persist(rewritten, out _)
                ? new SeedResult(SeedOutcome.Rewritten, null)
                : new SeedResult(SeedOutcome.RefusedUnwritable, "consent_seed_unwritable");
        }
    }

    /// Structural validation for BootSeed's raw re-parse: a recognized `default` with a malformed
    /// rules array (a null element, a bogus action, an unknown kind) must NOT count as Respected —
    /// <see cref="ToPolicy"/> silently drops such a rule, so an allow-with-rules doc would land as
    /// an effective zero-rule allow indistinguishable from the pre-consent factory default, never
    /// having actually been reviewed. Absent Rules is valid (an empty policy).
    static bool RulesStructurallyValid(List<RuleDoc>? rules) {
        if (rules is null) return true;

        foreach (var r in rules) {
            if (r is null) return false;
            if (r.Action is not ("allow" or "deny")) return false;
            if (r.Kind is not null && !ValidKinds.Contains(r.Kind)) return false;
        }

        return true;
    }

    static PolicyDoc FreshSeedDoc() =>
        new("prompt", LaunchConsentPolicy.UpgradeSafe.PromptTimeoutSeconds, [], "seed");

    /// Shared temp+rename+0600 writer for both <see cref="TryReplace"/> (stamps "operator" into
    /// the doc before calling this) and <see cref="BootSeed"/> (stamps "seed"). Caller holds
    /// _gate; updates _current on success.
    bool Persist(PolicyDoc doc, out string? error) {
        try {
            var tmp = _path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
            var json = JsonSerializer.Serialize(doc, LaunchConsentJsonCtx.Default.PolicyDoc);

            // Write via FileStream with UnixCreateMode so the temp file is owner-only from its
            // first byte — a chmod after WriteAllText would leave a world-readable window.
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

            using (var fs = new FileStream(tmp, options)) {
                fs.Write(Encoding.UTF8.GetBytes(json));
            }

            File.Move(tmp, _path, overwrite: true);

            // Re-assert owner-only on the published path: `overwrite: true` may have replaced
            // a pre-existing final file, closing any platform gap in what the rename carries across.
            if (!OperatingSystem.IsWindows()) {
                try { File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
                catch { /* best-effort */ }
            }

            _current = ToPolicy(doc);
            error = null;
            return true;
        } catch (Exception ex) {
            _log.LogWarning(ex, "Failed to persist consent policy to {Path}", _path);
            error = "failed to persist consent policy";
            return false;
        }
    }

    LaunchConsentPolicy Load() {
        try {
            if (!File.Exists(_path)) return LaunchConsentPolicy.UpgradeSafe;
            var doc = JsonSerializer.Deserialize(File.ReadAllText(_path), LaunchConsentJsonCtx.Default.PolicyDoc);
            return doc is null ? LaunchConsentPolicy.UpgradeSafe : ToPolicy(doc);
        } catch (Exception ex) {
            _log.LogWarning(ex, "Corrupt consent policy at {Path}; using upgrade-safe default (allow)", _path);
            return LaunchConsentPolicy.UpgradeSafe;
        }
    }

    // Unknown `Default` silently collapses to Allow here — this is the exact collapse BootSeed's
    // raw re-parse exists to avoid conflating with a deliberately-set Allow.
    static LaunchConsentPolicy ToPolicy(PolicyDoc doc) {
        var def = doc.Default switch { "deny" => LaunchConsentDefault.Deny, "prompt" => LaunchConsentDefault.Prompt, _ => LaunchConsentDefault.Allow };
        var rules = (doc.Rules ?? [])
            .Where(r => r.Action is "allow" or "deny")
            .Select(r => new LaunchConsentRule(r.Action, r.Requester, r.Kind, r.Repo, r.Vendor))
            .ToList();
        return new LaunchConsentPolicy(def, Math.Clamp(doc.PromptTimeoutSeconds ?? 45, 5, 300), rules);
    }

    internal sealed record PolicyDoc(string? Default, int? PromptTimeoutSeconds, List<RuleDoc>? Rules, string? DefaultSource = null);
    internal sealed record RuleDoc(string Action, string? Requester, string? Kind, string? Repo, string? Vendor);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true)]
    [JsonSerializable(typeof(PolicyDoc))]
    partial class LaunchConsentJsonCtx : JsonSerializerContext;
}
