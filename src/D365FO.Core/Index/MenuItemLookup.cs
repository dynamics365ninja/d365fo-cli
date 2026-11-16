namespace D365FO.Core.Index;

/// <summary>
/// "Which menu items open object X" — shared by <c>find menu-items</c> and the MCP
/// <c>search (type=menu-items)</c> route so both answer in the same shape.
/// </summary>
/// <remarks>
/// The question was answerable before only as <c>search any X --kind menuitem</c>, a substring
/// match over menu-item names AND objects that nobody reached for: agents asked a live ERP
/// server instead, and most of those calls timed out. This is an exact match on the menu item's
/// <c>Object</c>, across Display, Action and Output items.
/// </remarks>
public static class MenuItemLookup
{
    public static ToolResult<object> ForObject(MetadataRepository repo, string objectName)
    {
        if (string.IsNullOrWhiteSpace(objectName))
            return ToolResult<object>.Fail("BAD_INPUT", "Object name required.",
                "Pass the form, class or report the menu items open, e.g. `CustTable`.");

        var name = objectName.Trim();
        var items = repo.FindMenuItemsByObject(name);

        // An empty answer is only a fact when the object itself is known. Say what the index
        // holds under that name, so "no menu items" and "no such object" read differently.
        var indexedAs = repo.SymbolKinds(name);
        if (items.Count == 0 && indexedAs.Count == 0)
        {
            return ToolResult<object>.Success(new
            {
                @object = name,
                count = 0,
                items,
                objectIndexedAs = indexedAs,
                hint = $"No form, class, table or other object named '{name}' is in the index (reports are not "
                     + $"checked). Check the spelling with `search any {name}`.",
            });
        }

        return ToolResult<object>.Success(new
        {
            @object = name,
            count = items.Count,
            items,
            objectIndexedAs = indexedAs,
        });
    }
}
