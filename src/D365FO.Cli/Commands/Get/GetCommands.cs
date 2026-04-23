using D365FO.Core;
using Spectre.Console;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Get;

public sealed class GetTableCommand : Command<GetTableCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<NAME>")]
        public string Name { get; init; } = "";

        [CommandOption("--include <PARTS>")]
        [System.ComponentModel.Description("Comma list: fields,indexes,relations (default: all)")]
        public string? Include { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var repo = RepoFactory.Create();
        var details = repo.GetTableDetails(settings.Name);

        if (details is null)
        {
            return RenderHelpers.Render(kind,
                ToolResult<object>.Fail("TABLE_NOT_FOUND", $"Table '{settings.Name}' not found in index.",
                    "Run 'd365fo index build' after extracting metadata."));
        }

        var result = ToolResult<object>.Success(new
        {
            table = details.Table,
            fields = details.Fields,
            relations = details.Relations,
        });

        return RenderHelpers.Render(kind, result, _ =>
        {
            AnsiConsole.MarkupLine($"[bold]{RenderHelpers.Escape(details.Table.Name)}[/] — {RenderHelpers.Escape(details.Table.Label) ?? "(no label)"}  [grey]({details.Table.Model})[/]");
            var table = new Table().AddColumn("Field").AddColumn("Type/EDT").AddColumn("Label").AddColumn("Mand.");
            foreach (var f in details.Fields)
                table.AddRow(f.Name, f.EdtName ?? f.Type ?? "-", RenderHelpers.Escape(f.Label) ?? "-", f.Mandatory ? "yes" : "");
            AnsiConsole.Write(table);
            if (details.Relations.Count > 0)
            {
                AnsiConsole.MarkupLine("[bold]Relations[/]");
                var rel = new Table().AddColumn("From").AddColumn("To").AddColumn("Cardinality").AddColumn("Name");
                foreach (var r in details.Relations)
                    rel.AddRow(r.FromTable, r.ToTable, r.Cardinality ?? "-", r.RelationName ?? "-");
                AnsiConsole.Write(rel);
            }
        });
    }
}

public sealed class GetEdtCommand : Command<GetEdtCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<NAME>")]
        public string Name { get; init; } = "";
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var repo = RepoFactory.Create();
        var edt = repo.GetEdt(settings.Name);
        var result = edt is null
            ? ToolResult<object>.Fail("EDT_NOT_FOUND", $"EDT '{settings.Name}' not found.")
            : ToolResult<object>.Success(edt);
        return RenderHelpers.Render(kind, result);
    }
}

public sealed class GetClassCommand : Command<GetClassCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<NAME>")]
        public string Name { get; init; } = "";
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var repo = RepoFactory.Create();
        var details = repo.GetClassDetails(settings.Name);
        var result = details is null
            ? ToolResult<object>.Fail("CLASS_NOT_FOUND", $"Class '{settings.Name}' not found.")
            : ToolResult<object>.Success(details);
        return RenderHelpers.Render(kind, result);
    }
}

public sealed class GetMenuItemCommand : Command<GetMenuItemCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<NAME>")]
        public string Name { get; init; } = "";
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var repo = RepoFactory.Create();
        var mi = repo.GetMenuItem(settings.Name);
        var result = mi is null
            ? ToolResult<object>.Fail("MENU_ITEM_NOT_FOUND", $"Menu item '{settings.Name}' not found.")
            : ToolResult<object>.Success(mi);
        return RenderHelpers.Render(kind, result);
    }
}

public sealed class GetSecurityCommand : Command<GetSecurityCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<OBJECT>")]
        public string Object { get; init; } = "";

        [CommandOption("--type <TYPE>")]
        [System.ComponentModel.Description("Table|Form|Report|Class|Menuitem (default: Menuitem)")]
        public string Type { get; init; } = "Menuitem";
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var repo = RepoFactory.Create();
        var coverage = repo.GetSecurityCoverage(settings.Object, settings.Type);
        return RenderHelpers.Render(kind, ToolResult<object>.Success(coverage));
    }
}
