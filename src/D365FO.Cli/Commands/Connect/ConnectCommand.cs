using System.Text.Json;
using System.Text.Json.Nodes;
using D365FO.Core;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Connect;

/// <summary>
/// <c>d365fo connect &lt;URL&gt;</c> — point an editor's MCP client at an already
/// deployed <c>d365fo-mcp --http</c> server.
/// </summary>
/// <remarks>
/// Before this existed the documented procedure was "write this JSON by hand",
/// which is how MCP entries get clobbered: the config files are shared with every
/// other MCP server the developer uses. This command merges a single named entry
/// and leaves everything else in the file untouched.
/// <para>
/// The server is probed on <c>GET /health</c> first. That distinguishes the two
/// failures that look identical in an editor ("no tools appeared"): a typo in the
/// URL, versus a real server that is merely cold-starting. The probe is advisory —
/// <c>--force</c> writes the config regardless.
/// </para>
/// </remarks>
public sealed class ConnectCommand : AsyncCommand<ConnectCommand.Settings>
{
    /// <summary>Editor config layouts this command knows how to merge into.</summary>
    private static readonly IReadOnlyDictionary<string, (string File, string Section)> Targets =
        new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            // Claude Code project-scoped config.
            ["claude"] = (".mcp.json", "mcpServers"),
            // VS Code workspace config.
            ["vscode"] = (".vscode/mcp.json", "servers"),
        };

    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<URL>")]
        [System.ComponentModel.Description("Base URL of the deployed server, e.g. https://d365fo-mcp.example.com. A trailing /mcp is accepted and stripped.")]
        public string Url { get; init; } = "";

        [CommandOption("--editor <NAME>")]
        [System.ComponentModel.Description("Which config layout to write: claude (.mcp.json, default) | vscode (.vscode/mcp.json).")]
        public string Editor { get; init; } = "claude";

        [CommandOption("--name <NAME>")]
        [System.ComponentModel.Description("Name of the MCP server entry (default: d365fo). Only this entry is touched.")]
        public string Name { get; init; } = "d365fo";

        [CommandOption("--api-key <KEY>")]
        [System.ComponentModel.Description("Value for the X-Api-Key header, when the server runs with API_KEY set. Written into the config file — see the warning it emits.")]
        public string? ApiKey { get; init; }

        [CommandOption("--out <PATH>")]
        [System.ComponentModel.Description("Write to this config file instead of the editor's default location.")]
        public string? Out { get; init; }

        [CommandOption("--force")]
        [System.ComponentModel.Description("Overwrite an existing entry of the same name, and write even when the health probe fails.")]
        public bool Force { get; init; }

        [CommandOption("--no-probe")]
        [System.ComponentModel.Description("Skip the GET /health check entirely.")]
        public bool NoProbe { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);

        if (string.IsNullOrWhiteSpace(settings.Url))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "Server URL required."));

        if (!Targets.TryGetValue(settings.Editor, out var target))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                $"Unknown --editor '{settings.Editor}'.",
                hint: "Supported: claude (.mcp.json) | vscode (.vscode/mcp.json). Use --out to write somewhere else."));

        if (!TryNormalizeBaseUrl(settings.Url, out var baseUrl, out var urlError))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, urlError!,
                hint: "Pass an absolute http(s) URL, e.g. https://d365fo-mcp.example.com or http://localhost:3000."));

        var warnings = new List<string>();

        // ---- Probe -------------------------------------------------------
        object? health = null;
        if (!settings.NoProbe)
        {
            var (ok, payload, probeError) = await ProbeHealthAsync(baseUrl!).ConfigureAwait(false);
            health = payload;
            if (!ok)
            {
                if (!settings.Force)
                    return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                        "SERVER_UNREACHABLE",
                        $"No healthy server answered GET {baseUrl}/health: {probeError}",
                        hint: "Check the URL, or pass --force to write the config anyway (a cold-starting server may answer later). --no-probe skips the check."));

                warnings.Add($"Health probe failed ({probeError}); config written anyway because --force was passed.");
            }
            else if (payload is HealthInfo { Mode: { Length: > 0 } mode } && !string.Equals(mode, "full", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"Server reports MCP_SERVER_MODE={mode}, so it advertises only part of the tool surface. " +
                             (string.Equals(mode, "read-only", StringComparison.OrdinalIgnoreCase)
                                 ? "Scaffolding and label writes will not be available through this entry."
                                 : "Search and analysis tools will not be available through this entry."));
            }
        }

        if (!string.IsNullOrEmpty(settings.ApiKey))
            warnings.Add("The API key is stored in plain text in the config file — do not commit it if the file is tracked by git.");

        // ---- Merge -------------------------------------------------------
        var path = string.IsNullOrWhiteSpace(settings.Out)
            ? Path.GetFullPath(target.File)
            : Path.GetFullPath(settings.Out!);

        string? existing = null;
        try
        {
            if (File.Exists(path)) existing = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.SourceUnreadable,
                $"Cannot read existing config {path}: {ex.Message}"));
        }

        var merge = MergeServerEntry(existing, target.Section, settings.Name, baseUrl!, settings.ApiKey, settings.Force);
        if (merge.Error is not null)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(merge.Error.Code, merge.Error.Message, merge.Error.Hint));

        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, merge.Json!);
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed,
                $"Cannot write {path}: {ex.Message}"));
        }

        return RenderHelpers.Render(kind, ToolResult<object>.Success(new
        {
            editor = settings.Editor.ToLowerInvariant(),
            path,
            entry = settings.Name,
            section = target.Section,
            url = baseUrl + "/mcp",
            replaced = merge.Replaced,
            authenticated = !string.IsNullOrEmpty(settings.ApiKey),
            health,
        }, warnings.Count > 0 ? warnings : null));
    }

    /// <summary>Shape of the server's <c>GET /health</c> payload.</summary>
    public sealed record HealthInfo(string? Status, string? Mode, bool? IndexReachable);

    internal sealed record MergeError(string Code, string Message, string? Hint);

    internal sealed record MergeResult(string? Json, bool Replaced, MergeError? Error);

    /// <summary>
    /// Merge a single named MCP server entry into an existing config document,
    /// preserving every other key in the file. Pure and file-system free so the
    /// merge rules can be tested without a server or a real editor config.
    /// </summary>
    /// <param name="existingJson">Current file contents, or null when the file does not exist.</param>
    /// <param name="section">Top-level object holding the servers ("mcpServers" or "servers").</param>
    /// <param name="force">Allow replacing an entry that is already present.</param>
    internal static MergeResult MergeServerEntry(
        string? existingJson, string section, string name, string baseUrl, string? apiKey, bool force)
    {
        JsonObject root;
        if (string.IsNullOrWhiteSpace(existingJson))
        {
            root = new JsonObject();
        }
        else
        {
            try
            {
                root = JsonNode.Parse(existingJson, documentOptions: new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                }) as JsonObject
                    ?? throw new JsonException("root is not a JSON object");
            }
            catch (JsonException ex)
            {
                // Never overwrite a file we could not understand — it holds the
                // developer's other MCP servers.
                return new MergeResult(null, false, new MergeError(
                    "CONFIG_UNPARSEABLE",
                    $"Existing config is not valid JSON: {ex.Message}",
                    "Fix or move the file, then re-run. This command refuses to overwrite a config it cannot read."));
            }
        }

        if (root[section] is not JsonObject servers)
        {
            if (root[section] is not null)
                return new MergeResult(null, false, new MergeError(
                    "CONFIG_UNEXPECTED_SHAPE",
                    $"Existing config has a '{section}' key that is not an object.",
                    "Fix the file by hand — replacing it would discard whatever is there."));

            servers = new JsonObject();
            root[section] = servers;
        }

        var replaced = servers[name] is not null;
        if (replaced && !force)
            return new MergeResult(null, false, new MergeError(
                "ENTRY_EXISTS",
                $"'{section}.{name}' already exists in the config.",
                "Pass --force to replace it, or --name <NAME> to add a second entry alongside it."));

        var entry = new JsonObject
        {
            ["type"] = "http",
            ["url"] = baseUrl + "/mcp",
        };
        if (!string.IsNullOrEmpty(apiKey))
            entry["headers"] = new JsonObject { ["X-Api-Key"] = apiKey };

        servers[name] = entry;

        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        return new MergeResult(json + Environment.NewLine, replaced, null);
    }

    /// <summary>
    /// Normalise user input into a base URL with no trailing slash and no trailing
    /// <c>/mcp</c> — people paste the endpoint they configured, not the root.
    /// </summary>
    internal static bool TryNormalizeBaseUrl(string input, out string? baseUrl, out string? error)
    {
        baseUrl = null;
        error = null;

        var raw = input.Trim();
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = $"'{input}' is not an absolute http(s) URL.";
            return false;
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/mcp", StringComparison.OrdinalIgnoreCase))
            path = path[..^"/mcp".Length];
        else if (string.Equals(path, "/mcp", StringComparison.OrdinalIgnoreCase))
            path = string.Empty;

        baseUrl = uri.GetLeftPart(UriPartial.Authority) + path;
        return true;
    }

    private static async Task<(bool Ok, HealthInfo? Payload, string? Error)> ProbeHealthAsync(string baseUrl)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var response = await http.GetAsync(baseUrl + "/health").ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return (false, null, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

            try
            {
                var node = JsonNode.Parse(body) as JsonObject;
                var info = new HealthInfo(
                    (string?)node?["status"],
                    (string?)node?["mode"],
                    (bool?)node?["indexReachable"]);
                return (true, info, null);
            }
            catch (JsonException)
            {
                // Reachable but not our server — say so rather than claim success.
                return (false, null, "the endpoint answered but did not return a d365fo-mcp health payload");
            }
        }
        catch (TaskCanceledException)
        {
            return (false, null, "the request timed out after 10s");
        }
        catch (HttpRequestException ex)
        {
            return (false, null, ex.Message);
        }
    }
}
