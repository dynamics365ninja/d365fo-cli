using D365FO.Core.Knowledge;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// Ground-truth check for the warehouse-app recipes against a real installation.
/// </summary>
/// <remarks>
/// Inert unless <c>D365FO_PACKAGES_PATH</c> points at a <c>PackagesLocalDirectory</c>. These
/// recipes are AUTHORED knowledge rather than extracted, which is exactly why the class names in
/// them need checking: a recipe naming a base class the platform does not have reads as
/// authoritative and costs a build cycle to disprove.
/// </remarks>
public class MobileAppRecipesAotTests
{
    private static string? PackagesRoot()
    {
        var root = Environment.GetEnvironmentVariable("D365FO_PACKAGES_PATH");
        return string.IsNullOrWhiteSpace(root) || !Directory.Exists(root) ? null : root;
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

    [Fact]
    public void Every_base_class_a_recipe_names_exists_in_this_installation()
    {
        var root = PackagesRoot();
        if (root is null) return;

        var bases = MobileAppRecipes.List()
            .SelectMany(r => r.Roster)
            .Select(o => o.Extends)
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var missing = bases.Where(b => FindClassFile(root, b!) is null).ToList();

        Assert.True(missing.Count == 0,
            "Recipes name base classes this installation does not have: " + string.Join(", ", missing));
    }

    [Fact]
    public void Every_reference_class_exists_and_sits_under_the_framework_its_recipe_claims()
    {
        var root = PackagesRoot();
        if (root is null) return;

        var problems = new List<string>();

        foreach (var recipe in MobileAppRecipes.List())
        {
            foreach (var reference in recipe.ReferenceObjects)
            {
                var file = FindClassFile(root, reference);
                if (file is null)
                {
                    problems.Add($"{recipe.Id}: reference class '{reference}' is not in this installation");
                    continue;
                }

                // The framework a class belongs to is visible in its declared base. CONTAINS, not
                // starts-with: a concrete class often extends a module-prefixed intermediate base
                // rather than the framework type directly — WHSProcessGuidePromptCycleCountItemBuilder
                // extends WHSProcessGuideCycleCountPageBuilder, which extends ProcessGuidePageBuilder.
                // Measured in the ProcessGuide package: 287 classes extend a ProcessGuide* type
                // directly and a further ~17 go through such an intermediate. A starts-with rule
                // called those legacy, which is the opposite of the truth.
                var declared = DeclaredBase(file) ?? "";
                var isProcessGuide = declared.Contains("ProcessGuide", StringComparison.OrdinalIgnoreCase);
                var isLegacy = declared.Contains("WorkExecuteDisplay", StringComparison.OrdinalIgnoreCase);

                if (recipe.Framework == MobileFramework.ProcessGuide && !isProcessGuide)
                    problems.Add($"{recipe.Id} is a ProcessGuide recipe but '{reference}' extends {declared}");

                if (recipe.Framework == MobileFramework.WorkExecuteDisplay && !isLegacy)
                    problems.Add($"{recipe.Id} is a legacy recipe but '{reference}' extends {declared}");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void The_process_guide_framework_is_a_package_of_its_own()
    {
        // The recipes tell a reader ProcessGuide is a separate, current framework rather than a
        // handful of helper classes. If that stops being true the advice needs revisiting.
        var root = PackagesRoot();
        if (root is null) return;

        var classDir = Path.Combine(root, "ProcessGuide", "ProcessGuide", "AxClass");
        if (!Directory.Exists(classDir)) return;

        Assert.True(Directory.GetFiles(classDir, "*.xml").Length > 100,
            "the ProcessGuide package holds far fewer classes than a framework would");
    }
}
