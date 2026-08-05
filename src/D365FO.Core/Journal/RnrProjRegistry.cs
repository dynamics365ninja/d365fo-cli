using System.Xml.Linq;

namespace D365FO.Core.Journal;

/// <summary>
/// Best-effort synchronisation of a model's <c>.rnrproj</c> (Visual Studio "Dynamics 365 Project")
/// item list with on-disk object create/delete. D365FO's build/sync/AOT tooling discovers objects
/// by directory convention (<c>Ax&lt;Kind&gt;\&lt;Name&gt;.xml</c> under the model folder) — the
/// <c>.rnrproj</c> file only drives what Visual Studio's Solution Explorer shows under the model's
/// project node. Most headless/VM installs never have a project file with an explicit item list at
/// all, so this is deliberately a no-op (returns null, no exception) whenever:
/// <list type="bullet">
///   <item><description>no <c>*.rnrproj</c> file exists next to the model folder, or</description></item>
///   <item><description>the located project file has no <c>ItemGroup</c> with typed entries in the
///   shape Visual Studio emits (<c>&lt;Compile Include="Ax&lt;Kind&gt;\&lt;Name&gt;.xml" /&gt;</c>).</description></item>
/// </list>
/// When a project file DOES enumerate items explicitly, a create adds the matching entry and a
/// delete removes it — <see cref="JournalEntry.RnrProjDelta"/> records exactly what changed so
/// <c>undo</c> can invert it via <see cref="Invert"/>.
/// </summary>
public static class RnrProjRegistry
{
    private const string ItemElementName = "Compile";

    /// <summary>
    /// Locate a <c>.rnrproj</c> for <paramref name="modelFolder"/> — checked in the folder itself
    /// and its immediate parent, non-recursive, to keep the search bounded and side-effect free.
    /// </summary>
    public static string? FindRnrProj(string modelFolder)
    {
        if (string.IsNullOrWhiteSpace(modelFolder) || !Directory.Exists(modelFolder)) return null;
        try
        {
            var here = Directory.EnumerateFiles(modelFolder, "*.rnrproj").FirstOrDefault();
            if (here is not null) return here;

            var parent = Directory.GetParent(modelFolder)?.FullName;
            if (parent is not null && Directory.Exists(parent))
                return Directory.EnumerateFiles(parent, "*.rnrproj").FirstOrDefault();
        }
        catch { /* best-effort */ }
        return null;
    }

    /// <summary>
    /// On object CREATE: if a <c>.rnrproj</c> with an explicit item list exists and does not
    /// already reference <paramref name="axSubfolder"/>\<paramref name="objectName"/>.xml, add it.
    /// Returns null (no-op) when no applicable project file was found or the entry already existed.
    /// </summary>
    public static RnrProjDelta? TryRegisterCreate(string modelFolder, string axSubfolder, string objectName)
    {
        var rnrproj = FindRnrProj(modelFolder);
        if (rnrproj is null) return null;

        var include = Path.Combine(axSubfolder, objectName + ".xml");
        try
        {
            var doc = XDocument.Load(rnrproj, LoadOptions.PreserveWhitespace);
            var ns = doc.Root?.Name.Namespace ?? XNamespace.None;
            var itemGroups = doc.Root?.Elements(ns + "ItemGroup").ToList();
            if (itemGroups is null || itemGroups.Count == 0) return null; // not an item-list-shaped project — leave alone

            if (HasInclude(itemGroups, include)) return null; // already registered

            itemGroups[0].Add(new XElement(ns + ItemElementName, new XAttribute("Include", include)));
            doc.Save(rnrproj);
            return new RnrProjDelta(rnrproj, ItemElementName, include, WasAdded: true);
        }
        catch
        {
            return null; // best-effort — never fail the underlying object write over this
        }
    }

    /// <summary>
    /// On object DELETE: if a <c>.rnrproj</c> with an explicit item list references
    /// <paramref name="axSubfolder"/>\<paramref name="objectName"/>.xml, remove it.
    /// Returns null (no-op) when no applicable project file was found or no entry existed.
    /// </summary>
    public static RnrProjDelta? TryRegisterDelete(string modelFolder, string axSubfolder, string objectName)
    {
        var rnrproj = FindRnrProj(modelFolder);
        if (rnrproj is null) return null;

        var include = Path.Combine(axSubfolder, objectName + ".xml");
        try
        {
            var doc = XDocument.Load(rnrproj, LoadOptions.PreserveWhitespace);
            var ns = doc.Root?.Name.Namespace ?? XNamespace.None;
            var itemGroups = doc.Root?.Elements(ns + "ItemGroup").ToList();
            if (itemGroups is null || itemGroups.Count == 0) return null;

            var match = FindInclude(itemGroups, include);
            if (match is null) return null; // nothing to remove

            match.Remove();
            doc.Save(rnrproj);
            return new RnrProjDelta(rnrproj, ItemElementName, include, WasAdded: false);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Invert a previously-captured delta: undo of a create removes the entry it added; undo of a
    /// delete re-adds the entry it removed. Best-effort — swallows all failures (surfaced by the
    /// caller as a warning, never as an undo failure, since the object file itself is authoritative).
    /// </summary>
    public static bool Invert(RnrProjDelta delta)
    {
        try
        {
            if (!File.Exists(delta.RnrProjPath)) return false;
            var doc = XDocument.Load(delta.RnrProjPath, LoadOptions.PreserveWhitespace);
            var ns = doc.Root?.Name.Namespace ?? XNamespace.None;
            var itemGroups = doc.Root?.Elements(ns + "ItemGroup").ToList();
            if (itemGroups is null || itemGroups.Count == 0) return false;

            if (delta.WasAdded)
            {
                // Original write ADDED the entry -> undo REMOVES it.
                var match = FindInclude(itemGroups, delta.Include);
                if (match is null) return false;
                match.Remove();
            }
            else
            {
                // Original write REMOVED the entry -> undo RE-ADDS it.
                if (HasInclude(itemGroups, delta.Include)) return true; // already present
                itemGroups[0].Add(new XElement(ns + delta.ItemElementName, new XAttribute("Include", delta.Include)));
            }

            doc.Save(delta.RnrProjPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasInclude(IEnumerable<XElement> itemGroups, string include)
        => FindInclude(itemGroups, include) is not null;

    private static XElement? FindInclude(IEnumerable<XElement> itemGroups, string include)
        => itemGroups.SelectMany(g => g.Elements())
            .FirstOrDefault(e => string.Equals(
                (string?)e.Attribute("Include"), include, StringComparison.OrdinalIgnoreCase));
}
