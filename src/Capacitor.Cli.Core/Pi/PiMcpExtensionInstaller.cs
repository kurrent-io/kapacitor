namespace Capacitor.Cli.Core.Pi;

/// <summary>
/// Installs / removes kcap's MCP-bridge extension for Pi. Pi has no built-in MCP, so
/// instead of a JSON <c>mcpServers</c> config kcap ships a TypeScript extension
/// (<c>~/.pi/agent/extensions/kcap-mcp.ts</c>) that spawns the <c>kcap mcp &lt;name&gt;</c>
/// servers and registers their tools as native Pi tools. Sibling of
/// <see cref="PiExtensionInstaller"/> with a distinct file + marker; <see cref="ExtensionContent"/>
/// is the embedded source of truth (const, not a resource — NativeAOT-safe).
/// </summary>
public static class PiMcpExtensionInstaller {
    public const string MarkerFileName = ".kcap-mcp-extension-version";

    /// <summary>
    /// The kcap Pi MCP-bridge extension. Untyped (<c>pi: any</c>) so it carries no
    /// runtime dependency on the <c>@earendil-works/pi-coding-agent</c> types, and
    /// fail-safe so a kcap/server hiccup never disrupts the pi session: each server
    /// is spawned/handshaken independently and failures are logged and skipped.
    /// </summary>
    public const string ExtensionContent =
        """
        // kcap-mcp.ts — Kurrent Capacitor MCP-bridge extension for Pi.
        // Pi has no built-in MCP, so this bridges the kcap stdio servers (`kcap mcp <name>`)
        // into Pi as native tools: spawn each, handshake, register its tools. Dependency-free
        // (node:child_process only) and fail-safe — one bad server never blocks the rest.

        import { spawn } from "node:child_process";

        const KCAP_MCP_SERVERS = ["review", "sessions", "flows", "memory", "analytics"];
        const HANDSHAKE_TIMEOUT_MS = 10000;
        // Generous — above the flows server's own round timeouts; only a backstop against a
        // stalled-but-not-exited server. A timeout surfaces as a tool failure (execute throws).
        const TOOL_CALL_TIMEOUT_MS = 1200000; // 20 min
        const KILL_GRACE_MS = 2000;

        // Process-wide child registry + a single exit hook, guarded on globalThis so repeated
        // per-session extension loads never accumulate process listeners (Node's MaxListeners).
        const KCAP_BRIDGE = ((globalThis as any).__kcapPiMcpBridge ||= { children: new Set(), exitHooked: false });

        function trackChild(child: any) {
          KCAP_BRIDGE.children.add(child);
          child.once("exit", () => KCAP_BRIDGE.children.delete(child));
        }

        function ensureExitHook() {
          if (KCAP_BRIDGE.exitHooked) return;
          KCAP_BRIDGE.exitHooked = true;
          // Synchronous-only backstop: an `exit` handler can't await the graceful ladder, so it
          // hard-SIGKILLs any child still tracked at process teardown (no orphans).
          try {
            process.on("exit", () => {
              for (const c of KCAP_BRIDGE.children) { try { c.kill("SIGKILL"); } catch { /* ignore */ } }
            });
          } catch {
            // ignore
          }
        }

        function waitExit(child: any, ms: number): Promise<boolean> {
          return new Promise((resolve) => {
            if (child.exitCode !== null || child.signalCode !== null) { resolve(true); return; }
            let settled = false;
            const onExit = () => { if (!settled) { settled = true; resolve(true); } };
            child.once("exit", onExit);
            setTimeout(() => {
              if (!settled) { settled = true; try { child.removeListener("exit", onExit); } catch { /* ignore */ } resolve(false); }
            }, ms);
          });
        }

        // EOF -> SIGTERM -> SIGKILL ladder: a child may ignore EOF and SIGTERM, so escalate.
        async function killLadder(child: any) {
          try { if (child.stdin) child.stdin.end(); } catch { /* ignore */ }
          if (await waitExit(child, KILL_GRACE_MS)) return;
          try { child.kill("SIGTERM"); } catch { /* ignore */ }
          if (await waitExit(child, KILL_GRACE_MS)) return;
          try { child.kill("SIGKILL"); } catch { /* ignore */ }
        }

        // A minimal line-delimited JSON-RPC 2.0 client over one `kcap mcp <name>` subprocess.
        class McpStdioClient {
          server: string;
          child: any = null;
          nextId = 1;
          pending = new Map<number, { resolve: (v: any) => void; reject: (e: any) => void }>();
          buffer = "";
          closed = false;
          protocolVersion: string | null = null;

          constructor(server: string) {
            this.server = server;
          }

          // spawn + MCP handshake (initialize -> validate -> notifications/initialized).
          async start(): Promise<void> {
            const child = spawn("kcap", ["mcp", this.server], { stdio: ["pipe", "pipe", "pipe"] });
            this.child = child;
            trackChild(child);
            ensureExitHook();
            child.on("exit", () => this.fail(new Error("kcap mcp " + this.server + " exited")));
            child.on("error", (e: any) => this.fail(e instanceof Error ? e : new Error(String(e))));
            child.stdout.setEncoding("utf8");
            child.stdout.on("data", (chunk: string) => this.onData(chunk));
            // Forward the server's stderr (its primary diagnostics channel) with a prefix so
            // failures are debuggable; kept off stdout so it never corrupts the JSON-RPC stream.
            if (child.stderr) {
              child.stderr.setEncoding("utf8");
              child.stderr.on("data", (chunk: string) => {
                for (const line of String(chunk).split("\n")) {
                  if (line.trim()) console.error("[kcap-mcp " + this.server + "] " + line);
                }
              });
            }
            // A just-closed/dead stdin can surface EPIPE (or another stream error) asynchronously;
            // route it through fail() — rejecting pending calls — instead of letting it crash pi.
            if (child.stdin) child.stdin.on("error", (e: any) => this.fail(e instanceof Error ? e : new Error(String(e))));

            // Full MCP initialize; validate + capture the negotiated protocol version, THEN send
            // notifications/initialized before any tools/list or tools/call (a spec-strict server
            // rejects requests that arrive before it).
            const result = await this.request("initialize", {
              protocolVersion: "2024-11-05",
              capabilities: {},
              clientInfo: { name: "kcap-pi-bridge", version: "1" },
            }, HANDSHAKE_TIMEOUT_MS);
            this.protocolVersion = (result && result.protocolVersion) || "2024-11-05";
            this.notify("notifications/initialized", {});
          }

          onData(chunk: string) {
            this.buffer += chunk;
            let nl: number;
            while ((nl = this.buffer.indexOf("\n")) >= 0) {
              const line = this.buffer.slice(0, nl).trim();
              this.buffer = this.buffer.slice(nl + 1);
              if (!line) continue;
              let msg: any;
              try {
                msg = JSON.parse(line);
              } catch {
                continue; // ignore non-JSON lines (defensive)
              }
              const id = msg && msg.id;
              if (typeof id === "number" && this.pending.has(id)) {
                const p = this.pending.get(id)!;
                this.pending.delete(id);
                if (msg.error) p.reject(new Error((msg.error && msg.error.message) || "MCP error"));
                else p.resolve(msg.result);
              }
            }
          }

          // Reject every in-flight call when the subprocess dies so execute() never hangs;
          // mark closed so later calls fail fast too.
          fail(err: Error) {
            if (this.closed) return;
            this.closed = true;
            for (const p of this.pending.values()) p.reject(err);
            this.pending.clear();
          }

          send(obj: any) {
            if (this.closed || !this.child || !this.child.stdin || !this.child.stdin.writable) {
              throw new Error("kcap mcp " + this.server + " not available");
            }
            this.child.stdin.write(JSON.stringify(obj) + "\n");
          }

          notify(method: string, params: any) {
            try {
              this.send({ jsonrpc: "2.0", method, params });
            } catch {
              // notifications are best-effort
            }
          }

          request(method: string, params: any, timeoutMs?: number): Promise<any> {
            if (this.closed) return Promise.reject(new Error("kcap mcp " + this.server + " not available"));
            const id = this.nextId++;
            return new Promise((resolve, reject) => {
              let timer: any = null;
              const clear = () => {
                if (timer) clearTimeout(timer);
              };
              this.pending.set(id, {
                resolve: (v) => { clear(); resolve(v); },
                reject: (e) => { clear(); reject(e); },
              });
              if (timeoutMs && timeoutMs > 0) {
                timer = setTimeout(() => {
                  if (this.pending.delete(id)) reject(new Error("kcap mcp " + this.server + " " + method + " timed out"));
                }, timeoutMs);
              }
              try {
                this.send({ jsonrpc: "2.0", id, method, params });
              } catch (e) {
                this.pending.delete(id);
                clear();
                reject(e);
              }
            });
          }

          async listTools(): Promise<any[]> {
            const res = await this.request("tools/list", {}, HANDSHAKE_TIMEOUT_MS);
            return res && Array.isArray(res.tools) ? res.tools : [];
          }

          callTool(name: string, args: any): Promise<any> {
            return this.request("tools/call", { name, arguments: args || {} }, TOOL_CALL_TIMEOUT_MS);
          }

          // Graceful teardown via the EOF -> SIGTERM -> SIGKILL ladder.
          async stop(): Promise<void> {
            this.fail(new Error("kcap mcp " + this.server + " stopped"));
            const child = this.child;
            this.child = null;
            if (!child) return;
            await killLadder(child);
          }
        }

        function sanitizeToolName(name: string): string {
          return String(name)
            .toLowerCase()
            .replace(/[^a-z0-9_]+/g, "_")
            .replace(/^_+|_+$/g, "");
        }

        function withTimeout<T>(p: Promise<T>, ms: number, what: string): Promise<T> {
          return new Promise((resolve, reject) => {
            const timer = setTimeout(() => reject(new Error(what + " timed out")), ms);
            p.then(
              (v) => { clearTimeout(timer); resolve(v); },
              (e) => { clearTimeout(timer); reject(e); },
            );
          });
        }

        function mcpText(result: any): string {
          if (!result || !Array.isArray(result.content)) return "";
          return result.content
            .filter((b: any) => b && b.type === "text" && typeof b.text === "string")
            .map((b: any) => b.text)
            .join("\n");
        }

        // Session-scoped bridge. Registered tools route through a per-server holder that
        // startBridge() refreshes on every session_start, so a session switch never leaves a tool
        // bound to a dead subprocess. Registration is guarded per tool name (Pi has no unregister),
        // so a reused instance never double-registers.
        export default async function (pi: any) {
          // holders + registered are INSTANCE-LOCAL by design. Pi builds a fresh ExtensionRunner
          // (fresh tool registry) per session and re-invokes this factory with a `pi` bound to it,
          // so each instance must register into its OWN runner — a process-global `registered` set
          // would starve a freshly-rebuilt runner of tools. The child-process registry + the single
          // process-exit hook are the only things that ARE process-global (see KCAP_BRIDGE above).
          const holders = new Map<string, { client: McpStdioClient | null }>();
          const registered = new Set<string>();
          let startInFlight: Promise<void> | null = null;

          function registerTool(server: string, tool: any) {
            const mcpName = tool && tool.name;
            if (!mcpName) return;
            const toolName = sanitizeToolName("kcap_" + server + "_" + mcpName);
            if (!toolName || registered.has(toolName)) return;
            const description = String((tool && tool.description) || ("kcap " + server + " " + mcpName));
            try {
              pi.registerTool({
                name: toolName,
                label: "kcap " + server + ": " + mcpName,
                description,
                // Surface it in the system prompt's Available tools section too.
                promptSnippet: "kcap " + server + " — " + description.split("\n")[0],
                // MCP inputSchema is already JSON Schema; pi forwards `parameters` to the provider as-is.
                parameters: (tool && tool.inputSchema) || { type: "object", properties: {} },
                async execute(_toolCallId: string, params: any) {
                  const holder = holders.get(server);
                  const client = holder && holder.client;
                  if (!client || client.closed) {
                    throw new Error("kcap " + server + " " + mcpName + " unavailable — no live server");
                  }
                  // callTool rejects on timeout or subprocess death — let it propagate as a failure.
                  const result = await client.callTool(mcpName, params);
                  // AgentToolResult has no isError field, so signal an MCP error by THROWING (pi-agent-core
                  // records a thrown execute as an isError tool result the model sees).
                  if (result && result.isError) {
                    throw new Error(mcpText(result) || ("kcap " + server + " " + mcpName + " returned an error"));
                  }
                  const content =
                    result && Array.isArray(result.content) && result.content.length
                      ? result.content
                      : [{ type: "text", text: typeof result === "string" ? result : JSON.stringify(result || {}) }];
                  return { content, details: { server, tool: mcpName } };
                },
              });
              // Mark registered only after a successful call, so a failure (dup name / bad schema /
              // Pi API error) can retry later and never aborts the factory or the other servers.
              registered.add(toolName);
            } catch (e: any) {
              console.error("[kcap-mcp] could not register " + toolName + ": " + ((e && e.message) || String(e)));
            }
          }

          // Start (or re-establish) every server CONCURRENTLY — one 10s handshake budget each, so
          // hung servers add ~10s wall-clock total, not 10s per server. Healthy servers register +
          // route; a bad one is logged and skipped. Idempotent: a server already live is left as-is.
          async function startBridge(): Promise<void> {
            if (startInFlight) return startInFlight;
            startInFlight = (async () => {
              await Promise.all(
                KCAP_MCP_SERVERS.map(async (server) => {
                  const existing = holders.get(server);
                  if (existing && existing.client && !existing.client.closed) return; // already live
                  const client = new McpStdioClient(server);
                  // Register the holder BEFORE the handshake (synchronously, before any await) so an
                  // overlapping stopBridge() sees and stops this in-flight subprocess — otherwise it
                  // could finish after shutdown and leave an orphaned, unmanaged child.
                  holders.set(server, { client });
                  let tools: any[];
                  try {
                    tools = await withTimeout(
                      (async () => { await client.start(); return client.listTools(); })(),
                      HANDSHAKE_TIMEOUT_MS,
                      "kcap mcp " + server + " handshake",
                    );
                  } catch (e: any) {
                    console.error("[kcap-mcp] " + server + " unavailable, skipping: " + ((e && e.message) || String(e)));
                    if (holders.get(server)?.client === client) holders.delete(server);
                    await client.stop();
                    return;
                  }
                  // An overlapping stopBridge() may have stopped this client mid-handshake — if so,
                  // don't register tools bound to a dead subprocess.
                  if (client.closed) {
                    if (holders.get(server)?.client === client) holders.delete(server);
                    return;
                  }
                  for (const tool of tools) registerTool(server, tool);
                }),
              );
            })();
            try {
              await startInFlight;
            } finally {
              startInFlight = null;
            }
          }

          async function stopBridge(): Promise<void> {
            const live = [...holders.values()].map((h) => h.client).filter(Boolean) as McpStdioClient[];
            holders.clear();
            await Promise.all(live.map((c) => c.stop()));
          }

          // Turn-1 readiness: pi awaits the async factory before session_start.
          await startBridge();

          // Respawn on a session switch/restart within the same process (idempotent).
          pi.on("session_start", async () => { await startBridge(); });
          // Primary teardown — fires on switch AND on process exit (Ctrl+C/Ctrl+D/SIGHUP/SIGTERM).
          pi.on("session_shutdown", async () => { await stopBridge(); });
        }
        """;

    /// <summary>
    /// True when kcap-mcp.ts (or its marker) is present. Marker covers the case
    /// where a user deleted kcap-mcp.ts but kept the dir.
    /// </summary>
    public static bool IsInstalled(string extensionPath) {
        if (File.Exists(extensionPath)) return true;
        var dir = Path.GetDirectoryName(extensionPath);
        return dir is not null && File.Exists(Path.Combine(dir, MarkerFileName));
    }

    public static string? ReadMarker(string extensionPath) {
        var dir = Path.GetDirectoryName(extensionPath);
        if (string.IsNullOrEmpty(dir)) return null;
        var marker = Path.Combine(dir, MarkerFileName);
        try { return File.Exists(marker) ? File.ReadAllText(marker).Trim() : null; }
        catch { return null; }
    }

    public static void WriteMarker(string extensionPath) {
        var dir = Path.GetDirectoryName(extensionPath);
        if (string.IsNullOrEmpty(dir)) return;
        try {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, MarkerFileName), CapacitorVersion.Current());
        } catch { /* best effort */ }
    }

    public static void DeleteMarker(string extensionPath) {
        var dir = Path.GetDirectoryName(extensionPath);
        if (string.IsNullOrEmpty(dir)) return;
        var marker = Path.Combine(dir, MarkerFileName);
        try { if (File.Exists(marker)) File.Delete(marker); } catch { }
    }

    public static bool Install(string extensionPath) {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(extensionPath)!);
            File.WriteAllText(extensionPath, ExtensionContent);
            WriteMarker(extensionPath);
            return true;
        } catch {
            return false;
        }
    }

    /// <summary>
    /// Removes kcap-mcp.ts + marker. Returns true if kcap-mcp.ts existed. IO/permission failures on
    /// the file delete PROPAGATE (so callers can distinguish a failure from "nothing to remove" and
    /// warn); marker deletion stays best-effort.
    /// </summary>
    public static bool Remove(string extensionPath) {
        var existed = File.Exists(extensionPath);
        if (existed) File.Delete(extensionPath);
        DeleteMarker(extensionPath);
        return existed;
    }
}
