using D365FO.Cli;
using D365FO.Core;
using D365FO.Core.Bridge;
using D365FO.Core.Index;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Modify;

/// <summary>
/// Replace the body of an existing method on a live class/table/EDT/form via
/// <c>D365FO.Bridge</c> — the structured (never CDATA-string-surgery) counterpart
/// of upstream's <c>d365fo_file(action=modify)</c> (issue #112). Unlike the
/// <c>generate</c> family this has no on-disk scaffold mode: it always round-trips
/// the live object through <c>IMetadataProvider</c>.
/// </summary>
public sealed class ModifyMethodCommand : Command<ModifyMethodCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<KIND>")]
        [System.ComponentModel.Description("Object kind: class, table, edt, or form.")]
        public string Kind { get; init; } = "";

        [CommandArgument(1, "<OBJECT>")]
        [System.ComponentModel.Description("Object name (resolved via the index; --model overrides).")]
        public string ObjectName { get; init; } = "";

        [CommandArgument(2, "<METHOD>")]
        [System.ComponentModel.Description("Existing method name whose body is being replaced.")]
        public string Method { get; init; } = "";

        [CommandOption("--body <XPP>")]
        [System.ComponentModel.Description("New X++ method body (statements only — no signature/braces). Required.")]
        public string? Body { get; init; }

        [CommandOption("--model <MODEL>")]
        [System.ComponentModel.Description("Owning model. Resolved via the index when omitted.")]
        public string? Model { get; init; }

        [CommandOption("--grounding-token <TOKEN>")]
        [System.ComponentModel.Description("Grounding token from `d365fo prepare change`/`prepare create`. Required for the token check when D365FO_GROUNDING_ENFORCE=true; the reference/BP validation gate itself always blocks on error-severity findings.")]
        public string? GroundingToken { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);

        if (string.IsNullOrWhiteSpace(settings.Body))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "--body <XPP> is required."));

        MetadataRepository? repo = null;
        try { repo = RepoFactory.Create(); } catch { /* degrade to bridge-only; engine surfaces a warning */ }

        var request = new MethodModifyEngine.ModifyRequest(
            settings.Kind,
            settings.ObjectName,
            settings.Method,
            settings.Body!,
            settings.Model,
            settings.GroundingToken);

        var result = MethodModifyEngine.Modify(request, repo);
        return RenderHelpers.Render(kind, result);
    }
}
