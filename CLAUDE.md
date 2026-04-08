# Kurrent Capacitor CLI

**File paths:** CLI source at `src/kapacitor/`, npm packages at `npm/`, Claude Code plugin at `plugin/`, unit tests at `test/kapacitor.Tests.Unit/`, integration tests at `test/kapacitor.Tests.Integration/`.

## What this project does

The kapacitor CLI records Claude Code sessions by forwarding hook payloads and transcript data to a Capacitor server. It also hosts an agent daemon for remote Claude CLI management and provides PR review context via MCP tools.

## Tech stack

- .NET 10, NativeAOT compiled
- SignalR client for real-time server communication
- TUnit for testing, WireMock.Net for HTTP mocking

## Building

```bash
dotnet build src/kapacitor/kapacitor.csproj
```

## Running tests

Tests use TUnit on Microsoft Testing Platform. Run directly as executables:

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj
dotnet run --project test/kapacitor.Tests.Integration/kapacitor.Tests.Integration.csproj
```

## Publishing

AOT publish for the current platform:

```bash
dotnet publish src/kapacitor/kapacitor.csproj -c Release
```

Always verify no IL3050/IL2026 AOT warnings after changes:

```bash
dotnet publish src/kapacitor/kapacitor.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'
```

## Common mistakes to avoid

- **AOT warnings only show on publish** — `dotnet build` does NOT surface IL3050/IL2026 trimming warnings. Run `dotnet publish -c Release` after changes.
- **JsonArray collection expressions** — `[item1, item2]` compiles to `Add<T>()` which requires dynamic code. Use `new JsonArray(item1, item2)` constructor instead.
- **TUnit test filtering** — Use `--treenode-filter` with glob syntax, NOT `--filter`.
- **macOS AOT binary code signing** — After copying an AOT binary, run `codesign --force --sign -` to re-sign.
