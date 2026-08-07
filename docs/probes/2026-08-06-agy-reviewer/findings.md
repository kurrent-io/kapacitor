# Antigravity CLI (`agy`) unattended-reviewer probe — containment, auth and the read boundary

**Binary:** `agy --version` → `1.1.10`, resolved at `~/.local/bin/agy` (169,718,336 bytes).
**Host:** macOS 26.5.2, arm64, .NET 10.0.9.
**Dates:** 2026-08-06 (auth + capability surface), 2026-08-07 (containment enforcement, permission
table, binary-namespace re-check).

Everything below is a **measurement**. Where a claim could have been inferred from the agent's own
words, it is instead an observation of the filesystem, the process table or the binary — a
model-layer refusal is not containment evidence, and two of the issue's original premises were
falsified by exactly this discipline (§4).

The enforcement half of this probe is promoted to a gated test:
`test/Capacitor.Cli.Tests.Unit/Acp/AntigravityContainmentTests.cs`.

---

## 1. The contained-launch recipe

This is the launch shape every measurement below was taken under. `HOME` is a fresh, empty,
per-launch directory; `TMPDIR` sits inside it.

```sh
env -i \
  PATH=/usr/bin:/bin:/usr/sbin:/sbin:$HOME/.local/bin \
  HOME=<per-launch home> \
  TMPDIR=<per-launch home>/tmp \
  AGY_ADC_AUTH=1 \
  GOOGLE_CLOUD_PROJECT=<project> \
  GOOGLE_APPLICATION_CREDENTIALS=<real home>/.config/gcloud/application_default_credentials.json \
  agy -p "<prompt>" --output-format stream-json
```

`GOOGLE_APPLICATION_CREDENTIALS` deliberately points **outside** the relocated home — an absolute
path into the operator's real tree. That is the whole auth mechanism: it is an environment path, not
HOME-anchored file state (§4.2).

Production adds `--disable-slash-commands` and `--print-timeout <turn ceiling>s`, and passes
`--conversation <id>` on every turn after the first. It does **not** pass
`--dangerously-skip-permissions` or `--sandbox` (§5). `AntigravityHostedAgentRuntimeFactory.BuildTurnPsi`
is the single builder for that vector, and the containment test drives the real one rather than a
re-derivation.

### 1.1 Wire shape

NDJSON on stdout. The envelope key is **`event`**, with three values: `init` → `step_update`* →
terminal `result`. `init.conversation_id` carries the id. A tool step's descriptor is `tool_info`,
and its tool name is **`name`** — not `tool_name`.

```json
{"event":"init","conversation_id":"44508275-4e2f-4926-a5d9-f8c9af02bc27",
 "init":{"cwd":"…","tools":[…54 names…],"permission_mode":"request-review"}}
{"event":"step_update","step_update":{"step_index":1,"state":"DONE","step_type":"agent_response",
 "text_delta":"\n","duration_seconds":1.409684,
 "usage":{"input_tokens":11336,"output_tokens":35,"thinking_tokens":34,
          "cache_read_tokens":0,"total_tokens":11371}}}
{"event":"result","result":{"status":"SUCCESS","response":"OK\n","num_turns":1,"usage":{…}}}
```

Default `permission_mode` in print mode is `request-review`.

---

## 2. Containment — the four observations

One real turn (`Reply with the single word OK. Do not use any tools.`) under the production
per-launch home, built by `AntigravityReviewerHome.Create`, spawned by the production
`BuildTurnPsi`. Result: `status=SUCCESS`, exit `0`, ~4s.

| # | Observation | Measured |
|---|---|---|
| 1 | No kcap hook fired — no file under `~/.config/kcap/logs/` **or** `~/.config/kcap/watchers/` keyed on the conversation id | **none** (0 new keyed entries against an 8,512-entry pre-existing logs dir) |
| 2 | No process on this host carries the conversation id, in either spelling | **none** |
| 3 | `agy`'s Library writes landed **inside** the per-launch home | `<home>/Library/Caches/ms-playwright-go/1.57.0` — created under the RELOCATED home |
| 4 | The operator's real agy conversation store gained no entry | `~/.gemini/antigravity-cli/{brain/<id>, conversations/<id>.db}` absent; same for the GUI root `~/.gemini/antigravity/` |

Positive control for 3 and 4 together — the state exists, and this is about *where*:

```
<home>/.gemini/antigravity-cli/brain/<id>/.system_generated/logs/transcript_full.jsonl
<home>/.gemini/antigravity-cli/conversations/<id>.db  (+ .db-wal)
<home>/.gemini/antigravity-cli/presence/<id>.lock
<home>/.gemini/antigravity-cli/cache/last_conversations.json
```

Full home shape after one turn: `.gemini/{config,antigravity-cli/{brain,conversations,log,cache,
presence,knowledge,scratch,crashes,updater,bin,builtin}}`, `Library/Caches`, `tmp`.

### 2.1 Why observation 3 is the load-bearing one

The credential is *not* under `~/.gemini` (§4.2) — it is under `~/Library` and the keychain. So the
sharpest available question is whether `agy` resolves `~/Library` from the **real** user or from
`$HOME`. If it were the real user, a relocated HOME would contain nothing that matters and the
containment claim would be a fiction.

It resolves from `$HOME`. Two asymmetric consequences, both worth stating:

- **Writes are contained.** Reviewer conversations, caches and brain state land in the per-launch
  directory and are deleted with it.
- **The keychain is NOT contained** — keychain access is not HOME-derived. Accepted: this design
  never reads the keychain (auth is the ADC env path), and a `(deny default)` OS sandbox — the thing
  that would actually bound it — is explicitly out of scope. A future borrowed lane must revisit it.

### 2.2 The positive control that must not be run

The same turn under the operator's **real** `HOME` *does* fire the kcap capture hooks and spawn a
watcher: `agy -p` loads and fires `~/.gemini/config/plugins/kcap/hooks.json` in print mode. That is
measured, and it is the reason the reviewer's home must never be the real one — it would
double-capture the conversation the runtime is already parsing from NDJSON, and the watcher child
would (on an unfixed build) hold agy's own stdout open, which for an exec-per-turn runtime means
every turn blocks forever.

It stays a recorded measurement rather than a test case: running it would record a real session
against the operator's server and spawn a watcher over the reviewer's conversation — the exact damage
the containment exists to prevent.

### 2.3 One instrument defect, found by the control

The containment test's **first live run failed** — and the containment was working perfectly. The
positive control (`TreeMentions`) enumerated the home with a default `EnumerationOptions`, whose
`AttributesToSkip` skips `Hidden`; .NET reports every Unix dot-entry as hidden, so the entire
`.gemini` subtree — which is where `agy` writes *all* of its conversation state — was invisible to
the search. Fixed by setting `AttributesToSkip = FileAttributes.None` explicitly.

Worth recording because the failure mode is inverted: without the positive control the test would
have passed, and its four absence-assertions would have been silently unfalsifiable in the same way.

Each observation was then mutation-checked against a known-present value, and each detected it:

| Observation | Mutant | Result |
|---|---|---|
| 1 (kcap logs) | search an existing log id against an empty before-set | **detected** `00134e9d….log` |
| 2 (process table) | search a string certain to be in `ps` output | **detected** 6 command lines |
| 4 (operator store) | search an existing `brain/<id>` | **detected** |

---

## 3. The permission table — `--dangerously-skip-permissions` IS the read boundary

Same contained launch, same prompt: *"Use view_file to read the absolute path /etc/hosts and print
its first line."* `/etc/hosts` is absolute and outside the workspace.

| Flag | Tool step | `tool_info.error` | Content read? | `result` |
|---|---|---|---|---|
| absent (production) | `state: ERROR` | `{"type":"TOOL_ERROR","message":"User denied permission for read_file(/private/etc/hosts)."}` | **no** | `status: SUCCESS`, `response: ""` |
| `--dangerously-skip-permissions` | `state: DONE` | — | **yes** (`output: "15 lines, 366 bytes"`) | `status: SUCCESS`, non-empty response quoting the file |

stderr on the denied arm, verbatim:

```
jetski: no output produced — a tool required the "read_file" permission that headless mode cannot
prompt for, so it was auto-denied. Add an allow-rule under permissions.allow in settings.json
(e.g. read_file(<target>)). Alternatively, re-run with --dangerously-skip-permissions to
auto-approve all tools.
```

Three consequences for anything built on this:

1. **The flag is the entire read boundary.** The reviewer deliberately does not pass it: it works in
   a daemon-**owned** worktree and needs only to read that, which the headless defaults already
   permit. Adding the flag would grant a reviewer shell access and whole-filesystem reads it has no
   reason to hold.
2. **Assert the TYPED shape, never the message text.** The wire tool name is `view_file`; the name
   inside the message is `read_file`. They disagree, and a message-text assertion pins the wrong one.
3. **A denial ENDS the turn.** No closing `agent_response`, an empty `result.response`, and a second
   instruction in the same prompt never runs. A test that waits for a post-denial summary will
   **hang**, not fail — bound every wait.

---

## 4. Two falsified premises

The issue's original design sketch rested on two claims. Both are wrong.

### 4.1 `ANTIGRAVITY_API_KEY` / `ANTIGRAVITY_TOKEN` do not exist

Re-verified 2026-08-07 against the shipped 1.1.10 binary:

```sh
strings -a $(which agy) | grep -cE 'ANTIGRAVITY_(API_KEY|TOKEN)'                # → 0
strings -a $(which agy) | grep -oE 'ANTIGRAVITY_[A-Z0-9_]+' | sort -u | wc -l   # → 36
```

All 36 are IDE/sidecar plumbing or telemetry event names — `ANTIGRAVITY_SIDECAR_UI_TOKEN`,
`ANTIGRAVITY_LS_ADDRESS`, `ANTIGRAVITY_CSRF_TOKEN`, `ANTIGRAVITY_EXTENSION_ACTIVATED`,
`ANTIGRAVITY_CONVERSATION_ID`, … There is no API-key or bearer-token variable at any point in the
namespace. `AGY_ADC_AUTH` is present (3 occurrences) and is the only auth switch that matters.

### 4.2 Auth is NOT HOME-anchored file state

A **byte-complete copy of `~/.gemini`** (69 MB, everything except the kcap plugin directory) into a
relocated HOME **still demands fresh OAuth**. The credential is not in `~/.gemini` at all — the
candidates are `~/Library/Application Support/Antigravity`,
`~/Library/Preferences/com.google.antigravity.plist`, and a keychain item (service `gemini`, account
`antigravity`).

This is what makes the whole design work rather than what breaks it: because auth is *not* file
state under HOME, a **completely empty** relocated HOME authenticates fine via
`AGY_ADC_AUTH=1` + `GOOGLE_CLOUD_PROJECT` + `GOOGLE_APPLICATION_CREDENTIALS`. The containment does
not have to smuggle a credential into the isolated home, and the isolated home does not have to be
seeded with anything.

It also retires a third premise by making it moot: "APFS-clone the binary into the launch state root
so the sandbox profile grants nothing under home". There is no sandbox profile — containment here is
the Kiro shape (an isolated `HOME`), not the Copilot shape (`sandbox-exec`).

---

## 5. What production deliberately does not do

- **No `--dangerously-skip-permissions`** — §3. The soft-deny of shell and out-of-workspace
  operations *is* the desired unattended posture.
- **No `--sandbox`** — a vendor-side terminal restriction overlapping what containment already
  provides, and unprobed.
- **No borrowed-review lane.** `SupportsBorrowedReviewFlow` stays false; reviews fail closed to an
  owned worktree. `BorrowedReviewRuntimeRoots.MeasuredPrefixes` is `["/opt/homebrew"]` and `agy`
  installs to `~/.local/bin/agy` — **under `$HOME`**, which that roots shape refuses by design.
- **No credential gate on advertisement.** Availability is binary presence
  (`CliResolver.Exists(config.AntigravityPath)`) plus operator consent. An operator without durable
  auth gets a bounded, coded spawn-time failure rather than a vendor that silently disappears.

---

## 6. Reproducing

```sh
KCAP_ANTIGRAVITY_REVIEWER_LIVE=1 GOOGLE_CLOUD_PROJECT=<project> \
  dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj \
    -- --treenode-filter "/*/*/AntigravityContainmentTests/*"
```

Without both variables it skips in ~15 ms and spends nothing — CI has neither an `agy` binary nor
Google credentials. The test is read-only against `~/.config/kcap` and `~/.gemini`: it snapshots
names before the run and keys every observation on that run's own conversation id, so a live daemon
writing its own files alongside it can neither make it pass nor make it fail.
