using D365FO.Core;
using D365FO.Core.Scaffolding;
using Spectre.Console.Cli;
using D365FO.Cli.Commands.Get;

namespace D365FO.Cli.Commands.Generate;

public abstract class GenerateSettings : D365OutputSettings
{
    [CommandOption("--out <PATH>")]
    [System.ComponentModel.Description("Output file path. Required unless --install-to is used.")]
    public string? Out { get; init; }

    [CommandOption("--overwrite")]
    public bool Overwrite { get; init; }

    [CommandOption("--install-to <MODEL>")]
    [System.ComponentModel.Description("Install the generated artefact directly into <MODEL> via the metadata bridge. Requires D365FO_BRIDGE_ENABLED=1.")]
    public string? InstallTo { get; init; }
}

internal static class GenerateInstaller
{
    /// <summary>
    /// Resolve the on-disk install path for a scaffolded artefact. When
    /// <c>--install-to &lt;MODEL&gt;</c> is supplied we ask the bridge where
    /// the model lives on disk and compose
    /// <c>&lt;modelFolder&gt;/Ax&lt;Kind&gt;/&lt;Name&gt;.xml</c> — the
    /// canonical location Visual Studio and the D365FO build tools expect.
    /// The caller then invokes the regular <see cref="ScaffoldFileWriter"/>
    /// against this path. Returns null on failure and renders an error into
    /// <paramref name="failure"/>.
    /// </summary>
    internal static string? ResolveInstallPath(OutputMode.Kind kind, string axSubfolder, string name, string model, out int? failure)
    {
        failure = null;
        var folder = BridgeGate.TryGetModelFolder(model);
        if (string.IsNullOrEmpty(folder))
        {
            failure = RenderHelpers.Render(kind, ToolResult<object>.Fail(
                "INSTALL_FAILED",
                $"Could not resolve folder for model '{model}'. Set D365FO_BRIDGE_ENABLED=1 and D365FO_PACKAGES_PATH on a D365FO VM, and make sure the model exists."));
            return null;
        }
        return System.IO.Path.Combine(folder!, axSubfolder, name + ".xml");
    }
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
        var hasInstall = !string.IsNullOrWhiteSpace(settings.InstallTo);
        var hasOut     = !string.IsNullOrWhiteSpace(settings.Out);
        if (!hasInstall && !hasOut)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("BAD_INPUT", "--out or --install-to is required."));
        var outPath = settings.Out;
        if (hasInstall && !hasOut)
        {
            outPath = GenerateInstaller.ResolveInstallPath(kind, "AxTable", settings.Name, settings.InstallTo!, out var fail);
            if (fail.HasValue) return fail.Value;
        }

        var fields2 = settings.Fields.Select(ParseField).ToList();
        var doc = XppScaffolder.Table(settings.Name, settings.Label, fields2);
        try
        {
            var res = ScaffoldFileWriter.Write(doc, outPath!, settings.Overwrite);
            return RenderHelpers.Render(kind, ToolResult<object>.Success(new
            {
                kind = "AxTable",
                name = settings.Name,
                path = res.Path,
                bytes = res.Bytes,
                backup = res.BackupPath,
                fieldCount = fields2.Count,
                model = settings.InstallTo,
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
        var hasInstall = !string.IsNullOrWhiteSpace(settings.InstallTo);
        var hasOut     = !string.IsNullOrWhiteSpace(settings.Out);
        if (!hasInstall && !hasOut)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("BAD_INPUT", "--out or --install-to is required."));
        var outPath = settings.Out;
        if (hasInstall && !hasOut)
        {
            outPath = GenerateInstaller.ResolveInstallPath(kind, "AxClass", settings.Name, settings.InstallTo!, out var fail);
            if (fail.HasValue) return fail.Value;
        }

        var doc = XppScaffolder.Class(settings.Name, settings.Extends, !settings.NonFinal);
        try
        {
            var res = ScaffoldFileWriter.Write(doc, outPath!, settings.Overwrite);
            return RenderHelpers.Render(kind, ToolResult<object>.Success(new
            {
                kind = "AxClass", name = settings.Name, path = res.Path, bytes = res.Bytes, backup = res.BackupPath, model = settings.InstallTo,
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
        var hasInstall = !string.IsNullOrWhiteSpace(settings.InstallTo);
        var hasOut     = !string.IsNullOrWhiteSpace(settings.Out);
        if (!hasInstall && !hasOut)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("BAD_INPUT", "--out or --install-to is required."));
        var outPath = settings.Out;
        if (hasInstall && !hasOut)
        {
            outPath = GenerateInstaller.ResolveInstallPath(kind, "AxClass", settings.Target + "_Extension", settings.InstallTo!, out var fail);
            if (fail.HasValue) return fail.Value;
        }

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
            var res = ScaffoldFileWriter.Write(doc, outPath!, settings.Overwrite);
            return RenderHelpers.Render(kind, ToolResult<object>.Success(new
            {
                kind = "AxClass",
                name = settings.Target + "_Extension",
                path = res.Path,
                bytes = res.Bytes,
                backup = res.BackupPath,
                methodCount = settings.Methods.Length,
                model = settings.InstallTo,
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
        var hasInstall = !string.IsNullOrWhiteSpace(settings.InstallTo);
        var hasOut     = !string.IsNullOrWhiteSpace(settings.Out);
        if (!hasInstall && !hasOut)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("BAD_INPUT", "--out or --install-to is required."));
        var outPath = settings.Out;
        if (hasInstall && !hasOut)
        {
            outPath = GenerateInstaller.ResolveInstallPath(kind, "AxForm", settings.FormName, settings.InstallTo!, out var fail);
            if (fail.HasValue) return fail.Value;
        }

        var doc = XppScaffolder.SimpleList(settings.FormName, settings.Table!);
        try
        {
            var res = ScaffoldFileWriter.Write(doc, outPath!, settings.Overwrite);
            return RenderHelpers.Render(kind, ToolResult<object>.Success(new
            {
                kind = "AxForm", name = settings.FormName, path = res.Path, bytes = res.Bytes, backup = res.BackupPath, model = settings.InstallTo,
            }));
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("WRITE_FAILED", ex.Message));
        }
    }
}
