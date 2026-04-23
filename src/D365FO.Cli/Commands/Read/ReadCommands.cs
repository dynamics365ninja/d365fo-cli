using System.Linq;
using D365FO.Core;
using D365FO.Core.Extract;
using D365FO.Core.Index;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Read;

/// <summary>
/// Base for all 'read' commands. Loads the SourcePath from the index and
/// extracts declaration / methods from the AOT XML file.
/// </summary>
public abstract class ReadBaseCommand<TSettings> : Command<TSettings>
    where TSettings : ReadBaseCommand<TSettings>.ReadSettings
{
    public abstract class ReadSettings : D365OutputSettings
    {
        [CommandArgument(0, "<NAME>")]
        public string Name { get; init; } = "";

        [CommandOption("--method <NAME>")]
        [System.ComponentModel.Description("If set, return only the source of this method.")]
        public string? Method { get; init; }

        [CommandOption("--declaration")]
        [System.ComponentModel.Description("Return only the top-level declaration block.")]
        public bool DeclarationOnly { get; init; }

        [CommandOption("--lines <RANGE>")]
        [System.ComponentModel.Description("Return only lines in the given 1-based inclusive range (e.g. 10-40). Requires --method.")]
        public string? Lines { get; init; }

        [CommandOption("--around <REGEX>")]
        [System.ComponentModel.Description("Return only lines matching the regex plus 3 lines of context. Requires --method.")]
        public string? Around { get; init; }
    }

    protected abstract (string? SourcePath, string Kind, string NotFoundCode) ResolveTarget(MetadataRepository repo, string name);

    public override int Execute(CommandContext ctx, TSettings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var repo = RepoFactory.Create();
        var (path, objKind, notFoundCode) = ResolveTarget(repo, settings.Name);

        if (string.IsNullOrEmpty(path))
        {
            return RenderHelpers.Render(kind,
                ToolResult<object>.Fail(notFoundCode, $"{objKind} '{settings.Name}' has no source in the index."));
        }

        var src = XppSourceReader.Read(path!);
        if (src is null)
        {
            return RenderHelpers.Render(kind,
                ToolResult<object>.Fail("SOURCE_UNREADABLE", $"Could not read X++ source at {path}."));
        }

        if (!string.IsNullOrWhiteSpace(settings.Method))
        {
            var m = XppSourceReader.FindMethod(src, settings.Method!);
            if (m is null)
            {
                return RenderHelpers.Render(kind,
                    ToolResult<object>.Fail("METHOD_NOT_FOUND",
                        $"Method '{settings.Method}' not found on {objKind} '{settings.Name}'.",
                        $"Available methods: {string.Join(", ", src.Methods.Select(x => x.Name).Take(20))}"));
            }

            var body = m.Body;
            string? sliced = null;
            if (!string.IsNullOrWhiteSpace(settings.Lines))
            {
                var parts = settings.Lines.Split('-', 2);
                if (parts.Length == 2
                    && int.TryParse(parts[0], out var from)
                    && int.TryParse(parts[1], out var to))
                {
                    sliced = XppSourceReader.Slice(body, from, to);
                }
                else
                {
                    return RenderHelpers.Render(kind,
                        ToolResult<object>.Fail("INVALID_RANGE",
                            $"--lines expects FROM-TO (e.g. 10-40); got '{settings.Lines}'."));
                }
            }
            else if (!string.IsNullOrWhiteSpace(settings.Around))
            {
                sliced = XppSourceReader.AroundPattern(body, settings.Around!, 3);
            }

            return RenderHelpers.Render(kind, ToolResult<object>.Success(new
            {
                kind = objKind, name = settings.Name, path = src.Path,
                method = m.Name, source = sliced ?? body
            }));
        }

        if ((settings.Lines ?? settings.Around) is not null)
        {
            return RenderHelpers.Render(kind,
                ToolResult<object>.Fail("INVALID_ARGS", "--lines / --around require --method."));
        }

        if (settings.DeclarationOnly)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Success(new
            {
                kind = objKind, name = settings.Name, path = src.Path,
                declaration = src.Declaration
            }));
        }

        return RenderHelpers.Render(kind, ToolResult<object>.Success(new
        {
            kind = objKind,
            name = settings.Name,
            path = src.Path,
            declaration = src.Declaration,
            methodCount = src.Methods.Count,
            methods = src.Methods
        }));
    }
}

public sealed class ReadClassCommand : ReadBaseCommand<ReadClassCommand.Settings>
{
    public sealed class Settings : ReadSettings { }
    protected override (string? SourcePath, string Kind, string NotFoundCode) ResolveTarget(MetadataRepository repo, string name)
    {
        var d = repo.GetClassDetails(name);
        return (d?.Class?.SourcePath, "class", "CLASS_NOT_FOUND");
    }
}

public sealed class ReadTableCommand : ReadBaseCommand<ReadTableCommand.Settings>
{
    public sealed class Settings : ReadSettings { }
    protected override (string? SourcePath, string Kind, string NotFoundCode) ResolveTarget(MetadataRepository repo, string name)
    {
        var d = repo.GetTableDetails(name);
        return (d?.Table?.SourcePath, "table", "TABLE_NOT_FOUND");
    }
}

public sealed class ReadFormCommand : ReadBaseCommand<ReadFormCommand.Settings>
{
    public sealed class Settings : ReadSettings { }
    protected override (string? SourcePath, string Kind, string NotFoundCode) ResolveTarget(MetadataRepository repo, string name)
    {
        var d = repo.GetForm(name);
        return (d?.Form?.SourcePath, "form", "FORM_NOT_FOUND");
    }
}
