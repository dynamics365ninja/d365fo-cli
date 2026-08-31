using System.Text;
using System.Text.RegularExpressions;

namespace D365FO.Core.Validation;

/// <summary>
/// One unresolved reference found by <see cref="ReferenceResolver"/>.
/// Kind: unknown-type | unknown-static-member | unknown-method | unknown-field |
/// unknown-label | unknown-intrinsic-target | arity-mismatch.
/// </summary>
public sealed record ReferenceViolation(string Kind, string Severity, int Line, string Identifier, string Detail);

public sealed record ResolveResult(IReadOnlyList<ReferenceViolation> Violations, int VerifiedCount);

/// <summary>Result of a method lookup: found + optional signature text.</summary>
public sealed record MethodLookup(string? Signature);

/// <summary>
/// Minimal index surface the resolver needs. Implemented by
/// <c>MetadataRepository</c>; tests can supply an in-memory fake.
/// </summary>
public interface IReferenceIndex
{
    /// <summary>All kinds the name resolves to: class, table, view, map, data-entity, edt, enum, form, query.</summary>
    IReadOnlyList<string> SymbolKinds(string name);

    bool MenuItemExists(string name);

    /// <summary>Find a method on an object, walking the inheritance chain and CoC extensions.</summary>
    MethodLookup? FindMethod(string ownerName, string methodName);

    /// <summary>Field on a table/view/entity/map incl. extension-added fields (system fields handled by the resolver).</summary>
    bool FieldExists(string tableName, string fieldName);

    /// <summary>Label key exists (optionally scoped to a label file), any language.</summary>
    bool LabelExists(string key, string? labelFile);

    bool LabelFileExists(string fileId);
}

/// <summary>
/// Semantic reference resolver for generated X++ code — port of the upstream
/// MCP server's <c>resolve_references</c> anti-hallucination gate.
///
/// Extracts every external identifier from an X++ snippet and verifies it
/// against the indexed codebase. Nothing is assumed from training data — a
/// reference is either proven by the index or reported.
///
/// Severity model (conservative — false blocks are worse than misses):
///   error   — intrinsic target missing, static type/method missing,
///             field missing on a confidently-bound table, arity mismatch,
///             modern label id missing in a known label file
///   warning — unknown declared type (kernel classes are not in metadata XML),
///             instance method missing, legacy label not found,
///             label file unknown (may be created later in the same task)
/// </summary>
public static class ReferenceResolver
{
    // ── X++ language tables ──────────────────────────────────────────────────

    private static readonly HashSet<string> XppKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "abstract", "anytype", "as", "asc", "at", "avg", "break", "breakpoint", "by",
        "byref", "case", "catch", "changecompany", "class", "client", "const", "container",
        "continue", "count", "crosscompany", "default", "delegate", "delete_from", "desc",
        "display", "div", "do", "edit", "element", "else", "eventhandler", "exists",
        "extends", "false", "final", "finally", "firstfast", "firstonly", "firstonly10",
        "firstonly100", "firstonly1000", "flush", "for", "forceliterals", "forcenestedloop",
        "forceplaceholders", "forceselectorder", "forupdate", "from", "generateonly",
        "group", "if", "implements", "index", "insert_recordset", "interface", "internal",
        "is", "join", "like", "maxof", "minof", "mod", "new", "next", "nofetch",
        "notexists", "null", "optimisticlock", "order", "outer", "pause", "pessimisticlock",
        "print", "private", "protected", "public", "readonly", "repeatableread", "retry",
        "return", "reverse", "select", "server", "setting", "static", "sum", "super",
        "switch", "tablelock", "this", "throw", "true", "try", "ttsabort", "ttsbegin",
        "ttscommit", "update_recordset", "using", "validtimestate", "void", "where",
        "while", "window",
    };

    private static readonly HashSet<string> XppBuiltinTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "int", "int64", "real", "str", "boolean", "date", "utcdatetime", "timeofday",
        "anytype", "container", "guid", "void", "var",
    };

    /// <summary>
    /// Kernel (binary) classes are NOT present in PackagesLocalDirectory metadata
    /// XML, so the index cannot prove them. Common ones are allow-listed; unknown
    /// declared types degrade to warnings precisely because this list is not
    /// exhaustive.
    /// </summary>
    private static readonly HashSet<string> KernelTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "object", "xrecord", "common", "xsession", "xinfo", "xglobal", "xapplication",
        "xversion", "args", "classfactory",
        // Forms
        "formrun", "formdatasource", "formdataobject", "formcontrol", "formdesign",
        "formstringcontrol", "formbuttoncontrol", "formcheckboxcontrol",
        "formcomboboxcontrol", "formdatecontrol", "formdatetimecontrol",
        "formintcontrol", "formint64control", "formrealcontrol", "formgridcontrol",
        "formgroupcontrol", "formtabcontrol", "formtabpagecontrol",
        "formreferencegroupcontrol", "formfunctionbuttoncontrol",
        "formcommandbuttoncontrol", "formmenubuttoncontrol", "formactionpanecontrol",
        "formactionpanetabcontrol", "formbuttongroupcontrol", "formstaticcontrol",
        "formwindowcontrol", "formtreecontrol", "formlistcontrol",
        // Query framework
        "query", "queryrun", "querybuilddatasource", "querybuildrange",
        "querybuildlink", "querybuildfieldlist", "queryfilter", "queryhavingfilter",
        // Collections
        "map", "set", "list", "array", "struct", "listenumerator", "listiterator",
        "mapenumerator", "setenumerator", "recordinsertlist", "recordsortedlist",
        "recordlinklist",
        // Reflection
        "dicttable", "dictfield", "dictclass", "dictenum", "dicttype", "dictindex",
        "dictrelation", "dictview", "treenode",
        // IO / misc
        "textbuffer", "binary", "xmldocument", "xmlelement", "xmlnode", "xmlnodelist",
        "xmlattribute", "xmlreader", "xmlwriter", "textio", "commaio", "asciiio",
        "connection", "userconnection", "statement", "resultset", "sqlsystem",
        "sqldatadictionary", "sqlstatementexecutepermission", "executepermission",
        "fileiopermission", "runaspermission", "datetimeutil", "timezone", "random",
        "runbase", "image", "clrinterop", "clrobject", "thread", "webrequest",
        "webresponse", "gc", "session", "infolog", "debug", "global",
        // Kernel enums (not in metadata XML). `noyes` and `exception` are the two
        // commonest names in shipped X++, and precisely the two an index-only check
        // used to fail as hallucinations — `d365fo search any NoYes` then offers
        // NoYesBlank / NoYesCombo / DefaultNoYes, which are different types.
        "noyes", "exception",
        "types", "tablescope", "utcdatetimeorder", "dateorder", "dateday",
        "datemonth", "dateyear", "statementtype", "concurrencymodel", "isolationlevel",
        // More kernel enums shipped metadata and attributes use (upstream measured 44 such
        // names in a real install; this list is known-incomplete, which is why unknown
        // DECLARED types degrade to warnings rather than errors).
        "utilelementtype", "tablegroup", "accessright", "accesstype", "sortorder",
        "joinmode", "menuitemtype", "messageseverity", "openmode", "units",
    };

    /// <summary>
    /// True when <paramref name="name"/> is a kernel (binary) type or kernel enum. Those are
    /// NOT present in PackagesLocalDirectory metadata XML, so an index-only existence check is
    /// in no position to judge them — callers must treat a hit here as verified, never as a
    /// hallucination.
    /// </summary>
    public static bool IsKernelType(string name) => KernelTypes.Contains(name);

    /// <summary>Methods available on every table buffer via the kernel xRecord/Common base.</summary>
    private static readonly HashSet<string> TableBuiltinMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "insert", "doinsert", "update", "doupdate", "delete", "dodelete", "write",
        "validatewrite", "validatedelete", "validatefield", "validatefieldvalue",
        "initvalue", "modifiedfield", "modifiedfieldvalue", "clear", "selectforupdate",
        "selectlocked", "reread", "checkrecord", "skipdatamethods", "skipdatabaselog",
        "skipevents", "skipdeleteactions", "skipdeletemethod", "skipaosvalidation",
        "merge", "data", "orig", "postload", "caption", "helpfield", "tooltipfield",
        "tooltiprecord", "defaultfield", "defaultrow", "settmp", "settmpdata",
        "istmp", "wasvalidated", "recordlevelsecurity", "cansubmittoworkflow",
        "tablename", "fieldbuffercount", "dispose", "getfieldvalue", "setfieldvalue",
        "existsalready", "renameprimarykey", "aosvalidatedelete", "aosvalidateinsert",
        "aosvalidateread", "aosvalidateupdate", "joinchildren", "rowcount", "queryrun",
    };

    /// <summary>System fields present on every table (kernel-managed, not in metadata XML).</summary>
    internal static readonly HashSet<string> TableSystemFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "recid", "tableid", "dataareaid", "recversion", "partition",
        "createddatetime", "createdby", "modifieddatetime", "modifiedby",
        "createdtransactionid", "modifiedtransactionid",
    };

    /// <summary>Methods available on every class instance via the kernel Object base.</summary>
    private static readonly HashSet<string> ObjectBuiltinMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "new", "finalize", "tostring", "handle", "notify", "wait", "objectonserver",
        "usagecount", "owner", "gettimeouttimerhandle", "cancurrenttimeout",
        "setrefcountzero", "equal",
    };

    private static readonly HashSet<string> TableLikeTypes = new(StringComparer.OrdinalIgnoreCase)
        { "table", "view", "map", "data-entity", "table-extension" };

    /// <summary>
    /// Intrinsic function → expected symbol kinds of the FIRST argument.
    /// null = any indexed symbol kind counts (e.g. menuItemDisplayStr).
    /// </summary>
    private static readonly Dictionary<string, string[]?> IntrinsicTargetTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["classstr"] = new[] { "class", "class-extension" },
        ["classnum"] = new[] { "class", "class-extension" },
        ["tablestr"] = new[] { "table", "view", "map", "data-entity", "table-extension" },
        ["tablenum"] = new[] { "table", "view", "map", "data-entity" },
        ["fieldstr"] = new[] { "table", "view", "map", "data-entity" },
        ["fieldnum"] = new[] { "table", "view", "map", "data-entity" },
        ["enumstr"] = new[] { "enum" },
        ["enumnum"] = new[] { "enum" },
        ["enumcnt"] = new[] { "enum" },
        ["extendedtypestr"] = new[] { "edt" },
        ["extendedtypenum"] = new[] { "edt" },
        ["formstr"] = new[] { "form" },
        ["querystr"] = new[] { "query" },
        ["viewstr"] = new[] { "view" },
        ["mapstr"] = new[] { "map" },
        ["methodstr"] = new[] { "class", "table", "form", "class-extension" },
        ["staticmethodstr"] = new[] { "class", "class-extension" },
        ["dataentitydatasourcestr"] = new[] { "data-entity" },
        ["tablefieldgroupstr"] = new[] { "table", "table-extension" },
        ["menuitemdisplaystr"] = null,
        ["menuitemactionstr"] = null,
        ["menuitemoutputstr"] = null,
        ["tilestr"] = null,
        ["resourcestr"] = null,
    };

    // ── Code preprocessing ──────────────────────────────────────────────────

    private sealed record StringLiteral(string Value, int Index);

    /// <summary>Blank comments and string literals while preserving offsets/line numbers.</summary>
    private static (string Cleaned, List<StringLiteral> Strings) CleanCode(string code)
    {
        var strings = new List<StringLiteral>();
        var chars = code.ToCharArray();
        int n = chars.Length;
        int i = 0;
        void Blank(int from, int to)
        {
            for (int k = from; k < to && k < n; k++)
                if (chars[k] != '\n') chars[k] = ' ';
        }
        while (i < n)
        {
            char c = code[i];
            char next = i + 1 < n ? code[i + 1] : '\0';
            if (c == '/' && next == '/')
            {
                int j = i;
                while (j < n && code[j] != '\n') j++;
                Blank(i, j);
                i = j;
            }
            else if (c == '/' && next == '*')
            {
                int j = code.IndexOf("*/", i + 2, StringComparison.Ordinal);
                j = j == -1 ? n : j + 2;
                Blank(i, j);
                i = j;
            }
            else if (c == '"' || c == '\'')
            {
                char quote = c;
                int j = i + 1;
                var value = new StringBuilder();
                while (j < n)
                {
                    if (code[j] == '\\' && j + 1 < n) { value.Append(code[j]).Append(code[j + 1]); j += 2; continue; }
                    if (code[j] == quote) break;
                    value.Append(code[j]);
                    j++;
                }
                strings.Add(new StringLiteral(value.ToString(), i + 1));
                Blank(i + 1, Math.Min(j, n));
                i = Math.Min(j + 1, n);
            }
            else
            {
                i++;
            }
        }
        return (new string(chars), strings);
    }

    private static int LineOf(string code, int index)
    {
        int line = 1;
        for (int i = 0; i < index && i < code.Length; i++)
            if (code[i] == '\n') line++;
        return line;
    }

    // ── Signature arity ─────────────────────────────────────────────────────

    private static (int Min, int Max)? ParseSignatureArity(string signature)
    {
        int open = signature.IndexOf('(');
        int close = signature.LastIndexOf(')');
        if (open == -1 || close == -1 || close < open) return null;
        var inner = signature.Substring(open + 1, close - open - 1).Trim();
        if (inner.Length == 0) return (0, 0);
        var parameters = SplitTopLevel(inner);
        int optional = parameters.Count(p => p.Contains('='));
        return (parameters.Count - optional, parameters.Count);
    }

    /// <summary>Split on top-level commas (ignores commas inside (), [], &lt;&gt;).</summary>
    private static List<string> SplitTopLevel(string text)
    {
        var parts = new List<string>();
        int depth = 0;
        var current = new StringBuilder();
        foreach (char ch in text)
        {
            if (ch is '(' or '[' or '<') depth++;
            else if (ch is ')' or ']' or '>') depth = Math.Max(0, depth - 1);
            if (ch == ',' && depth == 0)
            {
                parts.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }
        if (current.ToString().Trim().Length > 0) parts.Add(current.ToString());
        return parts;
    }

    /// <summary>Extract the balanced argument list starting at the '(' at <paramref name="openIdx"/>.</summary>
    private static string? ExtractCallArgs(string code, int openIdx)
    {
        int depth = 0;
        for (int i = openIdx; i < code.Length; i++)
        {
            if (code[i] == '(') depth++;
            else if (code[i] == ')')
            {
                depth--;
                if (depth == 0) return code.Substring(openIdx + 1, i - openIdx - 1);
            }
        }
        return null;
    }

    private static int CountCallArgs(string argsText)
        => argsText.Trim().Length == 0 ? 0 : SplitTopLevel(argsText).Count;

    // ── Local declaration collection ────────────────────────────────────────

    private sealed class LocalScope
    {
        public HashSet<string> DeclaredNames { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Bindings { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static LocalScope CollectLocals(string cleaned)
    {
        var scope = new LocalScope();

        // Class / interface declarations inside the snippet
        foreach (Match m in Regex.Matches(cleaned, @"\b(?:class|interface)\s+([A-Za-z_]\w*)"))
            scope.DeclaredNames.Add(m.Groups[1].Value);

        // `Type var;`, `Type var = ...`, `Type var, var2;` — statement-leading position
        foreach (Match m in Regex.Matches(cleaned,
            @"(^|[;{}\n])\s*([A-Za-z_]\w*)\s+([A-Za-z_]\w*(?:\s*,\s*[A-Za-z_]\w*)*)\s*(?=[=;,)])"))
        {
            var typeName = m.Groups[2].Value;
            // `next` is a CoC keyword (`next methodName(...)`), never a type.
            if (XppKeywords.Contains(typeName) || typeName.Equals("next", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var raw in m.Groups[3].Value.Split(','))
            {
                var varName = raw.Trim();
                if (varName.Length == 0 || XppKeywords.Contains(varName)) continue;
                scope.DeclaredNames.Add(varName);
                scope.Bindings[varName] = typeName;
            }
        }

        // Method parameters: `(Type _a, Type _b = default)`
        foreach (Match m in Regex.Matches(cleaned, @"\(([^()]*)\)"))
        {
            foreach (var param in SplitTopLevel(m.Groups[1].Value))
            {
                var pm = Regex.Match(param.Trim(), @"^([A-Za-z_]\w*)\s+([A-Za-z_]\w*)");
                if (!pm.Success) continue;
                if (XppKeywords.Contains(pm.Groups[1].Value)) continue;
                scope.DeclaredNames.Add(pm.Groups[2].Value);
                scope.Bindings[pm.Groups[2].Value] = pm.Groups[1].Value;
            }
        }

        return scope;
    }

    // ── Main resolver ───────────────────────────────────────────────────────

    public static ResolveResult Resolve(string code, IReferenceIndex index)
    {
        var violations = new List<ReferenceViolation>();
        int verifiedCount = 0;
        var (cleaned, strings) = CleanCode(code);
        var locals = CollectLocals(cleaned);

        var kindCache = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<string> LookupKinds(string name)
        {
            if (!kindCache.TryGetValue(name, out var kinds))
            {
                try { kinds = index.SymbolKinds(name); }
                catch { kinds = Array.Empty<string>(); }
                kindCache[name] = kinds;
            }
            return kinds;
        }

        bool IsKnownType(string name)
            => XppBuiltinTypes.Contains(name)
               || KernelTypes.Contains(name)
               || locals.DeclaredNames.Contains(name)
               || LookupKinds(name).Count > 0;

        // ── 1. Label references (from original string literals) ─────────────
        foreach (var s in strings)
        {
            var modern = Regex.Match(s.Value, @"^@([A-Za-z][A-Za-z0-9_]*):([A-Za-z0-9_]+)$");
            var legacy = Regex.Match(s.Value, @"^@([A-Z]{2,4}\d+)$");
            if (modern.Success)
            {
                var fileId = modern.Groups[1].Value;
                var labelId = modern.Groups[2].Value;
                if (index.LabelExists(labelId, fileId))
                {
                    verifiedCount++;
                }
                else
                {
                    // Distinguish: known label file with missing id (error) vs unknown file (warning)
                    bool fileKnown;
                    try { fileKnown = index.LabelFileExists(fileId); } catch { fileKnown = false; }
                    violations.Add(new ReferenceViolation("unknown-label",
                        fileKnown ? "error" : "warning",
                        LineOf(code, s.Index),
                        $"@{fileId}:{labelId}",
                        fileKnown
                            ? $"Label id \"{labelId}\" not found in label file \"{fileId}\". Use `d365fo search label` to find the right id or `d365fo label create` to add it."
                            : $"Label file \"{fileId}\" not found in the index. If it is new, create the label first (`d365fo label create`), then re-run."));
                }
            }
            else if (legacy.Success)
            {
                var key = legacy.Groups[1].Value;
                if (index.LabelExists(key, null))
                {
                    verifiedCount++;
                }
                else
                {
                    violations.Add(new ReferenceViolation("unknown-label", "warning",
                        LineOf(code, s.Index), $"@{key}",
                        $"Legacy label \"@{key}\" not found in the labels index. Verify with `d365fo resolve label @{key}`."));
                }
            }
        }

        // ── 2. Intrinsic functions ───────────────────────────────────────────
        foreach (Match m in Regex.Matches(cleaned,
            @"\b([A-Za-z]+[Ss]tr|tableNum|classNum|enumNum|enumCnt|fieldNum|extendedTypeNum)\s*\(\s*([A-Za-z_]\w*)\s*(?:,\s*([A-Za-z_]\w*)\s*)?\)",
            RegexOptions.IgnoreCase))
        {
            var fn = m.Groups[1].Value;
            if (!IntrinsicTargetTypes.TryGetValue(fn, out var expected)) continue; // not an intrinsic we know (e.g. subStr)
            var target = m.Groups[2].Value;
            var member = m.Groups[3].Success ? m.Groups[3].Value : null;
            int line = LineOf(cleaned, m.Index);

            if (locals.DeclaredNames.Contains(target)) { verifiedCount++; continue; }

            var kinds = LookupKinds(target);
            bool targetOk = expected is null
                ? (kinds.Count > 0 || SafeMenuItemExists(index, target))
                : kinds.Any(t => expected.Contains(t, StringComparer.OrdinalIgnoreCase));
            if (!targetOk)
            {
                violations.Add(new ReferenceViolation("unknown-intrinsic-target", "error", line,
                    $"{m.Groups[1].Value}({target}{(member is not null ? $", {member}" : "")})",
                    expected is null
                        ? $"\"{target}\" not found in the index (checked symbols and menu items)."
                        : $"\"{target}\" is not a known {string.Join("/", expected)} in the index. Use `d365fo search any {target}` to find the correct name."));
                continue;
            }

            // Second argument: fieldStr(T, F) / methodStr(C, m) / tableFieldGroupStr(T, G)
            if (member is not null)
            {
                var fnLower = fn.ToLowerInvariant();
                if (fnLower is "fieldstr" or "fieldnum")
                {
                    if (SafeFieldExists(index, target, member))
                    {
                        verifiedCount++;
                    }
                    else
                    {
                        violations.Add(new ReferenceViolation("unknown-field", "error", line,
                            $"{target}.{member}",
                            $"Field \"{member}\" not found on {target} (checked fields, system fields, table extensions). Use `d365fo get table {target}`."));
                    }
                }
                else if (fnLower is "methodstr" or "staticmethodstr")
                {
                    if (SafeFindMethod(index, target, member) is not null)
                    {
                        verifiedCount++;
                    }
                    else
                    {
                        violations.Add(new ReferenceViolation("unknown-method", "error", line,
                            $"{target}.{member}",
                            $"Method \"{member}\" not found on {target} (checked inheritance chain and extensions). Use `d365fo get class {target}`."));
                    }
                }
                else
                {
                    verifiedCount++;
                }
            }
            else
            {
                verifiedCount++;
            }
        }

        // ── 3. Static member access Type::member ─────────────────────────────
        foreach (Match m in Regex.Matches(cleaned, @"\b([A-Za-z_]\w*)\s*::\s*([A-Za-z_]\w*)"))
        {
            var typeName = m.Groups[1].Value;
            var member = m.Groups[2].Value;
            int line = LineOf(cleaned, m.Index);

            if (locals.DeclaredNames.Contains(typeName)) continue;
            if (KernelTypes.Contains(typeName)) { verifiedCount++; continue; } // no metadata for kernel statics

            var kinds = LookupKinds(typeName);
            if (kinds.Count == 0)
            {
                violations.Add(new ReferenceViolation("unknown-type", "error", line,
                    $"{typeName}::{member}",
                    $"\"{typeName}\" not found in the index. Use `d365fo search any {typeName}` to find the correct name."));
                continue;
            }
            if (kinds.Contains("enum", StringComparer.OrdinalIgnoreCase))
            {
                // Enum value membership is verified separately when EnumValues are indexed;
                // the enum type itself is proven here.
                verifiedCount++;
                continue;
            }

            var method = SafeFindMethod(index, typeName, member);
            if (method is null)
            {
                violations.Add(new ReferenceViolation("unknown-static-member", "error", line,
                    $"{typeName}::{member}",
                    $"Static method \"{member}\" not found on {typeName} (checked inheritance chain and extensions). Use `d365fo get class {typeName}`."));
                continue;
            }
            verifiedCount++;

            // Arity check when the call site and the signature are both parseable
            if (method.Signature is not null)
            {
                var arity = ParseSignatureArity(method.Signature);
                int afterMatch = m.Index + m.Length;
                int callOpen = cleaned.IndexOf('(', afterMatch);
                var between = callOpen == -1 ? "" : cleaned[afterMatch..callOpen];
                if (arity is not null && callOpen != -1 && between.Trim().Length == 0)
                {
                    var argsText = ExtractCallArgs(cleaned, callOpen);
                    if (argsText is not null)
                    {
                        int count = CountCallArgs(argsText);
                        if (count < arity.Value.Min || count > arity.Value.Max)
                        {
                            violations.Add(new ReferenceViolation("arity-mismatch", "error", line,
                                $"{typeName}::{member}",
                                $"Call passes {count} argument(s), but the indexed signature expects " +
                                (arity.Value.Min == arity.Value.Max ? $"{arity.Value.Min}" : $"{arity.Value.Min}–{arity.Value.Max}") +
                                $": {method.Signature.Trim()}"));
                        }
                    }
                }
            }
        }

        // ── 4. Declared types ────────────────────────────────────────────────
        var reportedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var typeName in locals.Bindings.Values)
        {
            if (!reportedTypes.Add(typeName)) continue;
            if (IsKnownType(typeName))
            {
                verifiedCount++;
            }
            else
            {
                violations.Add(new ReferenceViolation("unknown-type", "warning", 0, typeName,
                    $"Declared type \"{typeName}\" not found in the index. " +
                    $"If it is a kernel class this is a false positive; otherwise use `d365fo search any {typeName}`."));
            }
        }

        // ── 5. Bound buffer member access var.Field / var.method() ──────────
        foreach (var (varName, typeName) in locals.Bindings)
        {
            var kinds = LookupKinds(typeName);
            bool isTableLike = kinds.Any(t => TableLikeTypes.Contains(t));
            bool isClass = !isTableLike && kinds.Contains("class", StringComparer.OrdinalIgnoreCase);
            if (!isTableLike && !isClass) continue;

            var checkedMembers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in Regex.Matches(cleaned,
                $@"\b{Regex.Escape(varName)}\s*\.\s*([A-Za-z_]\w*)\s*(\()?", RegexOptions.IgnoreCase))
            {
                var member = m.Groups[1].Value;
                bool isCall = m.Groups[2].Success;
                if (!checkedMembers.Add($"{member}:{isCall}")) continue;
                int line = LineOf(cleaned, m.Index);

                if (isCall)
                {
                    bool builtin = isTableLike
                        ? TableBuiltinMethods.Contains(member)
                        : ObjectBuiltinMethods.Contains(member);
                    if (builtin || SafeFindMethod(index, typeName, member) is not null)
                    {
                        verifiedCount++;
                    }
                    else
                    {
                        violations.Add(new ReferenceViolation("unknown-method", "warning", line,
                            $"{typeName}.{member}()",
                            $"Method \"{member}\" not found on {typeName} (checked builtins, inheritance, extensions). " +
                            $"Verify with `d365fo get {(isTableLike ? "table" : "class")} {typeName}`."));
                    }
                }
                else if (isTableLike)
                {
                    if (SafeFieldExists(index, typeName, member))
                    {
                        verifiedCount++;
                    }
                    else
                    {
                        violations.Add(new ReferenceViolation("unknown-field", "error", line,
                            $"{typeName}.{member}",
                            $"Field \"{member}\" not found on {typeName} (checked fields, system fields, table extensions). Use `d365fo get table {typeName}`."));
                    }
                }
            }
        }

        return new ResolveResult(violations, verifiedCount);
    }

    private static bool SafeMenuItemExists(IReferenceIndex index, string name)
    {
        try { return index.MenuItemExists(name); } catch { return false; }
    }

    private static MethodLookup? SafeFindMethod(IReferenceIndex index, string owner, string method)
    {
        try { return index.FindMethod(owner, method); } catch { return null; }
    }

    private static bool SafeFieldExists(IReferenceIndex index, string table, string field)
    {
        if (TableSystemFields.Contains(field)) return true;
        try { return index.FieldExists(table, field); } catch { return false; }
    }
}
