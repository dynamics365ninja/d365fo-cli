// <copyright file="SkillInstaller.cs" company="d365fo-cli contributors">
// MIT
// </copyright>

using System.Reflection;
using System.Text;
using System.Text.Json;

namespace D365FO.Core.Skills;

/// <summary>The agent a set of skills is laid out for.</summary>
public enum SkillTarget
{
    /// <summary>One folder per topic under <c>.claude/skills/</c>.</summary>
    Claude,

    /// <summary>The <c>d365fo-cli</c> skill with its references under <c>.github/skills/</c>.</summary>
    Copilot,
}

/// <summary>What an install did, or with <c>dryRun</c> would do. Paths are relative to <see cref="Directory"/>.</summary>
public sealed record SkillInstallResult(
    string Target,
    string Directory,
    IReadOnlyList<string> Written,
    IReadOnlyList<string> Unchanged,
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> LeftInPlace);

/// <summary>Existing files that an install would overwrite although this tool did not write them.</summary>
public sealed class SkillConflictException(IReadOnlyList<string> files)
    : Exception($"These files exist, were not written by d365fo and would be overwritten: {string.Join(", ", files)}")
{
    /// <summary>Full paths of the files in the way.</summary>
    public IReadOnlyList<string> Files { get; } = files;
}

/// <summary>
/// Writes the agent skills this build carries into an X++ repository. The skills are embedded
/// from <c>skills/</c>, so they describe the commands of the CLI that installs them, however
/// that CLI was installed.
/// </summary>
/// <remarks>
/// Every install records the files it wrote in a manifest beside them. Only recorded files are
/// removed when a later version drops a topic: a skill someone else put in the same folder is
/// never deleted, whatever it looks like.
/// </remarks>
public static class SkillInstaller
{
    /// <summary>Name of the record of installed files, in the folder they were installed to.</summary>
    public const string ManifestName = ".d365fo-cli-skills.json";

    private const string ResourceRoot = "D365FO.Skills/";

    private sealed record Manifest(string Version, string[] Files);

    /// <summary>The folder, below the repository, that a target's skills go to.</summary>
    public static string DirectoryFor(SkillTarget target, string repository) => target switch
    {
        SkillTarget.Claude => Path.Combine(repository, ".claude", "skills"),
        SkillTarget.Copilot => Path.Combine(repository, ".github", "skills", "d365fo-cli"),
        _ => throw new ArgumentOutOfRangeException(nameof(target)),
    };

    /// <summary>The embedded files of a target, by path relative to its folder, with <c>/</c>.</summary>
    public static IReadOnlyDictionary<string, byte[]> Bundle(SkillTarget target)
    {
        var assembly = typeof(SkillInstaller).Assembly;
        var prefix = ResourceRoot + (target == SkillTarget.Claude ? "claude/" : "copilot/");
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);

        foreach (var name in assembly.GetManifestResourceNames())
        {
            // %(RecursiveDir) carries the separator of the machine that built the assembly.
            var normalized = name.Replace('\\', '/');
            if (!normalized.StartsWith(prefix, StringComparison.Ordinal)) continue;

            using var stream = assembly.GetManifestResourceStream(name)!;
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            files[normalized[prefix.Length..]] = buffer.ToArray();
        }

        return files;
    }

    /// <summary>Install a target's skills into <paramref name="repository"/>.</summary>
    /// <param name="target">Which agent's layout to write.</param>
    /// <param name="repository">An existing folder: the root of the X++ repository.</param>
    /// <param name="dryRun">Report what would happen and change nothing.</param>
    /// <param name="force">Overwrite files that this tool did not write.</param>
    /// <exception cref="SkillConflictException">Such files exist and <paramref name="force"/> is false.</exception>
    public static SkillInstallResult Install(SkillTarget target, string repository, bool dryRun = false, bool force = false)
    {
        var root = Path.GetFullPath(DirectoryFor(target, repository));
        var bundle = Bundle(target);
        if (bundle.Count == 0)
            throw new InvalidOperationException($"This build carries no {target} skills.");

        var recorded = ReadManifest(root);
        var written = new List<string>();
        var unchanged = new List<string>();
        var conflicts = new List<string>();

        foreach (var (relative, content) in bundle)
        {
            var path = Resolve(root, relative)!;
            if (!File.Exists(path))
            {
                written.Add(relative);
                continue;
            }

            var existing = File.ReadAllBytes(path);
            if (SameText(existing, content))
            {
                unchanged.Add(relative);
                continue;
            }

            // Without a record, a file that names d365fo is an earlier install (the PowerShell
            // scripts kept none). One that does not is somebody's own skill with the same name.
            if (!recorded.Contains(relative) && !MentionsTool(existing)) conflicts.Add(path);
            written.Add(relative);
        }

        if (conflicts.Count > 0 && !force) throw new SkillConflictException(conflicts);

        var removed = recorded
            .Where(relative => !bundle.ContainsKey(relative)
                               && Resolve(root, relative) is { } path && File.Exists(path))
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToList();

        var leftInPlace = Unrecorded(target, root, bundle, recorded);

        if (!dryRun)
        {
            foreach (var relative in written)
            {
                var path = Resolve(root, relative)!;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, bundle[relative]);
            }

            foreach (var relative in removed)
            {
                var path = Resolve(root, relative)!;
                File.Delete(path);
                DeleteEmptyParents(root, Path.GetDirectoryName(path)!);
            }

            Directory.CreateDirectory(root);
            var manifest = new Manifest(ProductVersion.Informational, bundle.Keys.ToArray());
            File.WriteAllText(Path.Combine(root, ManifestName),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        }

        return new SkillInstallResult(
            target.ToString().ToLowerInvariant(), root, written, unchanged, removed, leftInPlace);
    }

    private static HashSet<string> ReadManifest(string root)
    {
        var path = Path.Combine(root, ManifestName);
        if (!File.Exists(path)) return new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path));
            return new HashSet<string>(manifest?.Files ?? [], StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            // An unreadable record removes nothing and protects nothing it cannot name.
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    // The record is a file in the user's repository: an entry that leaves the folder is ignored.
    private static string? Resolve(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) return null;
        var full = Path.GetFullPath(Path.Combine(root, relative));
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? full : null;
    }

    // Skills left by an install that recorded nothing, for a topic this version no longer has.
    private static List<string> Unrecorded(
        SkillTarget target, string root, IReadOnlyDictionary<string, byte[]> bundle, HashSet<string> recorded)
    {
        if (!Directory.Exists(root)) return [];

        var candidates = target == SkillTarget.Claude
            ? Directory.EnumerateDirectories(root).Select(d => Path.Combine(d, "SKILL.md")).Where(File.Exists)
            : Directory.Exists(Path.Combine(root, "references"))
                ? Directory.EnumerateFiles(Path.Combine(root, "references"), "*.md")
                : [];

        return candidates
            .Select(path => (path, relative: Path.GetRelativePath(root, path).Replace('\\', '/')))
            .Where(f => !bundle.ContainsKey(f.relative) && !recorded.Contains(f.relative)
                        && MentionsTool(File.ReadAllBytes(f.path)))
            .Select(f => f.relative)
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToList();
    }

    private static void DeleteEmptyParents(string root, string directory)
    {
        while (directory.Length > root.Length && !Directory.EnumerateFileSystemEntries(directory).Any())
        {
            Directory.Delete(directory);
            directory = Path.GetDirectoryName(directory)!;
        }
    }

    // Line endings differ between the machine that built the CLI and a git checkout of the target.
    private static bool SameText(byte[] first, byte[] second) =>
        string.Equals(Normalize(first), Normalize(second), StringComparison.Ordinal);

    private static string Normalize(byte[] bytes) =>
        Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n", StringComparison.Ordinal);

    private static bool MentionsTool(byte[] content) =>
        Encoding.UTF8.GetString(content).Contains("d365fo", StringComparison.OrdinalIgnoreCase);
}
