using System.Xml.Linq;
using D365FO.Core.Index;
using D365FO.Core.Scaffolding;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// The TRUDUtils-derived table augmenters (upstream generate_object modes
/// table-relation / find-methods): explicit relations from EDT references, and the
/// standard static find methods from the unique index.
/// </summary>
public class TableAugmentScaffolderTests
{
    private static TableDetails Table(
        IReadOnlyList<TableFieldInfo>? fields = null,
        IReadOnlyList<TableIndexInfo>? indexes = null,
        IReadOnlyList<TableMethodInfo>? methods = null)
        => new(
            new TableInfo(1, "ConDemoOrderLine", "ConDemo", null, null),
            fields ?? [],
            [],
            methods ?? [],
            indexes ?? [],
            []);

    private static EdtInfo Edt(string name, string? referenceTable) =>
        new(name, "Foundation", null, "String", null, 20, ReferenceTable: referenceTable);

    // ── Relations ────────────────────────────────────────────────────────────

    [Fact]
    public void DeriveRelations_maps_edt_reference_to_constraint_and_skips_system_fields()
    {
        var table = Table(fields:
        [
            new TableFieldInfo("ItemId", "String", "ItemId", null, true),
            new TableFieldInfo("Notes", "String", "FreeTxt", null, false),
            new TableFieldInfo("CreatedBy", "String", "UserId", null, false),
        ]);
        EdtInfo? Lookup(string edt) => edt switch
        {
            "ItemId" => Edt("ItemId", "InventTable"),
            "FreeTxt" => Edt("FreeTxt", null),
            "UserId" => Edt("UserId", "UserInfo"),
            _ => null,
        };

        var relations = TableAugmentScaffolder.DeriveRelations(table, Lookup, null, out var skipped);

        var rel = Assert.Single(relations);
        // The EDT name is the canonical PK field on the target: ItemId → InventTable.ItemId.
        Assert.Equal("ItemId", rel.Field);
        Assert.Equal("InventTable", rel.RelatedTable);
        Assert.Equal("ItemId", rel.RelatedField);
        // CreatedBy is kernel-managed — never a derived relation, even with a referencing EDT.
        Assert.DoesNotContain(relations, r => r.Field == "CreatedBy");
        // Unfiltered scans stay quiet about non-candidates.
        Assert.Empty(skipped);
    }

    [Fact]
    public void DeriveRelations_reports_why_an_explicitly_requested_field_produced_nothing()
    {
        var table = Table(fields: [new TableFieldInfo("Notes", "String", "FreeTxt", null, false)]);
        var relations = TableAugmentScaffolder.DeriveRelations(
            table, _ => Edt("FreeTxt", null), ["Notes"], out var skipped);

        Assert.Empty(relations);
        Assert.Contains(skipped, s => s.Contains("Notes") && s.Contains("no reference table"));
    }

    [Fact]
    public void RelationElement_pins_the_concrete_constraint_type()
    {
        var xml = TableAugmentScaffolder.RelationElement(
            new TableRelationInfo("ItemId", "ItemId", "InventTable", "ItemId")).ToString();

        Assert.Contains("<Name>ItemId</Name>", xml);
        Assert.Contains("<RelatedTable>InventTable</RelatedTable>", xml);
        // The abstract constraint element must pin its concrete type or the reader drops it.
        Assert.Contains("type=\"AxTableRelationConstraintField\"", xml);
        Assert.Contains("<RelatedField>ItemId</RelatedField>", xml);
        Assert.Contains("<Cardinality>ZeroMore</Cardinality>", xml);
        Assert.Contains("<RelationshipType>Association</RelationshipType>", xml);
    }

    [Fact]
    public void MergeRelations_creates_the_block_dedupes_and_keeps_serializer_order()
    {
        XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
        var doc = new XDocument(new XElement("AxTable",
            new XAttribute(XNamespace.Xmlns + "i", xsi.NamespaceName),
            new XElement("Name", "ConDemoOrderLine"),
            new XElement("Fields"),
            new XElement("Relations",
                new XElement("AxTableRelation", new XElement("Name", "ItemId")))));

        var added = TableAugmentScaffolder.MergeRelations(doc,
        [
            new TableRelationInfo("ItemId", "ItemId", "InventTable", "ItemId"),   // already there
            new TableRelationInfo("CustAccount", "CustAccount", "CustTable", "CustAccount"),
        ]);

        Assert.Equal(["CustAccount"], added);
        Assert.Equal(2, doc.Root!.Element("Relations")!.Elements("AxTableRelation").Count());
        // The serializer reads members in contract order — Relations must not trail Fields
        // out of order after the merge (a misordered member is dropped silently).
        var names = doc.Root.Elements().Select(e => e.Name.LocalName).ToList();
        Assert.True(names.IndexOf("Relations") > names.IndexOf("Name"));
    }

    [Fact]
    public void MergeRelations_refuses_a_non_table_document()
    {
        var doc = new XDocument(new XElement("AxClass", new XElement("Name", "X")));
        Assert.Throws<InvalidOperationException>(() =>
            TableAugmentScaffolder.MergeRelations(doc, [new TableRelationInfo("A", "A", "B", "A")]));
    }

    // ── Find methods ─────────────────────────────────────────────────────────

    [Fact]
    public void ResolveKeyFields_prefers_the_alternate_key_index_and_types_from_edts()
    {
        var table = Table(
            fields:
            [
                new TableFieldInfo("OrderId", "String", "ConDemoOrderId", null, true),
                new TableFieldInfo("LineNum", "Int64", null, null, true),
            ],
            indexes:
            [
                new TableIndexInfo("DateIdx", AllowDuplicates: true, AlternateKey: false, FieldsCsv: "TransDate"),
                new TableIndexInfo("OrderLineIdx", AllowDuplicates: false, AlternateKey: true, FieldsCsv: "OrderId,LineNum"),
            ]);

        var keys = TableAugmentScaffolder.ResolveKeyFields(table, null);
        Assert.Equal(2, keys.Count);
        Assert.Equal(("OrderId", "ConDemoOrderId"), (keys[0].Field, keys[0].Type));
        // No EDT on the field — the base type maps onto the X++ primitive.
        Assert.Equal(("LineNum", "int64"), (keys[1].Field, keys[1].Type));

        // Explicit override wins outright.
        var overridden = TableAugmentScaffolder.ResolveKeyFields(table, ["OrderId"]);
        Assert.Single(overridden);
    }

    [Fact]
    public void BuildFindMethods_follows_the_shipped_convention()
    {
        var methods = TableAugmentScaffolder.BuildFindMethods(
            "ConDemoOrderLine",
            [new FindKeyField("OrderId", "ConDemoOrderId")]);

        Assert.Equal(["find", "exists", "findRecId"], methods.Select(m => m.Name));
        var find = methods[0].Source;
        Assert.Contains("public static ConDemoOrderLine find(ConDemoOrderId _orderId, boolean _forUpdate = false)", find);
        Assert.Contains("if (_orderId)", find);                                  // key null-guard
        Assert.Contains("conDemoOrderLine.selectForUpdate(_forUpdate);", find);  // forUpdate guard
        Assert.Contains("select firstonly conDemoOrderLine", find);
        var exists = methods[1].Source;
        Assert.Contains("select firstonly RecId from conDemoOrderLine", exists);
        Assert.Contains(".RecId != 0;", exists);
    }

    [Fact]
    public void BuildFindMethods_without_keys_still_emits_findRecId()
    {
        // RecId always exists — a table with no determinable unique key still gets a lookup.
        var methods = TableAugmentScaffolder.BuildFindMethods("ConDemoOrderLine", []);
        var m = Assert.Single(methods);
        Assert.Equal("findRecId", m.Name);
        Assert.Contains("where conDemoOrderLine.RecId == _recId;", m.Source);
    }

    [Fact]
    public void MergeMethods_creates_SourceCode_and_never_overwrites_an_existing_method()
    {
        var doc = new XDocument(new XElement("AxTable",
            new XElement("Name", "ConDemoOrderLine"),
            new XElement("Fields")));

        var methods = TableAugmentScaffolder.BuildFindMethods(
            "ConDemoOrderLine", [new FindKeyField("OrderId", "ConDemoOrderId")]);
        var added = TableAugmentScaffolder.MergeMethods(doc, "ConDemoOrderLine", methods);
        Assert.Equal(["find", "exists", "findRecId"], added);
        Assert.Contains("public class ConDemoOrderLine extends common",
            doc.Root!.Element("SourceCode")!.Element("Declaration")!.Value);

        // Second merge: everything already there — nothing added, nothing replaced.
        var again = TableAugmentScaffolder.MergeMethods(doc, "ConDemoOrderLine", methods);
        Assert.Empty(again);
        Assert.Equal(3, doc.Root.Element("SourceCode")!.Element("Methods")!.Elements("Method").Count());
    }
}
