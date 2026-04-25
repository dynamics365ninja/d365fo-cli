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

        [CommandOption("--pattern <PATTERN>")]
        [System.ComponentModel.Description("Business-role preset: main|transaction|parameter|group|worksheetheader|worksheetline|reference|framework|miscellaneous. Sets <TableGroup> and provides default fields when --field is empty. Aliases: master, setup, config, transactional, lookup …")]
        public string? Pattern { get; init; }

        [CommandOption("--table-type <TYPE>")]
        [System.ComponentModel.Description("Storage kind: RegularTable|TempDB|InMemory (default RegularTable). NEVER pass TempDB to --pattern — it is a TableType, not a TableGroup.")]
        public string? TableType { get; init; }

        [CommandOption("--primary-key <FIELD>")]
        [System.ComponentModel.Description("Repeatable: field name(s) to compose the alternate-key index. Defaults to all mandatory fields, or the first field if none mandatory.")]
        public string[] PrimaryKey { get; init; } = Array.Empty<string>();
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        if (string.IsNullOrWhiteSpace(settings.Name))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("BAD_INPUT", "Table name required."));
        if (!TablePatternNormalizer.TryNormalize(settings.Pattern, out var pattern, out var perr))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("BAD_INPUT", perr!));
        if (!TablePatternNormalizer.TryNormalizeStorage(settings.TableType, out var storage, out var serr))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("BAD_INPUT", serr!));

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
        var doc = XppScaffolder.Table(settings.Name, settings.Label, fields2, pattern, storage, settings.PrimaryKey);
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
                fieldCount = fields2.Count > 0 ? fields2.Count : TablePatternPresets.DefaultFieldsFor(pattern).Count,
                pattern = pattern == TablePattern.None ? null : pattern.ToString(),
                tableType = storage == TableStorage.RegularTable ? null : storage.ToString(),
                usedPatternDefaults = fields2.Count == 0 && pattern != TablePattern.None,
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
        return GenerateFormImpl.Run(
            output:       settings.Output,
            formName:     settings.FormName,
            table:        settings.Table,
            patternRaw:   "SimpleList",
            caption:      null,
            fields:       Array.Empty<string>(),
            sections:     Array.Empty<string>(),
            linesTable:   null,
            outPath:      settings.Out,
            installTo:    settings.InstallTo,
            overwrite:    settings.Overwrite);
    }
}

/// <summary>
/// Pattern-aware form scaffolder. Mirrors <c>generate_smart_form</c> from
/// <c>d365fo-mcp-server</c>: nine D365FO patterns, optional grid fields,
/// optional sections (TabPages for TOC / Dialog / Workspace), optional lines
/// datasource for <c>DetailsTransaction</c>.
/// </summary>
public sealed class GenerateFormCommand : Command<GenerateFormCommand.Settings>
{
    public sealed class Settings : GenerateSettings
    {
        [CommandArgument(0, "<FORM_NAME>")]
        public string FormName { get; init; } = "";

        [CommandOption("--pattern <PATTERN>")]
        [System.ComponentModel.Description("Form pattern: SimpleList | SimpleListDetails | DetailsMaster | DetailsTransaction | Dialog | TableOfContents | Lookup | ListPage | Workspace. Aliases (master, transaction, toc, panorama, …) are accepted.")]
        public string? Pattern { get; init; }

        [CommandOption("--table <TABLE>")]
        [System.ComponentModel.Description("Primary datasource table.")]
        public string? Table { get; init; }

        [CommandOption("--caption <TEXT>")]
        [System.ComponentModel.Description("Caption / title (literal text or @File:Label).")]
        public string? Caption { get; init; }

        [CommandOption("--field <NAME>")]
        [System.ComponentModel.Description("Field name to render as a grid / detail column (repeatable).")]
        public string[] Fields { get; init; } = Array.Empty<string>();

        [CommandOption("--section <SECTION>")]
        [System.ComponentModel.Description("TabPage / section (repeatable). Format: <Name>:<Caption>. Used by TableOfContents, Dialog, Workspace.")]
        public string[] Sections { get; init; } = Array.Empty<string>();

        [CommandOption("--lines-table <TABLE>")]
        [System.ComponentModel.Description("Lines datasource table for DetailsTransaction.")]
        public string? LinesTable { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        return GenerateFormImpl.Run(
            output:       settings.Output,
            formName:     settings.FormName,
            table:        settings.Table,
            patternRaw:   settings.Pattern,
            caption:      settings.Caption,
            fields:       settings.Fields,
            sections:     settings.Sections,
            linesTable:   settings.LinesTable,
            outPath:      settings.Out,
            installTo:    settings.InstallTo,
            overwrite:    settings.Overwrite);
    }
}

internal static class GenerateFormImpl
{
    public static int Run(
        string? output,
        string formName,
        string? table,
        string? patternRaw,
        string? caption,
        IReadOnlyList<string> fields,
        IReadOnlyList<string> sections,
        string? linesTable,
        string? outPath,
        string? installTo,
        bool overwrite)
    {
        var kind = OutputMode.Resolve(output);
        if (string.IsNullOrWhiteSpace(formName))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("BAD_INPUT", "Form name required."));

        var pattern = FormPatternNormalizer.Normalize(patternRaw);

        // Patterns that need a datasource: everything except Dialog / TableOfContents (where it is optional).
        var dsRequired = pattern is not (FormPattern.Dialog or FormPattern.TableOfContents);
        if (dsRequired && string.IsNullOrWhiteSpace(table))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("BAD_INPUT", $"--table <TABLE> required for pattern {pattern}."));

        var hasInstall = !string.IsNullOrWhiteSpace(installTo);
        var hasOut     = !string.IsNullOrWhiteSpace(outPath);
        if (!hasInstall && !hasOut)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("BAD_INPUT", "--out or --install-to is required."));

        if (hasInstall && !hasOut)
        {
            outPath = GenerateInstaller.ResolveInstallPath(kind, "AxForm", formName, installTo!, out var fail);
            if (fail.HasValue) return fail.Value;
        }

        var sectionSpecs = ParseSections(sections);

        string xml;
        try
        {
            xml = XppScaffolder.Form(
                formName:        formName,
                dataSourceTable: table,
                pattern:         pattern,
                caption:         caption,
                gridFields:      fields,
                sections:        sectionSpecs,
                linesTable:      linesTable);
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("RENDER_FAILED", ex.Message));
        }

        try
        {
            var res = ScaffoldFileWriter.Write(xml, outPath!, overwrite);
            return RenderHelpers.Render(kind, ToolResult<object>.Success(new
            {
                kind         = "AxForm",
                name         = formName,
                pattern      = pattern.ToString(),
                path         = res.Path,
                bytes        = res.Bytes,
                backup       = res.BackupPath,
                model        = installTo,
                fieldCount   = fields.Count,
                sectionCount = sectionSpecs.Count,
            }));
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("WRITE_FAILED", ex.Message));
        }
    }

    private static IReadOnlyList<FormSectionSpec> ParseSections(IReadOnlyList<string> raw)
    {
        if (raw.Count == 0) return Array.Empty<FormSectionSpec>();
        var list = new List<FormSectionSpec>(raw.Count);
        foreach (var s in raw)
        {
            var idx = s.IndexOf(':');
            if (idx > 0)
                list.Add(new FormSectionSpec(s[..idx].Trim(), s[(idx + 1)..].Trim()));
            else
                list.Add(new FormSectionSpec(s.Trim(), s.Trim()));
        }
        return list;
    }
}
