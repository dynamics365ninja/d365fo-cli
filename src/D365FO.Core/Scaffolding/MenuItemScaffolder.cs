using System.Xml.Linq;

namespace D365FO.Core.Scaffolding;

public enum MenuItemKind { Display, Action, Output }
public enum MenuItemObjectType { Form, Class, Report, Query }

/// <summary>
/// Scaffolds <c>AxMenuItemDisplay</c>, <c>AxMenuItemAction</c>, and
/// <c>AxMenuItemOutput</c> AOT objects. Each maps a menu item name to a
/// target object (Form, Class, SSRSReport, or Query).
/// </summary>
public static class MenuItemScaffolder
{
    /// <param name="enumTypeParameter">Enum whose value is passed to the target as its parameter.</param>
    /// <param name="enumParameter">The enum value passed — meaningless without a type.</param>
    /// <param name="parameters">Free-text parameter string handed to the target object.</param>
    /// <param name="configurationKey">Configuration key gating the item.</param>
    /// <param name="query">Query the item opens the target over.</param>
    /// <param name="neededPermission">
    /// Access the item requires. There is no <c>NeededPermission</c> member on a menu item —
    /// that one belongs to form controls. A menu item declares five independent
    /// <c>*Permissions</c> flags (Auto/No/Yes), so a level here is expanded into them.
    /// </param>
    /// <param name="linkedPermissionObject">Object whose permissions this item inherits.</param>
    /// <param name="linkedPermissionType">Kind of the linked object (MenuItemDisplay, Table, …).</param>
    public static XDocument MenuItem(
        MenuItemKind kind,
        string name,
        string objectName,
        MenuItemObjectType objectType = MenuItemObjectType.Form,
        string? label = null,
        string? enumTypeParameter = null,
        string? enumParameter = null,
        string? parameters = null,
        string? configurationKey = null,
        string? query = null,
        string? neededPermission = null,
        string? linkedPermissionObject = null,
        string? linkedPermissionType = null)
    {
        var rootElement = kind switch
        {
            MenuItemKind.Action => "AxMenuItemAction",
            MenuItemKind.Output => "AxMenuItemOutput",
            _                   => "AxMenuItemDisplay",
        };

        var objTypeStr = objectType switch
        {
            MenuItemObjectType.Class  => "Class",
            MenuItemObjectType.Report => "SSRSReport",
            MenuItemObjectType.Query  => "Query",
            _                         => "Form",
        };

        return new XDocument(
            new XElement(rootElement,
                new XElement("Name", name),
                string.IsNullOrEmpty(configurationKey) ? null : new XElement("ConfigurationKey", configurationKey),
                string.IsNullOrEmpty(enumParameter) || string.IsNullOrEmpty(enumTypeParameter)
                    ? null
                    : new XElement("EnumParameter", enumParameter),
                string.IsNullOrEmpty(enumTypeParameter) ? null : new XElement("EnumTypeParameter", enumTypeParameter),
                string.IsNullOrEmpty(label) ? null : new XElement("Label", label),
                string.IsNullOrEmpty(linkedPermissionObject) ? null : new XElement("LinkedPermissionObject", linkedPermissionObject),
                string.IsNullOrEmpty(linkedPermissionType) ? null : new XElement("LinkedPermissionType", linkedPermissionType),
                // No image properties. This used to emit <Image><ImageType>Symbol</ImageType></Image>
                // to head off BPErrorMissingOrUnsupportedImage — but AxMenuItem* has no Image
                // member and no ImageType anywhere, so the whole block was discarded on read
                // while the file looked deliberate. The real members are ImageLocation +
                // NormalImage (e.g. AOTResource + a resource name); shipped menu items that
                // show no icon simply omit them, which is what we do until a caller asks for one.
                new XElement("Object", objectName),
                new XElement("ObjectType", objTypeStr),
                string.IsNullOrEmpty(parameters) ? null : new XElement("Parameters", parameters),
                string.IsNullOrEmpty(query) ? null : new XElement("Query", query),
                PermissionFlags(neededPermission)));
    }

    /// <summary>
    /// Expands an access level into the menu item's five permission flags.
    /// </summary>
    /// <remarks>
    /// Each is <c>AutoNoYes</c>, and <c>Auto</c> — the default — means "infer from the target".
    /// Only the flags a level actually raises are written; setting the rest to <c>No</c> would
    /// silently deny access the target would otherwise have granted.
    /// </remarks>
    private static IEnumerable<XElement> PermissionFlags(string? neededPermission)
    {
        if (string.IsNullOrWhiteSpace(neededPermission)) yield break;

        var raised = neededPermission.Trim().ToLowerInvariant() switch
        {
            "delete" or "full"  => new[] { "CorrectPermissions", "CreatePermissions", "DeletePermissions", "ReadPermissions", "UpdatePermissions" },
            "create"            => new[] { "CreatePermissions", "ReadPermissions", "UpdatePermissions" },
            "correct"           => new[] { "CorrectPermissions", "ReadPermissions", "UpdatePermissions" },
            "update" or "edit"  => new[] { "ReadPermissions", "UpdatePermissions" },
            _                   => new[] { "ReadPermissions" },
        };

        // Contract order: Correct, Create, Delete, Read, Update — which the literals follow.
        foreach (var flag in raised) yield return new XElement(flag, "Yes");
    }

    /// <summary>Returns the canonical AOT subfolder for a given menu-item kind.</summary>
    public static string AxSubfolder(MenuItemKind kind) => kind switch
    {
        MenuItemKind.Action => "AxMenuItemAction",
        MenuItemKind.Output => "AxMenuItemOutput",
        _                   => "AxMenuItemDisplay",
    };
}
