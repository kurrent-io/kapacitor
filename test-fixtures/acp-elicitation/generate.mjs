// ACP elicitation fixture generator.
//
// PROVENANCE: fixtures are validated against the OFFICIAL @agentclientprotocol/sdk 1.3.0
// package's shipped normative JSON Schema (node_modules/@agentclientprotocol/sdk/schema/
// schema.json, draft 2020-12), the same artifact the reference implementation is generated
// from. The SDK's JS API exposes no runtime request validator (its `CreateElicitationRequest`
// export is a variant-discriminator helper only), so the verdict mechanism is ajv 8 compiled
// against the SDK-shipped schema — recorded here per the design spec's "where the SDK can
// express that" clause (AI-1733 spec §8, Linear).
//
// Four-group taxonomy (spec §8):
//   A: protocol-valid, rendered        — MUST pass schema validation; classifier Renderable.
//   B: protocol-valid, subset-rejected — MUST pass schema validation; daemon cancels (reason).
//   C: protocol-invalid                — MUST fail schema validation where expressible.
//   D: open cases                      — no protocol-validity claim; the SDK verdict is
//      RECORDED **and pinned**: each D fixture declares the verdict observed when it was
//      authored, and a regeneration that observes a different verdict FAILS LOUDLY too —
//      reserved-union semantics drifting is exactly what this generator exists to surface.
//      (A per-fixture `verdictNondeterministic: true` opt-out exists for genuinely
//      environment-dependent cases; none are currently needed.)
// Any A/B failure, expressible-C pass, or D verdict change is SDK/schema drift: exit 1.
//
// Outputs (committed):
//   fixtures.json                                   — frames + groups + verdicts (human diffing)
//   ../../test/Capacitor.Cli.Tests.Unit/Acp/ElicitationFixtures.g.cs — C# constants (no test I/O)
//
// Cap note: lengths are measured in UTF-16 code units (JS string .length == C# string.Length);
// requestedSchema raw text is the generator's compact JSON.stringify form, which is byte-for-byte
// what the C# tests embed, so JsonElement.GetRawText().Length in the daemon sees the same counts.

import { readFileSync, writeFileSync } from "node:fs";
import Ajv2020 from "ajv/dist/2020.js";

const schemaDoc = JSON.parse(
  readFileSync(new URL("./node_modules/@agentclientprotocol/sdk/schema/schema.json", import.meta.url), "utf8"),
);

const ajv = new Ajv2020({ strict: false, allErrors: true });
ajv.addSchema(schemaDoc, "acp");
const validateRequest  = ajv.compile({ $ref: "acp#/$defs/CreateElicitationRequest" });
const validateResponse = ajv.compile({ $ref: "acp#/$defs/CreateElicitationResponse" });

const SID = "fc2e09cf-f4b0-4463-9dc1-bda11268896b";
const CAP_SCHEMA = 32 * 1024;
const CAP_OPTIONS = 32;
const CAP_OPTION_LEN = 1024;
const CAP_MESSAGE = 8 * 1024;

// ---------- helpers ----------

const form = (requestedSchema, extra = {}) => ({ sessionId: SID, message: "Pick", mode: "form", requestedSchema, ...extra });
const oneProp = (prop) => ({ type: "object", properties: { choice: prop } });

/** Pad a schema's root `description` so JSON.stringify(schema).length is exactly target. */
function padSchemaToExactLength(schema, target) {
  const clone = structuredClone(schema);
  clone.description = "";
  const base = JSON.stringify(clone).length;
  if (base > target) throw new Error(`padSchemaToExactLength: base ${base} > target ${target}`);
  clone.description = "p".repeat(target - base);
  const got = JSON.stringify(clone).length;
  if (got !== target) throw new Error(`padSchemaToExactLength: got ${got}, want ${target}`);
  return clone;
}

// ---------- fixture definitions ----------

/** @type {{name: string, kind: "schema"|"params"|"raw"|"response", group: "A"|"B"|"C"|"D", reason?: string, frame: unknown, sdkExpressible?: boolean, note?: string}[]} */
const fixtures = [];
const S = (name, group, frame, reason) => fixtures.push({ name: `Schema_${name}`, kind: "schema", group, frame, reason });
const P = (name, group, frame, reason) => fixtures.push({ name: `Params_${name}`, kind: "params", group, frame, reason });

// ===== Group A — protocol-valid, rendered (schema level) =====
S("SingleSelectEnum", "A", oneProp({ type: "string", enum: ["alpha", "beta", "gamma"] }));
S("SingleSelectTitledOneOf", "A", oneProp({ type: "string", oneOf: [
  { const: "a", title: "Alpha" }, { const: "b", title: "Beta" }] }));
S("FreeTextString", "A", oneProp({ type: "string" }));
S("MultiSelectEnum", "A", oneProp({ type: "array", items: { type: "string", enum: ["x", "y", "z"] } }));
S("MultiSelectTitledAnyOf", "A", oneProp({ type: "array", items: { anyOf: [
  { const: "x", title: "Ex" }, { const: "y", title: "Why" }, { const: "z", title: "Zed" }] } }));
S("MultiSelectWithBounds", "A", oneProp({ type: "array", minItems: 1, maxItems: 2, items: { type: "string", enum: ["x", "y", "z"] } }));
S("DuplicateEnumValues", "A", oneProp({ type: "string", enum: ["dup", "dup", "solo"] }));
S("EmptyStringEnumValue", "A", oneProp({ type: "string", enum: ["", "real"] }));
S("PropertyTitleAndDescription", "A", oneProp({ type: "string", title: "The title", description: "The description", enum: ["a", "b"] }));
S("NullEnumWithOneOf", "A", oneProp({ type: "string", enum: null, oneOf: [{ const: "a", title: "Alpha" }] }));
S("NullRequired", "A", { ...oneProp({ type: "string", enum: ["a"] }), required: null });
S("NullBounds", "A", oneProp({ type: "array", minItems: null, maxItems: null, items: { type: "string", enum: ["x", "y"] } }));
S("NullTitleDescription", "A", oneProp({ type: "string", title: null, description: null, enum: ["a", "b"] }));
S("IntMaxMaxItems", "A", oneProp({ type: "array", maxItems: 2147483647, items: { type: "string", enum: ["x", "y"] } }));
S("ExactCapSchema", "A", padSchemaToExactLength(oneProp({ type: "string", enum: ["a", "b"] }), CAP_SCHEMA));
S("ExactCapOptionLen", "A", oneProp({ type: "string", enum: ["o".repeat(CAP_OPTION_LEN), "b"] }));
S("ExactCapOptionCount", "A", oneProp({ type: "string", enum: Array.from({ length: CAP_OPTIONS }, (_, i) => `opt${i}`) }));
// 512 astral emoji = 1024 UTF-16 units exactly — at cap, valid.
S("MultibyteOptionAtCap", "A", oneProp({ type: "string", enum: ["\u{1F600}".repeat(512), "b"] }));
S("EscapeHeavyOptionUnderCap", "A", oneProp({ type: "string", enum: ['q"\\\n\t'.repeat(100), "b"] }));

// ===== Group A — params level =====
P("BothSessionAndRequestId", "A", { ...form(oneProp({ type: "string", enum: ["a", "b"] })), requestId: 7 });

// ===== Group B — protocol-valid, subset-rejected (schema level) =====
S("NumberProp", "B", oneProp({ type: "number" }), "unsupported_type");
S("IntegerProp", "B", oneProp({ type: "integer" }), "unsupported_type");
S("BooleanProp", "B", oneProp({ type: "boolean" }), "unsupported_type");
S("ReservedOtherPropType", "B", oneProp({ type: "_customVendorType" }), "unsupported_type");
S("ReservedOtherItemsType", "B", oneProp({ type: "array", items: { type: "_customItems" } }), "unsupported_type");
S("MultiProperty", "B", { type: "object", properties: {
  a: { type: "string", enum: ["x"] }, b: { type: "string", enum: ["y"] } } }, "multi_property");
S("ZeroProperty", "B", { type: "object", properties: {} }, "multi_property");
S("MaxItemsZero", "B", oneProp({ type: "array", maxItems: 0, items: { type: "string", enum: ["x"] } }), "empty_selection_unsupported");
S("MinItemsAboveOptionCount", "B", oneProp({ type: "array", minItems: 5, items: { type: "string", enum: ["x", "y"] } }), "unsatisfiable_bounds");
S("MinAboveMax", "B", oneProp({ type: "array", minItems: 3, maxItems: 1, items: { type: "string", enum: ["x", "y", "z"] } }), "unsatisfiable_bounds");
S("TooManyOptions", "B", oneProp({ type: "string", enum: Array.from({ length: CAP_OPTIONS + 1 }, (_, i) => `opt${i}`) }), "too_many_options");
S("OptionTooLong", "B", oneProp({ type: "string", enum: ["o".repeat(CAP_OPTION_LEN + 1), "b"] }), "option_too_long");
S("MultibyteOptionOverCap", "B", oneProp({ type: "string", enum: ["\u{1F600}".repeat(513), "b"] }), "option_too_long");
S("SchemaTooLarge", "B", padSchemaToExactLength(oneProp({ type: "string", enum: ["a", "b"] }), CAP_SCHEMA + 1), "schema_too_large");
{ // multibyte over-cap schema: pad with emoji so the raw length crosses the cap with multibyte content
  const clone = oneProp({ type: "string", enum: ["a", "b"] });
  clone.description = "\u{1F600}".repeat(Math.ceil((CAP_SCHEMA + 2) / 2));
  S("MultibyteSchemaOverCap", "B", clone, "schema_too_large");
}
S("StringEnumPlusOneOf", "B", oneProp({ type: "string", enum: ["a"], oneOf: [{ const: "b", title: "Bee" }] }), "unsupported_selector_combination");
S("EmptyStringPropEnum", "B", oneProp({ type: "string", enum: [] }), "empty_selector_unsupported");
S("EmptyStringPropOneOf", "B", oneProp({ type: "string", oneOf: [] }), "empty_selector_unsupported");
S("BoundIntMaxPlusOne", "B", oneProp({ type: "array", maxItems: 2147483648, items: { type: "string", enum: ["x", "y"] } }), "bounds_too_large");
S("BoundULongMax", "B", oneProp({ type: "array", maxItems: 18446744073709551615, items: { type: "string", enum: ["x", "y"] } }), "bounds_too_large");
S("RequiredNamingOtherProperty", "B", { ...oneProp({ type: "string", enum: ["a"] }), required: ["somethingElse"] }, "unsupported_required");

// ===== Group B — params level =====
P("EmptyMessage", "B", { ...form(oneProp({ type: "string", enum: ["a"] })), message: "" }, "blank_message_unsupported");
P("WhitespaceMessage", "B", { ...form(oneProp({ type: "string", enum: ["a"] })), message: " \t\n " }, "blank_message_unsupported");
P("OverlongMessage", "B", { ...form(oneProp({ type: "string", enum: ["a"] })), message: "m".repeat(CAP_MESSAGE + 1) }, "message_too_long");
P("ExactCapMessage", "A", { ...form(oneProp({ type: "string", enum: ["a"] })), message: "m".repeat(CAP_MESSAGE) });
P("UrlMode", "B", { sessionId: SID, message: "Visit", mode: "url", elicitationId: "e1", url: "https://example.com/x" }, "url_mode");
P("RequestScoped", "B", { requestId: 42, message: "Pick", mode: "form", requestedSchema: oneProp({ type: "string", enum: ["a"] }) }, "request_scoped_unsupported");

// ===== Group C — protocol-invalid (schema level; validated wrapped in a form params frame) =====
S("NonObjectRoot", "C", "not-an-object", "malformed_schema");
S("WrongRootType", "C", { type: "array", properties: { choice: { type: "string", enum: ["a"] } } }, "malformed_schema");
S("PropertiesNull", "C", { type: "object", properties: null }, "malformed_schema");
S("PropertiesNonObject", "C", { type: "object", properties: [1, 2] }, "malformed_schema");
S("NonObjectPropertySchema", "C", { type: "object", properties: { choice: "nope" } }, "malformed_schema");
S("MissingPropType", "C", oneProp({ enum: ["a", "b"] }), "malformed_schema");
S("NonStringPropType", "C", oneProp({ type: 7, enum: ["a"] }), "malformed_schema");
S("WrongKindSelector", "C", oneProp({ type: "string", enum: { a: 1 } }), "malformed_schema");
S("NonStringEnumEntry", "C", oneProp({ type: "string", enum: ["a", 5] }), "malformed_schema");
S("NonObjectItems", "C", oneProp({ type: "array", items: "nope" }), "malformed_schema");
// rev-9 reclassification (recorded drift): the union's reserved-"other" tolerance admits these frames,
// so they are protocol-tolerated → group B, declined as zero-option questions like the string-prop empties.
S("EmptyItemsEnum", "B", oneProp({ type: "array", items: { type: "string", enum: [] } }), "empty_selector_unsupported");
S("EmptyItemsAnyOf", "B", oneProp({ type: "array", items: { anyOf: [] } }), "empty_selector_unsupported");
S("NullItemsEnum", "C", oneProp({ type: "array", items: { type: "string", enum: null } }), "malformed_schema");
S("NullItemsAnyOf", "C", oneProp({ type: "array", items: { anyOf: null } }), "malformed_schema");
S("EnumOptionMissingTitle", "C", oneProp({ type: "string", oneOf: [{ const: "a" }] }), "malformed_schema");
S("EnumOptionNonStringConst", "C", oneProp({ type: "string", oneOf: [{ const: 5, title: "Five" }] }), "malformed_schema");
S("RequiredNonArray", "C", { ...oneProp({ type: "string", enum: ["a"] }), required: "choice" }, "malformed_schema");
S("RequiredNonStringEntry", "C", { ...oneProp({ type: "string", enum: ["a"] }), required: [7] }, "malformed_schema");
S("NegativeBound", "C", oneProp({ type: "array", minItems: -1, items: { type: "string", enum: ["x"] } }), "malformed_schema");
S("FractionalBound", "C", oneProp({ type: "array", minItems: 1.5, items: { type: "string", enum: ["x"] } }), "malformed_schema");

// ===== Group C — params level =====
// rev-9: unknown modes are reserved-tolerated by the pinned schema (protocol-valid) — the client
// MUST NOT render them, so the cancel is compliant behavior on a valid frame → group B.
P("UnknownMode", "B", { sessionId: SID, message: "Pick", mode: "wizard", requestedSchema: oneProp({ type: "string", enum: ["a"] }) }, "unknown_mode");
P("MissingMode", "C", { sessionId: SID, message: "Pick", requestedSchema: oneProp({ type: "string", enum: ["a"] }) }, "malformed_request");
P("MissingMessage", "C", { sessionId: SID, mode: "form", requestedSchema: oneProp({ type: "string", enum: ["a"] }) }, "malformed_request");
P("NullMessage", "C", { sessionId: SID, message: null, mode: "form", requestedSchema: oneProp({ type: "string", enum: ["a"] }) }, "malformed_request");
P("NonStringMessage", "C", { sessionId: SID, message: 7, mode: "form", requestedSchema: oneProp({ type: "string", enum: ["a"] }) }, "malformed_request");
P("MissingRequestedSchema", "C", { sessionId: SID, message: "Pick", mode: "form" }, "malformed_schema");
// rev-9: recorded SDK verdict is PASS (schema tolerance) → group B; daemon treats null requestId as absent.
P("JsonNullRequestId", "B", { requestId: null, message: "Pick", mode: "form", requestedSchema: oneProp({ type: "string", enum: ["a"] }) }, "session_uncorrelatable");
fixtures.push({ name: "Params_MalformedJson", kind: "raw", group: "C", reason: "malformed_request", frame: '{"sessionId": "x", "message": ', sdkExpressible: false, note: "not JSON — inexpressible to a schema validator" });

// ===== Group D — open cases (no protocol-validity claim; verdict recorded) =====
S("ItemsEnumPlusAnyOf", "D", oneProp({ type: "array", items: { type: "string", enum: ["a"], anyOf: [{ const: "b", title: "Bee" }] } }), "unsupported_selector_combination");
S("Bound100Digits", "D", null, "bounds_too_large"); // frame built below (raw JSON — JS numbers cannot carry 100 digits)
S("BoundExponent1e3", "D", null, "malformed_schema");
S("Bound1e30", "D", null, "malformed_schema");
S("BoundDecimal5Point0", "D", null, "malformed_schema");
S("BoundNegativeZero", "D", null, "malformed_schema");
for (const [name, lexeme] of [
  ["Schema_Bound100Digits", "9".repeat(100)],
  ["Schema_BoundExponent1e3", "1e3"],
  ["Schema_Bound1e30", "1e30"],
  ["Schema_BoundDecimal5Point0", "5.0"],
  ["Schema_BoundNegativeZero", "-0"],
]) {
  const f = fixtures.find((x) => x.name === name);
  f.rawJson = `{"type":"object","properties":{"choice":{"type":"array","maxItems":${lexeme},"items":{"type":"string","enum":["x","y"]}}}}`;
}
// Ordering-pinning fixtures (spec §8 classifier tests): protocol validity depends on the
// reserved-variant tolerance observed above, so recorded as group D; daemon reasons are fixed
// by the §4.2 stage order.
S("MultiPropertyMalformedChildren", "D", { type: "object", properties: { a: "nope", b: 5 } }, "multi_property");
S("Malformed40EntrySelector", "D", oneProp({ type: "string", enum: [...Array.from({ length: 39 }, (_, i) => `o${i}`), 7] }), "too_many_options");
S("MalformedEarlyEntryOverlongLater", "D", oneProp({ type: "string", enum: [5, "o".repeat(1025)] }), "malformed_schema");

for (const [t, name] of [["number", "MetaNumber"], ["boolean", "MetaBoolean"], ["object", "MetaObject"], ["array", "MetaArray"]]) {
  const v = { number: 7, boolean: true, object: { k: 1 }, array: [1, 2] }[t];
  S(`${name}Title`, "D", oneProp({ type: "string", title: v, enum: ["a", "b"] }));
  S(`${name}Description`, "D", oneProp({ type: "string", description: v, enum: ["a", "b"] }));
}

// ---------- validation ----------

// Pinned SDK verdicts for group D (observed at authoring time; a change is drift — R1-3).
const pinnedDVerdicts = {
  Schema_ItemsEnumPlusAnyOf: "pass",
  Schema_Bound100Digits: "pass",
  Schema_BoundExponent1e3: "pass",
  Schema_Bound1e30: "pass",
  Schema_BoundDecimal5Point0: "pass",
  Schema_BoundNegativeZero: "pass",
  Schema_MultiPropertyMalformedChildren: "fail",
  Schema_Malformed40EntrySelector: "fail",
  Schema_MalformedEarlyEntryOverlongLater: "fail",
  Schema_MetaNumberTitle: "fail",
  Schema_MetaNumberDescription: "fail",
  Schema_MetaBooleanTitle: "fail",
  Schema_MetaBooleanDescription: "fail",
  Schema_MetaObjectTitle: "fail",
  Schema_MetaObjectDescription: "fail",
  Schema_MetaArrayTitle: "fail",
  Schema_MetaArrayDescription: "fail",
};

let failed = false;
const results = [];
for (const f of fixtures) {
  const frameJson = f.rawJson ?? JSON.stringify(f.frame);
  let verdict = "n/a";
  if (f.kind !== "raw" && f.kind !== "response") {
    // schema-level fixtures are judged inside a minimal valid form params frame
    const params = f.kind === "schema"
      ? { sessionId: SID, message: "Pick", mode: "form", requestedSchema: JSON.parse(frameJson) }
      : JSON.parse(frameJson);
    verdict = validateRequest(params) ? "pass" : "fail";
  }
  const expressible = f.sdkExpressible !== false;
  if ((f.group === "A" || f.group === "B") && verdict !== "pass") {
    console.error(`DRIFT: ${f.name} (group ${f.group}) expected SDK-PASS but got ${verdict}:`, ajv.errorsText(validateRequest.errors));
    failed = true;
  }
  if (f.group === "C" && expressible && verdict !== "fail") {
    console.error(`DRIFT: ${f.name} (group C) expected SDK-FAIL but PASSED`);
    failed = true;
  }
  if (f.group === "D" && !f.verdictNondeterministic) {
    const pinned = pinnedDVerdicts[f.name];
    if (pinned === undefined) {
      console.error(`DRIFT: ${f.name} (group D) has no pinned SDK verdict — pin it in pinnedDVerdicts`);
      failed = true;
    } else if (verdict !== pinned) {
      console.error(`DRIFT: ${f.name} (group D) pinned SDK verdict '${pinned}' but observed '${verdict}'`);
      failed = true;
    }
  }
  results.push({ name: f.name, group: f.group, kind: f.kind, reason: f.reason ?? null, sdkVerdict: verdict, frame: frameJson, note: f.note ?? null });
}

// Positive control: the pre-stabilization response shape must FAIL, real responses must PASS.
const oldShape = { outcome: { outcome: "selected", optionId: "x" } };
if (validateResponse(oldShape)) { console.error("DRIFT: old {outcome} response shape PASSED CreateElicitationResponse validation"); failed = true; }
for (const good of [
  { action: "accept", content: { choice: ["a", "b"] } },
  { action: "accept", content: { choice: "a" } },
  { action: "cancel" },
  { action: "decline" },
]) {
  if (!validateResponse(good)) { console.error("DRIFT: expected-good response FAILED validation:", JSON.stringify(good), ajv.errorsText(validateResponse.errors)); failed = true; }
}

if (failed) { console.error("\nGeneration FAILED — SDK/schema drift or fixture bug above."); process.exit(1); }

// ---------- emit ----------

writeFileSync(new URL("./fixtures.json", import.meta.url), JSON.stringify(results, null, 1) + "\n");

const csName = (n) => n.replace(/[^A-Za-z0-9_]/g, "_");
let cs = `// <auto-generated />
// GENERATED by test-fixtures/acp-elicitation/generate.mjs — do NOT hand-edit.
// Regenerate: cd test-fixtures/acp-elicitation && npm install && node generate.mjs
// Provenance + verdict mechanism: see the header of generate.mjs. Groups: A = protocol-valid
// rendered, B = protocol-valid subset-rejected, C = protocol-invalid, D = open cases (SDK
// verdict recorded, no validity claim).

namespace Capacitor.Cli.Tests.Unit.Acp;

internal static class ElicitationFixtures {
`;
const csLiteral = (text) => {
  // A raw string cannot represent content that starts/ends with a quote (the delimiter eats it)
  // or contains a quad-quote run — fall back to a regular escaped literal for those.
  if (!text.startsWith('"') && !text.endsWith('"') && !text.includes('""""'))
    return `"""${text}"""`;
  const escaped = text.replace(/\\/g, "\\\\").replace(/"/g, '\\"').replace(/\n/g, "\\n").replace(/\r/g, "\\r").replace(/\t/g, "\\t");
  return `"${escaped}"`;
};
for (const r of results) {
  const id = csName(r.name);
  cs += `    /// <summary>Group ${r.group}; SDK verdict: ${r.sdkVerdict}${r.reason ? `; expected daemon reason: ${r.reason}` : ""}.</summary>\n`;
  cs += `    public const string ${id} = ${csLiteral(r.frame)};\n`;
  if (r.reason) cs += `    public const string Reason_${id} = "${r.reason}";\n`;
  cs += "\n";
}
cs += "}\n";
writeFileSync(new URL("../../test/Capacitor.Cli.Tests.Unit/Acp/ElicitationFixtures.g.cs", import.meta.url), cs);

console.log(`OK: ${results.length} fixtures. Groups:`,
  Object.fromEntries(["A", "B", "C", "D"].map((g) => [g, results.filter((r) => r.group === g).length])));
console.log("Group D SDK verdicts:", results.filter((r) => r.group === "D").map((r) => `${r.name}=${r.sdkVerdict}`).join(", "));
