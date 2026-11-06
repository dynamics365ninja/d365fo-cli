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

        var include = axSubfolder + "\\" + objectName + ".xml";
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

        var include = axSubfolder + "\\" + objectName + ".xml";
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
            // Only ever a project file: a delta read back from a hand-edited journal must not
            // let undo edit some other file.
            if (!IsProjectFile(delta.RnrProjPath)) return false;
            if (delta.WasAdded && TryRemoveItemText(delta)) return true;

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

    /// <summary>Outcome of <see cref="RegisterInProject"/>.</summary>
    /// <param name="ProjectPath">The project file that was checked.</param>
    /// <param name="Include">The item the object is listed under (<c>AxClass\Name</c>).</param>
    /// <param name="Added">True when this call added the entry, false when it was already there.</param>
    /// <param name="Delta">What changed, for the journal; null when nothing did.</param>
    public sealed record ProjectRegistration(string ProjectPath, string Include, bool Added, RnrProjDelta? Delta);

    /// <summary>
    /// Register an object in a project the caller named (<c>generate --project</c>), in the
    /// item shape Visual Studio writes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="TryRegisterCreate"/> only looks next to the model folder, which is where a
    /// project sits in a classic <c>PackagesLocalDirectory</c> layout. A repository that keeps
    /// projects apart from metadata (<c>Projects\&lt;Solution&gt;\&lt;Project&gt;.rnrproj</c>
    /// beside <c>Metadata\&lt;Model&gt;\&lt;Model&gt;</c>) never matches that, so a generated
    /// object stayed out of the solution until someone added it by hand.
    /// </para>
    /// <para>
    /// Visual Studio lists a D365FO object as
    /// <c>&lt;Content Include="AxClass\Name"&gt;</c> with <c>SubType</c>, <c>Name</c> and a
    /// <c>Link</c> that places it under a display folder. A project that already uses the
    /// <c>&lt;Compile Include="AxClass\Name.xml"/&gt;</c> shape keeps it. Unlike the
    /// best-effort helpers above, this throws: the caller asked for the registration by name,
    /// so a failure is reported rather than swallowed.
    /// </para>
    /// </remarks>
    /// <param name="rnrprojPath">The <c>.rnrproj</c> to register the object in.</param>
    /// <param name="axSubfolder">The object's AOT folder (<c>AxClass</c>).</param>
    /// <param name="objectName">The object's name.</param>
    /// <param name="model">The model the object was written to, when known. Refused when the project builds a different one.</param>
    public static ProjectRegistration RegisterInProject(string rnrprojPath, string axSubfolder, string objectName, string? model = null)
    {
        if (!IsProjectFile(rnrprojPath))
            throw new ArgumentException($"'{rnrprojPath}' is not an existing .rnrproj file.", nameof(rnrprojPath));

        var doc = XDocument.Load(rnrprojPath, LoadOptions.PreserveWhitespace);
        var root = doc.Root ?? throw new InvalidDataException($"{rnrprojPath} has no root element.");
        var ns = root.Name.Namespace;

        var projectModel = root.Descendants(ns + "Model").FirstOrDefault()?.Value.Trim();
        if (!string.IsNullOrEmpty(model) && !string.IsNullOrEmpty(projectModel)
            && !string.Equals(model, projectModel, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{Path.GetFileName(rnrprojPath)} builds model '{projectModel}', but '{objectName}' was written to model '{model}'.");
        }

        var itemGroups = root.Elements(ns + "ItemGroup").ToList();
        var compileShape = itemGroups.SelectMany(g => g.Elements(ns + ItemElementName))
            .Any(e => ((string?)e.Attribute("Include"))?.StartsWith("Ax", StringComparison.OrdinalIgnoreCase) == true);
        var elementName = compileShape ? ItemElementName : ContentElementName;
        var include = axSubfolder + "\\" + objectName + (compileShape ? ".xml" : string.Empty);

        if (HasInclude(itemGroups, include))
            return new ProjectRegistration(rnrprojPath, include, Added: false, Delta: null);

        // The group that already lists objects, so the new entry lands beside its siblings rather
        // than among the <Folder> entries; a project with none gets a group of its own.
        var allGroups = root.Descendants(ns + "ItemGroup").ToList();
        var group = itemGroups.FirstOrDefault(g => g.Elements().Any(e =>
                        (e.Name == ns + ContentElementName || e.Name == ns + ItemElementName)
                        && ((string?)e.Attribute("Include"))?.StartsWith("Ax", StringComparison.OrdinalIgnoreCase) == true));

        var bytes = File.ReadAllBytes(rnrprojPath);
        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var text = new System.Text.UTF8Encoding(false).GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0));
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

        // Escaped: the item is written as text, so a name with a markup character must not be
        // able to break (or inject into) the project XML.
        var xInclude = System.Security.SecurityElement.Escape(include);
        var xName = System.Security.SecurityElement.Escape(objectName);
        var item = new System.Text.StringBuilder();
        if (compileShape)
        {
            item.Append($"    <{elementName} Include=\"{xInclude}\" />{newline}");
        }
        else
        {
            item.Append($"    <{elementName} Include=\"{xInclude}\">{newline}");
            item.Append($"      <SubType>Content</SubType>{newline}");
            item.Append($"      <Name>{xName}</Name>{newline}");
            if (DisplayFolders.TryGetValue(axSubfolder, out var display))
                item.Append($"      <Link>{display}\\{xName}</Link>{newline}");
            item.Append($"    </{elementName}>{newline}");
        }

        // Inserted as text, not by re-serialising the document: XDocument rewrites every
        // self-closing tag ("<Folder Include=\"Classes\\\"/>" gains a space), so a one-item
        // registration would otherwise show up in git as a change to every folder line.
        var groupSpans = System.Text.RegularExpressions.Regex.Matches(
            text, @"<ItemGroup\b[^>]*?(/>|>.*?</ItemGroup>)", System.Text.RegularExpressions.RegexOptions.Singleline);
        var spansMatch = groupSpans.Count == allGroups.Count;

        // Where to add lines to an existing group: the start of its closing tag's line. A group
        // written on one line ("<ItemGroup><Content …/></ItemGroup>") has that line start outside
        // the group, so the lines go right before the tag instead, after a line break.
        (int At, string Prefix)? BeforeClose(XElement? g)
        {
            if (g is null || !spansMatch) return null;
            var span = groupSpans[allGroups.IndexOf(g)];
            if (!span.Value.EndsWith("</ItemGroup>", StringComparison.Ordinal)) return null;
            var close = span.Index + span.Length - "</ItemGroup>".Length;
            var lineStart = text.LastIndexOf('\n', close - 1) + 1;
            return lineStart <= span.Index ? (close, newline) : (lineStart, string.Empty);
        }

        int at;
        string insert;
        if (BeforeClose(group) is { } inGroup)
        {
            at = inGroup.At;
            insert = inGroup.Prefix + item;
        }
        else
        {
            var anchor = System.Text.RegularExpressions.Regex.Match(text, @"^[ \t]*<Import\b", System.Text.RegularExpressions.RegexOptions.Multiline);
            if (!anchor.Success)
                anchor = System.Text.RegularExpressions.Regex.Match(text, @"^[ \t]*</Project>", System.Text.RegularExpressions.RegexOptions.Multiline);
            if (!anchor.Success)
                throw new InvalidDataException($"{Path.GetFileName(rnrprojPath)} has no </Project> line to add an item group before.");
            at = anchor.Index;
            insert = $"  <ItemGroup>{newline}{item}  </ItemGroup>{newline}";
        }

        // Visual Studio will not load a project whose <Link> points into a folder that has no
        // <Folder Include="X\"/> entry, so add every one missing — the new item's and any an
        // earlier registration (before this fix) left out. Undo removes only the item; an empty
        // folder entry left behind is harmless.
        var missingFolders = MissingFolders(root, ns,
            !compileShape && DisplayFolders.TryGetValue(axSubfolder, out var newDisplay) ? newDisplay : null);
        if (missingFolders.Count > 0)
        {
            var folderLines = string.Concat(missingFolders.Select(f =>
                $"    <Folder Include=\"{System.Security.SecurityElement.Escape(f)}\\\" />{newline}"));
            var folderGroup = itemGroups.FirstOrDefault(g => g.Elements(ns + "Folder").Any());
            int folderAt;
            string folderInsert;
            if (BeforeClose(folderGroup) is { } inFolders)
            {
                folderAt = inFolders.At;
                folderInsert = inFolders.Prefix + folderLines;
            }
            else
            {
                // A group of its own, before the one listing the objects, as Visual Studio writes it.
                var groupStart = group is not null && spansMatch ? groupSpans[allGroups.IndexOf(group)].Index : -1;
                folderAt = groupStart >= 0 ? text.LastIndexOf('\n', Math.Max(groupStart - 1, 0)) + 1 : at;
                folderInsert = $"  <ItemGroup>{newline}{folderLines}  </ItemGroup>{newline}";
            }

            // Later position first, so the earlier one stays valid; at the same spot, folders go first.
            if (folderAt > at) text = text.Insert(folderAt, folderInsert).Insert(at, insert);
            else if (folderAt < at) text = text.Insert(at, insert).Insert(folderAt, folderInsert);
            else text = text.Insert(at, folderInsert + insert);
        }
        else
        {
            text = text.Insert(at, insert);
        }
        File.WriteAllBytes(rnrprojPath, (hasBom ? new byte[] { 0xEF, 0xBB, 0xBF } : [])
            .Concat(new System.Text.UTF8Encoding(false).GetBytes(text)).ToArray());

        return new ProjectRegistration(rnrprojPath, include, Added: true,
            Delta: new RnrProjDelta(rnrprojPath, elementName, include, WasAdded: true));
    }

    /// <summary>
    /// Remove an item <see cref="RegisterInProject"/> added, as text, so undo leaves the rest of
    /// the file byte-for-byte as it was. False when the item is not found in the shape written.
    /// </summary>
    private static bool TryRemoveItemText(RnrProjDelta delta)
    {
        var bytes = File.ReadAllBytes(delta.RnrProjPath);
        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var text = new System.Text.UTF8Encoding(false).GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0));
        var el = System.Text.RegularExpressions.Regex.Escape(delta.ItemElementName);
        var include = System.Text.RegularExpressions.Regex.Escape(System.Security.SecurityElement.Escape(delta.Include));
        var match = System.Text.RegularExpressions.Regex.Match(text,
            $@"^[ \t]*<{el} Include=""{include}""(\s*/>|>.*?</{el}>)[ \t]*\r?\n",
            System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.Singleline);
        if (!match.Success) return false;

        text = text.Remove(match.Index, match.Length);
        File.WriteAllBytes(delta.RnrProjPath, (hasBom ? new byte[] { 0xEF, 0xBB, 0xBF } : [])
            .Concat(new System.Text.UTF8Encoding(false).GetBytes(text)).ToArray());
        return true;
    }

    private const string ContentElementName = "Content";

    /// <summary>
    /// True when <paramref name="path"/> is an existing file with the <c>.rnrproj</c> extension
    /// (any case): the only kind of file project registration and its undo ever edit.
    /// </summary>
    public static bool IsProjectFile(string? path)
        => !string.IsNullOrWhiteSpace(path)
            && path.EndsWith(".rnrproj", StringComparison.OrdinalIgnoreCase)
            && File.Exists(path);

    /// <summary>
    /// The display folder Visual Studio files a new object under, per AOT folder. A kind that is
    /// not listed gets no <c>Link</c>, and Visual Studio shows it at the project root.
    /// </summary>
    private static readonly Dictionary<string, string> DisplayFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AxClass"] = "Classes",
        ["AxTable"] = "Tables",
        ["AxTableExtension"] = "Table Extensions",
        ["AxForm"] = "Forms",
        ["AxFormExtension"] = "Form Extensions",
        ["AxEdt"] = "Extended Data Types",
        ["AxEnum"] = "Base Enums",
        ["AxEnumExtension"] = "Base Enum Extensions",
        ["AxQuery"] = "Queries",
        ["AxView"] = "Views",
        ["AxMap"] = "Maps",
        ["AxDataEntityView"] = "Data Entities",
        ["AxDataEntityViewExtension"] = "Data Entity Extensions",
        ["AxMenu"] = "Menus",
        ["AxMenuExtension"] = "Menu Extensions",
        ["AxMenuItemDisplay"] = "Display Menu Items",
        ["AxMenuItemAction"] = "Action Menu Items",
        ["AxMenuItemOutput"] = "Output Menu Items",
        ["AxReport"] = "Reports",
        ["AxService"] = "Services",
        ["AxServiceGroup"] = "Service Groups",
        ["AxSecurityRole"] = "Security Roles",
        ["AxSecurityDuty"] = "Security Duties",
        ["AxSecurityPrivilege"] = "Security Privileges",
        ["AxSecurityPolicy"] = "Security Policies",
        ["AxConfigurationKey"] = "Configuration Keys",
        ["AxLabelFile"] = "Label Files",
        ["AxTile"] = "Tiles",
        ["AxResource"] = "Resources",
        ["AxWorkflowCategory"] = "Workflow Categories",
        ["AxWorkflowTemplate"] = "Workflow Types",
        ["AxWorkflowApproval"] = "Workflow Approvals",
        ["AxWorkflowTask"] = "Workflow Tasks",
    };

    /// <summary>
    /// Display folders a <c>Link</c> in <paramref name="root"/> (plus <paramref name="extra"/>)
    /// files an item under, parents included, that have no <c>&lt;Folder&gt;</c> entry yet.
    /// </summary>
    private static List<string> MissingFolders(XElement root, XNamespace ns, string? extra)
    {
        var existing = new HashSet<string>(
            root.Descendants(ns + "Folder").Select(f => ((string?)f.Attribute("Include") ?? "").Replace('/', '\\').TrimEnd('\\')),
            StringComparer.OrdinalIgnoreCase);
        var wanted = root.Descendants(ns + "Link")
            .Select(l => l.Value.Trim().Replace('/', '\\'))
            .Select(l => l.Contains('\\') ? l[..l.LastIndexOf('\\')] : null)
            .Append(extra);
        var missing = new List<string>();
        foreach (var folder in wanted)
        {
            // "A\B" needs "A" listed as well.
            for (var f = folder; !string.IsNullOrEmpty(f); f = f.Contains('\\') ? f[..f.LastIndexOf('\\')] : null)
                if (existing.Add(f)) missing.Add(f);
        }
        return missing.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool HasInclude(IEnumerable<XElement> itemGroups, string include)
        => FindInclude(itemGroups, include) is not null;

    // .rnrproj files are always Windows/Visual-Studio-authored (backslash-separated Include
    // paths) regardless of which OS this CLI runs on — normalize both sides so a lookup for
    // "AxTable\Foo.xml" matches an on-disk entry that (rarely) uses "AxTable/Foo.xml" or vice versa.
    // The ".xml" suffix is dropped too: Visual Studio lists an object as "AxTable\Foo", and that
    // entry is the same object as a "AxTable\Foo.xml" one.
    private static XElement? FindInclude(IEnumerable<XElement> itemGroups, string include)
        => itemGroups.SelectMany(g => g.Elements())
            .FirstOrDefault(e => string.Equals(
                Normalize((string?)e.Attribute("Include")), Normalize(include), StringComparison.OrdinalIgnoreCase));

    private static string? Normalize(string? path)
    {
        var p = path?.Replace('/', '\\');
        return p is not null && p.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ? p[..^4] : p;
    }
}
