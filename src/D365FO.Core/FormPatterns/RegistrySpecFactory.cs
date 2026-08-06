namespace D365FO.Core.FormPatterns;

/// <summary>
/// Builds the structural half of a <see cref="FormPatternSpec"/> — versions, design
/// properties and the required control tree — from the AOT pattern registry instead
/// of from a hand-written model of it.
/// </summary>
/// <remarks>
/// <para>
/// The catalog was written from Microsoft Learn prose and reference forms, and it
/// drifted: five of the nine generated patterns named a version that exists on no
/// AOS, and the structures behind the names disagreed too. Deriving them removes the
/// class of error entirely — the same move as <c>ObjectTypeRegistry</c> replacing
/// four hand-kept kind tables and <c>ContractOrderCanonicalizer</c> replacing a
/// hand-captured member order.
/// </para>
/// <para>
/// The catalog keeps everything the registry does not know: purpose, when to use it,
/// reference forms, lifecycle guidance, datasource expectations, and the sub-pattern
/// hints FP006/FP007 rely on. Those are editorial and stay hand-written.
/// </para>
/// </remarks>
public static class RegistrySpecFactory
{
    /// <summary>
    /// Registry control types that this repo's walker reports under a different name.
    /// The walker normalises <c>i:type</c> ("AxFormGridControl" → "Grid") and resolves
    /// extension controls to their extension name, which is already how the registry
    /// spells most of them.
    /// </summary>
    private static readonly Dictionary<string, string> TypeAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["FormDesign"] = "Design",
    };

    /// <summary>
    /// The newest active version of <paramref name="patternName"/>, or null when the
    /// registry has no such pattern — which is itself the finding.
    /// </summary>
    public static RegisteredFormPattern? Newest(string patternName) =>
        FormPatternRegistry.VersionsOf(patternName) is [var newest, ..]
            ? FormPatternRegistry.Find(patternName, newest)
            : null;

    /// <summary>Active versions of a pattern, newest first — what <c>FormPatternSpec.Versions</c> should say.</summary>
    public static IReadOnlyList<string> Versions(string patternName) =>
        FormPatternRegistry.VersionsOf(patternName);

    /// <summary>Design-level property values the pattern requires (<c>Style</c>, <c>ArrangeMethod</c>, …).</summary>
    public static IReadOnlyDictionary<string, string>? DesignProperties(string patternName) =>
        Newest(patternName)?.Design?.Properties is { Count: > 0 } props ? props : null;

    /// <summary>
    /// The pattern's required root controls as <see cref="NodeSpec"/>s, or null when
    /// the registry does not have the pattern.
    /// </summary>
    public static IReadOnlyList<NodeSpec>? Root(string patternName)
    {
        var design = Newest(patternName)?.Design;
        if (design is null) return null;
        return design.Children.Select(ToNodeSpec).ToList();
    }

    /// <summary>
    /// Whether anything beyond the declared parts may appear at the design root.
    /// A registry part without <c>Children="*"</c> means "these and nothing else",
    /// which is what produced "has child 'HeaderGroup' which is not allowed at its
    /// current location".
    /// </summary>
    public static ExtraChildren ExtraRoot(string patternName) =>
        Newest(patternName)?.Design?.ExtraChildrenAllowed == true ? ExtraChildren.Any : ExtraChildren.None;

    private static NodeSpec ToNodeSpec(RegisteredPart part) => new()
    {
        Id = string.IsNullOrEmpty(part.Part) ? (part.Alias ?? part.Type) : part.Part,
        // A choice slot lists its alternatives pipe-separated; NodeSpec already models
        // "one of these types" natively.
        ControlTypes = part.Type.Split('|', StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeType).ToList(),
        Occurrence = ToOccurrence(part.Count),
        // Never null: the repairer names a control it adds from the hint, and some
        // registry parts (the unnamed custom-filter group, choice slots) have no Part.
        NameHint = string.IsNullOrEmpty(part.Part)
            ? (part.Alias?.Replace(" ", "") is { Length: > 0 } alias ? alias : part.Type)
            : part.Part,
        RequiresSubPattern = part.SubPatterns.Count > 0,
        // Only names this repo's sub-pattern catalog actually knows. The registry uses
        // several our catalog spells differently (ToolbarList vs ToolbarAndList,
        // HorizontalFieldsButtonsGroup vs …ButtonGroup) or does not have at all — see
        // FormTemplatePatternRegistryTests, which lists them. Claiming an unknown name
        // is "allowed" would make FP007 accept anything.
        AllowedSubPatterns = KnownSubPatterns(part.SubPatterns),
        // Property requirements belong to a specific alternative, so a choice slot
        // carries none: demanding the Grid's AllowEdit of a Tree would be nonsense.
        Properties = !part.IsChoice && part.Properties.Count > 0 ? part.Properties : null,
        Children = part.Children.Count > 0 ? part.Children.Select(ToNodeSpec).ToList() : null,
        // The registry lists parts in the order the AOS expects them, but it does not
        // say that order is enforced — and it demonstrably is not for optional parts.
        // Leaving order unchecked keeps FP005 from inventing violations the AOS does
        // not report; missing and disallowed children (FP003/FP004) still fire.
        ChildrenOrdered = false,
        // Always permissive inside a part, and that is not a hedge — it is what the AOS
        // does. TableOfContents never writes Children="*" anywhere, yet it accepts field
        // controls on a TOC page while rejecting an ActionPane at the design root. So
        // the closed set is the design root (see ExtraRoot); inside a part, the declared
        // children are the ones that must be present, not the only ones that may be.
        Extra = ExtraChildren.Any,
    };

    /// <summary>
    /// The registry's sub-pattern names this repo's catalog can actually resolve.
    /// Returns null when none of them are known, which leaves FP006 asking for a
    /// sub-pattern without FP007 pretending to know which ones are valid.
    /// </summary>
    internal static IReadOnlyList<string>? KnownSubPatterns(IReadOnlyList<string> names)
    {
        var known = names.Where(IsKnownSubPattern).ToList();
        return known.Count > 0 ? known : null;
    }

    /// <summary>Registry sub-pattern names this repo's catalog does not have — a tracked gap.</summary>
    public static IReadOnlyList<string> UnknownSubPatternNames() =>
        FormPatternRegistry.All
            .Where(p => p.Active && p.Design is not null)
            .SelectMany(p => Flatten(p.Design!))
            .SelectMany(part => part.SubPatterns)
            .Distinct(StringComparer.Ordinal)
            .Where(n => !IsKnownSubPattern(n))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Matched against the sub-pattern array directly rather than through
    /// <c>FormPatternCatalog.ResolveSubPattern</c>: that goes via an index which is
    /// itself built from <c>Patterns</c>, and <c>Patterns</c> is what calls this.
    /// </summary>
    private static bool IsKnownSubPattern(string name) =>
        FormPatternCatalog.SubPatterns.Any(s =>
            string.Equals(s.XmlName, name, StringComparison.OrdinalIgnoreCase) ||
            (s.XmlAliases?.Contains(name, StringComparer.OrdinalIgnoreCase) ?? false));

    private static IEnumerable<RegisteredPart> Flatten(RegisteredPart part)
    {
        yield return part;
        foreach (var child in part.Children)
            foreach (var descendant in Flatten(child))
                yield return descendant;
    }

    private static string NormalizeType(string type) =>
        TypeAliases.TryGetValue(type, out var mapped) ? mapped : type;

    /// <summary><c>1</c> | <c>0..1</c> | <c>1..*</c> | <c>0..*</c> | <c>*</c> as the registry writes it.</summary>
    private static Occurrence ToOccurrence(string count) => count switch
    {
        "0..1" => Occurrence.Optional,
        "1..*" => Occurrence.OneOrMore,
        "0..*" or "*" => Occurrence.ZeroOrMore,
        _ => Occurrence.Required,
    };
}
