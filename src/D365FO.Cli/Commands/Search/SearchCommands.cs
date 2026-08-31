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

        [CommandOption("--fts")]
        [System.ComponentModel.Description("Use SQLite FTS5 ranking (phrase queries, NEAR, column filters). Falls back to LIKE if FTS5 is unavailable.")]
        public bool Fts { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var repo = RepoFactory.Create();
        string[]? langs = string.IsNullOrWhiteSpace(settings.Languages)
            ? null
            : settings.Languages.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // FTS5 is always preferred: SearchLabelsFts() auto-falls-back to LIKE when
        // the SQLite build lacks FTS5.  The --fts flag is kept for back-compat but
        // no longer changes behaviour.  Remove the ternary to avoid the footgun where
        // a default `d365fo search label` call bypasses the faster FTS5 path.
        var matches = repo.SearchLabelsFts(settings.Query, langs, settings.Limit);
        if (!settings.RawText)
        {
            matches = matches.Select(m => m with { Value = StringSanitizer.Sanitize(m.Value) }).ToList();
        }

        // Confirm every hit against the physical .label.txt before showing it as
        // reusable — the index is a cache that is never invalidated on delete or
        // rollback, and a phantom label passes the build (xppc does not check
        // labels) only to fail BP later with BPErrorUnknownLabel.
        var (phantoms, warnings) = D365FO.Core.Index.LabelDiskCheck.Annotate(repo, matches);

        var result = ToolResult<object>.Success(new
        {
            count = matches.Count,
            items = matches,
            phantomLabels = phantoms.Count > 0 ? phantoms : null,
        }, warnings);

        return RenderHelpers.Render(kind, result, _ =>
        {
            var table = new Table().AddColumn("File").AddColumn("Lang").AddColumn("Key").AddColumn("Value");
            foreach (var m in matches)
            {
                var key = phantoms.Contains($"@{m.File}:{m.Key}")
                    ? $"[red]{RenderHelpers.Escape(m.Key)} (not on disk)[/]"
                    : RenderHelpers.Escape(m.Key) ?? m.Key;
                table.AddRow(m.File, m.Language, key, RenderHelpers.Escape(m.Value) ?? "-");
            }
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

public sealed class SearchQueryCommand : Command<SearchQueryCommand.Settings>
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
        var items = RepoFactory.Create().SearchQueries(settings.Query, settings.Limit);
        return RenderHelpers.Render(kind, ToolResult<object>.Success(new { count = items.Count, items }));
    }
}

public sealed class SearchViewCommand : Command<SearchViewCommand.Settings>
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
        var items = RepoFactory.Create().SearchViews(settings.Query, settings.Limit);
        return RenderHelpers.Render(kind, ToolResult<object>.Success(new { count = items.Count, items }));
    }
}

public sealed class SearchEntityCommand : Command<SearchEntityCommand.Settings>
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
        var items = RepoFactory.Create().SearchDataEntities(settings.Query, settings.Limit);
        return RenderHelpers.Render(kind, ToolResult<object>.Success(new { count = items.Count, items }));
    }
}

public sealed class SearchReportCommand : Command<SearchReportCommand.Settings>
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
        var items = RepoFactory.Create().SearchReports(settings.Query, settings.Limit);
        return RenderHelpers.Render(kind, ToolResult<object>.Success(new { count = items.Count, items }));
    }
}

public sealed class SearchServiceCommand : Command<SearchServiceCommand.Settings>
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
        var items = RepoFactory.Create().SearchServices(settings.Query, settings.Limit);
        return RenderHelpers.Render(kind, ToolResult<object>.Success(new { count = items.Count, items }));
    }
}

public sealed class SearchWorkflowCommand : Command<SearchWorkflowCommand.Settings>
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
        var items = RepoFactory.Create().SearchWorkflowTypes(settings.Query, settings.Limit);
        return RenderHelpers.Render(kind, ToolResult<object>.Success(new { count = items.Count, items }));
    }
}

/// <summary>
/// Scope-agnostic quick jump across every indexed kind. Corresponds to
/// upstream MCP <c>search</c> and fulfils ROADMAP item 4.3.
/// </summary>
public sealed class SearchAnyCommand : Command<SearchAnyCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<QUERY>")]
        [System.ComponentModel.Description("Substring to look up across Tables / Classes / EDTs / Enums / MenuItems / Forms / Queries / Views / DataEntities / Reports / Services / Workflows.")]
        public string Query { get; init; } = "";

        [CommandOption("-l|--limit <N>")]
        public int Limit { get; init; } = 100;

        [CommandOption("--kind <KINDS>")]
        [System.ComponentModel.Description("Comma-separated kind filter, e.g. 'table,class,edt'. Omit to search all kinds.")]
        public string? Kind { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        if (string.IsNullOrWhiteSpace(settings.Query))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("BAD_INPUT", "Query required."));

        var kinds = string.IsNullOrWhiteSpace(settings.Kind)
            ? null
            : settings.Kind.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var repo = RepoFactory.Create();
        var rows = repo.FindUsagesFiltered(settings.Query, kinds, settings.Limit)
            .Select(t => new { kind = t.Kind, name = t.Name, model = t.Model })
            .ToList();
        var byKind = rows.GroupBy(r => r.kind).ToDictionary(g => g.Key, g => g.Count());
        return RenderHelpers.Render(kind, ToolResult<object>.Success(new
        {
            count = rows.Count,
            byKind,
            items = rows,
        }));
    }
}

public sealed class SearchBusinessEventCommand : Command<SearchBusinessEventCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<QUERY>")]
        public string Query { get; init; } = "";

        [CommandOption("-c|--category <CAT>")]
        [System.ComponentModel.Description("Filter by business event category (partial match).")]
        public string? Category { get; init; }

        [CommandOption("-l|--limit <N>")]
        public int Limit { get; init; } = 50;
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var repo = RepoFactory.Create();
        var items = repo.SearchBusinessEvents(settings.Query, settings.Category, settings.Limit);
        return RenderHelpers.Render(kind, ToolResult<object>.Success(new { count = items.Count, items }));
    }
}

public sealed class SearchSecurityPolicyCommand : Command<SearchSecurityPolicyCommand.Settings>
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
        var items = repo.SearchSecurityPolicies(settings.Query, settings.Limit);
        return RenderHelpers.Render(kind, ToolResult<object>.Success(new { count = items.Count, items }));
    }
}

public sealed class SearchConfigurationKeyCommand : Command<SearchConfigurationKeyCommand.Settings>
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
        var items = repo.SearchConfigurationKeys(settings.Query, settings.Limit);
        return RenderHelpers.Render(kind, ToolResult<object>.Success(new { count = items.Count, items }));
    }
}

public sealed class SearchTileCommand : Command<SearchTileCommand.Settings>
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
        var items = repo.SearchTiles(settings.Query, settings.Limit);
        return RenderHelpers.Render(kind, ToolResult<object>.Success(new { count = items.Count, items }));
    }
}

public sealed class SearchWorkspaceCommand : Command<SearchWorkspaceCommand.Settings>
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
        var items = repo.SearchWorkspaces(settings.Query, settings.Limit);
        return RenderHelpers.Render(kind, ToolResult<object>.Success(new { count = items.Count, items }));
    }
}

public sealed class SearchBatchCommand : Command<SearchBatchCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<QUERY>")]
        [System.ComponentModel.Description("One or more substrings to look up across every indexed kind.")]
        public string[] Queries { get; init; } = Array.Empty<string>();

        [CommandOption("-l|--limit <N>")]
        [System.ComponentModel.Description("Maximum hits per query.")]
        public int Limit { get; init; } = 50;

        [CommandOption("--kind <KINDS>")]
        [System.ComponentModel.Description("Comma-separated kind filter, e.g. 'class' or 'table,class'. Omit to search all kinds. Restricts each query to the listed object types only — much faster when you only care about one kind.")]
        public string? Kind { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var output = OutputMode.Resolve(settings.Output);
        var queries = settings.Queries
            .Where(q => !string.IsNullOrWhiteSpace(q))
            .Select(q => q.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (queries.Length == 0)
            return RenderHelpers.Render(output, ToolResult<object>.Fail("BAD_INPUT", "At least one query is required."));

        var kinds = string.IsNullOrWhiteSpace(settings.Kind)
            ? null
            : settings.Kind.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var repo = RepoFactory.Create();
        var results = queries.Select(q =>
        {
            var hits = repo.FindUsagesFiltered(q, kinds, settings.Limit)
                .Select(t => new { kind = t.Kind, name = t.Name, model = t.Model })
                .ToList();
            var byKind = hits.GroupBy(h => h.kind).ToDictionary(g => g.Key, g => g.Count());
            return new { query = q, count = hits.Count, byKind, items = hits };
        }).ToList();

        return RenderHelpers.Render(output, ToolResult<object>.Success(new
        {
            count = results.Count,
            limit = settings.Limit,
            kinds = kinds ?? ["all"],
            results,
        }));
    }
}

