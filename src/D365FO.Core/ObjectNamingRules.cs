namespace D365FO.Core;

/// <summary>
/// Lightweight, index-free conventions check used by upstream parity tool
/// <c>validate_object_naming</c>. Rules follow Microsoft's
/// <see href="https://learn.microsoft.com/en-us/dynamics365/fin-ops-core/dev-itpro/dev-ref/xpp-standards">
/// X++ naming guidelines</see>.
/// Kinds understood: <c>Table</c>, <c>Class</c>, <c>Edt</c>, <c>Enum</c>,
/// <c>Form</c>, <c>View</c>, <c>Query</c>, <c>Report</c>, <c>Entity</c>,
/// <c>Service</c>, <c>MenuItem</c>, <c>TableExtension</c>, <c>FormExtension</c>,
/// <c>Coc</c>. Unknown kinds still get the PascalCase / length checks.
/// </summary>
public static class ObjectNamingRules
{
    public readonly record struct Violation(string Code, string Severity, string Message);

    public static IReadOnlyList<Violation> Validate(string kind, string name, string? prefix = null)
    {
        var v = new List<Violation>();
        if (string.IsNullOrWhiteSpace(name))
        {
            v.Add(new("EMPTY_NAME", "error", "Name is empty."));
            return v;
        }

        if (name.Length > 80)
            v.Add(new("NAME_TOO_LONG", "warn",
                $"Name is {name.Length} chars (soft limit 80, metadata XML filename hits FS limits above 160)."));

        foreach (var ch in name)
        {
            if (!(char.IsLetterOrDigit(ch) || ch == '_'))
            {
                v.Add(new("INVALID_CHARS", "error",
                    $"Character '{ch}' is not allowed. Only letters, digits and underscore are legal."));
                break;
            }
        }

        if (char.IsDigit(name[0]))
            v.Add(new("LEADS_WITH_DIGIT", "error", "Name must not start with a digit."));

        if (!char.IsUpper(name[0]) && name[0] != '_')
            v.Add(new("NOT_PASCAL_CASE", "warn", "Name should start with an uppercase letter (PascalCase)."));

        var kindNorm = (kind ?? "").Trim();
        switch (kindNorm)
        {
            case "TableExtension":
            case "FormExtension":
            case "EdtExtension":
            case "EnumExtension":
                if (!name.Contains(".", StringComparison.Ordinal) &&
                    !name.EndsWith("_Extension", StringComparison.Ordinal))
                    v.Add(new("EXTENSION_SUFFIX", "warn",
                        "Extension objects should either contain '.' (target.Suffix) or end with '_Extension'."));
                break;
            case "Coc":
            case "CocClass":
                if (!name.EndsWith("_Extension", StringComparison.Ordinal))
                    v.Add(new("COC_SUFFIX", "warn", "Chain-of-Command classes should end with '_Extension'."));
                break;
            case "Table":
                if (name.EndsWith("Table", StringComparison.Ordinal) && name.Length > 5 && char.IsLower(name[^6]))
                    v.Add(new("TABLE_SUFFIX", "info", "Tables conventionally carry the functional area prefix, e.g. CustTable."));
                break;
            case "Enum":
                if (name.StartsWith("Enum", StringComparison.Ordinal))
                    v.Add(new("ENUM_PREFIX_REDUNDANT", "info", "Avoid the redundant 'Enum' prefix on enum names."));
                break;
            case "Edt":
                // Convention: Extended Data Types rarely carry the 'Edt' suffix
                // but are fine either way — no hard rule.
                break;
            case "MenuItem":
            case "MenuItemDisplay":
            case "MenuItemAction":
            case "MenuItemOutput":
                break;
        }

        if (!string.IsNullOrEmpty(prefix) && !name.StartsWith(prefix, StringComparison.Ordinal))
            v.Add(new("MISSING_PUBLISHER_PREFIX", "warn",
                $"Name does not start with the expected prefix '{prefix}'. Custom ISV artefacts must use the publisher prefix to avoid collisions."));

        return v;
    }
}
