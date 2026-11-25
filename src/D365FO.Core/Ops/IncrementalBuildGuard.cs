using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace D365FO.Core.Ops;

/// <summary>
/// Decides when <c>xppc -incremental</c> would give a wrong result, so the build has to be a full
/// one.
/// </summary>
/// <remarks>
/// <para>
/// An incremental build recompiles the elements whose own source changed. Code that only uses a
/// changed table, EDT or enum is not among them, so a change that breaks it is reported as no
/// errors and the next full build fails. And a data entity it recompiles gets its OData resource
/// rewritten without its actions, which the AOS then no longer offers.
/// </para>
/// <para>
/// "Changed" means newer than both the element's compiler-metadata copy (<c>XppMetadata</c>, which
/// not every type has; the newest file of that tree stands in) and the last successful build run
/// here. The second baseline is needed because xppc leaves a metadata file alone when its content
/// does not change, even in a full build.
/// </para>
/// </remarks>
public static class IncrementalBuildGuard
{
    private static readonly string[] ShapeTypes =
        ["AxTable", "AxTableExtension", "AxEdt", "AxEdtExtension", "AxEnum", "AxEnumExtension"];

    /// <summary>The element types whose source can declare an OData action.</summary>
    private static readonly string[] ActionTypes = ["AxDataEntityView", "AxDataEntityViewExtension", "AxClass"];

    private static readonly Regex Comments = new(@"/\*[\s\S]*?\*/|//[^\r\n]*", RegexOptions.Compiled);

    private static readonly Regex ODataAction =
        new(@"\bSysODataAction(?:Attribute)?\s*\(", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ODataActionName =
        new(@"\bSysODataAction(?:Attribute)?\s*\(\s*(?:""([^""]*)""|'([^']*)')", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RuntimeActionKey =
        new(@"<(?:\w+:)?KeyValueOfstringAxODataAction\w*>\s*<(?:\w+:)?Key>([^<]*)</", RegexOptions.Compiled);

    private const int NamesShown = 5;

    /// <summary>
    /// Why <paramref name="target"/> needs a full build, or null when an incremental one is sound —
    /// including a model with no earlier build, which is left as asked.
    /// </summary>
    /// <param name="target">The model about to be built.</param>
    /// <param name="lastBuildUtc">Start of the last successful build run here, when there was one.</param>
    public static string? FullBuildReason(XppModelBuild.Target target, DateTime? lastBuildUtc)
    {
        var package = Path.Combine(target.MetadataRoot, target.Module);
        var compiled = Path.Combine(package, "XppMetadata");
        var hasTree = Directory.Exists(compiled);
        if (!hasTree && lastBuildUtc is null) return null;

        try
        {
            var newestInTree = new Lazy<DateTime?>(() => hasTree
                ? Directory.EnumerateFiles(compiled, "*.xml", SearchOption.AllDirectories)
                    .Select(f => (DateTime?)File.GetLastWriteTimeUtc(f)).Max()
                : null);

            var shapes = new List<string>();
            string? withAction = null;
            foreach (var model in Directory.EnumerateDirectories(package))
            {
                if (string.Equals(Path.GetFileName(model), "XppMetadata", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var type in ShapeTypes.Concat(ActionTypes))
                {
                    var folder = Path.Combine(model, type);
                    if (!Directory.Exists(folder)) continue;
                    var isShape = ShapeTypes.Contains(type);
                    foreach (var source in Directory.EnumerateFiles(folder, "*.xml"))
                    {
                        if (!isShape && withAction is not null) break;
                        var copy = Path.Combine(compiled, Path.GetFileName(model), type, Path.GetFileName(source));
                        // Only tables, classes and entities get a copy; for the rest the tree stands in.
                        var hasCopyOfItsOwn = type is "AxTable" or "AxClass" or "AxDataEntityView";
                        DateTime? copied = hasCopyOfItsOwn
                            ? (File.Exists(copy) ? File.GetLastWriteTimeUtc(copy) : null)
                            : newestInTree.Value;
                        var baseline = Later(copied, lastBuildUtc);
                        // No baseline: a new element where copies exist, unknown where they do not.
                        if (baseline is null && !hasCopyOfItsOwn) continue;
                        if (baseline is not null && File.GetLastWriteTimeUtc(source) <= baseline) continue;

                        var name = Path.GetFileNameWithoutExtension(source);
                        if (isShape) shapes.Add($"{type}/{name}");
                        else if (ODataAction.IsMatch(Comments.Replace(File.ReadAllText(source), "")))
                            withAction = $"{type}/{name}";
                    }
                }
            }

            var reasons = new List<string>();
            if (shapes.Count > 0)
            {
                shapes.Sort(StringComparer.OrdinalIgnoreCase);
                var more = shapes.Count > NamesShown ? $" and {shapes.Count - NamesShown} more" : "";
                reasons.Add($"{shapes.Count} table, EDT or enum element(s) changed since the last build "
                            + $"({string.Join(", ", shapes.Take(NamesShown))}{more}), and an incremental build would not recompile the code that uses them");
            }
            if (withAction is not null)
                reasons.Add($"{withAction} declares an OData action, which an incremental recompile drops from the service");
            else if (MissingActions(package) is { Count: > 0 } missing)
                reasons.Add($"the OData resources lack actions the source declares ({string.Join("; ", missing.Take(NamesShown))}), "
                            + "as an earlier incremental build leaves them, and only a full build writes them back");
            return reasons.Count == 0 ? null : string.Join("; ", reasons) + ".";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"the model could not be checked for changes an incremental build would miss ({ex.Message}).";
        }
    }

    /// <summary>
    /// <c>Entity (Action, …)</c> for each data entity whose entry in the package's
    /// <c>bin\Dynamics.AX.&lt;Package&gt;.ODataExtends.runtime</c> lacks actions its source declares.
    /// The AOS reads the actions from that file. An entity without an entry is not public.
    /// </summary>
    private static List<string> MissingActions(string package)
    {
        var bin = Path.Combine(package, "bin");
        if (!Directory.Exists(bin)) return [];
        // Named after the assembly, whose casing need not match the folder's.
        var files = Directory.GetFiles(bin, "*.ODataExtends.runtime");
        var expected = $"Dynamics.AX.{Path.GetFileName(package)}.ODataExtends.runtime";
        var runtime = files.FirstOrDefault(f => string.Equals(Path.GetFileName(f), expected, StringComparison.OrdinalIgnoreCase))
                      ?? (files.Length == 1 ? files[0] : null);
        if (runtime is null) return [];

        // One line per entity: its name, a tab, and its resource document.
        var published = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(runtime))
        {
            var tab = line.IndexOf('\t');
            if (tab <= 0) continue;
            published[line[..tab].Trim()] = RuntimeActionKey.Matches(line)
                .Select(m => m.Groups[1].Value.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        var missing = new List<string>();
        foreach (var model in Directory.EnumerateDirectories(package))
        {
            var folder = Path.Combine(model, "AxDataEntityView");
            if (string.Equals(Path.GetFileName(model), "XppMetadata", StringComparison.OrdinalIgnoreCase)
                || !Directory.Exists(folder)) continue;
            foreach (var source in Directory.EnumerateFiles(folder, "*.xml"))
            {
                if (!published.TryGetValue(Path.GetFileNameWithoutExtension(source), out var actions)) continue;
                var lost = ODataActionName.Matches(Comments.Replace(File.ReadAllText(source), ""))
                    .Select(m => (m.Groups[1].Success ? m.Groups[1] : m.Groups[2]).Value.Trim())
                    .Where(name => name.Length > 0 && !actions.Contains(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (lost.Count > 0)
                    missing.Add($"{Path.GetFileNameWithoutExtension(source)} ({string.Join(", ", lost)})");
            }
        }
        missing.Sort(StringComparer.OrdinalIgnoreCase);
        return missing;
    }

    /// <summary>When the last successful build of this package that ran here started, or null.</summary>
    public static DateTime? ReadLastBuild(XppModelBuild.Target target)
    {
        try
        {
            var stamp = StampPath(target);
            return File.Exists(stamp) ? File.GetLastWriteTimeUtc(stamp) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Remember a successful build. Kept with the configuration, not in the package.</summary>
    public static void RecordBuild(XppModelBuild.Target target, DateTime startedUtc)
    {
        try
        {
            var stamp = StampPath(target);
            Directory.CreateDirectory(Path.GetDirectoryName(stamp)!);
            File.WriteAllText(stamp, Path.Combine(target.MetadataRoot, target.Module));
            File.SetLastWriteTimeUtc(stamp, startedUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without the stamp the next incremental build may be promoted once more than needed.
        }
    }

    private static string StampPath(XppModelBuild.Target target)
    {
        var package = Path.GetFullPath(Path.Combine(target.MetadataRoot, target.Module)).ToUpperInvariant();
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(package)))[..24];
        return Path.Combine(Path.GetDirectoryName(D365FoSettings.GetDefaultConfigPath())!, "build-stamps", key);
    }

    private static DateTime? Later(DateTime? a, DateTime? b)
        => a is null ? b : b is null ? a : (a > b ? a : b);
}
