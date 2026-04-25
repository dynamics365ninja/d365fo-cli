using D365FO.Core;
using D365FO.Core.Extract;
using Spectre.Console.Cli;
using D365FO.Cli.Commands.Get;

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

public sealed class FindExtensionsCommand : Command<FindExtensionsCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<TARGET>")]
        [System.ComponentModel.Description("Target artifact name (e.g. CustTable, SalesTable).")]
        public string Target { get; init; } = "";

        [CommandOption("--kind <KIND>")]
        [System.ComponentModel.Description("Filter: Table/Form/Edt/Enum/View/Map")]
        public string? Kind { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        if (string.IsNullOrWhiteSpace(settings.Target))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("BAD_INPUT", "Target required."));
        var repo = RepoFactory.Create();
        var items = repo.FindExtensions(settings.Target, settings.Kind);
        return RenderHelpers.Render(kind,
            ToolResult<object>.Success(new { count = items.Count, items }));
    }
}

public sealed class FindHandlersCommand : Command<FindHandlersCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<OBJECT>")]
        [System.ComponentModel.Description("Source object (form/table/class) whose events you want to list handlers for.")]
        public string Object { get; init; } = "";

        [CommandOption("--kind <KIND>")]
        [System.ComponentModel.Description("Filter: Form/FormDataSource/FormControl/Table/Delegate")]
        public string? Kind { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        if (string.IsNullOrWhiteSpace(settings.Object))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("BAD_INPUT", "Object required."));
        var repo = RepoFactory.Create();
        var items = repo.FindEventSubscribers(settings.Object, settings.Kind);
        return RenderHelpers.Render(kind,
            ToolResult<object>.Success(new { count = items.Count, items }));
    }
}

/// <summary>
/// Regex-based reverse-reference scanner. Walks every indexed X++ source
/// (Classes / Tables / Forms) and greps each method body for the given
/// symbol. Intended as a stopgap until the bridge-backed findReferences
/// is wired up against Microsoft's compiler API.
/// </summary>
public sealed class FindRefsCommand : Command<FindRefsCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<NAME>")]
        [System.ComponentModel.Description("Symbol to search for (class / table / EDT / enum / label).")]
        public string Name { get; init; } = "";

        [CommandOption("--kind <KIND>")]
        [System.ComponentModel.Description("Restrict scan to a single artifact kind: class | table | form.")]
        public string? Kind { get; init; }

        [CommandOption("--model <NAME>")]
        [System.ComponentModel.Description("Restrict scan to a single model.")]
        public string? Model { get; init; }

        [CommandOption("-l|--limit <N>")]
        public int Limit { get; init; } = 200;

        [CommandOption("--xref")]
        [System.ComponentModel.Description("Prefer the DYNAMICSXREFDB via the metadata bridge. Requires D365FO_BRIDGE_ENABLED=1 and a populated DYNAMICSXREFDB on the VM.")]
        public bool Xref { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        if (string.IsNullOrWhiteSpace(settings.Name))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("BAD_INPUT", "Name required."));

        // Bridge-backed compiler xref path — fast, precise, and returns
        // line/column plus reference kind (Call/Read/Set/Type/...).
        if (settings.Xref && BridgeGate.ShouldTry())
        {
            var xref = BridgeGate.TryFindReferences(settings.Name, settings.Kind, settings.Limit);
            if (xref is not null)
            {
                xref["_source"] = "xrefdb";
                return RenderHelpers.Render(kind, ToolResult<object>.Success((object)xref));
            }
            // Fall through to regex scan if bridge / DB unavailable.
        }

        var repo = RepoFactory.Create();
        var sources = repo.EnumerateSourcePaths(settings.Model);
        if (!string.IsNullOrWhiteSpace(settings.Kind))
        {
            var k = settings.Kind!;
            sources = sources.Where(s => string.Equals(s.Kind, k, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        var rx = new System.Text.RegularExpressions.Regex(
            $@"\b{System.Text.RegularExpressions.Regex.Escape(settings.Name)}\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        var hits = new System.Collections.Concurrent.ConcurrentBag<object>();
        int scanned = 0;

        System.Threading.Tasks.Parallel.ForEach(sources,
            new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            row =>
            {
                System.Threading.Interlocked.Increment(ref scanned);
                var src = XppSourceReader.Read(row.SourcePath);
                if (src is null) return;
                foreach (var method in src.Methods)
                {
                    if (!rx.IsMatch(method.Body)) continue;
                    var lines = method.Body.Replace("\r\n", "\n").Split('\n');
                    var sampleLines = new List<object>();
                    for (int i = 0; i < lines.Length && sampleLines.Count < 3; i++)
                    {
                        if (rx.IsMatch(lines[i]))
                            sampleLines.Add(new { line = i + 1, text = lines[i].Trim() });
                    }
                    hits.Add(new
                    {
                        kind = row.Kind,
                        name = row.Name,
                        model = row.Model,
                        method = method.Name,
                        matches = sampleLines,
                        path = row.SourcePath,
                    });
                }
            });

        var items = hits.Take(settings.Limit).ToList();
        return RenderHelpers.Render(kind, ToolResult<object>.Success(new
        {
            needle = settings.Name,
            filesScanned = scanned,
            count = items.Count,
            truncated = hits.Count > settings.Limit,
            items,
        }));
    }
}

/// <summary>
/// Pattern analyser for indexed AOT forms. Surfaces real reference forms so
/// the agent can ground "what pattern fits this table?" without guessing.
/// Three modes:
///   --pattern &lt;P&gt;        list all forms whose Microsoft pattern starts with P
///   --table &lt;T&gt;          list all forms whose primary datasource is T
///   --similar-to &lt;Form&gt;  resolve the reference form's pattern/table and list peers
///   (no flags)            histogram of patterns across the index
/// </summary>
public sealed class FindFormPatternsCommand : Command<FindFormPatternsCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandOption("--pattern <PATTERN>")]
        [System.ComponentModel.Description("Filter by Microsoft form pattern (SimpleList, DetailsMaster, ListPage, ...). Prefix match.")]
        public string? Pattern { get; init; }

        [CommandOption("--table <TABLE>")]
        [System.ComponentModel.Description("Filter forms whose datasources include this table.")]
        public string? Table { get; init; }

        [CommandOption("--similar-to <FORM>")]
        [System.ComponentModel.Description("Find forms similar to a reference form (same pattern + same primary table).")]
        public string? SimilarTo { get; init; }

        [CommandOption("--model <MODEL>")]
        [System.ComponentModel.Description("Restrict to a single model.")]
        public string? Model { get; init; }

        [CommandOption("-l|--limit <N>")]
        public int Limit { get; init; } = 50;
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var repo = RepoFactory.Create();

        // --similar-to: resolve the reference form first, then delegate.
        string? pattern = settings.Pattern;
        string? table = settings.Table;
        object? reference = null;
        if (!string.IsNullOrWhiteSpace(settings.SimilarTo))
        {
            var refForm = repo.GetForm(settings.SimilarTo);
            if (refForm is null)
            {
                return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                    "FORM_NOT_FOUND",
                    $"Form '{settings.SimilarTo}' is not in the index.",
                    "Run `d365fo index extract` (or refresh) and retry, or check the spelling with `d365fo search form`."));
            }
            // Pattern lives on Forms.Pattern but FormDetails doesn't surface
            // it yet. Re-use the analyser query with the model + name to
            // pull the reference row directly.
            var enriched = repo.FindFormPatterns(model: refForm.Form.Model, limit: int.MaxValue)
                .FirstOrDefault(r => string.Equals(r.Name, refForm.Form.Name, StringComparison.OrdinalIgnoreCase));
            pattern ??= enriched?.Pattern;
            table ??= enriched?.PrimaryTable
                  ?? refForm.DataSources.Select(d => d.TableName).FirstOrDefault(t => !string.IsNullOrEmpty(t));
            reference = new
            {
                name = refForm.Form.Name,
                model = refForm.Form.Model,
                pattern,
                primaryTable = table,
            };
        }

        // No filters => show histogram so the user can pick a pattern.
        if (string.IsNullOrWhiteSpace(pattern) && string.IsNullOrWhiteSpace(table))
        {
            var summary = repo.SummarizeFormPatterns();
            return RenderHelpers.Render(kind, ToolResult<object>.Success(new
            {
                mode = "summary",
                totalForms = summary.Sum(s => s.Count),
                patterns = summary,
                hint = "Pass --pattern <P>, --table <T>, or --similar-to <Form> to drill in.",
            }));
        }

        var rows = repo.FindFormPatterns(pattern, table, settings.Model, settings.Limit);
        // When --similar-to was used, drop the reference form itself.
        if (reference is not null && !string.IsNullOrEmpty(settings.SimilarTo))
        {
            rows = rows.Where(r => !string.Equals(r.Name, settings.SimilarTo, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        return RenderHelpers.Render(kind, ToolResult<object>.Success(new
        {
            mode = reference is null ? "filter" : "similar",
            filter = new { pattern, table, model = settings.Model },
            reference,
            count = rows.Count,
            items = rows,
        }));
    }
}
