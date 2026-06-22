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

    [CommandOption("--grounding-token <TOKEN>")]
    [System.ComponentModel.Description("Grounding token from `d365fo prepare change`/`prepare create` proving the index was consulted. Required for extension-shaped objects when D365FO_GROUNDING_ENFORCE=true.")]
    public string? GroundingToken { get; init; }
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
                $"Could not resolve folder for model '{model}'. Ensure the bridge can see the model: set D365FO_BRIDGE_ENABLED=1 and point D365FO_PACKAGES_PATH (and D365FO_CUSTOM_PACKAGES_PATH for custom-model roots) at the directories that contain the model, on a D365FO VM."));
            return null;
        }
        return System.IO.Path.Combine(folder!, axSubfolder, name + ".xml");
    }

    /// <summary>
    /// Build an EDT → primitive-base-type resolver backed by the SQLite index.
    /// Passed to <see cref="XppScaffolder.Table"/> so each field gets its
    /// concrete <c>i:type="AxTableField{Suffix}"</c> discriminator (issue #91).
    /// Returns null when the index is unavailable — the scaffolder then falls
    /// back to a name heuristic.
    /// </summary>
    internal static Func<string, string?>? BuildEdtBaseTypeResolver()
    {
        try
        {
            var repo = RepoFactory.Create();
            return edt =>
            {
                if (string.IsNullOrWhiteSpace(edt)) return null;
                try { return repo.GetEdt(edt)?.BaseType; }
                catch { return null; }
            };
        }
        catch { return null; }
    }

    internal enum InstallOutcome { CreatedViaApi, WriteScaffold, Failed }

    /// <summary>
    /// Plan how to install a generated artefact into <paramref name="model"/>.
    /// Prefers the live metadata provider (bridge <c>createObject</c>) so the
    /// on-disk XML is provider-canonical and consistent with Visual Studio /
    /// <c>d365fo-mcp-server</c>. When the provider is unavailable, falls back to
    /// writing the (now valid) scaffold into the resolved model folder, with a
    /// warning. When neither path is reachable, returns a rendered failure.
    /// </summary>
    internal static (InstallOutcome outcome, string? writePath, int? failure, List<string> warnings)
        PlanInstall(OutputMode.Kind kind, string axKind, string axSubfolder, string name, string model, string xml)
    {
        var warnings = new List<string>();

        // 1) Metadata-API path — canonical, consistent output.
        var (ok, err) = BridgeGate.TrySaveObject(axKind, name, model, xml);
        if (ok) return (InstallOutcome.CreatedViaApi, null, null, warnings);

        // 2) Fallback — write the scaffold into the model folder if resolvable.
        var folder = BridgeGate.TryGetModelFolder(model);
        if (!string.IsNullOrEmpty(folder))
        {
            warnings.Add(
                $"Metadata API unavailable or rejected the object ({err}); wrote the raw scaffold instead. " +
                "The file is structurally valid but not provider-canonicalised — open it in Visual Studio to verify.");
            return (InstallOutcome.WriteScaffold,
                System.IO.Path.Combine(folder!, axSubfolder, name + ".xml"), null, warnings);
        }

        // 3) Neither path worked.
        var failure = RenderHelpers.Render(kind, ToolResult<object>.Fail(
            "INSTALL_FAILED",
            $"Could not install '{name}' into model '{model}'. Metadata API: {err}. " +
            "Could not resolve the model folder either — set D365FO_BRIDGE_ENABLED=1 and point " +
            "D365FO_PACKAGES_PATH (and D365FO_CUSTOM_PACKAGES_PATH for custom-model roots) at the " +
            "directories that contain the model, on a D365FO VM."));
        return (InstallOutcome.Failed, null, failure, warnings);
    }

    /// <summary>Inputs handed to a command's payload factory after the artefact is written.</summary>
    internal readonly record struct EmitResult(string Source, string? Path, long? Bytes, string? Backup);

    /// <summary>
    /// Emit a generated artefact (<see cref="System.Xml.Linq.XDocument"/> form),
    /// preferring the live metadata provider for <c>--install-to</c> and falling
    /// back to the scaffold. <paramref name="axKind"/> must be one of the bridge
    /// collection kinds: <c>class | table | edt | enum | form</c>.
    /// </summary>
    internal static int Emit(
        OutputMode.Kind kind, string axKind, string axSubfolder, string name,
        string? installTo, string? outPath, bool overwrite,
        System.Xml.Linq.XDocument doc,
        Func<EmitResult, object> buildPayload,
        List<string>? warnings = null)
        => EmitCore(kind, axKind, axSubfolder, name, installTo, outPath, doc.ToString(),
            path => ScaffoldFileWriter.Write(doc, path, overwrite), buildPayload, warnings);

    /// <summary>String-rendered counterpart of <see cref="Emit"/> (used for forms).</summary>
    internal static int EmitString(
        OutputMode.Kind kind, string axKind, string axSubfolder, string name,
        string? installTo, string? outPath, bool overwrite,
        string xml,
        Func<EmitResult, object> buildPayload,
        List<string>? warnings = null)
        => EmitCore(kind, axKind, axSubfolder, name, installTo, outPath, xml,
            path => ScaffoldFileWriter.Write(xml, path, overwrite), buildPayload, warnings);

    private static int EmitCore(
        OutputMode.Kind kind, string axKind, string axSubfolder, string name,
        string? installTo, string? outPath, string xml,
        Func<string, ScaffoldFileWriter.WriteResult> write,
        Func<EmitResult, object> buildPayload,
        List<string>? warnings)
    {
        warnings ??= new List<string>();
        var hasInstall = !string.IsNullOrWhiteSpace(installTo);
        var hasOut     = !string.IsNullOrWhiteSpace(outPath);
        if (!hasInstall && !hasOut)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("BAD_INPUT", "--out or --install-to is required."));

        if (hasInstall && !hasOut)
        {
            var plan = PlanInstall(kind, axKind, axSubfolder, name, installTo!, xml);
            if (plan.failure.HasValue) return plan.failure.Value;
            var all = warnings.Concat(plan.warnings).ToList();

            if (plan.outcome == InstallOutcome.CreatedViaApi)
                return RenderHelpers.Render(kind, ToolResult<object>.Success(
                    buildPayload(new EmitResult("bridge", null, null, null)),
                    all.Count > 0 ? all : null));

            try
            {
                var res = write(plan.writePath!);
                return RenderHelpers.Render(kind, ToolResult<object>.Success(
                    buildPayload(new EmitResult("scaffold", res.Path, res.Bytes, res.BackupPath)),
                    all.Count > 0 ? all : null));
            }
            catch (Exception ex)
            {
                return RenderHelpers.Render(kind, ToolResult<object>.Fail("WRITE_FAILED", ex.Message));
            }
        }

        try
        {
            var res = write(outPath!);
            return RenderHelpers.Render(kind, ToolResult<object>.Success(
                buildPayload(new EmitResult("scaffold", res.Path, res.Bytes, res.BackupPath)),
                warnings.Count > 0 ? warnings : null));
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("WRITE_FAILED", ex.Message));
        }
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
        [System.ComponentModel.Description("Repeatable: <name>:<edt>[[:mandatory]]. Example: --field AccountNum:CustAccount:mandatory")]
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

        var fields2 = settings.Fields.Select(ParseField).ToList();
        // Resolve each field's EDT base type from the index so the scaffold
        // stamps the concrete i:type discriminator on every <AxTableField>.
        var edtResolver = GenerateInstaller.BuildEdtBaseTypeResolver();
        var doc = XppScaffolder.Table(settings.Name, settings.Label, fields2, pattern, storage, settings.PrimaryKey, edtResolver);

        var fieldCount = fields2.Count > 0 ? fields2.Count : TablePatternPresets.DefaultFieldsFor(pattern).Count;
        var patternStr = pattern == TablePattern.None ? null : pattern.ToString();
        var tableTypeStr = storage == TableStorage.RegularTable ? null : storage.ToString();
        var usedDefaults = fields2.Count == 0 && pattern != TablePattern.None;

        // Prefer the live metadata provider for --install-to (canonical output,
        // consistent with VS / d365fo-mcp-server); fall back to the scaffold.
        return GenerateInstaller.Emit(
            kind, "table", "AxTable", settings.Name,
            settings.InstallTo, settings.Out, settings.Overwrite, doc,
            r => new
            {
                kind = "AxTable",
                name = settings.Name,
                source = r.Source,
                path = r.Path,
                bytes = r.Bytes,
                backup = r.Backup,
                fieldCount,
                pattern = patternStr,
                tableType = tableTypeStr,
                usedPatternDefaults = usedDefaults,
                model = settings.InstallTo,
            });
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

        var doc = XppScaffolder.Class(settings.Name, settings.Extends, !settings.NonFinal);
        return GenerateInstaller.Emit(
            kind, "class", "AxClass", settings.Name,
            settings.InstallTo, settings.Out, settings.Overwrite, doc,
            r => new
            {
                kind = "AxClass", name = settings.Name, source = r.Source,
                path = r.Path, bytes = r.Bytes, backup = r.Backup, model = settings.InstallTo,
            });
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

        // Guardrail: warn if the target already has CoC wrappers, and resolve
        // the target's AOT kind so [ExtensionOf] uses the right intrinsic
        // (tableStr for tables, classStr for classes, …).
        var warnings = new List<string>();
        var targetKind = "class";
        try
        {
            var repo = RepoFactory.Create();
            var existing = repo.FindCocExtensions(settings.Target);
            if (existing.Count > 0)
                warnings.Add($"There are already {existing.Count} CoC extension(s) of {settings.Target}. Consider extending an existing one instead of stacking a new wrapper.");
            var kinds = repo.SymbolKinds(settings.Target);
            targetKind = kinds.FirstOrDefault(k => k is "class" or "table" or "form" or "data-entity" or "map" or "view") ?? "class";
        }
        catch { /* index may be empty; not fatal */ }

        var doc = XppScaffolder.CocExtension(settings.Target, targetKind, settings.Methods);

        // Grounding gate: prove the target and every wrapped method against the
        // index; fail closed under D365FO_GROUNDING_ENFORCE=true.
        var gate = GroundingGate.Check(
            settings.GroundingToken,
            settings.Target,
            doc,
            settings.Methods.Select(m => (settings.Target, m)));
        if (gate.Failure is not null) return RenderHelpers.Render(kind, gate.Failure);
        warnings.AddRange(gate.Warnings);

        return GenerateInstaller.Emit(
            kind, "class", "AxClass", settings.Target + "_Extension",
            settings.InstallTo, settings.Out, settings.Overwrite, doc,
            r => new
            {
                kind = "AxClass",
                name = settings.Target + "_Extension",
                source = r.Source,
                path = r.Path,
                bytes = r.Bytes,
                backup = r.Backup,
                methodCount = settings.Methods.Length,
                model = settings.InstallTo,
                grounding = gate.Grounding,
            },
            warnings);
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

        // Pre-write pattern self-test: structural violations (FP001-FP005, FP007)
        // block the write while D365FO_FORM_PATTERN_ENFORCE=true (the default),
        // mirroring the upstream MCP form-pattern write gate.
        var patternReport = D365FO.Core.FormPatterns.FormPatternValidator.ValidateXml(xml);
        var patternWarnings = patternReport.Violations
            .Select(v => $"form-pattern {v.Rule} [{v.Severity}] {v.Path}: {v.Excerpt}")
            .ToList();
        if (patternReport.HasErrors && FormPatternGate.EnforcementEnabled)
        {
            var errors = patternReport.Violations.Where(v => v.Severity == "error")
                .Select(v => $"{v.Rule} {v.Path}: {v.Excerpt} → {v.Fix}");
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                "FORM_PATTERN_VIOLATION",
                $"Generated form violates pattern {patternReport.Pattern} (D365FO_FORM_PATTERN_ENFORCE=true):\n" +
                string.Join("\n", errors),
                "Fix the structure (see `d365fo get form-pattern " + (patternReport.Pattern ?? "<pattern>") + "`), " +
                "or set D365FO_FORM_PATTERN_ENFORCE=false to bypass the gate."));
        }

        return GenerateInstaller.EmitString(
            kind, "form", "AxForm", formName,
            installTo, outPath, overwrite, xml,
            r => new
            {
                kind         = "AxForm",
                name         = formName,
                pattern      = pattern.ToString(),
                source       = r.Source,
                path         = r.Path,
                bytes        = r.Bytes,
                backup       = r.Backup,
                model        = installTo,
                fieldCount   = fields.Count,
                sectionCount = sectionSpecs.Count,
                patternCheck = new
                {
                    enforced = FormPatternGate.EnforcementEnabled,
                    errors   = patternReport.ErrorCount,
                    warnings = patternReport.WarningCount,
                },
            },
            patternWarnings.Count > 0 ? patternWarnings : null);
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
