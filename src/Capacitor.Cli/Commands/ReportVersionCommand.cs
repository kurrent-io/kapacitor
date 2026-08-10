using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Hidden command spawned by the npm wrapper right after <c>kcap update</c> installs a new
/// binary: makes one authenticated GET against <see cref="WhoamiCommand.ProbePath"/> (read-only,
/// no side effects — unlike <see cref="SetupCommand"/>'s cli-setup POST) so the server's
/// endpoint-agnostic version-observer middleware sees the new <see cref="HttpClientExtensions.CliVersionHeader"/>
/// immediately. Fail-open: never throws, never prints, always returns 0, bounded to
/// <see cref="Budget"/> total (discovery + request).
/// </summary>
public static class ReportVersionCommand {
    static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    public static async Task<int> HandleAsync(string? baseUrl) {
        try {
            using var cts = new CancellationTokenSource(Budget);

            // One resolved URL for both auth and the request — CreateClientWithAuthStatusAsync's
            // own fallback also consults KCAP_URL, so recomputing it differently here would
            // authenticate against one host and probe another.
            var effectiveBaseUrl = baseUrl
                ?? AppConfig.ResolvedServerUrl
                ?? Environment.GetEnvironmentVariable("KCAP_URL")
                ?? "http://localhost:5108";

            var (client, status) =
                await HttpClientExtensions.CreateClientWithAuthStatusAsync(effectiveBaseUrl, cts.Token);

            using (client) {
                if (status is not (AuthStatus.Ok or AuthStatus.NoAuthRequired)) return 0;

                var url = AppConfig.NormalizeUrl(effectiveBaseUrl) + WhoamiCommand.ProbePath;

                using var _ = await client.GetOnceAsync(url, Budget, cts.Token);
            }
        } catch {
            // Fail-open — see class doc.
        }

        return 0;
    }
}
