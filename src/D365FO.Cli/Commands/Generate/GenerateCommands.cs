using D365FO.Core;
using D365FO.Core.Scaffolding;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Generate;

public abstract class GenerateSettings : D365OutputSettings
{
    [CommandOption("--out <PATH>")]
    [System.ComponentModel.Description("Output file path. Required — scaffolding never writes to stdout.")]
    public string? Out { get; init; }

    [CommandOption("--overwrite")]
    public bool Overwrite { get; init; }
}

public sealed class GenerateTableCommand : Command<GenerateTableCommand.Settings>
{
    public sealed class Settings : GenerateSettings
    {
        [CommandArgument(0, "<NAME>")]
        public string Name { get; init; } = "";

        [CommandOption("--label <KEY>")]
        public string? Label { get; init; }

        [CommandOption("--field <SPEC>")]
        [System.ComponentModel.Description("Repeatable: <name>:<edt>[:mandatory]. Example: --field AccountNum:CustAccount:mandatory")]
        public string[] Fields { get; init; } = Array.Empty<string>();
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        if (string.IsNullOrWhiteSpace(settings.Name))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("BAD_INPUT", "Table name required."));
        if (string.IsNullOrWhiteSpace(settings.Out))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("BAD_INPUT", "--out is required."));

        var fields = settings.Fields.Select(ParseField).ToList();
        var doc = XppScaffolder.Table(settings.Name, settings.Label, fields);
        try
        {
            var res = ScaffoldFileWriter.Write(doc, settings.Out!, settings.Overwrite);
            return RenderHelpers.Render(kind, ToolResult<object>.Success(new
            {
                kind = "AxTable",
                name = settings.Name,
                path = res.Path,
                bytes = res.Bytes,
                backup = res.BackupPath,
                fieldCount = fields.Count,
            }));
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("WRITE_FAILED", ex.Message));
        }
    }

    private static TableFieldSpec ParseField(string raw)
    {
        var parts = raw.Split(':', StringSplitOptions.TrimEntries);
        var name = parts.Length > 0 ? parts[0] : "";
        var edt = parts.Length > 1 ? parts[1] : null;
        var mandatory = parts.Length > 2 && string.Equals(parts[2], "mandatory", StringComparison.OrdinalIgnoreCase);
        return new TableFieldSpec(name, string.IsNullOrEmpty(edt) ? null : edt, null, mandatory);
    }
}

public sealed class GenerateClassCommand : Command<GenerateClassCommand.Settings>
{
    public sealed class Settings : GenerateSettings
    {
        [CommandArgument(0, "<NAME>")]
        public string Name { get; init; } = "";

        [CommandOption("--extends <BASE>")]
        public string? Extends { get; init; }

        [CommandOption("--non-final")]
        public bool NonFinal { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        if (string.IsNullOrWhiteSpace(settings.Name))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("BAD_INPUT", "Class name required."));
        if (string.IsNullOrWhiteSpace(settings.Out))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("BAD_INPUT", "--out is required."));

        var doc = XppScaffolder.Class(settings.Name, settings.Extends, !settings.NonFinal);
        try
        {
            var res = ScaffoldFileWriter.Write(doc, settings.Out!, settings.Overwrite);
            return RenderHelpers.Render(kind, ToolResult<object>.Success(new
            {
                kind = "AxClass", name = settings.Name, path = res.Path, bytes = res.Bytes, backup = res.BackupPath,
            }));
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("WRITE_FAILED", ex.Message));
        }
    }
}

public sealed class GenerateCocCommand : Command<GenerateCocCommand.Settings>
{
    public sealed class Settings : GenerateSettings
    {
        [CommandArgument(0, "<TARGET>")]
        [System.ComponentModel.Description("Target class name. Extension will be named <TARGET>_Extension.")]
        public string Target { get; init; } = "";

        [CommandOption("--method <NAME>")]
        [System.ComponentModel.Description("Repeatable. Each method gets a `next` wrapper.")]
        public string[] Methods { get; init; } = Array.Empty<string>();
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        if (string.IsNullOrWhiteSpace(settings.Target))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("BAD_INPUT", "Target class required."));
        if (settings.Methods.Length == 0)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("BAD_INPUT", "At least one --method required."));
        if (string.IsNullOrWhiteSpace(settings.Out))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("BAD_INPUT", "--out is required."));

        // Guardrail: warn if the target already has CoC wrappers.
        var warnings = new List<string>();
        try
        {
            var repo = RepoFactory.Create();
            var existing = repo.FindCocExtensions(settings.Target);
            if (existing.Count > 0)
                warnings.Add($"There are already {existing.Count} CoC extension(s) of {settings.Target}. Consider extending an existing one instead of stacking a new wrapper.");
        }
        catch { /* index may be empty; not fatal */ }

        var doc = XppScaffolder.CocExtension(settings.Target, settings.Methods);
        try
        {
            var res = ScaffoldFileWriter.Write(doc, settings.Out!, settings.Overwrite);
            return RenderHelpers.Render(kind, ToolResult<object>.Success(new
            {
                kind = "AxClass",
                name = settings.Target + "_Extension",
                path = res.Path,
                bytes = res.Bytes,
                backup = res.BackupPath,
                methodCount = settings.Methods.Length,
            }, warnings: warnings));
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("WRITE_FAILED", ex.Message));
        }
    }
}

public sealed class GenerateSimpleListCommand : Command<GenerateSimpleListCommand.Settings>
{
    public sealed class Settings : GenerateSettings
    {
        [CommandArgument(0, "<FORM_NAME>")]
        public string FormName { get; init; } = "";

        [CommandOption("--table <TABLE>")]
        public string? Table { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        if (string.IsNullOrWhiteSpace(settings.FormName))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("BAD_INPUT", "Form name required."));
        if (string.IsNullOrWhiteSpace(settings.Table))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("BAD_INPUT", "--table <TABLE> required."));
        if (string.IsNullOrWhiteSpace(settings.Out))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("BAD_INPUT", "--out is required."));

        var doc = XppScaffolder.SimpleList(settings.FormName, settings.Table!);
        try
        {
            var res = ScaffoldFileWriter.Write(doc, settings.Out!, settings.Overwrite);
            return RenderHelpers.Render(kind, ToolResult<object>.Success(new
            {
                kind = "AxForm", name = settings.FormName, path = res.Path, bytes = res.Bytes, backup = res.BackupPath,
            }));
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("WRITE_FAILED", ex.Message));
        }
    }
}
