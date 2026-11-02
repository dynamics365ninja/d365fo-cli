using D365FO.Core.Knowledge;

namespace D365FO.Mcp;

/// <summary>
/// The <c>instructions</c> field of the MCP <c>initialize</c> response — guidance every client
/// receives once, before any tool call, rather than paying for it per tool description.
///
/// The rule half is composed from the same <see cref="RuleCanon"/> blocks the CLI's
/// <c>agent-prompt</c> and the emitted skill files use, so the three surfaces cannot disagree
/// about what the rules are (audit finding K1: the canon used to be written out three times).
/// </summary>
public static class ServerInstructions
{
    /// <summary>Canon blocks published to MCP clients, in reading order.</summary>
    private static readonly (string Id, string Heading)[] Sections =
    [
        ("never-auto", "Never-auto rules"),
        ("core", "Non-negotiable X++ rules"),
        ("queries", "X++ database query rules"),
        ("coc", "Chain of Command rules"),
        ("aot-xml-safety", "AOT XML safety"),
        ("bp", "Best-practice rules (must pass `bp check`)"),
    ];

    private static readonly Lazy<string> TextLazy = new(Build);

    /// <summary>The instructions text, built once per process.</summary>
    public static string Text => TextLazy.Value;

    /// <summary>
    /// The short routing guidance the SDK stdio server (the default transport, and the one
    /// Claude Desktop starts) sends in <c>initialize</c>. It sent none before, so a client
    /// without the <c>d365fo-cli</c> skill got 32 tool schemas and no word on which to use
    /// first. It restates the skill's tool mapping and the loop below in MCP tool names, and
    /// points at <c>get_knowledge</c> for the rule canon rather than carrying it: the full
    /// <see cref="Text"/> is ~7k characters. <c>ServerInstructionsTests</c> keeps this under
    /// 1,500 characters and every tool it names real.
    /// </summary>
    public const string Routing =
        """
        d365fo: D365 Finance & Operations metadata index and X++ authoring.
        Never guess a D365FO name or signature: look it up. A *_NOT_FOUND result means stop and ask.

        - Find objects: `search` (type=any when the kind is unknown, a queries array for several). Never grep AOT XML.
        - Read one object: `get_object_info` (tables: detail=brief first), several: `batch_get_info`, X++ source: `get_method`.
        - Before a change or a new object: `prepare` returns signature, existing CoC, naming and a grounding token in one call.
        - Write with `generate_object` / `modify_method`, passing that token; structural edits: `modify_object`. Never write AOT XML by hand.
        - Check hand-written X++ with `validate` (mode=references, then mode=xpp) before writing it.
        - Where-used and extensions: `find_references`, `extension_info`. Security: `security_info`.
        - Rules and patterns: `get_knowledge` (action=search, then action=get with a section).

        Build, sync, BP check and tests run only when the user asks.
        """;

    private static string Build() =>
        """
        # d365fo — D365 Finance & Operations metadata and X++ authoring

        Your training data is outdated and incomplete for D365FO: every environment has
        hundreds of thousands of tables, classes, EDTs and labels, most of them custom.
        **Never guess a name or a signature — query the index first.**

        The loop is **prepare → generate → validate → (build only on request)**:

        1. `prepare` returns signature, existing CoC, eligibility, naming check and a
           grounding token in one call. Do not issue separate search/get calls for facts
           `prepare` already returned.
        2. `generate_object` scaffolds the artifact; pass the grounding token.
        3. `validate_code` proves hand-written X++ against the index and the offline BP
           rules before anything is written.
        4. `get_knowledge` serves the verified topic corpus — prefer
           `action=search` then `action=get` with a `section` over fetching whole topics.

        A result carrying `warnings: ["served-from-index"]` means the live metadata bridge
        was offline and the answer came from the mirror. An `ok:false` with a `*_NOT_FOUND`
        code means **stop and ask** — do not invent a name.

        The rules below are the same canon the `d365fo` CLI and the shipped skill files
        carry; each one is explained in full by a `get_knowledge` topic.

        """ + RuleCanon.Digest(Sections);
}
