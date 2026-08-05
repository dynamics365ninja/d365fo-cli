using D365FO.Core;
using D365FO.Core.Extract;
using D365FO.Core.Guardrails;
using D365FO.Core.Index;
using System.IO;

namespace D365FO.Mcp;

/// <summary>
/// Shared delegate surface used by the MCP transport to invoke the same core
/// operations that back the CLI. Every method returns a <see cref="ToolResult{T}"/>
/// so MCP tool handlers and CLI commands produce byte-identical envelopes.
/// </summary>
public sealed class ToolHandlers
{
    private readonly MetadataRepository _repo;

    public ToolHandlers(MetadataRepository repo) => _repo = repo;

    public ToolResult<object> SearchClasses(string query, string? model = null, int limit = 50)
    {
        var items = _repo.SearchClasses(query, model, limit);
        return ToolResult<object>.Success(new { count = items.Count, items });
    }

    public ToolResult<object> SearchTables(string query, string? model = null, int limit = 50)
    {
        var items = _repo.SearchTables(query, model, limit);
        return ToolResult<object>.Success(new { count = items.Count, items });
    }

    public ToolResult<object> SearchEdts(string query, int limit = 50)
    {
        var items = _repo.SearchEdts(query, limit);
        return ToolResult<object>.Success(new { count = items.Count, items });
    }

    public ToolResult<object> SearchEnums(string query, int limit = 50)
    {
        var items = _repo.SearchEnums(query, limit);
        return ToolResult<object>.Success(new { count = items.Count, items });
    }

    public ToolResult<object> GetTable(string name)
    {
        var t = _repo.GetTableDetails(name);
        return t is null
            ? ToolResult<object>.Fail("TABLE_NOT_FOUND", $"Table '{name}' not found.", "Run 'd365fo index build'.")
            : ToolResult<object>.Success(new { table = t.Table, fields = t.Fields, relations = t.Relations });
    }

    public ToolResult<object> GetEdt(string name)
    {
        var e = _repo.GetEdt(name);
        return e is null
            ? ToolResult<object>.Fail("EDT_NOT_FOUND", $"EDT '{name}' not found.")
            : ToolResult<object>.Success(e);
    }

    public ToolResult<object> GetClass(string name)
    {
        var c = _repo.GetClassDetails(name);
        return c is null
            ? ToolResult<object>.Fail("CLASS_NOT_FOUND", $"Class '{name}' not found.")
            : ToolResult<object>.Success(c);
    }

    public ToolResult<object> GetEnum(string name)
    {
        var e = _repo.GetEnum(name);
        return e is null
            ? ToolResult<object>.Fail("ENUM_NOT_FOUND", $"Enum '{name}' not found.")
            : ToolResult<object>.Success(e);
    }

    public ToolResult<object> GetMenuItem(string name)
    {
        var mi = _repo.GetMenuItem(name);
        return mi is null
            ? ToolResult<object>.Fail("MENU_ITEM_NOT_FOUND", $"Menu item '{name}' not found.")
            : ToolResult<object>.Success(mi);
    }

    public ToolResult<object> GetLabel(string file, string language, string key, bool raw = false)
    {
        var hit = _repo.GetLabel(file, language, key);
        if (hit is null)
            return ToolResult<object>.Fail("LABEL_NOT_FOUND", $"{file}/{language}:{key} not found.");
        if (!raw) hit = hit with { Value = StringSanitizer.Sanitize(hit.Value) };
        return ToolResult<object>.Success(hit);
    }

    public ToolResult<object> FindCoc(string targetClass, string? method = null)
    {
        var items = _repo.FindCocExtensions(targetClass, method);
        return ToolResult<object>.Success(new { count = items.Count, items });
    }

    public ToolResult<object> FindUsages(string symbol, int limit = 100)
    {
        var items = _repo.FindUsages(symbol, limit)
            .Select(t => new { kind = t.Kind, name = t.Name, model = t.Model })
            .ToList();
        return ToolResult<object>.Success(new { count = items.Count, items });
    }

    public ToolResult<object> SearchLabels(string query, string[]? langs = null, int limit = 100, bool raw = false)
    {
        var items = _repo.SearchLabels(query, langs, limit);
        if (!raw)
            items = items.Select(l => l with { Value = StringSanitizer.Sanitize(l.Value) }).ToList();
        return ToolResult<object>.Success(new { count = items.Count, items });
    }

    public ToolResult<object> SearchLabelsFts(string query, string[]? langs = null, int limit = 100, bool raw = false)
    {
        var items = _repo.SearchLabelsFts(query, langs, limit);
        if (!raw)
            items = items.Select(l => l with { Value = StringSanitizer.Sanitize(l.Value) }).ToList();
        return ToolResult<object>.Success(new { count = items.Count, items });
    }

    public ToolResult<object> GetSecurity(string obj, string type)
        => ToolResult<object>.Success(_repo.GetSecurityCoverage(obj, type));

    public ToolResult<object> GetTableRelations(string table)
    {
        var items = _repo.GetTableRelations(table);
        return ToolResult<object>.Success(new { count = items.Count, items });
    }

    public ToolResult<object> IndexStatus()
        => ToolResult<object>.Success(_repo.CountAll());

    // ---- Parity tools (forms / queries / views / entities / reports / services / workflows) ----

    public ToolResult<object> GetForm(string name)
    {
        var f = _repo.GetForm(name);
        return f is null
            ? ToolResult<object>.Fail("FORM_NOT_FOUND", $"Form '{name}' not found.")
            : ToolResult<object>.Success(f);
    }

    public ToolResult<object> SearchQueries(string query, int limit = 50)
    {
        var items = _repo.SearchQueries(query, limit);
        return ToolResult<object>.Success(new { count = items.Count, items });
    }

    public ToolResult<object> GetQuery(string name)
    {
        var q = _repo.GetQuery(name);
        return q is null
            ? ToolResult<object>.Fail("QUERY_NOT_FOUND", $"Query '{name}' not found.")
            : ToolResult<object>.Success(q);
    }

    public ToolResult<object> SearchViews(string query, int limit = 50)
    {
        var items = _repo.SearchViews(query, limit);
        return ToolResult<object>.Success(new { count = items.Count, items });
    }

    public ToolResult<object> GetView(string name)
    {
        var v = _repo.GetView(name);
        return v is null
            ? ToolResult<object>.Fail("VIEW_NOT_FOUND", $"View '{name}' not found.")
            : ToolResult<object>.Success(v);
    }

    public ToolResult<object> SearchDataEntities(string query, int limit = 50)
    {
        var items = _repo.SearchDataEntities(query, limit);
        return ToolResult<object>.Success(new { count = items.Count, items });
    }

    public ToolResult<object> GetDataEntity(string name)
    {
        var e = _repo.GetDataEntity(name);
        return e is null
            ? ToolResult<object>.Fail("ENTITY_NOT_FOUND", $"Data entity '{name}' not found.")
            : ToolResult<object>.Success(e);
    }

    public ToolResult<object> SearchReports(string query, int limit = 50)
    {
        var items = _repo.SearchReports(query, limit);
        return ToolResult<object>.Success(new { count = items.Count, items });
    }

    public ToolResult<object> GetReport(string name)
    {
        var r = _repo.GetReport(name);
        return r is null
            ? ToolResult<object>.Fail("REPORT_NOT_FOUND", $"Report '{name}' not found.")
            : ToolResult<object>.Success(r);
    }

    public ToolResult<object> SearchServices(string query, int limit = 50)
    {
        var items = _repo.SearchServices(query, limit);
        return ToolResult<object>.Success(new { count = items.Count, items });
    }

    public ToolResult<object> GetService(string name)
    {
        var s = _repo.GetService(name);
        return s is null
            ? ToolResult<object>.Fail("SERVICE_NOT_FOUND", $"Service '{name}' not found.")
            : ToolResult<object>.Success(s);
    }

    public ToolResult<object> GetServiceGroup(string name)
    {
        var g = _repo.GetServiceGroup(name);
        return g is null
            ? ToolResult<object>.Fail("SERVICE_GROUP_NOT_FOUND", $"Service group '{name}' not found.")
            : ToolResult<object>.Success(g);
    }

    public ToolResult<object> SearchWorkflowTypes(string query, int limit = 50)
    {
        var items = _repo.SearchWorkflowTypes(query, limit);
        return ToolResult<object>.Success(new { count = items.Count, items });
    }

    // ---- Security details ----

    public ToolResult<object> GetSecurityRole(string name)
    {
        var r = _repo.GetSecurityRole(name);
        return r is null
            ? ToolResult<object>.Fail("ROLE_NOT_FOUND", $"Role '{name}' not found.")
            : ToolResult<object>.Success(r);
    }

    public ToolResult<object> GetSecurityDuty(string name)
    {
        var d = _repo.GetSecurityDuty(name);
        return d is null
            ? ToolResult<object>.Fail("DUTY_NOT_FOUND", $"Duty '{name}' not found.")
            : ToolResult<object>.Success(d);
    }

    public ToolResult<object> GetSecurityPrivilege(string name)
    {
        var p = _repo.GetSecurityPrivilege(name);
        return p is null
            ? ToolResult<object>.Fail("PRIVILEGE_NOT_FOUND", $"Privilege '{name}' not found.")
            : ToolResult<object>.Success(p);
    }

    // ---- Models ----

    public ToolResult<object> ListModels()
    {
        var items = _repo.ListModels();
        return ToolResult<object>.Success(new { count = items.Count, items });
    }

    public ToolResult<object> GetModelDependencies(string name)
    {
        var deps = _repo.GetModelDependencies(name);
        return deps is null
            ? ToolResult<object>.Fail("MODEL_NOT_FOUND", $"Model '{name}' not found.")
            : ToolResult<object>.Success(deps);
    }

    // ---- Extensions / event subscribers ----

    public ToolResult<object> FindExtensions(string target, string? kind = null)
    {
        var items = _repo.FindExtensions(target, kind);
        return ToolResult<object>.Success(new { count = items.Count, items });
    }

    public ToolResult<object> FindEventSubscribers(string sourceObject, string? sourceKind = null)
    {
        var items = _repo.FindEventSubscribers(sourceObject, sourceKind);
        return ToolResult<object>.Success(new { count = items.Count, items });
    }

    // ---- Labels ----

    public ToolResult<object> ResolveLabel(string token, string[]? langs = null, bool raw = false)
    {
        var items = _repo.ResolveLabel(token, langs);
        if (!raw)
            items = items.Select(l => l with { Value = StringSanitizer.Sanitize(l.Value) }).ToList();
        return ToolResult<object>.Success(new { count = items.Count, items });
    }

    // ---- Table details pieces ----

    public ToolResult<object> GetTableMethods(string table)
    {
        var items = _repo.GetTableMethods(table);
        return ToolResult<object>.Success(new { count = items.Count, items });
    }

    public ToolResult<object> GetTableIndexes(string table)
    {
        var items = _repo.GetTableIndexes(table);
        return ToolResult<object>.Success(new { count = items.Count, items });
    }

    public ToolResult<object> GetTableDeleteActions(string table)
    {
        var items = _repo.GetTableDeleteActions(table);
        return ToolResult<object>.Success(new { count = items.Count, items });
    }

    // ---- Heuristics & workspace ----

    public ToolResult<object> SearchAny(string query, int limit = 100)
    {
        if (string.IsNullOrWhiteSpace(query))
            return ToolResult<object>.Fail("BAD_INPUT", "Query required.");
        var rows = _repo.FindUsages(query, limit)
            .Select(t => new { kind = t.Kind, name = t.Name, model = t.Model })
            .ToList();
        var byKind = rows.GroupBy(r => r.kind).ToDictionary(g => g.Key, g => g.Count());
        return ToolResult<object>.Success(new { count = rows.Count, byKind, items = rows });
    }

    public ToolResult<object> SuggestEdt(string fieldName, int limit = 5)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
            return ToolResult<object>.Fail("BAD_INPUT", "fieldName required.");
        var items = EdtSuggester.Suggest(_repo, fieldName, limit)
            .Select(s => new
            {
                name = s.Edt.Name,
                model = s.Edt.Model,
                extends = s.Edt.Extends,
                baseType = s.Edt.BaseType,
                stringSize = s.Edt.StringSize,
                confidence = s.Confidence,
                reason = s.Reason,
            })
            .ToList();
        return ToolResult<object>.Success(new { fieldName, count = items.Count, suggestions = items });
    }

    public ToolResult<object> GetWorkspaceInfo()
    {
        var cfg = D365FoSettings.FromEnvironment();
        return ToolResult<object>.Success(new
        {
            packagesPath = cfg.PackagesPath,
            workspacePath = cfg.WorkspacePath,
            databasePath = cfg.DatabasePath,
            databaseExists = File.Exists(cfg.DatabasePath),
            customModelPatterns = cfg.CustomModels,
            labelLanguages = cfg.LabelLanguages,
            hint = string.IsNullOrEmpty(cfg.PackagesPath)
                ? "Set D365FO_PACKAGES_PATH before calling `index extract`."
                : null,
        });
    }

    public ToolResult<object> Stats(int topN = 10)
    {
        var stats = _repo.GetStats(topN);
        var counts = _repo.CountAll();
        return ToolResult<object>.Success(new
        {
            totals = counts,
            perModel = stats.PerModel,
            topTables = stats.TopTables,
            topClasses = stats.TopClasses,
            topCocTargets = stats.TopCocTargets,
        });
    }

    public ToolResult<object> ValidateObjectNaming(string kind, string name, string? prefix = null)
    {
        var violations = ObjectNamingRules.Validate(kind, name, prefix);
        var hasError = violations.Any(v => v.Severity == "error");
        return ToolResult<object>.Success(new
        {
            objectKind = kind,
            name,
            prefix,
            ok = !hasError,
            count = violations.Count,
            violations = violations.Select(v => new { code = v.Code, severity = v.Severity, message = v.Message }),
        });
    }

    public ToolResult<object> GetTableExtensionInfo(string table)
    {
        var items = _repo.FindExtensions(table, "Table");
        return ToolResult<object>.Success(new
        {
            target = table,
            count = items.Count,
            extensions = items,
        });
    }

    public ToolResult<object> AnalyzeExtensionPoints(string target)
    {
        var extensions = _repo.FindExtensions(target);
        var handlers = _repo.FindEventSubscribers(target);
        var coc = _repo.FindCocExtensions(target);
        return ToolResult<object>.Success(new
        {
            target,
            extensions = new { count = extensions.Count, items = extensions },
            eventHandlers = new { count = handlers.Count, items = handlers },
            cocExtensions = new { count = coc.Count, items = coc },
            summary = new
            {
                extensionCount = extensions.Count,
                eventHandlerCount = handlers.Count,
                cocCount = coc.Count,
                suggestedStrategy = SuggestStrategy(extensions.Count, handlers.Count, coc.Count),
            },
        });
    }

    private static string SuggestStrategy(int extensions, int handlers, int coc)
    {
        if (coc > 0) return "Chain-of-Command — a CoC already targets this symbol, follow the established pattern.";
        if (handlers > 0) return "Event handler — add a SubscribesTo handler class in your model.";
        if (extensions > 0) return "Object extension — extend the target with a '<Target>.<Suffix>' .xml (see `d365fo generate extension`).";
        return "No existing extensions — prefer the least-invasive option: an event handler or a CoC on a virtual method.";
    }

    public ToolResult<object> BatchSearch(string[] queries, int limit = 50, string[]? kinds = null)
    {
        if (queries is null || queries.Length == 0)
            return ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "queries must be a non-empty array.");
        var results = new List<object>();
        foreach (var q in queries)
        {
            if (string.IsNullOrWhiteSpace(q)) continue;
            var hits = _repo.FindUsagesFiltered(q, kinds, limit)
                .Select(t => new { kind = t.Kind, name = t.Name, model = t.Model })
                .ToList();
            var byKind = hits.GroupBy(h => h.kind).ToDictionary(g => g.Key, g => g.Count());
            results.Add(new { query = q, count = hits.Count, byKind, items = hits });
        }
        return ToolResult<object>.Success(new { count = results.Count, kinds = kinds ?? ["all"], results });
    }

    public ToolResult<object> Lint(string[]? categories = null, bool onlyCustomModels = true)
    {
        var run = categories is { Length: > 0 }
            ? categories
            : new[] { "table-no-index", "ext-named-not-attributed", "string-without-edt" };
        var sections = new List<object>();
        int total = 0;
        foreach (var cat in run)
        {
            IReadOnlyList<LintHit> hits = cat.ToLowerInvariant() switch
            {
                "table-no-index" => _repo.FindTablesWithoutIndex(onlyCustomModels),
                "ext-named-not-attributed" => _repo.FindExtensionNamedButNotAttributed(onlyCustomModels),
                "string-without-edt" => _repo.FindStringFieldsWithoutEdt(onlyCustomModels),
                _ => Array.Empty<LintHit>(),
            };
            total += hits.Count;
            sections.Add(new { category = cat, count = hits.Count, items = hits });
        }
        return ToolResult<object>.Success(new
        {
            onlyCustomModels,
            categories = run,
            totalFindings = total,
            sections,
        });
    }

    // ---- label write-ops (ROADMAP §4.2) ----

    public ToolResult<object> CreateLabel(string? file, string key, string value, bool overwrite = false, string? installTo = null, string? lang = null, string? labelFile = null)
    {
        var resolvedFile = file;
        if (string.IsNullOrWhiteSpace(resolvedFile) && !string.IsNullOrWhiteSpace(installTo))
        {
            var resolvedLang = string.IsNullOrWhiteSpace(lang) ? "en-us" : lang!;
            resolvedFile = ResolveLabelPath(installTo!, resolvedLang, labelFile);
            if (resolvedFile is null)
                return ToolResult<object>.Fail("INSTALL_FAILED",
                    $"Cannot resolve label file path for model '{installTo}': neither D365FO_CUSTOM_PACKAGES_PATH nor D365FO_PACKAGES_PATH is set.",
                    "Set D365FO_CUSTOM_PACKAGES_PATH to your git repo PackagesLocalDirectory before using installTo.");
        }
        if (string.IsNullOrWhiteSpace(resolvedFile))
            return ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                "Either 'file' (absolute path to the .label.txt) or 'installTo' (model name) is required.",
                "Use 'file' for an explicit path, or 'installTo' to resolve the path automatically from D365FO_CUSTOM_PACKAGES_PATH or D365FO_PACKAGES_PATH.");
        if (string.IsNullOrWhiteSpace(key)) return ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "key required");
        try
        {
            var res = D365FO.Core.Labels.LabelFileWriter.CreateOrUpdate(resolvedFile!, key, value, overwrite);
            if (res.Outcome == D365FO.Core.Labels.WriteOutcome.KeyExists)
                return ToolResult<object>.Fail("KEY_EXISTS",
                    $"Label '{key}' already exists; pass overwrite=true to replace.",
                    hint: $"Existing value: {res.OldValue}");
            D365FO.Core.Journal.LabelJournalRecorder.RecordCreateOrUpdate(res, "labels(create)");
            return ToolResult<object>.Success(new
            {
                outcome = res.Outcome.ToString(),
                file = res.Path,
                key = res.Key,
                oldValue = res.OldValue,
                newValue = res.NewValue,
            });
        }
        catch (Exception ex)
        {
            return ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed, ex.Message);
        }
    }

    /// <summary>
    /// Bulk variant of <see cref="CreateLabel"/>: each entry (key + value) fans out
    /// through the normal single-label path with shared top-level fields. A failed
    /// entry is recorded but does not abort the batch — the report aggregates per-entry
    /// outcomes so a partial success is still actionable.
    /// </summary>
    public ToolResult<object> CreateLabels(
        IReadOnlyList<(string key, string value)> entries,
        string? file, bool overwrite = false,
        string? installTo = null, string? lang = null, string? labelFile = null)
    {
        if (entries is null || entries.Count == 0)
            return ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                "labels array is empty.",
                "Provide labels:[{key,value}, …], or use a single create with key+value.");

        var results = new List<object>(entries.Count);
        int created = 0, failed = 0;
        foreach (var (key, value) in entries)
        {
            var r = CreateLabel(file, key, value, overwrite, installTo, lang, labelFile);
            if (r.Ok) created++; else failed++;
            results.Add(new
            {
                key,
                ok = r.Ok,
                error = r.Error?.Code,
                message = r.Error?.Message,
            });
        }
        return ToolResult<object>.Success(new { total = entries.Count, created, failed, results });
    }

    public ToolResult<object> RenameLabel(string file, string oldKey, string newKey, bool overwrite = false)
    {
        if (string.IsNullOrWhiteSpace(file)) return ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "file required");
        if (string.IsNullOrWhiteSpace(oldKey) || string.IsNullOrWhiteSpace(newKey))
            return ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "oldKey and newKey required");
        try
        {
            var res = D365FO.Core.Labels.LabelFileWriter.Rename(file, oldKey, newKey, overwrite);
            D365FO.Core.Journal.LabelJournalRecorder.RecordRename(res, oldKey, "labels(rename)");
            return res.Outcome switch
            {
                D365FO.Core.Labels.WriteOutcome.FileMissing => ToolResult<object>.Fail("FILE_NOT_FOUND", $"Label file not found: {file}"),
                D365FO.Core.Labels.WriteOutcome.KeyMissing => ToolResult<object>.Fail("KEY_NOT_FOUND", $"Label '{oldKey}' not present."),
                D365FO.Core.Labels.WriteOutcome.KeyExists => ToolResult<object>.Fail("KEY_EXISTS", $"Target key '{newKey}' already exists."),
                _ => ToolResult<object>.Success(new
                {
                    outcome = res.Outcome.ToString(),
                    file = res.Path,
                    oldKey,
                    newKey,
                    value = res.NewValue,
                }),
            };
        }
        catch (Exception ex)
        {
            return ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed, ex.Message);
        }
    }

    public ToolResult<object> DeleteLabel(string file, string key)
    {
        if (string.IsNullOrWhiteSpace(file)) return ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "file required");
        if (string.IsNullOrWhiteSpace(key)) return ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "key required");
        try
        {
            var res = D365FO.Core.Labels.LabelFileWriter.Delete(file, key);
            D365FO.Core.Journal.LabelJournalRecorder.RecordDelete(res, "labels(delete)");
            return res.Outcome switch
            {
                D365FO.Core.Labels.WriteOutcome.FileMissing => ToolResult<object>.Fail("FILE_NOT_FOUND", $"Label file not found: {file}"),
                D365FO.Core.Labels.WriteOutcome.KeyMissing => ToolResult<object>.Fail("KEY_NOT_FOUND", $"Label '{key}' not present."),
                _ => ToolResult<object>.Success(new
                {
                    outcome = res.Outcome.ToString(),
                    file = res.Path,
                    key = res.Key,
                    removedValue = res.OldValue,
                }),
            };
        }
        catch (Exception ex)
        {
            return ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed, ex.Message);
        }
    }

    // ---- modification journal / undo (issue #113) ----------------------

    /// <summary>
    /// MCP parity for upstream <c>undo_last_modification</c>: revert the last
    /// <paramref name="steps"/> journal entries, replaying each in reverse through the same
    /// write path (disk or bridge) that produced it. See <c>d365fo undo</c> /
    /// <see cref="D365FO.Core.Journal.UndoEngine"/>.
    /// </summary>
    public ToolResult<object> UndoLastModification(int steps, bool dryRun)
    {
        var journal = D365FO.Core.Journal.ModificationJournal.ForIndex();
        if (journal.Count() == 0)
            return ToolResult<object>.Fail(D365FoErrorCodes.JournalEmpty,
                "The modification journal is empty — nothing to undo.",
                "Every write from `generate_object` and `labels` (create/rename/delete) appends an entry here.");

        var effectiveSteps = steps <= 0 ? 1 : steps;
        var result = D365FO.Core.Journal.UndoEngine.Undo(journal, effectiveSteps, dryRun);

        var touchedModels = result.Steps
            .Where(s => s.Ok && !string.IsNullOrWhiteSpace(s.Entry.Model))
            .Select(s => s.Entry.Model!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var warnings = new List<string>();
        if (!dryRun && touchedModels.Count > 0)
            warnings.Add($"Index not auto-refreshed for {string.Join(", ", touchedModels)} — run `update_symbol_index` so reverted objects are searchable again.");
        foreach (var rnr in result.Steps.Where(s => s.RnrProjWarning is not null).Select(s => s.RnrProjWarning!))
            warnings.Add(rnr);

        var payload = new
        {
            dryRun = result.DryRun,
            requestedSteps = effectiveSteps,
            reverted = result.Steps.Count(s => s.Ok),
            steps = result.Steps.Select(s => new
            {
                id = s.Entry.Id,
                timestampUtc = s.Entry.TimestampUtc,
                command = s.Entry.Command,
                targetType = s.Entry.TargetType.ToString(),
                kind = s.Entry.Kind,
                name = s.Entry.ObjectName,
                model = s.Entry.Model,
                operation = s.Entry.Operation.ToString(),
                writePath = s.Entry.WritePath.ToString(),
                target = s.Entry.TargetPath,
                ok = s.Ok,
                error = s.Error,
                detail = s.Detail,
            }),
        };

        if (!result.AllOk)
        {
            var failedStep = result.Steps.FirstOrDefault(s => !s.Ok);
            return ToolResult<object>.Fail(D365FoErrorCodes.UndoFailed,
                    $"Undo stopped after {result.Steps.Count(s => s.Ok)} of {effectiveSteps} step(s): {failedStep?.Error}",
                    "Earlier (older) journal entries were left untouched.")
                with { Data = payload };
        }

        return ToolResult<object>.Success(payload, warnings.Count > 0 ? warnings : null);
    }

    /// <summary>Inspect the modification-journal stack without reverting anything.</summary>
    public ToolResult<object> JournalList(int limit)
    {
        var journal = D365FO.Core.Journal.ModificationJournal.ForIndex();
        var effectiveLimit = limit <= 0 ? 50 : limit;
        var entries = journal.List(effectiveLimit);
        return ToolResult<object>.Success(new
        {
            journalDirectory = journal.JournalDirectory,
            count = entries.Count,
            totalCount = journal.Count(),
            entries = entries.Select(e => new
            {
                id = e.Id,
                timestampUtc = e.TimestampUtc,
                command = e.Command,
                targetType = e.TargetType.ToString(),
                kind = e.Kind,
                name = e.ObjectName,
                model = e.Model,
                operation = e.Operation.ToString(),
                writePath = e.WritePath.ToString(),
                target = e.TargetPath,
                hasPreImage = e.PreImage is not null,
            }),
        });
    }

    public ToolResult<object> IndexHistory(int limit, string? model)
    {
        var rows = _repo.GetExtractionRuns(limit <= 0 ? 50 : limit, string.IsNullOrWhiteSpace(model) ? null : model);
        return ToolResult<object>.Success(new
        {
            count = rows.Count,
            model,
            runs = rows.Select(r => new
            {
                runId = r.RunId,
                startedUtc = r.StartedUtc,
                model = r.Model,
                elapsedMs = r.ElapsedMs,
                tables = r.Tables,
                classes = r.Classes,
                edts = r.Edts,
                enums = r.Enums,
                labels = r.Labels,
                isCustom = r.IsCustom,
            }).ToArray(),
        });
    }

    public ToolResult<object> ModelsCoupling(int topN, bool onlyCycles)
    {
        var graph = _repo.GetDependencyGraph();
        var report = D365FO.Core.Analysis.CouplingAnalyzer.Analyse(graph);
        var top = onlyCycles
            ? Array.Empty<object>()
            : report.Nodes.Take(topN <= 0 ? 20 : topN).Select(n => new
            {
                name = n.Name,
                fanIn = n.FanIn,
                fanOut = n.FanOut,
                instability = Math.Round(n.Instability, 3),
            }).ToArray<object>();
        return ToolResult<object>.Success(new
        {
            modelCount = report.Nodes.Count,
            cycleCount = report.Cycles.Count,
            cycles = report.Cycles,
            top,
        });
    }

    // ---- search / get handlers ----

    public ToolResult<object> SearchBusinessEvents(string query, string? category, int limit)
    {
        var items = _repo.SearchBusinessEvents(query, category, limit);
        return ToolResult<object>.Success(new { count = items.Count, items });
    }

    public ToolResult<object> GetBusinessEvent(string name)
    {
        var e = _repo.GetBusinessEvent(name);
        return e is null
            ? ToolResult<object>.Fail("BUSINESS_EVENT_NOT_FOUND", $"Business event '{name}' not found.")
            : ToolResult<object>.Success(e);
    }

    public ToolResult<object> SearchSecurityPolicies(string query, int limit)
    {
        var items = _repo.SearchSecurityPolicies(query, limit);
        return ToolResult<object>.Success(new { count = items.Count, items });
    }

    public ToolResult<object> SearchConfigurationKeys(string query, int limit)
    {
        var items = _repo.SearchConfigurationKeys(query, limit);
        return ToolResult<object>.Success(new { count = items.Count, items });
    }

    public ToolResult<object> SearchTiles(string query, int limit)
    {
        var items = _repo.SearchTiles(query, limit);
        return ToolResult<object>.Success(new { count = items.Count, items });
    }

    public ToolResult<object> SearchWorkspaces(string query, int limit)
    {
        var items = _repo.SearchWorkspaces(query, limit);
        return ToolResult<object>.Success(new { count = items.Count, items });
    }

    // ---- integration analysis handlers ----

    public ToolResult<object> AnalyzeIntegration(string? model)
    {
        var issues = _repo.AnalyzeIntegration(model);
        return ToolResult<object>.Success(new { count = issues.Count, issues });
    }

    public ToolResult<object> ReportIntegrations(string? model)
    {
        var r = _repo.GetIntegrationReport(model);
        return ToolResult<object>.Success(new
        {
            odataEntities  = new { count = r.ODataEntities.Count,  items = r.ODataEntities },
            customServices = new { count = r.CustomServices.Count,  items = r.CustomServices },
            businessEvents = new { count = r.BusinessEvents.Count,  items = r.BusinessEvents },
            workflowTypes  = new { count = r.WorkflowTypes.Count,   items = r.WorkflowTypes },
            batchJobs      = new { count = r.BatchJobs.Count,       items = r.BatchJobs },
        });
    }

    // ---- developer experience handlers ----

    public ToolResult<object> AnalyzeImpact(string objectName)
    {
        var r = _repo.AnalyzeImpact(objectName);
        return ToolResult<object>.Success(new
        {
            objectName    = r.ObjectName,
            directCount   = r.CocWrappers.Count + r.EventHandlers.Count + r.Extensions.Count,
            indirectCount = r.FormDataSources.Count + r.DataEntities.Count + r.Queries.Count,
            direct   = new { cocWrappers = r.CocWrappers, eventHandlers = r.EventHandlers, extensions = r.Extensions },
            indirect = new { formDataSources = r.FormDataSources, dataEntities = r.DataEntities, queries = r.Queries },
        });
    }

    public ToolResult<object> FindBatchJobs(string? model)
    {
        var items = _repo.FindBatchJobs(model);
        return ToolResult<object>.Success(new { count = items.Count, items });
    }

    // ---- scaffolding handlers (return XML as string) ----

    public ToolResult<object> GenerateEdt(string name, string? extends, string? label, int size)
    {
        if (string.IsNullOrWhiteSpace(name))
            return ToolResult<object>.Fail("BAD_INPUT", "name is required.");
        var doc = D365FO.Core.Scaffolding.XppScaffolder.Edt(name, extends, null, size > 0 ? size : null, label);
        return ToolResult<object>.Success(new { name, xml = doc.ToString() });
    }

    public ToolResult<object> GenerateEnum(string name, string? label, string[]? values)
    {
        if (string.IsNullOrWhiteSpace(name))
            return ToolResult<object>.Fail("BAD_INPUT", "name is required.");
        var vals = values?.Select((v, i) => new D365FO.Core.Scaffolding.EnumValueSpec(v, i, null)).ToList()
                   ?? new List<D365FO.Core.Scaffolding.EnumValueSpec>();
        var doc = D365FO.Core.Scaffolding.XppScaffolder.Enum(name, vals, label: label);
        return ToolResult<object>.Success(new { name, xml = doc.ToString() });
    }

    public ToolResult<object> GenerateQuery(string name, string rootTable, string? label)
    {
        if (string.IsNullOrWhiteSpace(name))
            return ToolResult<object>.Fail("BAD_INPUT", "name is required.");
        if (string.IsNullOrWhiteSpace(rootTable))
            return ToolResult<object>.Fail("BAD_INPUT", "rootTable is required.");
        var ds = new[] { new D365FO.Core.Scaffolding.QueryDataSourceSpec(rootTable) };
        var doc = D365FO.Core.Scaffolding.QueryScaffolder.Query(name, ds);
        return ToolResult<object>.Success(new { name, xml = doc.ToString() });
    }

    public ToolResult<object> GenerateSysOperation(string name, string executionMode)
    {
        if (string.IsNullOrWhiteSpace(name))
            return ToolResult<object>.Fail("BAD_INPUT", "name is required.");
        var mode = Enum.TryParse<D365FO.Core.Scaffolding.SysOperationExecutionMode>(executionMode, true, out var m)
            ? m : D365FO.Core.Scaffolding.SysOperationExecutionMode.Synchronous;
        var contractName   = name + "Contract";
        var serviceName    = name + "Service";
        var controllerName = name + "Controller";
        var serviceMethod  = "process";
        var contract   = D365FO.Core.Scaffolding.SysOperationScaffolder.Contract(contractName);
        var service    = D365FO.Core.Scaffolding.SysOperationScaffolder.Service(serviceName, contractName, serviceMethod);
        var controller = D365FO.Core.Scaffolding.SysOperationScaffolder.Controller(controllerName, serviceName, serviceMethod, mode);
        return ToolResult<object>.Success(new
        {
            name,
            contract   = new { name = contractName,   xml = contract.ToString() },
            service    = new { name = serviceName,     xml = service.ToString() },
            controller = new { name = controllerName,  xml = controller.ToString() },
        });
    }

    public ToolResult<object> GenerateBusinessEvent(string name, string? contractName, string category)
    {
        if (string.IsNullOrWhiteSpace(name))
            return ToolResult<object>.Fail("BAD_INPUT", "name is required.");
        var cn = string.IsNullOrWhiteSpace(contractName) ? name + "Contract" : contractName!;
        var eventDoc    = D365FO.Core.Scaffolding.BusinessEventScaffolder.EventClass(name, cn, category, null);
        var contractDoc = D365FO.Core.Scaffolding.BusinessEventScaffolder.ContractClass(cn, new List<D365FO.Core.Scaffolding.PayloadSpec>());
        return ToolResult<object>.Success(new
        {
            name,
            @event   = new { name, xml = eventDoc.ToString() },
            contract = new { name = cn, xml = contractDoc.ToString() },
        });
    }

    public ToolResult<object> GenerateRunBase(string name, bool batch)
    {
        if (string.IsNullOrWhiteSpace(name))
            return ToolResult<object>.Fail("BAD_INPUT", "name is required.");
        var doc = D365FO.Core.Scaffolding.RunBaseScaffolder.RunBaseClass(name, batch);
        return ToolResult<object>.Success(new { name, isBatch = batch, xml = doc.ToString() });
    }

    public ToolResult<object> GenerateSecurityPolicy(string name, string constrainedTable, string? policyQuery)
    {
        if (string.IsNullOrWhiteSpace(name))
            return ToolResult<object>.Fail("BAD_INPUT", "name is required.");
        if (string.IsNullOrWhiteSpace(constrainedTable))
            return ToolResult<object>.Fail("BAD_INPUT", "constrainedTable is required.");
        var pq = string.IsNullOrWhiteSpace(policyQuery) ? name + "Query" : policyQuery!;
        var doc = D365FO.Core.Scaffolding.SecurityPolicyScaffolder.Policy(name, constrainedTable, pq);
        return ToolResult<object>.Success(new { name, xml = doc.ToString() });
    }

    // ---- write-to-disk scaffolding handlers ----

    /// <summary>
    /// Resolve the canonical AOT install path:
    /// <c>&lt;root&gt;/&lt;model&gt;/&lt;model&gt;/&lt;axSubfolder&gt;/&lt;name&gt;.xml</c>.
    /// Searches <c>D365FO_CUSTOM_PACKAGES_PATH</c> first (write target), then
    /// <c>D365FO_PACKAGES_PATH</c>. Returns <c>null</c> when neither is set.
    /// </summary>
    private static string? ResolveAotPath(string model, string axSubfolder, string name)
    {
        var cfg = D365FoSettings.FromEnvironment();
        // Prefer custom paths (git repo / write target); fall back to standard path.
        var candidates = cfg.CustomPackagesPaths.Concat(new[] { cfg.PackagesPath });
        foreach (var root in candidates)
        {
            if (string.IsNullOrEmpty(root)) continue;
            if (Directory.Exists(Path.Combine(root, model, model)))
                return Path.Combine(root, model, model, axSubfolder, name + ".xml");
        }
        // Model not found — default to first custom path, then standard for new model creation.
        var writeRoot = cfg.CustomPackagesPaths.FirstOrDefault() ?? cfg.PackagesPath;
        return writeRoot is null ? null : Path.Combine(writeRoot, model, model, axSubfolder, name + ".xml");
    }

    /// <summary>
    /// Resolve the canonical label file path:
    /// <c>&lt;root&gt;/&lt;model&gt;/&lt;model&gt;/AxLabelFile/LabelResources/&lt;lang&gt;/&lt;labelFile&gt;.&lt;lang&gt;.label.txt</c>.
    /// Searches <c>D365FO_CUSTOM_PACKAGES_PATH</c> first (write target), then
    /// <c>D365FO_PACKAGES_PATH</c>. Returns <c>null</c> when neither is set.
    /// </summary>
    private static string? ResolveLabelPath(string model, string lang, string? labelFile)
    {
        var cfg = D365FoSettings.FromEnvironment();
        var lf = string.IsNullOrWhiteSpace(labelFile) ? model : labelFile!;
        var candidates = cfg.CustomPackagesPaths.Concat(new[] { cfg.PackagesPath });
        foreach (var root in candidates)
        {
            if (string.IsNullOrEmpty(root)) continue;
            if (Directory.Exists(Path.Combine(root, model, model)))
                return Path.Combine(root, model, model, "AxLabelFile", "LabelResources", lang, $"{lf}.{lang}.label.txt");
        }
        var writeRoot = cfg.CustomPackagesPaths.FirstOrDefault() ?? cfg.PackagesPath;
        return writeRoot is null ? null : Path.Combine(writeRoot, model, model, "AxLabelFile", "LabelResources", lang, $"{lf}.{lang}.label.txt");
    }

    private static ToolResult<object> WriteScaffold(
        System.Xml.Linq.XDocument doc, string name, string kind, string axSubfolder,
        string? installTo, string? outPath, bool overwrite, object extra)
    {
        var path = outPath;
        if (string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(installTo))
        {
            path = ResolveAotPath(installTo!, axSubfolder, name);
            if (path is null)
                return ToolResult<object>.Fail("INSTALL_FAILED",
                    $"Cannot resolve install path for model '{installTo}': neither D365FO_CUSTOM_PACKAGES_PATH nor D365FO_PACKAGES_PATH is set.",
                    "Set D365FO_CUSTOM_PACKAGES_PATH to your git repo PackagesLocalDirectory before using installTo.");
        }
        if (string.IsNullOrWhiteSpace(path))
            return ToolResult<object>.Fail("BAD_INPUT", "Either 'out' or 'installTo' is required.");

        try
        {
            var res = D365FO.Core.Scaffolding.ScaffoldFileWriter.Write(doc, path!, overwrite);
            return ToolResult<object>.Success(new
            {
                kind,
                name,
                path = res.Path,
                bytes = res.Bytes,
                backup = res.BackupPath,
                model = installTo,
                extra,
            });
        }
        catch (Exception ex)
        {
            return ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed, ex.Message);
        }
    }

    private static ToolResult<object> WriteScaffoldString(
        string xml, string name, string kind, string axSubfolder,
        string? installTo, string? outPath, bool overwrite, object extra)
    {
        var path = outPath;
        if (string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(installTo))
        {
            path = ResolveAotPath(installTo!, axSubfolder, name);
            if (path is null)
                return ToolResult<object>.Fail("INSTALL_FAILED",
                    $"Cannot resolve install path for model '{installTo}': neither D365FO_CUSTOM_PACKAGES_PATH nor D365FO_PACKAGES_PATH is set.",
                    "Set D365FO_CUSTOM_PACKAGES_PATH to your git repo PackagesLocalDirectory before using installTo.");
        }
        if (string.IsNullOrWhiteSpace(path))
            return ToolResult<object>.Fail("BAD_INPUT", "Either 'out' or 'installTo' is required.");

        try
        {
            var res = D365FO.Core.Scaffolding.ScaffoldFileWriter.Write(xml, path!, overwrite);
            return ToolResult<object>.Success(new
            {
                kind,
                name,
                path = res.Path,
                bytes = res.Bytes,
                backup = res.BackupPath,
                model = installTo,
                extra,
            });
        }
        catch (Exception ex)
        {
            return ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed, ex.Message);
        }
    }

    private static D365FO.Core.Scaffolding.TableFieldSpec ParseTableField(string raw)
    {
        var parts = raw.Split(':', StringSplitOptions.TrimEntries);
        var fname = parts.Length > 0 ? parts[0] : "";
        var edt   = parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]) ? parts[1] : null;
        var mand  = parts.Length > 2 && string.Equals(parts[2], "mandatory", StringComparison.OrdinalIgnoreCase);
        return new D365FO.Core.Scaffolding.TableFieldSpec(fname, edt, null, mand);
    }

    public ToolResult<object> GenerateTable(
        string name, string? label, string[]? fields, string? pattern,
        string? installTo, string? outPath, bool overwrite)
    {
        if (string.IsNullOrWhiteSpace(name))
            return ToolResult<object>.Fail("BAD_INPUT", "name is required.");

        if (!D365FO.Core.Scaffolding.TablePatternNormalizer.TryNormalize(pattern, out var pat, out var perr))
            return ToolResult<object>.Fail("BAD_INPUT", perr!);

        var fieldSpecs = (fields ?? Array.Empty<string>()).Select(ParseTableField).ToList();

        // Guardrail: warn if table already exists in index.
        var warnings = new List<string>();
        try
        {
            var existing = _repo.GetTableDetails(name);
            if (existing is not null)
                warnings.Add($"Table '{name}' already exists in the index (model: {existing.Table.Model}). Use overwrite=true to replace.");
        }
        catch { /* index may not contain the table — not fatal */ }

        // Resolve each field's EDT base type from the index so the scaffold
        // stamps the concrete i:type discriminator on every <AxTableField>
        // (AxTableField is abstract — without it the table is invalid). See issue #91.
        Func<string, string?> edtResolver = edt =>
        {
            if (string.IsNullOrWhiteSpace(edt)) return null;
            try { return _repo.GetEdt(edt)?.BaseType; }
            catch { return null; }
        };
        var doc = D365FO.Core.Scaffolding.XppScaffolder.Table(name, label, fieldSpecs, pat,
            D365FO.Core.Scaffolding.TableStorage.RegularTable, null, edtResolver);
        var extra = new { fieldCount = fieldSpecs.Count > 0 ? fieldSpecs.Count : (int?)null, pattern = pat == D365FO.Core.Scaffolding.TablePattern.None ? null : pat.ToString() };
        return WriteScaffold(doc, name, "AxTable", "AxTable", installTo, outPath, overwrite, extra);
    }

    /// <summary>
    /// Structured method-level modify via D365FO.Bridge (issue #112) — parity with
    /// upstream's <c>d365fo_file(action=modify)</c>. No on-disk fallback: fails
    /// <c>BRIDGE_REQUIRED</c> when the bridge is unavailable.
    /// </summary>
    public ToolResult<object> ModifyMethod(
        string kind, string name, string method, string body,
        string? model = null, string? groundingToken = null)
    {
        var request = new D365FO.Core.Bridge.MethodModifyEngine.ModifyRequest(
            kind, name, method, body, model, groundingToken);
        return D365FO.Core.Bridge.MethodModifyEngine.Modify(request, _repo);
    }

    public ToolResult<object> GenerateClass(
        string name, string? extends, bool nonFinal,
        string? installTo, string? outPath, bool overwrite)
    {
        if (string.IsNullOrWhiteSpace(name))
            return ToolResult<object>.Fail("BAD_INPUT", "name is required.");
        var doc = D365FO.Core.Scaffolding.XppScaffolder.Class(name, extends, !nonFinal);
        return WriteScaffold(doc, name, "AxClass", "AxClass", installTo, outPath, overwrite, new { extends });
    }

    public ToolResult<object> GenerateCoc(
        string target, string[] methods,
        string? installTo, string? outPath, bool overwrite)
    {
        if (string.IsNullOrWhiteSpace(target))
            return ToolResult<object>.Fail("BAD_INPUT", "target is required.");
        if (methods is null || methods.Length == 0)
            return ToolResult<object>.Fail("BAD_INPUT", "At least one method is required.");

        // Guardrail: warn if CoC wrappers already exist.
        var warnings = new List<string>();
        try
        {
            var existing = _repo.FindCocExtensions(target);
            if (existing.Count > 0)
                warnings.Add($"There are already {existing.Count} CoC extension(s) of '{target}'. Consider extending an existing one instead.");
        }
        catch { /* not fatal */ }

        var extensionName = target + "_Extension";
        var doc = D365FO.Core.Scaffolding.XppScaffolder.CocExtension(target, methods);
        var result = WriteScaffold(doc, extensionName, "AxClass", "AxClass", installTo, outPath, overwrite,
            new { target, methodCount = methods.Length });

        // Attach warnings to a successful result envelope.
        if (warnings.Count > 0 && result.Ok)
        {
            return ToolResult<object>.Success(new
            {
                kind = "AxClass",
                name = extensionName,
                target,
                methodCount = methods.Length,
                model = installTo,
                warnings,
            });
        }
        return result;
    }

    public ToolResult<object> GenerateForm(
        string name, string? table, string? pattern, string? caption,
        string[]? fields, string? linesTable,
        string? installTo, string? outPath, bool overwrite)
    {
        if (string.IsNullOrWhiteSpace(name))
            return ToolResult<object>.Fail("BAD_INPUT", "name is required.");

        var fp = D365FO.Core.Scaffolding.FormPatternNormalizer.Normalize(pattern);

        var xml = D365FO.Core.Scaffolding.XppScaffolder.Form(
            name, table, fp, caption,
            fields ?? Array.Empty<string>(),
            Array.Empty<D365FO.Core.Scaffolding.FormSectionSpec>(), linesTable);

        // Pre-write pattern self-test — same gate as `d365fo generate form`.
        var report = D365FO.Core.FormPatterns.FormPatternValidator.ValidateXml(xml);
        if (report.HasErrors && FormPatternEnforced())
        {
            var errors = report.Violations.Where(v => v.Severity == "error")
                .Select(v => $"{v.Rule} {v.Path}: {v.Excerpt} → {v.Fix}");
            return ToolResult<object>.Fail("FORM_PATTERN_VIOLATION",
                $"Generated form violates pattern {report.Pattern} (D365FO_FORM_PATTERN_ENFORCE=true):\n" +
                string.Join("\n", errors),
                "Fix the structure (see object_patterns domain=form action=spec), or set D365FO_FORM_PATTERN_ENFORCE=false to bypass the gate.");
        }

        return WriteScaffoldString(xml, name, "AxForm", "AxForm", installTo, outPath, overwrite,
            new { table, pattern = fp.ToString() });
    }

    private static bool FormPatternEnforced() =>
        !string.Equals(D365FoSettings.Resolve("D365FO_FORM_PATTERN_ENFORCE"), "false", StringComparison.OrdinalIgnoreCase);

    /// <summary>Up to 10 objects per call — parity with upstream <c>batch_get_info</c>.</summary>
    public ToolResult<object> BatchGetInfo(string[]? objects)
    {
        if (objects is null || objects.Length == 0)
            return ToolResult<object>.Fail("BAD_INPUT", "objects is required: array of \"<kind>:<name>\" specs.");
        if (objects.Length > 10)
            return ToolResult<object>.Fail("BAD_INPUT", $"Too many objects: {objects.Length} (max 10 per call).");

        var items = new List<object>(objects.Length);
        var found = 0;
        foreach (var spec in objects)
        {
            var idx = spec.IndexOf(':');
            if (idx <= 0 || idx == spec.Length - 1)
            {
                items.Add(new { spec, ok = false, error = new { code = "BAD_INPUT", message = $"Spec '{spec}' is not <kind>:<name>." } });
                continue;
            }
            var kind = spec[..idx].Trim();
            var name = spec[(idx + 1)..].Trim();
            var (data, code, message) = ObjectLookup.Fetch(_repo, kind, name);
            if (data is null)
            {
                items.Add(new { spec, kind, name, ok = false, error = new { code, message } });
            }
            else
            {
                found++;
                items.Add(new { spec, kind, name, ok = true, data });
            }
        }
        return ToolResult<object>.Success(new { requested = objects.Length, found, items });
    }

    public ToolResult<object> GetFormPatternSpec(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return ToolResult<object>.Success(new
            {
                patterns = D365FO.Core.FormPatterns.FormPatternCatalog.Patterns
                    .Select(p => new { id = p.Id, xmlName = p.XmlName, displayName = p.DisplayName, purpose = p.Purpose, referenceForms = p.ReferenceForms }),
                subPatterns = D365FO.Core.FormPatterns.FormPatternCatalog.SubPatterns
                    .Select(sp => new { id = sp.Id, xmlName = sp.XmlName, displayName = sp.DisplayName, appliesTo = sp.AppliesToControlTypes, purpose = sp.Purpose }),
            });
        }

        var spec = D365FO.Core.FormPatterns.FormPatternCatalog.Resolve(name);
        if (spec is not null)
        {
            return ToolResult<object>.Success(new
            {
                id = spec.Id,
                xmlName = spec.XmlName,
                displayName = spec.DisplayName,
                versions = spec.Versions,
                purpose = spec.Purpose,
                whenToUse = spec.WhenToUse,
                whenNotToUse = spec.WhenNotToUse,
                referenceForms = spec.ReferenceForms,
                designProperties = spec.DesignProperties,
                requiresDataSource = spec.RequiresDataSource,
                lifecycleGuidance = spec.LifecycleGuidance,
                notes = spec.Notes,
            });
        }

        var sub = D365FO.Core.FormPatterns.FormPatternCatalog.ResolveSubPattern(name);
        if (sub is not null)
        {
            return ToolResult<object>.Success(new
            {
                id = sub.Id,
                xmlName = sub.XmlName,
                displayName = sub.DisplayName,
                kind = "subPattern",
                versions = sub.Versions,
                appliesToControlTypes = sub.AppliesToControlTypes,
                parentPatterns = sub.ParentPatterns,
                purpose = sub.Purpose,
                referenceForms = sub.ReferenceForms,
                notes = sub.Notes,
            });
        }

        return ToolResult<object>.Fail("PATTERN_NOT_FOUND",
            $"Unknown form pattern or sub-pattern '{name}'.",
            $"Known patterns: {string.Join(", ", D365FO.Core.FormPatterns.FormPatternCatalog.KnownPatternNames())}.");
    }

    /// <summary>
    /// Read X++ source from the index for a class/table/form. Backs the unified
    /// <c>get_method</c> MCP tool (mirrors the CLI <c>read</c> commands). Omit
    /// <paramref name="method"/> to list every method's signature;
    /// <paramref name="include"/> selects signature / source / both for a single method.
    /// </summary>
    public ToolResult<object> ReadMethod(string objectType, string name, string? method = null, string include = "both")
    {
        if (string.IsNullOrWhiteSpace(name))
            return ToolResult<object>.Fail("BAD_INPUT", "name is required.");

        var (path, kind, notFound) = (objectType ?? "class").ToLowerInvariant() switch
        {
            "table" => (_repo.GetTableDetails(name)?.Table?.SourcePath, "table", "TABLE_NOT_FOUND"),
            "form"  => (_repo.GetForm(name)?.Form?.SourcePath,          "form",  "FORM_NOT_FOUND"),
            _       => (_repo.GetClassDetails(name)?.Class?.SourcePath, "class", "CLASS_NOT_FOUND"),
        };

        if (string.IsNullOrEmpty(path))
            return ToolResult<object>.Fail(notFound, $"{kind} '{name}' has no source in the index.");

        var src = XppSourceReader.Read(path!);
        if (src is null)
            return ToolResult<object>.Fail("SOURCE_UNREADABLE", $"Could not read X++ source at {path}.");

        // No method → list signatures of every method on the object (cheap context).
        if (string.IsNullOrWhiteSpace(method))
        {
            return ToolResult<object>.Success(new
            {
                kind, name, path = src.Path,
                declaration = src.Declaration,
                methodCount = src.Methods.Count,
                methods = src.Methods.Select(m => new { name = m.Name, signature = MethodSignature(m.Body) }),
            });
        }

        var hit = XppSourceReader.FindMethod(src, method!);
        if (hit is null)
            return ToolResult<object>.Fail("METHOD_NOT_FOUND",
                $"Method '{method}' not found on {kind} '{name}'.",
                $"Available methods: {string.Join(", ", src.Methods.Select(x => x.Name).Take(20))}");

        return (include ?? "both").ToLowerInvariant() switch
        {
            "signature" => ToolResult<object>.Success(new { kind, name, path = src.Path, method = hit.Name, signature = MethodSignature(hit.Body) }),
            "source"    => ToolResult<object>.Success(new { kind, name, path = src.Path, method = hit.Name, source = hit.Body }),
            _           => ToolResult<object>.Success(new { kind, name, path = src.Path, method = hit.Name, signature = MethodSignature(hit.Body), source = hit.Body }),
        };
    }

    /// <summary>The method header (everything up to the opening brace), collapsed to one line.</summary>
    private static string MethodSignature(string body)
    {
        if (string.IsNullOrEmpty(body)) return "";
        var brace = body.IndexOf('{');
        var head = brace > 0 ? body[..brace] : body;
        return string.Join(' ', head.Split(['\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries));
    }

    public ToolResult<object> ValidateFormPattern(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
            return ToolResult<object>.Fail("BAD_INPUT", "xml is required: complete AxForm XML.");

        var report = D365FO.Core.FormPatterns.FormPatternValidator.ValidateXml(xml);
        return ToolResult<object>.Success(new
        {
            formName = report.FormName,
            pattern = report.Pattern,
            patternVersion = report.PatternVersion,
            errors = report.ErrorCount,
            warnings = report.WarningCount,
            coverage = new { containersTotal = report.ContainersTotal, containersPatterned = report.ContainersPatterned },
            violations = report.Violations.Select(v => new { rule = v.Rule, severity = v.Severity, path = v.Path, excerpt = v.Excerpt, fix = v.Fix }),
        });
    }

    // ---- prepare (single-round context aggregators + grounding token) ----

    /// <summary>
    /// Aggregate context for extending/modifying an existing object: signature +
    /// CoC eligibility, existing wrappers, strategy, naming, similar objects, and
    /// a 30-min grounding token. Backs the unified <c>prepare</c> MCP tool
    /// (<c>mode=change</c>) and the CLI <c>prepare change</c> command.
    /// </summary>
    public ToolResult<object> PrepareChange(string objectName, string? goal, string? method, string? type, string? proposedName, string? prefix)
    {
        if (string.IsNullOrWhiteSpace(objectName))
            return ToolResult<object>.Fail("BAD_INPUT", "Object name required.");

        objectName = objectName.Trim();
        var resolvedGoal = goal ?? "(not stated)";

        var kinds = _repo.SymbolKinds(objectName);
        var objectType = type?.Trim().ToLowerInvariant() ?? kinds.FirstOrDefault();
        if (kinds.Count == 0 && type is null)
            return ToolResult<object>.Fail("OBJECT_NOT_FOUND",
                $"\"{objectName}\" not found in the index.",
                $"Use search (type=any) for \"{objectName}\" to find the correct name — do not invent one.");

        object? methodInfo = null;
        if (!string.IsNullOrEmpty(method))
        {
            var m = _repo.FindMethod(objectName, method!);
            if (m is null)
            {
                methodInfo = new
                {
                    name = method,
                    found = false,
                    eligibility = $"Method \"{method}\" not found on {objectName} (checked inheritance chain and extensions). " +
                                  $"Use get_object_info (objectType={(objectType == "table" ? "table" : "class")}) for {objectName} to list real methods.",
                };
            }
            else
            {
                var attrs = _repo.GetMethodAttributes(objectName, method!);
                var blockers = new List<string>();
                foreach (var (attrName, rawArgs) in attrs)
                {
                    var falseArg = rawArgs?.Contains("false", StringComparison.OrdinalIgnoreCase) ?? false;
                    if (attrName.Contains("Hookable", StringComparison.OrdinalIgnoreCase) && falseArg)
                        blockers.Add("[Hookable(false)] — CoC is blocked on this method.");
                    if (attrName.Contains("Wrappable", StringComparison.OrdinalIgnoreCase) && falseArg)
                        blockers.Add("[Wrappable(false)] — wrapping is blocked on this method.");
                }
                var isFinal = m.Signature?.Contains("final", StringComparison.OrdinalIgnoreCase) ?? false;
                if (isFinal && blockers.Count == 0)
                    blockers.Add("Method is final — requires [Wrappable(true)] to enable CoC.");
                methodInfo = new
                {
                    name = method,
                    found = true,
                    signature = m.Signature ?? "(signature unavailable — method proven via extension metadata)",
                    eligibility = blockers.Count > 0 ? string.Join(" ", blockers) : "Method appears CoC-eligible.",
                };
            }
        }

        var coc = _repo.FindCocExtensions(objectName, method)
            .Select(c => new { c.TargetMethod, c.ExtensionClass, c.Model })
            .ToList();

        object? naming = null;
        if (!string.IsNullOrEmpty(proposedName))
        {
            var violations = ObjectNamingRules.Validate("Coc", proposedName!, prefix);
            var collision = _repo.SymbolKinds(proposedName!);
            naming = new
            {
                proposedName,
                ok = !violations.Any(v => v.Severity == "error") && collision.Count == 0,
                collision = collision.Count > 0 ? $"\"{proposedName}\" already exists ({string.Join(", ", collision)})." : null,
                violations = violations.Select(v => new { code = v.Code, severity = v.Severity, message = v.Message }),
            };
        }

        var similar = _repo.FindSimilarObjects(objectType ?? "class", LastToken(objectName))
            .Where(s => !s.Name.Equals(objectName, StringComparison.OrdinalIgnoreCase))
            .Select(s => new { s.Name, s.Model })
            .ToList();

        var token = ProvenanceStore.CreateToken(new ProvenanceContext(
            resolvedGoal, objectName, method, objectType, proposedName));

        return ToolResult<object>.Success(new
        {
            goal = resolvedGoal,
            objectName,
            objectType,
            method = methodInfo,
            existingCocExtensions = coc,
            recommendedStrategies = StrategiesFor(objectType),
            namingValidation = naming,
            similarObjects = similar,
            groundingToken = token,
            groundingNote = ProvenanceStore.EnforcementEnabled
                ? $"D365FO_GROUNDING_ENFORCE=true — pass the grounding token to generate (coc/extension/event-handler). " +
                  $"The token is bound to \"{objectName}\" and expires in 30 minutes."
                : "Pass the grounding token to generate to confirm this context was used. " +
                  "Set D365FO_GROUNDING_ENFORCE=true to require it.",
        });
    }

    /// <summary>
    /// Aggregate context for a brand-new object: collision check, naming, similar
    /// objects, EDT suggestions, reusable labels, mined property defaults, and a
    /// grounding token. Backs the unified <c>prepare</c> MCP tool
    /// (<c>mode=create</c>) and the CLI <c>prepare create</c> command.
    /// </summary>
    public ToolResult<object> PrepareCreate(string name, string type, string? goal, string[]? fields, string? prefix)
    {
        if (string.IsNullOrWhiteSpace(name))
            return ToolResult<object>.Fail("BAD_INPUT", "Object name required.");

        var baseName = name.Trim();
        var resolvedGoal = goal ?? "(not stated)";
        var objectType = (string.IsNullOrWhiteSpace(type) ? "class" : type).Trim().ToLowerInvariant();
        var finalName = string.IsNullOrEmpty(prefix) || baseName.StartsWith(prefix!, StringComparison.OrdinalIgnoreCase)
            ? baseName
            : prefix + baseName;

        var collisions = new List<object>();
        foreach (var candidate in new[] { baseName, finalName }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var hit = _repo.SymbolKinds(candidate);
            if (hit.Count > 0)
                collisions.Add(new { name = candidate, existsAs = hit });
        }

        var namingKind = objectType switch
        {
            "data-entity" => "Entity",
            "menu-item" => "MenuItem",
            _ => char.ToUpperInvariant(objectType[0]) + objectType[1..],
        };
        var violations = ObjectNamingRules.Validate(namingKind, finalName, prefix);

        var similar = _repo.FindSimilarObjects(objectType, LastToken(baseName))
            .Select(s => new { s.Name, s.Model })
            .ToList();

        var fieldSuggestions = new List<object>();
        foreach (var field in (fields ?? Array.Empty<string>()).Take(10))
        {
            var suggestions = EdtSuggester.Suggest(_repo, field, 3);
            fieldSuggestions.Add(new
            {
                field,
                edts = suggestions.Select(s => new { s.Edt.Name, s.Confidence, s.Reason }),
                hint = suggestions.Count == 0
                    ? $"No EDT match — use suggest_edt for \"{field}\" or base it on a primitive + label."
                    : null,
            });
        }

        var words = System.Text.RegularExpressions.Regex.Replace(baseName, "([A-Z])", " $1").Trim();
        IReadOnlyList<LabelMatch> labels;
        try { labels = _repo.SearchLabels(words, new[] { "en-us" }, 5); }
        catch { labels = Array.Empty<LabelMatch>(); }

        object? propertyDefaults = null;
        if (objectType == "table" && _repo.HasPropertyStats())
        {
            var props = new List<object>();
            foreach (var prop in new[] { "Label", "TableGroup", "ClusteredIndex", "AlternateKeyIndex", "CacheLookup" })
            {
                var (present, total, ratio) = _repo.GetPropertyPresenceRatio("AxTable", prop);
                if (total == 0) continue;
                props.Add(new { property = prop, standardUsage = Math.Round(ratio * 100) + "%", required = ratio >= 0.8 });
            }
            var dist = _repo.GetPropertyValueDistribution("AxTable", "TableGroup", 4);
            propertyDefaults = new
            {
                properties = props,
                tableGroupValues = dist.Select(d => new { d.Value, d.Count }),
            };
        }

        var token = ProvenanceStore.CreateToken(new ProvenanceContext(
            resolvedGoal, baseName, null, objectType, finalName));

        return ToolResult<object>.Success(new
        {
            goal = resolvedGoal,
            objectType,
            baseName,
            finalName,
            collisions = collisions.Count > 0 ? collisions : null,
            collisionVerdict = collisions.Count > 0
                ? "Name already exists — pick a different name or extend the existing object instead."
                : $"No collision — neither \"{finalName}\" nor \"{baseName}\" exists in the index.",
            namingViolations = violations.Select(v => new { code = v.Code, severity = v.Severity, message = v.Message }),
            similarObjects = similar,
            fieldEdtSuggestions = fieldSuggestions.Count > 0 ? fieldSuggestions : null,
            reusableLabels = labels.Select(l => new { token = $"@{l.File}:{l.Key}", l.Value, l.Language }),
            labelHint = labels.Count > 0
                ? "Reuse instead of creating duplicates (rule: labels action=search before labels action=create)."
                : "No matching labels — create new ones via labels action=create.",
            propertyDefaults,
            groundingToken = token,
        });
    }

    internal static string LastToken(string name)
    {
        var tokens = System.Text.RegularExpressions.Regex
            .Split(name, "(?=[A-Z])")
            .Where(t => t.Length >= 4)
            .ToList();
        return tokens.Count > 0 ? tokens[^1] : name;
    }

    internal static IReadOnlyList<string> StrategiesFor(string? objectType) => objectType switch
    {
        "table" => new[]
        {
            "Table extension (AxTableExtension) — add fields, indexes, relations, field groups: generate extension table <Target> <Suffix>",
            "Table extension class [ExtensionOf(tableStr(...))] — CoC on table methods: generate_object (objectType=coc)",
            "Event handler [DataEventHandler(tableStr(X), DataEventType::...)] — subscribe to data events: generate event-handler",
            "New standalone class — if no suitable extension point exists",
        },
        "class" => new[]
        {
            "Class extension [ExtensionOf(classStr(...))] — CoC on class methods: generate_object (objectType=coc)",
            "Event handler [SubscribesTo(...)] — subscribe to delegate events: generate event-handler",
            "New standalone class — if no suitable extension point exists",
        },
        "form" => new[]
        {
            "Form extension (AxFormExtension) — add controls, data sources, menu items: generate extension form <Target> <Suffix>",
            "Form extension class [ExtensionOf(formStr(...))] — CoC on form methods",
            "Form datasource extension [ExtensionOf(formDataSourceStr(...))] — CoC on DS methods",
            "New standalone class — if no suitable extension point exists",
        },
        "map" => new[]
        {
            "Map extension class [ExtensionOf(mapStr(...))] — add/wrap map methods",
            "New standalone class — if no suitable extension point exists",
        },
        _ => new[]
        {
            "Extension class via [ExtensionOf] — check the object type for supported extension mechanisms",
            "New standalone class — if no suitable extension point exists",
        },
    };

    // ---- find_references (reverse references in indexed X++ source) ----

    /// <summary>
    /// Reverse-reference search over indexed X++ source for a symbol. Backs the
    /// unified <c>find_references</c> MCP tool and shares its implementation
    /// (<see cref="MethodSourceSearch"/>) with the CLI <c>find refs</c> command,
    /// so both surfaces use the MethodSourceFts index when it's populated and
    /// fall back to the same disk scan otherwise. (The bridge-backed
    /// DYNAMICSXREFDB path stays CLI-only via <c>find refs --xref</c>.)
    /// </summary>
    public ToolResult<object> FindReferences(string name, string? kind, string? model, int limit = 200)
    {
        if (string.IsNullOrWhiteSpace(name))
            return ToolResult<object>.Fail("BAD_INPUT", "name is required.");

        var result = MethodSourceSearch.Find(_repo, name, kind, model, limit);
        var items = result.Hits.Select(h => new
        {
            kind = h.Kind,
            name = h.Name,
            model = h.Model,
            method = h.Method,
            matches = h.Matches.Select(l => new { line = l.Line, text = l.Text }),
            path = h.Path,
        }).ToList();

        return ToolResult<object>.Success(new
        {
            needle = result.Needle,
            via = result.Via,
            filesScanned = result.FilesScanned,
            count = items.Count,
            truncated = result.Truncated,
            items,
        });
    }

    /// <summary>
    /// Field-level lookup shared with the CLI <c>find fields</c> command: which
    /// tables declare a field (or use an EDT) with this exact name. Answers
    /// "which tables contain field X" without the relation/FK false-positives
    /// that <c>find_references</c> / relation lookups produce (issue #101).
    /// </summary>
    public ToolResult<object> FindTablesByField(string name, string? model, int limit = 200)
    {
        if (string.IsNullOrWhiteSpace(name))
            return ToolResult<object>.Fail("BAD_INPUT", "name is required.");

        var items = _repo.FindTablesByField(name, model, limit);
        return ToolResult<object>.Success(new { count = items.Count, items });
    }
}
