namespace D365FO.Core.FormPatterns;

/// <summary>
/// D365FO Form Pattern Catalog — data model. Port of the upstream MCP
/// <c>src/knowledge/formPatterns/types.ts</c>.
///
/// The catalog encodes, as data, what the Visual Studio form-pattern engine
/// enforces: which containers a pattern requires, in which order, what may
/// appear inside them, and which sub-patterns apply to which containers.
///
/// Sources of truth:
///   - Microsoft Learn per-pattern guideline docs
///     (https://learn.microsoft.com/en-us/dynamics365/fin-ops-core/dev-itpro/user-interface/form-styles-patterns)
///   - Reference forms in PackagesLocalDirectory (CustGroup, CustTable, SalesTable, …)
///
/// Control types are normalized i:type values: 'AxFormGridControl' → 'Grid'.
/// Extension controls resolve to their FormControlExtension name
/// (e.g. 'QuickFilterControl').
/// </summary>
public enum Occurrence
{
    Required,
    Optional,
    OneOrMore,
    ZeroOrMore,
}

/// <summary>
/// What may appear in a container beyond the explicitly spec'd children.
/// <c>Any</c> = anything; <c>None</c> = nothing; otherwise the listed
/// normalized control types ('*' allows everything).
/// </summary>
public sealed class ExtraChildren
{
    public static readonly ExtraChildren Any = new(null);
    public static readonly ExtraChildren None = new(Array.Empty<string>());

    public IReadOnlyList<string>? Types { get; }

    private ExtraChildren(IReadOnlyList<string>? types) => Types = types;

    public static ExtraChildren Of(params string[] types) => new(types);
    public static ExtraChildren Of(IEnumerable<string> types) => new(types.ToArray());

    public bool IsAny => Types is null;
    public bool IsNone => Types is { Count: 0 };

    public bool Allows(string controlType) =>
        Types is null || Types.Contains("*") || Types.Contains(controlType);
}

/// <summary>One slot of a pattern's required/optional control tree.</summary>
public sealed class NodeSpec
{
    /// <summary>Stable id for diagnostics, e.g. 'ActionPane', 'FastTabs'.</summary>
    public required string Id { get; init; }

    /// <summary>Allowed normalized control types at this slot ('*' = any).</summary>
    public required IReadOnlyList<string> ControlTypes { get; init; }

    public Occurrence Occurrence { get; init; } = Occurrence.Required;

    /// <summary>Conventional control name — diagnostics only, never used for matching.</summary>
    public string? NameHint { get; init; }

    /// <summary>Properties the pattern expects on this node — mismatches warn (FP009).</summary>
    public IReadOnlyDictionary<string, string>? Properties { get; init; }

    /// <summary>Container must declare a sub-pattern (missing one warns FP006).</summary>
    public bool RequiresSubPattern { get; init; }

    /// <summary>Sub-pattern xmlNames valid here; empty/null = any known sub-pattern.</summary>
    public IReadOnlyList<string>? AllowedSubPatterns { get; init; }

    /// <summary>Explicitly spec'd children, in required order.</summary>
    public IReadOnlyList<NodeSpec>? Children { get; init; }

    /// <summary>Whether the spec'd children must appear in spec order (default true).</summary>
    public bool ChildrenOrdered { get; init; } = true;

    /// <summary>Children allowed beyond the spec'd ones (default Any — anti-false-positive posture).</summary>
    public ExtraChildren Extra { get; init; } = ExtraChildren.Any;
}

/// <summary>Spec of a top-level form pattern (declared on Design).</summary>
public sealed record FormPatternSpec
{
    /// <summary>Catalog id (PascalCase, unique).</summary>
    public required string Id { get; init; }

    /// <summary>Exact &lt;Pattern&gt; string serialized in form XML.</summary>
    public required string XmlName { get; init; }

    /// <summary>
    /// Alternative &lt;Pattern&gt; spellings that resolve to this spec — used for
    /// entries whose exact serialized name is not yet confirmed by mining.
    /// </summary>
    public IReadOnlyList<string>? XmlAliases { get; init; }

    /// <summary>Parent pattern id when this entry is a variant (e.g. DropDialog → Dialog).</summary>
    public string? VariantOf { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>Known PatternVersion strings, newest first.</summary>
    public required IReadOnlyList<string> Versions { get; init; }

    public required string Purpose { get; init; }

    public IReadOnlyList<string> WhenToUse { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string>? WhenNotToUse { get; init; }

    /// <summary>Microsoft reference forms that use this pattern (for cloning).</summary>
    public IReadOnlyList<string> ReferenceForms { get; init; } = Array.Empty<string>();

    /// <summary>Expected Design-level properties (e.g. Style) — mismatches warn FP009.</summary>
    public IReadOnlyDictionary<string, string>? DesignProperties { get; init; }

    /// <summary>Datasource expectation: 'none' / 'one' / 'headerLines' (≥2).</summary>
    public string? RequiresDataSource { get; init; }

    /// <summary>Required tree directly under Design, in required order.</summary>
    public required IReadOnlyList<NodeSpec> Root { get; init; }

    /// <summary>Children allowed directly under Design beyond Root (default None for strict patterns).</summary>
    public ExtraChildren ExtraRoot { get; init; } = ExtraChildren.None;

    /// <summary>FormRun/datasource lifecycle guidance for this pattern.</summary>
    public IReadOnlyList<string>? LifecycleGuidance { get; init; }

    /// <summary>Caveats: legacy status, namespace quirks, mining uncertainty, …</summary>
    public IReadOnlyList<string>? Notes { get; init; }
}

/// <summary>Spec of a container sub-pattern (declared on a Group/TabPage/Tab control).</summary>
public sealed class SubPatternSpec
{
    public required string Id { get; init; }
    public required string XmlName { get; init; }
    public IReadOnlyList<string>? XmlAliases { get; init; }
    public required string DisplayName { get; init; }

    /// <summary>Known PatternVersion strings, newest first.</summary>
    public required IReadOnlyList<string> Versions { get; init; }

    /// <summary>Container control types this sub-pattern can be applied to.</summary>
    public required IReadOnlyList<string> AppliesToControlTypes { get; init; }

    /// <summary>Restrict to specific top-level pattern ids (e.g. workspace section sub-patterns).</summary>
    public IReadOnlyList<string>? ParentPatterns { get; init; }

    public required string Purpose { get; init; }

    /// <summary>Reference form (and container) examples, e.g. 'CustTable (CustomFilterGroup)'.</summary>
    public IReadOnlyList<string>? ReferenceForms { get; init; }

    /// <summary>Required children of the container, in required order.</summary>
    public required IReadOnlyList<NodeSpec> Root { get; init; }

    /// <summary>Children allowed beyond Root (default Any).</summary>
    public ExtraChildren ExtraRoot { get; init; } = ExtraChildren.Any;

    public IReadOnlyList<string>? Notes { get; init; }
}
