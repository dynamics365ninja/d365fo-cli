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
        var repo = RepoFactory.Create();
        var details = repo.GetEnum(settings.Name);
        return RenderHelpers.Render(kind, details is null
            ? ToolResult<object>.Fail("ENUM_NOT_FOUND", $"Enum '{settings.Name}' not found.")
            : ToolResult<object>.Success(details));
    }
}

public sealed class GetLabelCommand : Command<GetLabelCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<FILE>")]
        public string File { get; init; } = "";

        [CommandArgument(1, "<KEY>")]
        public string Key { get; init; } = "";

        [CommandOption("--lang <LANG>")]
        public string Language { get; init; } = "en-us";
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var repo = RepoFactory.Create();
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
        var repo = RepoFactory.Create();
        var f = repo.GetForm(settings.Name);
        return RenderHelpers.Render(kind, f is null
            ? ToolResult<object>.Fail("FORM_NOT_FOUND", $"Form '{settings.Name}' not found.")
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
    }
    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var e = RepoFactory.Create().GetDataEntity(settings.Name);
        return RenderHelpers.Render(kind, e is null
            ? ToolResult<object>.Fail("ENTITY_NOT_FOUND", $"Data entity '{settings.Name}' not found.")
            : ToolResult<object>.Success(e));
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
