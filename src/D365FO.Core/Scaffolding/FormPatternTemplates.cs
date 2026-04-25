using System.Text;

namespace D365FO.Core.Scaffolding;

/// <summary>
/// Options shared by every <see cref="FormPatternTemplates"/> builder.
/// Property names match the upstream MCP <c>FormTemplateOptions</c> contract
/// so the same call-sites can be ported with minimal noise.
/// </summary>
public sealed record FormTemplateOptions
{
    /// <summary>Form name (also used for <c>classDeclaration</c>).</summary>
    public required string FormName { get; init; }

    /// <summary>Primary datasource name. Defaults to <see cref="FormName"/>.</summary>
    public string? DsName { get; init; }

    /// <summary>Primary datasource table. Defaults to <see cref="DsName"/>.</summary>
    public string? DsTable { get; init; }

    /// <summary>Optional caption / title (label string or label reference).</summary>
    public string? Caption { get; init; }

    /// <summary>Field names rendered as grid columns (or detail/header fields, per pattern).</summary>
    public IReadOnlyList<string> GridFields { get; init; } = Array.Empty<string>();

    /// <summary>Section definitions for <c>TableOfContents</c> / <c>Dialog</c> / <c>Workspace</c>.</summary>
    public IReadOnlyList<FormSectionSpec> Sections { get; init; } = Array.Empty<FormSectionSpec>();

    /// <summary>Lines datasource name for <see cref="FormPattern.DetailsTransaction"/>. Defaults to <c>{DsName}Lines</c>.</summary>
    public string? LinesDsName { get; init; }

    /// <summary>Lines datasource table for <see cref="FormPattern.DetailsTransaction"/>. Defaults to <see cref="LinesDsName"/>.</summary>
    public string? LinesDsTable { get; init; }
}

/// <summary>A named TabPage / section used by Dialog, TableOfContents, Workspace.</summary>
public sealed record FormSectionSpec(string Name, string Caption);

/// <summary>
/// Generates pattern-correct <c>AxForm</c> XML for the nine D365FO patterns
/// supported by <c>d365fo-mcp-server</c>. Templates are stored as embedded
/// resources under <c>FormTemplates/</c>; placeholders use the
/// <c>{Name}</c> convention and are substituted at render time.
/// </summary>
/// <remarks>
/// References (real AOT forms used for validation):
/// <list type="table">
///   <item><term>SimpleList</term><description>CustGroup</description></item>
///   <item><term>SimpleListDetails</term><description>PaymTerm</description></item>
///   <item><term>DetailsMaster</term><description>CustTable</description></item>
///   <item><term>DetailsTransaction</term><description>SalesTable</description></item>
///   <item><term>Dialog</term><description>ProjTableCreate</description></item>
///   <item><term>TableOfContents</term><description>CustParameters</description></item>
///   <item><term>Lookup</term><description>SysLanguageLookup</description></item>
///   <item><term>ListPage</term><description>CustTableListPage</description></item>
///   <item><term>Workspace</term><description>VendPaymentWorkspace</description></item>
/// </list>
/// </remarks>
public static class FormPatternTemplates
{
    private const string ResourcePrefix = "D365FO.Core.Scaffolding.FormTemplates.";

    /// <summary>Render the given pattern with the supplied options.</summary>
    public static string Build(FormPattern pattern, FormTemplateOptions opt) => pattern switch
    {
        FormPattern.SimpleList         => BuildSimpleList(opt),
        FormPattern.SimpleListDetails  => BuildSimpleListDetails(opt),
        FormPattern.DetailsMaster      => BuildDetailsMaster(opt),
        FormPattern.DetailsTransaction => BuildDetailsTransaction(opt),
        FormPattern.Dialog             => BuildDialog(opt),
        FormPattern.TableOfContents    => BuildTableOfContents(opt),
        FormPattern.Lookup             => BuildLookup(opt),
        FormPattern.ListPage           => BuildListPage(opt),
        FormPattern.Workspace          => BuildWorkspace(opt),
        _                              => BuildSimpleList(opt),
    };

    // --- public per-pattern helpers (exposed for tests + targeted callers) ---

    public static string BuildSimpleList(FormTemplateOptions opt)
    {
        var (dsName, dsTable) = ResolveDs(opt);
        var grid = string.Concat(opt.GridFields.Select(f => RenderGridFieldControl("Grid", f, dsName, indent: 10)));
        return Fill("SimpleList.template.xml", new()
        {
            ["FormName"]          = opt.FormName,
            ["DsName"]            = dsName,
            ["DsTable"]           = dsTable,
            ["Caption"]           = RenderCaption(opt.Caption),
            ["DefaultCol"]        = DefaultColumn(opt, dsName),
            ["DsFields"]          = RenderDsFields(opt.GridFields, indent: 6),
            ["GridFieldControls"] = grid,
        });
    }

    public static string BuildSimpleListDetails(FormTemplateOptions opt)
    {
        var (dsName, dsTable) = ResolveDs(opt);
        var listFields = string.Concat(opt.GridFields.Take(3).Select(f => RenderGridFieldControl("Grid", f, dsName, indent: 14)));
        var detail = string.Concat(opt.GridFields.Select(f => RenderGridFieldControl("Overview", f, dsName, indent: 20)));
        return Fill("SimpleListDetails.template.xml", new()
        {
            ["FormName"]            = opt.FormName,
            ["DsName"]              = dsName,
            ["DsTable"]             = dsTable,
            ["Caption"]             = RenderCaption(opt.Caption),
            ["DefaultCol"]          = DefaultColumn(opt, dsName),
            ["ListFieldControls"]   = listFields,
            ["DetailFieldControls"] = detail,
        });
    }

    public static string BuildDetailsMaster(FormTemplateOptions opt)
    {
        var (dsName, dsTable) = ResolveDs(opt);
        var overview = string.Concat(opt.GridFields.Select(f => RenderGridFieldControl("Overview", f, dsName, indent: 16)));
        return Fill("DetailsMaster.template.xml", new()
        {
            ["FormName"]              = opt.FormName,
            ["DsName"]                = dsName,
            ["DsTable"]               = dsTable,
            ["Caption"]               = RenderCaption(opt.Caption),
            ["OverviewFieldControls"] = overview,
        });
    }

    public static string BuildDetailsTransaction(FormTemplateOptions opt)
    {
        var (dsName, dsTable) = ResolveDs(opt);
        var linesDs = string.IsNullOrEmpty(opt.LinesDsName) ? $"{dsName}Lines" : opt.LinesDsName!;
        var linesTable = string.IsNullOrEmpty(opt.LinesDsTable) ? linesDs : opt.LinesDsTable!;
        var header = string.Concat(opt.GridFields.Select(f => RenderGridFieldControl("Header", f, dsName, indent: 18)));
        return Fill("DetailsTransaction.template.xml", new()
        {
            ["FormName"]            = opt.FormName,
            ["DsName"]              = dsName,
            ["DsTable"]             = dsTable,
            ["LinesDsName"]         = linesDs,
            ["LinesDsTable"]        = linesTable,
            ["Caption"]             = RenderCaption(opt.Caption),
            ["HeaderFieldControls"] = header,
        });
    }

    public static string BuildDialog(FormTemplateOptions opt)
    {
        var dsName = opt.DsName;
        var dsTable = opt.DsTable;
        var dsXml = (dsName, dsTable) switch
        {
            (string n, string t) when !string.IsNullOrEmpty(n) && !string.IsNullOrEmpty(t) =>
                $"  <DataSources>\n    <AxFormDataSource xmlns=\"\">\n      <Name>{n}</Name>\n      <Table>{t}</Table>\n      <Fields />\n      <ReferencedDataSources />\n      <DataSourceLinks />\n      <DerivedDataSources />\n    </AxFormDataSource>\n  </DataSources>\n",
            _ => "  <DataSources />\n",
        };

        string body;
        if (opt.Sections.Count > 0)
        {
            var pages = string.Concat(opt.Sections.Select(s => RenderTabPage(s, indent: 12)));
            body = $"          <AxFormControl xmlns=\"\" i:type=\"AxFormTabControl\">\n            <Name>Tab</Name>\n            <Type>Tab</Type>\n            <FormControlExtension i:nil=\"true\" />\n            <Controls>\n{pages}            </Controls>\n          </AxFormControl>\n";
        }
        else
        {
            body = string.Concat(opt.GridFields.Select(f => RenderDialogField(f, dsName, indent: 10)));
        }

        return Fill("Dialog.template.xml", new()
        {
            ["FormName"]        = opt.FormName,
            ["Caption"]         = RenderCaption(opt.Caption),
            ["DataSourcesXml"]  = dsXml,
            ["BodyContent"]     = body,
        });
    }

    public static string BuildTableOfContents(FormTemplateOptions opt)
    {
        var sections = opt.Sections.Count > 0
            ? opt.Sections
            : new[]
              {
                  new FormSectionSpec("TabPageGeneral", "General"),
                  new FormSectionSpec("TabPageSetup",   "Setup"),
              };
        var pages = string.Concat(sections.Select(s => RenderTocTabPage(s, indent: 8)));

        var (dsName, dsTable) = (opt.DsName, opt.DsTable);
        var dsXml = !string.IsNullOrEmpty(dsName) && !string.IsNullOrEmpty(dsTable)
            ? $"  <DataSources>\n    <AxFormDataSource xmlns=\"\">\n      <Name>{dsName}</Name>\n      <Table>{dsTable}</Table>\n      <Fields />\n      <ReferencedDataSources />\n      <InsertIfEmpty>No</InsertIfEmpty>\n      <DataSourceLinks />\n      <DerivedDataSources />\n    </AxFormDataSource>\n  </DataSources>\n"
            : "  <DataSources />\n";
        var dsOnDesign = !string.IsNullOrEmpty(dsName) ? $"    <DataSource xmlns=\"\">{dsName}</DataSource>\n" : string.Empty;

        return Fill("TableOfContents.template.xml", new()
        {
            ["FormName"]            = opt.FormName,
            ["Caption"]             = RenderCaption(opt.Caption),
            ["DataSourcesXml"]      = dsXml,
            ["DataSourceOnDesign"]  = dsOnDesign,
            ["TabPageControls"]     = pages,
        });
    }

    public static string BuildLookup(FormTemplateOptions opt)
    {
        var (dsName, dsTable) = ResolveDs(opt);
        var grid = string.Concat(opt.GridFields.Select(f => RenderGridFieldControl("Grid", f, dsName, indent: 10)));
        return Fill("Lookup.template.xml", new()
        {
            ["FormName"]          = opt.FormName,
            ["DsName"]            = dsName,
            ["DsTable"]           = dsTable,
            ["Caption"]           = RenderCaption(opt.Caption),
            ["DefaultCol"]        = DefaultColumn(opt, dsName),
            ["GridFieldControls"] = grid,
        });
    }

    public static string BuildListPage(FormTemplateOptions opt)
    {
        var (dsName, dsTable) = ResolveDs(opt);
        var grid = string.Concat(opt.GridFields.Select(f => RenderGridFieldControl("Grid", f, dsName, indent: 10)));
        return Fill("ListPage.template.xml", new()
        {
            ["FormName"]          = opt.FormName,
            ["DsName"]            = dsName,
            ["DsTable"]           = dsTable,
            ["Caption"]           = RenderCaption(opt.Caption),
            ["DefaultCol"]        = DefaultColumn(opt, dsName),
            ["GridFieldControls"] = grid,
        });
    }

    public static string BuildWorkspace(FormTemplateOptions opt)
    {
        var (dsName, dsTable) = ResolveDs(opt);
        var sections = string.Concat(opt.Sections.Select((s, i) => RenderWorkspaceListSection(s, i, dsName, indent: 10)));
        return Fill("Workspace.template.xml", new()
        {
            ["FormName"]      = opt.FormName,
            ["DsName"]        = dsName,
            ["DsTable"]       = dsTable,
            ["Caption"]       = RenderCaption(opt.Caption),
            ["ListSections"]  = sections,
        });
    }

    // --- internals -------------------------------------------------------

    private static (string Ds, string Table) ResolveDs(FormTemplateOptions opt)
    {
        var ds = string.IsNullOrEmpty(opt.DsName) ? opt.FormName : opt.DsName!;
        var t  = string.IsNullOrEmpty(opt.DsTable) ? ds : opt.DsTable!;
        return (ds, t);
    }

    private static string DefaultColumn(FormTemplateOptions opt, string dsName) =>
        opt.GridFields.Count > 0 ? $"Grid_{opt.GridFields[0]}" : $"Grid_{dsName}";

    private static string RenderCaption(string? caption) =>
        string.IsNullOrEmpty(caption) ? string.Empty : $"    <Caption xmlns=\"\">{caption}</Caption>\n";

    private static string RenderDsFields(IReadOnlyList<string> fields, int indent)
    {
        if (fields.Count == 0) return new string(' ', indent) + "<Fields />\n";
        var pad = new string(' ', indent);
        var inner = string.Concat(fields.Select(f =>
            $"{pad}  <AxFormDataSourceField>\n{pad}    <DataField>{f}</DataField>\n{pad}  </AxFormDataSourceField>\n"));
        return $"{pad}<Fields>\n{inner}{pad}</Fields>\n";
    }

    private static string RenderGridFieldControl(string namePrefix, string field, string ds, int indent)
    {
        var pad = new string(' ', indent);
        var sb = new StringBuilder();
        sb.Append(pad).Append("<AxFormControl xmlns=\"\" i:type=\"AxFormStringControl\">\n");
        sb.Append(pad).Append("  <Name>").Append(namePrefix).Append('_').Append(field).Append("</Name>\n");
        sb.Append(pad).Append("  <Type>String</Type>\n");
        sb.Append(pad).Append("  <FormControlExtension i:nil=\"true\" />\n");
        sb.Append(pad).Append("  <DataField>").Append(field).Append("</DataField>\n");
        sb.Append(pad).Append("  <DataSource>").Append(ds).Append("</DataSource>\n");
        sb.Append(pad).Append("</AxFormControl>\n");
        return sb.ToString();
    }

    private static string RenderDialogField(string field, string? ds, int indent)
    {
        var pad = new string(' ', indent);
        var sb = new StringBuilder();
        sb.Append(pad).Append("<AxFormControl xmlns=\"\" i:type=\"AxFormStringControl\">\n");
        sb.Append(pad).Append("  <Name>").Append(field).Append("</Name>\n");
        sb.Append(pad).Append("  <Type>String</Type>\n");
        sb.Append(pad).Append("  <FormControlExtension i:nil=\"true\" />\n");
        if (!string.IsNullOrEmpty(ds))
        {
            sb.Append(pad).Append("  <DataField>").Append(field).Append("</DataField>\n");
            sb.Append(pad).Append("  <DataSource>").Append(ds).Append("</DataSource>\n");
        }
        sb.Append(pad).Append("</AxFormControl>\n");
        return sb.ToString();
    }

    private static string RenderTabPage(FormSectionSpec s, int indent)
    {
        var pad = new string(' ', indent);
        var sb = new StringBuilder();
        sb.Append(pad).Append("<AxFormControl xmlns=\"\" i:type=\"AxFormTabPageControl\">\n");
        sb.Append(pad).Append("  <Name>").Append(s.Name).Append("</Name>\n");
        sb.Append(pad).Append("  <Pattern>FieldsFieldGroups</Pattern>\n");
        sb.Append(pad).Append("  <PatternVersion>1.1</PatternVersion>\n");
        sb.Append(pad).Append("  <Type>TabPage</Type>\n");
        sb.Append(pad).Append("  <Caption>").Append(s.Caption).Append("</Caption>\n");
        sb.Append(pad).Append("  <FormControlExtension i:nil=\"true\" />\n");
        sb.Append(pad).Append("  <Controls />\n");
        sb.Append(pad).Append("</AxFormControl>\n");
        return sb.ToString();
    }

    private static string RenderTocTabPage(FormSectionSpec s, int indent)
    {
        var pad = new string(' ', indent);
        var sb = new StringBuilder();
        sb.Append(pad).Append("<AxFormControl xmlns=\"\" i:type=\"AxFormTabPageControl\">\n");
        sb.Append(pad).Append("  <Name>").Append(s.Name).Append("</Name>\n");
        sb.Append(pad).Append("  <Pattern>FieldsFieldGroups</Pattern>\n");
        sb.Append(pad).Append("  <PatternVersion>1.1</PatternVersion>\n");
        sb.Append(pad).Append("  <Type>TabPage</Type>\n");
        sb.Append(pad).Append("  <Caption>").Append(s.Caption).Append("</Caption>\n");
        sb.Append(pad).Append("  <FrameType>None</FrameType>\n");
        sb.Append(pad).Append("  <FormControlExtension i:nil=\"true\" />\n");
        sb.Append(pad).Append("  <Controls />\n");
        sb.Append(pad).Append("</AxFormControl>\n");
        return sb.ToString();
    }

    private static string RenderWorkspaceListSection(FormSectionSpec s, int idx, string dsName, int indent)
    {
        // ElementPosition follows MCP's heuristic: 536870912 * (idx + 2)
        var pos = 536870912L * (idx + 2);
        var pad = new string(' ', indent);
        var sb = new StringBuilder();
        sb.Append(pad).Append("<AxFormControl xmlns=\"\" i:type=\"AxFormTabPageControl\">\n");
        sb.Append(pad).Append("  <Name>").Append(s.Name).Append("Section</Name>\n");
        sb.Append(pad).Append("  <Caption>").Append(s.Caption).Append("</Caption>\n");
        sb.Append(pad).Append("  <ElementPosition>").Append(pos).Append("</ElementPosition>\n");
        sb.Append(pad).Append("  <Type>TabPage</Type>\n");
        sb.Append(pad).Append("  <FormControlExtension i:nil=\"true\" />\n");
        sb.Append(pad).Append("  <Controls>\n");
        sb.Append(pad).Append("    <AxFormControl xmlns=\"\" i:type=\"AxFormGroupControl\">\n");
        sb.Append(pad).Append("      <Name>").Append(s.Name).Append("CustomFilterGroup</Name>\n");
        sb.Append(pad).Append("      <Pattern>CustomAndQuickFilters</Pattern>\n");
        sb.Append(pad).Append("      <PatternVersion>1.1</PatternVersion>\n");
        sb.Append(pad).Append("      <Type>Group</Type>\n");
        sb.Append(pad).Append("      <WidthMode>SizeToAvailable</WidthMode>\n");
        sb.Append(pad).Append("      <FormControlExtension i:nil=\"true\" />\n");
        sb.Append(pad).Append("      <Controls />\n");
        sb.Append(pad).Append("      <ArrangeMethod>HorizontalLeft</ArrangeMethod>\n");
        sb.Append(pad).Append("      <FrameType>None</FrameType>\n");
        sb.Append(pad).Append("      <Style>CustomFilter</Style>\n");
        sb.Append(pad).Append("    </AxFormControl>\n");
        sb.Append(pad).Append("    <AxFormControl xmlns=\"\" i:type=\"AxFormGridControl\">\n");
        sb.Append(pad).Append("      <Name>").Append(s.Name).Append("Grid</Name>\n");
        sb.Append(pad).Append("      <Type>Grid</Type>\n");
        sb.Append(pad).Append("      <WidthMode>SizeToAvailable</WidthMode>\n");
        sb.Append(pad).Append("      <FormControlExtension i:nil=\"true\" />\n");
        sb.Append(pad).Append("      <Controls />\n");
        sb.Append(pad).Append("      <DataSource>").Append(dsName).Append("</DataSource>\n");
        sb.Append(pad).Append("      <ShowRowLabels>No</ShowRowLabels>\n");
        sb.Append(pad).Append("      <Style>Tabular</Style>\n");
        sb.Append(pad).Append("    </AxFormControl>\n");
        sb.Append(pad).Append("  </Controls>\n");
        sb.Append(pad).Append("  <FrameType>None</FrameType>\n");
        sb.Append(pad).Append("</AxFormControl>\n");
        return sb.ToString();
    }

    private static string Fill(string resourceName, Dictionary<string, string> placeholders)
    {
        var template = LoadTemplate(resourceName);
        var sb = new StringBuilder(template);
        foreach (var kv in placeholders)
            sb.Replace("{" + kv.Key + "}", kv.Value ?? string.Empty);
        return sb.ToString();
    }

    private static string LoadTemplate(string fileName)
    {
        var asm = typeof(FormPatternTemplates).Assembly;
        var resource = ResourcePrefix + fileName;
        using var stream = asm.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException(
                $"Embedded form template '{resource}' not found. Ensure the .template.xml file is included as <EmbeddedResource>.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
