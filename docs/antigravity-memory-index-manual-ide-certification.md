# Manual IDE certification — Antigravity memory-index injection

**Why this exists.** The automated cert
(`test/Capacitor.Cli.Tests.Unit/SessionStartMemory/AntigravityMemoryIndexLiveCertTests.cs`) drives a
real `agy -p` turn and asserts the model echoes a nonce. It **fails**, and not because of anything on
our side: `agy`'s print mode fires the `PreInvocation` hook, reads our stdout, and then discards the
returned `injectSteps`. Our half is verified by that same run — the hook fires, a correct payload is
emitted, the run completes inside the hook budget — so the automated cert is kept as the regression
test that will notice upstream starting to honour it.

Observed on `agy` **1.1.10**, re-confirmed unchanged on **1.1.11** (2026-08-07).

The Antigravity **IDE** is a different runtime that shares the same `~/.gemini` plugin config. Nothing
measured says the IDE behaves like print mode, and nothing says it doesn't. Only a human driving the
GUI can answer that, which is what this procedure is for.

**This is the only path to certifying the IDE row.** Do not flip the README matrix row from `pending`
on the strength of unit tests or the automated cert — the row must record what was observed.

Every command below was executed while writing this document; none is inferred.

---

## Prerequisites

- Antigravity IDE installed and signed in.
- `kcap` on `PATH` and logged in (`kcap whoami`).
- The kcap plugin installed for Antigravity, which is what registers the hook:

```bash
kcap plugin install --antigravity
```

- Confirm injection is not disabled for your profile — the key is `disable_memory_index`, and it must
  be `false` or absent:

```bash
kcap config show
```

---

## Step 1 — save a nonce memory

Generate a value that cannot plausibly arrive any other way:

```bash
openssl rand -hex 16
```

Save it through the `kcap-memory` MCP `save_memory` tool from any agent session (there is no `kcap`
CLI verb for this — `kcap mcp memory` starts the MCP **server**, it is not a command group). Use
`audience: user`, and put the nonce in the **`description`**.

> **The description is load-bearing, not a style choice.** The injected index is one
> `slug: description` line per memory — the **content** is not included, and is reachable only through
> a `get_memory` call. A nonce hidden in `content` cannot be echoed without a tool call, which defeats
> the whole question the certification asks. The automated harness does the same thing for the same
> reason (`MemoryIndexLiveCertHarness.SaveNonceMemoryAsync`).

---

## Step 2 — prove our side actually emits it

This is the control that separates "the IDE dropped it" from "there was nothing to drop". Run the real
`PreInvocation` hook the IDE runs:

```bash
TP="/tmp/agy-cert-$(openssl rand -hex 4).jsonl"; : > "$TP"
ID="a1b2c3d4-0000-4000-8000-$(openssl rand -hex 6)"
printf '{"conversationId":"%s","transcriptPath":"%s","workspacePaths":["%s"]}' "$ID" "$TP" "$PWD" \
  | kcap hook --antigravity PreInvocation
```

Expect a few KB of `{"injectSteps":[{"userMessage":"…"}]}` containing your nonce.

> **All three fields are required, and omitting one fails SILENTLY.** The handler returns zero bytes
> with exit 0 when `conversationId` or `transcriptPath` is missing — before it ever consults the memory
> index. An empty result therefore does not mean "no index"; it may mean the payload was malformed.
> This wasted a debugging cycle while writing this document. If you get zero bytes, re-check the
> payload before concluding anything.

Use a **fresh** `conversationId` each time: injection is once-per-conversation.

If the nonce is absent here, stop — the fault is upstream of the IDE and running the GUI would prove
nothing.

---

## Step 3 — positive case

1. Open a **brand-new** Antigravity IDE conversation. Not a resumed one — a resumed conversation may
   legitimately inject nothing.
2. Ask, in a form that cannot be satisfied from the workspace or a tool:

   > Without using any tools, and without reading any files: if your context contains a string of the
   > form `kcap` … followed by 32 hex characters, reply with ONLY that string. Otherwise reply NONE.

3. Record the answer verbatim. The nonce is a pass. `NONE`, a refusal, or a different value is a fail
   — and note **which**, because a refusal is a different failure from an absent index.

---

## Step 4 — negative control

A positive case alone does not prove the mechanism: the nonce could have reached the model some other
way (a stray file, a prior conversation, a tool call).

```bash
kcap config set disable_memory_index true
```

Open **another** brand-new conversation, ask the identical question, and expect `NONE`. If the nonce
still appears, the positive case did not measure injection and neither result counts.

Restore afterwards:

```bash
kcap config set disable_memory_index false
```

> **Order matters.** If injection never works at all, the negative control passes for the wrong
> reason. It is only meaningful *after* the positive case passes — read the two as a pair.

---

## Step 5 — record and clean up

Archive the nonce memory (`archive_memory` on the `kcap-memory` MCP server). Leaving it in place
pollutes everyone's index, and the automated harness refuses to start when a stale nonce may be
present — so a forgotten one blocks the next cert run.

Comment on **AI-1467** with:

- Antigravity IDE version (Help → About, or the settings pane)
- `agy --version` and `kcap --version`
- Positive result: nonce echoed verbatim / `NONE` / refused / other
- Negative result: `NONE` / nonce still present

Then update the Antigravity row of the README memory-index matrix to match what was observed. If the
positive case fails, the row stays `pending` and the failure mode is recorded plainly — an IDE that
also discards `injectSteps` is an upstream limitation worth stating, exactly as the CLI's is.
