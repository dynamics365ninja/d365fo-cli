using D365FO.Core.Scaffolding;

namespace D365FO.Core.Knowledge;

/// <summary>
/// The three curated pattern catalogs — table shapes, SSRS report recipes and warehouse-app
/// screen recipes — rendered once for every surface that asks for them.
/// </summary>
/// <remarks>
/// The catalogs themselves are <see cref="TablePatternPresets"/>, <see cref="ReportRecipes"/>
/// and <see cref="MobileAppRecipes"/>; what lives here is the answer shape — which fields of a
/// recipe are worth returning for a list versus a spec, and the notes that stop a caller
/// misreading one. That shape was written inside the CLI commands, so the MCP
/// <c>object_patterns</c> tool served only the form domain and answered the other three with
/// "not backed here, use the CLI" — for catalogs that are pure in-process data.
/// </remarks>
public static class PatternCatalogAnswers
{
    // ---------------------------------------------------------------- table

    public static ToolResult<object> TableList() =>
        ToolResult<object>.Success(new
        {
            count = Enum.GetValues<TablePattern>().Count(p => p != TablePattern.None),
            items = Enum.GetValues<TablePattern>()
                .Where(p => p != TablePattern.None)
                .Select(p => new
                {
                    pattern = p.ToString(),
                    tableGroup = TablePatternPresets.TableGroupFor(p),
                    whenToUse = TablePatternGuidance.WhenToUse(p),
                    defaultFields = TablePatternPresets.DefaultFieldsFor(p)
                        .Select(f => new { f.Name, edt = f.Edt, f.Mandatory }),
                }),
            storage = Enum.GetValues<TableStorage>().Select(s => new
            {
                storage = s.ToString(),
                tableType = TablePatternPresets.TableTypeFor(s),
                note = TablePatternGuidance.StorageNote(s),
            }),
            usage = "d365fo generate table <NAME> --pattern <PATTERN> [--table-type <STORAGE>]",
        });

    public static ToolResult<object> TableSpec(string pattern)
    {
        if (!TablePatternNormalizer.TryNormalize(pattern, out var resolved, out var error))
        {
            return ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                error ?? $"Unknown table pattern '{pattern}'.",
                "See them all with `d365fo table-pattern list`.");
        }

        if (resolved == TablePattern.None)
        {
            return ToolResult<object>.Success(new
            {
                pattern = "None",
                tableGroup = (string?)null,
                whenToUse = TablePatternGuidance.WhenToUse(resolved),
                defaultFields = Array.Empty<object>(),
                note = "No TableGroup is written at all, so the AOT default (Miscellaneous) applies. "
                     + "Pick a real pattern unless the table genuinely fits none of them.",
            });
        }

        var fields = TablePatternPresets.DefaultFieldsFor(resolved);
        return ToolResult<object>.Success(new
        {
            pattern = resolved.ToString(),
            tableGroup = TablePatternPresets.TableGroupFor(resolved),
            whenToUse = TablePatternGuidance.WhenToUse(resolved),
            defaultFields = fields.Select(f => new { f.Name, edt = f.Edt, f.Mandatory }),
            scaffoldCall = $"d365fo generate table <NAME> --pattern {resolved} --install-to <Model>",
            note = fields.Count > 0
                ? "The default fields are a starting point the scaffold writes when --field is not given; "
                  + "any --field you pass replaces them entirely rather than adding to them."
                : null,
        });
    }

    // --------------------------------------------------------------- report

    public static ToolResult<object> ReportList() =>
        ToolResult<object>.Success(new
        {
            count = ReportRecipes.List().Count,
            items = ReportRecipes.List().Select(r => new
            {
                r.Id,
                r.Title,
                r.WhenToUse,
                objects = r.Roster.Count,
                r.ScaffoldCall,
            }),
            note = "Unlike a form pattern there is no pattern XML to validate a report against, so these are "
                 + "recipes, not specs: an object roster, the base classes, one scaffold call, and the checks "
                 + "worth running. `report-pattern spec <id>` has the rest.",
        });

    public static ToolResult<object> ReportSpec(string id)
    {
        var recipe = ReportRecipes.Find(id);
        if (recipe is null)
        {
            return ToolResult<object>.Fail(D365FoErrorCodes.TopicNotFound,
                $"No report recipe called '{id}'.",
                $"Available: {string.Join(", ", ReportRecipes.Ids())}.");
        }

        return ToolResult<object>.Success(new
        {
            recipe.Id,
            recipe.Title,
            recipe.WhenToUse,
            roster = recipe.Roster.Select(o => new { o.Kind, o.Role, o.Extends, o.Naming }),
            recipe.ScaffoldCall,
            methodGuidance = recipe.MethodGuidance,
            checks = recipe.Checks,
            referenceObjects = recipe.ReferenceObjects.Count > 0 ? recipe.ReferenceObjects : null,
            readTheReference = recipe.ReferenceObjects.Count > 0
                ? $"Shipped objects of this exact shape — read one with `d365fo read class {recipe.ReferenceObjects[0]}` "
                  + "rather than working from this description alone."
                : null,
        });
    }

    // --------------------------------------------------------- warehouse app

    public static ToolResult<object> MobileList(string? framework = null)
    {
        var recipes = MobileAppRecipes.List().AsEnumerable();
        if (!string.IsNullOrWhiteSpace(framework))
        {
            var wanted = framework!.Replace("-", "", StringComparison.Ordinal);
            if (!Enum.TryParse<MobileFramework>(wanted, ignoreCase: true, out var parsed))
            {
                return ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                    $"Unknown framework '{framework}'.",
                    "Use process-guide, work-execute-display or configuration.");
            }
            recipes = recipes.Where(r => r.Framework == parsed);
        }

        var items = recipes.ToList();
        return ToolResult<object>.Success(new
        {
            decideFirst = MobileAppRecipes.FrameworkDecision,
            count = items.Count,
            items = items.Select(r => new
            {
                r.Id,
                r.Title,
                framework = r.Framework.ToString(),
                r.WhenToUse,
                classes = r.Roster.Count,
            }),
        });
    }

    public static ToolResult<object> MobileSpec(string id)
    {
        var recipe = MobileAppRecipes.Find(id);
        if (recipe is null)
        {
            return ToolResult<object>.Fail(D365FoErrorCodes.TopicNotFound,
                $"No warehouse-app recipe called '{id}'.",
                $"Available: {string.Join(", ", MobileAppRecipes.Ids())}.");
        }

        return ToolResult<object>.Success(new
        {
            recipe.Id,
            recipe.Title,
            framework = recipe.Framework.ToString(),
            recipe.WhenToUse,
            roster = recipe.Roster.Select(o => new { o.Role, o.Extends, o.Naming, o.Required }),
            guidance = recipe.Guidance,
            checks = recipe.Checks,
            referenceObjects = recipe.ReferenceObjects.Count > 0 ? recipe.ReferenceObjects : null,
            readTheReference = recipe.ReferenceObjects.Count > 0
                ? $"Shipped classes of this exact shape — read one with `d365fo read class {recipe.ReferenceObjects[0]}`."
                : null,
            note = recipe.Framework == MobileFramework.Configuration
                ? "This one is CONFIGURATION, not code. Writing a class here is the mistake the recipe exists to prevent."
                : null,
        });
    }
}

/// <summary>
/// What each table pattern is for, in the words a developer choosing between them needs.
/// </summary>
/// <remarks>
/// Deliberately separate from <see cref="TablePatternPresets"/>: the presets carry the TableGroup
/// and the field defaults, which are facts about the AOT. This is advice about which pattern to
/// pick, which is a different thing and should not be mistaken for metadata.
/// </remarks>
public static class TablePatternGuidance
{
    public static string WhenToUse(TablePattern pattern) => pattern switch
    {
        TablePattern.None =>
            "No group. Only when the table fits none of the others — the AOT default (Miscellaneous) then applies.",
        TablePattern.Main =>
            "The master data a module is about: customers, vendors, items. One row per real-world entity, "
            + "long-lived, referenced by transactions.",
        TablePattern.Transaction =>
            "Rows recording that something happened, keyed by date and voucher. Never edited in place after "
            + "posting; volume grows without bound.",
        TablePattern.Parameter =>
            "Module configuration — typically ONE row per company. Reached through a parameters form and a "
            + "find() that creates the row on first use.",
        TablePattern.Group =>
            "A classification other tables point at: customer groups, item groups. Small, hand-maintained, "
            + "referenced by a relation from the tables it groups.",
        TablePattern.WorksheetHeader =>
            "The header of a document being prepared before posting — a journal, an order. Owns the lines and "
            + "carries the status that decides whether they can still change.",
        TablePattern.WorksheetLine =>
            "The lines of such a document, related to the header and deleted with it.",
        TablePattern.Reference =>
            "Lookup data referenced by others but not owned by a module — country codes, units. Rarely changes.",
        TablePattern.Framework =>
            "Plumbing for a framework rather than business data: state, queues, mappings. Not something a user "
            + "browses.",
        TablePattern.Miscellaneous =>
            "The explicit 'none of the above'. Prefer a specific pattern; this one tells a later reader nothing.",
        _ => "No guidance recorded for this pattern.",
    };

    public static string StorageNote(TableStorage storage) => storage switch
    {
        TableStorage.RegularTable =>
            "Persisted, backed by a real SQL table. The default, and what almost every table wants.",
        TableStorage.TempDB =>
            "Per-session temporary table in tempdb. Survives across method calls within the session and can be "
            + "joined server-side — the right choice for a report's data set.",
        TableStorage.InMemory =>
            "Held in memory and spilled to a file when it grows. Cheap for small sets, but every row crosses the "
            + "tier boundary, so it is the wrong choice for anything a query should join.",
        _ => "",
    };
}
