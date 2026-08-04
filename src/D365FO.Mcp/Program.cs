using D365FO.Core;
using D365FO.Mcp;

// Entry point for the `d365fo-mcp` executable. Wires the default
// MetadataRepository + ToolHandlers stack and runs an MCP server over stdio
// (official `ModelContextProtocol` SDK) until stdin closes — or, with `--http`,
// over a streamable-HTTP-lite transport for a shared team deployment (see
// docs/SETUP_AZURE.md).
//
// Usage:
//   d365fo-mcp                          # stdio, uses env vars
//   d365fo-mcp --db /path/to/idx.sqlite # override DB path
//   d365fo-mcp --legacy                 # use built-in StdioDispatcher (no SDK)
//   d365fo-mcp --http                   # HTTP transport (POST /mcp, GET /health)
//   d365fo-mcp --http --port 8080       # override HTTP listen port
//
// The stdio server speaks the MCP stdio transport — compatible with Claude
// Desktop, Cursor, VS Code Copilot, and any other MCP client that supports it.
// The HTTP transport is for a shared, remotely-hosted instance (Azure App
// Service): set MCP_SERVER_MODE (full|read-only|write-only) and API_KEY to
// gate which tools are exposed and require the X-Api-Key header.

string? dbPath = null;
bool legacy = false;
bool http = false;
int? port = null;
for (int i = 0; i < args.Length; i++)
{
    if ((args[i] == "--db" || args[i] == "-d") && i + 1 < args.Length)
    {
        dbPath = args[++i];
    }
    else if (args[i] == "--legacy")
    {
        legacy = true;
    }
    else if (args[i] == "--http")
    {
        http = true;
    }
    else if (args[i] == "--transport" && i + 1 < args.Length)
    {
        // --transport stdio is the (redundant) default; --transport http is
        // the --http shorthand. Any other value is ignored — stdio remains
        // the default transport rather than erroring on a typo.
        http = args[i + 1] == "http";
        i++;
    }
    else if (args[i] == "--port" && i + 1 < args.Length)
    {
        port = int.Parse(args[++i]);
    }
    else if (args[i] == "--help" || args[i] == "-h")
    {
        Console.Error.WriteLine("""
            d365fo-mcp — Model Context Protocol server for D365 F&O metadata.

            Options:
              --db, -d <PATH>     Override index database path.
              --legacy            Use the built-in StdioDispatcher (pre-SDK transport).
              --http              Serve over HTTP (POST /mcp, GET /health) instead of stdio.
              --port <N>          HTTP listen port (default: MCP_HTTP_PORT env var, else 3000).
              --help, -h          Print this message.

            Environment (HTTP transport):
              MCP_SERVER_MODE     full (default) | read-only | write-only — gates which tools are exposed.
              API_KEY             Shared secret checked against the X-Api-Key header on POST /mcp.
                                   Unset = no auth (a startup warning is logged); GET /health never needs it.
              MCP_HTTP_PORT       Listen port when --port is not passed.

            Route any log output to stderr — stdout is reserved for protocol frames (stdio transport only).
            """);
        return 0;
    }
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

if (http)
{
    var resolvedPort = port
        ?? (int.TryParse(D365FoSettings.Resolve("MCP_HTTP_PORT"), out var envPort) ? envPort : 3000);
    await HttpServerHost.RunAsync(dbPath, resolvedPort, cts.Token);
}
else if (legacy)
{
    var dispatcher = StdioDispatcher.CreateDefault(dbPath);
    await dispatcher.RunAsync(Console.In, Console.Out, cts.Token);
}
else
{
    await McpServerHost.RunStdioAsync(dbPath, loggerFactory: null, cts.Token);
}
return 0;
