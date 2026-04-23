using D365FO.Core;
using D365FO.Core.Guardrails;
using D365FO.Core.Index;
using Spectre.Console;
using Spectre.Console.Cli;

namespace D365FO.Cli;

public static class RenderHelpers
{
    private static readonly System.Threading.AsyncLocal<bool> _resolveLabels = new();

    /// <summary>
    /// Opt-in label resolution for subsequent <see cref="Render"/> calls on
    /// this logical flow. Commands call this in their Execute override when
    /// <c>--resolve-labels</c> was set on the settings.
    /// </summary>
    public static IDisposable EnableLabelResolution(bool on)
    {
        var prev = _resolveLabels.Value;
        _resolveLabels.Value = on;
        return new Restore(() => _resolveLabels.Value = prev);
    }

    private sealed class Restore : IDisposable
    {
        private readonly Action _restore;
        public Restore(Action restore) { _restore = restore; }
        public void Dispose() => _restore();
    }

    public static int Render<T>(OutputMode.Kind kind, ToolResult<T> result, Action<T>? tableRenderer = null)
        => Render(kind, result, tableRenderer, resolveLabels: _resolveLabels.Value);

    public static int Render<T>(OutputMode.Kind kind, ToolResult<T> result, Action<T>? tableRenderer, bool resolveLabels)
    {
        if (resolveLabels && result.Ok && result.Data is not null)
        {
            try
            {
                var repo = RepoFactory.Create();
                var langs = ResolveLanguages();
                // Inline tokens on the full envelope and render the resulting
                // JsonNode directly. Fall back to the original payload if the
                // output mode is Table with a custom renderer (cannot inline).
                if (kind != OutputMode.Kind.Table || tableRenderer is null)
                {
                    var node = System.Text.Json.JsonSerializer.SerializeToNode(result, D365Json.Options);
                    if (node is not null)
                    {
                        LabelInliner.WalkAndReplace(node, repo, langs);
                        Console.Out.WriteLine(node.ToJsonString(D365Json.Options));
                        return result.Ok ? 0 : 1;
                    }
                }
            }
            catch
            {
                // best-effort: fall through to standard rendering.
            }
        }

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

    private static IReadOnlyCollection<string> ResolveLanguages()
    {
        var env = Environment.GetEnvironmentVariable("D365FO_LABEL_LANGUAGES");
        if (string.IsNullOrWhiteSpace(env))
        {
            return new[] { "en-us" };
        }
        return env.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public static string Escape(string? s) => s is null ? string.Empty : Markup.Escape(s);
}

/// <summary>Shared DI-free accessor for the repository.</summary>
public static class RepoFactory
{
    public static MetadataRepository Create(string? databaseOverride = null)
    {
        var settings = D365FoSettings.FromEnvironment(databaseOverride);
        var dir = Path.GetDirectoryName(Path.GetFullPath(settings.DatabasePath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var repo = new MetadataRepository(settings.DatabasePath);
        repo.EnsureSchema();
        return repo;
    }
}
