using D365FO.Core;
using D365FO.Core.Extract;
using Spectre.Console;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Resolve;

public sealed class ResolveLabelCommand : Command<ResolveLabelCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        // Several tokens in one call: an agent reading a table or form XML holds a handful of
        // tokens at once, and `labels resolve @A @B @C` failed to parse, so each became its own
        // process start (or the agent fell back to grepping the .label.txt files).
        [CommandArgument(0, "<TOKEN>")]
        [System.ComponentModel.Description("Label token, e.g. @SYS12345 or SYS12345. Pass several to resolve them in one call.")]
        public string[] Tokens { get; init; } = [];

        [CommandOption("-l|--lang <CSV>")]
        [System.ComponentModel.Description("Comma-separated language tags (e.g. en-US,cs). Defaults to all indexed.")]
        public string? Languages { get; init; }
    }

    private const string NotFoundHint =
        "Verify @FilePrefix+Key spelling and that the language is indexed. " +
        "In PowerShell, wrap the token in single quotes to prevent @ from being treated as the splat operator: '@SYS12345'. " +
        "Also quote comma-separated --lang values: --lang 'en-us,cs'.";

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var langs = string.IsNullOrWhiteSpace(settings.Languages)
            ? null
            : settings.Languages.Split([',', ';'], System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries);

        var tokens = settings.Tokens.Where(t => !string.IsNullOrWhiteSpace(t)).ToArray();
        if (tokens.Length == 0)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "Token required."));

        var repo = RepoFactory.Create();
        return tokens.Length == 1
            ? ResolveOne(kind, repo, tokens[0], langs, settings.RawText)
            : ResolveMany(kind, repo, tokens, langs, settings.RawText);
    }

    /// <summary>One token: the envelope this command has always returned.</summary>
    private static int ResolveOne(OutputMode.Kind kind, D365FO.Core.Index.MetadataRepository repo,
        string token, string[]? langs, bool rawText)
    {
        var hits = Lookup(repo, token, langs, rawText);
        if (hits.Count == 0)
        {
            return RenderHelpers.Render(kind,
                ToolResult<object>.Fail("LABEL_NOT_FOUND", $"Label '{token}' not resolved.", NotFoundHint));
        }

        // Resolve is the call an agent makes right before reusing a token — confirm
        // against the physical .label.txt so a stale index row (rolled-back session,
        // out-of-band change) is not presented as reusable.
        var (phantoms, warnings) = D365FO.Core.Index.LabelDiskCheck.Annotate(repo, hits);

        return RenderHelpers.Render(kind, ToolResult<object>.Success(new
        {
            count = hits.Count,
            items = hits,
            phantomLabels = phantoms.Count > 0 ? phantoms : null,
        }, warnings), _ => AnsiConsole.Write(BuildTable(hits, phantoms)));
    }

    /// <summary>
    /// Several tokens: one result per token, in the order given. A token that does not resolve
    /// is reported in its own result (<c>found: false</c>) rather than failing the others; the
    /// call fails only when none of them resolves.
    /// </summary>
    private static int ResolveMany(OutputMode.Kind kind, D365FO.Core.Index.MetadataRepository repo,
        string[] tokens, string[]? langs, bool rawText)
    {
        var results = tokens
            .Select(t => (Token: t, Hits: Lookup(repo, t, langs, rawText)))
            .ToList();

        var missing = results.Where(r => r.Hits.Count == 0).Select(r => r.Token).ToList();
        if (missing.Count == results.Count)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("LABEL_NOT_FOUND",
                $"None of the labels resolved: {string.Join(", ", missing)}.", NotFoundHint));
        }

        var found = results.SelectMany(r => r.Hits).ToList();
        var (phantoms, diskWarnings) = D365FO.Core.Index.LabelDiskCheck.Annotate(repo, found);

        var warnings = new List<string>(diskWarnings ?? []);
        if (missing.Count > 0)
            warnings.Add($"Not resolved: {string.Join(", ", missing)}. {NotFoundHint}");

        return RenderHelpers.Render(kind, ToolResult<object>.Success(new
        {
            count = results.Count,
            resolved = results.Count - missing.Count,
            results = results.Select(r => new
            {
                token = r.Token,
                found = r.Hits.Count > 0,
                count = r.Hits.Count,
                items = r.Hits,
            }),
            notResolved = missing.Count > 0 ? missing : null,
            phantomLabels = phantoms.Count > 0 ? phantoms : null,
        }, warnings.Count > 0 ? warnings : null), _ =>
        {
            AnsiConsole.Write(BuildTable(found, phantoms));
            foreach (var token in missing)
                AnsiConsole.MarkupLine($"[red]{RenderHelpers.Escape(token)}: not resolved[/]");
        });
    }

    private static IReadOnlyList<D365FO.Core.Index.LabelMatch> Lookup(D365FO.Core.Index.MetadataRepository repo,
        string token, string[]? langs, bool rawText)
    {
        var hits = repo.ResolveLabel(token, langs);
        return rawText
            ? hits
            : hits.Select(h => h with { Value = StringSanitizer.Sanitize(h.Value) }).ToList();
    }

    private static Table BuildTable(IEnumerable<D365FO.Core.Index.LabelMatch> hits, IReadOnlyCollection<string> phantoms)
    {
        var table = new Table().AddColumn("File").AddColumn("Lang").AddColumn("Key").AddColumn("Value");
        foreach (var h in hits)
        {
            var key = phantoms.Contains($"@{h.File}:{h.Key}")
                ? $"[red]{RenderHelpers.Escape(h.Key)} (not on disk)[/]"
                : RenderHelpers.Escape(h.Key) ?? h.Key;
            table.AddRow(h.File, h.Language, key, RenderHelpers.Escape(h.Value) ?? "-");
        }
        return table;
    }
}
