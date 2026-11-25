using D365FO.Core;
using D365FO.Core.Index;
using Spectre.Console;
using Spectre.Console.Cli;
using System.Text.Json;

namespace D365FO.Cli.Commands.Lint;

/// <summary>
/// In-process Best-Practice gate. Corresponds to ROADMAP §7.1.
/// Categories today: <c>table-no-index</c>, <c>ext-named-not-attributed</c>,
/// <c>string-without-edt</c>. More will land as the index gains coverage
/// (e.g. UI literal strings once forms carry their label text).
/// </summary>
public sealed class LintCommand : Command<LintCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandOption("--category <NAMES>")]
        [System.ComponentModel.Description("Comma/semicolon-separated subset of: table-no-index, ext-named-not-attributed, string-without-edt, today-usage, do-insert-update, doc-comment-missing, nested-select, insert-in-loop, tts-try-catch, empty-table-method, batch-no-cango, force-literals, public-instance-field, cache-lookup-mismatch, missing-delete-action, no-alternate-key.")]
        public string? Category { get; init; }

        [CommandOption("--all-models")]
        [System.ComponentModel.Description("Include platform/ISV models. By default only IsCustom models are linted.")]
        public bool AllModels { get; init; }

        [CommandOption("--format <FMT>")]
        [System.ComponentModel.Description("Output shape: default envelope (json|table|raw) or 'sarif' for SARIF 2.1.0 (CI-friendly).")]
        public string? Format { get; init; }
    }

    private static readonly string[] All =
        { "table-no-index", "ext-named-not-attributed", "string-without-edt",
          "today-usage", "do-insert-update", "doc-comment-missing",
          "nested-select", "insert-in-loop", "tts-try-catch", "empty-table-method",
          "batch-no-cango", "force-literals", "public-instance-field",
          "cache-lookup-mismatch", "missing-delete-action", "no-alternate-key" };

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var repo = RepoFactory.Create();
        var categories = (settings.Category ?? "")
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToLowerInvariant())
            .ToArray();
        var run = categories.Length == 0 ? All : categories;
        var onlyCustom = !settings.AllModels;

        var hitsByCat = new Dictionary<string, IReadOnlyList<LintHit>>();
        foreach (var cat in run)
        {
            IReadOnlyList<LintHit> hits = cat switch
            {
                "table-no-index"          => repo.FindTablesWithoutIndex(onlyCustom),
                "ext-named-not-attributed" => repo.FindExtensionNamedButNotAttributed(onlyCustom),
                "string-without-edt"      => repo.FindStringFieldsWithoutEdt(onlyCustom),
                "today-usage"             => repo.FindTodayCallMethods(onlyCustom),
                "do-insert-update"        => repo.FindDoInsertOrUpdateMethods(onlyCustom),
                "doc-comment-missing"     => repo.FindMissingDocCommentMethods(onlyCustom),
                "nested-select"           => repo.FindNestedSelectMethods(onlyCustom),
                "insert-in-loop"          => repo.FindInsertInLoopMethods(onlyCustom),
                "tts-try-catch"           => repo.FindTtsTryCatchMethods(onlyCustom),
                "empty-table-method"      => repo.FindEmptyTableMethodOverrides(onlyCustom),
                "batch-no-cango"          => repo.FindRunBaseBatchWithoutCanGoBatch(onlyCustom),
                "force-literals"          => repo.FindForceLiteralsMethods(onlyCustom),
                "public-instance-field"   => repo.FindPublicInstanceFieldClasses(onlyCustom),
                "cache-lookup-mismatch"   => repo.FindCacheLookupMismatches(onlyCustom),
                "missing-delete-action"   => repo.FindMissingDeleteActions(onlyCustom),
                "no-alternate-key"        => repo.FindTablesWithoutAlternateKey(onlyCustom),
                _ => Array.Empty<LintHit>(),
            };
            hitsByCat[cat] = hits;
        }

        // SARIF 2.1.0 short-circuits the normal envelope: tooling (e.g. GitHub Code Scanning,
        // VS Code SARIF viewer) expects a raw SARIF document on stdout.
        if (string.Equals(settings.Format, "sarif", StringComparison.OrdinalIgnoreCase))
        {
            var sarif = BuildSarif(hitsByCat);
            Console.WriteLine(JsonSerializer.Serialize(sarif, new JsonSerializerOptions { WriteIndented = true }));
            return hitsByCat.Values.Sum(l => l.Count) == 0 ? 0 : 0; // BP findings are not a build failure by default
        }

        var sections = new List<object>();
        int total = 0;
        foreach (var cat in run)
        {
            var hits = hitsByCat[cat];
            total += hits.Count;
            sections.Add(new
            {
                category = cat,
                count = hits.Count,
                items = hits.Select(h => new { target = h.TargetName, model = h.Model, detail = h.Detail }),
            });
        }

        var result = ToolResult<object>.Success(new
        {
            onlyCustomModels = onlyCustom,
            categories = run,
            totalFindings = total,
            sections,
        });

        return RenderHelpers.Render(kind, result, _ =>
        {
            foreach (var s in run)
            {
                var count = ((dynamic)sections.First(x => ((dynamic)x).category == s)).count;
                var colour = count == 0 ? "green" : "yellow";
                AnsiConsole.MarkupLine($"[{colour}]{count}[/] [bold]{s}[/]");
            }
        });
    }

    private static readonly Dictionary<string, (string Level, string Name, string Help)> RuleMeta = new()
    {
        ["table-no-index"] = ("warning", "TableWithoutIndex",
            "Tables should have at least one cluster/alternate index for predictable query plans."),
        ["ext-named-not-attributed"] = ("error", "ExtensionClassMissingAttribute",
            "Classes named '*_Extension' must carry [ExtensionOf(...)] or CoC / event-handler attributes."),
        ["string-without-edt"] = ("warning", "StringFieldWithoutEdt",
            "String fields should use an Extended Data Type so they inherit length/label/help centrally."),
        ["today-usage"] = ("warning", "BPUpgradeCodeToday",
            "today() ignores the user time-zone. Use DateTimeUtil::getToday(DateTimeUtil::getUserPreferredTimeZone())."),
        ["do-insert-update"] = ("error", "DoInsertOrUpdateBypass",
            "doInsert()/doUpdate()/doDelete() bypass overridden table methods and framework validation. Reserved for data-fix/migration scripts."),
        ["doc-comment-missing"] = ("note", "BPXmlDocNoDocumentationComments",
            "Public/protected methods should carry a meaningful /// <summary> XML doc comment."),
        ["nested-select"] = ("warning", "NestedSelectStatement",
            "Methods containing two or more 'while select' loops may indicate a nested query that should be replaced with a joined query."),
        ["insert-in-loop"] = ("warning", "InsertInLoop",
            ".insert() called inside a loop can cause excessive database round-trips. Use RecordInsertList/set-based operations instead."),
        ["tts-try-catch"] = ("error", "TryCatchInsideTts",
            "try/catch inside a ttsbegin/ttscommit block can silently swallow transaction errors. Handle exceptions outside the transaction scope."),
        ["empty-table-method"] = ("note", "EmptyTableMethodOverride",
            "Table method override has an empty body. Either implement the override or remove it to avoid confusion."),
        ["batch-no-cango"] = ("warning", "RunBaseBatchMissingCanGoBatch",
            "Classes extending RunBaseBatch should override canGoBatch() to return true to enable batch scheduling."),
        ["force-literals"] = ("warning", "ForceLiteralsUsage",
            "forceLiterals bypasses query parameter safety and can cause SQL injection risks. Use forcePlaceholders instead."),
        ["public-instance-field"] = ("note", "PublicInstanceField",
            "Public instance fields violate encapsulation. Use private/protected fields with accessor methods."),
        ["cache-lookup-mismatch"] = ("note", "CacheLookupSet",
            "Table has a non-default CacheLookup setting. Verify it is appropriate for the table's usage pattern."),
        ["missing-delete-action"] = ("warning", "MissingDeleteAction",
            "Table relation without a delete action may leave orphaned records. Add a delete action (Cascade/Restricted/Cascade+Restricted)."),
        ["no-alternate-key"] = ("note", "UniqueIndexWithoutAlternateKey",
            "Table has a unique index but no AlternateKey index. AlternateKey enables the framework to use the index as a surrogate key."),
    };

    private static object BuildSarif(Dictionary<string, IReadOnlyList<LintHit>> hitsByCat)
    {
        var version = D365FO.Core.ProductVersion.Version;

        var rules = hitsByCat.Keys
            .Where(RuleMeta.ContainsKey)
            .Select(cat =>
            {
                var (level, name, help) = RuleMeta[cat];
                return new
                {
                    id = cat,
                    name,
                    shortDescription = new { text = name },
                    fullDescription = new { text = help },
                    defaultConfiguration = new { level },
                    helpUri = "https://github.com/dsg-tech/d365fo-cli/blob/main/docs/ROADMAP.md#7-code-quality--best-practices",
                };
            })
            .ToArray();

        var results = hitsByCat.SelectMany(kv => kv.Value.Select(h =>
        {
            var level = RuleMeta.TryGetValue(kv.Key, out var meta) ? meta.Level : "warning";
            return new
            {
                ruleId = kv.Key,
                level,
                message = new { text = $"{h.TargetName}: {h.Detail} (model: {h.Model})" },
                locations = new[]
                {
                    new
                    {
                        logicalLocations = new[]
                        {
                            new { name = h.TargetName, kind = "module" }
                        }
                    }
                },
            };
        })).ToArray();

        return new Dictionary<string, object?>
        {
            ["$schema"] = "https://raw.githubusercontent.com/oasis-tcs/sarif-spec/master/Schemata/sarif-schema-2.1.0.json",
            ["version"] = "2.1.0",
            ["runs"] = new[]
            {
                new
                {
                    tool = new
                    {
                        driver = new
                        {
                            name = "d365fo-cli-lint",
                            version,
                            informationUri = "https://github.com/dsg-tech/d365fo-cli",
                            rules,
                        }
                    },
                    results,
                }
            }
        };
    }
}
