using System.Net.Sockets;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Tests.Unit;

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
        var sockDir = Directory.CreateTempSubdirectory("kcap-lco-");
        DaemonLockPaths.OverrideDirectoryForTesting(sockDir.FullName);
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
            try { Directory.Delete(sockDir.FullName, true); } catch { }
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

        var sockDir = Directory.CreateTempSubdirectory("kcap-lco-");
        DaemonLockPaths.OverrideDirectoryForTesting(sockDir.FullName);
        try {
            var ops = new LocalControlOps("lco-none") { ConnectTimeout = TimeSpan.FromSeconds(2) };
            var ex = await Assert.ThrowsAsync<LocalControlOpsException>(
                async () => await ops.StopAgentAsync("a1", false, CancellationToken.None));
            await Assert.That(ex!.Reason).IsEqualTo("daemon_unreachable");
        } finally {
            DaemonLockPaths.OverrideDirectoryForTesting(null);
            try { Directory.Delete(sockDir.FullName, true); } catch { }
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
