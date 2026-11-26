// <copyright file="ObjectModifyEngine.FormData.cs" company="d365fo-cli contributors">
// MIT
// </copyright>

using System.Text.RegularExpressions;
using System.Xml.Linq;
using D365FO.Core.FormPatterns;
using D365FO.Core.Index;
using D365FO.Core.Metadata;

namespace D365FO.Core.Bridge;

/// <summary>
/// The data side of a form: <c>modify add-data-source</c>, and what <c>modify add-control</c>
/// binds to — a datasource, and optionally one of its display methods (issue #283).
/// </summary>
public static partial class ObjectModifyEngine
{
    /// <summary>One datasource a form declares, and the document that declares it.</summary>
    private sealed record FormDataSource(string Name, string? Table, XElement Root);

    private const string LinkTypeEnum = "DataSourceLinkType_ITxt";

    private static readonly IReadOnlySet<string> DataSourceElements = new HashSet<string>(StringComparer.Ordinal)
    {
        // A root datasource, one a query-based datasource references, a derived one, and the
        // reference an extension adds under an existing datasource.
        "AxFormDataSource", "AxFormReferencedDataSource", "AxFormDataSourceDerived", "FormDataSourceReferenced",
    };

    private static readonly Regex Identifier = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);

    private static (object?, ToolResult<object>?) AddDataSource(
        XDocument doc, ModifyRequest request, WriteTarget target, MetadataRepository? repo,
        List<string> warnings, BridgeClient client)
    {
        var name = request.Member.Trim();
        if (!Identifier.IsMatch(name))
        {
            return (null, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                $"'{request.Member}' is not a valid datasource name.",
                "Use letters, digits and underscores, starting with a letter or underscore."));
        }

        // The whole form: a name another extension already uses is a clash even though this
        // write would not see it.
        var (roots, readFailure) = FormRoots(doc, request, target, client);
        if (readFailure is not null) return (null, readFailure);
        roots!.AddRange(SiblingExtensionRoots(request, target, repo, client, warnings));

        var declared = roots.SelectMany(DataSourcesIn).ToList();
        var clash = declared.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
        if (clash is not null)
        {
            return (null, ToolResult<object>.Fail(D365FoErrorCodes.AlreadyExists,
                $"Datasource '{clash.Name}' already exists on {LocalValue(clash.Root, "Name") ?? request.ObjectName}.",
                "Pick another name. This operation only adds; it never replaces a datasource."));
        }

        // An extension may join to anything on the form; the form itself only to its own.
        var joinable = target.IsExtension ? declared : DataSourcesIn(doc.Root!).ToList();
        string? joinSource = null;
        if (!string.IsNullOrWhiteSpace(request.JoinSource))
        {
            var parent = joinable.FirstOrDefault(d =>
                string.Equals(d.Name, request.JoinSource.Trim(), StringComparison.OrdinalIgnoreCase));
            if (parent is null) return (null, UnknownDataSource(request.JoinSource, request, joinable));
            joinSource = parent.Name;
        }

        string? linkType = null;
        if (!string.IsNullOrWhiteSpace(request.LinkType))
        {
            var allowed = MetadataContracts.EnumValues(LinkTypeEnum);
            linkType = allowed.FirstOrDefault(v => string.Equals(v, request.LinkType.Trim(), StringComparison.OrdinalIgnoreCase));
            if (linkType is null)
            {
                return (null, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                    $"'{request.LinkType}' is not a datasource link type.",
                    $"Use one of: {string.Join(", ", allowed)}."));
            }
        }

        var table = request.Table!.Trim();
        if (repo is null)
        {
            warnings.Add($"No index available — '{table}' was not verified to be a table, view, data entity or map.");
        }
        else
        {
            var indexed = IndexedDataSourceObject(repo, table);
            if (indexed is null)
            {
                return (null, ToolResult<object>.Fail(D365FoErrorCodes.TableNotFound,
                    $"No table, view, data entity or map named '{table}' is in the index.",
                    "Check the name with `d365fo search table <NAME>`; if the object is new, run `d365fo index refresh`."));
            }
            table = indexed;
        }

        var root = doc.Root!;
        var collection = EnsureCollection(root, "DataSources");
        collection.Attribute(FormControlFactory.Xsi + "nil")?.Remove();

        // Items of a form's datasource collection are in the empty namespace, like controls.
        // <Fields> holds per-field overrides only, so a new datasource starts without any.
        collection.Add(new XElement("AxFormDataSource",
            new XElement("Name", name),
            new XElement("Table", table),
            new XElement("Fields"),
            new XElement("ReferencedDataSources"),
            joinSource is null ? null : new XElement("JoinSource", joinSource),
            linkType is null ? null : new XElement("LinkType", linkType),
            new XElement("DataSourceLinks"),
            new XElement("DerivedDataSources")));

        return (new { dataSource = name, table, joinSource, linkType }, null);
    }

    /// <summary>
    /// What a new control is bound to, checked against the form: the property values to write,
    /// or why the binding is refused.
    /// </summary>
    private static (Dictionary<string, string>? Properties, ToolResult<object>? Failure) ControlBinding(
        XDocument doc, ModifyRequest request, WriteTarget target, MetadataRepository? repo,
        List<string> warnings, BridgeClient client)
    {
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        var dataField = string.IsNullOrWhiteSpace(request.DataField) ? null : request.DataField;
        var dataMethod = string.IsNullOrWhiteSpace(request.DataMethod) ? null : request.DataMethod.Trim();
        var dataSourceName = string.IsNullOrWhiteSpace(request.DataSource) ? null : request.DataSource.Trim();

        if (dataMethod is not null)
        {
            if (!ControlAccepts(request.Type!, "DataMethod"))
            {
                return (null, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                    $"A {request.Type} control has no DataMethod property.",
                    "Display methods bind to field controls: String, Integer, Real, Date, DateTime, CheckBox, ComboBox, Image, …"));
            }
        }
        else if (dataField is null && dataSourceName is not null && !ControlAccepts(request.Type!, "DataSource"))
        {
            return (null, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                $"A {request.Type} control takes no datasource on its own.",
                "Add --datafield or --data-method to bind it, or drop --datasource."));
        }

        if (dataField is not null)
        {
            // A reference group binds the surrogate key through ReferenceField; it has no DataField.
            var binding = string.Equals(FormControlFactory.CanonicalType(request.Type!), "ReferenceGroup", StringComparison.OrdinalIgnoreCase)
                ? "ReferenceField"
                : "DataField";
            properties[binding] = dataField;
            properties["DataSource"] = dataSourceName ?? "";
        }

        if (dataSourceName is null) return (properties, null);

        var (roots, readFailure) = FormRoots(doc, request, target, client);
        if (readFailure is not null) return (null, readFailure);

        var dataSource = Find(roots!);
        if (dataSource is null && target.IsExtension)
        {
            roots!.AddRange(SiblingExtensionRoots(request, target, repo, client, warnings));
            dataSource = Find(roots);
        }
        if (dataSource is null)
            return (null, UnknownDataSource(dataSourceName, request, roots!.SelectMany(DataSourcesIn).ToList()));

        properties["DataSource"] = dataSource.Name;
        if (dataMethod is not null)
        {
            var failure = CheckDataMethod(dataMethod, dataSource, roots!, repo, warnings);
            if (failure is not null) return (null, failure);
            properties["DataMethod"] = dataMethod;
        }

        return (properties, null);

        FormDataSource? Find(IEnumerable<XElement> candidates) => candidates.SelectMany(DataSourcesIn)
            .FirstOrDefault(d => string.Equals(d.Name, dataSourceName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ControlAccepts(string type, string member)
        => MetadataContracts.Find(FormControlFactory.AxTypeFor(type)) is { } contract
           && MetadataContracts.AcceptsMember(contract, member);

    /// <summary>
    /// The form as an edit sees it: the document being written and, when that is an extension,
    /// the base form it extends.
    /// </summary>
    private static (List<XElement>? Roots, ToolResult<object>? Failure) FormRoots(
        XDocument doc, ModifyRequest request, WriteTarget target, BridgeClient client)
    {
        var roots = new List<XElement> { doc.Root! };
        if (!target.IsExtension) return (roots, null);

        var (baseForm, _, failure) = ReadObjectDocument(client, "form", request.ObjectName);
        if (failure is not null) return (null, failure);
        roots.Add(baseForm!.Root!);
        return (roots, null);
    }

    /// <summary>Every other extension of the form the index knows, read live.</summary>
    private static List<XElement> SiblingExtensionRoots(
        ModifyRequest request, WriteTarget target, MetadataRepository? repo, BridgeClient client, List<string> warnings)
    {
        var roots = new List<XElement>();
        if (repo is null) return roots;

        List<string> names;
        try
        {
            names = repo.FindExtensions(request.ObjectName, null)
                .Where(e => string.Equals(e.Kind, "Form", StringComparison.OrdinalIgnoreCase))
                .Select(e => e.ExtensionName)
                .Where(n => !string.Equals(n, target.Name, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            warnings.Add($"The other extensions of {request.ObjectName} could not be listed, so their datasources were not checked: {ex.Message}");
            return roots;
        }

        foreach (var name in names)
        {
            var (extension, _, failure) = ReadObjectDocument(client, "formextension", name, "form");
            if (failure is not null)
                warnings.Add($"Form extension '{name}' could not be read, so its datasources were not checked.");
            else
                roots.Add(extension!.Root!);
        }

        return roots;
    }

    private static IEnumerable<FormDataSource> DataSourcesIn(XElement root)
        => root.Elements()
            .Where(e => e.Name.LocalName is "DataSources" or "DataSourceReferences")
            .SelectMany(e => e.Descendants())
            .Where(e => DataSourceElements.Contains(e.Name.LocalName))
            .Select(e => (Name: LocalValue(e, "Name"), Table: LocalValue(e, "Table")))
            .Where(d => !string.IsNullOrEmpty(d.Name))
            .Select(d => new FormDataSource(d.Name!, d.Table, root));

    private static ToolResult<object> UnknownDataSource(
        string name, ModifyRequest request, IReadOnlyList<FormDataSource> known)
        => ToolResult<object>.Fail(D365FoErrorCodes.DataSourceNotFound,
            $"Form {request.ObjectName} has no datasource named '{name}'.",
            known.Count > 0
                ? $"Its datasources: {string.Join(", ", known.Select(d => d.Name).Distinct(StringComparer.OrdinalIgnoreCase))}. " +
                  "Add one with `d365fo modify add-data-source`."
                : "The form has no datasources. Add one with `d365fo modify add-data-source`.");

    /// <summary>The AOT spelling of a table, view, data entity or map in the index, or null.</summary>
    private static string? IndexedDataSourceObject(MetadataRepository repo, string name)
    {
        if (repo.GetTableDetails(name) is { } table) return table.Table.Name;
        if (repo.GetView(name) is { } view) return view.View.Name;
        if (repo.GetDataEntity(name) is { MatchedOn: "name" } entity) return entity.Entity.Name;
        return repo.GetMap(name)?.Map.Name;
    }

    /// <summary>
    /// A <c>DataMethod</c> names either a method of the datasource (declared on the form
    /// datasource or on its table) or, qualified as <c>Class.method</c> / <c>Class::method</c>,
    /// one an extension class adds. Either way it has to be a <c>display</c> or <c>edit</c>
    /// method. What the index cannot see is reported as a warning, never assumed.
    /// </summary>
    private static ToolResult<object>? CheckDataMethod(
        string dataMethod, FormDataSource dataSource, IReadOnlyList<XElement> roots,
        MetadataRepository? repo, List<string> warnings)
    {
        var parts = dataMethod.Split(new[] { "::", "." }, StringSplitOptions.None);
        if (parts.Length > 2 || parts.Any(p => !Identifier.IsMatch(p)))
        {
            return ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                $"'{dataMethod}' is not a method reference.",
                "Use <method>, <Class>.<method> or <Class>::<method> — no parentheses.");
        }

        var method = parts[^1];
        var owner = parts.Length == 2 ? parts[0] : null;

        if (owner is null)
        {
            var source = roots.Select(r => FormDataSourceMethod(r, dataSource.Name, method)).FirstOrDefault(s => s is not null);
            if (source is not null)
            {
                var declaration = new Regex($@"\b(display|edit)\b[^(){{}};=]*\b{method}\s*\(", RegexOptions.IgnoreCase);
                return declaration.IsMatch(source) ? null : NotADisplayMethod(dataMethod);
            }
            owner = dataSource.Table;
        }

        if (repo is null || string.IsNullOrEmpty(owner))
        {
            warnings.Add($"Display method '{dataMethod}' was not verified: " +
                         (repo is null ? "no index is available." : $"datasource '{dataSource.Name}' names no table."));
            return null;
        }

        // Only classes and tables have their methods indexed.
        if (parts.Length == 1 && repo.GetTableDetails(owner) is null)
        {
            warnings.Add($"Display method '{dataMethod}' was not verified: the index holds no methods for '{owner}'.");
            return null;
        }

        var found = repo.FindMethod(owner, method);
        if (found is null)
        {
            var displayMethods = repo.GetTableMethods(owner)
                .Where(m => m.Signature is not null && DisplayModifier.IsMatch(m.Signature))
                .Select(m => m.Name)
                .Take(20)
                .ToList();
            return ToolResult<object>.Fail(D365FoErrorCodes.MethodNotFound,
                parts.Length == 2
                    ? $"Class '{owner}' has no method '{method}' in the index."
                    : $"Neither datasource '{dataSource.Name}' nor its table '{owner}' has a method '{method}'.",
                (displayMethods.Count > 0 ? $"Display/edit methods on {owner}: {string.Join(", ", displayMethods)}. " : "") +
                "A method an extension class adds is written <Class>.<method>. " +
                "If you just added it, run `d365fo index refresh`.");
        }

        // A signature the index does not hold (a method known only through a wrapper) passes.
        var signature = found.Signature;
        return signature is not null && signature.Contains('(') && !DisplayModifier.IsMatch(signature)
            ? NotADisplayMethod(dataMethod)
            : null;
    }

    private static readonly Regex DisplayModifier =
        new(@"\b(display|edit)\b[^(]*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static ToolResult<object> NotADisplayMethod(string dataMethod)
        => ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
            $"'{dataMethod}' is not a display or edit method, so a control cannot bind to it.",
            "Declare it with the `display` or `edit` modifier.");

    /// <summary>The source of a method declared on one of the form's datasources, or null.</summary>
    private static string? FormDataSourceMethod(XElement root, string dataSource, string method)
    {
        static IEnumerable<XElement> Named(XElement? parent, string element)
            => parent?.Elements().Where(e => e.Name.LocalName == element) ?? Enumerable.Empty<XElement>();

        var owner = Named(root, "SourceCode")
            .SelectMany(s => Named(s, "DataSources"))
            .SelectMany(s => Named(s, "DataSource"))
            .FirstOrDefault(d => string.Equals(LocalValue(d, "Name"), dataSource, StringComparison.OrdinalIgnoreCase));
        var found = Named(owner, "Methods")
            .SelectMany(m => Named(m, "Method"))
            .FirstOrDefault(m => string.Equals(LocalValue(m, "Name"), method, StringComparison.OrdinalIgnoreCase));
        return found is null ? null : LocalValue(found, "Source");
    }
}
