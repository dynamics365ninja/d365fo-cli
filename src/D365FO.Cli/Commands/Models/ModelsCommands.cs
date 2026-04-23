using D365FO.Core;
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
