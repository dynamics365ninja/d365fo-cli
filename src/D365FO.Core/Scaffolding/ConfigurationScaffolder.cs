using System.Xml.Linq;

namespace D365FO.Core.Scaffolding;

/// <summary>
/// Scaffolds the small configuration objects: <c>AxConfigurationKey</c>,
/// <c>AxWorkflowCategory</c>, <c>AxResource</c> and <c>AxLabelFile</c>.
/// </summary>
/// <remarks>
/// Each shape is what the installation writes, measured with <c>d365fo oracle census</c>
/// rather than recalled: 472 configuration keys (Label on 463, ParentKey on 349), 37 workflow
/// categories (Label on all, Module on 35), 2 356 resources (FileName and
/// RelativeUriInModelStore on every one, TypeOfResource on 1 826 — the rest are images, the
/// enum default), and one label-file manifest per language. <c>AxWorkflowCategory</c>
/// contracts into <c>Microsoft.Dynamics.AX.Metadata.V2</c>; the writer moves it there.
/// </remarks>
public static class ConfigurationScaffolder
{
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    /// <summary>The <c>ResourceType</c> enum as the metadata assembly declares it.</summary>
    public static readonly IReadOnlyList<string> ResourceTypes =
    [
        "Images", "Audio", "Video", "Data", "PublishCSS", "OnlineHelpCSS", "ToolbarCSS", "XmlDoc",
        "Html", "Scripts", "Styles", "Text", "Certificate", "PowerBIReport", "PCFControl",
    ];

    /// <summary>An <c>AxConfigurationKey</c>: the switch a table, field, menu or form can be gated behind.</summary>
    public static XDocument ConfigurationKey(
        string name,
        string? label = null,
        string? parentKey = null,
        string? licenseCode = null,
        string? description = null,
        bool enabledByDefault = true)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Configuration key name is required.", nameof(name));
        if (string.Equals(parentKey, name, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Configuration key '{name}' cannot be its own parent.", nameof(parentKey));

        var root = new XElement("AxConfigurationKey",
            new XAttribute(XNamespace.Xmlns + "i", Xsi.NamespaceName),
            new XElement("Name", name));
        if (!string.IsNullOrEmpty(licenseCode)) root.Add(new XElement("LicenseCode", licenseCode));
        if (!string.IsNullOrEmpty(parentKey)) root.Add(new XElement("ParentKey", parentKey));
        if (!string.IsNullOrEmpty(label)) root.Add(new XElement("Label", label));
        // Yes is the contract default and is never written; only the opt-out is.
        if (!enabledByDefault) root.Add(new XElement("EnabledByDefault", "No"));
        if (!string.IsNullOrEmpty(description)) root.Add(new XElement("Description", description));
        return new XDocument(root);
    }

    /// <summary>
    /// An <c>AxWorkflowCategory</c>: the module bucket a workflow type is filed under in the
    /// workflow configuration form. <paramref name="module"/> is a <c>ModuleAxapta</c> value
    /// (<c>PurchaseOrder</c>, <c>Ledger</c>, <c>Basic</c> …); the caller validates it against
    /// the enum, because the contract types it as a plain string and the platform does not.
    /// </summary>
    public static XDocument WorkflowCategory(string name, string module, string? label = null, string? helpText = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Workflow category name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(module))
            throw new ArgumentException("A workflow category belongs to a module; --module is required.", nameof(module));

        var root = new XElement("AxWorkflowCategory",
            new XAttribute(XNamespace.Xmlns + "i", Xsi.NamespaceName),
            new XElement("Name", name));
        if (!string.IsNullOrEmpty(helpText)) root.Add(new XElement("HelpText", helpText));
        if (!string.IsNullOrEmpty(label)) root.Add(new XElement("Label", label));
        root.Add(new XElement("Module", module));
        return new XDocument(root);
    }

    /// <summary>
    /// The folder under <c>AxResource\ResourceContent\</c> a resource of <paramref name="resourceType"/>
    /// lives in — the enum value is the folder name (<c>Images</c>, <c>XmlDoc</c>, <c>Data</c> …).
    /// </summary>
    public static string ResourceContentFolder(string resourceType) => CanonicalResourceType(resourceType);

    /// <summary>
    /// An <c>AxResource</c>: the manifest for a file shipped inside a model — an image, an
    /// XML document, a Power BI report. The manifest names the file and where it lives relative
    /// to the model store; the file itself is the caller's to place.
    /// </summary>
    /// <param name="model">The model whose folder holds the content — the first segment of <c>RelativeUriInModelStore</c>.</param>
    public static XDocument Resource(string name, string fileName, string model, string? resourceType = null, string? label = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Resource name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("A resource names the file it ships; --file-name is required.", nameof(fileName));
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("The owning model is required to compute RelativeUriInModelStore.", nameof(model));
        if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || fileName.Contains('/') || fileName.Contains('\\'))
            throw new ArgumentException($"'{fileName}' is not a bare file name.", nameof(fileName));

        var type = resourceType is null ? "Images" : CanonicalResourceType(resourceType);

        var root = new XElement("AxResource",
            new XAttribute(XNamespace.Xmlns + "i", Xsi.NamespaceName),
            new XElement("Name", name));
        if (!string.IsNullOrEmpty(label)) root.Add(new XElement("Label", label));
        root.Add(new XElement("FileName", fileName));
        // Images is the enum default; the platform omits it and writes every other type.
        if (type != "Images") root.Add(new XElement("TypeOfResource", type));
        root.Add(new XElement("RelativeUriInModelStore", $"{model}/AxResource/ResourceContent/{type}/{fileName}"));
        return new XDocument(root);
    }

    /// <summary>
    /// An <c>AxLabelFile</c> manifest for one language of a label file. The object is named
    /// <c>&lt;FileId&gt;_&lt;Language&gt;</c>, and the content it points at is
    /// <c>&lt;Package&gt;\&lt;Model&gt;\AxLabelFile\LabelResources\&lt;Language&gt;\&lt;FileId&gt;.&lt;Language&gt;.label.txt</c>.
    /// </summary>
    public static XDocument LabelFile(string labelFileId, string language, string package, string model)
    {
        if (string.IsNullOrWhiteSpace(labelFileId))
            throw new ArgumentException("Label file id is required.", nameof(labelFileId));
        if (string.IsNullOrWhiteSpace(language))
            throw new ArgumentException("Language is required (e.g. en-US).", nameof(language));
        if (string.IsNullOrWhiteSpace(package) || string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Package and model are required to compute RelativeUriInModelStore.", nameof(model));

        var content = LabelContentFileName(labelFileId, language);
        return new XDocument(
            new XElement("AxLabelFile",
                new XAttribute(XNamespace.Xmlns + "i", Xsi.NamespaceName),
                new XElement("Name", LabelFileObjectName(labelFileId, language)),
                new XElement("LabelContentFileName", content),
                new XElement("LabelFileId", labelFileId),
                new XElement("Language", language),
                new XElement("RelativeUriInModelStore",
                    $@"{package}\{model}\AxLabelFile\LabelResources\{language}\{content}")));
    }

    public static string LabelFileObjectName(string labelFileId, string language) => $"{labelFileId}_{language}";
    public static string LabelContentFileName(string labelFileId, string language) => $"{labelFileId}.{language}.label.txt";

    private static string CanonicalResourceType(string value)
    {
        var hit = ResourceTypes.FirstOrDefault(t => string.Equals(t, value, StringComparison.OrdinalIgnoreCase));
        return hit ?? throw new ArgumentException(
            $"Unknown resource type '{value}'. Expected {string.Join("|", ResourceTypes)}.");
    }
}
