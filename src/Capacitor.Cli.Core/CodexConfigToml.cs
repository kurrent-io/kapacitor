using Capacitor.Cli.Core.Mcp;
using System.Collections;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Serialization;

namespace Capacitor.Cli.Core;

/// <summary>
/// Read-modify-write engine for <c>~/.codex/config.toml</c>. Owns the AOT-safe
/// Tomlyn plumbing (load, atomic write, untyped-model type info) so both the
/// daemon's per-worktree pre-trust (<see cref="TrustWorktree"/>) and the setup
/// wizard's sandbox network-access opt-in (<see cref="EnableNetworkAccess"/>)
/// share one code path.
///
/// Tomlyn's TomlTable mode round-trips preserve all other top-level tables
/// (model, mcp_servers, plugins, marketplaces, ad-hoc user keys) but DO NOT
/// preserve comments or original formatting. Acceptable because the file is
/// largely tool-managed and human edits are expected to be sparse.
/// </summary>
public static class CodexConfigToml {
    static readonly Lock              _writeLock    = new();
    static readonly TomlTableTypeInfo _tomlTypeInfo = new();

    public enum Change {
        Unchanged,
        Updated,
        UpdatedWithPreservedEntries,
        PreservedUnownedEntries,
        PreservedOwnershipUnknown,
        Failed
    }

    static string DefaultConfigPath => Path.Combine(CodexPaths.Home(), "config.toml");

    /// <summary>
    /// enable network access for Codex's <c>workspace-write</c> sandbox so
    /// kcap skills (which shell out to <c>kcap …</c>) can reach the Capacitor server.
    /// Codex blocks all sandbox network by default; a constrained
    /// <c>network_proxy</c> allowlist keeps everything else closed.
    ///
    /// The change respects what the user already has (see the truth table below).
    /// <paramref name="allowDomains"/> is the host allowlist to permit, e.g.
    /// <c>["**.kcap.ai"]</c> for SaaS plus any self-hosted hosts — build it with
    /// <see cref="BuildAllowDomains"/>. An empty allowlist is a no-op (enabling the
    /// proxy with no <c>allow</c> rule would deny everything, breaking kcap too).
    ///
    /// <list type="bullet">
    /// <item>Default config (no network) → set <c>network_access=true</c> +
    ///   <c>network_proxy.enabled=true</c> + the allowlist. Only the allowlisted
    ///   hosts become reachable; everything else stays blocked as before.</item>
    /// <item><c>network_proxy.enabled=true</c> already → merge the allow entries into
    ///   the user's <c>domains</c> (theirs preserved) and ensure
    ///   <c>network_access=true</c>.</item>
    /// <item><c>network_access=true</c>, no active proxy (fully open) → no-op; kcap
    ///   already works and pinning a proxy would only narrow their access.</item>
    /// </list>
    /// </summary>
    public static Change EnableNetworkAccess(IReadOnlyCollection<string> allowDomains, string? configPath = null) =>
        allowDomains.Count == 0
            ? Change.Unchanged
            : Update(configPath ?? DefaultConfigPath, root => MutateNetworkAccess(root, allowDomains), out _);

    /// <summary>
    /// Writes the per-worktree pre-trust entry:
    /// <code>
    /// [projects."/abs/worktree/path"]
    /// trust_level = "trusted"
    /// </code>
    /// </summary>
    public static Change TrustWorktree(string worktreePath, string? configPath = null) =>
        TrustWorktree(worktreePath, out _, configPath);

    /// <summary>
    /// As <see cref="TrustWorktree(string,string?)"/>, but surfaces the underlying
    /// exception on <see cref="Change.Failed"/> so the daemon can log the root cause
    /// (parse vs permissions vs IO) it would otherwise lose to the swallowing
    /// <see cref="Update"/>. <paramref name="error"/> is null unless the result is
    /// <see cref="Change.Failed"/>.
    /// </summary>
    internal static Change TrustWorktree(string worktreePath, out Exception? error, string? configPath = null) =>
        Update(configPath ?? DefaultConfigPath, root => MutateTrust(root, worktreePath), out error);

    /// <summary>
    /// Registers the kcap MCP servers (<see cref="KcapMcpServers.ForCodex"/>) under the
    /// top-level <c>[mcp_servers]</c> table of <c>~/.codex/config.toml</c> (or
    /// <paramref name="configPath"/>) so Codex CLI picks them up with no manual TOML edit.
    /// Idempotent, and non-destructive: an entry that already exists (a prior registration
    /// or a user customization such as an absolute-path <c>command</c>) is left untouched;
    /// only missing servers are added. User-defined <c>mcp_servers</c> entries are preserved.
    /// </summary>
    public static Change RegisterKcapMcpServers(string? configPath = null) =>
        UpdateMcpRegistration(configPath ?? DefaultConfigPath, remove: false);

    /// <summary>
    /// Removes the kcap-owned MCP server entries (<see cref="KcapMcpServers.ForCodex"/>)
    /// from <c>~/.codex/config.toml</c> (or <paramref name="configPath"/>). Only those names
    /// are touched; user-defined servers are preserved. Drops the <c>[mcp_servers]</c> table
    /// entirely when removing them empties it, so uninstall leaves no bare table behind.
    /// </summary>
    public static Change UnregisterKcapMcpServers(string? configPath = null) =>
        UpdateMcpRegistration(configPath ?? DefaultConfigPath, remove: true);

    static Change UpdateMcpRegistration(string configPath, bool remove) {
        lock (_writeLock) {
            try {
                configPath = CanonicalConfigPath(configPath);
                using var crossProcess = AcquireConfigLock(configPath);
                var root = File.Exists(configPath)
                    ? TomlSerializer.Deserialize(File.ReadAllText(configPath), _tomlTypeInfo.TableInfo) ?? new TomlTable()
                    : new TomlTable();
                if (root.TryGetValue("mcp_servers", out var existing) && existing is not TomlTable)
                    return Change.Failed;

                var ledgerPath = Path.Combine(Path.GetDirectoryName(configPath) ?? ".", "mcp-ownership-v1.json");
                var ledger = ReadOwnershipLedger(ledgerPath);
                var servers = root.TryGetValue("mcp_servers", out var serversObj) && serversObj is TomlTable found
                    ? found
                    : null;
                if (remove && ledger is null) {
                    var hasCanonicalEntries = servers is not null &&
                        KcapMcpServers.ForCodex.Any(d => servers.ContainsKey(d.Name));
                    return hasCanonicalEntries ? Change.PreservedOwnershipUnknown : Change.Unchanged;
                }
                ledger ??= NewOwnershipLedger();
                var claims = (JsonObject)ledger["entries"]!;
                var tomlChanged = false;
                var ledgerChanged = false;

                if (!remove) {
                    servers ??= GetOrAddTable(root, "mcp_servers");
                    foreach (var descriptor in KcapMcpServers.ForCodex) {
                        if (servers.TryGetValue(descriptor.Name, out var existingValue)) {
                            if (claims[descriptor.Name] is JsonObject claim && existingValue is TomlTable existingTable &&
                                !string.Equals(StringField(claim, "fingerprint"), Fingerprint(existingTable), StringComparison.Ordinal)) {
                                claims.Remove(descriptor.Name); // user changed an owned entry: relinquish it
                                ledgerChanged = true;
                            }
                            continue; // never mutate or claim a pre-existing/manual entry
                        }

                        var table = BuildMcpTable(descriptor);
                        servers[descriptor.Name] = table;
                        tomlChanged = true;
                        claims[descriptor.Name] = BuildClaim(table);
                        ledgerChanged = true;
                    }

                    // Safe crash ordering: config first, ownership claim second. A crash leaks an
                    // unowned entry, which uninstall deliberately preserves.
                    if (tomlChanged) {
                        EnsureParentDirectory(configPath);
                        WriteTomlAtomic(configPath, root);
                    }
                    if (ledgerChanged) WriteJsonAtomic(ledgerPath, ledger);
                    return tomlChanged || ledgerChanged ? Change.Updated : Change.Unchanged;
                }

                if (servers is null) {
                    foreach (var descriptor in KcapMcpServers.ForCodex)
                        ledgerChanged |= claims.Remove(descriptor.Name);
                    if (ledgerChanged) WriteJsonAtomic(ledgerPath, ledger);
                    return ledgerChanged ? Change.Updated : Change.Unchanged;
                }
                var removable = new List<string>();
                var preserved = false;
                foreach (var descriptor in KcapMcpServers.ForCodex) {
                    if (claims[descriptor.Name] is not JsonObject claim) {
                        preserved |= servers.ContainsKey(descriptor.Name);
                        continue;
                    }
                    if (!servers.TryGetValue(descriptor.Name, out var value) || value is not TomlTable table ||
                        !string.Equals(StringField(claim, "fingerprint"), Fingerprint(table), StringComparison.Ordinal)) {
                        claims.Remove(descriptor.Name); // missing/changed: clear claim and preserve config
                        ledgerChanged = true;
                        preserved |= servers.ContainsKey(descriptor.Name);
                        continue;
                    }
                    removable.Add(descriptor.Name);
                    claims.Remove(descriptor.Name);
                    ledgerChanged = true;
                }

                // Clear claims before touching config. A crash leaves safe, unowned entries.
                if (ledgerChanged) WriteJsonAtomic(ledgerPath, ledger);
                foreach (var name in removable) {
                    servers.Remove(name);
                    tomlChanged = true;
                }
                if (servers.Count == 0) root.Remove("mcp_servers");
                if (tomlChanged) WriteTomlAtomic(configPath, root);
                if (tomlChanged || ledgerChanged)
                    return preserved ? Change.UpdatedWithPreservedEntries : Change.Updated;
                return preserved ? Change.PreservedUnownedEntries : Change.Unchanged;
            } catch {
                return Change.Failed;
            }
        }
    }

    static TomlTable BuildMcpTable(KcapMcpServer descriptor) {
        var table = new TomlTable {
            ["command"] = KcapMcpServers.Command,
            ["args"] = ToTomlArray(descriptor.Args)
        };
        if (descriptor.ReadOnly) table["default_tools_approval_mode"] = "approve";
        return table;
    }

    static JsonObject NewOwnershipLedger() => new() {
        ["version"] = 1,
        ["entries"] = new JsonObject()
    };

    static JsonObject? ReadOwnershipLedger(string path) {
        if (!File.Exists(path)) return null;
        try {
            var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            return root?["version"]?.GetValue<int>() == 1 && root["entries"] is JsonObject ? root : null;
        } catch { return null; }
    }

    static JsonObject BuildClaim(TomlTable table) => new() {
        ["fingerprint"] = Fingerprint(table),
        ["normalized_table"] = NormalizeToml(table)
    };

    static string Fingerprint(TomlTable table) {
        var canonical = NormalizeToml(table).ToJsonString();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    static JsonNode NormalizeToml(object? value) => value switch {
        TomlTable table => new JsonObject(table.OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => KeyValuePair.Create<string, JsonNode?>(x.Key, NormalizeToml(x.Value)))),
        TomlTableArray tableArray => new JsonArray(tableArray.Select(NormalizeToml).ToArray()),
        TomlArray array => new JsonArray(array.Select(NormalizeToml).ToArray()),
        string s => new JsonObject { ["type"] = "string", ["value"] = s },
        bool b => new JsonObject { ["type"] = "bool", ["value"] = b },
        null => new JsonObject { ["type"] = "null" },
        IDictionary dictionary => new JsonObject(dictionary.Keys.Cast<object>()
            .Select(key => KeyValuePair.Create<string, JsonNode?>(
                Convert.ToString(key, CultureInfo.InvariantCulture) ?? "", NormalizeToml(dictionary[key])))
            .OrderBy(x => x.Key, StringComparer.Ordinal)),
        IEnumerable sequence => new JsonArray(sequence.Cast<object?>().Select(NormalizeToml).ToArray()),
        _ => new JsonObject {
            ["type"] = value.GetType().FullName,
            ["value"] = Convert.ToString(value, CultureInfo.InvariantCulture)
        }
    };

    static string? StringField(JsonObject value, string name) =>
        value[name] is JsonValue field && field.TryGetValue<string>(out var text) ? text : null;

    static void EnsureParentDirectory(string path) {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
    }

    static void WriteJsonAtomic(string path, JsonObject root) {
        EnsureParentDirectory(path);
        var tmp = path + ".tmp-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N");
        try {
            WriteOwnerOnlyTemp(tmp, root.ToJsonString(new() { WriteIndented = true }));
            File.Move(tmp, path, overwrite: true);
        } catch { try { File.Delete(tmp); } catch { } throw; }
    }

    /// <summary>
    /// Reads the top-level <c>model = "…"</c> key from <c>~/.codex/config.toml</c>
    /// (or <paramref name="configPath"/>), honouring <c>CODEX_HOME</c> via
    /// <see cref="CodexPaths.Home"/>. Returns null when the file is missing, unreadable,
    /// has no top-level <c>model</c> key, or the value isn't a string — so callers can
    /// fall back to the dispatched model. Read-only; never throws.
    /// </summary>
    public static string? ReadTopLevelModel(string? configPath = null) {
        var path = configPath ?? DefaultConfigPath;

        if (!File.Exists(path)) return null;

        try {
            var root = TomlSerializer.Deserialize(File.ReadAllText(path), _tomlTypeInfo.TableInfo);

            return root is not null && root.TryGetValue("model", out var v) && v is string s && !string.IsNullOrWhiteSpace(s)
                ? s
                : null;
        } catch {
            return null;
        }
    }

    /// <summary>
    /// Reads the top-level <c>[mcp_servers]</c> table keys from <c>~/.codex/config.toml</c>
    /// (or <paramref name="configPath"/>), honouring <c>CODEX_HOME</c> via
    /// <see cref="CodexPaths.Home"/>. Returns an empty, ordinal-sorted list when the file is
    /// missing, unreadable, or has no <c>[mcp_servers]</c> table. Read-only; never throws.
    ///
    /// NOTE: this reports ONLY config.toml-declared servers. It is NOT the authoritative
    /// enumeration of what a spawned Codex session inherits — Codex 0.144.3 also composes MCP
    /// servers from active native plugins, which this misses. The review-flow reviewer isolation
    /// therefore enumerates via <c>codex mcp list --json</c>
    /// (<c>Capacitor.Cli.Daemon.Services.CodexMcpInventory</c>), which reports the fully-composed
    /// effective list (config + plugins).
    /// </summary>
    public static IReadOnlyList<string> ReadMcpServerNames(string? configPath = null) {
        var path = configPath ?? DefaultConfigPath;

        if (!File.Exists(path)) return [];

        try {
            var root = TomlSerializer.Deserialize(File.ReadAllText(path), _tomlTypeInfo.TableInfo);

            if (root is null || !root.TryGetValue("mcp_servers", out var v) || v is not TomlTable servers)
                return [];

            var names = servers.Keys.ToList();
            names.Sort(StringComparer.Ordinal);

            return names;
        } catch {
            return [];
        }
    }

    /// <summary>
    /// Builds the Codex proxy allowlist from a set of Capacitor server URLs (the
    /// active one plus every configured profile). Any host under <c>kcap.ai</c>
    /// collapses to a single <c>**.kcap.ai</c> wildcard — it matches the apex and
    /// every subdomain, so all SaaS tenants (current and future) and the auth proxy
    /// (<c>auth.kcap.ai</c>) are covered without per-tenant maintenance. Self-hosted
    /// hosts are added as exact entries. Output is deterministic (wildcard first,
    /// then hosts sorted) so repeated writes are idempotent.
    /// </summary>
    public static IReadOnlyList<string> BuildAllowDomains(IEnumerable<string?> serverUrls) {
        var hosts              = new List<string>();
        var seen               = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var includeKcapWildcard = false;

        foreach (var url in serverUrls) {
            var host = TryGetHost(url);
            if (host is null) continue;

            if (host.Equals("kcap.ai", StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith(".kcap.ai", StringComparison.OrdinalIgnoreCase)) {
                includeKcapWildcard = true;
            } else if (seen.Add(host)) {
                hosts.Add(host);
            }
        }

        hosts.Sort(StringComparer.Ordinal);

        if (includeKcapWildcard) hosts.Insert(0, "**.kcap.ai");

        return hosts;
    }

    static string? TryGetHost(string? url) {
        if (string.IsNullOrWhiteSpace(url)) return null;

        if (Uri.TryCreate(url, UriKind.Absolute, out var abs) && !string.IsNullOrEmpty(abs.Host))
            return abs.Host;

        // Bare host[:port] without a scheme (self-hosted shorthand).
        return Uri.TryCreate("https://" + url, UriKind.Absolute, out var withScheme) && !string.IsNullOrEmpty(withScheme.Host)
            ? withScheme.Host
            : null;
    }

    /// <summary>
    /// Load → mutate → atomic-write. <paramref name="mutate"/> returns true when it
    /// changed <paramref name="root"/>. Returns <see cref="Change.Failed"/> on a
    /// parse error (we never clobber a config we can't read) or a write error,
    /// <see cref="Change.Unchanged"/> when nothing changed, otherwise
    /// <see cref="Change.Updated"/>. <paramref name="error"/> carries the captured
    /// exception on <see cref="Change.Failed"/> (null otherwise) so callers can log it.
    /// </summary>
    static Change Update(string configPath, Func<TomlTable, bool> mutate, out Exception? error) {
        error = null;

        lock (_writeLock) {
            IDisposable crossProcess;
            try {
                configPath = CanonicalConfigPath(configPath);
                crossProcess = AcquireConfigLock(configPath);
            } catch (Exception ex) {
                error = ex;
                return Change.Failed;
            }
            using (crossProcess) {
            TomlTable root;

            if (File.Exists(configPath)) {
                try {
                    root = TomlSerializer.Deserialize(File.ReadAllText(configPath), _tomlTypeInfo.TableInfo) ?? new TomlTable();
                } catch (Exception ex) {
                    error = ex;

                    return Change.Failed;
                }
            } else {
                root = new TomlTable();
            }

            bool changed;

            try {
                changed = mutate(root);
            } catch (Exception ex) {
                error = ex;

                return Change.Failed;
            }

            if (!changed) return Change.Unchanged;

            try {
                // First-time users have no ~/.codex; create it before the atomic rename.
                // GetDirectoryName is null/empty for a directory-less path — skip the
                // create in that case (the file lands in the current directory).
                var dir = Path.GetDirectoryName(configPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                WriteTomlAtomic(configPath, root);

                return Change.Updated;
            } catch (Exception ex) {
                error = ex;

                return Change.Failed;
            }
            }
        }
    }

    static bool MutateTrust(TomlTable root, string worktreePath) {
        var projects = GetOrAddTable(root, "projects");
        var entry    = GetOrAddTable(projects, worktreePath);

        if (entry.TryGetValue("trust_level", out var existing) &&
            existing is string s && string.Equals(s, "trusted", StringComparison.Ordinal))
            return false;

        entry["trust_level"] = "trusted";

        return true;
    }

    static TomlArray ToTomlArray(string[] values) {
        var arr = new TomlArray();
        foreach (var v in values) arr.Add(v);

        return arr;
    }

    static bool MutateNetworkAccess(TomlTable root, IReadOnlyCollection<string> allowDomains) {
        // Inspect current state WITHOUT mutating, so the "fully open" branch can
        // bail out without having created empty tables.
        var sandbox   = root.TryGetValue("sandbox_workspace_write", out var sObj) ? sObj as TomlTable : null;
        var networkOn = sandbox is not null && sandbox.TryGetValue("network_access", out var na) && na is true;

        var features     = root.TryGetValue("features", out var fObj) ? fObj as TomlTable : null;
        var proxy        = features is not null && features.TryGetValue("network_proxy", out var pObj) ? pObj as TomlTable : null;
        var proxyEnabled = proxy is not null && proxy.TryGetValue("enabled", out var pe) && pe is true;

        if (proxyEnabled) {
            // The user runs an allowlist policy — extend it with our hosts and make
            // sure network access is granted, preserving their existing entries.
            var changed = MergeAllowDomains(GetOrAddTable(proxy!, "domains"), allowDomains);

            if (!networkOn) {
                GetOrAddTable(root, "sandbox_workspace_write")["network_access"] = true;
                changed = true;
            }

            return changed;
        }

        // Fully open (network on, no active proxy): kcap already works; don't narrow it.
        if (networkOn) return false;

        // Default (network off, no active proxy): enable + tight kcap-only allowlist.
        GetOrAddTable(root, "sandbox_workspace_write")["network_access"] = true;

        var networkProxy = GetOrAddTable(GetOrAddTable(root, "features"), "network_proxy");
        networkProxy["enabled"] = true;
        MergeAllowDomains(GetOrAddTable(networkProxy, "domains"), allowDomains);

        return true;
    }

    static bool MergeAllowDomains(TomlTable domains, IReadOnlyCollection<string> allowDomains) {
        var changed = false;

        foreach (var domain in allowDomains) {
            if (domains.TryGetValue(domain, out var v) && v is string s && string.Equals(s, "allow", StringComparison.Ordinal))
                continue;

            domains[domain] = "allow";
            changed         = true;
        }

        return changed;
    }

    static TomlTable GetOrAddTable(TomlTable parent, string key) {
        if (parent.TryGetValue(key, out var existing) && existing is TomlTable table) return table;

        var created = new TomlTable();
        parent[key] = created;

        return created;
    }

    static void WriteTomlAtomic(string path, TomlTable root) {
        var tmp = path + ".tmp-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N");
        try {
            WriteOwnerOnlyTemp(tmp, TomlSerializer.Serialize(root, _tomlTypeInfo.TableInfo));
            File.Move(tmp, path, overwrite: true);
        } catch {
            try { File.Delete(tmp); } catch {
                /* best-effort */
            }

            throw;
        }
    }

    static string CanonicalConfigPath(string path) {
        var full = Path.GetFullPath(path);
        RejectSymlinkComponents(full);
        var ledger = Path.Combine(Path.GetDirectoryName(full) ?? ".", "mcp-ownership-v1.json");
        RejectSymlinkComponents(ledger);
        return full;
    }

    static void RejectSymlinkComponents(string path) {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? throw new IOException($"Path has no root: {path}");
        var current = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries)) {
            current = Path.Combine(current, part);
            RejectSymlink(current);
        }
    }

    static void RejectSymlink(string path) {
        if (!File.Exists(path) && !Directory.Exists(path)) return;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Refusing to update symlinked Codex configuration target: {path}");
    }

    static IDisposable AcquireConfigLock(string canonicalConfigPath) {
        EnsureParentDirectory(canonicalConfigPath);
        RejectSymlinkComponents(canonicalConfigPath);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalConfigPath))).ToLowerInvariant();
        var mutex = new Mutex(false, "kcap-codex-config-" + hash);
        try {
            try {
                if (!mutex.WaitOne(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("Timed out waiting for another kcap Codex configuration update.");
            } catch (AbandonedMutexException) {
                // The prior writer died while holding the lock; ownership transfers to us.
            }
            return new MutexLease(mutex);
        } catch {
            mutex.Dispose();
            throw;
        }
    }

    static void SetOwnerOnly(string path) {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    static void WriteOwnerOnlyTemp(string path, string content) {
        var options = new FileStreamOptions {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(path, options))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            writer.Write(content);
        // Defense in depth for platforms/filesystems that ignore the create mode.
        SetOwnerOnly(path);
    }

    sealed class MutexLease(Mutex mutex) : IDisposable {
        public void Dispose() {
            try { mutex.ReleaseMutex(); } finally { mutex.Dispose(); }
        }
    }

    /// <summary>
    /// AOT-safe accessor for <see cref="TomlTypeInfo{T}"/> of <see cref="TomlTable"/>.
    /// <see cref="TomlSerializerContext.GetBuiltInTypeInfo{T}"/> is protected, so a thin
    /// subclass surfaces the built-in untyped-model metadata without reflection.
    /// </summary>
    sealed class TomlTableTypeInfo : TomlSerializerContext {
        static readonly TomlSerializerOptions DefaultOptions = new();

        public readonly TomlTypeInfo<TomlTable> TableInfo = GetBuiltInTypeInfo<TomlTable>(DefaultOptions)!;

        public override TomlTypeInfo? GetTypeInfo(Type type, TomlSerializerOptions options) =>
            type == typeof(TomlTable) ? TableInfo : null;
    }
}
