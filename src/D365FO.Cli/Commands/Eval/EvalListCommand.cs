using D365FO.Core;
using D365FO.Core.Eval;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Eval;

/// <summary>Lists every case in the eval catalog (<c>eval/cases/*.json</c>).</summary>
public sealed class EvalListCommand : Command<EvalListCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var (root, failure) = EvalPathsResolver.Resolve(kind);
        if (failure is int f) return f;

        var (cases, errors) = EvalCaseCatalog.LoadAll(EvalPaths.CasesDir(root!));

        var payload = new
        {
            cases = cases.OrderBy(c => c.Id, StringComparer.Ordinal).Select(c => new
            {
                id = c.Id,
                tier = c.Tier,
                title = c.Title,
                goldenPending = c.GoldenPending,
                requiresFixtureIndex = c.RequiresFixtureIndex,
                hasCanonicalArgs = c.CanonicalArgs is { Count: > 0 },
                tags = c.Tags,
            }),
            count = cases.Count,
        };

        return RenderHelpers.Render(kind, ToolResult<object>.Success(payload, errors.Count > 0 ? errors : null));
    }
}
