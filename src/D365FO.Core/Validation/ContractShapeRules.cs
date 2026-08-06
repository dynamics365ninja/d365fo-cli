using System.Xml;
using System.Xml.Linq;
using D365FO.Core.Metadata;

namespace D365FO.Core.Validation;

/// <summary>
/// XML007 — a member the AOT type does not declare, which the reader silently discards.
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

        if (doc.Root is not null) CheckElement(doc.Root, doc.Root.Name.LocalName, violations);
    }

    private static void CheckElement(XElement element, string path, List<XppViolation> violations)
    {
        var contract = MetadataContracts.ForElement(
            element.Name.LocalName,
            element.Attribute(Xsi + "type")?.Value);

        if (contract is not null)
        {
            foreach (var child in element.Elements())
            {
                var name = child.Name.LocalName;
                // Subtypes count: an element named after a base type routinely carries a
                // derived type's members, with no i:type to announce it.
                if (MetadataContracts.AcceptsMember(contract, name)) continue;

                violations.Add(new XppViolation(
                    RuleUnknownMember,
                    "error",
                    (child as IXmlLineInfo)?.LineNumber,
                    $"{path}/{name}",
                    $"<{name}> is not a member of {contract.Name}. The serializer ignores unknown " +
                    $"elements, so this value is dropped on read while the file still looks correct. " +
                    $"Closest members: {Closest(contract, name)}."));
            }
        }

        foreach (var child in element.Elements())
            CheckElement(child, path + "/" + child.Name.LocalName, violations);
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
