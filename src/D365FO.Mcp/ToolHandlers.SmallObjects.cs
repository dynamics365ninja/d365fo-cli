using System.Text;
using System.Xml.Linq;
using D365FO.Core;
using D365FO.Core.Labels;
using D365FO.Core.Scaffolding;

namespace D365FO.Mcp;

/// <summary>
/// The nine object types the coverage report was waiting on, over MCP. XML-only, like the rest
/// of <see cref="ToolHandlers"/>'s scaffold surface: each returns the document by name, and the
/// two that also produce a content file (<c>label-file</c>, <c>resource</c>) say so in the
/// payload rather than pretending the manifest is the whole artefact.
/// </summary>
public sealed partial class ToolHandlers
{
    public ToolResult<object> GenerateConfigurationKey(
        string name, string? label, string? parentKey, string? licenseCode, string? description, bool disabledByDefault)
    {
        if (string.IsNullOrWhiteSpace(name)) return Required("name", "objectType=configuration-key");
        try
        {
            var doc = ConfigurationScaffolder.ConfigurationKey(name, label, parentKey, licenseCode, description, !disabledByDefault);
            return ToolResult<object>.Success(new { name, kind = "AxConfigurationKey", parentKey, xml = Aot(doc) });
        }
        catch (ArgumentException ex) { return ToolResult<object>.Fail(D365FoErrorCodes.BadInput, ex.Message); }
    }

    public ToolResult<object> GenerateFormPart(string name, string? form, string? caption)
    {
        if (string.IsNullOrWhiteSpace(name)) return Required("name", "objectType=form-part");
        if (string.IsNullOrWhiteSpace(form)) return Required("form", "objectType=form-part");
        if (string.IsNullOrWhiteSpace(caption)) return Required("caption", "objectType=form-part");
        try
        {
            var doc = NavigationScaffolder.FormPart(name, form!, caption!);
            return ToolResult<object>.Success(new { name, kind = "AxFormPart", form, xml = Aot(doc) });
        }
        catch (ArgumentException ex) { return ToolResult<object>.Fail(D365FoErrorCodes.BadInput, ex.Message); }
    }

    public ToolResult<object> GenerateLabelFile(string labelFileId, string? language, string? model, string[]? entries)
    {
        var id = (labelFileId ?? "").Trim();
        if (id.Length == 0) return Required("name", "objectType=label-file (the label file id)");
        if (!LabelFileWriter.IsReferenceableLabelFileId(id))
            return ToolResult<object>.Fail("LABEL_FILE_UNREFERENCEABLE",
                $"'{id}' cannot be named by an @File:Id token, so no label in it could ever be referenced.",
                "A label file id is letters, digits and underscore, starting with a letter.");
        if (string.IsNullOrWhiteSpace(model)) return Required("model", "objectType=label-file (RelativeUriInModelStore needs the owning model)");

        var lang = string.IsNullOrWhiteSpace(language) ? "en-US" : language!.Trim();
        var content = new StringBuilder();
        foreach (var raw in entries ?? [])
        {
            var kv = raw.Split('=', 2, StringSplitOptions.TrimEntries);
            if (kv.Length != 2 || kv[0].Length == 0)
                return ToolResult<object>.Fail(D365FoErrorCodes.BadInput, $"Invalid entry '{raw}'. Expected <Key>=<Text>.");
            content.Append(kv[0]).Append('=').Append(kv[1]).Append("\r\n");
        }

        try
        {
            var doc = ConfigurationScaffolder.LabelFile(id, lang, model!, model!);
            return ToolResult<object>.Success(new
            {
                name = ConfigurationScaffolder.LabelFileObjectName(id, lang),
                kind = "AxLabelFile",
                labelFileId = id,
                language = lang,
                xml = Aot(doc),
                contentFile = $"AxLabelFile/LabelResources/{lang}/{ConfigurationScaffolder.LabelContentFileName(id, lang)}",
                content = content.ToString(),
                note = "The manifest is one file of two: write `content` to `contentFile` beside it, or the label file is empty.",
            });
        }
        catch (ArgumentException ex) { return ToolResult<object>.Fail(D365FoErrorCodes.BadInput, ex.Message); }
    }

    public ToolResult<object> GenerateMenu(
        string name, string? label, string[]? submenus, string[]? items, string[]? tiles, string[]? menuRefs,
        bool inContentArea, bool setCompany, string? configurationKey)
    {
        if (string.IsNullOrWhiteSpace(name)) return Required("name", "objectType=menu");

        if (!MenuSpecs.TryParse(submenus, items, tiles, menuRefs, out var subs, out var entries, out var specError))
            return ToolResult<object>.Fail(D365FoErrorCodes.BadInput, specError!);
        if (entries.Count == 0 && subs.Count == 0)
            return ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "A menu with nothing on it: pass items, tiles, menuRefs or submenus.");

        try
        {
            var doc = NavigationScaffolder.Menu(name, label, subs, entries, inContentArea, setCompany, configurationKey);
            var unknown = MenuSpecs.MenuItemsOf(entries).Where(m => !SafeMenuItemExists(m)).ToList();
            return ToolResult<object>.Success(new
            {
                name, kind = "AxMenu",
                submenus = subs.Count,
                menuItems = entries.Count(e => e.Kind == MenuEntryKind.MenuItem),
                unknownMenuItems = unknown.Count > 0 ? unknown : null,
                xml = Aot(doc),
            });
        }
        catch (ArgumentException ex) { return ToolResult<object>.Fail(D365FoErrorCodes.BadInput, ex.Message); }
    }

    public ToolResult<object> GenerateResource(string name, string? fileName, string? model, string? resourceType, string? label)
    {
        if (string.IsNullOrWhiteSpace(name)) return Required("name", "objectType=resource");
        if (string.IsNullOrWhiteSpace(fileName)) return Required("fileName", "objectType=resource");
        if (string.IsNullOrWhiteSpace(model)) return Required("model", "objectType=resource (RelativeUriInModelStore needs the owning model)");
        try
        {
            var doc = ConfigurationScaffolder.Resource(name, fileName!, model!, resourceType, label);
            var folder = ConfigurationScaffolder.ResourceContentFolder(resourceType ?? "Images");
            return ToolResult<object>.Success(new
            {
                name, kind = "AxResource", fileName, xml = Aot(doc),
                contentFile = $"AxResource/ResourceContent/{folder}/{fileName}",
                note = "The manifest alone does not compile: the compiler reports \"Resource content file not found\" as a Metadata Error. Place the file at `contentFile`.",
            });
        }
        catch (ArgumentException ex) { return ToolResult<object>.Fail(D365FoErrorCodes.BadInput, ex.Message); }
    }

    public ToolResult<object> GenerateTile(
        string name, string? menuItem, string? tileType, string? label, string? size, string? image, string? display,
        string? query, string? kpi, string? configurationKey, string? menuItemType, string? helpText, string? url)
    {
        if (string.IsNullOrWhiteSpace(name)) return Required("name", "objectType=tile");
        try
        {
            // menuItem is required for a Standard or Count tile and meaningless for the other
            // two; the scaffolder holds that rule so both surfaces enforce the same one.
            var doc = NavigationScaffolder.Tile(name, menuItem, tileType, label, size, image, display, query, kpi,
                configurationKey, menuItemType, helpText, url);
            return ToolResult<object>.Success(new
            {
                name, kind = "AxTile", menuItem, url, type = tileType ?? "Standard",
                unknownMenuItems = string.IsNullOrWhiteSpace(menuItem) || SafeMenuItemExists(menuItem!)
                    ? null : new[] { menuItem },
                xml = Aot(doc),
            });
        }
        catch (ArgumentException ex) { return ToolResult<object>.Fail(D365FoErrorCodes.BadInput, ex.Message); }
    }

    public ToolResult<object> GenerateWorkflowCategory(string name, string? module, string? label, string? helpText)
    {
        if (string.IsNullOrWhiteSpace(name)) return Required("name", "objectType=workflow-category");
        if (string.IsNullOrWhiteSpace(module)) return Required("module", "objectType=workflow-category (a ModuleAxapta value)");

        var value = module!.Trim();
        string? note = null;
        try
        {
            var en = _repo.GetEnum("ModuleAxapta");
            if (en is not null && en.Values.Count > 0)
            {
                var hit = en.Values.FirstOrDefault(v => string.Equals(v.Name, value, StringComparison.OrdinalIgnoreCase));
                if (hit is null)
                    return ToolResult<object>.Fail(D365FoErrorCodes.BadInput, $"'{value}' is not a value of ModuleAxapta.",
                        "get_object_info(objectType=enum, name=ModuleAxapta) lists the legal values.");
                value = hit.Name;
            }
            else note = "ModuleAxapta is not in the index, so module was not validated.";
        }
        catch { note = "No index available, so module was not validated against ModuleAxapta."; }

        try
        {
            var doc = ConfigurationScaffolder.WorkflowCategory(name, value, label, helpText);
            return ToolResult<object>.Success(new { name, kind = "AxWorkflowCategory", module = value, note, xml = Aot(doc) });
        }
        catch (ArgumentException ex) { return ToolResult<object>.Fail(D365FoErrorCodes.BadInput, ex.Message); }
    }

    public ToolResult<object> GenerateCompositeEntity(
        string name, string[]? roots, string[]? embedded, string? label, string? tags, string? modules, string? entityCategory)
    {
        if (string.IsNullOrWhiteSpace(name)) return Required("name", "objectType=composite-entity");
        if (roots is null || roots.Length == 0) return Required("roots", "objectType=composite-entity (the root data entities)");

        var nodes = new Dictionary<string, (string Entity, string? Relation)>(StringComparer.OrdinalIgnoreCase);
        var rootOrder = new List<string>();
        foreach (var raw in roots)
        {
            var parts = raw.Split(':', 2, StringSplitOptions.TrimEntries);
            if (string.IsNullOrEmpty(parts[0]))
                return ToolResult<object>.Fail(D365FoErrorCodes.BadInput, $"Invalid root '{raw}'. Expected <dataEntity>[:<referenceName>].");
            var refName = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : parts[0];
            if (nodes.ContainsKey(refName))
                return ToolResult<object>.Fail(D365FoErrorCodes.BadInput, $"Reference name '{refName}' is used twice.");
            nodes[refName] = (parts[0], null);
            rootOrder.Add(refName);
        }
        var children = new List<(string RefName, string Entity, string Relation, string Parent)>();
        foreach (var raw in embedded ?? [])
        {
            var parts = raw.Split(':', 4, StringSplitOptions.TrimEntries);
            if (parts.Length < 2 || string.IsNullOrEmpty(parts[0]) || string.IsNullOrEmpty(parts[1]))
                return ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                    $"Invalid embedded '{raw}'. Expected <dataEntity>:<relation>[:<parentReference>[:<referenceName>]].");
            var parent = parts.Length > 2 && parts[2].Length > 0 ? parts[2] : rootOrder[0];
            var refName = parts.Length > 3 && parts[3].Length > 0 ? parts[3] : parts[0];
            if (nodes.ContainsKey(refName))
                return ToolResult<object>.Fail(D365FoErrorCodes.BadInput, $"Reference name '{refName}' is used twice.");
            nodes[refName] = (parts[0], parts[1]);
            children.Add((refName, parts[0], parts[1], parent));
        }
        foreach (var c in children)
            if (!nodes.ContainsKey(c.Parent))
                return ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                    $"embedded '{c.Entity}' names parent '{c.Parent}', which is neither a root nor an embedded reference.");

        // Resolving is not arriving: a parent cycle is dropped by Build() and reported as
        // bundled all the same.
        var unrooted = EntityShapeScaffolder.UnrootedEmbedded(children.Select(c => (c.RefName, c.Parent)));
        if (unrooted.Count > 0)
            return ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                $"embedded {string.Join(", ", unrooted.Select(u => $"'{u}'"))} never reaches a root: the parent chain loops.",
                "Every embedded entity hangs, directly or through other embedded entities, off a root.");

        CompositeEntityReferenceSpec Build(string refName) => new(
            refName, nodes[refName].Entity, nodes[refName].Relation,
            children.Where(c => string.Equals(c.Parent, refName, StringComparison.OrdinalIgnoreCase)).Select(c => Build(c.RefName)).ToList());

        try
        {
            var doc = EntityShapeScaffolder.CompositeDataEntityView(name, rootOrder.Select(Build), label, tags, modules, entityCategory);
            return ToolResult<object>.Success(new
            {
                name, kind = "AxCompositeDataEntityView",
                roots = rootOrder.Select(r => nodes[r].Entity),
                embedded = children.Select(c => new { entity = c.Entity, relation = c.Relation, under = c.Parent }),
                xml = Aot(doc),
            });
        }
        catch (ArgumentException ex) { return ToolResult<object>.Fail(D365FoErrorCodes.BadInput, ex.Message); }
    }

    public ToolResult<object> GenerateAggregateEntity(string name, string? measurement, string[]? measures, string[]? dimensions, string? label)
    {
        if (string.IsNullOrWhiteSpace(name)) return Required("name", "objectType=aggregate-entity");
        if (string.IsNullOrWhiteSpace(measurement)) return Required("measurement", "objectType=aggregate-entity");

        var fields = new List<AggregateEntityFieldSpec>();
        foreach (var raw in measures ?? [])
        {
            var p = raw.Split(':', StringSplitOptions.TrimEntries);
            if (p.Length != 4 || p.Any(string.IsNullOrEmpty))
                return ToolResult<object>.Fail(D365FoErrorCodes.BadInput, $"Invalid measure '{raw}'. Expected <field>:<measureGroup>:<measure>:<edt>.");
            fields.Add(new AggregateEntityFieldSpec(p[0], p[1], p[3], Measure: p[2]));
        }
        foreach (var raw in dimensions ?? [])
        {
            var p = raw.Split(':', StringSplitOptions.TrimEntries);
            if (p.Length != 5 || p.Any(string.IsNullOrEmpty))
                return ToolResult<object>.Fail(D365FoErrorCodes.BadInput, $"Invalid dimension '{raw}'. Expected <field>:<measureGroup>:<dimension>:<attribute>:<edt>.");
            fields.Add(new AggregateEntityFieldSpec(p[0], p[1], p[4], Dimension: p[2], Attribute: p[3]));
        }
        if (fields.Count == 0) return Required("measures or dimensions", "objectType=aggregate-entity");

        try
        {
            var doc = EntityShapeScaffolder.AggregateDataEntity(name, measurement!, fields, label);
            return ToolResult<object>.Success(new
            {
                name, kind = "AxAggregateDataEntity", measurement,
                measures = fields.Count(f => f.IsMeasure), dimensions = fields.Count(f => !f.IsMeasure),
                note = "The measurement, its groups, measures and attributes are not in the index and were not verified; the build proves them.",
                xml = Aot(doc),
            });
        }
        catch (ArgumentException ex) { return ToolResult<object>.Fail(D365FoErrorCodes.BadInput, ex.Message); }
    }

    private bool SafeMenuItemExists(string menuItem)
    {
        try { return _repo.MenuItemExists(menuItem); }
        catch { return true; } // an index that cannot answer cannot veto
    }

}
