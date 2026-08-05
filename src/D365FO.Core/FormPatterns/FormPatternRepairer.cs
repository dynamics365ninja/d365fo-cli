// <copyright file="FormPatternRepairer.cs" company="d365fo-cli contributors">
// MIT
// </copyright>

using System.Xml.Linq;

namespace D365FO.Core.FormPatterns;

/// <summary>One change the repairer made, or declined to make, to a form.</summary>
/// <param name="Rule">The <see cref="FormPatternValidator"/> rule this addresses (FP003, FP005, …).</param>
/// <param name="Path">Design path the change applies to.</param>
/// <param name="Action">What happened: <c>added</c>, <c>reordered</c>, <c>set-property</c>, <c>set-pattern</c>, <c>set-sub-pattern</c>, <c>set-version</c>, or <c>skipped</c>.</param>
/// <param name="Detail">Human-readable description.</param>
public sealed record FormRepairChange(string Rule, string Path, string Action, string Detail);

/// <summary>Result of a repair pass.</summary>
public sealed class FormRepairResult
{
    /// <summary>The repaired XML. Identical to the input when <see cref="Changes"/> is empty.</summary>
    public required string Xml { get; init; }

    public required IReadOnlyList<FormRepairChange> Changes { get; init; }

    /// <summary>Violations the repairer deliberately did not touch, with the reason.</summary>
    public required IReadOnlyList<FormRepairChange> Skipped { get; init; }

    public required FormPatternReport Before { get; init; }

    public required FormPatternReport After { get; init; }

    public bool Changed => Changes.Count > 0;

    /// <summary>True when the repair removed every error-severity violation.</summary>
    public bool FullyRepaired => !After.HasErrors;
}

/// <summary>
/// Deterministic form auto-repair: takes a form that <see cref="FormPatternValidator"/>
/// rejects and applies the minimal structural edits its own <c>Fix</c> strings describe.
///
/// The validator has always known *what* is wrong and *what would fix it* — every
/// <see cref="FormPatternViolation"/> carries a machine-generated remediation. Until now
/// nothing consumed those: the only way to get a pattern-correct form was to regenerate
/// it whole with <c>generate form --overwrite</c>, which destroys any hand-written
/// controls. This closes that loop, and it deliberately repairs by re-deriving structure
/// from the catalog rather than by patching violation text.
///
/// <para><b>What it repairs</b> — the cases with exactly one correct outcome:</para>
/// <list type="bullet">
/// <item><description><b>FP010/FP001</b> — declare a pattern on <c>&lt;Design&gt;</c>
/// (only with an explicit <c>pattern</c> argument; it never guesses).</description></item>
/// <item><description><b>FP002</b> — pin an unknown/older <c>PatternVersion</c> to the
/// newest catalog version.</description></item>
/// <item><description><b>FP003</b> — insert the missing required container/control,
/// built from the <see cref="NodeSpec"/> by <see cref="FormControlFactory"/>.</description></item>
/// <item><description><b>FP005</b> — reorder root controls into the spec's declared
/// order (relative order of unspec'd extras is preserved).</description></item>
/// <item><description><b>FP006</b> — apply the required sub-pattern when the slot allows
/// exactly one.</description></item>
/// <item><description><b>FP009</b> — set a Design/control property to the pattern
/// default.</description></item>
/// </list>
///
/// <para><b>What it refuses</b>, and why refusing is the correct behaviour:</para>
/// <list type="bullet">
/// <item><description><b>FP004</b> — a disallowed child means deleting a control someone
/// wrote. Never automatic.</description></item>
/// <item><description><b>FP007</b> — a sub-pattern on the wrong control type could be
/// fixed by changing either the pattern or the control; ambiguous.</description></item>
/// <item><description><b>FP008</b> — datasources are a modelling decision, not a
/// structural gap.</description></item>
/// <item><description><b>FP006</b> with several allowed sub-patterns — a real choice
/// about the container's purpose.</description></item>
/// </list>
/// Refusals are returned in <see cref="FormRepairResult.Skipped"/> rather than silently
/// dropped, so a caller can always tell "repaired" from "repaired what it could".
/// </summary>
public static class FormPatternRepairer
{
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    /// <summary>
    /// Repair <paramref name="xml"/> against its declared pattern, or against
    /// <paramref name="pattern"/> when supplied (which also lets an unpatterned form be
    /// adopted into a pattern).
    /// </summary>
    public static FormRepairResult Repair(string xml, string? pattern = null)
    {
        var before = FormPatternValidator.ValidateXml(xml);
        var changes = new List<FormRepairChange>();
        var skipped = new List<FormRepairChange>();

        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (Exception ex)
        {
            skipped.Add(new("FP000", "(document)", "skipped", "XML does not parse: " + ex.Message.Split('\n')[0]));
            return new FormRepairResult { Xml = xml, Changes = changes, Skipped = skipped, Before = before, After = before };
        }

        var axForm = doc.Root;
        if (axForm is null || axForm.Name.LocalName != "AxForm")
        {
            skipped.Add(new("FP000", "(document)", "skipped", "Not an AxForm document."));
            return new FormRepairResult { Xml = xml, Changes = changes, Skipped = skipped, Before = before, After = before };
        }

        var design = FormDesignWalker.Child(axForm, "Design");
        if (design is null)
        {
            skipped.Add(new("FP000", "Design", "skipped",
                "Form has no <Design> element — there is no structure to repair. Regenerate with `d365fo generate form`."));
            return new FormRepairResult { Xml = xml, Changes = changes, Skipped = skipped, Before = before, After = before };
        }

        // ---- Pattern declaration (FP010 / FP001) ----
        var declared = FormDesignWalker.Child(design, "Pattern")?.Value.Trim();
        var spec = FormPatternCatalog.ResolveExact(declared);

        if (pattern is not null)
        {
            var requested = FormPatternCatalog.Resolve(pattern);
            if (requested is null)
            {
                skipped.Add(new("FP001", "Design", "skipped",
                    $"Unknown pattern '{pattern}'. Known: {string.Join(", ", FormPatternCatalog.KnownPatternNames())}."));
            }
            else
            {
                if (spec is null || spec.Id != requested.Id)
                {
                    SetChild(design, "Pattern", requested.XmlName);
                    changes.Add(new(declared is null ? "FP010" : "FP001", "Design", "set-pattern",
                        $"Declared pattern {requested.XmlName} on Design."));
                }
                spec = requested;
            }
        }

        if (spec is null)
        {
            skipped.Add(new(declared is null ? "FP010" : "FP001", "Design", "skipped",
                declared is null
                    ? "Form declares no <Pattern> and no pattern was supplied — pass a pattern to adopt one."
                    : $"Declared pattern '{declared}' is not in the catalog; pass a known pattern to re-declare it."));
            return Finish(doc, changes, skipped, before);
        }

        // ---- PatternVersion (FP002) ----
        var newestVersion = spec.Versions[0];
        var versionEl = FormDesignWalker.Child(design, "PatternVersion");
        var declaredVersion = versionEl?.Value.Trim();
        if (declaredVersion is null || !spec.Versions.Contains(declaredVersion))
        {
            SetChild(design, "PatternVersion", newestVersion);
            changes.Add(new("FP002", "Design", "set-version",
                $"Set PatternVersion to {newestVersion}" +
                (declaredVersion is null ? " (was undeclared)." : $" (was {declaredVersion}).")));
        }

        // ---- Design properties (FP009) ----
        foreach (var (prop, expected) in spec.DesignProperties ?? new Dictionary<string, string>())
        {
            var current = FormDesignWalker.Child(design, prop)?.Value.Trim();
            if (current == expected) continue;
            SetChild(design, prop, expected);
            changes.Add(new("FP009", "Design", "set-property",
                $"Set Design.{prop} to \"{expected}\"" + (current is null ? "." : $" (was \"{current}\").")));
        }

        // ---- Structure (FP003 / FP005 / FP006 / FP009 on controls) ----
        var controls = FormDesignWalker.Child(design, "Controls");
        if (controls is null)
        {
            controls = new XElement(XNamespace.None + "Controls");
            design.Add(controls);
            changes.Add(new("FP003", "Design", "added", "Added the empty <Controls> collection."));
        }

        RepairContainer(controls, spec.Root, spec.ExtraRoot, "Design", spec.Id, changes, skipped);

        // The validator checks declared sub-patterns across the *whole* tree, not just
        // the slots the top-level spec reaches (ValidateSubPatternsDeep). Repair has to
        // match that reach or a container deep inside a TabPage keeps failing FP003.
        RepairSubPatternsDeep(controls, "Design", spec.Id, changes, skipped);

        return Finish(doc, changes, skipped, before);
    }

    /// <summary>
    /// Walk every control and, wherever one declares a sub-pattern, repair its children
    /// against that <see cref="SubPatternSpec"/>'s own required tree.
    /// </summary>
    private static void RepairSubPatternsDeep(
        XElement container,
        string path,
        string parentPatternId,
        List<FormRepairChange> changes,
        List<FormRepairChange> skipped)
    {
        foreach (var el in container.Elements().Where(e => e.Name.LocalName == "AxFormControl").ToList())
        {
            var childPath = $"{path}/{TypeOf(el)}[{NameOf(el)}]";
            var declared = FormDesignWalker.Child(el, "Pattern")?.Value.Trim();
            var childControls = FormDesignWalker.Child(el, "Controls");

            if (!string.IsNullOrEmpty(declared))
            {
                var sub = FormPatternCatalog.ResolveSubPattern(declared);
                if (sub is null)
                {
                    skipped.Add(new("FP001", childPath, "skipped",
                        $"Unknown sub-pattern \"{declared}\" — renaming it is a design decision."));
                }
                else if (!sub.AppliesToControlTypes.Contains(TypeOf(el), StringComparer.OrdinalIgnoreCase))
                {
                    skipped.Add(new("FP007", childPath, "skipped",
                        $"Sub-pattern {declared} does not apply to a {TypeOf(el)} — change the sub-pattern or the control type yourself."));
                }
                else
                {
                    var version = FormDesignWalker.Child(el, "PatternVersion")?.Value.Trim();
                    if (version is null || !sub.Versions.Contains(version))
                    {
                        SetChild(el, "PatternVersion", sub.Versions[0]);
                        changes.Add(new("FP002", childPath, "set-version",
                            $"Set PatternVersion to {sub.Versions[0]} for sub-pattern {sub.XmlName}."));
                    }

                    if (sub.Root.Count > 0)
                    {
                        if (childControls is null)
                        {
                            childControls = new XElement(XNamespace.None + "Controls");
                            var trailing = el.Elements().FirstOrDefault(e => IsTrailingProperty(e.Name.LocalName));
                            if (trailing is not null) trailing.AddBeforeSelf(childControls); else el.Add(childControls);
                            changes.Add(new("FP003", childPath, "added", "Added the missing <Controls> collection."));
                        }
                        RepairContainer(childControls, sub.Root, sub.ExtraRoot, childPath, parentPatternId, changes, skipped);
                    }
                }
            }

            if (childControls is not null)
                RepairSubPatternsDeep(childControls, childPath, parentPatternId, changes, skipped);
        }
    }

    /// <summary>
    /// Match the children of <paramref name="container"/> against <paramref name="specs"/>
    /// with the same type-only rule the validator uses, insert what is missing, put the
    /// matched children back in spec order, and recurse.
    /// </summary>
    private static void RepairContainer(
        XElement container,
        IReadOnlyList<NodeSpec> specs,
        ExtraChildren extra,
        string path,
        string parentPatternId,
        List<FormRepairChange> changes,
        List<FormRepairChange> skipped)
    {
        var actual = container.Elements().Where(e => e.Name.LocalName == "AxFormControl").ToList();
        var consumed = new HashSet<XElement>();
        var matches = new List<(NodeSpec Spec, List<XElement> Elements)>();

        foreach (var spec in specs)
        {
            var hits = new List<XElement>();
            foreach (var el in actual)
            {
                if (consumed.Contains(el)) continue;
                if (!TypeMatches(el, spec.ControlTypes)) continue;
                hits.Add(el);
                if (spec.Occurrence is Occurrence.Required or Occurrence.Optional) break;
            }

            if (hits.Count == 0)
            {
                if (spec.Occurrence is Occurrence.Required or Occurrence.OneOrMore)
                {
                    var created = FormControlFactory.CreateForSpec(spec);
                    container.Add(created);
                    hits.Add(created);
                    changes.Add(new("FP003", path, "added",
                        $"Added required {spec.ControlTypes[0]} \"{NameOf(created)}\" ({spec.Id})."));
                }
                else
                {
                    continue;
                }
            }

            foreach (var el in hits) consumed.Add(el);
            matches.Add((spec, hits));
        }

        // FP005 — reorder. Spec'd controls go first, in spec order; unspec'd extras keep
        // their relative order after them, so nothing a user wrote is lost or shuffled.
        var desired = matches.SelectMany(m => m.Elements).ToList();
        desired.AddRange(container.Elements()
            .Where(e => e.Name.LocalName == "AxFormControl" && !consumed.Contains(e)));

        var currentOrder = container.Elements().Where(e => e.Name.LocalName == "AxFormControl").ToList();
        if (!currentOrder.SequenceEqual(desired))
        {
            foreach (var el in currentOrder) el.Remove();
            foreach (var el in desired) container.Add(el);
            changes.Add(new("FP005", path, "reordered",
                $"Reordered controls under {path}: {string.Join(" → ", desired.Select(NameOf))}."));
        }

        // FP004 is never auto-fixed — report the extras we are leaving alone.
        foreach (var el in desired.Where(e => !consumed.Contains(e)))
        {
            var type = TypeOf(el);
            if (extra.Allows(type)) continue;
            skipped.Add(new("FP004", $"{path}/{type}[{NameOf(el)}]", "skipped",
                $"Control \"{NameOf(el)}\" ({type}) is not allowed here, but removing a control is never automatic — delete or move it yourself."));
        }

        // Per-slot repairs: sub-patterns, properties, and nested required children.
        foreach (var (spec, elements) in matches)
        {
            foreach (var el in elements)
            {
                var childPath = $"{path}/{TypeOf(el)}[{NameOf(el)}]";
                RepairNode(el, spec, childPath, parentPatternId, changes, skipped);
            }
        }
    }

    private static void RepairNode(
        XElement el,
        NodeSpec spec,
        string path,
        string parentPatternId,
        List<FormRepairChange> changes,
        List<FormRepairChange> skipped)
    {
        // FP009 — property defaults declared by the spec.
        foreach (var (prop, expected) in spec.Properties ?? new Dictionary<string, string>())
        {
            var current = FormDesignWalker.Child(el, prop)?.Value.Trim();
            if (current == expected) continue;
            SetChild(el, prop, expected);
            changes.Add(new("FP009", path, "set-property",
                $"Set {prop} to \"{expected}\"" + (current is null ? "." : $" (was \"{current}\").")));
        }

        // FP006 — a container that must declare a sub-pattern.
        if (spec.RequiresSubPattern && string.IsNullOrWhiteSpace(FormDesignWalker.Child(el, "Pattern")?.Value))
        {
            var allowed = spec.AllowedSubPatterns;
            if (allowed is null || allowed.Count == 0)
            {
                var controlType = TypeOf(el);
                var candidates = FormPatternCatalog.SubPatternsFor(controlType, parentPatternId);
                allowed = candidates.Select(c => c.XmlName).ToList();
            }

            if (allowed.Count == 1)
            {
                var sub = FormPatternCatalog.ResolveSubPattern(allowed[0]);
                SetChild(el, "Pattern", sub?.XmlName ?? allowed[0]);
                SetChild(el, "PatternVersion", sub?.Versions.FirstOrDefault() ?? "1.0");
                changes.Add(new("FP006", path, "set-sub-pattern", $"Applied sub-pattern {allowed[0]}."));
            }
            else
            {
                skipped.Add(new("FP006", path, "skipped",
                    allowed.Count == 0
                        ? "Container requires a sub-pattern but the catalog offers none for this control type."
                        : $"Container requires a sub-pattern; {allowed.Count} apply ({string.Join(", ", allowed)}) — pick one, it is a design decision."));
            }
        }

        // Recurse into spec'd children.
        if (spec.Children is not { Count: > 0 }) return;

        var childControls = FormDesignWalker.Child(el, "Controls");
        if (childControls is null)
        {
            childControls = new XElement(XNamespace.None + "Controls");
            // <Controls> sits before the trailing layout properties on a container.
            var trailing = el.Elements().FirstOrDefault(e => IsTrailingProperty(e.Name.LocalName));
            if (trailing is not null) trailing.AddBeforeSelf(childControls); else el.Add(childControls);
            changes.Add(new("FP003", path, "added", "Added the missing <Controls> collection."));
        }

        RepairContainer(childControls, spec.Children, spec.Extra, path, parentPatternId, changes, skipped);
    }

    private static FormRepairResult Finish(
        XDocument doc, List<FormRepairChange> changes, List<FormRepairChange> skipped, FormPatternReport before)
    {
        var xml = doc.Declaration is null
            ? doc.ToString()
            : doc.Declaration + Environment.NewLine + doc.ToString();
        var after = FormPatternValidator.ValidateXml(xml);
        return new FormRepairResult { Xml = xml, Changes = changes, Skipped = skipped, Before = before, After = after };
    }

    /// <summary>Normalized control type of a control element — the same resolution the walker does.</summary>
    private static string TypeOf(XElement el)
    {
        var fromAxType = FormDesignWalker.NormalizeControlType((string?)el.Attribute(Xsi + "type"));
        if (fromAxType.Length > 0) return fromAxType;

        var typeEl = FormDesignWalker.Child(el, "Type");
        if (typeEl is not null && !string.IsNullOrWhiteSpace(typeEl.Value)) return typeEl.Value.Trim();

        var ext = FormDesignWalker.Child(el, "FormControlExtension");
        if (ext is not null && (string?)ext.Attribute(Xsi + "nil") != "true")
        {
            var extName = FormDesignWalker.Child(ext, "Name")?.Value.Trim();
            if (!string.IsNullOrEmpty(extName)) return extName;
        }
        return "Control";
    }

    private static string NameOf(XElement el) => FormDesignWalker.Child(el, "Name")?.Value.Trim() ?? "Unknown";

    private static bool TypeMatches(XElement el, IReadOnlyList<string> allowed)
    {
        if (allowed.Contains("*")) return true;
        var type = TypeOf(el);
        return allowed.Any(a => string.Equals(a, type, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsTrailingProperty(string localName) => localName is
        "AlignChild" or "AlignChildren" or "ArrangeMethod" or "FrameType" or "Style" or
        "ViewEditMode" or "DataGroup" or "DataSource" or "MultiSelect" or "ShowRowLabels" or
        "AlternateRowShading" or "Columns" or "HeightMode" or "WidthMode";

    /// <summary>
    /// Set a simple child element's text, preserving the namespace of the existing
    /// element or of its siblings — Design children can sit in either the AxForm default
    /// namespace or the <c>xmlns=""</c> reset, and mixing them corrupts the document.
    /// </summary>
    private static void SetChild(XElement parent, string localName, string value)
    {
        var existing = FormDesignWalker.Child(parent, localName);
        if (existing is not null)
        {
            existing.SetValue(value);
            return;
        }

        var ns = parent.Elements().FirstOrDefault()?.Name.Namespace ?? XNamespace.None;
        var created = new XElement(ns + localName, value);

        // Keep <Controls> last-ish: insert new properties before it when present.
        var controls = FormDesignWalker.Child(parent, "Controls");
        if (controls is not null) controls.AddBeforeSelf(created); else parent.Add(created);
    }
}
