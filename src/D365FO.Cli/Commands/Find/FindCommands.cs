using D365FO.Core;
using D365FO.Core.Analysis;
using Spectre.Console.Cli;
using D365FO.Cli.Commands.Get;
using D365FO.Core.Bridge;
using D365FO.Core.Index;

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

/// <summary>
/// Field-level lookup: "which tables contain field/EDT X". Exact match against
/// TableFields.Name / TableFields.EdtName — NOT a relation or FK lookup, and
/// NOT a source-code grep. Use this instead of `find refs --kind table` or
/// `find relations` when the question is about a table's own field list.
/// </summary>
public sealed class FindFieldsCommand : Command<FindFieldsCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<NAME>")]
        [System.ComponentModel.Description("Field name or EDT name to match exactly (e.g. CustAccount).")]
        public string Name { get; init; } = "";

        [CommandOption("--model <NAME>")]
        [System.ComponentModel.Description("Restrict to a single model.")]
        public string? Model { get; init; }

        [CommandOption("-l|--limit <N>")]
        public int Limit { get; init; } = 200;
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        if (string.IsNullOrWhiteSpace(settings.Name))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("BAD_INPUT", "Name required."));
        var repo = RepoFactory.Create();
        if (!ModelFilter.TryResolve(repo, settings.Model, out var model, out var badModel))
            return RenderHelpers.Render(kind, badModel!);
        var items = repo.FindTablesByField(settings.Name, model, settings.Limit);
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
        [System.ComponentModel.Description("Symbol to search for (class / table / EDT / enum / label). With --xref, qualify a method/field as \"Owner.member\" (e.g. SalesTable.initFromSalesQuotationTable) or pass an AOT path to scope the where-used to its declaring type; a bare member name matches that name on every type. A label reference (\"@WAX2194\" or \"@LabelFile:LabelId\") returns that label's where-used.")]
        public string Name { get; init; } = "";

        [CommandOption("--kind <KIND>")]
        [System.ComponentModel.Description("Text scan: restrict to one artifact kind — class | table | form. With --xref: restrict to one reference kind — Declaration | Set | Read | Call | Reference | Type | Extends | Implements.")]
        public string? Kind { get; init; }

        [CommandOption("--model <NAME>")]
        [System.ComponentModel.Description("Restrict the text scan to a single model (not the package folder: Foundation, not ApplicationSuite). Not supported with --xref.")]
        public string? Model { get; init; }

        [CommandOption("-l|--limit <N>")]
        public int Limit { get; init; } = 200;

        [CommandOption("--all-models")]
        [System.ComponentModel.Description("Text scan without --model: also scan every model that has no source index and is not custom, from disk (minutes on a full installation). By default those are left out and counted in `coverage.notSearched`.")]
        public bool AllModels { get; init; }

        [CommandOption("--xref")]
        [System.ComponentModel.Description("Answer from DYNAMICSXREFDB via the metadata bridge — compiler-resolved, unlike the text scan. Requires D365FO_BRIDGE_ENABLED=1 and a populated DYNAMICSXREFDB on the VM; fails XREF_UNAVAILABLE rather than falling back to the text scan.")]
        public bool Xref { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        if (string.IsNullOrWhiteSpace(settings.Name))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("BAD_INPUT", "Name required."));

        // Bridge-backed compiler xref path — fast, precise, and returns
        // line/column plus reference kind (Call/Read/Set/Type/...). Asked for
        // by name, so it answers or fails: the text scan is a different, less
        // complete answer and must not stand in for it unannounced.
        if (settings.Xref)
        {
            if (settings.AllModels)
                return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                    "--all-models cannot be combined with --xref: the xref query is not scoped by model.",
                    "Drop --all-models (the xref answer already covers every module), or drop --xref for the text scan."));
            return RenderHelpers.Render(kind, FindViaXref(settings));
        }

        // Regex scan over indexed X++ source — shared with the unified
        // `find_references` MCP tool (D365FO.Mcp.ToolHandlers.FindReferences).
        // Wrapped so an unexpected disk/index error surfaces as a clear
        // ToolResult.Fail instead of an unhandled exception (issue #101).
        try
        {
            var repo = RepoFactory.Create();
            var result = new D365FO.Mcp.ToolHandlers(repo)
                .FindReferences(settings.Name, settings.Kind, settings.Model, settings.Limit, settings.AllModels);
            return RenderHelpers.Render(kind, result);
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                "SCAN_FAILED",
                $"find refs failed: {ex.Message}",
                "Try narrowing --kind/--model, or run `d365fo doctor --output json` to confirm the index is healthy."));
        }
    }

    private static readonly string[] XrefReferenceKinds =
        ["Declaration", "Set", "Read", "Call", "Reference", "Type", "Extends", "Implements"];

    private static ToolResult<object> FindViaXref(Settings settings)
    {
        // DYNAMICSXREFDB is keyed by module (package), and the bridge query takes no model —
        // silently dropping --model would widen the answer to every module.
        if (!string.IsNullOrWhiteSpace(settings.Model))
            return ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                "--model cannot be combined with --xref: DYNAMICSXREFDB is keyed by module, not model.",
                "Drop --model — every xref row carries its `module` — or drop --xref for the text scan, which honours --model.");

        // --kind means an artifact kind to the text scan and a reference kind to the xref
        // query; `--kind class` here would filter on a reference kind that does not exist
        // and return an empty list.
        string? refKind = null;
        if (!string.IsNullOrWhiteSpace(settings.Kind))
        {
            refKind = XrefReferenceKinds.FirstOrDefault(k =>
                string.Equals(k, settings.Kind.Trim(), StringComparison.OrdinalIgnoreCase));
            if (refKind is null)
                return ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                    $"--kind '{settings.Kind}' is not a reference kind. With --xref, --kind filters on how the symbol is used.",
                    "Use one of: " + string.Join(" | ", XrefReferenceKinds) + ". Every xref row's `source` path names the referencing artifact.");
        }

        var (xref, error) = BridgeGate.FindReferencesViaXref(settings.Name, refKind, settings.Limit);
        if (xref is null)
            return ToolResult<object>.Fail(D365FoErrorCodes.XrefUnavailable,
                "--xref was requested but DYNAMICSXREFDB could not be queried: " + error,
                "Fix the bridge (`d365fo doctor --output json`), or drop --xref for the text scan over indexed X++ "
                + "source — it is not compiler-resolved and can miss references made through variables and aliases.");

        xref["_source"] = "xrefdb";
        return ToolResult<object>.Success((object)xref);
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

        try
        {
            var repo = RepoFactory.Create();
            if (!ModelFilter.TryResolve(repo, settings.Model, out var model, out var badModel))
                return RenderHelpers.Render(kind, badModel!);
            var result = FormPatternMiner.Analyze(
                repo, settings.Pattern, settings.Table,
                settings.SimilarTo, model, settings.Limit);
            return RenderHelpers.Render(kind, ToolResult<object>.Success(result));
        }
        catch (FormPatternMiner.ReferenceNotFoundException ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                "FORM_NOT_FOUND", ex.Message,
                "Run `d365fo index extract` (or refresh) and retry, or check the spelling with `d365fo search form`."));
        }
    }
}

public sealed class FindRelatedCommand : Command<FindRelatedCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<RELATION>")]
        [System.ComponentModel.Description("name-search|refs|table-relations|coc|security|extensions|handlers|table-methods|table-indexes|table-delete-actions")]
        public string Relation { get; init; } = "";

        [CommandArgument(1, "<NAME>")]
        public string Name { get; init; } = "";

        [CommandOption("--kind <KIND>")]
        [System.ComponentModel.Description("Optional kind filter or security object type.")]
        public string? Kind { get; init; }

        [CommandOption("--method <NAME>")]
        [System.ComponentModel.Description("Method filter for relation=coc.")]
        public string? Method { get; init; }

        [CommandOption("-l|--limit <N>")]
        public int Limit { get; init; } = 100;
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var output = OutputMode.Resolve(settings.Output);
        if (string.IsNullOrWhiteSpace(settings.Relation) || string.IsNullOrWhiteSpace(settings.Name))
            return RenderHelpers.Render(output, ToolResult<object>.Fail("BAD_INPUT", "Relation and name are required."));

        var repo = RepoFactory.Create();
        return RenderHelpers.NormalizeKind(settings.Relation) switch
        {
            "namesearch" or "name-search" => RenderUsages(output, repo, settings.Name, settings.Limit),
            "refs" or "references" => RenderRefs(output, repo, settings.Name, settings.Kind, settings.Limit),
            "tablerelations" or "relations" => RenderItems(output, repo.GetTableRelations(settings.Name)),
            "coc" => RenderItems(output, repo.FindCocExtensions(settings.Name, settings.Method)),
            "security" => RenderHelpers.Render(output, ToolResult<object>.Success(repo.GetSecurityCoverage(
                settings.Name,
                string.IsNullOrWhiteSpace(settings.Kind) ? "Menuitem" : settings.Kind))),
            "extensions" => RenderItems(output, repo.FindExtensions(settings.Name, settings.Kind)),
            "handlers" or "eventhandlers" => RenderItems(output, repo.FindEventSubscribers(settings.Name, settings.Kind)),
            "tablemethods" => RenderItems(output, repo.GetTableMethods(settings.Name)),
            "tableindexes" => RenderItems(output, repo.GetTableIndexes(settings.Name)),
            "tabledeleteactions" => RenderItems(output, repo.GetTableDeleteActions(settings.Name)),
            _ => RenderHelpers.Render(output, ToolResult<object>.Fail(
                "BAD_INPUT",
                $"Unsupported relation '{settings.Relation}'.",
                "Use name-search, refs, table-relations, coc, security, extensions, handlers, table-methods, table-indexes, or table-delete-actions.")),
        };
    }

    private static int RenderUsages(OutputMode.Kind output, D365FO.Core.Index.MetadataRepository repo, string name, int limit)
    {
        var items = repo.FindUsages(name, limit)
            .Select(t => new { kind = t.Kind, name = t.Name, model = t.Model })
            .ToList();
        return RenderHelpers.Render(output, ToolResult<object>.Success(new { count = items.Count, items }));
    }

    private static int RenderRefs(OutputMode.Kind output, D365FO.Core.Index.MetadataRepository repo, string name, string? kind, int limit)
    {
        // Shared with the MCP find_references tool. Uses the MethodSourceFts index
        // when it's populated (index extract --index-source), else scans on disk.
        var result = D365FO.Core.Index.MethodSourceSearch.Find(repo, name, kind, model: null, limit: limit);
        var items = result.Hits.Select(h => new
        {
            kind = h.Kind,
            name = h.Name,
            model = h.Model,
            method = h.Method,
            path = h.Path,
            matches = h.Matches.Select(l => new { line = l.Line, text = l.Text }),
        });
        return RenderHelpers.Render(output, ToolResult<object>.Success(new
        {
            needle = result.Needle,
            via = result.Via,
            filesScanned = result.FilesScanned,
            scope = result.Coverage?.Scope,
            coverage = D365FO.Core.Index.MethodSourceSearch.CoveragePayload(result.Coverage),
            match = D365FO.Core.Index.MethodSourceSearch.MemberMatchPayload(result.Member),
            hint = result.Hint,
            count = result.Hits.Count,
            truncated = result.Truncated,
            items,
        }));
    }

    private static int RenderItems<T>(OutputMode.Kind output, IReadOnlyList<T> items)
        => RenderHelpers.Render(output, ToolResult<object>.Success(new { count = items.Count, items }));
}

/// <summary>Find all RunBaseBatch / SysOperationServiceController subclasses in the index.</summary>
public sealed class FindBatchJobsCommand : Command<FindBatchJobsCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandOption("--model <NAME>")]
        [System.ComponentModel.Description("Restrict to a single model.")]
        public string? Model { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var repo = RepoFactory.Create();
        if (!ModelFilter.TryResolve(repo, settings.Model, out var model, out var badModel))
            return RenderHelpers.Render(kind, badModel!);
        var jobs = repo.FindBatchJobs(model);
        return RenderHelpers.Render(kind, ToolResult<object>.Success(new { count = jobs.Count, items = jobs }));
    }
}
