using System.Text.Json;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Mcp;

/// <summary>
/// Read-modify-write engine for a harness's JSON MCP config file. Mirrors
/// <c>CodexConfigToml</c>: fail-closed on a malformed/wrong-type file (never clobber),
/// non-destructive (preserves user servers + surrounding config), idempotent, atomic.
/// Uses the JsonNode DOM — reflection-free and AOT-safe.
/// </summary>
public static class JsonMcpConfigWriter {
    static readonly Lock _writeLock = new();
    static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };

    public enum Change { Unchanged, Updated, Failed }

    public static Change Register(string configPath, IReadOnlyList<KcapMcpServer> servers,
                                  McpConfigShape shape, string? cwd, IMcpMarker marker,
                                  Func<string?>? resolveBinaryPath = null) {
        var command = KcapBinaryCommand.Resolve(resolveBinaryPath);
        var written = new List<KeyValuePair<string, JsonNode?>>();
        var adopted = false;

        var change = Update(configPath, root => {
            var block   = GetOrAddObject(root, shape.BlockKey); // throws on wrong-type → Failed
            var changed = false;
            written.Clear(); // Update may retry the mutate in principle — never double-record
            adopted = false;

            foreach (var s in servers) {
                var rendered = RenderEntry(s, shape, cwd, command);
                if (block[s.Name] is JsonNode existing) {
                    if (marker.Owns(configPath, s.Name, existing)) {
                        written.Add(new(s.Name, rendered));                     // kcap-owned → keep recorded
                        if (JsonNode.DeepEquals(existing, rendered)) continue;  // identical → idempotent no-op
                        block[s.Name] = rendered;                               // stale/old shape → heal to canonical
                        changed = true;
                        continue;
                    }
                    // Unowned but EXACTLY the entry this register would write: adopt it. This
                    // is the recovery lane for config-committed-but-marker-failed (a crash or
                    // marker-write failure after the config landed): re-claiming a shape
                    // indistinguishable from our own write strands nothing user-authored —
                    // uninstall would remove only what registration itself would have written.
                    if (JsonNode.DeepEquals(existing, rendered)) {
                        written.Add(new(s.Name, rendered));
                        adopted = true;
                    }
                    continue;                                                   // divergent user look-alike — never clobber
                }
                block[s.Name] = rendered;                                       // missing → add
                written.Add(new(s.Name, rendered));
                changed = true;
            }

            return changed;
        });

        // Record ownership only AFTER the config write commits (config first, claim second —
        // the CodexConfigToml crash ordering): a crash between the two leaks an unowned entry,
        // which registration and uninstall deliberately preserve, whereas the reverse order
        // could claim (fingerprint) a shape that never reached disk and strand healing.
        // Adoption records even on an Unchanged config (that IS the marker-only repair).
        if ((change == Change.Updated || (change == Change.Unchanged && adopted)) && written.Count > 0) {
            // A marker failure must never fail the caller: the config — the user-visible
            // artifact — already committed, and setup/plugin install must not report a
            // registration that IS in place as a hard failure (never-fails-install contract).
            // Degraded ownership self-heals: the adoption lane above re-records it on the
            // next register/refresh pass, because the committed entries still match the
            // canonical shape it renders.
            try { marker.Record(configPath, written); }
            catch { /* degraded: ownership heals via adoption on the next pass */ }
        }
        return change;
    }

    public static Change Unregister(string configPath, McpConfigShape shape, IMcpMarker marker) {
        var change = Update(configPath, root => {
            if (root[shape.BlockKey] is not JsonObject block) return false;
            var changed = false;

            foreach (var name in marker.Owned(configPath).ToArray())
                if (block[name] is JsonNode entry && marker.Owns(configPath, name, entry) && block.Remove(name))
                    changed = true;

            if (block.Count == 0 && root.Remove(shape.BlockKey)) changed = true;
            return changed;
        });

        // Always clear kcap's ownership marker on unregister — even when there were no JSON entries
        // to remove (e.g. the user hand-deleted them) — so no orphaned marker is left. Skip only on a
        // hard failure (couldn't read/parse the config) so state stays recoverable.
        if (change != Change.Failed) marker.Clear(configPath);
        return change;
    }

    static JsonObject RenderEntry(KcapMcpServer s, McpConfigShape shape, string? cwd, string command) {
        var o = new JsonObject();
        if (shape.TypeValue is not null) o["type"] = shape.TypeValue;

        if (shape.CommandAsArgvArray) {
            // Use the implicit string -> JsonValue conversion (cast to JsonNode?) rather
            // than JsonValue.Create / collection expressions, which lower to generic
            // Add<T> and trip NativeAOT (IL3050). Matches ReviewLaunchBuilder's pattern.
            var argv = new JsonArray();
            argv.Add((JsonNode?)command);
            foreach (var a in s.Args) argv.Add((JsonNode?)a);
            o["command"] = argv;
        } else {
            o["command"] = command;
            var args = new JsonArray();
            foreach (var a in s.Args) args.Add((JsonNode?)a);
            o["args"] = args;
        }

        if (cwd is not null && s.NeedsProjectCwd) o["cwd"] = cwd;
        if (shape.Enable == EnableStyle.EnabledTrue) o["enabled"] = true;

        // Auto-approve only read-only servers, and only where the harness has a per-server trust knob.
        // Write-capable / work-launching servers (kcap-memory, kcap-flows) keep prompting.
        if (s.ReadOnly && shape.Trust == TrustStyle.TrustBool) o["trust"] = true;   // Gemini

        return o;
    }

    static Change Update(string configPath, Func<JsonObject, bool> mutate) {
        lock (_writeLock) {
            JsonObject root;

            if (File.Exists(configPath)) {
                try {
                    var text = File.ReadAllText(configPath);
                    // An empty or whitespace-only file has nothing to preserve, so treat it as an
                    // empty config rather than fail-closed malformed JSON — some harnesses (e.g.
                    // Antigravity) ship a 0-byte mcp_config.json on first run.
                    if (string.IsNullOrWhiteSpace(text)) {
                        root = new JsonObject();
                    } else {
                        var parsed = JsonNode.Parse(text);
                        if (parsed is not JsonObject obj) return Change.Failed; // wrong top-level type
                        root = obj;
                    }
                } catch {
                    return Change.Failed; // malformed — never clobber
                }
            } else {
                root = new JsonObject();
            }

            bool changed;
            try { changed = mutate(root); }
            catch { return Change.Failed; } // e.g. wrong-type block

            if (!changed) return Change.Unchanged;

            try {
                var dir = Path.GetDirectoryName(configPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                WriteAtomic(configPath, root);
                return Change.Updated;
            } catch { return Change.Failed; }
        }
    }

    static JsonObject GetOrAddObject(JsonObject parent, string key) {
        if (parent.TryGetPropertyValue(key, out var v)) {
            if (v is JsonObject obj) return obj;
            throw new InvalidOperationException($"`{key}` is present but not an object; refusing to overwrite.");
        }
        var created = new JsonObject();
        parent[key] = created;
        return created;
    }

    static void WriteAtomic(string path, JsonNode root) {
        var tmp = path + ".tmp-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(tmp, root.ToJsonString(WriteOpts));
        try { File.Move(tmp, path, overwrite: true); }
        catch { try { File.Delete(tmp); } catch { /* best-effort */ } throw; }
    }
}
