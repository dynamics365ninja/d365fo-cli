using System.Xml.Linq;
using D365FO.Core.FormPatterns;

namespace D365FO.Core.Scaffolding;

/// <summary>
/// Read-modify-write injection of <c>FormDataSource</c> / <c>FormControl</c>
/// override methods into an existing <c>AxForm</c> document.
///
/// Visual Studio places these methods in the <b>form-level</b> source-code tree,
/// not inside the metadata datasources:
/// <code>
/// &lt;AxForm&gt;
///   &lt;SourceCode&gt;
///     &lt;DataSources xmlns=""&gt;   &lt;!-- datasource override methods --&gt;
///       &lt;DataSource&gt;&lt;Name&gt;DS&lt;/Name&gt;&lt;Methods&gt;…&lt;/Methods&gt;&lt;Fields /&gt;&lt;/DataSource&gt;
///     &lt;/DataSources&gt;
///     &lt;DataControls xmlns=""&gt; &lt;!-- control override methods --&gt;
///       &lt;Control&gt;&lt;Name&gt;Ctrl&lt;/Name&gt;&lt;Methods&gt;…&lt;/Methods&gt;&lt;/Control&gt;
///     &lt;/DataControls&gt;
///   &lt;/SourceCode&gt;
///   &lt;DataSources&gt;…metadata only…&lt;/DataSources&gt;
/// &lt;/AxForm&gt;
/// </code>
/// The <c>SourceCode</c> element stays in the AxForm default namespace; its
/// <c>DataSources</c>/<c>DataControls</c> subtrees reset to the empty namespace
/// (<c>xmlns=""</c>) — exactly the shape the metadata deserializer expects.
/// </summary>
public static class FormMethodScaffolder
{
    /// <summary>Outcome of an injection attempt.</summary>
    public sealed record InjectResult(bool Changed, bool AlreadyExisted, string Source);

    /// <summary>Raised for structural problems the caller maps to a user-facing error code.</summary>
    public sealed class FormMethodException : Exception
    {
        public string Code { get; }
        public FormMethodException(string code, string message) : base(message) => Code = code;
    }

    // ---- public surface used by the CLI commands --------------------------

    /// <summary>Names of every metadata datasource declared on the form.</summary>
    public static IReadOnlyList<string> ListDataSourceNames(XDocument formDoc)
    {
        var root = RequireForm(formDoc);
        var container = root.Elements()
            .Where(x => x.Name.LocalName == "DataSources")
            .FirstOrDefault(c => c.Elements().Any(e => e.Name.LocalName.StartsWith("AxFormDataSource", StringComparison.Ordinal)));
        if (container is null) return Array.Empty<string>();
        return container.Elements()
            .Where(e => e.Name.LocalName.StartsWith("AxFormDataSource", StringComparison.Ordinal))
            .Select(e => Local(e, "Name"))
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .ToList();
    }

    /// <summary>Names of every control in the form design tree (recursive).</summary>
    public static IReadOnlyList<string> ListControlNames(XDocument formDoc)
    {
        var root = RequireForm(formDoc);
        var design = root.Elements().FirstOrDefault(x => x.Name.LocalName == "Design");
        var info = FormDesignWalker.Walk(design);
        var names = new List<string>();
        void Recurse(IEnumerable<FormControlNode> nodes)
        {
            foreach (var n in nodes)
            {
                if (!string.IsNullOrEmpty(n.Name) && n.Name != "Unknown") names.Add(n.Name);
                Recurse(n.Children);
            }
        }
        Recurse(info.Controls);
        return names;
    }

    /// <summary>
    /// Build a compile-safe X++ method source. When <paramref name="customBody"/>
    /// is supplied it is used verbatim (re-indented); otherwise a
    /// <c>super()</c>-returning stub is generated from <paramref name="sig"/>.
    /// </summary>
    public static string BuildMethodSource(FormMethodSignature sig, string? customBody = null)
    {
        var header = $"    public {sig.ReturnType} {sig.Name}({sig.Parameters})";
        string body;
        if (!string.IsNullOrWhiteSpace(customBody))
        {
            var indented = string.Join("\n",
                customBody.Replace("\r\n", "\n").Split('\n').Select(l => l.Length == 0 ? l : "        " + l));
            body = indented;
        }
        else if (sig.ReturnType == "void")
        {
            body = $"        super({sig.SuperArgs});";
        }
        else
        {
            body =
                $"        {sig.ReturnType} ret;\n" +
                "\n" +
                $"        ret = super({sig.SuperArgs});\n" +
                "\n" +
                "        return ret;";
        }
        // Leading + trailing newline mirror the VS-emitted CDATA layout.
        return $"\n{header}\n    {{\n{body}\n    }}\n";
    }

    /// <summary>Inject (or overwrite) a method on a form datasource.</summary>
    public static InjectResult InjectDataSourceMethod(
        XDocument formDoc, string dataSource, FormMethodSignature sig, string? customBody, bool overwrite)
        => Inject(formDoc, "DataSources", "DataSource", dataSource, sig, customBody, overwrite, includeFields: true);

    /// <summary>Inject (or overwrite) a method on a form control.</summary>
    /// <remarks>
    /// The item element is <c>&lt;Control&gt;</c>, not <c>&lt;DataControl&gt;</c> — that is the
    /// contract name of <c>AxFormControlPropertyCollection</c>, and every shipped form uses it.
    /// Under the wrong name the whole entry is dropped on read and the control keeps none of the
    /// methods written for it, while the file still looks right.
    /// </remarks>
    public static InjectResult InjectControlMethod(
        XDocument formDoc, string control, FormMethodSignature sig, string? customBody, bool overwrite)
        => Inject(formDoc, "DataControls", "Control", control, sig, customBody, overwrite, includeFields: false);

    // ---- core injection ----------------------------------------------------

    private static InjectResult Inject(
        XDocument formDoc, string containerLocal, string itemLocal,
        string ownerName, FormMethodSignature sig, string? customBody, bool overwrite, bool includeFields)
    {
        var root = RequireForm(formDoc);
        XNamespace v6 = root.Name.Namespace;     // AxForm default namespace
        XNamespace none = XNamespace.None;       // xmlns="" subtree

        var source = BuildMethodSource(sig, customBody);

        // <SourceCode> lives in the AxForm namespace, placed right after <Name>.
        var sourceCode = ChildByLocal(root, "SourceCode");
        if (sourceCode is null)
        {
            sourceCode = new XElement(v6 + "SourceCode");
            InsertInOrder(root, sourceCode, RootOrder);
        }

        // <DataSources> / <DataControls> reset to the empty namespace.
        var container = ChildByLocal(sourceCode, containerLocal);
        if (container is null)
        {
            container = new XElement(none + containerLocal);
            InsertInOrder(sourceCode, container, SourceCodeOrder);
        }

        // <DataSource>/<Control> matched by its <Name> child.
        var item = container.Elements()
            .FirstOrDefault(e => e.Name.LocalName == itemLocal &&
                                 string.Equals(Local(e, "Name"), ownerName, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            item = new XElement(none + itemLocal, new XElement(none + "Name", ownerName));
            if (includeFields) item.Add(new XElement(none + "Fields"));
            container.Add(item);
        }

        // <Methods> within the owner.
        var methods = ChildByLocal(item, "Methods");
        if (methods is null)
        {
            methods = new XElement(none + "Methods");
            // Methods must precede <Fields> on a DataSource.
            var fields = ChildByLocal(item, "Fields");
            if (fields is not null) fields.AddBeforeSelf(methods);
            else item.Add(methods);
        }

        // <Method> matched by <Name>.
        var existing = methods.Elements()
            .FirstOrDefault(e => e.Name.LocalName == "Method" &&
                                 string.Equals(Local(e, "Name"), sig.Name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (!overwrite)
                return new InjectResult(Changed: false, AlreadyExisted: true, source);

            var srcEl = ChildByLocal(existing, "Source");
            if (srcEl is null)
            {
                srcEl = new XElement(none + "Source");
                existing.Add(srcEl);
            }
            srcEl.ReplaceAll(new XCData(source));
            return new InjectResult(Changed: true, AlreadyExisted: true, source);
        }

        methods.Add(new XElement(none + "Method",
            new XElement(none + "Name", sig.Name),
            new XElement(none + "Source", new XCData(source))));
        return new InjectResult(Changed: true, AlreadyExisted: false, source);
    }

    // ---- helpers -----------------------------------------------------------

    // Canonical child order under <AxForm> (only the elements we may insert
    // before matter). SourceCode precedes the metadata DataSources / Design.
    private static readonly string[] RootOrder = { "Name", "SourceCode", "DataSources", "Design", "Parts" };

    // Canonical child order under <SourceCode>.
    private static readonly string[] SourceCodeOrder = { "Declaration", "Methods", "DataSources", "DataControls" };

    private static XElement RequireForm(XDocument formDoc)
    {
        ArgumentNullException.ThrowIfNull(formDoc);
        var root = formDoc.Root
            ?? throw new FormMethodException("INVALID_FORM", "Form document has no root element.");
        if (root.Name.LocalName != "AxForm")
            throw new FormMethodException("INVALID_FORM", $"Expected <AxForm>, got <{root.Name.LocalName}>.");
        return root;
    }

    private static XElement? ChildByLocal(XElement parent, string localName)
        => parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

    private static string? Local(XElement parent, string localName)
        => parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName)?.Value;

    /// <summary>
    /// Insert <paramref name="child"/> into <paramref name="parent"/> so the
    /// elements named in <paramref name="order"/> stay in canonical sequence —
    /// the metadata DataContractSerializer is order-sensitive. Elements not in
    /// <paramref name="order"/> are treated as "after everything known".
    /// </summary>
    private static void InsertInOrder(XElement parent, XElement child, string[] order)
    {
        int Rank(string local)
        {
            var idx = Array.IndexOf(order, local);
            return idx < 0 ? int.MaxValue : idx;
        }

        var childRank = Rank(child.Name.LocalName);
        var successor = parent.Elements()
            .FirstOrDefault(e => Rank(e.Name.LocalName) > childRank);
        if (successor is not null) successor.AddBeforeSelf(child);
        else parent.Add(child);
    }
}
