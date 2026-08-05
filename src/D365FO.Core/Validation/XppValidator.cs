using System.Text.RegularExpressions;

namespace D365FO.Core.Validation;

/// <summary>One offline best-practice finding produced by <see cref="XppValidator"/>.</summary>
public sealed record XppViolation(string Rule, string Severity, int? Line, string Excerpt, string Fix);

/// <summary>
/// Provider of mined property statistics (PropertyStats table). When absent the
/// XML002–XML005 property rules fall back to static defaults.
/// </summary>
public interface IPropertyStatsProvider
{
    /// <summary>Presence ratio of <paramref name="property"/> on standard-model nodes of <paramref name="nodeType"/>.</summary>
    (long Present, long Total, double Ratio) GetPropertyPresenceRatio(string nodeType, string property);

    /// <summary>Most common values of <paramref name="property"/> across standard models.</summary>
    IReadOnlyList<(string Value, long Count)> GetPropertyValueDistribution(string nodeType, string property, int limit = 5);
}

/// <summary>
/// Offline X++ / XML Best Practice validator. Port of the upstream MCP server's
/// <c>validate_xpp</c> tool: checks generated code against the D365FO rule canon
/// without requiring xppbp.exe or a Windows VM.
///
/// Rules:
///   SEL001  today() deprecated
///   SEL002  forceLiterals forbidden (SQL injection risk)
///   SEL003  crossCompany on joined buffer (must be on driving buffer)
///   SEL004  Nested while select (N+1 query anti-pattern)
///   SEL005  Function call in where clause (assign to variable first)
///   COC001  Default param value copied into CoC wrapper signature
///   COC002  [ExtensionOf] class not declared final
///   COC003  [ExtensionOf] class name not ending _Extension
///   BP001   Hardcoded string literal in info/warning/error/checkFailed
///   BP002   doInsert/doUpdate/doDelete outside explicit migration comment
///   BP003   Generic doc-comment (/// Foo class. / /// methodName.)
///   XML001  AxTable XML missing an index with &lt;AlternateKey&gt;Yes&lt;/AlternateKey&gt;
///   XML002  AxTable missing &lt;Label&gt;            (data-driven)
///   XML003  AxTable missing &lt;TableGroup&gt;       (data-driven, suggests common standard values)
///   XML004  AxTableField without &lt;ExtendedDataType&gt;/&lt;EnumType&gt; (data-driven)
///   XML005  AxTable missing &lt;ClusteredIndex&gt;   (only when standard usage ≥ threshold)
/// </summary>
public static class XppValidator
{
    public const string CodeTypeXpp = "xpp";
    public const string CodeTypeXmlTable = "xml-table";
    public const string CodeTypeXmlAny = "xml-any";

    /// <summary>A property rule fires when the standard platform sets it at least this often.</summary>
    public const double PropertyRuleThreshold = 0.8;

    /// <summary>Behaviour when no mined statistics are available.</summary>
    private static readonly Dictionary<string, bool> StaticPropertyDefaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AxTable.Label"] = true,
        ["AxTable.TableGroup"] = true,
        ["AxTableField.ExtendedDataType"] = true,
        ["AxTable.ClusteredIndex"] = false, // only enforced when stats prove standard usage
    };

    private static readonly HashSet<string> IntrinsicFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "fieldnum", "tablenum", "classstr", "methodstr", "formstr", "tablestr",
        "enumnum", "extendedtypenum", "identifierstr", "literalstr", "resourcestr",
        "ssrsreportstr", "fieldstr", "querystr", "dataentitydatasourcestr",
        "formdatasourcestr", "formcontrolstr", "delegatestr", "enumstr",
        "classnum", "formnum", "reportstr", "menuitemactionstr", "menuitemdisplaystr",
        "menuitemoutputstr", "varstr", "con2str", "int2str", "num2str",
    };

    public static IReadOnlyList<XppViolation> Validate(string code, string codeType = CodeTypeXpp, IPropertyStatsProvider? stats = null)
    {
        var violations = new List<XppViolation>();
        var normalized = NormalizeCodeType(codeType);
        if (normalized == CodeTypeXpp)
        {
            RunXppRules(code, violations);
        }
        else if (normalized == CodeTypeXmlTable)
        {
            RunXppRules(code, violations);
            CheckMissingAlternateKey(code, violations);
            CheckTableProperties(code, stats, violations);
            CheckFieldEdt(code, stats, violations);
        }
        else
        {
            CheckMissingAlternateKey(code, violations);
            CheckTableProperties(code, stats, violations);
            CheckFieldEdt(code, stats, violations);
        }
        return violations;
    }

    public static string NormalizeCodeType(string? codeType) => codeType?.ToLowerInvariant() switch
    {
        "xml-table" or "xmltable" or "table-xml" => CodeTypeXmlTable,
        "xml-any" or "xmlany" or "xml" => CodeTypeXmlAny,
        _ => CodeTypeXpp,
    };

    private static void RunXppRules(string code, List<XppViolation> violations)
    {
        CheckTodayDeprecated(code, violations);
        CheckForceLiterals(code, violations);
        CheckCrossCompanyPlacement(code, violations);
        CheckNestedWhileSelect(code, violations);
        CheckFunctionInWhere(code, violations);
        CheckCocDefaultParam(code, violations);
        CheckExtensionOfNotFinal(code, violations);
        CheckExtensionOfNaming(code, violations);
        CheckHardcodedStrings(code, violations);
        CheckDoMethods(code, violations);
        CheckGenericDocComment(code, violations);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static int LineNumber(string code, int index)
        => code.AsSpan(0, Math.Min(index, code.Length)).Count('\n') + 1;

    private static void MatchAll(string code, string pattern, string rule, string severity, string fix,
        List<XppViolation> violations, bool skipIfComment = true)
    {
        var lines = code.Split('\n');
        foreach (Match m in Regex.Matches(code, pattern, RegexOptions.IgnoreCase))
        {
            var lineIdx = LineNumber(code, m.Index) - 1;
            var lineText = lineIdx < lines.Length ? lines[lineIdx].TrimStart() : "";
            if (skipIfComment && (lineText.StartsWith("//") || lineText.StartsWith('*'))) continue;
            violations.Add(new XppViolation(rule, severity, lineIdx + 1, m.Value.Trim(), fix));
        }
    }

    // ── X++ rules ────────────────────────────────────────────────────────────

    private static void CheckTodayDeprecated(string code, List<XppViolation> v) => MatchAll(code,
        @"\btoday\s*\(\s*\)", "SEL001", "error",
        "Replace today() with DateTimeUtil::getToday(DateTimeUtil::getUserPreferredTimeZone()). " +
        "today() ignores user time zone and fails BPUpgradeCodeToday.", v);

    private static void CheckForceLiterals(string code, List<XppViolation> v) => MatchAll(code,
        @"\bforceLiterals\b", "SEL002", "error",
        "Remove forceLiterals. Use forcePlaceholders (default for non-join selects) or omit. " +
        "forceLiterals exposes the query to SQL injection.", v);

    private static void CheckCrossCompanyPlacement(string code, List<XppViolation> v) => MatchAll(code,
        @"\bjoin\s+crossCompany\b", "SEL003", "error",
        "Move crossCompany to the outer select (driving buffer): \"select crossCompany tableBuffer join …\". " +
        "crossCompany is a query-level option, not a per-join option.", v);

    private static void CheckNestedWhileSelect(string code, List<XppViolation> v)
    {
        var lines = code.Split('\n');
        var whileSelectLines = new List<int>();
        for (int i = 0; i < lines.Length; i++)
        {
            if (Regex.IsMatch(lines[i], @"\bwhile\s+select\b", RegexOptions.IgnoreCase)
                && !lines[i].TrimStart().StartsWith("//"))
            {
                whileSelectLines.Add(i + 1);
            }
        }
        if (whileSelectLines.Count >= 2 && !Regex.IsMatch(code, @"\bjoin\b", RegexOptions.IgnoreCase))
        {
            v.Add(new XppViolation("SEL004", "warning", whileSelectLines[1],
                $"while select at lines {string.Join(", ", whileSelectLines)}",
                "Replace nested while select with a join in a single while select, or " +
                "pre-load the inner data into a Map/temp table. " +
                "Nested while select causes N+1 database queries (BPCheckNestedLoopinCode)."));
        }
    }

    private static void CheckFunctionInWhere(string code, List<XppViolation> v)
    {
        var lines = code.Split('\n');
        // inWhere carries an open where-clause across wrapped lines. It must be
        // closed at the clause's actual boundary — the statement terminator `;`
        // or a block-open `{` — otherwise every function call in the rest of
        // the file (including unrelated later methods) gets misattributed to
        // "inside where clause".
        bool inWhere = false;
        for (int i = 0; i < lines.Length; i++)
        {
            var rawLine = lines[i];
            var line = rawLine.TrimStart();
            if (line.StartsWith("//") || line.StartsWith('*')) continue;

            // Scanning starts right after the `where` keyword when a new clause
            // opens on this line (so a count(...) in the select list before the
            // `where` is never in scope), or at the top of the line when a
            // clause opened on an earlier line is still open.
            int scanStart = 0;
            if (!inWhere)
            {
                var whereMatch = Regex.Match(rawLine, @"\bwhere\b", RegexOptions.IgnoreCase);
                if (!whereMatch.Success) continue;
                inWhere = true;
                scanStart = whereMatch.Index + whereMatch.Length;
            }

            // The clause ends at the first `;` or `{` at/after scanStart — only
            // scan the segment before that terminator.
            var rest = rawLine[scanStart..];
            int endIdx = rest.IndexOfAny([';', '{']);
            var scanSegment = endIdx >= 0 ? rest[..endIdx] : rest;

            foreach (Match m in Regex.Matches(scanSegment, @"\b([a-zA-Z_]\w*)\s*\("))
            {
                var fnName = m.Groups[1].Value;
                if (IntrinsicFunctions.Contains(fnName)) continue;
                if (fnName.ToLowerInvariant() is "if" or "while" or "for" or "switch" or "catch" or "str" or "int" or "new" or "where") continue;
                v.Add(new XppViolation("SEL005", "warning", i + 1,
                    $"{fnName}(...) inside where clause",
                    $"Assign the result of {fnName}() to a local variable BEFORE the select statement, " +
                    "then use the variable in the where clause. " +
                    "Function calls in where clauses prevent index usage and may cause unexpected results."));
                break; // one violation per line is enough
            }

            if (endIdx >= 0) inWhere = false;
        }
    }

    private static void CheckCocDefaultParam(string code, List<XppViolation> v)
    {
        if (!Regex.IsMatch(code, @"\[ExtensionOf\s*\(", RegexOptions.IgnoreCase)) return;
        var lines = code.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var rawLine = lines[i];
            if (rawLine.TrimStart().StartsWith("//")) continue;
            if (!Regex.IsMatch(rawLine, @"\b(public|protected|private|internal)\b.*\([^)]*=\s*[^,)]+\)")) continue;
            // Constructors and parm* accessors keep their defaults intentionally.
            if (Regex.IsMatch(rawLine, @"\bnew\s*\(")) continue;
            if (Regex.IsMatch(rawLine, @"\bparm[A-Z]")) continue;
            v.Add(new XppViolation("COC001", "error", i + 1, rawLine.Trim(),
                "Remove default parameter values from CoC wrapper signatures. " +
                "The base method's defaults are already in effect when calling next. " +
                "Example: \"public void salute(str message)\" NOT \"public void salute(str message = \\\"Hi\\\")\"."));
        }
    }

    private static void CheckExtensionOfNotFinal(string code, List<XppViolation> v)
    {
        var lines = code.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains("[ExtensionOf", StringComparison.OrdinalIgnoreCase)) continue;
            for (int j = i; j <= Math.Min(i + 3, lines.Length - 1); j++)
            {
                if (!Regex.IsMatch(lines[j], @"\bclass\b", RegexOptions.IgnoreCase)) continue;
                if (!Regex.IsMatch(lines[j], @"\bfinal\b", RegexOptions.IgnoreCase))
                {
                    v.Add(new XppViolation("COC002", "error", j + 1, lines[j].Trim(),
                        "Extension classes must be declared final: \"[ExtensionOf(...)] final class MyClass_Extension\". " +
                        "Without final the compiler will reject the file."));
                }
                break;
            }
        }
    }

    private static void CheckExtensionOfNaming(string code, List<XppViolation> v)
    {
        var lines = code.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains("[ExtensionOf", StringComparison.OrdinalIgnoreCase)) continue;
            for (int j = i; j <= Math.Min(i + 3, lines.Length - 1); j++)
            {
                var m = Regex.Match(lines[j], @"\bclass\s+(\w+)", RegexOptions.IgnoreCase);
                if (!m.Success) continue;
                if (!m.Groups[1].Value.EndsWith("_Extension", StringComparison.Ordinal))
                {
                    v.Add(new XppViolation("COC003", "error", j + 1, lines[j].Trim(),
                        $"Rename class to \"{m.Groups[1].Value}_Extension\". " +
                        "Extension classes must end with _Extension per MS naming guidelines."));
                }
                break;
            }
        }
    }

    private static void CheckHardcodedStrings(string code, List<XppViolation> v)
    {
        var lines = code.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimStart();
            if (line.StartsWith("//") || line.StartsWith('*')) continue;
            foreach (Match m in Regex.Matches(lines[i], "\\b(?:info|warning|error|checkFailed)\\s*\\(\\s*\"(?!@)([^\"]{1,200})\"", RegexOptions.IgnoreCase))
            {
                v.Add(new XppViolation("BP001", "error", i + 1, m.Value.Trim(),
                    "Replace the hardcoded string with a label reference: info(\"@ModelName:LabelId\"). " +
                    "Use `d365fo search label` to find an existing label, or `d365fo label create` if none exists. " +
                    "Hardcoded strings fail BPErrorLabelIsText."));
            }
        }
    }

    private static void CheckDoMethods(string code, List<XppViolation> v) => MatchAll(code,
        @"\.\s*do(?:Insert|Update|Delete)\s*\(\s*\)", "BP002", "warning",
        "doInsert/doUpdate/doDelete bypasses overridden methods and event handlers. " +
        "Use insert()/update()/delete() in production code. " +
        "Reserve do* variants for data-fix / migration scripts and add a comment explaining why.", v);

    private static void CheckGenericDocComment(string code, List<XppViolation> v)
    {
        var lines = code.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var l = lines[i].Trim();
            if (!l.StartsWith("///")) continue;
            if (Regex.IsMatch(l, @"^///\s+\w+\s+(?:class|method|table|form|enum|edt|query|view)\.?\s*$", RegexOptions.IgnoreCase))
            {
                v.Add(new XppViolation("BP003", "warning", i + 1, l,
                    "Replace the generic doc-comment with a meaningful description of what the class/method does. " +
                    "Example: \"/// Validates the record before it is written to the database.\" " +
                    "Generic comments like \"/// MyClass class.\" fail BPXmlDocNoDocumentationComments."));
            }
            var singleWord = Regex.Match(l, @"^///\s+(\w+)\.?\s*$");
            if (singleWord.Success && i + 1 < lines.Length)
            {
                var nextCode = lines[i + 1].Trim();
                var word = singleWord.Groups[1].Value;
                if (nextCode.Contains(word + "(") || nextCode.Contains(word + " "))
                {
                    v.Add(new XppViolation("BP003", "warning", i + 1, l,
                        $"Replace \"/// {word}.\" with a sentence describing what this member does. " +
                        "Repeating the method name as the doc-comment fails BPXmlDocNoDocumentationComments."));
                }
            }
        }
    }

    // ── XML rules ────────────────────────────────────────────────────────────

    private static void CheckMissingAlternateKey(string code, List<XppViolation> v)
    {
        if (!Regex.IsMatch(code, @"<AxTable(Extension)?[\s>]", RegexOptions.IgnoreCase)) return;
        if (!Regex.IsMatch(code, @"<AlternateKey>\s*Yes\s*</AlternateKey>", RegexOptions.IgnoreCase))
        {
            v.Add(new XppViolation("XML001", "error", null,
                "<AxTable> — no index with <AlternateKey>Yes</AlternateKey>",
                "Add at least one <AxTableIndex> with <AlternateKey>Yes</AlternateKey>. " +
                "D365FO requires every table to have an alternate key index (BPCheckAlternateKeyAbsent). " +
                "`d365fo generate table` adds this automatically."));
        }
    }

    private static (bool Applies, string Evidence) PropertyRuleApplies(IPropertyStatsProvider? stats, string nodeType, string property)
    {
        if (stats is not null)
        {
            try
            {
                var (present, total, ratio) = stats.GetPropertyPresenceRatio(nodeType, property);
                if (total > 0)
                {
                    return (ratio >= PropertyRuleThreshold,
                        $"{Math.Round(ratio * 100)}% of {total:N0} standard {nodeType} nodes set this property");
                }
            }
            catch { /* stats unavailable — fall through to defaults */ }
        }
        var applies = StaticPropertyDefaults.TryGetValue($"{nodeType}.{property}", out var def) && def;
        return (applies, "static default (no mined statistics available — run `d365fo index extract` to mine standard models)");
    }

    /// <summary>Extract the table-level header segment (before &lt;Fields&gt;) of an AxTable XML.</summary>
    private static string TableHeaderSegment(string code)
    {
        var m = Regex.Match(code, @"<Fields\b", RegexOptions.IgnoreCase);
        return m.Success ? code[..m.Index] : code;
    }

    private static void CheckTableProperties(string code, IPropertyStatsProvider? stats, List<XppViolation> v)
    {
        if (!Regex.IsMatch(code, @"<AxTable[\s>]", RegexOptions.IgnoreCase)) return;
        var header = TableHeaderSegment(code);

        var label = PropertyRuleApplies(stats, "AxTable", "Label");
        if (label.Applies && !Regex.IsMatch(header, @"<Label>[^<]+</Label>", RegexOptions.IgnoreCase))
        {
            v.Add(new XppViolation("XML002", "error", null, "<AxTable> — missing <Label>",
                $"Add <Label>@YourModel:TableLabel</Label> to the table header (create the label first via `d365fo label create`). Evidence: {label.Evidence}."));
        }

        var tableGroup = PropertyRuleApplies(stats, "AxTable", "TableGroup");
        if (tableGroup.Applies && !Regex.IsMatch(header, @"<TableGroup>[^<]+</TableGroup>", RegexOptions.IgnoreCase))
        {
            var suggestion = "Main (master data), Transaction (postings), Parameter (settings), Group (groupings)";
            if (stats is not null)
            {
                try
                {
                    var dist = stats.GetPropertyValueDistribution("AxTable", "TableGroup", 4);
                    if (dist.Count > 0)
                    {
                        var total = dist.Sum(d => d.Count);
                        suggestion = string.Join(", ", dist.Select(d => $"{d.Value} ({Math.Round((double)d.Count / total * 100)}%)"));
                    }
                }
                catch { /* keep static suggestion */ }
            }
            v.Add(new XppViolation("XML003", "error", null, "<AxTable> — missing <TableGroup>",
                $"Add <TableGroup> to the table header. Most common standard values: {suggestion}. Evidence: {tableGroup.Evidence}."));
        }

        var clustered = PropertyRuleApplies(stats, "AxTable", "ClusteredIndex");
        if (clustered.Applies && !Regex.IsMatch(header, @"<ClusteredIndex>[^<]+</ClusteredIndex>", RegexOptions.IgnoreCase))
        {
            v.Add(new XppViolation("XML005", "warning", null, "<AxTable> — missing <ClusteredIndex>",
                $"Set <ClusteredIndex> to the primary index name for predictable physical ordering. Evidence: {clustered.Evidence}."));
        }
    }

    private static void CheckFieldEdt(string code, IPropertyStatsProvider? stats, List<XppViolation> v)
    {
        if (!Regex.IsMatch(code, @"<AxTableField[\s>]", RegexOptions.IgnoreCase)) return;
        var rule = PropertyRuleApplies(stats, "AxTableField", "ExtendedDataType");
        if (!rule.Applies) return;

        var blocks = Regex.Split(code, @"<AxTableField[\s>]", RegexOptions.IgnoreCase).Skip(1);
        foreach (var block in blocks)
        {
            var body = Regex.Split(block, @"</AxTableField>", RegexOptions.IgnoreCase)[0];
            if (Regex.IsMatch(body, @"<ExtendedDataType>[^<]+</ExtendedDataType>", RegexOptions.IgnoreCase)) continue;
            if (Regex.IsMatch(body, @"<EnumType>[^<]+</EnumType>", RegexOptions.IgnoreCase)) continue;
            var nameMatch = Regex.Match(body, @"<Name>([^<]+)</Name>", RegexOptions.IgnoreCase);
            var name = nameMatch.Success ? nameMatch.Groups[1].Value : "(unnamed)";
            v.Add(new XppViolation("XML004", "warning", null,
                $"<AxTableField> {name} — no <ExtendedDataType> or <EnumType>",
                $"Base field \"{name}\" on an EDT (use `d365fo suggest edt` to find one) or an enum. " +
                $"Primitive-typed fields lose label, help text, and length governance. Evidence: {rule.Evidence}."));
        }
    }
}
