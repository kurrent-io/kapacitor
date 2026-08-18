using System.Text.Json.Nodes;
using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// #579 — a live Cursor <c>sessionStart</c> hook must stamp the active profile's
/// <c>default_visibility</c> onto the payload, the same way the Codex hook does. Without it,
/// Cursor sessions in org repos silently default to org-visible (the server treats a null
/// <c>default_visibility</c> as the org-visibility fallback), ignoring a private-default
/// user's preference. The stamp is applied BEFORE the git-enrichment round-trip, so these
/// tests set <c>workspace_roots</c> to a real dir to force that reparse and prove the field
/// survives it.
/// </summary>
public class CursorSessionStartVisibilityTests : IDisposable {
    readonly WireMockServer _server     = WireMockServer.Start();
    readonly string         _configPath = PathHelpers.ConfigPath("config.json");
    readonly string?        _previousConfig;

    public CursorSessionStartVisibilityTests() {
        _previousConfig = File.Exists(_configPath) ? File.ReadAllText(_configPath) : null;
    }

    public void Dispose() {
        _server.Stop();

        if (_previousConfig is null) {
            if (File.Exists(_configPath)) File.Delete(_configPath);
        } else {
            File.WriteAllText(_configPath, _previousConfig);
        }
    }

    async Task<JsonNode> RunSessionStartAndCaptureBodyAsync(string defaultVisibility) {
        var config = new ProfileConfig {
            ActiveProfile = "work",
            Profiles = new() {
                ["work"] = new Profile {
                    ServerUrl         = _server.Url,
                    DefaultVisibility = defaultVisibility
                }
            }
        };
        await ConfigMutator.MutateAsync(_ => config);

        _server.Given(Request.Create().WithPath("/hooks/session-start/cursor").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));
        // Best-effort side calls (memory index, auth) must never fail the test.
        _server.Given(Request.Create().WithPath("/api/*").UsingAnyMethod())
            .RespondWith(Response.Create().WithStatusCode(404));
        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"provider":"None"}"""));

        // A real (non-git) temp dir under the OS temp root — outside any git repo, so no repository
        // block is added, but present enough to make the sessionStart enrichment branch actually
        // run (EnrichWithRepositoryInfoFromCwd reparses the payload), which is the serialization
        // round-trip the stamped field must survive. transcript_path is omitted → no watcher spawn.
        using var tmp = new TempDir();
        var body =
            $$"""
            {
              "hook_event_name": "sessionStart",
              "session_id":      "cursorvistestsession",
              "model":           "claude-3.5-sonnet",
              "workspace_roots": ["{{tmp.Path.Replace("\\", "\\\\")}}"]
            }
            """;

        using var client = new HttpClient();
        var spool = new HookSpool(tmp.CreateDir("spool").Path);

        var exit = await CursorHookCommand.HandleCore(client, _server.Url!, new StringReader(body), spool, TimeSpan.FromSeconds(2));
        await Assert.That(exit).IsEqualTo(0);

        var requests = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-start/cursor").UsingPost());
        await Assert.That(requests.Count).IsEqualTo(1);

        return JsonNode.Parse(requests[0].RequestMessage.Body!)!;
    }

    [Test, NotInParallel("AppConfig_FileState")]
    public async Task SessionStart_stamps_default_visibility_from_active_profile() {
        var body = await RunSessionStartAndCaptureBodyAsync("private");
        await Assert.That(body["default_visibility"]?.GetValue<string>()).IsEqualTo("private");
    }

    [Test, NotInParallel("AppConfig_FileState")]
    public async Task SessionStart_stamps_the_profiles_configured_visibility_value() {
        // Fidelity: the stamped value is the profile's configured visibility, not a hardcoded
        // constant — a different profile value must round-trip verbatim.
        var body = await RunSessionStartAndCaptureBodyAsync("public");
        await Assert.That(body["default_visibility"]?.GetValue<string>()).IsEqualTo("public");
    }
}
