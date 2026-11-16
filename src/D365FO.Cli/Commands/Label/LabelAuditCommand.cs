using D365FO.Core;
using D365FO.Core.Index;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Label;

/// <summary>
/// <c>d365fo labels audit --model M</c> (issue #272): labels the model uses that no label file
/// defines, keys missing in some of their file's languages, and keys nothing uses.
/// </summary>
public sealed class LabelAuditCommand : Command<LabelAuditCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandOption("-m|--model <MODEL>")]
        [System.ComponentModel.Description("Model to audit. Its folder is found under the configured packages roots.")]
        public string? Model { get; init; }

        [CommandOption("--path <DIR>")]
        [System.ComponentModel.Description("The model folder itself (<package>\\<model>), instead of looking it up.")]
        public string? Path { get; init; }

        [CommandOption("--check <CSV>")]
        [System.ComponentModel.Description("Which checks: undefined, untranslated, unused (comma-separated). All when omitted.")]
        public string? Check { get; init; }

        [CommandOption("--all-models")]
        [System.ComponentModel.Description("For the unused check, count a use anywhere in the index, not only in the model.")]
        public bool AllModels { get; init; }

        [CommandOption("-l|--limit <N>")]
        [System.ComponentModel.Description("Findings listed per check (default 50). The counts are always complete.")]
        public int Limit { get; init; } = 50;
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        MetadataRepository? repo = null;
        try { repo = RepoFactory.Create(); } catch { /* the audit says what it could not check */ }
        return RenderHelpers.Render(kind,
            LabelAudit.Run(repo, settings.Model, settings.Path, settings.Check, settings.AllModels, settings.Limit));
    }
}
