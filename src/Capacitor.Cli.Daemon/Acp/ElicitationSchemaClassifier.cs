// src/Capacitor.Cli.Daemon/Acp/ElicitationSchemaClassifier.cs
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Acp;

/// <summary>How a renderable elicitation schema maps onto the daemon's single-question subset.</summary>
internal enum ElicitationKind {
    SingleSelect,
    MultiSelect,
    FreeText
}

/// <summary>
/// The renderable classification of a stabilized ACP elicitation `requestedSchema`:
/// exactly one property, mapped to one of the three supported question kinds.
/// <see cref="Title"/>/<see cref="Description"/> are the SINGLE property's display metadata
/// (root-level schema metadata is deliberately ignored) — carried here so
/// <see cref="AcpInteractionBridge"/> never re-parses the raw schema. For
/// <see cref="ElicitationKind.MultiSelect"/>, <see cref="MinSelections"/>/<see cref="MaxSelections"/>
/// are the EFFECTIVE bounds (declared bounds validated, then clamped to [1, option count] — this
/// client never submits zero selections); null for the other kinds.
/// </summary>
internal sealed record ElicitationClassification(
        ElicitationKind        Kind,
        string                 PropertyName,
        AcpInteractionOption[] Options,
        int?                   MinSelections,
        int?                   MaxSelections,
        string?                Title,
        string?                Description
    );

/// <summary>
/// Pure classifier for stabilized ACP elicitation schemas (agent-client-protocol #1779,
/// `schema/v1/schema.json` `ElicitationSchema`/`ElicitationPropertySchema`/
/// `MultiSelectPropertySchema`) onto the daemon's single-question subset. No I/O, no logging —
/// the caller owns observability. Validation is a strictly ordered pipeline; the FIRST failing
/// stage's reason wins, and within a stage the first failing check wins, so every input has
/// exactly one deterministic reason:
///
///   stage 0  raw-size gate (`schema_too_large`) — nothing else is inspected past the cap
///   stage 1  root shape (`malformed_schema`)
///   stage 2  properties container (`malformed_schema`)
///   stage 3  property COUNT (`multi_property`) — before inspecting any child, so a
///            multi-property schema with malformed children still reports the count
///   stage 4  the single property's shape/type (`malformed_schema` / `unsupported_type`)
///   stage 5  selectors: dual-selector (`unsupported_selector_combination`), kind/emptiness
///            (`malformed_schema` for wrong kinds; `empty_selector_unsupported` for EMPTY
///            selector arrays on both variants — the union's reserved-variant tolerance makes a
///            zero-option frame protocol-tolerated, so it is declined by name, a deliberate
///            post-review refinement of the original malformed classification), entry-count cap
///            (`too_many_options`, before per-entry validation), then per-entry in array order,
///            shape before length (`malformed_schema` / `option_too_long`)
///   stage 6  `required` (`malformed_schema` typing / `unsupported_required` foreign names)
///   stage 7  bounds, array variant only, lexeme-first (`malformed_schema` / `bounds_too_large`)
///   stage 8  bounds sanity (`empty_selection_unsupported` / `unsatisfiable_bounds`)
///
/// Null-equivalence is scoped to the members the pinned schema declares nullable (the string
/// property's `enum`/`oneOf`, `required`, `minItems`/`maxItems`, `title`/`description`): JSON
/// null there is treated exactly like an absent member. The multi-select items selectors
/// (`items.enum`/`items.anyOf`) are REQUIRED non-nullable members, so null there is
/// `malformed_schema`. Non-string property `title`/`description` are treated as absent —
/// mirroring the schema's own `x-deserialize-default-on-error` annotation — never an error.
///
/// Caps (UTF-16 code units, resource bounds — not wire-byte bounds): raw schema 32 Ki, selector
/// entries 32, option id/label 1024 each. With those, the produced <see cref="AcpInteractionOption"/>
/// payload is a fixed, bounded function of the caps regardless of the raw schema's content.
/// </summary>
internal static class ElicitationSchemaClassifier {
    const int MaxSchemaCodeUnits = 32 * 1024;
    const int MaxOptions         = 32;
    const int MaxOptionCodeUnits = 1024;

    public static bool TryClassify(
        JsonElement requestedSchema,
        [NotNullWhen(true)]  out ElicitationClassification? classification,
        [NotNullWhen(false)] out string? unrenderableReason) {
        classification = null;

        // Stage 0: raw-size gate. Checked before anything else so an oversized schema costs one
        // length read, never a structural walk.
        if (requestedSchema.GetRawText().Length > MaxSchemaCodeUnits)
            return Fail("schema_too_large", out unrenderableReason);

        // Stage 1: root shape. Absent root `type` is tolerated (the pinned schema defaults it).
        if (requestedSchema.ValueKind != JsonValueKind.Object)
            return Fail("malformed_schema", out unrenderableReason);
        if (requestedSchema.TryGetProperty("type", out var rootType)
            && (rootType.ValueKind != JsonValueKind.String || rootType.GetString() != "object"))
            return Fail("malformed_schema", out unrenderableReason);

        // Stage 2: properties container.
        if (!requestedSchema.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object)
            return Fail("malformed_schema", out unrenderableReason);

        // Stage 3: property count — BEFORE inspecting any child.
        string? propertyName = null;
        JsonElement property = default;
        var count = 0;

        foreach (var member in properties.EnumerateObject()) {
            count++;
            if (count > 1)
                return Fail("multi_property", out unrenderableReason);
            propertyName = member.Name;
            property     = member.Value;
        }
        if (count == 0)
            return Fail("multi_property", out unrenderableReason);

        // Stage 4: the single property's shape and type discriminator (REQUIRED at property level,
        // unlike the root).
        if (property.ValueKind != JsonValueKind.Object)
            return Fail("malformed_schema", out unrenderableReason);
        if (!property.TryGetProperty("type", out var propType) || propType.ValueKind != JsonValueKind.String)
            return Fail("malformed_schema", out unrenderableReason);

        var typeName = propType.GetString();
        if (typeName is "number" or "integer" or "boolean")
            return Fail("unsupported_type", out unrenderableReason);
        if (typeName is not ("string" or "array"))
            return Fail("unsupported_type", out unrenderableReason);

        var title       = OptionalString(property, "title");
        var description = OptionalString(property, "description");

        if (typeName == "string") {
            // Stage 5 (string variant): enum (untitled) xor oneOf (titled); JSON null = absent.
            var hasEnum  = TryGetNonNull(property, "enum",  out var stringEnum);
            var hasOneOf = TryGetNonNull(property, "oneOf", out var stringOneOf);

            if (hasEnum && hasOneOf)
                return Fail("unsupported_selector_combination", out unrenderableReason);

            if (!hasEnum && !hasOneOf) {
                if (!ValidateRequired(requestedSchema, propertyName!, out unrenderableReason))
                    return false;
                classification = new ElicitationClassification(
                    ElicitationKind.FreeText, propertyName!, [], null, null, title, description);
                unrenderableReason = null;
                return true;
            }

            var selector = hasEnum ? stringEnum : stringOneOf;
            if (!TryReadOptions(selector, titled: hasOneOf, emptyReason: "empty_selector_unsupported",
                    out var options, out unrenderableReason))
                return false;

            if (!ValidateRequired(requestedSchema, propertyName!, out unrenderableReason))
                return false;

            classification = new ElicitationClassification(
                ElicitationKind.SingleSelect, propertyName!, options, null, null, title, description);
            return true;
        }

        // Array variant. Stage 5: items object carrying enum (string items) xor anyOf (titled).
        if (!property.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Object)
            return Fail("malformed_schema", out unrenderableReason);

        // items.enum / items.anyOf are REQUIRED, non-nullable members of their variants — JSON
        // null here is NOT absence (unlike the string property's nullable selectors).
        var hasItemsEnum  = items.TryGetProperty("enum",  out var itemsEnum);
        var hasItemsAnyOf = items.TryGetProperty("anyOf", out var itemsAnyOf);

        if (hasItemsEnum && hasItemsAnyOf)
            return Fail("unsupported_selector_combination", out unrenderableReason);
        if (!hasItemsEnum && !hasItemsAnyOf) {
            // Neither selector: a reserved-other items variant iff it carries its own `type`
            // discriminator; otherwise the required selector is simply missing.
            return Fail(items.TryGetProperty("type", out _) ? "unsupported_type" : "malformed_schema",
                out unrenderableReason);
        }

        // A `type` on the items object must be the string variant's discriminator; anything else
        // is a reserved-other items shape this client doesn't render.
        if (items.TryGetProperty("type", out var itemsType)
            && (itemsType.ValueKind != JsonValueKind.String || itemsType.GetString() != "string"))
            return Fail("unsupported_type", out unrenderableReason);

        // Empty items selectors: the specific variants pin `minItems: 1`, but the union's
        // reserved-variant tolerance still admits the frame, so a zero-option question is declined
        // by name (same reason as the string property's empty selectors) rather than as malformed.
        if (!TryReadOptions(hasItemsEnum ? itemsEnum : itemsAnyOf, titled: hasItemsAnyOf,
                emptyReason: "empty_selector_unsupported", out var itemOptions, out unrenderableReason))
            return false;

        // Stage 6: `required` — typing is pinned (`string[]`), but a well-typed set naming
        // anything other than the single property is protocol-valid and merely unsatisfiable by
        // this one-property subset.
        if (!ValidateRequired(requestedSchema, propertyName!, out unrenderableReason))
            return false;

        // Stage 7: bounds — array variant only, lexeme first (TryGetInt32 would accept exact-valued
        // spellings like `5.0`/`1e3`, so the raw token is the gate, never the numeric conversion).
        if (!TryReadBound(property, "minItems", out var declaredMin, out unrenderableReason))
            return false;
        if (!TryReadBound(property, "maxItems", out var declaredMax, out unrenderableReason))
            return false;

        // Stage 8: bounds sanity over the deduped option count.
        var n   = itemOptions.Length;
        var max = Math.Min(declaredMax ?? n, n);

        if (declaredMax == 0)
            return Fail("empty_selection_unsupported", out unrenderableReason);

        // This client's subset requires at least one selection — there is no "submit nothing"
        // affordance, and the daemon never emits an empty-array accept.
        var effectiveMin = Math.Max(declaredMin ?? 0, 1);
        if (effectiveMin > max)
            return Fail("unsatisfiable_bounds", out unrenderableReason);

        classification = new ElicitationClassification(
            ElicitationKind.MultiSelect, propertyName!, itemOptions, effectiveMin, max, title, description);
        unrenderableReason = null;
        return true;
    }

    static bool Fail(string reason, out string? unrenderableReason) {
        unrenderableReason = reason;
        return false;
    }

    /// <summary>Nullable-member read: missing and JSON null are both "absent" (the pinned schema's
    /// "Omitted and null are equivalent" annotation).</summary>
    static bool TryGetNonNull(JsonElement obj, string name, out JsonElement value) {
        if (obj.TryGetProperty(name, out value) && value.ValueKind != JsonValueKind.Null)
            return true;
        value = default;
        return false;
    }

    /// <summary>Missing, JSON-null, and NON-STRING values are all "absent" — mirroring the pinned
    /// schema's x-deserialize-default-on-error annotation on title/description.</summary>
    static string? OptionalString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>`required` stage: missing/JSON-null → no required set; non-array or non-string
    /// entries → `malformed_schema` (pinned typing); a well-typed entry naming another property →
    /// `unsupported_required` (protocol-valid, unsatisfiable by the one-property subset).</summary>
    static bool ValidateRequired(JsonElement schema, string propertyName, out string? reason) {
        reason = null;
        if (!TryGetNonNull(schema, "required", out var required))
            return true;
        if (required.ValueKind != JsonValueKind.Array) {
            reason = "malformed_schema";
            return false;
        }
        foreach (var entry in required.EnumerateArray()) {
            if (entry.ValueKind != JsonValueKind.String) {
                reason = "malformed_schema";
                return false;
            }
            if (!string.Equals(entry.GetString(), propertyName, StringComparison.Ordinal)) {
                reason = "unsupported_required";
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Reads a selector array into options: kind check, emptiness (reason differs by variant —
    /// the string property's selectors have no minItems, the items selectors pin minItems: 1),
    /// entry-count cap BEFORE any per-entry inspection, then per-entry validation in array order
    /// with shape checked before length. Duplicate ids dedup by first occurrence (ids are the
    /// resolution key).
    /// </summary>
    static bool TryReadOptions(
        JsonElement selector, bool titled, string emptyReason,
        out AcpInteractionOption[] options, out string? reason) {
        options = [];

        if (selector.ValueKind != JsonValueKind.Array) {
            reason = "malformed_schema";
            return false;
        }

        var length = selector.GetArrayLength();
        if (length == 0) {
            reason = emptyReason;
            return false;
        }
        // Entry-count cap before per-entry validation: a malformed over-cap selector
        // deterministically reports the cap (the 33rd option DTO is never created).
        if (length > MaxOptions) {
            reason = "too_many_options";
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<AcpInteractionOption>(length);

        foreach (var entry in selector.EnumerateArray()) {
            string id;
            string label;

            if (titled) {
                // EnumOption: const + title both REQUIRED strings — no const-fallback label.
                if (entry.ValueKind != JsonValueKind.Object
                    || !entry.TryGetProperty("const", out var constVal) || constVal.ValueKind != JsonValueKind.String
                    || !entry.TryGetProperty("title", out var titleVal) || titleVal.ValueKind != JsonValueKind.String) {
                    reason = "malformed_schema";
                    return false;
                }
                id    = constVal.GetString()!;
                label = titleVal.GetString()!;
            } else {
                if (entry.ValueKind != JsonValueKind.String) {
                    reason = "malformed_schema";
                    return false;
                }
                id    = entry.GetString()!;
                label = id;
            }

            // Shape first, then length — the first failing entry's first failing check wins.
            if (id.Length > MaxOptionCodeUnits || label.Length > MaxOptionCodeUnits) {
                reason = "option_too_long";
                return false;
            }

            if (seen.Add(id))
                list.Add(new AcpInteractionOption(id, label, null));
        }

        options = list.ToArray();
        reason  = null;
        return true;
    }

    /// <summary>
    /// Lexeme-first bound read. Missing/JSON-null → absent (null out). A raw token of pure ASCII
    /// digits is a non-negative integral of any magnitude: within int → the value; beyond int →
    /// `bounds_too_large` (protocol-valid uint64 territory and above — the lexeme itself proves
    /// integrality and non-negativity, no big-integer arithmetic needed). Any other spelling —
    /// leading '-' (including -0), decimal point, exponent — is `malformed_schema`, a deliberate
    /// documented narrowing (exact-valued spellings like 1e3/5.0 are recorded open cases).
    /// </summary>
    static bool TryReadBound(JsonElement property, string name, out int? value, out string? reason) {
        value  = null;
        reason = null;

        if (!TryGetNonNull(property, name, out var bound))
            return true;

        if (bound.ValueKind != JsonValueKind.Number) {
            reason = "malformed_schema";
            return false;
        }

        var lexeme = bound.GetRawText();
        foreach (var ch in lexeme) {
            if (ch is < '0' or > '9') {
                reason = "malformed_schema";
                return false;
            }
        }

        if (bound.TryGetInt32(out var parsed)) {
            value = parsed;
            return true;
        }

        reason = "bounds_too_large";
        return false;
    }
}
