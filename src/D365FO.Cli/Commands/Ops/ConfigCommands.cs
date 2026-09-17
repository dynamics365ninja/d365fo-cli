using System.ComponentModel;
using System.Text.RegularExpressions;
using D365FO.Core;
using Spectre.Console;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Ops;

/// <summary>
/// Shared plumbing for the <c>d365fo config</c> branch (named configuration
/// profiles, issue #210). The commands only read and select profiles; the
/// resolution rules themselves live in <see cref="D365FoSettings"/> and
/// <see cref="D365FoProfiles"/> so the CLI and the MCP server agree.
/// </summary>
internal static class ConfigCommandSupport
{
    /// <summary>
    /// Settings <c>config show</c> always lists, set or not, so "unset" is visible
    /// instead of silently missing. Mirrors the table in docs/CONFIGURATION.md;
    /// any extra key found in a settings/profile file is listed as well.
    /// </summary>
    internal static readonly string[] KnownKeys =
    {
        "D365FO_PACKAGES_PATH",
        "D365FO_CUSTOM_PACKAGES_PATH",
        "D365FO_EXTRA_PACKAGES_PATH",
        "D365FO_LABEL_LANGUAGES",
        "D365FO_INDEX_DB",
        "D365FO_WORKSPACE_PATH",
        "D365FO_CUSTOM_MODELS",
        "D365FO_BRIDGE_ENABLED",
        "D365FO_BRIDGE_PATH",
        "D365FO_BIN_PATH",
        "D365FO_MSBUILD_PATH",
        "D365FO_XREF_CONNECTIONSTRING",
        "D365FO_FORCE_JSON",
        "D365FO_HOME",
        "D365FO_GROUNDING_ENFORCE",
        "D365FO_FORM_PATTERN_ENFORCE",
        "MCP_SERVER_MODE",
        "API_KEY",
        "MCP_HTTP_PORT",
    };

    // Values printed to a terminal or an agent transcript: never echo secrets.
    private static readonly Regex SecretKey = new("(KEY|SECRET|PASSWORD|TOKEN|CONNECTIONSTRING)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static string? Mask(string key, string? value)
        => value is not null && SecretKey.IsMatch(key) ? "***" : value;

    internal static object? Describe(ActiveProfile? p) => p is null
        ? null
        : new
        {
            name = p.Name,
            source = p.Source.ToString().ToLowerInvariant(),
            sourceDetail = p.SourceLabel,
            path = p.FilePath,
            exists = p.Exists,
            validName = p.IsValidName,
        };

    internal static string PowerShellHint(string name) => $"$env:{D365FoProfiles.ProfileKey}='{name}'";
    internal static string PosixHint(string name) => $"export {D365FoProfiles.ProfileKey}={name}";

    internal static int Fail(OutputMode.Kind kind, ToolError error)
        => RenderHelpers.Render(kind, ToolResult<object>.Fail(error.Code, error.Message, error.Hint));

    internal static ToolError? ValidateExisting(string name)
    {
        if (!D365FoProfiles.IsValidName(name))
            return new ToolError(D365FoErrorCodes.InvalidProfileName,
                $"Invalid profile name '{name}'.",
                "Profile names must match ^[A-Za-z0-9][A-Za-z0-9._-]*$ (max 64 chars).");
        if (!D365FoProfiles.Exists(name))
            return new ToolError(D365FoErrorCodes.ProfileNotFound,
                $"Profile '{name}' does not exist: {D365FoProfiles.GetProfilePath(name)}",
                D365FoProfiles.ExistingProfilesHint(
                    $"Create it with 'd365fo init --profile {name}' (or 'd365fo config use {name} --create' for an empty one)."));
        return null;
    }

    internal static void RenderWarnings(IReadOnlyList<string>? warnings)
    {
        if (warnings is null) return;
        foreach (var w in warnings)
            AnsiConsole.MarkupLine($"[yellow]![/] {RenderHelpers.Escape(w)}");
    }
}

public sealed class ConfigListCommand : Command<ConfigListCommand.Settings>
{
    public sealed class Settings : D365OutputSettings { }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var active = D365FoProfiles.GetActive();
        var profiles = D365FoProfiles.List()
            .Select(n => new
            {
                name = n,
                active = active is not null && string.Equals(active.Name, n, StringComparison.OrdinalIgnoreCase),
                path = D365FoProfiles.GetProfilePath(n),
                packagesPath = D365FoProfiles.Load(n).TryGetValue("D365FO_PACKAGES_PATH", out var p) ? p : null,
            })
            .ToArray();

        var warnings = new List<string>();
        if (D365FoProfiles.CheckActive() is { } err) warnings.Add(err.Message);

        var payload = ToolResult<object>.Success(new
        {
            active = ConfigCommandSupport.Describe(active),
            profilesDirectory = D365FoProfiles.GetProfilesDirectory(),
            globalSettingsPath = D365FoSettings.GetDefaultConfigPath(),
            profiles,
        }, warnings.Count > 0 ? warnings : null);

        return RenderHelpers.Render(kind, payload, _ =>
        {
            AnsiConsole.MarkupLine(active is null
                ? "Active profile: [grey]none[/] (global settings.json applies)"
                : $"Active profile: [green]{RenderHelpers.Escape(active.Name)}[/] [grey](from {RenderHelpers.Escape(active.SourceLabel)})[/]");
            if (profiles.Length == 0)
            {
                AnsiConsole.MarkupLine($"[grey]No profiles in {RenderHelpers.Escape(D365FoProfiles.GetProfilesDirectory())}. Create one with 'd365fo init --profile <name>'.[/]");
            }
            else
            {
                var table = new Table().AddColumn("").AddColumn("Profile").AddColumn("Packages path").AddColumn("File");
                foreach (var p in profiles)
                    table.AddRow(p.active ? "[green]*[/]" : "", RenderHelpers.Escape(p.name),
                        RenderHelpers.Escape(p.packagesPath ?? "-"), RenderHelpers.Escape(p.path));
                AnsiConsole.Write(table);
            }
            ConfigCommandSupport.RenderWarnings(payload.Warnings);
        });
    }
}

public sealed class ConfigCurrentCommand : Command<ConfigCurrentCommand.Settings>
{
    public sealed class Settings : D365OutputSettings { }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var active = D365FoProfiles.GetActive();
        var db = D365FoSettings.ResolveDatabasePath();
        var warnings = D365FoProfiles.CheckActive() is { } err ? new[] { err.Message } : null;

        var payload = ToolResult<object>.Success(new
        {
            profile = ConfigCommandSupport.Describe(active),
            globalSettingsPath = D365FoSettings.GetDefaultConfigPath(),
            databasePath = db.Path,
        }, warnings);

        return RenderHelpers.Render(kind, payload, _ =>
        {
            if (active is null)
                AnsiConsole.MarkupLine("[grey]No profile selected[/] — using the global settings.json.");
            else
                AnsiConsole.MarkupLine($"[green]{RenderHelpers.Escape(active.Name)}[/] [grey](from {RenderHelpers.Escape(active.SourceLabel)}; {RenderHelpers.Escape(active.FilePath ?? "invalid name")})[/]");
            AnsiConsole.MarkupLine($"[grey]Index DB:[/] {RenderHelpers.Escape(db.Path)}");
            ConfigCommandSupport.RenderWarnings(payload.Warnings);
        });
    }
}

public sealed class ConfigShowCommand : Command<ConfigShowCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "[NAME]")]
        [Description("Profile to resolve as if it were selected. Omit to show the current resolution.")]
        public string? Name { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);

        // Resolving "as if selected" goes through the real chain by pinning the
        // flag-level selection for the duration of this command — a single-shot
        // CLI process, so nothing else observes the temporary value.
        var previousFlag = D365FoProfiles.FlagProfile;
        try
        {
            if (!string.IsNullOrWhiteSpace(settings.Name))
            {
                if (ConfigCommandSupport.ValidateExisting(settings.Name) is { } err)
                    return ConfigCommandSupport.Fail(kind, err);
                D365FoProfiles.FlagProfile = settings.Name;
            }
            else if (D365FoProfiles.CheckActive() is { } activeErr)
            {
                return ConfigCommandSupport.Fail(kind, activeErr);
            }

            var active = D365FoProfiles.GetActive();
            var keys = ConfigCommandSupport.KnownKeys
                .Concat(D365FoSettings.ReadGlobalJson().Keys)
                .Concat(active is { Exists: true } ? D365FoProfiles.Load(active.Name).Keys : [])
                .Where(k => !string.Equals(k, D365FoProfiles.ProfileKey, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var values = keys.Select(k =>
            {
                // The index DB has its own rule (per-profile default), so report
                // what commands will actually open rather than the raw key.
                var (value, source) = string.Equals(k, "D365FO_INDEX_DB", StringComparison.OrdinalIgnoreCase)
                    ? ((string?)D365FoSettings.ResolveDatabasePath().Path, (string?)D365FoSettings.ResolveDatabasePath().Source)
                    : D365FoSettings.ResolveWithSource(k);
                return new { key = k, value = ConfigCommandSupport.Mask(k, value), source };
            }).ToArray();

            var payload = ToolResult<object>.Success(new
            {
                profile = ConfigCommandSupport.Describe(active),
                globalSettingsPath = D365FoSettings.GetDefaultConfigPath(),
                precedence = new[] { "flag", "env", "profile:<name>", "settings", "default" },
                settings = values,
            });

            return RenderHelpers.Render(kind, payload, _ =>
            {
                AnsiConsole.MarkupLine(active is null
                    ? "Profile: [grey]none[/]"
                    : $"Profile: [green]{RenderHelpers.Escape(active.Name)}[/] [grey](from {RenderHelpers.Escape(active.SourceLabel)})[/]");
                var table = new Table().AddColumn("Key").AddColumn("Value").AddColumn("Source");
                foreach (var v in values)
                    table.AddRow(RenderHelpers.Escape(v.key),
                        v.value is null ? "[grey](unset)[/]" : RenderHelpers.Escape(v.value),
                        RenderHelpers.Escape(v.source ?? "-"));
                AnsiConsole.Write(table);
            });
        }
        finally
        {
            D365FoProfiles.FlagProfile = previousFlag;
        }
    }
}

public sealed class ConfigUseCommand : Command<ConfigUseCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "[NAME]")]
        [Description("Profile to make the persisted default (written as D365FO_PROFILE to the global settings.json).")]
        public string? Name { get; init; }

        [CommandOption("--clear")]
        [Description("Unset the persisted default profile; commands fall back to the global settings.json.")]
        public bool Clear { get; init; }

        [CommandOption("--create")]
        [Description("Create an empty profile file when NAME does not exist yet (edit it or run 'd365fo init --profile NAME' to fill it).")]
        public bool Create { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var settingsPath = D365FoSettings.GetDefaultConfigPath();

        // Checked here rather than in Settings.Validate: a Spectre validation
        // failure surfaces as an UNHANDLED runtime exception, not a BAD_INPUT result.
        if (settings.Clear == !string.IsNullOrWhiteSpace(settings.Name))
            return ConfigCommandSupport.Fail(kind, new ToolError(D365FoErrorCodes.BadInput,
                settings.Clear ? "Pass either a profile NAME or --clear, not both." : "A profile NAME is required (or --clear).",
                "Usage: d365fo config use <NAME> [--create] | d365fo config use --clear"));

        if (settings.Clear)
        {
            D365FoSettings.SaveJsonConfig(new Dictionary<string, string>(), remove: [D365FoProfiles.ProfileKey]);
            var after = D365FoProfiles.GetActive();
            var clearWarnings = after is null ? null : new[]
            {
                $"Profile '{after.Name}' is still selected by the {after.SourceLabel} for this shell/session.",
            };
            var cleared = ToolResult<object>.Success(new
            {
                cleared = true,
                persistedTo = settingsPath,
                active = ConfigCommandSupport.Describe(after),
                shellHint = $"Remove-Item Env:{D365FoProfiles.ProfileKey} -ErrorAction SilentlyContinue",
            }, clearWarnings);
            return RenderHelpers.Render(kind, cleared, _ =>
            {
                AnsiConsole.MarkupLine($"[green]Cleared[/] the persisted profile in {RenderHelpers.Escape(settingsPath)}.");
                ConfigCommandSupport.RenderWarnings(cleared.Warnings);
            });
        }

        var name = settings.Name!.Trim();
        var created = false;
        if (settings.Create && D365FoProfiles.IsValidName(name) && !D365FoProfiles.Exists(name))
        {
            D365FoProfiles.Save(name, new Dictionary<string, string>());
            created = true;
        }
        if (ConfigCommandSupport.ValidateExisting(name) is { } err)
            return ConfigCommandSupport.Fail(kind, err);

        D365FoSettings.SaveJsonConfig(new Dictionary<string, string> { [D365FoProfiles.ProfileKey] = name });

        // The persisted value is the lowest-precedence selector; say so when a
        // flag or env var in this session still points somewhere else.
        var active = D365FoProfiles.GetActive();
        var warnings = active is not null && !string.Equals(active.Name, name, StringComparison.OrdinalIgnoreCase)
            ? new[] { $"Profile '{active.Name}' from the {active.SourceLabel} overrides the persisted default in this session." }
            : null;

        var payload = ToolResult<object>.Success(new
        {
            profile = name,
            created,
            path = D365FoProfiles.GetProfilePath(name),
            persistedTo = settingsPath,
            active = ConfigCommandSupport.Describe(active),
            shellHint = ConfigCommandSupport.PowerShellHint(name),
            posixShellHint = ConfigCommandSupport.PosixHint(name),
        }, warnings);

        return RenderHelpers.Render(kind, payload, _ =>
        {
            AnsiConsole.MarkupLine($"[green]Default profile set to '{RenderHelpers.Escape(name)}'[/] [grey](saved in {RenderHelpers.Escape(settingsPath)})[/]");
            if (created)
                AnsiConsole.MarkupLine($"[grey]Created empty profile {RenderHelpers.Escape(D365FoProfiles.GetProfilePath(name))}.[/]");
            AnsiConsole.MarkupLine($"[grey]To select it for the current PowerShell session only instead:[/] {RenderHelpers.Escape(ConfigCommandSupport.PowerShellHint(name))}");
            ConfigCommandSupport.RenderWarnings(payload.Warnings);
        });
    }
}
