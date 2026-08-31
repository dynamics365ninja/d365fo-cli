using System.Xml.Linq;

namespace D365FO.Core.FormPatterns;

/// <summary>One validator finding.</summary>
public sealed record FormPatternViolation(
    string Rule,
    string Severity, // "error" | "warning"
    string Path,     // tree path, e.g. 'Design/Tab[TabHeader]/TabPage[General]'
    string Excerpt,
    string Fix);

/// <summary>Validator output for one AxForm document.</summary>
public sealed class FormPatternReport
{
    public string? FormName { get; init; }
    public string? Pattern { get; init; }
    public string? PatternVersion { get; init; }
    public required IReadOnlyList<FormPatternViolation> Violations { get; init; }
    public int ContainersTotal { get; init; }
    public int ContainersPatterned { get; init; }

    public bool HasErrors => Violations.Any(v => v.Severity == "error");
    public int ErrorCount => Violations.Count(v => v.Severity == "error");
    public int WarningCount => Violations.Count(v => v.Severity == "warning");
}

/// <summary>
/// Form Pattern Validator — pure structural validator for AxForm XML against
/// the curated <see cref="FormPatternCatalog"/>. Port of the upstream MCP
/// <c>src/validation/formPatternValidator.ts</c>.
///
/// Rules:
///   FP001 (error)   unknown &lt;Pattern&gt; on Design / unknown sub-pattern on a container
///   FP002 (error)   unknown PatternVersion for a known pattern
///         (warning) known-but-older version, or version newer than catalog (PU drift)
///   FP003 (error)   required node missing (e.g. SimpleList without a Grid)
///   FP004 (error)   child control type not allowed in a patterned container
///   FP005 (error)   required children out of order (e.g. Grid before ActionPane)
///   FP006 (warning) container that requires a sub-pattern has none ("unspecified")
///   FP007 (error)   sub-pattern applied to an unsupported control type / parent pattern,
///                   or not allowed at this slot of the parent pattern
///   FP008 (warning) datasource expectation unmet (count / TitleDataSource)
///   FP009 (warning) Design/control property differs from the pattern default
///   FP010 (warning) no &lt;Pattern&gt; declared on Design at all
///
/// Severity policy: only structural rules (FP001-FP005, FP007) are errors and
/// may block writes; the rest are recommendations.
/// </summary>
public static class FormPatternValidator
{
    private static readonly HashSet<string> ContainerTypes = new() { "Group", "TabPage", "Tab" };

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string NodePath(string parentPath, FormControlNode node)
        => $"{parentPath}/{node.Type}[{node.Name}]";

    private static bool TypeMatches(FormControlNode node, IReadOnlyList<string> allowed)
        => allowed.Contains("*") || allowed.Contains(node.Type);

    private static int CompareVersions(string a, string b)
    {
        var pa = a.Split('.');
        var pb = b.Split('.');
        for (var i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            var da = i < pa.Length && int.TryParse(pa[i], out var na) ? na : 0;
            var db = i < pb.Length && int.TryParse(pb[i], out var nb) ? nb : 0;
            if (da != db) return da - db;
        }
        return 0;
    }

    private static void CheckVersion(
        string? declared,
        IReadOnlyList<string> knownVersions,
        string patternLabel,
        string path,
        List<FormPatternViolation> violations,
        string? registryPatternName = null)
    {
        if (string.IsNullOrEmpty(declared))
        {
            violations.Add(new("FP002", "warning", path,
                $"{patternLabel}: no PatternVersion declared",
                $"Add <PatternVersion>{knownVersions[0]}</PatternVersion>."));
            return;
        }
        if (knownVersions.Contains(declared))
        {
            if (declared != knownVersions[0])
                violations.Add(new("FP002", "warning", path,
                    $"{patternLabel}: version {declared} is older than current {knownVersions[0]}",
                    $"Consider upgrading to PatternVersion {knownVersions[0]}."));
            return;
        }
        var newest = knownVersions[0];

        // A version the AOT registry declares ACTIVE for this pattern is not unknown, whatever
        // the catalog lists — the AOS is the authority on which versions exist, and it is the
        // AOS that rejects the form at build time. FormPartFactboxCard is the live example:
        // the only version the platform accepts is "UX7 1.0", which the catalog does not list,
        // so blocking on it would refuse the one spelling that compiles.
        if (registryPatternName is not null &&
            FormPatternRegistry.Exists(registryPatternName, declared))
        {
            violations.Add(new("FP002", "warning", path,
                $"{patternLabel}: version {declared} is not in the catalog but IS an active AOT pattern version",
                $"Catalog drift — add {declared} to the catalog's versions list after verifying."));
            return;
        }

        if (CompareVersions(declared, newest) > 0)
        {
            // Likely platform-update drift — the catalog lags behind, don't block
            violations.Add(new("FP002", "warning", path,
                $"{patternLabel}: version {declared} is newer than the catalog's {newest}",
                "Probably a newer platform pattern version — update the catalog (versions list) after verifying."));
        }
        else
        {
            violations.Add(new("FP002", "error", path,
                $"{patternLabel}: unknown version \"{declared}\" (known: {string.Join(", ", knownVersions)})",
                $"Use a known PatternVersion, typically {newest}."));
        }
    }

    // ── Child matching ───────────────────────────────────────────────────────

    private sealed record MatchedPair(NodeSpec Spec, FormControlNode Node, int ActualIndex);

    /// <summary>
    /// Match a container's actual children against the spec'd children.
    /// Emits FP003 (missing), FP004 (extra not allowed), FP005 (order) and
    /// returns matched pairs for recursion.
    /// </summary>
    private static List<MatchedPair> MatchChildren(
        IReadOnlyList<FormControlNode> actual,
        IReadOnlyList<NodeSpec> specs,
        ExtraChildren extra,
        bool ordered,
        string parentPath,
        string patternLabel,
        List<FormPatternViolation> violations)
    {
        var consumed = new bool[actual.Count];
        var matched = new List<MatchedPair>();
        // first actual index matched by each spec, in spec order (for FP005)
        var orderProbe = new List<(string SpecId, int FirstIndex)>();

        foreach (var spec in specs)
        {
            var indices = new List<int>();
            for (var i = 0; i < actual.Count; i++)
            {
                if (consumed[i]) continue;
                if (!TypeMatches(actual[i], spec.ControlTypes)) continue;
                indices.Add(i);
                if (spec.Occurrence is Occurrence.Required or Occurrence.Optional) break;
            }

            if (indices.Count == 0)
            {
                if (spec.Occurrence is Occurrence.Required or Occurrence.OneOrMore)
                {
                    var hint = spec.NameHint is not null ? $" (conventionally named {spec.NameHint})" : "";
                    violations.Add(new("FP003", "error", parentPath,
                        $"{patternLabel}: required {string.Join("/", spec.ControlTypes)} (\"{spec.Id}\") is missing",
                        $"Add a {spec.ControlTypes[0]} control{hint} under {parentPath}."));
                }
                continue;
            }

            foreach (var i in indices)
            {
                consumed[i] = true;
                matched.Add(new MatchedPair(spec, actual[i], i));
            }
            orderProbe.Add((spec.Id, indices.Min()));
        }

        if (ordered)
        {
            for (var i = 1; i < orderProbe.Count; i++)
            {
                if (orderProbe[i].FirstIndex < orderProbe[i - 1].FirstIndex)
                    violations.Add(new("FP005", "error", parentPath,
                        $"{patternLabel}: \"{orderProbe[i].SpecId}\" appears before \"{orderProbe[i - 1].SpecId}\" — pattern requires the opposite order",
                        $"Reorder the controls under {parentPath}: {string.Join(" → ", specs.Select(s => s.Id))}."));
            }
        }

        for (var i = 0; i < actual.Count; i++)
        {
            if (consumed[i]) continue;
            var child = actual[i];
            if (!extra.Allows(child.Type))
            {
                var fix = extra.IsNone
                    ? $"Remove or relocate \"{child.Name}\" — only [{string.Join(", ", specs.Select(s => string.Join("/", s.ControlTypes)))}] are allowed under {parentPath}."
                    : $"Move \"{child.Name}\" elsewhere — allowed extra types here: {string.Join(", ", extra.Types!)}.";
                violations.Add(new("FP004", "error", NodePath(parentPath, child),
                    $"{patternLabel}: control type \"{child.Type}\" is not allowed here", fix));
            }
        }

        return matched;
    }

    /// <summary>Recursively apply a matched spec node to its actual control.</summary>
    private static void ApplySpecToNode(
        MatchedPair pair,
        string parentPath,
        string patternLabel,
        string? topPatternId,
        List<FormPatternViolation> violations)
    {
        var (spec, node, _) = pair;
        var path = NodePath(parentPath, node);

        // FP009 — property defaults set by the pattern
        if (spec.Properties is not null)
        {
            foreach (var (prop, expected) in spec.Properties)
            {
                if (node.Properties.TryGetValue(prop, out var actualValue) && actualValue != expected)
                    violations.Add(new("FP009", "warning", path,
                        $"{patternLabel}: {prop}=\"{actualValue}\" differs from pattern default \"{expected}\"",
                        $"Set {prop} to \"{expected}\" unless the deviation is intentional."));
            }
        }

        // FP006 / FP007 — sub-pattern expectations on this slot
        if (node.Pattern is not null)
        {
            if (spec.AllowedSubPatterns is { Count: > 0 })
            {
                var declared = FormPatternCatalog.ResolveSubPattern(node.Pattern);
                var allowed = spec.AllowedSubPatterns.Any(name =>
                    string.Equals(name, node.Pattern, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(declared?.Id, name, StringComparison.OrdinalIgnoreCase));
                if (!allowed)
                    violations.Add(new("FP007", "error", path,
                        $"{patternLabel}: sub-pattern \"{node.Pattern}\" is not allowed on \"{spec.Id}\" (allowed: {string.Join(", ", spec.AllowedSubPatterns)})",
                        $"Apply one of: {string.Join(", ", spec.AllowedSubPatterns)}."));
            }
        }
        else if (spec.RequiresSubPattern)
        {
            violations.Add(new("FP006", "warning", path,
                $"{patternLabel}: container \"{node.Name}\" has no sub-pattern (unspecified)",
                spec.AllowedSubPatterns is { Count: > 0 }
                    ? $"Apply a sub-pattern: {string.Join(", ", spec.AllowedSubPatterns)}."
                    : "Apply an appropriate container sub-pattern (e.g. FieldsFieldGroups, ToolbarAndList)."));
        }

        // Recurse into spec'd children. Also runs when the spec has no explicit
        // children but restricts extras (e.g. one-level group nesting in
        // FieldsFieldGroups) — the extra-children policy must still be enforced.
        var hasChildSpecs = spec.Children is { Count: > 0 };
        var restrictsExtras = !spec.Extra.IsAny;
        if (hasChildSpecs || restrictsExtras)
        {
            var matched = MatchChildren(
                node.Children,
                spec.Children ?? Array.Empty<NodeSpec>(),
                spec.Extra,
                spec.ChildrenOrdered,
                path,
                patternLabel,
                violations);
            foreach (var childPair in matched)
                ApplySpecToNode(childPair, path, patternLabel, topPatternId, violations);
        }
    }

    /// <summary>
    /// Walk the whole tree validating every declared sub-pattern, independent
    /// of whether the top-level spec covers that container.
    /// </summary>
    private static void ValidateSubPatternsDeep(
        IReadOnlyList<FormControlNode> nodes,
        string parentPath,
        string? topPatternId,
        List<FormPatternViolation> violations)
    {
        foreach (var node in nodes)
        {
            var path = NodePath(parentPath, node);

            if (node.Pattern is not null)
            {
                var sp = FormPatternCatalog.ResolveSubPattern(node.Pattern);
                if (sp is null)
                {
                    violations.Add(new("FP001", "error", path,
                        $"Unknown sub-pattern \"{node.Pattern}\" on {node.Type} \"{node.Name}\"",
                        "Use a known container sub-pattern (e.g. FieldsFieldGroups, CustomAndQuickFilters, ToolbarAndList) or fix the spelling."));
                }
                else
                {
                    if (!sp.AppliesToControlTypes.Contains(node.Type) && !sp.AppliesToControlTypes.Contains("*"))
                        violations.Add(new("FP007", "error", path,
                            $"Sub-pattern \"{sp.XmlName}\" cannot be applied to control type \"{node.Type}\" (applies to: {string.Join(", ", sp.AppliesToControlTypes)})",
                            $"Move the sub-pattern to a {sp.AppliesToControlTypes[0]} container or choose a different sub-pattern."));

                    if (sp.ParentPatterns is not null && topPatternId is not null && !sp.ParentPatterns.Contains(topPatternId))
                        violations.Add(new("FP007", "error", path,
                            $"Sub-pattern \"{sp.XmlName}\" is only valid inside {string.Join("/", sp.ParentPatterns)} forms (this form: {topPatternId})",
                            "Choose a sub-pattern supported by this form pattern."));

                    CheckVersion(node.PatternVersion, sp.Versions, $"sub-pattern {sp.XmlName}", path, violations, sp.XmlName);

                    var matched = MatchChildren(
                        node.Children, sp.Root, sp.ExtraRoot, ordered: true, path,
                        $"sub-pattern {sp.XmlName}", violations);
                    foreach (var pair in matched)
                        ApplySpecToNode(pair, path, $"sub-pattern {sp.XmlName}", topPatternId, violations);
                }
            }

            ValidateSubPatternsDeep(node.Children, path, topPatternId, violations);
        }
    }

    private static (int Total, int Patterned) CountContainers(IReadOnlyList<FormControlNode> nodes)
    {
        int total = 0, patterned = 0;
        void Visit(FormControlNode node)
        {
            if (ContainerTypes.Contains(node.Type))
            {
                total++;
                if (node.Pattern is not null) patterned++;
            }
            foreach (var child in node.Children) Visit(child);
        }
        foreach (var node in nodes) Visit(node);
        return (total, patterned);
    }

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>Validate an already-walked design tree (used by tests and the gate).</summary>
    public static FormPatternReport ValidateTree(FormDesignInfo design, int dataSourceCount, string? formName = null)
    {
        var violations = new List<FormPatternViolation>();
        var (total, patterned) = CountContainers(design.Controls);

        FormPatternSpec? spec = null;

        if (design.Pattern is null)
        {
            violations.Add(new("FP010", "warning", "Design",
                "Form declares no <Pattern> on Design",
                $"Apply a form pattern (known: {string.Join(", ", FormPatternCatalog.KnownPatternNames())})."));
        }
        else
        {
            spec = FormPatternCatalog.ResolveExact(design.Pattern);
            if (spec is null)
                violations.Add(new("FP001", "error", "Design",
                    $"Unknown form pattern \"{design.Pattern}\"",
                    $"Use one of the known patterns: {string.Join(", ", FormPatternCatalog.KnownPatternNames())}."));
        }

        if (spec is not null)
        {
            var label = $"pattern {spec.XmlName}";
            CheckVersion(design.PatternVersion, spec.Versions, label, "Design", violations, spec.XmlName);

            // FP009 — Design-level property defaults
            if (spec.DesignProperties is not null)
            {
                foreach (var (prop, expected) in spec.DesignProperties)
                {
                    var actualValue = prop == "Style" ? design.Style : design.Properties.GetValueOrDefault(prop);
                    if (actualValue is not null && actualValue != expected)
                        violations.Add(new("FP009", "warning", "Design",
                            $"{label}: {prop}=\"{actualValue}\" differs from pattern default \"{expected}\"",
                            $"Set Design.{prop} to \"{expected}\"."));
                }
            }

            // FP008 — datasource expectations
            if (spec.RequiresDataSource == "one" && dataSourceCount < 1)
                violations.Add(new("FP008", "warning", "DataSources",
                    $"{label}: expects at least one datasource (found {dataSourceCount})",
                    "Add a primary AxFormDataSource bound to the entity table."));
            if (spec.RequiresDataSource == "headerLines" && dataSourceCount < 2)
                violations.Add(new("FP008", "warning", "DataSources",
                    $"{label}: expects header + lines datasources (found {dataSourceCount})",
                    "Add both a header datasource and a lines datasource (linked via JoinSource)."));

            // Structural matching of Design children
            var matched = MatchChildren(
                design.Controls, spec.Root, spec.ExtraRoot, ordered: true, "Design", label, violations);
            foreach (var pair in matched)
                ApplySpecToNode(pair, "Design", label, spec.Id, violations);
        }

        // Deep sub-pattern validation across the entire tree
        ValidateSubPatternsDeep(design.Controls, "Design", spec?.Id, violations);

        return new FormPatternReport
        {
            FormName = formName,
            Pattern = design.Pattern,
            PatternVersion = design.PatternVersion,
            Violations = violations,
            ContainersTotal = total,
            ContainersPatterned = patterned,
        };
    }

    /// <summary>Parse AxForm XML and validate it against the catalog.</summary>
    public static FormPatternReport ValidateXml(string xml)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (Exception ex)
        {
            return ParseFailure($"XML parse error: {ex.Message.Split('\n')[0]}",
                "Fix the XML syntax before validating the pattern.");
        }

        var axForm = doc.Root;
        if (axForm is null || axForm.Name.LocalName != "AxForm")
            return ParseFailure("Not an AxForm document (missing <AxForm> root)", "Pass complete AxForm XML.");

        var design = FormDesignWalker.Walk(FormDesignWalker.Child(axForm, "Design"));

        var dataSourceCount = 0;
        var dataSourcesNode = FormDesignWalker.Child(axForm, "DataSources");
        if (dataSourcesNode is not null)
            dataSourceCount = dataSourcesNode.Elements()
                .Count(e => e.Name.LocalName is "AxFormDataSource" or "AxFormDataSourceRoot");

        var formName = FormDesignWalker.Child(axForm, "Name")?.Value.Trim();
        return ValidateTree(design, dataSourceCount, string.IsNullOrEmpty(formName) ? null : formName);
    }

    private static FormPatternReport ParseFailure(string excerpt, string fix) => new()
    {
        Violations = new[] { new FormPatternViolation("FP000", "error", "(document)", excerpt, fix) },
    };
}
