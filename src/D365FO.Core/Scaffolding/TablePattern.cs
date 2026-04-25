namespace D365FO.Core.Scaffolding;

/// <summary>
/// Business-role presets for new tables, mirroring the upstream
/// <c>generate_smart_table</c> tool from <c>d365fo-mcp-server</c>. Each
/// pattern maps to a canonical D365FO <c>TableGroup</c> value plus a default
/// field skeleton when the caller does not supply <c>--field</c>.
/// </summary>
/// <remarks>
/// <para>
/// The values match the <c>TableGroup</c> system enum exactly so the scaffolder
/// can copy them verbatim into the generated XML. <see cref="None"/> means
/// "no pattern requested" — emit no <c>TableGroup</c> element and let the AOT
/// default (<c>Miscellaneous</c>) apply.
/// </para>
/// <para>
/// <c>TempDB</c> and <c>InMemory</c> are deliberately absent: they belong to
/// <see cref="TableStorage"/>, not to the business-role group. Passing them
/// through the pattern parser raises a <c>BAD_INPUT</c> error.
/// </para>
/// </remarks>
public enum TablePattern
{
    None,
    Main,
    Transaction,
    Parameter,
    Group,
    WorksheetHeader,
    WorksheetLine,
    Reference,
    Framework,
    Miscellaneous,
}

/// <summary>
/// Storage kinds for an <c>AxTable</c> — distinct from <see cref="TablePattern"/>.
/// Maps 1:1 to D365FO's <c>TableType</c> property.
/// </summary>
public enum TableStorage
{
    RegularTable,
    TempDB,
    InMemory,
}

/// <summary>
/// Normalises user-supplied pattern aliases (e.g. <c>"master"</c>,
/// <c>"setup"</c>, <c>"transactional"</c>) into a <see cref="TablePattern"/>.
/// </summary>
public static class TablePatternNormalizer
{
    public static bool TryNormalize(string? raw, out TablePattern pattern, out string? error)
    {
        pattern = TablePattern.None;
        error = null;
        if (string.IsNullOrWhiteSpace(raw)) return true;

        var token = new string(raw.Where(char.IsLetter).ToArray()).ToLowerInvariant();
        switch (token)
        {
            case "":
                return true;
            case "main":
            case "master":
                pattern = TablePattern.Main;
                return true;
            case "transaction":
            case "transactional":
            case "trans":
                pattern = TablePattern.Transaction;
                return true;
            case "parameter":
            case "parameters":
            case "setup":
            case "config":
            case "configuration":
                pattern = TablePattern.Parameter;
                return true;
            case "group":
            case "category":
                pattern = TablePattern.Group;
                return true;
            case "worksheetheader":
            case "header":
                pattern = TablePattern.WorksheetHeader;
                return true;
            case "worksheetline":
            case "line":
            case "lines":
                pattern = TablePattern.WorksheetLine;
                return true;
            case "reference":
            case "lookup":
            case "ref":
                pattern = TablePattern.Reference;
                return true;
            case "framework":
                pattern = TablePattern.Framework;
                return true;
            case "miscellaneous":
            case "misc":
                pattern = TablePattern.Miscellaneous;
                return true;

            // Common confusion — these belong to TableType, not TableGroup.
            case "tempdb":
            case "inmemory":
                error = $"'{raw}' is a TableType (storage kind), not a TablePattern. " +
                        "Pass --table-type TempDB (or InMemory) and keep --pattern empty or set to Main.";
                return false;
            default:
                error = $"Unknown table pattern '{raw}'. Valid: main|transaction|parameter|group|" +
                        "worksheetheader|worksheetline|reference|framework|miscellaneous.";
                return false;
        }
    }

    public static bool TryNormalizeStorage(string? raw, out TableStorage storage, out string? error)
    {
        storage = TableStorage.RegularTable;
        error = null;
        if (string.IsNullOrWhiteSpace(raw)) return true;

        var token = new string(raw.Where(char.IsLetter).ToArray()).ToLowerInvariant();
        switch (token)
        {
            case "regular":
            case "regulartable":
                storage = TableStorage.RegularTable;
                return true;
            case "tempdb":
            case "temp":
                storage = TableStorage.TempDB;
                return true;
            case "inmemory":
                storage = TableStorage.InMemory;
                return true;
            default:
                error = $"Unknown table type '{raw}'. Valid: RegularTable|TempDB|InMemory.";
                return false;
        }
    }
}

/// <summary>
/// Default field skeletons for each <see cref="TablePattern"/>. Used only when
/// the caller did not supply explicit fields. The skeletons are intentionally
/// short — they exist to give the user a compile-clean starting point, not to
/// pretend the table is "done".
/// </summary>
public static class TablePatternPresets
{
    /// <summary>Returns the canonical <c>TableGroup</c> value, or <c>null</c> for <see cref="TablePattern.None"/>.</summary>
    public static string? TableGroupFor(TablePattern p) => p switch
    {
        TablePattern.None            => null,
        TablePattern.Main            => "Main",
        TablePattern.Transaction     => "Transaction",
        TablePattern.Parameter       => "Parameter",
        TablePattern.Group           => "Group",
        TablePattern.WorksheetHeader => "WorksheetHeader",
        TablePattern.WorksheetLine   => "WorksheetLine",
        TablePattern.Reference       => "Reference",
        TablePattern.Framework       => "Framework",
        TablePattern.Miscellaneous   => "Miscellaneous",
        _                            => null,
    };

    public static string TableTypeFor(TableStorage s) => s switch
    {
        TableStorage.RegularTable => "Regular",
        TableStorage.TempDB       => "TempDB",
        TableStorage.InMemory     => "InMemory",
        _                         => "Regular",
    };

    /// <summary>
    /// Default field skeleton for a pattern. Never empty — even
    /// <see cref="TablePattern.None"/> returns a single <c>RecId</c> so the
    /// scaffold compiles. Skips defaults entirely when the caller already
    /// supplied at least one field.
    /// </summary>
    public static IReadOnlyList<TableFieldSpec> DefaultFieldsFor(TablePattern p) => p switch
    {
        TablePattern.Main => new[]
        {
            new TableFieldSpec("AccountNum",  "AccountNum",  null, true),
            new TableFieldSpec("Name",        "Name",        null, false),
            new TableFieldSpec("Description", "Description", null, false),
        },
        TablePattern.Transaction => new[]
        {
            new TableFieldSpec("AccountNum", "LedgerAccount", null, true),
            new TableFieldSpec("TransDate",  "TransDate",     null, true),
            new TableFieldSpec("Voucher",    "Voucher",       null, false),
            new TableFieldSpec("Amount",     "AmountMST",     null, false),
        },
        TablePattern.Parameter => new[]
        {
            new TableFieldSpec("Key",     "ParametersKey", null, true),
            new TableFieldSpec("Enabled", "NoYesId",       null, false),
        },
        TablePattern.Group => new[]
        {
            new TableFieldSpec("GroupId",     "Name",        null, true),
            new TableFieldSpec("Description", "Description", null, false),
        },
        TablePattern.WorksheetHeader => new[]
        {
            new TableFieldSpec("HeaderId",  "Num",       null, true),
            new TableFieldSpec("DocDate",   "TransDate", null, true),
            new TableFieldSpec("AccountNum","AccountNum",null, false),
        },
        TablePattern.WorksheetLine => new[]
        {
            new TableFieldSpec("HeaderId", "Num",       null, true),
            new TableFieldSpec("LineNum",  "LineNum",   null, true),
            new TableFieldSpec("Quantity", "Qty",       null, false),
            new TableFieldSpec("Amount",   "AmountMST", null, false),
        },
        TablePattern.Reference => new[]
        {
            new TableFieldSpec("Code",        "Name",        null, true),
            new TableFieldSpec("Description", "Description", null, false),
        },
        _ => Array.Empty<TableFieldSpec>(),
    };
}
