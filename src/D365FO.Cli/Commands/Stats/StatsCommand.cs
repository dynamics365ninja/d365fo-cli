using D365FO.Core;
using D365FO.Core.Index;
using Spectre.Console;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Stats;

/// <summary>
/// Parametric aggregation over the metadata index. Corresponds to
/// ROADMAP §4.2 and upstream MCP <c>analyze_code_patterns</c>-style
/// overviews.
/// </summary>
public sealed class StatsCommand : Command<StatsCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandOption("-n|--top <N>")]
        [System.ComponentModel.Description("Rows to return per ranking section (default 10).")]
        public int TopN { get; init; } = 10;
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var repo = RepoFactory.Create();
        var stats = repo.GetStats(settings.TopN);
        var counts = repo.CountAll();

        var result = ToolResult<object>.Success(new
        {
            totals = counts,
            perModel = stats.PerModel.Select(m => new
            {
                model = m.Model,
                isCustom = m.IsCustom,
                tables = m.Tables,
                classes = m.Classes,
                edts = m.Edts,
                enums = m.Enums,
                menuItems = m.MenuItems,
                forms = m.Forms,
                extensions = m.Extensions,
                coc = m.Coc,
                labels = m.Labels,
            }),
            topTables = stats.TopTables,
            topClasses = stats.TopClasses,
            topCocTargets = stats.TopCocTargets,
        });

        return RenderHelpers.Render(kind, result, _ =>
        {
            AnsiConsole.MarkupLine($"[bold]Index totals[/] — models=[green]{counts.Models}[/] tables=[green]{counts.Tables}[/] classes=[green]{counts.Classes}[/] labels=[green]{counts.Labels}[/]");

            var topTable = new Table().AddColumns("Top table (fields)", "model", "fields");
            foreach (var t in stats.TopTables) topTable.AddRow(t.Name, t.Model, t.FieldCount.ToString());
            AnsiConsole.Write(topTable);

            var topClass = new Table().AddColumns("Top class (methods)", "model", "methods");
            foreach (var c in stats.TopClasses) topClass.AddRow(c.Name, c.Model, c.MethodCount.ToString());
            AnsiConsole.Write(topClass);

            var topCoc = new Table().AddColumns("Top CoC target", "extensions");
            foreach (var c in stats.TopCocTargets) topCoc.AddRow(c.Target, c.ExtensionCount.ToString());
            AnsiConsole.Write(topCoc);
        });
    }
}
