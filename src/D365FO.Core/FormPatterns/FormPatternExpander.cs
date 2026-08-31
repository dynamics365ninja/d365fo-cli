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
        if (FormPatternRegistry.VersionsOf(spec.XmlName).Count == 0)
        {
            reason = $"the AOS has no active pattern named '{spec.XmlName}' — a form declaring it " +
                     "fails the build with \"Pattern not found\", whatever this catalog says";
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
            root.Where(IsRequired).Select(n => EmitNode(n, opt, dsName, spec.Id)).Where(e => e is not null)!);

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
            ConformChildren(controls, registeredDesign.Children, registeredDesign.ExtraChildrenAllowed, opt, dsName, spec.Id);
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

        EnsureUniqueControlNames(design);

        var doc = new XDocument(form);
        ContractOrderCanonicalizer.Apply(doc);
        return doc;
    }

    /// <summary>
    /// Control names are unique per FORM, not per container. Both the catalog skeleton and
    /// the registry parts name a filter control "QuickFilter", and DetailsMasterTabs has two
    /// of them in different branches — which the metadata provider rejects outright:
    /// "Element named: 'QuickFilter' of type 'AxFormControl' already exists". The first
    /// keeps the plain name; a later one is qualified with its parent control, and only
    /// numbered if that still collides.
    /// </summary>
    private static void EnsureUniqueControlNames(XElement design)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var control in design.Descendants(XNamespace.None + "AxFormControl"))
        {
            var nameEl = control.Element(XNamespace.None + "Name");
            if (nameEl is null || string.IsNullOrWhiteSpace(nameEl.Value)) continue;
            if (used.Add(nameEl.Value)) continue;

            var parent = control.Parent?.Parent?.Element(XNamespace.None + "Name")?.Value;
            var candidate = string.IsNullOrWhiteSpace(parent) ? nameEl.Value : parent + nameEl.Value;
            for (var i = 2; !used.Add(candidate); i++) candidate = $"{nameEl.Value}{i}";
            nameEl.Value = candidate;
        }
    }

    /// <summary>
    /// Identity and binding: what the caller or the skeleton decided, which no pattern gets
    /// to overwrite. Everything else on a patterned control is layout the pattern dictates.
    /// </summary>
    private static readonly IReadOnlySet<string> CallerOwnedProperties =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "Name", "Type", "Caption", "Pattern", "PatternVersion", "DataSource", "TitleDataSource",
            "DataField", "DataGroup", "FormControlExtension", "Controls", "Table",
        };

    /// <summary>
    /// Put the property values the registry requires on <paramref name="target"/>. A value the
    /// skeleton already set is REPLACED — the AOS checks these against the pattern and reports
    /// a mismatch as an error ("Property 'Style' … must have value 'CustomFilter' per pattern
    /// …"), so the catalog's guess cannot be allowed to stand where the registry is explicit.
    /// A required value the registry leaves empty is not a value: it only checks what it spells
    /// out.
    /// </summary>
    private static void ApplyProperties(XElement target, IReadOnlyDictionary<string, string> properties)
    {
        foreach (var (key, value) in properties.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (string.IsNullOrEmpty(value) || CallerOwnedProperties.Contains(key)) continue;

            var existing = target.Elements()
                .FirstOrDefault(e => string.Equals(e.Name.LocalName, key, StringComparison.Ordinal));
            if (existing is null) target.Add(new XElement(XNamespace.None + key, value));
            else existing.Value = value;
        }
    }

    /// <summary>
    /// Make one level of the control tree satisfy the registry: every part the AOS requires
    /// (<c>1</c> or <c>1..*</c>) is present with its required property values, and the ones
    /// the skeleton already built are matched by control type — the same way the validator
    /// matches them — rather than duplicated.
    /// </summary>
    private static void ConformChildren(
        XElement container,
        IReadOnlyList<RegisteredPart> parts,
        bool extraAllowed,
        FormExpandOptions opt,
        string? dsName,
        string topPatternId)
    {
        var claimed = new HashSet<XElement>();
        var ordered = new List<XElement>();

        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part.Type)) continue;

            // A choice slot (OneOf) names several types and which one belongs here is the
            // author's call, so none is created — but the control already filling it is
            // claimed, or the prune at the end of this method would take it for an extra.
            if (part.IsChoice)
            {
                var alternatives = part.Type.Split('|', StringSplitOptions.RemoveEmptyEntries);
                var filled = container.Elements().FirstOrDefault(e => !claimed.Contains(e)
                    && alternatives.Any(a => string.Equals(
                        e.Element(XNamespace.None + "Type")?.Value, a, StringComparison.OrdinalIgnoreCase)));
                if (filled is not null)
                {
                    claimed.Add(filled);
                    ordered.Add(filled);
                    // The properties belong to the slot, and the AOS demands them of whatever
                    // fills it — "Property 'DefaultAction' on control …/MainGrid must have
                    // value … per pattern 'Details Master w/Standard Tabs'".
                    ApplyProperties(filled, part.Properties);
                    if (string.Equals(filled.Element(XNamespace.None + "Type")?.Value, "Grid", StringComparison.OrdinalIgnoreCase))
                        BindGrid(filled, NameFor(part), opt, dsName);
                }
                continue;
            }

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

            // A container the pattern wants a sub-pattern on and none can be chosen for it is
            // better left out than emitted unspecified, when the slot is optional: the AOS
            // refuses the form over it rather than warning ("requires a sub-pattern specified
            // on control …"). FormPartSectionList's header group is the live case.
            if (!required && part.SubPatterns.Count > 0 && ChooseSubPattern(part, topPatternId) is null
                && existing.Element(XNamespace.None + "Pattern") is null)
            {
                existing.Remove();
                continue;
            }

            claimed.Add(existing);
            ordered.Add(existing);
            ApplyProperties(existing, part.Properties);
            if (string.Equals(part.Type, "Grid", StringComparison.OrdinalIgnoreCase))
                BindGrid(existing, NameFor(part), opt, dsName);

            // A container the pattern allows sub-patterns on must declare one, or the AOS
            // refuses the form ("requires a sub-pattern specified on control …") — that is
            // true whether one is allowed there or seven. The first one this repo's catalog
            // also models is taken: declaring a name the catalog does not know (the registry
            // offers several it has never heard of) turns the expansion into an FP001
            // self-test failure, i.e. a refusal, which helps nobody.
            if (existing.Element(XNamespace.None + "Pattern") is null && ChooseSubPattern(part, topPatternId) is { } sub)
            {
                var subVersions = FormPatternRegistry.VersionsOf(sub);
                existing.AddFirst(new XElement(XNamespace.None + "PatternVersion",
                    subVersions.Count > 0 ? subVersions[0] : "1.0"));
                existing.AddFirst(new XElement(XNamespace.None + "Pattern", sub));
                ExpandSubPattern(existing, sub, opt, dsName, topPatternId);
            }

            if (part.Children is { Count: > 0 } && existing.Element(XNamespace.None + "Controls") is { } inner)
                ConformChildren(inner, part.Children, part.ExtraChildrenAllowed, opt, dsName, topPatternId);
        }

        // The pattern allows nothing but its own parts here, so a control the skeleton added
        // that no part claims is a build error, not a bonus: FormPartSectionList's catalog
        // skeleton contributes a Group the AOS has no slot for and then fails the form on it.
        if (!extraAllowed && parts.Count > 0)
        {
            foreach (var extra in container.Elements().Where(e => !claimed.Contains(e)).ToList())
                extra.Remove();
        }

        // A grid the pattern pairs with a default-action button has to name it: "Property
        // 'DefaultAction' on control …/MainGrid cannot be empty per pattern 'Details Master
        // w/Standard Tabs'". The registry models the button as a sibling part named after the
        // grid, which is exactly the wiring the AOS is asking for.
        foreach (var grid in ordered.Where(e =>
                     string.Equals(e.Element(XNamespace.None + "Type")?.Value, "Grid", StringComparison.OrdinalIgnoreCase)))
        {
            if (grid.Elements().Any(e => e.Name.LocalName == "DefaultAction")) continue;
            var gridName = grid.Element(XNamespace.None + "Name")?.Value;
            if (string.IsNullOrEmpty(gridName)) continue;

            var action = container.Elements().FirstOrDefault(e =>
                string.Equals(e.Element(XNamespace.None + "Name")?.Value, gridName + "DefaultAction",
                    StringComparison.OrdinalIgnoreCase));
            if (action is not null)
                grid.Add(new XElement(XNamespace.None + "DefaultAction", gridName + "DefaultAction"));
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
    /// Bind a grid to the form's datasource and render the caller's <c>--field</c>s as
    /// columns, the way the hand-written templates do. Reached from both paths: the grid a
    /// pattern requires is as often created from the registry part tree as from the catalog
    /// skeleton, and a grid the caller asked for columns on has to get them either way.
    /// </summary>
    private static void BindGrid(XElement grid, string namePrefix, FormExpandOptions opt, string? dsName)
    {
        if (dsName is null) return;
        var controls = grid.Element(XNamespace.None + "Controls");
        if (controls is null) return;

        if (!grid.Elements().Any(e => e.Name.LocalName == "DataSource"))
            controls.AddBeforeSelf(new XElement(XNamespace.None + "DataSource", dsName));

        if (opt.GridFields is not { Count: > 0 } || controls.HasElements) return;

        foreach (var field in opt.GridFields)
        {
            var normalized = "String";
            if (opt.ControlTypeResolver is not null)
            {
                var (_, typeElement) = opt.ControlTypeResolver(field);
                if (!string.IsNullOrWhiteSpace(typeElement)) normalized = typeElement;
            }
            controls.Add(FormControlFactory.CreateBoundField(normalized, $"{namePrefix}_{field}", dsName, field));
        }
    }

    /// <summary>
    /// Fill a container that has just been given a sub-pattern with what that sub-pattern
    /// requires. Declaring one is not free: the AOS — and this repo's own FP003 — then hold
    /// the container to the sub-pattern's structure, so ToolbarList without its grid is as
    /// broken as no sub-pattern at all.
    /// </summary>
    private static void ExpandSubPattern(
        XElement control, string subPatternName, FormExpandOptions opt, string? dsName, string topPatternId)
    {
        if (control.Element(XNamespace.None + "Controls") is not { } slot) return;
        if (FormPatternCatalog.ResolveSubPattern(subPatternName) is not { } spec) return;

        foreach (var node in spec.Root.Where(IsRequired))
        {
            if (!IsConcrete(node)) continue;
            if (slot.Elements().Any(e => string.Equals(
                    e.Element(XNamespace.None + "Type")?.Value, node.ControlTypes[0], StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            if (EmitNode(node, opt, dsName, topPatternId) is { } emitted) slot.Add(emitted);
        }

        // …then hold that skeleton to the registry's own definition of the sub-pattern, the
        // same way the design root is conformed — including the property values the
        // sub-pattern requires of the CONTAINER itself ("Property 'ColumnsMode' on control …
        // must have value 'Fill' per pattern 'Fields and Field Groups'").
        if (RegistrySpecFactory.Newest(subPatternName)?.Design is { } subDesign)
        {
            ApplyProperties(control, subDesign.Properties);
            ConformChildren(slot, subDesign.Children, subDesign.ExtraChildrenAllowed, opt, dsName, topPatternId);
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

    /// <summary>
    /// The sub-pattern to declare on a container, or null when none of the ones the AOS
    /// allows there can be declared here. A candidate has to clear every gate the result is
    /// then held to: this repo's catalog has to model it (FP001), it has to apply to this
    /// control type and be legal under this form's pattern (FP007), and its own required
    /// children have to be things the expander can materialise (FP003) — declaring one and
    /// leaving its skeleton half-built is a refusal, not a feature.
    /// </summary>
    private static string? ChooseSubPattern(RegisteredPart part, string topPatternId) =>
        ChooseSubPattern(part.SubPatterns, part.Type, topPatternId);

    /// <inheritdoc cref="ChooseSubPattern(RegisteredPart, string)"/>
    private static string? ChooseSubPattern(
        IReadOnlyList<string>? allowed, string controlType, string topPatternId)
    {
        if (allowed is not { Count: > 0 }) return null;
        var candidates = allowed
            .Select(FormPatternCatalog.ResolveSubPattern)
            .Where(sub => sub is not null)
            .Select(sub => sub!)
            .Where(sub => sub.AppliesToControlTypes.Contains(controlType, StringComparer.OrdinalIgnoreCase))
            .Where(sub => sub.ParentPatterns is null ||
                          sub.ParentPatterns.Contains(topPatternId, StringComparer.OrdinalIgnoreCase))
            .Where(sub => sub.Root.Where(IsRequired).All(Materialisable))
            // Least constraining first: a container the author still has to fill is better
            // served by the sub-pattern that demands the least of it. Registry order decides
            // ties, so the choice stays deterministic.
            .OrderBy(sub => sub.Root.Count(IsRequired))
            .ToList();

        return candidates.Count > 0 ? candidates[0].XmlName : null;

        // A required slot the expander can emit AND the validator can then match. A wildcard
        // slot has no control type to create — that is how the Wizard body picked up
        // DimensionEntryControl (required "Control/*") and then failed its own FP003. A named
        // extension control is fine: QuickFilterControl is emitted from its own name, which is
        // what CustomAndQuickFilters — the sub-pattern every custom-filter group needs — asks
        // for.
        static bool Materialisable(NodeSpec node) =>
            IsConcrete(node) && !node.ControlTypes.Contains("*");
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
    private static XElement? EmitNode(NodeSpec spec, FormExpandOptions opt, string? dsName, string topPatternId)
    {
        if (!IsConcrete(spec)) return null;

        var el = FormControlFactory.CreateForSpec(spec);
        var type = spec.ControlTypes[0];

        // CreateForSpec only declares a sub-pattern when exactly one is allowed. The AOS does
        // not soften for the ambiguous case — a custom-filter group with two candidates still
        // fails the build with "requires a sub-pattern specified on control …" — so one is
        // chosen here, by the same rules the result is validated against.
        if (spec.RequiresSubPattern && el.Element(XNamespace.None + "Pattern") is null
            && ChooseSubPattern(spec.AllowedSubPatterns, type, topPatternId) is { } chosen)
        {
            var chosenVersions = FormPatternRegistry.VersionsOf(chosen);
            el.AddFirst(new XElement(XNamespace.None + "PatternVersion",
                chosenVersions.Count > 0 ? chosenVersions[0] : "1.0"));
            el.AddFirst(new XElement(XNamespace.None + "Pattern", chosen));
            ExpandSubPattern(el, chosen, opt, dsName, topPatternId);
        }

        if (string.Equals(type, "Grid", StringComparison.OrdinalIgnoreCase))
            BindGrid(el, spec.NameHint ?? spec.Id, opt, dsName);

        if (spec.Children is { Count: > 0 } && el.Element("Controls") is { } container)
        {
            foreach (var child in spec.Children.Where(IsRequired))
            {
                if (EmitNode(child, opt, dsName, topPatternId) is { } childEl) container.Add(childEl);
            }
        }

        return el;
    }
}
