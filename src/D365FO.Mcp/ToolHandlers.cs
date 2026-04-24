using D365FO.Core;
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

    public ToolResult<object> BatchSearch(string[] queries, int limit = 50)
    {
        if (queries is null || queries.Length == 0)
            return ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "queries must be a non-empty array.");
        var results = new List<object>();
        foreach (var q in queries)
        {
            if (string.IsNullOrWhiteSpace(q)) continue;
            var hits = _repo.FindUsages(q, limit)
                .Select(t => new { kind = t.Kind, name = t.Name, model = t.Model })
                .ToList();
            results.Add(new { query = q, count = hits.Count, items = hits });
        }
        return ToolResult<object>.Success(new { count = results.Count, results });
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

    public ToolResult<object> CreateLabel(string file, string key, string value, bool overwrite = false)
    {
        if (string.IsNullOrWhiteSpace(file)) return ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "file required");
        if (string.IsNullOrWhiteSpace(key)) return ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "key required");
        try
        {
            var res = D365FO.Core.Labels.LabelFileWriter.CreateOrUpdate(file, key, value, overwrite);
            if (res.Outcome == D365FO.Core.Labels.WriteOutcome.KeyExists)
                return ToolResult<object>.Fail("KEY_EXISTS",
                    $"Label '{key}' already exists; pass overwrite=true to replace.",
                    hint: $"Existing value: {res.OldValue}");
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

    public ToolResult<object> RenameLabel(string file, string oldKey, string newKey, bool overwrite = false)
    {
        if (string.IsNullOrWhiteSpace(file)) return ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "file required");
        if (string.IsNullOrWhiteSpace(oldKey) || string.IsNullOrWhiteSpace(newKey))
            return ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "oldKey and newKey required");
        try
        {
            var res = D365FO.Core.Labels.LabelFileWriter.Rename(file, oldKey, newKey, overwrite);
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
}
