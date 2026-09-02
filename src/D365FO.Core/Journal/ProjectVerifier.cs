using System.Xml.Linq;

namespace D365FO.Core.Journal;

/// <summary>
/// Does the model on disk and its Visual Studio project agree about what exists?
/// </summary>
/// <remarks>
/// <para>
/// A generated object reaches the AOT only if the XML is in the model folder AND the
/// <c>.rnrproj</c> references it. The two can disagree in both directions and neither one
/// complains: an XML the project does not list builds nowhere, and a project entry whose file is
/// gone fails the build with a path, not a reason. The write path registers objects as it
/// creates them, so the usual cause of a disagreement is a hand edit, a merge, or a file copied
/// in from elsewhere — exactly the cases nobody is watching.
/// </para>
/// <para>
/// A project with no explicit item list is not judged: some project shapes glob their content,
/// and calling every file "unregistered" there would be a wall of false findings.
/// </para>
/// </remarks>
public static class ProjectVerifier
{
    /// <param name="Object">Object name, without the .xml.</param>
    /// <param name="AxFolder">AOT subfolder it lives in, e.g. AxTable.</param>
    /// <param name="Finding">MISSING_FILE | UNREGISTERED.</param>
    /// <param name="Detail">What is wrong, and what it means for a build.</param>
    public sealed record Issue(string Object, string AxFolder, string Finding, string Detail);

    public sealed record Report(
        string ModelFolder,
        string? ProjectFile,
        bool ProjectHasItemList,
        int FilesOnDisk,
        int RegisteredItems,
        IReadOnlyList<Issue> Issues);

    /// <summary>Cross-check a model folder against its <c>.rnrproj</c>.</summary>
    /// <param name="modelFolder">
    /// The inner model folder — the one holding the <c>Ax*</c> subdirectories.
    /// </param>
    /// <param name="expected">
    /// Optional names the caller believes it created (<c>AxTable/ConFleetVehicle</c> or plain
    /// <c>ConFleetVehicle</c>). Each is checked explicitly, so "I just generated these five" gets
    /// an answer about those five rather than about the whole model.
    /// </param>
    public static ToolResult<object> Verify(string modelFolder, IReadOnlyList<string>? expected = null)
    {
        if (string.IsNullOrWhiteSpace(modelFolder) || !Directory.Exists(modelFolder))
            return ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                $"No such model folder: {modelFolder}",
                "Point this at the inner <Model>/<Model> folder — the one holding the Ax* subdirectories.");

        var onDisk = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // include -> full path
        foreach (var axDir in Directory.EnumerateDirectories(modelFolder, "Ax*"))
        {
            var axName = Path.GetFileName(axDir);
            foreach (var file in Directory.EnumerateFiles(axDir, "*.xml", SearchOption.TopDirectoryOnly))
                onDisk[axName + "\\" + Path.GetFileNameWithoutExtension(file) + ".xml"] = file;
        }

        var projectFile = RnrProjRegistry.FindRnrProj(modelFolder);
        var registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hasItemList = false;

        if (projectFile is not null)
        {
            try
            {
                var doc = XDocument.Load(projectFile);
                var ns = doc.Root?.Name.Namespace ?? XNamespace.None;
                foreach (var group in doc.Root?.Elements(ns + "ItemGroup") ?? [])
                foreach (var item in group.Elements())
                {
                    var include = item.Attribute("Include")?.Value;
                    if (string.IsNullOrWhiteSpace(include)) continue;
                    hasItemList = true;
                    registered.Add(include.Replace('/', '\\'));
                }
            }
            catch (Exception ex)
            {
                return ToolResult<object>.Fail(D365FoErrorCodes.SourceUnreadable,
                    $"Could not read {projectFile}: {ex.Message}");
            }
        }

        var issues = new List<Issue>();

        // Project entries whose file is gone.
        foreach (var include in registered)
        {
            if (onDisk.ContainsKey(include)) continue;
            var parts = include.Split('\\');
            issues.Add(new Issue(
                Path.GetFileNameWithoutExtension(parts[^1]),
                parts.Length > 1 ? parts[0] : "",
                "MISSING_FILE",
                $"The project references {include}, and no such file is in the model. The build fails on the path, "
                + "which does not say that the object was deleted or never arrived."));
        }

        // Files the project does not list — only meaningful when it lists anything at all.
        if (hasItemList)
        {
            foreach (var (include, _) in onDisk)
            {
                if (registered.Contains(include)) continue;
                var parts = include.Split('\\');
                issues.Add(new Issue(
                    Path.GetFileNameWithoutExtension(parts[^1]),
                    parts[0],
                    "UNREGISTERED",
                    $"{include} is in the model folder but not in the project, so it is not compiled and the AOT "
                    + "never sees it. Nothing reports this — the object simply does not exist as far as a build is concerned."));
            }
        }

        // Explicitly expected objects, answered by name.
        var expectedReport = new List<object>();
        foreach (var name in expected ?? [])
        {
            var wanted = name.Replace('/', '\\');
            if (!wanted.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) wanted += ".xml";
            var match = wanted.Contains('\\')
                ? (onDisk.ContainsKey(wanted) ? wanted : null)
                : onDisk.Keys.FirstOrDefault(k => k.EndsWith("\\" + wanted, StringComparison.OrdinalIgnoreCase));

            expectedReport.Add(new
            {
                name,
                onDisk = match is not null,
                registered = match is not null && registered.Contains(match),
                include = match,
            });
        }

        var warnings = new List<string>();
        if (projectFile is null)
            warnings.Add("No .rnrproj found beside this model, so only the on-disk side could be checked. "
                       + "An object outside the project is not compiled, and nothing here can tell you which ones.");
        else if (!hasItemList)
            warnings.Add($"{Path.GetFileName(projectFile)} declares no explicit item list; unregistered files are "
                       + "therefore not reported, because a globbing project legitimately lists nothing.");
        if (issues.Count > 0)
            warnings.Add($"{issues.Count} object(s) the model and the project disagree about.");

        return ToolResult<object>.Success(new
        {
            modelFolder,
            projectFile,
            projectHasItemList = hasItemList,
            filesOnDisk = onDisk.Count,
            registeredItems = registered.Count,
            issueCount = issues.Count,
            issues,
            expected = expectedReport.Count > 0 ? expectedReport : null,
        }, warnings.Count > 0 ? warnings : null);
    }
}
