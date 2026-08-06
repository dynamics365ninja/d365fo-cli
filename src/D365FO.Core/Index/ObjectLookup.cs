namespace D365FO.Core.Index;

/// <summary>
/// Kind-dispatched object lookup shared by the CLI (<c>get object</c>,
/// <c>get batch</c>) and the MCP adapter (<c>get_object_info</c>,
/// <c>batch_get_info</c>). Returns the object payload, or an error code +
/// message when the kind is unsupported or the object is missing.
/// </summary>
public static class ObjectLookup
{
    public const string SupportedKindsHint =
        "Use class, table, edt, enum, form, menu-item, query, view, entity, report, service, service-group, role, duty, or privilege.";

    /// <summary>
    /// Lower-case the kind and strip separators: "menu-item" → "menuitem". Shares
    /// <see cref="D365FO.Core.ObjectTypes.ObjectTypeRegistry.NormalizeKind"/> so a kind
    /// string means the same thing to the reader, the writer and the bridge.
    /// </summary>
    public static string NormalizeKind(string value)
        => D365FO.Core.ObjectTypes.ObjectTypeRegistry.NormalizeKind(value);

    public static (object? Data, string? Code, string? Message) Fetch(MetadataRepository repo, string kind, string name)
    {
        return NormalizeKind(kind) switch
        {
            "class" => Wrap(repo.GetClassDetails(name), "CLASS_NOT_FOUND", $"Class '{name}' not found."),
            "table" => WrapTable(repo.GetTableDetails(name), name),
            "edt" => Wrap(repo.GetEdt(name), "EDT_NOT_FOUND", $"EDT '{name}' not found."),
            "enum" => Wrap(repo.GetEnum(name), "ENUM_NOT_FOUND", $"Enum '{name}' not found."),
            "form" => Wrap(repo.GetForm(name), "FORM_NOT_FOUND", $"Form '{name}' not found."),
            "menuitem" => Wrap(repo.GetMenuItem(name), "MENU_ITEM_NOT_FOUND", $"Menu item '{name}' not found."),
            "query" => Wrap(repo.GetQuery(name), "QUERY_NOT_FOUND", $"Query '{name}' not found."),
            "view" => Wrap(repo.GetView(name), "VIEW_NOT_FOUND", $"View '{name}' not found."),
            "entity" or "dataentity" => Wrap(repo.GetDataEntity(name), "ENTITY_NOT_FOUND", $"Data entity '{name}' not found."),
            "report" => Wrap(repo.GetReport(name), "REPORT_NOT_FOUND", $"Report '{name}' not found."),
            "service" => Wrap(repo.GetService(name), "SERVICE_NOT_FOUND", $"Service '{name}' not found."),
            "servicegroup" => Wrap(repo.GetServiceGroup(name), "SERVICE_GROUP_NOT_FOUND", $"Service group '{name}' not found."),
            "role" => Wrap(repo.GetSecurityRole(name), "ROLE_NOT_FOUND", $"Role '{name}' not found."),
            "duty" => Wrap(repo.GetSecurityDuty(name), "DUTY_NOT_FOUND", $"Duty '{name}' not found."),
            "privilege" => Wrap(repo.GetSecurityPrivilege(name), "PRIVILEGE_NOT_FOUND", $"Privilege '{name}' not found."),
            _ => (null, "BAD_INPUT", $"Unsupported object kind '{kind}'."),
        };
    }

    private static (object?, string?, string?) Wrap<T>(T? data, string code, string message) where T : class
        => data is null ? (null, code, message) : (data, null, null);

    private static (object?, string?, string?) WrapTable(TableDetails? details, string name)
        => details is null
            ? (null, "TABLE_NOT_FOUND", $"Table '{name}' not found in index.")
            : (new
            {
                table = details.Table,
                fields = details.Fields,
                relations = details.Relations,
                methods = details.Methods,
                indexes = details.Indexes,
                deleteActions = details.DeleteActions,
            }, null, null);
}
