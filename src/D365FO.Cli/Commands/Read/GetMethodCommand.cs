using D365FO.Core;
using D365FO.Core.Extract;
using D365FO.Core.Index;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Read;

/// <summary>
/// <c>get method &lt;Owner&gt; &lt;method&gt;</c> and <c>get method &lt;Owner&gt;.&lt;method&gt;</c>:
/// one method's source, without having to know whether the owner is a class, a table or a form.
/// </summary>
/// <remarks>
/// The shape agents guess first. The MCP surface calls this read <c>get_method</c>, so an agent
/// that knows both surfaces typed <c>get method</c> and got "Unknown command" — five of the
/// fifteen guessed commands in a sample of real sessions. It answers exactly what
/// <c>read class|table|form &lt;Owner&gt; --method &lt;m&gt;</c> answers; the only thing it adds
/// is finding the owner's kind in the index. When the name is both a table and a form (as
/// <c>CustTable</c> is), the kind that declares the method wins, and if both do the call is
/// refused with <c>--kind</c> as the fix rather than one being picked.
/// </remarks>
public sealed class GetMethodCommand : Command<GetMethodCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<OWNER>")]
        [System.ComponentModel.Description("Class, table or form that declares the method — or `Owner.method` in one token.")]
        public string Owner { get; init; } = "";

        [CommandArgument(1, "[METHOD]")]
        [System.ComponentModel.Description("Method name. On a form, also the owner-qualified member name `find refs` reports (e.g. CustTable.init).")]
        public string? Method { get; init; }

        [CommandOption("--kind <KIND>")]
        [System.ComponentModel.Description("class | table | form. Only needed when the owner name exists as more than one kind and each declares the method.")]
        public string? Kind { get; init; }

        [CommandOption("--lines <RANGE>")]
        [System.ComponentModel.Description("Return only lines in the given 1-based inclusive range (e.g. 10-40).")]
        public string? Lines { get; init; }

        [CommandOption("--around <REGEX>")]
        [System.ComponentModel.Description("Return only lines matching the regex plus 3 lines of context.")]
        public string? Around { get; init; }
    }

    private static readonly string[] Kinds = ["class", "table", "form"];

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var output = OutputMode.Resolve(settings.Output);

        var (owner, method) = Split(settings.Owner, settings.Method);
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(method))
        {
            return RenderHelpers.Render(output, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                $"No method named in '{settings.Owner}'.",
                $"Name it: `d365fo get method {settings.Owner} <method>` or `d365fo get method {settings.Owner}.<method>`. "
                + $"To list the methods, run `d365fo read class|table|form {settings.Owner}`."));
        }

        string[] kinds;
        if (string.IsNullOrWhiteSpace(settings.Kind))
        {
            kinds = Kinds;
        }
        else if (Kinds.Contains(settings.Kind.Trim().ToLowerInvariant()))
        {
            kinds = [settings.Kind.Trim().ToLowerInvariant()];
        }
        else
        {
            return RenderHelpers.Render(output, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                $"--kind '{settings.Kind}' is not a kind that declares methods here.",
                "Use --kind class, --kind table or --kind form."));
        }

        var repo = RepoFactory.Create();
        var owners = new List<(string Kind, XppSourceReader.SourceResult Source)>();
        foreach (var kind in kinds)
        {
            var path = SourcePath(repo, kind, owner);
            if (string.IsNullOrEmpty(path)) continue;
            var src = XppSourceReader.Read(path);
            if (src is null)
            {
                return RenderHelpers.Render(output, ToolResult<object>.Fail(D365FoErrorCodes.SourceUnreadable,
                    $"Could not read X++ source at {path}."));
            }
            owners.Add((kind, src));
        }

        if (owners.Count == 0)
        {
            var code = kinds.Length == 1 ? NotFoundCode(kinds[0]) : D365FoErrorCodes.ClassNotFound;
            return RenderHelpers.Render(output, ToolResult<object>.Fail(code,
                $"No {string.Join(", ", kinds)} named '{owner}' has source in the index.",
                $"Check the spelling with `d365fo search any {owner}`."));
        }

        var hits = owners
            .Select(o => (o.Kind, o.Source, Member: XppSourceReader.FindMember(o.Source, method)))
            .Where(h => h.Member is not null)
            .ToList();

        if (hits.Count == 0)
        {
            var available = owners
                .Select(o => $"{o.Kind} {owner}: {string.Join(", ", o.Source.Methods.Select(x => x.Name).Take(20))}");
            return RenderHelpers.Render(output, ToolResult<object>.Fail(D365FoErrorCodes.MethodNotFound,
                $"Method '{method}' not found on {string.Join(" or ", owners.Select(o => $"{o.Kind} '{owner}'"))}.",
                $"Available methods — {string.Join(" | ", available)}"));
        }

        if (hits.Count > 1)
        {
            return RenderHelpers.Render(output, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                $"'{owner}' is a {string.Join(" and a ", hits.Select(h => h.Kind))}, and each declares '{method}'.",
                $"Pick one: {string.Join(", ", hits.Select(h => $"`d365fo get method {owner} {method} --kind {h.Kind}`"))}."));
        }

        var hit = hits[0];
        return ReadClassCommand.RenderMethod(output, hit.Kind, owner, hit.Source, hit.Member!, settings.Lines, settings.Around);
    }

    /// <summary>
    /// <c>Owner method</c> as typed, or <c>Owner.method</c> split at its first dot — so a form
    /// member keeps its own qualifier (<c>CustTable.CustTable.init</c> → form <c>CustTable</c>,
    /// member <c>CustTable.init</c>).
    /// </summary>
    internal static (string Owner, string Method) Split(string owner, string? method)
    {
        owner = owner.Trim();
        if (!string.IsNullOrWhiteSpace(method)) return (owner, method.Trim());

        var dot = owner.IndexOf('.');
        return dot > 0 && dot < owner.Length - 1
            ? (owner[..dot], owner[(dot + 1)..])
            : (owner, "");
    }

    private static string? SourcePath(MetadataRepository repo, string kind, string name) => kind switch
    {
        "table" => repo.GetTableDetails(name)?.Table?.SourcePath,
        "form" => repo.GetForm(name)?.Form?.SourcePath,
        _ => repo.GetClassDetails(name)?.Class?.SourcePath,
    };

    private static string NotFoundCode(string kind) => kind switch
    {
        "table" => D365FoErrorCodes.TableNotFound,
        "form" => D365FoErrorCodes.FormNotFound,
        _ => D365FoErrorCodes.ClassNotFound,
    };
}
