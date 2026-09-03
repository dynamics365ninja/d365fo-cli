using System.Xml.Linq;

namespace D365FO.Core.Scaffolding;

/// <summary>One entry on a scaffolded <c>AxMenu</c>: a menu item, a tile or a reference to another menu.</summary>
/// <param name="Submenu">Submenu the entry sits under; <c>null</c> puts it at the menu root.</param>
/// <param name="Kind">One of <see cref="MenuEntryKind"/>.</param>
/// <param name="Target">The menu item, tile or menu the entry points at.</param>
/// <param name="MenuItemType">For menu items: Display (default, omitted from the XML), Action or Output.</param>
public sealed record MenuEntrySpec(string? Submenu, MenuEntryKind Kind, string Target, string? MenuItemType = null);

public enum MenuEntryKind { MenuItem, Tile, MenuReference }

/// <summary>A submenu and its label; submenus are created in the order they are declared.</summary>
public sealed record MenuSubmenuSpec(string Name, string? Label = null);

/// <summary>
/// Scaffolds the navigation objects that hang off menu items: <c>AxMenu</c>, <c>AxTile</c>
/// and <c>AxFormPart</c>.
/// </summary>
/// <remarks>
/// Ground-truthed against the installation, not the documentation. <c>AxMenu</c> and
/// <c>AxTile</c> contract into <c>Microsoft.Dynamics.AX.Metadata.V1</c> — the writer moves the
/// root there (<see cref="ContractNamespaceApplier"/>), and every <c>AxMenuElement</c> item is
/// written by the platform with <c>xmlns=""</c>, so the elements are built in the empty
/// namespace here and stay there. A menu element's <c>Name</c> is the key of a
/// <c>KeyedObjectCollection</c>: two entries with one name in one container is a document the
/// reader silently halves, so it is refused before the write.
/// </remarks>
public static class NavigationScaffolder
{
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    public static readonly IReadOnlyList<string> TileTypes = ["Standard", "Count", "KPI", "Link"];
    public static readonly IReadOnlyList<string> TileSizes = ["Medium", "Wide", "ShortWide", "Large"];
    public static readonly IReadOnlyList<string> TileDisplays = ["Auto", "TextAndImage", "TextOnly", "ImageOnly", "BackgroundImage"];
    public static readonly IReadOnlyList<string> MenuItemTypes = ["Display", "Action", "Output"];

    /// <summary>
    /// An <c>AxMenu</c>: the navigation-pane node a module hangs its menu items, tiles and
    /// sub-menus under.
    /// </summary>
    /// <param name="name">Menu name (AOT <c>&lt;Name&gt;</c> and file stem).</param>
    /// <param name="label">Label token or text shown in the navigation pane.</param>
    /// <param name="submenus">Sub-menus, in order. An entry naming a submenu not listed here creates it unlabelled.</param>
    /// <param name="entries">Menu items, tiles and menu references, in order, each optionally under a submenu.</param>
    /// <param name="displayInContentArea">Stamp <c>DisplayInContentArea=Yes</c> on every menu-item entry (list pages).</param>
    /// <param name="setCompany">Whether the menu switches legal entity (<c>SetCompany</c>).</param>
    /// <param name="configurationKey">Configuration key gating the whole menu.</param>
    public static XDocument Menu(
        string name,
        string? label = null,
        IEnumerable<MenuSubmenuSpec>? submenus = null,
        IEnumerable<MenuEntrySpec>? entries = null,
        bool displayInContentArea = false,
        bool setCompany = false,
        string? configurationKey = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Menu name is required.", nameof(name));

        var subs = (submenus ?? []).ToList();
        var all = (entries ?? []).ToList();

        // Every submenu an entry names exists — declared or implied — and keeps declaration order.
        var order = new List<string>();
        var labels = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in subs)
        {
            if (string.IsNullOrWhiteSpace(s.Name))
                throw new ArgumentException("Every submenu needs a name.", nameof(submenus));
            if (labels.ContainsKey(s.Name))
                throw new ArgumentException($"Submenu '{s.Name}' is declared twice.", nameof(submenus));
            labels[s.Name] = s.Label;
            order.Add(s.Name);
        }
        foreach (var e in all)
        {
            if (string.IsNullOrWhiteSpace(e.Target))
                throw new ArgumentException("Every menu entry needs a target.", nameof(entries));
            if (e.MenuItemType is not null && !MenuItemTypes.Contains(e.MenuItemType, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException(
                    $"Menu item type '{e.MenuItemType}' is not one of {string.Join("|", MenuItemTypes)}.", nameof(entries));
            if (!string.IsNullOrWhiteSpace(e.Submenu) && !labels.ContainsKey(e.Submenu))
            {
                labels[e.Submenu] = null;
                order.Add(e.Submenu);
            }
        }

        var root = new XElement("AxMenu",
            new XAttribute(XNamespace.Xmlns + "i", Xsi.NamespaceName),
            new XElement("Name", name));
        if (!string.IsNullOrEmpty(label)) root.Add(new XElement("Label", label));
        if (setCompany) root.Add(new XElement("SetCompany", "Yes"));
        if (!string.IsNullOrEmpty(configurationKey)) root.Add(new XElement("ConfigurationKey", configurationKey));

        var elements = new XElement("Elements");
        var rootNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var sub in order)
        {
            if (!rootNames.Add(sub))
                throw new ArgumentException($"'{sub}' appears twice at the root of menu '{name}'.", nameof(entries));

            var subEl = new XElement("AxMenuElement",
                new XAttribute(Xsi + "type", "AxMenuElementSubMenu"),
                new XElement("Name", sub));
            if (!string.IsNullOrEmpty(labels[sub])) subEl.Add(new XElement("Label", labels[sub]));

            var inner = new XElement("Elements");
            var innerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in all.Where(e => string.Equals(e.Submenu, sub, StringComparison.OrdinalIgnoreCase)))
            {
                if (!innerNames.Add(e.Target))
                    throw new ArgumentException($"'{e.Target}' appears twice under submenu '{sub}'.", nameof(entries));
                inner.Add(Entry(e, displayInContentArea));
            }
            subEl.Add(inner);
            elements.Add(subEl);
        }

        foreach (var e in all.Where(e => string.IsNullOrWhiteSpace(e.Submenu)))
        {
            if (!rootNames.Add(e.Target))
                throw new ArgumentException($"'{e.Target}' appears twice at the root of menu '{name}'.", nameof(entries));
            elements.Add(Entry(e, displayInContentArea));
        }

        root.Add(elements);
        return new XDocument(root);
    }

    private static XElement Entry(MenuEntrySpec e, bool displayInContentArea)
    {
        switch (e.Kind)
        {
            case MenuEntryKind.Tile:
                return new XElement("AxMenuElement",
                    new XAttribute(Xsi + "type", "AxMenuElementTile"),
                    new XElement("Name", e.Target),
                    new XElement("Tile", e.Target));
            case MenuEntryKind.MenuReference:
                return new XElement("AxMenuElement",
                    new XAttribute(Xsi + "type", "AxMenuElementMenuReference"),
                    new XElement("Name", e.Target),
                    new XElement("MenuName", e.Target));
            default:
                var el = new XElement("AxMenuElement",
                    new XAttribute(Xsi + "type", "AxMenuElementMenuItem"),
                    new XElement("Name", e.Target));
                if (displayInContentArea) el.Add(new XElement("DisplayInContentArea", "Yes"));
                el.Add(new XElement("MenuItemName", e.Target));
                // Display is the contract default and the platform omits it; Action/Output are written.
                if (e.MenuItemType is not null && !string.Equals(e.MenuItemType, "Display", StringComparison.OrdinalIgnoreCase))
                    el.Add(new XElement("MenuItemType", Canonical(MenuItemTypes, e.MenuItemType)));
                return el;
        }
    }

    /// <summary>
    /// An <c>AxTile</c>: a workspace or dashboard tile, bound to whatever its type opens.
    /// <c>Type</c> is written only when it is not the default <c>Standard</c>.
    /// </summary>
    /// <remarks>
    /// What a tile binds to follows from its type, counted on this installation's 770 tiles:
    /// 762 carry <c>MenuItemName</c> and all of them are Standard or Count. The eight that do
    /// not are the other two types — a Link tile carries <c>URL</c>
    /// (<c>CTPTechDocumentation</c>) and a KPI tile carries <c>KPI</c>
    /// (<c>QMSCAPAAvgDaysToCloseAllCases</c>) instead. Requiring a menu item on every tile,
    /// as this used to, made those two types impossible to write correctly while still
    /// offering them.
    /// </remarks>
    public static XDocument Tile(
        string name,
        string? menuItemName,
        string? type = null,
        string? label = null,
        string? size = null,
        string? normalImage = null,
        string? tileDisplay = null,
        string? query = null,
        string? kpi = null,
        string? configurationKey = null,
        string? menuItemType = null,
        string? helpText = null,
        string? url = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Tile name is required.", nameof(name));

        var tileType = type is null ? "Standard" : Canonical(TileTypes, type, "tile type");
        switch (tileType)
        {
            case "KPI" when string.IsNullOrWhiteSpace(kpi):
                throw new ArgumentException("A KPI tile names the KPI it shows; pass --kpi <AxKPI name>.", nameof(kpi));
            case "Link" when string.IsNullOrWhiteSpace(url):
                throw new ArgumentException("A Link tile opens a URL; pass --url <address>.", nameof(url));
            case "Standard" or "Count" when string.IsNullOrWhiteSpace(menuItemName):
                throw new ArgumentException(
                    $"A {tileType} tile opens a menu item; --menu-item is required.", nameof(menuItemName));
        }
        if (!string.IsNullOrWhiteSpace(url) && !string.Equals(tileType, "Link", StringComparison.Ordinal))
            throw new ArgumentException($"--url is a Link tile's target; a {tileType} tile has no URL member to put it in.", nameof(url));

        var root = new XElement("AxTile",
            new XAttribute(XNamespace.Xmlns + "i", Xsi.NamespaceName),
            new XElement("Name", name));
        if (!string.IsNullOrWhiteSpace(menuItemName)) root.Add(new XElement("MenuItemName", menuItemName));
        if (!string.IsNullOrWhiteSpace(url)) root.Add(new XElement("URL", url));
        if (!string.IsNullOrEmpty(normalImage)) root.Add(new XElement("NormalImage", normalImage));
        if (!string.IsNullOrEmpty(label)) root.Add(new XElement("Label", label));
        if (size is not null) root.Add(new XElement("Size", Canonical(TileSizes, size, "tile size")));
        if (tileType != "Standard") root.Add(new XElement("Type", tileType));
        if (!string.IsNullOrEmpty(helpText)) root.Add(new XElement("HelpText", helpText));
        if (!string.IsNullOrEmpty(kpi)) root.Add(new XElement("KPI", kpi));
        if (menuItemType is not null && !string.Equals(menuItemType, "Display", StringComparison.OrdinalIgnoreCase))
            root.Add(new XElement("MenuItemType", Canonical(MenuItemTypes, menuItemType, "menu item type")));
        if (!string.IsNullOrEmpty(configurationKey)) root.Add(new XElement("ConfigurationKey", configurationKey));
        if (tileDisplay is not null) root.Add(new XElement("TileDisplay", Canonical(TileDisplays, tileDisplay, "tile display")));
        if (!string.IsNullOrEmpty(query)) root.Add(new XElement("Query", query));
        return new XDocument(root);
    }

    /// <summary>
    /// An <c>AxFormPart</c>: the metadata object a form is registered under so it can be
    /// hosted as an info part, fact box or preview pane on another form. Three members, all of
    /// them present in every one of the 222 the installation ships.
    /// </summary>
    public static XDocument FormPart(string name, string form, string caption)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Form part name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(form))
            throw new ArgumentException("A form part hosts a form; --form is required.", nameof(form));
        if (string.IsNullOrWhiteSpace(caption))
            throw new ArgumentException("A form part carries a caption; --caption is required.", nameof(caption));

        return new XDocument(
            new XElement("AxFormPart",
                new XAttribute(XNamespace.Xmlns + "i", Xsi.NamespaceName),
                new XElement("Name", name),
                new XElement("Caption", caption),
                new XElement("Form", form)));
    }

    private static string Canonical(IReadOnlyList<string> allowed, string value, string what = "value")
    {
        var hit = allowed.FirstOrDefault(a => string.Equals(a, value, StringComparison.OrdinalIgnoreCase));
        return hit ?? throw new ArgumentException($"Unknown {what} '{value}'. Expected {string.Join("|", allowed)}.");
    }
}
