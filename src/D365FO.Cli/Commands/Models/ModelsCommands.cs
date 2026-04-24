using D365FO.Core;
using D365FO.Core.Analysis;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Models;

public sealed class ModelsListCommand : Command<ModelsListCommand.Settings>
{
    public sealed class Settings : D365OutputSettings { }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var items = RepoFactory.Create().ListModels();
        return RenderHelpers.Render(kind, ToolResult<object>.Success(new { count = items.Count, items }));
    }
}

public sealed class ModelsDepsCommand : Command<ModelsDepsCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<NAME>")]
        public string Name { get; init; } = "";
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var deps = RepoFactory.Create().GetModelDependencies(settings.Name);
        return RenderHelpers.Render(kind, deps is null
            ? ToolResult<object>.Fail("MODEL_NOT_FOUND", $"Model '{settings.Name}' not found.")
            : ToolResult<object>.Success(deps));
    }
}

/// <summary>
/// Graph metrics (fan-in, fan-out, instability, cycles) over
/// <c>ModelDependencies</c> — ROADMAP §6.2.
/// </summary>
public sealed class ModelsCouplingCommand : Command<ModelsCouplingCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandOption("-n|--top <N>")]
        [System.ComponentModel.Description("Rows to return in the ranking (default 20).")]
        public int TopN { get; init; } = 20;

        [CommandOption("--only-cycles")]
        [System.ComponentModel.Description("Skip the per-model ranking and return only detected dependency cycles.")]
        public bool OnlyCycles { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var graph = RepoFactory.Create().GetDependencyGraph();
        var report = CouplingAnalyzer.Analyse(graph);
        var top = report.Nodes.Take(settings.TopN);
        return RenderHelpers.Render(kind, ToolResult<object>.Success(new
        {
            modelCount = report.Nodes.Count,
            cycleCount = report.Cycles.Count,
            cycles = report.Cycles,
            top = settings.OnlyCycles
                ? Array.Empty<object>()
                : top.Select(n => new
                {
                    name = n.Name,
                    fanIn = n.FanIn,
                    fanOut = n.FanOut,
                    instability = Math.Round(n.Instability, 3),
                }).ToArray(),
        }));
    }
}
