using System.Text.RegularExpressions;

namespace D365FO.Core;

/// <summary>Where the active profile name came from (highest precedence first).</summary>
public enum ProfileSource
{
    /// <summary>No profile selected — the single global settings.json applies (pre-#210 behavior).</summary>
    None,
    /// <summary>The global <c>--profile &lt;name&gt;</c> CLI option.</summary>
    Flag,
    /// <summary>The <c>D365FO_PROFILE</c> process environment variable.</summary>
    Environment,
    /// <summary>The <c>D365FO_PROFILE</c> key in the global settings.json (written by <c>d365fo config use</c>).</summary>
    Settings,
}

/// <summary>
/// The profile selected for this process. <see cref="FilePath"/> is null when
/// <see cref="Name"/> is not a valid profile name — an invalid name is never
/// turned into a path, so a hostile <c>D365FO_PROFILE=..\..\x</c> cannot point
/// the resolver outside the profiles directory.
/// </summary>
public sealed record ActiveProfile(string Name, ProfileSource Source, bool IsValidName, string? FilePath, bool Exists)
{
    public string SourceLabel => D365FoProfiles.DescribeSource(Source);
}

/// <summary>
/// Named configuration profiles (issue #210). A profile is a file
/// <c>&lt;config-root&gt;\profiles\&lt;name&gt;.json</c> with the same flat
/// key/value schema as settings.json. When one is active it sits between the
/// process environment and the global settings.json in the resolution chain
/// (see <see cref="D365FoSettings.Resolve"/>), so a developer juggling several
/// customers' UDEs can switch the whole environment with one name instead of
/// rewriting settings.json or exporting a handful of env vars.
/// </summary>
public static class D365FoProfiles
{
    /// <summary>Env var / settings.json key that selects the active profile.</summary>
    public const string ProfileKey = "D365FO_PROFILE";

    public const string ProfilesDirectoryName = "profiles";

    // Leading alphanumeric, then a conservative set — no separators, no "..",
    // so a name always maps to a file directly inside the profiles directory.
    private static readonly Regex NameRegex = new("^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Profile name passed via the global <c>--profile</c> option. Kept apart from
    /// the <c>D365FO_PROFILE</c> env var (which the CLI entry point also sets, so
    /// child processes such as the daemon inherit the selection) purely so
    /// <c>config list</c>/<c>doctor</c> can report the right source.
    /// </summary>
    public static string? FlagProfile { get; set; }

    public static bool IsValidName(string? name)
        => !string.IsNullOrWhiteSpace(name) && name.Length <= 64 && NameRegex.IsMatch(name);

    public static string GetProfilesDirectory()
        => Path.Combine(D365FoSettings.GetConfigRoot(), ProfilesDirectoryName);

    /// <summary>Path of the profile file. Throws for an invalid name — callers validate first.</summary>
    public static string GetProfilePath(string name)
    {
        if (!IsValidName(name))
            throw new ArgumentException($"Invalid profile name '{name}'.", nameof(name));
        return Path.Combine(GetProfilesDirectory(), name + ".json");
    }

    /// <summary>
    /// Per-profile data directory (holds the profile's default index DB) — a sibling
    /// folder of the profile file, so deleting a profile's folder never touches another's index.
    /// </summary>
    public static string GetProfileDataDirectory(string name)
    {
        if (!IsValidName(name))
            throw new ArgumentException($"Invalid profile name '{name}'.", nameof(name));
        return Path.Combine(GetProfilesDirectory(), name);
    }

    public static bool Exists(string name) => IsValidName(name) && File.Exists(GetProfilePath(name));

    /// <summary>Names of all profile files, sorted case-insensitively.</summary>
    public static IReadOnlyList<string> List()
    {
        var dir = GetProfilesDirectory();
        if (!Directory.Exists(dir)) return Array.Empty<string>();
        return Directory.EnumerateFiles(dir, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => IsValidName(n))
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Determine the active profile: <c>--profile</c> flag → <c>D365FO_PROFILE</c>
    /// env var → <c>D365FO_PROFILE</c> in the global settings.json. Returns null when
    /// none is selected, which keeps the pre-profile behavior exactly.
    /// </summary>
    public static ActiveProfile? GetActive()
    {
        string? name;
        ProfileSource source;
        if (!string.IsNullOrWhiteSpace(FlagProfile))
        {
            name = FlagProfile;
            source = ProfileSource.Flag;
        }
        else if (!string.IsNullOrWhiteSpace(name = Environment.GetEnvironmentVariable(ProfileKey)))
        {
            source = ProfileSource.Environment;
        }
        else if (!string.IsNullOrWhiteSpace(name = D365FoSettings.ReadGlobalJsonValue(ProfileKey)))
        {
            source = ProfileSource.Settings;
        }
        else
        {
            return null;
        }

        name = name!.Trim();
        var valid = IsValidName(name);
        var path = valid ? GetProfilePath(name) : null;
        return new ActiveProfile(name, source, valid, path, path is not null && File.Exists(path));
    }

    public static string DescribeSource(ProfileSource source) => source switch
    {
        ProfileSource.Flag => "--profile flag",
        ProfileSource.Environment => $"{ProfileKey} environment variable",
        ProfileSource.Settings => $"{ProfileKey} in {D365FoSettings.GetDefaultConfigPath()}",
        _ => "none",
    };

    /// <summary>
    /// Guardrail: a selected-but-missing (or malformed) profile is a hard error
    /// rather than a silent fallback to the global settings — silently running
    /// against the wrong customer's packages is exactly what profiles exist to
    /// prevent. Returns null when no profile is selected or it exists.
    /// </summary>
    public static ToolError? CheckActive()
    {
        var active = GetActive();
        if (active is null) return null;
        if (!active.IsValidName)
            return new ToolError(D365FoErrorCodes.InvalidProfileName,
                $"Profile name '{active.Name}' (from {active.SourceLabel}) is invalid.",
                "Profile names must match ^[A-Za-z0-9][A-Za-z0-9._-]*$ (max 64 chars).");
        if (!active.Exists)
            return new ToolError(D365FoErrorCodes.ProfileNotFound,
                $"Profile '{active.Name}' (from {active.SourceLabel}) does not exist: {active.FilePath}",
                ExistingProfilesHint($"Create it with 'd365fo init --profile {active.Name}', pick another with 'd365fo config use <name>', or clear the selection ('d365fo config use --clear' / unset {ProfileKey})."));
        return null;
    }

    public static string ExistingProfilesHint(string prefix)
    {
        var names = List();
        return names.Count == 0
            ? prefix + " No profiles exist yet."
            : prefix + " Existing profiles: " + string.Join(", ", names) + ".";
    }

    /// <summary>
    /// Remove the global <c>--profile &lt;name&gt;</c> / <c>--profile=&lt;name&gt;</c>
    /// option from <paramref name="args"/>. Spectre.Console.Cli has no true global
    /// options, so the entry point strips it before parsing. Scanning stops at a
    /// literal <c>--</c> so values after it are passed through untouched.
    /// Returns <c>Error</c> when the option is present without a value.
    /// </summary>
    public static (string[] Args, string? Profile, string? Error) StripProfileArg(IReadOnlyList<string> args)
    {
        var rest = new List<string>(args.Count);
        string? profile = null;
        string? error = null;
        var passthrough = false;
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (passthrough) { rest.Add(a); continue; }
            if (a == "--") { passthrough = true; rest.Add(a); continue; }

            if (string.Equals(a, "--profile", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Count && !args[i + 1].StartsWith('-'))
                    profile = args[++i];
                else
                    error = "Option '--profile' requires a profile name.";
                continue;
            }
            if (a.StartsWith("--profile=", StringComparison.OrdinalIgnoreCase))
            {
                profile = a["--profile=".Length..];
                if (string.IsNullOrWhiteSpace(profile))
                {
                    profile = null;
                    error = "Option '--profile' requires a profile name.";
                }
                continue;
            }
            rest.Add(a);
        }
        return (rest.ToArray(), profile, error);
    }

    /// <summary>Read a profile file's key/values (empty when missing or unreadable).</summary>
    public static IReadOnlyDictionary<string, string> Load(string name)
        => D365FoSettings.GetProfileJson(name);

    /// <summary>
    /// Merge <paramref name="values"/> into the profile file, creating it (and the
    /// profiles directory) when missing. Keys in <paramref name="remove"/> are dropped.
    /// </summary>
    public static string Save(string name, IReadOnlyDictionary<string, string> values, IEnumerable<string>? remove = null)
        => D365FoSettings.SaveProfileJson(name, values, remove);
}
