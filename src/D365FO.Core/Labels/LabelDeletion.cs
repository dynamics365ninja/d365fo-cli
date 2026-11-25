using System.Text.RegularExpressions;
using D365FO.Core.Bridge;
using D365FO.Core.Index;
using D365FO.Core.Journal;

namespace D365FO.Core.Labels;

/// <summary>What to delete, and how careful to be about it.</summary>
/// <param name="Key">The label key.</param>
/// <param name="Files">The label files to remove the key from.</param>
/// <param name="DryRun">Report the files and references; write nothing.</param>
/// <param name="Force">Delete although the owning model still references the label.</param>
/// <param name="AllowExtensionLabelFile">Permit deleting from a label file extension.</param>
public sealed record LabelDeleteRequest(
    string Key,
    IReadOnlyList<string> Files,
    bool DryRun = false,
    bool Force = false,
    bool AllowExtensionLabelFile = false);

/// <summary>
/// Removes a label, with the checks the CLI and the MCP adapter share: the file must be one the
/// caller may write, and a label its model still uses is not removed unless forced.
/// </summary>
public static class LabelDeletion
{
    private const int ReferenceCap = 20;

    /// <summary>
    /// The files of label file <paramref name="labelFileId"/> in a model, one per language;
    /// all languages when <paramref name="languages"/> is empty.
    /// </summary>
    public static IReadOnlyList<string> FilesOf(string modelDir, string labelFileId, IReadOnlyCollection<string>? languages = null)
    {
        var resources = Path.Combine(modelDir, "AxLabelFile", "LabelResources");
        if (!Directory.Exists(resources)) return [];
        return Directory.EnumerateDirectories(resources)
            .Where(d => languages is not { Count: > 0 }
                        || languages.Contains(Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
            .Select(d => Path.Combine(d, $"{labelFileId}.{Path.GetFileName(d)}.label.txt"))
            .Where(File.Exists)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static ToolResult<object> Run(LabelDeleteRequest request, MetadataRepository? repo, string command)
    {
        var key = request.Key;
        var files = request.Files.Select(Path.GetFullPath).ToList();

        if (!request.AllowExtensionLabelFile
            && files.FirstOrDefault(LabelFileWriter.IsExtensionLabelFile) is { } extension)
            return ToolResult<object>.Fail("EXTENSION_LABEL_FILE",
                $"'{Path.GetFileName(extension)}' is a label file EXTENSION — it only extends a base label file owned by another model.",
                "Delete the label from the model's ORIGINAL label file, or pass --allow-extension-label-file (CLI) to override.");

        foreach (var file in files)
        {
            if (ModelDirOf(file) is { } owner && IsForeignModel(owner, repo))
                return ToolResult<object>.Fail("FOREIGN_MODEL",
                    $"'{file}' belongs to model '{Path.GetFileName(owner)}', which is not one of your custom models.",
                    "A label of a model you do not own is not deleted. If the model is yours, add it to D365FO_CUSTOM_MODELS.");
            if (!File.Exists(file))
                return ToolResult<object>.Fail("FILE_NOT_FOUND", $"Label file not found: {file}");
        }

        var carriers = files.Where(f => LabelFileWriter.ReadValue(f, key) is not null).ToList();
        if (carriers.Count == 0)
            return ToolResult<object>.Fail("KEY_NOT_FOUND",
                $"Label '{key}' not present in {string.Join(", ", files)}.");

        var warnings = new List<string>();
        var references = new List<string>();
        foreach (var group in carriers.GroupBy(
                     f => (Model: ModelDirOf(f), Id: LabelFileWriter.LabelFileIdOf(f))))
        {
            var (modelDir, id) = group.Key;
            if (modelDir is null)
            {
                warnings.Add($"References were not checked: {group.First()} is not inside a model's AxLabelFile folder.");
                continue;
            }

            // A language that keeps the key keeps every reference to it resolving.
            var remaining = FilesOf(modelDir, id)
                .Select(Path.GetFullPath)
                .Where(f => !carriers.Contains(f, StringComparer.OrdinalIgnoreCase)
                            && LabelFileWriter.ReadValue(f, key) is not null)
                .ToList();
            if (remaining.Count > 0)
            {
                warnings.Add($"'{key}' is still defined in {remaining.Count} other language file(s): {string.Join(", ", remaining)}");
                continue;
            }

            references.AddRange(FindReferences(modelDir, id, key));
            warnings.Add($"Only model '{Path.GetFileName(modelDir)}' was checked for references. "
                         + $"Other models: d365fo find refs {Tokens(id, key)[0]} --all-models");
        }

        var shown = references.Take(ReferenceCap).ToList();
        if (references.Count > 0 && !request.Force && !request.DryRun)
            return ToolResult<object>.Fail("LABEL_IN_USE",
                $"Label '{key}' is referenced by {references.Count} file(s), so it was not deleted: {string.Join(", ", shown)}",
                "Change those references first, or pass --force (MCP: force=true) to delete anyway.");

        if (request.DryRun)
            return ToolResult<object>.Success((object)new
            {
                key,
                dryRun = true,
                files = carriers.Select(f => new { file = f, removedValue = LabelFileWriter.ReadValue(f, key) }).ToList(),
                referenceCount = references.Count,
                references = shown,
                wouldDelete = references.Count == 0 || request.Force,
            }, warnings.Count > 0 ? warnings : null);

        var removed = new List<object>();
        var failed = new List<string>();
        WriteResult? last = null;
        foreach (var file in carriers)
        {
            try
            {
                var res = LabelFileWriter.Delete(file, key);
                LabelJournalRecorder.RecordDelete(res, command);
                removed.Add(new { file = res.Path, removedValue = res.OldValue });
                last = res;
            }
            catch (Exception ex)
            {
                failed.Add($"{file}: {ex.Message}");
            }
        }

        if (failed.Count > 0)
            return ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed,
                $"Label '{key}' was removed from {removed.Count} of {carriers.Count} file(s). Failed: {string.Join("; ", failed)}",
                "`d365fo undo` restores the files that were written.");

        if (references.Count > 0)
            warnings.Add($"Deleted although {references.Count} file(s) still reference it: {string.Join(", ", shown)}");

        object data = files.Count == 1
            ? new { outcome = nameof(WriteOutcome.Deleted), file = last!.Path, key, removedValue = last.OldValue }
            : new { outcome = nameof(WriteOutcome.Deleted), key, files = removed };
        return ToolResult<object>.Success(data, warnings.Count > 0 ? warnings : null);
    }

    /// <summary>The model folder a <c>…/AxLabelFile/LabelResources/&lt;lang&gt;/x.label.txt</c> path sits in.</summary>
    private static string? ModelDirOf(string file)
    {
        var resources = Path.GetDirectoryName(Path.GetDirectoryName(file));
        var axFolder = Path.GetDirectoryName(resources);
        return string.Equals(Path.GetFileName(resources), "LabelResources", StringComparison.OrdinalIgnoreCase)
               && string.Equals(Path.GetFileName(axFolder), "AxLabelFile", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(axFolder)
            : null;
    }

    private static bool IsForeignModel(string modelDir, MetadataRepository? repo)
    {
        var model = Path.GetFileName(modelDir);
        var package = Path.GetDirectoryName(modelDir);
        var descriptor = package is null ? null : Path.Combine(package, "Descriptor", model + ".xml");
        if (descriptor is not null && File.Exists(descriptor)
            && Regex.IsMatch(File.ReadAllText(descriptor), @"<Publisher>\s*Microsoft\b", RegexOptions.IgnoreCase))
            return true;
        // Without configured patterns there is no statement of which models are yours.
        return D365FoSettings.FromEnvironment().CustomModels.Count > 0
               && !ObjectModifyEngine.IsCustomModel(model, repo);
    }

    /// <summary>The spellings that reference a label: <c>@File:Key</c>, and the legacy <c>@SYS123</c>.</summary>
    private static List<string> Tokens(string labelFileId, string key)
    {
        if (key.StartsWith('@')) return [key];
        var tokens = new List<string> { $"@{labelFileId}:{key}" };
        if (Regex.IsMatch(key, $"^{Regex.Escape(labelFileId)}\\d+$", RegexOptions.IgnoreCase))
            tokens.Add("@" + key);
        return tokens;
    }

    private static IEnumerable<string> FindReferences(string modelDir, string labelFileId, string key)
    {
        var pattern = new Regex(
            "(?:" + string.Join("|", Tokens(labelFileId, key).Select(Regex.Escape)) + ")(?![A-Za-z0-9_])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        foreach (var path in Directory.EnumerateFiles(modelDir, "*.xml", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(modelDir, path);
            // The label file's own descriptors name the file, not its labels.
            if (relative.StartsWith("AxLabelFile", StringComparison.OrdinalIgnoreCase)) continue;
            if (pattern.IsMatch(File.ReadAllText(path))) yield return relative.Replace('\\', '/');
        }
    }
}
