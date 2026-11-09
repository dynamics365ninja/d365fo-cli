using System.Text.Json.Nodes;
using D365FO.Core.Index;

namespace D365FO.Core.Analysis;

/// <summary>
/// The brief shape of a form: its properties, the design's pattern and caption, each data
/// source with its table, join and field count, the top-level controls with how many controls
/// each holds, and method names without bodies. <c>get form --brief</c> and MCP
/// <c>get_object_info(objectType=form, detail=brief)</c> return it.
/// </summary>
/// <remarks>
/// Through the bridge, <c>get form</c> returns the whole AxForm: every data source field's
/// property bag, every control's layout properties and every method body. Measured on a real
/// installation: one details-transaction form with 20 data sources came back as 199.8k
/// characters (data sources 144k, methods 40k, design 13k). The brief answers "what does this
/// form show and from which tables" at a small fraction of that. The default output is unchanged.
/// </remarks>
public static class FormBrief
{
    /// <summary>Design properties worth keeping: the ones that say what kind of form it is.</summary>
    private static readonly string[] DesignKeys =
    [
        "Pattern", "PatternVersion", "Style", "Caption", "DataSource", "TitleDataSource",
        "WorkflowEnabled", "WorkflowDataSource", "WorkflowType",
    ];

    /// <summary>Brief shape from the index: the form row and its data sources with their tables.</summary>
    public static object FromIndex(FormDetails details) => new
    {
        detail = TableBrief.Detail,
        form = details.Form,
        dataSources = details.DataSources,
    };

    /// <summary>Brief shape from the bridge payload (the serialised AxForm, PascalCase members).</summary>
    public static JsonObject FromBridge(JsonObject payload)
    {
        var properties = new JsonObject();
        foreach (var (key, value) in payload)
            if (value is JsonValue && !ClassBrief.SourceMembers.Contains(key) && !key.StartsWith('_')) properties[key] = value.DeepClone();

        var noSignatures = new Dictionary<string, string?>();

        var dataSources = new JsonArray();
        foreach (var ds in BriefJson.Items(payload, "DataSources"))
        {
            var dsMethods = new JsonArray();
            foreach (var m in BriefJson.Items(ds, "Methods"))
                if (BriefJson.Text(m, "Name") is { } n) dsMethods.Add(n);
            dataSources.Add(BriefJson.Compact(new JsonObject
            {
                ["name"] = BriefJson.Text(ds, "Name"),
                ["table"] = BriefJson.Text(ds, "Table"),
                ["joinSource"] = BriefJson.Text(ds, "JoinSource"),
                ["linkType"] = BriefJson.Text(ds, "JoinSource") is null ? null : BriefJson.Text(ds, "LinkType"),
                ["fieldCount"] = BriefJson.Items(ds, "Fields").Count(),
                ["methods"] = dsMethods.Count > 0 ? dsMethods : null,
            }));
        }

        JsonObject? design = null;
        var controls = new JsonArray();
        var controlCount = 0;
        if (BriefJson.Member(payload, "Design") is JsonObject d)
        {
            design = new JsonObject();
            foreach (var key in DesignKeys)
                if (BriefJson.Text(d, key) is { } v) design[key] = v;
            foreach (var control in BriefJson.Items(d, "Controls"))
            {
                var inside = CountControls(control);
                controlCount += 1 + inside;
                controls.Add(BriefJson.Compact(new JsonObject
                {
                    ["name"] = BriefJson.Text(control, "Name"),
                    ["type"] = BriefJson.Text(control, "Type"),
                    ["dataSource"] = BriefJson.Text(control, "DataSource"),
                    ["controlCount"] = inside > 0 ? inside : null,
                }));
            }
        }

        var methods = new JsonArray();
        foreach (var method in BriefJson.Items(payload, "Methods"))
            methods.Add(BriefJson.Method(method, noSignatures));

        return BriefJson.Compact(new JsonObject
        {
            ["detail"] = TableBrief.Detail,
            ["properties"] = properties,
            ["design"] = design,
            ["dataSources"] = dataSources,
            ["controls"] = controls,
            ["controlCount"] = controlCount,
            ["methods"] = methods,
        });
    }

    /// <summary>Controls nested anywhere under <paramref name="control"/>, not counting itself.</summary>
    private static int CountControls(JsonObject control)
    {
        var count = 0;
        foreach (var child in BriefJson.Items(control, "Controls"))
            count += 1 + CountControls(child);
        return count;
    }
}
