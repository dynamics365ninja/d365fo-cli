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
/// <c>validate_code(mode="syntax")</c> static rule set (40 rules as of upstream 1.15.0):
/// checks generated code against the D365FO rule canon without requiring xppbp.exe or a
/// Windows VM. Keyword scans run against a comment/string-masked copy of the source
/// (<see cref="XppLexer"/>) so a keyword inside a literal cannot fire a rule, and the
/// keyword/intrinsic/arity catalogs come from the shipped compiler
/// (<see cref="CompilerFacts"/>) rather than hand-typed lists.
///
/// Rules:
///   SEL001  today() deprecated (BPUpgradeCodeToday)
///   SEL002  forceLiterals (SQL-injection risk when values come from user input)
///   SEL003  crossCompany on joined buffer (must be on driving buffer)
///   SEL004  Nested while select (N+1 query anti-pattern)
///   SEL005  Function call in where clause (assign to variable first)
///   SEL006  index hint without allowIndexHint(true)
///   SEL007  left/right join or join…on — SQL/C# join syntax that is not X++
///   SEL008  order by / group by after the where of the same segment
///   SEL009  the in operator with an inline container literal
///   SEL010  a select expression on an aliased buffer; validTimeState given an expression
///   COC001  Default param value copied into CoC wrapper signature
///   COC002  [ExtensionOf] class neither final nor static
///   COC003  [ExtensionOf] class name not ending _Extension
///   COC004  next not reached exactly once and unconditionally (SYS10028)
///   COC005  Global function (checkFailed/error/…) called as this.&lt;fn&gt;() on a table buffer
///   COC006  Re-reading the record the buffer already holds, instead of this.orig()
///   BP001   Hardcoded string literal in info/warning/error/checkFailed
///   BP002   doInsert/doUpdate/doDelete outside explicit migration comment
///   BP003   Generic doc-comment (/// Foo class. / /// methodName.)
///   BP004   Developer-only statements left in code (print / breakpoint)
///   BP005   An enum SYMBOL (enum2Symbol / value2Symbol) in user-facing text — never translated
///   BP006   pause / window / tableLock / changeSite — removed from the language
///   FN001   Fixed-arity built-in called with the wrong number of arguments
///   FN002   A call to a predefined function this platform version does not have
///   TTS001  Unbalanced ttsbegin / ttscommit
///   TTS002  Dead catch inside an open tts scope (only UpdateConflict/DuplicateKeyException reach it)
///   TTS003  retry with no visible guard in its catch block (infinite-loop risk)
///   CS001   C# constructs that do not compile in X++ ($"…", =&gt;, foreach, ??, string type, …)
///   MAC001  A precompiler directive written without its dot (#define X)
///   ATTR001 An attribute argument that is not a compile-time literal
///   ATTR002 [SysObsolete] without message, isError AND date (xppbp moniker)
///   EXT001  An extension-method class whose class or methods are not static
///   KW001   A variable named after a reserved word (the compiler's own 115-word set)
///   RPT001  DP reads parmDataContract() but declares no [SRSReportParameterAttribute]
///   RPT002  DP has processReport() but no [SRSReportDataSetAttribute] getter
///   RPT101  AxReport XML without a design node (codeType="xml-report")
///   RPT102  AxReport dataset without &lt;Query&gt; (codeType="xml-report")
///   XML001  AxTable XML missing an index with &lt;AlternateKey&gt;Yes&lt;/AlternateKey&gt;
///   XML002  AxTable missing &lt;Label&gt;            (data-driven)
///   XML003  AxTable missing &lt;TableGroup&gt;       (data-driven, suggests common standard values)
///   XML004  AxTableField without &lt;ExtendedDataType&gt;/&lt;EnumType&gt; (data-driven)
///   XML005  AxTable missing &lt;ClusteredIndex&gt;   (only when standard usage ≥ threshold)
///   XML007  Member the AOT type does not declare — silently dropped when the file is read
///   XML008  Value outside the enum its member is typed as — the whole document fails to load
///   XML009  Root element names no AOT type
///   XML010  Abstract root with no concrete &lt;c&gt;i:type&lt;/c&gt; pinned
///   XML011  XMLSchema-instance namespace used or required but not declared on the root
///   XML012  Document not in the XML namespace its contract declares
///   XML013  File sitting in an AOT folder another family owns (path-aware)
/// </summary>
/// <remarks>
/// XML001–XML005 are AxTable-only by nature — they are property-presence rules mined from
/// standard tables. XML007–XML013 are not: they come from
/// <see cref="D365FO.Core.Metadata.MetadataContracts"/> (565 types) and
/// <see cref="D365FO.Core.ObjectTypes.ObjectTypeRegistry"/>, so they apply to every AOT family.
/// Together they are the offline counterpart of <c>validate metadata</c>, which asks the live
/// provider the same questions — see <see cref="ObjectShapeRules"/> for the mapping onto the
/// bridge's own rejection codes. The upstream XML006 (element order) is deliberately not
/// ported — see <see cref="ContractShapeRules"/> for why an order rule is not justified by
/// evidence here.
/// </remarks>
public static class XppValidator
{
    public const string CodeTypeXpp = "xpp";
    public const string CodeTypeXmlTable = "xml-table";
    public const string CodeTypeXmlAny = "xml-any";
    public const string CodeTypeXmlReport = "xml-report";

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

    /// <summary>
    /// SEL005's exclusion list. Deliberately the hand list every port of this rule has used —
    /// the full compiler intrinsic set lives in <see cref="CompilerFacts"/> and drives
    /// FN001/ATTR001 instead.
    /// </summary>
    private static readonly HashSet<string> IntrinsicFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "fieldnum", "tablenum", "classstr", "methodstr", "formstr", "tablestr",
        "enumnum", "extendedtypenum", "identifierstr", "literalstr", "resourcestr",
        "ssrsreportstr", "fieldstr", "querystr", "dataentitydatasourcestr",
        "formdatasourcestr", "formcontrolstr", "delegatestr", "enumstr",
        "classnum", "formnum", "reportstr", "menuitemactionstr", "menuitemdisplaystr",
        "menuitemoutputstr", "varstr", "con2str", "int2str", "num2str",
    };

    /// <param name="sourcePath">
    /// Where the document came from, when the caller knows. Only XML013 uses it — a file in the
    /// wrong AOT folder is not something stdin can be guilty of.
    /// </param>
    public static IReadOnlyList<XppViolation> Validate(
        string code, string codeType = CodeTypeXpp, IPropertyStatsProvider? stats = null, string? sourcePath = null)
    {
        var violations = new List<XppViolation>();
        var normalized = NormalizeCodeType(codeType);
        if (normalized == CodeTypeXpp)
        {
            RunXppRules(code, violations);
        }
        else if (normalized == CodeTypeXmlReport)
        {
            // AxReport XML embeds RDL in CDATA — running the X++ keyword rules over it
            // would only produce noise, so the report document gets its own rule set.
            CheckReportHasDesign(code, violations);
            CheckReportDatasetShape(code, violations);
            RunXmlShapeRules(code, sourcePath, violations);
        }
        else if (normalized == CodeTypeXmlTable)
        {
            RunXppRules(code, violations);
            CheckMissingAlternateKey(code, violations);
            CheckTableProperties(code, stats, violations);
            CheckFieldEdt(code, stats, violations);
            RunXmlShapeRules(code, sourcePath, violations);
        }
        else
        {
            CheckMissingAlternateKey(code, violations);
            CheckTableProperties(code, stats, violations);
            CheckFieldEdt(code, stats, violations);
            RunXmlShapeRules(code, sourcePath, violations);
        }
        return violations;
    }

    /// <summary>
    /// The family-agnostic half of the XML rules: the root's own shape (XML009–XML013) and
    /// everything inside it (XML007–XML008). Both are driven by the contract catalog and the
    /// object-type registry, so they apply to every AOT family rather than the AxTable-only set
    /// XML001–XML005 covers (issue #163).
    /// </summary>
    private static void RunXmlShapeRules(string code, string? sourcePath, List<XppViolation> violations)
    {
        ObjectShapeRules.Check(code, violations, sourcePath);
        ContractShapeRules.Check(code, violations);
    }

    public static string NormalizeCodeType(string? codeType) => codeType?.ToLowerInvariant() switch
    {
        "xml-table" or "xmltable" or "table-xml" => CodeTypeXmlTable,
        "xml-report" or "xmlreport" or "report-xml" => CodeTypeXmlReport,
        "xml-any" or "xmlany" or "xml" => CodeTypeXmlAny,
        _ => CodeTypeXpp,
    };

    private static void RunXppRules(string code, List<XppViolation> violations)
    {
        var masked = XppLexer.Mask(code);
        CheckTodayDeprecated(code, violations);
        CheckForceLiterals(masked, violations);
        CheckCrossCompanyPlacement(code, violations);
        CheckNestedWhileSelect(masked, violations);
        CheckFunctionInWhere(masked, violations);
        CheckCocDefaultParam(code, masked, violations);
        CheckExtensionOfNotFinal(code, masked, violations);
        CheckExtensionOfNaming(code, masked, violations);
        CheckCocNextUnconditional(code, masked, violations);
        CheckGlobalFunctionOnTableBuffer(code, masked, violations);
        CheckRecordReReadInTableCoc(code, masked, violations);
        CheckEnumSymbolInMessage(code, masked, violations);
        CheckBuiltinArity(code, masked, violations);
        CheckHardcodedStrings(code, violations);
        CheckDoMethods(code, violations);
        CheckGenericDocComment(code, violations);
        CheckUnbalancedTts(masked, violations);
        CheckDevArtifacts(masked, violations);
        CheckCSharpIsms(masked, violations);
        CheckCatchInsideTts(masked, violations);
        CheckUnguardedRetry(masked, violations);
        CheckIndexHint(masked, violations);
        CheckForeignJoinSyntax(masked, violations);
        CheckReportDpShape(code, masked, violations);
        CheckRemovedStatements(masked, violations);
        CheckMacroDirectiveForm(code, violations);
        CheckSelectClauseOrder(masked, violations);
        CheckInOperatorLiteral(masked, violations);
        CheckSelectExpressionAndValidTimeState(masked, violations);
        CheckAttributeArguments(code, masked, violations);
        CheckExtensionMethodClassShape(code, masked, violations);
        CheckReservedIdentifiers(code, masked, violations);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static int LineNumber(string code, int index)
        => code.AsSpan(0, Math.Min(index, code.Length)).Count('\n') + 1;

    private static void MatchAll(string code, string pattern, string rule, string severity, string fix,
        List<XppViolation> violations, bool skipIfComment = true, RegexOptions options = RegexOptions.IgnoreCase)
    {
        var lines = code.Split('\n');
        foreach (Match m in Regex.Matches(code, pattern, options))
        {
            var lineIdx = LineNumber(code, m.Index) - 1;
            var lineText = lineIdx < lines.Length ? lines[lineIdx].TrimStart() : "";
            if (skipIfComment && (lineText.StartsWith("//") || lineText.StartsWith('*'))) continue;
            violations.Add(new XppViolation(rule, severity, lineIdx + 1, m.Value.Trim(), fix));
        }
    }

    /// <summary>
    /// The body of the method whose declaration starts at <paramref name="declIdx"/>, as masked
    /// lines, located by brace depth. Empty when the declaration has no body.
    /// </summary>
    private static List<string> MethodBodyLines(string[] maskedLines, int declIdx)
    {
        int depth = 0;
        bool started = false;
        var body = new List<string>();
        for (int i = declIdx; i < maskedLines.Length; i++)
        {
            var line = maskedLines[i];
            foreach (var ch in line)
            {
                if (ch == '{') { depth++; started = true; }
                else if (ch == '}') depth--;
            }
            if (started)
            {
                if (i > declIdx) body.Add(line);
                if (depth <= 0) break;
            }
            else if (i > declIdx + 2)
            {
                break; // a declaration with no body within reach (interface method, abstract)
            }
        }
        return body;
    }

    /// <summary>True when the method declared at <paramref name="declIdx"/> calls <c>next</c> — i.e. it is a CoC wrapper.</summary>
    private static bool MethodBodyCallsNext(string[] maskedLines, int declIdx)
        => MethodBodyLines(maskedLines, declIdx).Any(l => Regex.IsMatch(l, @"\bnext\s+[A-Za-z_]"));

    /// <summary>
    /// The class declaration that follows line <paramref name="fromIdx"/>, skipping doc comments
    /// and blank lines. The search runs on masked lines: scanning the raw text found
    /// <c>class</c> inside <c>/// Extends the &lt;c&gt;X&lt;/c&gt; class …</c> and reported the
    /// comment as the declaration.
    /// </summary>
    private static (int Index, string Text)? ClassDeclarationAfter(string[] maskedLines, int fromIdx, int maxLookahead = 12)
    {
        int end = Math.Min(fromIdx + maxLookahead, maskedLines.Length - 1);
        for (int j = fromIdx; j <= end; j++)
        {
            if (Regex.IsMatch(maskedLines[j], @"\bclass\b", RegexOptions.IgnoreCase)) return (j, maskedLines[j]);
        }
        return null;
    }

    // ── X++ rules ────────────────────────────────────────────────────────────

    private static void CheckTodayDeprecated(string code, List<XppViolation> v) => MatchAll(code,
        // xppc compiles today() — this is a best-practice finding (BPUpgradeCodeToday),
        // not a compile error, and the severity must say so.
        @"\btoday\s*\(\s*\)", "SEL001", "warning",
        "Replace today() with DateTimeUtil::getToday(DateTimeUtil::getUserPreferredTimeZone()). " +
        "today() ignores user time zone and fails BPUpgradeCodeToday.", v);

    // A warning, not an error: xppc accepts the keyword and the platform itself ships
    // 57 uses of it. The risk is real only when a where-clause value comes from user input.
    private static void CheckForceLiterals(string masked, List<XppViolation> v) => MatchAll(masked,
        @"\bforceLiterals\b", "SEL002", "warning",
        "Avoid forceLiterals: it reveals the where-clause values to the query optimiser and, " +
        "with values that come from user input, exposes the statement to SQL injection. " +
        "Use forcePlaceholders (the default for non-join selects) or omit the hint. " +
        "Standard code uses it only where the plan measurably needs the literal.", v);

    private static void CheckCrossCompanyPlacement(string code, List<XppViolation> v) => MatchAll(code,
        @"\bjoin\s+crossCompany\b", "SEL003", "error",
        "Move crossCompany to the outer select (driving buffer): \"select crossCompany tableBuffer join …\". " +
        "crossCompany is a query-level option, not a per-join option.", v);

    private static void CheckNestedWhileSelect(string masked, List<XppViolation> v)
    {
        var lines = masked.Split('\n');
        var whileSelectLines = new List<int>();
        for (int i = 0; i < lines.Length; i++)
        {
            if (Regex.IsMatch(lines[i], @"\bwhile\s+select\b", RegexOptions.IgnoreCase)
                && !lines[i].TrimStart().StartsWith("//"))
            {
                whileSelectLines.Add(i + 1);
            }
        }
        if (whileSelectLines.Count >= 2 && !Regex.IsMatch(masked, @"\bjoin\b", RegexOptions.IgnoreCase))
        {
            v.Add(new XppViolation("SEL004", "warning", whileSelectLines[1],
                $"while select at lines {string.Join(", ", whileSelectLines)}",
                "Replace nested while select with a join in a single while select, or " +
                "pre-load the inner data into a Map/temp table. " +
                "Nested while select causes N+1 database queries (BPCheckNestedLoopinCode)."));
        }
    }

    private static void CheckFunctionInWhere(string masked, List<XppViolation> v)
    {
        var lines = masked.Split('\n');
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

    private static void CheckCocDefaultParam(string code, string masked, List<XppViolation> v)
    {
        if (!Regex.IsMatch(code, @"\[ExtensionOf\s*\(", RegexOptions.IgnoreCase)) return;
        var lines = code.Split('\n');
        var maskedLines = masked.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var rawLine = lines[i];
            if (rawLine.TrimStart().StartsWith("//")) continue;
            // Only a CoC WRAPPER inherits the base signature; a brand-new method that an
            // extension class merely adds may carry defaults like any other method, and the
            // platform ships 20 such classes. The distinguishing mark is a call to next
            // inside the body.
            if (!MethodBodyCallsNext(maskedLines, i)) continue;
            // Method-like line with a default param: "public Foo method(Type _p = val)".
            // The second alternative catches declarations with NO access modifier (legal
            // X++ — members default to public). A CoC template that strips access modifiers
            // is the single most likely source of this defect, so a modifier-only regex
            // misses it. It is anchored to a whole-line declaration (no trailing ';') so
            // that call statements containing '=' inside parens — strFmt("a = %1", x) —
            // don't match.
            var withModifier = Regex.IsMatch(rawLine, @"\b(public|protected|private|internal)\b.*\([^)]*=\s*[^,)]+\)");
            var bareDeclaration = Regex.IsMatch(rawLine,
                @"^\s*(?:(?:static|final|abstract|display|edit|server|client)\s+)*[A-Za-z_]\w*\s+[A-Za-z_]\w*\s*\([^)]*=\s*[^,)]+\)\s*$");
            if (!withModifier && !bareDeclaration) continue;
            // Constructors and parm* accessors keep their defaults intentionally.
            if (Regex.IsMatch(rawLine, @"\bnew\s*\(")) continue;
            if (Regex.IsMatch(rawLine, @"\bparm[A-Z]")) continue;
            v.Add(new XppViolation("COC001", "error", i + 1, rawLine.Trim(),
                "Remove default parameter values from CoC wrapper signatures. " +
                "The base method's defaults are already in effect when calling next. " +
                "Example: \"public void salute(str message)\" NOT \"public void salute(str message = \\\"Hi\\\")\"."));
        }
    }

    private static void CheckExtensionOfNotFinal(string code, string masked, List<XppViolation> v)
    {
        var lines = code.Split('\n');
        var maskedLines = masked.Split('\n');
        for (int i = 0; i < maskedLines.Length; i++)
        {
            if (!Regex.IsMatch(maskedLines[i], @"\[\s*ExtensionOf", RegexOptions.IgnoreCase)) continue;
            var decl = ClassDeclarationAfter(maskedLines, i);
            if (decl is null) continue;
            // final = a CoC class (wrappers); static = an extension-method class. Both carry
            // [ExtensionOf] and both compile — the platform ships static ones, e.g.
            // TaxCalculationAdjustment_ApplicationSuite_Extension.
            if (!Regex.IsMatch(decl.Value.Text, @"\bfinal\b", RegexOptions.IgnoreCase)
                && !Regex.IsMatch(decl.Value.Text, @"\bstatic\b", RegexOptions.IgnoreCase))
            {
                v.Add(new XppViolation("COC002", "error", decl.Value.Index + 1,
                    (decl.Value.Index < lines.Length ? lines[decl.Value.Index] : decl.Value.Text).Trim(),
                    "An [ExtensionOf] class must be final (Chain of Command wrappers) or static " +
                    "(extension methods): \"[ExtensionOf(...)] final class MyClass_Extension\". " +
                    "Without either the compiler rejects the file."));
            }
            i = decl.Value.Index;
        }
    }

    private static void CheckExtensionOfNaming(string code, string masked, List<XppViolation> v)
    {
        var lines = code.Split('\n');
        var maskedLines = masked.Split('\n');
        for (int i = 0; i < maskedLines.Length; i++)
        {
            if (!Regex.IsMatch(maskedLines[i], @"\[\s*ExtensionOf", RegexOptions.IgnoreCase)) continue;
            var decl = ClassDeclarationAfter(maskedLines, i);
            if (decl is null) continue;
            var m = Regex.Match(decl.Value.Text, @"\bclass\s+(\w+)", RegexOptions.IgnoreCase);
            if (m.Success && !m.Groups[1].Value.EndsWith("_Extension", StringComparison.Ordinal))
            {
                v.Add(new XppViolation("COC003", "error", decl.Value.Index + 1,
                    (decl.Value.Index < lines.Length ? lines[decl.Value.Index] : decl.Value.Text).Trim(),
                    $"Rename class to \"{m.Groups[1].Value}_Extension\". " +
                    "Extension classes must end with _Extension per MS naming guidelines."));
            }
            i = decl.Value.Index;
        }
    }

    /// <summary>
    /// COC004 — <c>next</c> not reached exactly once and unconditionally (compiler SYS10028).
    /// The one CoC mistake that looks completely reasonable as ordinary X++ —
    /// <c>if (ret) { ret = next foo(); }</c> is how you would write a short-circuit anywhere
    /// else — and neither xppbp nor reference checks catch it. Brace depths are counted on the
    /// masked copy, so a '{' inside a literal or a doc comment cannot shift the method
    /// boundaries.
    /// </summary>
    private static void CheckCocNextUnconditional(string code, string masked, List<XppViolation> v)
    {
        if (!Regex.IsMatch(code, @"\[ExtensionOf\s*\(", RegexOptions.IgnoreCase)) return;

        var lines = code.Split('\n');
        var maskedLines = masked.Split('\n');
        var methodDecl = new Regex(
            @"^\s*(?:(?:public|protected|private|internal|static|final|display|edit|server|client)\s+)*[A-Za-z_]\w*\s+([A-Za-z_]\w*)\s*\([^;]*\)\s*$");

        int depth = 0;
        int? classBodyDepth = null;
        string? methodName = null;
        int methodBodyDepth = 0;
        var nexts = new List<(int Line, string Excerpt, bool Conditional)>();
        int? earlyReturn = null;

        void CloseMethod()
        {
            if (methodName is null) return;
            foreach (var n in nexts.Where(n => n.Conditional))
            {
                v.Add(new XppViolation("COC004", "error", n.Line, n.Excerpt,
                    $"\"next {methodName}\" is inside a conditional block. The compiler rejects this with " +
                    "SYS10028 \"Call to 'next' should be done only once and unconditionally\". " +
                    $"Call it as the first statement instead — \"ret = next {methodName}();\" — then apply the " +
                    "business rule afterwards and use \"ret = checkFailed('@Model:Label')\" to fail the write."));
            }
            if (nexts.Count > 1)
            {
                v.Add(new XppViolation("COC004", "error", nexts[1].Line, nexts[1].Excerpt,
                    $"\"next {methodName}\" is called {nexts.Count} times in one CoC method; SYS10028 allows exactly one. " +
                    "Store the single result in a local and reuse it."));
            }
            if (earlyReturn is int er && nexts.Count > 0 && er < nexts[0].Line)
            {
                v.Add(new XppViolation("COC004", "error", er, lines[er - 1].Trim(),
                    $"This \"return\" can skip \"next {methodName}\" below it, so the call is not unconditional (SYS10028). " +
                    $"Call \"next {methodName}\" first, then let the rule decide the return value."));
            }
            methodName = null;
        }

        for (int i = 0; i < lines.Length; i++)
        {
            var clean = i < maskedLines.Length ? maskedLines[i] : "";
            var depthAtLineStart = depth;

            if (classBodyDepth is null && Regex.IsMatch(clean, @"\bclass\b", RegexOptions.IgnoreCase))
            {
                classBodyDepth = depthAtLineStart + 1;
            }

            if (methodName is not null)
            {
                var nextCall = Regex.Match(clean, $@"\bnext\s+{Regex.Escape(methodName)}\b");
                if (nextCall.Success)
                {
                    nexts.Add((i + 1, lines[i].Trim(),
                        // Deeper than the method body means it sits inside if/while/switch/try;
                        // the same-line form "if (ret) ret = next foo();" never opens a block.
                        depthAtLineStart > methodBodyDepth || Regex.IsMatch(clean, @"\b(if|while|for|switch|case)\b")));
                }
                else if (earlyReturn is null && depthAtLineStart > methodBodyDepth && Regex.IsMatch(clean, @"\breturn\b"))
                {
                    earlyReturn = i + 1;
                }
            }
            else if (classBodyDepth is int cbd && depthAtLineStart == cbd)
            {
                var decl = methodDecl.Match(clean);
                if (decl.Success && !Regex.IsMatch(clean, @"\bnew\s*\("))
                {
                    methodName = decl.Groups[1].Value;
                    methodBodyDepth = cbd + 1;
                    nexts = [];
                    earlyReturn = null;
                }
            }

            foreach (var ch in clean)
            {
                if (ch == '{') depth++;
                else if (ch == '}')
                {
                    depth--;
                    if (methodName is not null && depth < methodBodyDepth) CloseMethod();
                }
            }
        }
        CloseMethod();
    }

    /// <summary>Global functions, not members of <c>Common</c> — unqualified on a table buffer.</summary>
    private static readonly string[] GlobalFunctionsNotOnTable =
        ["checkFailed", "error", "warning", "info", "strFmt", "setPrefix", "funcName"];

    /// <summary>
    /// COC005 — a Global function called as <c>this.&lt;fn&gt;()</c> on a table buffer.
    /// <c>this.checkFailed(...)</c> reads as consistent next to <c>this.orig()</c>, and xppc
    /// rejects it with ClassDoesNotContainMethod. Scoped to
    /// <c>[ExtensionOf(tableStr(...))]</c> — on a RunBase descendant the same call is legal.
    /// </summary>
    private static void CheckGlobalFunctionOnTableBuffer(string code, string masked, List<XppViolation> v)
    {
        if (!Regex.IsMatch(code, @"\[ExtensionOf\s*\(\s*tableStr\s*\(", RegexOptions.IgnoreCase)) return;

        var lines = code.Split('\n');
        var maskedLines = masked.Split('\n');
        var pattern = new Regex($@"\bthis\s*\.\s*({string.Join("|", GlobalFunctionsNotOnTable)})\s*\(", RegexOptions.IgnoreCase);

        for (int i = 0; i < maskedLines.Length; i++)
        {
            var m = pattern.Match(maskedLines[i]);
            if (!m.Success) continue;
            var fn = m.Groups[1].Value;
            v.Add(new XppViolation("COC005", "error", i + 1, lines[i].Trim(),
                $"\"{fn}\" is a Global function, not a method of the table buffer. The compiler rejects " +
                $"\"this.{fn}(…)\" with \"Table '<name>' does not contain a definition for method '{fn}'\". " +
                $"Call it unqualified: \"{fn}(…)\"" +
                (fn == "checkFailed"
                    ? " — the idiom in a validateWrite wrapper is \"ret = checkFailed('@Model:LabelId');\"."
                    : ".")));
        }
    }

    /// <summary>
    /// COC006 — re-reading the record the buffer already holds, instead of <c>this.orig()</c>.
    /// Inside <c>[ExtensionOf(tableStr(X))]</c>, <c>this</c> IS the record. Fetching the row
    /// again by its own RecId costs a database round trip on every write of the table — and it
    /// is not even the same answer, because it reads the CURRENT stored state rather than this
    /// buffer's pre-image.
    /// </summary>
    private static void CheckRecordReReadInTableCoc(string code, string masked, List<XppViolation> v)
    {
        if (!Regex.IsMatch(code, @"\[ExtensionOf\s*\(\s*tableStr\s*\(", RegexOptions.IgnoreCase)) return;

        var lines = code.Split('\n');
        var reported = new HashSet<int>();

        void Report(int offset, string excerptFallback, string spelling)
        {
            var lineNo = LineNumber(masked, offset);
            if (!reported.Add(lineNo)) return;
            v.Add(new XppViolation("COC006", "warning", lineNo,
                lineNo - 1 < lines.Length ? lines[lineNo - 1].Trim() : excerptFallback,
                $"This re-reads the record the buffer already holds. Inside a table CoC, {spelling} " +
                "the pre-image is `this.orig()` — already in memory, filled when the record was fetched — " +
                "and the new values are `this` itself. Compare `this.MyField` against `this.orig().MyField` " +
                "and delete the lookup: it costs a database round trip on every write of the table, and it " +
                "returns the CURRENT stored state, not the values this buffer was fetched with. " +
                "On an insert `this.orig()` is empty, so `this.orig().RecId == 0` is the \"new record\" test."));
        }

        // A select whose where clause ties another buffer's RecId to this one's. The
        // where clause is usually on its own line, so the statement — up to its ';' or
        // the '{' of a while select — is the unit to scan, not the line.
        var sameRecId = new Regex(
            @"(?:\w+\s*\.\s*RecId\s*==\s*this\s*\.\s*RecId)|(?:this\s*\.\s*RecId\s*==\s*\w+\s*\.\s*RecId)",
            RegexOptions.IgnoreCase);
        foreach (Match m in Regex.Matches(masked, @"\bselect\b", RegexOptions.IgnoreCase))
        {
            var semi = masked.IndexOf(';', m.Index);
            var brace = masked.IndexOf('{', m.Index);
            var ends = new[] { semi, brace }.Where(x => x != -1).ToArray();
            var end = ends.Length > 0 ? ends.Min() : masked.Length;
            var span = masked[m.Index..end];
            var hit = sameRecId.Match(span);
            if (hit.Success) Report(m.Index + hit.Index, hit.Value, "a select on the same table is never the way to it —");
        }

        // The same fetch spelled as a static find.
        foreach (Match m in Regex.Matches(masked, @"\b\w+\s*::\s*find\w*\s*\(([^)]*)\)", RegexOptions.IgnoreCase))
        {
            if (!Regex.IsMatch(m.Groups[1].Value, @"\bthis\s*\.\s*RecId\b", RegexOptions.IgnoreCase)) continue;
            Report(m.Index, m.Value, "a find() on your own RecId is never the way to it —");
        }
    }

    /// <summary>
    /// BP005 — an enum's SYMBOL feeding user-facing text. <c>enum2Symbol()</c> /
    /// <c>DictEnum.value2Symbol()</c> return the AOT name ('Gold'), which is not a label and is
    /// never translated. NOT enum2str: enum2str resolves the value's &lt;Label&gt; in the
    /// session language. Matched over the call's whole argument span so a wrapped
    /// <c>checkFailed(strFmt(...))</c> with the symbol call on another line is still caught.
    /// </summary>
    private static void CheckEnumSymbolInMessage(string code, string masked, List<XppViolation> v)
    {
        var lines = code.Split('\n');
        var reported = new HashSet<int>();

        foreach (Match m in Regex.Matches(masked, @"\b(?:info|warning|error|checkFailed|strFmt)\s*\("))
        {
            // Walk from the opening paren to its match so nested calls and multi-line
            // argument lists are one span.
            int depth = 0;
            int end = m.Index + m.Length - 1;
            for (; end < masked.Length; end++)
            {
                var ch = masked[end];
                if (ch == '(') depth++;
                else if (ch == ')')
                {
                    depth--;
                    if (depth == 0) break;
                }
            }
            var span = masked[m.Index..Math.Min(end + 1, masked.Length)];

            // Both spellings: the Global wrapper and the DictEnum method it delegates to.
            foreach (Match hit in Regex.Matches(span, @"(?:\benum2Symbol|\.\s*value2Symbol)\s*\(", RegexOptions.IgnoreCase))
            {
                var lineNo = LineNumber(masked, m.Index + hit.Index);
                if (!reported.Add(lineNo)) continue;
                v.Add(new XppViolation("BP005", "warning", lineNo, lines[lineNo - 1].Trim(),
                    "This prints the enum's AOT name, which is never translated — the message stays English in " +
                    "every locale. Use enum2str(value) when the enum type is known at compile time; when it is " +
                    "only known at runtime, new DictEnum(enumId).value2Label(value). Keep the symbol for logs, " +
                    "filenames and anything persisted."));
            }
        }
    }

    /// <summary>
    /// Extra guidance for the built-ins whose arity is a documented trap. The ARITY itself is
    /// never written here — it comes from the compiler's own answer (<see cref="CompilerFacts"/>);
    /// this map only adds the sentence that explains why the wrong count looked right.
    /// </summary>
    private static readonly Dictionary<string, string> BuiltinArityNotes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enum2str"] = "enum2Str(value) — the value alone. It resolves that value's <Label> in the session language, which is why it needs no enum id",
        ["enum2symbol"] = "enum2Symbol(enumNum(MyEnum), value) — enum id AND value",
        ["symbol2enum"] = "symbol2Enum(enumNum(MyEnum), symbolString) — enum id AND symbol",
        ["enumnum"] = "enumNum(MyEnum) — the enum TYPE name alone, not a value",
        ["substr"] = "subStr(text, position, number) — position is 1-based",
        ["strfind"] = "strFind(text, characters, start, count)",
        ["strscan"] = "strScan(text, pattern, start, count)",
        ["conpeek"] = "conPeek(container, position) — 1-based",
        ["condel"] = "conDel(container, start, number)",
        ["mkdate"] = "mkDate(day, month, year)",
        ["date2str"] = "date2Str(date, sequence, day, sep1, month, sep2, year [, DateFlags]) — the 8th argument is optional",
        ["datetime2str"] = "datetime2Str(utcdatetime [, DateFlags]) — the flags argument is optional",
        ["num2str"] = "num2Str(value, characters, decimals, decimalSeparator, thousandSeparator)",
        ["fieldid2name"] = "fieldId2Name(tableId, fieldId [, arrayIndex])",
        ["ssrsreportstr"] = "ssrsReportStr(MyReport, MyDesign) — report AND design name; the design must exist inside that AxReport (scaffolded reports name it AutoDesign)",
    };

    /// <summary>
    /// Words that may legally precede a predefined-function CALL. Anything else that is a bare
    /// identifier in front of <c>name(</c> marks a method DECLARATION (<c>Type name()</c>).
    /// </summary>
    private static readonly HashSet<string> CallPrecedingKeywords = new(StringComparer.Ordinal)
    {
        "return", "if", "while", "for", "switch", "case", "throw", "else", "do", "and", "or",
        "not", "select", "where", "join", "setting", "by", "in", "next", "new", "super", "print",
    };

    /// <summary>
    /// Arguments in the call whose '(' sits at <paramref name="open"/>, or null when the
    /// parentheses never close — a snippet cut mid-call is not something to have an opinion
    /// about. Counts top-level commas only. Runs on masked source, where a comma inside a
    /// string literal is already a space.
    /// </summary>
    private static int? CountCallArguments(string masked, int open)
    {
        int parens = 0;
        int brackets = 0;
        int commas = 0;
        bool hasContent = false;

        for (int i = open; i < masked.Length; i++)
        {
            var ch = masked[i];
            if (ch == '(') { parens++; continue; }
            if (ch == ')')
            {
                parens--;
                if (parens == 0) return hasContent ? commas + 1 : 0;
                continue;
            }
            if (ch == '[') { brackets++; continue; }
            if (ch == ']') { brackets--; continue; }
            if (parens == 1 && brackets == 0 && ch == ',') { commas++; continue; }
            if (!char.IsWhiteSpace(ch)) hasContent = true;
        }

        return null;
    }

    /// <summary>
    /// FN001 — a fixed-arity built-in called with the wrong number of arguments; FN002 — a call
    /// to a predefined function this platform version does not have. xppc catches every one of
    /// these, but only after a build; catching it at validate time saves the failed compile.
    /// </summary>
    private static void CheckBuiltinArity(string code, string masked, List<XppViolation> v)
    {
        var lines = code.Split('\n');

        foreach (Match m in Regex.Matches(masked, @"\b([A-Za-z_]\w*)\s*\("))
        {
            var called = m.Groups[1].Value;
            var intrinsic = CompilerFacts.IntrinsicInfo(called);
            var runtime = intrinsic is null ? CompilerFacts.RuntimeFunctionInfo(called) : null;
            var unknown = intrinsic is null && runtime is null && CompilerFacts.IsUnknownFunction(called);
            if (intrinsic is null && runtime is null && !unknown) continue;
            // Variadic — the compiler has no count to check (strFmt, conIns, max, min).
            if (runtime is { Arity.Max: null }) continue;
            // `something.enum2Str(…)` is a method on another type, not the global.
            var before = masked[..m.Index].TrimEnd();
            if (before.EndsWith('.')) continue;
            // `MyClass::year(…)` is that class's own static; only Global:: shares the
            // predefined names.
            if (before.EndsWith("::") && !Regex.IsMatch(before, @"\bGlobal\s*::$")) continue;
            // A DECLARATION, not a call: `public IntEditAdaptor Year()` in a form adaptor
            // reads as a call to the predefined year() unless the preceding token is
            // recognised as a type name. In a call the previous token is an operator, a
            // separator or a statement keyword — never a bare identifier.
            var prevToken = Regex.Match(before, @"([A-Za-z_]\w*)\s*$");
            if (prevToken.Success && !CallPrecedingKeywords.Contains(prevToken.Groups[1].Value.ToLowerInvariant())) continue;

            var lineNo = LineNumber(masked, m.Index);
            var excerpt = lines[lineNo - 1].Trim();

            if (unknown)
            {
                v.Add(new XppViolation("FN002", "error", lineNo, excerpt,
                    $"{called} is not a predefined function on this platform (xppc {CompilerFacts.CompilerVersion}): " +
                    $"\"The name '{called}' does not denote a predefined function, a static method on the Global " +
                    "class nor a previously defined local function\". It reads as one because AX 2012 had it — " +
                    "see the runtime-functions knowledge topic for the function that replaced it."));
                continue;
            }

            var actual = CountCallArguments(masked, m.Index + m.Length - 1);
            if (actual is not int count) continue;

            BuiltinArityNotes.TryGetValue(called, out var note);
            if (intrinsic is (string iname, int iargs))
            {
                if (count == iargs) continue;
                v.Add(new XppViolation("FN001", "error", lineNo, excerpt,
                    $"{iname} is a compile-time intrinsic taking {iargs} argument(s); " +
                    $"{count} given.{(note is null ? "" : $" {note}.")}"));
                continue;
            }

            var (rname, arity) = runtime!.Value;
            if (arity.Accepts(count)) continue;
            var expected = arity.Max ?? arity.Min;
            v.Add(new XppViolation("FN001", "error", lineNo, excerpt,
                $"{rname} takes {arity.Describe()}; {count} given. xppc rejects this with " +
                $"\"'{rname}' expects {expected} argument(s), but {count} specified\"" +
                (count < arity.Min ? $" or \"is missing argument {arity.Min}\"" : "") +
                $".{(note is null ? "" : $" {note}.")}"));
        }
    }

    private static void CheckHardcodedStrings(string code, List<XppViolation> v)
    {
        var lines = code.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimStart();
            if (line.StartsWith("//") || line.StartsWith('*')) continue;
            // Match: info("...") / warning('...') / error("...") / checkFailed("...")
            // where the first argument is a raw string (not starting with @).
            // Both quote styles count — the platform writes single-quoted literals as often
            // as double-quoted ones. The lookbehind keeps the rule off member calls such as
            // AifChangeTrackingEventSource::…Info("…") and this.error(…) on a logger: only
            // the Global functions carry the label obligation.
            foreach (Match m in Regex.Matches(lines[i],
                @"(?<![.\w:])(?:info|warning|error|checkFailed)\s*\(\s*([""'])(?!@)([^""']{1,200})\1",
                RegexOptions.IgnoreCase))
            {
                v.Add(new XppViolation("BP001",
                    // xppc compiles a hardcoded string; xppbp reports BPErrorLabelIsText.
                    "warning", i + 1, m.Value.Trim(),
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

    /// <summary>
    /// TTS001 — unbalanced ttsbegin / ttscommit, counted per top-level brace block — i.e. per
    /// method when the input is a method source or a concatenated class. Counting across the
    /// whole text conflated separate methods (upstream measured 21 shipped classes wrongly
    /// reported): one method opening two transactions and another closing three is not a defect.
    /// </summary>
    private static void CheckUnbalancedTts(string masked, List<XppViolation> v)
    {
        var lines = masked.Split('\n');

        var regions = new List<(int Line, int Begins, int Commits, int Aborts)>();
        (int Line, int Begins, int Commits, int Aborts)? current = null;
        int depth = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (depth == 0 && line.Contains('{')) current = (i + 1, 0, 0, 0);
            if (current is not null)
            {
                var c = current.Value;
                c.Begins += Regex.Matches(line, @"\bttsbegin\b", RegexOptions.IgnoreCase).Count;
                c.Commits += Regex.Matches(line, @"\bttscommit\b", RegexOptions.IgnoreCase).Count;
                c.Aborts += Regex.Matches(line, @"\bttsabort\b", RegexOptions.IgnoreCase).Count;
                current = c;
            }
            foreach (var ch in line)
            {
                if (ch == '{') depth++;
                else if (ch == '}') depth--;
            }
            if (depth <= 0 && current is not null)
            {
                regions.Add(current.Value);
                current = null;
                depth = 0;
            }
        }
        if (current is not null) regions.Add(current.Value);
        if (regions.Count == 0)
        {
            regions.Add((1,
                Regex.Matches(masked, @"\bttsbegin\b", RegexOptions.IgnoreCase).Count,
                Regex.Matches(masked, @"\bttscommit\b", RegexOptions.IgnoreCase).Count,
                Regex.Matches(masked, @"\bttsabort\b", RegexOptions.IgnoreCase).Count));
        }

        foreach (var r in regions)
        {
            if (r.Begins == 0 && r.Commits == 0) continue;
            if (r.Begins == r.Commits) continue;
            // ttsabort closes a transaction too, so a guard clause that aborts on one path
            // legitimately leaves fewer commits than begins.
            if (r.Begins > r.Commits && r.Begins <= r.Commits + r.Aborts) continue;
            v.Add(new XppViolation("TTS001", "warning", r.Line,
                $"ttsbegin × {r.Begins}, ttscommit × {r.Commits}" + (r.Aborts > 0 ? $", ttsabort × {r.Aborts}" : ""),
                "Balance every ttsbegin with a matching ttscommit (or a ttsabort on the failure path). " +
                "An unmatched ttsbegin leaves the transaction open; an unmatched ttscommit throws at runtime."));
        }
    }

    /// <summary>BP004 — developer-only statements left in code (print / breakpoint).</summary>
    private static void CheckDevArtifacts(string masked, List<XppViolation> v) => MatchAll(masked,
        @"^\s*(?:print|breakpoint)\b", "BP004", "warning",
        "Remove developer-only statements (print / breakpoint) before shipping — they still " +
        "compile but go nowhere useful in the cloud. Use the Infolog (info/warning) or telemetry.", v,
        options: RegexOptions.Multiline);

    /// <summary>
    /// CS001 — C# constructs that do not exist in X++. Every one is a guaranteed compile
    /// failure that reads perfectly naturally to anyone who writes C# all day, which is exactly
    /// why they slip into generated X++.
    /// </summary>
    private static void CheckCSharpIsms(string masked, List<XppViolation> v)
    {
        var patterns = new (string Re, RegexOptions Options, string Fix)[]
        {
            (@"\$""", RegexOptions.None,
                "X++ has no string interpolation ($\"…\") — use strFmt(\"%1 / %2\", a, b)."),
            (@"=>", RegexOptions.None,
                "X++ has no lambdas/anonymous methods (=>) — use a named (private) method, or a delegate plus an eventhandler subscription."),
            (@"\bforeach\b", RegexOptions.None,
                "X++ has no foreach — iterate collections with their Enumerator (while (en.moveNext()) { en.current(); }) and tables with while select."),
            (@"\?\?", RegexOptions.None,
                "X++ has no null-coalescing operator (??) — value types hold null-EQUIVALENT values (0, empty string, 1900-01-01); test explicitly."),
            (@"\bstring\s+\w+\s*[;=]", RegexOptions.None,
                "The X++ string type is str (or an EDT) — \"string\" is C#."),
            // xppc: "The name 'bool' does not denote a class, a table, or an extended data type."
            (@"\b(?:bool|decimal|double|long|uint)\s+\w+\s*[;=,)]", RegexOptions.None,
                "C# primitive names do not exist in X++: use boolean, real, int64 and int. " +
                "There are no unsigned types."),
            // xppc: "';' expected." — X++ has no override/virtual; every non-final
            // instance method is virtual and redeclaring the signature overrides it.
            (@"\b(?:public|protected|private|internal)\s+(?:override|virtual)\b", RegexOptions.None,
                "X++ has no override/virtual keywords — redeclare the method with the same signature " +
                "to override it, and mark it final to forbid further overriding."),
            // xppc: "Conflicting modifiers 'protected private'."
            (@"\bprivate\s+protected\b", RegexOptions.None,
                "private protected is not an X++ access combination (\"Conflicting modifiers\"). " +
                "protected internal does compile."),
            // NO generics rule — ApplicationSuite ships `private List<str> …;`, so an offline
            // rule would report Microsoft's own compiling code (see upstream note).
            // xppc: "')' expected." — the catch variable must be DECLARED first and then
            // named alone: `System.Exception ex; … catch (ex)`.
            (@"\bcatch\s*\(\s*(?:System|Microsoft)\.[\w.]+\s+\w+\s*\)", RegexOptions.None,
                "X++ cannot declare the exception variable in the catch: declare it first " +
                "(\"System.ArgumentException ex;\") and write catch (ex)."),
        };
        foreach (var (re, options, fix) in patterns)
        {
            MatchAll(masked, re, "CS001", "error", fix, v, options: options);
        }
    }

    /// <summary>
    /// TTS002 — a catch inside an open ttsbegin/ttscommit scope that can never fire. Inside a
    /// transaction only Exception::UpdateConflict and Exception::DuplicateKeyException are
    /// deliverable to an INNER catch, and only when named explicitly — everything else aborts
    /// the transaction and unwinds to the first catch OUTSIDE the tts block. Depth is
    /// approximated by counting ttsbegin minus ttscommit/ttsabort before the catch — heuristic,
    /// hence warning severity.
    /// </summary>
    private static void CheckCatchInsideTts(string masked, List<XppViolation> v)
    {
        if (!Regex.IsMatch(masked, @"\bttsbegin\b", RegexOptions.IgnoreCase)) return;
        foreach (Match m in Regex.Matches(masked, @"\bcatch\b\s*(?:\(([^)]*)\))?"))
        {
            var before = masked[..m.Index];
            var depth =
                Regex.Matches(before, @"\bttsbegin\b", RegexOptions.IgnoreCase).Count -
                Regex.Matches(before, @"\bttscommit\b", RegexOptions.IgnoreCase).Count -
                Regex.Matches(before, @"\bttsabort\b", RegexOptions.IgnoreCase).Count;
            if (depth <= 0) continue;
            var filter = m.Groups[1].Success ? m.Groups[1].Value : "";
            if (Regex.IsMatch(filter, @"(?:UpdateConflict|DuplicateKeyException)\b", RegexOptions.IgnoreCase)
                && !Regex.IsMatch(filter, "NotRecovered", RegexOptions.IgnoreCase)) continue;
            v.Add(new XppViolation("TTS002", "warning", LineNumber(masked, m.Index),
                string.IsNullOrWhiteSpace(m.Value) ? "catch" : m.Value.Trim(),
                "Inside an open transaction only Exception::UpdateConflict and Exception::DuplicateKeyException reach an inner catch, " +
                "and only when named explicitly — this catch is dead code; every other exception unwinds to the first catch OUTSIDE the tts block. " +
                "Move the try/catch outside ttsbegin/ttscommit (knowledge topic: transactions)."));
        }
    }

    /// <summary>
    /// TTS003 — <c>retry</c> with no visible guard in its catch block. retry jumps back to the
    /// START of the try block; on a deterministic error an unguarded retry loops forever.
    /// </summary>
    private static void CheckUnguardedRetry(string masked, List<XppViolation> v)
    {
        foreach (Match m in Regex.Matches(masked, @"\bretry\s*;"))
        {
            var before = masked[..m.Index];
            var catches = Regex.Matches(before, @"\bcatch\b");
            if (catches.Count == 0) continue; // retry outside catch — the compiler rejects it
            var segment = masked[catches[^1].Index..m.Index];
            if (Regex.IsMatch(segment, @"(\+\+|\+=|\bif\s*\()")) continue;
            v.Add(new XppViolation("TTS003", "warning", LineNumber(masked, m.Index), "retry;",
                "retry jumps back to the start of the try block and discards infolog messages logged since try entry — " +
                "unguarded, it loops forever on a deterministic error. Guard it with a counter " +
                "(retryCount++; if (retryCount > maxRetries) throw …; retry;)."));
        }
    }

    /// <summary>SEL006 — <c>index hint</c> used without evidence of allowIndexHint(true).</summary>
    private static void CheckIndexHint(string masked, List<XppViolation> v)
    {
        if (Regex.IsMatch(masked, @"\ballowIndexHint\s*\(\s*true\s*\)", RegexOptions.IgnoreCase)) return;
        MatchAll(masked, @"\bindex\s+hint\s+\w+", "SEL006", "warning",
            "\"index hint\" is silently IGNORED unless the buffer called allowIndexHint(true) first — and it overrides the " +
            "optimizer, so use it only when measured. For sort order use plain \"index IndexName\" (no hint).", v);
    }

    /// <summary>SEL007 — SQL/C# join syntax that does not exist in X++.</summary>
    private static void CheckForeignJoinSyntax(string masked, List<XppViolation> v)
    {
        MatchAll(masked, @"\b(?:left|right)\s+(?:outer\s+)?join\b", "SEL007", "error",
            "X++ has no left/right join keywords — \"outer join\" IS the left outer join; there is no right outer (swap the buffers). " +
            "Join kinds: join, outer join, exists join, notexists join.", v);
        MatchAll(masked, @"\bjoin\s+\w+\s+on\b", "SEL007", "error",
            "X++ joins have no \"on\" keyword — put the join criteria in the joined buffer's own where clause: " +
            "join otherTable where otherTable.Field == driver.Field.", v);
    }

    /// <summary>
    /// RPT001/RPT002 — SSRS data-provider class shape. A DP that reads parmDataContract()
    /// without [SRSReportParameterAttribute] binds no contract (the dialog values never
    /// arrive), and a DP without a single [SRSReportDataSetAttribute] getter gives SSRS no
    /// dataset to read. Both compile clean and fail only at report run time. PreProcess
    /// variants are exempt from RPT001 (their contract can travel via the controller).
    /// </summary>
    private static void CheckReportDpShape(string code, string masked, List<XppViolation> v)
    {
        var extendsMatch = Regex.Match(masked, @"\bextends\s+(SRSReportDataProvider(?:Base|PreProcess(?:TempDB)?))\b", RegexOptions.IgnoreCase);
        if (!extendsMatch.Success) return;
        var isPreProcess = extendsMatch.Groups[1].Value.Contains("PreProcess", StringComparison.OrdinalIgnoreCase);

        var parmContract = Regex.Match(masked, @"\bparmDataContract\s*\(", RegexOptions.IgnoreCase);
        if (!isPreProcess && parmContract.Success
            && !Regex.IsMatch(code, "SRSReportParameterAttribute", RegexOptions.IgnoreCase))
        {
            v.Add(new XppViolation("RPT001", "error", LineNumber(code, parmContract.Index),
                "parmDataContract() without [SRSReportParameterAttribute]",
                "The DP reads parmDataContract() but declares no contract — add " +
                "[SRSReportParameterAttribute(classStr(MyContract))] on the DP class, or the dialog values never reach processReport(). " +
                "Compiles clean, fails at report run time."));
        }

        var processReport = Regex.Match(masked, @"\bprocessReport\s*\(", RegexOptions.IgnoreCase);
        if (processReport.Success && !Regex.IsMatch(code, "SRSReportDataSetAttribute", RegexOptions.IgnoreCase))
        {
            v.Add(new XppViolation("RPT002", "warning", LineNumber(code, processReport.Index),
                "processReport() without any [SRSReportDataSetAttribute] getter",
                "SSRS reads report data through [SRSReportDataSetAttribute(tableStr(MyTmp))] getter methods — without one this DP " +
                "fills a table nothing reads. Add the getter (returns the tmp buffer after select * from it), or ignore if the " +
                "getters live in a separate partial listing."));
        }
    }

    /// <summary>
    /// BP006 — statements that were REMOVED from the language. pause, window, tableLock and
    /// changeSite are no longer keywords, so xppc does not report them as deprecated: it
    /// reports a syntax error, and the message names the token rather than the statement.
    /// Code carried over from AX 2012 fails here first, and the message does not say why.
    /// </summary>
    private static void CheckRemovedStatements(string masked, List<XppViolation> v)
    {
        var removed = new (string Re, RegexOptions Options, string Fix)[]
        {
            (@"^\s*pause\s*;", RegexOptions.Multiline,
                "pause was removed from X++ (xppc: \"Invalid token ';'\"). Delete it — a batch or " +
                "a service has no console to pause."),
            (@"^\s*window\s+\d", RegexOptions.Multiline,
                "window was removed from X++ (xppc: \"Invalid token\"). Delete it together with the " +
                "print statements it sized."),
            (@"^\s*tableLock\b", RegexOptions.Multiline,
                "tableLock was removed from X++ (xppc: \"The name 'tableLock' does not denote a class, " +
                "a table, or an extended data type\"). Use the select lock hints (pessimisticLock, " +
                "optimisticLock) or a transaction scope."),
            (@"\bchangeSite\s*\(", RegexOptions.IgnoreCase,
                "changeSite was removed from X++ (xppc: \"';' expected\"). Use changeCompany, or set " +
                "the site through the record's InventDim."),
        };
        foreach (var (re, options, fix) in removed)
        {
            MatchAll(masked, re, "BP006", "error", fix, v, options: options);
        }
    }

    /// <summary>
    /// MAC001 — a precompiler directive written without its dot. <c>#define X(1)</c> does not
    /// define anything: the precompiler reads <c>#define</c> as a macro REFERENCE, and the
    /// failure surfaces far away as "The macro 'define' is not defined".
    /// </summary>
    private static void CheckMacroDirectiveForm(string code, List<XppViolation> v) => MatchAll(code,
        @"^\s*#(define|localmacro|macro|macrolib|globaldefine|globalmacro|if|ifnot|undef|defInc|defDec)\s+\w",
        "MAC001", "error",
        "Precompiler directives that name a macro use a DOT, not a space: \"#define.MyMacro(42)\", " +
        "\"#localmacro.MyBlock\", \"#macrolib.MyLibrary\", \"#if.MyMacro\". Written with a space the " +
        "precompiler reads the directive as a macro reference and reports " +
        "\"The macro 'define' is not defined\".", v,
        skipIfComment: false, options: RegexOptions.IgnoreCase | RegexOptions.Multiline);

    /// <summary>
    /// Select statements in masked source: from <c>select</c> to the <c>;</c> or <c>{</c> that
    /// ends the statement — the <c>{</c> matters because a <c>while select</c> body holds
    /// statements of its own and must not be read as part of the header.
    /// </summary>
    private static IEnumerable<(string Text, int Index)> SelectStatements(string masked)
    {
        foreach (Match m in Regex.Matches(masked, @"\bselect\b[^;{]*[;{]", RegexOptions.IgnoreCase))
        {
            yield return (m.Value, m.Index);
        }
    }

    /// <summary>
    /// SEL008 — order by / group by placed after the where of the same segment. X++ fixes the
    /// clause order inside each segment of a select, and a where before the ordering is a
    /// COMPILE error whose message names neither: xppc answers "'join' expected". After a join
    /// the next segment starts over.
    /// </summary>
    private static void CheckSelectClauseOrder(string masked, List<XppViolation> v)
    {
        foreach (var (text, index) in SelectStatements(masked))
        {
            // Segment boundaries are the join keywords; each segment orders independently.
            var segments = new List<(string Text, int Offset)>();
            int last = 0;
            foreach (Match j in Regex.Matches(text, @"\bjoin\b", RegexOptions.IgnoreCase))
            {
                segments.Add((text[last..j.Index], last));
                last = j.Index;
            }
            segments.Add((text[last..], last));

            foreach (var (segText, segOffset) in segments)
            {
                var where = Regex.Match(segText, @"\bwhere\b", RegexOptions.IgnoreCase);
                var ordering = Regex.Match(segText, @"\b(?:order|group)\s+by\b", RegexOptions.IgnoreCase);
                if (!where.Success || !ordering.Success || where.Index >= ordering.Index) continue;
                var at = index + segOffset + ordering.Index;
                v.Add(new XppViolation("SEL008", "error", LineNumber(masked, at),
                    $"{ordering.Value} after where",
                    "Put order by / group by BEFORE the where of the same segment: " +
                    "\"select t order by Field where t.Field != ''\". Written after the where, xppc " +
                    "reports \"'join' expected\" — after a where clause it can only accept another join. " +
                    "Each joined buffer starts a new segment with the same order."));
            }
        }
    }

    /// <summary>
    /// SEL009 — the <c>in</c> operator with an inline container literal. xppc: "Container
    /// literals in 'in' expression are not supported. Declare container variable instead."
    /// </summary>
    private static void CheckInOperatorLiteral(string masked, List<XppViolation> v)
    {
        foreach (var (text, index) in SelectStatements(masked))
        {
            foreach (Match m in Regex.Matches(text, @"\bin\s*\[", RegexOptions.IgnoreCase))
            {
                var at = index + m.Index;
                v.Add(new XppViolation("SEL009", "error", LineNumber(masked, at),
                    text[Math.Max(0, m.Index - 40)..Math.Min(text.Length, m.Index + 20)].Trim(),
                    "The `in` operator needs a container VARIABLE, not an inline literal: " +
                    "declare \"container statuses = [Status::A, Status::B];\" and write " +
                    "\"where t.Status in statuses\". xppc: \"Container literals in 'in' expression are " +
                    "not supported. Declare container variable instead.\" Note the left side must be an " +
                    "ENUM field — for a string or number field, write the OR chain."));
            }
        }
    }

    /// <summary>
    /// SEL010 — a select EXPRESSION that names a buffer variable, and validTimeState given an
    /// expression. <c>(select firstOnly cg).Name</c> looks like the natural shorthand when
    /// <c>cg</c> is already declared, but the expression form takes the TABLE name — xppc
    /// answers "Table 'cg' is not found".
    /// </summary>
    private static void CheckSelectExpressionAndValidTimeState(string masked, List<XppViolation> v)
    {
        // Scoped per method: `QueryBuildDataSource inventSerial = …` in one method must
        // not decide what `inventSerial` means in another, where it is the table name.
        foreach (var (regionText, regionOffset) in TopLevelRegions(masked))
        {
            SelectExpressionViolations(masked, regionOffset, regionText, v);
        }
        ValidTimeStateViolations(masked, v);
    }

    /// <summary>
    /// Top-level brace blocks of the masked source — one per method when the input is a method
    /// body or a concatenated class. The prelude (class declaration / field list) prefixes
    /// every region so a class field counts as declared in each method.
    /// </summary>
    private static List<(string Text, int Offset)> TopLevelRegions(string masked)
    {
        var regions = new List<(string Text, int Offset)>();
        int depth = 0;
        int start = 0;
        var outside = "";
        for (int i = 0; i < masked.Length; i++)
        {
            var ch = masked[i];
            if (ch == '{')
            {
                if (depth == 0)
                {
                    outside += masked[start..i];
                    start = i;
                }
                depth++;
            }
            else if (ch == '}')
            {
                depth--;
                if (depth <= 0)
                {
                    regions.Add((masked[start..(i + 1)], start));
                    start = i + 1;
                    depth = 0;
                }
            }
        }
        if (regions.Count == 0) return [(masked, 0)];
        return regions.Select(r => (outside + r.Text, r.Offset - outside.Length)).ToList();
    }

    private static void SelectExpressionViolations(string masked, int regionOffset, string regionText, List<XppViolation> v)
    {
        // Buffers declared here as `Type name;` — and only those whose NAME differs from
        // their TYPE. X++ is case-insensitive, so the common `UserGroupInfo userGroupInfo;`
        // leaves an identifier that resolves as the table either way and compiles;
        // `CustGroup cg;` does not, and `(select firstonly cg)` is then "Table 'cg' is not
        // found" in every form (xppc 7.0.7996.33, upstream probe round 4).
        var aliasedBuffers = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(regionText, @"^[ \t]*([A-Za-z_]\w*)[ \t]+([A-Za-z_]\w*)[ \t]*[;,=]", RegexOptions.Multiline))
        {
            var type = m.Groups[1].Value;
            var name = m.Groups[2].Value;
            // `flush CustParameters;` is a statement, not a declaration — the type slot has
            // to be a real type name, and the compiler's own keyword set is what says so.
            if (CompilerFacts.IsReservedKeyword(type) || CallPrecedingKeywords.Contains(type.ToLowerInvariant())) continue;
            if (string.Equals(type, name, StringComparison.OrdinalIgnoreCase)) continue;
            aliasedBuffers.Add(name.ToLowerInvariant());
        }

        foreach (Match m in Regex.Matches(regionText, @"\(\s*select\b((?:\s+\w+)*?)\s+([A-Za-z_]\w*)\s*[).]", RegexOptions.IgnoreCase))
        {
            if (!aliasedBuffers.Contains(m.Groups[2].Value.ToLowerInvariant())) continue;
            var at = regionOffset + m.Index;
            if (at < 0 || at >= masked.Length) continue;
            v.Add(new XppViolation("SEL010", "error", LineNumber(masked, at), m.Value.Trim(),
                $"A select EXPRESSION names the TABLE, not a buffer: \"(select firstOnly MyTable).Field\". " +
                $"\"{m.Groups[2].Value}\" is declared as a buffer in this method, so xppc answers \"Table '{m.Groups[2].Value}' is not found\". " +
                "Either name the table, or use an ordinary select statement into the buffer."));
        }
    }

    private static void ValidTimeStateViolations(string masked, List<XppViolation> v)
    {
        // validTimeState takes plain identifiers only: a call ("Invalid token '::'"), a
        // field access ("Invalid token '.'") and even a date literal ("'identifier'
        // expected") are all parse errors.
        foreach (Match m in Regex.Matches(masked, @"\bvalidTimeState\s*\(([^)]*)\)", RegexOptions.IgnoreCase))
        {
            var operands = m.Groups[1].Value.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            if (operands.Count > 0 && operands.All(o => Regex.IsMatch(o, @"^[A-Za-z_]\w*$"))) continue;
            v.Add(new XppViolation("SEL010", "error", LineNumber(masked, m.Index), m.Value.Trim(),
                "validTimeState takes plain variable names — not a call (\"Invalid token '::'\"), " +
                "not a field (\"Invalid token '.'\") and not a date literal (\"'identifier' expected\"). " +
                "Assign it first: \"utcdatetime asOf = DateTimeUtil::utcNow(); select validTimeState(asOf) t;\"."));
        }
    }

    /// <summary>Values an attribute argument may take: the compiler stores literals, nothing else.</summary>
    private static readonly Regex AttributeLiteralRe = new(
        @"^(?:-?\d+(?:\.\d+)?|true|false|null|#\w+|\w+\s*::\s*\w+|\d{1,2}\\\d{1,2}\\\d{4}|@?[""'][^""']*[""'])$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// True when the bracket content is a list of attributes rather than a container literal or
    /// a multi-assignment: at depth 0 an attribute list holds only names and commas, while
    /// <c>[DatabaseLogType::Update, tableNum(X)]</c> shows <c>::</c> and <c>[a, b] = c</c>
    /// shows an assignment.
    /// </summary>
    private static bool LooksLikeAttributeList(string body)
    {
        int depth = 0;
        bool sawName = false;
        for (int i = 0; i < body.Length; i++)
        {
            var ch = body[i];
            if (ch == '(' || ch == '[') { depth++; continue; }
            if (ch == ')' || ch == ']') { depth--; continue; }
            if (depth > 0) continue;
            if (char.IsWhiteSpace(ch) || ch == ',') continue;
            if (char.IsLetter(ch) || ch == '_')
            {
                var rest = Regex.Match(body[i..], @"^[A-Za-z_]\w*").Value;
                i += rest.Length - 1;
                sawName = true;
                continue;
            }
            return false; // ::, quotes, digits, operators — not an attribute list
        }
        return sawName;
    }

    /// <summary>
    /// ATTR001 — an attribute argument that is not a compile-time literal; ATTR002 —
    /// [SysObsolete] without all three arguments. The compiler does not construct the
    /// attribute: it stores the class name and the literal values, so a variable is "Invalid
    /// token ','" and a call is "Invalid token '('". An intrinsic is fine (it IS a literal
    /// after compilation) and so is a macro, which expands before the compiler sees it.
    /// </summary>
    private static void CheckAttributeArguments(string code, string masked, List<XppViolation> v)
    {
        var lines = code.Split('\n');
        var found = new List<(string Name, string ArgText, int At)>();

        // One bracket may carry several attributes, and they may span lines, so the bracket is
        // located first and each Name(args) inside it read separately. The bracket must BE the
        // line (nothing after the closing ]), must not contain a statement, and at depth 0 may
        // hold only attribute names and commas. Without the last test a container literal at
        // the start of a line and a multi-assignment `[a, b] = f();` read as attributes, which
        // is 195 shipped classes' worth of noise.
        foreach (Match bracket in Regex.Matches(masked, @"^[ \t]*\[([^\];=]*(?:\n[^\];=]*){0,5}?)\]\s*$", RegexOptions.Multiline))
        {
            var body = bracket.Groups[1].Value;
            if (!LooksLikeAttributeList(body)) continue;
            // A container literal of intrinsics — `[fieldNum(T, A), fieldNum(T, B)]` on its
            // own line — passes the shape test, because an intrinsic name followed by a
            // parenthesis is exactly what an attribute looks like. An attribute is never an
            // intrinsic, so the head settles it.
            var head = Regex.Match(body, @"^\s*([A-Za-z_]\w*)");
            if (head.Success && CompilerFacts.IntrinsicInfo(head.Groups[1].Value) is not null) continue;
            var bodyStart = bracket.Index + bracket.Value.IndexOf('[') + 1;
            var attrRe = new Regex(@"([A-Za-z_]\w*)\s*\(");
            int searchAt = 0;
            while (searchAt < body.Length)
            {
                var a = attrRe.Match(body, searchAt);
                if (!a.Success) break;
                // Skip a nested call — only the attribute name sits at depth 0.
                int depth = 0;
                for (int i = 0; i < a.Index; i++)
                {
                    if (body[i] == '(') depth++;
                    else if (body[i] == ')') depth--;
                }
                if (depth != 0)
                {
                    searchAt = a.Index + a.Length;
                    continue;
                }
                // Read the balanced argument list.
                int d = 0;
                int end = a.Index + a.Length - 1;
                for (int i = end; i < body.Length; i++)
                {
                    if (body[i] == '(') d++;
                    else if (body[i] == ')')
                    {
                        d--;
                        if (d == 0) { end = i; break; }
                    }
                }
                found.Add((a.Groups[1].Value, body[(a.Index + a.Length)..end], bodyStart + a.Index));
                searchAt = end;
            }
        }

        foreach (var (name, argText, at) in found)
        {
            var lineNo = LineNumber(masked, at);
            var excerpt = lineNo - 1 < lines.Length && lines[lineNo - 1].Trim().Length > 0
                ? lines[lineNo - 1].Trim()
                : $"[{name}(…)]";

            // Split on top-level commas so a nested intrinsic call stays one argument.
            var args = new List<string>();
            int depth = 0;
            var buf = "";
            foreach (var ch in argText)
            {
                if (ch == '(' || ch == '[') depth++;
                else if (ch == ')' || ch == ']') depth--;
                if (ch == ',' && depth == 0)
                {
                    args.Add(buf.Trim());
                    buf = "";
                    continue;
                }
                buf += ch;
            }
            if (buf.Trim().Length > 0) args.Add(buf.Trim());

            foreach (var arg in args)
            {
                if (arg.Length == 0) continue;
                if (AttributeLiteralRe.IsMatch(arg)) continue;
                var call = Regex.Match(arg, @"^([A-Za-z_]\w*)\s*\(");
                if (call.Success && CompilerFacts.IntrinsicInfo(call.Groups[1].Value) is not null) continue; // classStr(...), tableStr(...), …
                v.Add(new XppViolation("ATTR001", "error", lineNo, excerpt,
                    $"Attribute arguments must be compile-time literals — \"{arg}\" is not one. " +
                    "The compiler stores the literal values without constructing the attribute, so a " +
                    "variable reads as \"Invalid token ','\" and a call as \"Invalid token '('\". " +
                    "Allowed: a number, a quoted string, true/false/null, an enum value (MyEnum::Value), " +
                    "a date literal, an intrinsic (classStr/tableStr/methodStr/…) or a #define macro."));
            }

            if (Regex.IsMatch(name, "^SysObsolete(Attribute)?$", RegexOptions.IgnoreCase) && args.Count < 3)
            {
                v.Add(new XppViolation("ATTR002", "warning", lineNo, excerpt,
                    "Give SysObsolete all three arguments — message, isError AND the date: " +
                    "[SysObsolete(\"Use MyNewClass instead.\", false, 31\\12\\2026)]. The constructor defaults " +
                    "them, but xppbp answers BPCheckSysObsoleteAttributeParametersMismatch when they are " +
                    "omitted, and attribute arguments are positional so the date cannot be skipped."));
            }
        }
    }

    /// <summary>
    /// EXT001 — an extension-method class whose members do not match its shape. A static class
    /// holds extension methods; every method in it must be static. xppc: "The method 'bad'
    /// must be declared as static because it is declared in a static class" and "Extension
    /// class 'X_Extension' must be static and public or internal".
    /// </summary>
    private static void CheckExtensionMethodClassShape(string code, string masked, List<XppViolation> v)
    {
        var lines = code.Split('\n');
        var classDecl = Regex.Match(masked,
            @"\b(?:(public|internal|private)\s+)?(static\s+)?(?:final\s+)?class\s+(\w+_Extension)\b",
            RegexOptions.IgnoreCase);
        if (!classDecl.Success) return;
        var isStatic = classDecl.Groups[2].Success;
        var isCoc = Regex.IsMatch(masked, @"\[\s*ExtensionOf", RegexOptions.IgnoreCase);

        if (!isStatic && !isCoc)
        {
            var lineNo = LineNumber(masked, classDecl.Index);
            v.Add(new XppViolation("EXT001", "error", lineNo,
                lineNo - 1 < lines.Length ? lines[lineNo - 1].Trim() : classDecl.Value,
                "An _Extension class is one of two things, and this one is neither: a Chain of Command " +
                "class ([ExtensionOf(...)] final class) or an extension-method class (public static class " +
                "whose methods are all static and take the extended type first). xppc: \"Extension class " +
                $"'{classDecl.Groups[3].Value}' must be static and public or internal\"."));
            return;
        }
        if (!isStatic) return;

        var maskedLines = masked.Split('\n');
        for (int i = 0; i < maskedLines.Length; i++)
        {
            var decl = Regex.Match(maskedLines[i], @"^\s*(?:public|protected|private|internal)\s+(?!static\b)[A-Za-z_][\w.]*\s+(\w+)\s*\(");
            if (!decl.Success) continue;
            v.Add(new XppViolation("EXT001", "error", i + 1,
                i < lines.Length ? lines[i].Trim() : maskedLines[i].Trim(),
                $"Every method in a static extension class must be static: \"public static <Type> {decl.Groups[1].Value}" +
                $"(<ExtendedType> _target, …)\". xppc: \"The method '{decl.Groups[1].Value}' must be declared as " +
                "static because it is declared in a static class\"."));
        }
    }

    /// <summary>
    /// KW001 — a variable named after a reserved word. The reserved set is the parser's own
    /// (115 words, read from the shipped compiler), and it is not the set the language
    /// reference lists: <c>having</c>, <c>foreach</c>, <c>async</c>, <c>await</c> and
    /// <c>namespace</c> are reserved without being implemented. <c>in</c> is reserved but
    /// exempted and stays legal.
    /// </summary>
    private static void CheckReservedIdentifiers(string code, string masked, List<XppViolation> v)
    {
        var lines = code.Split('\n');
        foreach (Match m in Regex.Matches(masked,
            @"^\s*(?:(?:public|protected|private|internal|static|final|const|readonly)\s+)*(str\s+\d+|str|int64|int|real|boolean|date|utcdatetime|timeOfDay|guid|container|anytype)\s+([A-Za-z_]\w*)\s*[;,=]",
            RegexOptions.IgnoreCase | RegexOptions.Multiline))
        {
            var name = m.Groups[2].Value;
            if (!CompilerFacts.IsReservedKeyword(name)) continue;
            var lineNo = LineNumber(masked, m.Index);
            v.Add(new XppViolation("KW001", "error", lineNo,
                lineNo - 1 < lines.Length ? lines[lineNo - 1].Trim() : m.Value.Trim(),
                $"\"{name}\" is a reserved word in X++ (the parser's own keyword set) and cannot name a " +
                "variable. Rename it — the compiler reports the failure on the token that follows, not on " +
                "the name, so the message will not point here."));
        }
    }

    // ── AxReport XML rules (codeType="xml-report") ───────────────────────────

    /// <summary>RPT101 — AxReport without a design node.</summary>
    private static void CheckReportHasDesign(string code, List<XppViolation> v)
    {
        if (!Regex.IsMatch(code, @"<AxReport[\s>]", RegexOptions.IgnoreCase)) return;
        if (Regex.IsMatch(code, @"<AxReportDesign\b", RegexOptions.IgnoreCase)) return;
        v.Add(new XppViolation("RPT101", "error", null, "<AxReport> — no <AxReportDesign>",
            "The report declares no design — ssrsReportStr(report, design) can never reference it and the report cannot run. " +
            "`d365fo generate report` scaffolds one auto design named \"AutoDesign\"."));
    }

    /// <summary>RPT102 — report dataset without a Query.</summary>
    private static void CheckReportDatasetShape(string code, List<XppViolation> v)
    {
        if (!Regex.IsMatch(code, @"<AxReport[\s>]", RegexOptions.IgnoreCase)) return;
        foreach (Match m in Regex.Matches(code, @"<AxReportDataSet\b[\s\S]*?</AxReportDataSet>", RegexOptions.IgnoreCase))
        {
            if (Regex.IsMatch(m.Value, "<Query>", RegexOptions.IgnoreCase)) continue;
            v.Add(new XppViolation("RPT102", "warning", LineNumber(code, m.Index),
                "<AxReportDataSet> without <Query>",
                "A ReportDataProvider dataset needs <Query>SELECT * FROM DPClass.TmpTable</Query> (with " +
                "<DataSourceType>ReportDataProvider</DataSourceType>) — without it the dataset is empty at run time."));
        }
    }

    // ── XML rules ────────────────────────────────────────────────────────────

    private static void CheckMissingAlternateKey(string code, List<XppViolation> v)
    {
        var isExtension = Regex.IsMatch(code, @"<AxTableExtension[\s>]", RegexOptions.IgnoreCase);
        var isBaseTable = Regex.IsMatch(code, @"<AxTable[\s>]", RegexOptions.IgnoreCase);
        if (!isExtension && !isBaseTable) return;

        // A table extension merges into the base table it targets — it does not
        // define a new physical table, and D365FO's extension model already gives
        // it the base table's alternate key automatically. `d365fo generate
        // extension Table <target>` legitimately emits a field-less, index-less
        // shell (a placeholder, or purely a CoC/event-handler target) as a normal,
        // common pattern, and there is nothing in that extension's own delta that
        // needs an alternate key. The real BPCheckAlternateKeyAbsent's documented
        // precondition (Microsoft Learn's Customization Analysis Report reference)
        // is "tables that have a unique index" — only hold the extension to this
        // rule once it actually introduces an index of its own.
        if (isExtension && !Regex.IsMatch(code, @"<AxTableIndex[\s>]", RegexOptions.IgnoreCase)) return;

        if (!Regex.IsMatch(code, @"<AlternateKey>\s*Yes\s*</AlternateKey>", RegexOptions.IgnoreCase))
        {
            // Warning, not error: xppbp raises BPCheckAlternateKeyAbsent as a warning and the
            // table still builds. As an error it made a legitimately single-index table
            // unsatisfiable (upstream eval #7).
            v.Add(new XppViolation("XML001", "warning", null,
                "<AxTable> — no index with <AlternateKey>Yes</AlternateKey>",
                "Add an <AxTableIndex> with <AlternateKey>Yes</AlternateKey> unless the table " +
                "deliberately has none — xppbp reports BPCheckAlternateKeyAbsent as a warning and " +
                "the table still builds. `d365fo generate table` adds this automatically."));
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
