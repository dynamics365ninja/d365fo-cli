// <copyright file="FormPatternExpander.cs" company="d365fo-cli contributors">
// MIT
// </copyright>

using System.Xml.Linq;
using D365FO.Core.Scaffolding;

namespace D365FO.Core.FormPatterns;

/// <summary>Options for one registry-driven form expansion.</summary>
/// <param name="FormName">AOT form name.</param>
/// <param name="DsTable">Primary datasource table, when the caller binds one.</param>
/// <param name="Caption">Design caption (a label token).</param>
/// <param name="GridFields">Fields rendered as columns when the pattern's skeleton has a required Grid.</param>
/// <param name="ControlTypeResolver">Field name → (AxForm i:type, &lt;Type&gt; element) resolver, as the templates use.</param>
public sealed record FormExpandOptions(
    string FormName,
    string? DsTable = null,
    string? Caption = null,
    IReadOnlyList<string>? GridFields = null,
    Func<string, (string AxType, string TypeElement)>? ControlTypeResolver = null);

/// <summary>
/// Deterministic, catalog-driven form control expander — port of the upstream MCP server's
/// <c>formControlExpander.ts</c>.
///
/// Walks the SAME <see cref="FormPatternSpec"/> the validator enforces (for the migrated
/// patterns that spec is itself derived from the AOT registry) and emits the required
/// control skeleton directly, instead of refusing every pattern that has no hand-written
/// <c>FormPatternTemplates</c> entry. Because generation and validation share one source of
/// truth, the output is structurally pattern-correct by construction — every
/// required/oneOrMore container the pattern mandates is present, with the pattern's expected
/// container properties.
///
/// Scope &amp; safety (upstream's rules, kept):
/// <list type="bullet">
/// <item>covers patterns with no hand-written template — the nine templated patterns keep
/// their proven templates;</item>
/// <item><c>requiresSubPattern</c> containers are emitted without a declared sub-pattern
/// unless exactly one is allowed (an FP006 warning, never an error);</item>
/// <item>the caller self-tests the result against <see cref="FormPatternValidator"/> and
/// refuses on error rather than writing a form the AOS would reject.</item>
/// </list>
/// </summary>
public static class FormPatternExpander
{
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";
    private static readonly XNamespace Ax = "Microsoft.Dynamics.AX.Metadata.V6";

    /// <summary>A node is concrete (emittable) when it names a single, specific control type.</summary>
    internal static bool IsConcrete(NodeSpec spec)
        => spec.ControlTypes.Count >= 1 && !string.IsNullOrEmpty(spec.ControlTypes[0]) && spec.ControlTypes[0] != "*";

    /// <summary>Required containers only — optional/zeroOrMore slots are left out of the skeleton.</summary>
    internal static bool IsRequired(NodeSpec spec)
        => spec.Occurrence is Occurrence.Required or Occurrence.OneOrMore;

    /// <summary>
    /// Whether the expander can faithfully build <paramref name="patternXmlName"/>. It bails
    /// when the registry has no such pattern, when any REQUIRED node (at any depth) names no
    /// concrete control type (a <c>*</c> wildcard slot the expander cannot materialise —
    /// FP003 would fire), or when sibling slots share a control type (the validator matches
    /// children by type, so duplicates become ambiguous once optionals are dropped).
    /// </summary>
    public static bool CanExpand(FormPatternSpec spec, out string? reason)
    {
        reason = null;
        if (spec.Versions is not { Count: > 0 })
        {
            reason = $"'{spec.XmlName}' declares no versions (the Custom escape-hatch pattern is authored by hand)";
            return false;
        }
        var root = spec.Root;
        if (root is not { Count: > 0 })
        {
            reason = $"'{spec.XmlName}' declares no structural spec to expand";
            return false;
        }

        static bool RequiredConcrete(IEnumerable<NodeSpec> nodes) =>
            nodes.Where(IsRequired).All(n => IsConcrete(n) && RequiredConcrete(n.Children ?? []));

        static bool LevelUnambiguous(IReadOnlyList<NodeSpec> nodes)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in nodes.Where(IsConcrete))
            {
                if (!seen.Add(n.ControlTypes[0])) return false;
            }
            return nodes.Where(IsRequired).All(n => LevelUnambiguous(n.Children ?? Array.Empty<NodeSpec>()));
        }

        if (!RequiredConcrete(root))
        {
            reason = $"'{spec.XmlName}' has a required wildcard slot the expander cannot materialise";
            return false;
        }
        if (!LevelUnambiguous(root))
        {
            reason = $"'{spec.XmlName}' has sibling slots sharing a control type — expansion would be ambiguous";
            return false;
        }
        return true;
    }

    /// <summary>
    /// Expand <paramref name="spec"/> into a complete AxForm document. Callers must
    /// self-test the result with <see cref="FormPatternValidator"/> before writing it.
    /// Returns null when <see cref="CanExpand"/> says no.
    /// </summary>
    public static XDocument? Expand(FormPatternSpec spec, FormExpandOptions opt)
    {
        if (!CanExpand(spec, out _)) return null;

        var root = spec.Root!;
        var version = spec.Versions[0];
        var designProps = spec.DesignProperties;
        var dsName = string.IsNullOrWhiteSpace(opt.DsTable) ? null : opt.DsTable;

        var controls = new XElement("Controls",
            root.Where(IsRequired).Select(n => EmitNode(n, opt, dsName)).Where(e => e is not null)!);

        var design = new XElement("Design");
        foreach (var (key, value) in (designProps ?? new Dictionary<string, string>()).OrderBy(p => p.Key, StringComparer.Ordinal))
            design.Add(new XElement(key, value));
        if (!string.IsNullOrWhiteSpace(opt.Caption)) design.Add(new XElement("Caption", opt.Caption));
        if (dsName is not null)
        {
            design.Add(new XElement("DataSource", dsName));
            design.Add(new XElement("TitleDataSource", dsName));
        }
        design.Add(new XElement("Pattern", spec.XmlName));
        design.Add(new XElement("PatternVersion", version));
        design.Add(controls);

        var dataSources = new XElement("DataSources");
        if (dsName is not null)
        {
            var fieldsEl = opt.GridFields is { Count: > 0 }
                ? new XElement("Fields", opt.GridFields.Select(f =>
                    new XElement("AxFormDataSourceField", new XElement("DataField", f))))
                : new XElement("Fields");
            dataSources.Add(new XElement("AxFormDataSource",
                new XElement("Name", dsName),
                new XElement("Table", opt.DsTable),
                fieldsEl,
                new XElement("ReferencedDataSources"),
                new XElement("InsertIfEmpty", "No"),
                new XElement("DataSourceLinks"),
                new XElement("DerivedDataSources")));
        }

        var form = new XElement(Ax + "AxForm",
            new XAttribute(XNamespace.Xmlns + "i", Xsi.NamespaceName),
            new XElement("Name", opt.FormName),
            new XElement("SourceCode",
                new XElement("Methods",
                    new XElement("Method",
                        new XElement("Name", "classDeclaration"),
                        new XElement("Source",
                            new XCData($"\n[Form]\npublic class {opt.FormName} extends FormRun\n{{\n}}\n\n")))),
                new XElement("DataSources"),
                new XElement("DataControls"),
                new XElement("Members")),
            dataSources,
            design);

        // XElement children of an element in the Ax namespace inherit it; the AOT reads
        // inner elements in the EMPTY namespace (the templates spell xmlns="" everywhere).
        foreach (var el in form.Descendants().Where(e => e.Name.Namespace == Ax && e != form))
            el.Name = XNamespace.None + el.Name.LocalName;

        var doc = new XDocument(form);
        ContractOrderCanonicalizer.Apply(doc);
        return doc;
    }

    /// <summary>
    /// Emit one control node and its required descendants. GridFields are rendered as
    /// column controls when the node is a Grid and a datasource is bound.
    /// </summary>
    private static XElement? EmitNode(NodeSpec spec, FormExpandOptions opt, string? dsName)
    {
        if (!IsConcrete(spec)) return null;

        var el = FormControlFactory.CreateForSpec(spec);
        var type = spec.ControlTypes[0];

        if (string.Equals(type, "Grid", StringComparison.OrdinalIgnoreCase) && dsName is not null)
        {
            // Bind the grid and render the caller's fields as columns, the same way the
            // hand-written templates do.
            el.Element("Controls")?.AddBeforeSelf(new XElement("DataSource", dsName));
            if (opt.GridFields is { Count: > 0 } && el.Element("Controls") is { } gridControls)
            {
                foreach (var field in opt.GridFields)
                {
                    var normalized = "String";
                    if (opt.ControlTypeResolver is not null)
                    {
                        var (_, typeElement) = opt.ControlTypeResolver(field);
                        if (!string.IsNullOrWhiteSpace(typeElement)) normalized = typeElement;
                    }
                    gridControls.Add(FormControlFactory.CreateBoundField(
                        normalized, $"{spec.NameHint ?? spec.Id}_{field}", dsName, field));
                }
            }
        }

        if (spec.Children is { Count: > 0 } && el.Element("Controls") is { } container)
        {
            foreach (var child in spec.Children.Where(IsRequired))
            {
                if (EmitNode(child, opt, dsName) is { } childEl) container.Add(childEl);
            }
        }

        return el;
    }
}
