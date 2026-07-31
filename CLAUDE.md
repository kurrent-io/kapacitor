# Kurrent Capacitor CLI

**File paths:** CLI source at `src/Capacitor.Cli/`, shared core at `src/Capacitor.Cli.Core/`, daemon at `src/Capacitor.Cli.Daemon/`, npm packages at `npm/`, Claude Code plugin at `kcap/`, unit tests at `test/Capacitor.Cli.Tests.Unit/`, integration tests at `test/Capacitor.Cli.Tests.Integration/`.

## What this project does

The `kcap` CLI records Claude Code sessions by forwarding hook payloads and transcript data to a Kurrent Capacitor server. It also hosts an agent daemon for remote Claude CLI management and provides PR review context via MCP tools.

Review flows use a vendor-neutral catalog-start v2 protocol: reserved `spec-review`/`code-review`
aliases select an explicit or server-default reviewer independently of the driver. Codex setup
registers `kcap-flows` without auto-approval and tracks only newly-created global TOML entries in
`mcp-ownership-v1.json`, so uninstall preserves manual/customized MCP configuration. Daemons retain
the string unattended-vendor list for compatibility and additionally advertise structured
per-vendor CLI/launcher-policy capabilities. Cursor serves borrowed review context from a
daemon-owned snapshot (dirty tracked and non-ignored untracked files, refreshed between rounds),
because its zero-interaction modes may write; Claude has no borrowed-review containment strategy
and therefore fails closed to an owned worktree.

Copilot also borrows from a daemon-owned snapshot, but its read boundary is an **OS sandbox**
(`BorrowedReviewSandbox`, `sandbox-exec`, `(deny default)`) rather than a tool clamp, because the
read tools it needs to see the snapshot are the same tools that could be pointed elsewhere. The
profile grants nothing under the user's home: a per-launch `HOME`/`TMPDIR` state directory replaces
the vendor's own config and cache grants, `BorrowedReviewAuthBroker` replaces the keychain grant with
a token from the daemon's own environment, and `BorrowedReviewRuntimeRoots` replaces whole-prefix
grants with software subdirectories derived from the vendor binary — never the prefix's `etc`/`var`. Support is
conjunctive: macOS/ARM64 **and** `sandbox-exec` **and** a brokerable token, or the capability is not
advertised and the server answers `vendor_containment_unreadable` with the `context-only` remedy.
Enforcement is asserted by tests that run a real process under the profile, because a model-layer
refusal is not containment evidence.

The daemon never *looks* for a credential: no keychain read, no prompt, no cache, no persistence, no
default command. It forwards a token the operator exported, or — for a supervised daemon, whose unit
file must not hold a secret — runs the single command the operator configured in
`KCAP_COPILOT_TOKEN_CMD`, and only when an actual borrowed launch needs one. Availability is
deliberately passive (configuration presence, never execution): probing by running the command at
startup would mint a credential nobody asked for, so a configured-but-broken command instead fails at
spawn with the coded `borrowed_review_auth_unavailable`. Service units are written owner-only, and
installation fails rather than leaving one readable.

Borrowed-review capability is **trust-by-default**: a vendor advertises it whenever its runtime
factory declares a containment strategy, for whatever build of the vendor CLI is installed and on
every platform. It is deliberately not gated on the installed binary matching a validated-build
record — a vendor auto-update would then silently withdraw the capability and reviewers would fall
back to a stale committed base. The daemon logs the CLI version it probed at startup (a startup
observation, not a launch-time fact) and does no automated drift detection; a defective vendor
release is handled by a human report and a corrected record. See
`docs/superpowers/specs/2026-07-27-ai1528-trust-by-default-borrowed-review-design.md` in kcap-server.

A daemon-owned launch-consent gate (AI-1623, `LaunchConsent*` in `Capacitor.Cli.Daemon/Services`)
sits in front of every SERVER-driven launch: `LaunchConsentStore` owns `{stateDir}/consent.json` as
the single writer, degrading a missing/corrupt file to the upgrade-safe default (`allow`, so no
pre-existing daemon bricks on update) rather than failing closed. Every decision — rule-matched or
human — is appended to `consent-decisions.jsonl` (1MB rotation, 0600 from first byte) for the
`kcap daemon consent log` verb and the eventual desktop Activity feed, and a non-owner denial
surfaces to the server as the coded `launch_denied_by_owner` reason. The policy is queried/edited
live over the local control socket via the append-only `FrameType` values 11–14
(`ConsentSubscribe`/`ConsentResolve`/`ConsentRulesGet`/`ConsentRulesPut`) and 72–74
(`ConsentPending`/`ConsentRules`/`ConsentAck`); `kcap daemon consent {show,set-default,allow,deny,remove,log}`
is the CLI surface and never blocks a launch waiting on a terminal prompt.

## Tech stack

- .NET 10, NativeAOT compiled
- SignalR client for real-time server communication
- TUnit for testing, WireMock.Net for HTTP mocking

## Building

```bash
dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj
```

## Running tests

Tests use TUnit on Microsoft Testing Platform. Run directly as executables:

```bash
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj
dotnet run --project test/Capacitor.Cli.Tests.Integration/Capacitor.Cli.Tests.Integration.csproj
```

## Publishing

AOT publish for the current platform:

```bash
dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release
```

Always verify no IL3050/IL2026 AOT warnings after changes:

```bash
dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'
```

## Issues and pull requests

This is a public repository — we develop in the open.

- **Open issues in GitHub Issues**, not Linear. Linear auto-imports GitHub issues, so there is no need to create the issue in Linear by hand.
- **PRs must reference both the Linear issue and the GitHub issue.** Put these references in the PR *description*, not the title (the title stays clean and human-readable). Reference the GitHub issue with a closing keyword (e.g. `Closes #123`) and include the Linear issue (e.g. `AI-774`) so Linear links the PR back to the imported issue.

## Dos and donts

- DO use `JsonElementExtensions` instead of checking JSON value kind.
- DO NOT use Linear issue numbers in comments. If you absolutely need an issue number, use the GitHub issue number.
- DO NOT get too verbose in comments. Write self-explanatory code instead.

## Common mistakes to avoid

- **AOT warnings only show on publish** — `dotnet build` does NOT surface IL3050/IL2026 trimming warnings. Run `dotnet publish -c Release` after changes.
- **JsonArray collection expressions** — `[item1, item2]` compiles to `Add<T>()` which requires dynamic code. Use `new JsonArray(item1, item2)` constructor instead.
- **TUnit test filtering** — Use `--treenode-filter` with glob syntax, NOT `--filter`.
- **macOS AOT binary code signing** — After copying an AOT binary, run `codesign --force --sign -` to re-sign.
- **README sync on CLI changes** — Any change to user-facing CLI surface (new command, new/renamed/removed flag, changed default behavior, new prerequisite) must update `README.md` in the *same* PR. Check both the quick-start (`## Getting started`) and the per-command section under `## CLI commands`. Updating only `src/Capacitor.Cli.Core/Resources/help-*.txt` is not enough — the README is the public-facing docs. This has been missed repeatedly and has required follow-up doc-only PRs (#60, #61).
