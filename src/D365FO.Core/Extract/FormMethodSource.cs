using System.Xml.Linq;

namespace D365FO.Core.Extract;

/// <summary>
/// The X++ method bodies of an <c>AxForm</c>, each named by the member it belongs to.
/// </summary>
/// <remarks>
/// <para>
/// A form carries methods at four levels, all under <c>&lt;SourceCode&gt;</c>: the form itself
/// (<c>Methods</c>), its data sources (<c>DataSources/DataSource</c>), their fields
/// (<c>DataSource/Fields/DataField</c>) and its controls (<c>DataControls/Control</c>). The bare
/// method name does not say which: <c>init</c> is the form's, every data source's and often a
/// control's at once, so a hit reported as <c>init</c> sends the reader to the wrong one.
/// </para>
/// <para>
/// Members are therefore qualified by their owner — <c>init</c> for the form,
/// <c>CustTable.init</c> for a data source, <c>CustTable.AccountNum.modified</c> for a data
/// source field, <c>OkButton.clicked</c> for a control. The same names are used when the
/// bodies are indexed (<c>MethodSourceFts</c>, Kind <c>Form</c>) and when a form is scanned on
/// disk, so <c>find refs</c> reports a form hit identically on both paths.
/// </para>
/// </remarks>
public static class FormMethodSource
{
    /// <summary>One method body and the member name that identifies it within the form.</summary>
    public sealed record Member(string Name, string Body);

    /// <summary>Every method body in the form at <paramref name="xmlPath"/>, or null when it cannot be read.</summary>
    public static IReadOnlyList<Member>? Read(string? xmlPath)
    {
        if (string.IsNullOrWhiteSpace(xmlPath) || !File.Exists(xmlPath)) return null;
        try { return Enumerate(XDocument.Load(xmlPath, LoadOptions.None).Root); }
        catch { return null; }
    }

    /// <summary>The method body of <paramref name="member"/> (as <see cref="Enumerate"/> names it), or null.</summary>
    public static string? FindBody(string? xmlPath, string member) =>
        Read(xmlPath)?.FirstOrDefault(m => string.Equals(m.Name, member, StringComparison.OrdinalIgnoreCase))?.Body;

    /// <summary>Every method body under <paramref name="formRoot"/>, in document order within each level.</summary>
    public static IReadOnlyList<Member> Enumerate(XElement? formRoot)
    {
        var members = new List<Member>();
        if (formRoot is null) return members;
        var seen = new HashSet<XElement>();

        void AddMethods(XElement? owner, string prefix)
        {
            var methods = Child(owner, "Methods");
            if (methods is null) return;
            foreach (var m in methods.Elements().Where(e => e.Name.LocalName == "Method"))
            {
                var name = Child(m, "Name")?.Value.Trim();
                var body = Child(m, "Source")?.Value;
                if (string.IsNullOrEmpty(name) || body is null) continue;
                seen.Add(m);
                members.Add(new Member(prefix + name, body));
            }
        }

        var sourceCode = Child(formRoot, "SourceCode");
        if (sourceCode is not null)
        {
            AddMethods(sourceCode, "");

            foreach (var ds in Child(sourceCode, "DataSources")?.Elements().Where(e => e.Name.LocalName == "DataSource")
                               ?? Enumerable.Empty<XElement>())
            {
                var dsName = Child(ds, "Name")?.Value.Trim();
                if (string.IsNullOrEmpty(dsName)) continue;
                AddMethods(ds, dsName + ".");
                foreach (var field in Child(ds, "Fields")?.Elements() ?? Enumerable.Empty<XElement>())
                {
                    // <DataField><DataField>AccountNum</DataField><Methods>…</Methods></DataField>
                    var fieldName = (Child(field, "DataField") ?? Child(field, "Name"))?.Value.Trim();
                    if (string.IsNullOrEmpty(fieldName)) continue;
                    AddMethods(field, dsName + "." + fieldName + ".");
                }
            }

            foreach (var ctrl in Child(sourceCode, "DataControls")?.Elements().Where(e => e.Name.LocalName == "Control")
                                 ?? Enumerable.Empty<XElement>())
            {
                var ctrlName = Child(ctrl, "Name")?.Value.Trim();
                if (string.IsNullOrEmpty(ctrlName)) continue;
                AddMethods(ctrl, ctrlName + ".");
            }
        }

        // Anything in a layout the walk above does not know keeps its bare name rather than
        // dropping out: a body missing from the index is a reference find refs cannot see.
        foreach (var m in formRoot.Descendants().Where(e => e.Name.LocalName == "Method" && !seen.Contains(e)))
        {
            var name = Child(m, "Name")?.Value.Trim();
            var body = Child(m, "Source")?.Value;
            if (string.IsNullOrEmpty(name) || body is null) continue;
            members.Add(new Member(name, body));
        }
        return members;
    }

    private static XElement? Child(XElement? parent, string localName) =>
        parent?.Elements().FirstOrDefault(e => e.Name.LocalName == localName);
}
