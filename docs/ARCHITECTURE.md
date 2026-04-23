# Architecture

## Three projects, one core

```
           ┌──────────────────────────────────────────────┐
           │               D365FO.Core                    │
           │  Index  Extract  Metadata  Scaffolding       │
           │  Guardrails  ToolResult  Settings            │
           └────────────────┬─────────────────────────────┘
                            │ same in-process API
            ┌───────────────┴────────────────┐
            ▼                                ▼
     D365FO.Cli                       D365FO.Mcp
  Spectre.Console.Cli             StdioDispatcher
  (stable default)           (coexistence adapter)
     `d365fo` binary          JSON-RPC / stdio
```

Key invariant: **only `D365FO.Core` knows about D365FO**. Both CLI and MCP
transports are thin adapters. A command handler is never more than "parse
args → call Core → render envelope".

## Output contract

Every tool returns `ToolResult<T>`:

```json
{ "ok": true,  "data": { ... }, "warnings": ["..."] }
{ "ok": false, "error": { "code": "UPPER_SNAKE", "message": "...", "hint": "..." } }
```

JSON is the default on non-TTY stdout. TTY renders Spectre tables. Flag
`--output json|table|raw` overrides.

## Index

SQLite single-file (`$D365FO_INDEX_DB` or `$LOCALAPPDATA/d365fo-cli/d365fo-index.sqlite`).
Schema v1 in [src/D365FO.Core/Index/Schema.sql](../src/D365FO.Core/Index/Schema.sql).
`MetadataRepository` is stateless — every call opens and closes its own
connection so the same type runs from a short-lived CLI process, a long-lived
MCP server, or a future daemon.

SQLite booleans are stored as INTEGER; `SqliteBoolHandler` teaches Dapper the
conversion once at static init.

## Guardrails

- `StringSanitizer` strips control characters from free-form metadata
  (labels, descriptions) to defend against prompt-injection embedded in
  customer data. CLI opt-out: `--raw-text`.
- Error envelope is always structured — never leak raw exception text to stdout.
- Write-ops that mutate XML on disk use atomic swap + `.bak` (wired in Phase 2
  `generate` group).

## MCP coexistence

`D365FO.Mcp.ToolHandlers` forwards to the same `D365FO.Core` primitives. A
follow-up commit replaces `StdioDispatcher` with the official
`modelcontextprotocol/csharp-sdk`, keeping `ToolHandlers` as the stable
internal surface.

## Why .NET 8 (targeted) / net10 (dev SDK today)

- Single source of truth for D365FO developers (C# is the language of the
  upstream C# bridge).
- Native single-file publish (`dotnet publish --self-contained`) avoids a
  Node runtime on every dev workstation.
- LTS support window.
- TFM is tracked in `Directory.Build.props`; switch to explicit `net8.0` once
  the reference pack is present on the build matrix.
