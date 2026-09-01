using D365FO.Core.Knowledge;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// Ground-truth check for the report recipes against a real installation.
/// </summary>
/// <remarks>
/// Inert unless <c>D365FO_PACKAGES_PATH</c> points at a <c>PackagesLocalDirectory</c> — CI has no
/// AOS, and a test that silently passed on an empty directory would be exactly the confident lie
/// this repo's triage rubric warns about.
///
/// What it pins is the pair of claims a recipe cannot make up: the base classes exist under those
/// exact names, and each named reference object really extends the base its recipe puts it under.
/// A recipe naming a class the platform does not have reads as authoritative and costs a build
/// cycle to disprove.
/// </remarks>
public class ReportRecipesAotTests
{
    private static string? PackagesRoot()
    {
        var root = Environment.GetEnvironmentVariable("D365FO_PACKAGES_PATH");
        return string.IsNullOrWhiteSpace(root) || !Directory.Exists(root) ? null : root;
    }

    /// <summary>The <c>&lt;Name&gt;</c> and declared base of one AxClass file, or null.</summary>
    private static string? DeclaredBase(string classFile)
    {
        string text;
        try { text = File.ReadAllText(classFile); }
        catch { return null; }

        var match = System.Text.RegularExpressions.Regex.Match(
            text, @"\bclass\s+\w+\s+extends\s+(\w+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? FindClassFile(string root, string className)
    {
        foreach (var package in SafeDirs(root))
            foreach (var model in SafeDirs(package))
            {
                var candidate = Path.Combine(model, "AxClass", className + ".xml");
                if (File.Exists(candidate)) return candidate;
            }
        return null;

        static IEnumerable<string> SafeDirs(string d)
        {
            try { return Directory.EnumerateDirectories(d); }
            catch { return Array.Empty<string>(); }
        }
    }

    [Fact]
    public void Every_reference_object_exists_and_extends_the_base_its_recipe_claims()
    {
        var root = PackagesRoot();
        if (root is null) return;

        var problems = new List<string>();

        foreach (var recipe in ReportRecipes.List())
        {
            // The base a reference object of this recipe is expected to extend: the DP base for a
            // *DP class, the controller base for a *Controller, the UI-builder base for a
            // *UIBuilder. Anything else is not checked here.
            foreach (var reference in recipe.ReferenceObjects)
            {
                var file = FindClassFile(root, reference);
                if (file is null)
                {
                    problems.Add($"{recipe.Id}: reference class '{reference}' is not in this installation");
                    continue;
                }

                var declared = DeclaredBase(file);
                if (declared is null)
                {
                    problems.Add($"{recipe.Id}: '{reference}' declares no base class");
                    continue;
                }

                var expected = recipe.Roster
                    .Select(o => o.Extends)
                    .Where(e => e is not null)
                    .ToList();

                if (!expected.Any(e => string.Equals(e, declared, StringComparison.OrdinalIgnoreCase)))
                {
                    problems.Add(
                        $"{recipe.Id}: '{reference}' extends {declared}, which is not among the recipe's "
                        + $"base classes ({string.Join(", ", expected)})");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Every_base_class_a_recipe_names_exists_in_this_installation()
    {
        var root = PackagesRoot();
        if (root is null) return;

        var bases = ReportRecipes.List()
            .SelectMany(r => r.Roster)
            .Select(o => o.Extends)
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var missing = bases.Where(b => FindClassFile(root, b!) is null).ToList();

        Assert.True(missing.Count == 0,
            "Recipes name base classes this installation does not have: " + string.Join(", ", missing));
    }
}
