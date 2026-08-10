using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Daemon.Services;

/// Local-socket entry points invoked by <see cref="LocalControlServer"/>.
internal partial class AgentOrchestrator {
    /// <summary>Reply to a <c>kcap agent ls</c> request with a tab-separated agent table.</summary>
    public Task HandleLocalListAsync(Stream stream, CancellationToken ct) {
        var lines = _agents.Values.Select(a =>
            $"{a.Id}\t{a.Status}\t{Cell(a.RepoPath)}\t{KindText(a.Kind)}\t{Cell(a.FlowRunId)}\t{Cell(a.FlowRole)}");

        return FrameCodec.WriteAsync(stream, new LocalFrame(FrameType.AgentList) { Text = string.Join('\n', lines) }, ct);
    }

    /// <summary>
    /// Neutralises the table's own delimiters in a free-form field. A repo path may legally
    /// contain a tab or newline, which would shift the reader's columns or split the row — and the
    /// CLI keys `stop --all`'s confirmation off the kind column, so a shifted row understates the
    /// blast radius the user is agreeing to.
    /// </summary>
    static string Cell(string? value) =>
        value is null ? "" : value.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

    /// Wire spelling of <see cref="LaunchKind"/>. Kept separate from the enum name so the table
    /// reads as a CLI column rather than a .NET identifier. A future kind that falls through the
    /// switch reports its own enum name rather than masquerading as "agent" — the CLI's
    /// `IsProtectedKind` then fails safe (protected) on anything it doesn't recognise, instead of
    /// the daemon advertising an unprotected kind that its own `Kind != Default` checks would
    /// still protect.
    static string KindText(LaunchKind kind) => kind switch {
        LaunchKind.Default    => "agent",
        LaunchKind.Review     => "review",
        LaunchKind.ReviewFlow => "review-flow",
        _                     => kind.ToString(),
    };

    /// <summary>
    /// The supervision payload's agent rows: every entry in _agents (all statuses — same
    /// visibility as `kcap agent ls`; quarantined-but-removed children are gone from _agents
    /// already). Order is a wire contract: created_at ascending, id-ordinal tie-break —
    /// ConcurrentDictionary enumeration order must never leak into the payload.
    /// </summary>
    internal List<AgentStatusDto> SnapshotAgentsForStatus() =>
        [.. _agents.Values
            .OrderBy(a => a.CreatedAt)
            .ThenBy(a => a.Id, StringComparer.Ordinal)
            .Select(a => new AgentStatusDto(
                a.Id, KindText(a.Kind), a.Vendor, a.RepoPath, a.Status,
                a.FlowRunId, a.FlowRole, a.RequesterUserId, a.CreatedAt,
                // Local spawns store "" for "no model" (HandleLocalSpawnAsync's LauncherContext),
                // and a server-driven launch with a blank requested model can retain "" too
                // (ModelSelectionLaunchPolicy.Evaluate treats blank as Honor, i.e. pass-through
                // unchanged) — but the wire contract pins absent = null. Normalize here, at the
                // wire boundary, rather than changing what AgentInstance stores.
                string.IsNullOrWhiteSpace(a.Model) ? null : a.Model, a.RequesterDisplay))];

    /// <summary>
    /// Serves the legacy <c>Stop</c> frame from older clients that predate --force. That frame
    /// has no force concept, so it always behaves as if --force were passed: an older client
    /// gets exactly its previous (unprotected) behaviour, and gains no new refusals it has no
    /// way to override.
    /// </summary>
    public Task HandleLocalStopAsync(string agentId, Stream stream, CancellationToken ct) =>
        HandleLocalStopV2Async(force: true, agentId, stream, ct);

    /// <summary>
    /// `kcap agent stop` with protection. A review or flow agent is refused unless the user
    /// passed --force; a stop-all reports them as `skipped` rather than silently omitting them.
    /// Calls the stop core directly rather than <c>HandleStopAgent</c>: the private-agent guard
    /// there defends against server-origin commands, and a request arriving on the daemon's own
    /// 0600 socket is the owner's. Stops run concurrently — each can take up to 25s (graceful
    /// wait plus terminate), so serial teardown would be unusable.
    /// </summary>
    public async Task HandleLocalStopV2Async(bool force, string agentId, Stream stream, CancellationToken ct) {
        if (agentId.Length == 0) {
            var all       = _agents.Values.ToList();
            var eligible  = all.Where(a => force || a.Kind == LaunchKind.Default).ToList();
            var results   = await Task.WhenAll(eligible.Select(StopAgentCoreAsync));
            var stopped   = eligible.Zip(results, (a, ok) => $"{a.Id}\t{StatusText(ok)}");

            // The exact negation of the eligible predicate above — not a set difference.
            // AgentInstance is a record with mutable fields (Status, LastOutputAt, ...), so
            // Except would hash teardown-mutable state while the eligible agents are still
            // draining from the WhenAll above, and could misreport a stopped agent as skipped too.
            var skipped = all.Where(a => !force && a.Kind != LaunchKind.Default).Select(a => $"{a.Id}\tskipped");

            await FrameCodec.WriteAsync(stream, LocalFrame.StopAck(string.Join('\n', stopped.Concat(skipped))), ct);

            return;
        }

        if (_agents.TryGetValue(agentId, out var agent)) {
            if (!force && agent.Kind != LaunchKind.Default) {
                // A flow participant going away mid-round strands the flow; a plain hosted
                // review has no round to strand, just a result that will never come back.
                var consequence = agent.Kind == LaunchKind.ReviewFlow
                    ? "Stopping it mid-round leaves the flow without a participant."
                    : "Stopping it discards the review before it can report back.";

                await FrameCodec.WriteAsync(stream, LocalFrame.Error(
                    $"{agentId} is a {ProtectionReason(agent)}. {consequence} Pass --force to stop it anyway."), ct);

                return;
            }

            var ok = await StopAgentCoreAsync(agent);
            await FrameCodec.WriteAsync(stream, LocalFrame.StopAck($"{agentId}\t{StatusText(ok)}"), ct);

            return;
        }

        // Not live here — it may be a survivor of a previous daemon incarnation, which the PID
        // record can still reap. This is why the client sends full ids verbatim. The record
        // carries the same Kind/FlowRunId/FlowRole the live agent would have, so protection still
        // applies — a review-flow survivor from a prior incarnation is refused exactly like a
        // live one. TryStopByPidRecordAsync itself stays policy-free (it's shared with the
        // server-origin HandleStopAgent path); the decision is made here, before it ever runs.
        if (!force && FindPidRecord(agentId) is { Kind: not nameof(LaunchKind.Default) } record) {
            var consequence = record.Kind == nameof(LaunchKind.ReviewFlow)
                ? "Stopping it mid-round leaves the flow without a participant."
                : "Stopping it discards the review before it can report back.";

            await FrameCodec.WriteAsync(stream, LocalFrame.Error(
                $"{agentId} is a {ProtectionReason(record)}. {consequence} Pass --force to stop it anyway."), ct);

            return;
        }

        var reaped = await TryStopByPidRecordAsync(agentId);

        await FrameCodec.WriteAsync(
            stream,
            reaped ? LocalFrame.StopAck($"{agentId}\t{StatusText(true)}") : LocalFrame.Error($"no such agent {agentId}"),
            ct);
    }

    static string StatusText(bool confirmedStopped) => confirmedStopped ? "stopped" : "failed";

    /// <summary>
    /// Spawn a new agent from a local <c>agent start</c> request, then attach the requesting
    /// client. The agent runs <b>PrivateLocal</b> (no per-agent server calls) in either an
    /// owned worktree (<c>--worktree</c>) or the user's borrowed cwd (default in-place).
    /// </summary>
    public async Task HandleLocalSpawnAsync(LocalFrame spawn, Stream stream, CancellationToken ct) {
        var (vendor, work, isPrivate, cwd, args, cols, rows) = FrameCodec.Spawn(spawn);

        if (!_launchers.TryGetValue(vendor, out var launcher)) {
            await FrameCodec.WriteAsync(stream, LocalFrame.Error($"Unknown vendor: {vendor}"), ct);
            return;
        }

        if (!Directory.Exists(cwd)) {
            await FrameCodec.WriteAsync(stream, LocalFrame.Error($"Directory does not exist: {cwd}"), ct);
            return;
        }

        var           agentId       = Guid.NewGuid().ToString("N");
        AgentInstance agent;
        WorktreeInfo? ownedWorktree = null; // tracked so a failure after creation cleans it up

        try {
            var worktree = work == WorkLocation.OwnedWorktree
                ? ownedWorktree = await _worktreeManager.CreateAsync(cwd)
                : WorktreeInfo.Borrowed(cwd);

            var ctx = new LauncherContext(
                agentId, cwd, worktree, Prompt: null, Model: "", Effort: null,
                Tools: null, IsReview: false, IsReviewFlow: false, Review: null, ReviewLaunch: null
            ) {
                Work = work
            };

            launcher.Prepare(ctx);
            var built = launcher.BuildPassthrough(ctx, args);

            // Records to the account either way. Keep KCAP_URL and re-add ANTHROPIC_API_KEY so
            // normal local auth survives UnixPtyProcess.Spawn's headless scrub (it applies
            // extraEnv after unsetenv).
            var env = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(_config.ServerUrl)) env["KCAP_URL"] = _config.ServerUrl;
            var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
            if (!string.IsNullOrEmpty(apiKey)) env["ANTHROPIC_API_KEY"] = apiKey;

            if (!isPrivate) {
                // Register like a UI-launched agent: hosted env so it's visible/drivable from the
                // owner's web UI, the session links via KCAP_AGENT_ID, and permissions route
                // through the daemon bridge. --private omits all of this.
                env["KCAP_RENDERED_AGENT"] = "1";
                env["KCAP_AGENT_ID"]       = agentId;
                if (_permissionBridge.BaseUrl is { } bridgeUrl) env["KCAP_DAEMON_URL"] = bridgeUrl;
            }

            var pty     = _ptyFactory.Spawn(launcher.CliPath, built.Args, worktree.Path, env, cols, rows);
            var runtime = new PtyHostedAgentRuntime(vendor, pty);

            agent = new AgentInstance(agentId, null, "", null, cwd, vendor, runtime, worktree, new CancellationTokenSource()) {
                // Every launch path must go through CreateActivityClock() so the stage-advance report
                // wiring is attached by construction. Inert here today (a local spawn is PTY-only and
                // stamps no stage) — but a hand-built clock is exactly how that wiring goes silently
                // missing the day this path grows an ACP runtime.
                ActivityClock  = CreateActivityClock(),
                IsPrivate      = isPrivate,
                IsLocalSpawned = true,
                Work           = work,
                McpConfigPath  = built.McpConfigPath,
                CurrentCols    = cols,
                CurrentRows    = rows
            };
            PublishAgent(agent);
        } catch (Exception ex) {
            // Don't leak a daemon-created worktree if Prepare / passthrough-arg building /
            // spawn fails after the worktree was created (mirrors the server launch path).
            if (ownedWorktree is { } leaked) {
                try { await WorktreeManager.RemoveAsync(leaked); } catch { /* best-effort */ }
            }

            await FrameCodec.WriteAsync(stream, LocalFrame.Error($"Launch failed: {ex.Message}"), ct);
            return;
        }

        // Register like a UI launch (no-op for --private). Best-effort: a registration hiccup
        // must not break the local terminal session.
        try { await RegisterAgentAsync(agent); }
        catch (Exception ex) { LogLocalRegisterFailed(ex, agentId); }

        _ = ReadAgentOutputAsync(agent);
        await AttachClientLoopAsync(agent, stream, ct);
    }

    /// <summary>Attach an existing agent to a local client (used by <c>kcap agent attach</c>).</summary>
    public Task HandleLocalAttachAsync(string agentId, Stream stream, CancellationToken ct) {
        if (!_agents.TryGetValue(agentId, out var agent))
            return FrameCodec.WriteAsync(stream, LocalFrame.Error($"no such agent {agentId}"), ct);

        // A runtime that emits no terminal output has nothing for a terminal to attach TO: its
        // stdout is protocol traffic (agy's NDJSON, ACP's JSON-RPC), and its output buffer is
        // therefore always empty. Attaching anyway painted a blank screen that never repaints and
        // only admitted the problem if the user typed (AttachClientLoopAsync's raw-input refusal) —
        // indistinguishable from a wedged daemon. Refuse by name instead, and say where the agent
        // actually lives. Decided here rather than in the CLI: `kcap agent attach` sends a full id
        // verbatim without fetching the agent table, so the client cannot know the vendor.
        if (!agent.Runtime.EmitsTerminalOutput)
            return FrameCodec.WriteAsync(stream, LocalFrame.Error(
                $"{agentId} is a hosted {agent.Runtime.Vendor} agent — it has no terminal to attach to. "
              + "Drive it from the dashboard."), ct);

        // A review or flow agent is addressed through the flow protocol, never by typing at it,
        // so the daemon — not the client — decides this attach carries no input.
        return AttachClientLoopAsync(agent, stream, ct, readOnly: agent.Kind != LaunchKind.Default);
    }

    /// Human-readable "why is this read-only/refused", carried on the AttachedReadOnly frame and
    /// the not-live StopV2 refusal below (which reads the same kind from a persisted PID record
    /// instead of a live AgentInstance).
    static string ProtectionReason(AgentInstance agent) => ProtectionReason(agent.Kind.ToString(), agent.FlowRunId, agent.FlowRole);

    static string ProtectionReason(AgentPidRecord record) => ProtectionReason(record.Kind, record.FlowRunId, record.FlowRole);

    /// A kind this build doesn't recognise reports its own name rather than being mislabelled as
    /// "review" — mirrors KindText's fail-safe shape.
    static string ProtectionReason(string kind, string? flowRunId, string? flowRole) {
        var label = kind switch {
            nameof(LaunchKind.ReviewFlow) => "review-flow",
            nameof(LaunchKind.Review)     => "review",
            _                             => kind,
        };
        var role = string.IsNullOrEmpty(flowRole) ? "" : $", role {flowRole}";
        var flow = string.IsNullOrEmpty(flowRunId) ? "" : $" (flow {flowRunId}{role})";

        return $"{label} agent{flow}";
    }

    /// <summary>
    /// Registers a local sink, replays the agent's buffered output once, then pumps the
    /// client's input (stdin/resize) until it detaches or disconnects. The agent keeps
    /// running either way — the sink is just removed.
    /// </summary>
    internal async Task AttachClientLoopAsync(
            AgentInstance agent, Stream stream, CancellationToken ct, bool readOnly = false) {
        // One NetworkStream, two writers (the sink's Stdout frames + Attached/Exited here):
        // serialise all writes through this lock. Reads (the input loop) are independent.
        var writeLock = new SemaphoreSlim(1, 1);

        async Task Send(LocalFrame f) {
            await writeLock.WaitAsync(ct);
            try { await FrameCodec.WriteAsync(stream, f, ct); } finally { writeLock.Release(); }
        }

        var sink = new LocalSocketSink(capacity: 4096, (chunk, _) => Send(LocalFrame.Stdout(chunk)));

        // Snapshot the replay buffer AND register the sink atomically under SinksLock (paired
        // with the read loop's locked append+enqueue) so no chunk is both replayed and sent
        // live, and none is dropped between the two.
        byte[] snapshot;
        lock (agent.SinksLock) {
            snapshot = agent.OutputBuffer.Snapshot();
            agent.LocalSinks.Add(sink);
        }

        try {
            // Bounded replay BEFORE any live chunk so the client paints a coherent screen.
            await Send(readOnly
                ? FrameCodec.AttachedReadOnly(agent.Id, ProtectionReason(agent), snapshot)
                : FrameCodec.Attached(agent.Id, snapshot));
            var pump = sink.RunAsync(ct);

            // Break this read loop when the agent exits on its own (CleanupAgentAsync trips
            // ExitedCts) — not only on client input — so a self-exiting agent (e.g. /exit)
            // doesn't leave us blocked here and never flush the final output or send Exited.
            using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct, agent.ExitedCts.Token);

            // ...and break it when the sink force-detaches (overflow / send failure). The sink
            // stops accepting output and completes its channel, so its pump finishes with
            // Detached set; without this the client keeps typing into a dead output path
            // (blind). Cancelling ends the loop so the client disconnects and reattaches for a
            // fresh replay — the intended force-detach behaviour.
            var detachMonitor = pump.ContinueWith(
                _ => { if (sink.Detached) { try { loopCts.Cancel(); } catch (ObjectDisposedException) { } } },
                TaskScheduler.Default
            );

            try {
                while (!loopCts.Token.IsCancellationRequested) {
                    var f = await FrameCodec.ReadAsync(stream, loopCts.Token);
                    if (f is null || f.Type == FrameType.Detach) break;

                    if (f.Type == FrameType.Stdin) {
                        if (readOnly) continue; // protected agent: input is never delivered

                        try {
                            await agent.Runtime.SendRawInputAsync(f.Bytes);
                        } catch (NotSupportedException) {
                            // ACP-backed runtimes (e.g. cursor) have no raw-input surface —
                            // AcpHostedAgentRuntime.SendRawInputAsync throws by design. Tell the
                            // client and detach gracefully instead of letting the exception
                            // escape the read loop and crash the attach handler.
                            try { await Send(LocalFrame.Error("This agent does not support local attach input")); } catch { /* client already gone */ }

                            break;
                        }
                    } else if (f.Type == FrameType.Resize) {
                        // A read-only viewer must not enter ClientDims, or the min-clamp would
                        // let an observer shrink the participant's terminal.
                        if (!readOnly) ApplyResizeClamp(agent, sink, f.Cols, f.Rows);
                    }
                }
            } catch (Exception ex) when (ex is EndOfStreamException or IOException or OperationCanceledException) {
                /* client gone or session ended */
            } finally {
                sink.Complete();
                await pump.ConfigureAwait(false);
                await detachMonitor.ConfigureAwait(false); // ensure the cancel ran before loopCts disposes
            }

            if (sink.Detached && !agent.Runtime.HasExited) {
                // We dropped this client because its output overflowed — tell it so the user
                // reattaches (a fresh `kcap agent attach` replays the buffer from a clean frame).
                try { await Send(LocalFrame.Error("terminal output overflowed — detached; reattach with `kcap agent attach`")); } catch { /* client already gone */ }
            }

            if (agent.Runtime.HasExited) {
                try { await Send(LocalFrame.Exited(agent.Runtime.ExitCode ?? 0)); } catch { /* client already gone */ }
            }
        } finally {
            lock (agent.SinksLock) {
                agent.LocalSinks.Remove(sink);
                agent.ClientDims.Remove(sink);
                ClampPtyLocked(agent); // a departing (possibly smaller) client must not leave the rest clamped
            }

            // A detach can grow the PTY (the smaller client left) — re-announce so web viewers re-lock.
            if (!agent.IsPrivate) _ = SafeSendDimsAsync(agent);
        }
    }

    void ApplyResizeClamp(AgentInstance agent, ITerminalSink sink, ushort cols, ushort rows) {
        lock (agent.SinksLock) {
            agent.ClientDims[sink] = new AgentInstance.Dim(cols, rows);
            ClampPtyLocked(agent);
        }

        // Announce the new clamped size so registered agents' web viewers re-lock. Outside the
        // lock (best-effort, fire-and-forget); no-op for --private.
        if (!agent.IsPrivate) _ = SafeSendDimsAsync(agent);
    }

    async Task SafeSendDimsAsync(AgentInstance agent) {
        try { await _server.SendTerminalDimensionsAsync(agent.Id, agent.CurrentCols, agent.CurrentRows); }
        catch (Exception ex) { LogTerminalDimsSendFailed(ex, agent.Id); }
    }

    /// <summary>
    /// Min-clamp the one PTY to the smallest cols × rows across every attached viewer — the local
    /// clients (<see cref="AgentInstance.ClientDims"/>) <b>and</b> the server-aggregated web viewers
    /// (<see cref="AgentInstance.WebDims"/>) — so no surface's redraw is corrupted (tmux semantics).
    /// Recomputed on local attach/detach/resize and on a server-origin web resize. Caller
    /// holds <see cref="AgentInstance.SinksLock"/>; no-op when no viewer has a reported size.
    /// </summary>
    void ClampPtyLocked(AgentInstance agent) {
        ushort c = 0, r = 0;

        foreach (var d in agent.ClientDims.Values) {
            if (c == 0 || d.Cols < c) c = d.Cols;
            if (r == 0 || d.Rows < r) r = d.Rows;
        }
        if (agent.WebDims is { } w) {
            if (c == 0 || w.Cols < c) c = w.Cols;
            if (r == 0 || w.Rows < r) r = w.Rows;
        }

        if (c > 0 && r > 0) {
            agent.Runtime.Resize(c, r);
            agent.CurrentCols = c;
            agent.CurrentRows = r;
        }
    }
}
