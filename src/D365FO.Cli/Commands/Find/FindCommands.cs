using D365FO.Core;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Find;

public sealed class FindCocCommand : Command<FindCocCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<TARGET>")]
        [System.ComponentModel.Description("ClassName or ClassName::methodName")]
        public string Target { get; init; } = "";
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        if (string.IsNullOrWhiteSpace(settings.Target))
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                "BAD_INPUT", "Target is required.", "Pass ClassName or ClassName::method."));
        }
        var parts = settings.Target.Split("::", 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || string.IsNullOrWhiteSpace(parts[0]))
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                "BAD_INPUT", "Target must contain a class name.", "Example: CustTable::validateWrite"));
        }
        var cls = parts[0];
        var method = parts.Length > 1 ? parts[1] : null;

        var repo = RepoFactory.Create();
        var items = repo.FindCocExtensions(cls, method);
        return RenderHelpers.Render(kind,
            ToolResult<object>.Success(new { count = items.Count, items }));
    }
}

public sealed class FindRelationsCommand : Command<FindRelationsCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<TABLE>")]
        public string Table { get; init; } = "";
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var repo = RepoFactory.Create();
        var items = repo.GetTableRelations(settings.Table);
        return RenderHelpers.Render(kind,
            ToolResult<object>.Success(new { count = items.Count, items }));
    }
}

public sealed class FindUsagesCommand : Command<FindUsagesCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<SYMBOL>")]
        public string Symbol { get; init; } = "";

        [CommandOption("-l|--limit <N>")]
        public int Limit { get; init; } = 100;
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        if (string.IsNullOrWhiteSpace(settings.Symbol))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("BAD_INPUT", "Symbol required."));
        var repo = RepoFactory.Create();
        var items = repo.FindUsages(settings.Symbol, settings.Limit)
            .Select(t => new { kind = t.Kind, name = t.Name, model = t.Model })
            .ToList();
        return RenderHelpers.Render(kind,
            ToolResult<object>.Success(new { count = items.Count, items }));
    }
}
