using System.Xml.Linq;
using D365FO.Core;
using D365FO.Core.FormPatterns;
using D365FO.Core.Scaffolding;

namespace D365FO.Mcp;

/// <summary>
/// The object types <c>generate_object</c> reached only from the shell.
/// </summary>
/// <remarks>
/// <para>
/// Seventeen of the CLI's thirty-three <c>generate</c> sub-commands had an MCP objectType and
/// sixteen did not — among them the whole SSRS stack, the workflow set, views, maps, custom
/// services, number sequences and the SysTest scaffold. An agent on the MCP surface could not
/// produce them at all, and nothing said so; it simply got "Unknown objectType" for a capability
/// the tool has.
/// </para>
/// <para>
/// These are XML-only: they return each document by name rather than writing it, which is the
/// shape the existing multi-document handlers (<c>sysoperation</c>, <c>business-event</c>) already
/// use and the one that works off a D365FO VM. Writing to disk stays with the four objectTypes
/// that do it today and with the CLI, which owns the install path, the bridge round trip and the
/// <c>--verify</c> read-back.
/// </para>
/// </remarks>
public sealed partial class ToolHandlers
{
    // ------------------------------------------------------------ projections

    private static object Doc(string name, XDocument doc) => new { name, xml = Aot(doc) };

    private static ToolResult<object> Required(string parameter, string forWhat, string? hint = null)
        => ToolResult<object>.Fail(D365FoErrorCodes.BadInput, $"{parameter} is required for {forWhat}.", hint);

    // ------------------------------------------------------------------ view

    public ToolResult<object> GenerateView(
        string name, string? query, string[]? fields, string[]? computed, string? label, string? configurationKey)
    {
        if (string.IsNullOrWhiteSpace(name)) return Required("name", "objectType=view");
        if (string.IsNullOrWhiteSpace(query))
            return Required("query", "objectType=view",
                "A view projects an AxQuery; generate the query first with objectType=query.");

        var specs = new List<ViewFieldSpec>();
        foreach (var raw in fields ?? [])
        {
            // <name>:<dataSource>[:<dataField>]
            var parts = raw.Split(':', 3, StringSplitOptions.TrimEntries);
            if (parts.Length < 2 || string.IsNullOrEmpty(parts[0]) || string.IsNullOrEmpty(parts[1]))
                return ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                    $"Invalid field '{raw}'. Expected <name>:<dataSource>[:<dataField>].");
            var dataField = parts.Length > 2 && !string.IsNullOrEmpty(parts[2]) ? parts[2] : parts[0];
            specs.Add(new ViewFieldSpec(parts[0], DataSource: parts[1], DataField: dataField));
        }
        foreach (var raw in computed ?? [])
        {
            // <name>:<viewMethod>:<type>
            var parts = raw.Split(':', 3, StringSplitOptions.TrimEntries);
            if (parts.Length < 3 || parts.Any(string.IsNullOrEmpty))
                return ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                    $"Invalid computed field '{raw}'. Expected <name>:<viewMethod>:<type>.");
            specs.Add(new ViewFieldSpec(parts[0], ViewMethod: parts[1], ComputedType: parts[2]));
        }
        if (specs.Count == 0)
            return Required("fields or computed", "objectType=view", "A view with no fields projects nothing.");

        try
        {
            return ToolResult<object>.Success(Doc(name, ViewScaffolder.View(name, query!, specs, label, configurationKey)));
        }
        catch (ArgumentException ex)
        {
            return ToolResult<object>.Fail(D365FoErrorCodes.BadInput, ex.Message);
        }
    }

    // ------------------------------------------------------------------- map

    public ToolResult<object> GenerateMap(string name, string[]? fields, string[]? mapTo, string? label)
    {
        if (string.IsNullOrWhiteSpace(name)) return Required("name", "objectType=map");

        var fieldSpecs = new List<MapFieldSpec>();
        foreach (var raw in fields ?? [])
        {
            var parts = raw.Split(':', 3, StringSplitOptions.TrimEntries);
            if (parts.Length < 2 || string.IsNullOrEmpty(parts[0]) || string.IsNullOrEmpty(parts[1]))
                return ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                    $"Invalid field '{raw}'. Expected <name>:<edt>[:<label>].");
            fieldSpecs.Add(new MapFieldSpec(parts[0], parts[1], parts.Length > 2 ? parts[2] : null));
        }
        if (fieldSpecs.Count == 0) return Required("fields", "objectType=map");

        var mappings = new List<MapTableMappingSpec>();
        foreach (var raw in mapTo ?? [])
        {
            var parts = raw.Split(':', 2, StringSplitOptions.TrimEntries);
            var table = parts.Length > 0 ? parts[0] : "";
            if (string.IsNullOrEmpty(table))
                return ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                    $"Invalid mapping '{raw}'. Expected <table>[:<mapField>=<tableField>,…].");

            List<MapFieldConnection> connections;
            if (parts.Length < 2 || string.IsNullOrEmpty(parts[1]))
            {
                // No explicit pairs — connect every map field to the identically named table field.
                connections = fieldSpecs.Select(f => new MapFieldConnection(f.Name)).ToList();
            }
            else
            {
                connections = [];
                foreach (var pair in parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var eq = pair.Split('=', 2, StringSplitOptions.TrimEntries);
                    if (eq.Length == 0 || string.IsNullOrEmpty(eq[0]))
                        return ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                            $"Invalid connection '{pair}' in mapping '{raw}'. Expected <mapField>=<tableField>.");
                    connections.Add(new MapFieldConnection(eq[0], eq.Length > 1 && !string.IsNullOrEmpty(eq[1]) ? eq[1] : null));
                }
            }
            mappings.Add(new MapTableMappingSpec(table, connections));
        }

        try
        {
            return ToolResult<object>.Success(Doc(name, MapScaffolder.Map(name, fieldSpecs, mappings, label, EdtBaseType)));
        }
        catch (ArgumentException ex)
        {
            return ToolResult<object>.Fail(D365FoErrorCodes.BadInput, ex.Message);
        }
    }

    /// <summary>EDT → primitive base type, so a scaffolded field gets its concrete i:type (issue #91).</summary>
    private string? EdtBaseType(string edt)
    {
        if (string.IsNullOrWhiteSpace(edt)) return null;
        try { return _repo.GetEdt(edt)?.BaseType; }
        catch { return null; }
    }

    // --------------------------------------------------------------- systest

    public ToolResult<object> GenerateSysTest(
        string name, string? dataAreaId, bool atl, string? targetClass, string? targetTable, string[]? methods)
    {
        if (string.IsNullOrWhiteSpace(name)) return Required("name", "objectType=systest");

        var target = string.IsNullOrWhiteSpace(targetClass) ? targetTable : targetClass;
        var doc = SysTestScaffolder.TestClass(
            name, dataAreaId, atl, methods, target,
            targetIsTable: string.IsNullOrWhiteSpace(targetClass) && !string.IsNullOrWhiteSpace(targetTable));

        return ToolResult<object>.Success(Doc(name, doc), [
            "The scaffolded methods end in this.fail(...) on purpose: the first run is RED, and a test "
            + "that passes before it is written proves nothing.",
        ]);
    }

    // ------------------------------------------------------ migration script

    public ToolResult<object> GenerateMigrationScript(
        string name, string? sourceTable, string? targetTable, string? mode, int batchSize)
    {
        if (string.IsNullOrWhiteSpace(name)) return Required("name", "objectType=migration-script");
        if (string.IsNullOrWhiteSpace(sourceTable)) return Required("sourceTable", "objectType=migration-script");
        if (string.IsNullOrWhiteSpace(targetTable)) return Required("targetTable", "objectType=migration-script");

        if (!Enum.TryParse<MigrationMode>(mode, ignoreCase: true, out var parsed))
        {
            if (!string.IsNullOrWhiteSpace(mode))
                return ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                    $"Unknown mode '{mode}'.", "Use insert, update or upsert.");
            parsed = MigrationMode.Insert;
        }

        return ToolResult<object>.Success(Doc(name,
            MigrationScriptScaffolder.MigrationClass(name, sourceTable!, targetTable!, parsed,
                batchSize > 0 ? batchSize : 1000)));
    }

    // -------------------------------------------------------- custom service

    public ToolResult<object> GenerateCustomService(
        string name, string? className, string? externalName, string? groupName,
        string[]? operations, string? contractParam)
    {
        if (string.IsNullOrWhiteSpace(name)) return Required("name", "objectType=custom-service");

        var serviceClass = string.IsNullOrWhiteSpace(className) ? name + "Service" : className!;
        var group = string.IsNullOrWhiteSpace(groupName) ? name + "ServiceGroup" : groupName!;

        var ops = new List<OperationSpec>();
        foreach (var raw in operations ?? [])
        {
            // <name>[:<returnType>]
            var parts = raw.Split(':', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 0 || string.IsNullOrEmpty(parts[0]))
                return ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                    $"Invalid operation '{raw}'. Expected <name>[:<returnType>].");
            ops.Add(new OperationSpec(parts[0],
                parts.Length > 1 && !string.IsNullOrEmpty(parts[1]) ? parts[1] : "void",
                contractParam));
        }
        if (ops.Count == 0) ops.Add(new OperationSpec("process", "void", contractParam));

        return ToolResult<object>.Success(new
        {
            name,
            serviceClass = Doc(serviceClass, CustomServiceScaffolder.ServiceClass(serviceClass, ops)),
            service = Doc(name, CustomServiceScaffolder.ServiceXml(name, serviceClass, ops, externalName)),
            serviceGroup = Doc(group, CustomServiceScaffolder.ServiceGroupXml(group, name)),
        });
    }

    // ------------------------------------------------------- number sequence

    public ToolResult<object> GenerateNumberSequence(
        string moduleName, string? edt, string? edtLabel, string? scope, string? table)
    {
        if (string.IsNullOrWhiteSpace(moduleName)) return Required("name", "objectType=number-sequence");

        if (!Enum.TryParse<NumberSequenceScope>(scope, ignoreCase: true, out var parsedScope))
        {
            if (!string.IsNullOrWhiteSpace(scope))
                return ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                    $"Unknown scope '{scope}'.", "Use company or shared.");
            parsedScope = NumberSequenceScope.Company;
        }

        var edtName = string.IsNullOrWhiteSpace(edt) ? moduleName + "Id" : edt!;
        var moduleClass = NumberSequenceScaffolder.ModuleClassName(moduleName);

        var documents = new List<object>
        {
            Doc(moduleClass + "_Extension", NumberSequenceScaffolder.ModuleExtension(moduleName, edtName, parsedScope)),
            Doc(edtName, NumberSequenceScaffolder.Edt(edtName, moduleName, parsedScope, edtLabel)),
        };

        if (!string.IsNullOrWhiteSpace(table))
        {
            var handler = table + "NumberSeqHandler";
            documents.Add(Doc(handler, NumberSequenceScaffolder.FormHandler(table!, edtName, handler, moduleName)));
        }

        return ToolResult<object>.Success(new
        {
            module = moduleName,
            moduleClass,
            edt = edtName,
            scope = parsedScope.ToString(),
            documents,
            note = table is null
                ? "Pass `table` to also scaffold the form handler that stamps the number on insert."
                : null,
        });
    }

    // -------------------------------------------------------------- workflow

    public ToolResult<object> GenerateWorkflow(
        string name, string? table, string? approvalName, string? taskName, string? category,
        string? documentMenuItem, string? submitMenuItem, string? documentClass, string? query)
    {
        if (string.IsNullOrWhiteSpace(name)) return Required("name", "objectType=workflow");

        var document = string.IsNullOrWhiteSpace(documentClass) ? name + "Document" : documentClass!;
        var approval = string.IsNullOrWhiteSpace(approvalName) ? null : approvalName;
        var task = string.IsNullOrWhiteSpace(taskName) ? null : taskName;

        var documents = new List<object>
        {
            Doc(name, WorkflowScaffolder.WorkflowTemplate(
                name, document, category, documentMenuItem, submitMenuItem, approval, task)),
            Doc(document, WorkflowScaffolder.WorkflowDocument(document, query)),
        };

        if (approval is not null)
            documents.Add(Doc(approval, WorkflowScaffolder.WorkflowApproval(approval, document, documentMenuItem)));
        if (task is not null)
            documents.Add(Doc(task, WorkflowScaffolder.WorkflowTask(task, document, documentMenuItem)));
        if (!string.IsNullOrWhiteSpace(table))
            documents.Add(Doc(table + "_Extension", WorkflowScaffolder.CanSubmitExtension(table!)));

        return ToolResult<object>.Success(new
        {
            name,
            documentClass = document,
            documents,
            note = "A workflow type is inert until its elements are reachable: the document menu item "
                 + "launches it and canSubmitToWorkflow decides when the Submit button appears.",
        });
    }

    // ---------------------------------------------------------------- report

    public ToolResult<object> GenerateReport(
        string name, string? dpClass, string? tmpTable, string? datasetName, string? caption,
        string[]? fields, string[]? parameters, bool preProcess, string? controllerType, bool uiBuilder)
    {
        if (string.IsNullOrWhiteSpace(name)) return Required("name", "objectType=report");

        var parameterSpecs = new List<ReportParameterSpec>();
        foreach (var raw in parameters ?? [])
        {
            // <name>[:<dataType>]
            var parts = raw.Split(':', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 0 || string.IsNullOrEmpty(parts[0]))
                return ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                    $"Invalid parameter '{raw}'. Expected <name>[:<dataType>].");
            parameterSpecs.Add(new ReportParameterSpec(parts[0],
                parts.Length > 1 && !string.IsNullOrEmpty(parts[1]) ? parts[1] : "String"));
        }

        var printMgmt = string.Equals(controllerType, "print-mgmt", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(controllerType, "printmgmt", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(controllerType) && !printMgmt
            && !string.Equals(controllerType, "standard", StringComparison.OrdinalIgnoreCase))
        {
            return ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                $"Unknown controllerType '{controllerType}'.", "Use standard or print-mgmt.");
        }

        var spec = new ReportSpec(
            name, dpClass, tmpTable, datasetName, caption,
            Fields: fields is { Length: > 0 } ? fields : null,
            Parameters: parameterSpecs.Count > 0 ? parameterSpecs : null)
        {
            PreProcess = preProcess,
            PrintMgmtController = printMgmt,
            UiBuilder = uiBuilder,
        };

        var documents = new List<object>
        {
            Doc(name, XppScaffolder.Report(spec)),
            Doc(spec.EffectiveDpClass, XppScaffolder.ReportDp(spec)),
            Doc(spec.EffectiveTmpTable, XppScaffolder.ReportTmpTable(spec.EffectiveDatasets[0], caption)),
            Doc(spec.EffectiveController, XppScaffolder.ReportController(spec)),
        };

        if (XppScaffolder.ReportContract(spec) is { } contract) documents.Add(Doc(spec.ContractClass, contract));
        if (XppScaffolder.ReportUiBuilder(spec) is { } builder) documents.Add(Doc(spec.EffectiveDpClass + "UIBuilder", builder));

        return ToolResult<object>.Success(new
        {
            name,
            dpClass = spec.EffectiveDpClass,
            tmpTable = spec.EffectiveTmpTable,
            controller = spec.EffectiveController,
            preProcess,
            printMgmtController = printMgmt,
            documents,
            note = "An SSRS report is a stack, not a file: the AxReport carries the dataset shape, the DP "
                 + "fills the temp table and the controller launches it. The RDL design is authored in "
                 + "Visual Studio — nothing here can produce it.",
        });
    }

    // ------------------------------------------------------ report extension

    public ToolResult<object> GenerateReportExtension(
        string pattern, string? dpClass, string? tmpTable, string? datasetAccessor, string? report,
        string? design, string? documentType, string? baseController, string? controller, string? suffix)
    {
        var effectiveSuffix = string.IsNullOrWhiteSpace(suffix) ? "Extension" : suffix!;

        switch ((pattern ?? "").ToLowerInvariant())
        {
            case "dataset":
                if (string.IsNullOrWhiteSpace(dpClass)) return Required("dp", "the dataset pattern");
                if (string.IsNullOrWhiteSpace(tmpTable)) return Required("tmpTable", "the dataset pattern");
                var datasetClass = dpClass + "_" + effectiveSuffix;
                return ToolResult<object>.Success(new
                {
                    pattern = "dataset",
                    documents = new[]
                    {
                        Doc(datasetClass, ReportExtensionScaffolder.DatasetExtension(
                            dpClass!, tmpTable!, datasetClass, datasetAccessor)),
                    },
                });

            case "custom-design":
                if (string.IsNullOrWhiteSpace(report)) return Required("report", "the custom-design pattern");
                if (string.IsNullOrWhiteSpace(design)) return Required("design", "the custom-design pattern");
                var docs = new List<object>
                {
                    Doc(report + "Controller", ReportExtensionScaffolder.CustomDesignController(
                        report!, design!, string.IsNullOrWhiteSpace(baseController) ? "SrsReportRunController" : baseController!)),
                };
                if (!string.IsNullOrWhiteSpace(documentType))
                    docs.Add(Doc(report + "PrintMgmtHandler",
                        ReportExtensionScaffolder.CustomDesignPrintMgmtHandler(report!, design!, documentType!)));
                return ToolResult<object>.Success(new { pattern = "custom-design", documents = docs });

            case "menu-redirect":
                if (string.IsNullOrWhiteSpace(controller)) return Required("controller", "the menu-redirect pattern");
                if (string.IsNullOrWhiteSpace(report)) return Required("report", "the menu-redirect pattern");
                if (string.IsNullOrWhiteSpace(design)) return Required("design", "the menu-redirect pattern");
                var redirectClass = controller + "_" + effectiveSuffix;
                return ToolResult<object>.Success(new
                {
                    pattern = "menu-redirect",
                    documents = new[]
                    {
                        Doc(redirectClass, ReportExtensionScaffolder.MenuRedirect(
                            controller!, report!, design!, redirectClass)),
                    },
                });

            default:
                return ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                    $"Unknown report-extension pattern '{pattern}'.",
                    "Use dataset, custom-design or menu-redirect — the three ways a shipped report can be "
                    + "extended without editing it.");
        }
    }

    // --------------------------------------------------------- event handler

    public ToolResult<object> GenerateEventHandler(
        string name, string? sourceKind, string? sourceObject, string? eventName, string? method)
    {
        if (string.IsNullOrWhiteSpace(name)) return Required("name", "objectType=event-handler");
        if (string.IsNullOrWhiteSpace(sourceKind)) return Required("sourceKind", "objectType=event-handler",
            "Form | FormDataSource | FormControl | Table | Class.");
        if (string.IsNullOrWhiteSpace(sourceObject)) return Required("sourceObject", "objectType=event-handler");
        if (string.IsNullOrWhiteSpace(eventName)) return Required("event", "objectType=event-handler");

        return ToolResult<object>.Success(Doc(name, XppScaffolder.EventHandler(
            name, sourceKind!, sourceObject!, eventName!,
            string.IsNullOrWhiteSpace(method) ? "OnEvent" : method!)));
    }

    // ------------------------------------------------- table augmentation

    public ToolResult<object> GenerateFindMethods(
        string table, string[]? keys, bool includeExists, bool includeFindRecId)
    {
        if (string.IsNullOrWhiteSpace(table)) return Required("table", "objectType=find-methods");

        var details = _repo.GetTableDetails(table);
        if (details is null)
            return ToolResult<object>.Fail("NOT_FOUND", $"Table '{table}' is not in the index.",
                "Run `d365fo index extract` (or refresh), or check the spelling with search(type=table).");

        var keyFields = TableAugmentScaffolder.ResolveKeyFields(details, keys);
        if (keyFields.Count == 0)
            return ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                $"'{table}' has no unique index to key find() on.",
                "Pass `keys` explicitly, or add a unique index first with modify_object(action=add-index).");

        var methods = TableAugmentScaffolder.BuildFindMethods(table, keyFields, includeExists, includeFindRecId);
        return ToolResult<object>.Success(new
        {
            table,
            keys = keyFields.Select(k => new { k.Field, k.Type }),
            count = methods.Count,
            methods = methods.Select(m => new { m.Name, source = m.Source }),
            note = "These are method bodies, not a document: merge them into the table's own XML "
                 + "(`d365fo generate find-methods <TABLE> --apply-to <PATH>` does the merge).",
        });
    }

    public ToolResult<object> GenerateTableRelation(string table, string[]? fields)
    {
        if (string.IsNullOrWhiteSpace(table)) return Required("table", "objectType=table-relation");

        var details = _repo.GetTableDetails(table);
        if (details is null)
            return ToolResult<object>.Fail("NOT_FOUND", $"Table '{table}' is not in the index.",
                "Run `d365fo index extract` (or refresh), or check the spelling with search(type=table).");

        var relations = TableAugmentScaffolder.DeriveRelations(
            details, edt => { try { return _repo.GetEdt(edt); } catch { return null; } }, fields, out var skipped);

        return ToolResult<object>.Success(new
        {
            table,
            count = relations.Count,
            relations = relations.Select(r => new
            {
                r.Field,
                r.Edt,
                relatedTable = r.RelatedTable,
                relatedField = r.RelatedField,
                xml = TableAugmentScaffolder.RelationElement(r).ToString(),
            }),
            skipped = skipped.Count > 0 ? skipped : null,
            note = "Fragments, not a document: merge them into the table's Relations "
                 + "(`d365fo generate table-relation <TABLE> --apply-to <PATH>` does the merge).",
        }, skipped.Count > 0 ? [$"{skipped.Count} field(s) skipped — see `skipped`."] : null);
    }

    // ------------------------------------------------------------ form clone

    public ToolResult<object> GenerateFormClone(string name, string? from, string[]? rebind)
    {
        if (string.IsNullOrWhiteSpace(name)) return Required("name", "objectType=form-clone");
        if (string.IsNullOrWhiteSpace(from)) return Required("from", "objectType=form-clone",
            "The form to clone: an indexed form name, or a path to its AxForm XML.");

        var (sourceXml, readError) = AotSourceReader.ReadForm(_repo, from!);
        if (readError is not null)
            return ToolResult<object>.Fail(D365FoErrorCodes.SourceUnreadable, readError);

        var rebinds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var spec in (rebind ?? []).Where(r => !string.IsNullOrWhiteSpace(r)))
        {
            var parts = spec.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
                return ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                    $"Invalid rebind '{spec}'. Expected <OldTable>=<NewTable>.");
            rebinds[parts[0]] = parts[1];
        }

        try
        {
            var clone = FormCloner.Clone(sourceXml!, name, rebinds);
            return ToolResult<object>.Success(new
            {
                name,
                source = from,
                rebound = rebinds.Count,
                xml = clone.Xml,
            }, clone.Warnings.Count > 0 ? clone.Warnings.ToList() : null);
        }
        catch (FormCloneException ex)
        {
            return ToolResult<object>.Fail("CLONE_FAILED", ex.Message);
        }
    }

    // -------------------------------------------------------- form methods

    /// <param name="control">Set for a control method; leave null for a datasource method.</param>
    public ToolResult<object> GenerateFormMethod(
        string form, string? dataSource, string? control, string? method, string? returnType, string? body)
    {
        if (string.IsNullOrWhiteSpace(form)) return Required("form", "the form-method object types");

        var (sourceXml, readError) = AotSourceReader.ReadForm(_repo, form!);
        if (readError is not null)
            return ToolResult<object>.Fail(D365FoErrorCodes.SourceUnreadable, readError);

        var onControl = !string.IsNullOrWhiteSpace(control);

        XDocument doc;
        try { doc = XDocument.Parse(sourceXml!); }
        catch (Exception ex)
        {
            return ToolResult<object>.Fail(D365FoErrorCodes.SourceUnreadable,
                $"'{form}' is not readable XML: {ex.Message}");
        }

        var target = onControl ? FormMethodCatalog.Target.Control : FormMethodCatalog.Target.DataSource;

        // No method named: list what can be overridden here, which is the question an agent has
        // before it can pick one.
        if (string.IsNullOrWhiteSpace(method))
        {
            return ToolResult<object>.Success(new
            {
                form,
                members = onControl
                    ? FormMethodScaffolder.ListControlNames(doc)
                    : FormMethodScaffolder.ListDataSourceNames(doc),
                overridable = FormMethodCatalog.List(target)
                    .Select(m => new { m.Name, m.ReturnType, m.Parameters }),
                note = "Pass `method` to inject one. The signature comes from the catalog unless "
                     + "`returnType` overrides it, which emits a parameterless stub.",
            });
        }

        var owner = onControl ? control! : dataSource;
        if (string.IsNullOrWhiteSpace(owner))
            return Required(onControl ? "control" : "dataSource", "injecting a form method");

        var warnings = new List<string>();
        var sig = FormMethodCatalog.TryGet(target, method!);
        if (sig is null)
        {
            if (string.IsNullOrWhiteSpace(returnType))
                return ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                    $"'{method}' is not a catalogued {target.ToString().ToLowerInvariant()} method.",
                    "Call this with no `method` for the catalogued list, or pass `returnType` to emit an "
                    + "uncatalogued stub — whose parameters you then have to check against the base method yourself.");
            sig = new FormMethodSignature(method!, returnType!.Trim());
            warnings.Add($"'{method}' is not catalogued; emitted a parameterless {sig.ReturnType} stub. "
                       + "Verify its parameters match the framework base method.");
        }

        try
        {
            var injected = onControl
                ? FormMethodScaffolder.InjectControlMethod(doc, owner!, sig, body, overwrite: false)
                : FormMethodScaffolder.InjectDataSourceMethod(doc, owner!, sig, body, overwrite: false);

            if (injected.AlreadyExisted && !injected.Changed)
                return ToolResult<object>.Fail("ALREADY_EXISTS",
                    $"Method '{sig.Name}' already exists on {target.ToString().ToLowerInvariant()} '{owner}'.",
                    "Change its body with modify_method instead — this tool does not overwrite.");

            return ToolResult<object>.Success(new
            {
                form,
                member = owner,
                method = sig.Name,
                returnType = sig.ReturnType,
                xml = Aot(doc),
                note = "The whole AxForm is returned with the method injected — form methods live in the "
                     + "form's own SourceCode, not in a file of their own.",
            }, warnings.Count > 0 ? warnings : null);
        }
        catch (FormMethodScaffolder.FormMethodException ex)
        {
            return ToolResult<object>.Fail(ex.Code, ex.Message);
        }
    }
}
