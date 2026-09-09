# Local Pull-Request Context Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The desktop reads a linked pull request through the user's own `gh` when it is installed and signed in, behind a provider registry that keeps the server route as the fallback.

**Architecture:** A `PullRequestReaderRegistry` in Cli.Core implements the existing `IPullRequestSource` and routes each read to the first ready `IPullRequestReaderProvider` that serves the PR's provider kind and host. Two providers ship: `GitHubCliReaderProvider` (a `gh` runner plus JSON mapping onto the existing wire records) and `ServerReaderProvider` (an adapter over the untouched `ServerPullRequestSource`). The view model keeps its shape; two GitHub-only gates move behind the registry and the PR card gains a provider-generic prerequisite note.

**Tech Stack:** .NET 10 NativeAOT, TUnit 1.65, `IProcessRunner`/`BinaryProbe`/`LoginShellProbe` from Cli.Core, Avalonia + ReactiveUI in the desktop app, `gh` 2.x JSON output.

**Spec:** `docs/superpowers/specs/2026-09-08-local-pr-context-design.md`

**Issue:** GitHub kurrent-io/kcap-cli#813. Commit subjects end with `(#813)`.

## Global Constraints

- NativeAOT: no reflection-based JSON. Parse `gh` output with `JsonDocument` (`MaxDepth = 64`) and `JsonElementExtensions` predicates (`IsObject`, `IsArray`, `IsString`, `IsNumber`, `IsNull`, `Prop(...)`), never `ValueKind` comparisons. Run `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` after Core changes and expect no output.
- `gh` is spawned through `IProcessRunner.RunAsync` with an argument array, never a shell. Deadline 20 seconds. Output over 4 MiB is rejected. Environment overlay: `GH_PROMPT_DISABLED=1`, `GH_NO_UPDATE_NOTIFIER=1`, `NO_COLOR=1`, `GH_PAGER=cat`, `CLICOLOR=0`. Never set `GH_TOKEN`, `GH_HOST` or `GH_CONFIG_DIR`.
- Every identifier is validated before a spawn; a failure returns without spawning.
- Every successful local read reports `AccessValidForSeconds: 30` and `PollAfterSeconds: 30`.
- Bodies and diff hunks over 262,144 characters are cut to that length with the truncated flag set.
- Snapshot ids and cursors are 64 lowercase hex characters (`PullRequestWire.ValidHandle`). Pages hold at most 50 items. A page with `HasMore` must have at least one item.
- The reader note never starts with `Sign in` or `Link GitHub`: those prefixes bind the Capacitor sign-in buttons on the card.
- Comments: none that narrate history, tickets, or reviews. One line only where a trap or non-obvious constraint exists.
- One type per file, named after the type. Namespace follows directory.
- Test conventions from `CLAUDE.md`: `TempDir` from Helpers, `[NotInParallel("AvaloniaSession")]` on view-model tests, no `HasCount()` (use `Assert.That(x.Count).IsEqualTo(n)`).
- Branch: create `feat/local-pr-reader` from `main` in this worktree before Task 1. Use `/usr/bin/git -C <worktree>` for every git command.

## File Structure

Cli.Core, namespace `Capacitor.Cli.Core.PullRequests.Readers`:

| File | Responsibility |
| --- | --- |
| `src/Capacitor.Cli.Core/PullRequests/Readers/PullRequestReaderStatusKind.cs` | `Ready`, `ToolMissing`, `SignedOut`, `Failed` |
| `.../Readers/PullRequestReaderStatus.cs` | Probe result record |
| `.../Readers/PullRequestReaderTool.cs` | Tool name, install URL, sign-in command builder |
| `.../Readers/PullRequestRepository.cs` | Provider kind, host, owner, name, hash of a session repository |
| `.../Readers/PullRequestReaderNote.cs` | Text plus optional install URL for the card |
| `.../Readers/IPullRequestReaderProvider.cs` | The provider contract |
| `.../Readers/IPullRequestReaders.cs` | What the view model asks the registry beyond `IPullRequestSource` |
| `.../Readers/PullRequestReaderRegistry.cs` | Routing, session links, live discovery merge, notes |
| `.../Readers/GitHubCli/GitHubCliRunner.cs` | Locate and spawn `gh`; identifier validation |
| `.../Readers/GitHubCli/GitHubCliOutcome.cs` | `Ok`, `Failed`, `TimedOut`, `Oversized`, `NotStarted` |
| `.../Readers/GitHubCli/GitHubCliResult.cs` | Outcome plus exit code, stdout, stderr |
| `.../Readers/GitHubCli/GitHubCliReaderProvider.cs` | Probe, serves, links, discover, reads, coalescing |
| `.../Readers/GitHubCli/GitHubCliMapping.cs` | Static `gh` JSON to wire-record mapping |
| `.../Readers/GitHubCli/GitHubCliCursors.cs` | Bounded handle store for paged sections |

Desktop app:

| File | Responsibility |
| --- | --- |
| `src/Capacitor.App/Services/ServerReaderProvider.cs` | Adapter over `ServerPullRequestSource` |
| `src/Capacitor.App/App.axaml.cs` | Build the registry, hand it to workspaces |
| `src/Capacitor.App/ViewModels/PullRequestContextViewModel*.cs` | Repository hint, session description, gates via registry, reader note |
| `src/Capacitor.App/ViewModels/WorkContextViewModel*.cs` | `PrimaryRepository` replaces `PrimaryRepositoryHash` |
| `src/Capacitor.App/ViewModels/WorkspaceViewModel.cs` | Pass the new hint |
| `src/Capacitor.App/Views/PullRequestCard.axaml` | Note text, Install and Recheck buttons |

Tests: `test/Capacitor.Cli.Core.Tests.Unit/PullRequests/Readers/*Tests.cs`, fixtures under `test/fixtures/gh/`, `test/Capacitor.App.Tests.Unit/*Tests.cs`.

---

### Task 1: Reader contract and registry

**Files:**
- Create: the seven `Readers/` contract files and `PullRequestReaderRegistry.cs` listed above
- Test: `test/Capacitor.Cli.Core.Tests.Unit/PullRequests/Readers/PullRequestReaderRegistryTests.cs`

**Interfaces:**
- Consumes: `IPullRequestSource`, `PullRequestRead<T>`, `PullRequestCapability`, `PullRequestLinkDto`, `PullRequestSubjectDto`, `PullRequestWire`, `RepoHashHelper.ComputeRepoHash(owner, name)`.
- Produces: everything below. Later tasks implement `IPullRequestReaderProvider` and call `IPullRequestReaders` from the view model.

- [ ] **Step 1: Create the branch**

```bash
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/lazy-wandering-blum checkout -b feat/local-pr-reader
```

- [ ] **Step 2: Write the contract types**

`PullRequestReaderStatusKind.cs`:

```csharp
namespace Capacitor.Cli.Core.PullRequests.Readers;

public enum PullRequestReaderStatusKind { Ready, ToolMissing, SignedOut, Failed }
```

`PullRequestReaderStatus.cs`:

```csharp
namespace Capacitor.Cli.Core.PullRequests.Readers;

public sealed record PullRequestReaderStatus(PullRequestReaderStatusKind Kind, string? Reason = null) {
    public bool IsReady => Kind == PullRequestReaderStatusKind.Ready;
}
```

`PullRequestReaderTool.cs`:

```csharp
namespace Capacitor.Cli.Core.PullRequests.Readers;

/// <summary>What the card needs to tell a user how to get a CLI provider working.</summary>
public sealed record PullRequestReaderTool(string Name, string InstallUrl, Func<string?, string> SignInCommand);
```

`PullRequestRepository.cs`:

```csharp
namespace Capacitor.Cli.Core.PullRequests.Readers;

public sealed record PullRequestRepository(string Provider, string Host, string Owner, string RepoName, string RepoHash);
```

`PullRequestReaderNote.cs`:

```csharp
namespace Capacitor.Cli.Core.PullRequests.Readers;

public sealed record PullRequestReaderNote(string Text, string? InstallUrl, string ToolName);
```

`IPullRequestReaderProvider.cs`:

```csharp
namespace Capacitor.Cli.Core.PullRequests.Readers;

public interface IPullRequestReaderProvider {
    string Name { get; }
    /// <summary>The subject provider kind this reader handles, e.g. <c>github</c>.</summary>
    string ProviderKind { get; }
    PullRequestReaderTool? Tool { get; }
    Task<PullRequestReaderStatus> ProbeAsync(bool refresh, CancellationToken ct);
    /// <summary>Decided from the last probe and the host alone, never from a network call.</summary>
    bool Serves(string provider, string host);
    PullRequestSubjectDto? ParseLink(string? url);
    string? PrLink(string? url, PullRequestSubjectDto subject);
    Task<IReadOnlyList<PullRequestLinkDto>> DiscoverAsync(PullRequestRepository repository, string branch, CancellationToken ct);
    Task<PullRequestRead<PullRequestOverviewDto>> OverviewAsync(string sessionId, PullRequestSubjectDto subject, CancellationToken ct);
    Task<PullRequestRead<PullRequestPageDto<T>>> PageAsync<T>(string sessionId, PullRequestSubjectDto subject, string section,
        string? cursor, string? resolved, string? threadId, CancellationToken ct) where T : class;
    void ResetSession(string sessionId);
}
```

`IPullRequestReaders.cs`:

```csharp
namespace Capacitor.Cli.Core.PullRequests.Readers;

/// <summary>The registry's surface beyond <see cref="IPullRequestSource"/>: the view model reaches it through an <c>as</c> cast so the server-only source stays untouched.</summary>
public interface IPullRequestReaders {
    void DescribeSession(string sessionId, PullRequestRepository? repository, string? branch);
    PullRequestReaderNote? NoteFor(string provider, string host);
    string? PrLink(string? url, PullRequestSubjectDto subject);
}
```

- [ ] **Step 3: Write the failing registry tests**

```csharp
using Capacitor.Cli.Core.PullRequests;
using Capacitor.Cli.Core.PullRequests.Readers;

namespace Capacitor.Cli.Core.Tests.Unit.PullRequests.Readers;

public class PullRequestReaderRegistryTests {
    static PullRequestSubjectDto Subject(string host = "github.com", string provider = "github", int number = 1) => new() {
        Provider = provider, Host = host, RepoHash = "hash", Owner = "example", RepoName = "repo", Number = number };
    static PullRequestLinkDto Link(string host, int number, string provider = "github", string? url = null) => new() {
        Provider = provider, Host = host, RepoHash = "hash", Owner = "example", RepoName = "repo", Number = number,
        Url = url ?? $"https://{host}/example/repo/pull/{number}", HeadRef = "feature" };

    [Test]
    public async Task Reads_route_to_the_first_ready_provider_that_serves_the_host() {
        var first = new StubProvider("first", ready: true, hosts: ["ghe.example"]);
        var second = new StubProvider("second", ready: true, hosts: ["github.com", "ghe.example"]);
        var registry = new PullRequestReaderRegistry(new StubLinks(), [first, second]);
        await registry.DiscoverAsync(false, default);
        await registry.OverviewAsync("session", Subject("ghe.example"), default);
        await registry.OverviewAsync("session", Subject(), default);
        await Assert.That(first.Overviews).IsEqualTo(1);
        await Assert.That(second.Overviews).IsEqualTo(1);
    }

    [Test]
    public async Task A_subject_no_provider_serves_reads_as_unavailable_with_no_reader() {
        var registry = new PullRequestReaderRegistry(new StubLinks(), [new StubProvider("gh", ready: true, hosts: ["github.com"])]);
        await registry.DiscoverAsync(false, default);
        var read = await registry.OverviewAsync("session", Subject("gitlab.com", "gitlab"), default);
        await Assert.That(read.Kind).IsEqualTo(PullRequestReadKind.Unavailable);
        await Assert.That(read.Reason).IsEqualTo("no_reader");
        await Assert.That(read.AccessFailure).IsEqualTo("invalid");
    }

    [Test]
    public async Task Capability_is_supported_when_any_provider_is_ready_else_the_session_link_capability() {
        var links = new StubLinks { Capability = new(PullRequestCapabilityKind.Legacy) };
        var provider = new StubProvider("gh", ready: false, hosts: []);
        var registry = new PullRequestReaderRegistry(links, [provider]);
        await Assert.That((await registry.DiscoverAsync(false, default)).Kind).IsEqualTo(PullRequestCapabilityKind.Legacy);
        provider.Ready = true;
        await Assert.That((await registry.DiscoverAsync(true, default)).Kind).IsEqualTo(PullRequestCapabilityKind.Supported);
    }

    [Test]
    public async Task Legacy_links_are_parsed_into_subjects_by_the_provider_that_recognizes_them() {
        var links = new StubLinks { Capability = new(PullRequestCapabilityKind.Legacy),
            Legacy = [Link("github.com", 7, provider: "unknown"), Link("gitlab.com", 8, provider: "unknown", url: "https://gitlab.com/example/repo/-/merge_requests/8")] };
        var gh = new StubProvider("gh", ready: true, hosts: ["github.com"]);
        var lab = new StubProvider("lab", ready: true, hosts: ["gitlab.com"], kind: "gitlab", linkShape: "/-/merge_requests/");
        var registry = new PullRequestReaderRegistry(links, [gh, lab]);
        await registry.DiscoverAsync(false, default);
        var list = await registry.ListAsync("session", default);
        await Assert.That(list.Kind).IsEqualTo(PullRequestReadKind.Ready);
        await Assert.That(list.Data!.Items.Select(item => item.Provider).ToArray()).IsEquivalentTo(new[] { "github", "gitlab" });
        await Assert.That(list.Data.Items[1].Number).IsEqualTo(8);
        await Assert.That(registry.PrLink("https://gitlab.com/example/repo/-/merge_requests/8", PullRequestWire.Subject(list.Data.Items[1]))).IsNotNull();
    }

    [Test]
    public async Task Live_discovery_merges_with_session_links_deduplicated_and_canonically_ordered() {
        var links = new StubLinks { Links = [Link("github.com", 5)] };
        var gh = new StubProvider("gh", ready: true, hosts: ["github.com"]) { Discovered = [Link("github.com", 5), Link("github.com", 2)] };
        var registry = new PullRequestReaderRegistry(links, [gh]);
        await registry.DiscoverAsync(false, default);
        registry.DescribeSession("session", new("github", "github.com", "example", "repo", "hash"), "feature");
        var list = await registry.ListAsync("session", default);
        await Assert.That(list.Data!.Items.Select(item => item.Number).ToArray()).IsEquivalentTo(new[] { 2, 5 });
        await Assert.That(gh.DiscoverCalls).IsEqualTo(1);
        registry.ResetSession("session");
        await registry.ListAsync("session", default);
        await Assert.That(gh.DiscoverCalls).IsEqualTo(1);
    }

    [Test]
    public async Task A_provider_change_on_rediscovery_restarts_the_next_read_once() {
        var provider = new StubProvider("gh", ready: false, hosts: ["github.com"]);
        var server = new StubProvider("server", ready: true, hosts: ["github.com"]);
        var registry = new PullRequestReaderRegistry(new StubLinks(), [provider, server]);
        await registry.DiscoverAsync(false, default);
        await registry.OverviewAsync("session", Subject(), default);
        provider.Ready = true;
        await registry.DiscoverAsync(true, default);
        var restart = await registry.OverviewAsync("session", Subject(), default);
        await Assert.That(restart.Kind).IsEqualTo(PullRequestReadKind.Restart);
        await Assert.That(restart.Reason).IsEqualTo("integration_changed");
        await Assert.That((await registry.OverviewAsync("session", Subject(), default)).Kind).IsEqualTo(PullRequestReadKind.Ready);
        await Assert.That(provider.Overviews).IsEqualTo(1);
    }

    [Test]
    public async Task Notes_describe_the_missing_or_signed_out_tool_for_a_host_and_nothing_when_served() {
        var gh = new StubProvider("gh", ready: false, hosts: [], status: PullRequestReaderStatusKind.ToolMissing);
        var registry = new PullRequestReaderRegistry(new StubLinks(), [gh]);
        await registry.DiscoverAsync(false, default);
        await Assert.That(registry.NoteFor("github", "github.com")!.Text).IsEqualTo("Install GitHub CLI to read pull requests here.");
        await Assert.That(registry.NoteFor("github", "github.com")!.InstallUrl).IsEqualTo("https://cli.github.com");
        gh.Status = PullRequestReaderStatusKind.SignedOut;
        await registry.DiscoverAsync(true, default);
        await Assert.That(registry.NoteFor("github", "github.com")!.Text).IsEqualTo("GitHub CLI is not signed in. Run gh auth login to read pull requests here.");
        gh.Ready = true; gh.Hosts = ["github.com"];
        await registry.DiscoverAsync(true, default);
        await Assert.That(registry.NoteFor("github", "github.com")).IsNull();
        await Assert.That(registry.NoteFor("github", "ghe.example")!.Text).IsEqualTo("GitHub CLI is not signed in for ghe.example. Run gh auth login --hostname ghe.example to read it here.");
        await Assert.That(registry.NoteFor("gitlab", "gitlab.com")).IsNull();
    }

    internal sealed class StubLinks : IPullRequestSource {
        public PullRequestCapability Capability = new(PullRequestCapabilityKind.Supported, 1);
        public PullRequestLinkDto[] Links = [];
        public PullRequestLinkDto[] Legacy = [];
        public Task<PullRequestCapability> DiscoverAsync(bool refresh, CancellationToken ct) => Task.FromResult(Capability);
        public void ResetSession(string sessionId) { }
        public Task<PullRequestRead<PullRequestLinkListDto>> ListAsync(string sessionId, CancellationToken ct)
            => Task.FromResult(new PullRequestRead<PullRequestLinkListDto>(PullRequestReadKind.Ready, new() { Items = Links }, FetchedAt: DateTime.UtcNow));
        public Task<PullRequestRead<PullRequestLinkListDto>> LegacyLinksAsync(string sessionId, CancellationToken ct)
            => Task.FromResult(new PullRequestRead<PullRequestLinkListDto>(PullRequestReadKind.Ready, new() { Items = Legacy }, FetchedAt: DateTime.UtcNow));
        public Task<PullRequestRead<PullRequestOverviewDto>> OverviewAsync(string sessionId, PullRequestSubjectDto subject, CancellationToken ct) => throw new NotSupportedException();
        public Task<PullRequestRead<PullRequestPageDto<T>>> PageAsync<T>(string sessionId, PullRequestSubjectDto subject, string section, string? cursor, string? resolved, string? threadId, CancellationToken ct) where T : class => throw new NotSupportedException();
    }

    internal sealed class StubProvider(string name, bool ready, string[] hosts, string kind = "github", string linkShape = "/pull/",
            PullRequestReaderStatusKind status = PullRequestReaderStatusKind.SignedOut) : IPullRequestReaderProvider {
        public bool Ready = ready;
        public string[] Hosts = hosts;
        public PullRequestReaderStatusKind Status = status;
        public int Overviews, DiscoverCalls;
        public PullRequestLinkDto[] Discovered = [];
        public string Name => name;
        public string ProviderKind => kind;
        public PullRequestReaderTool? Tool => kind == "github"
            ? new("GitHub CLI", "https://cli.github.com", host => host is null ? "gh auth login" : "gh auth login --hostname " + host)
            : new("GitLab CLI", "https://gitlab.com/gitlab-org/cli", host => host is null ? "glab auth login" : "glab auth login --hostname " + host);
        public Task<PullRequestReaderStatus> ProbeAsync(bool refresh, CancellationToken ct)
            => Task.FromResult(new PullRequestReaderStatus(Ready ? PullRequestReaderStatusKind.Ready : Status));
        public bool Serves(string provider, string host) => Ready && provider == kind && Hosts.Contains(host);
        public PullRequestSubjectDto? ParseLink(string? url) {
            if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || !Hosts.Contains(uri.Host)) return null;
            var parts = uri.AbsolutePath.Split(linkShape, 2);
            if (parts.Length != 2 || !int.TryParse(parts[1].TrimEnd('/'), out var number)) return null;
            var repo = parts[0].Trim('/').Split('/');
            return new() { Provider = kind, Host = uri.Host, RepoHash = "hash", Owner = repo[0], RepoName = repo[1], Number = number };
        }
        public string? PrLink(string? url, PullRequestSubjectDto subject) => ParseLink(url) == subject ? url : null;
        public Task<IReadOnlyList<PullRequestLinkDto>> DiscoverAsync(PullRequestRepository repository, string branch, CancellationToken ct) {
            DiscoverCalls++;
            return Task.FromResult<IReadOnlyList<PullRequestLinkDto>>(Discovered);
        }
        public Task<PullRequestRead<PullRequestOverviewDto>> OverviewAsync(string sessionId, PullRequestSubjectDto subject, CancellationToken ct) {
            Overviews++;
            return Task.FromResult(new PullRequestRead<PullRequestOverviewDto>(PullRequestReadKind.Ready, new() { Title = name }, subject, DateTime.UtcNow, AccessValidForSeconds: 30));
        }
        public Task<PullRequestRead<PullRequestPageDto<T>>> PageAsync<T>(string sessionId, PullRequestSubjectDto subject, string section, string? cursor, string? resolved, string? threadId, CancellationToken ct) where T : class
            => throw new NotSupportedException();
        public void ResetSession(string sessionId) { }
    }
}
```

- [ ] **Step 4: Run the tests to verify they fail**

Run: `dotnet build test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj 2>&1 | grep -E 'error' | head`
Expected: compile errors naming `PullRequestReaderRegistry`.

- [ ] **Step 5: Write the registry**

`PullRequestReaderRegistry.cs`:

```csharp
namespace Capacitor.Cli.Core.PullRequests.Readers;

/// <summary>
/// Session links always come from <paramref name="sessionLinks"/>; reading routes to the first
/// ready provider serving the subject's kind and host. Nothing here names a provider.
/// </summary>
public sealed class PullRequestReaderRegistry(IPullRequestSource sessionLinks, IReadOnlyList<IPullRequestReaderProvider> providers, TimeProvider? time = null)
        : IPullRequestSource, IPullRequestReaders {
    readonly TimeProvider _time = time ?? TimeProvider.System;
    readonly Lock _lock = new();
    readonly Dictionary<string, (PullRequestRepository? Repository, string? Branch)> _sessions = new(StringComparer.Ordinal);
    PullRequestReaderStatus[] _statuses = [.. providers.Select(_ => new PullRequestReaderStatus(PullRequestReaderStatusKind.Failed, "not_probed"))];
    string _readyKey = "";
    bool _changed;

    public async Task<PullRequestCapability> DiscoverAsync(bool refresh, CancellationToken ct) {
        var probes = providers.Select(provider => provider.ProbeAsync(refresh, ct)).ToArray();
        var links = sessionLinks.DiscoverAsync(refresh, ct);
        var statuses = await Task.WhenAll(probes).ConfigureAwait(false);
        var capability = await links.ConfigureAwait(false);
        var key = string.Join(",", providers.Where((_, i) => statuses[i].IsReady).Select(provider => provider.Name));
        lock (_lock) {
            _statuses = statuses;
            if (key != _readyKey) { if (_readyKey.Length > 0) _changed = true; _readyKey = key; }
        }
        return statuses.Any(status => status.IsReady) ? new(PullRequestCapabilityKind.Supported, 1) : capability;
    }

    public void ResetSession(string sessionId) {
        lock (_lock) _sessions.Remove(sessionId);
        sessionLinks.ResetSession(sessionId);
        foreach (var provider in providers) provider.ResetSession(sessionId);
    }

    public void DescribeSession(string sessionId, PullRequestRepository? repository, string? branch) {
        lock (_lock) {
            if (_sessions.Count >= 1024 && !_sessions.ContainsKey(sessionId)) _sessions.Remove(_sessions.Keys.First());
            _sessions[sessionId] = (repository, branch);
        }
    }

    public async Task<PullRequestRead<PullRequestLinkListDto>> ListAsync(string sessionId, CancellationToken ct) {
        var capability = await sessionLinks.DiscoverAsync(false, ct).ConfigureAwait(false);
        var links = capability.Kind switch {
            PullRequestCapabilityKind.Supported => await sessionLinks.ListAsync(sessionId, ct).ConfigureAwait(false),
            PullRequestCapabilityKind.Legacy or PullRequestCapabilityKind.Unsupported => await sessionLinks.LegacyLinksAsync(sessionId, ct).ConfigureAwait(false),
            PullRequestCapabilityKind.SignedOut => new(PullRequestReadKind.SignedOut, AccessFailure: "invalid"),
            _ => new PullRequestRead<PullRequestLinkListDto>(PullRequestReadKind.Unavailable, Reason: capability.Reason ?? "discovery_unavailable", AccessFailure: "transient", RetryAt: capability.RetryAt)
        };
        if (links.Kind != PullRequestReadKind.Ready || links.Data is null) return links;
        var items = links.Data.Items.Select(Resolve).ToList();
        (PullRequestRepository? Repository, string? Branch) context;
        lock (_lock) context = _sessions.GetValueOrDefault(sessionId);
        if (context is { Repository: { } repository, Branch: { Length: > 0 } branch })
            foreach (var provider in Ready().Where(provider => provider.Serves(repository.Provider, repository.Host)))
                items.AddRange(await provider.DiscoverAsync(repository, branch, ct).ConfigureAwait(false));
        var merged = items.DistinctBy(item => (item.Provider, item.Host, item.Owner.ToLowerInvariant(), item.RepoName.ToLowerInvariant(), item.Number))
            .OrderBy(item => item.Owner.ToLowerInvariant(), StringComparer.Ordinal).ThenBy(item => item.RepoName.ToLowerInvariant(), StringComparer.Ordinal).ThenBy(item => item.Number).ToArray();
        return links with { Data = new() { Items = merged } };
    }

    public Task<PullRequestRead<PullRequestLinkListDto>> LegacyLinksAsync(string sessionId, CancellationToken ct) => sessionLinks.LegacyLinksAsync(sessionId, ct);

    public Task<PullRequestRead<PullRequestOverviewDto>> OverviewAsync(string sessionId, PullRequestSubjectDto subject, CancellationToken ct) {
        if (Route(subject) is not { } provider) return Task.FromResult(NoReader<PullRequestOverviewDto>(subject));
        if (TakeChange()) return Task.FromResult(new PullRequestRead<PullRequestOverviewDto>(PullRequestReadKind.Restart, Subject: subject, Reason: "integration_changed"));
        return provider.OverviewAsync(sessionId, subject, ct);
    }

    public Task<PullRequestRead<PullRequestPageDto<T>>> PageAsync<T>(string sessionId, PullRequestSubjectDto subject, string section,
            string? cursor, string? resolved, string? threadId, CancellationToken ct) where T : class {
        if (Route(subject) is not { } provider) return Task.FromResult(NoReader<PullRequestPageDto<T>>(subject));
        if (TakeChange()) return Task.FromResult(new PullRequestRead<PullRequestPageDto<T>>(PullRequestReadKind.Restart, Subject: subject, Reason: "integration_changed"));
        return provider.PageAsync<T>(sessionId, subject, section, cursor, resolved, threadId, ct);
    }

    public PullRequestReaderNote? NoteFor(string provider, string host) {
        if (Ready().Any(reader => reader.Serves(provider, host))) return null;
        PullRequestReaderStatus[] statuses;
        lock (_lock) statuses = _statuses;
        for (var i = 0; i < providers.Count; i++) {
            var reader = providers[i];
            if (reader.ProviderKind != provider || reader.Tool is not { } tool) continue;
            var text = statuses[i].Kind switch {
                PullRequestReaderStatusKind.ToolMissing => $"Install {tool.Name} to read pull requests here.",
                PullRequestReaderStatusKind.SignedOut => $"{tool.Name} is not signed in. Run {tool.SignInCommand(null)} to read pull requests here.",
                PullRequestReaderStatusKind.Ready => $"{tool.Name} is not signed in for {host}. Run {tool.SignInCommand(host)} to read it here.",
                _ => null
            };
            if (text is not null) return new(text, statuses[i].Kind == PullRequestReaderStatusKind.ToolMissing ? tool.InstallUrl : null, tool.Name);
        }
        return null;
    }

    public string? PrLink(string? url, PullRequestSubjectDto subject) =>
        providers.FirstOrDefault(provider => provider.ProviderKind == subject.Provider)?.PrLink(url, subject) ?? PullRequestWire.SafeLink(url) is { } safe
            && providers.All(provider => provider.ProviderKind != subject.Provider) ? PullRequestWire.SafeLink(url) : null;

    IEnumerable<IPullRequestReaderProvider> Ready() {
        PullRequestReaderStatus[] statuses;
        lock (_lock) statuses = _statuses;
        return providers.Where((_, i) => statuses[i].IsReady);
    }
    IPullRequestReaderProvider? Route(PullRequestSubjectDto subject) => Ready().FirstOrDefault(provider => provider.Serves(subject.Provider, subject.Host));
    bool TakeChange() { lock (_lock) { var changed = _changed; _changed = false; return changed; } }
    PullRequestLinkDto Resolve(PullRequestLinkDto link) {
        if (link.Provider != "unknown") return link;
        foreach (var provider in providers) {
            if (provider.ParseLink(link.Url) is not { } subject) continue;
            var hash = link.RepoHash == "legacy" ? RepoHashHelper.ComputeRepoHash(subject.Owner, subject.RepoName) : link.RepoHash;
            return link with { Provider = subject.Provider, Host = subject.Host, Owner = subject.Owner, RepoName = subject.RepoName, Number = subject.Number, RepoHash = hash };
        }
        return link;
    }
    static PullRequestRead<T> NoReader<T>(PullRequestSubjectDto subject) where T : class
        => new(PullRequestReadKind.Unavailable, Subject: subject, Reason: "no_reader", AccessFailure: "invalid");
}
```

The `PrLink` expression above is hard to read; write it as a method body instead:

```csharp
    public string? PrLink(string? url, PullRequestSubjectDto subject) {
        var owner = providers.FirstOrDefault(provider => provider.ProviderKind == subject.Provider);
        return owner is null ? PullRequestWire.SafeLink(url) : owner.PrLink(url, subject);
    }
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/PullRequestReaderRegistryTests/*"`
Expected: 7 passed.

- [ ] **Step 7: Commit**

```bash
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/lazy-wandering-blum add src/Capacitor.Cli.Core/PullRequests/Readers test/Capacitor.Cli.Core.Tests.Unit/PullRequests/Readers
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/lazy-wandering-blum commit -m "Route pull-request reads through a reader-provider registry (#813)"
```

### Task 2: GitHub CLI runner

**Files:**
- Create: `src/Capacitor.Cli.Core/PullRequests/Readers/GitHubCli/GitHubCliOutcome.cs`, `GitHubCliResult.cs`, `GitHubCliRunner.cs`
- Create test doubles: `test/Capacitor.Cli.Core.Tests.Unit/PullRequests/Readers/GitHubCli/FakeGhProcessRunner.cs`, `FakeLoginShellProbe.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/PullRequests/Readers/GitHubCli/GitHubCliRunnerTests.cs`

**Interfaces:**
- Consumes: `IProcessRunner`, `RunOptions`, `ProcessResult`, `CancelMode` (`Capacitor.Cli.Core`), `ILoginShellProbe`, `BinaryProbe` (`Capacitor.Cli.Core.Setup`).
- Produces: `GitHubCliRunner.LocateAsync(bool refresh, CancellationToken)`, `GitHubCliRunner.RunAsync(string[] args, CancellationToken)` returning `GitHubCliResult(Outcome, ExitCode, Stdout, Stderr)`, and the static validators `ValidHost`, `ValidOwner`, `ValidRepo`, `ValidNumber`, `ValidBranch`, `ValidNodeId`. Task 3 onward builds every `gh` call on these.

- [ ] **Step 1: Write the test doubles**

`FakeGhProcessRunner.cs`:

```csharp
namespace Capacitor.Cli.Core.Tests.Unit.PullRequests.Readers.GitHubCli;

/// <summary>Answers by argument prefix; an unmatched call fails with exit 1 so a test cannot pass on an unscripted spawn.</summary>
internal sealed class FakeGhProcessRunner : IProcessRunner {
    public readonly List<(string FileName, string[] Args, RunOptions Options)> Calls = [];
    readonly List<(string[] Prefix, Func<Task<ProcessResult>> Reply)> _replies = [];
    public Exception? StartFailure;

    public void When(string[] prefix, string stdout, int exitCode = 0, string stderr = "", bool timedOut = false)
        => _replies.Add((prefix, () => Task.FromResult(new ProcessResult(exitCode, stdout, stderr, timedOut))));
    public void WhenPending(string[] prefix, TaskCompletionSource<ProcessResult> source) => _replies.Add((prefix, () => source.Task));

    public Task<ProcessResult> RunAsync(string fileName, string[] args, RunOptions options, CancellationToken ct) {
        Calls.Add((fileName, args, options));
        if (StartFailure is not null) throw StartFailure;
        foreach (var (prefix, reply) in _replies)
            if (args.Length >= prefix.Length && prefix.SequenceEqual(args.Take(prefix.Length))) return reply();
        return Task.FromResult(new ProcessResult(1, "", "unscripted: " + string.Join(' ', args), false));
    }

    public Task<StreamingResult> RunStreamingAsync(string fileName, string[] args, RunOptions options, Action<StreamedLine> onLine, CancellationToken ct)
        => throw new NotSupportedException();
}
```

`FakeLoginShellProbe.cs`:

```csharp
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Core.Tests.Unit.PullRequests.Readers.GitHubCli;

internal sealed class FakeLoginShellProbe(string? terminalPath) : ILoginShellProbe {
    public int Probes;
    public Task<string?> TerminalPathAsync(CancellationToken ct) { Probes++; return Task.FromResult(terminalPath); }
    public Task<bool?> KcapOnPathAsync(CancellationToken ct, bool forceRefresh = false) => Task.FromResult<bool?>(null);
    public Task<string?> KcapPathAsync(CancellationToken ct, bool forceRefresh = false) => Task.FromResult<string?>(null);
}
```

- [ ] **Step 2: Write the failing runner tests**

```csharp
using Capacitor.Cli.Core.PullRequests.Readers.GitHubCli;

namespace Capacitor.Cli.Core.Tests.Unit.PullRequests.Readers.GitHubCli;

public class GitHubCliRunnerTests {
    [TempDir] public required TempDir Tmp { get; init; }

    static string Executable => OperatingSystem.IsWindows() ? "gh.exe" : "gh";

    string InstallGh(string directory) {
        string dir = Tmp.CreateDir(directory);
        var path = Path.Combine(dir, Executable);
        File.WriteAllText(path, "");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    [Test]
    public async Task Resolves_gh_from_the_terminal_path_before_the_process_path() {
        var terminal = InstallGh("terminal");
        var process = InstallGh("process");
        var shell = new FakeLoginShellProbe(Path.GetDirectoryName(terminal));
        var runner = new GitHubCliRunner(new FakeGhProcessRunner(), shell, name => name == "PATH" ? Path.GetDirectoryName(process) : null);
        var expected = OperatingSystem.IsWindows() ? process : terminal;
        await Assert.That(await runner.LocateAsync(false, default)).IsEqualTo(expected);
        await runner.LocateAsync(false, default);
        await Assert.That(shell.Probes).IsEqualTo(OperatingSystem.IsWindows() ? 0 : 1);
    }

    [Test]
    public async Task Falls_back_to_the_process_path_and_reports_null_when_nothing_has_gh() {
        var process = InstallGh("process");
        var runner = new GitHubCliRunner(new FakeGhProcessRunner(), new FakeLoginShellProbe(Tmp.CreateDir("empty").Path), name => name == "PATH" ? Path.GetDirectoryName(process) : null);
        await Assert.That(await runner.LocateAsync(false, default)).IsEqualTo(process);
        string nothing = Tmp.CreateDir("nothing");
        var missing = new GitHubCliRunner(new FakeGhProcessRunner(), new FakeLoginShellProbe(null), _ => nothing);
        await Assert.That(await missing.LocateAsync(false, default)).IsNull();
        var result = await missing.RunAsync(["auth", "status"], default);
        await Assert.That(result.Outcome).IsEqualTo(GitHubCliOutcome.NotStarted);
    }

    [Test]
    public async Task Runs_with_the_fixed_overlay_deadline_and_kill_mode() {
        var gh = InstallGh("bin");
        var process = new FakeGhProcessRunner();
        process.When(["auth", "status"], """{"hosts":{}}""");
        var runner = new GitHubCliRunner(process, null, name => name == "PATH" ? Path.GetDirectoryName(gh) : null);
        var result = await runner.RunAsync(["auth", "status", "--json", "hosts"], default);
        await Assert.That(result.Outcome).IsEqualTo(GitHubCliOutcome.Ok);
        await Assert.That(result.Stdout).IsEqualTo("""{"hosts":{}}""");
        var call = process.Calls.Single();
        await Assert.That(call.FileName).IsEqualTo(gh);
        await Assert.That(call.Options.Timeout).IsEqualTo(TimeSpan.FromSeconds(20));
        await Assert.That(call.Options.CancelMode).IsEqualTo(CancelMode.KillTree);
        var overlay = call.Options.EnvOverlay!;
        await Assert.That(overlay["GH_PROMPT_DISABLED"]).IsEqualTo("1");
        await Assert.That(overlay["GH_NO_UPDATE_NOTIFIER"]).IsEqualTo("1");
        await Assert.That(overlay["NO_COLOR"]).IsEqualTo("1");
        await Assert.That(overlay["GH_PAGER"]).IsEqualTo("cat");
        await Assert.That(overlay.ContainsKey("GH_TOKEN")).IsFalse();
        await Assert.That(overlay.ContainsKey("GH_HOST")).IsFalse();
    }

    [Test]
    public async Task Timeouts_failures_and_oversized_output_map_to_outcomes() {
        var gh = InstallGh("bin");
        var process = new FakeGhProcessRunner();
        process.When(["slow"], "", exitCode: -1, timedOut: true);
        process.When(["bad"], "", exitCode: 1, stderr: "GraphQL: Could not resolve to a PullRequest");
        process.When(["big"], new string('x', GitHubCliRunner.OutputLimit + 1));
        var runner = new GitHubCliRunner(process, null, name => name == "PATH" ? Path.GetDirectoryName(gh) : null);
        await Assert.That((await runner.RunAsync(["slow"], default)).Outcome).IsEqualTo(GitHubCliOutcome.TimedOut);
        var failed = await runner.RunAsync(["bad"], default);
        await Assert.That(failed.Outcome).IsEqualTo(GitHubCliOutcome.Failed);
        await Assert.That(failed.Stderr).Contains("Could not resolve");
        var big = await runner.RunAsync(["big"], default);
        await Assert.That(big.Outcome).IsEqualTo(GitHubCliOutcome.Oversized);
        await Assert.That(big.Stdout).IsEmpty();
    }

    [Test]
    public async Task A_start_failure_forgets_the_located_path_so_the_next_call_relocates() {
        var gh = InstallGh("bin");
        var process = new FakeGhProcessRunner { StartFailure = new InvalidOperationException("Failed to start") };
        var shell = new FakeLoginShellProbe(Path.GetDirectoryName(gh));
        var runner = new GitHubCliRunner(process, shell, name => name == "PATH" ? Path.GetDirectoryName(gh) : null);
        await Assert.That((await runner.RunAsync(["auth", "status"], default)).Outcome).IsEqualTo(GitHubCliOutcome.NotStarted);
        process.StartFailure = null;
        process.When(["auth", "status"], "{}");
        await Assert.That((await runner.RunAsync(["auth", "status"], default)).Outcome).IsEqualTo(GitHubCliOutcome.Ok);
        await Assert.That(shell.Probes).IsEqualTo(OperatingSystem.IsWindows() ? 0 : 2);
    }

    [Test]
    public async Task At_most_two_processes_run_at_once() {
        var gh = InstallGh("bin");
        var process = new FakeGhProcessRunner();
        var a = new TaskCompletionSource<ProcessResult>(); var b = new TaskCompletionSource<ProcessResult>();
        process.WhenPending(["a"], a); process.WhenPending(["b"], b); process.When(["c"], "");
        var runner = new GitHubCliRunner(process, null, name => name == "PATH" ? Path.GetDirectoryName(gh) : null);
        var first = runner.RunAsync(["a"], default); var second = runner.RunAsync(["b"], default); var third = runner.RunAsync(["c"], default);
        await Task.Delay(50);
        await Assert.That(process.Calls.Count).IsEqualTo(2);
        a.SetResult(new(0, "", "", false));
        await first;
        await third;
        await Assert.That(process.Calls.Count).IsEqualTo(3);
        b.SetResult(new(0, "", "", false));
        await second;
    }

    [Test]
    [Arguments("octocat", true)] [Arguments("-octo", false)] [Arguments("octo cat", false)] [Arguments("", false)]
    [Arguments("a-very-long-owner-name-that-exceeds-the-github-maximum", false)]
    public async Task Owner_validation(string owner, bool valid) => await Assert.That(GitHubCliRunner.ValidOwner(owner)).IsEqualTo(valid);

    [Test]
    [Arguments("kcap-cli", true)] [Arguments("re.po_x", true)] [Arguments("..", false)] [Arguments("a/b", false)] [Arguments("", false)]
    public async Task Repository_validation(string repo, bool valid) => await Assert.That(GitHubCliRunner.ValidRepo(repo)).IsEqualTo(valid);

    [Test]
    [Arguments("feature/x", true)] [Arguments("-bad", false)] [Arguments("has space", false)] [Arguments("a..b", false)] [Arguments("x.lock", false)]
    [Arguments("a\tb", false)] [Arguments("a~b", false)] [Arguments("/lead", false)]
    public async Task Branch_validation(string branch, bool valid) => await Assert.That(GitHubCliRunner.ValidBranch(branch)).IsEqualTo(valid);

    [Test]
    [Arguments("github.com", true)] [Arguments("ghe.example", true)] [Arguments("bad host", false)] [Arguments("", false)] [Arguments("a/b", false)]
    public async Task Host_validation(string host, bool valid) => await Assert.That(GitHubCliRunner.ValidHost(host)).IsEqualTo(valid);

    [Test]
    [Arguments("PRRT_kwDOR9HOJ86gJOag", true)] [Arguments("Y3Vyc29yOnYyOpK0MjAyNi0wOS0wOFQwNzo1MTozOVrOoCTmpA==", true)] [Arguments("", false)] [Arguments("a b", false)]
    public async Task Node_id_validation(string id, bool valid) => await Assert.That(GitHubCliRunner.ValidNodeId(id)).IsEqualTo(valid);
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet build test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj 2>&1 | grep -E 'error' | head`
Expected: compile errors naming `GitHubCliRunner`.

- [ ] **Step 4: Write the runner**

`GitHubCliOutcome.cs`:

```csharp
namespace Capacitor.Cli.Core.PullRequests.Readers.GitHubCli;

public enum GitHubCliOutcome { Ok, Failed, TimedOut, Oversized, NotStarted }
```

`GitHubCliResult.cs`:

```csharp
namespace Capacitor.Cli.Core.PullRequests.Readers.GitHubCli;

public sealed record GitHubCliResult(GitHubCliOutcome Outcome, int ExitCode, string Stdout, string Stderr);
```

`GitHubCliRunner.cs`:

```csharp
using System.ComponentModel;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Core.PullRequests.Readers.GitHubCli;

/// <summary>
/// Locates and spawns <c>gh</c>. A GUI app inherits launchd's PATH, which omits Homebrew and
/// user-local prefixes, so the login shell's PATH is searched first on macOS and Linux.
/// </summary>
public sealed class GitHubCliRunner(IProcessRunner runner, ILoginShellProbe? shell, Func<string, string?> getEnv) {
    public const int OutputLimit = 4 * 1024 * 1024;
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);
    static readonly IReadOnlyDictionary<string, string> Overlay = new Dictionary<string, string>(StringComparer.Ordinal) {
        ["GH_PROMPT_DISABLED"] = "1", ["GH_NO_UPDATE_NOTIFIER"] = "1", ["NO_COLOR"] = "1", ["GH_PAGER"] = "cat", ["CLICOLOR"] = "0",
    };
    readonly SemaphoreSlim _slots = new(2, 2);
    string? _path;

    public async Task<string?> LocateAsync(bool refresh, CancellationToken ct) {
        if (!refresh && _path is not null) return _path;
        string? found = null;
        if (shell is not null && !OperatingSystem.IsWindows() && await shell.TerminalPathAsync(ct).ConfigureAwait(false) is { } terminal)
            found = BinaryProbe.Searching(terminal).Resolve("gh");
        found ??= BinaryProbe.Searching(getEnv("PATH")).Resolve("gh");
        _path = found;
        return found;
    }

    public async Task<GitHubCliResult> RunAsync(string[] args, CancellationToken ct) {
        var path = _path ?? await LocateAsync(false, ct).ConfigureAwait(false);
        if (path is null) return new(GitHubCliOutcome.NotStarted, -1, "", "gh is not installed");
        await _slots.WaitAsync(ct).ConfigureAwait(false);
        try {
            ProcessResult result;
            try {
                result = await runner.RunAsync(path, args, new RunOptions(Overlay, Deadline, CancelMode.KillTree), ct).ConfigureAwait(false);
            } catch (Exception exception) when (exception is InvalidOperationException or IOException or Win32Exception) {
                _path = null;
                return new(GitHubCliOutcome.NotStarted, -1, "", exception.Message);
            }
            if (result.TimedOut) return new(GitHubCliOutcome.TimedOut, result.ExitCode, "", result.Stderr);
            if (result.Stdout.Length > OutputLimit) return new(GitHubCliOutcome.Oversized, result.ExitCode, "", "");
            return new(result.ExitCode == 0 ? GitHubCliOutcome.Ok : GitHubCliOutcome.Failed, result.ExitCode, result.Stdout, result.Stderr);
        } finally { _slots.Release(); }
    }

    public static bool ValidHost(string? host) => host is { Length: > 0 and <= 253 } && !host.Contains('/') && Uri.CheckHostName(host) == UriHostNameType.Dns;
    public static bool ValidOwner(string? owner) => owner is { Length: > 0 and <= 39 } && owner[0] != '-' && owner.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
    public static bool ValidRepo(string? repo) => repo is { Length: > 0 and <= 100 } && repo is not ("." or "..")
        && repo.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
    public static bool ValidNumber(int number) => number > 0;
    public static bool ValidBranch(string? branch) => branch is { Length: > 0 and <= 256 } && branch[0] is not ('-' or '/') && !branch.EndsWith('/')
        && !branch.EndsWith(".lock", StringComparison.Ordinal) && !branch.Contains("..", StringComparison.Ordinal) && !branch.Contains("@{", StringComparison.Ordinal)
        && !branch.Contains("//", StringComparison.Ordinal)
        && branch.All(c => !char.IsWhiteSpace(c) && !char.IsControl(c) && c is not ('~' or '^' or ':' or '?' or '*' or '[' or '\\'));
    public static bool ValidNodeId(string? id) => id is { Length: > 0 and <= 256 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '=' or '-');
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/GitHubCliRunnerTests/*"`
Expected: all passed. On macOS run with `TMPDIR=/private/tmp` if `TempDir` paths are refused.

- [ ] **Step 6: Commit**

```bash
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/lazy-wandering-blum add src/Capacitor.Cli.Core/PullRequests/Readers/GitHubCli test/Capacitor.Cli.Core.Tests.Unit/PullRequests/Readers/GitHubCli
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/lazy-wandering-blum commit -m "Add a validated, bounded runner for the GitHub CLI (#813)"
```

### Task 3: GitHub CLI provider: probe, hosts, links, live discovery

**Files:**
- Create: `src/Capacitor.Cli.Core/PullRequests/Readers/GitHubCli/GitHubCliReaderProvider.cs`, `GitHubCliMapping.cs`
- Create fixtures: `test/fixtures/gh/auth-status.json`, `test/fixtures/gh/pr-list.json`
- Modify: `test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj:2-4` (copy the `gh` fixtures)
- Create: `test/Capacitor.Cli.Core.Tests.Unit/PullRequests/Readers/GitHubCli/GhHarness.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/PullRequests/Readers/GitHubCli/GitHubCliReaderProviderTests.cs`

**Interfaces:**
- Consumes: Task 1 contract, Task 2 runner and validators, `RepoHashHelper.ComputeRepoHash(string owner, string repoName)`, `JsonElementExtensions` (`IsObject`, `IsArray`, `IsString`, `IsNumber`, `Prop(name)` returning `JsonElement?`).
- Produces: `GitHubCliReaderProvider(GitHubCliRunner cli, TimeProvider? time = null)` implementing `IPullRequestReaderProvider`; `GitHubCliMapping.SignedInHosts(string json)` and `GitHubCliMapping.Links(string json, PullRequestRepository repository)`; `GitHubCliReaderProvider.Repo(host, owner, name)` building `host/owner/name`. `OverviewAsync` and `PageAsync` return `Unavailable` with reason `unsupported` until Task 4.

- [ ] **Step 1: Add the fixtures and the copy rule**

`test/fixtures/gh/auth-status.json` (real `gh auth status --json hosts` shape):

```json
{"hosts":{"github.com":[{"state":"success","active":true,"host":"github.com","login":"octocat","tokenSource":"keyring","scopes":"repo, read:org","gitProtocol":"ssh"}],"ghe.example":[{"state":"error","active":true,"host":"ghe.example","login":"","tokenSource":"keyring","error":"token invalid"}]}}
```

`test/fixtures/gh/pr-list.json`:

```json
[{"headRefName":"feature","isDraft":false,"number":12,"state":"OPEN","title":"Add the thing","url":"https://github.com/example/repo/pull/12"},{"headRefName":"feature","isDraft":true,"number":9,"state":"CLOSED","title":"Earlier attempt","url":"https://github.com/example/repo/pull/9"},{"headRefName":"feature","isDraft":false,"number":"bad","state":"OPEN","title":"Malformed","url":"https://github.com/example/repo/pull/13"}]
```

In `Capacitor.Cli.Core.Tests.Unit.csproj`, inside the first `<ItemGroup>` after the existing `<None Include="../fixtures/pull-request-reads-v1.json" ... />` line add:

```xml
        <None Include="../fixtures/gh/*.json" Link="fixtures/gh/%(Filename)%(Extension)" CopyToOutputDirectory="PreserveNewest" />
```

- [ ] **Step 2: Write the harness**

`GhHarness.cs`:

```csharp
using Capacitor.Cli.Core.PullRequests.Readers.GitHubCli;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Core.Tests.Unit.PullRequests.Readers.GitHubCli;

internal sealed class GhHarness {
    public readonly FakeGhProcessRunner Process = new();
    public readonly FakeTimeProvider Time = new();
    public readonly GitHubCliReaderProvider Provider;
    public readonly string? GhPath;

    public GhHarness(TempDir tmp, bool installed = true) {
        string dir = tmp.CreateDir("bin");
        if (installed) {
            GhPath = tmp.CreateFile(["bin", OperatingSystem.IsWindows() ? "gh.exe" : "gh"]);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(GhPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var runner = new GitHubCliRunner(Process, null, name => name == "PATH" ? dir : null);
        Provider = new GitHubCliReaderProvider(runner, Time);
    }

    public static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "gh", name));

    public void SignedIn(params string[] hosts) {
        var entries = hosts.Select(host => $"\"{host}\":[{{\"state\":\"success\",\"active\":true,\"host\":\"{host}\",\"login\":\"octocat\"}}]");
        Process.When(["auth", "status"], "{\"hosts\":{" + string.Join(',', entries) + "}}");
    }

    public string[] LastArgs => Process.Calls[^1].Args;
}
```

- [ ] **Step 3: Write the failing provider tests**

```csharp
using Capacitor.Cli.Core.PullRequests;
using Capacitor.Cli.Core.PullRequests.Readers;
using Capacitor.Cli.Core.PullRequests.Readers.GitHubCli;

namespace Capacitor.Cli.Core.Tests.Unit.PullRequests.Readers.GitHubCli;

public class GitHubCliReaderProviderTests {
    [TempDir] public required TempDir Tmp { get; init; }

    static PullRequestSubjectDto Subject(string host = "github.com", int number = 12) => new() {
        Provider = "github", Host = host, RepoHash = "hash", Owner = "example", RepoName = "repo", Number = number };
    static readonly PullRequestRepository Repository = new("github", "github.com", "example", "repo", "hash");

    [Test]
    public async Task Probe_reports_the_tool_missing_without_spawning() {
        var h = new GhHarness(Tmp, installed: false);
        var status = await h.Provider.ProbeAsync(false, default);
        await Assert.That(status.Kind).IsEqualTo(PullRequestReaderStatusKind.ToolMissing);
        await Assert.That(h.Process.Calls).IsEmpty();
        await Assert.That(h.Provider.Serves("github", "github.com")).IsFalse();
        await Assert.That(h.Provider.Tool!.Name).IsEqualTo("GitHub CLI");
        await Assert.That(h.Provider.Tool.SignInCommand("ghe.example")).IsEqualTo("gh auth login --hostname ghe.example");
    }

    [Test]
    public async Task Probe_reports_signed_out_and_ready_from_the_hosts_payload() {
        var h = new GhHarness(Tmp);
        h.Process.When(["auth", "status"], """{"hosts":{}}""", exitCode: 1);
        await Assert.That((await h.Provider.ProbeAsync(false, default)).Kind).IsEqualTo(PullRequestReaderStatusKind.SignedOut);
        await Assert.That(h.LastArgs).IsEquivalentTo(new[] { "auth", "status", "--json", "hosts" });
        var fresh = new GhHarness(Tmp);
        fresh.Process.When(["auth", "status"], GhHarness.Fixture("auth-status.json"));
        await Assert.That((await fresh.Provider.ProbeAsync(false, default)).Kind).IsEqualTo(PullRequestReaderStatusKind.Ready);
        await Assert.That(fresh.Provider.Serves("github", "github.com")).IsTrue();
        await Assert.That(fresh.Provider.Serves("github", "GitHub.com")).IsTrue();
        await Assert.That(fresh.Provider.Serves("github", "ghe.example")).IsFalse();
        await Assert.That(fresh.Provider.Serves("gitlab", "github.com")).IsFalse();
    }

    [Test]
    public async Task Probe_results_are_cached_for_five_minutes_and_refresh_reprobes() {
        var h = new GhHarness(Tmp); h.SignedIn("github.com");
        await h.Provider.ProbeAsync(false, default);
        await h.Provider.ProbeAsync(false, default);
        await Assert.That(h.Process.Calls.Count).IsEqualTo(1);
        h.Time.Advance(TimeSpan.FromMinutes(5));
        await h.Provider.ProbeAsync(false, default);
        await Assert.That(h.Process.Calls.Count).IsEqualTo(2);
        await h.Provider.ProbeAsync(true, default);
        await Assert.That(h.Process.Calls.Count).IsEqualTo(3);
    }

    [Test]
    public async Task A_probe_that_cannot_run_backs_off_instead_of_caching_absence() {
        var h = new GhHarness(Tmp);
        h.Process.When(["auth", "status"], "", timedOut: true);
        var status = await h.Provider.ProbeAsync(false, default);
        await Assert.That(status.Kind).IsEqualTo(PullRequestReaderStatusKind.Failed);
        await h.Provider.ProbeAsync(false, default);
        await Assert.That(h.Process.Calls.Count).IsEqualTo(1);
        h.Time.Advance(TimeSpan.FromSeconds(31));
        await h.Provider.ProbeAsync(false, default);
        await Assert.That(h.Process.Calls.Count).IsEqualTo(2);
    }

    [Test]
    [Arguments("https://github.com/example/repo/pull/12", true)]
    [Arguments("https://github.com/example/repo/pull/12/files", true)]
    [Arguments("https://ghe.example/example/repo/pull/12", false)]
    [Arguments("https://github.com/example/repo/issues/12", false)]
    [Arguments("http://github.com/example/repo/pull/12", false)]
    [Arguments("https://github.com/-bad/repo/pull/12", false)]
    public async Task Links_parse_only_on_github_com_or_a_signed_in_host(string url, bool parsed) {
        var h = new GhHarness(Tmp); h.SignedIn("github.com");
        await h.Provider.ProbeAsync(false, default);
        var subject = h.Provider.ParseLink(url);
        await Assert.That(subject is not null).IsEqualTo(parsed);
        if (parsed) {
            await Assert.That(subject!.Number).IsEqualTo(12);
            await Assert.That(subject.RepoHash).IsEqualTo(RepoHashHelper.ComputeRepoHash("example", "repo"));
        }
    }

    [Test]
    public async Task A_signed_in_enterprise_host_parses_and_validates_its_own_links() {
        var h = new GhHarness(Tmp); h.SignedIn("github.com", "ghe.example");
        await h.Provider.ProbeAsync(false, default);
        var subject = h.Provider.ParseLink("https://ghe.example/example/repo/pull/3")!;
        await Assert.That(subject.Host).IsEqualTo("ghe.example");
        await Assert.That(h.Provider.PrLink("https://ghe.example/example/repo/pull/3", subject)).IsEqualTo("https://ghe.example/example/repo/pull/3");
        await Assert.That(h.Provider.PrLink("https://ghe.example/example/repo/pull/4", subject)).IsNull();
        await Assert.That(h.Provider.PrLink("https://github.com/example/repo/pull/3", subject)).IsNull();
    }

    [Test]
    public async Task Live_discovery_runs_pr_list_for_the_branch_and_maps_valid_rows() {
        var h = new GhHarness(Tmp); h.SignedIn("github.com");
        h.Process.When(["pr", "list"], GhHarness.Fixture("pr-list.json"));
        await h.Provider.ProbeAsync(false, default);
        var links = await h.Provider.DiscoverAsync(Repository, "feature", default);
        await Assert.That(h.LastArgs).IsEquivalentTo(new[] { "pr", "list", "--repo", "github.com/example/repo", "--head", "feature", "--state", "all",
            "--limit", "20", "--json", "number,title,url,headRefName,state,isDraft" });
        await Assert.That(links.Select(link => link.Number).ToArray()).IsEquivalentTo(new[] { 12, 9 });
        await Assert.That(links[0].Provider).IsEqualTo("github");
        await Assert.That(links[0].RepoHash).IsEqualTo("hash");
        await Assert.That(links[0].HeadRef).IsEqualTo("feature");
        await Assert.That(links[0].Title).IsEqualTo("Add the thing");
    }

    [Test]
    public async Task Live_discovery_never_spawns_for_an_unserved_host_or_an_invalid_branch() {
        var h = new GhHarness(Tmp); h.SignedIn("github.com");
        await h.Provider.ProbeAsync(false, default);
        var calls = h.Process.Calls.Count;
        await Assert.That(await h.Provider.DiscoverAsync(Repository with { Host = "ghe.example" }, "feature", default)).IsEmpty();
        await Assert.That(await h.Provider.DiscoverAsync(Repository, "-bad", default)).IsEmpty();
        await Assert.That(await h.Provider.DiscoverAsync(Repository with { Owner = "bad owner" }, "feature", default)).IsEmpty();
        await Assert.That(h.Process.Calls.Count).IsEqualTo(calls);
    }
}
```

- [ ] **Step 4: Run the tests to verify they fail**

Run: `dotnet build test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj 2>&1 | grep -E 'error' | head`
Expected: compile errors naming `GitHubCliReaderProvider`.

- [ ] **Step 5: Write the mapping and the provider**

`GitHubCliMapping.cs` (Task 4 and Task 5 add more members to this class):

```csharp
using System.Globalization;
using System.Text.Json;

namespace Capacitor.Cli.Core.PullRequests.Readers.GitHubCli;

/// <summary>Reads <c>gh</c> JSON into the wire records. Every entry point takes the raw text and tolerates any shape; null means malformed.</summary>
public static class GitHubCliMapping {
    static readonly JsonDocumentOptions Options = new() { MaxDepth = 64 };

    public static JsonDocument? Parse(string json) {
        try { return JsonDocument.Parse(json, Options); }
        catch (JsonException) { return null; }
    }

    public static HashSet<string>? SignedInHosts(string json) {
        using var document = Parse(json);
        if (document is null || !document.RootElement.IsObject || document.RootElement.Prop("hosts") is not { } hosts || !hosts.IsObject) return null;
        var signedIn = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var host in hosts.EnumerateObject()) {
            if (!host.Value.IsArray || !GitHubCliRunner.ValidHost(host.Name)) continue;
            if (host.Value.EnumerateArray().Any(entry => entry.IsObject && entry.Prop("state") is { } state && state.IsString && state.GetString() == "success"))
                signedIn.Add(host.Name);
        }
        return signedIn;
    }

    public static IReadOnlyList<PullRequestLinkDto> Links(string json, PullRequestRepository repository) {
        using var document = Parse(json);
        if (document is null || !document.RootElement.IsArray) return [];
        var links = new List<PullRequestLinkDto>();
        foreach (var row in document.RootElement.EnumerateArray()) {
            if (!row.IsObject || row.Prop("number") is not { } number || !number.IsNumber || !number.TryGetInt32(out var value) || value <= 0) continue;
            links.Add(new() { Provider = "github", Host = repository.Host, RepoHash = repository.RepoHash, Owner = repository.Owner, RepoName = repository.RepoName,
                Number = value, Url = PullRequestWire.SafeLink(Text(row, "url")), Title = Text(row, "title"), HeadRef = Text(row, "headRefName") });
            if (links.Count == 20) break;
        }
        return links;
    }

    public static string? Text(JsonElement element, string name) => element.Prop(name) is { } value && value.IsString ? value.GetString() : null;
    public static DateTime? Time(JsonElement element, string name) => element.Prop(name) is { } value && value.IsString
        && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at) ? at.UtcDateTime : null;
}
```

`GitHubCliReaderProvider.cs`:

```csharp
using System.Globalization;

namespace Capacitor.Cli.Core.PullRequests.Readers.GitHubCli;

public sealed class GitHubCliReaderProvider(GitHubCliRunner cli, TimeProvider? time = null) : IPullRequestReaderProvider {
    static readonly PullRequestReaderTool GitHubCliTool = new("GitHub CLI", "https://cli.github.com",
        host => host is null ? "gh auth login" : "gh auth login --hostname " + host);
    readonly TimeProvider _time = time ?? TimeProvider.System;
    readonly SemaphoreSlim _probeGate = new(1, 1);
    HashSet<string> _hosts = new(StringComparer.OrdinalIgnoreCase);
    PullRequestReaderStatus? _status;
    long _probedAt;
    int _failures;
    TimeSpan _ttl;

    public string Name => "github-cli";
    public string ProviderKind => "github";
    public PullRequestReaderTool? Tool => GitHubCliTool;

    public async Task<PullRequestReaderStatus> ProbeAsync(bool refresh, CancellationToken ct) {
        await _probeGate.WaitAsync(ct).ConfigureAwait(false);
        try {
            if (_status is { } cached && !refresh && _time.GetElapsedTime(_probedAt) < _ttl) return cached;
            if (await cli.LocateAsync(refresh, ct).ConfigureAwait(false) is null) return Save(new(PullRequestReaderStatusKind.ToolMissing), []);
            var result = await cli.RunAsync(["auth", "status", "--json", "hosts"], ct).ConfigureAwait(false);
            if (result.Outcome == GitHubCliOutcome.NotStarted) return Save(new(PullRequestReaderStatusKind.ToolMissing), []);
            if (result.Outcome is GitHubCliOutcome.TimedOut or GitHubCliOutcome.Oversized)
                return Save(new(PullRequestReaderStatusKind.Failed, result.Outcome == GitHubCliOutcome.TimedOut ? "timeout" : "oversized"), []);
            var hosts = GitHubCliMapping.SignedInHosts(result.Stdout);
            if (hosts is null) return Save(result.Outcome == GitHubCliOutcome.Failed ? new(PullRequestReaderStatusKind.SignedOut) : new(PullRequestReaderStatusKind.Failed, "malformed"), []);
            return Save(hosts.Count == 0 ? new(PullRequestReaderStatusKind.SignedOut) : new(PullRequestReaderStatusKind.Ready), hosts);
        } finally { _probeGate.Release(); }
    }
    PullRequestReaderStatus Save(PullRequestReaderStatus status, HashSet<string> hosts) {
        var failed = status.Kind == PullRequestReaderStatusKind.Failed;
        _failures = failed ? Math.Min(_failures + 1, 3) : 0;
        _ttl = failed ? TimeSpan.FromSeconds(_failures switch { 1 => 30, 2 => 60, _ => 300 }) : TimeSpan.FromMinutes(5);
        _hosts = hosts;
        _status = status;
        _probedAt = _time.GetTimestamp();
        return status;
    }

    public bool Serves(string provider, string host) => provider == "github" && _status is { IsReady: true } && _hosts.Contains(host);

    public PullRequestSubjectDto? ParseLink(string? url) {
        if (PullRequestWire.SafeLink(url) is not { } safe) return null;
        var uri = new Uri(safe);
        if (!(uri.IdnHost == "github.com" || _hosts.Contains(uri.IdnHost))) return null;
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length is not (4 or 5) || parts[2] != "pull" || parts.Length == 5 && parts[4] != "files") return null;
        if (!GitHubCliRunner.ValidOwner(parts[0]) || !GitHubCliRunner.ValidRepo(parts[1])
            || !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0) return null;
        return new() { Provider = "github", Host = uri.IdnHost, RepoHash = RepoHashHelper.ComputeRepoHash(parts[0], parts[1]),
            Owner = parts[0], RepoName = parts[1], Number = number };
    }

    public string? PrLink(string? url, PullRequestSubjectDto subject) {
        if (PullRequestWire.SafeLink(url) is not { } safe) return null;
        var uri = new Uri(safe);
        var path = $"/{subject.Owner}/{subject.RepoName}/pull/{subject.Number.ToString(CultureInfo.InvariantCulture)}";
        var actual = uri.AbsolutePath.TrimEnd('/');
        return uri.IdnHost.Equals(subject.Host, StringComparison.OrdinalIgnoreCase)
            && (actual.Equals(path, StringComparison.OrdinalIgnoreCase) || actual.Equals(path + "/files", StringComparison.OrdinalIgnoreCase)) ? safe : null;
    }

    public async Task<IReadOnlyList<PullRequestLinkDto>> DiscoverAsync(PullRequestRepository repository, string branch, CancellationToken ct) {
        if (!Serves(repository.Provider, repository.Host) || !GitHubCliRunner.ValidOwner(repository.Owner)
            || !GitHubCliRunner.ValidRepo(repository.RepoName) || !GitHubCliRunner.ValidBranch(branch)) return [];
        var result = await cli.RunAsync(["pr", "list", "--repo", Repo(repository.Host, repository.Owner, repository.RepoName), "--head", branch,
            "--state", "all", "--limit", "20", "--json", "number,title,url,headRefName,state,isDraft"], ct).ConfigureAwait(false);
        return result.Outcome == GitHubCliOutcome.Ok ? GitHubCliMapping.Links(result.Stdout, repository) : [];
    }

    public Task<PullRequestRead<PullRequestOverviewDto>> OverviewAsync(string sessionId, PullRequestSubjectDto subject, CancellationToken ct)
        => Task.FromResult(new PullRequestRead<PullRequestOverviewDto>(PullRequestReadKind.Unavailable, Subject: subject, Reason: "unsupported", AccessFailure: "invalid"));
    public Task<PullRequestRead<PullRequestPageDto<T>>> PageAsync<T>(string sessionId, PullRequestSubjectDto subject, string section,
            string? cursor, string? resolved, string? threadId, CancellationToken ct) where T : class
        => Task.FromResult(new PullRequestRead<PullRequestPageDto<T>>(PullRequestReadKind.Unavailable, Subject: subject, Reason: "unsupported", AccessFailure: "invalid"));
    public void ResetSession(string sessionId) { }

    public static string Repo(string host, string owner, string name) => $"{host}/{owner}/{name}";
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/GitHubCliReaderProviderTests/*"`
Expected: all passed.

- [ ] **Step 7: Commit**

```bash
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/lazy-wandering-blum add src/Capacitor.Cli.Core/PullRequests/Readers/GitHubCli test/Capacitor.Cli.Core.Tests.Unit test/fixtures/gh
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/lazy-wandering-blum commit -m "Probe, link and discover pull requests through the GitHub CLI (#813)"
```

### Task 4: GitHub CLI provider: overview, whole sections, failures, coalescing

**Files:**
- Create: `src/Capacitor.Cli.Core/PullRequests/Readers/GitHubCli/GitHubCliCursors.cs`, `GitHubCliCursorEntry.cs`, `GitHubCliView.cs`
- Modify: `GitHubCliMapping.cs` (add `View`, `Failure`, `Truncate`), `GitHubCliReaderProvider.cs` (replace the `unsupported` reads)
- Create fixture: `test/fixtures/gh/pr-view.json`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/PullRequests/Readers/GitHubCli/GitHubCliReaderProviderReadTests.cs`

**Interfaces:**
- Consumes: Task 2 runner, Task 3 provider and mapping, `PullRequestOverviewDto` and section DTOs, `PullRequestWire.SafeLink/CheckLink`.
- Produces: `GitHubCliCursors.Mint(GitHubCliCursorEntry)`, `GitHubCliCursors.Get(string handle)`, `GitHubCliCursors.NewHandle()`; `GitHubCliMapping.View(string json, PullRequestSubjectDto subject, DateTime fetchedAt)`; `GitHubCliMapping.Failure<T>(GitHubCliResult, PullRequestSubjectDto)`; `GitHubCliMapping.Truncate(string?)` returning `(string? Text, bool Truncated)`; provider `OverviewAsync` and `PageAsync` for `checks`, `reviewers`, `reviews`, `conversation`. Task 5 reuses `Page<T>` slicing and the cursor store for threads.

- [ ] **Step 1: Add the fixture**

`test/fixtures/gh/pr-view.json`, the real `gh pr view --json` shape with two check runs, one commit status, a pending review to drop, and two comments:

```json
{"author":{"id":"MDQ6VXNlcjE=","is_bot":false,"login":"octocat","name":"The Octocat"},"baseRefName":"main","body":"Adds the thing.\n\nCloses #1","comments":[{"author":{"login":"reviewer-bot"},"authorAssociation":"NONE","body":"Automated summary","createdAt":"2026-09-08T07:48:10Z","id":"IC_kwDOR9HOJ88AAAABTKu62w","includesCreatedEdit":true,"isMinimized":false,"minimizedReason":"","reactionGroups":[],"url":"https://github.com/example/repo/pull/12#issuecomment-5581290203","viewerDidAuthor":false},{"author":{"login":"octocat"},"authorAssociation":"MEMBER","body":"Thanks, addressed.","createdAt":"2026-09-08T08:00:00Z","id":"IC_kwDOR9HOJ88AAAABTKu63A","includesCreatedEdit":false,"isMinimized":false,"minimizedReason":"","reactionGroups":[],"url":"https://github.com/example/repo/pull/12#issuecomment-5581290204","viewerDidAuthor":true}],"headRefName":"feature","headRefOid":"8dc30b635dcd4aac3970e376d5c2d55fc33b91da","isDraft":false,"latestReviews":[{"id":"","author":{"login":"alice"},"authorAssociation":"MEMBER","body":"","submittedAt":"2026-09-08T10:21:05Z","includesCreatedEdit":false,"reactionGroups":[],"state":"APPROVED","commit":{"oid":""}},{"id":"","author":{"login":"bob"},"authorAssociation":"MEMBER","body":"Needs work","submittedAt":"2026-09-08T09:00:00Z","includesCreatedEdit":false,"reactionGroups":[],"state":"CHANGES_REQUESTED","commit":{"oid":""}}],"number":12,"reviewDecision":"CHANGES_REQUESTED","reviewRequests":[{"__typename":"User","login":"carol"},{"__typename":"Team","name":"Core","slug":"core"}],"reviews":[{"author":{"login":"bob"},"authorAssociation":"MEMBER","body":"Needs work","commit":{"oid":"69f3dc2a1ad4eefca609618de4b210787a892732"},"id":"PRR_kwDOR9HOJ88AAAABMkzATw","includesCreatedEdit":false,"reactionGroups":[],"state":"CHANGES_REQUESTED","submittedAt":"2026-09-08T09:00:00Z"},{"author":{"login":"alice"},"authorAssociation":"MEMBER","body":"","commit":{"oid":"8dc30b635dcd4aac3970e376d5c2d55fc33b91da"},"id":"PRR_kwDOR9HOJ88AAAABMkzAUA","includesCreatedEdit":false,"reactionGroups":[],"state":"APPROVED","submittedAt":"2026-09-08T10:21:05Z"},{"author":{"login":"octocat"},"authorAssociation":"MEMBER","body":"draft","commit":{"oid":""},"id":"PRR_kwDOR9HOJ88AAAABMkzAUQ","includesCreatedEdit":false,"reactionGroups":[],"state":"PENDING","submittedAt":null}],"state":"OPEN","statusCheckRollup":[{"__typename":"CheckRun","completedAt":"2026-09-08T11:19:44Z","conclusion":"SUCCESS","detailsUrl":"https://github.com/example/repo/actions/runs/1/job/2","name":"Build and test (ubuntu-latest)","startedAt":"2026-09-08T11:05:47Z","status":"COMPLETED","workflowName":"CI"},{"__typename":"CheckRun","completedAt":null,"conclusion":"","detailsUrl":"https://github.com/example/repo/actions/runs/1/job/3","name":"Build and test (windows-latest)","startedAt":"2026-09-08T11:05:47Z","status":"IN_PROGRESS","workflowName":"CI"},{"__typename":"StatusContext","context":"license/cla","state":"FAILURE","startedAt":"2026-09-08T11:05:00Z","targetUrl":"https://cla.example/check/1"}],"title":"Add the thing","updatedAt":"2026-09-08T11:37:48Z","url":"https://github.com/example/repo/pull/12"}
```

- [ ] **Step 2: Write the failing read tests**

```csharp
using System.Text.Json;
using Capacitor.Cli.Core.PullRequests;
using Capacitor.Cli.Core.PullRequests.Readers.GitHubCli;

namespace Capacitor.Cli.Core.Tests.Unit.PullRequests.Readers.GitHubCli;

public class GitHubCliReaderProviderReadTests {
    [TempDir] public required TempDir Tmp { get; init; }

    static readonly PullRequestSubjectDto Subject = new() { Provider = "github", Host = "github.com", RepoHash = "hash", Owner = "example", RepoName = "repo", Number = 12 };

    static async Task<GhHarness> Ready(TempDir tmp, string? view = null) {
        var h = new GhHarness(tmp); h.SignedIn("github.com");
        h.Process.When(["pr", "view"], view ?? GhHarness.Fixture("pr-view.json"));
        await h.Provider.ProbeAsync(false, default);
        return h;
    }

    [Test]
    public async Task Overview_maps_lifecycle_decision_rollup_and_summaries_with_a_constant_lease() {
        var h = await Ready(Tmp);
        var read = await h.Provider.OverviewAsync("session", Subject, default);
        await Assert.That(h.LastArgs).IsEquivalentTo(new[] { "pr", "view", "12", "--repo", "github.com/example/repo", "--json",
            "title,url,state,isDraft,headRefName,baseRefName,headRefOid,body,updatedAt,reviewDecision,author,statusCheckRollup,reviewRequests,latestReviews,reviews,comments" });
        await Assert.That(read.Kind).IsEqualTo(PullRequestReadKind.Ready);
        await Assert.That(read.AccessValidForSeconds).IsEqualTo(30);
        await Assert.That(read.PollAfterSeconds).IsEqualTo(30);
        await Assert.That(read.Subject).IsEqualTo(Subject);
        var data = read.Data!;
        await Assert.That(data.Title).IsEqualTo("Add the thing");
        await Assert.That(data.Lifecycle).IsEqualTo("open");
        await Assert.That(data.HeadSha).IsEqualTo("8dc30b635dcd4aac3970e376d5c2d55fc33b91da");
        await Assert.That(data.Description).IsEqualTo("Adds the thing.\n\nCloses #1");
        await Assert.That(data.ReviewDecision).IsEqualTo("changes_requested");
        await Assert.That(data.Checks!.Rollup).IsEqualTo("failure");
        await Assert.That(data.Checks.Counts!["pending"].Value).IsEqualTo(1);
        await Assert.That(data.Reviews!.Published!.Value).IsEqualTo(2);
        await Assert.That(data.Reviews.Approved!.Value).IsEqualTo(1);
        await Assert.That(data.Reviews.ChangesRequested!.Value).IsEqualTo(1);
        await Assert.That(data.Reviews.OutstandingUsers!.Value).IsEqualTo(1);
        await Assert.That(data.Reviews.OutstandingTeams!.Value).IsEqualTo(1);
        await Assert.That(data.Conversation!.Count!.Value).IsEqualTo(2);
    }

    [Test]
    [Arguments("MERGED", false, "merged")] [Arguments("CLOSED", true, "closed")] [Arguments("OPEN", true, "draft")] [Arguments("WEIRD", false, "unknown")]
    public async Task Lifecycle_prefers_merged_over_closed_and_draft_over_open(string state, bool draft, string lifecycle) {
        var json = GhHarness.Fixture("pr-view.json").Replace("\"state\":\"OPEN\"", $"\"state\":\"{state}\"").Replace("\"isDraft\":false", $"\"isDraft\":{(draft ? "true" : "false")}");
        var h = await Ready(Tmp, json);
        await Assert.That((await h.Provider.OverviewAsync("session", Subject, default)).Data!.Lifecycle).IsEqualTo(lifecycle);
    }

    [Test]
    public async Task Checks_page_maps_check_runs_and_commit_statuses_as_one_complete_page() {
        var h = await Ready(Tmp);
        var read = await h.Provider.PageAsync<PullRequestCheckDto>("session", Subject, "checks", null, null, null, default);
        var page = read.Data!;
        await Assert.That(page.Coverage).IsEqualTo("complete");
        await Assert.That(page.HasMore).IsFalse();
        await Assert.That(page.HeadSha).IsEqualTo("8dc30b635dcd4aac3970e376d5c2d55fc33b91da");
        await Assert.That(page.Total.Kind).IsEqualTo("exact");
        await Assert.That(page.Total.Value).IsEqualTo(3);
        await Assert.That(PullRequestWire.ValidHandle(page.SnapshotId)).IsTrue();
        await Assert.That(PullRequestWire.ValidHandle(page.PageCursor)).IsTrue();
        await Assert.That(page.Items.Select(item => item.Outcome).ToArray()).IsEquivalentTo(new[] { "success", "pending", "failure" });
        await Assert.That(page.Items[0].Name).IsEqualTo("Build and test (ubuntu-latest)");
        await Assert.That(page.Items[0].AppName).IsEqualTo("CI");
        await Assert.That(page.Items[0].Url).IsEqualTo("https://github.com/example/repo/actions/runs/1/job/2");
        await Assert.That(page.Items[2].Name).IsEqualTo("license/cla");
        await Assert.That(page.Items[2].Source).IsEqualTo("status");
        await Assert.That(page.Items.Select(item => item.Id).Distinct().Count()).IsEqualTo(3);
    }

    [Test]
    public async Task Reviewers_union_requests_and_latest_reviews_and_reviews_drop_pending_drafts() {
        var h = await Ready(Tmp);
        var reviewers = (await h.Provider.PageAsync<PullRequestReviewerDto>("session", Subject, "reviewers", null, null, null, default)).Data!;
        await Assert.That(reviewers.Items.Select(item => item.Actor!.Login ?? item.Actor.Name).ToArray()).IsEquivalentTo(new[] { "carol", "Core", "alice", "bob" });
        await Assert.That(reviewers.Items[0].Requested).IsTrue();
        await Assert.That(reviewers.Items[1].Actor!.Kind).IsEqualTo("team");
        await Assert.That(reviewers.Items[2].ReviewState).IsEqualTo("approved");
        var reviews = (await h.Provider.PageAsync<PullRequestReviewDto>("session", Subject, "reviews", null, null, null, default)).Data!;
        await Assert.That(reviews.Items.Select(item => item.State).ToArray()).IsEquivalentTo(new[] { "changes_requested", "approved" });
        await Assert.That(reviews.Items[0].Author!.Login).IsEqualTo("bob");
        await Assert.That(reviews.Items[0].Id).IsEqualTo("PRR_kwDOR9HOJ88AAAABMkzATw");
        var conversation = (await h.Provider.PageAsync<PullRequestCommentDto>("session", Subject, "conversation", null, null, null, default)).Data!;
        await Assert.That(conversation.Items.Select(item => item.Body).ToArray()).IsEquivalentTo(new[] { "Automated summary", "Thanks, addressed." });
        await Assert.That(conversation.Items[0].Url).IsEqualTo("https://github.com/example/repo/pull/12#issuecomment-5581290203");
    }

    [Test]
    public async Task A_list_at_the_tool_limit_is_limited_paged_by_fifty_and_reloadable_by_cursor() {
        using var fixture = JsonDocument.Parse(GhHarness.Fixture("pr-view.json"));
        var comments = Enumerable.Range(0, 100).Select(i => $$"""{"author":{"login":"u{{i}}"},"body":"c{{i}}","createdAt":"2026-09-08T08:00:00Z","id":"IC_{{i}}","url":"https://github.com/example/repo/pull/12#issuecomment-{{i}}"}""");
        var json = GhHarness.Fixture("pr-view.json").Replace(fixture.RootElement.GetProperty("comments").GetRawText(), "[" + string.Join(',', comments) + "]");
        var h = await Ready(Tmp, json);
        var first = (await h.Provider.PageAsync<PullRequestCommentDto>("session", Subject, "conversation", null, null, null, default)).Data!;
        await Assert.That(first.Coverage).IsEqualTo("limited");
        await Assert.That(first.Total.Kind).IsEqualTo("lower_bound");
        await Assert.That(first.Items.Length).IsEqualTo(50);
        await Assert.That(first.HasMore).IsTrue();
        var second = (await h.Provider.PageAsync<PullRequestCommentDto>("session", Subject, "conversation", first.NextCursor, null, null, default)).Data!;
        await Assert.That(second.SnapshotId).IsEqualTo(first.SnapshotId);
        await Assert.That(second.Items[0].Id).IsEqualTo("IC_50");
        await Assert.That(second.HasMore).IsFalse();
        var again = (await h.Provider.PageAsync<PullRequestCommentDto>("session", Subject, "conversation", first.PageCursor, null, null, default)).Data!;
        await Assert.That(again.Items[0].Id).IsEqualTo("IC_0");
        await Assert.That(h.Process.Calls.Count(call => call.Args[0] == "pr")).IsEqualTo(1);
        var stale = await h.Provider.PageAsync<PullRequestCommentDto>("session", Subject, "conversation", new string('f', 64), null, null, default);
        await Assert.That(stale.Kind).IsEqualTo(PullRequestReadKind.Restart);
        await Assert.That(stale.Reason).IsEqualTo("snapshot_expired");
    }

    [Test]
    public async Task Oversized_bodies_are_cut_with_the_flag_set() {
        var json = GhHarness.Fixture("pr-view.json").Replace("\"body\":\"Adds the thing.\\n\\nCloses #1\"", "\"body\":\"" + new string('x', 262_145) + "\"");
        var h = await Ready(Tmp, json);
        var data = (await h.Provider.OverviewAsync("session", Subject, default)).Data!;
        await Assert.That(data.Description!.Length).IsEqualTo(262_144);
        await Assert.That(data.DescriptionTruncated).IsTrue();
    }

    [Test]
    public async Task Concurrent_reads_of_one_subject_share_a_single_spawn_and_a_completed_view_is_reused_for_ten_seconds() {
        var h = new GhHarness(Tmp); h.SignedIn("github.com");
        var pending = new TaskCompletionSource<ProcessResult>();
        h.Process.WhenPending(["pr", "view"], pending);
        await h.Provider.ProbeAsync(false, default);
        var overview = h.Provider.OverviewAsync("session", Subject, default);
        var checks = h.Provider.PageAsync<PullRequestCheckDto>("session", Subject, "checks", null, null, null, default);
        await Task.Delay(50);
        await Assert.That(h.Process.Calls.Count(call => call.Args[0] == "pr")).IsEqualTo(1);
        pending.SetResult(new(0, GhHarness.Fixture("pr-view.json"), "", false));
        await Assert.That((await overview).Kind).IsEqualTo(PullRequestReadKind.Ready);
        await Assert.That((await checks).Kind).IsEqualTo(PullRequestReadKind.Ready);
        h.Time.Advance(TimeSpan.FromSeconds(9));
        await h.Provider.OverviewAsync("session", Subject, default);
        await Assert.That(h.Process.Calls.Count(call => call.Args[0] == "pr")).IsEqualTo(1);
        h.Time.Advance(TimeSpan.FromSeconds(2));
        h.Process.When(["pr", "view"], GhHarness.Fixture("pr-view.json"));
        await h.Provider.OverviewAsync("session", Subject, default);
        await Assert.That(h.Process.Calls.Count(call => call.Args[0] == "pr")).IsEqualTo(2);
    }

    [Test]
    public async Task A_cancelled_caller_returns_promptly_while_the_shared_spawn_finishes_for_its_peers() {
        var h = new GhHarness(Tmp); h.SignedIn("github.com");
        var pending = new TaskCompletionSource<ProcessResult>();
        h.Process.WhenPending(["pr", "view"], pending);
        await h.Provider.ProbeAsync(false, default);
        using var cancel = new CancellationTokenSource();
        var cancelled = h.Provider.OverviewAsync("session", Subject, cancel.Token);
        var peer = h.Provider.OverviewAsync("session", Subject, default);
        cancel.Cancel();
        var threw = false;
        try { await cancelled; } catch (OperationCanceledException) { threw = true; }
        await Assert.That(threw).IsTrue();
        pending.SetResult(new(0, GhHarness.Fixture("pr-view.json"), "", false));
        await Assert.That((await peer).Kind).IsEqualTo(PullRequestReadKind.Ready);
    }

    [Test]
    [Arguments(1, "GraphQL: Could not resolve to a PullRequest with the number of 12. (repository.pullRequest)", PullRequestReadKind.Unavailable, "not_found", "invalid")]
    [Arguments(1, "HTTP 401: Bad credentials (https://api.github.com/graphql)", PullRequestReadKind.Unavailable, "tool_signed_out", "invalid")]
    [Arguments(1, "HTTP 403: API rate limit exceeded for user ID 1. (https://api.github.com/graphql)", PullRequestReadKind.Unavailable, "rate_limited", null)]
    [Arguments(1, "HTTP 403: Resource not accessible by integration", PullRequestReadKind.Unavailable, "tool_denied", "denied")]
    [Arguments(1, "something else went wrong", PullRequestReadKind.Unavailable, "tool_failed", "transient")]
    public async Task Failed_exits_map_by_message(int exit, string stderr, PullRequestReadKind kind, string reason, string? failure) {
        var h = new GhHarness(Tmp); h.SignedIn("github.com");
        h.Process.When(["pr", "view"], "", exitCode: exit, stderr: stderr);
        await h.Provider.ProbeAsync(false, default);
        var read = await h.Provider.OverviewAsync("session", Subject, default);
        await Assert.That(read.Kind).IsEqualTo(kind);
        await Assert.That(read.Reason).IsEqualTo(reason);
        await Assert.That(read.AccessFailure).IsEqualTo(failure);
        if (reason == "rate_limited") await Assert.That(read.RetryAt).IsEqualTo(h.Time.GetUtcNow().UtcDateTime.AddSeconds(60));
    }

    [Test]
    public async Task Timeouts_oversized_and_malformed_output_map_to_transport_and_protocol_failures() {
        var h = new GhHarness(Tmp); h.SignedIn("github.com");
        h.Process.When(["pr", "view", "1"], "", timedOut: true);
        h.Process.When(["pr", "view", "2"], new string('{', GitHubCliRunner.OutputLimit + 1));
        h.Process.When(["pr", "view", "3"], "not json");
        await h.Provider.ProbeAsync(false, default);
        var timeout = await h.Provider.OverviewAsync("session", Subject with { Number = 1 }, default);
        await Assert.That(timeout.Kind).IsEqualTo(PullRequestReadKind.TransportFailure);
        await Assert.That(timeout.AccessFailure).IsEqualTo("transient");
        await Assert.That((await h.Provider.OverviewAsync("session", Subject with { Number = 2 }, default)).Kind).IsEqualTo(PullRequestReadKind.InvalidProtocol);
        await Assert.That((await h.Provider.OverviewAsync("session", Subject with { Number = 3 }, default)).Kind).IsEqualTo(PullRequestReadKind.InvalidProtocol);
    }

    [Test]
    public async Task An_unserved_host_or_invalid_subject_never_spawns() {
        var h = new GhHarness(Tmp); h.SignedIn("github.com");
        await h.Provider.ProbeAsync(false, default);
        var calls = h.Process.Calls.Count;
        await Assert.That((await h.Provider.OverviewAsync("session", Subject with { Host = "ghe.example" }, default)).Reason).IsEqualTo("no_reader");
        await Assert.That((await h.Provider.OverviewAsync("session", Subject with { Owner = "bad owner" }, default)).Kind).IsEqualTo(PullRequestReadKind.InvalidProtocol);
        await Assert.That((await h.Provider.PageAsync<PullRequestCheckDto>("session", Subject, "checks", "not-a-handle", null, null, default)).Kind).IsEqualTo(PullRequestReadKind.InvalidProtocol);
        await Assert.That((await h.Provider.PageAsync<PullRequestReviewDto>("session", Subject, "checks", null, null, null, default)).Kind).IsEqualTo(PullRequestReadKind.InvalidProtocol);
        await Assert.That(h.Process.Calls.Count).IsEqualTo(calls);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet build test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj 2>&1 | grep -E 'error' | head`
Expected: compile errors naming `GitHubCliView` and the cursor types.

- [ ] **Step 4: Write the cursor store and the view record**

`GitHubCliCursorEntry.cs`:

```csharp
namespace Capacitor.Cli.Core.PullRequests.Readers.GitHubCli;

/// <summary>What a minted handle resolves to. <see cref="Items"/> freezes a whole section; <see cref="After"/> continues a GraphQL connection.</summary>
public sealed record GitHubCliCursorEntry(string SnapshotId, string Key, DateTime StartedAt, string? HeadSha, object? Items = null, int Offset = 0, string? After = null, bool Capped = false);
```

`GitHubCliCursors.cs`:

```csharp
using System.Security.Cryptography;

namespace Capacitor.Cli.Core.PullRequests.Readers.GitHubCli;

/// <summary>Opaque 64-hex handles for pages, bounded to 256 entries, least recently used first.</summary>
public sealed class GitHubCliCursors {
    const int Capacity = 256;
    readonly Lock _lock = new();
    readonly Dictionary<string, LinkedListNode<(string Handle, GitHubCliCursorEntry Entry)>> _entries = new(StringComparer.Ordinal);
    readonly LinkedList<(string Handle, GitHubCliCursorEntry Entry)> _order = new();

    public static string NewHandle() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    public string Mint(GitHubCliCursorEntry entry) {
        var handle = NewHandle();
        lock (_lock) {
            _entries[handle] = _order.AddFirst((handle, entry));
            while (_order.Count > Capacity) { _entries.Remove(_order.Last!.Value.Handle); _order.RemoveLast(); }
        }
        return handle;
    }

    public GitHubCliCursorEntry? Get(string handle) {
        lock (_lock) {
            if (!_entries.TryGetValue(handle, out var node)) return null;
            _order.Remove(node);
            _order.AddFirst(node);
            return node.Value.Entry;
        }
    }
}
```

`GitHubCliView.cs`:

```csharp
namespace Capacitor.Cli.Core.PullRequests.Readers.GitHubCli;

/// <summary>One <c>gh pr view</c> result mapped once; the overview and every whole section read from it.</summary>
public sealed record GitHubCliView(PullRequestOverviewDto Overview, string? HeadSha, DateTime FetchedAt, PullRequestCheckDto[] Checks,
    PullRequestReviewerDto[] Reviewers, PullRequestReviewDto[] Reviews, bool ReviewsCapped, PullRequestCommentDto[] Comments, bool CommentsCapped);
```

- [ ] **Step 5: Add the view mapping, truncation and failure mapping to `GitHubCliMapping`**

Append inside the class:

```csharp
    public const int BodyLimit = 262_144;
    public const int ToolListLimit = 100;
    public const string ViewFields = "title,url,state,isDraft,headRefName,baseRefName,headRefOid,body,updatedAt,reviewDecision,author,statusCheckRollup,reviewRequests,latestReviews,reviews,comments";

    public static (string? Text, bool Truncated) Truncate(string? text) => text is { Length: > BodyLimit } ? (text[..BodyLimit], true) : (text, false);

    public static GitHubCliView? View(string json, PullRequestSubjectDto subject, DateTime fetchedAt) {
        using var document = Parse(json);
        if (document is null || !document.RootElement.IsObject) return null;
        var root = document.RootElement;
        var headSha = Text(root, "headRefOid");
        var checks = Checks(root.Prop("statusCheckRollup"), headSha);
        var reviewers = Reviewers(root.Prop("reviewRequests"), root.Prop("latestReviews"));
        var reviews = Reviews(root.Prop("reviews"), out var reviewsCapped);
        var comments = Comments(root.Prop("comments"), out var commentsCapped);
        var (description, truncated) = Truncate(Text(root, "body"));
        var availability = new PullRequestAvailabilityDto { Status = "ready", FetchedAt = fetchedAt };
        var counts = checks.GroupBy(check => check.Outcome ?? "unknown", StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => new PullRequestCountDto { Kind = "exact", Value = group.Count() }, StringComparer.Ordinal);
        var latest = root.Prop("latestReviews") is { } latestReviews && latestReviews.IsArray ? latestReviews.EnumerateArray().ToArray() : [];
        var requests = root.Prop("reviewRequests") is { } reviewRequests && reviewRequests.IsArray ? reviewRequests.EnumerateArray().ToArray() : [];
        var overview = new PullRequestOverviewDto {
            Title = Text(root, "title"), Url = PullRequestWire.SafeLink(Text(root, "url")), Lifecycle = Lifecycle(Text(root, "state"), root.Bool("isDraft")),
            IsDraft = root.Bool("isDraft"), HeadRef = Text(root, "headRefName"), BaseRef = Text(root, "baseRefName"),
            HeadSha = headSha, Description = description, DescriptionTruncated = truncated, UpdatedAt = Time(root, "updatedAt"),
            ReviewDecision = ReviewDecision(Text(root, "reviewDecision")), AccessCheckedFor = "your GitHub CLI sign-in",
            Checks = new() { Availability = availability, Rollup = Rollup(checks), HeadSha = headSha, Counts = counts },
            Reviews = new() { Availability = availability, Published = Count(reviews.Length, reviewsCapped),
                Approved = Exact(latest.Count(review => Text(review, "state") == "APPROVED")),
                ChangesRequested = Exact(latest.Count(review => Text(review, "state") == "CHANGES_REQUESTED")),
                OutstandingUsers = Exact(requests.Count(request => Text(request, "__typename") == "User")),
                OutstandingTeams = Exact(requests.Count(request => Text(request, "__typename") == "Team")) },
            Conversation = new() { Availability = availability, Count = Count(comments.Length, commentsCapped) },
        };
        return new(overview, headSha, fetchedAt, checks, reviewers, reviews, reviewsCapped, comments, commentsCapped);
    }

    static PullRequestCountDto Exact(int value) => new() { Kind = "exact", Value = value };
    static PullRequestCountDto Count(int value, bool capped) => new() { Kind = capped ? "lower_bound" : "exact", Value = value };

    static string Lifecycle(string? state, bool? draft) => state switch {
        "MERGED" => "merged", "CLOSED" => "closed", "OPEN" => draft == true ? "draft" : "open", _ => "unknown"
    };
    static string? ReviewDecision(string? value) => value switch {
        "APPROVED" => "approved", "CHANGES_REQUESTED" => "changes_requested", "REVIEW_REQUIRED" => "review_required", _ => null };
    static string? ReviewState(string? value) => value switch {
        "APPROVED" => "approved", "CHANGES_REQUESTED" => "changes_requested", "COMMENTED" => "commented", "DISMISSED" => "dismissed", "PENDING" => "pending", _ => null };
    static string? Rollup(PullRequestCheckDto[] checks) => checks.Length == 0 ? null
        : checks.Any(check => check.Outcome is "failure" or "timed_out" or "action_required") ? "failure"
        : checks.Any(check => check.Outcome == "pending") ? "pending" : "success";

    static PullRequestCheckDto[] Checks(JsonElement? rollup, string? headSha) {
        if (rollup is not { } array || !array.IsArray) return [];
        var checks = new List<PullRequestCheckDto>();
        foreach (var entry in array.EnumerateArray()) {
            if (!entry.IsObject) continue;
            var index = checks.Count.ToString(CultureInfo.InvariantCulture);
            if (Text(entry, "__typename") == "CheckRun") {
                var status = Text(entry, "status"); var conclusion = Text(entry, "conclusion");
                checks.Add(new() { Id = "check-" + index, Availability = "available", Url = PullRequestWire.CheckLink(Text(entry, "detailsUrl")), Name = Text(entry, "name"),
                    AppName = Text(entry, "workflowName"), Source = "check_run", Outcome = CheckOutcome(status, conclusion), Status = status?.ToLowerInvariant(),
                    Conclusion = string.IsNullOrEmpty(conclusion) ? null : conclusion.ToLowerInvariant(), StartedAt = Time(entry, "startedAt"), CompletedAt = Time(entry, "completedAt"), HeadSha = headSha });
            } else if (Text(entry, "__typename") == "StatusContext") {
                var state = Text(entry, "state");
                checks.Add(new() { Id = "status-" + index, Availability = "available", Url = PullRequestWire.CheckLink(Text(entry, "targetUrl")), Name = Text(entry, "context"),
                    Source = "status", Outcome = StatusOutcome(state), Status = state?.ToLowerInvariant(), StartedAt = Time(entry, "startedAt"), HeadSha = headSha });
            }
        }
        return [.. checks];
    }
    static string CheckOutcome(string? status, string? conclusion) => status != "COMPLETED" ? "pending" : conclusion switch {
        "SUCCESS" => "success", "FAILURE" or "STARTUP_FAILURE" => "failure", "NEUTRAL" => "neutral", "SKIPPED" => "skipped", "CANCELLED" => "cancelled",
        "TIMED_OUT" => "timed_out", "ACTION_REQUIRED" => "action_required", "STALE" => "stale", _ => "unknown" };
    static string StatusOutcome(string? state) => state switch { "SUCCESS" => "success", "FAILURE" or "ERROR" => "failure", "PENDING" or "EXPECTED" => "pending", _ => "unknown" };

    static PullRequestReviewerDto[] Reviewers(JsonElement? requests, JsonElement? latest) {
        var reviewers = new List<PullRequestReviewerDto>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        if (requests is { } requested && requested.IsArray)
            foreach (var request in requested.EnumerateArray()) {
                if (!request.IsObject) continue;
                var team = Text(request, "__typename") == "Team";
                var id = team ? Text(request, "slug") ?? Text(request, "name") : Text(request, "login");
                if (id is null) continue;
                index[id] = reviewers.Count;
                reviewers.Add(new() { Id = "reviewer:" + id, Availability = "available", Requested = true,
                    Actor = new() { Id = id, Kind = team ? "team" : "user", Login = team ? null : id, Name = team ? Text(request, "name") ?? id : null } });
            }
        if (latest is { } reviews && reviews.IsArray)
            foreach (var review in reviews.EnumerateArray()) {
                if (!review.IsObject || review.Prop("author") is not { } author || Text(author, "login") is not { } login) continue;
                var mapped = new PullRequestReviewerDto { Id = "reviewer:" + login, Availability = "available", Requested = false,
                    Actor = new() { Id = login, Kind = "user", Login = login }, ReviewState = ReviewState(Text(review, "state")), SubmittedAt = Time(review, "submittedAt") };
                if (index.TryGetValue(login, out var at)) reviewers[at] = mapped with { Requested = true };
                else { index[login] = reviewers.Count; reviewers.Add(mapped); }
            }
        return [.. reviewers];
    }

    static PullRequestReviewDto[] Reviews(JsonElement? element, out bool capped) {
        capped = false;
        if (element is not { } array || !array.IsArray) return [];
        capped = array.GetArrayLength() >= ToolListLimit;
        var reviews = new List<PullRequestReviewDto>();
        foreach (var review in array.EnumerateArray()) {
            if (!review.IsObject || Text(review, "state") == "PENDING") continue;
            var (body, truncated) = Truncate(Text(review, "body"));
            var login = review.Prop("author") is { } author ? Text(author, "login") : null;
            var id = Text(review, "id") is { Length: > 0 } nodeId ? nodeId : "review-" + reviews.Count.ToString(CultureInfo.InvariantCulture);
            reviews.Add(new() { Id = id, Availability = "available", Author = login is null ? null : new() { Id = login, Kind = "user", Login = login },
                Body = body, BodyTruncated = truncated, State = ReviewState(Text(review, "state")), SubmittedAt = Time(review, "submittedAt") });
        }
        return [.. reviews];
    }

    static PullRequestCommentDto[] Comments(JsonElement? element, out bool capped) {
        capped = false;
        if (element is not { } array || !array.IsArray) return [];
        capped = array.GetArrayLength() >= ToolListLimit;
        var comments = new List<PullRequestCommentDto>();
        foreach (var comment in array.EnumerateArray()) {
            if (!comment.IsObject || Text(comment, "id") is not { Length: > 0 } id) continue;
            comments.Add(Comment(comment, id, null));
        }
        return [.. comments];
    }

    public static PullRequestCommentDto Comment(JsonElement comment, string id, string? replyTo) {
        var (body, truncated) = Truncate(Text(comment, "body"));
        var login = comment.Prop("author") is { } author ? Text(author, "login") : null;
        return new() { Id = id, Availability = "available", Url = PullRequestWire.SafeLink(Text(comment, "url")), Author = login is null ? null : new() { Id = login, Kind = "user", Login = login },
            Body = body, BodyTruncated = truncated, CreatedAt = Time(comment, "createdAt"), UpdatedAt = Time(comment, "updatedAt"), PublishedAt = Time(comment, "publishedAt"), ReplyToId = replyTo };
    }

    public static PullRequestRead<T> Failure<T>(GitHubCliResult result, PullRequestSubjectDto subject, DateTime now) where T : class {
        switch (result.Outcome) {
            case GitHubCliOutcome.NotStarted: return new(PullRequestReadKind.Unavailable, Subject: subject, Reason: "tool_failed", AccessFailure: "transient");
            case GitHubCliOutcome.TimedOut: return new(PullRequestReadKind.TransportFailure, Subject: subject, Reason: "timeout", AccessFailure: "transient");
            case GitHubCliOutcome.Oversized: return new(PullRequestReadKind.InvalidProtocol, Subject: subject, Reason: "oversized", AccessFailure: "invalid");
        }
        var message = result.Stderr;
        if (message.Contains("Could not resolve to a", StringComparison.Ordinal) || message.Contains("HTTP 404", StringComparison.Ordinal))
            return new(PullRequestReadKind.Unavailable, Subject: subject, Reason: "not_found", AccessFailure: "invalid");
        if (message.Contains("HTTP 401", StringComparison.Ordinal) || message.Contains("not logged in", StringComparison.OrdinalIgnoreCase) || message.Contains("authentication", StringComparison.OrdinalIgnoreCase))
            return new(PullRequestReadKind.Unavailable, Subject: subject, Reason: "tool_signed_out", AccessFailure: "invalid");
        if (message.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
            return new(PullRequestReadKind.Unavailable, Subject: subject, Reason: "rate_limited", RetryAt: now.AddSeconds(60));
        if (message.Contains("HTTP 403", StringComparison.Ordinal))
            return new(PullRequestReadKind.Unavailable, Subject: subject, Reason: "tool_denied", AccessFailure: "denied");
        return new(PullRequestReadKind.Unavailable, Subject: subject, Reason: "tool_failed", AccessFailure: "transient");
    }
```

- [ ] **Step 6: Replace the `unsupported` reads in the provider**

Add these fields to `GitHubCliReaderProvider`:

```csharp
    readonly GitHubCliCursors _cursors = new();
    readonly Lock _views = new();
    readonly Dictionary<string, Task<(GitHubCliView? View, GitHubCliResult Result)>> _inflight = new(StringComparer.Ordinal);
    readonly Dictionary<string, (long At, GitHubCliView View)> _recent = new(StringComparer.Ordinal);
```

Replace `OverviewAsync`, `PageAsync` and add the helpers:

```csharp
    public async Task<PullRequestRead<PullRequestOverviewDto>> OverviewAsync(string sessionId, PullRequestSubjectDto subject, CancellationToken ct) {
        if (Refuse<PullRequestOverviewDto>(subject) is { } refused) return refused;
        var started = _time.GetTimestamp();
        var (view, result) = await ViewAsync(subject, ct).ConfigureAwait(false);
        if (view is null) return result.Outcome == GitHubCliOutcome.Ok ? Invalid<PullRequestOverviewDto>(subject) : GitHubCliMapping.Failure<PullRequestOverviewDto>(result, subject, Now);
        return Ready(view.Overview, subject, view.FetchedAt, started);
    }

    public async Task<PullRequestRead<PullRequestPageDto<T>>> PageAsync<T>(string sessionId, PullRequestSubjectDto subject, string section,
            string? cursor, string? resolved, string? threadId, CancellationToken ct) where T : class {
        if (Refuse<PullRequestPageDto<T>>(subject) is { } refused) return refused;
        var valid = section switch {
            "checks" => typeof(T) == typeof(PullRequestCheckDto), "reviewers" => typeof(T) == typeof(PullRequestReviewerDto),
            "reviews" => typeof(T) == typeof(PullRequestReviewDto), "conversation" => typeof(T) == typeof(PullRequestCommentDto), _ => false
        };
        if (!valid || cursor is not null && !PullRequestWire.ValidHandle(cursor)) return Invalid<PullRequestPageDto<T>>(subject);
        var key = Key(subject) + "|" + section;
        var started = _time.GetTimestamp();
        if (cursor is not null) return Slice<T>(cursor, key, null, subject, started);
        var (view, result) = await ViewAsync(subject, ct).ConfigureAwait(false);
        if (view is null) return result.Outcome == GitHubCliOutcome.Ok ? Invalid<PullRequestPageDto<T>>(subject) : GitHubCliMapping.Failure<PullRequestPageDto<T>>(result, subject, Now);
        (object Items, bool Capped) frozen = section switch {
            "checks" => ((object)view.Checks, false), "reviewers" => ((object)view.Reviewers, false),
            "reviews" => ((object)view.Reviews, view.ReviewsCapped), _ => ((object)view.Comments, view.CommentsCapped)
        };
        var entry = new GitHubCliCursorEntry(GitHubCliCursors.NewHandle(), key, Now, section == "checks" ? view.HeadSha : null, frozen.Items, 0, null, frozen.Capped);
        return Slice<T>(_cursors.Mint(entry), key, entry, subject, started);
    }

    PullRequestRead<PullRequestPageDto<T>> Slice<T>(string handle, string key, GitHubCliCursorEntry? entry, PullRequestSubjectDto subject, long started) where T : class {
        entry ??= _cursors.Get(handle);
        if (entry is null || entry.Key != key || entry.Items is not T[] items)
            return new(PullRequestReadKind.Restart, Subject: subject, Reason: "snapshot_expired");
        var slice = items.Skip(entry.Offset).Take(50).ToArray();
        var hasMore = entry.Offset + 50 < items.Length;
        var capped = entry.Capped;
        var page = new PullRequestPageDto<T> {
            SnapshotId = entry.SnapshotId, SnapshotStartedAt = entry.StartedAt, SnapshotCompletedAt = entry.StartedAt,
            Coverage = capped ? "limited" : "complete", CoverageReason = capped ? "tool_limit" : null, HeadSha = entry.HeadSha,
            Total = new() { Kind = capped ? "lower_bound" : "exact", Value = items.Length }, ExcludedByFilter = new() { Kind = "exact", Value = 0 },
            Items = slice, PageCursor = handle, HasMore = hasMore, NextCursor = hasMore ? _cursors.Mint(entry with { Offset = entry.Offset + 50 }) : null,
        };
        return Ready(page, subject, entry.StartedAt, started);
    }

    Task<(GitHubCliView? View, GitHubCliResult Result)> ViewAsync(PullRequestSubjectDto subject, CancellationToken ct) {
        var key = Key(subject);
        Task<(GitHubCliView?, GitHubCliResult)> task;
        lock (_views) {
            if (_recent.TryGetValue(key, out var recent) && _time.GetElapsedTime(recent.At) < TimeSpan.FromSeconds(10))
                return Task.FromResult<(GitHubCliView?, GitHubCliResult)>((recent.View, new(GitHubCliOutcome.Ok, 0, "", "")));
            if (!_inflight.TryGetValue(key, out task!)) { task = FetchAsync(subject, key); _inflight[key] = task; }
        }
        return task.WaitAsync(ct);
    }

    // A shared fetch runs on its own token: one caller's cancellation must not fail its peers, and the runner's deadline bounds it.
    async Task<(GitHubCliView?, GitHubCliResult)> FetchAsync(PullRequestSubjectDto subject, string key) {
        try {
            var result = await cli.RunAsync(["pr", "view", subject.Number.ToString(CultureInfo.InvariantCulture), "--repo", Repo(subject.Host, subject.Owner, subject.RepoName),
                "--json", GitHubCliMapping.ViewFields], CancellationToken.None).ConfigureAwait(false);
            var view = result.Outcome == GitHubCliOutcome.Ok ? GitHubCliMapping.View(result.Stdout, subject, Now) : null;
            if (view is not null) lock (_views) {
                _recent[key] = (_time.GetTimestamp(), view);
                while (_recent.Count > 64) _recent.Remove(_recent.MinBy(pair => pair.Value.At).Key);
            }
            return (view, result);
        } finally { lock (_views) _inflight.Remove(key); }
    }

    PullRequestRead<T>? Refuse<T>(PullRequestSubjectDto subject) where T : class {
        if (!Serves(subject.Provider, subject.Host)) return new(PullRequestReadKind.Unavailable, Subject: subject, Reason: "no_reader", AccessFailure: "invalid");
        if (!PullRequestWire.ValidSubject(subject) || !GitHubCliRunner.ValidHost(subject.Host) || !GitHubCliRunner.ValidOwner(subject.Owner)
            || !GitHubCliRunner.ValidRepo(subject.RepoName) || !GitHubCliRunner.ValidNumber(subject.Number)) return Invalid<T>(subject);
        return null;
    }
    PullRequestRead<T> Ready<T>(T data, PullRequestSubjectDto subject, DateTime fetchedAt, long started) where T : class
        => new(PullRequestReadKind.Ready, data, subject, fetchedAt, PollAfterSeconds: 30, AccessValidForSeconds: 30, RequestStarted: started);
    static PullRequestRead<T> Invalid<T>(PullRequestSubjectDto subject) where T : class
        => new(PullRequestReadKind.InvalidProtocol, Subject: subject, Reason: "protocol_error", AccessFailure: "invalid");
    DateTime Now => _time.GetUtcNow().UtcDateTime;
    static string Key(PullRequestSubjectDto subject) => $"{subject.Host}|{subject.Owner}|{subject.RepoName}|{subject.Number.ToString(CultureInfo.InvariantCulture)}".ToLowerInvariant();
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/GitHubCliReaderProvider*/*"`
Expected: all passed.

- [ ] **Step 8: Commit**

```bash
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/lazy-wandering-blum add src/Capacitor.Cli.Core/PullRequests/Readers/GitHubCli test/Capacitor.Cli.Core.Tests.Unit test/fixtures/gh
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/lazy-wandering-blum commit -m "Read overview, checks, reviewers, reviews and conversation via gh pr view (#813)"
```

### Task 5: GitHub CLI provider: inline threads and replies over GraphQL

**Files:**
- Create: `src/Capacitor.Cli.Core/PullRequests/Readers/GitHubCli/GitHubCliThreadsPage.cs`, `GitHubCliCommentsPage.cs`
- Modify: `GitHubCliMapping.cs` (add the two queries and `Threads`/`ThreadComments`), `GitHubCliReaderProvider.cs` (add the `threads` and `thread_comments` sections)
- Modify: `test/.../GitHubCli/FakeGhProcessRunner.cs` (add `WhenAll`)
- Create fixtures: `test/fixtures/gh/review-threads.json`, `review-threads-2.json`, `thread-comments.json`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/PullRequests/Readers/GitHubCli/GitHubCliReaderProviderThreadTests.cs`

**Interfaces:**
- Consumes: Task 4 cursor store (`GitHubCliCursorEntry.After` carries the GraphQL end cursor), `GitHubCliMapping.Comment(JsonElement, id, replyTo)`, `Truncate`, `Failure`, `Ready`, `Invalid`, `Refuse`, `Now`, `Key`.
- Produces: `GitHubCliMapping.ThreadsQuery`, `ThreadCommentsQuery`, `Threads(string json)` returning `GitHubCliThreadsPage?`, `ThreadComments(string json)` returning `GitHubCliCommentsPage?`; provider `PageAsync` for `threads` (with `resolved` filter) and `thread_comments`.

- [ ] **Step 1: Add the fixtures and the fake's `WhenAll`**

`review-threads.json`:

```json
{"data":{"repository":{"pullRequest":{"headRefOid":"8dc30b635dcd4aac3970e376d5c2d55fc33b91da","reviewThreads":{"totalCount":3,"pageInfo":{"hasNextPage":true,"endCursor":"Y3Vyc29yOnYyOpK0MjAyNi0wOS0wOFQwNzo1MTozOVrOoCTmpA=="},"nodes":[{"id":"PRRT_1","isResolved":true,"isOutdated":true,"path":"src/A.cs","line":null,"startLine":null,"originalLine":35,"originalStartLine":34,"diffSide":"RIGHT","startDiffSide":"RIGHT","subjectType":"LINE","comments":{"totalCount":2,"nodes":[{"id":"PRRC_1","url":"https://github.com/example/repo/pull/12#discussion_r1","body":"Resolved remark","createdAt":"2026-09-08T07:51:39Z","updatedAt":"2026-09-08T07:51:39Z","publishedAt":"2026-09-08T07:51:39Z","diffHunk":"@@ -1,2 +1,3 @@\n+using X;","author":{"login":"alice"}}]}},{"id":"PRRT_2","isResolved":false,"isOutdated":false,"path":"src/B.cs","line":10,"startLine":null,"originalLine":10,"originalStartLine":null,"diffSide":"RIGHT","startDiffSide":null,"subjectType":"LINE","comments":{"totalCount":1,"nodes":[{"id":"PRRC_2","url":"https://github.com/example/repo/pull/12#discussion_r2","body":"Open question","createdAt":"2026-09-08T08:00:00Z","updatedAt":"2026-09-08T08:00:00Z","publishedAt":"2026-09-08T08:00:00Z","diffHunk":"@@ -5,2 +5,3 @@\n+var y;","author":{"login":"bob"}}]}}]}}}}}
```

`review-threads-2.json`:

```json
{"data":{"repository":{"pullRequest":{"headRefOid":"8dc30b635dcd4aac3970e376d5c2d55fc33b91da","reviewThreads":{"totalCount":3,"pageInfo":{"hasNextPage":false,"endCursor":"Y3Vyc29yOnYyOpK0MjAyNi0wOS0wOFQwODowMDowMFrOoCTmpQ=="},"nodes":[{"id":"PRRT_3","isResolved":false,"isOutdated":false,"path":"src/C.cs","line":3,"startLine":1,"originalLine":3,"originalStartLine":1,"diffSide":"LEFT","startDiffSide":"LEFT","subjectType":"FILE","comments":{"totalCount":1,"nodes":[{"id":"PRRC_4","url":"https://github.com/example/repo/pull/12#discussion_r4","body":"Second page","createdAt":"2026-09-08T08:10:00Z","updatedAt":"2026-09-08T08:10:00Z","publishedAt":"2026-09-08T08:10:00Z","diffHunk":"@@ -1 +1 @@\n-old\n+new","author":{"login":"carol"}}]}}]}}}}}
```

`thread-comments.json`:

```json
{"data":{"node":{"comments":{"totalCount":2,"pageInfo":{"hasNextPage":false,"endCursor":"Y3Vyc29yOnYyOpK0MjAyNi0wOS0wOFQwODowMDowMFrOoCTmpg=="},"nodes":[{"id":"PRRC_2","url":"https://github.com/example/repo/pull/12#discussion_r2","body":"Open question","createdAt":"2026-09-08T08:00:00Z","updatedAt":"2026-09-08T08:00:00Z","publishedAt":"2026-09-08T08:00:00Z","replyTo":null,"author":{"login":"bob"}},{"id":"PRRC_3","url":"https://github.com/example/repo/pull/12#discussion_r3","body":"Reply","createdAt":"2026-09-08T08:05:00Z","updatedAt":"2026-09-08T08:05:00Z","publishedAt":"2026-09-08T08:05:00Z","replyTo":{"id":"PRRC_2"},"author":{"login":"alice"}}]}}}}
```

In `FakeGhProcessRunner` add, after `When`:

```csharp
    /// <summary>Matches when every needle appears somewhere in the argument list; register the more specific rule first.</summary>
    public void WhenAll(string[] needles, string stdout, int exitCode = 0, string stderr = "")
        => _replies.Add((needles, () => Task.FromResult(new ProcessResult(exitCode, stdout, stderr, false))));
```

and change the matching loop in `RunAsync` to accept either shape:

```csharp
        foreach (var (prefix, reply) in _replies)
            if (args.Length >= prefix.Length && prefix.SequenceEqual(args.Take(prefix.Length)) || prefix.All(args.Contains)) return reply();
```

- [ ] **Step 2: Write the failing thread tests**

```csharp
using Capacitor.Cli.Core.PullRequests;
using Capacitor.Cli.Core.PullRequests.Readers.GitHubCli;

namespace Capacitor.Cli.Core.Tests.Unit.PullRequests.Readers.GitHubCli;

public class GitHubCliReaderProviderThreadTests {
    [TempDir] public required TempDir Tmp { get; init; }

    static readonly PullRequestSubjectDto Subject = new() { Provider = "github", Host = "github.com", RepoHash = "hash", Owner = "example", RepoName = "repo", Number = 12 };
    const string Cursor1 = "Y3Vyc29yOnYyOpK0MjAyNi0wOS0wOFQwNzo1MTozOVrOoCTmpA==";

    static async Task<GhHarness> Ready(TempDir tmp, string? secondPage = null) {
        var h = new GhHarness(tmp); h.SignedIn("github.com");
        h.Process.WhenAll(["api", "graphql", "after=" + Cursor1], secondPage ?? GhHarness.Fixture("review-threads-2.json"));
        h.Process.WhenAll(["api", "graphql", "-F", "number=12"], GhHarness.Fixture("review-threads.json"));
        h.Process.WhenAll(["api", "graphql", "id=PRRT_2"], GhHarness.Fixture("thread-comments.json"));
        await h.Provider.ProbeAsync(false, default);
        return h;
    }

    [Test]
    public async Task First_threads_page_queries_with_typed_variables_and_hides_resolved_threads_by_default() {
        var h = await Ready(Tmp);
        var read = await h.Provider.PageAsync<PullRequestThreadDto>("session", Subject, "threads", null, null, null, default);
        await Assert.That(h.LastArgs).IsEquivalentTo(new[] { "api", "graphql", "--hostname", "github.com", "-f", "query=" + GitHubCliMapping.ThreadsQuery,
            "-f", "owner=example", "-f", "repo=repo", "-F", "number=12" });
        var page = read.Data!;
        await Assert.That(page.Items.Select(item => item.Id).ToArray()).IsEquivalentTo(new[] { "PRRT_2" });
        await Assert.That(page.ExcludedByFilter.Value).IsEqualTo(1);
        await Assert.That(page.Total.Kind).IsEqualTo("unknown");
        await Assert.That(page.HasMore).IsTrue();
        await Assert.That(page.HeadSha).IsEqualTo("8dc30b635dcd4aac3970e376d5c2d55fc33b91da");
        var thread = page.Items[0];
        await Assert.That(thread.Path).IsEqualTo("src/B.cs");
        await Assert.That(thread.Line).IsEqualTo(10);
        await Assert.That(thread.DiffSide).IsEqualTo("right");
        await Assert.That(thread.SubjectType).IsEqualTo("line");
        await Assert.That(thread.DiffHunk).IsEqualTo("@@ -5,2 +5,3 @@\n+var y;");
        await Assert.That(thread.RootComment!.Body).IsEqualTo("Open question");
        await Assert.That(thread.RootComment.Author!.Login).IsEqualTo("bob");
        await Assert.That(thread.Comments!.Value).IsEqualTo(1);
        await Assert.That(thread.Url).IsEqualTo("https://github.com/example/repo/pull/12#discussion_r2");
    }

    [Test]
    public async Task Including_resolved_threads_reports_an_exact_total_and_keeps_every_thread() {
        var h = await Ready(Tmp);
        var page = (await h.Provider.PageAsync<PullRequestThreadDto>("session", Subject, "threads", null, "all", null, default)).Data!;
        await Assert.That(page.Items.Length).IsEqualTo(2);
        await Assert.That(page.Items[0].IsResolved).IsTrue();
        await Assert.That(page.Items[0].IsOutdated).IsTrue();
        await Assert.That(page.Total.Kind).IsEqualTo("exact");
        await Assert.That(page.Total.Value).IsEqualTo(3);
        await Assert.That(page.ExcludedByFilter.Value).IsEqualTo(0);
    }

    [Test]
    public async Task The_next_cursor_continues_the_connection_under_the_same_snapshot() {
        var h = await Ready(Tmp);
        var first = (await h.Provider.PageAsync<PullRequestThreadDto>("session", Subject, "threads", null, null, null, default)).Data!;
        var second = (await h.Provider.PageAsync<PullRequestThreadDto>("session", Subject, "threads", first.NextCursor, null, null, default)).Data!;
        await Assert.That(h.LastArgs).Contains("after=" + Cursor1);
        await Assert.That(second.SnapshotId).IsEqualTo(first.SnapshotId);
        await Assert.That(second.Items.Single().Id).IsEqualTo("PRRT_3");
        await Assert.That(second.HasMore).IsFalse();
        await Assert.That(second.NextCursor).IsNull();
        var again = (await h.Provider.PageAsync<PullRequestThreadDto>("session", Subject, "threads", first.PageCursor, null, null, default)).Data!;
        await Assert.That(again.Items.Single().Id).IsEqualTo("PRRT_2");
    }

    [Test]
    public async Task A_head_change_between_pages_restarts_the_chain() {
        var moved = GhHarness.Fixture("review-threads-2.json").Replace("8dc30b635dcd4aac3970e376d5c2d55fc33b91da", new string('b', 40));
        var h = await Ready(Tmp, moved);
        var first = (await h.Provider.PageAsync<PullRequestThreadDto>("session", Subject, "threads", null, null, null, default)).Data!;
        var read = await h.Provider.PageAsync<PullRequestThreadDto>("session", Subject, "threads", first.NextCursor, null, null, default);
        await Assert.That(read.Kind).IsEqualTo(PullRequestReadKind.Restart);
        await Assert.That(read.Reason).IsEqualTo("head_changed");
    }

    [Test]
    public async Task An_all_resolved_page_with_more_behind_it_keeps_fetching_so_a_page_with_more_is_never_empty() {
        var allResolved = GhHarness.Fixture("review-threads.json").Replace("\"isResolved\":false", "\"isResolved\":true");
        var h = new GhHarness(Tmp); h.SignedIn("github.com");
        h.Process.WhenAll(["api", "graphql", "after=" + Cursor1], GhHarness.Fixture("review-threads-2.json"));
        h.Process.WhenAll(["api", "graphql", "-F", "number=12"], allResolved);
        await h.Provider.ProbeAsync(false, default);
        var page = (await h.Provider.PageAsync<PullRequestThreadDto>("session", Subject, "threads", null, null, null, default)).Data!;
        await Assert.That(h.Process.Calls.Count(call => call.Args[0] == "api")).IsEqualTo(2);
        await Assert.That(page.Items.Single().Id).IsEqualTo("PRRT_3");
        await Assert.That(page.ExcludedByFilter.Value).IsEqualTo(2);
        await Assert.That(page.HasMore).IsFalse();
    }

    [Test]
    public async Task Thread_replies_query_the_thread_node_and_carry_reply_targets() {
        var h = await Ready(Tmp);
        var read = await h.Provider.PageAsync<PullRequestCommentDto>("session", Subject, "thread_comments", null, null, "PRRT_2", default);
        await Assert.That(h.LastArgs).IsEquivalentTo(new[] { "api", "graphql", "--hostname", "github.com", "-f", "query=" + GitHubCliMapping.ThreadCommentsQuery, "-f", "id=PRRT_2" });
        var page = read.Data!;
        await Assert.That(page.Items.Length).IsEqualTo(2);
        await Assert.That(page.Items[1].ReplyToId).IsEqualTo("PRRC_2");
        await Assert.That(page.Total.Value).IsEqualTo(2);
        await Assert.That(page.HasMore).IsFalse();
    }

    [Test]
    public async Task Invalid_thread_ids_filters_and_a_missing_pull_request_are_refused_or_mapped_without_a_bad_spawn() {
        var h = await Ready(Tmp);
        var calls = h.Process.Calls.Count;
        await Assert.That((await h.Provider.PageAsync<PullRequestCommentDto>("session", Subject, "thread_comments", null, null, "bad id", default)).Kind).IsEqualTo(PullRequestReadKind.InvalidProtocol);
        await Assert.That((await h.Provider.PageAsync<PullRequestThreadDto>("session", Subject, "threads", null, "resolved", null, default)).Kind).IsEqualTo(PullRequestReadKind.InvalidProtocol);
        await Assert.That(h.Process.Calls.Count).IsEqualTo(calls);
        var gone = new GhHarness(Tmp); gone.SignedIn("github.com");
        gone.Process.WhenAll(["api", "graphql"], """{"data":{"repository":{"pullRequest":null}}}""");
        await gone.Provider.ProbeAsync(false, default);
        var read = await gone.Provider.PageAsync<PullRequestThreadDto>("session", Subject, "threads", null, null, null, default);
        await Assert.That(read.Kind).IsEqualTo(PullRequestReadKind.Unavailable);
        await Assert.That(read.Reason).IsEqualTo("not_found");
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet build test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj 2>&1 | grep -E 'error' | head`
Expected: compile errors naming `ThreadsQuery`.

- [ ] **Step 4: Write the page records and the mapping**

`GitHubCliThreadsPage.cs`:

```csharp
namespace Capacitor.Cli.Core.PullRequests.Readers.GitHubCli;

/// <summary>One GraphQL <c>reviewThreads</c> page. <see cref="Found"/> is false when the PR resolved to null.</summary>
public sealed record GitHubCliThreadsPage(bool Found, string? HeadSha, int Total, bool HasNext, string? EndCursor, PullRequestThreadDto[] Threads);
```

`GitHubCliCommentsPage.cs`:

```csharp
namespace Capacitor.Cli.Core.PullRequests.Readers.GitHubCli;

public sealed record GitHubCliCommentsPage(bool Found, int Total, bool HasNext, string? EndCursor, PullRequestCommentDto[] Comments);
```

Append to `GitHubCliMapping`:

```csharp
    public const string ThreadsQuery = "query($owner:String!,$repo:String!,$number:Int!,$after:String){repository(owner:$owner,name:$repo){pullRequest(number:$number){headRefOid reviewThreads(first:50,after:$after){totalCount pageInfo{hasNextPage endCursor} nodes{id isResolved isOutdated path line startLine originalLine originalStartLine diffSide startDiffSide subjectType comments(first:1){totalCount nodes{id url body createdAt updatedAt publishedAt diffHunk author{login}}}}}}}}";
    public const string ThreadCommentsQuery = "query($id:ID!,$after:String){node(id:$id){... on PullRequestReviewThread{comments(first:50,after:$after){totalCount pageInfo{hasNextPage endCursor} nodes{id url body createdAt updatedAt publishedAt replyTo{id} author{login}}}}}}";

    public static GitHubCliThreadsPage? Threads(string json) {
        using var document = Parse(json);
        if (document is null || !document.RootElement.IsObject || document.RootElement.Prop("data") is not { } data || !data.IsObject) return null;
        var pull = data.Prop("repository") is { } repository && repository.IsObject ? repository.Prop("pullRequest") : null;
        if (pull is null || pull.Value.IsNull) return new(false, null, 0, false, null, []);
        if (!pull.Value.IsObject || pull.Value.Prop("reviewThreads") is not { } connection || !connection.IsObject
            || connection.Prop("nodes") is not { } nodes || !nodes.IsArray) return null;
        var threads = new List<PullRequestThreadDto>();
        foreach (var node in nodes.EnumerateArray()) {
            if (!node.IsObject || Text(node, "id") is not { Length: > 0 } id || !GitHubCliRunner.ValidNodeId(id)) continue;
            var first = node.Prop("comments") is { } comments && comments.IsObject && comments.Prop("nodes") is { } list && list.IsArray
                ? list.EnumerateArray().FirstOrDefault(comment => comment.IsObject) : default;
            var root = first.IsObject && Text(first, "id") is { Length: > 0 } commentId ? Comment(first, commentId, null) : null;
            var (hunk, hunkTruncated) = Truncate(first.IsObject ? Text(first, "diffHunk") : null);
            threads.Add(new() { Id = id, Availability = "available", Url = root?.Url, IsResolved = node.Bool("isResolved"), IsOutdated = node.Bool("isOutdated"),
                Path = Text(node, "path"), DiffSide = Text(node, "diffSide")?.ToLowerInvariant(), StartDiffSide = Text(node, "startDiffSide")?.ToLowerInvariant(),
                Line = Number(node, "line"), StartLine = Number(node, "startLine"), OriginalLine = Number(node, "originalLine"), OriginalStartLine = Number(node, "originalStartLine"),
                SubjectType = Text(node, "subjectType")?.ToLowerInvariant(), DiffHunk = hunk, HunkTruncated = hunkTruncated, RootComment = root,
                Comments = node.Prop("comments") is { } count && count.IsObject && Number(count, "totalCount") is { } total ? new() { Kind = "exact", Value = total } : null });
        }
        return new(true, Text(pull.Value, "headRefOid"), Number(connection, "totalCount") ?? threads.Count, HasNext(connection), EndCursor(connection), [.. threads]);
    }

    public static GitHubCliCommentsPage? ThreadComments(string json) {
        using var document = Parse(json);
        if (document is null || !document.RootElement.IsObject || document.RootElement.Prop("data") is not { } data || !data.IsObject) return null;
        var node = data.Prop("node");
        if (node is null || node.Value.IsNull) return new(false, 0, false, null, []);
        if (!node.Value.IsObject || node.Value.Prop("comments") is not { } connection || !connection.IsObject
            || connection.Prop("nodes") is not { } nodes || !nodes.IsArray) return null;
        var comments = new List<PullRequestCommentDto>();
        foreach (var comment in nodes.EnumerateArray()) {
            if (!comment.IsObject || Text(comment, "id") is not { Length: > 0 } id) continue;
            var replyTo = comment.Prop("replyTo") is { } parent && parent.IsObject ? Text(parent, "id") : null;
            comments.Add(Comment(comment, id, replyTo));
        }
        return new(true, Number(connection, "totalCount") ?? comments.Count, HasNext(connection), EndCursor(connection), [.. comments]);
    }

    static int? Number(JsonElement element, string name) => element.Prop(name) is { } value && value.IsNumber && value.TryGetInt32(out var number) ? number : null;
    static bool HasNext(JsonElement connection) => connection.Prop("pageInfo") is { } info && info.IsObject && info.Bool("hasNextPage") == true;
    static string? EndCursor(JsonElement connection) => connection.Prop("pageInfo") is { } info && info.IsObject && Text(info, "endCursor") is { } cursor
        && GitHubCliRunner.ValidNodeId(cursor) ? cursor : null;
```

- [ ] **Step 5: Add the two sections to the provider**

In `PageAsync`, extend the `valid` switch:

```csharp
            "threads" => typeof(T) == typeof(PullRequestThreadDto), "thread_comments" => typeof(T) == typeof(PullRequestCommentDto),
```

and, right after the `if (!valid || cursor ...)` guard, before `var key = ...`, add:

```csharp
        if (resolved is not null && (section != "threads" || resolved is not ("unresolved" or "all"))) return Invalid<PullRequestPageDto<T>>(subject);
        if (section == "thread_comments" && !GitHubCliRunner.ValidNodeId(threadId)) return Invalid<PullRequestPageDto<T>>(subject);
        if (section == "threads") return (PullRequestRead<PullRequestPageDto<T>>)(object)await ThreadsAsync(subject, cursor, resolved ?? "unresolved", ct).ConfigureAwait(false);
        if (section == "thread_comments") return (PullRequestRead<PullRequestPageDto<T>>)(object)await ThreadCommentsAsync(subject, cursor, threadId!, ct).ConfigureAwait(false);
```

Add the two methods:

```csharp
    async Task<PullRequestRead<PullRequestPageDto<PullRequestThreadDto>>> ThreadsAsync(PullRequestSubjectDto subject, string? cursor, string resolved, CancellationToken ct) {
        var key = Key(subject) + "|threads|" + resolved;
        var started = _time.GetTimestamp();
        var entry = cursor is null ? new GitHubCliCursorEntry(GitHubCliCursors.NewHandle(), key, Now, null) : _cursors.Get(cursor);
        if (entry is null || entry.Key != key) return new(PullRequestReadKind.Restart, Subject: subject, Reason: "snapshot_expired");
        var after = entry.After;
        var items = new List<PullRequestThreadDto>();
        var excluded = 0; var total = 0; var hasNext = false; string? endCursor = null; string? head = entry.HeadSha;
        for (var fetches = 0; fetches < 10; fetches++) {
            string[] args = ["api", "graphql", "--hostname", subject.Host, "-f", "query=" + GitHubCliMapping.ThreadsQuery, "-f", "owner=" + subject.Owner,
                "-f", "repo=" + subject.RepoName, "-F", "number=" + subject.Number.ToString(CultureInfo.InvariantCulture)];
            if (after is not null) args = [.. args, "-f", "after=" + after];
            var result = await cli.RunAsync(args, ct).ConfigureAwait(false);
            if (result.Outcome != GitHubCliOutcome.Ok) return GitHubCliMapping.Failure<PullRequestPageDto<PullRequestThreadDto>>(result, subject, Now);
            var page = GitHubCliMapping.Threads(result.Stdout);
            if (page is null) return Invalid<PullRequestPageDto<PullRequestThreadDto>>(subject);
            if (!page.Found) return new(PullRequestReadKind.Unavailable, Subject: subject, Reason: "not_found", AccessFailure: "invalid");
            if (head is not null && page.HeadSha != head) return new(PullRequestReadKind.Restart, Subject: subject, Reason: "head_changed");
            head ??= page.HeadSha;
            total = page.Total; hasNext = page.HasNext; endCursor = page.EndCursor;
            foreach (var thread in page.Threads) {
                if (resolved == "unresolved" && thread.IsResolved == true) excluded++;
                else items.Add(thread);
            }
            if (items.Count > 0 || !hasNext || endCursor is null) break;
            after = endCursor;
        }
        if (cursor is null) { entry = entry with { HeadSha = head }; cursor = _cursors.Mint(entry); }
        var more = hasNext && endCursor is not null;
        var data = new PullRequestPageDto<PullRequestThreadDto> {
            SnapshotId = entry.SnapshotId, SnapshotStartedAt = entry.StartedAt, SnapshotCompletedAt = Now, Coverage = "complete", HeadSha = head,
            Total = resolved == "all" ? new() { Kind = "exact", Value = total } : new() { Kind = "unknown" }, ExcludedByFilter = new() { Kind = "exact", Value = excluded },
            Items = [.. items.Take(50)], PageCursor = cursor, HasMore = more, NextCursor = more ? _cursors.Mint(entry with { After = endCursor, HeadSha = head }) : null,
        };
        return Ready(data, subject, data.SnapshotCompletedAt, started);
    }

    async Task<PullRequestRead<PullRequestPageDto<PullRequestCommentDto>>> ThreadCommentsAsync(PullRequestSubjectDto subject, string? cursor, string threadId, CancellationToken ct) {
        var key = Key(subject) + "|thread_comments|" + threadId;
        var started = _time.GetTimestamp();
        var entry = cursor is null ? new GitHubCliCursorEntry(GitHubCliCursors.NewHandle(), key, Now, null) : _cursors.Get(cursor);
        if (entry is null || entry.Key != key) return new(PullRequestReadKind.Restart, Subject: subject, Reason: "snapshot_expired");
        string[] args = ["api", "graphql", "--hostname", subject.Host, "-f", "query=" + GitHubCliMapping.ThreadCommentsQuery, "-f", "id=" + threadId];
        if (entry.After is not null) args = [.. args, "-f", "after=" + entry.After];
        var result = await cli.RunAsync(args, ct).ConfigureAwait(false);
        if (result.Outcome != GitHubCliOutcome.Ok) return GitHubCliMapping.Failure<PullRequestPageDto<PullRequestCommentDto>>(result, subject, Now);
        var page = GitHubCliMapping.ThreadComments(result.Stdout);
        if (page is null) return Invalid<PullRequestPageDto<PullRequestCommentDto>>(subject);
        if (!page.Found) return new(PullRequestReadKind.Unavailable, Subject: subject, Reason: "not_found", AccessFailure: "invalid");
        cursor ??= _cursors.Mint(entry);
        var more = page.HasNext && page.EndCursor is not null && page.Comments.Length > 0;
        var data = new PullRequestPageDto<PullRequestCommentDto> {
            SnapshotId = entry.SnapshotId, SnapshotStartedAt = entry.StartedAt, SnapshotCompletedAt = Now, Coverage = "complete",
            Total = new() { Kind = "exact", Value = page.Total }, ExcludedByFilter = new() { Kind = "exact", Value = 0 },
            Items = [.. page.Comments.Take(50)], PageCursor = cursor, HasMore = more, NextCursor = more ? _cursors.Mint(entry with { After = page.EndCursor }) : null,
        };
        return Ready(data, subject, data.SnapshotCompletedAt, started);
    }
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/GitHubCli*/*"`
Expected: all passed, Tasks 2 to 4 included.

- [ ] **Step 7: Publish check and commit**

Run: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`
Expected: no output.

```bash
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/lazy-wandering-blum add src/Capacitor.Cli.Core/PullRequests/Readers/GitHubCli test/Capacitor.Cli.Core.Tests.Unit test/fixtures/gh
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/lazy-wandering-blum commit -m "Page inline threads and replies through gh api graphql (#813)"
```

### Task 6: Server provider adapter and app wiring

**Files:**
- Create: `src/Capacitor.App/Services/ServerReaderProvider.cs`
- Modify: `src/Capacitor.App/App.axaml.cs:366` (inside `BuildDaemonGraph`, where `pullRequests` is built) and `:404-406` (`BuildWorkspace`)
- Test: `test/Capacitor.App.Tests.Unit/ServerReaderProviderTests.cs`

**Interfaces:**
- Consumes: Task 1 contract, `ServerPullRequestSource` (untouched), `GitHubCliRunner`, `GitHubCliReaderProvider`, `PullRequestReaderRegistry`, `ProcessRunner`, `LoginShellProbe`.
- Produces: `ServerReaderProvider(ServerPullRequestSource source)`; the app hands a `PullRequestReaderRegistry` to every workspace as its `IPullRequestSource`.

- [ ] **Step 1: Write the failing adapter test**

```csharp
using System.Net;
using System.Text;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.PullRequests;
using Capacitor.Cli.Core.PullRequests.Readers;

namespace Capacitor.App.Tests.Unit;

public class ServerReaderProviderTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    static readonly PullRequestSubjectDto Subject = new() { Provider = "github", Host = "github.com", RepoHash = "hash", Owner = "example", RepoName = "repo", Number = 1 };

    [Test]
    public async Task Probe_maps_the_server_capability_and_serves_github_com_only_while_supported() {
        using var handler = new Handler { Versions = "[1]" };
        await using var source = new ServerPullRequestSource(Config.Root, Resolutions.At("https://server.test", Config.Root),
            (_, _, _, _) => Task.FromResult((new HttpClient(handler), AuthStatus.Ok)));
        var provider = new ServerReaderProvider(source);
        await Assert.That(provider.Name).IsEqualTo("server");
        await Assert.That(provider.ProviderKind).IsEqualTo("github");
        await Assert.That(provider.Tool).IsNull();
        await Assert.That(provider.Serves("github", "github.com")).IsFalse();
        var status = await provider.ProbeAsync(false, default);
        await Assert.That(status.Kind).IsEqualTo(PullRequestReaderStatusKind.Ready);
        await Assert.That(provider.Serves("github", "github.com")).IsTrue();
        await Assert.That(provider.Serves("github", "ghe.example")).IsFalse();
        await Assert.That(provider.ParseLink("https://github.com/example/repo/pull/1")).IsNull();
        await Assert.That(await provider.DiscoverAsync(new("github", "github.com", "example", "repo", "hash"), "feature", default)).IsEmpty();
        await Assert.That(provider.PrLink("https://github.com/example/repo/pull/1", Subject)).IsEqualTo("https://github.com/example/repo/pull/1");
        await Assert.That((await provider.OverviewAsync("session", Subject, default)).Kind).IsEqualTo(PullRequestReadKind.Ready);
    }

    [Test]
    public async Task An_older_server_probes_as_failed_with_its_capability_named() {
        using var handler = new Handler { Versions = null };
        await using var source = new ServerPullRequestSource(Config.Root, Resolutions.At("https://server.test", Config.Root),
            (_, _, _, _) => Task.FromResult((new HttpClient(handler), AuthStatus.Ok)));
        var provider = new ServerReaderProvider(source);
        var status = await provider.ProbeAsync(false, default);
        await Assert.That(status.Kind).IsEqualTo(PullRequestReaderStatusKind.Failed);
        await Assert.That(status.Reason).IsEqualTo("Legacy");
        await Assert.That(provider.Serves("github", "github.com")).IsFalse();
    }

    sealed class Handler : HttpMessageHandler {
        internal string? Versions;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            var discovery = request.RequestUri!.AbsolutePath == "/auth/config";
            var body = discovery ? (Versions is null ? """{"provider":"workos"}""" : $$$"""{"provider":"workos","pull_request_reads_versions":{{{Versions}}}}""")
                : """{"status":"ready","subject":{"provider":"github","host":"github.com","repo_hash":"hash","owner":"example","repo_name":"repo","number":1},"data":{"title":"Server PR"},"fetched_at":"2026-09-08T10:00:00Z","poll_after_seconds":30,"access_valid_for_seconds":30}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet build test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj 2>&1 | grep -E 'error' | head`
Expected: compile errors naming `ServerReaderProvider`.

- [ ] **Step 3: Write the adapter**

```csharp
using Capacitor.Cli.Core.PullRequests;
using Capacitor.Cli.Core.PullRequests.Readers;

namespace Capacitor.App.Services;

/// <summary>The server route as one reader among others. The same source also serves the registry's session links.</summary>
public sealed class ServerReaderProvider(ServerPullRequestSource source) : IPullRequestReaderProvider {
    PullRequestCapability _capability = new(PullRequestCapabilityKind.Unavailable, Reason: "not_probed");

    public string Name => "server";
    public string ProviderKind => "github";
    public PullRequestReaderTool? Tool => null;

    public async Task<PullRequestReaderStatus> ProbeAsync(bool refresh, CancellationToken ct) {
        _capability = await source.DiscoverAsync(refresh, ct).ConfigureAwait(false);
        return _capability.Kind == PullRequestCapabilityKind.Supported
            ? new(PullRequestReaderStatusKind.Ready) : new(PullRequestReaderStatusKind.Failed, _capability.Kind.ToString());
    }
    public bool Serves(string provider, string host) => provider == "github" && host == "github.com" && _capability.Kind == PullRequestCapabilityKind.Supported;
    public PullRequestSubjectDto? ParseLink(string? url) => null;
    public string? PrLink(string? url, PullRequestSubjectDto subject) => PullRequestWire.PrLink(url, subject);
    public Task<IReadOnlyList<PullRequestLinkDto>> DiscoverAsync(PullRequestRepository repository, string branch, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<PullRequestLinkDto>>([]);
    public Task<PullRequestRead<PullRequestOverviewDto>> OverviewAsync(string sessionId, PullRequestSubjectDto subject, CancellationToken ct)
        => source.OverviewAsync(sessionId, subject, ct);
    public Task<PullRequestRead<PullRequestPageDto<T>>> PageAsync<T>(string sessionId, PullRequestSubjectDto subject, string section,
        string? cursor, string? resolved, string? threadId, CancellationToken ct) where T : class
        => source.PageAsync<T>(sessionId, subject, section, cursor, resolved, threadId, ct);
    public void ResetSession(string sessionId) => source.ResetSession(sessionId);
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/ServerReaderProviderTests/*"`
Expected: 2 passed.

- [ ] **Step 5: Wire the registry in the app**

In `App.axaml.cs`, add the usings `Capacitor.Cli.Core.PullRequests.Readers;` and `Capacitor.Cli.Core.PullRequests.Readers.GitHubCli;`. Replace the line `var pullRequests = new ServerPullRequestSource(_config, profiles);` (around line 366) with:

```csharp
        var pullRequests = new ServerPullRequestSource(_config, profiles);
        var ghRunner = new ProcessRunner();
        var gh = new GitHubCliRunner(ghRunner, OperatingSystem.IsWindows() ? null : new LoginShellProbe(ghRunner, Environment.GetEnvironmentVariable), Environment.GetEnvironmentVariable);
        // Registration order is precedence: local CLI readers before the server.
        var readers = new PullRequestReaderRegistry(pullRequests, [new GitHubCliReaderProvider(gh), new ServerReaderProvider(pullRequests)]);
```

`ServerClients` keeps receiving `pullRequests` (it owns disposal and sign-in invalidation). In `BuildWorkspace` change `pullRequests: pullRequests` to `pullRequests: readers`.

Build the app: `dotnet build src/Capacitor.App/Capacitor.App.csproj 2>&1 | grep -E 'warning|error' | head` and expect no output.

- [ ] **Step 6: Commit**

```bash
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/lazy-wandering-blum add src/Capacitor.App/Services/ServerReaderProvider.cs src/Capacitor.App/App.axaml.cs test/Capacitor.App.Tests.Unit/ServerReaderProviderTests.cs
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/lazy-wandering-blum commit -m "Wire the reader registry with GitHub CLI and server providers (#813)"
```

### Task 7: View model: repository hint, session description, gates behind the registry

**Files:**
- Modify: `src/Capacitor.App/ViewModels/WorkContextViewModel.cs:27,207`, `WorkContextViewModel.Projections.cs:255-256`, `WorkspaceViewModel.cs:106`
- Modify: `src/Capacitor.App/ViewModels/PullRequestContextViewModel.cs` (fields, ctor, `OpenRowCommand`, `OpenGitHubCommand`), `PullRequestContextViewModel.Reads.cs` (`RequestRefresh`, `RequestOverview`), `PullRequestContextViewModel.Projections.cs` (`Reason`, `ToRow`)
- Modify: `test/Capacitor.App.Tests.Unit/PullRequestContextViewModelTests.cs` (`Harness` and the late-hint test)
- Create: `test/Capacitor.App.Tests.Unit/StubReaderProvider.cs`, `test/Capacitor.App.Tests.Unit/PullRequestContextViewModelRegistryTests.cs`

**Interfaces:**
- Consumes: `PullRequestRepository`, `IPullRequestReaders` (Task 1), `PullRequestReaderRegistry`.
- Produces: `WorkContextViewModel.PrimaryRepository` (`PullRequestRepository?`), the view-model constructor parameter `Func<PullRequestRepository?>? primaryRepo`, the private `PrLink(string?, PullRequestSubjectDto)` helper Task 8 also uses.

- [ ] **Step 1: Update the existing test harness and write the failing registry tests**

In `PullRequestContextViewModelTests.cs` change the harness constructor parameter type and the late-hint test:

```csharp
        internal Harness(Func<PullRequestRepository?>? primary = null) {
```

```csharp
    public Task A_late_primary_repository_hint_corrects_the_default_without_changing_an_explicit_choice() => RunOnUiAsync(async () => {
        PullRequestRepository? primary = null;
        var h = new Harness(() => primary);
        h.Source.Links[1] = h.Source.Links[1] with { RepoHash = "primary" };
        h.Push(); await h.Show();
        await Assert.That(h.Vm.Selected!.Subject.Number).IsEqualTo(1);
        primary = new("github", "github.com", "example", "repo", "primary");
```

Add `using Capacitor.Cli.Core.PullRequests.Readers;` to that file. Run `rtk proxy grep -rn 'PrimaryRepositoryHash' test/ src/Capacitor.App` and change every remaining reference to `PrimaryRepository` (asserting `?.RepoHash` where a test compared the hash).

`StubReaderProvider.cs`:

```csharp
using Capacitor.Cli.Core.PullRequests;
using Capacitor.Cli.Core.PullRequests.Readers;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

internal sealed class StubReaderProvider(FakeTimeProvider time, params string[] hosts) : IPullRequestReaderProvider {
    public PullRequestReaderStatusKind Status = PullRequestReaderStatusKind.Ready;
    public readonly List<(PullRequestRepository Repository, string Branch)> Discoveries = [];
    public PullRequestLinkDto[] Discovered = [];
    public string Name => "stub";
    public string ProviderKind => "github";
    public PullRequestReaderTool? Tool => new("GitHub CLI", "https://cli.github.com", host => host is null ? "gh auth login" : "gh auth login --hostname " + host);
    public Task<PullRequestReaderStatus> ProbeAsync(bool refresh, CancellationToken ct) => Task.FromResult(new PullRequestReaderStatus(Status));
    public bool Serves(string provider, string host) => Status == PullRequestReaderStatusKind.Ready && provider == "github" && hosts.Contains(host);
    public PullRequestSubjectDto? ParseLink(string? url) => null;
    public string? PrLink(string? url, PullRequestSubjectDto subject) => PullRequestWire.SafeLink(url) is { } safe
        && new Uri(safe).Host == subject.Host && new Uri(safe).AbsolutePath == $"/{subject.Owner}/{subject.RepoName}/pull/{subject.Number}" ? safe : null;
    public Task<IReadOnlyList<PullRequestLinkDto>> DiscoverAsync(PullRequestRepository repository, string branch, CancellationToken ct) {
        Discoveries.Add((repository, branch));
        return Task.FromResult<IReadOnlyList<PullRequestLinkDto>>(Discovered);
    }
    public Task<PullRequestRead<PullRequestOverviewDto>> OverviewAsync(string sessionId, PullRequestSubjectDto subject, CancellationToken ct)
        => Task.FromResult(new PullRequestRead<PullRequestOverviewDto>(PullRequestReadKind.Ready,
            new() { Title = "Local PR", Description = "Local description", HeadSha = new string('a', 40), Lifecycle = "open" },
            subject, time.GetUtcNow().UtcDateTime, AccessValidForSeconds: 30, RequestStarted: time.GetTimestamp()));
    public Task<PullRequestRead<PullRequestPageDto<T>>> PageAsync<T>(string sessionId, PullRequestSubjectDto subject, string section,
        string? cursor, string? resolved, string? threadId, CancellationToken ct) where T : class
        => Task.FromResult(new PullRequestRead<PullRequestPageDto<T>>(PullRequestReadKind.Unavailable, Subject: subject, Reason: "tool_failed", AccessFailure: "transient"));
    public void ResetSession(string sessionId) { }
}
```

`PullRequestContextViewModelRegistryTests.cs`:

```csharp
using System.Reactive.Subjects;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.PullRequests;
using Capacitor.Cli.Core.PullRequests.Readers;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

[NotInParallel("AvaloniaSession")]
public class PullRequestContextViewModelRegistryTests {
    static PullRequestLinkDto Link(string host, int number) => new() { Provider = "github", Host = host, RepoHash = "hash", Owner = "example", RepoName = "repo",
        Number = number, Url = $"https://{host}/example/repo/pull/{number}", Title = "Linked PR", HeadRef = "feature" };

    [Test]
    public Task A_subject_on_an_enterprise_host_reads_and_opens_through_the_registry() => RunOnUiAsync(async () => {
        var h = new Harness("ghe.example");
        h.Links.Links = [Link("ghe.example", 4)];
        h.Push(); await h.Show();
        await Assert.That(h.Vm.Title).IsEqualTo("Local PR");
        h.Vm.SetReaderVisible(true);
        await Assert.That(h.Vm.Description).IsEqualTo("Local description");
        await h.Vm.OpenGitHubCommand.Execute();
        await Assert.That(h.Opener.Opened).IsEquivalentTo(new[] { "https://ghe.example/example/repo/pull/4" });
        await h.Dispose();
    });

    [Test]
    public Task The_session_is_described_to_the_registry_so_live_discovery_can_run() => RunOnUiAsync(async () => {
        var h = new Harness("github.com", new PullRequestRepository("github", "github.com", "example", "repo", "hash"));
        h.Provider.Discovered = [Link("github.com", 9)];
        h.Push(); await h.Show();
        await Assert.That(h.Provider.Discoveries.Count).IsEqualTo(1);
        await Assert.That(h.Provider.Discoveries[0].Branch).IsEqualTo("feature");
        await Assert.That(h.Provider.Discoveries[0].Repository.Owner).IsEqualTo("example");
        await Assert.That(h.Vm.Choices.Select(choice => choice.Subject.Number).ToArray()).IsEquivalentTo(new[] { 1, 2, 9 });
        await h.Dispose();
    });

    [Test]
    public Task A_subject_no_provider_serves_shows_the_no_reader_notice_without_the_capacitor_sign_in() => RunOnUiAsync(async () => {
        var h = new Harness("github.com");
        h.Links.Links = [Link("ghe.example", 4)];
        h.Push(); h.Vm.SetForeground(true);
        await WaitUntilAsync(() => !h.Vm.IsReading && h.Vm.HasChoice, what: "list applied");
        await Assert.That(h.Vm.CanReveal).IsFalse();
        await Assert.That(h.Vm.Notice).IsEqualTo("No reader is available for this pull request's host.");
        await Assert.That(h.Vm.ShowsSignIn).IsFalse();
        await h.Dispose();
    });

    sealed class Harness {
        internal BehaviorSubject<AgentStatusDto?> Presence { get; } = new(null);
        internal FakeTimeProvider Time { get; } = new();
        internal FakePullRequestSource Links { get; }
        internal StubReaderProvider Provider { get; }
        internal PullRequestReaderRegistry Registry { get; }
        internal RecordingOpener Opener { get; } = new();
        internal PullRequestContextViewModel Vm { get; }
        internal Harness(string host, PullRequestRepository? primary = null) {
            Links = new(Time);
            Provider = new(Time, host);
            Registry = new(Links, [Provider]);
            Vm = new(Presence, Registry, Time, Opener, () => { }, primaryRepo: () => primary);
        }
        internal void Push() => Presence.OnNext(Agent("agent", "claude", hasTerminal: false, sessionId: "session", branch: "feature"));
        internal async Task Show() { Vm.SetForeground(true); await WaitUntilAsync(() => Vm.CanReveal, what: "PR overview admitted"); }
        internal async Task Dispose() { await Vm.TeardownAsync(); Presence.Dispose(); }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet build test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj 2>&1 | grep -E 'error' | head`
Expected: type errors on the `Func<PullRequestRepository?>` parameter.

- [ ] **Step 3: Grow the primary-repository hint**

`WorkContextViewModel.cs` line 27: replace `public string? PrimaryRepositoryHash { get; private set; }` with

```csharp
    public PullRequestRepository? PrimaryRepository { get; private set; }
```

and add `using Capacitor.Cli.Core.PullRequests.Readers;`. Line 207: `PrimaryRepository = null;`.

`WorkContextViewModel.Projections.cs` lines 255-256:

```csharp
        var primaryRepositories = summary.Repositories.Where(repository => repository.IsPrimary).ToArray();
        // The summary names no host, so a session repository is assumed to be github.com until a link says otherwise.
        PrimaryRepository = primaryRepositories.Length == 1
            ? new("github", "github.com", primaryRepositories[0].Owner, primaryRepositories[0].RepoName, primaryRepositories[0].RepoHash) : null;
```

`WorkspaceViewModel.cs` line 106: `() => WorkContext.PrimaryRepository`.

- [ ] **Step 4: Move the gates behind the registry**

`PullRequestContextViewModel.cs`:

```csharp
    readonly Func<PullRequestRepository?>? _primaryRepo;
    readonly IPullRequestReaders? _readers;
```

Constructor signature: `Func<PullRequestRepository?>? primaryRepo = null`, then after `_primaryRepo = primaryRepo;` add `_readers = source as IPullRequestReaders;`. Add `using Capacitor.Cli.Core.PullRequests.Readers;`.

Replace the two commands:

```csharp
        OpenRowCommand = ReactiveCommand.Create<PullRequestRow>(row => {
            if (CanDisplayReader) LinkPolicy.Open(_opener, row.IsCheck ? PullRequestWire.CheckLink(row.Url) : _selected is null ? null : PrLink(row.Url, _selected.Subject));
        });
        OpenGitHubCommand = ReactiveCommand.Create(() => {
            if (_selected is { IsAvailable: true } choice) LinkPolicy.Open(_opener, PrLink(choice.Link.Url, choice.Subject));
        });
```

Add the helper beside `SetNotice`:

```csharp
    string? PrLink(string? url, PullRequestSubjectDto subject) => _readers is not null ? _readers.PrLink(url, subject)
        : PullRequestWire.IsGitHub(subject) ? PullRequestWire.PrLink(url, subject) : PullRequestWire.SafeLink(url);
```

`PullRequestContextViewModel.Reads.cs`, in `RequestRefresh` before `Start(async ct => {`:

```csharp
        var repository = _primaryRepo?.Invoke();
        var branch = _branch;
```

and as the first line inside the lambda:

```csharp
            _readers?.DescribeSession(session, repository, branch);
```

In the default-selection block replace the `primary` lines with:

```csharp
                    var primary = _primaryRepo?.Invoke()?.RepoHash;
```

(the rest of that expression is unchanged). In `RequestOverview` delete the line beginning `if (!PullRequestWire.IsGitHub(choice.Subject))`.

`PullRequestContextViewModel.Projections.cs`: make the five `ToRow` methods instance methods (drop `static`) and replace every `PullRequestWire.PrLink(item.Url, subject)` with `PrLink(item.Url, subject)`. Extend `Reason`'s inner switch with, before the default arm:

```csharp
            "no_reader" => "No reader is available for this pull request's host.",
            "not_found" => "This pull request could not be found.",
            "tool_signed_out" => "The local CLI is not signed in for this host. Sign in and refresh.",
            "tool_denied" => "Your account cannot read this pull request.",
            "tool_failed" => "The local CLI could not read this pull request. Refresh to try again.",
```

- [ ] **Step 5: Run the app tests**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/PullRequest*/*"`
Expected: all passed, including the existing `PullRequestContextViewModelTests` and `PullRequestViewSmokeTests`.

- [ ] **Step 6: Commit**

```bash
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/lazy-wandering-blum add src/Capacitor.App/ViewModels test/Capacitor.App.Tests.Unit
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/lazy-wandering-blum commit -m "Route the PR view model's host gate and links through the registry (#813)"
```

### Task 8: The card's reader note

**Files:**
- Modify: `src/Capacitor.App/ViewModels/PullRequestContextViewModel.cs` (note state and command), `PullRequestContextViewModel.Projections.cs` (`Notify`)
- Modify: `src/Capacitor.App/Views/PullRequestCard.axaml` (note text and two buttons)
- Modify: `test/Capacitor.App.Tests.Unit/PullRequestContextViewModelRegistryTests.cs` (append the note tests)

**Interfaces:**
- Consumes: `IPullRequestReaders.NoteFor(provider, host)`, `PullRequestReaderNote`, `LinkPolicy.Open`.
- Produces: view-model members `ReaderNote`, `HasReaderNote`, `ShowsInstallTool`, `InstallToolLabel`, `InstallToolCommand`.

- [ ] **Step 1: Append the failing note tests**

Inside `PullRequestContextViewModelRegistryTests`, before `sealed class Harness`:

```csharp
    static readonly PullRequestRepository Primary = new("github", "github.com", "example", "repo", "hash");

    [Test]
    public Task A_missing_tool_shows_the_install_note_before_any_pr_is_linked() => RunOnUiAsync(async () => {
        var h = new Harness("github.com", Primary);
        h.Provider.Status = PullRequestReaderStatusKind.ToolMissing;
        h.Links.Links = [];
        h.Push(); h.Vm.SetForeground(true);
        await WaitUntilAsync(() => !h.Vm.IsReading && h.Vm.HasReaderNote, what: "note shown");
        await Assert.That(h.Vm.ReaderNote).IsEqualTo("Install GitHub CLI to read pull requests here.");
        await Assert.That(h.Vm.ShowsInstallTool).IsTrue();
        await Assert.That(h.Vm.InstallToolLabel).IsEqualTo("Install GitHub CLI");
        await Assert.That(h.Vm.ShowsSignIn).IsFalse();
        await Assert.That(h.Vm.ShowsLinkGitHub).IsFalse();
        await h.Vm.InstallToolCommand.Execute();
        await Assert.That(h.Opener.Opened).IsEquivalentTo(new[] { "https://cli.github.com" });
        await h.Dispose();
    });

    [Test]
    public Task A_signed_out_tool_names_the_sign_in_command_and_recheck_clears_the_note_once_ready() => RunOnUiAsync(async () => {
        var h = new Harness("github.com", Primary);
        h.Provider.Status = PullRequestReaderStatusKind.SignedOut;
        h.Push(); h.Vm.SetForeground(true);
        await WaitUntilAsync(() => !h.Vm.IsReading && h.Vm.HasReaderNote, what: "note shown");
        await Assert.That(h.Vm.ReaderNote).IsEqualTo("GitHub CLI is not signed in. Run gh auth login to read pull requests here.");
        await Assert.That(h.Vm.ShowsInstallTool).IsFalse();
        h.Provider.Status = PullRequestReaderStatusKind.Ready;
        h.Time.Advance(TimeSpan.FromSeconds(16));
        await h.Vm.RefreshCommand.Execute();
        await WaitUntilAsync(() => h.Vm.CanReveal, what: "rechecked and reading");
        await Assert.That(h.Vm.HasReaderNote).IsFalse();
        await h.Dispose();
    });

    [Test]
    public Task A_selected_pr_on_a_host_the_tool_is_not_signed_in_to_names_that_host() => RunOnUiAsync(async () => {
        var h = new Harness("github.com", Primary);
        h.Links.Links = [Link("ghe.example", 4)];
        h.Push(); h.Vm.SetForeground(true);
        await WaitUntilAsync(() => !h.Vm.IsReading && h.Vm.HasChoice, what: "list applied");
        await Assert.That(h.Vm.ReaderNote).IsEqualTo("GitHub CLI is not signed in for ghe.example. Run gh auth login --hostname ghe.example to read it here.");
        await Assert.That(h.Vm.ShowsInstallTool).IsFalse();
        await h.Dispose();
    });

    [Test]
    public Task No_note_shows_while_a_provider_serves_the_session_host() => RunOnUiAsync(async () => {
        var h = new Harness("github.com", Primary);
        h.Push(); await h.Show();
        await Assert.That(h.Vm.HasReaderNote).IsFalse();
        await h.Dispose();
    });
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet build test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj 2>&1 | grep -E 'error' | head`
Expected: errors naming `HasReaderNote`.

- [ ] **Step 3: Add the note to the view model**

`PullRequestContextViewModel.cs`, fields and properties beside `Notice`:

```csharp
    PullRequestReaderNote? _readerNote;
    public string ReaderNote => _readerNote?.Text ?? "";
    public bool HasReaderNote => _readerNote is not null;
    public bool ShowsInstallTool => _readerNote?.InstallUrl is not null;
    public string InstallToolLabel => _readerNote is null ? "" : "Install " + _readerNote.ToolName;
    public ReactiveCommand<Unit, Unit> InstallToolCommand { get; }
```

In the constructor, after `LinkGitHubCommand = ...`:

```csharp
        InstallToolCommand = ReactiveCommand.Create(() => LinkPolicy.Open(_opener, _readerNote?.InstallUrl));
        _subscriptions.Add(InstallToolCommand);
```

`PullRequestContextViewModel.Projections.cs`, at the top of `Notify()`:

```csharp
        _readerNote = _readers is null ? null
            : _selected?.Subject is { } subject ? _readers.NoteFor(subject.Provider, subject.Host)
            : _primaryRepo?.Invoke() is { } repository ? _readers.NoteFor(repository.Provider, repository.Host) : null;
```

and add `nameof(ReaderNote), nameof(HasReaderNote), nameof(ShowsInstallTool), nameof(InstallToolLabel)` to the property array it raises.

- [ ] **Step 4: Add the note to the card**

In `PullRequestCard.axaml`, after the `Notice` `TextBlock`:

```xml
            <TextBlock Text="{Binding ReaderNote}" IsVisible="{Binding HasReaderNote}" FontSize="11.5" TextWrapping="Wrap"
                       Foreground="{StaticResource KcapMutedBrush}" />
            <StackPanel Orientation="Horizontal" Spacing="7" IsVisible="{Binding HasReaderNote}">
                <Button Content="{Binding InstallToolLabel}" Command="{Binding InstallToolCommand}" IsVisible="{Binding ShowsInstallTool}" Padding="10,5" FontSize="11.5" />
                <Button Content="Recheck" Command="{Binding RefreshCommand}" Padding="10,5" FontSize="11.5" />
            </StackPanel>
```

- [ ] **Step 5: Run the app tests and a full app build**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/PullRequest*/*"`
Expected: all passed.

Run: `dotnet build src/Capacitor.App/Capacitor.App.csproj --no-incremental 2>&1 | grep -E 'warning|error' | head`
Expected: no output (AVLN XAML warnings count).

- [ ] **Step 6: Commit**

```bash
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/lazy-wandering-blum add src/Capacitor.App test/Capacitor.App.Tests.Unit
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/lazy-wandering-blum commit -m "Tell the user what to install or sign in to on the PR card (#813)"
```

---

### Task 9: Live check, docs, and the pull request

**Files:**
- Create: `test/Capacitor.Cli.Core.Tests.Unit/PullRequests/Readers/GitHubCli/GitHubCliLiveTests.cs`
- Modify: `README.md` (`## Requirements` list), `docs/CHANGES.md` (new entry after the header paragraphs), `docs/superpowers/specs/2026-09-08-local-pr-context-design.md` (GraphQL argument line)

- [ ] **Step 1: Write the opt-in live test**

```csharp
using Capacitor.Cli.Core.PullRequests;
using Capacitor.Cli.Core.PullRequests.Readers.GitHubCli;

namespace Capacitor.Cli.Core.Tests.Unit.PullRequests.Readers.GitHubCli;

/// <summary>Runs the real <c>gh</c> against a public PR when it is installed and signed in; skipped otherwise, so CI without a sign-in stays green.</summary>
public class GitHubCliLiveTests {
    [Test]
    public async Task The_installed_gh_reads_a_public_pull_request_end_to_end() {
        var runner = new GitHubCliRunner(new ProcessRunner(), null, Environment.GetEnvironmentVariable);
        if (await runner.LocateAsync(false, default) is null) Skip.Test("GitHub CLI is not installed");
        var provider = new GitHubCliReaderProvider(runner);
        var status = await provider.ProbeAsync(false, default);
        if (!provider.Serves("github", "github.com")) Skip.Test($"GitHub CLI is not signed in to github.com ({status.Kind})");
        var subject = new PullRequestSubjectDto { Provider = "github", Host = "github.com", RepoHash = RepoHashHelper.ComputeRepoHash("kurrent-io", "kcap-cli"),
            Owner = "kurrent-io", RepoName = "kcap-cli", Number = 812 };
        var overview = await provider.OverviewAsync("session", subject, default);
        await Assert.That(overview.Kind).IsEqualTo(PullRequestReadKind.Ready);
        await Assert.That(overview.Data!.Lifecycle).IsEqualTo("merged");
        await Assert.That(overview.Data.Title).IsEqualTo("Read linked pull requests in the desktop workspace");
        var checks = await provider.PageAsync<PullRequestCheckDto>("session", subject, "checks", null, null, null, default);
        await Assert.That(checks.Data!.Items.Length).IsGreaterThan(0);
        var threads = await provider.PageAsync<PullRequestThreadDto>("session", subject, "threads", null, "all", null, default);
        await Assert.That(threads.Kind).IsEqualTo(PullRequestReadKind.Ready);
        await Assert.That(threads.Data!.Items.Length).IsGreaterThan(0);
        var replies = await provider.PageAsync<PullRequestCommentDto>("session", subject, "thread_comments", null, null, threads.Data.Items[0].Id, default);
        await Assert.That(replies.Kind).IsEqualTo(PullRequestReadKind.Ready);
    }
}
```

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/GitHubCliLiveTests/*"`
Expected: passed on a signed-in machine, skipped with the reason otherwise.

- [ ] **Step 2: README**

In `README.md` under `## Requirements`, after the "At least one supported coding agent" bullet, add:

```markdown
- **GitHub CLI (`gh`), optional** — the desktop app reads a linked pull request (description, checks, reviews, inline threads, conversation) through your own `gh` sign-in when it is installed and signed in, including GitHub Enterprise hosts. Without it the card says what to install; a tenant with the server-side GitHub App enabled reads without it.
```

- [ ] **Step 3: CHANGES.md**

Insert after the two header paragraphs, before `## Read a linked pull request inside the workspace`:

```markdown
## Read pull requests through the local GitHub CLI

The desktop reads a linked pull request through the user's own `gh` when it is
installed and signed in, and falls back to the server route otherwise. Reading
sits behind a registry of reader providers, each declaring the provider kind and
hosts it serves; a read routes to the first ready provider for that PR's host,
local CLI providers before the server. Session links stay a server concern.

The user's own sign-in is the authorization, so there is no linked-user gate and
the access window is a constant 30 seconds that keeps the existing masking and
renewal logic unchanged. `gh` is spawned with an argument array, a fixed
environment overlay, a 20-second deadline and a 4 MiB output cap, and every
identifier is validated before a spawn. Snapshot ids and cursors are minted
locally; whole sections page in fifties over a frozen array, threads page over
the GraphQL connection and restart on a head change.

The PR card carries a provider-generic note naming what to install or sign in
to. A GitLab provider is one new type plus one registration line. See the
[design](superpowers/specs/2026-09-08-local-pr-context-design.md).
```

- [ ] **Step 4: Align the spec with the implementation**

In `docs/superpowers/specs/2026-09-08-local-pr-context-design.md`, the runner's call table row for inline threads becomes:

```markdown
| Inline threads and replies | `gh api graphql --hostname <host> -f query=<fixed query> -f owner=… -f repo=… -F number=… -f after=…` |
```

and the sentence after the table becomes: "GraphQL variables are always passed as separate arguments, strings with `-f` and the PR number with `-F`, never interpolated into the query text."

- [ ] **Step 5: Full verification**

```bash
dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj 2>&1 | grep -E 'warning|error' | head
dotnet build src/Capacitor.App/Capacitor.App.csproj --no-incremental 2>&1 | grep -E 'warning|error' | head
dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'
dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/PullRequest*/*"
dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/GitHubCli*/*"
dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj
bash scripts/check-linear-ids.sh
```

Expected: no warnings, no IL warnings, every suite green, no Linear ids.

- [ ] **Step 6: Commit and open the pull request**

```bash
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/lazy-wandering-blum add README.md docs/CHANGES.md docs/superpowers/specs/2026-09-08-local-pr-context-design.md docs/superpowers/plans/2026-09-08-local-pr-context.md test/Capacitor.Cli.Core.Tests.Unit/PullRequests/Readers/GitHubCli/GitHubCliLiveTests.cs
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/lazy-wandering-blum commit -m "Document local PR reading and add the live gh check (#813)"
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/lazy-wandering-blum push https://github.com/kurrent-io/kcap-cli.git feat/local-pr-reader
```

Open `.github/PULL_REQUEST_TEMPLATE.md` and write the description to its comment block. Title: `Read pull requests through the local GitHub CLI behind a reader-provider registry`. The reference line carries `Closes #813` and the Linear key Linear assigned when it imported issue #813: find it with the Linear MCP `list_issues` tool, query `Read pull requests through the local GitHub CLI`. Create the PR with `gh pr create --base main --head feat/local-pr-reader --title ... --body-file <file>`, then register the session with `declare_work_item` using `pr_number`.
