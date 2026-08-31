using D365FO.Core;
using D365FO.Core.Extract;
using Spectre.Console;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Resolve;

public sealed class ResolveLabelCommand : Command<ResolveLabelCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<TOKEN>")]
        [System.ComponentModel.Description("Label token, e.g. @SYS12345 or SYS12345.")]
        public string Token { get; init; } = "";

        [CommandOption("-l|--lang <CSV>")]
        [System.ComponentModel.Description("Comma-separated language tags (e.g. en-US,cs). Defaults to all indexed.")]
        public string? Languages { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var langs = string.IsNullOrWhiteSpace(settings.Languages)
            ? null
            : settings.Languages.Split([',', ';'], System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries);

        var repo = RepoFactory.Create();
        var hits = repo.ResolveLabel(settings.Token, langs);
        if (hits.Count == 0)
        {
            return RenderHelpers.Render(kind,
                ToolResult<object>.Fail("LABEL_NOT_FOUND", $"Label '{settings.Token}' not resolved.",
                    "Verify @FilePrefix+Key spelling and that the language is indexed. " +
                    "In PowerShell, wrap the token in single quotes to prevent @ from being treated as the splat operator: '@SYS12345'. " +
                    "Also quote comma-separated --lang values: --lang 'en-us,cs'."));
        }

        if (!settings.RawText)
            hits = hits.Select(h => h with { Value = StringSanitizer.Sanitize(h.Value) }).ToList();

        // Resolve is the call an agent makes right before reusing a token — confirm
        // against the physical .label.txt so a stale index row (rolled-back session,
        // out-of-band change) is not presented as reusable.
        var (phantoms, warnings) = D365FO.Core.Index.LabelDiskCheck.Annotate(repo, hits);

        return RenderHelpers.Render(kind, ToolResult<object>.Success(new
        {
            count = hits.Count,
            items = hits,
            phantomLabels = phantoms.Count > 0 ? phantoms : null,
        }, warnings), _ =>
        {
            var table = new Table().AddColumn("File").AddColumn("Lang").AddColumn("Key").AddColumn("Value");
            foreach (var h in hits)
            {
                var key = phantoms.Contains($"@{h.File}:{h.Key}")
                    ? $"[red]{RenderHelpers.Escape(h.Key)} (not on disk)[/]"
                    : RenderHelpers.Escape(h.Key) ?? h.Key;
                table.AddRow(h.File, h.Language, key, RenderHelpers.Escape(h.Value) ?? "-");
            }
            AnsiConsole.Write(table);
        });
    }
}
