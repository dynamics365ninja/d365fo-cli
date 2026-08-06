using D365FO.Core;
using D365FO.Core.Analysis;
using Spectre.Console;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Index;

/// <summary>
/// Reports where this tool's catalogs are narrower than the installation in front of you.
/// </summary>
/// <remarks>
/// Issue #164 / R5. Every catalog the tool answers from — the form-pattern registry, the object-type
/// registry, the DataContract catalog — is generated from one platform version and then committed.
/// That makes it right when it was made and silent about drift afterwards. This command asks the
/// installation instead, and reports what the catalogs do not cover.
/// Exit codes: 0 = no gaps (or skipped), 1 = command failure, 2 = gaps found.
/// </remarks>
public sealed class IndexCrossCheckCommand : Command<IndexCrossCheckCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandOption("--packages <PATH>")]
        [System.ComponentModel.Description("Packages root to sweep for AOT folders. Defaults to D365FO_PACKAGES_PATH; the form-pattern half runs without it.")]
        public string? PackagesPath { get; init; }

        [CommandOption("--show-uncovered")]
        [System.ComponentModel.Description("Also list AOT families this tool does not cover. Off by default — on a real installation it is dozens of entries and none of them is a defect.")]
        public bool ShowUncovered { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var cfg = D365FoSettings.FromEnvironment();

        CrossCheckReport report;
        try
        {
            var repo = RepoFactory.Create();
            report = CatalogCrossCheck.Run(repo, settings.PackagesPath ?? cfg.PackagesPath);
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("NO_INDEX",
                $"Cross-check needs the SQLite index: {ex.Message}",
                "Run `d365fo index build` then `d365fo index extract` first."));
        }

        var result = ToolResult<object>.Success(new
        {
            clean = report.Clean,
            objectsConsidered = report.ObjectsConsidered,
            gaps = report.Gaps.Select(g => new { catalog = g.Catalog, item = g.Item, observed = g.Observed, detail = g.Detail }),
            uncoveredCount = report.Uncovered.Count,
            uncovered = settings.ShowUncovered
                ? report.Uncovered.Select(u => new { folder = u.Folder, models = u.Models })
                : null,
            unusedCount = report.Unused.Count,
            verdict = report.Clean
                ? "Every pattern and family the installation uses is covered by a catalog that claims to cover it."
                : $"{report.Gaps.Count} catalog gap(s) — the tool will answer wrongly about these. Regenerate the catalog named in each.",
        });

        var rc = RenderHelpers.Render(kind, result, _ =>
        {
            foreach (var g in report.Gaps)
            {
                AnsiConsole.MarkupLine($"[red]{RenderHelpers.Escape(g.Catalog)}[/] {RenderHelpers.Escape(g.Item)} [grey]({g.Observed})[/]");
                AnsiConsole.MarkupLine($"  [grey]{RenderHelpers.Escape(g.Detail)}[/]");
            }
            if (settings.ShowUncovered)
                foreach (var u in report.Uncovered)
                    AnsiConsole.MarkupLine($"[yellow]uncovered[/] {RenderHelpers.Escape(u.Folder)} [grey]({u.Models} model(s))[/]");
            AnsiConsole.MarkupLine(report.Clean
                ? $"[green]no catalog gaps[/] ({report.Uncovered.Count} uncovered famil(ies), {report.Unused.Count} unused entr(ies))"
                : $"[red]{report.Gaps.Count} catalog gap(s)[/], {report.Uncovered.Count} uncovered famil(ies)");
        });

        return rc != 0 ? rc : report.Clean ? 0 : 2;
    }
}
