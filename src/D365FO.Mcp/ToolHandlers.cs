using D365FO.Core;
using D365FO.Core.Extract;
using D365FO.Core.Guardrails;
using D365FO.Core.Index;
using D365FO.Core.Knowledge;
using System.IO;

namespace D365FO.Mcp;

/// <summary>
/// Shared delegate surface used by the MCP transport to invoke the same core
/// operations that back the CLI. Every method returns a <see cref="ToolResult{T}"/>
/// so MCP tool handlers and CLI commands produce byte-identical envelopes.
/// </summary>
public sealed partial class ToolHandlers
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
        var resolved = _repo.GetEdtResolved(name);
        return resolved is null
            ? ToolResult<object>.Fail("EDT_NOT_FOUND", $"EDT '{name}' not found.")
            : ToolResult<object>.Success(
                (object)resolved.Value.Edt,
                resolved.Value.StringSizeInheritedFrom is null
                    ? null
                    : new List<string>
                    {
                        $"StringSize {resolved.Value.Edt.StringSize} is inherited from {resolved.Value.StringSizeInheritedFrom} — this EDT declares none of its own.",
                    });
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
        return LabelResultWithDiskCheck(items);
    }

    public ToolResult<object> SearchLabelsFts(string query, string[]? langs = null, int limit = 100, bool raw = false)
    {
        var items = _repo.SearchLabelsFts(query, langs, limit);
        if (!raw)
            items = items.Select(l => l with { Value = StringSanitizer.Sanitize(l.Value) }).ToList();
        return LabelResultWithDiskCheck(items);
    }

    /// <summary>
    /// Search/resolve results confirmed against the physical .label.txt before anything
    /// recommends them. The index is a snapshot that is never invalidated on delete or
    /// rollback; upstream watched a benchmark run take all three labels it needed from
    /// phantom rows — xppc does not check labels, so the build passed and BP failed two
    /// steps later (BPErrorUnknownLabel), costing a second build and a second BP run.
    /// Only a positive "the file reads fine and the id is not in it" is reported; a
    /// pre-v17 index (no LabelFiles rows) or an unreadable file says nothing.
    /// </summary>
    private ToolResult<object> LabelResultWithDiskCheck(IReadOnlyList<LabelMatch> items)
    {
        var (phantoms, warnings) = LabelDiskCheck.Annotate(_repo, items);
        return ToolResult<object>.Success(new
        {
            count = items.Count,
            items,
            phantomLabels = phantoms.Count > 0 ? phantoms.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList() : null,
        }, warnings);
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
        return LabelResultWithDiskCheck(items);
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

    /// <summary>
    /// Every TableExtension targeting <paramref name="table"/>, plus the effective merged schema.
    /// </summary>
    /// <remarks>
    /// The roster alone was returned under a contract promising a merge, which is worse than
    /// returning a roster honestly: a caller that trusts it reads the absence of a field as the
    /// field not existing. <see cref="TableMergeAnalyzer"/> now folds each extension's own XML
    /// onto the base table, and an extension whose file cannot be read is reported rather than
    /// silently dropped.
    /// </remarks>
    public ToolResult<object> GetTableExtensionInfo(string table)
        => D365FO.Core.Analysis.ExtensionAnswers.TableMerge(_repo, table);

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

    /// <summary>
    /// Correct the text of a label that already exists, in every language file it resolves to.
    /// </summary>
    /// <remarks>
    /// Deliberately not <c>CreateLabel</c> with overwrite: that writes a new entry when the key
    /// is absent, so a mistyped key in a correction produces a second label and reports success.
    /// Nothing updated anywhere is a failure, because the caller believes a correction landed.
    /// </remarks>
    public ToolResult<object> UpdateLabel(
        string? file, string key, string value, string? installTo, string? lang, string? labelFile)
    {
        if (string.IsNullOrWhiteSpace(key))
            return ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "key is required.");

        var targets = new List<string>();
        if (!string.IsNullOrWhiteSpace(file))
        {
            targets.Add(file!);
        }
        else if (!string.IsNullOrWhiteSpace(installTo))
        {
            foreach (var l in (string.IsNullOrWhiteSpace(lang) ? "en-us" : lang!)
                     .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var path = ResolveLabelPath(installTo!, l, labelFile);
                if (path is not null) targets.Add(path);
            }
        }

        if (targets.Count == 0)
            return ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                "file or installTo is required, and installTo must resolve to a packages path.");

        var updated = new List<object>();
        var missing = new List<string>();
        try
        {
            foreach (var path in targets)
            {
                var res = D365FO.Core.Labels.LabelFileWriter.Update(path, key, value);
                if (res.Outcome == D365FO.Core.Labels.WriteOutcome.KeyMissing) { missing.Add(path); continue; }
                D365FO.Core.Journal.LabelJournalRecorder.RecordCreateOrUpdate(res, "labels update");
                updated.Add(new { file = res.Path, key = res.Key, oldValue = res.OldValue, newValue = res.NewValue });
            }
        }
        catch (Exception ex)
        {
            return ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed, ex.Message);
        }

        if (updated.Count == 0)
            return ToolResult<object>.Fail("KEY_MISSING",
                $"Label '{key}' is in none of the target files, so nothing was corrected.",
                $"Checked: {string.Join(", ", targets)}. Use action=create to add it, or action=search to find "
                + "the key it was actually written under.");

        return ToolResult<object>.Success(new { key, updated = updated.Count, files = updated },
            missing.Count > 0
                ? [$"{missing.Count} target file(s) do not carry this key and were left alone: {string.Join(", ", missing)}"]
                : null);
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

    public ToolResult<object> GetSecurityPolicy(string name)
    {
        var item = _repo.GetSecurityPolicy(name);
        return item is null
            ? ToolResult<object>.Fail("NOT_FOUND", $"Security policy '{name}' not found.")
            : ToolResult<object>.Success(item);
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

    /// <summary>
    /// Cross-check a workspace folder's AOT XML against the index — the one <c>analyze</c> mode
    /// that reads the developer's own working tree rather than the index alone.
    /// </summary>
    public ToolResult<object> AnalyzeCompleteness(string path, bool skipLabels, bool skipEdts, bool skipSecurity)
    {
        path = (path ?? "").Trim();
        if (string.IsNullOrWhiteSpace(path) || (!File.Exists(path) && !Directory.Exists(path)))
            return ToolResult<object>.Fail(D365FoErrorCodes.BadInput, $"Path not found: {path}");

        var report = D365FO.Core.Analysis.CompletenessAnalyzer.Analyze(
            path, _repo, new D365FO.Core.Analysis.CompletenessAnalyzer.Options(skipLabels, skipEdts, skipSecurity));

        return ToolResult<object>.Success(report,
            report.IssueCount == 0 ? null : [$"{report.IssueCount} completeness issue(s) found."]);
    }

    /// <summary>Mined form patterns: the histogram, a filtered list, or the peers of one form.</summary>
    public ToolResult<object> AnalyzeFormPatterns(
        string? pattern, string? table, string? similarTo, string? model, int limit)
    {
        try
        {
            return ToolResult<object>.Success(
                D365FO.Core.Analysis.FormPatternMiner.Analyze(_repo, pattern, table, similarTo, model, limit));
        }
        catch (D365FO.Core.Analysis.FormPatternMiner.ReferenceNotFoundException ex)
        {
            return ToolResult<object>.Fail("FORM_NOT_FOUND", ex.Message,
                "Run `d365fo index extract` (or refresh) and retry, or check the spelling with search(type=form).");
        }
    }

    /// <summary>How a scenario is usually done in THIS installation.</summary>
    public ToolResult<object> AnalyzePatterns(string scenario, string? model, int limit)
        => D365FO.Core.Analysis.CodeAnalysis.Patterns(_repo, scenario, model, limit);

    /// <summary>Who else declares this method, and with what signature.</summary>
    public ToolResult<object> AnalyzeImplementations(string method, string? model, int limit)
        => D365FO.Core.Analysis.CodeAnalysis.Implementations(_repo, method, model, limit);

    /// <summary>How an API is constructed and called here.</summary>
    public ToolResult<object> AnalyzeApiUsage(string api, string? model, int limit)
        => D365FO.Core.Analysis.CodeAnalysis.ApiUsage(_repo, api, model, limit);

    /// <summary>
    /// Cross-check a model folder against its <c>.rnrproj</c>: present on disk, and listed by the
    /// project that compiles it.
    /// </summary>
    public ToolResult<object> VerifyProject(string? model, string? path, string[]? expect)
    {
        var folder = path;
        if (string.IsNullOrWhiteSpace(folder))
        {
            if (string.IsNullOrWhiteSpace(model))
                return ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                    "A model name or a path to the model folder is required.");

            var cfg = D365FoSettings.FromEnvironment();
            folder = cfg.CustomPackagesPaths.Concat(new[] { cfg.PackagesPath })
                .Where(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r))
                .SelectMany(r => D365FO.Core.Index.IndexSync.EnumerateModelDirs(r!, model))
                .FirstOrDefault();

            if (folder is null)
                return ToolResult<object>.Fail("MODEL_NOT_FOUND",
                    $"No model directory called '{model}' under any configured packages path.",
                    "Set D365FO_PACKAGES_PATH (and D365FO_CUSTOM_PACKAGES_PATH for custom-model roots), or pass a path.");
        }

        return D365FO.Core.Journal.ProjectVerifier.Verify(folder!, expect);
    }

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

    /// <summary>
    /// Renders a scaffolded document exactly as the CLI would write it to disk.
    /// </summary>
    /// <remarks>
    /// These handlers return XML instead of a path, and used to return the raw scaffold — so
    /// the contract namespace, the contract member order and the shape rules were all applied
    /// on the CLI path and none of them here. The same request produced a correct file through
    /// one surface and a document the reader would quietly strip through the other.
    /// </remarks>
    private static string Aot(System.Xml.Linq.XDocument doc)
        => D365FO.Core.Scaffolding.ScaffoldFileWriter.ToAotXml(doc);

    public ToolResult<object> GenerateEdt(string name, string? extends, string? label, int size)
    {
        if (string.IsNullOrWhiteSpace(name))
            return ToolResult<object>.Fail("BAD_INPUT", "name is required.");
        var doc = D365FO.Core.Scaffolding.XppScaffolder.Edt(name, extends, null, size > 0 ? size : null, label);
        return ToolResult<object>.Success(new { name, xml = Aot(doc) });
    }

    public ToolResult<object> GenerateEnum(string name, string? label, string[]? values)
    {
        if (string.IsNullOrWhiteSpace(name))
            return ToolResult<object>.Fail("BAD_INPUT", "name is required.");
        var vals = values?.Select((v, i) => new D365FO.Core.Scaffolding.EnumValueSpec(v, i, null)).ToList()
                   ?? new List<D365FO.Core.Scaffolding.EnumValueSpec>();
        var doc = D365FO.Core.Scaffolding.XppScaffolder.Enum(name, vals, label: label);
        return ToolResult<object>.Success(new { name, xml = Aot(doc) });
    }

    public ToolResult<object> GenerateQuery(string name, string rootTable, string? label)
    {
        if (string.IsNullOrWhiteSpace(name))
            return ToolResult<object>.Fail("BAD_INPUT", "name is required.");
        if (string.IsNullOrWhiteSpace(rootTable))
            return ToolResult<object>.Fail("BAD_INPUT", "rootTable is required.");
        var ds = new[] { new D365FO.Core.Scaffolding.QueryDataSourceSpec(rootTable) };
        var doc = D365FO.Core.Scaffolding.QueryScaffolder.Query(name, ds);
        return ToolResult<object>.Success(new { name, xml = Aot(doc) });
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
            contract   = new { name = contractName,   xml = Aot(contract) },
            service    = new { name = serviceName,     xml = Aot(service) },
            controller = new { name = controllerName,  xml = Aot(controller) },
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
            @event   = new { name, xml = Aot(eventDoc) },
            contract = new { name = cn, xml = Aot(contractDoc) },
        });
    }

    public ToolResult<object> GenerateRunBase(string name, bool batch)
    {
        if (string.IsNullOrWhiteSpace(name))
            return ToolResult<object>.Fail("BAD_INPUT", "name is required.");
        var doc = D365FO.Core.Scaffolding.RunBaseScaffolder.RunBaseClass(name, batch);
        return ToolResult<object>.Success(new { name, isBatch = batch, xml = Aot(doc) });
    }

    public ToolResult<object> GenerateMenuItem(
        string name, string objectName, string menuKind, string objectType, string? label, string? neededPermission)
    {
        if (string.IsNullOrWhiteSpace(name))
            return ToolResult<object>.Fail("BAD_INPUT", "name is required.");
        if (string.IsNullOrWhiteSpace(objectName))
            return ToolResult<object>.Fail("BAD_INPUT", "objectName is required: a menu item that opens nothing is not useful.");

        var k = Enum.TryParse<D365FO.Core.Scaffolding.MenuItemKind>(menuKind, true, out var mk)
            ? mk : D365FO.Core.Scaffolding.MenuItemKind.Display;
        var ot = Enum.TryParse<D365FO.Core.Scaffolding.MenuItemObjectType>(objectType, true, out var mo)
            ? mo : D365FO.Core.Scaffolding.MenuItemObjectType.Form;

        var doc = D365FO.Core.Scaffolding.MenuItemScaffolder.MenuItem(
            k, name, objectName, ot, label, neededPermission: neededPermission);
        return ToolResult<object>.Success(new { name, kind = D365FO.Core.Scaffolding.MenuItemScaffolder.AxSubfolder(k), xml = Aot(doc) });
    }

    public ToolResult<object> GeneratePrivilege(
        string name, string? entryPoint, string? entryKind, string? access, string? label, string? dataEntity)
    {
        if (string.IsNullOrWhiteSpace(name))
            return ToolResult<object>.Fail("BAD_INPUT", "name is required.");
        if (string.IsNullOrWhiteSpace(entryPoint) && string.IsNullOrWhiteSpace(dataEntity))
            return ToolResult<object>.Fail("BAD_INPUT",
                "A privilege needs something to grant access to: pass entryPoint or dataEntity.");

        var doc = D365FO.Core.Scaffolding.XppScaffolder.Privilege(
            name, entryPoint, entryKind, entryPointObject: null, access: access ?? "Read",
            label: label, dataEntity: dataEntity);
        return ToolResult<object>.Success(new { name, xml = Aot(doc) });
    }

    public ToolResult<object> GenerateDuty(string name, string[]? privileges, string? label)
    {
        if (string.IsNullOrWhiteSpace(name))
            return ToolResult<object>.Fail("BAD_INPUT", "name is required.");
        var doc = D365FO.Core.Scaffolding.XppScaffolder.Duty(name, privileges ?? [], label);
        return ToolResult<object>.Success(new { name, xml = Aot(doc) });
    }

    public ToolResult<object> GenerateRole(string name, string[]? duties, string[]? privileges, string? label)
    {
        if (string.IsNullOrWhiteSpace(name))
            return ToolResult<object>.Fail("BAD_INPUT", "name is required.");
        var doc = D365FO.Core.Scaffolding.XppScaffolder.Role(name, duties, privileges, label);
        return ToolResult<object>.Success(new { name, xml = Aot(doc) });
    }

    public ToolResult<object> GenerateEntity(string name, string table, string[]? fields, string? entityCategory)
    {
        if (string.IsNullOrWhiteSpace(name))
            return ToolResult<object>.Fail("BAD_INPUT", "name is required.");
        if (string.IsNullOrWhiteSpace(table))
            return ToolResult<object>.Fail("BAD_INPUT", "table is required: an entity projects one.");

        var specs = (fields ?? [])
            .Select(f => f.Split(':', 2))
            .Select(parts => new D365FO.Core.Scaffolding.EntityFieldSpec(
                parts[0], parts.Length > 1 ? parts[1] : null, IsMandatory: false))
            .ToList();

        var doc = D365FO.Core.Scaffolding.XppScaffolder.DataEntity(
            name, table, fields: specs, entityCategory: entityCategory);
        return ToolResult<object>.Success(new { name, table, xml = Aot(doc) });
    }

    public ToolResult<object> GenerateExtension(string extensionKind, string target, string? suffix)
    {
        if (string.IsNullOrWhiteSpace(target))
            return ToolResult<object>.Fail("BAD_INPUT", "target is required: the object being extended.");
        try
        {
            var doc = D365FO.Core.Scaffolding.XppScaffolder.Extension(
                extensionKind ?? string.Empty, target, string.IsNullOrWhiteSpace(suffix) ? "Extension" : suffix!);
            return ToolResult<object>.Success(new { name = $"{target}.{suffix ?? "Extension"}", xml = Aot(doc) });
        }
        catch (ArgumentException ex)
        {
            return ToolResult<object>.Fail("BAD_INPUT", ex.Message);
        }
    }

    public ToolResult<object> GenerateSecurityPolicy(string name, string constrainedTable, string? policyQuery)
    {
        if (string.IsNullOrWhiteSpace(name))
            return ToolResult<object>.Fail("BAD_INPUT", "name is required.");
        if (string.IsNullOrWhiteSpace(constrainedTable))
            return ToolResult<object>.Fail("BAD_INPUT", "constrainedTable is required.");
        var pq = string.IsNullOrWhiteSpace(policyQuery) ? name + "Query" : policyQuery!;
        var doc = D365FO.Core.Scaffolding.SecurityPolicyScaffolder.Policy(name, constrainedTable, pq);
        return ToolResult<object>.Success(new { name, xml = Aot(doc) });
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
    /// <summary>
    /// Resolve the label file for a model and language, or null when the id is one no
    /// <c>@File:Id</c> token could name — a label written there could never be referenced.
    /// </summary>
    private static string? ResolveLabelPath(string model, string lang, string? labelFile)
    {
        var cfg = D365FoSettings.FromEnvironment();
        var lf = string.IsNullOrWhiteSpace(labelFile) ? model : labelFile!;
        if (!D365FO.Core.Labels.LabelFileWriter.IsReferenceableLabelFileId(lf)) return null;
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

    /// <summary>
    /// Run the grounding gate, then write. Both halves, in that order, for the same reason the
    /// CLI does it: a write that reaches disk without the gate is one that
    /// <c>D365FO_GROUNDING_ENFORCE=true</c> did not enforce, and this path used to be exactly
    /// that — the flag was honoured on the shell surface and silently inert here.
    /// </summary>
    /// <param name="targetObject">
    /// What the write is bound to, and what a <c>prepare</c> token is checked against: the
    /// extension or CoC target for extension-shaped scaffolds, the artefact's own name otherwise.
    /// </param>
    private ToolResult<object> WriteScaffold(
        System.Xml.Linq.XDocument doc, string name, string kind, string axSubfolder,
        string? installTo, string? outPath, bool overwrite, object extra,
        string? groundingToken = null, string? targetObject = null,
        IEnumerable<string>? requiredSymbols = null)
    {
        var gate = D365FO.Core.Scaffolding.GroundingGate.Check(
            groundingToken, targetObject ?? name, doc,
            requiredMethods: null, requiredSymbols: requiredSymbols,
            requested: null, repository: _repo);
        if (gate.Failure is not null) return gate.Failure;

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
            gate.Observe(doc.ToString());
            return ToolResult<object>.Success(new
            {
                kind,
                name,
                path = res.Path,
                bytes = res.Bytes,
                backup = res.BackupPath,
                model = installTo,
                grounding = gate.Grounding,
                extra,
            }, gate.Warnings.Count > 0 ? gate.Warnings : null);
        }
        catch (Exception ex)
        {
            return ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed, ex.Message);
        }
    }

    /// <summary>String-rendered counterpart of <see cref="WriteScaffold"/> (used for forms).</summary>
    private ToolResult<object> WriteScaffoldString(
        string xml, string name, string kind, string axSubfolder,
        string? installTo, string? outPath, bool overwrite, object extra,
        string? groundingToken = null, string? targetObject = null)
    {
        System.Xml.Linq.XDocument? parsed = null;
        try { parsed = System.Xml.Linq.XDocument.Parse(xml); } catch { /* the writer reports it */ }

        var gate = D365FO.Core.Scaffolding.GroundingGate.Check(
            groundingToken, targetObject ?? name, parsed, repository: _repo);
        if (gate.Failure is not null) return gate.Failure;

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
            gate.Observe(xml);
            return ToolResult<object>.Success(new
            {
                kind,
                name,
                path = res.Path,
                bytes = res.Bytes,
                backup = res.BackupPath,
                model = installTo,
                grounding = gate.Grounding,
                extra,
            }, gate.Warnings.Count > 0 ? gate.Warnings : null);
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
        string? installTo, string? outPath, bool overwrite, string? groundingToken = null)
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
        // configurationKey / formRef stay CLI-only: the MCP surface already exposes a
        // reduced subset of generate table's options (no tableType, no primaryKey) and
        // every added property widens the shared generate_object schema for all types.
        var doc = D365FO.Core.Scaffolding.XppScaffolder.Table(name, label, fieldSpecs, pat,
            D365FO.Core.Scaffolding.TableStorage.RegularTable, null, edtBaseTypeResolver: edtResolver);
        var extra = new { fieldCount = fieldSpecs.Count > 0 ? fieldSpecs.Count : (int?)null, pattern = pat == D365FO.Core.Scaffolding.TablePattern.None ? null : pat.ToString() };
        return WriteScaffold(doc, name, "AxTable", "AxTable", installTo, outPath, overwrite, extra,
            groundingToken);
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

    /// <summary>
    /// Structured edits beyond a method body — property, field, enum value, control.
    /// Writes to a model outside <c>D365FO_CUSTOM_MODELS</c> are redirected to an
    /// extension object, and every write is journaled for <c>undo_last_modification</c>.
    /// </summary>
    /// <summary>
    /// Apply one structured modification — or a batch of them — through the same engine the
    /// <c>d365fo modify</c> sub-commands use.
    /// </summary>
    /// <remarks>
    /// The request arrives fully bound (see <c>ToolCatalog.ModifyObjectCall</c>) rather than as
    /// a parameter list, because the parameter list is what fell behind: the engine grew from
    /// five operations to twenty and this handler kept binding the four it was written with.
    /// Taking the engine's own request type means an operation that the engine gains cannot be
    /// unreachable here without also being unreachable from the CLI.
    /// </remarks>
    public ToolResult<object> ModifyObject(D365FO.Core.Bridge.ObjectModifyEngine.ModifyRequest request)
        => D365FO.Core.Bridge.ObjectModifyEngine.Modify(request, _repo);

    public ToolResult<object> GenerateClass(
        string name, string? extends, bool nonFinal,
        string? installTo, string? outPath, bool overwrite, string? groundingToken = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            return ToolResult<object>.Fail("BAD_INPUT", "name is required.");
        var doc = D365FO.Core.Scaffolding.XppScaffolder.Class(name, extends, !nonFinal);
        return WriteScaffold(doc, name, "AxClass", "AxClass", installTo, outPath, overwrite, new { extends },
            groundingToken);
    }

    public ToolResult<object> GenerateCoc(
        string target, string[] methods,
        string? installTo, string? outPath, bool overwrite, string? groundingToken = null)
    {
        if (string.IsNullOrWhiteSpace(target))
            return ToolResult<object>.Fail("BAD_INPUT", "target is required.");
        if (methods is null || methods.Length == 0)
            return ToolResult<object>.Fail("BAD_INPUT", "At least one method is required.");

        // A target spelled as the extension itself (Base.Suffix, Base_Extension) is
        // rewritten to its base rather than suffixed twice.
        target = D365FO.Core.ObjectNamingRules.NormalizeExtensionTarget(target, out var renameNote);

        // Guardrail: warn if CoC wrappers already exist.
        var warnings = new List<string>();
        if (renameNote is not null) warnings.Add(renameNote);
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
            new { target, methodCount = methods.Length },
            groundingToken: groundingToken, targetObject: target, requiredSymbols: [target]);

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
        string? installTo, string? outPath, bool overwrite, string? groundingToken = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            return ToolResult<object>.Fail("BAD_INPUT", "name is required.");

        if (!D365FO.Core.Scaffolding.FormPatternNormalizer.TryNormalize(pattern, out var fp, out var patternError))
            return ToolResult<object>.Fail("BAD_INPUT", patternError!);

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
            new { table, pattern = fp.ToString() }, groundingToken: groundingToken);
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

    /// <summary>
    /// One validation entry point over MCP, mirroring <c>d365fo validate</c>.
    /// </summary>
    /// <remarks>
    /// Until now MCP exposed only naming and form patterns, so an agent could generate XML
    /// through <c>generate_object</c> and had no way to ask whether it was sound — the checks
    /// existed, on the other surface. <c>metadata-shape</c> is the offline half of
    /// <c>validate metadata</c>: it needs no bridge and no VM, because it judges against the
    /// contract catalog the AOT reader itself is generated from.
    /// </remarks>
    public ToolResult<object> Validate(string mode, string code, string? context, string? codeType)
    {
        if (string.IsNullOrWhiteSpace(code))
            return ToolResult<object>.Fail("BAD_INPUT", "code is required: X++ source or AOT XML.");

        switch ((mode ?? "xpp").Trim().ToLowerInvariant())
        {
            case "xpp":
                return ValidateXppCode(code, context, codeType);
            case "references":
                return ValidateReferences(code, context);
            case "form-pattern":
                return ValidateFormPattern(code);
            case "metadata-shape":
                return ValidateMetadataShape(code, context);
            default:
                return ToolResult<object>.Fail("BAD_INPUT",
                    $"Unknown validate mode '{mode}'.",
                    "Modes: xpp | references | form-pattern | metadata-shape.");
        }
    }

    private ToolResult<object> ValidateXppCode(string code, string? context, string? codeType)
    {
        var normalized = D365FO.Core.Validation.XppValidator.NormalizeCodeType(
            codeType ?? DetectCodeType(code));

        D365FO.Core.Validation.IPropertyStatsProvider? stats = null;
        try { if (_repo.HasPropertyStats()) stats = _repo; }
        catch { /* no index — the property rules fall back to static defaults */ }

        var violations = D365FO.Core.Validation.XppValidator.Validate(code, normalized, stats);
        return ValidationEnvelope(context, normalized, violations.Select(v =>
            (object)new { rule = v.Rule, severity = v.Severity, line = v.Line, excerpt = v.Excerpt, fix = v.Fix }),
            violations.Count(v => v.Severity == "error"),
            violations.Count(v => v.Severity == "warning"));
    }

    /// <summary>
    /// Mirror of the CLI's ValidateXppCommand.DetectCodeType: an AxTable routes to the table
    /// rules, an AxReport to the report-only rules (its RDL lives in CDATA and the X++
    /// keyword rules would only produce noise over it), any other XML to xml-any.
    /// </summary>
    private static string DetectCodeType(string code)
    {
        if (System.Text.RegularExpressions.Regex.IsMatch(code, @"<AxTable[\s>]", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return D365FO.Core.Validation.XppValidator.CodeTypeXmlTable;
        if (System.Text.RegularExpressions.Regex.IsMatch(code, @"<AxReport[\s>]", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return D365FO.Core.Validation.XppValidator.CodeTypeXmlReport;
        return code.TrimStart().StartsWith('<') ? "xml-any" : "xpp";
    }

    private ToolResult<object> ValidateReferences(string code, string? context)
    {
        D365FO.Core.Validation.ResolveResult result;
        try
        {
            result = D365FO.Core.Validation.ReferenceResolver.Resolve(code, _repo);
        }
        catch (Exception ex)
        {
            return ToolResult<object>.Fail("NO_INDEX",
                $"Reference resolution requires the SQLite index: {ex.Message}",
                "Run `d365fo index build` then `d365fo index extract` first.");
        }

        return ValidationEnvelope(context, "references", result.Violations.Select(v =>
            (object)new { kind = v.Kind, severity = v.Severity, line = v.Line, identifier = v.Identifier, detail = v.Detail }),
            result.Violations.Count(v => v.Severity == "error"),
            result.Violations.Count(v => v.Severity == "warning"),
            extra: new { verifiedCount = result.VerifiedCount });
    }

    private static ToolResult<object> ValidateMetadataShape(string xml, string? context)
    {
        var violations = new List<D365FO.Core.Validation.XppViolation>();
        // The root's own shape first (issue #163), then everything inside it. Both are driven by
        // the same catalog, and both speak for every AOT family — an agent that asks about a
        // document whose root the provider cannot even construct should hear that before it
        // hears about the members inside it.
        D365FO.Core.Validation.ObjectShapeRules.Check(xml, violations);
        D365FO.Core.Validation.ContractShapeRules.Check(xml, violations);

        return ValidationEnvelope(context, "metadata-shape", violations.Select(v =>
            (object)new { rule = v.Rule, severity = v.Severity, line = v.Line, path = v.Excerpt, fix = v.Fix }),
            violations.Count(v => v.Severity == "error"),
            violations.Count(v => v.Severity == "warning"));
    }

    private static ToolResult<object> ValidationEnvelope(
        string? context, string mode, IEnumerable<object> violations, int errors, int warnings, object? extra = null)
        => ToolResult<object>.Success(new
        {
            context,
            mode,
            errors,
            warnings,
            violations,
            extra,
            verdict = errors > 0
                ? "Fix every error before writing — the artifact is wrong as it stands."
                : warnings > 0 ? "No errors; review the warnings." : "Clean.",
        });

    /// <summary>
    /// The metadata provider's own verdict on one document: what it deserialises to, and every
    /// member it drops on the way in.
    /// </summary>
    /// <remarks>
    /// A dropped element is a property the type does not declare — the document reads without
    /// error and arrives missing what you wrote, which no offline rule can see. Off a machine
    /// with the metadata assemblies this returns <c>skipped</c>: a verdict it cannot support is
    /// worse than no verdict.
    /// </remarks>
    public ToolResult<object> ValidateMetadata(string? kind, string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
            return ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "code (the AOT XML) is required.");

        if (!D365FO.Core.Bridge.BridgeGate.ShouldTry())
            return ToolResult<object>.Success(new
            {
                skipped = true,
                reason = "The metadata provider is not enabled here. Set D365FO_BRIDGE_ENABLED=1 (and D365FO_BIN_PATH) "
                       + "on a machine with the D365FO metadata assemblies to validate against Microsoft's own serializer.",
            });

        var verdict = D365FO.Core.Bridge.BridgeGate.TryValidateArtifact(kind, xml);
        if (verdict is null)
            return ToolResult<object>.Success(new
            {
                skipped = true,
                reason = "The bridge did not answer. Check D365FO_BRIDGE_PATH points at D365FO.Bridge.exe and "
                       + "D365FO_BIN_PATH at the folder holding Microsoft.Dynamics.AX.Metadata.dll.",
            });

        return ToolResult<object>.Success(new
        {
            rootElement = verdict.RootElement,
            clrType = verdict.ClrType,
            deserialized = verdict.Deserialized,
            valid = verdict.Valid,
            errorCode = verdict.ErrorCode,
            errorMessage = verdict.ErrorMessage,
            droppedCount = verdict.Dropped.Count,
            dropped = verdict.Dropped,
            verdict = verdict.Valid
                ? "The provider reads this document as written."
                : "The provider cannot read this document as written — a dropped element is a property the type does not have.",
        });
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

    /// <summary>
    /// Deterministic form auto-repair over AxForm XML — the write-side counterpart of
    /// <see cref="ValidateFormPattern"/>. Returns the repaired XML plus what it changed
    /// and what it refused to change; the caller decides whether to persist it.
    /// </summary>
    public ToolResult<object> RepairFormPattern(string xml, string? pattern)
    {
        if (string.IsNullOrWhiteSpace(xml))
            return ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "xml is required: complete AxForm XML.");

        var result = D365FO.Core.FormPatterns.FormPatternRepairer.Repair(xml, pattern);
        return ToolResult<object>.Success(new
        {
            formName = result.Before.FormName,
            pattern = result.After.Pattern,
            changed = result.Changed,
            fullyRepaired = result.FullyRepaired,
            errorsBefore = result.Before.ErrorCount,
            errorsAfter = result.After.ErrorCount,
            changes = result.Changes.Select(c => new { rule = c.Rule, path = c.Path, action = c.Action, detail = c.Detail }),
            skipped = result.Skipped.Select(c => new { rule = c.Rule, path = c.Path, reason = c.Detail }),
            remaining = result.After.Violations
                .Where(v => v.Severity == "error")
                .Select(v => new { rule = v.Rule, path = v.Path, excerpt = v.Excerpt, fix = v.Fix }),
            repairedXml = result.Xml,
        });
    }

    // ---- knowledge (verified X++/D365FO topic corpus + build-error triage) ----

    /// <summary>
    /// Serve the embedded <c>skills/_source</c> knowledge corpus — the CLI's
    /// equivalent of upstream <c>d365fo-mcp-server</c>'s <c>get_knowledge</c>.
    /// <paramref name="action"/> is <c>list</c> (catalog), <c>get</c> (one topic,
    /// optionally one <c>##</c> section or just the outline), or <c>search</c>
    /// (rank sections across the corpus against a free-text question).
    /// </summary>
    public ToolResult<object> GetKnowledge(string action, string? topic, string? section, string? query, int limit, bool outline)
    {
        switch ((action ?? "list").ToLowerInvariant())
        {
            case "search":
                if (string.IsNullOrWhiteSpace(query))
                    return ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "query is required for action=search.");
                var hits = D365FO.Core.Knowledge.KnowledgeBase.Search(query, limit, topic);
                return ToolResult<object>.Success(new
                {
                    query,
                    count = hits.Count,
                    hits = hits.Select(h => new { topic = h.TopicId, heading = h.Heading, score = h.Score, excerpt = h.Excerpt }),
                });

            case "get":
                var found = D365FO.Core.Knowledge.KnowledgeBase.Get(topic);
                if (found is null)
                {
                    var suggestions = D365FO.Core.Knowledge.KnowledgeBase.Suggest(topic);
                    return ToolResult<object>.Fail(D365FoErrorCodes.TopicNotFound,
                        $"No knowledge topic matches '{topic}'.",
                        suggestions.Count > 0
                            ? $"Did you mean: {string.Join(", ", suggestions)}?"
                            : "Call get_knowledge(action=list) for the catalog.");
                }
                if (outline)
                {
                    return ToolResult<object>.Success(new
                    {
                        id = found.Id,
                        description = found.Description,
                        sections = found.Sections.Select(s => new { heading = s.Heading, approxTokens = s.ApproxTokens }),
                    });
                }
                if (!string.IsNullOrWhiteSpace(section))
                {
                    var matches = found.Sections
                        .Where(s => s.Heading.Contains(section!, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (matches.Count == 0)
                    {
                        return ToolResult<object>.Fail(D365FoErrorCodes.TopicNotFound,
                            $"Topic '{found.Id}' has no section matching '{section}'.",
                            $"Available sections: {string.Join(" | ", found.Sections.Select(s => s.Heading))}");
                    }
                    return ToolResult<object>.Success(new
                    {
                        id = found.Id,
                        description = found.Description,
                        sections = matches.Select(s => new { heading = s.Heading, text = s.Text }),
                    });
                }
                return ToolResult<object>.Success(new
                {
                    id = found.Id,
                    description = found.Description,
                    appliesWhen = found.AppliesWhen,
                    approxTokens = found.ApproxTokens,
                    body = found.Body,
                });

            default:
                return ToolResult<object>.Success(new
                {
                    count = D365FO.Core.Knowledge.KnowledgeBase.Topics.Count,
                    topics = D365FO.Core.Knowledge.KnowledgeBase.Topics.Select(t => new
                    {
                        id = t.Id,
                        description = t.Description,
                        appliesWhen = t.AppliesWhen,
                        sections = t.Sections.Count,
                        approxTokens = t.ApproxTokens,
                    }),
                });
        }
    }

    /// <summary>
    /// Score xppc/build output against the <see cref="D365FO.Core.Validation.XppcFixHints"/>
    /// rules and return ranked fixes plus the knowledge topic behind each. Offline —
    /// no VM needed, so an agent can triage a log it was handed.
    /// </summary>
    public ToolResult<object> ExplainBuildError(string log, bool all)
    {
        if (string.IsNullOrWhiteSpace(log))
            return ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "log is required: a compiler message or a whole xppc log.");

        var parsed = D365FO.Core.Validation.XppcDiagnostics.Parse(log);
        var messages = parsed.Count > 0
            ? parsed.Select(d => (d.Severity, d.Object, d.Member, d.Line, d.Message)).ToList()
            : [("error", (string?)null, (string?)null, (int?)null, log.Trim())];

        var explained = messages.Select(m =>
        {
            var matches = D365FO.Core.Validation.XppcFixHints.Match(m.Message);
            return new
            {
                severity = m.Item1,
                obj = m.Item2,
                member = m.Item3,
                line = m.Item4,
                message = m.Message,
                hints = (all ? matches : matches.Take(1))
                    .Select(h => new { rule = h.RuleId, hint = h.Hint, score = h.Score, knowledge = h.Knowledge }),
            };
        }).ToList();

        var warnings = new List<string>();
        if (D365FO.Core.Validation.XppcDiagnostics.IndicatesStaleSymbols(log))
            warnings.Add("Log indicates stale incremental-build symbols — do a full build before trusting these errors.");

        return ToolResult<object>.Success(new
        {
            count = explained.Count,
            explained = explained.Count(e => e.hints.Any()),
            diagnostics = explained,
        }, warnings.Count > 0 ? warnings : null);
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
                // Kernel fallback: every table inherits its data methods (validateWrite,
                // insert, modifiedField, …) from xRecord/Common — kernel types with no AOT
                // metadata, so the index has no row for the most common CoC target there
                // is. "Not found" would read as "does not exist" and leave the caller to
                // invent the wrapper unaided.
                var kernel = D365FO.Core.Knowledge.TableDataMethods.AppliesTo(objectType)
                    ? D365FO.Core.Knowledge.TableDataMethods.Lookup(method!)
                    : null;
                methodInfo = kernel is not null
                    ? (object)new
                    {
                        name = kernel.Name,
                        found = true,
                        signature = kernel.Signature,
                        inherited = new
                        {
                            declaredOn = kernel.DeclaredOn,
                            note = $"{objectName} does not declare {kernel.Name}; every table gets it from " +
                                   $"{kernel.DeclaredOn}, a kernel type with no AOT metadata, which is why the " +
                                   "symbol index has no row for it. The signature above is the one a CoC wrapper must match exactly.",
                        },
                        eligibility = $"CoC-eligible — [ExtensionOf(tableStr({objectName}))] final class … wrapping {kernel.Signature}. {kernel.Purpose}",
                        contract = kernel.Contract,
                    }
                    : new
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
        try
        {
            labels = _repo.SearchLabels(words, new[] { "en-us" }, 5);
            // reusableLabels is a RECOMMENDATION — a phantom row (in the index, in no
            // .label.txt on disk) must never be recommended for reuse: xppc does not
            // check labels, so the mistake only surfaces at BP time as
            // BPErrorUnknownLabel, two builds later.
            var (phantoms, _) = LabelDiskCheck.Annotate(_repo, labels);
            if (phantoms.Count > 0)
                labels = labels.Where(l => !phantoms.Contains($"@{l.File}:{l.Key}")).ToList();
        }
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

    /// <summary>
    /// Aggregate context for WRITING A SYSTEST — <c>prepare</c> mode <c>test</c> (port of the
    /// upstream MCP server's <c>prepare(mode="test")</c>). The other two modes answer "how do
    /// I change this" and "how do I create this"; this one answers "how do I TEST this": the
    /// method list worth covering lives in the index, the tests that already cover the target
    /// live in the index too, and the one thing that reliably breaks a first test run — the
    /// model not referencing TestEssentials — is visible in the descriptor and nowhere else
    /// until the build fails. It deliberately states the RED-first order: a test written after
    /// the code, that passes on its first run, has proven nothing about the assertion inside it.
    /// </summary>
    public ToolResult<object> PrepareTest(string objectName, string? goal, string? methodName, string? modelName)
    {
        if (string.IsNullOrWhiteSpace(objectName))
            return ToolResult<object>.Fail("BAD_INPUT", "Object name (the class or table under test) required.");

        // `CustTable.validateWrite` is how a developer names a table CoC target, and it was the
        // single most-asked shape across real runs. Split it before resolution, and let an
        // explicit --method win over the dotted half so the two spellings cannot disagree.
        var raw = objectName.Trim();
        var focus = methodName?.Trim();
        var target = raw;
        if (raw.Contains('.'))
        {
            var cut = raw.LastIndexOf('.');
            var head = raw[..cut].Trim();
            var tail = raw[(cut + 1)..].Trim();
            if (head.Length > 0 && tail.Length > 0)
            {
                target = head;
                focus ??= tail;
            }
        }

        var testClass = $"{target}Test";
        var details = _repo.GetClassDetails(target);

        // Resolve the kind. Class first (the original contract), table second — a table only wins
        // when nothing of that name is a class, so an installation holding both keeps the old answer.
        TableDetails? tableDetails = details is null ? _repo.GetTableDetails(target) : null;
        var isTable = details is null && tableDetails is not null;
        var targetKind = isTable ? "table" : "class";

        // Lifecycle and serialisation members are not what a unit test pins down.
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "new", "finalize", "typenew", "pack", "unpack", "classdeclaration" };

        List<SuggestedTestMethod> suggested;
        if (isTable)
        {
            // The index stores DECLARED members only, so a table that has never overridden
            // validateWrite has no row for it — and that is the method the caller came for.
            // Declared overrides first (they carry the table's own signature), then the kernel
            // data methods it inherits and could still wrap.
            var declared = (tableDetails!.Methods ?? Array.Empty<TableMethodInfo>())
                .Where(m => !skip.Contains(m.Name))
                .Select(m => new SuggestedTestMethod(m.Name, m.Signature, "declared on the table"))
                .ToList();
            var declaredNames = new HashSet<string>(declared.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
            var inherited = TableDataMethods.All
                .Where(m => !declaredNames.Contains(m.Name))
                .Select(m => new SuggestedTestMethod(m.Name, m.Signature, $"inherited from {m.DeclaredOn} — no AOT row, wrap with CoC"))
                .ToList();

            suggested = declared.Concat(inherited)
                .Where(m => focus is null || string.Equals(m.Name, focus, StringComparison.OrdinalIgnoreCase))
                .Take(8)
                .ToList();
        }
        else
        {
            suggested = (details?.Methods ?? Array.Empty<MethodInfo>())
                .Where(m => !skip.Contains(m.Name))
                .Where(m => focus is null || string.Equals(m.Name, focus, StringComparison.OrdinalIgnoreCase))
                .Take(8)
                .Select(m => new SuggestedTestMethod(m.Name, m.Signature, null))
                .ToList();
        }

        // Classes that look like tests and mention the target — the coverage that exists.
        var existingTests = new List<string>();
        try
        {
            existingTests = _repo.SearchClasses(target + "Test", limit: 5)
                .Concat(_repo.SearchClasses("Test" + target, limit: 5))
                .Select(c => c.Name)
                .Where(n => !string.Equals(n, target, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .ToList();
        }
        catch { /* index hiccup — coverage listing is best-effort */ }

        var testEssentials = TestEssentialsReferenced(modelName);

        var scaffold = $"d365fo generate systest {testClass} --{targetKind} {target}"
            + string.Join("", suggested.Select(m => $" --method {m.Name}"))
            + " --install-to <Model>";

        var token = ProvenanceStore.CreateToken(new ProvenanceContext(
            goal ?? $"unit tests for {target}", target, focus, targetKind, testClass));

        var resolved = details is not null || tableDetails is not null;

        return ToolResult<object>.Success(new
        {
            target,
            targetKind,
            testClass = $"{testClass} extends SysTestCase (the platform's own convention: <Target>Test)",
            targetIndexed = resolved,
            targetHint = resolved
                ? null
                : $"\"{target}\" is in the index as neither a class nor a table — check the name (`d365fo search any {target}`), or re-extract if it was written outside this session. The rest of this answer is generic.",
            methodsWorthTesting = suggested,
            tableTestShape = isTable ? TableTestShape(target, focus) : null,
            scaffoldCall = scaffold,
            existingTests = existingTests.Count > 0 ? existingTests : null,
            existingTestsVerdict = existingTests.Count > 0
                ? "Extend one of those rather than starting a second class for the same target."
                : "No existing test class found for this target.",
            testEssentials = testEssentials switch
            {
                true => $"Model references TestEssentials — the filtering attributes ([SysTestCategory], [SysTestOwner], …) are available.",
                false => "🚨 Model does not reference TestEssentials. [SysTestMethod] comes from ApplicationFoundation and compiles, but " +
                         "[SysTestCategory], [SysTestOwner], [SysTestPriority] and [SysTestAreaPath] are in TestEssentials and will not — " +
                         "add the reference to the model descriptor BEFORE the first build if you plan to use them.",
                null => "(TestEssentials reference not checked — pass modelName and configure D365FO_PACKAGES_PATH to enable it.)",
            },
            redFirstCycle = new[]
            {
                "1. Scaffold + write the test class (every scaffolded method ends in this.fail — red on purpose).",
                "2. Build — it must COMPILE. Red means a failing assertion, not a broken file.",
                $"3. d365fo test run {testClass} — expect failures. If it passes here, the assertion is empty and the test is worthless.",
                "4. Implement the behaviour.",
                "5. Build, then run again — expect green.",
                "6. d365fo validate xpp on the class you changed.",
            },
            frameworkApi =
                "Asserts come from SysTestAssert — assertEquals, assertNotEqual, assertTrue, assertFalse, assertNull, " +
                "assertNotNull, assertSame, assertNotSame, assertRealEquals, assertUTCDateTimeEquals, fail. There is " +
                "no assertExpectedException: declare it with this.parmExceptionExpected(true) before the call that " +
                "must throw. Every test runs in its own transaction and is rolled back, so created records need no " +
                "cleanup and there is no rollback attribute to add.",
            groundingToken = token,
        });
    }

    /// <summary>One method <c>prepare test</c> puts forward, with why it is on the list.</summary>
    /// <param name="Name">Method name as the scaffold call spells it.</param>
    /// <param name="Signature">The declaration, when one is known.</param>
    /// <param name="Origin">Declared on the object, or inherited from a kernel type. Null for classes.</param>
    private sealed record SuggestedTestMethod(string Name, string? Signature, string? Origin);

    /// <summary>
    /// What a test for a TABLE method has to do that a test for a class method does not. A table
    /// rule is exercised through a record buffer, and the two mistakes it invites are structural:
    /// asserting only the rejecting case (a rule that refuses every row then passes its own test),
    /// and asserting only the boolean while the message the rule writes goes unchecked.
    /// </summary>
    private static object TableTestShape(string table, string? focus)
    {
        var method = focus ?? "validateWrite";
        var isVerdict = TableDataMethods.IsVerdictMethod(method);
        var isWrite = TableDataMethods.IsWritePath(method);

        var steps = new List<string>
        {
            $"Arrange a buffer, do not select one: `{table} rec; rec.initValue();` then set exactly the fields the rule reads. " +
            "A row fetched from the database drags in whatever demo data the box happens to hold.",
        };

        if (isVerdict)
        {
            steps.Add($"Act on the buffer and keep the verdict: `boolean verdict = rec.{method}();`");
            steps.Add("Assert the verdict AND the message: SysTestAssert::assertFalse(verdict) alone passes even when the " +
                      "rule refuses every row. Pin the infolog line the rule writes with " +
                      "`this.assertExpectedInfoLogMessage(\"@MyModel:MyLabel\")` after the act.");
            steps.Add("Add the ACCEPTING case beside the rejecting one — same arrangement, a value the rule must let through, " +
                      "assertTrue. Without it the test cannot tell a working rule from one that refuses everything.");
        }
        else if (isWrite)
        {
            steps.Add($"Act inside a transaction: `ttsbegin; rec.{method}(); ttscommit;` — the write path is what is under test, " +
                      "so it has to actually run.");
            steps.Add($"Assert against a RE-READ, not against the buffer you wrote: `{table} stored = {table}::find(rec.RecId);` " +
                      "then assert on `stored`. The in-memory buffer still holds what you set, so asserting on it proves nothing.");
        }
        else
        {
            steps.Add($"Act on the buffer: `rec.{method}(…);` then assert on the fields the method is supposed to have derived.");
        }

        return new
        {
            method,
            steps,
            rollback = "Every SysTest method runs in its own transaction and is rolled back, so records created here need no " +
                       "cleanup and there is no rollback attribute to add.",
            company = "A table with SaveDataPerCompany needs a company: pass one with [SysTestCaseDataDependency('<Company>')] " +
                      "(`d365fo generate systest … --data-area-id <Company>`) rather than switching company inside the method.",
        };
    }

    /// <summary>Does the model that will hold the test reference TestEssentials? Null when unknowable here.</summary>
    private static bool? TestEssentialsReferenced(string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName)) return null;
        try
        {
            var packages = D365FoSettings.FromEnvironment().PackagesPath;
            if (string.IsNullOrWhiteSpace(packages)) return null;
            var descriptorDir = Path.Combine(packages!, modelName!, "Descriptor");
            if (!Directory.Exists(descriptorDir)) return null;
            foreach (var file in Directory.EnumerateFiles(descriptorDir, "*.xml"))
            {
                var xml = File.ReadAllText(file);
                if (System.Text.RegularExpressions.Regex.IsMatch(
                        xml, "<d2p1:string>TestEssentials</d2p1:string>",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    return true;
            }
            return false;
        }
        catch
        {
            return null;
        }
    }

    internal static string LastToken(string name)
    {
        var tokens = System.Text.RegularExpressions.Regex
            .Split(name, "(?=[A-Z])")
            .Where(t => t.Length >= 4)
            .ToList();
        return tokens.Count > 0 ? tokens[^1] : name;
    }

    /// <summary>
    /// How to change an object of this kind without overlaying it, most specific first.
    /// </summary>
    /// <remarks>
    /// The bespoke lists below say things the registry cannot — which attribute to use, which
    /// method a CoC wraps. Everything else is derived: the registry knows whether a kind has an
    /// extension type at all, so an unknown kind now gets its real extension named rather than
    /// "check the object type for supported extension mechanisms", and a kind with no extension
    /// form is told so plainly instead of being sent looking for one.
    /// </remarks>
    internal static IReadOnlyList<string> StrategiesFor(string? objectType)
    {
        var kind = D365FO.Core.ObjectTypes.ObjectTypeRegistry.NormalizeKind(objectType ?? string.Empty);

        var bespoke = kind switch
        {
            "table" => new[]
            {
                "Table extension (AxTableExtension) — add fields, indexes, relations, field groups: generate extension table <Target> <Suffix>",
                "Table extension class [ExtensionOf(tableStr(...))] — CoC on table methods: generate_object (objectType=coc)",
                "Event handler [DataEventHandler(tableStr(X), DataEventType::...)] — subscribe to data events: generate event-handler",
            },
            "class" => new[]
            {
                "Class extension [ExtensionOf(classStr(...))] — CoC on class methods: generate_object (objectType=coc)",
                "Event handler [SubscribesTo(...)] — subscribe to delegate events: generate event-handler",
            },
            "form" => new[]
            {
                "Form extension (AxFormExtension) — add controls, data sources, menu items: generate extension form <Target> <Suffix>",
                "Form extension class [ExtensionOf(formStr(...))] — CoC on form methods",
                "Form datasource extension [ExtensionOf(formDataSourceStr(...))] — CoC on DS methods",
            },
            "map" => new[]
            {
                "Map extension class [ExtensionOf(mapStr(...))] — add/wrap map methods",
            },
            "report" or "axreport" => new[]
            {
                "Extend the report's data provider class [ExtensionOf(classStr(<Report>DP))] — CoC on processReport to add or filter rows",
                "Extend the DP's temp table (AxTableExtension on <DP>Tmp) — new columns flow into the dataset",
                "New design on the existing report — an added AxReportAutoDesign leaves the shipped one intact",
                "Print management, where the report participates in it — no metadata change needed",
            },
            "entity" or "dataentityview" => new[]
            {
                "Data entity extension (AxDataEntityViewExtension) — add mapped fields, relations, field groups: generate extension dataEntityView <Target> <Suffix>",
                "Entity extension class [ExtensionOf(dataEntityViewStr(...))] — CoC on postLoad/insertEntityDataSource/etc.",
                "Computed column via a static SQL-producing method — no row-level code runs",
            },
            "privilege" or "duty" or "role" or "securityduty" or "securityrole" => new[]
            {
                "Duty extension (AxSecurityDutyExtension) / role extension (AxSecurityRoleExtension) — add references to a Microsoft-owned duty or role without overlaying it",
                "New privilege granting only the entry points you added, then reference it from a duty extension",
                "Never edit a shipped duty or role in place: the overlay is lost on every update",
            },
            _ => null,
        };

        var strategies = new List<string>(bespoke ?? []);

        // Registry-derived: does this kind have an extension object at all?
        var extension = D365FO.Core.ObjectTypes.ObjectTypeRegistry.Find(kind + "extension");
        if (extension is not null && bespoke is null)
            strategies.Add($"{extension.AotSubfolder} — extend the object rather than overlaying it: generate extension {kind} <Target> <Suffix>");
        else if (extension is null && bespoke is null)
            strategies.Add($"No extension object exists for '{objectType}' — the change belongs in a new object, or in code that subscribes to it");

        strategies.Add("New standalone class — if no suitable extension point exists");
        return strategies;
    }

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
            searched = result.Searched,
            count = items.Count,
            truncated = result.Truncated,
            items,
        }, result.Caveat is null ? null : [result.Caveat]);
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
