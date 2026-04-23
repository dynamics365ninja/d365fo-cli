using D365FO.Core;
using Spectre.Console;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Search;

public sealed class SearchClassCommand : Command<SearchClassCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<QUERY>")]
        public string Query { get; init; } = "";

        [CommandOption("-m|--model <MODEL>")]
        public string? Model { get; init; }

        [CommandOption("-l|--limit <N>")]
        public int Limit { get; init; } = 50;
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var repo = RepoFactory.Create();
        var matches = repo.SearchClasses(settings.Query, settings.Model, settings.Limit);
        var result = ToolResult<object>.Success(new { count = matches.Count, items = matches });

        return RenderHelpers.Render(kind, result, _ =>
        {
            var table = new Table().Title($"[bold]Classes matching[/] '{RenderHelpers.Escape(settings.Query)}'")
                .AddColumn("Name").AddColumn("Model").AddColumn("Extends").AddColumn("Flags");
            foreach (var c in matches)
            {
                var flags = (c.IsAbstract ? "abstract " : "") + (c.IsFinal ? "final" : "");
                table.AddRow(c.Name, c.Model, c.Extends ?? "-", flags.Trim());
            }
            AnsiConsole.Write(table);
            AnsiConsole.MarkupLine($"[grey]{matches.Count} result(s)[/]");
        });
    }
}

public sealed class SearchLabelCommand : Command<SearchLabelCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<QUERY>")]
        public string Query { get; init; } = "";

        [CommandOption("--lang <CSV>")]
        public string? Languages { get; init; }

        [CommandOption("-l|--limit <N>")]
        public int Limit { get; init; } = 100;
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var repo = RepoFactory.Create();
        string[]? langs = string.IsNullOrWhiteSpace(settings.Languages)
            ? null
            : settings.Languages.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var matches = repo.SearchLabels(settings.Query, langs, settings.Limit);
        if (!settings.RawText)
        {
            matches = matches.Select(m => m with { Value = StringSanitizer.Sanitize(m.Value) }).ToList();
        }

        var result = ToolResult<object>.Success(new { count = matches.Count, items = matches });

        return RenderHelpers.Render(kind, result, _ =>
        {
            var table = new Table().AddColumn("File").AddColumn("Lang").AddColumn("Key").AddColumn("Value");
            foreach (var m in matches)
                table.AddRow(m.File, m.Language, m.Key, RenderHelpers.Escape(m.Value) ?? "-");
            AnsiConsole.Write(table);
        });
    }
}

public sealed class SearchTableCommand : Command<SearchTableCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<QUERY>")]
        public string Query { get; init; } = "";

        [CommandOption("-m|--model <MODEL>")]
        public string? Model { get; init; }

        [CommandOption("-l|--limit <N>")]
        public int Limit { get; init; } = 50;
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var repo = RepoFactory.Create();
        var items = repo.SearchTables(settings.Query, settings.Model, settings.Limit);
        return RenderHelpers.Render(kind, ToolResult<object>.Success(new { count = items.Count, items }));
    }
}

public sealed class SearchEdtCommand : Command<SearchEdtCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<QUERY>")]
        public string Query { get; init; } = "";

        [CommandOption("-l|--limit <N>")]
        public int Limit { get; init; } = 50;
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var repo = RepoFactory.Create();
        var items = repo.SearchEdts(settings.Query, settings.Limit);
        return RenderHelpers.Render(kind, ToolResult<object>.Success(new { count = items.Count, items }));
    }
}

public sealed class SearchEnumCommand : Command<SearchEnumCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<QUERY>")]
        public string Query { get; init; } = "";

        [CommandOption("-l|--limit <N>")]
        public int Limit { get; init; } = 50;
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var repo = RepoFactory.Create();
        var items = repo.SearchEnums(settings.Query, settings.Limit);
        return RenderHelpers.Render(kind, ToolResult<object>.Success(new { count = items.Count, items }));
    }
}
