using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace D365FO.Core.FormPatterns;

/// <summary>The outcome of cloning a form.</summary>
/// <param name="Xml">The cloned document.</param>
/// <param name="RenamedDataSources">Datasources whose name changed because their table did.</param>
/// <param name="Rebound">Table rebinds that were actually applied.</param>
/// <param name="Warnings">Things the caller has to finish by hand.</param>
public sealed record FormCloneResult(
    string Xml,
    IReadOnlyList<string> RenamedDataSources,
    IReadOnlyList<string> Rebound,
    IReadOnlyList<string> Warnings);

/// <summary>Raised when the source is not a form, or the clone would produce a broken one.</summary>
public sealed class FormCloneException(string message) : Exception(message);

/// <summary>
/// Clones a reference form under a new name, optionally re-binding its datasources to other
/// tables.
/// </summary>
/// <remarks>
/// <para>
/// Issue #164 / R5's <c>formCloner</c>. Starting from a Microsoft form that already has the
/// pattern, the control tree and the wiring right is a far better starting point than a template,
/// and it is what a developer does by hand anyway.
/// </para>
/// <para>
/// <b>String-level by design, and this repo has the evidence for it.</b> An <c>AxForm</c> is a V6
/// contract whose Design subtree is written in the empty namespace, carries
/// <c>i:type</c> discriminators on every control and <c>&lt;FormControlExtension i:nil="true" /&gt;</c>
/// on all of them. Loading that into an <see cref="XDocument"/> and writing it back reorders
/// namespace declarations and rewrites prefixes, which is why <c>FormPatternTemplates</c> renders
/// forms as strings rather than building them as documents. A cloner that round-tripped would
/// return a form that differs from the original in ways nobody asked for, on every clone.
/// </para>
/// <para>
/// So every edit here is anchored and narrow. The one thing this deliberately does not do is a
/// blind replace of the old form name: form names are short and appear inside unrelated
/// identifiers (<c>CustTable</c> inside <c>CustTableListPage</c>), and a global replace would
/// quietly corrupt references to other objects. What it changes is the root
/// <c>&lt;Name&gt;</c>, the class declaration, and <c>formStr()</c> self-references — anything
/// else it finds, it reports rather than touches.
/// </para>
/// </remarks>
public static class FormCloner
{
    /// <summary>
    /// Clone <paramref name="sourceXml"/> as <paramref name="newName"/>.
    /// </summary>
    /// <param name="sourceXml">The reference form's XML, exactly as on disk.</param>
    /// <param name="newName">Name for the clone.</param>
    /// <param name="tableRebinds">
    /// Old table name → new table name. A datasource whose name matched its old table is renamed
    /// with it, and every control bound to that datasource follows.
    /// </param>
    public static FormCloneResult Clone(
        string sourceXml, string newName, IReadOnlyDictionary<string, string>? tableRebinds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceXml);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);

        var sourceName = ReadFormName(sourceXml)
            ?? throw new FormCloneException("Source is not an AxForm document: no root <Name> found.");

        if (string.Equals(sourceName, newName, StringComparison.Ordinal))
            throw new FormCloneException($"The clone's name is the same as the source's ('{sourceName}').");

        var warnings = new List<string>();
        var xml = sourceXml;

        // 1. The root <Name>. Anchored to the first one in the document, which is the form's own:
        //    every nested Name belongs to a datasource, a control or a method.
        xml = ReplaceFirst(xml, $"<Name>{Regex.Escape(sourceName)}</Name>", $"<Name>{newName}</Name>")
              ?? throw new FormCloneException($"Could not rewrite the root <Name> of '{sourceName}'.");

        // 2. The X++ class declaration. A form's class is named after the form, and a clone whose
        //    declaration still names the original does not compile.
        xml = Regex.Replace(
            xml,
            $@"(?<=\bclass\s+){Regex.Escape(sourceName)}(?=\s+extends\b)",
            newName);

        // 3. formStr() self-references — a form that hands its own name to the framework.
        xml = Regex.Replace(
            xml,
            $@"(?<=\bformStr\s*\(\s*){Regex.Escape(sourceName)}(?=\s*\))",
            newName);

        var (rebound, renamed) = RebindTables(ref xml, tableRebinds, warnings);

        // Menu items, security privileges and display-menu references point at the source by
        // name and live outside this document; nothing here can fix them.
        warnings.Add(
            $"'{newName}' is a copy of '{sourceName}'. Anything outside the form that referenced " +
            $"'{sourceName}' — menu items, privileges, extensions, callers using formStr — still " +
            "points at the original.");

        EnsureStillWellFormed(xml, newName);

        return new FormCloneResult(xml, renamed, rebound, warnings);
    }

    /// <summary>The form's own name, or null when the document is not a form.</summary>
    public static string? ReadFormName(string xml)
    {
        var match = Regex.Match(xml, @"<Name>(?<n>[^<]+)</Name>");
        return match.Success ? match.Groups["n"].Value.Trim() : null;
    }

    /// <summary>
    /// Point the clone's datasources at different tables.
    /// </summary>
    /// <remarks>
    /// A datasource is conventionally named after its table, and controls refer to it by that
    /// name — so rebinding the table without renaming the datasource leaves a form whose
    /// datasource is called <c>CustTable</c> and reads <c>VendTable</c>, which compiles and is
    /// a lie. When the names do not match, the datasource keeps its name and only the table
    /// moves, because the name was chosen deliberately.
    /// </remarks>
    private static (List<string> Rebound, List<string> Renamed) RebindTables(
        ref string xml, IReadOnlyDictionary<string, string>? rebinds, List<string> warnings)
    {
        var rebound = new List<string>();
        var renamed = new List<string>();
        if (rebinds is null || rebinds.Count == 0) return (rebound, renamed);

        foreach (var (oldTable, newTable) in rebinds)
        {
            if (string.IsNullOrWhiteSpace(oldTable) || string.IsNullOrWhiteSpace(newTable)) continue;

            var tableTag = $"<Table>{Regex.Escape(oldTable)}</Table>";
            if (!Regex.IsMatch(xml, tableTag))
            {
                warnings.Add($"No datasource is bound to '{oldTable}', so that rebind did nothing.");
                continue;
            }

            xml = Regex.Replace(xml, tableTag, $"<Table>{newTable}</Table>");
            rebound.Add($"{oldTable} -> {newTable}");

            // The datasource element that carried the table, and everything pointing at it.
            var dsName = $"<Name>{Regex.Escape(oldTable)}</Name>";
            if (Regex.IsMatch(xml, dsName))
            {
                xml = Regex.Replace(xml, dsName, $"<Name>{newTable}</Name>");
                xml = RenameDataSourceReferences(xml, oldTable, newTable);
                renamed.Add($"{oldTable} -> {newTable}");
            }

            warnings.Add(
                $"Fields bound through '{oldTable}' were not checked against '{newTable}'. A " +
                "<DataField> naming a column the new table does not have is a form that compiles " +
                "and fails at runtime — run `d365fo validate references` over the result.");
        }

        return (rebound, renamed);
    }

    /// <summary>
    /// Elements whose text is the name of a datasource, so a renamed datasource has to be
    /// followed into all of them.
    /// </summary>
    /// <remarks>
    /// Grounded on a live installation rather than guessed: across 300 shipped
    /// <c>ApplicationSuite\Foundation\AxForm</c> forms these are the only elements carrying a
    /// datasource name as their value. The rest of the <c>*DataSource*</c> family is either a
    /// container (<c>DataSources</c>, <c>DataSourceLinks</c>, <c>ReferencedDataSources</c>) or
    /// holds something else entirely (<c>DataSourceChangeGroupMode</c> is an enum,
    /// <c>DataSourceRelation</c> names a relation). Datasource names reached through a nested
    /// <c>&lt;Name&gt;</c> — root links, referenced and derived datasources — are already covered
    /// by the <c>&lt;Name&gt;</c> rewrite above.
    /// </remarks>
    private static readonly string[] DataSourceRefElements =
        ["DataSource", "TitleDataSource", "WorkflowDataSource", "PresenceDataSource", "JoinSource"];

    /// <summary>
    /// Repoint every reference to a renamed datasource, whatever attributes the tag carries.
    /// </summary>
    /// <remarks>
    /// The attribute tolerance is the whole point. A <c>&lt;Design&gt;</c>'s own
    /// <c>DataSource</c>/<c>TitleDataSource</c> are first-level children written in the empty
    /// namespace, so they appear as <c>&lt;DataSource xmlns=""&gt;</c>, while the control-level
    /// ones nested under <c>&lt;Controls xmlns=""&gt;</c> inherit it and appear bare. Matching only
    /// the bare form rebinds the controls and leaves the design pointing at a datasource the
    /// clone no longer has.
    /// <para>
    /// The alternation cannot bleed into a longer tag: the group must be followed immediately by
    /// whitespace or <c>&gt;</c>, so <c>DataSource</c> never matches the start of
    /// <c>DataSourceLinks</c>. The closing tag is a backreference, so the two always agree.
    /// </para>
    /// </remarks>
    private static string RenameDataSourceReferences(string xml, string oldName, string newName)
    {
        var tags = string.Join('|', DataSourceRefElements);
        return Regex.Replace(
            xml,
            $@"<({tags})((?:\s[^>]*)?)>{Regex.Escape(oldName)}</\1>",
            m => $"<{m.Groups[1].Value}{m.Groups[2].Value}>{newName}</{m.Groups[1].Value}>");
    }

    /// <summary>
    /// The clone has to still be a parseable form carrying its new name.
    /// </summary>
    /// <remarks>
    /// The edits are regex-driven over a document this code does not own, so the cheap
    /// structural assertion is worth its cost: returning a corrupted form would be worse than
    /// refusing to clone.
    /// </remarks>
    private static void EnsureStillWellFormed(string xml, string newName)
    {
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (System.Xml.XmlException ex)
        {
            throw new FormCloneException($"The clone is not well-formed XML: {ex.Message}");
        }

        var root = doc.Root ?? throw new FormCloneException("The clone has no root element.");
        if (root.Name.LocalName != "AxForm")
            throw new FormCloneException($"Expected an <AxForm> root, got <{root.Name.LocalName}>.");

        var name = root.Elements().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value;
        if (!string.Equals(name, newName, StringComparison.Ordinal))
            throw new FormCloneException($"The clone's root <Name> is '{name}', expected '{newName}'.");
    }

    private static string? ReplaceFirst(string haystack, string pattern, string replacement)
    {
        var match = Regex.Match(haystack, pattern);
        return match.Success
            ? haystack[..match.Index] + replacement + haystack[(match.Index + match.Length)..]
            : null;
    }
}
