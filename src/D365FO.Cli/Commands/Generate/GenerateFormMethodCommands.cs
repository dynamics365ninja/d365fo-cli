using System.Xml.Linq;
using D365FO.Cli.Commands.Get;
using D365FO.Core;
using D365FO.Core.Journal;
using D365FO.Core.Scaffolding;
using Spectre.Console.Cli;
using D365FO.Core.Bridge;

namespace D365FO.Cli.Commands.Generate;

/// <summary>
/// Add (or override) a method on a <b>form datasource</b>. Unlike the other
/// <c>generate</c> commands this mutates an <em>existing</em> <c>AxForm</c>:
/// it reads the form, injects the method into the form-level
/// <c>&lt;SourceCode&gt;&lt;DataSources&gt;</c> tree (where Visual Studio puts
/// datasource override methods), and writes the form back.
/// </summary>
public sealed class GenerateDataSourceMethodCommand : Command<GenerateDataSourceMethodCommand.Settings>
{
    public sealed class Settings : FormMethodSettings
    {
        [CommandOption("--datasource <NAME>")]
        [System.ComponentModel.Description("Target form datasource name.")]
        public string? DataSource { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
        => FormMethodImpl.Run(FormMethodCatalog.Target.DataSource, settings, settings.DataSource, "--datasource");
}

/// <summary>
/// Add (or override) a method on a <b>form control</b>. Injects into the
/// form-level <c>&lt;SourceCode&gt;&lt;DataControls&gt;</c> tree.
/// </summary>
public sealed class GenerateControlMethodCommand : Command<GenerateControlMethodCommand.Settings>
{
    public sealed class Settings : FormMethodSettings
    {
        [CommandOption("--control <NAME>")]
        [System.ComponentModel.Description("Target form control name.")]
        public string? Control { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
        => FormMethodImpl.Run(FormMethodCatalog.Target.Control, settings, settings.Control, "--control");
}

/// <summary>Shared options for the two form-method commands.</summary>
public abstract class FormMethodSettings : GenerateSettings
{
    [CommandArgument(0, "<FORM>")]
    [System.ComponentModel.Description("Form name (resolved via the index) or a path to the AxForm XML file.")]
    public string Form { get; init; } = "";

    [CommandOption("--method <NAME>")]
    [System.ComponentModel.Description("Method to add/override (e.g. active, validateWrite, modified). Omit (or pass --list) to list overridable methods.")]
    public string? Method { get; init; }

    [CommandOption("--return-type <TYPE>")]
    [System.ComponentModel.Description("X++ return type for a method not in the built-in catalog (escape hatch). The stub is emitted parameterless — verify parameters against the framework.")]
    public string? ReturnType { get; init; }

    [CommandOption("--body <XPP>")]
    [System.ComponentModel.Description("Custom method body (X++ statements). When omitted a super()-returning stub is generated.")]
    public string? Body { get; init; }

    [CommandOption("--list")]
    [System.ComponentModel.Description("List the overridable methods for the target instead of writing.")]
    public bool List { get; init; }
}

internal static class FormMethodImpl
{
    public static int Run(FormMethodCatalog.Target target, FormMethodSettings s, string? ownerName, string ownerFlag)
    {
        var kind = OutputMode.Resolve(s.Output);

        if (string.IsNullOrWhiteSpace(s.Form))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "Form name or path required."));

        // ---- LIST mode: no write, just surface the catalog ----
        if (s.List || string.IsNullOrWhiteSpace(s.Method))
        {
            var methods = FormMethodCatalog.List(target)
                .Select(m => new { name = m.Name, returnType = m.ReturnType, parameters = m.Parameters })
                .ToList();
            return GenerateInstaller.Done(kind, gate: null, s, new
            {
                kind = "OverridableMethods",
                form = s.Form,
                target = target.ToString(),
                owner = ownerName,
                count = methods.Count,
                methods,
                note = "Curated subset. For a framework method not listed, pass --method <NAME> --return-type <TYPE>.",
            });
        }

        if (string.IsNullOrWhiteSpace(ownerName))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, $"{ownerFlag} <NAME> is required."));

        // ---- Resolve the form file on disk ----
        var (formPath, resolveFail) = ResolveFormPath(kind, s.Form);
        if (resolveFail.HasValue) return resolveFail.Value;

        // Captured BEFORE injection mutates the in-memory document — the exact pre-image
        // the journal needs to restore on `d365fo undo` (issue #113).
        string preImageXml;
        try { preImageXml = System.IO.File.ReadAllText(formPath!); }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.SourceUnreadable, $"Failed to read form XML: {ex.Message}"));
        }

        XDocument doc;
        try { doc = XDocument.Load(formPath!, LoadOptions.PreserveWhitespace); }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.SourceUnreadable, $"Failed to parse form XML: {ex.Message}"));
        }

        var formName = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value
                       ?? System.IO.Path.GetFileNameWithoutExtension(formPath!);

        // ---- Anti-hallucination: the owner must exist in the form ----
        var warnings = new List<string>();
        try
        {
            var available = target == FormMethodCatalog.Target.DataSource
                ? FormMethodScaffolder.ListDataSourceNames(doc)
                : FormMethodScaffolder.ListControlNames(doc);
            if (!available.Any(n => string.Equals(n, ownerName, StringComparison.OrdinalIgnoreCase)))
            {
                var code = target == FormMethodCatalog.Target.DataSource ? "DATASOURCE_NOT_FOUND" : "CONTROL_NOT_FOUND";
                var hint = available.Count > 0
                    ? $"Available: {string.Join(", ", available)}."
                    : "The form declares none of this kind.";
                return RenderHelpers.Render(kind, ToolResult<object>.Fail(code,
                    $"{(target == FormMethodCatalog.Target.DataSource ? "Datasource" : "Control")} '{ownerName}' not found in form '{formName}'. {hint}"));
            }
        }
        catch (FormMethodScaffolder.FormMethodException ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(ex.Code, ex.Message));
        }

        // ---- Resolve the method signature (catalog, or --return-type escape hatch) ----
        var sig = FormMethodCatalog.TryGet(target, s.Method!);
        if (sig is null)
        {
            if (string.IsNullOrWhiteSpace(s.ReturnType))
            {
                var known = string.Join(", ", FormMethodCatalog.List(target).Select(m => m.Name));
                return RenderHelpers.Render(kind, ToolResult<object>.Fail("METHOD_NOT_CATALOGUED",
                    $"'{s.Method}' is not in the built-in {target} method catalog. " +
                    $"Pass --return-type <TYPE> to override, or pick one of: {known}."));
            }
            sig = new FormMethodSignature(s.Method!, s.ReturnType!.Trim());
            warnings.Add($"'{s.Method}' is not catalogued; emitted a parameterless {sig.ReturnType} stub. Verify its parameters match the framework base method.");
        }

        // ---- Inject ----
        FormMethodScaffolder.InjectResult inject;
        try
        {
            inject = target == FormMethodCatalog.Target.DataSource
                ? FormMethodScaffolder.InjectDataSourceMethod(doc, ownerName, sig, s.Body, s.Overwrite)
                : FormMethodScaffolder.InjectControlMethod(doc, ownerName, sig, s.Body, s.Overwrite);
        }
        catch (FormMethodScaffolder.FormMethodException ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(ex.Code, ex.Message));
        }

        if (inject.AlreadyExisted && !inject.Changed)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("METHOD_ALREADY_EXISTS",
                $"Method '{sig.Name}' already exists on {target.ToString().ToLowerInvariant()} '{ownerName}'. Pass --overwrite to replace its body."));

        warnings.Add("Index not auto-refreshed — run `d365fo index refresh` so the new method is searchable.");

        // ---- Grounding gate ----
        // This command mutates an existing form, which is the strongest case for the gate and
        // was one of the twenty-six subcommands it never ran for (issue #161). It is gated on
        // the injected method rather than the whole form: judging Microsoft's form as if this
        // command had written it would report their code as our hallucinations.
        var gate = GenerateInstaller.Gate(
            s, formName,
            doc: new XDocument(new XElement("Method", new XElement("Source", inject.Source))),
            requiredSymbols: new[] { formName });
        if (gate.Failure is not null) return RenderHelpers.Render(kind, gate.Failure);
        warnings.AddRange(gate.Warnings);

        // ---- Persist: bridge update, explicit --out, or in-place ----
        var xml = doc.ToString(SaveOptions.DisableFormatting);
        gate.Observe(xml);

        object PayloadFor(string source, string? path) => new
        {
            kind = "FormMethod",
            form = formName,
            target = target.ToString(),
            owner = ownerName,
            method = sig.Name,
            returnType = sig.ReturnType,
            overwritten = inject.AlreadyExisted,
            source,
            path,
            model = s.InstallTo,
        };

        if (!string.IsNullOrWhiteSpace(s.InstallTo))
        {
            var (ok, err) = BridgeGate.TryUpdateObject("form", formName, s.InstallTo!, xml);
            if (!ok)
                return RenderHelpers.Render(kind, ToolResult<object>.Fail("INSTALL_FAILED",
                    $"Could not update form '{formName}' in model '{s.InstallTo}' via the metadata bridge: {err}"));
            RecordJournalUpdate("form", formName, s.InstallTo, JournalWritePath.Bridge, null, preImageXml,
                $"generate {(target == FormMethodCatalog.Target.DataSource ? "datasource-method" : "control-method")} --install-to");
            // The form went in through the provider rather than the shared writer, so the
            // artefact --verify reads back is recorded here (issue #180).
            gate.RecordArtefact("form", formName, null);
            return GenerateInstaller.Done(kind, gate, s, PayloadFor("bridge", null), warnings);
        }

        var outPath = string.IsNullOrWhiteSpace(s.Out) ? formPath! : s.Out!;
        try
        {
            AtomicSave(doc, outPath);
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed, ex.Message));
        }

        // Only journal a pre-image when the write landed on the SAME path we read from —
        // `--out <other path>` is a copy-out, not a mutation of the source form, so there
        // is nothing on the target path to restore an undo to.
        if (string.Equals(System.IO.Path.GetFullPath(outPath), System.IO.Path.GetFullPath(formPath!), StringComparison.OrdinalIgnoreCase))
        {
            RecordJournalUpdate("form", formName, null, JournalWritePath.Disk, outPath, preImageXml,
                $"generate {(target == FormMethodCatalog.Target.DataSource ? "datasource-method" : "control-method")}");
        }

        gate.RecordArtefact("form", formName, outPath);
        return GenerateInstaller.Done(kind, gate, s, PayloadFor("scaffold", outPath), warnings);
    }

    /// <summary>
    /// Resolve the FORM argument to an on-disk path: treat it as a path when it
    /// looks like one (or exists on disk), otherwise look it up by name in the
    /// index and use its SourcePath.
    /// </summary>
    private static (string? path, int? failure) ResolveFormPath(OutputMode.Kind kind, string form)
    {
        var looksLikePath = form.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                            || form.Contains('/') || form.Contains('\\')
                            || System.IO.File.Exists(form);
        if (looksLikePath)
        {
            if (!System.IO.File.Exists(form))
                return (null, RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.SourceUnreadable, $"Form file not found: {form}")));
            return (form, null);
        }

        try
        {
            var repo = RepoFactory.Create();
            var details = repo.GetForm(form);
            var src = details?.Form?.SourcePath;
            if (string.IsNullOrWhiteSpace(src))
                return (null, RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.FormNotFound,
                    $"Form '{form}' not found in the index (or it has no source path). Pass a file path, or run `d365fo index build`.")));
            if (!System.IO.File.Exists(src))
                return (null, RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.SourceUnreadable,
                    $"Indexed source for form '{form}' no longer exists on disk: {src}")));
            return (src, null);
        }
        catch (Exception ex)
        {
            return (null, RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.FormNotFound,
                $"Could not resolve form '{form}' via the index: {ex.Message}. Pass a file path instead.")));
        }
    }

    /// <summary>
    /// Best-effort modification-journal append (issue #113) for a datasource/control method
    /// injection — always an Update (the form already existed), never lets a journal failure
    /// fail the write it is recording.
    /// </summary>
    private static void RecordJournalUpdate(
        string aotKind, string objectName, string? model, JournalWritePath writePath,
        string? targetPath, string preImage, string command)
    {
        try
        {
            ModificationJournal.ForIndex().Append(new JournalEntry(
                Id: Guid.NewGuid().ToString("N"),
                TimestampUtc: DateTimeOffset.UtcNow,
                Command: command,
                TargetType: JournalTargetType.AotObject,
                Kind: aotKind,
                ObjectName: objectName,
                SecondaryKey: null,
                Model: model,
                Operation: JournalOperation.Update,
                WritePath: writePath,
                TargetPath: targetPath,
                PreImage: preImage,
                IsTombstone: false,
                RnrProjDelta: null));
        }
        catch { /* best-effort */ }
    }

    /// <summary>Write the document atomically (.tmp → move), keeping a .bak of any prior file.</summary>
    private static void AtomicSave(XDocument doc, string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        var dir = System.IO.Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);

        var tmp = full + ".tmp";
        using (var fs = System.IO.File.Create(tmp)) doc.Save(fs);

        if (System.IO.File.Exists(full))
        {
            var bak = full + ".bak";
            if (System.IO.File.Exists(bak)) System.IO.File.Delete(bak);
            System.IO.File.Move(full, bak);
        }
        System.IO.File.Move(tmp, full);
    }
}
