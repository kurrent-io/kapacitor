using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// Verifies that <see cref="ClaudeHookCommand"/> stamps the
/// <c>default_visibility</c> from the active V2 profile onto the
/// session-start payload — not from the legacy top-level config
/// shape, which would silently fall back to <c>org_public</c> on
/// every current (v2) config and ignore per-profile <c>private</c>
/// settings.
/// </summary>
public class SessionStartVisibilityTests : IDisposable {
    readonly WireMockServer _server = WireMockServer.Start();
    readonly string         _configPath = PathHelpers.ConfigPath("config.json");
    readonly string?        _previousConfig;

    public SessionStartVisibilityTests() {
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

    static string SessionStartPayloadWithoutTranscriptPath() =>
        """
        {
          "cwd":             "/tmp/test",
          "model":           "claude-sonnet-4-6",
          "source":          "startup",
          "hook_event_name": "session-start"
        }
        """;

    [Test, NotInParallel("AppConfig_FileState")]
    public async Task Stamps_private_visibility_from_active_profile_v2_config() {
        var config = new ProfileConfig {
            ActiveProfile = "work",
            Profiles = new() {
                ["work"] = new Profile {
                    ServerUrl         = _server.Url,
                    DefaultVisibility = "private"
                }
            }
        };
        await ConfigMutator.MutateAsync(_ => config);

        _server.Given(Request.Create().WithPath("/hooks/session-start").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        await ClaudeHookCommand.Handle(_server.Url!, new StringReader(SessionStartPayloadWithoutTranscriptPath()));

        var requests = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-start").UsingPost());
        await Assert.That(requests.Count).IsEqualTo(1);

        var body = JsonNode.Parse(requests[0].RequestMessage.Body!)!;
        await Assert.That(body["default_visibility"]?.GetValue<string>()).IsEqualTo("private");
    }

    [Test, NotInParallel("AppConfig_FileState")]
    public async Task Lowercases_mixedcase_visibility_from_v2_config() {
        var config = new ProfileConfig {
            ActiveProfile = "work",
            Profiles = new() {
                ["work"] = new Profile {
                    ServerUrl         = _server.Url,
                    DefaultVisibility = "Private"
                }
            }
        };
        await ConfigMutator.MutateAsync(_ => config);

        _server.Given(Request.Create().WithPath("/hooks/session-start").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        await ClaudeHookCommand.Handle(_server.Url!, new StringReader(SessionStartPayloadWithoutTranscriptPath()));

        var requests = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-start").UsingPost());
        await Assert.That(requests.Count).IsEqualTo(1);

        var body = JsonNode.Parse(requests[0].RequestMessage.Body!)!;
        await Assert.That(body["default_visibility"]?.GetValue<string>()).IsEqualTo("private");
    }

    [Test, NotInParallel("AppConfig_FileState")]
    public async Task Falls_back_to_org_public_when_v2_config_visibility_is_invalid() {
        var config = new ProfileConfig {
            ActiveProfile = "work",
            Profiles = new() {
                ["work"] = new Profile {
                    ServerUrl         = _server.Url,
                    DefaultVisibility = "totally-bogus"
                }
            }
        };
        await ConfigMutator.MutateAsync(_ => config);

        _server.Given(Request.Create().WithPath("/hooks/session-start").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        await ClaudeHookCommand.Handle(_server.Url!, new StringReader(SessionStartPayloadWithoutTranscriptPath()));

        var requests = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-start").UsingPost());
        await Assert.That(requests.Count).IsEqualTo(1);

        var body = JsonNode.Parse(requests[0].RequestMessage.Body!)!;
        await Assert.That(body["default_visibility"]?.GetValue<string>()).IsEqualTo("org_public");
    }

    [Test, NotInParallel("AppConfig_FileState")]
    public async Task Skips_session_start_when_repo_is_excluded_by_active_profile_v2_config() {
        var config = new ProfileConfig {
            ActiveProfile = "work",
            Profiles = new() {
                ["work"] = new Profile {
                    ServerUrl     = _server.Url,
                    ExcludedRepos = ["acme/secret-repo"]
                }
            }
        };
        await ConfigMutator.MutateAsync(_ => config);

        _server.Given(Request.Create().WithPath("/hooks/session-start").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        const string payload =
            """
            {
              "cwd":             "/tmp/test",
              "model":           "claude-sonnet-4-6",
              "source":          "startup",
              "hook_event_name": "session-start",
              "repository":      { "owner": "acme", "repo_name": "secret-repo" }
            }
            """;

        await ClaudeHookCommand.Handle(_server.Url!, new StringReader(payload));

        var requests = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-start").UsingPost());
        await Assert.That(requests.Count).IsEqualTo(0);
    }
}
