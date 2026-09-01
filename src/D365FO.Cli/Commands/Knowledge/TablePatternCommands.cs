using D365FO.Core;
using D365FO.Core.Scaffolding;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Knowledge;

// `d365fo table-pattern` — the decision layer over `generate table`.
//
// The patterns, their canonical TableGroup and their default fields already existed, but only
// as an argument `generate table --pattern` accepts. An agent choosing a shape had no way to see
// what the choices ARE or what each one implies, so it either guessed a pattern name (and got a
// refusal listing valid values, one round trip wasted) or skipped --pattern entirely and got a
// table with no TableGroup at all.

/// <summary><c>d365fo table-pattern list</c> — every pattern, what it is for, what it presets.</summary>
public sealed class TablePatternListCommand : Command<TablePatternListCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);

        var items = Enum.GetValues<TablePattern>()
            .Where(p => p != TablePattern.None)
            .Select(p => new
            {
                pattern = p.ToString(),
                tableGroup = TablePatternPresets.TableGroupFor(p),
                whenToUse = TablePatternGuidance.WhenToUse(p),
                defaultFields = TablePatternPresets.DefaultFieldsFor(p)
                    .Select(f => new { f.Name, edt = f.Edt, f.Mandatory }),
            })
            .ToList();

        return RenderHelpers.Render(kind, ToolResult<object>.Success(new
        {
            count = items.Count,
            items,
            storage = Enum.GetValues<TableStorage>().Select(s => new
            {
                storage = s.ToString(),
                tableType = TablePatternPresets.TableTypeFor(s),
                note = TablePatternGuidance.StorageNote(s),
            }),
            usage = "d365fo generate table <NAME> --pattern <PATTERN> [--table-type <STORAGE>]",
        }));
    }
}

/// <summary><c>d365fo table-pattern spec</c> — one pattern in full.</summary>
public sealed class TablePatternSpecCommand : Command<TablePatternSpecCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<PATTERN>")]
        [System.ComponentModel.Description("Pattern name or a known alias — master, setup, config, transactional, worksheet-header, lookup, …")]
        public string Pattern { get; init; } = "";
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);

        if (!TablePatternNormalizer.TryNormalize(settings.Pattern, out var pattern, out var error))
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                error ?? $"Unknown table pattern '{settings.Pattern}'.",
                "See them all with `d365fo table-pattern list`."));
        }

        if (pattern == TablePattern.None)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Success(new
            {
                pattern = "None",
                tableGroup = (string?)null,
                whenToUse = TablePatternGuidance.WhenToUse(pattern),
                defaultFields = Array.Empty<object>(),
                note = "No TableGroup is written at all, so the AOT default (Miscellaneous) applies. "
                     + "Pick a real pattern unless the table genuinely fits none of them.",
            }));
        }

        var fields = TablePatternPresets.DefaultFieldsFor(pattern);
        return RenderHelpers.Render(kind, ToolResult<object>.Success(new
        {
            pattern = pattern.ToString(),
            tableGroup = TablePatternPresets.TableGroupFor(pattern),
            whenToUse = TablePatternGuidance.WhenToUse(pattern),
            defaultFields = fields.Select(f => new { f.Name, edt = f.Edt, f.Mandatory }),
            scaffoldCall = $"d365fo generate table <NAME> --pattern {pattern} --install-to <Model>",
            note = fields.Count > 0
                ? "The default fields are a starting point the scaffold writes when --field is not given; "
                  + "any --field you pass replaces them entirely rather than adding to them."
                : null,
        }));
    }
}

/// <summary>
/// What each pattern is for, in the words a developer choosing between them needs.
/// </summary>
/// <remarks>
/// Kept beside the CLI rather than in the scaffolder: the scaffolder needs the TableGroup and the
/// field defaults, which are facts about the AOT. This is advice about which to pick, which is a
/// different thing and should not be mistaken for metadata.
/// </remarks>
internal static class TablePatternGuidance
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
