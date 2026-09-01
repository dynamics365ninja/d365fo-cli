using D365FO.Core;
using D365FO.Core.Knowledge;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Knowledge;

// `d365fo report-pattern` — which of the seven SSRS shapes to build, and what each one costs.
//
// `generate report` could already produce every one of them. What was missing was the decision:
// an agent asked for "a report of open sales orders grouped by customer with totals" had to infer
// the shape, and inferring it wrong is expensive in a way a form pattern violation is not —
// there is no pattern XML to validate a report against, so a pre-processed requirement built as a
// plain DP simply behaves wrong in batch, with nothing failing anywhere.

/// <summary><c>d365fo report-pattern list</c> — the seven shapes and when each applies.</summary>
public sealed class ReportPatternListCommand : Command<ReportPatternListCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);

        return RenderHelpers.Render(kind, ToolResult<object>.Success(new
        {
            count = ReportRecipes.List().Count,
            items = ReportRecipes.List().Select(r => new
            {
                r.Id,
                r.Title,
                r.WhenToUse,
                objects = r.Roster.Count,
                r.ScaffoldCall,
            }),
            note = "Unlike a form pattern there is no pattern XML to validate a report against, so these are "
                 + "recipes, not specs: an object roster, the base classes, one scaffold call, and the checks "
                 + "worth running. `report-pattern spec <id>` has the rest.",
        }));
    }
}

/// <summary><c>d365fo report-pattern spec</c> — one shape in full.</summary>
public sealed class ReportPatternSpecCommand : Command<ReportPatternSpecCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<PATTERN>")]
        [System.ComponentModel.Description("Recipe id: simple-list | grouped-with-totals | header-detail | pre-process | query-based | print-mgmt-form-letter | ui-builder-dialog")]
        public string Pattern { get; init; } = "";
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);

        var recipe = ReportRecipes.Find(settings.Pattern);
        if (recipe is null)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.TopicNotFound,
                $"No report recipe called '{settings.Pattern}'.",
                $"Available: {string.Join(", ", ReportRecipes.Ids())}."));
        }

        return RenderHelpers.Render(kind, ToolResult<object>.Success(new
        {
            recipe.Id,
            recipe.Title,
            recipe.WhenToUse,
            roster = recipe.Roster.Select(o => new { o.Kind, o.Role, o.Extends, o.Naming }),
            recipe.ScaffoldCall,
            methodGuidance = recipe.MethodGuidance,
            checks = recipe.Checks,
            referenceObjects = recipe.ReferenceObjects.Count > 0 ? recipe.ReferenceObjects : null,
            readTheReference = recipe.ReferenceObjects.Count > 0
                ? $"Shipped objects of this exact shape — read one with `d365fo read class {recipe.ReferenceObjects[0]}` "
                  + "rather than working from this description alone."
                : null,
        }));
    }
}
