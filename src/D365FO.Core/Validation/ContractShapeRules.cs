using System.Xml;
using System.Xml.Linq;
using D365FO.Core.Metadata;

namespace D365FO.Core.Validation;

/// <summary>
/// XML007 — a member the AOT type does not declare, which the reader silently discards — and
/// XML008, an enum value the AOT does not define, which stops the read outright.
/// </summary>
/// <remarks>
/// <para>
/// The AOT's on-disk format is a DataContract, and <c>DataContractSerializer</c> ignores
/// elements the type does not know: the file parses, every other validator passes, and the
/// value is simply gone. That is how <c>&lt;Image&gt;</c> on a menu item and
/// <c>&lt;Datasets&gt;</c> on a report (it is <c>DataSets</c>) went unnoticed. This is the
/// offline half of what <c>d365fo validate metadata</c> proves against the live provider —
/// same catalog, no VM needed.
/// </para>
/// <para>
/// The rule only speaks about types the catalog knows
/// (<see cref="MetadataContracts"/>, generated from <c>Microsoft.Dynamics.AX.Metadata.dll</c>),
/// so an unrecognised root or a hand-rolled fragment is left alone rather than guessed at.
/// </para>
/// <para>
/// <b>Why there is no companion order rule.</b> Member order matters — the writer
/// canonicalises it, and misordered collections really do get dropped (a table listing
/// <c>Fields</c> before <c>FieldGroups</c> loses every field group). But a lint rule that
/// flags any deviation is not justified by evidence: shipped Microsoft files deviate from the
/// contract order in places, and the provider reads them back with no loss at all. Until the
/// tolerance is understood well enough to describe, the ordering knowledge is applied where it
/// is proven useful (canonical output) rather than asserted as a defect in other people's files.
/// </para>
/// </remarks>
public static class ContractShapeRules
{
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    /// <summary>A member the type does not declare — silently dropped on read.</summary>
    public const string RuleUnknownMember = "XML007";

    /// <summary>A value outside its enum — the whole document fails to deserialize.</summary>
    public const string RuleInvalidEnumValue = "XML008";

    /// <summary>Appends any contract-shape violations found in <paramref name="xml"/>.</summary>
    public static void Check(string xml, List<XppViolation> violations)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml, LoadOptions.SetLineInfo);
        }
        catch (XmlException)
        {
            return; // Not well-formed — other rules and the parser report that.
        }

        if (doc.Root is not null)
            CheckElement(
                doc.Root,
                doc.Root.Name.LocalName,
                MetadataContracts.GoverningContract(doc.Root, parent: null),
                violations);
    }

    private static void CheckElement(XElement element, string path, MetadataContract? contract, List<XppViolation> violations)
    {
        foreach (var child in element.Elements())
        {
            var name = child.Name.LocalName;
            var childContract = MetadataContracts.GoverningContract(child, contract);

            // An element named after a type is a collection item or a nested object, not a
            // member of the enclosing one — judging <AxTableField> as a member of AxTableField
            // would flag every collection in every file.
            var isMember = MetadataContracts.Find(name) is null;

            if (contract is not null && isMember)
            {
                // Subtypes count when the named contract is abstract: the element then stands in
                // for a derived type, with no i:type to announce it.
                if (!MetadataContracts.AcceptsMember(contract, name))
                {
                    violations.Add(new XppViolation(
                        RuleUnknownMember,
                        "error",
                        (child as IXmlLineInfo)?.LineNumber,
                        $"{path}/{name}",
                        $"<{name}> is not a member of {contract.Name}. The serializer ignores unknown " +
                        $"elements, so this value is dropped on read while the file still looks correct. " +
                        $"Closest members: {Closest(contract, name)}."));
                    continue;
                }

                CheckEnumValue(contract, child, path, violations);
            }

            CheckElement(child, path + "/" + name, childContract, violations);
        }
    }

    /// <summary>
    /// Flags a leaf whose text is outside the enum its member is typed as.
    /// </summary>
    /// <remarks>
    /// Unlike an unknown member, this one is not silent: <c>DataContractSerializer</c> throws on
    /// an unrecognised enum value and abandons the document, so a single bad word makes the whole
    /// object unreadable. Both cases seen in this repo looked entirely plausible —
    /// <c>Style=TileSection</c> on a workspace group and <c>TabStyle=TOCList</c> on a
    /// table-of-contents form — which is exactly why they survived review.
    /// </remarks>
    private static void CheckEnumValue(MetadataContract contract, XElement child, string path, List<XppViolation> violations)
    {
        if (child.HasElements) return;

        var value = child.Value.Trim();
        if (value.Length == 0) return;

        var name = child.Name.LocalName;
        var enumName = MetadataContracts.EnumForMember(contract, name);
        if (enumName is null) return;

        var allowed = MetadataContracts.EnumValues(enumName);
        if (allowed.Count == 0 || allowed.Contains(value, StringComparer.Ordinal)) return;

        var suggestion = allowed.FirstOrDefault(v => string.Equals(v, value, StringComparison.OrdinalIgnoreCase))
            ?? allowed.FirstOrDefault(v => v.StartsWith(value[..Math.Min(3, value.Length)], StringComparison.OrdinalIgnoreCase));

        violations.Add(new XppViolation(
            RuleInvalidEnumValue,
            "error",
            (child as IXmlLineInfo)?.LineNumber,
            $"{path}/{name}",
            $"'{value}' is not a value of {enumName}. The serializer throws on an unknown enum " +
            $"value, so the whole object fails to load rather than losing one property. " +
            (suggestion is not null ? $"Did you mean '{suggestion}'? " : string.Empty) +
            $"Valid: {string.Join(", ", allowed.Take(12))}{(allowed.Count > 12 ? ", …" : string.Empty)}."));
    }

    /// <summary>
    /// Members whose names look like the one that was written — usually the answer is a
    /// casing or plural slip (<c>Datasets</c> → <c>DataSets</c>).
    /// </summary>
    private static string Closest(MetadataContract contract, string written)
    {
        var near = contract.Members
            .Where(m => m.StartsWith(written[..Math.Min(3, written.Length)], StringComparison.OrdinalIgnoreCase)
                        || string.Equals(m, written, StringComparison.OrdinalIgnoreCase))
            .Take(4)
            .ToList();

        return near.Count > 0
            ? string.Join(", ", near)
            : string.Join(", ", contract.Members.Take(6)) + (contract.Members.Count > 6 ? ", …" : string.Empty);
    }
}
