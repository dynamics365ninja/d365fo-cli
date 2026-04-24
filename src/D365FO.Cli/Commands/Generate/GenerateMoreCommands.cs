using D365FO.Core;
using D365FO.Core.Scaffolding;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Generate;

/// <summary>Scaffolds an <c>AxDataEntityView</c>. ROADMAP §6.</summary>
public sealed class GenerateEntityCommand : Command<GenerateEntityCommand.Settings>
{
    public sealed class Settings : GenerateSettings
    {
        [CommandArgument(0, "<ENTITY>")]
        public string EntityName { get; init; } = "";

        [CommandOption("--table <TABLE>")]
        [System.ComponentModel.Description("Root data source table for the entity.")]
        public string? Table { get; init; }

        [CommandOption("--public-entity <NAME>")]
        public string? PublicEntity { get; init; }

        [CommandOption("--public-collection <NAME>")]
        public string? PublicCollection { get; init; }

        [CommandOption("--field <SPEC>")]
        [System.ComponentModel.Description("Repeatable: <name>[:<dataField>[:mandatory]].")]
        public string[] Fields { get; init; } = Array.Empty<string>();

        [CommandOption("--all-fields")]
        [System.ComponentModel.Description("Populate <Fields /> from the source table's columns. Requires the table to be indexed.")]
        public bool AllFields { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        if (string.IsNullOrWhiteSpace(settings.EntityName))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "Entity name required."));
        if (string.IsNullOrWhiteSpace(settings.Table))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "--table <TABLE> required."));
        var hasInstall = !string.IsNullOrWhiteSpace(settings.InstallTo);
        var hasOut = !string.IsNullOrWhiteSpace(settings.Out);
        if (!hasInstall && !hasOut)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "--out or --install-to is required."));
        var outPath = settings.Out;
        if (hasInstall && !hasOut)
        {
            outPath = GenerateInstaller.ResolveInstallPath(kind, "AxDataEntityView", settings.EntityName, settings.InstallTo!, out var fail);
            if (fail.HasValue) return fail.Value;
        }

        var fields = settings.Fields.Select(ParseField).ToList();
        var autoFromTable = false;
        if (fields.Count == 0 && settings.AllFields)
        {
            var repo = RepoFactory.Create();
            var tableDetails = repo.GetTableDetails(settings.Table!);
            if (tableDetails is null)
                return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                    D365FoErrorCodes.TableNotFound,
                    $"Table '{settings.Table}' not found in the index. Extract the model first or pass explicit --field <SPEC>."));
            foreach (var f in tableDetails.Fields)
                fields.Add(new EntityFieldSpec(f.Name, f.Name, f.Mandatory));
            autoFromTable = true;
        }
        var doc = XppScaffolder.DataEntity(
            settings.EntityName, settings.Table!, settings.PublicEntity, settings.PublicCollection, fields);
        try
        {
            var res = ScaffoldFileWriter.Write(doc, outPath!, settings.Overwrite);
            return RenderHelpers.Render(kind, ToolResult<object>.Success(new
            {
                kind = "AxDataEntityView",
                name = settings.EntityName,
                table = settings.Table,
                path = res.Path,
                bytes = res.Bytes,
                backup = res.BackupPath,
                fieldCount = fields.Count,
                fieldsFromTable = autoFromTable,
                model = settings.InstallTo,
            }));
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed, ex.Message));
        }
    }

    private static EntityFieldSpec ParseField(string raw)
    {
        var parts = raw.Split(':', StringSplitOptions.TrimEntries);
        var name = parts.Length > 0 ? parts[0] : "";
        var data = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null;
        var mandatory = parts.Length > 2 && string.Equals(parts[2], "mandatory", StringComparison.OrdinalIgnoreCase);
        return new EntityFieldSpec(name, data, mandatory);
    }
}

/// <summary>Scaffolds <c>AxTableExtension</c> / <c>AxFormExtension</c> / <c>AxEdtExtension</c> / <c>AxEnumExtension</c>. ROADMAP §6.</summary>
public sealed class GenerateExtensionCommand : Command<GenerateExtensionCommand.Settings>
{
    public sealed class Settings : GenerateSettings
    {
        [CommandArgument(0, "<KIND>")]
        [System.ComponentModel.Description("Extension kind: Table, Form, Edt, Enum.")]
        public string Kind { get; init; } = "";

        [CommandArgument(1, "<TARGET>")]
        public string Target { get; init; } = "";

        [CommandOption("--suffix <SUFFIX>")]
        [System.ComponentModel.Description("Extension suffix. Defaults to the InstallTo model name or 'Extension'.")]
        public string? Suffix { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        if (string.IsNullOrWhiteSpace(settings.Kind))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "Extension kind required."));
        if (string.IsNullOrWhiteSpace(settings.Target))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "Target object required."));
        var hasInstall = !string.IsNullOrWhiteSpace(settings.InstallTo);
        var hasOut = !string.IsNullOrWhiteSpace(settings.Out);
        if (!hasInstall && !hasOut)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "--out or --install-to is required."));

        var suffix = settings.Suffix
            ?? (hasInstall ? settings.InstallTo! : "Extension");
        var axFolder = settings.Kind switch
        {
            "Table" => "AxTableExtension",
            "Form" => "AxFormExtension",
            "Edt" => "AxEdtExtension",
            "Enum" => "AxEnumExtension",
            _ => null,
        };
        if (axFolder is null)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                $"Unsupported extension kind: {settings.Kind}. Expected Table|Form|Edt|Enum."));

        var fullName = $"{settings.Target}.{suffix}";
        var outPath = settings.Out;
        if (hasInstall && !hasOut)
        {
            outPath = GenerateInstaller.ResolveInstallPath(kind, axFolder, fullName, settings.InstallTo!, out var fail);
            if (fail.HasValue) return fail.Value;
        }

        var doc = XppScaffolder.Extension(settings.Kind, settings.Target, suffix);
        try
        {
            var res = ScaffoldFileWriter.Write(doc, outPath!, settings.Overwrite);
            return RenderHelpers.Render(kind, ToolResult<object>.Success(new
            {
                kind = axFolder,
                name = fullName,
                target = settings.Target,
                suffix,
                path = res.Path,
                bytes = res.Bytes,
                backup = res.BackupPath,
                model = settings.InstallTo,
            }));
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed, ex.Message));
        }
    }
}

/// <summary>Scaffolds an event-handler subscriber class. ROADMAP §6.</summary>
public sealed class GenerateEventHandlerCommand : Command<GenerateEventHandlerCommand.Settings>
{
    public sealed class Settings : GenerateSettings
    {
        [CommandArgument(0, "<CLASS_NAME>")]
        public string ClassName { get; init; } = "";

        [CommandOption("--source-kind <KIND>")]
        [System.ComponentModel.Description("Form | FormDataSource | Table | Class.")]
        public string SourceKind { get; init; } = "Form";

        [CommandOption("--source-object <NAME>")]
        public string? SourceObject { get; init; }

        [CommandOption("--event <NAME>")]
        [System.ComponentModel.Description("E.g. OnInitialized / OnValidatingWrite / PostActivate.")]
        public string Event { get; init; } = "OnInitialized";

        [CommandOption("--method <NAME>")]
        public string HandlerMethod { get; init; } = "OnEvent";
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        if (string.IsNullOrWhiteSpace(settings.ClassName))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "Class name required."));
        if (string.IsNullOrWhiteSpace(settings.SourceObject))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "--source-object required."));
        var hasInstall = !string.IsNullOrWhiteSpace(settings.InstallTo);
        var hasOut = !string.IsNullOrWhiteSpace(settings.Out);
        if (!hasInstall && !hasOut)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "--out or --install-to is required."));
        var outPath = settings.Out;
        if (hasInstall && !hasOut)
        {
            outPath = GenerateInstaller.ResolveInstallPath(kind, "AxClass", settings.ClassName, settings.InstallTo!, out var fail);
            if (fail.HasValue) return fail.Value;
        }

        var doc = XppScaffolder.EventHandler(settings.ClassName, settings.SourceKind, settings.SourceObject!, settings.Event, settings.HandlerMethod);
        try
        {
            var res = ScaffoldFileWriter.Write(doc, outPath!, settings.Overwrite);
            return RenderHelpers.Render(kind, ToolResult<object>.Success(new
            {
                kind = "AxClass",
                role = "EventHandler",
                name = settings.ClassName,
                sourceKind = settings.SourceKind,
                sourceObject = settings.SourceObject,
                @event = settings.Event,
                method = settings.HandlerMethod,
                path = res.Path,
                bytes = res.Bytes,
                backup = res.BackupPath,
                model = settings.InstallTo,
            }));
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed, ex.Message));
        }
    }
}

/// <summary>Scaffolds a security privilege granting a single entry point. ROADMAP §6.</summary>
public sealed class GeneratePrivilegeCommand : Command<GeneratePrivilegeCommand.Settings>
{
    public sealed class Settings : GenerateSettings
    {
        [CommandArgument(0, "<NAME>")]
        public string Name { get; init; } = "";

        [CommandOption("--entry-point <NAME>")]
        public string? EntryPoint { get; init; }

        [CommandOption("--entry-kind <KIND>")]
        [System.ComponentModel.Description("MenuItemDisplay | MenuItemAction | MenuItemOutput | WebMenuItem.")]
        public string EntryKind { get; init; } = "MenuItemDisplay";

        [CommandOption("--entry-object <NAME>")]
        [System.ComponentModel.Description("Target object name when different from --entry-point.")]
        public string? EntryObject { get; init; }

        [CommandOption("--access <LEVEL>")]
        [System.ComponentModel.Description("NoAccess | Read | Update | Create | Correct | Delete.")]
        public string Access { get; init; } = "Read";

        [CommandOption("--label <TEXT>")]
        public string? Label { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        if (string.IsNullOrWhiteSpace(settings.Name))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "Privilege name required."));
        if (string.IsNullOrWhiteSpace(settings.EntryPoint))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "--entry-point required."));
        var hasInstall = !string.IsNullOrWhiteSpace(settings.InstallTo);
        var hasOut = !string.IsNullOrWhiteSpace(settings.Out);
        if (!hasInstall && !hasOut)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "--out or --install-to is required."));
        var outPath = settings.Out;
        if (hasInstall && !hasOut)
        {
            outPath = GenerateInstaller.ResolveInstallPath(kind, "AxSecurityPrivilege", settings.Name, settings.InstallTo!, out var fail);
            if (fail.HasValue) return fail.Value;
        }

        var doc = XppScaffolder.Privilege(settings.Name, settings.EntryPoint!, settings.EntryKind, settings.EntryObject, settings.Access, settings.Label);
        try
        {
            var res = ScaffoldFileWriter.Write(doc, outPath!, settings.Overwrite);
            return RenderHelpers.Render(kind, ToolResult<object>.Success(new
            {
                kind = "AxSecurityPrivilege",
                name = settings.Name,
                entryPoint = settings.EntryPoint,
                entryKind = settings.EntryKind,
                access = settings.Access,
                path = res.Path,
                bytes = res.Bytes,
                backup = res.BackupPath,
                model = settings.InstallTo,
            }));
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed, ex.Message));
        }
    }
}

/// <summary>Scaffolds a security duty grouping one or more privileges. ROADMAP §6.</summary>
public sealed class GenerateDutyCommand : Command<GenerateDutyCommand.Settings>
{
    public sealed class Settings : GenerateSettings
    {
        [CommandArgument(0, "<NAME>")]
        public string Name { get; init; } = "";

        [CommandOption("--privilege <NAME>")]
        [System.ComponentModel.Description("Repeatable. Privileges to aggregate under this duty.")]
        public string[] Privileges { get; init; } = Array.Empty<string>();

        [CommandOption("--label <TEXT>")]
        public string? Label { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        if (string.IsNullOrWhiteSpace(settings.Name))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "Duty name required."));
        if (settings.Privileges.Length == 0)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "At least one --privilege required."));
        var hasInstall = !string.IsNullOrWhiteSpace(settings.InstallTo);
        var hasOut = !string.IsNullOrWhiteSpace(settings.Out);
        if (!hasInstall && !hasOut)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "--out or --install-to is required."));
        var outPath = settings.Out;
        if (hasInstall && !hasOut)
        {
            outPath = GenerateInstaller.ResolveInstallPath(kind, "AxSecurityDuty", settings.Name, settings.InstallTo!, out var fail);
            if (fail.HasValue) return fail.Value;
        }

        var doc = XppScaffolder.Duty(settings.Name, settings.Privileges, settings.Label);
        try
        {
            var res = ScaffoldFileWriter.Write(doc, outPath!, settings.Overwrite);
            return RenderHelpers.Render(kind, ToolResult<object>.Success(new
            {
                kind = "AxSecurityDuty",
                name = settings.Name,
                privilegeCount = settings.Privileges.Length,
                privileges = settings.Privileges,
                path = res.Path,
                bytes = res.Bytes,
                backup = res.BackupPath,
                model = settings.InstallTo,
            }));
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed, ex.Message));
        }
    }
}

/// <summary>
/// Scaffolds a new <c>AxSecurityRole</c> or, with <c>--add-to</c>, appends duty /
/// privilege references to an existing role document. ROADMAP §6.
/// </summary>
public sealed class GenerateRoleCommand : Command<GenerateRoleCommand.Settings>
{
    public sealed class Settings : GenerateSettings
    {
        [CommandArgument(0, "<NAME>")]
        public string Name { get; init; } = "";

        [CommandOption("--duty <NAME>")]
        [System.ComponentModel.Description("Repeatable. Duties referenced by this role.")]
        public string[] Duties { get; init; } = Array.Empty<string>();

        [CommandOption("--privilege <NAME>")]
        [System.ComponentModel.Description("Repeatable. Privileges referenced directly by this role.")]
        public string[] Privileges { get; init; } = Array.Empty<string>();

        [CommandOption("--label <TEXT>")]
        public string? Label { get; init; }

        [CommandOption("--description <TEXT>")]
        public string? Description { get; init; }

        [CommandOption("--add-to <PATH>")]
        [System.ComponentModel.Description("Path to an existing AxSecurityRole XML file; duties/privileges are merged in-place instead of creating a new file.")]
        public string? AddTo { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);

        if (!string.IsNullOrWhiteSpace(settings.AddTo))
            return ExecuteAddTo(kind, settings);

        if (string.IsNullOrWhiteSpace(settings.Name))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "Role name required."));
        if (settings.Duties.Length == 0 && settings.Privileges.Length == 0)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                D365FoErrorCodes.BadInput,
                "At least one --duty or --privilege required."));

        var hasInstall = !string.IsNullOrWhiteSpace(settings.InstallTo);
        var hasOut = !string.IsNullOrWhiteSpace(settings.Out);
        if (!hasInstall && !hasOut)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "--out or --install-to is required."));
        var outPath = settings.Out;
        if (hasInstall && !hasOut)
        {
            outPath = GenerateInstaller.ResolveInstallPath(kind, "AxSecurityRole", settings.Name, settings.InstallTo!, out var fail);
            if (fail.HasValue) return fail.Value;
        }

        var doc = XppScaffolder.Role(settings.Name, settings.Duties, settings.Privileges, settings.Label, settings.Description);
        try
        {
            var res = ScaffoldFileWriter.Write(doc, outPath!, settings.Overwrite);
            return RenderHelpers.Render(kind, ToolResult<object>.Success(new
            {
                kind = "AxSecurityRole",
                name = settings.Name,
                dutyCount = settings.Duties.Length,
                privilegeCount = settings.Privileges.Length,
                duties = settings.Duties,
                privileges = settings.Privileges,
                path = res.Path,
                bytes = res.Bytes,
                backup = res.BackupPath,
                model = settings.InstallTo,
            }));
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed, ex.Message));
        }
    }

    private static int ExecuteAddTo(OutputMode.Kind kind, Settings settings)
    {
        var path = settings.AddTo!;
        if (!System.IO.File.Exists(path))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, $"Role file not found: {path}"));
        if (settings.Duties.Length == 0 && settings.Privileges.Length == 0)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                D365FoErrorCodes.BadInput,
                "At least one --duty or --privilege required when using --add-to."));

        try
        {
            var doc = System.Xml.Linq.XDocument.Load(path);
            var changed = XppScaffolder.AddToRole(doc, settings.Duties, settings.Privileges);
            if (!changed)
            {
                return RenderHelpers.Render(kind, ToolResult<object>.Success(new
                {
                    kind = "AxSecurityRole",
                    path,
                    changed = false,
                    note = "All supplied duties / privileges were already referenced.",
                }));
            }

            var tmp = path + ".tmp";
            using (var fs = System.IO.File.Create(tmp))
                doc.Save(fs);
            var backup = path + ".bak";
            if (System.IO.File.Exists(backup)) System.IO.File.Delete(backup);
            System.IO.File.Move(path, backup);
            System.IO.File.Move(tmp, path);

            return RenderHelpers.Render(kind, ToolResult<object>.Success(new
            {
                kind = "AxSecurityRole",
                path,
                changed = true,
                backup,
                addedDuties = settings.Duties,
                addedPrivileges = settings.Privileges,
            }));
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed, ex.Message));
        }
    }
}
