# Desktop Remote Daemons — Slice 1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The desktop app shows agents hosted on the user's other machines' daemons (aggregated into the rail and tray) and can launch on a chosen machine with truthful launch-failure feedback — spec §9 slice 1.

**Architecture:** A new zero-dependency contracts project mirrors the server's UI-client wire shapes. A long-lived `ServerConnectionService` (absorbing today's per-launch `ServerLaunchClient`) holds one authenticated SignalR connection and exposes broadcasts + invokes. `RemoteAgentsService` seeds/refreshes remote agent + daemon caches over HTTP/hub, and `AgentDirectory` merges them with the local daemon's cache into source-scoped `AgentRow`s (fail-open dedup by `(MachineId, Name)` twin match). The rail groups by repository identity; the tray aggregates both lanes; the launcher gains a machine picker restricted to the signed-in user's own daemons plus launch-outcome correlation.

**Tech Stack:** .NET 10 (NativeAOT), Avalonia + ReactiveUI + DynamicData, Microsoft.AspNetCore.SignalR.Client, System.Text.Json source-gen, TUnit + WireMock.Net.

**Spec:** `docs/superpowers/specs/2026-09-06-ai2371-desktop-remote-daemons-design.md` — read it before starting any task; every design rule referenced below (twin match, fail-open, grouping, gating) is normative there. GitHub issue #708 / Linear AI-2371.

## Global Constraints

- **AOT:** no reflective JSON anywhere — every wire type gets `[JsonPropertyName]` on every member and rides a source-generated `JsonSerializerContext` (the `ILaunchClient.cs` precedent). After code changes run `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` — must print nothing.
- **Wire names are the contract**: snake_case, pinned explicitly, never derived from a naming policy. SignalR hub method arity is frozen — never add a parameter to an existing method name.
- **Fail-open dedup** (spec sub-decision): when twin identity is uncertain, show duplicate rows; never hide an agent.
- **Comments:** scarce, per CLAUDE.md `## Comments` — no change narration, no spec-section coordinates in code ("spec §3" style references exist in old files; do NOT add new ones), no review artifacts.
- **Commits:** subject `one clause (#708)`, imperative, ≤80 chars total. Body only for a non-obvious constraint, ≤5 lines. End with `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.
- **Tests:** TUnit on Microsoft Testing Platform. Run one suite: `dotnet run --project test/<Proj>/<Proj>.csproj -- --treenode-filter "/*/*/<ClassName>/*"` (glob filter, NOT `--filter`). Full run: `dotnet test --solution Capacitor.slnx`. One test project per prod project; shared fixtures go to `test/Capacitor.Tests.Helpers` with `public` surface. Throwaway dirs via Helpers' `TempDir`.
- **Env-var absence must never be asserted** on a built `ProcessStartInfo`; console capture via `ConsoleOutput.StartCapture()` + bare `[NotInParallel]`.
- The app builds with `dotnet build src/Capacitor.App/Capacitor.App.csproj` — build it (not just Cli) after every app change; XAML (AVLN) warnings only appear on a full rebuild of the app project and must be fixed in the same commit.

---

### Task 1: Contracts project `Capacitor.Remote.Models`

**Files:**
- Create: `src/Capacitor.Remote.Models/Capacitor.Remote.Models.csproj`
- Create: `src/Capacitor.Remote.Models/AgentInstanceDto.cs`
- Create: `src/Capacitor.Remote.Models/DaemonInfo.cs`
- Create: `src/Capacitor.Remote.Models/RemoteWire.cs`
- Create: `src/Capacitor.Remote.Models/RemoteModelsJsonContext.cs`
- Create: `test/Capacitor.Remote.Models.Tests.Unit/Capacitor.Remote.Models.Tests.Unit.csproj`
- Create: `test/Capacitor.Remote.Models.Tests.Unit/WireShapeTests.cs`
- Modify: `Capacitor.slnx` (add both projects to the `/src/` and `/test/` folders)
- Modify: `src/Capacitor.App/Capacitor.App.csproj` (add `<ProjectReference Include="..\Capacitor.Remote.Models\Capacitor.Remote.Models.csproj" />` next to the Cli.Core reference)

**Interfaces:**
- Consumes: nothing (leaf project, zero dependencies).
- Produces: `Capacitor.Remote.Models.AgentInstanceDto`, `Capacitor.Remote.Models.DaemonInfo`, `Capacitor.Remote.Models.AccessGrant`, static `HubMethods`, `HubBroadcasts`, `ApiRoutes`, `SpecialKeys`, `WireTokens`, and `RemoteModelsJsonContext`. Every later task uses these exact names.

- [ ] **Step 1: Create the project file**

`src/Capacitor.Remote.Models/Capacitor.Remote.Models.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
        <TargetFramework>net10.0</TargetFramework>
        <IsPackable>false</IsPackable>
    </PropertyGroup>
    <ItemGroup>
        <InternalsVisibleTo Include="Capacitor.Remote.Models.Tests.Unit" />
    </ItemGroup>
</Project>
```

Add to `Capacitor.slnx` under the `/src/` folder: `<Project Path="src\Capacitor.Remote.Models\Capacitor.Remote.Models.csproj" />` (alphabetical position is fine).

- [ ] **Step 2: Write the DTOs**

`src/Capacitor.Remote.Models/AgentInstanceDto.cs`. The header comment carries the two wire rules the spec mandates for this project (frozen arity, names-are-the-contract) — that is a live constraint, not narration, so it passes the comment test:

```csharp
using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// One agent instance as the server's UI-facing wire presents it (HTTP GET api/agent-instances
/// and hub payloads alike). Property NAMES are the contract on both transports — pinned
/// explicitly so no serializer policy can move them — and the server ignores members it does not
/// know, so additions here are always trailing and nullable.
public sealed record AgentInstanceDto {
    [JsonPropertyName("agent_id")]          public required string AgentId { get; init; }
    [JsonPropertyName("session_id")]        public string? SessionId { get; init; }
    [JsonPropertyName("status")]            public required string Status { get; init; }
    [JsonPropertyName("prompt")]            public string? Prompt { get; init; }
    [JsonPropertyName("model")]             public string? Model { get; init; }
    [JsonPropertyName("effort")]            public string? Effort { get; init; }
    [JsonPropertyName("repo_path")]         public string? RepoPath { get; init; }
    [JsonPropertyName("client_connected")]  public bool ClientConnected { get; init; }
    [JsonPropertyName("registered_at")]     public DateTime RegisteredAt { get; init; }
    [JsonPropertyName("repo_owner")]        public string? RepoOwner { get; init; }
    [JsonPropertyName("repo_name")]         public string? RepoName { get; init; }
    [JsonPropertyName("repo_hash")]         public string? RepoHash { get; init; }
    [JsonPropertyName("pr_number")]         public int? PrNumber { get; init; }
    [JsonPropertyName("pr_url")]            public string? PrUrl { get; init; }
    [JsonPropertyName("pr_title")]          public string? PrTitle { get; init; }
    [JsonPropertyName("failure_reason")]    public string? FailureReason { get; init; }
    [JsonPropertyName("owner_user_id")]     public string? OwnerUserId { get; init; }
    [JsonPropertyName("visibility_mode")]   public string? VisibilityMode { get; init; }
    [JsonPropertyName("grants")]            public AccessGrant[]? Grants { get; init; }
    [JsonPropertyName("vendor")]            public string? Vendor { get; init; }
    [JsonPropertyName("ended_at")]          public DateTime? EndedAt { get; init; }
    [JsonPropertyName("status_changed_at")] public DateTime? StatusChangedAt { get; init; }
    [JsonPropertyName("sandbox_policy")]    public string? SandboxPolicy { get; init; }
    [JsonPropertyName("approval_policy")]   public string? ApprovalPolicy { get; init; }
    [JsonPropertyName("daemon_name")]       public string? DaemonName { get; init; }
    [JsonPropertyName("permission_preset")] public string? PermissionPreset { get; init; }
}

public sealed record AccessGrant {
    [JsonPropertyName("grant_type")]   public required string GrantType { get; init; }
    [JsonPropertyName("grantee_id")]   public required string GranteeId { get; init; }
    [JsonPropertyName("grantee_name")] public required string GranteeName { get; init; }
}
```

`src/Capacitor.Remote.Models/DaemonInfo.cs`:

```csharp
using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// One connected daemon as the server's registry presents it (hub GetConnectedDaemons and HTTP
/// GET api/daemons). Daemon names are unique only per OwnerUserId; MachineId is null from a
/// daemon that predates it. Every vendor list is null-when-unknown, never empty-when-unknown.
public sealed record DaemonInfo {
    [JsonPropertyName("name")]                    public required string Name { get; init; }
    [JsonPropertyName("platform")]                public string? Platform { get; init; }
    [JsonPropertyName("repo_paths")]              public string[]? RepoPaths { get; init; }
    [JsonPropertyName("max_agents")]              public int MaxAgents { get; init; }
    [JsonPropertyName("active_agents")]           public int ActiveAgents { get; init; }
    [JsonPropertyName("connected")]               public bool Connected { get; init; }
    [JsonPropertyName("connected_at")]            public DateTime? ConnectedAt { get; init; }
    [JsonPropertyName("owner_user_id")]           public string? OwnerUserId { get; init; }
    [JsonPropertyName("version")]                 public string? Version { get; init; }
    [JsonPropertyName("supported_vendors")]       public string[]? SupportedVendors { get; init; }
    [JsonPropertyName("machine_id")]              public string? MachineId { get; init; }
    [JsonPropertyName("unattended_vendors")]      public string[]? UnattendedVendors { get; init; }
    [JsonPropertyName("pr_review_vendors")]       public string[]? PrReviewVendors { get; init; }
    [JsonPropertyName("acp_preset_vendors")]      public string[]? AcpPresetVendors { get; init; }
    [JsonPropertyName("permission_mode_vendors")] public string[]? PermissionModeVendors { get; init; }
}
```

- [ ] **Step 3: Write the wire-name constants**

`src/Capacitor.Remote.Models/RemoteWire.cs`:

```csharp
namespace Capacitor.Remote.Models;

/// Hub methods a UI client invokes. SignalR JSON does not backfill missing trailing arguments,
/// so every method's arity is frozen — new capability means a NEW method taking one record.
public static class HubMethods {
    public const string GetConnectedDaemons    = "GetConnectedDaemons";
    public const string RequestLaunchAgentV2   = "RequestLaunchAgentV2";
    public const string RequestStopAgent       = "RequestStopAgent";
    public const string SendUserInput          = "SendUserInput";
    public const string SendSpecialKey         = "SendSpecialKey";
    public const string SubscribeToTerminal    = "SubscribeToTerminal";
    public const string UnsubscribeFromTerminal = "UnsubscribeFromTerminal";
    public const string RequestResizeTerminal  = "RequestResizeTerminal";
    public const string ReleaseResizeTerminal  = "ReleaseResizeTerminal";
    public const string SubscribeToChat        = "SubscribeToChat";
    public const string UnsubscribeFromChat    = "UnsubscribeFromChat";
    public const string SubscribeToAcpEphemeral = "SubscribeToAcpEphemeral";
    public const string SubscribeToStream      = "SubscribeToStream";
    public const string RegisterSessionAccessWatch = "RegisterSessionAccessWatch";
    public const string ResolveAttribution     = "ResolveAttribution";
}

/// Server → UI-client pushes. Org-wide ones arrive with no join call; the rest are group-scoped.
public static class HubBroadcasts {
    public const string AgentInstancesChanged  = "AgentInstancesChanged";
    public const string DaemonsChanged         = "DaemonsChanged";
    public const string LaunchFailed           = "LaunchFailed";
    public const string PermissionPending      = "PermissionPending";
    public const string PermissionResponded    = "PermissionResponded";
    public const string PermissionRequested    = "PermissionRequested";
    public const string AcpElicitationRequested = "AcpElicitationRequested";
    public const string PendingInputChanged    = "PendingInputChanged";
    public const string TerminalOutput         = "TerminalOutput";
    public const string TerminalDimensions     = "TerminalDimensions";
    public const string SessionTitleChanged    = "SessionTitleChanged";
    public const string ActiveSessionAdded     = "ActiveSessionAdded";
    public const string ActiveSessionChanged   = "ActiveSessionChanged";
    public const string ActiveSessionRemoved   = "ActiveSessionRemoved";
    public const string SessionAccessChanged   = "SessionAccessChanged";
}

public static class ApiRoutes {
    public const string AgentInstances = "api/agent-instances";
    public const string Daemons        = "api/daemons";
    public static string SessionDetail(string sessionId) =>
        $"api/sessions/{Uri.EscapeDataString(sessionId)}/detail";
    public static string PermissionResponse(string sessionId, string requestId) =>
        $"api/sessions/{Uri.EscapeDataString(sessionId)}/permission-response/{Uri.EscapeDataString(requestId)}";
}

/// The daemon's fixed special-key vocabulary — anything else is a server-side no-op.
public static class SpecialKeys {
    public const string Escape = "Escape";
    public const string Tab = "Tab";
    public const string Enter = "Enter";
    public const string CtrlC = "CtrlC";
    public const string ArrowUp = "ArrowUp";
    public const string ArrowDown = "ArrowDown";
    public const string ShiftTab = "ShiftTab";
    public static readonly string[] All = [Escape, Tab, Enter, CtrlC, ArrowUp, ArrowDown, ShiftTab];
}

/// Literal tokens compared against wire values.
public static class WireTokens {
    /// LaunchFailed reason prefix for a consent-gate denial on the target machine.
    public const string LaunchDeniedByOwnerPrefix = "launch_denied_by_owner";
}
```

- [ ] **Step 4: Write the JSON context**

`src/Capacitor.Remote.Models/RemoteModelsJsonContext.cs`:

```csharp
using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

[JsonSerializable(typeof(AgentInstanceDto))]
[JsonSerializable(typeof(AgentInstanceDto[]))]
[JsonSerializable(typeof(DaemonInfo))]
[JsonSerializable(typeof(List<DaemonInfo>))]
public partial class RemoteModelsJsonContext : JsonSerializerContext;
```

- [ ] **Step 5: Create the test project**

`test/Capacitor.Remote.Models.Tests.Unit/Capacitor.Remote.Models.Tests.Unit.csproj` — copy the shape of `test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj` exactly (open it first; it carries the TUnit wiring and the Helpers reference), changing only the `ProjectReference` to `..\..\src\Capacitor.Remote.Models\Capacitor.Remote.Models.csproj`. Add to `Capacitor.slnx` under `/test/`. `test/Directory.Build.props` applies automatically.

- [ ] **Step 6: Write the failing wire-shape tests**

`test/Capacitor.Remote.Models.Tests.Unit/WireShapeTests.cs`:

```csharp
using System.Text.Json;
using Capacitor.Remote.Models;

namespace Capacitor.Remote.Models.Tests.Unit;

public class WireShapeTests {
    // The property name IS the wire contract: deserialize a captured server-shaped payload and
    // pin every field, so a rename on our side fails here before it fails against a live server.
    [Test]
    public async Task AgentInstanceRoundTripsSnakeCase() {
        const string json = """
        {"agent_id":"a1","session_id":"s1","status":"Running","prompt":"fix the bug","model":"m",
         "effort":"high","repo_path":"/work/repo","client_connected":true,
         "registered_at":"2026-09-06T10:00:00Z","repo_owner":"kurrent-io","repo_name":"kcap-cli",
         "repo_hash":"abc","pr_number":7,"pr_url":"https://x","pr_title":"t","failure_reason":null,
         "owner_user_id":"u1","visibility_mode":"private",
         "grants":[{"grant_type":"user","grantee_id":"g1","grantee_name":"G"}],
         "vendor":"claude","ended_at":null,"status_changed_at":"2026-09-06T10:05:00Z",
         "sandbox_policy":"sp","approval_policy":"ap","daemon_name":"work-mac","permission_preset":"pp"}
        """;
        var dto = JsonSerializer.Deserialize(json, RemoteModelsJsonContext.Default.AgentInstanceDto)!;
        await Assert.That(dto.AgentId).IsEqualTo("a1");
        await Assert.That(dto.OwnerUserId).IsEqualTo("u1");
        await Assert.That(dto.DaemonName).IsEqualTo("work-mac");
        await Assert.That(dto.RepoOwner).IsEqualTo("kurrent-io");
        await Assert.That(dto.Grants![0].GranteeId).IsEqualTo("g1");

        var back = JsonSerializer.Serialize(dto, RemoteModelsJsonContext.Default.AgentInstanceDto);
        await Assert.That(back).Contains("\"agent_id\"");
        await Assert.That(back).Contains("\"owner_user_id\"");
        await Assert.That(back).Contains("\"daemon_name\"");
    }

    [Test]
    public async Task DaemonInfoRoundTripsSnakeCase() {
        const string json = """
        {"name":"work-mac","platform":"osx","repo_paths":["/work/repo"],"max_agents":5,
         "active_agents":1,"connected":true,"connected_at":"2026-09-06T09:00:00Z",
         "owner_user_id":"u1","version":"1.2.3","supported_vendors":["claude","codex"],
         "machine_id":"m-abc","unattended_vendors":["codex"],"pr_review_vendors":null,
         "acp_preset_vendors":null,"permission_mode_vendors":["claude"]}
        """;
        var dto = JsonSerializer.Deserialize(json, RemoteModelsJsonContext.Default.DaemonInfo)!;
        await Assert.That(dto.Name).IsEqualTo("work-mac");
        await Assert.That(dto.MachineId).IsEqualTo("m-abc");
        await Assert.That(dto.OwnerUserId).IsEqualTo("u1");
        await Assert.That(dto.SupportedVendors).IsEquivalentTo(new[] { "claude", "codex" });
        var back = JsonSerializer.Serialize(dto, RemoteModelsJsonContext.Default.DaemonInfo);
        await Assert.That(back).Contains("\"machine_id\"");
        await Assert.That(back).Contains("\"owner_user_id\"");
    }

    [Test]
    public async Task UnknownServerFieldsAreIgnored() {
        const string json = """{"agent_id":"a1","status":"Running","brand_new_field":42}""";
        var dto = JsonSerializer.Deserialize(json, RemoteModelsJsonContext.Default.AgentInstanceDto)!;
        await Assert.That(dto.AgentId).IsEqualTo("a1");
    }

    [Test]
    public async Task PermissionResponseRouteEscapesBothIds() {
        var route = ApiRoutes.PermissionResponse("s/1", "r 2");
        await Assert.That(route).IsEqualTo("api/sessions/s%2F1/permission-response/r%202");
    }
}
```

- [ ] **Step 7: Run the tests to verify they fail** (project doesn't build yet without the sources, then passes once they do)

Run: `dotnet run --project test/Capacitor.Remote.Models.Tests.Unit/Capacitor.Remote.Models.Tests.Unit.csproj`
Expected first: compile errors if any source is missing; then all 4 PASS.

- [ ] **Step 8: Build the App project with its new reference; commit**

Run: `dotnet build src/Capacitor.App/Capacitor.App.csproj` (expect success, zero warnings), then:

```bash
git add src/Capacitor.Remote.Models test/Capacitor.Remote.Models.Tests.Unit Capacitor.slnx src/Capacitor.App/Capacitor.App.csproj
git commit -m "Add the Capacitor.Remote.Models UI-client wire contract project (#708)"
```

---

### Task 2: JWT claim reader for viewer identity

**Files:**
- Create: `src/Capacitor.Cli.Core/Auth/JwtClaims.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Auth/JwtClaimsTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `Capacitor.Cli.Core.Auth.JwtClaims.TryGetString(string accessToken, string claimName) : string?`. Later tasks read `"sub"` (viewer identity) and `"team_id"` (silent-deafness diagnostic).

- [ ] **Step 1: Write the failing tests**

`test/Capacitor.Cli.Core.Tests.Unit/Auth/JwtClaimsTests.cs`:

```csharp
using System.Text;
using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

public class JwtClaimsTests {
    static string Token(string payloadJson) {
        static string B64Url(string s) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{B64Url("{\"alg\":\"RS256\"}")}.{B64Url(payloadJson)}.sig";
    }

    [Test]
    public async Task ReadsAStringClaim() =>
        await Assert.That(JwtClaims.TryGetString(Token("""{"sub":"user_123","team_id":"t9"}"""), "sub"))
            .IsEqualTo("user_123");

    [Test]
    public async Task MissingClaimIsNull() =>
        await Assert.That(JwtClaims.TryGetString(Token("""{"sub":"user_123"}"""), "team_id")).IsNull();

    [Test]
    public async Task NonStringClaimIsNull() =>
        await Assert.That(JwtClaims.TryGetString(Token("""{"n":42}"""), "n")).IsNull();

    [Test]
    [Arguments("")]
    [Arguments("not-a-jwt")]
    [Arguments("a.b")]
    [Arguments("a.%%%.c")]
    public async Task GarbageIsNullNeverThrows(string token) =>
        await Assert.That(JwtClaims.TryGetString(token, "sub")).IsNull();

    [Test]
    public async Task PayloadNeedingBase64PaddingParses() {
        // A payload whose base64url length % 4 == 2 exercises the padding branch.
        var token = Token("""{"sub":"ab"}""");
        await Assert.That(JwtClaims.TryGetString(token, "sub")).IsEqualTo("ab");
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/JwtClaimsTests/*"`
Expected: compile error — `JwtClaims` not defined.

- [ ] **Step 3: Implement**

`src/Capacitor.Cli.Core/Auth/JwtClaims.cs`:

```csharp
using System.Text.Json;

namespace Capacitor.Cli.Core.Auth;

/// Reads one string claim out of a JWT payload WITHOUT validating the signature — the server
/// validates via JWKS; a client only ever uses these values for display and row classification,
/// never for authorization.
public static class JwtClaims {
    public static string? TryGetString(string accessToken, string claimName) {
        var parts = accessToken.Split('.');
        if (parts.Length < 2) return null;

        try {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = (payload.Length % 4) switch {
                2 => payload + "==",
                3 => payload + "=",
                1 => throw new FormatException("truncated base64url"),
                _ => payload,
            };
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            return doc.RootElement.TryGetProperty(claimName, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        } catch (Exception e) when (e is FormatException or JsonException) {
            return null;
        }
    }
}
```

- [ ] **Step 4: Run to verify pass**

Same command as Step 2. Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/Auth/JwtClaims.cs test/Capacitor.Cli.Core.Tests.Unit/Auth/JwtClaimsTests.cs
git commit -m "Add an unvalidated JWT string-claim reader (#708)"
```

---

### Task 3: In-process SignalR hub host for app tests

**Files:**
- Create: `test/Capacitor.App.Tests.Unit/HubTestHost.cs`
- Modify: `test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj` (add `<FrameworkReference Include="Microsoft.AspNetCore.App" />` inside an `<ItemGroup>`)
- Test: `test/Capacitor.App.Tests.Unit/HubTestHostTests.cs`

**Interfaces:**
- Consumes: `Capacitor.Remote.Models.DaemonInfo`, `HubMethods`.
- Produces (used by Tasks 4, 5, 11):
  - `HubTestHost : IAsyncDisposable` with `static Task<HubTestHost> StartAsync()`, `string Url` (e.g. `http://127.0.0.1:{port}`), `Task BroadcastAsync(string method, params object?[] args)`, settable `Func<List<DaemonInfo>> DaemonsHandler`, `Func<object, string> LaunchHandler` (receives the raw launch payload as `JsonElement`, returns an agent id or throws `HubException`), `int LaunchCalls`, `Task StopAsync()` (kills the server to drive reconnect tests).

- [ ] **Step 1: Add the framework reference and write the host**

The host runs a minimal ASP.NET Core server with one hub mapped at `/hubs/sessions`, snake_case JSON like the real server. `test/Capacitor.App.Tests.Unit/HubTestHost.cs`:

```csharp
using System.Text.Json;
using Capacitor.Remote.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Capacitor.App.Tests.Unit;

/// A scriptable stand-in for the server's sessions hub: loopback Kestrel on an OS-assigned
/// port, snake_case hub JSON (the real server's policy), no auth. Handlers are static because
/// SignalR constructs a fresh hub instance per invocation.
public sealed class HubTestHost : IAsyncDisposable {
    WebApplication? _app;
    public string Url { get; private set; } = "";

    public static Func<List<DaemonInfo>> DaemonsHandler = () => [];
    public static Func<JsonElement, string> LaunchHandler = _ => "agent-1";
    public static int LaunchCalls;

    public static async Task<HubTestHost> StartAsync() {
        DaemonsHandler = () => [];
        LaunchHandler = _ => "agent-1";
        LaunchCalls = 0;

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSignalR().AddJsonProtocol(o =>
            o.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);

        var app = builder.Build();
        app.MapHub<SessionsHub>("/hubs/sessions");
        await app.StartAsync();

        var host = new HubTestHost { _app = app };
        host.Url = app.Urls.First();
        return host;
    }

    public Task BroadcastAsync(string method, params object?[] args) =>
        _app!.Services.GetRequiredService<IHubContext<SessionsHub>>()
            .Clients.All.SendCoreAsync(method, args);

    public Task StopAsync() => _app!.StopAsync();

    public async ValueTask DisposeAsync() {
        if (_app is null) return;
        await _app.StopAsync();
        await _app.DisposeAsync();
        _app = null;
    }

    public sealed class SessionsHub : Hub {
        public List<DaemonInfo> GetConnectedDaemons() => DaemonsHandler();

        public string RequestLaunchAgentV2(JsonElement payload) {
            Interlocked.Increment(ref LaunchCalls);
            return LaunchHandler(payload);
        }
    }
}
```

In `test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj`, add:

```xml
<ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
</ItemGroup>
```

- [ ] **Step 2: Write the failing smoke test**

`test/Capacitor.App.Tests.Unit/HubTestHostTests.cs`:

```csharp
using Capacitor.Remote.Models;
using Microsoft.AspNetCore.SignalR.Client;

namespace Capacitor.App.Tests.Unit;

// Static scripted handlers make host state process-global.
[NotInParallel(nameof(HubTestHost))]
public class HubTestHostTests {
    [Test]
    public async Task ClientCanInvokeAndReceiveBroadcasts() {
        await using var host = await HubTestHost.StartAsync();
        HubTestHost.DaemonsHandler = () => [new DaemonInfo { Name = "work-mac", Connected = true }];

        await using var hub = new HubConnectionBuilder().WithUrl($"{host.Url}/hubs/sessions").Build();
        var changed = new TaskCompletionSource();
        hub.On(HubBroadcasts.DaemonsChanged, () => changed.TrySetResult());
        await hub.StartAsync();

        var daemons = await hub.InvokeAsync<List<DaemonInfo>>(HubMethods.GetConnectedDaemons);
        await Assert.That(daemons[0].Name).IsEqualTo("work-mac");

        await host.BroadcastAsync(HubBroadcasts.DaemonsChanged);
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
```

- [ ] **Step 3: Run, fix, pass**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/HubTestHostTests/*"`
Expected: PASS (after the FrameworkReference lands; a missing reference fails compile with unknown `WebApplication`).

- [ ] **Step 4: Commit**

```bash
git add test/Capacitor.App.Tests.Unit/HubTestHost.cs test/Capacitor.App.Tests.Unit/HubTestHostTests.cs test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj
git commit -m "Add a scriptable in-process sessions-hub host for app tests (#708)"
```

---

### Task 4: `ServerConnectionService` — the lane core

**Files:**
- Create: `src/Capacitor.App/Services/IServerLane.cs`
- Create: `src/Capacitor.App/Services/ServerConnectionService.cs`
- Test: `test/Capacitor.App.Tests.Unit/ServerConnectionServiceTests.cs`

**Interfaces:**
- Consumes: `HubTestHost` (Task 3), `JwtClaims` (Task 2), `Capacitor.Remote.Models` (Task 1), existing `TokenStore.GetValidTokensForServerAsync(profileName, serverUrl)` and `ProfileContext` (`profiles.Name`, `profiles.Resolution.ServerUrl`) — the exact pattern in `src/Capacitor.App/Services/ServerLaunchClient.cs:55-72`.
- Produces:

```csharp
public enum ServerLaneState { Dormant, Connecting, Connected, Retrying, SignedOut }

/// Diagnostic is the silent-deafness notice text, or null.
public sealed record ServerLaneStatus(ServerLaneState State, string? Detail = null, string? Diagnostic = null);

public sealed record LaunchFailure(string AgentId, string Reason);

public interface IServerLane {
    /// Replay-1; initial value (Dormant) published synchronously at construction.
    IObservable<ServerLaneStatus> Status { get; }
    IObservable<System.Reactive.Unit> AgentInstancesChanged { get; }
    IObservable<System.Reactive.Unit> DaemonsChanged { get; }
    IObservable<LaunchFailure> LaunchFailures { get; }
    /// Null when the lane has no live connection right now.
    Task<IReadOnlyList<Capacitor.Remote.Models.DaemonInfo>?> GetConnectedDaemonsAsync(CancellationToken ct);
}
```

  plus `ServerConnectionService : IServerLane, IAsyncDisposable` with `void Start()`, `Task RestartAsync(CancellationToken ct = default)`, and an internal ctor seam for tests: `internal ServerConnectionService(string? serverUrl, Func<Task<string?>> accessTokenProvider)` and the production ctor `public ServerConnectionService(ConfigRoot config, ProfileContext? profiles)` that derives both (null/absent server → permanently `Dormant`).

**Behavior to implement (all pinned by tests below):**
1. `Start()` is a no-op when dormant. Otherwise it runs a background connect loop: build a `HubConnection` to `{serverUrl}/hubs/sessions` with `options.AccessTokenProvider = accessTokenProvider` and `.AddJsonProtocol(o => o.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower)` plus `.WithAutomaticReconnect()`; register `On` handlers for `HubBroadcasts.AgentInstancesChanged` (0 args), `DaemonsChanged` (0 args), `LaunchFailed` (`(string agentId, string reason)`) BEFORE `StartAsync` — handlers registered after a start can miss pushes.
2. A failed or closed connection re-dials on the ladder 1/2/5/10/30s then 30s forever (`Status` shows `Retrying` with the exception message as `Detail`) — SignalR's automatic reconnect does not cover cold-start failure or a server-initiated close without reconnect, so the manual loop owns those.
3. On connect: publish `Connected`. If the access token is readable and `JwtClaims.TryGetString(token, "team_id")` is null, set `Diagnostic` to `ServerConnectionService.TeamClaimMissingNotice` (`const string` = `"Signed-in token carries no team claim — server broadcasts may not reach this app."`); otherwise `Diagnostic` stays null. The diagnostic is informational only (footer tooltip in Task 12), never an error state — a dedicated tenant's token may legitimately lack the claim.
4. `GetConnectedDaemonsAsync`: invoke `HubMethods.GetConnectedDaemons` returning `List<DaemonInfo>` on the live hub; return null (never throw) when not connected or on invoke failure.
5. `RestartAsync`: tear down the current connection and dial fresh — the sign-in-completed hook (a fresh `AccessTokenProvider` read picks up new tokens).
6. Thread-safety: one connect loop at a time, `SemaphoreSlim(1,1)` gating loop restarts, mirroring `DaemonClientService.RestartLoopAsync`'s single-flight shape (`src/Capacitor.App/Services/DaemonClientService.cs:80-99`).

- [ ] **Step 1: Write the failing tests**

`test/Capacitor.App.Tests.Unit/ServerConnectionServiceTests.cs`:

```csharp
using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.Remote.Models;

namespace Capacitor.App.Tests.Unit;

[NotInParallel(nameof(HubTestHost))]
public class ServerConnectionServiceTests {
    static ServerConnectionService Lane(HubTestHost host, string? token = null) =>
        new(host.Url, () => Task.FromResult(token));

    static async Task<T> Next<T>(IObservable<T> source, Func<T, bool> match, int seconds = 10) =>
        await source.Where(match).Take(1).ToTask().WaitAsync(TimeSpan.FromSeconds(seconds));

    [Test]
    public async Task ConnectsAndServesDaemons() {
        await using var host = await HubTestHost.StartAsync();
        HubTestHost.DaemonsHandler = () => [new DaemonInfo { Name = "work-mac", Connected = true }];
        await using var lane = Lane(host);
        lane.Start();

        await Next(lane.Status, s => s.State == ServerLaneState.Connected);
        var daemons = await lane.GetConnectedDaemonsAsync(CancellationToken.None);
        await Assert.That(daemons![0].Name).IsEqualTo("work-mac");
    }

    [Test]
    public async Task BroadcastsSurfaceAsObservables() {
        await using var host = await HubTestHost.StartAsync();
        await using var lane = Lane(host);
        lane.Start();
        await Next(lane.Status, s => s.State == ServerLaneState.Connected);

        var agentsPing = lane.AgentInstancesChanged.Take(1).ToTask();
        var failure    = lane.LaunchFailures.Take(1).ToTask();
        await host.BroadcastAsync(HubBroadcasts.AgentInstancesChanged);
        await host.BroadcastAsync(HubBroadcasts.LaunchFailed, "a1", "launch_denied_by_owner: default");
        await agentsPing.WaitAsync(TimeSpan.FromSeconds(10));
        var f = await failure.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(f.AgentId).IsEqualTo("a1");
        await Assert.That(f.Reason).Contains("launch_denied_by_owner");
    }

    [Test]
    public async Task NoServerMeansDormantForever() {
        await using var lane = new ServerConnectionService(serverUrl: null, () => Task.FromResult<string?>(null));
        lane.Start();
        var status = await lane.Status.Take(1).ToTask();
        await Assert.That(status.State).IsEqualTo(ServerLaneState.Dormant);
        await Assert.That(await lane.GetConnectedDaemonsAsync(CancellationToken.None)).IsNull();
    }

    [Test]
    public async Task ColdStartFailureRetriesUntilServerAppears() {
        // Reserve a port by starting and stopping a host, then start the lane against the dead
        // URL — it must sit in Retrying, then connect once a server appears... but the OS may
        // reassign the port. Instead: start lane against a fresh host, kill it, watch Retrying,
        // then verify the lane recovers when broadcasting resumes is NOT possible on a new port —
        // so this test pins only: dead server → Retrying with no throw.
        await using var host = await HubTestHost.StartAsync();
        var url = host.Url;
        await host.StopAsync();

        await using var lane = new ServerConnectionService(url, () => Task.FromResult<string?>(null));
        lane.Start();
        await Next(lane.Status, s => s.State == ServerLaneState.Retrying, seconds: 15);
    }

    [Test]
    public async Task MissingTeamClaimSetsDiagnostic() {
        await using var host = await HubTestHost.StartAsync();
        // "sub" only — no team_id. Header/payload/sig shape per JwtClaimsTests.
        const string token = "eyJhbGciOiJub25lIn0.eyJzdWIiOiJ1MSJ9.s";
        await using var lane = Lane(host, token);
        lane.Start();
        var status = await Next(lane.Status, s => s.State == ServerLaneState.Connected);
        await Assert.That(status.Diagnostic).IsEqualTo(ServerConnectionService.TeamClaimMissingNotice);
    }

    [Test]
    public async Task RestartReconnects() {
        await using var host = await HubTestHost.StartAsync();
        await using var lane = Lane(host);
        lane.Start();
        await Next(lane.Status, s => s.State == ServerLaneState.Connected);
        await lane.RestartAsync();
        await Next(lane.Status, s => s.State == ServerLaneState.Connected);
    }
}
```

- [ ] **Step 2: Run to verify compile failure** (`IServerLane`/`ServerConnectionService` undefined)

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/ServerConnectionServiceTests/*"`

- [ ] **Step 3: Implement `IServerLane.cs` (the records/interface above verbatim) and `ServerConnectionService.cs`**

`src/Capacitor.App/Services/ServerConnectionService.cs` — the essential skeleton (fill nothing in later; this is the implementation):

```csharp
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Remote.Models;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Capacitor.App.Services;

/// The app's one long-lived server connection. Handlers are registered before StartAsync so no
/// broadcast can slip past a fresh connection; a closed or cold-failed connection re-dials on a
/// 1/2/5/10/30s ladder because SignalR's automatic reconnect covers neither cold-start failure
/// nor a close it decides not to retry.
public sealed class ServerConnectionService : IServerLane, IAsyncDisposable {
    public const string TeamClaimMissingNotice =
        "Signed-in token carries no team claim — server broadcasts may not reach this app.";

    static readonly TimeSpan[] Backoff =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    readonly string? _serverUrl;
    readonly Func<Task<string?>> _token;
    readonly BehaviorSubject<ServerLaneStatus> _status = new(new(ServerLaneState.Dormant));
    readonly Subject<Unit> _agentsChanged = new();
    readonly Subject<Unit> _daemonsChanged = new();
    readonly Subject<LaunchFailure> _launchFailures = new();
    readonly SemaphoreSlim _restartGate = new(1, 1);
    readonly CancellationTokenSource _lifetime = new();
    CancellationTokenSource? _loopCts;
    Task _loop = Task.CompletedTask;
    volatile HubConnection? _hub;

    public ServerConnectionService(ConfigRoot config, ProfileContext? profiles)
        : this(
            profiles?.Resolution.ServerUrl,
            profiles is null
                ? () => Task.FromResult<string?>(null)
                : async () => (await new TokenStore(config).GetValidTokensForServerAsync(
                    profiles.Name, profiles.Resolution.ServerUrl!)).Tokens?.AccessToken) { }

    internal ServerConnectionService(string? serverUrl, Func<Task<string?>> accessTokenProvider) {
        _serverUrl = string.IsNullOrEmpty(serverUrl) ? null : serverUrl.TrimEnd('/');
        _token = accessTokenProvider;
    }

    public IObservable<ServerLaneStatus> Status => _status.AsObservable();
    public IObservable<Unit> AgentInstancesChanged => _agentsChanged.AsObservable();
    public IObservable<Unit> DaemonsChanged => _daemonsChanged.AsObservable();
    public IObservable<LaunchFailure> LaunchFailures => _launchFailures.AsObservable();

    public void Start() {
        if (_serverUrl is null) return;
        _ = RestartAsync();
    }

    public async Task RestartAsync(CancellationToken ct = default) {
        if (_serverUrl is null || _lifetime.IsCancellationRequested) return;
        await _restartGate.WaitAsync(ct).ConfigureAwait(false);
        try {
            if (_lifetime.IsCancellationRequested) return;
            _loopCts?.Cancel();
            await AwaitQuietly(_loop).ConfigureAwait(false);
            _loopCts?.Dispose();
            _loopCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _loop = Task.Run(() => RunAsync(_loopCts.Token));
        } finally {
            _restartGate.Release();
        }
    }

    async Task RunAsync(CancellationToken ct) {
        var attempt = 0;
        while (!ct.IsCancellationRequested) {
            _status.OnNext(new(ServerLaneState.Connecting));
            HubConnection? hub = null;
            try {
                hub = Build();
                await hub.StartAsync(ct).ConfigureAwait(false);
                _hub = hub;
                attempt = 0;
                _status.OnNext(new(ServerLaneState.Connected, Diagnostic: await DiagnoseAsync().ConfigureAwait(false)));

                var closed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
                hub.Closed += ex => { closed.TrySetResult(ex); return Task.CompletedTask;  };
                hub.Reconnecting += _ => { _status.OnNext(new(ServerLaneState.Retrying, "reconnecting")); return Task.CompletedTask; };
                hub.Reconnected += async _ =>
                    _status.OnNext(new(ServerLaneState.Connected, Diagnostic: await DiagnoseAsync().ConfigureAwait(false)));

                await using (ct.Register(() => closed.TrySetResult(null)))
                    await closed.Task.ConfigureAwait(false);
            } catch (OperationCanceledException) {
                break;
            } catch (Exception ex) {
                _status.OnNext(new(ServerLaneState.Retrying, ex.Message));
            } finally {
                _hub = null;
                if (hub is not null) await hub.DisposeAsync().ConfigureAwait(false);
            }

            if (ct.IsCancellationRequested) break;
            var delay = Backoff[Math.Min(attempt++, Backoff.Length - 1)];
            try { await Task.Delay(delay, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }
    }

    HubConnection Build() {
        var hub = new HubConnectionBuilder()
            .WithUrl($"{_serverUrl}/hubs/sessions", o => o.AccessTokenProvider = _token)
            .WithAutomaticReconnect()
            .AddJsonProtocol(o => o.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower)
            .Build();
        hub.On(HubBroadcasts.AgentInstancesChanged, () => _agentsChanged.OnNext(Unit.Default));
        hub.On(HubBroadcasts.DaemonsChanged, () => _daemonsChanged.OnNext(Unit.Default));
        hub.On<string, string>(HubBroadcasts.LaunchFailed, (agentId, reason) => _launchFailures.OnNext(new(agentId, reason)));
        return hub;
    }

    async Task<string?> DiagnoseAsync() {
        try {
            var token = await _token().ConfigureAwait(false);
            return token is not null && JwtClaims.TryGetString(token, "team_id") is null
                ? TeamClaimMissingNotice
                : null;
        } catch {
            return null;
        }
    }

    public async Task<IReadOnlyList<DaemonInfo>?> GetConnectedDaemonsAsync(CancellationToken ct) {
        var hub = _hub;
        if (hub is not { State: HubConnectionState.Connected }) return null;
        try {
            return await hub.InvokeAsync<List<DaemonInfo>>(HubMethods.GetConnectedDaemons, ct).ConfigureAwait(false);
        } catch (Exception) {
            return null;
        }
    }

    static async Task AwaitQuietly(Task t) {
        try { await t.ConfigureAwait(false); } catch { }
    }

    public async ValueTask DisposeAsync() {
        _lifetime.Cancel();
        await _restartGate.WaitAsync().ConfigureAwait(false);
        try {
            _loopCts?.Cancel();
            await AwaitQuietly(_loop).ConfigureAwait(false);
            _loopCts?.Dispose();
            _loopCts = null;
        } finally {
            _restartGate.Release();
        }
        _status.Dispose();
        _agentsChanged.Dispose();
        _daemonsChanged.Dispose();
        _launchFailures.Dispose();
        _restartGate.Dispose();
    }
}
```

Note the `AddJsonProtocol` naming-policy approach differs from `LaunchHubJson`'s source-gen context: the hub-side DTOs (`DaemonInfo`) carry explicit `[JsonPropertyName]`, so the policy is belt-and-braces; the launch payload keeps its own context when Task 5 moves it here. AOT check in Task 13 verifies nothing reflective slipped in — if `AddJsonProtocol` requires a `TypeInfoResolver` for AOT, chain `o.PayloadSerializerOptions.TypeInfoResolverChain.Add(RemoteModelsJsonContext.Default)` and `.Add(LaunchJsonContext.Default)`.

- [ ] **Step 4: Run the tests to verify pass**

Same command as Step 2. Expected: all 6 PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.App/Services/IServerLane.cs src/Capacitor.App/Services/ServerConnectionService.cs test/Capacitor.App.Tests.Unit/ServerConnectionServiceTests.cs
git commit -m "Add the app's long-lived server lane service (#708)"
```

---

### Task 5: Launch moves onto the lane; `ServerLaunchClient` retires

**Files:**
- Modify: `src/Capacitor.App/Services/ServerConnectionService.cs` (implement `ILaunchClient`)
- Delete: `src/Capacitor.App/Services/ServerLaunchClient.cs`
- Modify: `src/Capacitor.App/App.axaml.cs` (composition — see steps)
- Test: `test/Capacitor.App.Tests.Unit/ServerConnectionServiceTests.cs` (extend); existing `LaunchRequestTests.cs` must keep passing untouched.

**Interfaces:**
- Consumes: `ILaunchClient`, `LaunchRequest`, `LaunchOutcome`, `LaunchPayload`, `LaunchHubJson` — all stay in `src/Capacitor.App/Services/ILaunchClient.cs`, unchanged.
- Produces: `ServerConnectionService : ILaunchClient`. `StartAsync(LaunchRequest, ct)` keeps `ServerLaunchClient`'s exact outcome semantics: `HubException` text becomes `LaunchOutcome.Error`; a chained `HttpRequestException` with 401 anywhere sets `Unauthorized: true` (move `ServerLaunchClient.IsUnauthorized` here verbatim as `internal static bool IsUnauthorized(Exception ex)`).

- [ ] **Step 1: Extend the tests**

Append to `ServerConnectionServiceTests.cs`:

```csharp
    [Test]
    public async Task LaunchInvokesOverTheSharedConnection() {
        await using var host = await HubTestHost.StartAsync();
        HubTestHost.LaunchHandler = payload => {
            // The payload arrives with the pinned snake_case names whatever the policy does.
            if (!payload.TryGetProperty("daemon_name", out var d) || d.GetString() != "work-mac")
                throw new InvalidOperationException("daemon_name missing");
            return "agent-42";
        };
        await using var lane = Lane(host);
        lane.Start();
        await Next(lane.Status, s => s.State == ServerLaneState.Connected);

        var outcome = await ((ILaunchClient)lane).StartAsync(
            new LaunchRequest("work-mac", "/work/repo", "claude", "do it"), CancellationToken.None);
        await Assert.That(outcome.Started).IsTrue();
        await Assert.That(outcome.AgentId).IsEqualTo("agent-42");
        await Assert.That(HubTestHost.LaunchCalls).IsEqualTo(1);
    }

    [Test]
    public async Task LaunchWhileDisconnectedFailsWithoutThrowing() {
        await using var lane = new ServerConnectionService("http://127.0.0.1:1", () => Task.FromResult<string?>(null));
        lane.Start();
        var outcome = await ((ILaunchClient)lane).StartAsync(
            new LaunchRequest("d", "/r", "claude", null), CancellationToken.None);
        await Assert.That(outcome.Started).IsFalse();
        await Assert.That(outcome.Error).IsNotNull();
    }
```

Run the class filter; expect the two new tests to fail (no `ILaunchClient` on the lane).

- [ ] **Step 2: Implement launch on the lane**

Add to `ServerConnectionService` (interface list gains `ILaunchClient`):

```csharp
    public async Task<LaunchOutcome> StartAsync(LaunchRequest request, CancellationToken ct) {
        try {
            var hub = _hub;
            if (hub is not { State: HubConnectionState.Connected })
                return new LaunchOutcome(false, null, "Not connected to the server.");
            var agentId = await hub.InvokeAsync<string>(
                HubMethods.RequestLaunchAgentV2, LaunchPayload.For(request), ct).ConfigureAwait(false);
            return new LaunchOutcome(Started: true, AgentId: agentId, Error: null);
        } catch (Exception ex) {
            return new LaunchOutcome(false, null, ex.Message, IsUnauthorized(ex));
        }
    }

    internal static bool IsUnauthorized(Exception ex) {
        for (Exception? e = ex; e is not null; e = e.InnerException) {
            if (e is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized }) return true;
        }
        return false;
    }
```

The launch payload serializes through the naming policy; its members carry explicit `[JsonPropertyName]`, so the wire names hold either way. If the protocol options need the source-gen resolver for AOT, chain `LaunchJsonContext.Default` as noted in Task 4.

- [ ] **Step 3: Delete `ServerLaunchClient.cs` and rewire composition**

In `src/Capacitor.App/App.axaml.cs`:
- Field `ServerLaunchClient? _launch;` (line ~104) → `ServerConnectionService? _serverLane;`.
- In `BuildDaemonGraph` (lines ~327-334), replace

```csharp
        var launch = new ServerLaunchClient(_config, profiles);
        var workContext = new ServerWorkContextSource(_config, profiles);
        var serverClients = new ServerClients(launch, workContext);
        _launch = launch;
```

  with

```csharp
        var serverLane = new ServerConnectionService(_config, profiles);
        serverLane.Start();
        var workContext = new ServerWorkContextSource(_config, profiles);
        var serverClients = new ServerClients(serverLane, workContext);
        _serverLane = serverLane;
        ILaunchClient launch = serverLane;
```

  (`ServerClients` takes `IAsyncDisposable?`, so the lane slots in as the launch disposable; its `SignInCompleted` observable is subscribed in Task 12 to call `serverLane.RestartAsync()` — today `MainWindowViewModel`/sign-in code calls `ServerLaunchClient.InvalidateAsync`; grep for `InvalidateAsync` callers and point them at `RestartAsync` now: `rtk proxy grep -rn "InvalidateAsync" src/Capacitor.App --include='*.cs'`.)
- The fallback construction at line ~768 (`launch ?? new ServerLaunchClient(config, null)`) sits inside `BuildAndShowMainWindow`, a static-ish helper the tests call directly; change its parameter to a required non-null `ILaunchClient` and pass the lane from `BuildDaemonGraph`; update `AppStartupTests` call sites with a stub `ILaunchClient` if they relied on the null default (check `rtk proxy grep -rn "BuildAndShowMainWindow" test/ --include='*.cs'`).

- [ ] **Step 4: Build app + run the full app suite**

Run: `dotnet build src/Capacitor.App/Capacitor.App.csproj` then `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj`
Expected: green (fix any call sites the compiler flags — the compiler is the migration checklist).

- [ ] **Step 5: Commit**

```bash
git add -A src/Capacitor.App test/Capacitor.App.Tests.Unit
git commit -m "Retire ServerLaunchClient into the server lane (#708)"
```

---

### Task 6: `RemoteAgentsService` — remote caches

**Files:**
- Create: `src/Capacitor.App/Services/RemoteAgentsService.cs`
- Create: `test/Capacitor.App.Tests.Unit/FakeServerLane.cs`
- Test: `test/Capacitor.App.Tests.Unit/RemoteAgentsServiceTests.cs`

**Interfaces:**
- Consumes: `IServerLane` (Task 4), `AgentInstanceDto`/`DaemonInfo` (Task 1).
- Produces:

```csharp
public interface IRemoteAgentsService {
    /// Keyed by AgentId. Retained across lane loss — staleness is presentation (spec §6).
    IObservableCache<AgentInstanceDto, string> Agents { get; }
    /// Replay-1, seeded with an empty list.
    IObservable<IReadOnlyList<DaemonInfo>> Daemons { get; }
}

public sealed class RemoteAgentsService : IRemoteAgentsService, IDisposable {
    public RemoteAgentsService(
        IServerLane lane,
        Func<CancellationToken, Task<AgentInstanceDto[]?>> fetchAgents,
        TimeSpan? debounce = null);           // default 250ms; tests pass TimeSpan.Zero
    /// Production fetch: authenticated GET {server}/api/agent-instances via the
    /// HttpClientExtensions choke point, client built per call, null on auth failure/unreachable.
    public static Func<CancellationToken, Task<AgentInstanceDto[]?>> HttpFetch(
        ConfigRoot config, ProfileContext? profiles);
}
```

**Behavior:** on lane `Connected` → fetch agents (replace cache via `EditDiff` keyed on `AgentId`) and `GetConnectedDaemonsAsync` (publish list). On `AgentInstancesChanged` (debounced) → re-fetch agents. On `DaemonsChanged` (debounced) → re-fetch daemons. A null fetch result leaves the caches untouched (lane loss is data, not error). Single-flight per kind: a refresh arriving while one runs coalesces into one trailing re-run.

- [ ] **Step 1: Write `FakeServerLane`**

```csharp
using System.Reactive;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.Remote.Models;

namespace Capacitor.App.Tests.Unit;

sealed class FakeServerLane : IServerLane {
    public readonly BehaviorSubject<ServerLaneStatus> StatusSubject = new(new(ServerLaneState.Dormant));
    public readonly Subject<Unit> AgentsChangedSubject = new();
    public readonly Subject<Unit> DaemonsChangedSubject = new();
    public readonly Subject<LaunchFailure> LaunchFailuresSubject = new();
    public Func<Task<IReadOnlyList<DaemonInfo>?>> DaemonsHandler = () => Task.FromResult<IReadOnlyList<DaemonInfo>?>([]);

    public IObservable<ServerLaneStatus> Status => StatusSubject;
    public IObservable<Unit> AgentInstancesChanged => AgentsChangedSubject;
    public IObservable<Unit> DaemonsChanged => DaemonsChangedSubject;
    public IObservable<LaunchFailure> LaunchFailures => LaunchFailuresSubject;
    public Task<IReadOnlyList<DaemonInfo>?> GetConnectedDaemonsAsync(CancellationToken ct) => DaemonsHandler();
}
```

- [ ] **Step 2: Write the failing tests**

`test/Capacitor.App.Tests.Unit/RemoteAgentsServiceTests.cs`:

```csharp
using Capacitor.App.Services;
using Capacitor.Remote.Models;

namespace Capacitor.App.Tests.Unit;

public class RemoteAgentsServiceTests {
    static AgentInstanceDto Agent(string id, string status = "Running", string daemon = "work-mac") =>
        new() { AgentId = id, Status = status, DaemonName = daemon, OwnerUserId = "u1", Vendor = "claude" };

    static async Task Eventually(Func<bool> condition, int ms = 5000) {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition()) {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(10);
        }
    }

    [Test]
    public async Task ConnectSeedsAgentsAndDaemons() {
        var lane = new FakeServerLane {
            DaemonsHandler = () => Task.FromResult<IReadOnlyList<DaemonInfo>?>([new DaemonInfo { Name = "work-mac", Connected = true }]),
        };
        using var svc = new RemoteAgentsService(lane, _ => Task.FromResult<AgentInstanceDto[]?>([Agent("a1")]), TimeSpan.Zero);

        lane.StatusSubject.OnNext(new(ServerLaneState.Connected));
        await Eventually(() => svc.Agents.Count == 1);
        IReadOnlyList<DaemonInfo>? seen = null;
        using var sub = svc.Daemons.Subscribe(d => seen = d);
        await Eventually(() => seen is { Count: 1 });
    }

    [Test]
    public async Task PingRefreshesAndRemovalsPropagate() {
        var results = new Queue<AgentInstanceDto[]?>([ [Agent("a1"), Agent("a2")], [Agent("a2")] ]);
        var lane = new FakeServerLane();
        using var svc = new RemoteAgentsService(lane, _ => Task.FromResult(results.Count > 0 ? results.Dequeue() : null), TimeSpan.Zero);

        lane.StatusSubject.OnNext(new(ServerLaneState.Connected));
        await Eventually(() => svc.Agents.Count == 2);
        lane.AgentsChangedSubject.OnNext(System.Reactive.Unit.Default);
        await Eventually(() => svc.Agents.Count == 1 && svc.Agents.Lookup("a2").HasValue);
    }

    [Test]
    public async Task NullFetchLeavesCacheUntouched() {
        var first = true;
        var lane = new FakeServerLane();
        using var svc = new RemoteAgentsService(lane, _ => {
            var r = first ? new[] { Agent("a1") } : null;
            first = false;
            return Task.FromResult<AgentInstanceDto[]?>(r);
        }, TimeSpan.Zero);

        lane.StatusSubject.OnNext(new(ServerLaneState.Connected));
        await Eventually(() => svc.Agents.Count == 1);
        lane.AgentsChangedSubject.OnNext(System.Reactive.Unit.Default);
        await Task.Delay(100);
        await Assert.That(svc.Agents.Count).IsEqualTo(1);
    }
}
```

- [ ] **Step 3: Run to verify compile failure, then implement**

`src/Capacitor.App/Services/RemoteAgentsService.cs`:

```csharp
using System.Net.Http.Json;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Remote.Models;
using DynamicData;

namespace Capacitor.App.Services;

public interface IRemoteAgentsService {
    IObservableCache<AgentInstanceDto, string> Agents { get; }
    IObservable<IReadOnlyList<DaemonInfo>> Daemons { get; }
}

/// Remote registry caches: seeded on lane Connected, refreshed on the org-wide pings. A failed
/// or signed-out fetch returns null and leaves the caches as they were — lane loss is data.
public sealed class RemoteAgentsService : IRemoteAgentsService, IDisposable {
    readonly SourceCache<AgentInstanceDto, string> _agents = new(a => a.AgentId);
    readonly BehaviorSubject<IReadOnlyList<DaemonInfo>> _daemons = new([]);
    readonly IDisposable _subscriptions;
    readonly SemaphoreSlim _agentsFlight = new(1, 1);
    readonly SemaphoreSlim _daemonsFlight = new(1, 1);

    public RemoteAgentsService(
            IServerLane lane, Func<CancellationToken, Task<AgentInstanceDto[]?>> fetchAgents,
            TimeSpan? debounce = null) {
        var wait = debounce ?? TimeSpan.FromMilliseconds(250);
        var connected = lane.Status
            .Select(s => s.State == ServerLaneState.Connected)
            .DistinctUntilChanged()
            .Where(c => c)
            .Select(_ => System.Reactive.Unit.Default);

        var refreshAgents = connected.Merge(lane.AgentInstancesChanged.Throttle(wait))
            .Select(_ => Observable.FromAsync(async () => await RefreshAgentsAsync(fetchAgents)))
            .Concat()
            .Subscribe();
        var refreshDaemons = connected.Merge(lane.DaemonsChanged.Throttle(wait))
            .Select(_ => Observable.FromAsync(async () => await RefreshDaemonsAsync(lane)))
            .Concat()
            .Subscribe();
        _subscriptions = new System.Reactive.Disposables.CompositeDisposable(refreshAgents, refreshDaemons);
    }

    public IObservableCache<AgentInstanceDto, string> Agents => _agents.AsObservableCache();
    public IObservable<IReadOnlyList<DaemonInfo>> Daemons => _daemons.AsObservable();

    async Task RefreshAgentsAsync(Func<CancellationToken, Task<AgentInstanceDto[]?>> fetch) {
        if (!await _agentsFlight.WaitAsync(0)) return;
        try {
            var result = await fetch(CancellationToken.None).ConfigureAwait(false);
            if (result is not null) _agents.EditDiff(result, EqualityComparer<AgentInstanceDto>.Default);
        } catch (Exception) {
            // Data-plane refresh: a throw here is a missed refresh, never an app fault.
        } finally {
            _agentsFlight.Release();
        }
    }

    async Task RefreshDaemonsAsync(IServerLane lane) {
        if (!await _daemonsFlight.WaitAsync(0)) return;
        try {
            var result = await lane.GetConnectedDaemonsAsync(CancellationToken.None).ConfigureAwait(false);
            if (result is not null) _daemons.OnNext(result);
        } catch (Exception) {
        } finally {
            _daemonsFlight.Release();
        }
    }

    public static Func<CancellationToken, Task<AgentInstanceDto[]?>> HttpFetch(
            ConfigRoot config, ProfileContext? profiles) => async ct => {
        var serverUrl = profiles?.Resolution.ServerUrl;
        if (profiles is null || string.IsNullOrEmpty(serverUrl)) return null;
        try {
            var (client, status) = await HttpClientExtensions.CreateClientWithAuthStatusAsync(
                config, profiles, serverUrl, ct, autoRetryUnauthorized: true).ConfigureAwait(false);
            using (client) {
                if (status is not (AuthStatus.Ok or AuthStatus.NoAuthRequired)) return null;
                using var response = await client.GetAsync(ApiRoutes.AgentInstances, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return null;
                await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                return await System.Text.Json.JsonSerializer.DeserializeAsync(
                    stream, RemoteModelsJsonContext.Default.AgentInstanceDtoArray, ct).ConfigureAwait(false);
            }
        } catch (Exception) {
            return null;
        }
    };

    public void Dispose() {
        _subscriptions.Dispose();
        _agents.Dispose();
        _daemons.Dispose();
        _agentsFlight.Dispose();
        _daemonsFlight.Dispose();
    }
}
```

Adaptation note: `CreateClientWithAuthStatusAsync`'s exact signature/return is in `src/Capacitor.Cli.Core/HttpClientExtensions.cs:47` — match it (the `ServerWorkContextSource` factory default at `ServerWorkContextSource.cs:35` is the working example). If the generated context names the array type differently, use whatever `RemoteModelsJsonContext.Default` exposes for `AgentInstanceDto[]`.

- [ ] **Step 4: Run to verify pass, commit**

Run the `RemoteAgentsServiceTests` filter, then:

```bash
git add src/Capacitor.App/Services/RemoteAgentsService.cs test/Capacitor.App.Tests.Unit/FakeServerLane.cs test/Capacitor.App.Tests.Unit/RemoteAgentsServiceTests.cs
git commit -m "Add remote agent and daemon registry caches over the server lane (#708)"
```

---

### Task 7: Repository identity resolver

**Files:**
- Create: `src/Capacitor.Cli.Core/GitRemoteReader.cs`
- Create: `src/Capacitor.App/Services/RepoIdentity.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/GitRemoteReaderTests.cs`
- Test: `test/Capacitor.App.Tests.Unit/RepoIdentityTests.cs`

**Interfaces:**
- Consumes: existing `RemoteMatcher.NormalizeRemoteUrl(url)` / `RemoteMatcher.PathAfterHost(normalized)` (`src/Capacitor.Cli.Core/Config/RemoteMatcher.cs`), `PlatformPaths.Normalize`, `RepoLabel.Leaf`.
- Produces:
  - `Capacitor.Cli.Core.GitRemoteReader.ReadOriginUrl(string mainRepoRoot) : string?` — parses `{root}/.git/config` for the `[remote "origin"]` section's `url`; null when the file/section/key is absent or unreadable. No process spawn (the rail resolves identities on the UI path).
  - `Capacitor.App.Services.RepoIdentity` record: `(string Key, string Label)`.
  - `Capacitor.App.Services.RepoIdentityResolver` with:
    - `RepoIdentity ForLocalRoot(string normalizedRoot)` — memoized per root: origin URL → `RemoteMatcher.NormalizeRemoteUrl` → `PathAfterHost` → key `"repo:" + ownerRepo.ToLowerInvariant()`, label `ownerRepo`; no usable remote → key `"path:" + normalizedRoot`, label `RepoLabel.Leaf(normalizedRoot)`.
    - `static RepoIdentity ForRemote(string? repoOwner, string? repoName, string? repoPath, string daemonKey)` — owner+name → `"repo:{owner}/{name}"` lowercased, label `"{owner}/{name}"`; else machine-scoped `"daemon:" + daemonKey + ":" + (repoPath ?? "")`, label `RepoLabel.Leaf(repoPath ?? "")` (empty path → label `"No repository"` and key `"daemon:{daemonKey}:"`). `daemonKey` is `"{ownerUserId}/{daemonName}"`.
    - ctor takes `Func<string, string?> readOriginUrl` (defaults to `GitRemoteReader.ReadOriginUrl`) so tests inject pure lookups.

- [ ] **Step 1: Failing tests for `GitRemoteReader`**

`test/Capacitor.Cli.Core.Tests.Unit/GitRemoteReaderTests.cs`:

```csharp
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Core.Tests.Unit;

public class GitRemoteReaderTests {
    [TempDir] public required TempDir Tmp { get; init; }

    void WriteConfig(string content) {
        Tmp.CreateDir(".git");
        Tmp.CreateFile(Path.Combine(".git", "config"), content);
    }

    [Test]
    public async Task ReadsTheOriginUrl() {
        WriteConfig("""
        [core]
            bare = false
        [remote "origin"]
            url = git@github.com:kurrent-io/kcap-cli.git
            fetch = +refs/heads/*:refs/remotes/origin/*
        [remote "fork"]
            url = git@github.com:someone/kcap-cli.git
        """);
        await Assert.That(GitRemoteReader.ReadOriginUrl(Tmp.Path))
            .IsEqualTo("git@github.com:kurrent-io/kcap-cli.git");
    }

    [Test]
    public async Task NoOriginSectionIsNull() {
        WriteConfig("""
        [remote "upstream"]
            url = https://github.com/kurrent-io/kcap-cli.git
        """);
        await Assert.That(GitRemoteReader.ReadOriginUrl(Tmp.Path)).IsNull();
    }

    [Test]
    public async Task MissingConfigIsNull() =>
        await Assert.That(GitRemoteReader.ReadOriginUrl(Tmp.Path)).IsNull();
}
```

- [ ] **Step 2: Implement `GitRemoteReader`**

`src/Capacitor.Cli.Core/GitRemoteReader.cs`:

```csharp
namespace Capacitor.Cli.Core;

/// Reads the origin remote URL straight from .git/config — no git process, safe on a UI path.
/// Callers pass the MAIN repo root (GitRepository.ResolveMainRepoRoot), so worktree gitfiles
/// never reach this parser.
public static class GitRemoteReader {
    public static string? ReadOriginUrl(string mainRepoRoot) {
        var path = Path.Combine(mainRepoRoot, ".git", "config");
        string[] lines;
        try {
            if (!File.Exists(path)) return null;
            lines = File.ReadAllLines(path);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            return null;
        }

        var inOrigin = false;
        foreach (var raw in lines) {
            var line = raw.Trim();
            if (line.StartsWith('[')) {
                inOrigin = line.Equals("[remote \"origin\"]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inOrigin || !line.StartsWith("url", StringComparison.OrdinalIgnoreCase)) continue;
            var eq = line.IndexOf('=');
            if (eq < 0) continue;
            var value = line[(eq + 1)..].Trim();
            return value.Length > 0 ? value : null;
        }
        return null;
    }
}
```

Run the `GitRemoteReaderTests` filter — PASS.

- [ ] **Step 3: Failing tests for `RepoIdentityResolver`**

`test/Capacitor.App.Tests.Unit/RepoIdentityTests.cs`:

```csharp
using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

public class RepoIdentityTests {
    [Test]
    public async Task LocalAndRemoteCheckoutsOfOneRepoShareAKey() {
        var resolver = new RepoIdentityResolver(_ => "git@github.com:Kurrent-io/kcap-cli.git");
        var local = resolver.ForLocalRoot("/home/me/kcap-cli");
        var remote = RepoIdentityResolver.ForRemote("kurrent-io", "kcap-cli", "/work/kcap-cli", "u1/work-mac");
        await Assert.That(local.Key).IsEqualTo(remote.Key);
        await Assert.That(local.Key).IsEqualTo("repo:kurrent-io/kcap-cli");
    }

    [Test]
    public async Task RemoteWithoutIdentityIsMachineScoped() {
        var a = RepoIdentityResolver.ForRemote(null, null, "/work/repo", "u1/work-mac");
        var b = RepoIdentityResolver.ForRemote(null, null, "/work/repo", "u1/home-pc");
        await Assert.That(a.Key).IsNotEqualTo(b.Key);
        await Assert.That(a.Label).IsEqualTo("repo");
    }

    [Test]
    public async Task LocalWithoutRemoteStaysPathScoped() {
        var resolver = new RepoIdentityResolver(_ => null);
        var id = resolver.ForLocalRoot("/home/me/private");
        await Assert.That(id.Key).IsEqualTo("path:/home/me/private");
        await Assert.That(id.Label).IsEqualTo("private");
    }

    [Test]
    public async Task LocalResolutionIsMemoized() {
        var reads = 0;
        var resolver = new RepoIdentityResolver(_ => { reads++; return null; });
        resolver.ForLocalRoot("/r");
        resolver.ForLocalRoot("/r");
        await Assert.That(reads).IsEqualTo(1);
    }
}
```

- [ ] **Step 4: Implement `RepoIdentity.cs`**

```csharp
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;

namespace Capacitor.App.Services;

/// A rail group's identity: same Key = same repository wherever it is checked out. The key is
/// NOT a filesystem path — never hand it to path-formatting helpers.
public sealed record RepoIdentity(string Key, string Label);

public sealed class RepoIdentityResolver(Func<string, string?>? readOriginUrl = null) {
    readonly Func<string, string?> _readOriginUrl = readOriginUrl ?? GitRemoteReader.ReadOriginUrl;
    readonly Dictionary<string, RepoIdentity> _byRoot = new(StringComparer.Ordinal);
    readonly Lock _lock = new();

    public RepoIdentity ForLocalRoot(string normalizedRoot) {
        lock (_lock) {
            if (_byRoot.TryGetValue(normalizedRoot, out var cached)) return cached;
            var identity = Resolve(normalizedRoot);
            _byRoot[normalizedRoot] = identity;
            return identity;
        }
    }

    RepoIdentity Resolve(string root) {
        var url = root.Length > 0 ? _readOriginUrl(root) : null;
        var ownerRepo = url is null ? null : OwnerRepoOf(url);
        return ownerRepo is null
            ? new RepoIdentity($"path:{root}", root.Length > 0 ? RepoLabel.Leaf(root) : "No repository")
            : new RepoIdentity($"repo:{ownerRepo.ToLowerInvariant()}", ownerRepo);
    }

    public static RepoIdentity ForRemote(string? repoOwner, string? repoName, string? repoPath, string daemonKey) {
        if (!string.IsNullOrEmpty(repoOwner) && !string.IsNullOrEmpty(repoName))
            return new($"repo:{repoOwner.ToLowerInvariant()}/{repoName.ToLowerInvariant()}", $"{repoOwner}/{repoName}");
        var path = repoPath ?? "";
        return new($"daemon:{daemonKey}:{path}", path.Length > 0 ? RepoLabel.Leaf(path) : "No repository");
    }

    static string? OwnerRepoOf(string url) {
        var normalized = RemoteMatcher.NormalizeRemoteUrl(url);
        return normalized is null ? null : RemoteMatcher.PathAfterHost(normalized);
    }
}
```

(`RepoLabel` lives in the app already — check its namespace with `rtk proxy grep -rn "static class RepoLabel" src/` and import accordingly; if `RemoteMatcher` is in `Capacitor.Cli.Core.Config`, keep that using.)

- [ ] **Step 5: Run both filters, commit**

```bash
git add src/Capacitor.Cli.Core/GitRemoteReader.cs src/Capacitor.App/Services/RepoIdentity.cs test/Capacitor.Cli.Core.Tests.Unit/GitRemoteReaderTests.cs test/Capacitor.App.Tests.Unit/RepoIdentityTests.cs
git commit -m "Resolve repository identity for rail grouping across machines (#708)"
```

---

### Task 8: Twin match, `AgentRow`, and `AgentDirectory`

**Files:**
- Create: `src/Capacitor.App/Services/AgentRow.cs`
- Create: `src/Capacitor.App/Services/LocalDaemonTwin.cs`
- Create: `src/Capacitor.App/Services/AgentDirectory.cs`
- Test: `test/Capacitor.App.Tests.Unit/LocalDaemonTwinTests.cs`
- Test: `test/Capacitor.App.Tests.Unit/AgentDirectoryTests.cs`

**Interfaces:**
- Consumes: `IDaemonClientService` (local cache/status/snapshots), `IRemoteAgentsService` (Task 6), `RepoIdentityResolver` (Task 7), `AgentStatusDto`, `AgentInstanceDto`, `ServerIdentity.Canonicalize` (find it: `rtk proxy grep -rn "static.*Canonicalize" src/Capacitor.Cli.Core --include='*.cs'` — it is the server-URL canonicalizer named in CLAUDE.md; match its actual signature).
- Produces:

```csharp
public enum AgentOrigin { Local, Remote }

/// One merged row. Key is SOURCE-scoped ("local:{id}" / "remote:{id}") so the lanes can never
/// clobber each other; Id is the logical agent id workspaces bind to.
public sealed record AgentRow(
    string Key, AgentOrigin Origin, string Id, string Kind, string Vendor, string Status,
    DateTime CreatedAt, string? RepoPath, string? Title, string? Model, string? RequesterDisplay,
    string? WorktreePath, string? WorkLocation, string? BorrowedFrom,
    string? MachineBadge,             // remote rows: the daemon name; local rows: null
    string RepoGroupKey, string RepoGroupLabel, string CheckoutKey, string CheckoutLabel);

public static class LocalDaemonTwin {
    /// Exactly-one match or null (fail open). Server scoping first: no twin when the local
    /// daemon's server is not the app's server.
    public static (string OwnerUserId, string DaemonName)? Find(
        IReadOnlyList<DaemonInfo> daemons, string? localMachineId, string localDaemonName,
        string? localServerUrl, string? appServerUrl);
}

public interface IAgentDirectory {
    IObservableCache<AgentRow, string> Rows { get; }
    /// True while the server lane is not Connected — rail rows grey out on it.
    IObservable<bool> RemoteStale { get; }
}

public sealed class AgentDirectory : IAgentDirectory, IDisposable {
    public AgentDirectory(
        IDaemonClientService local, IRemoteAgentsService remote, IServerLane lane,
        RepoIdentityResolver repoIdentity, Func<string, string> resolveLocalRepoRoot,
        string? localMachineId, string? appServerUrl);
}
```

**Behavior:**
- Local rows: every `local.Agents` item, projected via `AgentRow` with `Origin: Local`, `Key: "local:"+Id`, `MachineBadge: null`; `RepoGroupKey/Label` from `repoIdentity.ForLocalRoot(PlatformPaths.Normalize(resolveLocalRepoRoot(RepoPath)))` when `RepoPath` is non-empty, else the path-scoped empty identity (`Key "path:"`, label `"No repository"`); `CheckoutKey/Label` reproduce today's rail semantics: `CheckoutKey = SessionRailViewModel.WorktreeKeyFor(dto)`, `CheckoutLabel` left `""` (the worktree VM computes its own label from rows today — keep that; `CheckoutLabel` is used only for remote pseudo-checkouts).
- Remote rows: `remote.Agents` items with `Status is "Starting" or "Running"` (the rail is live sessions, not history), projected with `Origin: Remote`, `Key: "remote:"+AgentId`, `Kind: "agent"`, `Vendor: dto.Vendor ?? ""`, `CreatedAt: RegisteredAt`, `Title: RemoteTitle.FromPrompt(dto.Prompt)` (first non-blank line, trimmed to 80 chars — implement as a small static in `AgentRow.cs`), `MachineBadge: dto.DaemonName`, `RepoGroupKey/Label` via `RepoIdentityResolver.ForRemote(RepoOwner, RepoName, RepoPath, $"{OwnerUserId}/{DaemonName}")`, `CheckoutKey: $"@{OwnerUserId}/{DaemonName}"`, `CheckoutLabel: $"on {DaemonName}"`.
- **Suppression**: recompute on every change of (`remote.Daemons`, `local.Status` connected-ness, `local.Snapshots` first value's `Daemon.ServerUrl`, `remote.Agents`): twin = `LocalDaemonTwin.Find(daemons, localMachineId, local.DaemonName, localServerUrl, appServerUrl)`; a remote row is EXCLUDED iff twin is non-null AND local attach state is `Connected` AND `(row.OwnerUserId, row.DaemonName) == twin`. Everything else is included — zero/multiple twin candidates mean no suppression at all (fail open).
- Implementation shape: hold an internal `SourceCache<AgentRow, string>`; subscribe local cache changes → `AddOrUpdate`/`RemoveKey("local:"+id)` mechanically; keep the latest remote inputs in fields and on ANY remote-side input change rebuild the full remote row set and `EditDiff` **only** the `remote:`-prefixed keys (compute the union of current local rows + new remote rows and `EditDiff` the whole cache — simplest correct approach since `EditDiff` diffs by key and local rows are also deterministic projections).
- `RemoteStale` = `lane.Status.Select(s => s.State != ServerLaneState.Connected).DistinctUntilChanged()`.

- [ ] **Step 1: Failing twin tests**

`test/Capacitor.App.Tests.Unit/LocalDaemonTwinTests.cs`:

```csharp
using Capacitor.App.Services;
using Capacitor.Remote.Models;

namespace Capacitor.App.Tests.Unit;

public class LocalDaemonTwinTests {
    static DaemonInfo D(string name, string? machineId, string owner = "u1") =>
        new() { Name = name, MachineId = machineId, OwnerUserId = owner, Connected = true };

    const string Server = "https://cap.example.com";

    [Test]
    public async Task ExactlyOneMatchWins() {
        var twin = LocalDaemonTwin.Find([D("work-mac", "m1"), D("work-mac", "m2", "u2")], "m1", "work-mac", Server, Server);
        await Assert.That(twin).IsEqualTo(("u1", "work-mac"));
    }

    [Test]
    public async Task TwoCandidatesFailOpen() {
        var twin = LocalDaemonTwin.Find([D("work-mac", "m1"), D("work-mac", "m1", "u2")], "m1", "work-mac", Server, Server);
        await Assert.That(twin).IsNull();
    }

    [Test]
    public async Task MissingMachineIdFailsOpen() {
        await Assert.That(LocalDaemonTwin.Find([D("work-mac", null)], "m1", "work-mac", Server, Server)).IsNull();
        await Assert.That(LocalDaemonTwin.Find([D("work-mac", "m1")], null, "work-mac", Server, Server)).IsNull();
    }

    [Test]
    public async Task DifferentServerNeverMatches() {
        var twin = LocalDaemonTwin.Find([D("work-mac", "m1")], "m1", "work-mac", "https://other.example.com", Server);
        await Assert.That(twin).IsNull();
    }
}
```

- [ ] **Step 2: Implement `LocalDaemonTwin` + `AgentRow`**

`src/Capacitor.App/Services/LocalDaemonTwin.cs`:

```csharp
using Capacitor.Remote.Models;

namespace Capacitor.App.Services;

public static class LocalDaemonTwin {
    public static (string OwnerUserId, string DaemonName)? Find(
            IReadOnlyList<DaemonInfo> daemons, string? localMachineId, string localDaemonName,
            string? localServerUrl, string? appServerUrl) {
        if (localMachineId is null) return null;
        if (!ServersMatch(localServerUrl, appServerUrl)) return null;

        (string, string)? found = null;
        foreach (var d in daemons) {
            if (d.MachineId != localMachineId || d.Name != localDaemonName || d.OwnerUserId is null) continue;
            if (found is not null) return null;
            found = (d.OwnerUserId, d.Name);
        }
        return found;
    }

    static bool ServersMatch(string? localServerUrl, string? appServerUrl) =>
        localServerUrl is not null && appServerUrl is not null
        && Canonical(localServerUrl) == Canonical(appServerUrl);

    static string Canonical(string url) => url.TrimEnd('/').ToLowerInvariant();
}
```

(If `ServerIdentity.Canonicalize` exists with a usable static shape, call it in `Canonical` instead of the local lowering — check first, prefer the shared canonicalizer.)

`src/Capacitor.App/Services/AgentRow.cs` — the record from the Interfaces block verbatim, plus:

```csharp
    public static AgentRow FromLocal(AgentStatusDto dto, RepoIdentity repo) => new(
        Key: $"local:{dto.Id}", Origin: AgentOrigin.Local, Id: dto.Id, Kind: dto.Kind,
        Vendor: dto.Vendor, Status: dto.Status, CreatedAt: dto.CreatedAt, RepoPath: dto.RepoPath,
        Title: dto.Title, Model: dto.Model, RequesterDisplay: dto.RequesterDisplay,
        WorktreePath: dto.WorktreePath, WorkLocation: dto.WorkLocation, BorrowedFrom: dto.BorrowedFrom,
        MachineBadge: null, RepoGroupKey: repo.Key, RepoGroupLabel: repo.Label,
        CheckoutKey: ViewModels.SessionRailViewModel.WorktreeKeyFor(dto), CheckoutLabel: "");

    public static AgentRow FromRemote(AgentInstanceDto dto) {
        var daemonKey = $"{dto.OwnerUserId}/{dto.DaemonName}";
        var repo = RepoIdentityResolver.ForRemote(dto.RepoOwner, dto.RepoName, dto.RepoPath, daemonKey);
        return new(
            Key: $"remote:{dto.AgentId}", Origin: AgentOrigin.Remote, Id: dto.AgentId, Kind: "agent",
            Vendor: dto.Vendor ?? "", Status: dto.Status, CreatedAt: dto.RegisteredAt, RepoPath: dto.RepoPath,
            Title: TitleFromPrompt(dto.Prompt), Model: dto.Model, RequesterDisplay: null,
            WorktreePath: null, WorkLocation: null, BorrowedFrom: null,
            MachineBadge: dto.DaemonName, RepoGroupKey: repo.Key, RepoGroupLabel: repo.Label,
            CheckoutKey: $"@{daemonKey}", CheckoutLabel: $"on {dto.DaemonName}");
    }

    internal static string? TitleFromPrompt(string? prompt) {
        if (string.IsNullOrWhiteSpace(prompt)) return null;
        var line = prompt.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        return line is null ? null : line.Length <= 80 ? line : line[..80];
    }
```

Run the twin filter — PASS.

- [ ] **Step 3: Failing directory tests**

`test/Capacitor.App.Tests.Unit/AgentDirectoryTests.cs`:

```csharp
using Capacitor.App.Services;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Remote.Models;
using DynamicData;

namespace Capacitor.App.Tests.Unit;

public class AgentDirectoryTests {
    const string Server = "http://localhost:9999"; // FakeDaemonClientService.Snap's default ServerUrl

    sealed class FakeRemoteAgents : IRemoteAgentsService, IDisposable {
        public readonly SourceCache<AgentInstanceDto, string> Cache = new(a => a.AgentId);
        public readonly System.Reactive.Subjects.BehaviorSubject<IReadOnlyList<DaemonInfo>> DaemonsSubject = new([]);
        public IObservableCache<AgentInstanceDto, string> Agents => Cache.AsObservableCache();
        public IObservable<IReadOnlyList<DaemonInfo>> Daemons => DaemonsSubject;
        public void Dispose() => Cache.Dispose();
    }

    static AgentInstanceDto Remote(string id, string daemon = "work-mac", string owner = "u1", string status = "Running") =>
        new() { AgentId = id, Status = status, DaemonName = daemon, OwnerUserId = owner, Vendor = "claude", RepoOwner = "o", RepoName = "r" };

    static (FakeDaemonClientService Local, FakeRemoteAgents Remote, FakeServerLane Lane, AgentDirectory Dir) Build(
            string? machineId = "m1") {
        var local = new FakeDaemonClientService();
        var remote = new FakeRemoteAgents();
        var lane = new FakeServerLane();
        var dir = new AgentDirectory(
            local, remote, lane, new RepoIdentityResolver(_ => null), p => p,
            machineId, Server);
        return (local, remote, lane, dir);
    }

    static AgentStatusDto LocalAgent(string id) => new(
        Id: id, Kind: "agent", Vendor: "claude", RepoPath: "/r", Status: "Running",
        FlowRunId: null, FlowRole: null, Requester: null, CreatedAt: DateTime.UtcNow, Model: null,
        RequesterDisplay: null);

    [Test]
    public async Task LocalAndRemoteRowsMerge() {
        var (local, remote, _, dir) = Build();
        using var _d = dir;
        local.Agents.AddOrUpdate(LocalAgent("a1"));
        remote.Cache.AddOrUpdate(Remote("b1"));
        await Assert.That(dir.Rows.Count).IsEqualTo(2);
        await Assert.That(dir.Rows.Lookup("local:a1").HasValue).IsTrue();
        await Assert.That(dir.Rows.Lookup("remote:b1").HasValue).IsTrue();
    }

    [Test]
    public async Task TwinAgentsSuppressWhileLocalConnected() {
        var (local, remote, _, dir) = Build();
        using var _d = dir;
        // Local daemon "daemon-a" on machine m1, connected, reporting Server.
        local.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap());
        local.StatusSubject.OnNext(new(AttachState.Connected, null, ["status/1"]));
        remote.DaemonsSubject.OnNext([new DaemonInfo { Name = "daemon-a", MachineId = "m1", OwnerUserId = "u1", Connected = true }]);
        remote.Cache.AddOrUpdate(Remote("b1", daemon: "daemon-a"));
        remote.Cache.AddOrUpdate(Remote("b2", daemon: "home-pc"));

        await Assert.That(dir.Rows.Lookup("remote:b1").HasValue).IsFalse(); // twin's agent suppressed
        await Assert.That(dir.Rows.Lookup("remote:b2").HasValue).IsTrue();  // other machine stands
    }

    [Test]
    public async Task SuppressionLiftsWhenLocalUnreachable() {
        var (local, remote, _, dir) = Build();
        using var _d = dir;
        local.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap());
        local.StatusSubject.OnNext(new(AttachState.Connected, null, ["status/1"]));
        remote.DaemonsSubject.OnNext([new DaemonInfo { Name = "daemon-a", MachineId = "m1", OwnerUserId = "u1", Connected = true }]);
        remote.Cache.AddOrUpdate(Remote("b1", daemon: "daemon-a"));
        await Assert.That(dir.Rows.Lookup("remote:b1").HasValue).IsFalse();

        local.StatusSubject.OnNext(new(AttachState.Unreachable, "daemon_unreachable", null));
        await Assert.That(dir.Rows.Lookup("remote:b1").HasValue).IsTrue();
    }

    [Test]
    public async Task UncertainTwinFailsOpenToDuplicates() {
        var (local, remote, _, dir) = Build(machineId: null); // no persisted machine id
        using var _d = dir;
        local.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap());
        local.StatusSubject.OnNext(new(AttachState.Connected, null, ["status/1"]));
        local.Agents.AddOrUpdate(LocalAgent("a1"));
        remote.DaemonsSubject.OnNext([new DaemonInfo { Name = "daemon-a", MachineId = "m1", OwnerUserId = "u1", Connected = true }]);
        remote.Cache.AddOrUpdate(Remote("a1", daemon: "daemon-a")); // same agent id, both lanes

        await Assert.That(dir.Rows.Lookup("local:a1").HasValue).IsTrue();
        await Assert.That(dir.Rows.Lookup("remote:a1").HasValue).IsTrue(); // two rows, never hidden
    }

    [Test]
    public async Task EndedRemoteAgentsAreNotRows() {
        var (_, remote, _, dir) = Build();
        using var _d = dir;
        remote.Cache.AddOrUpdate(Remote("b1", status: "Completed"));
        await Assert.That(dir.Rows.Count).IsEqualTo(0);
    }

    [Test]
    public async Task RemoteStaleTracksLane() {
        var (_, _, lane, dir) = Build();
        using var _d = dir;
        bool? stale = null;
        using var sub = dir.RemoteStale.Subscribe(s => stale = s);
        await Assert.That(stale).IsEqualTo(true);
        lane.StatusSubject.OnNext(new(ServerLaneState.Connected));
        await Assert.That(stale).IsEqualTo(false);
    }
}
```

- [ ] **Step 4: Implement `AgentDirectory`**

`src/Capacitor.App/Services/AgentDirectory.cs`:

```csharp
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Remote.Models;
using DynamicData;

namespace Capacitor.App.Services;

public interface IAgentDirectory {
    IObservableCache<AgentRow, string> Rows { get; }
    IObservable<bool> RemoteStale { get; }
}

/// Merges the local daemon's agents with the server registry's into source-scoped rows.
/// Suppression is daemon-level and evidence-based: only the proven twin's agents are hidden,
/// and only while the local socket is Connected — everything uncertain renders twice rather
/// than not at all.
public sealed class AgentDirectory : IAgentDirectory, IDisposable {
    readonly SourceCache<AgentRow, string> _rows = new(r => r.Key);
    readonly CompositeDisposable _subscriptions = new();
    readonly IDaemonClientService _local;
    readonly RepoIdentityResolver _repoIdentity;
    readonly Func<string, string> _resolveLocalRepoRoot;
    readonly string? _localMachineId;
    readonly string? _appServerUrl;
    readonly object _lock = new();

    IReadOnlyList<DaemonInfo> _daemons = [];
    bool _localConnected;
    string? _localServerUrl;
    List<AgentInstanceDto> _remoteAgents = [];

    public AgentDirectory(
            IDaemonClientService local, IRemoteAgentsService remote, IServerLane lane,
            RepoIdentityResolver repoIdentity, Func<string, string> resolveLocalRepoRoot,
            string? localMachineId, string? appServerUrl) {
        _local = local;
        _repoIdentity = repoIdentity;
        _resolveLocalRepoRoot = resolveLocalRepoRoot;
        _localMachineId = localMachineId;
        _appServerUrl = appServerUrl;

        RemoteStale = lane.Status.Select(s => s.State != ServerLaneState.Connected).DistinctUntilChanged();

        local.Agents.Connect().Subscribe(changes => {
            foreach (var change in changes) {
                switch (change.Reason) {
                    case ChangeReason.Add or ChangeReason.Update or ChangeReason.Refresh:
                        _rows.AddOrUpdate(ProjectLocal(change.Current));
                        break;
                    case ChangeReason.Remove:
                        _rows.RemoveKey($"local:{change.Current.Id}");
                        break;
                }
            }
        }).DisposeWith(_subscriptions);

        remote.Agents.Connect().ToCollection()
            .Subscribe(items => { lock (_lock) _remoteAgents = [.. items]; RecomputeRemote(); })
            .DisposeWith(_subscriptions);
        remote.Daemons
            .Subscribe(d => { lock (_lock) _daemons = d; RecomputeRemote(); })
            .DisposeWith(_subscriptions);
        local.Status
            .Select(s => s.State == AttachState.Connected).DistinctUntilChanged()
            .Subscribe(c => { lock (_lock) _localConnected = c; RecomputeRemote(); })
            .DisposeWith(_subscriptions);
        local.Snapshots
            .Select(s => s.Daemon.ServerUrl).DistinctUntilChanged()
            .Subscribe(u => { lock (_lock) _localServerUrl = u; RecomputeRemote(); })
            .DisposeWith(_subscriptions);
    }

    public IObservableCache<AgentRow, string> Rows => _rows.AsObservableCache();
    public IObservable<bool> RemoteStale { get; }

    AgentRow ProjectLocal(AgentStatusDto dto) {
        var repo = dto.RepoPath is { Length: > 0 } path
            ? _repoIdentity.ForLocalRoot(PlatformPaths.Normalize(_resolveLocalRepoRoot(path)))
            : new RepoIdentity("path:", "No repository");
        return AgentRow.FromLocal(dto, repo);
    }

    void RecomputeRemote() {
        List<AgentRow> next;
        lock (_lock) {
            var twin = LocalDaemonTwin.Find(_daemons, _localMachineId, _local.DaemonName, _localServerUrl, _appServerUrl);
            next = _remoteAgents
                .Where(a => a.Status is "Starting" or "Running")
                .Where(a => !(twin is { } t && _localConnected
                              && a.OwnerUserId == t.OwnerUserId && a.DaemonName == t.DaemonName))
                .Select(AgentRow.FromRemote)
                .ToList();
        }
        _rows.Edit(cache => {
            foreach (var key in cache.Keys.Where(k => k.StartsWith("remote:", StringComparison.Ordinal)).ToList())
                if (!next.Any(r => r.Key == key)) cache.RemoveKey(key);
            foreach (var row in next) cache.AddOrUpdate(row);
        });
    }

    public void Dispose() {
        _subscriptions.Dispose();
        _rows.Dispose();
    }
}
```

- [ ] **Step 5: Run both new filters + the whole app suite; commit**

```bash
git add src/Capacitor.App/Services/AgentRow.cs src/Capacitor.App/Services/LocalDaemonTwin.cs src/Capacitor.App/Services/AgentDirectory.cs test/Capacitor.App.Tests.Unit/LocalDaemonTwinTests.cs test/Capacitor.App.Tests.Unit/AgentDirectoryTests.cs
git commit -m "Merge local and remote agents into source-scoped directory rows (#708)"
```

---

### Task 9: The rail renders `AgentRow`s with machine badges

**Files:**
- Modify: `src/Capacitor.App/ViewModels/SessionRailViewModel.cs`
- Modify: `src/Capacitor.App/ViewModels/RailRepoViewModel.cs`
- Modify: `src/Capacitor.App/ViewModels/RailWorktreeViewModel.cs`
- Modify: `src/Capacitor.App/ViewModels/RailSessionViewModel.cs`
- Modify: `src/Capacitor.App/Views/SessionRailView.axaml` (machine badge element)
- Test: `test/Capacitor.App.Tests.Unit/SessionRailViewModelTests.cs` (extend the existing class)

**Interfaces:**
- Consumes: `IAgentDirectory` (Task 8). `SessionRailViewModel`'s ctor becomes
  `SessionRailViewModel(IAgentDirectory directory, IDaemonClientService daemon, Action<string> openLocalSession, Action<string> openRemoteInWeb, Func<string, string>? resolveRepoRoot = null, IObservable<IReadOnlySet<string>>? agentsWithPending = null)` — `daemon` stays only for `NotifySessionOpened`'s lookup; the tree now builds from `directory.Rows`.
- Produces: `RailSessionViewModel.MachineBadge : string?` (bound in XAML), `RailSessionViewModel.IsRemote : bool`.

**Transformation rules** (read each file first; the pipeline shape, comparers, collapse state, and the single top-of-pipeline `ObserveOn` all stay exactly as they are):
1. `SessionRailViewModel`: the pipeline becomes `directory.Rows.Connect().ObserveOn(...).Group(r => r.RepoGroupKey)` — the repo-root resolution and memoization move out (Task 8's `AgentDirectory` already computed `RepoGroupKey`), so delete `_rootByPath`, `RepoRootFor`, and the `_resolveRepoRoot` field; `_isEmpty`/`_hostedText` count `directory.Rows` instead of `daemon.Agents`. `WorktreeKeyFor(AgentStatusDto)` stays (Task 8 calls it); add the row-based `static string WorktreeKeyFor(AgentRow row) => row.CheckoutKey;`. `NotifySessionOpened` looks up `directory.Rows` by key `"local:"+agentId` falling back to `"remote:"+agentId` and uses its `CheckoutKey`.
2. `RailRepoViewModel`: group element type changes `IGroup<AgentStatusDto, string, string>` → `IGroup<AgentRow, string, string>`; its `Label` comes from the group's first row's `RepoGroupLabel` (rows in one group share it by construction — take it via the nested cache's items); `RootPath` (used only for the comparer tiebreak and collapse keys) becomes the group key string; `IsNoRepository` = group key starts with `"path:"` and label is `"No repository"`, OR key starts with `"daemon:"` with an empty path tail — express it as `Label == "No repository"`. Wherever the current code formats the group key as a filesystem path (line ~39 passes it into checkout formatting), stop: labels now come from `RepoGroupLabel`/`CheckoutLabel` fields.
3. `RailWorktreeViewModel`: group element type change; the worktree label: keep today's checkout-label derivation for local rows (`CheckoutLabel == ""`), and use `row.CheckoutLabel` verbatim when non-empty (remote pseudo-checkout, e.g. `"on work-mac"`).
4. `RailSessionViewModel`: ctor parameter `AgentStatusDto dto` → `AgentRow row`, same projections field-for-field (`row.Title`, `row.Kind`, `row.Vendor`, `row.Model`, `row.Status`, `row.CreatedAt`, `row.WorkLocation`, `row.BorrowedFrom`, `row.RequesterDisplay`); add:

```csharp
    public string? MachineBadge { get; }   // = row.MachineBadge
    public bool IsRemote { get; }          // = row.Origin == AgentOrigin.Remote
```

   and the open action: ctor takes `Action<string> openLocal, Action<string> openRemoteInWeb`; `OpenCommand = ReactiveCommand.Create(() => (IsRemote ? openRemoteInWeb : openLocal)(row.Id));`. Slice 1 remote rows are read-only in-app; opening deep-links to the web.
5. `SessionRailView.axaml`: inside the session row template, after the primary text block, add a badge shown only when remote:

```xml
<Border IsVisible="{Binding MachineBadge, Converter={x:Static ObjectConverters.IsNotNull}}"
        Background="{DynamicResource SystemControlBackgroundBaseLowBrush}"
        CornerRadius="3" Padding="4,0" Margin="6,0,0,0" VerticalAlignment="Center">
    <TextBlock Text="{Binding MachineBadge}" FontSize="10" Opacity="0.8" />
</Border>
```

   (Match the file's actual resource keys and layout idiom — read the surrounding template first and mirror it.)

- [ ] **Step 1: Extend `SessionRailViewModelTests` with failing tests** (read the existing file first and reuse its builder helpers; these tests drive an `AgentDirectory` over the fakes from Task 8):

```csharp
    [Test]
    public async Task RemoteRowsGroupWithLocalRowsOfTheSameRepository() {
        // Arrange: local agent whose origin remote says kurrent-io/kcap-cli, and a remote row
        // with RepoOwner/RepoName kurrent-io/kcap-cli — one repo group, two sessions, the
        // remote one carrying the machine badge.
        // Use RepoIdentityResolver(_ => "git@github.com:kurrent-io/kcap-cli.git") for the local side.
    }

    [Test]
    public async Task RemoteRowOpensInWebNotInWorkspace() {
        // openRemoteInWeb must receive the id; openLocalSession must not fire.
    }
```

Flesh both out against the real builder in the file — assert `Repos.Count == 1`, sessions count 2, `MachineBadge == "work-mac"` on the remote row, and the command routing. (The arrangement code mirrors `AgentDirectoryTests.Build` — construct the directory, hand it to `SessionRailViewModel`.)

- [ ] **Step 2: Run the rail test filter — expect compile failures, then apply the transformation** per the rules above, keeping every existing test green (existing rail tests construct `SessionRailViewModel(daemon, open, resolveRepoRoot, pending)` — update those call sites to build a directory over the fake daemon with an empty remote side: `new AgentDirectory(daemon, emptyRemote, lane, new RepoIdentityResolver(_ => null), resolveRepoRoot ?? GitRepository.ResolveMainRepoRoot, null, null)`; add a tiny shared helper in the test file rather than touching every test body).

- [ ] **Step 3: Build the app project** (`dotnet build src/Capacitor.App/Capacitor.App.csproj`) — fix all compile errors this type change surfaces (`MainWindowViewModel`, `App.axaml.cs` construction of the rail is finished in Task 12; for now update signatures mechanically so the build is green with the directory built where the rail is built).

- [ ] **Step 4: Run the full app suite; commit**

```bash
git add -A src/Capacitor.App test/Capacitor.App.Tests.Unit
git commit -m "Group the session rail by repository identity with machine badges (#708)"
```

---

### Task 10: Tray aggregates both lanes

**Files:**
- Modify: `src/Capacitor.App/ViewModels/TrayViewModel.cs`
- Test: `test/Capacitor.App.Tests.Unit/TrayViewModelTests.cs` (extend)

**Interfaces:**
- Consumes: `IAgentDirectory.Rows` + `RemoteStale` (Task 8).
- Produces: `TrayViewModel` ctor gains `IObservable<RemoteTraySummary>? remote = null` (null keeps every existing test compiling and behaving identically); new record in `TrayModels.cs`:

```csharp
/// The server lane's contribution to the tray verdict: live remote agents (twin-suppressed
/// rows excluded already) and whether the lane is up.
public readonly record struct RemoteTraySummary(int RemoteLiveAgents, bool LaneConnected);
```

  plus `internal static IObservable<RemoteTraySummary> SummaryFrom(IAgentDirectory directory)` on `TrayViewModel` (counts `Rows` where `Origin == Remote && Status is "Starting" or "Running"`, combined with `RemoteStale.Select(s => !s)`).

**Aggregation rule (spec §6)** — `Project` keeps its exact ten-row local mapping, then a wrapper applies the remote contribution:

```csharp
    internal static (TrayState State, int Count) ProjectAggregate(
            AttachStatus status, DaemonStatusDto? snap, RemoteTraySummary remote) {
        var (state, count) = Project(status, snap);
        var total = count + remote.RemoteLiveAgents;

        // Local Stopped is only the whole story when the server lane has nothing to show.
        if (state == TrayState.Stopped && remote.LaneConnected && remote.RemoteLiveAgents > 0)
            return (TrayState.Running, total);
        if (state is TrayState.Idle && remote.RemoteLiveAgents > 0)
            return (TrayState.Running, total);
        if (state is TrayState.Running)
            return (TrayState.Running, total);
        return (state, count);
    }
```

Attention/Connecting verdicts keep precedence untouched (remote attention arrives in slice 2). `Build` calls `ProjectAggregate` instead of `Project`; the header for the `Stopped`-locally-but-remote-running case reads `"{daemonName}: not running — {n} agent(s) on other machines"`. Entries stay local-only in slice 1 (`BuildEntries` unchanged) — remote sessions are reachable from the rail.

- [ ] **Step 1: Failing tests** (extend `TrayViewModelTests` — read its existing builder first; it drives `FakeDaemonClientService` subjects):

```csharp
    [Test]
    public async Task LocalStoppedWithRemoteAgentsShowsRunning() {
        // status Unreachable(daemon_unreachable) + RemoteTraySummary(2, LaneConnected: true)
        // → TrayState.Running, count 2, header contains "on other machines".
    }

    [Test]
    public async Task LocalStoppedWithIdleLaneStaysStopped() {
        // Unreachable + RemoteTraySummary(0, true) → Stopped (spec: lane with nothing to show).
    }

    [Test]
    public async Task LocalRunningAddsRemoteCount() {
        // Connected snap active=1 + RemoteTraySummary(2, true) → Running, count 3.
    }

    [Test]
    public async Task NullRemoteKeepsLegacyBehavior() {
        // Omit the parameter entirely — existing Project verdicts unchanged.
    }
```

Write them as real tests against `MenuModel` (the existing tests show the pattern — `Build` is exercised through the OAPH seed).

- [ ] **Step 2: Implement** — thread `remote` into the `CombineLatest` (an eighth source, seeded `Observable.Return(default(RemoteTraySummary))` when null so `CombineLatest` still emits synchronously — the same null-source shape `lifecycleAttention` uses at `TrayViewModel.cs:102`), pass it to `Build`, swap `Project` → `ProjectAggregate`, extend `HeaderText` for the stopped-locally case.

- [ ] **Step 3: Run the tray filter + full app suite; commit**

```bash
git add src/Capacitor.App/ViewModels/TrayViewModel.cs src/Capacitor.App/ViewModels/TrayModels.cs test/Capacitor.App.Tests.Unit/TrayViewModelTests.cs
git commit -m "Aggregate remote agents into the tray state and count (#708)"
```

---

### Task 11: Launcher machine picker (own daemons only)

**Files:**
- Modify: `src/Capacitor.App/ViewModels/HomeViewModel.cs`
- Modify: `src/Capacitor.App/Views/LauncherPaneView.axaml` (machine chip next to the repository chip)
- Test: `test/Capacitor.App.Tests.Unit/HomeViewModelTests.cs` (extend)

**Interfaces:**
- Consumes: `IRemoteAgentsService.Daemons`, `JwtClaims` (via an injected `Func<CancellationToken, Task<string?>> viewerId`), existing `HostedHarnessCatalog.Build(string[]?)`.
- Produces on `HomeViewModel`:

```csharp
public sealed record MachineOption(
    string DaemonName, bool IsLocal, bool Connected, string? Platform,
    string[] RepoPaths, string[]? SupportedVendors, bool Selected);

public string SelectedMachine { get; }          // daemon name; initialized to _daemon.DaemonName
public bool RemoteMachineSelected { get; }      // false ⇒ every existing behavior is untouched
public Task<IReadOnlyList<MachineOption>> ListMachinesAsync();
public Task SelectMachineAsync(string daemonName);
```

  Ctor gains `IObservable<IReadOnlyList<DaemonInfo>>? daemons = null, Func<CancellationToken, Task<string?>>? viewerId = null, IObservable<ServerLaneStatus>? laneStatus = null` — all defaulted so existing constructions compile and behave exactly as today (no daemons observable ⇒ the picker lists only the local machine).

**Rules:**
- `ListMachinesAsync()`: first option is always the local daemon (`IsLocal: true`, `Connected` from the current availability, repo paths from the existing local flow). Remote options: latest `daemons` list where `OwnerUserId == await viewerId(ct)` (a null viewer id ⇒ no remote options — never guess ownership) and NOT the local twin (`MachineId == localMachineId && Name == local name` — pass `localMachineId` in as another defaulted ctor arg `string? localMachineId = null`). Name-based launch routing is only defined within one owner, which is why other owners' daemons are never offered (spec §5).
- `SelectMachineAsync(name)`: local name → clear `RemoteMachineSelected`, everything behaves as before. Remote name → `RemoteMachineSelected = true`; `SelectedRepoPath` set to the machine's first advertised repo path (or `ScratchRepoPath` when none); `Harnesses` for a remote machine come from that machine's `SupportedVendors` (`HostedHarnessCatalog.Build(machine.SupportedVendors)` — hold the override in an internal subject the `_harnesses` OAPH merges with: `daemon.Snapshots`-driven when local, machine-driven when remote); vendor selection resets to `DefaultVendor` when the current vendor is not offered.
- `ListRepositoriesAsync()`: when `RemoteMachineSelected`, return only the selected machine's `RepoPaths` (vendor: `Lookup` of remembered harness is skipped — remote repo paths get `DefaultVendor`), no scratch adoption, no local known-repos merge.
- `StartAsync()`: `LaunchRequest.DaemonName` = `SelectedMachine` (today it is hardwired `_daemon.DaemonName` at `HomeViewModel.cs:490`).
- Launch availability when remote selected: `Ready` iff `laneStatus` is `Connected` AND the selected daemon's latest `Connected == true`; the daemon-down notices stay local-only (a remote selection with the lane down shows `ServerLostNotice`). Extend `AvailabilityFor` usage with a remote branch rather than changing the existing static (add `internal static LaunchAvailability RemoteAvailabilityFor(ServerLaneStatus lane, MachineOption? machine)`).

- [ ] **Step 1: Failing tests** (extend `HomeViewModelTests` using its existing fakes/builders — read them first):

```csharp
    [Test]
    public async Task PickerOffersLocalPlusOwnConnectedDaemons() {
        // daemons: own "home-pc" (u1), foreign "work-mac" (u2), own twin (local machine id+name).
        // viewerId → "u1". Expect: [local (selected), home-pc]; never u2's, never the twin.
    }

    [Test]
    public async Task NullViewerIdOffersOnlyLocal() { }

    [Test]
    public async Task SelectingRemoteMachineSwitchesRepoAndVendorSources() {
        // home-pc advertises RepoPaths ["/w/repo"], SupportedVendors ["codex"].
        // After SelectMachineAsync("home-pc"): SelectedRepoPath "/w/repo",
        // ListRepositoriesAsync returns exactly that path, Harnesses built from ["codex"],
        // SelectedVendor reset off "claude".
    }

    [Test]
    public async Task LaunchCarriesTheSelectedMachine() {
        // Capture the LaunchRequest through the fake ILaunchClient: DaemonName == "home-pc".
    }

    [Test]
    public async Task RemoteSelectionRequiresTheLane() {
        // Lane Retrying ⇒ StartCommand cannot execute; lane Connected + daemon Connected ⇒ can.
    }
```

- [ ] **Step 2: Implement per the rules; keep every existing HomeViewModel test green** (the null-defaults guarantee is the mechanism).

- [ ] **Step 3: XAML** — in `LauncherPaneView.axaml`, add a machine chip mirroring the repository chip's flyout pattern (read the repository chip's markup and copy its structure; menu items bind `MachineOption.DaemonName` with a checkmark on `Selected`, disabled when not `Connected`). The chip is hidden when `ListMachinesAsync` would return a single option — bind its visibility to a new `MachinePickerVisible` OAPH (`daemons` list has ≥1 own remote machine).

- [ ] **Step 4: Build app (watch AVLN warnings), run Home filter + full suite; commit**

```bash
git add src/Capacitor.App/ViewModels/HomeViewModel.cs src/Capacitor.App/Views/LauncherPaneView.axaml test/Capacitor.App.Tests.Unit/HomeViewModelTests.cs
git commit -m "Add an own-daemons machine picker to the launcher (#708)"
```

---

### Task 12: Launch outcome correlation and denial rendering

**Files:**
- Modify: `src/Capacitor.App/ViewModels/HomeViewModel.cs`
- Test: `test/Capacitor.App.Tests.Unit/HomeViewModelTests.cs` (extend)

**Interfaces:**
- Consumes: `IServerLane.LaunchFailures` (Task 4), `IAgentDirectory.Rows` (Task 8), `WireTokens.LaunchDeniedByOwnerPrefix` (Task 1).
- Produces: `internal static string FriendlyLaunchFailure(string reason)`; ctor gains `IObservable<LaunchFailure>? launchFailures = null` and `IAgentDirectory? directory = null` (defaulted; wired in Task 13).

**Rules (spec §5 launch outcome correlation):**
- The id `RequestLaunchAgentV2` returns is request-accepted, not success. After a `Started` outcome, `StartAsync` records the normalized id in a pending set (id → recorded-at timestamp).
- `launchFailures` subscription: a failure whose `AgentId` is in the pending set (or arrived within 10 minutes of any pending entry whose id matches) sets `StartError = FriendlyLaunchFailure(reason)` on the UI thread and removes the entry. Failures for unknown ids are ignored (another client's launch).
- A row with that id appearing in `directory.Rows` (either origin) removes the pending entry — success confirmation.
- `FriendlyLaunchFailure`: `reason.StartsWith(WireTokens.LaunchDeniedByOwnerPrefix, StringComparison.Ordinal)` → `"That machine's consent policy denied the launch. Approve it there, or pre-set a rule with kcap consent."`; anything else passes through verbatim (the server already truncates to 400 chars).
- The failure may arrive BEFORE the invoke returns: `StartAsync` checks the pending set race by recording the id first, then checking a small recent-failures buffer (last 30s of failures kept with ids) and applying immediately if its id already failed.

- [ ] **Step 1: Failing tests**

```csharp
    [Test]
    public async Task DenialReasonRendersFriendly() =>
        await Assert.That(HomeViewModel.FriendlyLaunchFailure("launch_denied_by_owner: prompt_no_ui"))
            .Contains("consent policy denied");

    [Test]
    public async Task LaunchFailureAfterAcceptSetsStartError() {
        // Fake launch returns Started("agent-9"); push LaunchFailure("agent-9", "launch_denied_by_owner: default")
        // through the injected subject; StartError becomes the friendly text.
    }

    [Test]
    public async Task FailureBeforeInvokeReturnsIsStillApplied() {
        // Fake launch client that pushes the LaunchFailure BEFORE completing StartAsync's task.
    }

    [Test]
    public async Task ForeignFailuresAreIgnored() {
        // LaunchFailure("other-id", ...) leaves StartError null.
    }

    [Test]
    public async Task RowAppearanceClearsPendingSoLateFailureIsIgnored() {
        // Row "agent-9" lands in the directory, then a (stale) failure for it arrives — ignored.
    }
```

- [ ] **Step 2: Implement; run; commit**

```bash
git add src/Capacitor.App/ViewModels/HomeViewModel.cs test/Capacitor.App.Tests.Unit/HomeViewModelTests.cs
git commit -m "Correlate launch outcomes and render consent denials readably (#708)"
```

---

### Task 13: Composition root wiring

**Files:**
- Modify: `src/Capacitor.App/App.axaml.cs` (`BuildDaemonGraph`, `BuildAndShowMainWindow`)
- Modify: `src/Capacitor.App/ViewModels/MainWindowViewModel.cs` (thread new dependencies to Home/Rail; footer lane tooltip)
- Test: `test/Capacitor.App.Tests.Unit/AppStartupTests.cs` / `MainWindowViewModelTests.cs` (fix what the compiler flags; add one wiring test)

**Interfaces:** consumes everything produced above; produces the running app.

- [ ] **Step 1: Build the remote graph in `BuildDaemonGraph`** — after the `serverLane` block Task 5 added (anchor: `_serverLane = serverLane;`), add:

```csharp
        var machineId = new MachineId(_config).ReadPersisted();
        var remoteAgents = new RemoteAgentsService(serverLane, RemoteAgentsService.HttpFetch(_config, profiles));
        var repoIdentity = new RepoIdentityResolver();
        var directory = new AgentDirectory(
            service, remoteAgents, serverLane, repoIdentity, GitRepository.ResolveMainRepoRoot,
            machineId, profiles?.Resolution.ServerUrl);
        _remoteAgents = remoteAgents;
        _directory = directory;
```

  with fields `RemoteAgentsService? _remoteAgents; AgentDirectory? _directory;` disposed in the app's existing teardown next to `_service` (find the teardown with `rtk proxy grep -n "_service" src/Capacitor.App/App.axaml.cs`).
- [ ] **Step 2: Sign-in restart** — where the app learns a sign-in completed (`serverClients.NotifySignInCompleted` callers / `SignInCompleted` subscribers; `rtk proxy grep -rn "SignInCompleted" src/Capacitor.App --include='*.cs'`), subscribe once in `BuildDaemonGraph`:

```csharp
        serverClients.SignInCompleted.Subscribe(_ => { _ = serverLane.RestartAsync(); });
```

- [ ] **Step 3: Thread into the window** — extend `BuildAndShowMainWindow`'s parameters with `IAgentDirectory directory, IRemoteAgentsService remoteAgents, IServerLane lane, Func<CancellationToken, Task<string?>> viewerId, string? localMachineId`, and inside it:
  - `SessionRailViewModel` construction gains the directory + `openRemoteInWeb: actions.OpenInWeb` (already URL-building from the latest snapshot; for remote rows with the local daemon down there is no snapshot — extend `AgentActionService` with an optional `string? fallbackServerUrl` ctor arg used when no snapshot arrived, passing `profiles?.Resolution.ServerUrl`).
  - `HomeViewModel` construction gains `daemons: remoteAgents.Daemons, viewerId: viewerId, laneStatus: lane.Status, localMachineId: localMachineId, launchFailures: lane.LaunchFailures, directory: directory`.
  - `TrayViewModel` construction (in `BuildDaemonGraph`) gains `remote: TrayViewModel.SummaryFrom(directory)`.
  - `viewerId` delegate built once in `BuildDaemonGraph`:

```csharp
        Func<CancellationToken, Task<string?>> viewerId = async ct => {
            if (profiles is null) return null;
            var resolution = await new TokenStore(_config).GetValidTokensForServerAsync(
                profiles.Name, profiles.Resolution.ServerUrl!).ConfigureAwait(false);
            var token = resolution.Tokens?.AccessToken;
            return token is null ? null : JwtClaims.TryGetString(token, "sub");
        };
```

- [ ] **Step 4: Footer diagnostic** — `MainWindowViewModel` gains an OAPH `ServerLaneTip : string?` from `lane.Status.Select(s => s.Diagnostic)` (ObserveOn main thread), bound as the tooltip on the existing footer connection element in `MainWindow.axaml`. Silent-deafness stays informational (spec §6).
- [ ] **Step 5: Build the app, fix every compiler-flagged call site (tests included), run the FULL app suite**

Run: `dotnet build src/Capacitor.App/Capacitor.App.csproj` (zero warnings incl. AVLN), then `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj`
- [ ] **Step 6: Manually smoke it** — `dotnet run --project src/Capacitor.App/Capacitor.App.csproj` with a signed-in profile: the rail shows local sessions as before; if another daemon is registered for the account, its agents appear with machine badges and the launcher offers the machine.
- [ ] **Step 7: Commit**

```bash
git add -A src/Capacitor.App test/Capacitor.App.Tests.Unit
git commit -m "Wire the remote directory, picker, and tray aggregate into the app (#708)"
```

---

### Task 14: Verification pass

**Files:**
- Modify: `docs/CHANGES.md` (feature entry)
- Possibly modify: `README.md`

- [ ] **Step 1: Full solution test run**

Run: `dotnet test --solution Capacitor.slnx`
Expected: green. Known-flaky exceptions (memory-documented): NudgeLease/lease-store CPU-contention flakes, the ubuntu-only GitRepo daemon flake — rerun a failing suite alone before treating it as a regression.

- [ ] **Step 2: AOT check**

Run: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`
Expected: no output. (The app project is not AOT-published, but Cli.Core changes — `JwtClaims`, `GitRemoteReader` — ride the CLI publish.)

- [ ] **Step 3: README check** — the user-facing CLI surface did not change, but scan `README.md` for a desktop-app section describing what the app shows; if it claims local-only visibility, update the sentence to mention agents on the user's other machines. No section → no change.

- [ ] **Step 4: `docs/CHANGES.md` entry** — add a feature entry (match the file's existing format) recording the two invariants worth protecting: fail-open twin dedup (never hide an agent on uncertain identity) and the machine picker's own-daemons-only rule (name-based launch routing is defined only within one owner).

- [ ] **Step 5: Commit**

```bash
git add docs/CHANGES.md README.md
git commit -m "Record the remote-visibility invariants in CHANGES (#708)"
```

---

## Self-review notes (already applied)

- Spec coverage for slice 1: contracts (§1 → Task 1), lane (§2 → Tasks 4-5), viewer identity (§2 → Tasks 2, 13), caches (§3 → Task 6), twin/dedup/fail-open (§3 → Task 8), grouping (§3 → Tasks 7-9), origin precedence for *rows* (§3 → Task 8; open-workspace rebinding is slice 3), tray/launcher gating (§6 → Tasks 10-11), machine picker + own-daemons rule (§5 → Task 11), launch correlation + denial rendering (§5/§6 → Task 12), silent-deafness diagnostic (§6 → Tasks 4, 13), lane-loss grey-out (§6 → `RemoteStale`, surfaced via row opacity binding on `IsRemote` rows in Task 9's XAML — bind `Opacity` on the badge/row to the directory's `RemoteStale` if trivially reachable, else defer the visual to slice 2 and keep the retained-cache behavior, which is the substance).
- Deliberately NOT in slice 1 (per spec §9): remote permission cards and attention, remote stop, chat, terminal, access watches, the `server_request_id` daemon wire change, `PermissionResponsePayload`/`AcpInteractionOption`/`AcpEventEnvelope` contract types (they land with slice 2's plan so they are born next to their consumers).
- Types cross-checked: `AgentRow` fields used by Tasks 9-10 all exist in Task 8's record; `RemoteTraySummary`, `MachineOption`, `LaunchFailure`, `ServerLaneStatus` each defined exactly once.
