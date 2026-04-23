using D365FO.Core;
using D365FO.Core.Index;
using Spectre.Console;
using Spectre.Console.Cli;

namespace D365FO.Cli;

public static class RenderHelpers
{
    public static int Render<T>(OutputMode.Kind kind, ToolResult<T> result, Action<T>? tableRenderer = null)
    {
        switch (kind)
        {
            case OutputMode.Kind.Json:
            case OutputMode.Kind.Raw:
                Console.Out.WriteLine(D365Json.Serialize(result, indented: OutputMode.IsTty));
                break;
            case OutputMode.Kind.Table:
                if (!result.Ok || result.Data is null)
                {
                    AnsiConsole.MarkupLine($"[red]ERROR[/] {Escape(result.Error?.Message)}");
                    if (!string.IsNullOrEmpty(result.Error?.Hint))
                        AnsiConsole.MarkupLine($"[yellow]Hint:[/] {Escape(result.Error?.Hint)}");
                }
                else if (tableRenderer is not null)
                {
                    tableRenderer(result.Data);
                }
                else
                {
                    Console.Out.WriteLine(D365Json.Serialize(result, indented: true));
                }
                break;
        }

        return result.Ok ? 0 : 1;
    }

    public static string Escape(string? s) => s is null ? string.Empty : Markup.Escape(s);
}

/// <summary>Shared DI-free accessor for the repository.</summary>
public static class RepoFactory
{
    public static MetadataRepository Create(string? databaseOverride = null)
    {
        var settings = D365FoSettings.FromEnvironment(databaseOverride);
        Directory.CreateDirectory(Path.GetDirectoryName(settings.DatabasePath)!);
        var repo = new MetadataRepository(settings.DatabasePath);
        repo.EnsureSchema();
        return repo;
    }
}
