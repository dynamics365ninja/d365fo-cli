using D365FO.Core;
using D365FO.Core.Journal;
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
        [CommandArgument(0, "[KEY]")]
        [System.ComponentModel.Description("Label key. Optional when one or more --entry pairs are given.")]
        public string Key { get; init; } = "";

        [CommandArgument(1, "[VALUE]")]
        [System.ComponentModel.Description("Label value. Required when <KEY> is given.")]
        public string Value { get; init; } = "";

        [CommandOption("--entry <KEY=VALUE>")]
        [System.ComponentModel.Description("Repeatable: create several keys in one pass. Split on the first '=' so values may contain '='. Each key is written independently — one failure does not stop the rest.")]
        public string[] Entries { get; init; } = Array.Empty<string>();

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

        // A single positional key keeps the original one-shot behaviour (including a
        // hard KEY_EXISTS failure); --entry switches to batch semantics where each
        // key stands or falls on its own.
        var batchMode = settings.Entries.Length > 0;
        var entries = new List<(string Key, string Value)>();
        if (!string.IsNullOrWhiteSpace(settings.Key))
            entries.Add((settings.Key, settings.Value));
        foreach (var raw in settings.Entries)
        {
            // Split on the FIRST '=' only: label values legitimately contain '='.
            var eq = raw.IndexOf('=');
            if (eq <= 0)
                return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                    $"Malformed --entry '{raw}'.",
                    hint: "Expected <KEY>=<VALUE>, e.g. --entry FmVehicle=Vehicle."));
            entries.Add((raw[..eq].Trim(), raw[(eq + 1)..]));
        }

        if (entries.Count == 0)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "Label key required.",
                hint: "Pass <KEY> <VALUE>, or one or more --entry <KEY>=<VALUE> pairs."));

        var blank = entries.FirstOrDefault(e => string.IsNullOrWhiteSpace(e.Key));
        if (blank.Key is not null && string.IsNullOrWhiteSpace(blank.Key))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "Label key required."));

        var duplicate = entries.GroupBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                $"Key '{duplicate.Key}' appears more than once in this batch.",
                hint: "Each key may only be written once per invocation — the later value would silently win."));

        var hasFile    = !string.IsNullOrWhiteSpace(settings.File);
        var hasInstall = !string.IsNullOrWhiteSpace(settings.InstallTo);

        if (!hasFile && !hasInstall)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                "--file <PATH> or --install-to <MODEL> is required.",
                hint: "Use --file for an explicit absolute path, or --install-to <MODEL> to resolve the path automatically from D365FO_CUSTOM_PACKAGES_PATH or D365FO_PACKAGES_PATH."));

        var targets = LabelTargets.Resolve(
            settings.File, settings.InstallTo, settings.Lang, settings.LabelFile,
            settings.AllowExtensionLabelFile);
        if (targets.Failure is not null) return RenderHelpers.Render(kind, targets.Failure);
        var resolvedFiles = targets.Files;

        if (!batchMode)
        {
            // ---- Legacy single-key path: unchanged, including the hard KEY_EXISTS. ----
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
                    LabelJournalRecorder.RecordCreateOrUpdate(res, "labels create");
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

        // ---- Batch path: per-key error isolation. ----
        // One bad key (already present, unwritable file) must not cost the caller the
        // other writes, so each key is attempted independently and reported on its own.
        // The payload mirrors the MCP `labels action=create` bulk shape.
        var entryResults = new List<BatchEntryResult>(entries.Count);
        int ok = 0, failed = 0;
        foreach (var (key, value) in entries)
        {
            var files = new List<object>();
            string? errorCode = null, errorMessage = null;

            foreach (var file in resolvedFiles)
            {
                try
                {
                    var res = LabelFileWriter.CreateOrUpdate(file, key, value, settings.Overwrite);
                    if (res.Outcome == WriteOutcome.KeyExists)
                    {
                        errorCode ??= "KEY_EXISTS";
                        errorMessage ??= $"Label '{key}' already exists in {file} (current value: {res.OldValue}). Pass --overwrite to replace.";
                        continue;
                    }
                    LabelJournalRecorder.RecordCreateOrUpdate(res, "labels create");
                    files.Add(new
                    {
                        outcome = res.Outcome.ToString(),
                        file = res.Path,
                        oldValue = res.OldValue,
                        newValue = res.NewValue,
                    });
                }
                catch (Exception ex)
                {
                    errorCode ??= D365FoErrorCodes.WriteFailed;
                    errorMessage ??= $"{file}: {ex.Message}";
                }
            }

            var entryOk = errorCode is null;
            if (entryOk) ok++; else failed++;
            entryResults.Add(new BatchEntryResult(key, entryOk, errorCode, errorMessage, files));
        }

        var summary = new { total = entries.Count, created = ok, failed, results = entryResults };

        // Nothing landed at all — that is a failed operation, not a partial one, and
        // the caller deserves a non-zero exit rather than a success envelope to parse.
        if (ok == 0)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                "BATCH_FAILED",
                $"All {entries.Count} label(s) failed to write; the first error was: {entryResults[0].Message}",
                hint: "Pass --overwrite to replace existing keys, or check the target label file is writable."));

        // Partial success stays ok:true — the writes that succeeded are real and
        // already journaled. Failures are repeated as warnings so they are visible
        // without walking `results`; scripts should gate on `data.failed == 0`.
        var warnings = entryResults
            .Where(r => !r.Ok)
            .Select(r => $"label '{r.Key}': {r.Message}")
            .ToList();

        return RenderHelpers.Render(kind, ToolResult<object>.Success(
            (object)summary, warnings.Count > 0 ? warnings : null));
    }

    /// <summary>Per-key outcome of a <c>--entry</c> batch; one bad key does not sink the rest.</summary>
    private sealed record BatchEntryResult(
        string Key,
        bool Ok,
        string? Error,
        string? Message,
        IReadOnlyList<object> Files);

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
            LabelJournalRecorder.RecordRename(res, settings.OldKey, "labels rename");
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
            LabelJournalRecorder.RecordDelete(res, "labels delete");
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

/// <summary>
/// Where a label write lands: an explicit file, or one file per language under a model.
/// </summary>
/// <remarks>
/// Shared by <c>labels create</c> and <c>labels update</c>. The two differ in what they do to
/// an entry, not in where they look for it, and a second copy of "which file is this label in"
/// is a second place for the on-disk locale casing rule to be got wrong.
/// </remarks>
internal static class LabelTargets
{
    internal sealed record Resolution(IReadOnlyList<string> Files, ToolResult<object>? Failure);

    internal static Resolution Resolve(
        string? file, string? installTo, string? lang, string? labelFile, bool allowExtensionLabelFile)
    {
        var files = new List<string>();

        if (!string.IsNullOrWhiteSpace(file))
        {
            files.Add(file!);
        }
        else
        {
            var cfg = D365FoSettings.FromEnvironment();
            // Search custom paths first (write target for git repo models), then standard path.
            var allRoots = cfg.CustomPackagesPaths.Concat(new[] { cfg.PackagesPath });
            var root = allRoots
                .FirstOrDefault(r => !string.IsNullOrEmpty(r) && Directory.Exists(Path.Combine(r!, installTo!)))
                ?? cfg.CustomPackagesPaths.FirstOrDefault()
                ?? cfg.PackagesPath;
            if (string.IsNullOrEmpty(root))
                return new Resolution([], ToolResult<object>.Fail("INSTALL_FAILED",
                    $"Cannot resolve label file path for model '{installTo}': neither D365FO_CUSTOM_PACKAGES_PATH nor D365FO_PACKAGES_PATH is set.",
                    hint: "Set D365FO_CUSTOM_PACKAGES_PATH to your git repo PackagesLocalDirectory, or use --file with an absolute path."));

            var langs = (string.IsNullOrWhiteSpace(lang) ? "en-us" : lang!)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var lf = string.IsNullOrWhiteSpace(labelFile) ? installTo! : labelFile!;
            var resourcesDir = Path.Combine(root!, installTo!, installTo!, "AxLabelFile", "LabelResources");
            foreach (var l in langs)
            {
                var diskLang = LabelCreateCommand.ResolveOnDiskCasing(resourcesDir, l);
                files.Add(Path.Combine(resourcesDir, diskLang, $"{lf}.{diskLang}.label.txt"));
            }
        }

        if (!allowExtensionLabelFile)
        {
            var extFile = files.FirstOrDefault(LabelFileWriter.IsExtensionLabelFile);
            if (extFile is not null)
                return new Resolution([], ToolResult<object>.Fail(
                    "EXTENSION_LABEL_FILE",
                    $"'{Path.GetFileName(extFile)}' is a label file EXTENSION — it only extends a base label file owned by another model. New labels belong in the model's ORIGINAL label file.",
                    hint: "Target the model's own label file (e.g. --label-file <MODEL>), or pass --allow-extension-label-file to override."));
        }

        return new Resolution(files, null);
    }
}

/// <summary>
/// <c>d365fo labels update</c> — correct the text of a label that already exists.
/// </summary>
/// <remarks>
/// Distinct from <c>labels create --overwrite</c> on purpose: create writes a new entry when the
/// key is absent, so a mistyped key in a correction produces a second label instead of fixing the
/// first — and the caller is told "created", which reads as success. Fixing a typo in a
/// translation used to mean delete plus create, which loses the entry's position in the file and
/// its comment block on the way.
/// </remarks>
public sealed class LabelUpdateCommand : Command<LabelUpdateCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<KEY>")]
        [System.ComponentModel.Description("Label key to correct. Must already exist — this never creates.")]
        public string Key { get; init; } = "";

        [CommandArgument(1, "<VALUE>")]
        [System.ComponentModel.Description("The corrected text.")]
        public string Value { get; init; } = "";

        [CommandOption("--file <PATH>")]
        [System.ComponentModel.Description("Target <Name>.<lang>.label.txt file (absolute path). Required unless --install-to is used.")]
        public string? File { get; init; }

        [CommandOption("--install-to <MODEL>")]
        [System.ComponentModel.Description("Model name; resolves the label file per language, as `labels create` does.")]
        public string? InstallTo { get; init; }

        [CommandOption("--lang <LANG>")]
        [System.ComponentModel.Description("Language code(s), comma-separated (default: en-us). Each is updated independently.")]
        public string? Lang { get; init; }

        [CommandOption("--label-file <NAME>")]
        [System.ComponentModel.Description("Label file name (without extension) used with --install-to (default: model name).")]
        public string? LabelFile { get; init; }

        [CommandOption("--allow-extension-label-file")]
        [System.ComponentModel.Description("Permit writing into a label file EXTENSION (…_Extension…).")]
        public bool AllowExtensionLabelFile { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);

        if (string.IsNullOrWhiteSpace(settings.Key))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "Label key required."));
        if (string.IsNullOrWhiteSpace(settings.File) && string.IsNullOrWhiteSpace(settings.InstallTo))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                "--file <PATH> or --install-to <MODEL> is required."));

        var targets = LabelTargets.Resolve(
            settings.File, settings.InstallTo, settings.Lang, settings.LabelFile,
            settings.AllowExtensionLabelFile);
        if (targets.Failure is not null) return RenderHelpers.Render(kind, targets.Failure);

        var results = new List<object>();
        var missing = new List<string>();
        try
        {
            foreach (var file in targets.Files)
            {
                var res = LabelFileWriter.Update(file, settings.Key, settings.Value);
                if (res.Outcome == WriteOutcome.KeyMissing)
                {
                    missing.Add(file);
                    continue;
                }
                LabelJournalRecorder.RecordCreateOrUpdate(res, "labels update");
                results.Add(new
                {
                    outcome = res.Outcome.ToString(),
                    file = res.Path,
                    key = res.Key,
                    oldValue = res.OldValue,
                    newValue = res.NewValue,
                });
            }
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed, ex.Message));
        }

        // Nothing updated anywhere is a failure, not an empty success: the caller believes a
        // correction landed.
        if (results.Count == 0)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("KEY_MISSING",
                $"Label '{settings.Key}' is in none of the target files, so nothing was corrected.",
                hint: $"Checked: {string.Join(", ", targets.Files)}. Use `labels create` to add it, or "
                    + "`labels search` to find the key it was actually written under."));

        return RenderHelpers.Render(kind, ToolResult<object>.Success(new
        {
            key = settings.Key,
            updated = results.Count,
            files = results,
        }, missing.Count > 0
            ? [$"{missing.Count} target file(s) do not carry this key and were left alone: {string.Join(", ", missing)}"]
            : null));
    }
}
