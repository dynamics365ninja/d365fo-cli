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
