// <copyright file="ProductVersion.cs" company="d365fo-cli contributors">
// MIT
// </copyright>

using System.Reflection;

namespace D365FO.Core;

/// <summary>
/// The identity of the running build, as every surface reports it: <c>d365fo --version</c>,
/// <c>d365fo version</c>, the MCP <c>serverInfo</c> and SARIF output.
/// </summary>
/// <remarks>
/// The number comes from <c>VersionPrefix</c>/<c>VersionSuffix</c> in
/// <c>Directory.Build.props</c>, which stamps every project including the bridge. The SDK
/// appends <c>+&lt;commit&gt;</c> when it builds from a git checkout; a build from a source
/// archive has no commit to report. See <c>docs/RELEASING.md</c>.
/// </remarks>
public static class ProductVersion
{
    /// <summary>Version and, when known, source commit: <c>0.2.0+5ef6a7d…</c>.</summary>
    public static string Informational { get; } =
        typeof(ProductVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? "0.0.0-unknown";

    /// <summary>The semantic version alone: <c>0.2.0</c> or <c>0.3.0-dev</c>.</summary>
    public static string Version { get; } = Split(Informational).Version;

    /// <summary>The source commit this build came from, or null when it was not recorded.</summary>
    public static string? Commit { get; } = Split(Informational).Commit;

    /// <summary>Separate an informational version into its version and commit.</summary>
    public static (string Version, string? Commit) Split(string informational)
    {
        var plus = informational.IndexOf('+');
        return plus < 0
            ? (informational, null)
            : (informational[..plus], informational[(plus + 1)..]);
    }

    /// <summary>
    /// True when two informational versions name the same build: equal versions, and equal
    /// commits where both recorded one.
    /// </summary>
    public static bool SameBuild(string first, string second)
    {
        var a = Split(first);
        var b = Split(second);
        return string.Equals(a.Version, b.Version, StringComparison.OrdinalIgnoreCase)
               && (a.Commit is null || b.Commit is null
                   || string.Equals(a.Commit, b.Commit, StringComparison.OrdinalIgnoreCase));
    }
}
