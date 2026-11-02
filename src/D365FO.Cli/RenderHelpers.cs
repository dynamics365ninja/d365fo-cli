using D365FO.Core;
using D365FO.Core.Guardrails;
using D365FO.Core.Index;
using Spectre.Console;
using Spectre.Console.Cli;

namespace D365FO.Cli;

public static class RenderHelpers
{
    /// <summary>
    /// Strips non-alphanumeric characters and lower-cases the result so that
    /// CLI argument values like "table-relations", "tableRelations", and
    /// "tablerelations" all compare equal in command switch expressions.
    /// </summary>
    public static string NormalizeKind(string value)
        => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

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
        // A write this command made is re-indexed before its result is shown, so the next command
        // reads what was just written. Nothing is pending unless the command journaled a write.
        if (IndexAutoRefresh.CurrentScope?.Flush() is { } refreshed)
            return RenderRefreshed(kind, result, tableRenderer, resolveLabels, refreshed);

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

    /// <summary>
    /// Render a result after the auto-refresh ran: <c>data.indexRefreshed</c> in JSON, one extra
    /// line under a table.
    /// </summary>
    private static int RenderRefreshed<T>(
        OutputMode.Kind kind, ToolResult<T> result, Action<T>? tableRenderer, bool resolveLabels,
        IReadOnlyList<IndexAutoRefresh.ModelRefresh> refreshed)
    {
        if (kind == OutputMode.Kind.Table && tableRenderer is not null)
        {
            var code = Render(kind, result, tableRenderer, resolveLabels);
            foreach (var r in refreshed)
                AnsiConsole.MarkupLine(r.Ok
                    ? $"[grey]index refreshed: {Escape(r.Model)} ({r.ElapsedMs} ms)[/]"
                    : $"[yellow]warn[/] index not refreshed for {Escape(r.Model)}: {Escape(r.Error)}. Run `d365fo index sync {Escape(r.Model)}`.");
            return code;
        }

        var envelope = new ToolResult<object>(result.Ok, result.Data, result.Error, result.Warnings);
        return Render(kind, IndexAutoRefresh.Attach(envelope, refreshed), null, resolveLabels);
    }

    private static IReadOnlyCollection<string> ResolveLanguages()
    {
        var raw = D365FoSettings.Resolve("D365FO_LABEL_LANGUAGES");
        return string.IsNullOrWhiteSpace(raw)
            ? new[] { "en-us" }
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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
