// test/Capacitor.Cli.Tests.Unit/Acp/ElicitationSchemaClassifierTests.cs
using System.Text.Json;
using Capacitor.Cli.Daemon.Acp;

namespace Capacitor.Cli.Tests.Unit.Acp;

/// <summary>
/// <see cref="ElicitationSchemaClassifier"/> maps a stabilized ACP `requestedSchema` to the
/// daemon's single-question subset — or a named unrenderable reason. Every case is a fixture from
/// <see cref="ElicitationFixtures"/> (generated + verdict-checked against the SDK-shipped schema;
/// see test-fixtures/acp-elicitation/generate.mjs). The reason assertions deliberately pin the
/// classifier's STAGE ORDER (size gate → root → properties → count → property shape → selectors →
/// required → bounds), not just membership: e.g. a multi-property schema with malformed children
/// must report the count, never the children.
/// </summary>
public class ElicitationSchemaClassifierTests {
    static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    static ElicitationClassification MustClassify(string fixture) {
        var ok = ElicitationSchemaClassifier.TryClassify(Parse(fixture), out var classification, out var reason);
        if (!ok) throw new InvalidOperationException($"expected Renderable, got Unrenderable({reason})");
        return classification!;
    }

    // ===== Renderable (group A) =====

    [Test]
    public async Task SingleSelectEnum_ClassifiesWithValueBackedOptions() {
        var c = MustClassify(ElicitationFixtures.Schema_SingleSelectEnum);

        await Assert.That(c.Kind).IsEqualTo(ElicitationKind.SingleSelect);
        await Assert.That(c.PropertyName).IsEqualTo("choice");
        await Assert.That(c.Options.Select(o => o.OptionId).ToArray()).IsEquivalentTo(new[] { "alpha", "beta", "gamma" });
        await Assert.That(c.Options.Select(o => o.Label).ToArray()).IsEquivalentTo(new[] { "alpha", "beta", "gamma" });
        await Assert.That(c.MinSelections).IsNull();
        await Assert.That(c.MaxSelections).IsNull();
    }

    [Test]
    public async Task TitledOneOf_ClassifiesWithConstIdsAndTitleLabels() {
        var c = MustClassify(ElicitationFixtures.Schema_SingleSelectTitledOneOf);

        await Assert.That(c.Kind).IsEqualTo(ElicitationKind.SingleSelect);
        await Assert.That(c.Options.Select(o => o.OptionId).ToArray()).IsEquivalentTo(new[] { "a", "b" });
        await Assert.That(c.Options.Select(o => o.Label).ToArray()).IsEquivalentTo(new[] { "Alpha", "Beta" });
    }

    [Test]
    public async Task FreeTextString_ClassifiesWithNoOptions() {
        var c = MustClassify(ElicitationFixtures.Schema_FreeTextString);

        await Assert.That(c.Kind).IsEqualTo(ElicitationKind.FreeText);
        await Assert.That(c.Options).IsEmpty();
    }

    [Test]
    public async Task MultiSelectEnum_ClassifiesWithEffectiveBounds() {
        var c = MustClassify(ElicitationFixtures.Schema_MultiSelectEnum);

        await Assert.That(c.Kind).IsEqualTo(ElicitationKind.MultiSelect);
        await Assert.That(c.Options.Select(o => o.OptionId).ToArray()).IsEquivalentTo(new[] { "x", "y", "z" });
        // No declared bounds: effective min is 1 (this client never submits zero selections),
        // effective max is the option count.
        await Assert.That(c.MinSelections).IsEqualTo(1);
        await Assert.That(c.MaxSelections).IsEqualTo(3);
    }

    [Test]
    public async Task MultiSelectTitledAnyOf_ClassifiesWithConstIdsAndTitleLabels() {
        var c = MustClassify(ElicitationFixtures.Schema_MultiSelectTitledAnyOf);

        await Assert.That(c.Kind).IsEqualTo(ElicitationKind.MultiSelect);
        await Assert.That(c.Options.Select(o => o.OptionId).ToArray()).IsEquivalentTo(new[] { "x", "y", "z" });
        await Assert.That(c.Options.Select(o => o.Label).ToArray()).IsEquivalentTo(new[] { "Ex", "Why", "Zed" });
    }

    /// <summary>Mutation anchor (spec §8): BOTH the option set AND the bounds are asserted here,
    /// so dropping either from the classification fails this test.</summary>
    [Test]
    public async Task MultiSelectWithBounds_CarriesDeclaredBoundsAndOptions() {
        var c = MustClassify(ElicitationFixtures.Schema_MultiSelectWithBounds);

        await Assert.That(c.Options.Select(o => o.OptionId).ToArray()).IsEquivalentTo(new[] { "x", "y", "z" });
        await Assert.That(c.MinSelections).IsEqualTo(1);
        await Assert.That(c.MaxSelections).IsEqualTo(2);
    }

    [Test]
    public async Task DuplicateEnumValues_DedupByFirstOccurrence() {
        var c = MustClassify(ElicitationFixtures.Schema_DuplicateEnumValues);

        await Assert.That(c.Options.Select(o => o.OptionId).ToArray()).IsEquivalentTo(new[] { "dup", "solo" });
    }

    [Test]
    public async Task EmptyStringEnumValue_IsALegitimateOfferedId() {
        var c = MustClassify(ElicitationFixtures.Schema_EmptyStringEnumValue);

        await Assert.That(c.Options.Select(o => o.OptionId).ToArray()).IsEquivalentTo(new[] { "", "real" });
    }

    [Test]
    public async Task PropertyTitleAndDescription_AreCarriedOnTheClassification() {
        var c = MustClassify(ElicitationFixtures.Schema_PropertyTitleAndDescription);

        await Assert.That(c.Title).IsEqualTo("The title");
        await Assert.That(c.Description).IsEqualTo("The description");
    }

    [Test]
    public async Task NullEnumWithOneOf_NullSelectorIsAbsent_ClassifiesViaOneOf() {
        var c = MustClassify(ElicitationFixtures.Schema_NullEnumWithOneOf);

        await Assert.That(c.Kind).IsEqualTo(ElicitationKind.SingleSelect);
        await Assert.That(c.Options.Select(o => o.Label).ToArray()).IsEquivalentTo(new[] { "Alpha" });
    }

    [Test]
    public async Task NullRequiredAndNullBoundsAndNullMetadata_AreAbsent() {
        var required = MustClassify(ElicitationFixtures.Schema_NullRequired);
        await Assert.That(required.Kind).IsEqualTo(ElicitationKind.SingleSelect);

        var bounds = MustClassify(ElicitationFixtures.Schema_NullBounds);
        await Assert.That(bounds.MinSelections).IsEqualTo(1);
        await Assert.That(bounds.MaxSelections).IsEqualTo(2);

        var meta = MustClassify(ElicitationFixtures.Schema_NullTitleDescription);
        await Assert.That(meta.Title).IsNull();
        await Assert.That(meta.Description).IsNull();
    }

    [Test]
    public async Task IntMaxMaxItems_IsSupportedAndClampedToOptionCount() {
        var c = MustClassify(ElicitationFixtures.Schema_IntMaxMaxItems);

        await Assert.That(c.MinSelections).IsEqualTo(1);
        await Assert.That(c.MaxSelections).IsEqualTo(2);
    }

    [Test]
    public async Task ExactCapFixtures_AreRenderable() {
        await Assert.That(MustClassify(ElicitationFixtures.Schema_ExactCapSchema).Kind).IsEqualTo(ElicitationKind.SingleSelect);
        await Assert.That(MustClassify(ElicitationFixtures.Schema_ExactCapOptionLen).Options[0].OptionId.Length).IsEqualTo(1024);
        await Assert.That(MustClassify(ElicitationFixtures.Schema_ExactCapOptionCount).Options.Length).IsEqualTo(32);
        await Assert.That(MustClassify(ElicitationFixtures.Schema_MultibyteOptionAtCap).Options[0].OptionId.Length).IsEqualTo(1024);
        await Assert.That(MustClassify(ElicitationFixtures.Schema_EscapeHeavyOptionUnderCap).Kind).IsEqualTo(ElicitationKind.SingleSelect);
    }

    // ===== Non-string metadata (group D): treated absent, never a throw =====

    [Test]
    [Arguments(ElicitationFixtures.Schema_MetaNumberTitle)]
    [Arguments(ElicitationFixtures.Schema_MetaBooleanTitle)]
    [Arguments(ElicitationFixtures.Schema_MetaObjectTitle)]
    [Arguments(ElicitationFixtures.Schema_MetaArrayTitle)]
    public async Task NonStringTitle_IsTreatedAbsent(string fixture) {
        var c = MustClassify(fixture);
        await Assert.That(c.Title).IsNull();
        await Assert.That(c.Kind).IsEqualTo(ElicitationKind.SingleSelect);
    }

    [Test]
    [Arguments(ElicitationFixtures.Schema_MetaNumberDescription)]
    [Arguments(ElicitationFixtures.Schema_MetaBooleanDescription)]
    [Arguments(ElicitationFixtures.Schema_MetaObjectDescription)]
    [Arguments(ElicitationFixtures.Schema_MetaArrayDescription)]
    public async Task NonStringDescription_IsTreatedAbsent(string fixture) {
        var c = MustClassify(fixture);
        await Assert.That(c.Description).IsNull();
    }

    // ===== Unrenderable: exact reason per fixture (groups B/C/D) =====

    [Test]
    [Arguments(ElicitationFixtures.Schema_NumberProp, ElicitationFixtures.Reason_Schema_NumberProp)]
    [Arguments(ElicitationFixtures.Schema_IntegerProp, ElicitationFixtures.Reason_Schema_IntegerProp)]
    [Arguments(ElicitationFixtures.Schema_BooleanProp, ElicitationFixtures.Reason_Schema_BooleanProp)]
    [Arguments(ElicitationFixtures.Schema_ReservedOtherPropType, ElicitationFixtures.Reason_Schema_ReservedOtherPropType)]
    [Arguments(ElicitationFixtures.Schema_ReservedOtherItemsType, ElicitationFixtures.Reason_Schema_ReservedOtherItemsType)]
    [Arguments(ElicitationFixtures.Schema_MultiProperty, ElicitationFixtures.Reason_Schema_MultiProperty)]
    [Arguments(ElicitationFixtures.Schema_ZeroProperty, ElicitationFixtures.Reason_Schema_ZeroProperty)]
    [Arguments(ElicitationFixtures.Schema_MaxItemsZero, ElicitationFixtures.Reason_Schema_MaxItemsZero)]
    [Arguments(ElicitationFixtures.Schema_MinItemsAboveOptionCount, ElicitationFixtures.Reason_Schema_MinItemsAboveOptionCount)]
    [Arguments(ElicitationFixtures.Schema_MinAboveMax, ElicitationFixtures.Reason_Schema_MinAboveMax)]
    [Arguments(ElicitationFixtures.Schema_TooManyOptions, ElicitationFixtures.Reason_Schema_TooManyOptions)]
    [Arguments(ElicitationFixtures.Schema_OptionTooLong, ElicitationFixtures.Reason_Schema_OptionTooLong)]
    [Arguments(ElicitationFixtures.Schema_MultibyteOptionOverCap, ElicitationFixtures.Reason_Schema_MultibyteOptionOverCap)]
    [Arguments(ElicitationFixtures.Schema_SchemaTooLarge, ElicitationFixtures.Reason_Schema_SchemaTooLarge)]
    [Arguments(ElicitationFixtures.Schema_MultibyteSchemaOverCap, ElicitationFixtures.Reason_Schema_MultibyteSchemaOverCap)]
    [Arguments(ElicitationFixtures.Schema_StringEnumPlusOneOf, ElicitationFixtures.Reason_Schema_StringEnumPlusOneOf)]
    [Arguments(ElicitationFixtures.Schema_EmptyStringPropEnum, ElicitationFixtures.Reason_Schema_EmptyStringPropEnum)]
    [Arguments(ElicitationFixtures.Schema_EmptyStringPropOneOf, ElicitationFixtures.Reason_Schema_EmptyStringPropOneOf)]
    [Arguments(ElicitationFixtures.Schema_BoundIntMaxPlusOne, ElicitationFixtures.Reason_Schema_BoundIntMaxPlusOne)]
    [Arguments(ElicitationFixtures.Schema_BoundULongMax, ElicitationFixtures.Reason_Schema_BoundULongMax)]
    [Arguments(ElicitationFixtures.Schema_RequiredNamingOtherProperty, ElicitationFixtures.Reason_Schema_RequiredNamingOtherProperty)]
    [Arguments(ElicitationFixtures.Schema_EmptyItemsEnum, ElicitationFixtures.Reason_Schema_EmptyItemsEnum)]
    [Arguments(ElicitationFixtures.Schema_EmptyItemsAnyOf, ElicitationFixtures.Reason_Schema_EmptyItemsAnyOf)]
    [Arguments(ElicitationFixtures.Schema_NonObjectRoot, ElicitationFixtures.Reason_Schema_NonObjectRoot)]
    [Arguments(ElicitationFixtures.Schema_WrongRootType, ElicitationFixtures.Reason_Schema_WrongRootType)]
    [Arguments(ElicitationFixtures.Schema_PropertiesNull, ElicitationFixtures.Reason_Schema_PropertiesNull)]
    [Arguments(ElicitationFixtures.Schema_PropertiesNonObject, ElicitationFixtures.Reason_Schema_PropertiesNonObject)]
    [Arguments(ElicitationFixtures.Schema_NonObjectPropertySchema, ElicitationFixtures.Reason_Schema_NonObjectPropertySchema)]
    [Arguments(ElicitationFixtures.Schema_MissingPropType, ElicitationFixtures.Reason_Schema_MissingPropType)]
    [Arguments(ElicitationFixtures.Schema_NonStringPropType, ElicitationFixtures.Reason_Schema_NonStringPropType)]
    [Arguments(ElicitationFixtures.Schema_WrongKindSelector, ElicitationFixtures.Reason_Schema_WrongKindSelector)]
    [Arguments(ElicitationFixtures.Schema_NonStringEnumEntry, ElicitationFixtures.Reason_Schema_NonStringEnumEntry)]
    [Arguments(ElicitationFixtures.Schema_NonObjectItems, ElicitationFixtures.Reason_Schema_NonObjectItems)]
    [Arguments(ElicitationFixtures.Schema_NullItemsEnum, ElicitationFixtures.Reason_Schema_NullItemsEnum)]
    [Arguments(ElicitationFixtures.Schema_NullItemsAnyOf, ElicitationFixtures.Reason_Schema_NullItemsAnyOf)]
    [Arguments(ElicitationFixtures.Schema_EnumOptionMissingTitle, ElicitationFixtures.Reason_Schema_EnumOptionMissingTitle)]
    [Arguments(ElicitationFixtures.Schema_EnumOptionNonStringConst, ElicitationFixtures.Reason_Schema_EnumOptionNonStringConst)]
    [Arguments(ElicitationFixtures.Schema_RequiredNonArray, ElicitationFixtures.Reason_Schema_RequiredNonArray)]
    [Arguments(ElicitationFixtures.Schema_RequiredNonStringEntry, ElicitationFixtures.Reason_Schema_RequiredNonStringEntry)]
    [Arguments(ElicitationFixtures.Schema_NegativeBound, ElicitationFixtures.Reason_Schema_NegativeBound)]
    [Arguments(ElicitationFixtures.Schema_FractionalBound, ElicitationFixtures.Reason_Schema_FractionalBound)]
    [Arguments(ElicitationFixtures.Schema_ItemsEnumPlusAnyOf, ElicitationFixtures.Reason_Schema_ItemsEnumPlusAnyOf)]
    [Arguments(ElicitationFixtures.Schema_Bound100Digits, ElicitationFixtures.Reason_Schema_Bound100Digits)]
    [Arguments(ElicitationFixtures.Schema_BoundExponent1e3, ElicitationFixtures.Reason_Schema_BoundExponent1e3)]
    [Arguments(ElicitationFixtures.Schema_Bound1e30, ElicitationFixtures.Reason_Schema_Bound1e30)]
    [Arguments(ElicitationFixtures.Schema_BoundDecimal5Point0, ElicitationFixtures.Reason_Schema_BoundDecimal5Point0)]
    [Arguments(ElicitationFixtures.Schema_BoundNegativeZero, ElicitationFixtures.Reason_Schema_BoundNegativeZero)]
    public async Task Unrenderable_ReportsExactReason(string fixture, string expectedReason) {
        var ok = ElicitationSchemaClassifier.TryClassify(Parse(fixture), out _, out var reason);

        await Assert.That(ok).IsFalse();
        await Assert.That(reason).IsEqualTo(expectedReason);
    }

    // ===== Stage-order pins (spec §4.2: the FIRST failing stage's reason wins) =====

    /// <summary>A multi-property schema whose children are ALSO malformed reports the count
    /// (stage 3), never the children (stage 4+).</summary>
    [Test]
    public async Task MultiPropertyWithMalformedChildren_ReportsMultiProperty() {
        ElicitationSchemaClassifier.TryClassify(Parse(ElicitationFixtures.Schema_MultiPropertyMalformedChildren), out _, out var reason);
        await Assert.That(reason).IsEqualTo("multi_property");
    }

    /// <summary>A 40-entry selector containing a malformed entry reports the entry-count cap
    /// (stage 5c, checked before per-entry validation), never the malformed entry.</summary>
    [Test]
    public async Task Malformed40EntrySelector_ReportsTooManyOptions() {
        ElicitationSchemaClassifier.TryClassify(Parse(ElicitationFixtures.Schema_Malformed40EntrySelector), out _, out var reason);
        await Assert.That(reason).IsEqualTo("too_many_options");
    }

    /// <summary>Per-entry validation runs in array order, shape before length: a malformed FIRST
    /// entry beats an over-long SECOND entry.</summary>
    [Test]
    public async Task MalformedEarlyEntryPlusOverlongLaterEntry_ReportsMalformedSchema() {
        ElicitationSchemaClassifier.TryClassify(Parse(ElicitationFixtures.Schema_MalformedEarlyEntryOverlongLater), out _, out var reason);
        await Assert.That(reason).IsEqualTo("malformed_schema");
    }
}
