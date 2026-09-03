namespace D365FO.Core.Scaffolding;

/// <summary>
/// The command-line spellings of menu content, parsed once for every surface that takes them:
/// <c>generate menu</c>, <c>generate extension Menu</c>, and both over MCP. A spelling that
/// parses differently on two surfaces is a defect the parity harness cannot see, so there is
/// one parser.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>submenu</c>: <c>[&lt;parent&gt;/]&lt;name&gt;[:&lt;label&gt;]</c></item>
/// <item><c>item</c>: <c>[&lt;submenu&gt;/]&lt;menuItem&gt;[:Display|Action|Output]</c></item>
/// <item><c>tile</c>: <c>[&lt;submenu&gt;/]&lt;tile&gt;</c></item>
/// <item><c>menuRef</c>: <c>[&lt;submenu&gt;/]&lt;menu&gt;</c></item>
/// </list>
/// </remarks>
public static class MenuSpecs
{
    public static bool TryParse(
        IEnumerable<string>? submenus, IEnumerable<string>? items, IEnumerable<string>? tiles, IEnumerable<string>? menuRefs,
        out List<MenuSubmenuSpec> submenuSpecs, out List<MenuEntrySpec> entries, out string? error)
    {
        submenuSpecs = [];
        entries = [];
        error = null;

        foreach (var raw in submenus ?? [])
        {
            var (parent, rest) = SplitPath(raw);
            var parts = rest.Split(':', 2, StringSplitOptions.TrimEntries);
            if (string.IsNullOrEmpty(parts[0]))
            {
                error = $"Invalid submenu '{raw}'. Expected [<parent>/]<name>[:<label>].";
                return false;
            }
            submenuSpecs.Add(new MenuSubmenuSpec(parts[0], parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null, parent));
        }
        foreach (var raw in items ?? [])
        {
            var (sub, rest) = SplitPath(raw);
            var parts = rest.Split(':', 2, StringSplitOptions.TrimEntries);
            if (string.IsNullOrEmpty(parts[0]))
            {
                error = $"Invalid item '{raw}'. Expected [<submenu>/]<menuItem>[:Display|Action|Output].";
                return false;
            }
            entries.Add(new MenuEntrySpec(sub, MenuEntryKind.MenuItem, parts[0], parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null));
        }
        foreach (var raw in tiles ?? [])
        {
            var (sub, target) = SplitPath(raw);
            if (string.IsNullOrEmpty(target)) { error = $"Invalid tile '{raw}'. Expected [<submenu>/]<tile>."; return false; }
            entries.Add(new MenuEntrySpec(sub, MenuEntryKind.Tile, target));
        }
        foreach (var raw in menuRefs ?? [])
        {
            var (sub, target) = SplitPath(raw);
            if (string.IsNullOrEmpty(target)) { error = $"Invalid menuRef '{raw}'. Expected [<submenu>/]<menu>."; return false; }
            entries.Add(new MenuEntrySpec(sub, MenuEntryKind.MenuReference, target));
        }
        return true;
    }

    /// <summary>The menu items the specs reference — the symbols a grounding gate should prove.</summary>
    public static IEnumerable<string> MenuItemsOf(IEnumerable<MenuEntrySpec> entries) =>
        entries.Where(e => e.Kind == MenuEntryKind.MenuItem).Select(e => e.Target).Distinct(StringComparer.OrdinalIgnoreCase);

    private static (string? Container, string Remainder) SplitPath(string raw)
    {
        var slash = raw.IndexOf('/');
        return slash < 0
            ? (null, raw.Trim())
            : (raw[..slash].Trim() is { Length: > 0 } s ? s : null, raw[(slash + 1)..].Trim());
    }
}
