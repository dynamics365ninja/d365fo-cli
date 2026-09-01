// <copyright file="TableMergeAnalyzer.cs" company="d365fo-cli contributors">
// MIT
// </copyright>

using System.Xml.Linq;

namespace D365FO.Core.Index;

/// <summary>One member of a merged table, and where it came from.</summary>
/// <param name="Name">Member name as the AOT spells it.</param>
/// <param name="Origin">The base table, or the extension that contributes it.</param>
/// <param name="Model">Model the contributor belongs to.</param>
/// <param name="Detail">Type, key fields, or related table — whatever identifies the member.</param>
public sealed record MergedMember(string Name, string Origin, string? Model, string? Detail);

/// <summary>The effective shape of a table once every extension of it is folded in.</summary>
public sealed record MergedTableSchema(
    string Table,
    string? BaseModel,
    IReadOnlyList<string> Extensions,
    IReadOnlyList<MergedMember> Fields,
    IReadOnlyList<MergedMember> Indexes,
    IReadOnlyList<MergedMember> Relations,
    IReadOnlyList<MergedMember> FieldGroups,
    IReadOnlyList<string> Unreadable);

/// <summary>
/// Folds a table's extensions onto the base table to produce the shape the AOS actually sees.
/// </summary>
/// <remarks>
/// <para>
/// The index answers "what extends CustTable" — a roster of extension names. It does not answer
/// the question a developer is really asking, which is "what fields does CustTable HAVE here",
/// and the two differ by exactly the customisations that matter. Before this, the roster was
/// returned under a contract that promised a merged schema; a caller who trusted it read the
/// absence of a field as the field not existing.
/// </para>
/// <para>
/// Extensions are read from their own XML rather than from the index, because the index stores
/// an extension as a row about the relationship, not as a member list. An extension whose file
/// cannot be read is REPORTED in <see cref="MergedTableSchema.Unreadable"/> rather than skipped:
/// a merged schema missing a contributor is worse than no merged schema at all, and the caller
/// has to be able to tell the two apart.
/// </para>
/// </remarks>
public static class TableMergeAnalyzer
{
    public static MergedTableSchema Merge(MetadataRepository repo, string tableName)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        var details = repo.GetTableDetails(tableName);
        var baseModel = details?.Table.Model;
        var table = details?.Table.Name ?? tableName;

        var fields = new List<MergedMember>();
        var indexes = new List<MergedMember>();
        var relations = new List<MergedMember>();
        var fieldGroups = new List<MergedMember>();
        var unreadable = new List<string>();

        if (details is not null)
        {
            foreach (var f in details.Fields)
                fields.Add(new MergedMember(f.Name, table, baseModel, f.EdtName ?? f.Type));
            foreach (var i in details.Indexes)
                indexes.Add(new MergedMember(i.Name, table, baseModel, i.FieldsCsv));
            foreach (var r in details.Relations)
                relations.Add(new MergedMember(r.RelationName ?? r.ToTable, table, baseModel, r.ToTable));
        }

        var extensions = repo.FindExtensions(table, "Table");
        foreach (var extension in extensions)
        {
            if (string.IsNullOrWhiteSpace(extension.SourcePath) || !File.Exists(extension.SourcePath))
            {
                unreadable.Add($"{extension.ExtensionName} ({extension.Model}): source file not on disk");
                continue;
            }

            XDocument doc;
            try
            {
                doc = XDocument.Load(extension.SourcePath!);
            }
            catch (Exception ex)
            {
                unreadable.Add($"{extension.ExtensionName} ({extension.Model}): {ex.GetType().Name}");
                continue;
            }

            var root = doc.Root;
            if (root is null)
            {
                unreadable.Add($"{extension.ExtensionName} ({extension.Model}): empty document");
                continue;
            }

            Collect(root, "Fields", "AxTableField", "ExtendedDataType", extension, fields, "EnumType");
            Collect(root, "Indexes", "AxTableIndex", null, extension, indexes);
            Collect(root, "Relations", "AxTableRelation", "RelatedTable", extension, relations);
            Collect(root, "FieldGroups", "AxTableFieldGroup", null, extension, fieldGroups);
        }

        return new MergedTableSchema(
            table,
            baseModel,
            extensions.Select(e => e.ExtensionName).ToList(),
            fields, indexes, relations, fieldGroups,
            unreadable);
    }

    /// <summary>
    /// Append one collection's members from an extension document.
    /// </summary>
    /// <remarks>
    /// A member an extension redeclares is recorded a SECOND time rather than replacing the
    /// first. Two contributors naming the same field is a real conflict the AOS resolves by
    /// layer, and collapsing it here would hide the thing most worth seeing.
    /// </remarks>
    private static void Collect(
        XElement root,
        string collection,
        string itemPrefix,
        string? detailElement,
        ObjectExtensionInfo extension,
        List<MergedMember> into,
        string? fallbackDetailElement = null)
    {
        var container = root.Elements().FirstOrDefault(e => e.Name.LocalName == collection);
        if (container is null) return;

        foreach (var item in container.Elements()
                     .Where(e => e.Name.LocalName.StartsWith(itemPrefix, StringComparison.Ordinal)))
        {
            var name = Value(item, "Name");
            if (string.IsNullOrEmpty(name)) continue;

            var detail = detailElement is null ? null : Value(item, detailElement);
            if (string.IsNullOrEmpty(detail) && fallbackDetailElement is not null)
                detail = Value(item, fallbackDetailElement);

            into.Add(new MergedMember(name!, extension.ExtensionName, extension.Model, detail));
        }
    }

    private static string? Value(XElement parent, string localName) =>
        parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName)?.Value;
}
