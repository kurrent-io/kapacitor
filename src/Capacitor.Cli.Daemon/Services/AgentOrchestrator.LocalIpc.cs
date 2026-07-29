using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Daemon.Services;

/// Local-socket entry points invoked by <see cref="LocalControlServer"/>.
internal partial class AgentOrchestrator {
    /// <summary>Reply to a <c>kcap agent ls</c> request with a tab-separated agent table.</summary>
    public Task HandleLocalListAsync(Stream stream, CancellationToken ct) {
        var lines = _agents.Values.Select(a =>
            $"{a.Id}\t{a.Status}\t{a.RepoPath}\t{KindText(a.Kind)}\t{a.FlowRunId ?? ""}\t{a.FlowRole ?? ""}");

        return FrameCodec.WriteAsync(stream, new LocalFrame(FrameType.AgentList) { Text = string.Join('\n', lines) }, ct);
    }

    /// Wire spelling of <see cref="LaunchKind"/>. Kept separate from the enum name so the table
    /// reads as a CLI column rather than a .NET identifier.
    static string KindText(LaunchKind kind) => kind switch {
        LaunchKind.Review     => "review",
        LaunchKind.ReviewFlow => "review-flow",
        _                     => "agent",
    };

    /// <summary>
    /// Stop one agent (or every agent, when <paramref name="agentId"/> is empty) on behalf of
    /// `kcap agent stop`. Calls the stop core directly rather than <c>HandleStopAgent</c>: the
    /// private-agent guard there defends against server-origin commands, and a request arriving
    /// on the daemon's own 0600 socket is the owner's. Stops run concurrently — each can take up
    /// to 25s (graceful wait plus terminate), so serial teardown would be unusable.
    /// </summary>
    public async Task HandleLocalStopAsync(string agentId, Stream stream, CancellationToken ct) {
        if (agentId.Length == 0) {
            var all     = _agents.Values.ToList();
            var results = await Task.WhenAll(all.Select(StopAgentCoreAsync));
            var lines   = all.Zip(results, (a, ok) => $"{a.Id}\t{StatusText(ok)}");
            await FrameCodec.WriteAsync(stream, LocalFrame.StopAck(string.Join('\n', lines)), ct);

            return;
        }

        if (_agents.TryGetValue(agentId, out var agent)) {
            var ok = await StopAgentCoreAsync(agent);
            await FrameCodec.WriteAsync(stream, LocalFrame.StopAck($"{agentId}\t{StatusText(ok)}"), ct);

            return;
        }

        // Not live here — it may be a survivor of a previous daemon incarnation, which the PID
        // record can still reap. This is why the client sends full ids verbatim.
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
                IsPrivate      = isPrivate,
                IsLocalSpawned = true,
                Work           = work,
                McpConfigPath  = built.McpConfigPath,
                CurrentCols    = cols,
                CurrentRows    = rows
            };
            _agents[agentId] = agent;
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

        // A review or flow agent is addressed through the flow protocol, never by typing at it,
        // so the daemon — not the client — decides this attach carries no input.
        return AttachClientLoopAsync(agent, stream, ct, readOnly: agent.Kind != LaunchKind.Default);
    }

    /// Human-readable "why is this read-only", carried on the AttachedReadOnly frame.
    static string ProtectionReason(AgentInstance agent) {
        var kind = agent.Kind == LaunchKind.ReviewFlow ? "review-flow" : "review";
        var role = string.IsNullOrEmpty(agent.FlowRole) ? "" : $", role {agent.FlowRole}";
        var flow = string.IsNullOrEmpty(agent.FlowRunId) ? "" : $" (flow {agent.FlowRunId}{role})";

        return $"{kind} agent{flow}";
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
