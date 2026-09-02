using System.Text.Json;

namespace D365FO.Core.Bridge;

/// <summary>
/// Parses the JSON step array both write surfaces accept for a batched modification —
/// <c>d365fo modify batch --operations</c> and the MCP <c>modify_object</c> tool's
/// <c>operations</c> argument.
/// </summary>
/// <remarks>
/// <para>
/// It lives in Core rather than beside the CLI command because it is the shape of an input,
/// not of a command. While it was CLI-only, MCP had no batch at all: an agent applying five
/// edits to one table published four intermediate states of that table, each its own bridge
/// read, write and journal entry.
/// </para>
/// <para>
/// Operation names resolve through <see cref="ObjectModifyEngine.TryParseOperation"/>, the same
/// map the sub-commands and the MCP <c>action</c> use. The parser used to derive the enum
/// spelling itself (<c>add-field</c> → <c>AddField</c> → <see cref="ObjectModifyEngine.Operation"/>),
/// which worked for every operation whose command name matched its enum name and failed for the
/// one that does not: <c>property</c> is <c>SetProperty</c>, so a step naming the operation the
/// error text itself recommends was refused as unknown.
/// </para>
/// </remarks>
public static class BatchStepParser
{
    /// <summary>
    /// Turn a JSON array of steps into requests bound to one object.
    /// </summary>
    /// <param name="json">The step array. Anything else throws.</param>
    /// <param name="kind">Object kind every step inherits.</param>
    /// <param name="objectName">Object every step applies to.</param>
    /// <param name="model">Model every step inherits, when the caller pinned one.</param>
    /// <exception cref="InvalidOperationException">The array, or one step, is not usable.</exception>
    public static List<ObjectModifyEngine.ModifyRequest> Parse(
        string json, string kind, string objectName, string? model)
    {
        using var doc = JsonDocument.Parse(json);
        return Parse(doc.RootElement, kind, objectName, model);
    }

    /// <summary>Step-array overload for callers that already hold the parsed element (MCP).</summary>
    /// <exception cref="InvalidOperationException">The array, or one step, is not usable.</exception>
    public static List<ObjectModifyEngine.ModifyRequest> Parse(
        JsonElement array, string kind, string objectName, string? model)
    {
        if (array.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("expected a JSON array of steps");

        var steps = new List<ObjectModifyEngine.ModifyRequest>();
        var index = 0;
        foreach (var element in array.EnumerateArray())
        {
            index++;
            var name = Str(element, "operation")
                       ?? throw new InvalidOperationException($"step {index} has no \"operation\"");
            if (!ObjectModifyEngine.TryParseOperation(name, out var operation))
                throw new InvalidOperationException($"step {index}: unknown operation \"{name}\"");

            steps.Add(new ObjectModifyEngine.ModifyRequest
            {
                Operation = operation,
                Kind = kind,
                ObjectName = objectName,
                Model = model,
                Member = Str(element, "member") ?? Str(element, "name") ?? "",
                Value = Str(element, "value"),
                Type = Str(element, "type") ?? Str(element, "edt"),
                Label = Str(element, "label"),
                Mandatory = Bool(element, "mandatory"),
                Parent = Str(element, "parent"),
                DataSource = Str(element, "dataSource"),
                DataField = Str(element, "dataField"),
                Fields = Strings(element, "fields"),
                RelatedTable = Str(element, "relatedTable"),
                RelatedField = Str(element, "relatedField"),
                DeleteAction = Str(element, "action") ?? Str(element, "deleteAction"),
                AllowDuplicates = Bool(element, "allowDuplicates"),
                AlternateKey = Bool(element, "alternateKey"),
                NewName = Str(element, "newName"),
                DataSourceName = Str(element, "dataSourceName") ?? Str(element, "dataSource"),
                RangeValue = Str(element, "rangeValue") ?? Str(element, "value"),
                EntryPointType = Str(element, "entryPointType") ?? Str(element, "type"),
                Access = Str(element, "access"),
            });
        }
        return steps;
    }

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static IReadOnlyList<string>? Strings(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            return null;
        return value.EnumerateArray()
            .Where(v => v.ValueKind == JsonValueKind.String)
            .Select(v => v.GetString()!)
            .ToList();
    }
}
