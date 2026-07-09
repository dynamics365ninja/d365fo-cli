using D365FO.Core;
using D365FO.Core.Labels;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Label;

/// <summary>
/// <c>d365fo label create|update|rename|delete</c> — in-place edits of
/// <c>*.label.txt</c> resource files. ROADMAP §4.2.
/// </summary>
public sealed class LabelCreateCommand : Command<LabelCreateCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<KEY>")]
        public string Key { get; init; } = "";

        [CommandArgument(1, "<VALUE>")]
        public string Value { get; init; } = "";

        [CommandOption("--file <PATH>")]
        [System.ComponentModel.Description("Target <Name>.<lang>.label.txt file (absolute path). Created if missing. Required unless --install-to is used.")]
        public string? File { get; init; }

        [CommandOption("--install-to <MODEL>")]
        [System.ComponentModel.Description("Model name. Auto-resolves the label file path to <PackagesPath>/<MODEL>/<MODEL>/AxLabelFile/LabelResources/<lang>/<MODEL>.<lang>.label.txt. Requires D365FO_CUSTOM_PACKAGES_PATH or D365FO_PACKAGES_PATH.")]
        public string? InstallTo { get; init; }

        [CommandOption("--lang <LANG>")]
        [System.ComponentModel.Description("Language code(s) for --install-to path resolution; comma-separated for multiple locales (default: en-us). Existing on-disk folder casing (en-US vs en-us) is reused to avoid duplicate locale folders on case-sensitive file systems.")]
        public string? Lang { get; init; }

        [CommandOption("--label-file <NAME>")]
        [System.ComponentModel.Description("Label file name (without extension) used with --install-to (default: model name).")]
        public string? LabelFile { get; init; }

        [CommandOption("--overwrite")]
        [System.ComponentModel.Description("Replace an existing value. Default: fail with KEY_EXISTS.")]
        public bool Overwrite { get; init; }

        [CommandOption("--allow-extension-label-file")]
        [System.ComponentModel.Description("Permit writing into a label file EXTENSION (…_Extension…). Off by default: new labels belong in the model's original label file.")]
        public bool AllowExtensionLabelFile { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        if (string.IsNullOrWhiteSpace(settings.Key))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "Label key required."));

        var hasFile    = !string.IsNullOrWhiteSpace(settings.File);
        var hasInstall = !string.IsNullOrWhiteSpace(settings.InstallTo);

        if (!hasFile && !hasInstall)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                "--file <PATH> or --install-to <MODEL> is required.",
                hint: "Use --file for an explicit absolute path, or --install-to <MODEL> to resolve the path automatically from D365FO_CUSTOM_PACKAGES_PATH or D365FO_PACKAGES_PATH."));

        var resolvedFiles = new List<string>();
        if (hasFile)
        {
            resolvedFiles.Add(settings.File!);
        }
        else
        {
            var cfg = D365FoSettings.FromEnvironment();
            // Search custom paths first (write target for git repo models), then standard path.
            var allRoots = cfg.CustomPackagesPaths.Concat(new[] { cfg.PackagesPath });
            var root = allRoots
                .FirstOrDefault(r => !string.IsNullOrEmpty(r) && Directory.Exists(Path.Combine(r!, settings.InstallTo!)))
                ?? cfg.CustomPackagesPaths.FirstOrDefault()
                ?? cfg.PackagesPath;
            if (string.IsNullOrEmpty(root))
                return RenderHelpers.Render(kind, ToolResult<object>.Fail("INSTALL_FAILED",
                    $"Cannot resolve label file path for model '{settings.InstallTo}': neither D365FO_CUSTOM_PACKAGES_PATH nor D365FO_PACKAGES_PATH is set.",
                    hint: "Set D365FO_CUSTOM_PACKAGES_PATH to your git repo PackagesLocalDirectory, or use --file with an absolute path."));

            var langs = (string.IsNullOrWhiteSpace(settings.Lang) ? "en-us" : settings.Lang!)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var lf = string.IsNullOrWhiteSpace(settings.LabelFile) ? settings.InstallTo! : settings.LabelFile!;
            var resourcesDir = System.IO.Path.Combine(root!, settings.InstallTo!, settings.InstallTo!, "AxLabelFile", "LabelResources");
            foreach (var lang in langs)
            {
                var diskLang = ResolveOnDiskCasing(resourcesDir, lang);
                resolvedFiles.Add(System.IO.Path.Combine(resourcesDir, diskLang, $"{lf}.{diskLang}.label.txt"));
            }
        }

        if (!settings.AllowExtensionLabelFile)
        {
            var extFile = resolvedFiles.FirstOrDefault(LabelFileWriter.IsExtensionLabelFile);
            if (extFile is not null)
                return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                    "EXTENSION_LABEL_FILE",
                    $"'{System.IO.Path.GetFileName(extFile)}' is a label file EXTENSION — it only extends a base label file owned by another model. New labels belong in the model's ORIGINAL label file.",
                    hint: "Target the model's own label file (e.g. --label-file <MODEL>), or pass --allow-extension-label-file to override."));
        }

        try
        {
            var results = new List<object>();
            foreach (var file in resolvedFiles)
            {
                var res = LabelFileWriter.CreateOrUpdate(file, settings.Key, settings.Value, settings.Overwrite);
                if (res.Outcome == WriteOutcome.KeyExists)
                    return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                        "KEY_EXISTS",
                        $"Label '{settings.Key}' already exists in {file}. Pass --overwrite to replace.",
                        hint: $"Existing value: {res.OldValue}"));
                results.Add(new
                {
                    outcome = res.Outcome.ToString(),
                    file = res.Path,
                    key = res.Key,
                    oldValue = res.OldValue,
                    newValue = res.NewValue,
                });
            }

            return RenderHelpers.Render(kind, ToolResult<object>.Success(
                results.Count == 1 ? results[0] : new { key = settings.Key, files = results }));
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed, ex.Message));
        }
    }

    /// <summary>
    /// Reuse the existing locale folder's casing ("en-US" vs "en-us") so writes
    /// on case-sensitive file systems never create a duplicate locale folder.
    /// </summary>
    internal static string ResolveOnDiskCasing(string resourcesDir, string lang)
    {
        try
        {
            if (Directory.Exists(resourcesDir))
            {
                foreach (var dir in Directory.EnumerateDirectories(resourcesDir))
                {
                    var name = System.IO.Path.GetFileName(dir);
                    if (string.Equals(name, lang, StringComparison.OrdinalIgnoreCase))
                        return name;
                }
            }
        }
        catch { /* fall through to requested casing */ }
        return lang;
    }
}

public sealed class LabelRenameCommand : Command<LabelRenameCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<OLD>")]
        public string OldKey { get; init; } = "";

        [CommandArgument(1, "<NEW>")]
        public string NewKey { get; init; } = "";

        [CommandOption("--file <PATH>")]
        public string? File { get; init; }

        [CommandOption("--overwrite")]
        public bool Overwrite { get; init; }

        [CommandOption("--allow-extension-label-file")]
        [System.ComponentModel.Description("Permit renaming inside a label file EXTENSION (…_Extension…). Off by default.")]
        public bool AllowExtensionLabelFile { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        if (string.IsNullOrWhiteSpace(settings.OldKey) || string.IsNullOrWhiteSpace(settings.NewKey))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "Both <OLD> and <NEW> label keys required."));
        if (string.IsNullOrWhiteSpace(settings.File))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "--file <PATH> required."));
        if (!settings.AllowExtensionLabelFile && LabelFileWriter.IsExtensionLabelFile(settings.File!))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                "EXTENSION_LABEL_FILE",
                $"'{System.IO.Path.GetFileName(settings.File)}' is a label file EXTENSION — it only extends a base label file owned by another model.",
                hint: "Rename the label in the model's ORIGINAL label file, or pass --allow-extension-label-file to override."));

        try
        {
            var res = LabelFileWriter.Rename(settings.File!, settings.OldKey, settings.NewKey, settings.Overwrite);
            return res.Outcome switch
            {
                WriteOutcome.FileMissing => RenderHelpers.Render(kind, ToolResult<object>.Fail("FILE_NOT_FOUND", $"Label file not found: {settings.File}")),
                WriteOutcome.KeyMissing => RenderHelpers.Render(kind, ToolResult<object>.Fail("KEY_NOT_FOUND", $"Label '{settings.OldKey}' not present in file.")),
                WriteOutcome.KeyExists => RenderHelpers.Render(kind, ToolResult<object>.Fail("KEY_EXISTS", $"Target key '{settings.NewKey}' already exists. Pass --overwrite to replace.")),
                _ => RenderHelpers.Render(kind, ToolResult<object>.Success(new
                {
                    outcome = res.Outcome.ToString(),
                    file = res.Path,
                    oldKey = settings.OldKey,
                    newKey = settings.NewKey,
                    value = res.NewValue,
                })),
            };
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed, ex.Message));
        }
    }
}

public sealed class LabelDeleteCommand : Command<LabelDeleteCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<KEY>")]
        public string Key { get; init; } = "";

        [CommandOption("--file <PATH>")]
        public string? File { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        if (string.IsNullOrWhiteSpace(settings.Key))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "Label key required."));
        if (string.IsNullOrWhiteSpace(settings.File))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "--file <PATH> required."));

        try
        {
            var res = LabelFileWriter.Delete(settings.File!, settings.Key);
            return res.Outcome switch
            {
                WriteOutcome.FileMissing => RenderHelpers.Render(kind, ToolResult<object>.Fail("FILE_NOT_FOUND", $"Label file not found: {settings.File}")),
                WriteOutcome.KeyMissing => RenderHelpers.Render(kind, ToolResult<object>.Fail("KEY_NOT_FOUND", $"Label '{settings.Key}' not present in file.")),
                _ => RenderHelpers.Render(kind, ToolResult<object>.Success(new
                {
                    outcome = res.Outcome.ToString(),
                    file = res.Path,
                    key = res.Key,
                    removedValue = res.OldValue,
                })),
            };
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed, ex.Message));
        }
    }
}
