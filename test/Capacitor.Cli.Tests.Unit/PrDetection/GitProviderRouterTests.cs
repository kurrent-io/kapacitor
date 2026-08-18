using Capacitor.Cli.PrDetection;

namespace Capacitor.Cli.Tests.Unit.PrDetection;

public class GitProviderRouterTests {
    [Before(Test)]
    public void Reset() => GitProviderRouter.ResetMemoForTests();

    static CommandRunner Never => (_, _, _, _) => throw new InvalidOperationException("probe should not run for SaaS hosts");

    [Test]
    public async Task Saas_hosts_route_without_probing() {
        await Assert.That(await GitProviderRouter.ResolveAsync("github.com", "/c", TimeSpan.FromSeconds(2), Never)).IsEqualTo(GitProviderKind.GitHub);
        await Assert.That(await GitProviderRouter.ResolveAsync("gitlab.com", "/c", TimeSpan.FromSeconds(2), Never)).IsEqualTo(GitProviderKind.GitLab);
    }

    [Test]
    public async Task Custom_host_in_gh_auth_status_is_github() {
        CommandRunner fake = async (cmd, args, _, _) => {
            await Assert.That(cmd).IsEqualTo("gh");
            await Assert.That(args).IsEqualTo("auth status --json hosts");
            return "{\"hosts\":{\"github.com\":[],\"ghe.corp.com\":[]}}";
        };
        await Assert.That(await GitProviderRouter.ResolveAsync("ghe.corp.com", "/c", TimeSpan.FromSeconds(2), fake)).IsEqualTo(GitProviderKind.GitHub);
    }

    [Test]
    public async Task Custom_host_not_in_gh_falls_back_to_gitlab() {
        CommandRunner fake = (_, _, _, _) => Task.FromResult<string?>("""{"hosts":{"github.com":[]}}""");
        await Assert.That(await GitProviderRouter.ResolveAsync("gitlab.corp.com", "/c", TimeSpan.FromSeconds(2), fake)).IsEqualTo(GitProviderKind.GitLab);
    }

    [Test]
    public async Task Probe_result_is_memoized_per_host() {
        var calls = 0;
        CommandRunner fake = (_, _, _, _) => { calls++; return Task.FromResult<string?>("""{"hosts":{"ghe.corp.com":[]}}"""); };
        await GitProviderRouter.ResolveAsync("ghe.corp.com", "/c", TimeSpan.FromSeconds(2), fake);
        await GitProviderRouter.ResolveAsync("ghe.corp.com", "/c", TimeSpan.FromSeconds(2), fake);
        await Assert.That(calls).IsEqualTo(1); // memoized: bulk-import loop can't multiply the probe
    }

    [Test]
    public async Task Null_host_is_unknown() {
        await Assert.That(await GitProviderRouter.ResolveAsync(null, "/c", TimeSpan.FromSeconds(2), Never)).IsEqualTo(GitProviderKind.Unknown);
    }

    [Test]
    public async Task Malformed_gh_json_falls_back_to_gitlab() {
        // `gh auth status --json hosts` returned junk → JsonNode.Parse throws → we can't confirm
        // the host is a GitHub host, so best-effort GitLab (unique host avoids memo collisions).
        CommandRunner fake = (_, _, _, _) => Task.FromResult<string?>("{not valid json");
        await Assert.That(await GitProviderRouter.ResolveAsync("malformed-json.example.com", "/c", TimeSpan.FromSeconds(2), fake)).IsEqualTo(GitProviderKind.GitLab);
    }

    [Test]
    public async Task Null_probe_result_falls_back_to_gitlab() {
        // Probe failed / timed out (runner returned null) → best-effort GitLab.
        CommandRunner fake = (_, _, _, _) => Task.FromResult<string?>(null);
        await Assert.That(await GitProviderRouter.ResolveAsync("null-probe.example.com", "/c", TimeSpan.FromSeconds(2), fake)).IsEqualTo(GitProviderKind.GitLab);
    }
}
