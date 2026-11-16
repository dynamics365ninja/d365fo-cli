using System.Text.Json.Nodes;
using D365FO.Core.Index;

namespace D365FO.Core.Analysis;

/// <summary>
/// The brief shape of a class: its properties, and each method's name and signature without
/// its body. <c>get class --brief</c> and MCP <c>get_object_info(objectType=class, detail=brief)</c>
/// return it.
/// </summary>
/// <remarks>
/// Through the bridge, <c>get class</c> returns every method's full source. Measured on a real
/// installation: a 15-method custom class came back as 23.7k characters, 20.6k of them method
/// bodies. That is the right answer before editing a method and the wrong one for "what does this
/// class offer". The brief keeps what a caller needs to call the class or pick the method to read
/// (<c>d365fo get method</c> returns one body), and drops the rest. The default output is unchanged.
/// </remarks>
public static class ClassBrief
{
    /// <summary>Scalar members of the bridge payload that are source text, not properties.</summary>
    internal static readonly HashSet<string> SourceMembers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Declaration", "UnparsableSource",
    };

    /// <summary>
    /// Brief shape from the index. Inherited methods are counted, not listed: a class that
    /// extends <c>RunBaseBatch</c> inherits well over a hundred.
    /// </summary>
    public static object FromIndex(ClassDetails details) => new
    {
        detail = TableBrief.Detail,
        @class = details.Class,
        methods = details.Methods.Select(m => new { name = m.Name, signature = m.Signature }).ToList(),
        inheritedMethodCount = details.InheritedMethods.Count,
    };

    /// <summary>
    /// Brief shape from the bridge payload (the serialised AxClass, PascalCase members).
    /// <paramref name="indexed"/> supplies each method's signature, which the payload carries
    /// only inside the method's source; without it the method's return type stands in.
    /// </summary>
    public static JsonObject FromBridge(JsonObject payload, ClassDetails? indexed)
    {
        var signatures = indexed?.Methods
            .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Signature, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        var properties = new JsonObject();
        foreach (var (key, value) in payload)
            if (value is JsonValue && !SourceMembers.Contains(key) && !key.StartsWith('_')) properties[key] = value.DeepClone();

        var methods = new JsonArray();
        foreach (var method in BriefJson.Items(payload, "Methods"))
            methods.Add(BriefJson.Method(method, signatures));

        return new JsonObject
        {
            ["detail"] = TableBrief.Detail,
            ["properties"] = properties,
            ["methods"] = methods,
        };
    }
}

/// <summary>JSON helpers shared by the class and form briefs.</summary>
internal static class BriefJson
{
    internal static JsonNode? Member(JsonObject obj, string name) =>
        obj.FirstOrDefault(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>
    /// The objects of a collection member. A collection with one element can arrive as the
    /// element itself rather than a one-item array, so a lone object counts as one item.
    /// </summary>
    internal static IEnumerable<JsonObject> Items(JsonObject obj, string name) => Member(obj, name) switch
    {
        JsonArray array => array.OfType<JsonObject>(),
        JsonObject single => [single],
        _ => Enumerable.Empty<JsonObject>(),
    };

    internal static string? Text(JsonObject obj, string name) =>
        Member(obj, name) is JsonValue value && value.TryGetValue<string>(out var s) && !string.IsNullOrEmpty(s) ? s : null;

    internal static bool Yes(JsonObject obj, string name) =>
        Text(obj, name) is { } s && (s.Equals("True", StringComparison.OrdinalIgnoreCase) || s.Equals("Yes", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// One method without its body. With a signature from the index, name and signature: the
    /// signature already carries the modifiers. Without one, the return type and the flags that
    /// are set. The payload's <c>Visibility</c> is left out: the bridge reports <c>Private</c>
    /// for methods whose source declares them public, so it would contradict the signature.
    /// </summary>
    internal static JsonObject Method(JsonObject method, IReadOnlyDictionary<string, string?> signatures)
    {
        var name = Text(method, "Name");
        if (name is not null && signatures.TryGetValue(name, out var signature) && !string.IsNullOrWhiteSpace(signature))
            return new JsonObject { ["name"] = name, ["signature"] = signature };

        return Compact(new JsonObject
        {
            ["name"] = name,
            ["returnType"] = ReturnType(method),
            ["static"] = Yes(method, "IsStatic") ? true : null,
            ["abstract"] = Yes(method, "IsAbstract") ? true : null,
            ["delegate"] = Yes(method, "IsDelegate") ? true : null,
            ["display"] = Yes(method, "IsDisplay") ? true : null,
            ["edit"] = Yes(method, "IsEdit") ? true : null,
        });
    }

    private static string? ReturnType(JsonObject method) => Member(method, "ReturnType") switch
    {
        JsonObject rt => Text(rt, "TypeName") ?? Text(rt, "Type"),
        JsonValue v when v.TryGetValue<string>(out var s) && !string.IsNullOrEmpty(s) => s,
        _ => null,
    };

    /// <summary>Drop null members, as the index answer does (D365Json ignores nulls).</summary>
    internal static JsonObject Compact(JsonObject obj)
    {
        foreach (var key in obj.Where(kv => kv.Value is null).Select(kv => kv.Key).ToList())
            obj.Remove(key);
        return obj;
    }
}
