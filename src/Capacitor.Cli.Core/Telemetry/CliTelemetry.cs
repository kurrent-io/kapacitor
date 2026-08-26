using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Telemetry;

/// <summary>
/// The only telemetry surface call sites touch. Every method swallows every exception:
/// an exception escaping to the NativeAOT runtime aborts the process (see Program.cs), so a
/// telemetry bug must never become a crash-on-every-command regression.
/// </summary>
public static class CliTelemetry {
    const string Endpoint = "https://phog.kurrent.io";
    const string Token    = "phc_DeHBgHGersY4LmDlADnPrsCPOAmMO7QFOH8f4DVEVmD";

    static readonly TimeSpan FlushBudget = TimeSpan.FromSeconds(1.5);

    static TelemetryClient? _client;
    static string?          _deviceId;
    static string?          _orgGroup;
    static JsonObject       _shared = new();
    static bool             _debug;
    static bool             _suppressedSticky; // Once set true, remains true for process lifetime

    /// <summary>Test seam: when set, events are collected here instead of being queued.</summary>
    public static List<TelemetryEvent>? TestSink { get; set; }

    public static bool Enabled { get; private set; }

    /// <summary>
    /// Restores every static below to its pristine, never-initialized state. Every test that
    /// touches these statics (assigns <see cref="TestSink"/>, calls <see cref="Initialize"/>,
    /// or drives a code path that reaches <see cref="DiscardAndDisable"/>) must call this FIRST —
    /// before assigning <see cref="TestSink"/> — because a prior test in the same process can
    /// leave <see cref="Enabled"/> false: <see cref="DiscardAndDisable"/> is real production
    /// behaviour (it runs whenever telemetry is persisted to "off"), not a test artifact, so its
    /// effects are exactly the kind of thing a later test must not silently inherit.
    /// </summary>
    public static void Reset() {
        _client = null; _deviceId = null; _orgGroup = null;
        lock (_sharedGate) _shared = new JsonObject();
        Enabled = false; TestSink = null; _suppressedSticky = false;
    }

    /// <summary>
    /// The app-spawned-child marker (spec decision 9): consumed for telemetry suppression and
    /// REMOVED from the process environment before command dispatch, so nothing this process
    /// spawns (a detached daemon, hosted children) can observe it. Never touches the user's own
    /// KCAP_TELEMETRY choice.
    /// </summary>
    public const string SpawnNoTelemetryVar = "KCAP_APP_SPAWN_NO_TELEMETRY";

    /// <summary>
    /// Consumes the spawn marker from the environment and marks suppression as sticky for this
    /// process. Sets <see cref="_suppressedSticky"/> so every future Initialize call in this
    /// process honors suppression regardless of its `suppressed` parameter.
    /// </summary>
    public static bool ConsumeSpawnMarker(Func<string, string?> get, Action<string> clear) {
        if (string.IsNullOrEmpty(get(SpawnNoTelemetryVar))) return false;
        clear(SpawnNoTelemetryVar);
        _suppressedSticky = true;
        return true;
    }

    public static void Initialize(string command, string? serverUrl, bool loggedIn, ConfigRoot config, bool suppressed = false) {
        try {
            if (suppressed) {
                _suppressedSticky = true;
            }
            if (_suppressedSticky) return; // app-spawned child: no notice, no device id, no events, _client stays null
            Enabled = TelemetrySettings.Resolve(TelemetryState.PersistedEnabled(config)).Enabled
                   && CommandEvents.IsReportable(command);
            if (!Enabled) return;

            _debug = Environment.GetEnvironmentVariable("KCAP_TELEMETRY_DEBUG") == "1";

            // A device id that can't be persisted still gets an in-memory-only id for this
            // process, rather than disabling telemetry outright: silently going dark on a disk
            // hiccup costs more in data quality than a marginally inflated unique-device count in
            // this rare fallback case.
            _deviceId = TelemetryDeviceId.GetOrCreate(config) ?? Guid.NewGuid().ToString("N");

            var version = Version();

            _orgGroup = PostHogPayload.OrgGroup(serverUrl);
            var shared = new JsonObject {
                ["source"]        = "cli",
                ["cli_version"]   = version,
                ["build_channel"] = TelemetryEnvironment.BuildChannel(version),
                ["os"]            = OS(),
                ["arch"]          = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
                ["is_ci"]         = TelemetryEnvironment.IsCi(),
                ["is_headless"]   = Auth.HeadlessEnvironment.IsHeadless(),
                ["has_server"]    = serverUrl is not null,
                ["logged_in"]     = loggedIn,
            };
            lock (_sharedGate) _shared = shared;

            if (TestSink is null)
                _client = new TelemetryClient(new HttpClientHandler(), Spool(config), Token, Endpoint);

            // "mcp-server" is the re-initialise long-lived MCP servers perform on top of the
            // denylisted "mcp" (see McpTelemetry) — an agent-spawned, non-interactive process
            // whose stderr no human is watching. kcap-memory/-sessions/-flows/-review
            // auto-register and spawn on every agent session, so on a fresh machine one of them
            // is plausibly the very first kcap-family process ever run. Letting the notice fire
            // there would print the disclosure into a void AND consume the once-per-device
            // marker, so no human-invoked command would ever show it — silently reproducing the
            // silent-by-default posture this feature exists to avoid. Skip the notice, the
            // marker, and the cli_first_run event for this pseudo-command; the first
            // human-invoked (reportable, non-"mcp-server") command still shows it as designed.
            if (command != "mcp-server")
                NoticeAndFirstRun(config);
        } catch {
            Enabled = false;
        }
    }

    /// <summary>
    /// Guards <see cref="_shared"/>. It used to be written once during <see cref="Initialize"/>
    /// and only read afterwards, so no lock was needed. It is now also written from the loopback
    /// browser's background wait, which merges the returned web identity at whatever moment the
    /// browser comes back — and that moment coincides with the foreground funnel by design, since
    /// sign-in reports completion immediately after the browser call returns.
    /// <para>Unsynchronised, an insert during a capture's read faults the read, the exception is
    /// swallowed on the way out (telemetry must never throw), and the event silently never
    /// appears. Reproduced, and it takes the event the feature exists to measure.</para>
    /// </summary>
    static readonly object _sharedGate = new();

    /// <summary>Queue an event for the exit flush.</summary>
    public static void Capture(string name, JsonObject properties) {
        try {
            if (!Enabled) return;

            lock (_sharedGate)
                foreach (var (key, value) in _shared)
                    properties[key] ??= value?.DeepClone();

            var e = new TelemetryEvent(name, properties, DateTimeOffset.UtcNow);

            if (_debug) Console.Error.WriteLine($"[telemetry] {name} {DebugRender(properties)}");

            if (TestSink is not null) TestSink.Add(e);
            else                      _client?.Enqueue(e);
        } catch { }
    }

    /// <summary>
    /// Attach a property to every event this process captures from here on. Swallowing like
    /// everything else in this class.
    /// </summary>
    public static void AddSharedProperty(string name, JsonNode? value) {
        try {
            if (!Enabled) return;

            lock (_sharedGate) _shared[name] = value;
        } catch { }
    }

    /// <summary>
    /// Attach several properties as ONE step, so no event can be captured carrying half of them.
    /// The returned web identity arrives as a set — an arm and one id per host — and an event
    /// holding one host's id while the other is still missing would read as a real absence rather
    /// than a moment mid-merge.
    /// </summary>
    public static void AddSharedProperties(JsonObject properties) {
        try {
            if (!Enabled) return;

            lock (_sharedGate)
                foreach (var (name, value) in properties)
                    _shared[name] = value?.DeepClone();
        } catch { }
    }

    /// <summary>
    /// Queue an event and flush immediately, rather than leaving it for the exit-time flush.
    /// Deliberately sync-over-async: setup funnel steps must reach PostHog before an abandoned
    /// run dies — the population being measured is people who quit mid-setup and never run kcap
    /// again, so a deferred event is a lost event, not a delayed one. Safe here because this is a
    /// console app with no SynchronizationContext to deadlock against; do not convert to
    /// fire-and-forget.
    /// </summary>
    public static void CaptureNow(string name, JsonObject properties) {
        Capture(name, properties);
        FlushAndClose().GetAwaiter().GetResult();
    }

    public static void RecordCommand(string command, string[] args, int exitCode, long durationMs) {
        try {
            if (!Enabled || !CommandEvents.IsReportable(command)) return;

            var props = new JsonObject {
                ["command"]     = CommandEvents.ReportableCommand(command),
                ["exit_code"]   = exitCode,
                ["duration_ms"] = durationMs,
            };

            if (CommandEvents.Subcommand(command, args) is { } sub) props["subcommand"] = sub;

            var flags = CommandEvents.Flags(args);
            if (flags.Length > 0) {
                var arr = new JsonArray();
                foreach (var f in flags) {
                    // Not a collection expression, and not arr.Add(f) either: JsonArray.Add<T>(T)
                    // binds whenever the argument's static type is narrower than JsonNode?, which
                    // "f: string" is — so a bare Add(f) still pulls in the AOT-unsafe generic
                    // overload. Only an argument statically typed JsonNode? selects the plain
                    // Add(JsonNode?) instance method.
                    JsonNode? node = JsonValue.Create(f);
                    arr.Add(node);
                }
                props["flags"] = arr;
            }

            Capture("cli_command", props);
        } catch { }
    }

    public static async Task FlushAndClose() {
        try {
            if (_client is null || _deviceId is null) return;
            await _client.FlushAsync(_deviceId, _orgGroup, FlushBudget);
        } catch { }
    }

    /// <summary>
    /// Tears telemetry down in THIS process the instant the persisted flag flips to false.
    /// Program.cs calls <see cref="Initialize"/> before any command handler runs, so by the time
    /// `kcap config set telemetry off` executes, telemetry has already resolved enabled (no file
    /// on a fresh machine), minted a device id, and possibly queued <c>cli_first_run</c> — the
    /// persisted flag alone would not stop THIS process's own ProcessExit flush from shipping it.
    /// Called from <c>ConfigCommand.TryApplyTelemetry</c> right after
    /// <see cref="TelemetryState.SetEnabled"/> persists the "off" (which already clears the
    /// on-disk id — this clears the in-memory copy and whatever was queued for it). Re-enabling
    /// later mints a fresh id via the normal <see cref="Initialize"/> path.
    /// </summary>
    public static void DiscardAndDisable() {
        try {
            TestSink?.Clear();
            _client   = null;
            _deviceId = null;
            Enabled   = false;
        } catch { }
    }

    static void NoticeAndFirstRun(ConfigRoot config) {
        if (TelemetryState.Read(config).NoticeShown) return;

        Console.Error.WriteLine(
            "kcap collects pseudonymous usage data — command and flag names only, never argument values,");
        Console.Error.WriteLine(
            "file paths, or transcript content. It can be associated with your workspace and its creator.");
        Console.Error.WriteLine(
            "Opt out: kcap config set telemetry off (or DO_NOT_TRACK=1).");
        Console.Error.WriteLine("https://capacitor.kurrent.io/privacy");

        TelemetryState.MarkNoticeShown(config);
        Capture("cli_first_run", new JsonObject());
    }

    /// <summary>
    /// Debug render with the join key redacted. <see cref="Capture"/> prints the whole merged
    /// properties object, so without this a <c>KCAP_TELEMETRY_DEBUG=1</c> run would emit the
    /// bridge key to stderr on every <c>cli_*</c> event — and an access log holding that key is a
    /// copy of the join.
    /// <para>A placeholder rather than omission: the useful debug signal is WHETHER the property
    /// is attached, which the placeholder shows. The value itself is only ever needed in PostHog,
    /// where it legitimately lives.</para>
    /// </summary>
    static string DebugRender(JsonObject properties) {
        if (properties[SetupJoin.PropertyName] is null) return properties.ToJsonString();

        var redacted = properties.DeepClone().AsObject();
        redacted[SetupJoin.PropertyName] = "[set]";

        return redacted.ToJsonString();
    }

    static TelemetrySpool Spool(ConfigRoot config) => new(config.Path("telemetry-spool.jsonl"));

    static string Version() =>
        typeof(CliTelemetry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown";

    static string OS() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows"
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)   ? "macos"
        : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux"
        : "other";
}
