using System.Text;
using System.Xml.Linq;
using D365FO.Core;
using D365FO.Core.Labels;
using D365FO.Core.Scaffolding;
using Spectre.Console.Cli;

using static D365FO.Core.ObjectTypes.ObjectTypeRegistry;

namespace D365FO.Cli.Commands.Generate;

/// <summary>Scaffolds an <c>AxConfigurationKey</c>.</summary>
public sealed class GenerateConfigurationKeyCommand : Command<GenerateConfigurationKeyCommand.Settings>
{
    public sealed class Settings : GenerateSettings
    {
        [CommandArgument(0, "<NAME>")]
        [System.ComponentModel.Description("Configuration key name.")]
        public string Name { get; init; } = "";

        [CommandOption("--label <KEY>")]
        public string? Label { get; init; }

        [CommandOption("--parent-key <NAME>")]
        [System.ComponentModel.Description("Parent AxConfigurationKey; disabling the parent disables this key.")]
        public string? ParentKey { get; init; }

        [CommandOption("--license-code <NAME>")]
        [System.ComponentModel.Description("AxLicenseCode the key hangs under. Rare outside ISV models.")]
        public string? LicenseCode { get; init; }

        [CommandOption("--description <KEY>")]
        public string? Description { get; init; }

        [CommandOption("--disabled-by-default")]
        [System.ComponentModel.Description("Write EnabledByDefault=No (the key must be switched on in License configuration).")]
        public bool DisabledByDefault { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        if (string.IsNullOrWhiteSpace(settings.Name))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "Configuration key name required."));

        XDocument doc;
        try
        {
            doc = ConfigurationScaffolder.ConfigurationKey(settings.Name, settings.Label, settings.ParentKey,
                settings.LicenseCode, settings.Description, enabledByDefault: !settings.DisabledByDefault);
        }
        catch (ArgumentException ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, ex.Message));
        }

        if (!GenerateViewCommand.TryResolveOutPath(kind, settings, Folders.ConfigurationKey, settings.Name, out var outPath, out var pathFailure))
            return pathFailure;

        var warnings = new List<string>();
        if (!string.IsNullOrWhiteSpace(settings.ParentKey))
        {
            // Configuration keys are indexed but are not in SymbolKinds, so the gate cannot see
            // them; ask the index directly and say what it could not find.
            try
            {
                var repo = RepoFactory.Create();
                var hits = repo.SearchConfigurationKeys(settings.ParentKey!, 50);
                if (!hits.Any(h => string.Equals(h.Name, settings.ParentKey, StringComparison.OrdinalIgnoreCase)))
                    warnings.Add($"Parent key '{settings.ParentKey}' is not in the index. A key whose parent does not exist is dropped by the reader; check the spelling with `d365fo search configuration-key {settings.ParentKey}`.");
            }
            catch { /* no index: nothing to compare against */ }
        }

        try
        {
            var gate = GenerateInstaller.Gate(settings, settings.Name, doc);
            if (gate.Failure is not null) return RenderHelpers.Render(kind, gate.Failure);

            var res = GenerateInstaller.Write(gate, doc, outPath!, settings.Overwrite);
            return GenerateInstaller.Done(kind, gate, settings, new
            {
                kind = "AxConfigurationKey",
                name = settings.Name,
                parentKey = settings.ParentKey,
                licenseCode = settings.LicenseCode,
                enabledByDefault = !settings.DisabledByDefault,
                path = res.Path,
                bytes = res.Bytes,
                backup = res.BackupPath,
                model = settings.InstallTo,
            }, warnings);
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed, ex.Message));
        }
    }
}

/// <summary>Scaffolds an <c>AxWorkflowCategory</c>.</summary>
public sealed class GenerateWorkflowCategoryCommand : Command<GenerateWorkflowCategoryCommand.Settings>
{
    public sealed class Settings : GenerateSettings
    {
        [CommandArgument(0, "<NAME>")]
        [System.ComponentModel.Description("Workflow category name.")]
        public string Name { get; init; } = "";

        [CommandOption("--module <MODULE>")]
        [System.ComponentModel.Description("ModuleAxapta value the category is filed under (PurchaseOrder, Ledger, Basic, Customer …). Required; validated against the enum when the index has it.")]
        public string? Module { get; init; }

        [CommandOption("--label <KEY>")]
        public string? Label { get; init; }

        [CommandOption("--help-text <KEY>")]
        public string? HelpText { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        if (string.IsNullOrWhiteSpace(settings.Name))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "Workflow category name required."));
        if (string.IsNullOrWhiteSpace(settings.Module))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                "--module <MODULE> required.", hint: "List the values with `d365fo get enum ModuleAxapta`."));

        // The contract types Module as a string, the platform reads it as ModuleAxapta: a value
        // outside the enum is a category the workflow configuration form cannot place.
        var module = settings.Module!.Trim();
        var warnings = new List<string>();
        try
        {
            var repo = RepoFactory.Create();
            var en = repo.GetEnum("ModuleAxapta");
            if (en is not null && en.Values.Count > 0)
            {
                var hit = en.Values.FirstOrDefault(v => string.Equals(v.Name, module, StringComparison.OrdinalIgnoreCase));
                if (hit is null)
                    return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                        $"'{module}' is not a value of ModuleAxapta.",
                        hint: "Run `d365fo get enum ModuleAxapta` for the list (PurchaseOrder, SalesOrder, Ledger, Bank, Customer, Vendor, Basic …)."));
                module = hit.Name; // the enum's own casing
            }
            else
            {
                warnings.Add("ModuleAxapta is not in the index, so --module was not validated. `d365fo get enum ModuleAxapta` lists the legal values once ApplicationPlatform is extracted.");
            }
        }
        catch
        {
            warnings.Add("No index available, so --module was not validated against ModuleAxapta.");
        }

        XDocument doc;
        try { doc = ConfigurationScaffolder.WorkflowCategory(settings.Name, module, settings.Label, settings.HelpText); }
        catch (ArgumentException ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, ex.Message));
        }

        if (!GenerateViewCommand.TryResolveOutPath(kind, settings, Folders.WorkflowCategory, settings.Name, out var outPath, out var pathFailure))
            return pathFailure;

        try
        {
            var gate = GenerateInstaller.Gate(settings, settings.Name, doc);
            if (gate.Failure is not null) return RenderHelpers.Render(kind, gate.Failure);

            var res = GenerateInstaller.Write(gate, doc, outPath!, settings.Overwrite);
            return GenerateInstaller.Done(kind, gate, settings, new
            {
                kind = "AxWorkflowCategory",
                name = settings.Name,
                module,
                label = settings.Label,
                path = res.Path,
                bytes = res.Bytes,
                backup = res.BackupPath,
                model = settings.InstallTo,
            }, warnings);
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed, ex.Message));
        }
    }
}

/// <summary>
/// Where a model-relative URI gets its model (and package) from when the command writes to a
/// bare <c>--out</c> path: an explicit option first, then <c>--install-to</c>, then the folder
/// layout <c>&lt;package&gt;/&lt;model&gt;/&lt;AxKind&gt;/&lt;file&gt;</c> when the path has it.
/// </summary>
internal static class ModelOfOutput
{
    internal static (string? Package, string? Model) Resolve(string? explicitModel, string? installTo, string? outPath, string axFolder)
    {
        if (!string.IsNullOrWhiteSpace(explicitModel)) return (explicitModel, explicitModel);
        if (!string.IsNullOrWhiteSpace(installTo)) return (installTo, installTo);
        if (string.IsNullOrWhiteSpace(outPath)) return (null, null);

        var dir = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        if (!string.Equals(dir.Name, axFolder, StringComparison.OrdinalIgnoreCase)) return (null, null);
        var model = dir.Parent;
        var package = model?.Parent;
        return (package?.Name, model?.Name);
    }
}

/// <summary>Scaffolds an <c>AxResource</c> manifest, optionally copying the file it describes into place.</summary>
public sealed class GenerateResourceCommand : Command<GenerateResourceCommand.Settings>
{
    public sealed class Settings : GenerateSettings
    {
        [CommandArgument(0, "<NAME>")]
        [System.ComponentModel.Description("Resource name.")]
        public string Name { get; init; } = "";

        [CommandOption("--file-name <FILE>")]
        [System.ComponentModel.Description("Bare file name the resource ships (e.g. GPS.png). Defaults to the name of --source.")]
        public string? FileName { get; init; }

        [CommandOption("--source <PATH>")]
        [System.ComponentModel.Description("Local file to copy into AxResource/ResourceContent/<Type>/ next to the manifest. Without it only the manifest is written and the content is yours to place.")]
        public string? Source { get; init; }

        [CommandOption("--type <TYPE>")]
        [System.ComponentModel.Description("ResourceType: Images (default, omitted from the XML) | Data | XmlDoc | Html | Scripts | Styles | Text | Certificate | PowerBIReport | PCFControl | Audio | Video | PublishCSS | OnlineHelpCSS | ToolbarCSS.")]
        public string? Type { get; init; }

        [CommandOption("--model <NAME>")]
        [System.ComponentModel.Description("Model whose folder holds the content — the first segment of RelativeUriInModelStore. Defaults to --install-to, or to the model folder --out sits in.")]
        public string? Model { get; init; }

        [CommandOption("--label <KEY>")]
        public string? Label { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        if (string.IsNullOrWhiteSpace(settings.Name))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "Resource name required."));

        var fileName = settings.FileName;
        if (string.IsNullOrWhiteSpace(fileName) && !string.IsNullOrWhiteSpace(settings.Source))
            fileName = Path.GetFileName(settings.Source);
        if (string.IsNullOrWhiteSpace(fileName))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "--file-name <FILE> or --source <PATH> required."));
        if (!string.IsNullOrWhiteSpace(settings.Source) && !File.Exists(settings.Source))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, $"--source '{settings.Source}' does not exist."));

        if (!GenerateViewCommand.TryResolveOutPath(kind, settings, Folders.Resource, settings.Name, out var outPath, out var pathFailure))
            return pathFailure;

        var (_, model) = ModelOfOutput.Resolve(settings.Model, settings.InstallTo, outPath, Folders.Resource);
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(model))
        {
            model = settings.Name;
            warnings.Add($"No --model given and --out is not inside a <package>/<model>/AxResource/ folder, so RelativeUriInModelStore starts with '{model}'. Pass --model <Model> for the real path.");
        }

        XDocument doc;
        try { doc = ConfigurationScaffolder.Resource(settings.Name, fileName!, model!, settings.Type, settings.Label); }
        catch (ArgumentException ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, ex.Message));
        }

        try
        {
            var gate = GenerateInstaller.Gate(settings, settings.Name, doc);
            if (gate.Failure is not null) return RenderHelpers.Render(kind, gate.Failure);

            var res = GenerateInstaller.Write(gate, doc, outPath!, settings.Overwrite);

            var contentDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outPath!))!, "ResourceContent",
                ConfigurationScaffolder.ResourceContentFolder(settings.Type ?? "Images"));
            var contentPath = Path.Combine(contentDir, fileName!);
            string? copied = null;
            if (!string.IsNullOrWhiteSpace(settings.Source))
            {
                Directory.CreateDirectory(contentDir);
                File.Copy(settings.Source!, contentPath, overwrite: settings.Overwrite);
                copied = contentPath;
            }
            else if (!File.Exists(contentPath))
            {
                warnings.Add($"Manifest only: the file it describes is not at {contentPath}. Copy it there (or re-run with --source <PATH>) before building — a resource whose content is missing fails at deployment, not at compile time.");
            }

            return GenerateInstaller.Done(kind, gate, settings, new
            {
                kind = "AxResource",
                name = settings.Name,
                fileName,
                type = settings.Type ?? "Images",
                model,
                content = copied,
                path = res.Path,
                bytes = res.Bytes,
                backup = res.BackupPath,
                installedTo = settings.InstallTo,
            }, warnings);
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed, ex.Message));
        }
    }
}

/// <summary>Scaffolds an <c>AxLabelFile</c> manifest for one language and its <c>.label.txt</c>.</summary>
public sealed class GenerateLabelFileCommand : Command<GenerateLabelFileCommand.Settings>
{
    public sealed class Settings : GenerateSettings
    {
        [CommandArgument(0, "<LABEL_FILE_ID>")]
        [System.ComponentModel.Description("Label file id — the part before the colon in @Id:Key (e.g. ConFleet). Letters, digits and underscore, starting with a letter.")]
        public string LabelFileId { get; init; } = "";

        [CommandOption("--language <LANG>")]
        [System.ComponentModel.Description("Language of this manifest, e.g. en-US (default). One AxLabelFile per language.")]
        public string? Language { get; init; }

        [CommandOption("--model <NAME>")]
        [System.ComponentModel.Description("Model whose folder holds the content (RelativeUriInModelStore). Defaults to --install-to, or to the model folder --out sits in.")]
        public string? Model { get; init; }

        [CommandOption("--entry <SPEC>")]
        [System.ComponentModel.Description("Repeatable initial label: <Key>=<Text>. Written into the .label.txt, not the manifest.")]
        public string[] Labels { get; init; } = Array.Empty<string>();
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var id = settings.LabelFileId?.Trim() ?? "";
        if (id.Length == 0)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "Label file id required."));
        if (!LabelFileWriter.IsReferenceableLabelFileId(id))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("LABEL_FILE_UNREFERENCEABLE",
                $"'{id}' cannot be named by an @File:Id token, so no label in it could ever be referenced.",
                "A label file id is letters, digits and underscore, starting with a letter — no spaces, dots or dashes."));

        var language = string.IsNullOrWhiteSpace(settings.Language) ? "en-US" : settings.Language!.Trim();
        var entries = new List<(string Key, string Text)>();
        foreach (var raw in settings.Labels)
        {
            var kv = raw.Split('=', 2, StringSplitOptions.TrimEntries);
            if (kv.Length != 2 || kv[0].Length == 0)
                return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, $"Invalid --entry '{raw}'. Expected <Key>=<Text>."));
            entries.Add((kv[0], kv[1]));
        }

        var objectName = ConfigurationScaffolder.LabelFileObjectName(id, language);
        if (!GenerateViewCommand.TryResolveOutPath(kind, settings, Folders.LabelFile, objectName, out var outPath, out var pathFailure))
            return pathFailure;

        var (package, model) = ModelOfOutput.Resolve(settings.Model, settings.InstallTo, outPath, Folders.LabelFile);
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(model))
        {
            package = model = id;
            warnings.Add($"No --model given and --out is not inside a <package>/<model>/AxLabelFile/ folder, so RelativeUriInModelStore starts with '{id}\\{id}'. Pass --model <Model> for the real path.");
        }

        XDocument doc;
        try { doc = ConfigurationScaffolder.LabelFile(id, language, package!, model!); }
        catch (ArgumentException ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, ex.Message));
        }

        try
        {
            var gate = GenerateInstaller.Gate(settings, objectName, doc);
            if (gate.Failure is not null) return RenderHelpers.Render(kind, gate.Failure);

            var res = GenerateInstaller.Write(gate, doc, outPath!, settings.Overwrite);

            // The content file the manifest points at, next to it under LabelResources/<lang>/.
            var contentPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outPath!))!,
                "LabelResources", language, ConfigurationScaffolder.LabelContentFileName(id, language));
            Directory.CreateDirectory(Path.GetDirectoryName(contentPath)!);
            if (File.Exists(contentPath) && !settings.Overwrite)
            {
                warnings.Add($"{contentPath} already exists and was left alone; --entry values were not written. Use `d365fo labels create` to add to it, or --overwrite to replace it.");
            }
            else
            {
                var sb = new StringBuilder();
                foreach (var (key, text) in entries) sb.Append(key).Append('=').Append(text).Append("\r\n");
                File.WriteAllText(contentPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            }

            return GenerateInstaller.Done(kind, gate, settings, new
            {
                kind = "AxLabelFile",
                name = objectName,
                labelFileId = id,
                language,
                model,
                labels = entries.Count,
                content = contentPath,
                path = res.Path,
                bytes = res.Bytes,
                backup = res.BackupPath,
                installedTo = settings.InstallTo,
            }, warnings);
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed, ex.Message));
        }
    }
}
