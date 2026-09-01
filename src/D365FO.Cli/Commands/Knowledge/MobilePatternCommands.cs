using D365FO.Core;
using D365FO.Core.Knowledge;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Knowledge;

// `d365fo mobile-pattern` — warehouse scanner screens.
//
// The list deliberately leads with the framework decision rather than the recipes, because that
// is the one choice the platform will not let you take back cheaply: the same screens are built
// by two frameworks, and building on the wrong one is a rewrite.

/// <summary><c>d365fo mobile-pattern list</c> — the framework decision, then the recipes.</summary>
public sealed class MobilePatternListCommand : Command<MobilePatternListCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandOption("--framework <NAME>")]
        [System.ComponentModel.Description("Show only one framework's recipes: process-guide | work-execute-display | configuration.")]
        public string? Framework { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);

        var recipes = MobileAppRecipes.List().AsEnumerable();
        if (!string.IsNullOrWhiteSpace(settings.Framework))
        {
            var wanted = settings.Framework!.Replace("-", "", StringComparison.Ordinal);
            if (!Enum.TryParse<MobileFramework>(wanted, ignoreCase: true, out var framework))
            {
                return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                    $"Unknown framework '{settings.Framework}'.",
                    "Use process-guide, work-execute-display or configuration."));
            }
            recipes = recipes.Where(r => r.Framework == framework);
        }

        var items = recipes.ToList();
        return RenderHelpers.Render(kind, ToolResult<object>.Success(new
        {
            decideFirst = MobileAppRecipes.FrameworkDecision,
            count = items.Count,
            items = items.Select(r => new
            {
                r.Id,
                r.Title,
                framework = r.Framework.ToString(),
                r.WhenToUse,
                classes = r.Roster.Count,
            }),
        }));
    }
}

/// <summary><c>d365fo mobile-pattern spec</c> — one recipe in full.</summary>
public sealed class MobilePatternSpecCommand : Command<MobilePatternSpecCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<RECIPE>")]
        [System.ComponentModel.Description("Recipe id: processguide-flow | processguide-page-control | processguide-page-replace | processguide-step-insert | app-step-identity | legacy-workexecutedisplay | gs1-scan-input")]
        public string Recipe { get; init; } = "";
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);

        var recipe = MobileAppRecipes.Find(settings.Recipe);
        if (recipe is null)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.TopicNotFound,
                $"No warehouse-app recipe called '{settings.Recipe}'.",
                $"Available: {string.Join(", ", MobileAppRecipes.Ids())}."));
        }

        return RenderHelpers.Render(kind, ToolResult<object>.Success(new
        {
            recipe.Id,
            recipe.Title,
            framework = recipe.Framework.ToString(),
            recipe.WhenToUse,
            roster = recipe.Roster.Select(o => new { o.Role, o.Extends, o.Naming, o.Required }),
            guidance = recipe.Guidance,
            checks = recipe.Checks,
            referenceObjects = recipe.ReferenceObjects.Count > 0 ? recipe.ReferenceObjects : null,
            readTheReference = recipe.ReferenceObjects.Count > 0
                ? $"Shipped classes of this exact shape — read one with `d365fo read class {recipe.ReferenceObjects[0]}`."
                : null,
            note = recipe.Framework == MobileFramework.Configuration
                ? "This one is CONFIGURATION, not code. Writing a class here is the mistake the recipe exists to prevent."
                : null,
        }));
    }
}
