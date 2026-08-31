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
        if (UndeclarableSubPattern(spec) is { } slot)
        {
            reason = $"'{spec.XmlName}' requires a sub-pattern on its {slot} that this tool cannot choose " +
                     "— the AOS rejects the form without one";
            return false;
        }
        return true;
    }

    /// <summary>
    /// The first required container the AOT pattern demands a sub-pattern on that cannot be
    /// filled in here: the registry allows exactly one there — which the AOS then insists on —
    /// and it is a pattern this repo's catalog does not model, so declaring it would only fail
    /// the self-test. A slot with several allowed sub-patterns is a free choice, not a demand
    /// (DropDialog's dialog content lists seven and compiles with none), so it does not block.
    /// Null when the pattern has no such slot.
    /// </summary>
    /// <remarks>
    /// A form emitted without the sub-pattern compiles no better: the AOS fails it with
    /// "Pattern 'Task Single' requires a sub-pattern specified on control …". Refusing with a
    /// reason is the honest answer until the catalog and the AOT registry are reconciled.
    /// </remarks>
    private static string? UndeclarableSubPattern(FormPatternSpec spec)
    {
        var versions = FormPatternRegistry.VersionsOf(spec.XmlName);
        if (versions.Count == 0) return null;
        var design = FormPatternRegistry.Find(spec.XmlName, versions[0])?.Design;
        return design is null ? null : Walk(design.Children);

        static string? Walk(IReadOnlyList<RegisteredPart> parts)
        {
            foreach (var part in parts)
            {
                if (!part.Count.StartsWith('1')) continue;
                if (part.SubPatterns is { Count: 1 } &&
                    FormPatternCatalog.ResolveSubPattern(part.SubPatterns[0]) is null)
                {
                    return $"{part.Type} \"{part.Part}\"";
                }
                if (Walk(part.Children) is { } deeper) return deeper;
            }
            return null;
        }
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

        // The version has to be one the AOS itself declares, not merely one this repo's
        // catalog lists: the compiler rejects the form outright otherwise ("Unable to
        // validate pattern 'DropDialog 1.1'. Message: Pattern 'DropDialog 1.1' not found"),
        // and the two models disagree for several patterns — the catalog's newest Wizard is
        // 1.1, the AOS's is 1.2, which is what Microsoft's own WrkCtrBulkResReqEditWizard
        // declares. The registry wins where it knows the pattern.
        var registryVersions = FormPatternRegistry.VersionsOf(spec.XmlName);
        var version = registryVersions.Count > 0 ? registryVersions[0] : spec.Versions[0];
        var registered = FormPatternRegistry.Find(spec.XmlName, version);
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

        // Conform the skeleton to what the AOS will validate it against. The catalog gives
        // the shape this repo's FP rules check; the registry gives the shape the platform
        // enforces at build time, and where the catalog is silent the platform is not:
        // it failed FormPartFactboxGrid on seven missing property values and Wizard on a
        // required MainInstruction the catalog does not model.
        if (registered?.Design is { } registeredDesign)
        {
            ApplyProperties(design, registeredDesign.Properties);
            ConformChildren(controls, registeredDesign.Children, registeredDesign.ExtraChildrenAllowed);
        }

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

        // `new XElement("Name", …)` names an element in NO namespace — a child does not
        // inherit its parent's — so every element built above is already empty-namespace and
        // only the root carries V6. That is right for everything below the root's direct
        // children, and wrong for the direct children themselves: the AOT reader wants
        // <Name>, <SourceCode>, <DataSources> and <Design> in the form's own namespace, which
        // is what the hand-written templates and Microsoft's own AxForm files spell. Written
        // as <Name xmlns=""> the provider reads no name at all and xppc fails the file with
        // "The element must be named 'X' instead of '' to be consistent with its file name" —
        // every registry-expanded pattern was a hard build error. Found by compiling the
        // expanded patterns with xppc 7.0.7996.33.
        foreach (var el in form.Elements())
            el.Name = Ax + el.Name.LocalName;

        var doc = new XDocument(form);
        ContractOrderCanonicalizer.Apply(doc);
        return doc;
    }

    /// <summary>
    /// Add the property values the registry requires on <paramref name="target"/>, without
    /// touching one the skeleton already set (the caller's caption, datasource and pattern
    /// are its own). A required value the registry leaves empty is not a value — the AOS
    /// only checks the ones it spells out.
    /// </summary>
    private static void ApplyProperties(XElement target, IReadOnlyDictionary<string, string> properties)
    {
        foreach (var (key, value) in properties.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (string.IsNullOrEmpty(value)) continue;
            if (target.Elements().Any(e => string.Equals(e.Name.LocalName, key, StringComparison.Ordinal))) continue;
            target.Add(new XElement(XNamespace.None + key, value));
        }
    }

    /// <summary>
    /// Make one level of the control tree satisfy the registry: every part the AOS requires
    /// (<c>1</c> or <c>1..*</c>) is present with its required property values, and the ones
    /// the skeleton already built are matched by control type — the same way the validator
    /// matches them — rather than duplicated.
    /// </summary>
    private static void ConformChildren(XElement container, IReadOnlyList<RegisteredPart> parts, bool extraAllowed)
    {
        var claimed = new HashSet<XElement>();
        var ordered = new List<XElement>();

        foreach (var part in parts)
        {
            // A choice slot (OneOf) names several types; which one belongs here is the
            // author's call, so it is left to them exactly as the catalog path leaves it.
            if (part.IsChoice || string.IsNullOrEmpty(part.Type)) continue;

            // A "$"-prefixed type ($Button, $Container) is a family marker, not a control
            // type: materialising it literally produced an AxForm$ButtonControl the metadata
            // provider refuses to deserialize at all. Such a slot is never created — but the
            // control the skeleton already put there still has to carry the properties the
            // AOS requires of it (DropDialog's commit button needs Command=OK).
            if (part.Type.StartsWith('$'))
            {
                var marker = container.Elements()
                    .FirstOrDefault(e => !claimed.Contains(e) && IsSameFamily(e, part.Type));
                if (marker is not null)
                {
                    claimed.Add(marker);
                    ApplyProperties(marker, part.Properties);
                }
                continue;
            }

            var required = part.Count.StartsWith('1');

            var existing = container.Elements()
                .FirstOrDefault(e => !claimed.Contains(e) && string.Equals(
                    e.Element(XNamespace.None + "Type")?.Value, part.Type, StringComparison.OrdinalIgnoreCase));

            if (existing is null)
            {
                if (!required) continue;
                existing = FormControlFactory.Create(part.Type, NameFor(part));
                container.Add(existing);
            }

            // A container the pattern wants a sub-pattern on, where more than one is allowed,
            // cannot be filled in for the author — and the AOS refuses the form rather than
            // warning about it. An OPTIONAL slot like that is better left out entirely than
            // emitted unspecified (FormPartSectionList's header group is the live case).
            if (!required && part.SubPatterns is { Count: > 1 }
                && existing.Element(XNamespace.None + "Pattern") is null)
            {
                existing.Remove();
                continue;
            }

            claimed.Add(existing);
            ordered.Add(existing);
            ApplyProperties(existing, part.Properties);

            // A container the pattern requires a sub-pattern on must declare one, or the
            // AOS refuses the form ("requires a sub-pattern specified on control …"). Only
            // an unambiguous choice is made here — the same rule the catalog path follows.
            // Only a sub-pattern this repo's catalog also knows: declaring one it does not
            // (the registry's Task tab pages ask for "ToolbarList") turns the expansion into
            // an FP001 self-test failure, i.e. a refusal, which helps nobody.
            if (part.SubPatterns is { Count: 1 } && existing.Element(XNamespace.None + "Pattern") is null
                && FormPatternCatalog.ResolveSubPattern(part.SubPatterns[0]) is not null)
            {
                var sub = part.SubPatterns[0];
                var subVersions = FormPatternRegistry.VersionsOf(sub);
                existing.AddFirst(new XElement(XNamespace.None + "PatternVersion",
                    subVersions.Count > 0 ? subVersions[0] : "1.0"));
                existing.AddFirst(new XElement(XNamespace.None + "Pattern", sub));
            }

            if (part.Children is { Count: > 0 } && existing.Element(XNamespace.None + "Controls") is { } inner)
                ConformChildren(inner, part.Children, part.ExtraChildrenAllowed);
        }

        // The pattern allows nothing but its own parts here, so a control the skeleton added
        // that no part claims is a build error, not a bonus: FormPartSectionList's catalog
        // skeleton contributes a Group the AOS has no slot for and then fails the form on it.
        if (!extraAllowed && parts.Count > 0)
        {
            foreach (var extra in container.Elements().Where(e => !claimed.Contains(e)).ToList())
                extra.Remove();
        }

        // The AOS checks the order of the parts it knows, and a control created here lands at
        // the end of whatever the catalog skeleton built. Re-lay the level in registry order,
        // with anything the registry does not model kept, in its own order, after them.
        if (ordered.Count > 1)
        {
            var extras = container.Elements().Where(e => !claimed.Contains(e)).ToList();
            foreach (var e in container.Elements().ToList()) e.Remove();
            foreach (var e in ordered) container.Add(e);
            foreach (var e in extras) container.Add(e);
        }
    }

    /// <summary>
    /// Does <paramref name="control"/> belong to the control family a <c>$</c>-marker part
    /// names? <c>$Button</c> covers every button control the AOT has; the marker's own name
    /// (minus the sigil) is the family, matched against the control's type.
    /// </summary>
    private static bool IsSameFamily(XElement control, string markerType)
    {
        var family = markerType.TrimStart('$');
        var type = control.Element(XNamespace.None + "Type")?.Value;
        return type is not null && type.Contains(family, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Control name for a registry part: its stable part id, else its alias with the spaces removed.</summary>
    private static string NameFor(RegisteredPart part) =>
        !string.IsNullOrWhiteSpace(part.Part) ? part.Part
        : !string.IsNullOrWhiteSpace(part.Alias) ? part.Alias!.Replace(" ", string.Empty)
        : part.Type;

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
