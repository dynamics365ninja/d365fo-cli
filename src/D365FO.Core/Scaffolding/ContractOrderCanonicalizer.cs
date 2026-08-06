using System.Xml.Linq;
using D365FO.Core.Metadata;

namespace D365FO.Core.Scaffolding;

/// <summary>
/// Reorders a scaffolded document's elements into the order the AOT serializer expects.
/// </summary>
/// <remarks>
/// <para>
/// <c>DataContractSerializer</c> — which is what reads AOT files — matches child elements in
/// contract order and skips anything that arrives out of turn. The element is not rejected,
/// it is <em>dropped</em>: the file parses, every offline validator passes, and the property
/// is silently missing. A generated query that listed <c>JoinMode</c> before <c>DataSources</c>
/// lost its join mode entirely, turning an inner join into a cross join with nothing to show
/// for it (audit finding R2).
/// </para>
/// <para>
/// Which contract governs an element is a question in itself: an element may be named after a
/// type, after a member holding a type, or after an abstract base standing in for a subtype.
/// All three are resolved by <see cref="MetadataContracts.GoverningContract"/>. Anything
/// unresolvable is left exactly as authored, as is the relative order of items inside a
/// collection, which is data rather than contract.
/// </para>
/// </remarks>
public static class ContractOrderCanonicalizer
{
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    /// <summary>Reorders <paramref name="doc"/> in place. Safe on documents with no known types.</summary>
    public static void Apply(XDocument doc)
    {
        if (doc?.Root is not null) Apply(doc.Root);
    }

    /// <summary>Reorders <paramref name="element"/> and its descendants in place.</summary>
    public static void Apply(XElement element)
        => Apply(element, MetadataContracts.GoverningContract(element, parent: null));

    private static void Apply(XElement element, MetadataContract? contract)
    {
        var children = element.Elements().ToList();

        foreach (var child in children)
            Apply(child, MetadataContracts.GoverningContract(child, contract));

        if (contract is null || children.Count < 2) return;

        // Members the contract does not know keep their position relative to the member they
        // follow, so a document with an unknown element is not scrambled on top of being
        // wrong — XML007 reports it separately.
        var ranked = new List<(int Rank, int Original, XElement Element)>(children.Count);
        var lastKnown = -1;
        for (var i = 0; i < children.Count; i++)
        {
            var index = contract.IndexOf(children[i].Name.LocalName);
            if (index >= 0) lastKnown = index;
            ranked.Add((index >= 0 ? index : lastKnown, i, children[i]));
        }

        var sorted = ranked
            .OrderBy(x => x.Rank)
            .ThenBy(x => x.Original)
            .Select(x => x.Element)
            .ToList();

        if (sorted.SequenceEqual(children)) return;

        foreach (var child in children) child.Remove();
        element.Add(sorted);
    }
}
