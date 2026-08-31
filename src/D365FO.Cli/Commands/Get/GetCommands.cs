using D365FO.Core;
using D365FO.Core.Index;
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

        if (BridgeGate.ShouldTry())
        {
            var bridged = BridgeGate.TryReadTable(settings.Name);
            if (bridged is not null)
            {
                return RenderHelpers.Render(kind, ToolResult<object>.Success(bridged));
            }
        }

        var repo = RepoFactory.Create();
        var details = repo.GetTableDetails(settings.Name);

        if (details is null)
        {
            var hint = NameSuggester.HintFor(repo, NameSuggester.Kind.Table, settings.Name)
                       ?? "Run 'd365fo index build' after extracting metadata.";
            return RenderHelpers.Render(kind,
                ToolResult<object>.Fail("TABLE_NOT_FOUND", $"Table '{settings.Name}' not found in index.", hint));
        }

        var result = ToolResult<object>.Success(new
        {
            table = details.Table,
            fields = details.Fields,
            relations = details.Relations,
            methods = details.Methods,
            indexes = details.Indexes,
            deleteActions = details.DeleteActions,
        });

        return RenderHelpers.Render(kind, result, _ =>
        {
            AnsiConsole.MarkupLine($"[bold]{RenderHelpers.Escape(details.Table.Name)}[/] — {RenderHelpers.Escape(details.Table.Label) ?? "(no label)"}  [grey]({details.Table.Model})[/]");
            var table = new Table().AddColumn("Field").AddColumn("Type/EDT").AddColumn("Label").AddColumn("Mand.");
            foreach (var f in details.Fields)
                table.AddRow(f.Name, f.EdtName ?? f.Type ?? "-", RenderHelpers.Escape(f.Label) ?? "-", f.Mandatory ? "yes" : "");
            AnsiConsole.Write(table);
            if (details.Indexes.Count > 0)
            {
                AnsiConsole.MarkupLine("[bold]Indexes[/]");
                var ix = new Table().AddColumn("Name").AddColumn("Fields").AddColumn("AllowDup").AddColumn("AltKey");
                foreach (var i in details.Indexes)
                    ix.AddRow(i.Name, i.FieldsCsv ?? "-", i.AllowDuplicates ? "yes" : "", i.AlternateKey ? "yes" : "");
                AnsiConsole.Write(ix);
            }
            if (details.Relations.Count > 0)
            {
                AnsiConsole.MarkupLine("[bold]Relations[/]");
                var rel = new Table().AddColumn("From").AddColumn("To").AddColumn("Cardinality").AddColumn("Name");
                foreach (var r in details.Relations)
                    rel.AddRow(r.FromTable, r.ToTable, r.Cardinality ?? "-", r.RelationName ?? "-");
                AnsiConsole.Write(rel);
            }
            if (details.DeleteActions.Count > 0)
            {
                AnsiConsole.MarkupLine("[bold]Delete actions[/]");
                var da = new Table().AddColumn("Name").AddColumn("Related").AddColumn("Action");
                foreach (var d in details.DeleteActions)
                    da.AddRow(d.Name ?? "-", d.RelatedTable, d.DeleteAction ?? "-");
                AnsiConsole.Write(da);
            }
            if (details.Methods.Count > 0)
            {
                AnsiConsole.MarkupLine("[bold]Methods[/]");
                var mt = new Table().AddColumn("Name").AddColumn("Return").AddColumn("Static").AddColumn("Signature");
                foreach (var m in details.Methods)
                    mt.AddRow(m.Name, m.ReturnType ?? "-", m.IsStatic ? "yes" : "", RenderHelpers.Escape(m.Signature) ?? "-");
                AnsiConsole.Write(mt);
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
        if (BridgeGate.ShouldTry())
        {
            var bridged = BridgeGate.TryReadEdt(settings.Name);
            if (bridged is not null)
            {
                return RenderHelpers.Render(kind, ToolResult<object>.Success(bridged));
            }
        }
        var repo = RepoFactory.Create();
        var resolved = repo.GetEdtResolved(settings.Name);
        var result = resolved is null
            ? ToolResult<object>.Fail("EDT_NOT_FOUND", $"EDT '{settings.Name}' not found.",
                NameSuggester.HintFor(repo, NameSuggester.Kind.Edt, settings.Name))
            : ToolResult<object>.Success(
                (object)resolved.Value.Edt,
                resolved.Value.StringSizeInheritedFrom is null
                    ? null
                    : new List<string>
                    {
                        $"StringSize {resolved.Value.Edt.StringSize} is inherited from {resolved.Value.StringSizeInheritedFrom} — this EDT declares none of its own.",
                    });
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

        // Bridge-primary path (opt-in via D365FO_BRIDGE_ENABLED=1).
        // Silently falls back to the SQLite index when the bridge is missing,
        // disabled, or returns NOT_IMPLEMENTED (POC stub). Errors from a
        // running bridge are surfaced via --verbose only; the final result
        // is still produced from the index so the user never sees a hole.
        if (BridgeGate.ShouldTry())
        {
            var bridged = BridgeGate.TryReadClass(settings.Name);
            if (bridged is not null)
            {
                return RenderHelpers.Render(kind, ToolResult<object>.Success(bridged));
            }
        }

        var repo = RepoFactory.Create();
        var details = repo.GetClassDetails(settings.Name);
        var result = details is null
            ? ToolResult<object>.Fail("CLASS_NOT_FOUND", $"Class '{settings.Name}' not found.",
                NameSuggester.HintFor(repo, NameSuggester.Kind.Class, settings.Name))
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
        [System.ComponentModel.Description("Required. Matched literally against the indexed AOT ObjectType — e.g. MenuItemDisplay|MenuItemAction|MenuItemOutput|Table|Form|Report|Class. There is no usable default: entry points are always indexed as one of the specific MenuItem* kinds, never bare \"Menuitem\".")]
        public string? Type { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        if (string.IsNullOrWhiteSpace(settings.Type))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                "--type is required. Use the specific indexed ObjectType, e.g. MenuItemDisplay|MenuItemAction|MenuItemOutput|Table|Form|Report|Class."));
        var repo = RepoFactory.Create();
        var coverage = repo.GetSecurityCoverage(settings.Object, settings.Type);
        return RenderHelpers.Render(kind, ToolResult<object>.Success(coverage));
    }
}

public sealed class GetEnumCommand : Command<GetEnumCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<NAME>")]
        public string Name { get; init; } = "";
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        if (BridgeGate.ShouldTry())
        {
            var bridged = BridgeGate.TryReadEnum(settings.Name);
            if (bridged is not null)
            {
                return RenderHelpers.Render(kind, ToolResult<object>.Success(bridged));
            }
        }
        var repo = RepoFactory.Create();
        var details = repo.GetEnum(settings.Name);
        return RenderHelpers.Render(kind, details is null
            ? ToolResult<object>.Fail("ENUM_NOT_FOUND", $"Enum '{settings.Name}' not found.",
                NameSuggester.HintFor(repo, NameSuggester.Kind.Enum, settings.Name))
            : ToolResult<object>.Success(details));
    }
}

public sealed class GetLabelCommand : Command<GetLabelCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<FILE_OR_KEY>")]
        [System.ComponentModel.Description("Label file name (e.g. SYS) when <KEY> is given, otherwise the bare key / @File+Id token to look up across all files.")]
        public string File { get; init; } = "";

        [CommandArgument(1, "[KEY]")]
        [System.ComponentModel.Description("Label key inside <FILE>. Omit to search every indexed file for <FILE_OR_KEY> as a key.")]
        public string? Key { get; init; }

        [CommandOption("--lang <LANG>")]
        public string Language { get; init; } = "en-us";
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var repo = RepoFactory.Create();

        // Single-argument form: treat as either an @File+Id token or a bare
        // key to look up across every indexed label file. Keeps muscle
        // memory from `d365fo resolve label` working in the `get` tree.
        if (string.IsNullOrWhiteSpace(settings.Key))
        {
            var needle = settings.File;
            if (string.IsNullOrWhiteSpace(needle))
            {
                return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                    "BAD_INPUT", "Label key required.",
                    "Pass `<FILE> <KEY>` or a single `@File+Id` / bare key argument."));
            }

            IReadOnlyList<D365FO.Core.Index.LabelMatch> hits;
            IReadOnlyList<D365FO.Core.Index.LabelMatch> likeHits = Array.Empty<D365FO.Core.Index.LabelMatch>();
            if (needle.StartsWith('@'))
            {
                hits = repo.ResolveLabel(needle, new[] { settings.Language });
            }
            else
            {
                // SearchLabels is case-insensitive on the LIKE side; keep a
                // copy for the fallback hint and post-filter to exact Key
                // (case-insensitive) for the primary answer so the result
                // stays focused.
                likeHits = repo.SearchLabels(needle, new[] { settings.Language });
                hits = likeHits
                    .Where(h => string.Equals(h.Key, needle, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            if (hits.Count == 0)
            {
                var hint = likeHits.Count > 0
                    ? $"No exact key match. `d365fo search label {needle}` returns {likeHits.Count} substring hit(s)."
                    : $"Try `d365fo search label {needle}` for substring matches, or pass `@File+Id` / `<FILE> <KEY>`.";
                return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                    "LABEL_NOT_FOUND", $"No label with key '{needle}' in language '{settings.Language}'.", hint));
            }

            var items = settings.RawText
                ? hits
                : hits.Select(h => h with { Value = D365FO.Core.StringSanitizer.Sanitize(h.Value) }).ToList();
            return RenderHelpers.Render(kind, ToolResult<object>.Success(new { count = items.Count, items }));
        }

        var hit = repo.GetLabel(settings.File, settings.Language, settings.Key);
        if (hit is null)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("LABEL_NOT_FOUND", $"{settings.File}/{settings.Language}:{settings.Key} not found."));
        if (!settings.RawText)
            hit = hit with { Value = D365FO.Core.StringSanitizer.Sanitize(hit.Value) };
        return RenderHelpers.Render(kind, ToolResult<object>.Success(hit));
    }
}

public sealed class GetFormCommand : Command<GetFormCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<NAME>")]
        public string Name { get; init; } = "";
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        if (BridgeGate.ShouldTry())
        {
            var bridged = BridgeGate.TryReadForm(settings.Name);
            if (bridged is not null)
            {
                return RenderHelpers.Render(kind, ToolResult<object>.Success(bridged));
            }
        }
        var repo = RepoFactory.Create();
        var f = repo.GetForm(settings.Name);
        return RenderHelpers.Render(kind, f is null
            ? ToolResult<object>.Fail("FORM_NOT_FOUND", $"Form '{settings.Name}' not found.",
                NameSuggester.HintFor(repo, NameSuggester.Kind.Form, settings.Name))
            : ToolResult<object>.Success(f));
    }
}

public sealed class GetRoleCommand : Command<GetRoleCommand.Settings>
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
        var r = repo.GetSecurityRole(settings.Name);
        return RenderHelpers.Render(kind, r is null
            ? ToolResult<object>.Fail("ROLE_NOT_FOUND", $"Role '{settings.Name}' not found.")
            : ToolResult<object>.Success(r));
    }
}

public sealed class GetDutyCommand : Command<GetDutyCommand.Settings>
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
        var d = repo.GetSecurityDuty(settings.Name);
        return RenderHelpers.Render(kind, d is null
            ? ToolResult<object>.Fail("DUTY_NOT_FOUND", $"Duty '{settings.Name}' not found.")
            : ToolResult<object>.Success(d));
    }
}

public sealed class GetPrivilegeCommand : Command<GetPrivilegeCommand.Settings>
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
        var p = repo.GetSecurityPrivilege(settings.Name);
        return RenderHelpers.Render(kind, p is null
            ? ToolResult<object>.Fail("PRIVILEGE_NOT_FOUND", $"Privilege '{settings.Name}' not found.")
            : ToolResult<object>.Success(p));
    }
}

public sealed class GetQueryCommand : Command<GetQueryCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<NAME>")] public string Name { get; init; } = "";
    }
    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var q = RepoFactory.Create().GetQuery(settings.Name);
        return RenderHelpers.Render(kind, q is null
            ? ToolResult<object>.Fail("QUERY_NOT_FOUND", $"Query '{settings.Name}' not found.")
            : ToolResult<object>.Success(q));
    }
}

public sealed class GetViewCommand : Command<GetViewCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<NAME>")] public string Name { get; init; } = "";
    }
    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var v = RepoFactory.Create().GetView(settings.Name);
        return RenderHelpers.Render(kind, v is null
            ? ToolResult<object>.Fail("VIEW_NOT_FOUND", $"View '{settings.Name}' not found.")
            : ToolResult<object>.Success(v));
    }
}

public sealed class GetEntityCommand : Command<GetEntityCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<NAME>")] public string Name { get; init; } = "";

        [CommandOption("--odata-metadata")]
        [System.ComponentModel.Description("Emit OData $metadata fragment (EntityType declaration) instead of the standard JSON detail.")]
        public bool ODataMetadata { get; init; }
    }
    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var e = RepoFactory.Create().GetDataEntity(settings.Name);
        if (e is null)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("ENTITY_NOT_FOUND", $"Data entity '{settings.Name}' not found."));

        if (settings.ODataMetadata)
        {
            var metadata = BuildODataMetadata(e);
            return RenderHelpers.Render(kind, ToolResult<object>.Success(new { metadata }));
        }

        return RenderHelpers.Render(kind, ToolResult<object>.Success(e));
    }

    private static string BuildODataMetadata(D365FO.Core.Index.DataEntityDetails e)
    {
        var entityName = e.Entity.PublicEntityName ?? e.Entity.Name;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"<EntityType Name=\"{entityName}\">");

        // Key fields: mandatory non-readonly fields treated as key candidates.
        var keyFields = e.Fields.Where(f => f.IsMandatory && !f.IsReadOnly).ToList();
        if (keyFields.Count > 0)
        {
            sb.AppendLine("  <Key>");
            foreach (var kf in keyFields)
                sb.AppendLine($"    <PropertyRef Name=\"{kf.Name}\" />");
            sb.AppendLine("  </Key>");
        }

        foreach (var f in e.Fields)
        {
            var nullable = f.IsMandatory ? "" : " Nullable=\"true\"";
            sb.AppendLine($"  <Property Name=\"{f.Name}\" Type=\"Edm.String\"{nullable} />");
        }

        sb.AppendLine($"</EntityType>");
        sb.AppendLine($"<EntitySet Name=\"{e.Entity.PublicCollectionName ?? entityName + "s"}\" EntityType=\"{entityName}\" />");
        return sb.ToString();
    }
}

public sealed class GetReportCommand : Command<GetReportCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<NAME>")] public string Name { get; init; } = "";
    }
    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var r = RepoFactory.Create().GetReport(settings.Name);
        return RenderHelpers.Render(kind, r is null
            ? ToolResult<object>.Fail("REPORT_NOT_FOUND", $"Report '{settings.Name}' not found.")
            : ToolResult<object>.Success(r));
    }
}

public sealed class GetServiceCommand : Command<GetServiceCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<NAME>")] public string Name { get; init; } = "";
    }
    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var s = RepoFactory.Create().GetService(settings.Name);
        return RenderHelpers.Render(kind, s is null
            ? ToolResult<object>.Fail("SERVICE_NOT_FOUND", $"Service '{settings.Name}' not found.")
            : ToolResult<object>.Success(s));
    }
}

public sealed class GetServiceGroupCommand : Command<GetServiceGroupCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<NAME>")] public string Name { get; init; } = "";
    }
    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var g = RepoFactory.Create().GetServiceGroup(settings.Name);
        return RenderHelpers.Render(kind, g is null
            ? ToolResult<object>.Fail("SERVICE_GROUP_NOT_FOUND", $"Service group '{settings.Name}' not found.")
            : ToolResult<object>.Success(g));
    }
}

public sealed class GetObjectCommand : Command<GetObjectCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<KIND>")]
        [System.ComponentModel.Description("class|table|edt|enum|form|menu-item|query|view|entity|report|service|service-group|role|duty|privilege")]
        public string Kind { get; init; } = "";

        [CommandArgument(1, "<NAME>")]
        public string Name { get; init; } = "";
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var output = OutputMode.Resolve(settings.Output);
        if (string.IsNullOrWhiteSpace(settings.Kind) || string.IsNullOrWhiteSpace(settings.Name))
            return RenderHelpers.Render(output, ToolResult<object>.Fail("BAD_INPUT", "Kind and name are required."));

        var repo = RepoFactory.Create();
        var (data, code, message) = ObjectLookup.Fetch(repo, settings.Kind, settings.Name);
        return RenderHelpers.Render(output, data is null
            ? ToolResult<object>.Fail(code!, message!,
                code == "BAD_INPUT" ? ObjectLookup.SupportedKindsHint : null)
            : ToolResult<object>.Success(data));
    }
}

/// <summary>
/// Batch object lookup — port of the upstream MCP <c>batch_get_info</c> tool.
/// Fetches up to 10 objects in one CLI invocation; one failed lookup never
/// fails the batch (each item carries its own ok/error).
/// </summary>
public sealed class GetBatchCommand : Command<GetBatchCommand.Settings>
{
    public const int MaxItems = 10;

    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<SPEC>")]
        [System.ComponentModel.Description("Object specs, each as <kind>:<name> (e.g. table:CustTable class:SalesLineType). Max 10 per call.")]
        public string[] Specs { get; init; } = Array.Empty<string>();
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var output = OutputMode.Resolve(settings.Output);
        if (settings.Specs.Length == 0)
            return RenderHelpers.Render(output, ToolResult<object>.Fail("BAD_INPUT",
                "At least one <kind>:<name> spec is required.",
                "Example: d365fo get batch table:CustTable class:CustTableType edt:CustAccount"));
        if (settings.Specs.Length > MaxItems)
            return RenderHelpers.Render(output, ToolResult<object>.Fail("BAD_INPUT",
                $"Too many objects: {settings.Specs.Length} (max {MaxItems} per call).",
                "Split the request into multiple `get batch` calls."));

        var repo = RepoFactory.Create();
        var items = new List<object>(settings.Specs.Length);
        var found = 0;
        foreach (var spec in settings.Specs)
        {
            var idx = spec.IndexOf(':');
            if (idx <= 0 || idx == spec.Length - 1)
            {
                items.Add(new { spec, ok = false, error = new { code = "BAD_INPUT", message = $"Spec '{spec}' is not <kind>:<name>." } });
                continue;
            }
            var kind = spec[..idx].Trim();
            var name = spec[(idx + 1)..].Trim();
            var (data, code, message) = ObjectLookup.Fetch(repo, kind, name);
            if (data is null)
            {
                items.Add(new { spec, kind, name, ok = false, error = new { code, message } });
            }
            else
            {
                found++;
                items.Add(new { spec, kind, name, ok = true, data });
            }
        }

        return RenderHelpers.Render(output, ToolResult<object>.Success(new
        {
            requested = settings.Specs.Length,
            found,
            items,
        }));
    }
}

public sealed class GetBusinessEventCommand : Command<GetBusinessEventCommand.Settings>
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
        var item = repo.GetBusinessEvent(settings.Name);
        return RenderHelpers.Render(kind, item is null
            ? ToolResult<object>.Fail("NOT_FOUND", $"Business event '{settings.Name}' not found.")
            : ToolResult<object>.Success(item));
    }
}

public sealed class GetSecurityPolicyCommand : Command<GetSecurityPolicyCommand.Settings>
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
        var item = repo.GetSecurityPolicy(settings.Name);
        return RenderHelpers.Render(kind, item is null
            ? ToolResult<object>.Fail("NOT_FOUND", $"Security policy '{settings.Name}' not found.")
            : ToolResult<object>.Success(item));
    }
}
