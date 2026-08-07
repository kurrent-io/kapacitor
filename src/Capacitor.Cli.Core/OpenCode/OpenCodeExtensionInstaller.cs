namespace Capacitor.Cli.Core.OpenCode;

/// <summary>
/// Installs / removes kcap's live-ingest plugin for SST OpenCode.
/// OpenCode has no shell hooks, so — like Pi — kcap ships a TypeScript plugin
/// file (<c>~/.config/opencode/plugins/kcap.ts</c>) that OpenCode auto-discovers
/// and loads in-process. The plugin bridges OpenCode's event bus to the kcap CLI:
/// on <c>session.created</c> it shells <c>kcap hook --opencode --event session-start</c>
/// (which POSTs lifecycle + spawns the transcript watcher), and on
/// <c>session.idle</c> it fetches the session's full messages via the injected SDK
/// <c>client</c> and appends them as native <c>{info,parts}</c> JSONL lines to the
/// file the watcher tails.
///
/// <para><see cref="ExtensionContent"/> is the single source of truth for the
/// installed file (embedding it as a const keeps NativeAOT happy — no manifest
/// resource reflection). A version marker beside it gates the upgrade-time
/// refresh, mirroring <see cref="Pi.PiExtensionInstaller"/>.</para>
/// </summary>
public static class OpenCodeExtensionInstaller {
    public const string MarkerFileName = ".kcap-extension-version";

    /// <summary>
    /// The kcap OpenCode plugin. Dependency-free (only <c>node:</c> builtins,
    /// available in Bun) so it works in airgapped installs with no npm registry,
    /// and untyped (<c>any</c>) so it carries no runtime dependency on
    /// <c>@opencode-ai/sdk</c> / <c>@opencode-ai/plugin</c> types. Fail-safe — every
    /// handler swallows errors so a kcap/server hiccup never disrupts the session.
    /// </summary>
    public const string ExtensionContent =
        """
        // kcap.ts — Kurrent Capacitor live-ingest plugin for SST OpenCode.
        //
        // Installed by `kcap setup` / `kcap plugin install --opencode` into
        // ~/.config/opencode/plugins/kcap.ts, which OpenCode auto-loads in-process.
        // OpenCode has no shell hooks, so this plugin bridges OpenCode's event bus to
        // the kcap CLI:
        //   - session.created → `kcap hook --opencode --event session-start ...`, which
        //     POSTs /hooks/session-start/opencode and spawns the transcript watcher
        //     (vendor=opencode) tailing the JSONL file this plugin writes. The watcher
        //     synthesizes session-end when the opencode process exits (Kiro pattern).
        //   - session.idle → fetch the session's full messages via the injected SDK
        //     client and append any new {info,parts} as JSONL lines to that file; the
        //     watcher streams them. The server's OpenCodeTranscriptNormalizer dedupes by
        //     deterministic part id, so re-appending an updated message is safe.
        //
        // Dependency-free (only node: builtins) and fail-safe by default.

        import { appendFileSync, mkdirSync } from "node:fs"
        import { homedir } from "node:os"
        import { join, dirname } from "node:path"

        export const KcapPlugin = async ({ client, $, directory }: any) => {
          const dir = join(homedir(), ".cache", "kcap", "opencode")
          const file = (sid: string) => join(dir, sid + ".jsonl")
          // Subagents run as child sessions; their {info,parts} go in a nested dir beside
          // the parent file (<dir>/<parent>/<child>.jsonl) that the kcap watcher scans and
          // streams with the child's agent_id → AgentSubsession-*.
          const childFile = (parent: string, child: string) => join(dir, parent, child + ".jsonl")
          const started = new Set<string>()
          const children = new Set<string>()           // known subagent (child) session ids — skip top-level
          const written = new Map<string, Set<string>>()
          const flushedChildren = new Set<string>()     // child ids whose COMPLETE transcript has been written
          // child id → { content key, ms it has been UNCHANGED }: gates the last-resort flush on a
          // STABLE transcript, so a still-streaming markerless child is never flushed mid-stream.
          const childFirstSeen = new Map<string, { key: string; since: number }>()

          // Team-memory index, keyed by TOP-LEVEL session id. The value is whatever
          // `kcap hook --opencode --event session-start` printed on stdout: the fragment, or "" when
          // there is none. kcap owns every decision behind it (auth, scope, the once-per-session
          // durable lease, byte budget) — this plugin never talks to the server and never renders
          // anything.
          const memory = new Map<string, string>()
          // In-flight CLASSIFY+START per session id, published synchronously by ensureStarted so a
          // chat.system.transform racing it has something to await rather than concluding there is
          // nothing in flight.
          const pendingStart = new Map<string, Promise<boolean>>()
          const MEMORY_MARKER = "<!-- kcap-memory-index:v1 -->"
          const MEMORY_MAX_SESSIONS = 64      // bounded: never an unbounded process-global map
          // Matches runKcap's own 10s ceiling. Only ever paid on a session's FIRST request, and only
          // while the start is genuinely still running — a shorter bound would silently give up on a
          // slow-but-healthy fetch and lose the one injection that session's lease paid for.
          const MEMORY_WAIT_MS = 10000
          // Cold-start attempts per session from the TRANSFORM path, and why it is bounded: a session
          // whose classification keeps failing never reaches start(), so `started` never records it and
          // the transform would re-trigger classify on EVERY model request, forever. The event path is
          // unaffected (session.idle retries there as designed) — this bounds only the request-path
          // healing, which exists for a plugin loaded mid-session and has no business retrying
          // indefinitely. Found by review tracing the `!started.has(sid)` guard.
          const MEMORY_COLD_START_ATTEMPTS = 3
          const coldStarts = new Map<string, number>()

          function rememberMemory(sid: string, fragment: string) {
            if (!fragment) return
            // VALIDATE before trusting: only stdout that actually opens with the marker is a memory
            // fragment. Without this, any future line some other code path prints on this command's
            // stdout would be appended to the model's system prompt verbatim, which is both a
            // correctness and a content problem — stdout is a data channel here, and a data channel
            // needs a shape.
            if (!fragment.startsWith(MEMORY_MARKER)) return
            // A repeated start returning nothing must not erase a fragment already cached.
            if (memory.has(sid)) return
            // Cheap bound: drop the oldest insertion (Map preserves insertion order).
            if (memory.size >= MEMORY_MAX_SESSIONS) {
              const oldest = memory.keys().next()
              if (!oldest.done) memory.delete(oldest.value)
            }
            memory.set(sid, fragment)
          }

          async function runKcap(args: string[]): Promise<string> {
            try {
              // kcap spawns a detached watcher and returns fast; bound it so a hung
              // kcap can never stall OpenCode. `.quiet()` keeps the child's output off OpenCode's
              // own stdio while still handing it back on the result — session-start uses stdout as a
              // DATA channel for the team-memory fragment.
              const res: any = await Promise.race([
                $`kcap ${args}`.quiet().nothrow(),
                new Promise((res) => setTimeout(() => res(null), 10000)),
              ])
              const out = res?.stdout
              return typeof out === "string" ? out : out ? out.toString() : ""
            } catch {
              // never disrupt the OpenCode session
              return ""
            }
          }

          // Classify a session: "child" (a subagent — never ingest as top-level), "top"
          // (no parent), or "unknown" (a transient session.get failure left it ambiguous).
          // NEVER start a top-level session on "unknown" — that is exactly the case that would
          // misfile a child as BOTH a top-level session AND a subagent. Only a SUCCESSFUL
          // session.get with no parent proves top-level; otherwise defer to the next idle
          // (session.created carries parentID only for some children, and session.idle never
          // does, so the SDK is the authority).
          async function classify(sid: string, info?: any): Promise<"child" | "top" | "unknown"> {
            if (children.has(sid)) return "child"
            if (info?.parentID) { children.add(sid); return "child" }
            try {
              const s: any = await client.session.get({ path: { id: sid } })
              const parentId = s?.parentID ?? s?.data?.parentID
              if (parentId) { children.add(sid); return "child" }
              return "top"
            } catch {
              // session.get failed — FAIL CLOSED: stay "unknown" and DEFER. Never promote an
              // ambiguous id to top-level: a misfiled child gets double-ingested (a top-level file
              // AND a nested subagent file) and can't be cleanly un-ingested, whereas a real
              // top-level session just waits for session.get to recover (and if the SDK is
              // unreachable, fetching its transcript would fail too — nothing is lost by waiting).
              return "unknown"
            }
          }

          // Per-message dedup key. parts.length catches new parts streaming in; the
          // terminal-tool count catches a tool part transitioning to completed/error WITHOUT
          // the part count changing — the server skips non-terminal tool snapshots and keeps
          // the FIRST append per prt_ id, so the completed snapshot MUST be re-appended under a
          // new key to be ingested. (Text/reasoning are final by session.idle = turn end.)
          function dedupeKey(m: any) {
            const parts: any[] = m?.parts ?? []
            let terminalTools = 0
            for (const p of parts) {
              const st = p?.state?.status
              if (p?.type === "tool" && (st === "completed" || st === "error")) terminalTools++
            }
            return (m?.info?.id ?? "") + ":" + parts.length + ":" + terminalTools
          }

          async function start(sid: string, info?: any) {
            if (started.has(sid)) return
            started.add(sid)
            try { mkdirSync(dir, { recursive: true }); appendFileSync(file(sid), "") } catch {}
            // --memory-contract declares that THIS plugin captures and delivers the command's stdout.
            // A binary that understands it only then fetches the index and spends the session's
            // once-only injection lease; an older plugin, which discards stdout, sends no flag and the
            // binary spends nothing on output nobody will deliver.
            const args = ["hook", "--opencode", "--event", "session-start", "--session", sid,
                          "--file", file(sid), "--memory-contract", "1"]
            const cwd = info?.directory ?? directory
            if (cwd) args.push("--cwd", String(cwd))
            if (info?.version) args.push("--version", String(info.version))
            rememberMemory(sid, (await runKcap(args)).trim())
          }

          // Classification + start for one session, as ONE awaitable whose in-flight marker is
          // published SYNCHRONOUSLY — before the classify's first await — so a chat.system.transform
          // firing between session.created and the end of classification finds it. Resolves true once
          // the session is proven top-level and its start has run.
          //
          // That synchronous publication is the whole point, and its absence was a real defect: an
          // earlier revision set the marker inside start(), which only runs AFTER classify resolves.
          // OpenCode publishes session.created and then issues the first LLM request, so the transform
          // reliably landed in the window where classification was still in flight, saw no pending
          // start, and declined to inject. On a one-turn `opencode run` — the shape the live cert
          // uses — that meant the session never received its index at all, and because it is a race it
          // presented as intermittent rather than broken. Caught by the live cert, not by unit tests.
          function ensureStarted(sid: string, info?: any): Promise<boolean> {
            const existing = pendingStart.get(sid)
            if (existing) return existing

            const gate = (async () => {
              if (await classify(sid, info) !== "top") return false
              await start(sid, info)
              return true
            })()

            pendingStart.set(sid, gate)
            // Cleared on settle, which is exactly when `memory` has been populated (or proven empty),
            // so a later transform reads the cache rather than waiting on a finished promise.
            const clear = () => pendingStart.delete(sid)
            gate.then(clear, clear)
            return gate
          }

          async function fetchMessages(sid: string): Promise<any[]> {
            try {
              const res: any = await client.session.messages({ path: { id: sid } })
              return Array.isArray(res) ? res : (res?.data ?? [])
            } catch {
              return []
            }
          }

          // Append any not-yet-written {info,parts} lines to targetFile. A key is marked seen ONLY
          // after the append SUCCEEDS, so a transient disk/permission/full-volume error doesn't
          // permanently suppress those snapshots — a later flush retries them. Returns false iff the
          // append threw. Re-emits when a message's dedup key changes (new parts, or a tool turning
          // terminal); prt_ ids dedupe server-side.
          function writeMessages(targetFile: string, msgs: any[]): boolean {
            const seen = written.get(targetFile) ?? new Set<string>()
            written.set(targetFile, seen)
            const pending: { key: string; line: string }[] = []
            for (const m of msgs) {
              if (!m?.info?.id) continue
              const key = dedupeKey(m)
              if (seen.has(key)) continue
              pending.push({ key, line: JSON.stringify({ info: m.info, parts: m.parts ?? [] }) })
            }
            if (pending.length === 0) return true
            try {
              mkdirSync(dirname(targetFile), { recursive: true })
              appendFileSync(targetFile, pending.map((p) => p.line).join("\n") + "\n")
            } catch {
              return false // append failed — leave keys unseen so a later flush retries
            }
            for (const p of pending) seen.add(p.key)
            return true
          }

          // The spawned child session id carried on a `task` tool part — alias-tolerant so a small
          // upstream metadata-key rename can't silently drop the child (it would otherwise hit the
          // fallback below, never permanent loss, but prefer the durable signal).
          function taskChildId(part: any): string | undefined {
            const md = part?.state?.metadata
            return md?.sessionId ?? md?.sessionID ?? md?.session_id
          }

          // Child ids whose spawning `task` tool part on the PARENT is terminal (completed/error)
          // — a DURABLE "subagent finished" signal, not the assumed "parent only idles once the
          // task returned". Scans the parent messages ALREADY fetched this idle (no 2nd fetch).
          function completedChildIds(parentMsgs: any[]): Set<string> {
            const done = new Set<string>()
            for (const m of parentMsgs) {
              for (const p of (m?.parts ?? [])) {
                if (p?.type !== "tool" || p?.tool !== "task") continue
                const st = p?.state?.status
                const cid = taskChildId(p)
                if (cid && (st === "completed" || st === "error")) done.add(cid)
              }
            }
            return done
          }

          // "Structurally at rest" = every tool part terminal AND the latest message is an assistant
          // message (no turn in flight). A necessary precondition for completeness, and the only
          // state in which the last-resort time bound may flush (so it never writes mid-stream).
          function structurallyAtRest(msgs: any[]): boolean {
            if (msgs.length === 0) return false
            for (const m of msgs) {
              for (const p of (m?.parts ?? [])) {
                if (p?.type === "tool") {
                  const st = p?.state?.status
                  if (st !== "completed" && st !== "error") return false
                }
              }
            }
            return msgs[msgs.length - 1]?.info?.role === "assistant"
          }

          // True once a child session is COMPLETE: structurally at rest AND its final assistant turn
          // carries an end-of-turn marker — info.finish / info.time.completed, OR a structural
          // `step-finish` part (OpenCode emits one to close a completed step even when the info
          // markers are absent). A child's transcript is never written until complete, so a
          // still-streaming child's partial text/reasoning is never the first (and, by the server's
          // keep-first, permanent) append for its prt_ ids. A bare idle count is NOT a signal.
          function childComplete(msgs: any[]): boolean {
            if (!structurallyAtRest(msgs)) return false
            const last = msgs[msgs.length - 1]
            if (last?.info?.finish != null || last?.info?.time?.completed != null) return true
            return (last?.parts ?? []).some((p: any) => p?.type === "step-finish")
          }

          // A cheap fingerprint of a child's transcript that changes whenever ANY content is appended
          // or a tool turns terminal — last message id, total part count, terminal-tool count, and the
          // final message's text length. The last-resort flush requires this to stay UNCHANGED across
          // the whole window, so a child still streaming text (growing text length) keeps resetting
          // the window and is never flushed mid-stream — even with every end-of-turn marker absent.
          function stabilityKey(msgs: any[]): string {
            const last = msgs[msgs.length - 1]
            let parts = 0, termTools = 0
            for (const m of msgs) {
              const ps = m?.parts ?? []
              parts += ps.length
              for (const p of ps) {
                if (p?.type === "tool") {
                  const st = p?.state?.status
                  if (st === "completed" || st === "error") termTools++
                }
              }
            }
            let textLen = 0
            for (const p of (last?.parts ?? [])) textLen += (typeof p?.text === "string" ? p.text.length : 0)
            return (last?.info?.id ?? "") + "|" + parts + "|" + termTools + "|" + textLen
          }

          // Last-resort flush window: a child that is structurally at rest but carries NO end-of-turn
          // marker AND has no terminal parent-`task` is flushed once its transcript has stayed
          // UNCHANGED (stabilityKey) this long — so a completed child can NEVER be deferred forever,
          // while a child still streaming (a non-terminal tool, a non-assistant last message, OR
          // growing text that keeps resetting the key) is still never written. ~Impossible for real
          // OpenCode (completed turns carry step-finish); a guarantee if the markers ever change.
          const LAST_RESORT_MS = 120000

          // On the parent's idle, stream COMPLETE child sessions (subagents) into the nested dir the
          // watcher scans. A child is flushed once proven complete — by its OWN transcript
          // (childComplete) or the parent's terminal `task` part (completedChildIds) — or, as a last
          // resort, once it has been structurally at rest past LAST_RESORT_MS. Until then it's
          // deferred (recorded in `children` so its own events skip top-level; no partial write).
          // Once flushed it isn't re-fetched, so the per-idle re-fetch of a pending child is bounded.
          async function flushSubagents(parent: string, parentMsgs: any[]) {
            try {
              const done = completedChildIds(parentMsgs)
              const res: any = await client.session.children({ path: { id: parent } })
              const kids: any[] = Array.isArray(res) ? res : (res?.data ?? [])

              // Bound childFirstSeen: drop last-resort timers for children no longer returned by the
              // scan. A transient drop only RESETS a timer on reappearance (longer defer) — never an
              // early flush — so this is safe and keeps the map from growing across a long session.
              const currentIds = new Set<string>(kids.map((k: any) => k?.id).filter(Boolean))
              for (const id of childFirstSeen.keys()) if (!currentIds.has(id)) childFirstSeen.delete(id)

              for (const k of kids) {
                const cid = k?.id
                if (!cid) continue
                children.add(cid)
                if (flushedChildren.has(cid)) continue // already written — no re-fetch

                const cmsgs = await fetchMessages(cid)
                if (cmsgs.length === 0) continue // fetch failed/empty — retry next idle (no state change)

                let ready = done.has(cid) || childComplete(cmsgs)
                if (!ready && structurallyAtRest(cmsgs)) {
                  // Structurally done but missing every end-of-turn marker. Flush only once the
                  // transcript has been UNCHANGED for the whole window: a changed key (new part, a
                  // tool turning terminal, or growing text) resets the timer, so a child still
                  // streaming markerless text is never flushed mid-stream.
                  const k = stabilityKey(cmsgs)
                  const prev = childFirstSeen.get(cid)
                  if (!prev || prev.key !== k) childFirstSeen.set(cid, { key: k, since: Date.now() })
                  else ready = (Date.now() - prev.since) >= LAST_RESORT_MS
                }
                if (!ready) continue // not complete yet — defer (no partial write)

                if (writeMessages(childFile(parent, cid), cmsgs)) {
                  flushedChildren.add(cid) // mark flushed only on a successful write
                  childFirstSeen.delete(cid)
                }
              }
            } catch {
              // never disrupt the OpenCode session
            }
          }

          return {
            // Team-memory injection. OpenCode calls this once per LLM REQUEST with a mutable
            // { system: string[] } that it rebuilds from scratch each time, so appending on every
            // request is what keeps the index in the model's context for the whole session — and each
            // array still ends up with exactly one copy.
            //
            // Four things this must not do, each measured against opencode 1.18.9:
            //  1. Never inject without a sessionID. The same hook name is also triggered by OpenCode's
            //     agent-config GENERATOR, which passes no sessionID — injecting there would pollute
            //     generated agent definitions.
            //  2. Never inject into a subagent's session. `children` is the classifier's own record;
            //     an id it has not proven top-level simply has no cached fragment.
            //  3. Never REPLACE or reorder existing entries. system[0] is OpenCode's whole assembled
            //     prompt; it also collapses entries 1..n itself, but only while system[0] is
            //     unchanged, so mutating it would defeat OpenCode's own normalisation.
            //  4. Never throw. OpenCode awaits this hook without a try/catch, so an exception here
            //     would surface as a failed model request.
            "experimental.chat.system.transform": async (input: any, output: any) => {
              try {
                const sid = input?.sessionID
                if (!sid) return                       // (1) the agent-config generator path
                if (children.has(sid)) return          // (2)
                const system: any = output?.system
                if (!Array.isArray(system)) return

                let fragment = memory.get(sid)

                if (fragment === undefined) {
                  // The classify+start for a brand-new session runs CONCURRENTLY with its first
                  // request, so without this wait the very first turn — often the only turn — would
                  // miss the fragment its once-per-session lease has already been spent on.
                  const pending = pendingStart.get(sid)
                  if (pending) {
                    await Promise.race([
                      pending,
                      new Promise((res) => setTimeout(res, MEMORY_WAIT_MS)),
                    ])
                    fragment = memory.get(sid)
                  } else if (!started.has(sid)) {
                    // Nothing cached and nothing in flight: the plugin never saw this session start
                    // (loaded mid-session, or a resumed one). Heal in the BACKGROUND — a kcap spawn
                    // belongs nowhere near the critical path of a model request — and inject from the
                    // next request on. ensureStarted's classify fails closed, so ambiguous parentage
                    // defers rather than injecting into a possible child.
                    //
                    // Bounded, because a session that never classifies would otherwise re-probe on
                    // every single request for the life of the process.
                    const attempts = coldStarts.get(sid) ?? 0
                    if (attempts < MEMORY_COLD_START_ATTEMPTS) {
                      coldStarts.set(sid, attempts + 1)
                      void ensureStarted(sid)
                    }
                  }
                }

                if (!fragment) return
                // (3) Append only, and only once per array — the marker check is what keeps this
                // correct if a future OpenCode retains transformed entries instead of rebuilding.
                if (system.some((s: any) => typeof s === "string" && s.includes(MEMORY_MARKER))) return
                system.push(fragment)
              } catch {
                // (4) fail open: leave the system prompt exactly as it was
              }
            },
            event: async ({ event }: any) => {
              try {
                const type = event?.type
                const sid = event?.properties?.sessionID
                if (!sid) return
                if (children.has(sid)) return  // known subagent — its parent streams it
                if (type === "session.deleted") {
                  // Drop this session's cached fragment promptly rather than waiting for the bounded
                  // map to evict it. The bound is the guarantee; this is the tidy path.
                  memory.delete(sid)
                  coldStarts.delete(sid)
                  return
                }
                if (type === "session.created") {
                  // START a top-level session only on a CONFIRMED classification — never on
                  // "unknown" (a session.get hiccup), which would misfile a child as both a
                  // top-level session and a subagent. "unknown" defers to the next idle.
                  // Via ensureStarted so the in-flight marker exists from this call's first
                  // synchronous instant — the first LLM request races this.
                  await ensureStarted(sid, event.properties?.info)
                } else if (type === "session.idle") {
                  if (!started.has(sid)) {
                    // child → skip; unknown → retry next idle
                    if (!(await ensureStarted(sid))) return
                  }
                  // Fetch the parent transcript ONCE and reuse it for both the write and the
                  // subagent completion-scan (avoids two full fetches per idle / snapshot skew).
                  const msgs = await fetchMessages(sid)
                  writeMessages(file(sid), msgs)
                  await flushSubagents(sid, msgs)
                }
              } catch {
                // never disrupt the OpenCode session
              }
            },
          }
        }
        """;

    /// <summary>
    /// True when kcap.ts (or its marker) is present. Marker covers the case where
    /// a user deleted kcap.ts but kept the dir.
    /// </summary>
    public static bool IsInstalled(string pluginPath) {
        if (File.Exists(pluginPath)) return true;
        var dir = Path.GetDirectoryName(pluginPath);
        return dir is not null && File.Exists(Path.Combine(dir, MarkerFileName));
    }

    public static string? ReadMarker(string pluginPath) {
        var dir = Path.GetDirectoryName(pluginPath);
        if (string.IsNullOrEmpty(dir)) return null;
        var marker = Path.Combine(dir, MarkerFileName);
        try { return File.Exists(marker) ? File.ReadAllText(marker).Trim() : null; }
        catch { return null; }
    }

    public static void WriteMarker(string pluginPath) {
        var dir = Path.GetDirectoryName(pluginPath);
        if (string.IsNullOrEmpty(dir)) return;
        try {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, MarkerFileName), CapacitorVersion.Current());
        } catch { /* best effort */ }
    }

    public static void DeleteMarker(string pluginPath) {
        var dir = Path.GetDirectoryName(pluginPath);
        if (string.IsNullOrEmpty(dir)) return;
        var marker = Path.Combine(dir, MarkerFileName);
        try { if (File.Exists(marker)) File.Delete(marker); } catch { }
    }

    public static bool Install(string pluginPath) {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(pluginPath)!);
            File.WriteAllText(pluginPath, ExtensionContent);
            WriteMarker(pluginPath);
            return true;
        } catch {
            return false;
        }
    }

    /// <summary>Removes kcap.ts + marker. Returns true if kcap.ts existed.</summary>
    public static bool Remove(string pluginPath) {
        var existed = File.Exists(pluginPath);
        try {
            if (existed) File.Delete(pluginPath);
            DeleteMarker(pluginPath);
        } catch {
            return false;
        }
        return existed;
    }
}
