using System.Text;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Hidden, tooling-internal command: <c>npm/kcap/bin/kcap.js</c>'s <c>runUpdate</c> spawns the
/// freshly-installed binary with this one argument right after <c>npm install</c> succeeds.
///
/// <para>The problem it closes: the server only learns a CLI's version from the
/// <see cref="HttpClientExtensions.CliVersionHeader"/> header, attached at
/// <see cref="HttpClientExtensions.CreateClientCoreAsync"/>'s single choke point to every
/// authenticated request. `kcap update` itself is handled entirely by the npm wrapper — the OLD
/// binary never makes another server call after installing the new one — so without this command
/// the server keeps believing the OLD version until whatever the user runs next happens to hit
/// the server, and the "CLI out of date" banner/notification linger in the meantime. Running the
/// NEW binary once, right here, closes that gap immediately.</para>
///
/// <para>Fail-open by construction: never throws, never prints on the happy path, always returns
/// 0. It reuses <see cref="SetupCommand"/>'s <c>/api/users/me/cli-setup</c> POST shape as an
/// explicit "report my version" call, but — unlike that best-effort ping — goes through
/// <see cref="HttpClientExtensions.CreateClientWithAuthStatusAsync"/> so the version header is
/// actually attached; a bare <see cref="HttpClient"/> would carry no header and defeat the whole
/// point. When the caller isn't authenticated (offline, never logged in, wrong server) it makes
/// no request at all — there is nothing useful to observe from an anonymous or foreign token, and
/// attempting one would just be a doomed round-trip on every update.</para>
/// </summary>
public static class ReportVersionCommand {
    static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    public static async Task<int> HandleAsync(string? baseUrl) {
        try {
            var (client, status) = await HttpClientExtensions.CreateClientWithAuthStatusAsync(baseUrl);

            using (client) {
                if (status != AuthStatus.Ok) return 0;

                var version = CapacitorVersion.CurrentDisplay();
                var payload = new JsonObject { ["cliVersion"] = version };

                using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");

                var url = AppConfig.NormalizeUrl(baseUrl ?? AppConfig.ResolvedServerUrl ?? "http://localhost:5108")
                        + "/api/users/me/cli-setup";

                using var _ = await client.PostOnceAsync(url, content, RequestTimeout);
            }
        } catch {
            // Fail-open: this command's sole purpose is a best-effort side-effect that the
            // server will otherwise learn on the next incidental request anyway. Nothing here
            // may ever surface as a non-zero exit or console output.
        }

        return 0;
    }
}
