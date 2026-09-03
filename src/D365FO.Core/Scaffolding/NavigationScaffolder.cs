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
/// <param name="Name">Submenu name — the key of the element in its container.</param>
/// <param name="Label">Label token or text; <c>null</c> writes no label.</param>
/// <param name="Parent">
/// For a menu <em>extension</em>: an existing element of the base menu to nest the new submenu
/// under. Ignored by <see cref="NavigationScaffolder.Menu"/>, whose submenus are always at the root.
/// </param>
public sealed record MenuSubmenuSpec(string Name, string? Label = null, string? Parent = null);

/// <summary>Where an <c>AxMenuExtensionElement</c> lands among the base menu's elements.</summary>
/// <param name="Position"><c>Begin</c>, <c>End</c> (the contract default, not written) or <c>AfterItem</c>.</param>
/// <param name="PreviousSibling">For <c>AfterItem</c>: the element the new one follows.</param>
public sealed record MenuExtensionPlacement(string Position = "End", string? PreviousSibling = null);

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

    /// <summary>
    /// An <c>AxMenuExtension</c>: what a model adds to a menu it does not own. Each addition is
    /// an <c>AxMenuExtensionElement</c> wrapping the same <c>AxMenuElement</c> shapes a menu
    /// carries, optionally under a <c>Parent</c> — an existing element of the base menu — and
    /// optionally positioned <c>AfterItem</c> a <c>PreviousSibling</c>. Shipped counts (248 files):
    /// <c>Parent</c> on 154, <c>PositionType</c> on 98 (AfterItem 166 elements, Begin 72), and
    /// the two modification collections present but empty on 216.
    /// </summary>
    /// <param name="baseMenu">The menu being extended (the AOT name before the dot).</param>
    /// <param name="suffix">The extension suffix (after the dot).</param>
    /// <param name="placement">Where root-level additions land among the base menu's elements; additions under a <c>Parent</c> land at the end of that parent.</param>
    /// <param name="submenus">
    /// New submenus this extension adds. An entry whose <see cref="MenuEntrySpec.Submenu"/> names
    /// one of these nests inside it; an entry naming anything else is placed under that name as
    /// a <c>Parent</c> — an element the base menu already has.
    /// </param>
    /// <param name="entries">Menu items, tiles and menu references the extension adds.</param>
    /// <param name="displayInContentArea">Stamp <c>DisplayInContentArea=Yes</c> on every added menu item.</param>
    public static XDocument MenuExtension(
        string baseMenu,
        string suffix,
        IEnumerable<MenuSubmenuSpec>? submenus = null,
        IEnumerable<MenuEntrySpec>? entries = null,
        bool displayInContentArea = false,
        MenuExtensionPlacement? placement = null)
    {
        if (string.IsNullOrWhiteSpace(baseMenu))
            throw new ArgumentException("The menu being extended is required.", nameof(baseMenu));
        if (string.IsNullOrWhiteSpace(suffix))
            throw new ArgumentException("An extension suffix is required.", nameof(suffix));
        if (baseMenu.Contains('.'))
            throw new ArgumentException($"'{baseMenu}' names an extension, not a menu; extend the base menu.", nameof(baseMenu));

        placement ??= new MenuExtensionPlacement();
        var position = placement.Position is null ? "End" : placement.Position.Trim();
        if (!position.Equals("Begin", StringComparison.OrdinalIgnoreCase)
            && !position.Equals("End", StringComparison.OrdinalIgnoreCase)
            && !position.Equals("AfterItem", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Unknown position '{position}'. Expected Begin|End|AfterItem.", nameof(placement));
        if (position.Equals("AfterItem", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(placement.PreviousSibling))
            throw new ArgumentException("AfterItem needs the element the additions follow (--after <element>).", nameof(placement));
        if (!position.Equals("AfterItem", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(placement.PreviousSibling))
            position = "AfterItem";

        var subs = (submenus ?? []).ToList();
        var all = (entries ?? []).ToList();
        var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in subs)
        {
            if (string.IsNullOrWhiteSpace(s.Name)) throw new ArgumentException("Every submenu needs a name.", nameof(submenus));
            if (!declared.Add(s.Name)) throw new ArgumentException($"Submenu '{s.Name}' is declared twice.", nameof(submenus));
        }
        foreach (var e in all)
        {
            if (string.IsNullOrWhiteSpace(e.Target)) throw new ArgumentException("Every menu entry needs a target.", nameof(entries));
            if (e.MenuItemType is not null && !MenuItemTypes.Contains(e.MenuItemType, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException($"Menu item type '{e.MenuItemType}' is not one of {string.Join("|", MenuItemTypes)}.", nameof(entries));
        }
        if (subs.Count == 0 && all.Count == 0)
            throw new ArgumentException("A menu extension that adds nothing: pass at least one submenu, item, tile or menu reference.", nameof(entries));

        var elements = new XElement("Elements");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        XElement Wrap(string? parent, XElement menuElement)
        {
            var wrapper = new XElement("AxMenuExtensionElement");
            if (!string.IsNullOrEmpty(parent)) wrapper.Add(new XElement("Parent", parent));
            // The placement names a sibling among the base menu's ROOT elements, so it applies
            // to root-level additions only; an addition under a Parent lands at the end of that
            // parent — "after Customers" inside Customers would name a sibling that is not there.
            if (string.IsNullOrEmpty(parent))
            {
                if (!position.Equals("End", StringComparison.OrdinalIgnoreCase))
                    wrapper.Add(new XElement("PositionType", position.Equals("Begin", StringComparison.OrdinalIgnoreCase) ? "Begin" : "AfterItem"));
                if (position.Equals("AfterItem", StringComparison.OrdinalIgnoreCase))
                    wrapper.Add(new XElement("PreviousSibling", placement.PreviousSibling));
            }
            menuElement.Name = "MenuElement";
            wrapper.Add(menuElement);
            return wrapper;
        }

        foreach (var s in subs)
        {
            if (!seen.Add(s.Name))
                throw new ArgumentException($"'{s.Name}' is added twice by extension '{baseMenu}.{suffix}'.", nameof(submenus));
            var subEl = new XElement("AxMenuElement",
                new XAttribute(Xsi + "type", "AxMenuElementSubMenu"),
                new XElement("Name", s.Name));
            if (!string.IsNullOrEmpty(s.Label)) subEl.Add(new XElement("Label", s.Label));
            var inner = new XElement("Elements");
            var innerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in all.Where(e => string.Equals(e.Submenu, s.Name, StringComparison.OrdinalIgnoreCase)))
            {
                if (!innerNames.Add(e.Target))
                    throw new ArgumentException($"'{e.Target}' appears twice under submenu '{s.Name}'.", nameof(entries));
                inner.Add(Entry(e, displayInContentArea));
            }
            subEl.Add(inner);
            elements.Add(Wrap(s.Parent, subEl));
        }

        // Entries not under a submenu this extension declares go straight into the base menu:
        // at its root, or under the existing element they name.
        foreach (var e in all.Where(e => string.IsNullOrWhiteSpace(e.Submenu) || !declared.Contains(e.Submenu!)))
        {
            if (!seen.Add(e.Target))
                throw new ArgumentException($"'{e.Target}' is added twice by extension '{baseMenu}.{suffix}'.", nameof(entries));
            elements.Add(Wrap(e.Submenu, Entry(e, displayInContentArea)));
        }

        return new XDocument(
            new XElement("AxMenuExtension",
                new XAttribute(XNamespace.Xmlns + "i", Xsi.NamespaceName),
                new XElement("Name", $"{baseMenu}.{suffix}"),
                new XElement("Customizations"),
                elements,
                new XElement("MenuElementModifications"),
                new XElement("PropertyModifications")));
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
