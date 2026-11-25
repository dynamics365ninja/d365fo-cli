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
    /// Turn a JSON array of steps into requests.
    /// </summary>
    /// <remarks>
    /// A step may name its own object with <c>"kind"</c>, <c>"object"</c> and <c>"model"</c>
    /// (issue #268); the arguments are the defaults for a step that does not. Whether the steps
    /// named any object is what <see cref="NamesObjects(JsonElement)"/> reports, and decides
    /// between a batch on one object and a batch over several.
    /// </remarks>
    /// <param name="json">The step array. Anything else throws.</param>
    /// <param name="kind">Object kind a step inherits when it names none.</param>
    /// <param name="objectName">Object a step applies to when it names none.</param>
    /// <param name="model">Model a step inherits when it names none.</param>
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
            if (element.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException($"step {index} is not an object");
            var name = Str(element, "operation") ?? Str(element, "op")
                       ?? throw new InvalidOperationException($"step {index} has no \"operation\"");
            if (!ObjectModifyEngine.TryParseOperation(name, out var operation))
                throw new InvalidOperationException($"step {index}: unknown operation \"{name}\"");

            steps.Add(new ObjectModifyEngine.ModifyRequest
            {
                Operation = operation,
                Kind = Str(element, "kind") ?? kind,
                ObjectName = Str(element, "object") ?? Str(element, "objectName") ?? objectName,
                Model = Str(element, "model") ?? model,
                Member = Str(element, "member") ?? Str(element, "name") ?? "",
                Value = Str(element, "value"),
                Type = Str(element, "type") ?? Str(element, "edt"),
                Label = Str(element, "label"),
                Mandatory = Bool(element, "mandatory"),
                NoEdit = Bool(element, "noEdit"),
                NoEditOnCreate = Bool(element, "noEditOnCreate"),
                Property = Str(element, "property"),
                Parent = Str(element, "parent"),
                DataSource = Str(element, "dataSource"),
                DataField = Str(element, "dataField"),
                DataMethod = Str(element, "dataMethod"),
                Table = Str(element, "table"),
                JoinSource = Str(element, "joinSource"),
                LinkType = Str(element, "linkType"),
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

    /// <summary>True when any step names its own object (<c>"object"</c> or <c>"objectName"</c>).</summary>
    public static bool NamesObjects(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return NamesObjects(doc.RootElement);
    }

    /// <inheritdoc cref="NamesObjects(string)"/>
    public static bool NamesObjects(JsonElement array) =>
        array.ValueKind == JsonValueKind.Array
        && array.EnumerateArray().Any(e => e.ValueKind == JsonValueKind.Object
                                           && (Str(e, "object") is not null || Str(e, "objectName") is not null));

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
