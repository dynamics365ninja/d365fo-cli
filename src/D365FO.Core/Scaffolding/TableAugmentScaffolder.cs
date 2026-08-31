// <copyright file="TableAugmentScaffolder.cs" company="d365fo-cli contributors">
// MIT
// </copyright>

using System.Xml.Linq;
using D365FO.Core.Index;

namespace D365FO.Core.Scaffolding;

/// <summary>One explicit table relation derived from a field's EDT reference.</summary>
/// <param name="Field">Field on the owning table.</param>
/// <param name="Edt">The field's EDT (its name is the canonical PK field on the target).</param>
/// <param name="RelatedTable">Table the EDT references.</param>
/// <param name="RelatedField">PK field on the related table the constraint binds to.</param>
public sealed record TableRelationInfo(string Field, string Edt, string RelatedTable, string RelatedField);

/// <summary>A key field for the generated find()/exists() pair.</summary>
public sealed record FindKeyField(string Field, string Type);

/// <summary>
/// Augments an EXISTING table the caller owns: explicit relations and the standard static
/// find methods. Ports the upstream MCP server's TRUDUtils-derived
/// <c>generate_object(mode="table-relation")</c> and <c>mode="find-methods"</c>
/// (d365fo-mcp-server 1.0, "port TRUDUtils generators").
/// </summary>
public static class TableAugmentScaffolder
{
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    /// <summary>Kernel-managed fields no relation is ever derived for.</summary>
    public static readonly HashSet<string> SystemFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "RecId", "RecVersion", "DataAreaId", "Partition", "CreatedBy", "CreatedDateTime",
        "ModifiedBy", "ModifiedDateTime", "CreatedTransactionId", "ModifiedTransactionId",
    };

    // ── Relations ────────────────────────────────────────────────────────────

    /// <summary>
    /// Derive the explicit relations a table's EDT-backed fields imply. For a field whose
    /// EDT declares a reference table, D365FO requires the implicit EDT relation to be
    /// migrated to an explicit <c>&lt;AxTableRelation&gt;</c> (<c>BPErrorEDTNotMigrated</c>).
    /// The EDT name is the canonical PK field name on the target table
    /// (<c>ItemId → InventTable.ItemId</c>).
    /// </summary>
    /// <param name="skipped">Fields that produced no relation, with the reason — reported
    /// only for explicitly requested fields, matching the upstream behaviour.</param>
    public static IReadOnlyList<TableRelationInfo> DeriveRelations(
        TableDetails table,
        Func<string, EdtInfo?> edtLookup,
        IReadOnlyCollection<string>? fieldFilter,
        out List<string> skipped)
    {
        skipped = [];
        var wanted = fieldFilter is { Count: > 0 }
            ? new HashSet<string>(fieldFilter, StringComparer.OrdinalIgnoreCase)
            : null;

        var relations = new List<TableRelationInfo>();
        foreach (var f in table.Fields)
        {
            if (SystemFields.Contains(f.Name)) continue;
            if (wanted is not null && !wanted.Contains(f.Name)) continue;
            if (string.IsNullOrWhiteSpace(f.EdtName))
            {
                if (wanted is not null) skipped.Add($"{f.Name}: no EDT on the field");
                continue;
            }
            var edt = edtLookup(f.EdtName!);
            if (string.IsNullOrWhiteSpace(edt?.ReferenceTable))
            {
                if (wanted is not null) skipped.Add($"{f.Name} ({f.EdtName}): EDT carries no reference table");
                continue;
            }
            relations.Add(new TableRelationInfo(f.Name, edt!.Name, edt.ReferenceTable!, edt.Name));
        }
        return relations;
    }

    /// <summary>
    /// Render one <c>&lt;AxTableRelation&gt;</c> element in the shape the serializer reads
    /// (concrete constraint type pinned via <c>i:type="AxTableRelationConstraintField"</c>).
    /// </summary>
    public static XElement RelationElement(TableRelationInfo rel) => new(
        "AxTableRelation",
        new XElement("Name", rel.Field),
        new XElement("Cardinality", "ZeroMore"),
        new XElement("RelatedTable", rel.RelatedTable),
        new XElement("RelatedTableCardinality", "ExactlyOne"),
        new XElement("RelationshipType", "Association"),
        new XElement("Constraints",
            new XElement("AxTableRelationConstraint",
                new XAttribute(Xsi + "type", "AxTableRelationConstraintField"),
                new XElement("Name", rel.Field),
                new XElement("Field", rel.Field),
                new XElement("RelatedField", rel.RelatedField))));

    /// <summary>
    /// Merge relations into an existing AxTable document. A relation whose name the table
    /// already declares is skipped, never duplicated. Members are re-canonicalized to
    /// serializer order afterwards — an out-of-order element is dropped SILENTLY on read.
    /// Returns the relation names actually added.
    /// </summary>
    public static IReadOnlyList<string> MergeRelations(XDocument tableDoc, IEnumerable<TableRelationInfo> relations)
    {
        var root = tableDoc.Root ?? throw new InvalidOperationException("Empty document.");
        if (root.Name.LocalName != "AxTable")
            throw new InvalidOperationException($"Expected an <AxTable> root, found <{root.Name.LocalName}>.");

        var relationsEl = root.Element("Relations");
        if (relationsEl is null)
        {
            relationsEl = new XElement("Relations");
            root.Add(relationsEl);
        }

        var existing = relationsEl.Elements("AxTableRelation")
            .Select(r => r.Element("Name")?.Value)
            .Where(n => !string.IsNullOrEmpty(n))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var added = new List<string>();
        foreach (var rel in relations)
        {
            if (existing.Contains(rel.Field)) continue;
            relationsEl.Add(RelationElement(rel));
            added.Add(rel.Field);
        }

        if (added.Count > 0) ContractOrderCanonicalizer.Apply(tableDoc);
        return added;
    }

    // ── Find methods ─────────────────────────────────────────────────────────

    /// <summary>
    /// Resolve the key fields find()/exists() should select on. Preference: explicit
    /// override → the alternate-key index → the first unique index. Empty when no unique
    /// key can be determined (findRecId is still valid — RecId always exists).
    /// </summary>
    public static IReadOnlyList<FindKeyField> ResolveKeyFields(TableDetails table, IReadOnlyList<string>? @override)
    {
        string TypeOf(string fieldName)
        {
            var f = table.Fields.FirstOrDefault(x => string.Equals(x.Name, fieldName, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(f?.EdtName)) return f!.EdtName!;
            return (f?.Type ?? "").ToLowerInvariant() switch
            {
                "int" or "integer" or "enum" => "int",
                "int64" => "int64",
                "real" => "real",
                "date" => "date",
                "utcdatetime" or "datetime" => "utcdatetime",
                "guid" => "guid",
                _ => "str",
            };
        }

        List<FindKeyField> ToKeys(IEnumerable<string> names) =>
            names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => new FindKeyField(n, TypeOf(n))).ToList();

        if (@override is { Count: > 0 }) return ToKeys(@override);

        var pk = table.Indexes.FirstOrDefault(i => i.AlternateKey && !string.IsNullOrWhiteSpace(i.FieldsCsv))
              ?? table.Indexes.FirstOrDefault(i => !i.AllowDuplicates && !string.IsNullOrWhiteSpace(i.FieldsCsv));
        if (pk is null) return [];
        return ToKeys(pk.FieldsCsv!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
    }

    private static string BufferName(string table)
        => table.Length > 0 ? char.ToLowerInvariant(table[0]) + table[1..] : table;

    private static string ParamName(string field)
        => "_" + char.ToLowerInvariant(field[0]) + field[1..];

    /// <summary>
    /// Render find()/exists()/findRecId() for a table, following Microsoft's shipped
    /// convention (selectForUpdate guard, firstonly, key null-guard) so the output compiles
    /// and matches BP expectations. When <paramref name="keys"/> is empty, key-based
    /// find()/exists() are skipped and only findRecId() is produced.
    /// </summary>
    public static IReadOnlyList<(string Name, string Source)> BuildFindMethods(
        string tableName,
        IReadOnlyList<FindKeyField> keys,
        bool includeExists = true,
        bool includeFindRecId = true)
    {
        var buf = BufferName(tableName);
        var methods = new List<(string, string)>();

        if (keys.Count > 0)
        {
            var params_ = string.Join(", ", keys.Select(k => $"{k.Type} {ParamName(k.Field)}"));
            var guard = string.Join(" && ", keys.Select(k => ParamName(k.Field)));
            var whereClause = string.Join("", keys.Select((k, i) =>
                $"{(i == 0 ? "" : "\n           && ")}{buf}.{k.Field} == {ParamName(k.Field)}"));

            methods.Add(("find",
                "/// <summary>\n" +
                $"/// Finds the <c>{tableName}</c> record matching the supplied key.\n" +
                "/// </summary>\n" +
                $"public static {tableName} find({params_}, boolean _forUpdate = false)\n" +
                "{\n" +
                $"    {tableName} {buf};\n" +
                "\n" +
                $"    if ({guard})\n" +
                "    {\n" +
                $"        {buf}.selectForUpdate(_forUpdate);\n" +
                "\n" +
                $"        select firstonly {buf}\n" +
                $"            where {whereClause};\n" +
                "    }\n" +
                "\n" +
                $"    return {buf};\n" +
                "}\n"));

            if (includeExists)
            {
                var existsWhere = string.Join("", keys.Select((k, i) =>
                    $"{(i == 0 ? "" : "\n               && ")}{buf}.{k.Field} == {ParamName(k.Field)}"));
                methods.Add(("exists",
                    "/// <summary>\n" +
                    $"/// Determines whether a <c>{tableName}</c> record exists for the supplied key.\n" +
                    "/// </summary>\n" +
                    $"public static boolean exists({params_})\n" +
                    "{\n" +
                    $"    return {guard}\n" +
                    $"        && (select firstonly RecId from {buf}\n" +
                    $"               where {existsWhere}).RecId != 0;\n" +
                    "}\n"));
            }
        }

        if (includeFindRecId)
        {
            methods.Add(("findRecId",
                "/// <summary>\n" +
                $"/// Finds the <c>{tableName}</c> record with the supplied <c>RecId</c>.\n" +
                "/// </summary>\n" +
                $"public static {tableName} findRecId(RefRecId _recId, boolean _forUpdate = false)\n" +
                "{\n" +
                $"    {tableName} {buf};\n" +
                "\n" +
                "    if (_recId)\n" +
                "    {\n" +
                $"        {buf}.selectForUpdate(_forUpdate);\n" +
                "\n" +
                $"        select firstonly {buf}\n" +
                $"            where {buf}.RecId == _recId;\n" +
                "    }\n" +
                "\n" +
                $"    return {buf};\n" +
                "}\n"));
        }

        return methods;
    }

    /// <summary>
    /// Merge methods into an existing AxTable document's <c>&lt;SourceCode&gt;</c>, creating
    /// the block (with an empty class declaration) when the table has none. A method whose
    /// name the table already declares is skipped, never overwritten. Returns the method
    /// names actually added.
    /// </summary>
    public static IReadOnlyList<string> MergeMethods(
        XDocument tableDoc, string tableName, IEnumerable<(string Name, string Source)> methods)
    {
        var root = tableDoc.Root ?? throw new InvalidOperationException("Empty document.");
        if (root.Name.LocalName != "AxTable")
            throw new InvalidOperationException($"Expected an <AxTable> root, found <{root.Name.LocalName}>.");

        var sourceCode = root.Element("SourceCode");
        if (sourceCode is null)
        {
            sourceCode = new XElement("SourceCode",
                new XElement("Declaration",
                    $"public class {tableName} extends common\n{{\n}}\n"));
            root.Add(sourceCode);
        }

        var methodsEl = sourceCode.Element("Methods");
        if (methodsEl is null)
        {
            methodsEl = new XElement("Methods");
            sourceCode.Add(methodsEl);
        }

        var existing = methodsEl.Elements("Method")
            .Select(m => m.Element("Name")?.Value)
            .Where(n => !string.IsNullOrEmpty(n))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var added = new List<string>();
        foreach (var (name, source) in methods)
        {
            if (existing.Contains(name)) continue;
            methodsEl.Add(new XElement("Method",
                new XElement("Name", name),
                new XElement("Source", source)));
            added.Add(name);
        }

        if (added.Count > 0) ContractOrderCanonicalizer.Apply(tableDoc);
        return added;
    }
}
