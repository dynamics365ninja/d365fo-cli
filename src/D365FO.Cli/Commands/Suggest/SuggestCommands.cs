using D365FO.Core;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Suggest;

/// <summary>
/// Suggests indexed Extended Data Types for a field name using name
/// similarity heuristics. Mirrors upstream MCP <c>suggest_edt</c>.
/// </summary>
public sealed class SuggestEdtCommand : Command<SuggestEdtCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<FIELDNAME>")]
        [System.ComponentModel.Description("Field name to suggest an EDT for, e.g. CustomerAccount, OrderAmount.")]
        public string FieldName { get; init; } = "";

        [CommandOption("-l|--limit <N>")]
        public int Limit { get; init; } = 5;
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        if (string.IsNullOrWhiteSpace(settings.FieldName))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("BAD_INPUT", "Field name required."));

        var suggestions = EdtSuggester.Suggest(RepoFactory.Create(), settings.FieldName, settings.Limit)
            .Select(s => new
            {
                name = s.Edt.Name,
                model = s.Edt.Model,
                extends = s.Edt.Extends,
                baseType = s.Edt.BaseType,
                stringSize = s.Edt.StringSize,
                confidence = s.Confidence,
                reason = s.Reason,
            })
            .ToList();
        return RenderHelpers.Render(kind, ToolResult<object>.Success(new
        {
            fieldName = settings.FieldName,
            count = suggestions.Count,
            suggestions,
        }));
    }
}
