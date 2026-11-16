using D365FO.Cli;
using D365FO.Core;
using D365FO.Core.Bridge;
using D365FO.Core.Index;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Modify;

/// <summary>
/// Replace the body (or, with <c>--source</c>, the whole source) of an existing method on a live class/table/EDT/form via
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
        [System.ComponentModel.Description("New X++ method body (statements only — no signature/braces). Replaces what sits between the method's outer braces and keeps its doc comment, attributes and signature. A full declaration is refused. Exactly one of --body / --source.")]
        public string? Body { get; init; }

        [CommandOption("--source <XPP>")]
        [System.ComponentModel.Description("The whole method instead: doc comment, attributes, signature and body. Replaces the method's source as given. It must declare exactly one method, named <METHOD>. Exactly one of --body / --source.")]
        public string? Source { get; init; }

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

        var hasBody = !string.IsNullOrWhiteSpace(settings.Body);
        var hasSource = !string.IsNullOrWhiteSpace(settings.Source);
        if (hasBody == hasSource)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                hasBody ? "Pass --body or --source, not both." : "--body <XPP> or --source <XPP> is required.",
                "--body takes the statements between the braces and keeps the signature. --source replaces the whole method."));
        }

        MetadataRepository? repo = null;
        try { repo = RepoFactory.Create(); } catch { /* degrade to bridge-only; engine surfaces a warning */ }

        var request = new MethodModifyEngine.ModifyRequest(
            settings.Kind,
            settings.ObjectName,
            settings.Method,
            settings.Body,
            settings.Model,
            settings.GroundingToken,
            settings.Source);

        var result = MethodModifyEngine.Modify(request, repo);
        return RenderHelpers.Render(kind, result);
    }
}

/// <summary>
/// Add a new method to an existing class, table or form via <c>D365FO.Bridge</c> (issue #256).
/// The source is the whole method; <c>static</c> in its signature makes it a class method.
/// </summary>
public sealed class ModifyAddMethodCommand : Command<ModifyAddMethodCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<KIND>")]
        [System.ComponentModel.Description("Object kind: class, table, or form.")]
        public string Kind { get; init; } = "";

        [CommandArgument(1, "<OBJECT>")]
        [System.ComponentModel.Description("Object name (resolved via the index; --model overrides). It must be in a configured custom model.")]
        public string ObjectName { get; init; } = "";

        [CommandArgument(2, "<METHOD>")]
        [System.ComponentModel.Description("Name of the new method. The source must declare a method of this name, and the object must not have one yet.")]
        public string Method { get; init; } = "";

        [CommandOption("--source <XPP>")]
        [System.ComponentModel.Description("The whole new method: optional doc comment and attributes, signature, body. Add `static` to the signature for a class (static) method. Exactly one of --source / --source-file.")]
        public string? Source { get; init; }

        [CommandOption("--source-file <PATH>")]
        [System.ComponentModel.Description("File holding the same X++ method source. Avoids quoting a multi-line method on the command line.")]
        public string? SourceFile { get; init; }

        [CommandOption("--model <MODEL>")]
        [System.ComponentModel.Description("Owning model. Resolved via the index when omitted.")]
        public string? Model { get; init; }

        [CommandOption("--grounding-token <TOKEN>")]
        [System.ComponentModel.Description("Grounding token from `d365fo prepare change`. Required for the token check when D365FO_GROUNDING_ENFORCE=true; the reference/BP validation gate always blocks on error-severity findings.")]
        public string? GroundingToken { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);

        var hasSource = !string.IsNullOrWhiteSpace(settings.Source);
        var hasFile = !string.IsNullOrWhiteSpace(settings.SourceFile);
        if (hasSource == hasFile)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                hasSource ? "Pass --source or --source-file, not both." : "--source <XPP> or --source-file <PATH> is required.",
                "The source is the whole new method: signature, braces and body."));
        }

        var source = settings.Source;
        if (hasFile)
        {
            if (!File.Exists(settings.SourceFile))
            {
                return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                    $"No such file: {settings.SourceFile}"));
            }
            source = File.ReadAllText(settings.SourceFile!);
        }

        MetadataRepository? repo = null;
        try { repo = RepoFactory.Create(); } catch { /* degrade to bridge-only; engine surfaces a warning */ }

        var result = MethodModifyEngine.Add(
            new MethodModifyEngine.AddRequest(settings.Kind, settings.ObjectName, settings.Method, source!,
                settings.Model, settings.GroundingToken),
            repo);
        return RenderHelpers.Render(kind, result);
    }
}
