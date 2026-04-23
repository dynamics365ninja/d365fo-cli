using D365FO.Mcp;

// Entry point for the `d365fo-mcp` executable. Wires the default
// MetadataRepository + ToolHandlers stack and runs the JSON-RPC 2.0 stdio
// server until stdin closes (i.e., the MCP client disconnects).
//
// Usage:
//   d365fo-mcp                          # uses env vars
//   d365fo-mcp --db /path/to/idx.sqlite # override DB path
//
// The server speaks newline-delimited JSON-RPC on stdio — compatible with
// Claude Desktop, Cursor, VS Code Copilot, and any other MCP client that
// supports the stdio transport.

string? dbPath = null;
for (int i = 0; i < args.Length; i++)
{
    if ((args[i] == "--db" || args[i] == "-d") && i + 1 < args.Length)
    {
        dbPath = args[++i];
    }
    else if (args[i] == "--help" || args[i] == "-h")
    {
        Console.Error.WriteLine("""
            d365fo-mcp — Model Context Protocol server for D365 F&O metadata.

            Options:
              --db, -d <PATH>   Override index database path.
              --help, -h        Print this message.

            The server reads JSON-RPC 2.0 requests from stdin and writes
            responses to stdout (one JSON object per line). Route log output
            to stderr — stdout is reserved for protocol frames.
            """);
        return 0;
    }
}

var dispatcher = StdioDispatcher.CreateDefault(dbPath);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

await dispatcher.RunAsync(Console.In, Console.Out, cts.Token);
return 0;
