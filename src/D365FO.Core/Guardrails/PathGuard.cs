// <copyright file="PathGuard.cs" company="d365fo-cli contributors">
// MIT
// </copyright>

namespace D365FO.Core.Guardrails;

/// <summary>
/// Validates that file write paths stay within an allowed boundary
/// (PackagesLocalDirectory or workspace root). Prevents directory traversal
/// attacks where a malicious or malformed path argument could write to
/// arbitrary locations on disk.
/// </summary>
public static class PathGuard
{
    /// <summary>
    /// Ensures <paramref name="targetPath"/> is contained within at least one
    /// of the allowed root directories. Throws <see cref="IOException"/> on
    /// violation.
    /// </summary>
    /// <param name="targetPath">Absolute path to validate.</param>
    /// <param name="allowedRoots">
    /// Allowed root directories. Null/empty entries are ignored.
    /// When no non-null roots are provided, the current working directory is
    /// used as the boundary.
    /// </param>
    public static void EnsureWithinBoundary(string targetPath, params string?[] allowedRoots)
    {
        var full = Path.GetFullPath(targetPath);

        // Collect non-null, non-empty roots; fall back to cwd.
        var roots = allowedRoots
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => NormalizeDirPath(Path.GetFullPath(r!)))
            .ToList();

        if (roots.Count == 0)
        {
            roots.Add(NormalizeDirPath(Environment.CurrentDirectory));
        }

        // The system temp directory is always allowed — tests and staging
        // workflows legitimately write to temp before final placement.
        roots.Add(NormalizeDirPath(Path.GetTempPath()));

        foreach (var root in roots)
        {
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return; // Path is within this allowed root.
            }
        }

        throw new IOException(
            $"Path traversal blocked: '{full}' is outside the allowed boundaries " +
            $"[{string.Join(", ", roots.Select(r => r.TrimEnd(Path.DirectorySeparatorChar)))}]. " +
            "A model store outside PackagesLocalDirectory (a unified developer environment, a repo " +
            "checkout) belongs in D365FO_CUSTOM_PACKAGES_PATH, or set D365FO_WORKSPACE_PATH to it.");
    }

    private static string NormalizeDirPath(string dir)
    {
        // Ensure trailing separator so "/foo" doesn't match "/foobar".
        if (!dir.EndsWith(Path.DirectorySeparatorChar))
            return dir + Path.DirectorySeparatorChar;
        return dir;
    }
}
