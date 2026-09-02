using D365FO.Core;
using D365FO.Core.FormPatterns;
using D365FO.Core.Scaffolding;
using Spectre.Console.Cli;

using static D365FO.Core.ObjectTypes.ObjectTypeRegistry;

namespace D365FO.Cli.Commands.Generate;

/// <summary>
/// Clone an existing form under a new name, optionally re-binding its datasources.
/// </summary>
/// <remarks>
/// Issue #164 / R5. A Microsoft form that already has the pattern, the control tree and the
/// wiring right is a better starting point than any template, and cloning one is what a developer
/// does by hand anyway. The edits are string-level and narrow — see <see cref="FormCloner"/> for
/// why a round-trip through <c>XDocument</c> would return a form that differs from the original
/// in ways nobody asked for.
/// </remarks>
public sealed class GenerateFormCloneCommand : Command<GenerateFormCloneCommand.Settings>
{
    public sealed class Settings : GenerateSettings
    {
        [CommandArgument(0, "<NAME>")]
        [System.ComponentModel.Description("Name for the clone.")]
        public string Name { get; init; } = "";

        [CommandOption("--from <FORM>")]
        [System.ComponentModel.Description("Reference form: a form name resolved through the index, or a path to its AxForm XML.")]
        public string? From { get; init; }

        [CommandOption("--rebind <SPEC>")]
        [System.ComponentModel.Description("Repeatable: <OldTable>=<NewTable>. Moves the datasource, its name when it matched the table, and every control that references it.")]
        public string[] Rebind { get; init; } = Array.Empty<string>();
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);

        if (string.IsNullOrWhiteSpace(settings.Name))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "Clone name required."));
        if (string.IsNullOrWhiteSpace(settings.From))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "--from <FORM> required."));

        var (sourceXml, readError) = AotSourceReader.ReadForm(TryRepo(), settings.From!);
        if (readError is not null)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.SourceUnreadable, readError));

        if (!TryParseRebinds(settings.Rebind, out var rebinds, out var rebindError))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, rebindError!));

        var hasInstall = !string.IsNullOrWhiteSpace(settings.InstallTo);
        var hasOut = !string.IsNullOrWhiteSpace(settings.Out);
        if (!hasInstall && !hasOut)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "--out or --install-to is required."));

        var outPath = settings.Out;
        if (hasInstall && !hasOut)
        {
            outPath = GenerateInstaller.ResolveInstallPath(kind, Folders.Form, settings.Name, settings.InstallTo!, out var fail);
            if (fail.HasValue) return fail.Value;
        }

        FormCloneResult clone;
        try { clone = FormCloner.Clone(sourceXml!, settings.Name, rebinds); }
        catch (FormCloneException ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("CLONE_FAILED", ex.Message));
        }

        // The clone claims the tables it was rebound onto exist — that is the one thing here the
        // index can prove, and the reason this goes through the gate like every other generate.
        var gate = GenerateInstaller.Gate(
            settings, settings.Name, doc: null,
            requiredSymbols: rebinds.Values);
        if (gate.Failure is not null) return RenderHelpers.Render(kind, gate.Failure);

        var warnings = gate.Warnings;
        warnings.AddRange(clone.Warnings);

        try
        {
            var res = GenerateInstaller.Write(gate, clone.Xml, outPath!, settings.Overwrite);
            return GenerateInstaller.Done(kind, gate, settings, new
            {
                kind = "AxForm",
                role = "Clone",
                name = settings.Name,
                from = settings.From,
                rebound = clone.Rebound,
                renamedDataSources = clone.RenamedDataSources,
                path = res.Path,
                bytes = res.Bytes,
                backup = res.BackupPath,
                model = settings.InstallTo,
                grounding = gate.Grounding,
            }, warnings);
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed, ex.Message));
        }
    }

    /// <summary>
    /// Read the reference form, from a path or by name through the index.
    /// </summary>
    /// <remarks>
    /// A path wins when it exists, so a developer can clone a form they have in front of them
    /// without an index. The name route reads <c>SourcePath</c> off the index and then reads the
    /// file — the index stores metadata, not the document.
    /// </remarks>

    /// <summary>The index, or null when there is none — a path-based clone does not need one.</summary>
    private static D365FO.Core.Index.MetadataRepository? TryRepo()
    {
        try { return RepoFactory.Create(); }
        catch { return null; }
    }

    private static bool TryParseRebinds(
        string[] raw, out Dictionary<string, string> rebinds, out string? error)
    {
        rebinds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        error = null;

        foreach (var spec in raw.Where(r => !string.IsNullOrWhiteSpace(r)))
        {
            var parts = spec.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
            {
                error = $"Invalid --rebind '{spec}'. Expected <OldTable>=<NewTable>.";
                return false;
            }
            rebinds[parts[0]] = parts[1];
        }

        return true;
    }
}
