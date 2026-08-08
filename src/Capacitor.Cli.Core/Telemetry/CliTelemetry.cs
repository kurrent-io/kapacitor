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

    /// <summary>Test seam: when set, events are collected here instead of being queued.</summary>
    public static List<TelemetryEvent>? TestSink { get; set; }

    public static bool Enabled { get; private set; }

    public static void Reset() {
        _client = null; _deviceId = null; _orgGroup = null;
        _shared = new JsonObject(); Enabled = false; TestSink = null;
    }

    public static void Initialize(string command, string? serverUrl, bool loggedIn) {
        try {
            Enabled = TelemetrySettings.Resolve(TelemetryState.PersistedEnabled()).Enabled
                   && CommandEvents.IsReportable(command);
            if (!Enabled) return;

            _debug    = Environment.GetEnvironmentVariable("KCAP_TELEMETRY_DEBUG") == "1";
            _deviceId = TelemetryState.GetOrCreateDeviceId();
            if (_deviceId is null) { Enabled = false; return; }

            _orgGroup = PostHogPayload.OrgGroup(serverUrl);
            _shared   = new JsonObject {
                ["source"]      = "cli",
                ["cli_version"] = Version(),
                ["os"]          = OS(),
                ["arch"]        = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
                ["is_ci"]       = IsCi(),
                ["is_headless"] = Auth.HeadlessEnvironment.IsHeadless(),
                ["has_server"]  = serverUrl is not null,
                ["logged_in"]   = loggedIn,
            };

            if (TestSink is null)
                _client = new TelemetryClient(new HttpClientHandler(), Spool(), Token, Endpoint);

            NoticeAndFirstRun();
        } catch {
            Enabled = false;
        }
    }

    /// <summary>Queue an event for the exit flush.</summary>
    public static void Capture(string name, JsonObject properties) {
        try {
            if (!Enabled) return;

            foreach (var (key, value) in _shared)
                properties[key] ??= value?.DeepClone();

            var e = new TelemetryEvent(name, properties, DateTimeOffset.UtcNow);

            if (_debug) Console.Error.WriteLine($"[telemetry] {name} {properties.ToJsonString()}");

            if (TestSink is not null) TestSink.Add(e);
            else                      _client?.Enqueue(e);
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
                ["command"]     = command,
                ["exit_code"]   = exitCode,
                ["duration_ms"] = durationMs,
            };

            if (CommandEvents.Subcommand(command, args) is { } sub) props["subcommand"] = sub;

            var flags = CommandEvents.Flags(args);
            if (flags.Length > 0) {
                var arr = new JsonArray();
                foreach (var f in flags) arr.Add(f);   // never a collection expression — AOT
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

    static void NoticeAndFirstRun() {
        if (TelemetryState.Read().NoticeShown) return;

        Console.Error.WriteLine(
            "kcap collects anonymous usage data — command names only, never arguments, file paths, or");
        Console.Error.WriteLine(
            "transcript content. Opt out: kcap config set telemetry off (or DO_NOT_TRACK=1).");
        Console.Error.WriteLine("https://capacitor.kurrent.io/privacy");

        TelemetryState.MarkNoticeShown();
        Capture("cli_first_run", new JsonObject());
    }

    static TelemetrySpool Spool() => new(PathHelpers.ConfigPath("telemetry-spool.jsonl"));

    static string Version() =>
        typeof(CliTelemetry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown";

    static string OS() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows"
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)   ? "macos"
        : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux"
        : "other";

    // CI machines are ephemeral and mint a fresh device id per run, so they are tagged rather
    // than dropped — funnel insights filter is_ci = false.
    static bool IsCi() =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI"))
     || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"));
}
