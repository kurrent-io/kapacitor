using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text.Json;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Core.Tests.Unit.LocalIpc;

/// <summary>
/// LocalControlOps one-shot stop/consent operations over a REAL Unix socket driven by a
/// scripted server (design spec §10). Harness conventions (short socket paths for the macOS
/// sockaddr_un ~104-byte limit, Windows guard, [NotInParallel], daemon-name→socket-path
/// arrangement) are copied from <see cref="LocalControlClientTests"/> — this is a one-shot
/// request/reply protocol rather than a long-lived subscribe stream, so <see cref="ConnScript"/>
/// also hands scripts the raw accepted <see cref="Socket"/> (not just the wrapping
/// <see cref="NetworkStream"/>), needed to force an abrupt RST close for
/// <see cref="Post_connect_reset"/> — LocalControlClientTests never needed that.
/// </summary>
public class LocalControlOpsTests {
    delegate Task ConnScript(Socket raw, NetworkStream s, CancellationToken ct);

    sealed class ScriptedOpsServer : IAsyncDisposable {
        readonly Socket _listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        readonly CancellationTokenSource _cts = new();
        readonly ConnScript[] _scripts;
        volatile int _served;
        readonly Task _accept;

        public int Served => _served;

        public ScriptedOpsServer(string sockPath, params ConnScript[] scripts) {
            _scripts = scripts;
            _listener.Bind(new UnixDomainSocketEndPoint(sockPath));
            _listener.Listen(8);
            _accept = Task.Run(async () => {
                try {
                    while (!_cts.IsCancellationRequested) {
                        var conn = await _listener.AcceptAsync(_cts.Token);
                        var script = _scripts[Math.Min(_served++, _scripts.Length - 1)];
                        _ = Task.Run(async () => {
                            using var c = conn;
                            await using var s = new NetworkStream(c, ownsSocket: false);
                            try { await script(c, s, _cts.Token); } catch { /* scripted teardown */ }
                        }, _cts.Token);
                    }
                } catch { /* shutdown */ }
            });
        }

        public async ValueTask DisposeAsync() {
            _cts.Cancel();
            _listener.Dispose();
            if (_accept is { } a) { try { await a; } catch { } }
        }
    }

    // ---- script building blocks ----
    static ConnScript StopAckThen(string payload) => async (_, s, ct) => {
        var f = await FrameCodec.ReadAsync(s, ct);                       // expect StopV2
        if (f?.Type == FrameType.StopV2)
            await FrameCodec.WriteAsync(s, LocalFrame.StopAck(payload), ct);
    };
    static ConnScript ErrorThen(string text) => async (_, s, ct) => {
        await FrameCodec.ReadAsync(s, ct);                                // consume the request, whatever it is
        await FrameCodec.WriteAsync(s, LocalFrame.Error(text), ct);
    };
    static ConnScript ConsentRulesThen(string json) => async (_, s, ct) => {
        var f = await FrameCodec.ReadAsync(s, ct);                       // expect ConsentRulesGet
        if (f?.Type == FrameType.ConsentRulesGet)
            await FrameCodec.WriteAsync(s, LocalFrame.ConsentJson(FrameType.ConsentRules, json), ct);
    };
    static ConnScript ConsentAckThen(string json) => async (_, s, ct) => {
        var f = await FrameCodec.ReadAsync(s, ct);                       // expect ConsentRulesPut
        if (f?.Type == FrameType.ConsentRulesPut)
            await FrameCodec.WriteAsync(s, LocalFrame.ConsentJson(FrameType.ConsentAck, json), ct);
    };
    static ConnScript ConsentResolveV2Ack(string json) => async (_, s, ct) => {
        var f = await FrameCodec.ReadAsync(s, ct);                       // expect ConsentResolveV2
        if (f?.Type == FrameType.ConsentResolveV2)
            await FrameCodec.WriteAsync(s, LocalFrame.ConsentJson(FrameType.ConsentAck, json), ct);
    };
    /// Same as <see cref="ConsentResolveV2Ack"/> but hands the request's raw Text to the caller
    /// before replying, so a test can assert what was actually written on the wire (the
    /// prompt_id echo) rather than just the parsed reply.
    static ConnScript ConsentResolveV2AckCapturing(string json, Action<string> captureRequestText) => async (_, s, ct) => {
        var f = await FrameCodec.ReadAsync(s, ct);
        if (f?.Type == FrameType.ConsentResolveV2) {
            captureRequestText(f.Text);
            await FrameCodec.WriteAsync(s, LocalFrame.ConsentJson(FrameType.ConsentAck, json), ct);
        }
    };
    /// A faithful v1 daemon: reads the raw 5-byte header AND the full payload it declares — the
    /// real FrameCodec.ReadAsync (src/Capacitor.Cli.Core/LocalIpc/FrameCodec.cs) always consumes
    /// header+payload before Decode ever runs, so it only throws InvalidDataException (unknown
    /// type byte) once the whole frame is off the wire. HandleConnectionAsync then catches/logs/
    /// closes, writing nothing. A header-only close would leave the ConsentResolveV2 JSON payload
    /// unread in the kernel socket buffer; closing with unread bytes sends RST on Linux (not on
    /// macOS), so CI would observe ECONNRESET (daemon_unreachable) instead of the intended clean
    /// EOF (unexpected_reply) — this is the ubuntu-latest-only CI failure this script fixes. NOT a
    /// routing-default Error reply — no deployed v1 daemon produces one for byte 18 (spec §4.1).
    static ConnScript V1CodecReject() => async (_, s, ct) => {
        var head = new byte[5];
        var read = 0;
        while (read < 5) {
            var n = await s.ReadAsync(head.AsMemory(read), ct);
            if (n == 0) return;
            read += n;
        }
        var len = BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(1));
        var payload = new byte[len];
        var got = 0;
        while (got < len) {
            var n = await s.ReadAsync(payload.AsMemory(got), ct);
            if (n == 0) return;
            got += n;
        }
        // v1 FrameCodec would throw here (Decode sees an unmapped type byte) — the server closes
        // the socket, writing nothing.
    };
    static ConnScript Eof() => async (_, s, ct) => { await FrameCodec.ReadAsync(s, ct); }; // read, close silently
    static ConnScript TruncatedHeader() => async (_, s, ct) => {
        await FrameCodec.ReadAsync(s, ct);
        await s.WriteAsync(new byte[] { (byte)FrameType.StopAck, 0 }, ct); // 2 of 5 header bytes, then close
    };
    /// A frame header naming a type byte FrameCodec has no case for — the codec's own
    /// InvalidDataException path (undecodable frame), distinct from "decodes fine but is an
    /// unexpected type" (covered by the switch defaults in StopAgentAsync/GetConsentPolicyAsync).
    static ConnScript UndecodableFrameType() => async (_, s, ct) => {
        await FrameCodec.ReadAsync(s, ct);
        var head = new byte[] { 200, 0, 0, 0, 0 }; // type=200 (unmapped), len=0
        await s.WriteAsync(head, ct);
    };
    /// AF_UNIX sockets have no TCP-style RST: an abrupt peer close is indistinguishable from a
    /// graceful one on the READ side — both surface as a clean EOF (see <see cref="Eof"/>), so a
    /// "reset" cannot be observed there on this platform. Writing to an already-aborted peer is
    /// the one transport failure this platform DOES surface distinctly ("broken pipe"), so the
    /// server closes with SO_LINGER(0) without reading anything, before the client's own request
    /// write lands — exercising the same catch branch a genuine mid-read reset would on a
    /// platform that has one. Paired with <see cref="Post_connect_reset"/>'s oversized agent id:
    /// a small request write completes synchronously (into the kernel socket buffer) faster than
    /// this abort can be scheduled, so the client would see a false "wrote ok" — see that test.
    static ConnScript AbruptReset() => (raw, _, _) => {
        raw.LingerState = new LingerOption(true, 0);
        raw.Close();
        return Task.CompletedTask;
    };
    static ConnScript Stall() => async (_, s, ct) => {
        await FrameCodec.ReadAsync(s, ct); await Task.Delay(Timeout.Infinite, ct);            // accept, never reply
    };

    /// Runs `body` against an ops client wired to a scripted server in an isolated socket dir.
    static async Task WithOpsAsync(
            ConnScript[] scripts, Func<LocalControlOps, Task> body, Action<LocalControlOps>? configure = null) {
        // Short name: macOS allows 104 bytes of socket path and $TMPDIR takes 49.
        using var sockDir = new TempDir("lco");
        DaemonLockPaths.OverrideDirectoryForTesting(sockDir.Path);
        try {
            var name = "lco-" + Guid.NewGuid().ToString("N")[..6];
            await using var server = new ScriptedOpsServer(LocalSocketPaths.Socket(name), scripts);
            var ops = new LocalControlOps(name) {
                ConnectTimeout = TimeSpan.FromSeconds(2),
                ConsentReplyTimeout = TimeSpan.FromSeconds(2),
                StopReplyTimeout = TimeSpan.FromSeconds(2),
            };
            configure?.Invoke(ops);
            await body(ops);
        } finally {
            DaemonLockPaths.OverrideDirectoryForTesting(null);
        }
    }

    // ---- StopAgentAsync ----

    [Test]
    [NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
    public async Task Stop_ok() {
        if (OperatingSystem.IsWindows()) return;

        await WithOpsAsync([StopAckThen("a1\tstopped")], async ops => {
            var result = await ops.StopAgentAsync("a1", false, CancellationToken.None);
            await Assert.That(result.Ok).IsTrue();
            await Assert.That(result.Status).IsEqualTo("stopped");
            await Assert.That(result.Error).IsNull();
        });
    }

    [Test]
    [NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
    public async Task Stop_failed() {
        if (OperatingSystem.IsWindows()) return;

        await WithOpsAsync([StopAckThen("a1\tfailed")], async ops => {
            var result = await ops.StopAgentAsync("a1", false, CancellationToken.None);
            await Assert.That(result.Ok).IsFalse();
            await Assert.That(result.Status).IsEqualTo("failed");
            await Assert.That(result.Error).IsNull();
        });
    }

    [Test]
    [NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
    public async Task Stop_skipped() {
        if (OperatingSystem.IsWindows()) return;

        await WithOpsAsync([StopAckThen("a1\tskipped")], async ops => {
            var result = await ops.StopAgentAsync("a1", false, CancellationToken.None);
            await Assert.That(result.Ok).IsFalse();
            await Assert.That(result.Status).IsEqualTo("skipped");
            await Assert.That(result.Error).IsNull();
        });
    }

    [Test]
    [NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
    public async Task Stop_error_frame() {
        if (OperatingSystem.IsWindows()) return;

        await WithOpsAsync([ErrorThen("x is protected")], async ops => {
            var result = await ops.StopAgentAsync("x", false, CancellationToken.None);
            await Assert.That(result.Ok).IsFalse();
            await Assert.That(result.Status).IsEqualTo("error");
            await Assert.That(result.Error).IsEqualTo("x is protected");
        });
    }

    [Test]
    [NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
    public async Task Stop_missing_line() {
        if (OperatingSystem.IsWindows()) return;

        await WithOpsAsync([StopAckThen("other\tstopped")], async ops => {
            var ex = await Assert.ThrowsAsync<LocalControlOpsException>(
                async () => await ops.StopAgentAsync("a1", false, CancellationToken.None));
            await Assert.That(ex!.Reason).IsEqualTo("unexpected_reply");
        });
    }

    [Test]
    [NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
    public async Task Stop_duplicate_line() {
        if (OperatingSystem.IsWindows()) return;

        await WithOpsAsync([StopAckThen("a1\tstopped\na1\tstopped")], async ops => {
            var ex = await Assert.ThrowsAsync<LocalControlOpsException>(
                async () => await ops.StopAgentAsync("a1", false, CancellationToken.None));
            await Assert.That(ex!.Reason).IsEqualTo("unexpected_reply");
        });
    }

    [Test]
    [NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
    public async Task Stop_three_fields() {
        if (OperatingSystem.IsWindows()) return;

        await WithOpsAsync([StopAckThen("a1\tstopped\textra")], async ops => {
            var ex = await Assert.ThrowsAsync<LocalControlOpsException>(
                async () => await ops.StopAgentAsync("a1", false, CancellationToken.None));
            await Assert.That(ex!.Reason).IsEqualTo("unexpected_reply");
        });
    }

    [Test]
    [NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
    public async Task Stop_unknown_status() {
        if (OperatingSystem.IsWindows()) return;

        await WithOpsAsync([StopAckThen("a1\tbogus")], async ops => {
            var ex = await Assert.ThrowsAsync<LocalControlOpsException>(
                async () => await ops.StopAgentAsync("a1", false, CancellationToken.None));
            await Assert.That(ex!.Reason).IsEqualTo("unexpected_reply");
        });
    }

    /// An empty agentId is not "stop nothing" on the wire — the daemon's StopV2 handler reads it
    /// as stop-ALL (AgentOrchestrator.LocalIpc.cs). No scripted server or socket-dir arrangement
    /// here: the whole point is that the guard throws BEFORE StopAgentAsync ever reaches
    /// ExchangeAsync/LocalSocketPaths, so this runs against a daemon name nothing is listening on
    /// and would fail with LocalControlOpsException(daemon_unreachable) if the guard were
    /// missing or placed after the connect attempt.
    [Test]
    public async Task Stop_empty_agent_id_throws_before_connecting() {
        var ops = new LocalControlOps("lco-nonexistent-" + Guid.NewGuid().ToString("N")[..6]);
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await ops.StopAgentAsync("", false, CancellationToken.None));
    }

    // ---- GetConsentPolicyAsync ----

    [Test]
    [NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
    public async Task Get_policy_ok() {
        if (OperatingSystem.IsWindows()) return;

        const string json = """{"default":"prompt","prompt_timeout_seconds":30,"rules":[{"action":"deny","requester":null,"kind":null,"repo":null,"vendor":null}]}""";
        await WithOpsAsync([ConsentRulesThen(json)], async ops => {
            var policy = await ops.GetConsentPolicyAsync(CancellationToken.None);
            await Assert.That(policy.Default).IsEqualTo("prompt");
            await Assert.That(policy.PromptTimeoutSeconds).IsEqualTo(30);
            await Assert.That(policy.Rules.Count).IsEqualTo(1);
            await Assert.That(policy.Rules[0].Action).IsEqualTo("deny");
        });
    }

    [Test]
    [NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
    [Arguments("""{"default":"allow","prompt_timeout_seconds":45,"rules":null}""")]                    // null rules
    [Arguments("""{"default":"allow","prompt_timeout_seconds":45,"rules":[null]}""")]                  // null rule element
    [Arguments("""{"default":"bogus","prompt_timeout_seconds":45,"rules":[]}""")]                      // unknown default
    [Arguments("""{"default":"allow","prompt_timeout_seconds":0,"rules":[]}""")]                       // timeout < 1
    public async Task Get_policy_invalid(string json) {
        if (OperatingSystem.IsWindows()) return;

        await WithOpsAsync([ConsentRulesThen(json)], async ops => {
            var ex = await Assert.ThrowsAsync<LocalControlOpsException>(
                async () => await ops.GetConsentPolicyAsync(CancellationToken.None));
            await Assert.That(ex!.Reason).IsEqualTo("unexpected_reply");
        });
    }

    [Test]
    [NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
    public async Task Get_policy_error_frame() {
        if (OperatingSystem.IsWindows()) return;

        await WithOpsAsync([ErrorThen("not authorized")], async ops => {
            var ex = await Assert.ThrowsAsync<LocalControlOpsException>(
                async () => await ops.GetConsentPolicyAsync(CancellationToken.None));
            await Assert.That(ex!.Reason).IsEqualTo("daemon_rejected");
            await Assert.That(ex.Message).IsEqualTo("not authorized");
        });
    }

    // ---- PutConsentPolicyAsync ----

    [Test]
    [NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
    public async Task Put_ack_ok() {
        if (OperatingSystem.IsWindows()) return;

        await WithOpsAsync([ConsentAckThen("""{"ok":true,"error":null}""")], async ops => {
            var ack = await ops.PutConsentPolicyAsync(new ConsentPolicyDto("allow", 45, []), CancellationToken.None);
            await Assert.That(ack.Ok).IsTrue();
            await Assert.That(ack.Error).IsNull();
        });
    }

    [Test] // {} is a wire-shape edge case (STJ default bool), not an exception — presentation is the app's job
    [NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
    public async Task Put_ack_empty_object() {
        if (OperatingSystem.IsWindows()) return;

        await WithOpsAsync([ConsentAckThen("{}")], async ops => {
            var ack = await ops.PutConsentPolicyAsync(new ConsentPolicyDto("allow", 45, []), CancellationToken.None);
            await Assert.That(ack.Ok).IsFalse();
            await Assert.That(ack.Error).IsNull();
        });
    }

    [Test]
    [NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
    public async Task Put_error_frame() {
        if (OperatingSystem.IsWindows()) return;

        await WithOpsAsync([ErrorThen("not authorized")], async ops => {
            var ex = await Assert.ThrowsAsync<LocalControlOpsException>(
                async () => await ops.PutConsentPolicyAsync(new ConsentPolicyDto("allow", 45, []), CancellationToken.None));
            await Assert.That(ex!.Reason).IsEqualTo("daemon_rejected");
            await Assert.That(ex.Message).IsEqualTo("not authorized");
        });
    }

    // ---- ResolveConsentAsync ----

    [Test]
    [NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
    [Arguments("""{"ok":true,"error":null,"rule_saved":null}""", true, null, null)]
    [Arguments("""{"ok":true,"error":"partial rule save failure","rule_saved":false}""", true, "partial rule save failure", false)]
    [Arguments("""{"ok":false,"error":"no pending consent request with that id","rule_saved":true}""", false, "no pending consent request with that id", true)]
    [Arguments("""{"ok":false,"error":"x"}""", false, "x", null)] // old-format ack: no rule_saved member at all
    public async Task Resolve_ack_shapes(string json, bool ok, string? error, bool? ruleSaved) {
        if (OperatingSystem.IsWindows()) return;

        await WithOpsAsync([ConsentResolveV2Ack(json)], async ops => {
            var ack = await ops.ResolveConsentAsync(new ConsentResolveDto("r1", "allow", null, "p1"), CancellationToken.None);
            await Assert.That(ack.Ok).IsEqualTo(ok);
            await Assert.That(ack.Error).IsEqualTo(error);
            await Assert.That(ack.RuleSaved).IsEqualTo(ruleSaved);
        });
    }

    [Test]
    [NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
    public async Task Resolve_echoes_prompt_id_on_the_written_frame() {
        if (OperatingSystem.IsWindows()) return;

        string? sentText = null;
        await WithOpsAsync(
            [ConsentResolveV2AckCapturing("""{"ok":true,"error":null,"rule_saved":null}""", t => sentText = t)],
            async ops => await ops.ResolveConsentAsync(new ConsentResolveDto("r1", "allow", null, "prompt-xyz"), CancellationToken.None));

        var sent = JsonSerializer.Deserialize(sentText!, ConsentIpcJsonContext.Default.ConsentResolveDto);
        await Assert.That(sent!.PromptId).IsEqualTo("prompt-xyz");
    }

    [Test]
    [NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
    public async Task Resolve_maps_error_frame_to_daemon_rejected() {
        if (OperatingSystem.IsWindows()) return;

        await WithOpsAsync([ErrorThen("nope")], async ops => {
            var ex = await Assert.ThrowsAsync<LocalControlOpsException>(
                async () => await ops.ResolveConsentAsync(new ConsentResolveDto("r1", "allow", null, "p1"), CancellationToken.None));
            await Assert.That(ex!.Reason).IsEqualTo("daemon_rejected");
            await Assert.That(ex.Message).IsEqualTo("nope");
        });
    }

    /// The incarnation-swap pin (spec §9/§10): a v1 daemon fails closed at its own codec rather
    /// than resolving by request id without the identity check, so the caller must see this as
    /// "nothing was resolved", never as a successful ack.
    [Test]
    [NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
    public async Task Resolve_against_a_v1_codec_observes_eof_as_unexpected_reply_and_nothing_was_resolved() {
        if (OperatingSystem.IsWindows()) return;

        await WithOpsAsync([V1CodecReject()], async ops => {
            var ex = await Assert.ThrowsAsync<LocalControlOpsException>(
                async () => await ops.ResolveConsentAsync(new ConsentResolveDto("r1", "allow", null, "p1"), CancellationToken.None));
            await Assert.That(ex!.Reason).IsEqualTo("unexpected_reply");
            await Assert.That(ex.Message).IsEqualTo("daemon closed the connection without replying");
        });
    }

    [Test]
    [NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
    public async Task Resolve_clean_eof() {
        if (OperatingSystem.IsWindows()) return;

        await WithOpsAsync([Eof()], async ops => {
            var ex = await Assert.ThrowsAsync<LocalControlOpsException>(
                async () => await ops.ResolveConsentAsync(new ConsentResolveDto("r1", "allow", null, "p1"), CancellationToken.None));
            await Assert.That(ex!.Reason).IsEqualTo("unexpected_reply");
        });
    }

    [Test]
    [NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
    public async Task Resolve_malformed_ack() {
        if (OperatingSystem.IsWindows()) return;

        await WithOpsAsync([ConsentResolveV2Ack("not json")], async ops => {
            var ex = await Assert.ThrowsAsync<LocalControlOpsException>(
                async () => await ops.ResolveConsentAsync(new ConsentResolveDto("r1", "allow", null, "p1"), CancellationToken.None));
            await Assert.That(ex!.Reason).IsEqualTo("unexpected_reply");
        });
    }

    [Test] // real short timeout, matching this file's existing choice of real time over FakeTimeProvider for socket tests
    [NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
    public async Task Resolve_reply_timeout() {
        if (OperatingSystem.IsWindows()) return;

        await WithOpsAsync([Stall()], async ops => {
            var ex = await Assert.ThrowsAsync<LocalControlOpsException>(
                async () => await ops.ResolveConsentAsync(new ConsentResolveDto("r1", "allow", null, "p1"), CancellationToken.None));
            await Assert.That(ex!.Reason).IsEqualTo("timed_out");
        }, configure: ops => ops.ConsentReplyTimeout = TimeSpan.FromMilliseconds(100));
    }

    [Test]
    [NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
    public async Task Resolve_caller_cancellation() {
        if (OperatingSystem.IsWindows()) return;

        await WithOpsAsync([Stall()], async ops => {
            using var cts = new CancellationTokenSource();
            var task = ops.ResolveConsentAsync(new ConsentResolveDto("r1", "allow", null, "p1"), cts.Token);
            await Task.Delay(50); // let connect+write land so cancellation is observed during the reply wait
            cts.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await task);
        }, configure: ops => ops.ConsentReplyTimeout = TimeSpan.FromSeconds(30));
    }

    // ---- shared transport classification (exercised via StopAgentAsync) ----

    [Test]
    [NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
    public async Task Clean_eof() {
        if (OperatingSystem.IsWindows()) return;

        await WithOpsAsync([Eof()], async ops => {
            var ex = await Assert.ThrowsAsync<LocalControlOpsException>(
                async () => await ops.StopAgentAsync("a1", false, CancellationToken.None));
            await Assert.That(ex!.Reason).IsEqualTo("unexpected_reply");
        });
    }

    [Test]
    [NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
    public async Task Truncated_frame() {
        if (OperatingSystem.IsWindows()) return;

        await WithOpsAsync([TruncatedHeader()], async ops => {
            var ex = await Assert.ThrowsAsync<LocalControlOpsException>(
                async () => await ops.StopAgentAsync("a1", false, CancellationToken.None));
            await Assert.That(ex!.Reason).IsEqualTo("unexpected_reply");
        });
    }

    [Test] // design spec §10: "undecodable frame ... → unexpected_reply" — FrameCodec.ReadAsync's
           // own InvalidDataException path, distinct from EndOfStreamException (Truncated_frame)
    [NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
    public async Task Undecodable_frame_type() {
        if (OperatingSystem.IsWindows()) return;

        await WithOpsAsync([UndecodableFrameType()], async ops => {
            var ex = await Assert.ThrowsAsync<LocalControlOpsException>(
                async () => await ops.StopAgentAsync("a1", false, CancellationToken.None));
            await Assert.That(ex!.Reason).IsEqualTo("unexpected_reply");
        });
    }

    [Test] // an ordinary small request write completes synchronously into the kernel socket
           // buffer before a background abort can be scheduled (races unfavorably, verified
           // empirically), so the request carries an oversized agent id: a write that big can't
           // complete in one synchronous syscall, forcing a genuine async suspension that gives
           // AbruptReset's SO_LINGER(0) close real wall-clock time to land first — deterministic
           // "broken pipe" every run, not a timing-dependent flake.
    [NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
    public async Task Post_connect_reset() {
        if (OperatingSystem.IsWindows()) return;

        var hugeAgentId = new string('a', 6 * 1024 * 1024);
        await WithOpsAsync([AbruptReset()], async ops => {
            var ex = await Assert.ThrowsAsync<LocalControlOpsException>(
                async () => await ops.StopAgentAsync(hugeAgentId, false, CancellationToken.None));
            await Assert.That(ex!.Reason).IsEqualTo("daemon_unreachable");
        });
    }

    [Test]
    [NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
    public async Task Connect_failure() {
        if (OperatingSystem.IsWindows()) return;

        using var sockDir = new TempDir("lco");
        DaemonLockPaths.OverrideDirectoryForTesting(sockDir.Path);
        try {
            var ops = new LocalControlOps("lco-none") { ConnectTimeout = TimeSpan.FromSeconds(2) };
            var ex = await Assert.ThrowsAsync<LocalControlOpsException>(
                async () => await ops.StopAgentAsync("a1", false, CancellationToken.None));
            await Assert.That(ex!.Reason).IsEqualTo("daemon_unreachable");
        } finally {
            DaemonLockPaths.OverrideDirectoryForTesting(null);
        }
    }

    [Test] // real short timeout, matching LocalControlClientTests' choice of real time over FakeTimeProvider for socket tests
    [NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
    public async Task Reply_timeout() {
        if (OperatingSystem.IsWindows()) return;

        await WithOpsAsync([Stall()], async ops => {
            var ex = await Assert.ThrowsAsync<LocalControlOpsException>(
                async () => await ops.StopAgentAsync("a1", false, CancellationToken.None));
            await Assert.That(ex!.Reason).IsEqualTo("timed_out");
        }, configure: ops => ops.StopReplyTimeout = TimeSpan.FromMilliseconds(100));
    }

    [Test]
    [NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
    public async Task Caller_cancellation() {
        if (OperatingSystem.IsWindows()) return;

        await WithOpsAsync([Stall()], async ops => {
            using var cts = new CancellationTokenSource();
            var task = ops.StopAgentAsync("a1", false, cts.Token);
            await Task.Delay(50); // let connect+write land so cancellation is observed during the reply wait
            cts.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await task);
        }, configure: ops => ops.StopReplyTimeout = TimeSpan.FromSeconds(30));
    }
}
