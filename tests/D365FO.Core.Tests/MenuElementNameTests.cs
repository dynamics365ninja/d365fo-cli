using System.Xml.Linq;
using D365FO.Core.Scaffolding;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// Element names are unique across a whole menu, not per submenu: xppc refuses a repeat with
/// "The name '…' is not unique. Multiple elements have the same name."
/// </summary>
public sealed class MenuElementNameTests
{
    private static string[] ElementNames(XDocument doc)
        => doc.Descendants()
            .Where(e => e.Name.LocalName is "AxMenuElement" or "MenuElement")
            .Select(e => e.Element("Name")!.Value)
            .ToArray();

    private static string[] Targets(XDocument doc)
        => doc.Descendants("MenuItemName").Select(e => e.Value).ToArray();

    private static MenuEntrySpec Item(string? submenu, string target) => new(submenu, MenuEntryKind.MenuItem, target);

    [Fact]
    public void The_same_item_in_two_submenus_gets_two_element_names()
    {
        var doc = NavigationScaffolder.Menu("ConMenu",
            entries: [Item("ConFirst", "ConVehicle"), Item("ConSecond", "ConVehicle"), Item(null, "ConVehicle")]);

        var names = ElementNames(doc);
        Assert.Equal(names.Length, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains("ConVehicle", names);
        Assert.Contains("ConSecond_ConVehicle", names);
        Assert.Equal(["ConVehicle", "ConVehicle", "ConVehicle"], Targets(doc));
    }

    [Fact]
    public void An_item_named_like_a_submenu_does_not_take_its_name()
    {
        var doc = NavigationScaffolder.Menu("ConMenu",
            entries: [Item("ConVehicle", "ConDriver"), Item("ConOther", "ConVehicle")]);

        var names = ElementNames(doc);
        Assert.Equal(names.Length, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        // A submenu is referred to by its plain name, so it is the item that changes.
        Assert.Equal("ConVehicle", doc.Descendants("AxMenuElement")
            .First(e => e.Elements("Elements").Any()).Element("Name")!.Value);
    }

    [Fact]
    public void A_menu_extension_keeps_its_element_names_unique_too()
    {
        var doc = NavigationScaffolder.MenuExtension("AccountsReceivable", "ConFleet",
            submenus: [new MenuSubmenuSpec("ConFirst", null), new MenuSubmenuSpec("ConSecond", null)],
            entries: [Item("ConFirst", "ConVehicle"), Item("ConSecond", "ConVehicle")]);

        var names = ElementNames(doc);
        Assert.Equal(names.Length, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Distinct_items_keep_their_own_names()
    {
        var doc = NavigationScaffolder.Menu("ConMenu", entries: [Item("ConFirst", "ConVehicle"), Item(null, "ConDriver")]);

        Assert.Equal(["ConFirst", "ConVehicle", "ConDriver"], ElementNames(doc));
    }
}
