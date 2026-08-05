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
    public static XDocument MenuItem(
        MenuItemKind kind,
        string name,
        string objectName,
        MenuItemObjectType objectType = MenuItemObjectType.Form,
        string? label = null)
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
                string.IsNullOrEmpty(label) ? null : new XElement("Label", label),
                // No image properties. This used to emit <Image><ImageType>Symbol</ImageType></Image>
                // to head off BPErrorMissingOrUnsupportedImage — but AxMenuItem* has no Image
                // member and no ImageType anywhere, so the whole block was discarded on read
                // while the file looked deliberate. The real members are ImageLocation +
                // NormalImage (e.g. AOTResource + a resource name); shipped menu items that
                // show no icon simply omit them, which is what we do until a caller asks for one.
                new XElement("Object", objectName),
                new XElement("ObjectType", objTypeStr)));
    }

    /// <summary>Returns the canonical AOT subfolder for a given menu-item kind.</summary>
    public static string AxSubfolder(MenuItemKind kind) => kind switch
    {
        MenuItemKind.Action => "AxMenuItemAction",
        MenuItemKind.Output => "AxMenuItemOutput",
        _                   => "AxMenuItemDisplay",
    };
}
