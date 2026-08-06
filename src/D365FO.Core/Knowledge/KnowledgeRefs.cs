// <copyright file="KnowledgeRefs.cs" company="d365fo-cli contributors">
// MIT
// </copyright>

using System.Text.RegularExpressions;

namespace D365FO.Core.Knowledge;

/// <summary>One named AOT element found in a knowledge topic.</summary>
/// <param name="TopicId">Topic the reference came from.</param>
/// <param name="Name">The element name exactly as the topic spells it.</param>
/// <param name="Member">For <c>static-call</c>: the identifier after <c>::</c>.</param>
/// <param name="Kind">How the reference was recognised — see <see cref="KnowledgeRefKinds"/>.</param>
/// <param name="Field">Where in the topic it was found (<c>§heading · source</c>), for defect reporting.</param>
/// <param name="Key">Stable snapshot key: <c>topicId|kind|name[::member]</c>.</param>
public sealed record KnowledgeRef(
    string TopicId,
    string Name,
    string? Member,
    string Kind,
    string Field,
    string Key);

/// <summary>The recognised reference shapes. Values are the strings written into the snapshot key.</summary>
public static class KnowledgeRefKinds
{
    public const string StaticCall = "static-call";
    public const string Extends = "extends";
    public const string New = "new";
    public const string Attribute = "attribute";
    public const string Intrinsic = "intrinsic";
    public const string Declaration = "declaration";
}

/// <summary>
/// Pulls every named AOT type / API out of the <c>skills/_source</c> corpus so it can be
/// resolved against the real symbol index.
///
/// Rationale (ported from upstream <c>d365fo-mcp-server</c>'s <c>knowledgeRefs.ts</c>):
/// generated code is gated fail-closed by <c>validate references</c> and the build, but the
/// knowledge shipped to the model was never gated at all. That asymmetry is how a
/// <c>SysRunnable::run()</c> that exists in no AOT can survive review.
///
/// Extraction is deliberately conservative: only shapes where a PascalCase token is
/// unambiguously an AOT element reference are emitted, so an unresolved reference is a real
/// defect rather than prose noise. Everything here is pure string work — no index, no VM.
/// </summary>
public static class KnowledgeRefExtractor
{
    /// <summary>PascalCase-ish AOT element name.</summary>
    private const string Ident = "[A-Z][A-Za-z0-9_]*";

    /// <summary>
    /// X++ primitives, keywords, and prose placeholders that are never AOT elements.
    /// Compared lowercase.
    /// </summary>
    private static readonly HashSet<string> NonAot = new(StringComparer.OrdinalIgnoreCase)
    {
        // primitives / built-in types
        "str", "int", "int64", "real", "date", "utcdatetime", "boolean", "container",
        "anytype", "guid", "void", "timeofday", "enum", "class", "interface", "table",
        "common", "blob", "varstring", "var",
        // statement keywords that can precede an identifier
        "if", "else", "while", "for", "switch", "case", "return", "throw", "new",
        "select", "firstonly", "from", "where", "join", "order", "group", "by",
        "public", "private", "protected", "static", "final", "abstract", "extends",
        "implements", "using", "true", "false", "null", "this", "super", "next",
        "ttsbegin", "ttscommit", "ttsabort", "changecompany", "delete_from",
        "insert_recordset", "update_recordset", "validtimestate", "crosscompany",
        "display", "edit", "client", "server", "internal", "const", "exists", "notexists",
        // Prose placeholders the corpus uses to stand in for a name the reader supplies.
        "target", "classname", "tablename", "formname", "enumname", "methodname",
        "findoptions", "fieldlist", "tablebuffer", "name", "object", "model", "suffix",
        "kind", "pattern", "field", "method", "path", "file", "query", "type", "value",
        "existingsuffix", "activecustommodel", "role", "id", "text", "lang", "heading",
    };

    /// <summary>
    /// Intrinsic functions whose first argument is an AOT element name.
    /// <c>methodStr</c> is handled separately (two args → type + method).
    /// </summary>
    private static readonly string[] Intrinsics =
    [
        "classStr", "tableStr", "formStr", "enumStr", "queryStr", "reportStr",
        "extendedTypeStr", "menuItemDisplayStr", "menuItemActionStr", "menuItemOutputStr",
        "classNum", "tableNum", "enumNum", "delegateStr", "staticMethodStr",
        "formControlStr", "formDataSourceStr", "dataEntityDataSourceStr", "ssrsReportStr",
    ];

    /// <summary>
    /// Illustrative placeholder names. The corpus writes hypothetical elements as
    /// <c>MyFoo</c> / <c>IMyFoo</c>, so these are <i>supposed</i> not to resolve — flagging
    /// them would bury the real defects.
    /// </summary>
    private static readonly Regex Placeholder = new(@"^I?My[A-Z0-9]", RegexOptions.Compiled);

    /// <summary>Markdown inline link / reference — the bracket is link text, not an X++ attribute.</summary>
    private static readonly Regex MarkdownLink = new(@"\[([^\]\[]*)\]\s*[\(\[][^\)\]]*[\)\]]", RegexOptions.Compiled);

    /// <summary>
    /// <c>&lt;Placeholder&gt;</c> tokens in shell/prose examples — a slot, not an element. Any
    /// identifier glued to the slot goes with it: <c>&lt;Module&gt;Parameters</c> names no
    /// element either, it names "the parameters table of the module you pick".
    /// </summary>
    private static readonly Regex AngleSlot = new(@"\w*<[A-Za-z][A-Za-z0-9_ .|/-]*>\w*", RegexOptions.Compiled);

    private static readonly Regex StaticCallRx = new($@"\b({Ident})::([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);
    private static readonly Regex ExtendsRx = new($@"\b(?:extends|implements)\s+({Ident}(?:\s*,\s*{Ident})*)", RegexOptions.Compiled);
    private static readonly Regex NewRx = new($@"\bnew\s+({Ident})\s*\(", RegexOptions.Compiled);
    private static readonly Regex BracketRx = new(@"\[([^\]\[]+)\]", RegexOptions.Compiled);
    private static readonly Regex AttributeHeadRx = new($@"^\s*({Ident})\s*(?:\(|$)", RegexOptions.Compiled);
    private static readonly Regex MethodStrRx = new($@"\bmethodStr\s*\(\s*({Ident})\s*,\s*([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);
    private static readonly Regex DeclarationRx = new($@"^\s{{0,8}}({Ident})\s+([a-z_][A-Za-z0-9_]*)\s*(;|=[^=])", RegexOptions.Compiled);
    private static readonly Regex LocalDeclRx = new($@"\b(?:class|interface)\s+({Ident})", RegexOptions.Compiled);

    /// <summary>Fence languages carrying X++ — the only ones the declaration rule runs on.</summary>
    private static readonly HashSet<string> XppLanguages = new(StringComparer.OrdinalIgnoreCase) { "xpp", "x++" };

    /// <summary>
    /// Fence languages whose content is markup, not code: element names there are AOT XML
    /// node types (<c>AxTable</c>, <c>AxFormControl</c>), not symbols in the index.
    /// </summary>
    private static readonly HashSet<string> SkippedLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "xml", "json", "sarif", "yaml", "yml", "csv", "sql", "diff",
    };

    /// <summary>Extract every AOT reference from the whole embedded corpus, in topic order.</summary>
    public static IReadOnlyList<KnowledgeRef> ExtractAll(IEnumerable<KnowledgeTopic>? topics = null) =>
        (topics ?? KnowledgeBase.Topics).SelectMany(Extract).ToList();

    /// <summary>Extract every AOT reference from one topic.</summary>
    public static IReadOnlyList<KnowledgeRef> Extract(KnowledgeTopic topic)
    {
        var acc = new Dictionary<string, KnowledgeRef>(StringComparer.Ordinal);
        var blocks = KnowledgeMarkdown.Blocks(topic.Body);

        // Types the examples declare themselves are not expected to be in the AOT.
        var local = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var block in blocks)
        {
            foreach (Match m in LocalDeclRx.Matches(block.Text)) local.Add(m.Groups[1].Value);
        }

        foreach (var block in blocks)
        {
            if (SkippedLanguages.Contains(block.Language)) continue;
            var isXpp = XppLanguages.Contains(block.Language);
            var text = isXpp ? block.Text : Soften(block.Text);
            var field = block.Field;

            foreach (Match m in StaticCallRx.Matches(text))
                Push(acc, topic.Id, field, KnowledgeRefKinds.StaticCall, m.Groups[1].Value, m.Groups[2].Value);

            foreach (Match m in ExtendsRx.Matches(text))
            {
                foreach (var one in m.Groups[1].Value.Split(','))
                    Push(acc, topic.Id, field, KnowledgeRefKinds.Extends, one.Trim());
            }

            foreach (Match m in NewRx.Matches(text))
                Push(acc, topic.Id, field, KnowledgeRefKinds.New, m.Groups[1].Value);

            // [FooAttribute] / [FooAttribute(...)]. Brackets containing '::' are X++
            // container literals ([NoYes::Yes, NoYes::No]), not attributes at all.
            foreach (Match m in BracketRx.Matches(text))
            {
                if (m.Groups[1].Value.Contains("::", StringComparison.Ordinal)) continue;
                var head = AttributeHeadRx.Match(m.Groups[1].Value);
                if (head.Success) Push(acc, topic.Id, field, KnowledgeRefKinds.Attribute, head.Groups[1].Value);
            }

            foreach (var fn in Intrinsics)
            {
                foreach (Match m in Regex.Matches(text, $@"\b{fn}\s*\(\s*({Ident})"))
                    Push(acc, topic.Id, field, KnowledgeRefKinds.Intrinsic, m.Groups[1].Value);
            }

            foreach (Match m in MethodStrRx.Matches(text))
                Push(acc, topic.Id, field, KnowledgeRefKinds.StaticCall, m.Groups[1].Value, m.Groups[2].Value);

            // `Foo fooVar;` / `Foo fooVar =` — only inside X++ examples; prose sentences
            // produce false positives.
            if (!isXpp) continue;
            foreach (var line in text.Split('\n'))
            {
                var m = DeclarationRx.Match(line);
                if (m.Success) Push(acc, topic.Id, field, KnowledgeRefKinds.Declaration, m.Groups[1].Value);
            }
        }

        return acc.Values
            .Where(r => !local.Contains(r.Name) && !Placeholder.IsMatch(r.Name))
            .ToList();
    }

    /// <summary>Markdown/shell noise removal: link syntax and <c>&lt;Slot&gt;</c> placeholders.</summary>
    private static string Soften(string text) => AngleSlot.Replace(MarkdownLink.Replace(text, "$1"), " ");

    private static void Push(
        Dictionary<string, KnowledgeRef> acc,
        string topicId,
        string field,
        string kind,
        string name,
        string? member = null)
    {
        if (name.Length < 3 || NonAot.Contains(name)) return;
        var key = $"{topicId}|{kind}|{name}{(member is null ? "" : "::" + member)}";
        // First occurrence wins — keeps the reported location stable across edits elsewhere.
        if (!acc.ContainsKey(key)) acc[key] = new KnowledgeRef(topicId, name, member, kind, field, key);
    }
}
