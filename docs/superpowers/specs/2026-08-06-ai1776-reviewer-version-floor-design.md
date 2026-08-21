# AI-1776 — Reviewer version gate: minimum floor, not exact match — Design

## 0. What this is

`KiroReviewerCapability` and `GeminiReviewerCapability` are the only two version-gated reviewers in
the product. Both currently fail closed the instant the installed vendor build **changes** in any
direction, because `ReviewerVersionAffirmations.Decide` compares with
`string.Equals(installed, affirmed, StringComparison.Ordinal)`.

This changes that one comparison to a **minimum version floor**: the recorded value stops meaning
"the exact build the operator accepted" and starts meaning "the oldest build this daemon will run".

Nothing else about either gate changes. The operator consent flags
(`KCAP_KIRO_UNATTENDED_REVIEWER` / `KCAP_GEMINI_UNATTENDED_REVIEWER`) are untouched, Kiro's POSIX
precondition is untouched, and the per-vendor consent text is untouched.

## 1. This reverses a decision that was written down — deliberately

`GeminiReviewerCapability`'s own class doc rejects exactly this design:

> *"A **minimum-version floor** was considered and rejected: it would assume the allowlist's
> semantics can only improve, which is an assumption about someone else's code, and would silently
> admit a future build that changed matching to prefix, flipped empty-list semantics, or let
> repository settings win."*

That objection is not wrong, and this spec does not pretend it is. It is **overruled on stated
grounds** (owner decision, 2026-08-06):

> *"Treat the version already certified as the minimum version and that's it. We'll assume new
> versions work fine until we learn of bugs and fix them."*

The reasoning that makes it defensible rather than merely decided:

1. **The same doc records that the stricter model already failed in production, twice over.** The
   maintainer-curated certified set took the Gemini reviewer offline at `0.54.0`, *one patch* ahead
   of certified `0.53.0`, recoverable only by a kcap release — "every Gemini release repeated it".
   Affirmation fixed the release-coupling but kept the treadmill, just relocated onto the operator.
2. **The rest of the product already made this trade.** Copilot and Cursor borrowed review are
   trust-by-default, on the reasoning that *"a vendor auto-update would then silently withdraw the
   capability and reviewers would fall back to a stale committed base."* Claude's certification is a
   `>=`-capable **range** defaulting to unrestricted. No hosted agent is version-gated at all. Kiro
   and Gemini are the outliers.
3. **The response mechanism survives.** "Fix them when we learn of bugs" is executable here: `kcap
   daemon reviewer affirm` becomes *raise the floor past the bad build*, and it takes effect without
   a kcap release. The old model had no way to express "anything newer is fine"; the new one has no
   way to express "not this one specific build" — but it can express "not this one or anything
   older", which is the actionable half.

**The residual, stated once, plainly:** a future vendor build that *weakens* its own containment
behaviour is admitted silently. Per vendor:

| Vendor | What containment rests on | What a bad newer build costs |
|---|---|---|
| **Gemini** | the build's MCP allowlist being an exclusive exact-match gate the reviewed repo cannot widen | repository-controlled process execution under the daemon uid — the doc's own phrase is *"a broken MCP gate degrades to repository-controlled process execution"* |
| **Kiro** | the build honouring `KIRO_HOME` and reading no other global config source | the reviewer's transcript-bearing home stops being isolated |

A second vector, raised in review and **not** obvious from the above: because the floor can be
*lowered*, an operator who runs `affirm` while an older build is installed re-admits that build **and
everything above it**, including builds a higher floor had previously excluded. The asymmetry with the
old model is real and worth naming — exact-match required a positive act to admit *each* new build,
whereas a floor requires a positive act to *exclude* an old one, and re-admitting a known-bad old
build costs one command. This is accepted rather than mitigated: the operator lowering their own
floor is the same party who consented to the reviewer at all, and the alternative (a monotonic,
never-lowerable floor) would leave an operator who upgraded into a broken build with no way back
short of deleting state files.

Both of the table's risks were already accepted at the moment of the operator's *first* affirmation — what changes
is that the acceptance now carries forward across upgrades instead of being re-taken each time. That
is the whole of the change, and it should be described that way in the code comments rather than
deleting the rejected-alternative paragraph, which stays as the record of why the boundary sits
where it does.

## 2. The rule

`Decide(installed, floor)`, evaluated **in this order**:

| # | Condition | Result |
|---|---|---|
| 1 | `installed` missing / empty / whitespace | **Unresolved** — refuse |
| 2 | no floor recorded (absent, empty, or unreadable file) | **NoMinimumRecorded** — refuse (new arm; §4) |
| 3 | **both** sides fail to parse as a version | ordinal equality: equal → **MeetsMinimum**, else **BelowMinimum** (§3) |
| 4 | **exactly one** side fails to parse | **Incomparable** — refuse (new arm; §2.3) |
| 5 | `installed >= floor` | **MeetsMinimum** — allow |
| 6 | `installed < floor` | **BelowMinimum** — refuse |

Two corrections to an earlier draft of this table, both from review, both load-bearing:

- **Row 1 does NOT include "unparseable".** `Unresolved` today means *only* that the installed string
  is null/empty/whitespace — there is no version parsing in this type at all. Widening it to cover
  unparseable input would refuse a pair that is allowed today (see §3), which is the one thing this
  change must not do. `Unresolved` keeps its exact current meaning.
- **Row 3 requires BOTH sides to fail**, and a one-sided failure is its own arm (row 4). Two earlier
  drafts got this wrong in opposite directions — the first refused whenever the *installed* side was
  odd (breaking monotonicity, §3), the second ordinal-compared whenever *either* side was odd, which
  silently refused a genuine upgrade whenever the recorded floor happened to be unparseable. Ordinal
  equality is only sound when both values are in the same domain; when they are not, we do not have
  an ordering and must not fabricate one from string equality.

Row 2 covers the empty-file case without a separate arm, because `ReviewerVersionStore.Affirmed`
already returns `null` for empty, whitespace-only, unreadable, or absent content — one null, one arm.

### 2.1 The floor never auto-advances

Seeing a newer build does **not** rewrite the record. The floor moves only when an operator runs
`affirm`. An auto-advancing floor would silently convert every upgrade into a new downgrade barrier,
re-creating a treadmill in the opposite direction — and would make the record no longer mean what an
operator set.

### 2.2 A downgrade below the floor is still refused

"Minimum" is load-bearing, not decorative. The owner's instruction covers *new* versions; an older
build than the one we certified is not that case, and refusing it costs nothing an operator cannot
undo with one command.

### 2.3 `Incomparable`, and why the remedy actually works

Row 4 fires when one value orders and the other does not — e.g. floor `1.2.3.4.5` (five components;
`System.Version` tops out at four) against installed `2.0.0`. Refusing here is unsatisfying, since the
installed build is obviously newer, but the alternatives are worse: ordinal-comparing them refuses
the same upgrade while *claiming* it is "below minimum", and admitting them means asserting an order
we did not compute.

What makes the refusal acceptable is that **`affirm` provably clears it, in both directions**:

- floor odd, installed orders → `affirm` writes the installed value, so the floor now orders. Fixed.
- installed odd, floor orders → `affirm` writes the installed value, so *both* are now that same odd
  value → row 3, ordinal-equal → allowed. Fixed.

So the denial text must name `affirm` as the remedy, and it is not the dead-end loop it would be if
`affirm` re-wrote the same unusable value.

### 2.4 Rejected: widening the parse to make row 4 unreachable

The obvious alternative is a tolerant dotted-numeric comparison (split on `.`, compare components
numerically, no four-component ceiling), which would order `1.2.3.4.5` fine and empty row 4 of
everything except genuinely malformed input.

**Rejected on scope, not on merit.** §6 requires one shared comparison, and the other caller is
`DaemonRunner.CliVersionAllowed` — the *Claude certification* gate. Widening what parses there widens
what that gate admits, since it denies anything `Version.TryParse` rejects. Loosening a second
vendor's security gate as a side effect of this issue is not a trade this spec gets to make silently.
If row 4 is ever observed in the wild, that is the fix to reach for, as its own change with its own
review.

**This argument is load-bearing only once §6's extraction is done, and §6 is in this PR.** Until the
two paths share a parser they are independent, and a maintainer could widen one without touching the
other — the scope constraint would then be a convention rather than a structural fact. That is
another reason §6 is not optional tidying.

Also rejected: tightening `VendorVersionResolver.ExtractVersionToken` to reject what `TryParse`
rejects. It is shared beyond this path, and it would convert a vendor that emits a five-component
version from "works" (today, via ordinal equality) into `Unresolved` — a regression, and a silent one.

## 3. Monotonicity — the property that makes this safe to ship

**The new rule must never refuse a pair the current rule allows.** Today's only allowed pair is
`installed == floor` (ordinal, both non-blank). Two cases, both covered:

- both parse → equal versions satisfy `>=` → row 5, allowed;
- both fail to parse → row 3, ordinal-equal, allowed.

A pair cannot be ordinal-equal with exactly one side parsing (identical strings parse identically),
so **row 4 — `Incomparable` — is unreachable for an allowed-today pair** and cannot break the
property. That is the specific check the second draft failed, and it is worth re-running by hand
against any future change to the row order.

An earlier draft got this wrong and review caught it. The counterexample: a record hand-written as
`daily-20240806` with the same string installed is **allowed today** (ordinal equality does not care
about format), but the draft mapped unparseable-installed to `Unresolved → refuse`. That would have
been a regression introduced by a change whose entire purpose is to loosen the gate — the worst
possible shape for this PR. The symmetric row 3 removes the whole class rather than patching the one
case.

### 3.1 How reachable the fallback actually is — state the contract, keep the guard

Both writers of the record (`DaemonRunner.SeedReviewerAffirmation` and the `affirm` verb) store
`VendorVersionResolver.Resolve` output, and `ExtractVersionToken` constrains that hard:

```csharp
var tok = raw.Trim().TrimStart('v', 'V');
if (tok.Length > 0 && tok.All(c => char.IsAsciiDigit(c) || c == '.') && tok.Contains('.'))
    return tok;
```

So a resolver-written value is **digits and dots only, with the `v` prefix already stripped and at
least one dot present**. It can never be `v0.53.0`, `0.54.0-rc1`, `1.2.3+build.456`, a date stamp or a
git describe string. That answers the review's sharpest practical worry — that real version strings
would fail `TryParse`, silently fall back to ordinal, and leave the change a no-op for the exact
upgrade case it exists to fix. They will not: `0.53.0` → `0.54.0` parses and compares on row 4.

The fallback is therefore **near-unreachable, not dead**, and stays for two reasons:

1. `ExtractVersionToken`'s character filter still admits strings `Version.TryParse` rejects —
   `1.2.3.4.5` (five components), `.5`, `1.`, `1..2`. Pathological, but the resolver would return them.
2. The record is a plain file an operator can edit, and the store reads whatever is there.

## 3.2 Test it as a property, not as arms

Pin monotonicity directly: a table of `(installed, floor)` pairs, asserting that every pair the *old*
comparison admits the *new* one also admits. Include at minimum
`(daily-20240806, daily-20240806)`, `(1.2.3.4.5, 1.2.3.4.5)`, `(0.53.0, 0.53.0)`. Arm-by-arm tests
cannot express this property and will not catch its violation.

## 4. `NoMinimumRecorded` as its own arm

Today "nothing recorded" and "a different build is installed" both collapse into `Unaffirmed`. Under
floor semantics they have genuinely different remedies:

- **no record** → `SeedReviewerAffirmation` writes one only when the reviewer is enabled **and only at
  daemon startup**. Review made the point that "seeding failed" is an incomplete characterisation, and
  it is: the ordinary way to reach this state is not failure at all but the **normal first-enable
  path** — an operator sets `KCAP_*_UNATTENDED_REVIEWER=1` against a daemon that is already running,
  so no startup has yet observed the flag. The other routes are a record the operator removed, or a
  seeding attempt that threw (it is caught and warned, never fatal). The denial text must lead with
  the restart, since that is the common case: *restart the daemon with the reviewer enabled so it can
  record a minimum, or set one now with `kcap daemon reviewer affirm --vendor <v>`.*
- **below minimum** → the installed build is older than the floor. Remedy: upgrade the vendor CLI,
  or deliberately lower the floor with `affirm`.

Both vendor decision enums gain the corresponding arm, **and so does `Incomparable` from §2.3.**

### 4.1 Delete the `_ => Allowed` default — it makes every new arm fail OPEN

Both vendor `Decide` methods currently end:

```csharp
return ReviewerVersionAffirmations.Decide(installedVersion, affirmedVersion) switch {
    ReviewerVersionAffirmation.Unresolved => KiroReviewerDecision.VersionUnresolved,
    ReviewerVersionAffirmation.Unaffirmed => KiroReviewerDecision.VersionUnaffirmed,
    _                                     => KiroReviewerDecision.Allowed   // ← everything else ALLOWS
};
```

Review caught that adding `Incomparable` (or `NoMinimumRecorded`) without touching these switches
routes it to `_` and **allows the launch** — inverting a refusal into an admission, silently, in a
gate whose entire purpose is to fail closed. That would have made §2.3's whole analysis moot.

Adding the two arms fixes the instance. **Also remove the discard**, which fixes the class:

```csharp
=> ReviewerVersionAffirmations.Decide(installed, floor) switch {
    ReviewerVersionAffirmation.MeetsMinimum      => KiroReviewerDecision.Allowed,
    ReviewerVersionAffirmation.Unresolved        => KiroReviewerDecision.VersionUnresolved,
    ReviewerVersionAffirmation.NoMinimumRecorded => KiroReviewerDecision.VersionNoMinimum,
    ReviewerVersionAffirmation.BelowMinimum      => KiroReviewerDecision.VersionBelowMinimum,
    ReviewerVersionAffirmation.Incomparable      => KiroReviewerDecision.VersionIncomparable
};
```

With every named arm listed and no `_`, the compiler's exhaustiveness check (CS8509) flags the next
arm someone adds. A default that maps the permissive outcome is the wrong shape for a fail-closed
gate regardless of how many arms exist today: it means the safe direction is whatever nobody thought
about.

**CS8509 is a warning by default, so this PR must also make it an error — otherwise the guarantee is
a hope.** Review flagged that an earlier draft of this section asserted "build failure" without
checking, and the check says it was wrong: `Directory.Build.props` sets only `EnforceCodeStyleInBuild`
and a `NoWarn` for two IDE rules — there is no `TreatWarningsAsErrors` and no `WarningsAsErrors`
anywhere in the repo.

Add to `Directory.Build.props`:

```xml
<WarningsAsErrors>CS8509</WarningsAsErrors>
```

**Measured before proposing it:** a build of `Capacitor.Cli.Daemon` (and transitively Core and the
CLI) plus `Capacitor.Cli.Tests.Unit` currently emits **zero** CS8509 warnings, so this breaks nothing
today. It is repo-wide rather than scoped to these two assemblies deliberately — a non-exhaustive
switch that silently picks a default is a hazard everywhere, this one just happens to be a security
gate — and repo-wide costs nothing given the measurement. Narrow it to the two `.csproj` files if a
future build disagrees, but do not drop it and leave §4.1 asserting a guarantee that is not there.

The same applies to the mutation check — deleting one explicit arm must now fail the *build*, which
is a stronger guarantee than a reddening test. Where a test is still wanted, assert that
`Incomparable` and `NoMinimumRecorded` each produce a refusing vendor decision, per vendor.

### 4.2 Implementation note: a cascade, not a two-parse match

Row 2 must be evaluated **before** rows 3 and 4. `Version.TryParse(null)` returns `false` silently, so
an implementation that parses both sides up front and then matches on the two boolean results routes
a missing record into `Incomparable` or `BelowMinimum` instead of `NoMinimumRecorded` — a wrong,
confusing denial for the most common misconfiguration. `ReviewerVersionStore.Affirmed` returns `null`
for the missing-file case, which is exactly the input that makes this silent. Write the rows as an
ordered cascade with the null checks first, and say so in a comment at the site.

## 5. Do not rename the record file

`ReviewerVersionStore.FileNameFor(vendor)` is `{vendor}-reviewer-affirmed-version`. It reads
"affirmed" and this change makes that word inaccurate — **leave it alone anyway.** The store's own
doc already records why: *"Kiro's filename is unchanged from when this type was Kiro-only — renaming
it would have silently discarded every existing affirmation and taken shipped reviewers offline on
upgrade."* The identical hazard applies here, and it is precisely the sort of tidying this rename-heavy
change invites. A comment at the constant should say so.

Likewise the `affirm` verb keeps its name. Its *meaning* becomes "record the installed build as the
minimum", its help text and output change, but renaming the verb would break documented operator
muscle memory and every README reference for no functional gain.

## 6. Share the version parsing — do not write a second one

`DaemonRunner.CliVersionAllowed` already normalizes a vendor version string for comparison:
`TrimStart('v','V')`, `Split('-','+')[0]`, `Version.TryParse`. This change needs a
string→`Version?` step in `Capacitor.Cli.Core`.

**Extract it into Core and have `CliVersionAllowed` call it.** Do not copy the three lines and add a
test asserting the two agree — two implementations that must agree with nothing structurally making
them agree is a known recurring defect shape in this codebase, and the fix is to delete one, not to
watch them.

**The justification is cross-path consistency, not merely drift prevention.** Review's point, and it
is the stronger form of the argument: per §3.1 the extra trimming is a no-op on these values, so
"they might drift apart later" is a style preference an implementer can argue with. The invariant
that is *not* arguable is that **both paths must agree on what counts as an orderable version.** The
same resolver output feeds this gate and `CliVersionAllowed`; if the two disagreed on parseability,
a reviewer could be admitted as running a version the rest of the daemon's version logic would treat
as unrecognisable — and §2.4 turns that agreement into a load-bearing property, since it is exactly
why widening one parse cannot be done without considering the other. Share the parse because the two
gates must classify identically, not because they happen to today.

## 7. Operator-facing text

Every message that today says a build "was affirmed" and a "changed build is refused" must stop
saying that — the refusal is now specifically *older than*. Update:

- `KiroReviewerCapability.DenialReason` / `GeminiReviewerCapability.DenialReason` — coded prefixes
  become `*_reviewer_version_below_minimum`, `*_reviewer_version_no_minimum`, and
  `*_reviewer_version_incomparable` (§2.3), each naming the installed version, the floor, and the
  remedy. `*_incomparable` must name `affirm` specifically — §2.3's proof that the remedy terminates
  is the reason that arm is acceptable, and an operator cannot act on it if the text is vague.
- `DaemonReviewerCommand` usage + success output. The verb can now **lower** a floor (affirming an
  older build than the record); say so explicitly in that case rather than reusing the neutral
  `(was {previous})` phrasing.
- `README.md`, which currently states the exact-match rule in two places (~L783-787 Gemini, ~L818-825
  Kiro) plus the startup-diagnostic line (~L850). The Gemini passage explicitly documents the old
  behaviour — *"a build other than the one this daemon affirmed is refused even with the flag on"* —
  and must not be left contradicting the code. Per the repo's own README-sync rule, this lands in the
  **same PR**.

  Review asked for the replacement wording rather than "say it's a minimum", on the grounds that a
  vague phrasing like *"at least the affirmed version"* can still be read as exact-or-newer by a
  confused operator. Use this shape, adapted per vendor:

  > The recorded version is a **minimum**, not an exact match. Any build at or above it runs; an older
  > one is refused. A vendor upgrade needs no action from you. Use
  > `kcap daemon reviewer affirm --vendor <v>` to move the minimum to whatever is installed now —
  > which is also how you exclude a build you have found to be broken.

## 8. Testing

- The five rows of §2's table, per vendor, each reachable from any host (Kiro's platform argument
  stays a parameter — the existing comment records that reading the ambient OS broke a dozen tests on
  the Windows CI leg).
- **The monotonicity property of §3**, as its own table-driven test over `(installed, floor)` pairs,
  including `(daily-20240806, daily-20240806)` and `(1.2.3.4.5, 1.2.3.4.5)` — the pairs an
  arm-by-arm suite cannot express and the earlier draft would have regressed.
- **Row 4 (`Incomparable`) in both directions**: unparseable floor + parseable installed, and the
  reverse. Assert it is NOT reported as `BelowMinimum` — mislabelling it is how the second draft hid
  a refused upgrade behind a plausible-sounding message.
- **`affirm` clears `Incomparable` in both directions** (§2.3) — the property that makes refusing
  acceptable. Two tests, one per direction, each asserting the *next* `Decide` allows.
- `Unresolved` still fires **only** for null/empty/whitespace installed — a test that an unparseable
  but non-blank installed string does NOT reach it.
- Floor does not auto-advance (§2.1): a `Decide` returning `MeetsMinimum` for a newer build leaves the
  store untouched.
- Downgrade refused (§2.2).
- Empty record file → `NoMinimumRecorded`, same as an absent one (§2 row 2).
- `affirm` lowering the floor reports that it lowered it.
- Mutation-check each guard: break the comparison, confirm the specific test reddens, restore with the
  inverse edit.

## 9. Acceptance

1. A daemon whose vendor upgraded past its recorded version runs the reviewer with **no operator
   action** — the failure this issue exists to remove. Scoped honestly: this holds for any pair
   `System.Version` can order, which per §3.1 is every value the resolver can produce except the
   pathological `1.2.3.4.5` / `1.` / `.5` class. That class lands on §2.3's `Incomparable`, which
   costs one `affirm` and is documented rather than hidden.
2. A daemon whose vendor is older than its record refuses, with text naming both versions.
3. No existing record is rewritten, moved, or invalidated by deploying this.
4. `rg 'AI-[0-9]+' src/ test/ --type cs` stays empty (repo rule; this file is documentation and is
   exempt).
5. No IL2026/IL3050 from `dotnet publish -c Release` on either the CLI or the daemon.
6. `WarningsAsErrors` includes CS8509 (§4.1), and the solution still builds clean — the exhaustiveness
   guard is only real if it is enforced, and a green build proves it costs nothing.
