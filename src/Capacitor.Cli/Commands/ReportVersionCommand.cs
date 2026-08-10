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
/// <para>The server's version observer (<c>CliVersionObserverMiddleware</c>) is
/// ENDPOINT-AGNOSTIC — it reads the header off any authenticated request, so the request itself
/// only needs to (a) require auth, so <c>GetUserId()</c> resolves, and (b) have no side effects,
/// since this command runs on every update whether or not the user ever intended to trigger
/// anything. That rules out <see cref="SetupCommand"/>'s <c>/api/users/me/cli-setup</c> POST — its
/// handler fires a one-time "setup completed" event the first time a user's <c>CliSetupAt</c> is
/// null, which would falsely mark onboarding complete for a user who logged in but never ran
/// `kcap setup`. Instead this reuses <see cref="WhoamiCommand.ProbePath"/>, the same read-only
/// identity GET `kcap whoami` uses to ask "do you accept this token?" — authenticated, and
/// provably free of writes.</para>
///
/// <para>Fail-open by construction: never throws, never prints on the happy path, always returns
/// 0. Goes through <see cref="HttpClientExtensions.CreateClientWithAuthStatusAsync"/> so the
/// version header is actually attached; a bare <see cref="HttpClient"/> would carry no header and
/// defeat the whole point. Proceeds on <see cref="AuthStatus.Ok"/> (a real bearer token) and on
/// <see cref="AuthStatus.NoAuthRequired"/> (an <c>Auth:Provider=None</c> tenant, where the request
/// still authenticates via a synthetic principal and the middleware still observes it) — anything
/// else (offline, never logged in, expired, wrong server) makes no request at all, since there is
/// nothing useful to observe from an anonymous or foreign token and attempting one would just be a
/// doomed round-trip on every update.</para>
/// </summary>
public static class ReportVersionCommand {
    static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    public static async Task<int> HandleAsync(string? baseUrl) {
        try {
            var (client, status) = await HttpClientExtensions.CreateClientWithAuthStatusAsync(baseUrl);

            using (client) {
                if (status is not (AuthStatus.Ok or AuthStatus.NoAuthRequired)) return 0;

                var url = AppConfig.NormalizeUrl(baseUrl ?? AppConfig.ResolvedServerUrl ?? "http://localhost:5108")
                        + WhoamiCommand.ProbePath;

                using var _ = await client.GetOnceAsync(url, RequestTimeout);
            }
        } catch {
            // Fail-open: this command's sole purpose is a best-effort side-effect that the
            // server will otherwise learn on the next incidental request anyway. Nothing here
            // may ever surface as a non-zero exit or console output.
        }

        return 0;
    }
}
