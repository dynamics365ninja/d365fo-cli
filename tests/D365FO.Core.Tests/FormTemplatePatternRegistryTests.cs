using System.Text.RegularExpressions;
using System.Xml.Linq;
using D365FO.Core.FormPatterns;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// Pins every form template's design pattern against the AOT pattern registry
/// (<see cref="FormPatternRegistry"/>, derived from
/// <c>Microsoft.Dynamics.AX.Metadata.Patterns.dll</c>).
/// </summary>
/// <remarks>
/// <para>
/// Naming a pattern the AOS does not have is unambiguous breakage — the form fails
/// with "Unable to validate pattern 'X'. Message: Pattern 'X' not found" — and until
/// now nothing but a VM could see it. <c>eval verify-build</c> found five templates
/// doing exactly that.
/// </para>
/// <para>
/// The five are listed in <see cref="KnownWrong"/> rather than silently tolerated:
/// each needs its template restructured to satisfy the real pattern's required parts,
/// and its entry removed here when that lands. Shrinking this list is the work item;
/// adding to it is a regression.
/// </para>
/// </remarks>
public class FormTemplatePatternRegistryTests
{
    /// <summary>
    /// Templates whose design pattern is not in the registry. Every entry is a known
    /// defect with the real pattern named — not an exemption.
    /// </summary>
    private static readonly Dictionary<string, string> KnownWrong = new(StringComparer.Ordinal)
    {
        ["DetailsMaster 1.1"] = "DetailsMaster exists; 1.1 does not. Newest is 1.4, which wants NavigationList(SidePanel) + Details/Overview tab pages",
        ["DetailsTransaction 1.1"] = "DetailsTransaction exists; 1.1 does not. Newest is 1.4",
        ["ListPage 1.1"] = "ListPage exists with exactly one version, the string 'UX7 1.0' — only the version is wrong",
        ["Lookup 1.2"] = "there is no pattern named 'Lookup' at all: LookupGridOnly 1.1, LookupTab 1.0, LookupPreview 1.0",
        ["Workspace 1.0"] = "Workspace exists only as an inactive 2.0; the active pattern is WorkspaceOperational 1.1",
    };

    public static IEnumerable<object[]> Templates =>
        FormTemplateResources.Names.Select(n => new object[] { n });

    [Theory]
    [MemberData(nameof(Templates))]
    public void Design_pattern_exists_in_the_AOT_registry(string templateName)
    {
        var (pattern, version) = DesignPattern(templateName);
        if (pattern is null) return; // TableOfContents-style templates render it via a placeholder

        var key = $"{pattern} {version}";
        if (KnownWrong.ContainsKey(key)) return;

        Assert.True(
            FormPatternRegistry.Exists(pattern, version!),
            $"{templateName} declares pattern '{key}', which the AOT registry does not have. " +
            $"Active versions of '{pattern}': {string.Join(", ", FormPatternRegistry.VersionsOf(pattern))}. " +
            "Either fix the template or add it to KnownWrong with the real pattern named.");
    }

    /// <summary>An entry that starts resolving is a fix waiting to be recorded — remove it from the list.</summary>
    [Fact]
    public void The_known_wrong_list_contains_nothing_that_already_resolves()
    {
        foreach (var (key, _) in KnownWrong)
        {
            var split = key.LastIndexOf(' ');
            var name = key[..split];
            var version = key[(split + 1)..];

            Assert.False(
                FormPatternRegistry.Exists(name, version),
                $"'{key}' resolves against the registry now — drop it from KnownWrong.");
        }
    }

    [Fact]
    public void The_registry_loaded_and_knows_the_patterns_the_templates_already_get_right()
    {
        Assert.NotEmpty(FormPatternRegistry.All);

        // The three the audit's census confirmed our templates already name correctly.
        Assert.True(FormPatternRegistry.Exists("SimpleList", "1.1"));
        Assert.True(FormPatternRegistry.Exists("Dialog", "1.2"));
        Assert.True(FormPatternRegistry.Exists("TableOfContents", "1.1"));

        // And the alias is what the AOS puts in its error messages.
        Assert.Equal("Details Master", FormPatternRegistry.Find("DetailsMaster", "1.4")!.Alias);
    }

    /// <summary>
    /// A pattern carries two version lineages — plain numbers and an older series
    /// whose version string is literally "UX7 1.0". Sorting the strings would make
    /// UX7 1.2 look newer than 1.4 and pin every migrated template to the legacy
    /// lineage.
    /// </summary>
    [Fact]
    public void Numeric_versions_rank_above_the_UX7_lineage()
    {
        Assert.Equal("1.4", FormPatternRegistry.VersionsOf("DetailsMaster")[0]);
        Assert.Equal("1.1", FormPatternRegistry.VersionsOf("SimpleList")[0]);

        // ListPage has only the legacy lineage, so that is legitimately its newest.
        Assert.Equal(["UX7 1.0"], FormPatternRegistry.VersionsOf("ListPage"));
    }

    /// <summary>
    /// The structural half of a migrated pattern comes from the registry, not from the
    /// hand-written entry — the thing that had drifted.
    /// </summary>
    [Fact]
    public void A_migrated_pattern_takes_its_structure_from_the_registry()
    {
        var simpleList = FormPatternCatalog.Patterns.Single(p => p.Id == "SimpleList");

        Assert.Equal(FormPatternRegistry.VersionsOf("SimpleList"), simpleList.Versions);
        Assert.Equal(
            RegistrySpecFactory.Root("SimpleList")!.Select(n => n.Id),
            simpleList.Root.Select(n => n.Id));

        // …including the parts the hand-written entry did not have: the AOS declares an
        // optional Footer group, and makes the custom-filter group required.
        Assert.Contains(simpleList.Root, n => n.Id == "Footer" && n.Occurrence == Occurrence.Optional);
        Assert.Contains(simpleList.Root, n => n.Id == "CustomFilterGroup" && n.Occurrence == Occurrence.Required);
    }

    private static (string? Pattern, string? Version) DesignPattern(string templateName)
    {
        var xml = FormTemplateResources.Read(templateName);

        // The design's own Pattern/PatternVersion are the first pair in the file that
        // sits directly under <Design>; control patterns come later and are nested.
        var m = Regex.Match(
            xml,
            @"<Design>.*?<Pattern xmlns="""">([^<]+)</Pattern>\s*<PatternVersion xmlns="""">([^<]+)</PatternVersion>",
            RegexOptions.Singleline);

        return m.Success ? (m.Groups[1].Value.Trim(), m.Groups[2].Value.Trim()) : (null, null);
    }
}

/// <summary>The embedded form templates, by resource name.</summary>
internal static class FormTemplateResources
{
    private const string Prefix = "D365FO.Core.Scaffolding.FormTemplates.";

    public static IReadOnlyList<string> Names =>
        typeof(FormPatternRegistry).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith(".template.xml", StringComparison.Ordinal))
            .Select(n => n[Prefix.Length..])
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    public static string Read(string shortName)
    {
        using var stream = typeof(FormPatternRegistry).Assembly.GetManifestResourceStream(Prefix + shortName)
            ?? throw new InvalidOperationException($"Template '{shortName}' not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
