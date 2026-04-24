using D365FO.Core;
using D365FO.Core.Labels;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Label;

/// <summary>
/// <c>d365fo label create|update|rename|delete</c> — in-place edits of
/// <c>*.label.txt</c> resource files. ROADMAP §4.2.
/// </summary>
public sealed class LabelCreateCommand : Command<LabelCreateCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<KEY>")]
        public string Key { get; init; } = "";

        [CommandArgument(1, "<VALUE>")]
        public string Value { get; init; } = "";

        [CommandOption("--file <PATH>")]
        [System.ComponentModel.Description("Target <Name>.<lang>.label.txt file. Created if missing.")]
        public string? File { get; init; }

        [CommandOption("--overwrite")]
        [System.ComponentModel.Description("Replace an existing value. Default: fail with KEY_EXISTS.")]
        public bool Overwrite { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        if (string.IsNullOrWhiteSpace(settings.Key))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "Label key required."));
        if (string.IsNullOrWhiteSpace(settings.File))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "--file <PATH> required."));

        try
        {
            var res = LabelFileWriter.CreateOrUpdate(settings.File!, settings.Key, settings.Value, settings.Overwrite);
            if (res.Outcome == WriteOutcome.KeyExists)
                return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                    "KEY_EXISTS",
                    $"Label '{settings.Key}' already exists. Pass --overwrite to replace.",
                    hint: $"Existing value: {res.OldValue}"));

            return RenderHelpers.Render(kind, ToolResult<object>.Success(new
            {
                outcome = res.Outcome.ToString(),
                file = res.Path,
                key = res.Key,
                oldValue = res.OldValue,
                newValue = res.NewValue,
            }));
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed, ex.Message));
        }
    }
}

public sealed class LabelRenameCommand : Command<LabelRenameCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<OLD>")]
        public string OldKey { get; init; } = "";

        [CommandArgument(1, "<NEW>")]
        public string NewKey { get; init; } = "";

        [CommandOption("--file <PATH>")]
        public string? File { get; init; }

        [CommandOption("--overwrite")]
        public bool Overwrite { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        if (string.IsNullOrWhiteSpace(settings.OldKey) || string.IsNullOrWhiteSpace(settings.NewKey))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "Both <OLD> and <NEW> label keys required."));
        if (string.IsNullOrWhiteSpace(settings.File))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "--file <PATH> required."));

        try
        {
            var res = LabelFileWriter.Rename(settings.File!, settings.OldKey, settings.NewKey, settings.Overwrite);
            return res.Outcome switch
            {
                WriteOutcome.FileMissing => RenderHelpers.Render(kind, ToolResult<object>.Fail("FILE_NOT_FOUND", $"Label file not found: {settings.File}")),
                WriteOutcome.KeyMissing => RenderHelpers.Render(kind, ToolResult<object>.Fail("KEY_NOT_FOUND", $"Label '{settings.OldKey}' not present in file.")),
                WriteOutcome.KeyExists => RenderHelpers.Render(kind, ToolResult<object>.Fail("KEY_EXISTS", $"Target key '{settings.NewKey}' already exists. Pass --overwrite to replace.")),
                _ => RenderHelpers.Render(kind, ToolResult<object>.Success(new
                {
                    outcome = res.Outcome.ToString(),
                    file = res.Path,
                    oldKey = settings.OldKey,
                    newKey = settings.NewKey,
                    value = res.NewValue,
                })),
            };
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed, ex.Message));
        }
    }
}

public sealed class LabelDeleteCommand : Command<LabelDeleteCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<KEY>")]
        public string Key { get; init; } = "";

        [CommandOption("--file <PATH>")]
        public string? File { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        if (string.IsNullOrWhiteSpace(settings.Key))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "Label key required."));
        if (string.IsNullOrWhiteSpace(settings.File))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "--file <PATH> required."));

        try
        {
            var res = LabelFileWriter.Delete(settings.File!, settings.Key);
            return res.Outcome switch
            {
                WriteOutcome.FileMissing => RenderHelpers.Render(kind, ToolResult<object>.Fail("FILE_NOT_FOUND", $"Label file not found: {settings.File}")),
                WriteOutcome.KeyMissing => RenderHelpers.Render(kind, ToolResult<object>.Fail("KEY_NOT_FOUND", $"Label '{settings.Key}' not present in file.")),
                _ => RenderHelpers.Render(kind, ToolResult<object>.Success(new
                {
                    outcome = res.Outcome.ToString(),
                    file = res.Path,
                    key = res.Key,
                    removedValue = res.OldValue,
                })),
            };
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed, ex.Message));
        }
    }
}
